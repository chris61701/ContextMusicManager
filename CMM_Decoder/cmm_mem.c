/* ──────────────────────────────────────────────────────────────────────────
 * cmm_mem.c — NATIVE HEAP ACCOUNTING for the whole DLL.
 *
 * WHY THIS EXISTS. Every memory instrument the mod had pointed at the MANAGED heap:
 * the Debug HUD's Heap/GC rows, CMMPerf's per-phase _allocB, and the CmmSession soak's
 * forced-GC drift. Decode and mix are C, so the only thing ever aimed at them was the
 * process working set — and that cannot resolve a leak of a few MB/hour. Measured on
 * 2026-08-27: four seeds of IDENTICAL code gave back-half RSS slopes of -1.51, +0.16,
 * +0.67 and +2.49 MB/sim-h. The SIGN is not stable, so no single RSS reading means
 * anything, and the native allocators were simply unverified.
 *
 * HOW. The whole DLL is one gcc link, so -Wl,--wrap=malloc/free/realloc/calloc/strdup
 * redirects every call in OUR objects AND in the static libs we pull in (fdk-aac,
 * vorbis, opus, ogg, and libstdc++'s operator new, which bottoms out in malloc). No
 * third-party source is touched and nothing needs a custom-allocator hook.
 *
 * NO HEADER BLOCK. The obvious implementation over-allocates and stashes the size in
 * front of the pointer, which would be wrong here: msvcrt hands out blocks of its own
 * (a CRT-internal strdup, say) that our wrapped free() would then read a bogus header
 * from — and reading behind a pointer can fault at a page boundary. _msize() asks the
 * CRT how big a block actually is, works on any msvcrt-heap pointer whoever allocated
 * it, and needs no space of its own.
 *
 * The counters are __atomic so the producer thread, the loudness worker and the main
 * thread can all allocate without a lock. Accounting is a handful of ns per call.
 * ────────────────────────────────────────────────────────────────────────── */

#include <stdlib.h>
#include <string.h>
#include <malloc.h>      /* _msize */
#include <stdint.h>      /* uintptr_t */
#include "cmm_decoder.h"

void *__real_malloc(size_t n);
void  __real_free(void *p);
void *__real_realloc(void *p, size_t n);
void *__real_calloc(size_t a, size_t b);

static long long g_live, g_peak, g_allocs, g_frees, g_bytesTotal;

static void note_add(size_t bytes)
{
    long long live = __atomic_add_fetch(&g_live, (long long)bytes, __ATOMIC_RELAXED);
    __atomic_add_fetch(&g_allocs, 1, __ATOMIC_RELAXED);
    __atomic_add_fetch(&g_bytesTotal, (long long)bytes, __ATOMIC_RELAXED);
    /* Peak is advisory, so a racing pair that both read a stale max is acceptable —
     * the alternative is a CAS loop on every allocation for a diagnostic number. */
    long long peak = __atomic_load_n(&g_peak, __ATOMIC_RELAXED);
    if (live > peak) __atomic_store_n(&g_peak, live, __ATOMIC_RELAXED);
}

static void note_sub(size_t bytes)
{
    __atomic_sub_fetch(&g_live, (long long)bytes, __ATOMIC_RELAXED);
    __atomic_add_fetch(&g_frees, 1, __ATOMIC_RELAXED);
}

void *__wrap_malloc(size_t n)
{
    void *p = __real_malloc(n);
    if (p) note_add(_msize(p));
    return p;
}

void *__wrap_calloc(size_t a, size_t b)
{
    void *p = __real_calloc(a, b);
    if (p) note_add(_msize(p));
    return p;
}

void *__wrap_realloc(void *p, size_t n)
{
    size_t was = p ? _msize(p) : 0;
    void *q = __real_realloc(p, n);
    if (!q) return NULL;                 /* p still live and still accounted */
    if (was) __atomic_sub_fetch(&g_live, (long long)was, __ATOMIC_RELAXED);
    else     __atomic_add_fetch(&g_allocs, 1, __ATOMIC_RELAXED);
    {
        size_t now = _msize(q);
        __atomic_add_fetch(&g_live, (long long)now, __ATOMIC_RELAXED);
        if (now > was) __atomic_add_fetch(&g_bytesTotal, (long long)(now - was), __ATOMIC_RELAXED);
        long long live = __atomic_load_n(&g_live, __ATOMIC_RELAXED);
        long long peak = __atomic_load_n(&g_peak, __ATOMIC_RELAXED);
        if (live > peak) __atomic_store_n(&g_peak, live, __ATOMIC_RELAXED);
    }
    return q;
}

