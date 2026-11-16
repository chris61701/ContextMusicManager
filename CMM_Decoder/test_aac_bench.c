/* AAC decoder benchmark: two builds of CMM_Decoder.dll, the same files, five measurements.
 *
 *   decode   open once, decode up to 600 s, best of 5 after a warm-up; one thread pinned to one
 *            core at high priority. Reported as ns per output frame and multiples of real time.
 *            Excludes the open, so it is the codec plus the read path, not the file system.
 *   open     open + close, median of 9 (the file is in the OS cache after the warm-up)
 *   seek     30 random seeks, each followed by a 1024-frame read; median and 95th percentile
 *   memory   native heap held by an open decoder after 1 s of playback, and the peak during the
 *            open (cmm_meminfo, which counts both builds' libraries)
 *   parallel N threads (env CMM_THREADS, default 8) each decoding every file (cap 120 s per file)
 *            at the same time; aggregate multiples of real time
 *
 * Build:  gcc -O2 -municode -o test_aac_bench.exe test_aac_bench.c
 * Run:    test_aac_bench.exe <old.dll> <new.dll> <file|@list.txt>... */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <wchar.h>

typedef void* (*open_fn)(const wchar_t*, int*, int*, long long*, int*);
typedef int   (*read_fn)(void*, float*, int);
typedef int   (*seek_fn)(void*, long long);
typedef void  (*close_fn)(void*);
typedef void  (*mi_fn)(long long*, long long*, long long*, long long*, long long*);
typedef void  (*rp_fn)(void);
typedef struct { open_fn ao, mo; read_fn ar, mr; seek_fn as, ms; close_fn ac, mc; mi_fn mi; rp_fn rp; const char* name; } api;

static double now_ms(void) { LARGE_INTEGER f, c; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&c); return 1000.0 * c.QuadPart / f.QuadPart; }

static int load(const wchar_t* p, api* a, const char* name)
{
    HMODULE m = LoadLibraryW(p); if (!m) return -1;
#define P(f, n) a->f = (void*)GetProcAddress(m, n)
    P(ao, "cmm_aac_open_file"); P(ar, "cmm_aac_read_float"); P(as, "cmm_aac_seek"); P(ac, "cmm_aac_close");
    P(mo, "cmm_container_open_file"); P(mr, "cmm_container_read_float"); P(ms, "cmm_container_seek"); P(mc, "cmm_container_close");
    P(mi, "cmm_meminfo"); P(rp, "cmm_meminfo_reset_peak");
#undef P
    a->name = name;
    return 0;
}

typedef struct { void* d; int mkv; const api* a; int ch, sr; long long total; } dec;
static int  dopen(dec* x, const api* a, const wchar_t* p, int mkv) { int e; x->a = a; x->mkv = mkv; x->d = (mkv ? a->mo : a->ao)(p, &x->ch, &x->sr, &x->total, &e); return x->d ? 0 : -1; }
static int  dread(dec* x, float* o, int n) { return (x->mkv ? x->a->mr : x->a->ar)(x->d, o, n); }
static int  dseek(dec* x, long long f) { return (x->mkv ? x->a->ms : x->a->as)(x->d, f); }
static void dclose(dec* x) { if (x->d) (x->mkv ? x->a->mc : x->a->ac)(x->d); x->d = NULL; }

static int cmpd(const void* a, const void* b) { double x = *(const double*)a, y = *(const double*)b; return x < y ? -1 : x > y; }

typedef struct { int ok; int sr; long long frames; double ns_per_frame, xrt, open_med, seek_med, seek_p95, held_mb, peak_mb; } result;