void __wrap_free(void *p)
{
    if (p) note_sub(_msize(p));
    __real_free(p);
}

/* ── FFmpeg's heap (cmm_ff_*) ─────────────────────────────────────────────────
 * FFmpeg is built with --malloc-prefix=cmm_ff_ (build_ffmpeg.sh), so every av_malloc,
 * av_realloc and av_free in it lands here instead of in msvcrt. That matters because the
 * obvious route does not work: left alone, FFmpeg uses msvcrt's _aligned_malloc, which mingw
 * declares dllimport, so the call goes through __imp__aligned_malloc and -Wl,--wrap never
 * sees it. Checked, not assumed: with that build a 4-hour MP4 open showed 8.7 KB live --
 * our own decoder struct -- and none of FFmpeg's index, which is megabytes.
 *
 * Each block is carved out of __wrap_malloc, so it is counted like everything else, and a
 * small header in front of the returned pointer keeps the raw pointer and the size. A
 * header is safe HERE, unlike in the plain wraps above: these functions are called only by
 * FFmpeg's mem.c, and every block it frees through cmm_ff_free was made by cmm_ff_*.
 * Alignment is 64 bytes throughout: FFmpeg's AVX-512 code expects that from av_malloc, and
 * giving it to realloc'd blocks too costs nothing. */
typedef struct { void *raw; size_t size; } cmm_ff_hdr;
#define CMM_FF_ALIGN 64

static void *cmm_ff_alloc(size_t size, size_t align)
{
    if (align < CMM_FF_ALIGN) align = CMM_FF_ALIGN;
    if (align & (align - 1)) return NULL;                         /* power of two only */
    size_t slack = align - 1 + sizeof(cmm_ff_hdr);
    if (size > (size_t)-1 - slack) return NULL;
    unsigned char *raw = (unsigned char *)__wrap_malloc(size + slack);
    if (!raw) return NULL;
    uintptr_t a = ((uintptr_t)(raw + sizeof(cmm_ff_hdr)) + (align - 1)) & ~(uintptr_t)(align - 1);
    cmm_ff_hdr *h = (cmm_ff_hdr *)a - 1;
    h->raw = raw; h->size = size;
    return (void *)a;
}

void cmm_ff_free(void *p)
{
    if (p) __wrap_free(((cmm_ff_hdr *)p - 1)->raw);
}

void *cmm_ff_malloc(size_t size)                  { return cmm_ff_alloc(size, CMM_FF_ALIGN); }
void *cmm_ff_memalign(size_t align, size_t size)  { return cmm_ff_alloc(size, align); }

int cmm_ff_posix_memalign(void **out, size_t align, size_t size)
{
    void *p = cmm_ff_alloc(size, align);
    if (!p) return 12;                                            /* ENOMEM */
    *out = p;
    return 0;
}

void *cmm_ff_realloc(void *p, size_t size)
{
    if (!p) return cmm_ff_alloc(size, CMM_FF_ALIGN);
    if (!size) { cmm_ff_free(p); return NULL; }
    void *q = cmm_ff_alloc(size, CMM_FF_ALIGN);
    if (!q) return NULL;                                          /* p untouched, as realloc */
    size_t old = ((cmm_ff_hdr *)p - 1)->size;
    memcpy(q, p, old < size ? old : size);
    cmm_ff_free(p);
    return q;
}

/* Live snapshot. Any pointer may be NULL. `live` is the number that matters for a leak
 * hunt: a flat live figure across hours of play is the native side's clean bill of
 * health, and a rising one is the leak RSS could never resolve. */
CMM_API void cmm_meminfo(long long *live, long long *peak,
                         long long *allocs, long long *frees, long long *bytesTotal)
{
    if (live)       *live       = __atomic_load_n(&g_live, __ATOMIC_RELAXED);
    if (peak)       *peak       = __atomic_load_n(&g_peak, __ATOMIC_RELAXED);
    if (allocs)     *allocs     = __atomic_load_n(&g_allocs, __ATOMIC_RELAXED);
    if (frees)      *frees      = __atomic_load_n(&g_frees, __ATOMIC_RELAXED);
    if (bytesTotal) *bytesTotal = __atomic_load_n(&g_bytesTotal, __ATOMIC_RELAXED);
}

/* Re-arm the peak to the current live figure, so a soak can measure a phase rather than
 * everything since load. Never touches `live` — that is a real quantity, not a window. */
CMM_API void cmm_meminfo_reset_peak(void)
{
    __atomic_store_n(&g_peak, __atomic_load_n(&g_live, __ATOMIC_RELAXED), __ATOMIC_RELAXED);
}