static result measure(const api* a, const wchar_t* path, int mkv)
{
    static float buf[8192 * 8];
    result r; memset(&r, 0, sizeof r);
    dec x; int g;
    if (dopen(&x, a, path, mkv)) return r;                      /* warm-up: also fills the OS cache */
    while (dread(&x, buf, 8192) > 0) {}
    dclose(&x);
    r.ok = 1;

    double best = -1; long long cap = 0;
    for (int run = 0; run < 5; run++) {
        if (dopen(&x, a, path, mkv)) { r.ok = 0; return r; }
        cap = (long long)x.sr * 600; long long got = 0;
        double t = now_ms();
        while (got < cap && (g = dread(&x, buf, 8192)) > 0) got += g;
        double ms = now_ms() - t;
        r.frames = got; r.sr = x.sr;
        dclose(&x);
        if (best < 0 || ms < best) best = ms;
    }
    r.ns_per_frame = best * 1e6 / (double)r.frames;
    r.xrt = (r.frames / (double)r.sr) / (best / 1000.0);

    double o[9];
    for (int k = 0; k < 9; k++) { double t = now_ms(); dopen(&x, a, path, mkv); o[k] = now_ms() - t; dclose(&x); }
    qsort(o, 9, sizeof(double), cmpd); r.open_med = o[4];

    double s[30]; int ns = 0;
    if (dopen(&x, a, path, mkv) == 0) {
        long long total = x.total > 0 ? x.total : r.frames;
        srand(12345);
        for (int k = 0; k < 30; k++) {
            long long at = (long long)((rand() / (double)RAND_MAX) * (total * 0.95));
            double t = now_ms();
            if (dseek(&x, at) != 0) continue;
            dread(&x, buf, 1024);
            s[ns++] = now_ms() - t;
        }
        dclose(&x);
    }
    if (ns) { qsort(s, ns, sizeof(double), cmpd); r.seek_med = s[ns / 2]; r.seek_p95 = s[(int)(ns * 0.95) < ns ? (int)(ns * 0.95) : ns - 1]; }

    if (a->mi && a->rp) {
        long long l0, l1, pk;
        a->mi(&l0, 0, 0, 0, 0); a->rp();
        if (dopen(&x, a, path, mkv) == 0) {
            long long want = x.sr, got = 0;
            while (got < want && (g = dread(&x, buf, 8192)) > 0) got += g;
            a->mi(&l1, &pk, 0, 0, 0);
            r.held_mb = (l1 - l0) / 1048576.0; r.peak_mb = (pk - l0) / 1048576.0;
            dclose(&x);
        }
    }
    return r;
}

/* ── parallel throughput ── */
typedef struct { const api* a; wchar_t** files; int n; volatile LONG next; volatile LONG64 frames; volatile LONG64 rate; int iters; } par;
static DWORD WINAPI par_thread(LPVOID v)
{
    par* p = (par*)v;
    float* buf = malloc(sizeof(float) * 8192 * 8);           /* one per thread */
    for (;;) {
        LONG k = InterlockedIncrement(&p->next) - 1;
        if (k >= p->iters) break;
        const wchar_t* f = p->files[k % p->n];
        const wchar_t* ext = wcsrchr(f, L'.');
        dec x; int g; long long got = 0;
        if (dopen(&x, p->a, f, ext && !_wcsicmp(ext, L".mkv"))) continue;
        long long cap = (long long)x.sr * 120;
        while (got < cap && (g = dread(&x, buf, 8192)) > 0) got += g;
        InterlockedAdd64(&p->frames, got);
        InterlockedAdd64(&p->rate, x.sr);
        dclose(&x);
    }
    free(buf);
    return 0;
}
static double parallel(const api* a, wchar_t** files, int n, int threads, double* wallOut)
{
    par p = { a, files, n, 0, 0, 0, n * threads };
    HANDLE* th = malloc(sizeof(HANDLE) * threads);
    double t = now_ms();
    for (int i = 0; i < threads; i++) th[i] = CreateThread(NULL, 0, par_thread, &p, 0, NULL);
    WaitForMultipleObjects(threads, th, TRUE, INFINITE);
    double wall = (now_ms() - t) / 1000.0;
    *wallOut = wall;
    double avgRate = p.rate / (double)p.iters;
    return (p.frames / avgRate) / wall;                       /* seconds of audio per second */
}

static int cls(const wchar_t* n)
{
    if (wcsstr(n, L"he32")) return 0; if (wcsstr(n, L"he64")) return 1;
    if (wcsstr(n, L"lc128")) return 2; if (wcsstr(n, L"lc192")) return 3; if (wcsstr(n, L"lc256")) return 4;
    return 5;
}
static const wchar_t* clsName[] = { L"Apple HE-AAC 32k", L"Apple HE-AAC 64k", L"Apple AAC-LC 128k", L"FFmpeg AAC-LC 192k", L"Apple AAC-LC 256k", L"library files as-is" };

int wmain(int argc, wchar_t** argv)
{
    api A, B;
    if (load(argv[1], &A, "fdk") || load(argv[2], &B, "ffmpeg")) { fprintf(stderr, "cannot load DLLs\n"); return 2; }

    wchar_t** files = malloc(sizeof(wchar_t*) * 4096); int n = 0;
    for (int i = 3; i < argc; i++) {
        if (argv[i][0] != L'@') { files[n++] = argv[i]; continue; }
        FILE* lf = _wfopen(argv[i] + 1, L"rb"); static char line[4096];
        while (lf && fgets(line, sizeof line, lf)) {
            size_t L = strlen(line); while (L && (line[L - 1] == '\n' || line[L - 1] == '\r')) line[--L] = 0;
            if (!L) continue;
            int wl = MultiByteToWideChar(CP_UTF8, 0, line, -1, NULL, 0);
            files[n] = malloc(sizeof(wchar_t) * wl); MultiByteToWideChar(CP_UTF8, 0, line, -1, files[n], wl); n++;
        }
        if (lf) fclose(lf);
    }

    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
    SetThreadAffinityMask(GetCurrentThread(), 1ull << 2);

    double lsum[6] = { 0 }, osum[6] = { 0 }, ssum[6] = { 0 }, hO[6] = { 0 }, hN[6] = { 0 }; int cnt[6] = { 0 };
    double allL = 0; int allN = 0;
    wchar_t** both = malloc(sizeof(wchar_t*) * n); int nb = 0;
    wprintf(L"%-34ls | %9ls %9ls %6ls | %8ls %8ls | %8ls %8ls | %7ls %7ls\n", L"file", L"fdk ns/f", L"ffm ns/f", L"speed",
            L"fdk open", L"ffm open", L"fdk seek", L"ffm seek", L"fdk MB", L"ffm MB");
    for (int i = 0; i < n; i++) {
        const wchar_t* ext = wcsrchr(files[i], L'.');
        int mkv = ext && !_wcsicmp(ext, L".mkv");
        const wchar_t* nm = wcsrchr(files[i], L'\\'); nm = nm ? nm + 1 : files[i];
        result o = measure(&A, files[i], mkv), f = measure(&B, files[i], mkv);
        if (!o.ok || !f.ok) { wprintf(L"%-34.34ls | skipped (%ls cannot open)\n", nm, !o.ok && !f.ok ? L"neither" : !o.ok ? L"fdk" : L"ffmpeg"); continue; }
        double ratio = o.ns_per_frame / f.ns_per_frame;           /* >1 = FFmpeg faster */
        wprintf(L"%-34.34ls | %9.1f %9.1f %5.2fx | %6.2fms %6.2fms | %6.2fms %6.2fms | %7.2f %7.2f\n", nm,
                o.ns_per_frame, f.ns_per_frame, ratio, o.open_med, f.open_med, o.seek_med, f.seek_med, o.held_mb, f.held_mb);
        int c = cls(nm);
        lsum[c] += log(ratio); osum[c] += log(f.open_med / o.open_med); ssum[c] += log(f.seek_med / (o.seek_med > 0 ? o.seek_med : 1e-3));
        hO[c] += o.held_mb; hN[c] += f.held_mb; cnt[c]++;
        allL += log(ratio); allN++;
        both[nb++] = files[i];
    }

    wprintf(L"\n%-22ls %5ls | %18ls | %14ls | %14ls | %21ls\n", L"class", L"files", L"FFmpeg decode speed", L"open time", L"seek time", L"memory held fdk->ffm");
    for (int c = 0; c < 6; c++) if (cnt[c])
        wprintf(L"%-22ls %5d | %17.2fx | %13.2fx | %13.2fx | %8.2f -> %6.2f MB\n", clsName[c], cnt[c],
                exp(lsum[c] / cnt[c]), exp(osum[c] / cnt[c]), exp(ssum[c] / cnt[c]), hO[c] / cnt[c], hN[c] / cnt[c]);
    wprintf(L"%-22ls %5d | %17.2fx   (geometric mean; >1 = FFmpeg faster; time ratios <1 = FFmpeg quicker)\n", L"ALL", allN, exp(allL / allN));

    int threads = getenv("CMM_THREADS") ? atoi(getenv("CMM_THREADS")) : 8;
    SetThreadAffinityMask(GetCurrentThread(), ~0ull);
    double wO, wN;
    double pO = parallel(&A, both, nb, threads, &wO), pN = parallel(&B, both, nb, threads, &wN);
    wprintf(L"\nparallel, %d threads, %d files each: fdk %.0fx real time (%.1f s), ffmpeg %.0fx real time (%.1f s), ratio %.2fx\n",
            threads, nb, pO, wO, pN, wN, pN / pO);
    return 0;
}
