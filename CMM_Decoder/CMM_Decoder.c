/* CMM_Decoder — Opus (libopusfile) + FLAC (dr_flac) backends. See CMM_Decoder.h. */
#include "CMM_Decoder.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <wchar.h>

/* 64-bit file offsets: mingw/MSVC use the _ variants, POSIX uses the o variants. */
#ifdef _WIN32
  #define CMM_FSEEK(f,o,w) _fseeki64((f),(o),(w))
  #define CMM_FTELL(f)     _ftelli64((f))
#else
  #include <stdlib.h>
  #define CMM_FSEEK(f,o,w) fseeko((f),(o),(w))
  #define CMM_FTELL(f)     ftello((f))
#endif

#ifdef _WIN32
#include <windows.h>
#include <io.h>
#include <fcntl.h>
#endif

/* Open a file by wide path.
 *
 * Windows: we MUST pass FILE_SHARE_DELETE so the user can move / rename / delete
 * the track that is currently streaming. A playback reader holds this FILE* open
 * for the WHOLE duration of the track, and neither the CRT's _wfopen nor _wfsopen
 * (whose share flags top out at _SH_DENYNO = FILE_SHARE_READ|FILE_SHARE_WRITE)
 * grant delete-sharing — so the file stays locked against deletion/rename for as
 * long as it plays. That broke hot-swapping music in/out of the context folders
 * while the game runs (the user's move/delete fails at the OS level, leaving the
 * folder in a state the file-watcher then mis-reads). CreateFileW lets us request
 * all three share bits; we wrap the HANDLE in a CRT FILE* via _open_osfhandle +
 * _wfdopen so every decoder's existing fread/fseek/ftell callbacks keep working
 * unchanged. (A file deleted while open keeps streaming from the still-valid
 * handle until the reader is disposed — exactly what we want: the playing track
 * finishes or is hot-swapped out, instead of blocking the user's edit.)
 *
 * _wfopen keeps non-ASCII music paths working (bare fopen is ANSI); on POSIX we
 * down-convert (future Linux build) — unlink there already allows delete-while-open. */
/* ── File-open serialization ───────────────────────────────────────────────
 * msvcrt's low-level fd table (__pioinfo) + FILE* buffer setup is NOT safe to
 * run on multiple threads at once: two threads simultaneously creating a FILE*
 * (CreateFileW → _open_osfhandle → _wfdopen) — or one creating while another is
 * still reading a freshly-opened stream — can cross the fd/buffer bookkeeping,
 * so a read returns full-length but WRONG bytes. drflac_open then rejects those
 * bytes and returns NULL, i.e. an intermittent open failure with NO short read
 * and a perfectly healthy FILE* afterward.
 *
 * This bit us specifically on FLAC because a FLAC open reads the most through
 * the callbacks (STREAMINFO + all metadata blocks, our seek doing an extra
 * SEEK_END probe each time) — the widest concurrent-read window — and the
 * in-game loudness prescan opens several tracks in parallel. Measured on the
 * real library: ~18-38% of concurrent FLAC opens failed. A global critical
 * section spanning the FILE* creation AND the decoder's metadata-read init
 * drops that to 0/80000. Opens are brief (metadata only); the expensive decode
 * runs after the lock is released, so parallelism where it matters is kept.
 *
 * Lazy-initialised (no DllMain): the first caller InitializeCriticalSection's
 * it; others spin until state==2. Windows critical sections are re-entrant, so
 * nesting (a format's open holding it across cmm_open_path) is fine. */
static CRITICAL_SECTION g_openCs;
static volatile LONG g_openCsState = 0;
static void cmm_openlock(void) {
    if (InterlockedCompareExchange(&g_openCsState, 1, 0) == 0) {
        InitializeCriticalSection(&g_openCs);
        InterlockedExchange(&g_openCsState, 2);
    } else { while (g_openCsState != 2) Sleep(0); }
    EnterCriticalSection(&g_openCs);
}
static void cmm_openunlock(void) { LeaveCriticalSection(&g_openCs); }

static FILE* cmm_open_path(const wchar_t* path)
{
    if (!path) return NULL;
#ifdef _WIN32
    HANDLE h = CreateFileW(path, GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return NULL;
    /* On success the fd (and later the FILE*) OWNS the handle: closing the FILE*
     * via fclose closes the underlying handle. Don't CloseHandle(h) after this. */
    int fd = _open_osfhandle((intptr_t)h, _O_RDONLY | _O_BINARY);
    if (fd == -1) { CloseHandle(h); return NULL; }
    FILE* fp = _wfdopen(fd, L"rb");
    if (!fp) { _close(fd); return NULL; }   /* _close also closes the handle */
    return fp;
#else
    char buf[4096];
    if (wcstombs(buf, path, sizeof(buf)) == (size_t)-1) return NULL;
    return fopen(buf, "rb");
#endif
}

/* Files at/under this size are decoded from a whole-file native buffer; larger ones
 * stream (Windows: over a raw Win32 HANDLE — see the FLAC streaming path). This is now
 * purely a RAM-vs-speed tradeoff, NOT a safety boundary: both paths are race-free, so
 * the loudness measure normalizes files of ANY size (the >100 MB ambient tracks stream
 * with a few KB of state instead of a 100 MB buffer). Mirrors HotSwapMaxFileSizeBytes.
 * Overridable at build time (e.g. -DCMM_BUFFER_MAX=1 forces the streaming path for ALL
 * files, to exercise it in a test build). */
#ifndef CMM_BUFFER_MAX
#define CMM_BUFFER_MAX (100LL * 1024 * 1024)
#endif

/* Whole-file → native buffer via raw Win32 (thread-safe; no msvcrt fd table). Returns
 * NULL (caller falls back to streaming) if the file exceeds maxBytes (>0) or the 2 GiB
 * hard cap, or on any I/O error. Defined below cmm_flac_open_file; forward-declared here. */
static unsigned char* cmm_read_whole_file(const wchar_t* path, long long maxBytes, long long* outLen);

/* opusfile pulls in the opus + ogg headers it needs. We only use the
 * memory-stream + float-read API, so HTTP support is compiled out upstream. */
#include <opusfile.h>
/* dr_flac prototypes + drflac struct (implementation lives in dr_flac_impl.c). */
#include "dr_flac.h"
/* dr_mp3 / dr_wav prototypes + structs (implementations in dr_mp3_impl.c / dr_wav_impl.c). */
#include "dr_mp3.h"
#include "dr_wav.h"
/* libvorbisfile (ov_*) — the high-level Vorbis file/stream API. Prebuilt into
 * build/vorbis/libvorbis.a from third_party/vorbis (see build_vorbis.sh); it
 * pulls in vorbis/codec.h + ogg/ogg.h. Replaces the old header-only stb_vorbis,
 * which had no pull read/seek callback API and so could not stream. Like
 * opusfile, ov_open_callbacks lets us stream big files over raw Win32 HANDLE /
 * FILE* callbacks (race-free — no msvcrt fd table) instead of buffering whole. */
#include <vorbis/vorbisfile.h>

/* AAC: fdk-aac decoder (libfdk-aac.a) + minimp4 MP4/M4A demuxer. The minimp4
 * IMPLEMENTATION lives in minimp4_impl.c; here we only pull its MP4D_* demux
 * declarations + structs. fdk-aac decodes to interleaved INT16 (SAMPLE_BITS 16),
 * which the read path converts to f32 like every other codec. */
#include <aacdecoder_lib.h>
#include "minimp4.h"

/* Container (Matroska/WebM) demux + remux: matroska.c extracts the audio track;
 * libogg re-pages Opus/Vorbis packets; libopus gives exact per-packet sample
 * counts for Ogg granulepos. libogg/libopus are already linked (via opusfile). */
#include "matroska.h"
#include <ogg/ogg.h>
#include <opus.h>

enum { CMM_KIND_OPUS = 0, CMM_KIND_FLAC = 1, CMM_KIND_VORBIS = 2,
       CMM_KIND_MP3 = 3, CMM_KIND_WAV = 4, CMM_KIND_AAC = 5,
       CMM_KIND_CONTAINER = 6 };

/* ID3-prefixed FLAC support — defined below cmm_flac_open_file, used by the memory open above it. */
static long long cmm_id3_prefix_len(const unsigned char* head, long long avail);
static long long cmm_flac_start_offset(const unsigned char* head, long long headLen, long long avail);

/* dr_mp3 / dr_wav init into a CALLER-allocated struct (unlike drflac_open, which
 * returns a heap handle), so we malloc one and store the pointer here. */
struct cmm_decoder {
    int            kind;   /* CMM_KIND_* */
    OggOpusFile*   of;     /* libopusfile handle (opus)    */
    drflac*        flac;   /* dr_flac handle (flac)        */
    OggVorbis_File* vorbis; /* libvorbisfile handle (vorbis); owned, malloc'd */
    drmp3*         mp3;    /* dr_mp3 handle (mp3)          */
    drwav*         wav;    /* dr_wav handle (wav/aiff)     */
    unsigned char* buf;    /* owned copy of source bytes (memory opens; NULL for file opens) */
    FILE*          fp;     /* owned file handle (POSIX file/streaming opens; NULL otherwise) */
    void*          fh;     /* owned Win32 HANDLE for streamed FLAC (Windows big-file path; NULL otherwise) */
    long long      baseOff;/* bytes to skip at the head of the streamed file (an ID3 tag before
                            * the fLaC marker). Seek/tell translate by this so dr_flac sees a
                            * stream that starts at the marker. 0 for everything else. */
    long long      fsize;  /* cached file size for the streamed past-EOF seek check (fh path);
                            * also the buffer length for the Vorbis in-memory callback stream */
    long long      mpos;   /* read cursor for the Vorbis in-memory callback stream (buf/fsize) */
    int            channels;

    /* ── AAC (fdk-aac + minimp4) ──────────────────────────────────────────
     * Two source shapes share one decoder: an MP4/M4A container (demuxed by
     * minimp4 — random-access reads via cmm_mp4_read over buf OR fp, so even a
     * huge movie file is read on demand, never fully buffered) and a raw ADTS
     * stream (.aac — fdk-aac frames it internally; always over an in-memory
     * buffer, which the file open reads whole since ADTS music files are small). */
    HANDLE_AACDECODER aac;        /* fdk-aac handle */
    MP4D_demux_t*     mp4;        /* minimp4 demux (MP4 container; NULL for raw ADTS) */
    long long         srclen;     /* length of buf (memory open) for cmm_mp4_read bounds */
    unsigned          aac_track;  /* index of the chosen audio track (MP4) */
    unsigned          aac_sample; /* next MP4 sample (access unit) index to decode */
    unsigned          aac_count;  /* total MP4 samples in the audio track */
    long long         adts_pos;   /* read cursor into buf for raw ADTS */
    unsigned char*    aubuf;      /* scratch for one access unit (MP4 file path) */
    unsigned          aubuf_cap;  /* capacity of aubuf */
    unsigned char*    moov_buf;   /* cached MP4 'moov' box (file path); NULL if not cached */
    long long         moov_off;   /* file offset where moov_buf begins */
    long long         moov_len;   /* length of moov_buf */
    short*            i16;        /* one decoded frame, interleaved int16 (fdk output) */
    float*            pcm;        /* same frame converted to interleaved float */
    int               pcm_fill;   /* valid floats in pcm */
    int               pcm_pos;    /* floats already handed out from pcm */
    int               frame_size; /* output frame size per channel (from CStreamInfo) */
    long long         seek_skip;  /* output frames still to discard after a seek (to reach exact frame) */
    int               clr_hist;   /* 1 = pass AACDEC_CLRHIST on the next DecodeFrame (post-seek) */

    /* ── Container (Matroska/WebM) ────────────────────────────────────────
     * A .mkv/.webm handle owns a real decoder for the remuxed audio stream:
     * the audio track is demuxed (matroska.c) and repackaged ON DEMAND into a
     * framing an existing backend reads, streamed through a lazy pull source so
     * a multi-GB recording never has to be read whole at open. read/seek/close
     * delegate to the inner decoder; the container_stream is the byte producer. */
    struct cmm_decoder*    inner;   /* delegate decoder (kind != CONTAINER) */
    struct container_stream* cs;    /* lazy remux source feeding `inner` (CONTAINER only) */

    /* ── Streaming ADTS (AAC-in-container) ────────────────────────────────
     * The AAC backend normally frames a whole in-memory ADTS buffer; when fed
     * from a container it instead pulls ADTS bytes on demand via src_read into
     * a small refill window (sbuf), so the mkv audio track is never buffered
     * whole. src_read == NULL for every non-streaming AAC open. */
    int  (*src_read)(void* ud, void* dst, int n);
    void*          src_ud;
    unsigned char* sbuf;          /* refill window for streaming ADTS */
    int            scap, slen, spos;
    int            src_eof;
};

/* Generous one-frame scratch: HE-AAC frameSize is up to 2048, up to 8 channels. */
#define CMM_AAC_MAX_SAMPLES (2048 * 8)

/* Upper bound on the MP4 'moov' (index) we will pull into RAM to serve minimp4's
 * open-time sample-table parse from memory instead of a per-read syscall storm.
 * A music moov is a few MB, but a LONG VIDEO's is not: a 10 h AAC track carries a
 * ~36 MB sample table (one stsz/stco/stts entry per ~23 ms access unit), and past
 * the cap the streamed fallback parses it via millions of tiny fseek+fread calls —
 * seconds-to-minutes of stall on a multi-GB file (the in-game "video freezes the
 * game" case). So the cap is sized to comfortably hold even very long videos'
 * indexes (256 MB ≈ ~70 h of AAC); the box is validated to fit inside the file
 * before this and freed on close, and 'mdat' (the audio) is NEVER buffered — only
 * the index. Above the cap we still fall back to streamed reads (correct, just slow
 * to open — a hard parse guard covers that pathological tail separately).
 * Overridable at build time (e.g. -DCMM_MP4_MOOV_CACHE_MAX=0 disables the cache
 * for an A/B benchmark). */
#ifndef CMM_MP4_MOOV_CACHE_MAX
#define CMM_MP4_MOOV_CACHE_MAX (256 * 1024 * 1024)
#endif

CMM_API int CMM_Decoder_abi(void) { return CMM_DECODER_ABI; }

CMM_API cmm_decoder* cmm_opus_open_memory(const unsigned char* data, long long len,
                                          int* channels, int* sampleRate,
                                          long long* totalFrames, int* err)
{
    if (err) *err = 0;
    if (!data || len <= 0) { if (err) *err = OP_EFAULT; return NULL; }

    /* op_open_memory keeps a pointer to the buffer for the life of the stream, so
     * we own a private copy — the caller's buffer may be pooled/overwritten. */
    unsigned char* copy = (unsigned char*)malloc((size_t)len);
    if (!copy) { if (err) *err = OP_EFAULT; return NULL; }
    memcpy(copy, data, (size_t)len);

    int e = 0;
    OggOpusFile* of = op_open_memory(copy, (size_t)len, &e);
    if (!of) { free(copy); if (err) *err = e; return NULL; }

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { op_free(of); free(copy); if (err) *err = OP_EFAULT; return NULL; }

    d->kind     = CMM_KIND_OPUS;
    d->of       = of;
    d->flac     = NULL;
    d->vorbis   = NULL;
    d->buf      = copy;
    d->fp       = NULL;
    d->channels = op_channel_count(of, -1);

    if (channels)    *channels    = d->channels;
    if (sampleRate)  *sampleRate  = 48000;             /* Opus always decodes to 48 kHz */
    if (totalFrames) *totalFrames = (long long)op_pcm_total(of, -1);
    return d;
}

CMM_API int cmm_opus_read_float(cmm_decoder* d, float* out, int maxFrames)
{
    if (!d || !out || maxFrames <= 0) return 0;

    int total = 0;
    while (total < maxFrames) {
        /* op_read_float returns FRAMES per channel; buf_size is total float capacity. */
        int got = op_read_float(d->of,
                                out + (long)total * d->channels,
                                (maxFrames - total) * d->channels,
                                NULL);
        if (got == 0) break;        /* end of stream */
        if (got < 0)  return got;   /* decode error (negative OP_* code) */
        total += got;
    }
    return total;
}

CMM_API int cmm_opus_seek(cmm_decoder* d, long long frame)
{
    if (!d) return OP_EFAULT;
    return op_pcm_seek(d->of, (ogg_int64_t)frame);
}

CMM_API void cmm_opus_close(cmm_decoder* d)
{
    if (!d) return;
    if (d->of)  op_free(d->of);
    if (d->buf) free(d->buf);
    if (d->fp)  fclose(d->fp);
#ifdef _WIN32
    if (d->fh)  CloseHandle((HANDLE)d->fh);   /* Windows streamed-Opus handle */
#endif
    free(d);
}

/* ── FLAC (dr_flac) ──────────────────────────────────────────────────────── */

CMM_API cmm_decoder* cmm_flac_open_memory(const unsigned char* data, long long len,
                                          int* channels, int* sampleRate,
                                          long long* totalFrames, int* err)
{
    if (err) *err = 0;
    if (!data || len <= 0) { if (err) *err = -1; return NULL; }

    /* dr_flac reads the buffer on demand for the life of the decoder, so we own
     * a private copy — the caller's buffer may be pooled/overwritten. */
    unsigned char* copy = (unsigned char*)malloc((size_t)len);
    if (!copy) { if (err) *err = -1; return NULL; }
    memcpy(copy, data, (size_t)len);

    long long moff = cmm_flac_start_offset(copy, len, len);   /* skip an ID3 tag, if present */
    drflac* f = drflac_open_memory(copy + moff, (size_t)(len - moff), NULL);
    if (!f) { free(copy); if (err) *err = -1; return NULL; }

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { drflac_close(f); free(copy); if (err) *err = -1; return NULL; }

    d->kind     = CMM_KIND_FLAC;
    d->of       = NULL;
    d->flac     = f;
    d->vorbis   = NULL;
    d->buf      = copy;
    d->fp       = NULL;
    d->channels = (int)f->channels;

    if (channels)    *channels    = d->channels;
    if (sampleRate)  *sampleRate  = (int)f->sampleRate;          /* native rate, preserved */
    if (totalFrames) *totalFrames = (long long)f->totalPCMFrameCount;
    return d;
}

CMM_API int cmm_flac_read_float(cmm_decoder* d, float* out, int maxFrames)
{
    if (!d || !d->flac || !out || maxFrames <= 0) return 0;
    /* dr_flac fills exactly the frames requested (interleaved f32), looping
     * across FLAC frames internally; returns frames read, 0 at EOF. */
    return (int)drflac_read_pcm_frames_f32(d->flac, (drflac_uint64)maxFrames, out);
}

CMM_API int cmm_flac_seek(cmm_decoder* d, long long frame)
{
    if (!d || !d->flac) return -1;
    return drflac_seek_to_pcm_frame(d->flac, (drflac_uint64)frame) ? 0 : -1;
}

CMM_API void cmm_flac_close(cmm_decoder* d)
{
    if (!d) return;
    if (d->flac) drflac_close(d->flac);
    if (d->buf)  free(d->buf);
    if (d->fp)   fclose(d->fp);
#ifdef _WIN32
    if (d->fh)   CloseHandle((HANDLE)d->fh);   /* Windows streamed-FLAC handle */
#endif
    free(d);
}

/* ── Vorbis (libvorbisfile) ───────────────────────────────────────────────
 * ov_callbacks mirror opusfile's OpusFileCallbacks: {read, seek, close, tell}.
 * close is always NULL — the cmm_decoder owns the datasource and cmm_vorbis_close
 * frees it (buffer / FILE* / HANDLE), exactly like the Opus/FLAC file opens.
 * Three datasource shapes share these: an in-memory buffer (owned copy for
 * open_memory, or the whole-file buffer for the ≤cap file path), a raw Win32
 * HANDLE (Windows big-file streaming), and a FILE* (POSIX big-file streaming). */

/* In-memory stream over d->buf (length d->fsize, cursor d->mpos). */
static size_t cmm_ov_mread(void* p, size_t size, size_t nmemb, void* ds)
{
    cmm_decoder* d = (cmm_decoder*)ds;
    long long remain = d->fsize - d->mpos;
    if (remain <= 0 || size == 0) return 0;
    size_t items = (size_t)(remain / (long long)size);
    if (items > nmemb) items = nmemb;
    size_t bytes = items * size;
    memcpy(p, d->buf + d->mpos, bytes);
    d->mpos += (long long)bytes;
    return items;
}
static int cmm_ov_mseek(void* ds, ogg_int64_t off, int whence)
{
    cmm_decoder* d = (cmm_decoder*)ds;
    long long base = (whence == SEEK_CUR) ? d->mpos
                   : (whence == SEEK_END) ? d->fsize : 0;
    long long np = base + (long long)off;
    if (np < 0 || np > d->fsize) return -1;
    d->mpos = np;
    return 0;
}
static long cmm_ov_mtell(void* ds) { return (long)((cmm_decoder*)ds)->mpos; }
static const ov_callbacks CMM_OV_CB_MEM = { cmm_ov_mread, cmm_ov_mseek, NULL, cmm_ov_mtell };

#ifdef _WIN32
/* Raw Win32 HANDLE stream (userData = the cmm_decoder; d->fh is the handle) —
 * race-free against concurrent opens (no msvcrt fd table), like the FLAC/Opus
 * HANDLE callbacks. cmm_vorbis_close owns the handle. */
static size_t cmm_ov_read_h(void* p, size_t size, size_t nmemb, void* ds)
{
    if (size == 0) return 0;
    HANDLE h = (HANDLE)((cmm_decoder*)ds)->fh;
    DWORD got = 0;
    if (!ReadFile(h, p, (DWORD)(size * nmemb), &got, NULL)) return 0;
    return (size_t)got / size;   /* complete elements */
}
static int cmm_ov_seek_h(void* ds, ogg_int64_t off, int whence)
{
    HANDLE h = (HANDLE)((cmm_decoder*)ds)->fh;
    LARGE_INTEGER dist; dist.QuadPart = off;
    DWORD method = (whence == SEEK_CUR) ? FILE_CURRENT
                 : (whence == SEEK_END) ? FILE_END : FILE_BEGIN;
    return SetFilePointerEx(h, dist, NULL, method) ? 0 : -1;
}
static long cmm_ov_tell_h(void* ds)
{
    HANDLE h = (HANDLE)((cmm_decoder*)ds)->fh;
    LARGE_INTEGER zero; zero.QuadPart = 0; LARGE_INTEGER pos;
    if (!SetFilePointerEx(h, zero, &pos, FILE_CURRENT)) return -1;
    return (long)pos.QuadPart;
}
static const ov_callbacks CMM_OV_CB_H = { cmm_ov_read_h, cmm_ov_seek_h, NULL, cmm_ov_tell_h };
#else
/* POSIX FILE* stream (datasource = the FILE*; cmm_vorbis_close fcloses it). */
static int  cmm_ov_fseek(void* ds, ogg_int64_t off, int whence) { return CMM_FSEEK((FILE*)ds, off, whence); }
static long cmm_ov_ftell(void* ds) { return (long)CMM_FTELL((FILE*)ds); }
static const ov_callbacks CMM_OV_CB_FP = { fread, cmm_ov_fseek, NULL, cmm_ov_ftell };
#endif

/* Finish opening: read stream info into the out-params. Shared by every open. */
static cmm_decoder* cmm_vorbis_finish(cmm_decoder* d, int* channels, int* sampleRate,
                                      long long* totalFrames)
{
    vorbis_info* vi = ov_info(d->vorbis, -1);
    d->channels = vi ? vi->channels : 0;
    if (channels)    *channels    = d->channels;
    if (sampleRate)  *sampleRate  = vi ? (int)vi->rate : 0;   /* native rate, preserved */
    if (totalFrames) *totalFrames = (long long)ov_pcm_total(d->vorbis, -1);
    return d;
}

CMM_API cmm_decoder* cmm_vorbis_open_memory(const unsigned char* data, long long len,
                                            int* channels, int* sampleRate,
                                            long long* totalFrames, int* err)
{
    if (err) *err = 0;
    if (!data || len <= 0) { if (err) *err = -1; return NULL; }

    /* Own a copy — the caller's buffer may be pooled/overwritten after this returns. */
    unsigned char* copy = (unsigned char*)malloc((size_t)len);
    if (!copy) { if (err) *err = -1; return NULL; }
    memcpy(copy, data, (size_t)len);

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { free(copy); if (err) *err = -1; return NULL; }
    d->kind = CMM_KIND_VORBIS; d->buf = copy; d->fsize = len; d->mpos = 0;
    d->vorbis = (OggVorbis_File*)calloc(1, sizeof(OggVorbis_File));
    if (!d->vorbis) { free(copy); free(d); if (err) *err = -1; return NULL; }

    int e = ov_open_callbacks(d, d->vorbis, NULL, 0, CMM_OV_CB_MEM);
    if (e < 0) { free(d->vorbis); free(copy); free(d); if (err) *err = e; return NULL; }
    return cmm_vorbis_finish(d, channels, sampleRate, totalFrames);
}

CMM_API int cmm_vorbis_read_float(cmm_decoder* d, float* out, int maxFrames)
{
    if (!d || !d->vorbis || !out || maxFrames <= 0) return 0;
    int ch = d->channels;
    /* ov_read_float returns non-interleaved channel pointers and only up to one
     * packet per call, so loop and interleave until the request is filled or EOF. */
    int total = 0;
    while (total < maxFrames) {
        float** pcm = NULL; int bs = 0;
        long got = ov_read_float(d->vorbis, &pcm, maxFrames - total, &bs);
        if (got <= 0) break;                       /* 0 = EOF, <0 = decode error */
        for (long i = 0; i < got; i++)
            for (int c = 0; c < ch; c++)
                out[(long)(total + i) * ch + c] = pcm[c][i];
        total += (int)got;
    }
    return total;
}

CMM_API int cmm_vorbis_seek(cmm_decoder* d, long long frame)
{
    if (!d || !d->vorbis) return -1;
    /* ov_pcm_seek returns 0 on success, nonzero on failure. */
    return ov_pcm_seek(d->vorbis, (ogg_int64_t)frame) == 0 ? 0 : -1;
}

CMM_API void cmm_vorbis_close(cmm_decoder* d)
{
    if (!d) return;
    if (d->vorbis) { ov_clear(d->vorbis); free(d->vorbis); }  /* close=NULL: we own the datasource */
    if (d->buf)    free(d->buf);
    if (d->fp)     fclose(d->fp);
#ifdef _WIN32
    if (d->fh)     CloseHandle((HANDLE)d->fh);
#endif
    free(d);
}

/* ── File-based opens (large-file path) ──────────────────────────────────────
 * Each opens a FILE* via cmm_open_path (wide/_wfopen for non-ASCII paths) and
 * hands it to the decoder; the cmm_decoder OWNS the FILE* and the matching
 * *_close fcloses it (the decoders are told NOT to close it themselves). The
 * returned handle is otherwise identical to a *_open_memory one — same read /
 * seek / close functions apply. */

/* opusfile FILE* callbacks (close=NULL: cmm_opus_close owns the handle). Used by the
 * POSIX big-file streaming path; on Windows the raw-HANDLE variants below are used. */
static int        cmm_op_read(void* s, unsigned char* p, int n) { return (int)fread(p, 1, (size_t)n, (FILE*)s); }
static int        cmm_op_seek(void* s, opus_int64 off, int whence) { return CMM_FSEEK((FILE*)s, off, whence); }
static opus_int64 cmm_op_tell(void* s) { return (opus_int64)CMM_FTELL((FILE*)s); }
static const OpusFileCallbacks CMM_OP_CB = { cmm_op_read, cmm_op_seek, cmm_op_tell, NULL };

#ifdef _WIN32
/* opusfile callbacks over a raw Win32 HANDLE (userData = the cmm_decoder; d->fh is the
 * handle) — the big-file STREAMING path, race-free against concurrent opens exactly like
 * the FLAC HANDLE callbacks (no msvcrt fd table). cmm_opus_close owns the handle. */
static int cmm_op_read_h(void* s, unsigned char* p, int n)
{
    if (n <= 0) return 0;
    HANDLE h = (HANDLE)((cmm_decoder*)s)->fh;
    DWORD got = 0;
    if (!ReadFile(h, p, (DWORD)n, &got, NULL)) return -1;   /* opus contract: <0 = error */
    return (int)got;                                        /* 0 = EOF */
}
static int cmm_op_seek_h(void* s, opus_int64 off, int whence)
{
    HANDLE h = (HANDLE)((cmm_decoder*)s)->fh;
    LARGE_INTEGER dist; dist.QuadPart = off;
    DWORD method = (whence == SEEK_CUR) ? FILE_CURRENT
                 : (whence == SEEK_END) ? FILE_END : FILE_BEGIN;
    return SetFilePointerEx(h, dist, NULL, method) ? 0 : -1;
}
static opus_int64 cmm_op_tell_h(void* s)
{
    HANDLE h = (HANDLE)((cmm_decoder*)s)->fh;
    LARGE_INTEGER zero; zero.QuadPart = 0; LARGE_INTEGER pos;
    if (!SetFilePointerEx(h, zero, &pos, FILE_CURRENT)) return -1;
    return (opus_int64)pos.QuadPart;
}
static const OpusFileCallbacks CMM_OP_CB_H = { cmm_op_read_h, cmm_op_seek_h, cmm_op_tell_h, NULL };
#endif /* _WIN32 */

CMM_API cmm_decoder* cmm_opus_open_file(const wchar_t* path, int* channels,
                                        int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;

    /* BUFFERED path (files ≤ CMM_BUFFER_MAX): decode from a whole-file native buffer read
     * via raw Win32 (race-free — see cmm_read_whole_file), referenced IN PLACE. No second
     * copy: op_open_memory keeps this buffer for the stream's life and cmm_opus_close frees
     * it. (cmm_opus_open_memory still copies — it can't assume it owns a caller's buffer.) */
    long long len = 0;
    unsigned char* buf = cmm_read_whole_file(path, CMM_BUFFER_MAX, &len);
    if (buf) {
        int e = 0;
        OggOpusFile* of = op_open_memory(buf, (size_t)len, &e);
        if (!of) { free(buf); if (err) *err = e; return NULL; }
        cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
        if (!d) { op_free(of); free(buf); if (err) *err = OP_EFAULT; return NULL; }
        d->kind = CMM_KIND_OPUS; d->of = of; d->buf = buf;
        d->channels = op_channel_count(of, -1);
        if (channels)    *channels    = d->channels;
        if (sampleRate)  *sampleRate  = 48000;            /* Opus always decodes to 48 kHz */
        if (totalFrames) *totalFrames = (long long)op_pcm_total(of, -1);
        return d;
    }

    /* STREAMING path (files > CMM_BUFFER_MAX): stream via callbacks so a very long Opus file
     * isn't held whole in RAM (mirrors the FLAC big-file path). Windows streams over a RAW
     * Win32 HANDLE (no msvcrt fd table — race-free); POSIX keeps the plain FILE*. */
#ifdef _WIN32
    HANDLE h = CreateFileW(path, GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) { if (err) *err = OP_EFAULT; return NULL; }
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(h, &sz)) { CloseHandle(h); if (err) *err = OP_EFAULT; return NULL; }
    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { CloseHandle(h); if (err) *err = OP_EFAULT; return NULL; }
    d->fh = (void*)h; d->fsize = sz.QuadPart;   /* set BEFORE op_open — it reads via the callbacks */
    int e = 0;
    OggOpusFile* of = op_open_callbacks(d, &CMM_OP_CB_H, NULL, 0, &e);
    if (!of) { CloseHandle(h); free(d); if (err) *err = e; return NULL; }
#else
    FILE* fp = cmm_open_path(path);
    if (!fp) { if (err) *err = OP_EFAULT; return NULL; }
    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { fclose(fp); if (err) *err = OP_EFAULT; return NULL; }
    d->fp = fp;
    int e = 0;
    OggOpusFile* of = op_open_callbacks(fp, &CMM_OP_CB, NULL, 0, &e);
    if (!of) { fclose(fp); free(d); if (err) *err = e; return NULL; }
#endif
    d->kind = CMM_KIND_OPUS; d->of = of;
    d->channels = op_channel_count(of, -1);
    if (channels)    *channels    = d->channels;
    if (sampleRate)  *sampleRate  = 48000;
    if (totalFrames) *totalFrames = (long long)op_pcm_total(of, -1);
    return d;
}

/* dr_flac FILE* callbacks (cmm_flac_close owns the handle). */
static size_t cmm_drf_read(void* ud, void* out, size_t n) { return fread(out, 1, n, (FILE*)ud); }
static drflac_bool32 cmm_drf_seek(void* ud, int offset, drflac_seek_origin origin)
{
    FILE* f = (FILE*)ud;
    int w = (origin == DRFLAC_SEEK_CUR) ? SEEK_CUR
          : (origin == DRFLAC_SEEK_END) ? SEEK_END
          : SEEK_SET;
    if (CMM_FSEEK(f, offset, w) != 0) return DRFLAC_FALSE;

    /* dr_flac REQUIRES a seek past the end of the stream to be reported as
     * failure (dr_flac.h: drflac_seek_proc contract). _fseeki64/fseek on Windows
     * happily seek past EOF and return success, so we must detect it ourselves:
     * dr_flac's binary search speculatively seeks to estimated byte offsets that
     * routinely land past EOF and relies on this callback failing there. Reporting
     * success instead makes it frame-sync on a stale buffer and crash in the frame
     * decoder. Verify the resulting position is within the file. */
    {
        long long cur = CMM_FTELL(f);
        long long end;
        if (cur < 0)                        return DRFLAC_FALSE;
        if (CMM_FSEEK(f, 0, SEEK_END) != 0) return DRFLAC_FALSE;
        end = CMM_FTELL(f);
        if (CMM_FSEEK(f, cur, SEEK_SET) != 0) return DRFLAC_FALSE;
        if (end >= 0 && cur > end)          return DRFLAC_FALSE;
    }
    return DRFLAC_TRUE;
}
static drflac_bool32 cmm_drf_tell(void* ud, drflac_int64* cursor)
{
    drflac_int64 pos = (drflac_int64)CMM_FTELL((FILE*)ud);
    if (pos < 0) return DRFLAC_FALSE;
    *cursor = pos;
    return DRFLAC_TRUE;
}

#ifdef _WIN32
/* dr_flac callbacks over a raw Win32 HANDLE (userData = the cmm_decoder; d->fh is the
 * handle, d->fsize the cached size). Used for the big-file STREAMING path so it never
 * touches msvcrt's stdio FILE handle or fd table — the concurrent create-vs-read race that silently
 * un-normalized FLACs lives entirely in that stdio layer; ReadFile / SetFilePointerEx
 * take no shared CRT state and run fully in parallel. cmm_flac_close owns the handle. */
static size_t cmm_drf_read_h(void* ud, void* out, size_t n)
{
    HANDLE h = (HANDLE)((cmm_decoder*)ud)->fh;
    size_t done = 0;
    while (done < n) {
        size_t remain = n - done;
        DWORD  want   = remain > (1u << 30) ? (1u << 30) : (DWORD)remain;
        DWORD  got    = 0;
        if (!ReadFile(h, (unsigned char*)out + done, want, &got, NULL)) break;
        if (got == 0) break;   /* EOF */
        done += got;
    }
    return done;
}
static drflac_bool32 cmm_drf_seek_h(void* ud, int offset, drflac_seek_origin origin)
{
    cmm_decoder* d = (cmm_decoder*)ud;
    LARGE_INTEGER dist; dist.QuadPart = offset;
    DWORD method = (origin == DRFLAC_SEEK_CUR) ? FILE_CURRENT
                 : (origin == DRFLAC_SEEK_END) ? FILE_END
                 : FILE_BEGIN;
    /* dr_flac addresses a stream that begins at the fLaC marker; the file may begin with an
     * ID3 tag before it. Only an absolute seek needs translating -- CUR is already relative and
     * END is the same end either way. */
    if (method == FILE_BEGIN) dist.QuadPart += d->baseOff;
    LARGE_INTEGER newpos;
    if (!SetFilePointerEx((HANDLE)d->fh, dist, &newpos, method)) return DRFLAC_FALSE;
    /* dr_flac REQUIRES a seek past the end of the stream to be reported as failure
     * (its binary search speculatively probes past EOF). SetFilePointerEx happily
     * sets the pointer past the end and returns success, so reject it ourselves
     * against the size captured at open — same contract the FILE* callback enforced. */
    if (d->fsize >= 0 && newpos.QuadPart > d->fsize) return DRFLAC_FALSE;
    return DRFLAC_TRUE;
}
static drflac_bool32 cmm_drf_tell_h(void* ud, drflac_int64* cursor)
{
    LARGE_INTEGER zero; zero.QuadPart = 0;
    LARGE_INTEGER pos;
    if (!SetFilePointerEx((HANDLE)((cmm_decoder*)ud)->fh, zero, &pos, FILE_CURRENT))
        return DRFLAC_FALSE;
    *cursor = (drflac_int64)(pos.QuadPart - ((cmm_decoder*)ud)->baseOff);   /* logical, not file, position */
    return DRFLAC_TRUE;
}
#endif /* _WIN32 */

/* Read a whole file into a malloc'd native buffer via RAW Win32 (CreateFileW +
 * ReadFile + CloseHandle) — NOT the msvcrt stdio stack. This deliberately avoids
 * _open_osfhandle/_wfdopen/fread: msvcrt's fd table + FILE* buffer setup is not
 * safe to run concurrently (see cmm_openlock), and the failure is a concurrent
 * FILE* CREATE crossing another thread's FILE* READ — which no open-only lock can
 * cover, since decode/measure reads run unlocked for hundreds of ms. Win32
 * CreateFile/ReadFile take no shared CRT state, so any number of these run in
 * parallel safely. Caller owns the buffer (free it). NULL on error/empty/too big.
 * Keeps FILE_SHARE_DELETE so a played/measured track can still be moved/deleted. */
static unsigned char* cmm_read_whole_file(const wchar_t* path, long long maxBytes, long long* outLen)
{
    if (outLen) *outLen = 0;
    if (!path) return NULL;
#ifdef _WIN32
    HANDLE h = CreateFileW(path, GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return NULL;
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(h, &sz) || sz.QuadPart <= 0 || sz.QuadPart > 0x7FFFFFFFLL) {
        CloseHandle(h); return NULL;   /* empty or >2GiB */
    }
    if (maxBytes > 0 && sz.QuadPart > maxBytes) { CloseHandle(h); return NULL; }   /* caller streams */
    unsigned char* buf = (unsigned char*)malloc((size_t)sz.QuadPart);
    if (!buf) { CloseHandle(h); return NULL; }
    long long off = 0;
    while (off < sz.QuadPart) {
        long long rem = sz.QuadPart - off;
        DWORD toRead = (DWORD)(rem > 0x08000000LL ? 0x08000000LL : rem);   /* 128 MB chunks */
        DWORD got = 0;
        if (!ReadFile(h, buf + off, toRead, &got, NULL) || got == 0) {
            free(buf); CloseHandle(h); return NULL;
        }
        off += got;
    }
    CloseHandle(h);
    if (outLen) *outLen = sz.QuadPart;
    return buf;
#else
    char nb[4096];
    if (wcstombs(nb, path, sizeof(nb)) == (size_t)-1) return NULL;
    FILE* f = fopen(nb, "rb");
    if (!f) return NULL;
    if (fseeko(f, 0, SEEK_END) != 0) { fclose(f); return NULL; }
    long long sz = (long long)ftello(f);
    if (sz <= 0 || fseeko(f, 0, SEEK_SET) != 0) { fclose(f); return NULL; }
    if (maxBytes > 0 && sz > maxBytes) { fclose(f); return NULL; }   /* caller streams */
    unsigned char* buf = (unsigned char*)malloc((size_t)sz);
    if (!buf) { fclose(f); return NULL; }
    if (fread(buf, 1, (size_t)sz, f) != (size_t)sz) { free(buf); fclose(f); return NULL; }
    fclose(f);
    if (outLen) *outLen = sz;
    return buf;
#endif
}

/* ── ID3-PREFIXED FLAC ────────────────────────────────────────────────────
 * A FLAC stream is supposed to begin with "fLaC". Plenty of taggers in the wild write an
 * ID3v2 tag in front of one anyway, and the result plays fine in ffmpeg, foobar, VLC --
 * but dr_flac looks for the marker at offset 0, finds "ID3", and refuses to open. The file
 * is not corrupt; the decoder is just starting in the wrong place.
 *
 * That silently cost 146 of 9,839 FLACs (1.5%) in one real library -- entire albums, since
 * whatever wrote the tags did a whole rip at a time. The only sign was one
 * `Loudness OPEN FAILED ... err=-2` line per attempt, which reads like a broken file.
 *
 * ID3v2 header: "ID3", 2 version bytes, 1 flags byte, then 4 SYNCSAFE size bytes (7 bits
 * each, high bit always clear so the pattern can never look like an MPEG sync word). The
 * size counts the tag body only, so audio starts at 10 + size. Flag bit 4 adds a 10-byte
 * footer. Returns the offset to start decoding at, or 0 when there is no tag.
 *
 * Every caller must still verify "fLaC" is actually there: a plausible-looking header on a
 * genuinely corrupt file must not send us seeking into the middle of nowhere. */
static long long cmm_id3_prefix_len(const unsigned char* head, long long avail)
{
    if (!head || avail < 10) return 0;
    if (head[0] != 'I' || head[1] != 'D' || head[2] != '3') return 0;
    if (head[3] == 0xFF || head[4] == 0xFF) return 0;             /* invalid version */
    /* Syncsafe: any high bit set means this is not a real ID3v2 size. */
    if ((head[6] | head[7] | head[8] | head[9]) & 0x80) return 0;
    long long size = ((long long)head[6] << 21) | ((long long)head[7] << 14)
                   | ((long long)head[8] <<  7) | ((long long)head[9]);
    long long off = 10 + size;
    if (head[5] & 0x10) off += 10;                                /* footer present */
    if (off < 0 || (avail > 0 && off >= avail)) return 0;         /* nonsense: leave it alone */
    return off;
}

/* The offset a FLAC decode should start at: past any ID3 tag, and only if "fLaC" is really
 * there. `avail` is the whole file length when known, or 0 for a stream we cannot bound. */
static long long cmm_flac_start_offset(const unsigned char* head, long long headLen, long long avail)
{
    long long off = cmm_id3_prefix_len(head, avail ? avail : headLen);
    if (off <= 0) return 0;
    if (headLen >= off + 4) {   /* we can confirm from the buffer we already hold */
        const unsigned char* m = head + off;
        if (!(m[0] == 'f' && m[1] == 'L' && m[2] == 'a' && m[3] == 'C')) return 0;
    }
    return off;
}

CMM_API cmm_decoder* cmm_flac_open_file(const wchar_t* path, int* channels,
                                        int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;

    /* BUFFERED path (files ≤ CMM_BUFFER_MAX): decode from a whole-file native buffer,
     * NOT a streaming msvcrt FILE*. The loudness prescan opens many FLACs in parallel
     * and each measure/decode does long unlocked reads; concurrent msvcrt FILE* create-
     * vs-read crosses buffers so drflac_open returns NULL (~18-38% of concurrent opens
     * → tracks silently un-normalized). Raw Win32 read + in-memory decode removes the
     * FILE* — and with it the fd-table race — entirely, and holds nothing open so the
     * folder isn't pinned (better hot-swap). Measured files are ALWAYS here (the measure
     * skips > CMM_BUFFER_MAX). drflac reads on demand from `buf`; cmm_flac_close frees it. */
    long long len = 0;
    unsigned char* buf = cmm_read_whole_file(path, CMM_BUFFER_MAX, &len);
    if (buf)
    {
        /* Start past an ID3 tag if one is in front of the marker. drflac keeps referencing the
         * buffer, so hand it buf+off while d->buf keeps the BASE pointer that must be freed. */
        long long off = cmm_flac_start_offset(buf, len, len);
        drflac* f = drflac_open_memory(buf + off, (size_t)(len - off), NULL);
        if (!f) { free(buf); if (err) *err = -2; return NULL; }   /* corrupt: don't retry via stream */

        cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
        if (!d) { drflac_close(f); free(buf); if (err) *err = -3; return NULL; }

        d->kind = CMM_KIND_FLAC; d->of = NULL; d->flac = f; d->vorbis = NULL;
        d->buf = buf;      /* owned; drflac references it for its life */
        d->fp = NULL;
        d->channels = (int)f->channels;
        if (channels)    *channels    = d->channels;
        if (sampleRate)  *sampleRate  = (int)f->sampleRate;
        if (totalFrames) *totalFrames = (long long)f->totalPCMFrameCount;
        return d;
    }

    /* STREAMING path (files > CMM_BUFFER_MAX, e.g. the 100 MB+ ambient tracks): buffering
     * would hold the whole file in RAM for the song, so stream instead — a few KB of decode
     * state regardless of file size. On Windows this streams over a RAW Win32 HANDLE (no
     * msvcrt stdio FILE handle or fd table), so it is race-free against concurrent opens AND against the
     * playing track's reads — that is what lets the loudness measure normalize files of ANY
     * size safely (no size cap needed). FILE_SHARE_DELETE keeps the file movable for hot-swap
     * while it plays. POSIX has no fd-table race, so it keeps the plain FILE* stream. */
#ifdef _WIN32
    HANDLE h = CreateFileW(path, GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) { if (err) *err = -1; return NULL; }
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(h, &sz)) { CloseHandle(h); if (err) *err = -1; return NULL; }

    cmm_decoder* ds = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!ds) { CloseHandle(h); if (err) *err = -3; return NULL; }
    ds->fh = (void*)h; ds->fsize = sz.QuadPart;   /* set BEFORE drflac_open — it reads via the callbacks */

    /* Sniff for an ID3 tag in front of the fLaC marker (see cmm_flac_start_offset). The 14-byte
     * read covers the 10-byte ID3 header; the marker itself sits past the tag body, too far to
     * confirm from here, so pass headLen = 10 and let the offset stand on the header alone -- a
     * wrong guess simply fails the open below exactly as it does today. */
    {
        unsigned char head[14]; DWORD got = 0;
        if (ReadFile(h, head, sizeof(head), &got, NULL) && got >= 10)
            ds->baseOff = cmm_flac_start_offset(head, 10, ds->fsize);
        LARGE_INTEGER start; start.QuadPart = ds->baseOff;
        SetFilePointerEx(h, start, NULL, FILE_BEGIN);
    }

    drflac* fs = drflac_open(cmm_drf_read_h, cmm_drf_seek_h, cmm_drf_tell_h, ds, NULL);
    if (!fs) { CloseHandle(h); free(ds); if (err) *err = -2; return NULL; }
    ds->kind = CMM_KIND_FLAC; ds->of = NULL; ds->flac = fs; ds->vorbis = NULL;
    ds->buf = NULL; ds->fp = NULL;
#else
    FILE* fp = cmm_open_path(path);
    if (!fp) { if (err) *err = -1; return NULL; }
    drflac* fs = drflac_open(cmm_drf_read, cmm_drf_seek, cmm_drf_tell, fp, NULL);
    if (!fs) { fclose(fp); if (err) *err = -2; return NULL; }

    cmm_decoder* ds = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!ds) { drflac_close(fs); fclose(fp); if (err) *err = -3; return NULL; }
    ds->kind = CMM_KIND_FLAC; ds->of = NULL; ds->flac = fs; ds->vorbis = NULL;
    ds->buf = NULL; ds->fp = fp;
#endif
    ds->channels = (int)fs->channels;
    if (channels)    *channels    = ds->channels;
    if (sampleRate)  *sampleRate  = (int)fs->sampleRate;
    if (totalFrames) *totalFrames = (long long)fs->totalPCMFrameCount;
    return ds;
}

CMM_API cmm_decoder* cmm_vorbis_open_file(const wchar_t* path, int* channels,
                                          int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;

    /* BUFFERED path (files ≤ CMM_BUFFER_MAX): decode from a whole-file native buffer read
     * via raw Win32 (race-free), referenced IN PLACE by the in-memory ov_callbacks; no
     * second copy — cmm_vorbis_close frees it. (cmm_vorbis_open_memory still copies: it
     * can't assume it owns the caller's buffer.) */
    long long len = 0;
    unsigned char* buf = cmm_read_whole_file(path, CMM_BUFFER_MAX, &len);
    if (buf) {
        cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
        if (!d) { free(buf); if (err) *err = -1; return NULL; }
        d->kind = CMM_KIND_VORBIS; d->buf = buf; d->fsize = len; d->mpos = 0;
        d->vorbis = (OggVorbis_File*)calloc(1, sizeof(OggVorbis_File));
        if (!d->vorbis) { free(buf); free(d); if (err) *err = -1; return NULL; }
        int e = ov_open_callbacks(d, d->vorbis, NULL, 0, CMM_OV_CB_MEM);
        if (e < 0) { free(d->vorbis); free(buf); free(d); if (err) *err = e; return NULL; }
        return cmm_vorbis_finish(d, channels, sampleRate, totalFrames);
    }

    /* STREAMING path (files > CMM_BUFFER_MAX): stream via ov_callbacks so a very long Vorbis
     * file isn't held whole in RAM (mirrors the FLAC/Opus big-file path). Windows streams
     * over a RAW Win32 HANDLE (no msvcrt fd table — race-free); POSIX keeps the FILE*. */
#ifdef _WIN32
    HANDLE h = CreateFileW(path, GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) { if (err) *err = -1; return NULL; }
    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { CloseHandle(h); if (err) *err = -1; return NULL; }
    d->kind = CMM_KIND_VORBIS; d->fh = (void*)h;   /* set BEFORE ov_open — it reads via the callbacks */
    d->vorbis = (OggVorbis_File*)calloc(1, sizeof(OggVorbis_File));
    if (!d->vorbis) { CloseHandle(h); free(d); if (err) *err = -1; return NULL; }
    int e = ov_open_callbacks(d, d->vorbis, NULL, 0, CMM_OV_CB_H);
    if (e < 0) { free(d->vorbis); CloseHandle(h); free(d); if (err) *err = e; return NULL; }
#else
    FILE* fp = cmm_open_path(path);
    if (!fp) { if (err) *err = -1; return NULL; }
    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { fclose(fp); if (err) *err = -1; return NULL; }
    d->kind = CMM_KIND_VORBIS; d->fp = fp;
    d->vorbis = (OggVorbis_File*)calloc(1, sizeof(OggVorbis_File));
    if (!d->vorbis) { fclose(fp); free(d); if (err) *err = -1; return NULL; }
    int e = ov_open_callbacks(fp, d->vorbis, NULL, 0, CMM_OV_CB_FP);
    if (e < 0) { free(d->vorbis); fclose(fp); free(d); if (err) *err = e; return NULL; }
#endif
    return cmm_vorbis_finish(d, channels, sampleRate, totalFrames);
}

/* ── MP3 (dr_mp3) ─────────────────────────────────────────────────────────── */

CMM_API cmm_decoder* cmm_mp3_open_memory(const unsigned char* data, long long len,
                                         int* channels, int* sampleRate,
                                         long long* totalFrames, int* err)
{
    if (err) *err = 0;
    if (!data || len <= 0) { if (err) *err = -1; return NULL; }

    /* dr_mp3 reads the buffer on demand for the life of the decoder, so we own
     * a private copy — the caller's buffer may be pooled/overwritten. */
    unsigned char* copy = (unsigned char*)malloc((size_t)len);
    if (!copy) { if (err) *err = -1; return NULL; }
    memcpy(copy, data, (size_t)len);

    drmp3* m = (drmp3*)malloc(sizeof(drmp3));
    if (!m) { free(copy); if (err) *err = -1; return NULL; }
    if (!drmp3_init_memory(m, copy, (size_t)len, NULL)) {
        free(m); free(copy); if (err) *err = -1; return NULL;
    }

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { drmp3_uninit(m); free(m); free(copy); if (err) *err = -1; return NULL; }

    d->kind     = CMM_KIND_MP3;
    d->mp3      = m;
    d->buf      = copy;
    d->channels = (int)m->channels;

    if (channels)    *channels    = (int)m->channels;
    if (sampleRate)  *sampleRate  = (int)m->sampleRate;          /* native rate, preserved */
    /* Exact count via a one-pass scan (dr_mp3 seeks back to start afterwards),
     * matching NAudio's Mp3FileReader, which indexed the whole file at open. */
    if (totalFrames) *totalFrames = (long long)drmp3_get_pcm_frame_count(m);
    return d;
}

CMM_API int cmm_mp3_read_float(cmm_decoder* d, float* out, int maxFrames)
{
    if (!d || !d->mp3 || !out || maxFrames <= 0) return 0;
    return (int)drmp3_read_pcm_frames_f32(d->mp3, (drmp3_uint64)maxFrames, out);
}

CMM_API int cmm_mp3_seek(cmm_decoder* d, long long frame)
{
    if (!d || !d->mp3) return -1;
    return drmp3_seek_to_pcm_frame(d->mp3, (drmp3_uint64)frame) ? 0 : -1;
}

CMM_API void cmm_mp3_close(cmm_decoder* d)
{
    if (!d) return;
    if (d->mp3) { drmp3_uninit(d->mp3); free(d->mp3); }
    if (d->buf) free(d->buf);
    if (d->fp)  fclose(d->fp);
#ifdef _WIN32
    if (d->fh)  CloseHandle((HANDLE)d->fh);   /* Windows streamed-MP3 handle */
#endif
    free(d);
}

/* ── WAV / AIFF (dr_wav) ──────────────────────────────────────────────────── */

CMM_API cmm_decoder* cmm_wav_open_memory(const unsigned char* data, long long len,
                                         int* channels, int* sampleRate,
                                         long long* totalFrames, int* err)
{
    if (err) *err = 0;
    if (!data || len <= 0) { if (err) *err = -1; return NULL; }

    unsigned char* copy = (unsigned char*)malloc((size_t)len);
    if (!copy) { if (err) *err = -1; return NULL; }
    memcpy(copy, data, (size_t)len);

    drwav* w = (drwav*)malloc(sizeof(drwav));
    if (!w) { free(copy); if (err) *err = -1; return NULL; }
    if (!drwav_init_memory(w, copy, (size_t)len, NULL)) {
        free(w); free(copy); if (err) *err = -1; return NULL;
    }

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { drwav_uninit(w); free(w); free(copy); if (err) *err = -1; return NULL; }

    d->kind     = CMM_KIND_WAV;
    d->wav      = w;
    d->buf      = copy;
    d->channels = (int)w->channels;

    if (channels)    *channels    = (int)w->channels;
    if (sampleRate)  *sampleRate  = (int)w->sampleRate;          /* native rate, preserved */
    if (totalFrames) *totalFrames = (long long)w->totalPCMFrameCount;
    return d;
}

CMM_API int cmm_wav_read_float(cmm_decoder* d, float* out, int maxFrames)
{
    if (!d || !d->wav || !out || maxFrames <= 0) return 0;
    /* dr_wav converts any source sample format (PCM 8/16/24/32, float, ADPCM…)
     * to interleaved f32 here, so the managed side always sees IEEE float. */
    return (int)drwav_read_pcm_frames_f32(d->wav, (drwav_uint64)maxFrames, out);
}

CMM_API int cmm_wav_seek(cmm_decoder* d, long long frame)
{
    if (!d || !d->wav) return -1;
    return drwav_seek_to_pcm_frame(d->wav, (drwav_uint64)frame) ? 0 : -1;
}

CMM_API void cmm_wav_close(cmm_decoder* d)
{
    if (!d) return;
    if (d->wav) { drwav_uninit(d->wav); free(d->wav); }
    if (d->buf) free(d->buf);
    if (d->fp)  fclose(d->fp);
#ifdef _WIN32
    if (d->fh)  CloseHandle((HANDLE)d->fh);   /* Windows streamed-WAV handle */
#endif
    free(d);
}

/* ── MP3 / WAV file opens (large-file path) ───────────────────────────────────
 * Same FILE*-via-_wfopen + callbacks pattern as the FLAC file open above; the
 * cmm_decoder OWNS the FILE* and the matching *_close fcloses it. */

/* dr_mp3 FILE* callbacks (cmm_mp3_close owns the handle). */
static size_t       cmm_drm_read(void* ud, void* out, size_t n) { return fread(out, 1, n, (FILE*)ud); }
static drmp3_bool32 cmm_drm_seek(void* ud, int offset, drmp3_seek_origin origin)
{
    int w = (origin == DRMP3_SEEK_CUR) ? SEEK_CUR
          : (origin == DRMP3_SEEK_END) ? SEEK_END
          : SEEK_SET;
    return CMM_FSEEK((FILE*)ud, offset, w) == 0 ? DRMP3_TRUE : DRMP3_FALSE;
}
static drmp3_bool32 cmm_drm_tell(void* ud, drmp3_int64* cursor)
{
    drmp3_int64 pos = (drmp3_int64)CMM_FTELL((FILE*)ud);
    if (pos < 0) return DRMP3_FALSE;
    *cursor = pos;
    return DRMP3_TRUE;
}

#ifdef _WIN32
/* dr_mp3 callbacks over a raw Win32 HANDLE (userData = the cmm_decoder) — the big-file
 * STREAMING path, race-free like the FLAC HANDLE callbacks. cmm_mp3_close owns the handle. */
static size_t cmm_drm_read_h(void* ud, void* out, size_t n)
{
    HANDLE h = (HANDLE)((cmm_decoder*)ud)->fh;
    size_t done = 0;
    while (done < n) {
        size_t remain = n - done;
        DWORD  want   = remain > (1u << 30) ? (1u << 30) : (DWORD)remain;
        DWORD  got    = 0;
        if (!ReadFile(h, (unsigned char*)out + done, want, &got, NULL) || got == 0) break;
        done += got;
    }
    return done;
}
static drmp3_bool32 cmm_drm_seek_h(void* ud, int offset, drmp3_seek_origin origin)
{
    LARGE_INTEGER dist; dist.QuadPart = offset;
    DWORD method = (origin == DRMP3_SEEK_CUR) ? FILE_CURRENT
                 : (origin == DRMP3_SEEK_END) ? FILE_END : FILE_BEGIN;
    return SetFilePointerEx((HANDLE)((cmm_decoder*)ud)->fh, dist, NULL, method) ? DRMP3_TRUE : DRMP3_FALSE;
}
static drmp3_bool32 cmm_drm_tell_h(void* ud, drmp3_int64* cursor)
{
    LARGE_INTEGER zero; zero.QuadPart = 0; LARGE_INTEGER pos;
    if (!SetFilePointerEx((HANDLE)((cmm_decoder*)ud)->fh, zero, &pos, FILE_CURRENT)) return DRMP3_FALSE;
    *cursor = (drmp3_int64)pos.QuadPart;
    return DRMP3_TRUE;
}
#endif /* _WIN32 */

CMM_API cmm_decoder* cmm_mp3_open_file(const wchar_t* path, int* channels,
                                       int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;

    /* BUFFERED path (files ≤ CMM_BUFFER_MAX): whole-file native buffer (raw Win32 — race-
     * free), referenced IN PLACE by dr_mp3; cmm_mp3_close frees it. No second copy. */
    long long len = 0;
    unsigned char* buf = cmm_read_whole_file(path, CMM_BUFFER_MAX, &len);
    if (buf) {
        drmp3* m = (drmp3*)malloc(sizeof(drmp3));
        if (!m) { free(buf); if (err) *err = -1; return NULL; }
        if (!drmp3_init_memory(m, buf, (size_t)len, NULL)) { free(m); free(buf); if (err) *err = -1; return NULL; }
        cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
        if (!d) { drmp3_uninit(m); free(m); free(buf); if (err) *err = -1; return NULL; }
        d->kind = CMM_KIND_MP3; d->mp3 = m; d->buf = buf; d->channels = (int)m->channels;
        if (channels)    *channels    = (int)m->channels;
        if (sampleRate)  *sampleRate  = (int)m->sampleRate;
        if (totalFrames) *totalFrames = (long long)drmp3_get_pcm_frame_count(m);
        return d;
    }

    /* STREAMING path (files > CMM_BUFFER_MAX). Windows: raw Win32 HANDLE. POSIX: FILE*. */
#ifdef _WIN32
    HANDLE h = CreateFileW(path, GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) { if (err) *err = -1; return NULL; }
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(h, &sz)) { CloseHandle(h); if (err) *err = -1; return NULL; }
    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { CloseHandle(h); if (err) *err = -1; return NULL; }
    d->fh = (void*)h; d->fsize = sz.QuadPart;   /* set BEFORE drmp3_init — it reads via the callbacks */
    drmp3* m = (drmp3*)malloc(sizeof(drmp3));
    if (!m) { CloseHandle(h); free(d); if (err) *err = -1; return NULL; }
    if (!drmp3_init(m, cmm_drm_read_h, cmm_drm_seek_h, cmm_drm_tell_h, NULL, d, NULL)) {
        free(m); CloseHandle(h); free(d); if (err) *err = -1; return NULL;
    }
#else
    FILE* fp = cmm_open_path(path);
    if (!fp) { if (err) *err = -1; return NULL; }
    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { fclose(fp); if (err) *err = -1; return NULL; }
    d->fp = fp;
    drmp3* m = (drmp3*)malloc(sizeof(drmp3));
    if (!m) { fclose(fp); free(d); if (err) *err = -1; return NULL; }
    if (!drmp3_init(m, cmm_drm_read, cmm_drm_seek, cmm_drm_tell, NULL, fp, NULL)) {
        free(m); fclose(fp); free(d); if (err) *err = -1; return NULL;
    }
#endif
    d->kind = CMM_KIND_MP3; d->mp3 = m; d->channels = (int)m->channels;
    if (channels)    *channels    = (int)m->channels;
    if (sampleRate)  *sampleRate  = (int)m->sampleRate;
    if (totalFrames) *totalFrames = (long long)drmp3_get_pcm_frame_count(m);
    return d;
}

/* dr_wav FILE* callbacks (cmm_wav_close owns the handle). */
static size_t       cmm_drw_read(void* ud, void* out, size_t n) { return fread(out, 1, n, (FILE*)ud); }
static drwav_bool32 cmm_drw_seek(void* ud, int offset, drwav_seek_origin origin)
{
    int w = (origin == DRWAV_SEEK_CUR) ? SEEK_CUR
          : (origin == DRWAV_SEEK_END) ? SEEK_END
          : SEEK_SET;
    return CMM_FSEEK((FILE*)ud, offset, w) == 0 ? DRWAV_TRUE : DRWAV_FALSE;
}
static drwav_bool32 cmm_drw_tell(void* ud, drwav_int64* cursor)
{
    drwav_int64 pos = (drwav_int64)CMM_FTELL((FILE*)ud);
    if (pos < 0) return DRWAV_FALSE;
    *cursor = pos;
    return DRWAV_TRUE;
}

#ifdef _WIN32
/* dr_wav callbacks over a raw Win32 HANDLE (userData = the cmm_decoder) — the big-file
 * STREAMING path (uncompressed WAV is the format most likely to exceed CMM_BUFFER_MAX),
 * race-free like the FLAC HANDLE callbacks. cmm_wav_close owns the handle. */
static size_t cmm_drw_read_h(void* ud, void* out, size_t n)
{
    HANDLE h = (HANDLE)((cmm_decoder*)ud)->fh;
    size_t done = 0;
    while (done < n) {
        size_t remain = n - done;
        DWORD  want   = remain > (1u << 30) ? (1u << 30) : (DWORD)remain;
        DWORD  got    = 0;
        if (!ReadFile(h, (unsigned char*)out + done, want, &got, NULL) || got == 0) break;
        done += got;
    }
    return done;
}
static drwav_bool32 cmm_drw_seek_h(void* ud, int offset, drwav_seek_origin origin)
{
    LARGE_INTEGER dist; dist.QuadPart = offset;
    DWORD method = (origin == DRWAV_SEEK_CUR) ? FILE_CURRENT
                 : (origin == DRWAV_SEEK_END) ? FILE_END : FILE_BEGIN;
    return SetFilePointerEx((HANDLE)((cmm_decoder*)ud)->fh, dist, NULL, method) ? DRWAV_TRUE : DRWAV_FALSE;
}
static drwav_bool32 cmm_drw_tell_h(void* ud, drwav_int64* cursor)
{
    LARGE_INTEGER zero; zero.QuadPart = 0; LARGE_INTEGER pos;
    if (!SetFilePointerEx((HANDLE)((cmm_decoder*)ud)->fh, zero, &pos, FILE_CURRENT)) return DRWAV_FALSE;
    *cursor = (drwav_int64)pos.QuadPart;
    return DRWAV_TRUE;
}
#endif /* _WIN32 */

CMM_API cmm_decoder* cmm_wav_open_file(const wchar_t* path, int* channels,
                                       int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;

    /* BUFFERED path (files ≤ CMM_BUFFER_MAX): whole-file native buffer (raw Win32 — race-
     * free), referenced IN PLACE by dr_wav; cmm_wav_close frees it. No second copy. */
    long long len = 0;
    unsigned char* buf = cmm_read_whole_file(path, CMM_BUFFER_MAX, &len);
    if (buf) {
        drwav* w = (drwav*)malloc(sizeof(drwav));
        if (!w) { free(buf); if (err) *err = -1; return NULL; }
        if (!drwav_init_memory(w, buf, (size_t)len, NULL)) { free(w); free(buf); if (err) *err = -1; return NULL; }
        cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
        if (!d) { drwav_uninit(w); free(w); free(buf); if (err) *err = -1; return NULL; }
        d->kind = CMM_KIND_WAV; d->wav = w; d->buf = buf; d->channels = (int)w->channels;
        if (channels)    *channels    = (int)w->channels;
        if (sampleRate)  *sampleRate  = (int)w->sampleRate;
        if (totalFrames) *totalFrames = (long long)w->totalPCMFrameCount;
        return d;
    }

    /* STREAMING path (files > CMM_BUFFER_MAX). Windows: raw Win32 HANDLE. POSIX: FILE*. */
#ifdef _WIN32
    HANDLE h = CreateFileW(path, GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) { if (err) *err = -1; return NULL; }
    LARGE_INTEGER sz;
    if (!GetFileSizeEx(h, &sz)) { CloseHandle(h); if (err) *err = -1; return NULL; }
    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { CloseHandle(h); if (err) *err = -1; return NULL; }
    d->fh = (void*)h; d->fsize = sz.QuadPart;   /* set BEFORE drwav_init — it reads via the callbacks */
    drwav* w = (drwav*)malloc(sizeof(drwav));
    if (!w) { CloseHandle(h); free(d); if (err) *err = -1; return NULL; }
    if (!drwav_init(w, cmm_drw_read_h, cmm_drw_seek_h, cmm_drw_tell_h, d, NULL)) {
        free(w); CloseHandle(h); free(d); if (err) *err = -1; return NULL;
    }
#else
    FILE* fp = cmm_open_path(path);
    if (!fp) { if (err) *err = -1; return NULL; }
    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { fclose(fp); if (err) *err = -1; return NULL; }
    d->fp = fp;
    drwav* w = (drwav*)malloc(sizeof(drwav));
    if (!w) { fclose(fp); free(d); if (err) *err = -1; return NULL; }
    if (!drwav_init(w, cmm_drw_read, cmm_drw_seek, cmm_drw_tell, fp, NULL)) {
        free(w); fclose(fp); free(d); if (err) *err = -1; return NULL;
    }
#endif
    d->kind = CMM_KIND_WAV; d->wav = w; d->channels = (int)w->channels;
    if (channels)    *channels    = (int)w->channels;
    if (sampleRate)  *sampleRate  = (int)w->sampleRate;
    if (totalFrames) *totalFrames = (long long)w->totalPCMFrameCount;
    return d;
}

/* ── AAC (fdk-aac + minimp4) ──────────────────────────────────────────────────
 * Covers AAC-LC + HE-AAC in either an MP4/M4A container (demuxed by minimp4 — it
 * also transparently finds the audio track inside an MP4 that has video, which is
 * the future "audio out of a movie" path) or a raw ADTS .aac stream (fdk-aac
 * frames those itself). One cmm_aac_open_{memory,file} detects the container from
 * the leading bytes; everything below shares the read/seek/close family. fdk-aac
 * emits interleaved INT16 PCM; we convert to f32 so the managed side sees IEEE
 * float exactly like the other codecs. */

CMM_API void cmm_aac_close(cmm_decoder* d);   /* fwd: open paths free via close on error */

/* minimp4 random-access read callback. Serves the demuxer from either the owned
 * in-memory copy (buf) or the owned FILE* (fp) — so an MP4/movie is read on
 * demand and never has to be fully buffered. Returns nonzero on failure. */
static int cmm_mp4_read(int64_t offset, void* buffer, size_t size, void* token)
{
    cmm_decoder* d = (cmm_decoder*)token;
    if (d->buf) {
        if (offset < 0 || (long long)offset + (long long)size > d->srclen) return 1;
        memcpy(buffer, d->buf + offset, size);
        return 0;
    }
    /* Serve any read wholly inside the cached 'moov' from RAM — this is where the
     * open-time sample-table parse hammers the file with tiny reads. mdat reads
     * (playback) fall through to the FILE* path below. */
    if (d->moov_buf && (long long)offset >= d->moov_off &&
        (long long)offset + (long long)size <= d->moov_off + d->moov_len) {
        memcpy(buffer, d->moov_buf + (offset - d->moov_off), size);
        return 0;
    }
    if (d->fp) {
        if (CMM_FSEEK(d->fp, (long long)offset, SEEK_SET) != 0) return 1;
        return fread(buffer, 1, size, d->fp) != size;
    }
    return 1;
}

/* Walk the top-level ISOBMFF boxes to locate 'moov' and, if it is within the cap,
 * read the whole box into d->moov_buf in a single fread. minimp4's sample-table
 * parse then hits RAM (memcpy) instead of thousands of tiny seek+read syscalls.
 * Best-effort: any failure just leaves moov_buf NULL and the streamed path stands.
 * File opens only (memory opens already serve from d->buf). */
static void cmm_mp4_prime_moov(cmm_decoder* d, long long filesize)
{
    if (!d->fp || d->buf) return;
    long long pos = 0;
    while (pos + 8 <= filesize) {
        unsigned char h[16];
        if (CMM_FSEEK(d->fp, pos, SEEK_SET) != 0) return;
        if (fread(h, 1, 8, d->fp) != 8) return;
        long long boxsize = ((long long)h[0] << 24) | ((long long)h[1] << 16) |
                            ((long long)h[2] << 8)  |  (long long)h[3];
        long long hdr = 8;
        if (boxsize == 1) {                    /* 64-bit largesize follows the type */
            if (fread(h + 8, 1, 8, d->fp) != 8) return;
            boxsize = ((long long)h[8]  << 56) | ((long long)h[9]  << 48) |
                      ((long long)h[10] << 40) | ((long long)h[11] << 32) |
                      ((long long)h[12] << 24) | ((long long)h[13] << 16) |
                      ((long long)h[14] << 8)  |  (long long)h[15];
            hdr = 16;
        } else if (boxsize == 0) {
            boxsize = filesize - pos;          /* box runs to EOF */
        }
        if (boxsize < hdr || pos + boxsize > filesize) return;   /* malformed → bail */

        if (h[4] == 'm' && h[5] == 'o' && h[6] == 'o' && h[7] == 'v') {
            if (boxsize <= CMM_MP4_MOOV_CACHE_MAX) {
                unsigned char* mb = (unsigned char*)malloc((size_t)boxsize);
                if (mb) {
                    if (CMM_FSEEK(d->fp, pos, SEEK_SET) == 0 &&
                        fread(mb, 1, (size_t)boxsize, d->fp) == (size_t)boxsize) {
                        d->moov_buf = mb; d->moov_off = pos; d->moov_len = boxsize;
                    } else {
                        free(mb);
                    }
                }
            }
            return;   /* found moov (cached or too big) — done either way */
        }
        pos += boxsize;
    }
}

/* Decode exactly one access unit into d->pcm (interleaved f32). Returns the
 * number of frames (per channel) produced, 0 at end-of-stream, <0 on error. */
static int cmm_aac_decode_one_au(cmm_decoder* d)
{
    if (!d->aac) return -1;

    AAC_DECODER_ERROR err;

    if (d->mp4) {
        /* ── MP4: one sample == one AAC access unit ── */
        if (d->aac_sample >= d->aac_count) return 0;   /* EOF */
        unsigned fb = 0, ts = 0, dur = 0;
        MP4D_file_offset_t ofs = MP4D_frame_offset(d->mp4, d->aac_track,
                                                   d->aac_sample, &fb, &ts, &dur);
        d->aac_sample++;
        if (fb == 0) return 0;

        UCHAR* au;
        if (d->buf) {
            au = d->buf + (size_t)ofs;
        } else {
            if (d->aubuf_cap < fb) {
                unsigned char* nb = (unsigned char*)realloc(d->aubuf, fb);
                if (!nb) return -1;
                d->aubuf = nb; d->aubuf_cap = fb;
            }
            if (CMM_FSEEK(d->fp, (long long)ofs, SEEK_SET) != 0) return -1;
            if (fread(d->aubuf, 1, fb, d->fp) != fb) return -1;
            au = d->aubuf;
        }

        UINT insize = fb, valid = fb;
        UCHAR* inp = au;
        if (aacDecoder_Fill(d->aac, &inp, &insize, &valid) != AAC_DEC_OK) return -1;
    } else if (d->src_read) {
        /* ── Streaming ADTS (container source): pull ADTS bytes on demand into the
         * refill window (sbuf) and frame them with fdk-aac. Never buffers the whole
         * track. Mirrors the in-memory loop below but refills from src_read. ── */
        for (;;) {
            /* Top up the window: compact any leftover to the front, then read more. */
            if (!d->src_eof && d->spos >= d->slen) {           /* window drained */
                d->slen = d->spos = 0;
                int got = d->src_read(d->src_ud, d->sbuf, d->scap);
                if (got > 0) d->slen = got; else d->src_eof = 1;
            } else if (!d->src_eof && d->spos > 0) {           /* leftover + room */
                memmove(d->sbuf, d->sbuf + d->spos, (size_t)(d->slen - d->spos));
                d->slen -= d->spos; d->spos = 0;
                if (d->slen < d->scap) {
                    int got = d->src_read(d->src_ud, d->sbuf + d->slen, d->scap - d->slen);
                    if (got > 0) d->slen += got; else d->src_eof = 1;
                }
            }
            if (d->slen > d->spos) {
                UCHAR* inp = d->sbuf + d->spos;
                UINT insize = (UINT)(d->slen - d->spos), valid = insize;
                if (aacDecoder_Fill(d->aac, &inp, &insize, &valid) != AAC_DEC_OK) return -1;
                d->spos += (int)(insize - valid);             /* bytes consumed */
            }
            UINT flags = d->clr_hist ? AACDEC_CLRHIST : 0;
            err = aacDecoder_DecodeFrame(d->aac, d->i16, CMM_AAC_MAX_SAMPLES, flags);
            if (err == AAC_DEC_NOT_ENOUGH_BITS) {
                if (d->src_eof && d->spos >= d->slen) return 0;   /* stream drained → EOF */
                continue;                                          /* need more bytes */
            }
            d->clr_hist = 0;
            if (err != AAC_DEC_OK) return -1;
            goto have_frame;
        }
    } else {
        /* ── Raw ADTS: feed the in-memory buffer; fdk-aac frames it. Loop because
         * a single Fill may not have handed the decoder a whole frame yet. ── */
        for (;;) {
            if (d->adts_pos < d->srclen) {
                UCHAR* inp = d->buf + (size_t)d->adts_pos;
                UINT insize = (UINT)(d->srclen - d->adts_pos), valid = insize;
                if (aacDecoder_Fill(d->aac, &inp, &insize, &valid) != AAC_DEC_OK) return -1;
                d->adts_pos += (long long)(insize - valid);   /* bytes consumed */
            }
            UINT flags = d->clr_hist ? AACDEC_CLRHIST : 0;
            err = aacDecoder_DecodeFrame(d->aac, d->i16, CMM_AAC_MAX_SAMPLES, flags);
            if (err == AAC_DEC_NOT_ENOUGH_BITS) {
                if (d->adts_pos >= d->srclen) return 0;   /* fed everything → EOF */
                continue;                                  /* need more bytes */
            }
            d->clr_hist = 0;
            if (err != AAC_DEC_OK) return -1;
            goto have_frame;   /* skip the MP4 single-decode below */
        }
    }

    {
        UINT flags = d->clr_hist ? AACDEC_CLRHIST : 0;
        err = aacDecoder_DecodeFrame(d->aac, d->i16, CMM_AAC_MAX_SAMPLES, flags);
        d->clr_hist = 0;
        if (err == AAC_DEC_NOT_ENOUGH_BITS) return 0;   /* AU produced no frame */
        if (err != AAC_DEC_OK) return -1;
    }

have_frame:;
    CStreamInfo* info = aacDecoder_GetStreamInfo(d->aac);
    if (!info || info->frameSize <= 0 || info->numChannels <= 0) return -1;
    int n = info->frameSize * info->numChannels;
    if (n > CMM_AAC_MAX_SAMPLES) n = CMM_AAC_MAX_SAMPLES;   /* clamp to scratch */
    const float inv = 1.0f / 32768.0f;
    for (int i = 0; i < n; i++) d->pcm[i] = (float)d->i16[i] * inv;
    d->pcm_fill   = n;
    d->pcm_pos    = 0;
    d->frame_size = info->frameSize;
    d->channels   = info->numChannels;
    return info->frameSize;
}

/* Shared init once buf/fp + srclen are set: detect container, open the decoder,
 * decode the first frame to learn the authoritative rate/channels/frameSize, and
 * fill the out-params. Returns 0 on success, <0 on failure (caller frees d). */
static int cmm_aac_init(cmm_decoder* d, long long filesize, int* channels,
                        int* sampleRate, long long* totalFrames)
{
    /* Peek the leading bytes for container detection. */
    unsigned char hdr[16];
    int hdrn = 0;
    if (d->buf) {
        hdrn = (int)(d->srclen < (long long)sizeof(hdr) ? d->srclen : (long long)sizeof(hdr));
        memcpy(hdr, d->buf, (size_t)hdrn);
    } else if (d->fp) {
        if (CMM_FSEEK(d->fp, 0, SEEK_SET) != 0) return -1;
        hdrn = (int)fread(hdr, 1, sizeof(hdr), d->fp);
    }
    if (hdrn < 8) return -1;

    int is_mp4 = (hdr[4] == 'f' && hdr[5] == 't' && hdr[6] == 'y' && hdr[7] == 'p');

    if (is_mp4) {
        d->mp4 = (MP4D_demux_t*)calloc(1, sizeof(MP4D_demux_t));
        if (!d->mp4) return -1;
        cmm_mp4_prime_moov(d, filesize);   /* cache the index so the parse reads from RAM */
        if (!MP4D_open(d->mp4, cmm_mp4_read, d, filesize) || d->mp4->track_count == 0)
            return -1;

        /* Pick the first AAC audio track (one carrying a decoder-config DSI). */
        int found = -1;
        for (unsigned t = 0; t < d->mp4->track_count; t++) {
            MP4D_track_t* tr = &d->mp4->track[t];
            if (tr->handler_type == MP4D_HANDLER_TYPE_SOUN && tr->dsi && tr->dsi_bytes > 0) {
                found = (int)t; break;
            }
        }
        if (found < 0) return -1;
        d->aac_track = (unsigned)found;
        d->aac_count = d->mp4->track[found].sample_count;
        d->aac_sample = 0;

        d->aac = aacDecoder_Open(TT_MP4_RAW, 1);
        if (!d->aac) return -1;
        UCHAR* dsi = (UCHAR*)d->mp4->track[found].dsi;
        UINT   dsl = d->mp4->track[found].dsi_bytes;
        if (aacDecoder_ConfigRaw(d->aac, &dsi, &dsl) != AAC_DEC_OK) return -1;

        /* The cached moov index has done its only job — feeding MP4D_open's
         * sample-table parse from RAM. minimp4 now holds the tables (and a copy of
         * the dsi) in its own arrays, and every read from here on is 'mdat' sample
         * data (outside the moov offset range), so cmm_mp4_read never touches
         * moov_buf again. Free it NOW rather than holding it for the whole track's
         * decode: the allocation is the actual moov size (a few KB for music, tens
         * of MB for a multi-hour video — never the 256 MB cap), and this keeps that
         * peak transient to the open, not resident for the life of the reader. */
        if (d->moov_buf) { free(d->moov_buf); d->moov_buf = NULL; d->moov_off = 0; d->moov_len = 0; }
    } else {
        /* Raw ADTS: operate over an in-memory buffer. (A file open reads the whole
         * file in below; ADTS music files are small, and movies are always MP4.) */
        if (!d->buf) return -1;
        /* Skip a leading ID3v2 tag if present (its size field is syncsafe). */
        if (d->srclen >= 10 && hdr[0] == 'I' && hdr[1] == 'D' && hdr[2] == '3') {
            long long sz = ((long long)(hdr[6] & 0x7f) << 21) |
                           ((long long)(hdr[7] & 0x7f) << 14) |
                           ((long long)(hdr[8] & 0x7f) << 7)  |
                            (long long)(hdr[9] & 0x7f);
            d->adts_pos = 10 + sz;
            if (d->adts_pos > d->srclen) d->adts_pos = d->srclen;
        }
        /* Require an ADTS syncword at the start of the audio data. */
        if (d->adts_pos + 2 > d->srclen ||
            d->buf[d->adts_pos] != 0xFF || (d->buf[d->adts_pos + 1] & 0xF0) != 0xF0)
            return -1;

        d->aac = aacDecoder_Open(TT_MP4_ADTS, 1);
        if (!d->aac) return -1;
    }

    /* One-frame scratch (interleaved int16 from fdk + the f32 conversion). */
    d->i16 = (short*)malloc(sizeof(short) * CMM_AAC_MAX_SAMPLES);
    d->pcm = (float*)malloc(sizeof(float) * CMM_AAC_MAX_SAMPLES);
    if (!d->i16 || !d->pcm) return -1;

    /* Decode the first frame so the rate/channels/frameSize are authoritative
     * (HE-AAC's SBR doubles the rate vs. the container header). The decoded
     * samples stay buffered as the first output. */
    if (cmm_aac_decode_one_au(d) <= 0) return -1;

    int sr = 0;
    {
        CStreamInfo* info = aacDecoder_GetStreamInfo(d->aac);
        sr = info ? info->sampleRate : 0;
    }
    if (sr <= 0 || d->channels <= 0 || d->frame_size <= 0) return -1;

    long long total = -1;
    if (d->mp4) {
        total = (long long)d->aac_count * d->frame_size;   /* output frames */
    } else {
        /* Walk ADTS frame headers (cheap, no decode) to count access units, then
         * multiply by the known output frame size (already SBR-doubled for HE).
         * Recount from the audio-data start (after any ID3), independent of the
         * read cursor (which has already advanced past the first decoded AU). */
        long long au = 0, p, start = 0;
        if (d->srclen >= 10 && d->buf[0] == 'I' && d->buf[1] == 'D' && d->buf[2] == '3') {
            start = 10 + (((long long)(d->buf[6] & 0x7f) << 21) |
                          ((long long)(d->buf[7] & 0x7f) << 14) |
                          ((long long)(d->buf[8] & 0x7f) << 7)  |
                           (long long)(d->buf[9] & 0x7f));
        }
        for (p = start; p + 7 <= d->srclen; ) {
            if (d->buf[p] != 0xFF || (d->buf[p + 1] & 0xF0) != 0xF0) break;
            long long flen = (((long long)(d->buf[p + 3] & 0x03)) << 11) |
                             (((long long)d->buf[p + 4]) << 3) |
                             (((long long)(d->buf[p + 5] >> 5)) & 0x07);
            if (flen < 7) break;
            au++; p += flen;
        }
        if (au > 0) total = au * d->frame_size;
    }

    if (channels)    *channels    = d->channels;
    if (sampleRate)  *sampleRate  = sr;
    if (totalFrames) *totalFrames = total;
    return 0;
}

CMM_API cmm_decoder* cmm_aac_open_memory(const unsigned char* data, long long len,
                                         int* channels, int* sampleRate,
                                         long long* totalFrames, int* err)
{
    if (err) *err = 0;
    if (!data || len <= 0) { if (err) *err = -1; return NULL; }

    unsigned char* copy = (unsigned char*)malloc((size_t)len);
    if (!copy) { if (err) *err = -1; return NULL; }
    memcpy(copy, data, (size_t)len);

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { free(copy); if (err) *err = -1; return NULL; }
    d->kind   = CMM_KIND_AAC;
    d->buf    = copy;
    d->srclen = len;

    if (cmm_aac_init(d, len, channels, sampleRate, totalFrames) != 0) {
        cmm_aac_close(d); if (err) *err = -1; return NULL;
    }
    return d;
}

CMM_API cmm_decoder* cmm_aac_open_file(const wchar_t* path, int* channels,
                                       int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;
    /* Serialize the whole open — create + demux/decoder init all read the FILE*
     * (see cmm_openlock). AAC/MP4 is rare in a music library, so full serialization
     * of its open is negligible. */
    cmm_openlock();
    FILE* fp = cmm_open_path(path);
    if (!fp) { cmm_openunlock(); if (err) *err = -1; return NULL; }

    long long filesize = 0;
    if (CMM_FSEEK(fp, 0, SEEK_END) == 0) { filesize = (long long)CMM_FTELL(fp); CMM_FSEEK(fp, 0, SEEK_SET); }
    if (filesize <= 0) { cmm_openunlock(); fclose(fp); if (err) *err = -1; return NULL; }

    /* Container peek: MP4 streams from the FILE* (random-access via callback);
     * raw ADTS is read whole into memory (small; movies are always MP4). */
    unsigned char hdr[8];
    if (fread(hdr, 1, sizeof(hdr), fp) != sizeof(hdr)) { cmm_openunlock(); fclose(fp); if (err) *err = -1; return NULL; }
    int is_mp4 = (hdr[4] == 'f' && hdr[5] == 't' && hdr[6] == 'y' && hdr[7] == 'p');

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { cmm_openunlock(); fclose(fp); if (err) *err = -1; return NULL; }
    d->kind = CMM_KIND_AAC;

    if (is_mp4) {
        d->fp = fp;                       /* demuxer reads via cmm_mp4_read */
    } else {
        unsigned char* copy = (unsigned char*)malloc((size_t)filesize);
        if (!copy) { cmm_openunlock(); fclose(fp); free(d); if (err) *err = -1; return NULL; }
        if (CMM_FSEEK(fp, 0, SEEK_SET) != 0 ||
            fread(copy, 1, (size_t)filesize, fp) != (size_t)filesize) {
            cmm_openunlock(); free(copy); fclose(fp); free(d); if (err) *err = -1; return NULL;
        }
        fclose(fp);
        d->buf = copy; d->srclen = filesize;   /* fp left NULL: ADTS works in memory */
    }

    if (cmm_aac_init(d, filesize, channels, sampleRate, totalFrames) != 0) {
        cmm_openunlock(); cmm_aac_close(d); if (err) *err = -1; return NULL;
    }
    cmm_openunlock();
    return d;
}

CMM_API int cmm_aac_read_float(cmm_decoder* d, float* out, int maxFrames)
{
    if (!d || d->kind != CMM_KIND_AAC || !out || maxFrames <= 0) return 0;

    int produced = 0;
    while (produced < maxFrames) {
        if (d->pcm_pos >= d->pcm_fill) {
            int r = cmm_aac_decode_one_au(d);
            if (r < 0)  return produced > 0 ? produced : -1;
            if (r == 0) break;   /* EOF */
            if (d->seek_skip > 0) {
                long long avail = d->pcm_fill / d->channels;
                long long drop  = d->seek_skip < avail ? d->seek_skip : avail;
                d->pcm_pos += (int)(drop * d->channels);
                d->seek_skip -= drop;
                continue;        /* re-check (frame may be fully discarded) */
            }
        }
        int availFrames = (d->pcm_fill - d->pcm_pos) / d->channels;
        if (availFrames <= 0) { d->pcm_pos = d->pcm_fill; continue; }
        int want = maxFrames - produced;
        int take = availFrames < want ? availFrames : want;
        memcpy(out + (size_t)produced * d->channels, d->pcm + d->pcm_pos,
               (size_t)take * d->channels * sizeof(float));
        d->pcm_pos += take * d->channels;
        produced   += take;
    }
    return produced;
}

CMM_API int cmm_aac_seek(cmm_decoder* d, long long frame)
{
    if (!d || d->kind != CMM_KIND_AAC || d->frame_size <= 0) return -1;
    if (frame < 0) frame = 0;

    d->pcm_fill = d->pcm_pos = 0;
    d->clr_hist = 1;   /* clear filterbank history on the next decode */

    if (d->mp4) {
        long long target = frame / d->frame_size;
        if (target >= d->aac_count) target = d->aac_count > 0 ? d->aac_count - 1 : 0;
        long long start = target > 0 ? target - 1 : 0;   /* one AU of overlap warmup */
        d->aac_sample = (unsigned)start;
        d->seek_skip  = frame - start * d->frame_size;    /* discard down to exact frame */
    } else {
        /* ADTS has no index: restart from the audio-data origin and discard
         * forward. Reopen the decoder to flush its internal input + history. */
        long long start = 0;
        if (d->srclen >= 10 && d->buf[0] == 'I' && d->buf[1] == 'D' && d->buf[2] == '3') {
            start = 10 + (((long long)(d->buf[6] & 0x7f) << 21) |
                          ((long long)(d->buf[7] & 0x7f) << 14) |
                          ((long long)(d->buf[8] & 0x7f) << 7)  |
                           (long long)(d->buf[9] & 0x7f));
        }
        d->adts_pos = start;
        d->seek_skip = frame;
        if (d->aac) aacDecoder_Close(d->aac);
        d->aac = aacDecoder_Open(TT_MP4_ADTS, 1);
        if (!d->aac) return -1;
        d->clr_hist = 0;   /* fresh decoder; nothing to clear */
    }
    return 0;
}

CMM_API void cmm_aac_close(cmm_decoder* d)
{
    if (!d) return;
    if (d->aac)   aacDecoder_Close(d->aac);
    if (d->mp4) { MP4D_close(d->mp4); free(d->mp4); }
    if (d->moov_buf) free(d->moov_buf);
    if (d->aubuf) free(d->aubuf);
    if (d->sbuf)  free(d->sbuf);   /* streaming-ADTS refill window */
    if (d->i16)   free(d->i16);
    if (d->pcm)   free(d->pcm);
    if (d->buf)   free(d->buf);
    if (d->fp)    fclose(d->fp);
    free(d);
}

/* ── Container: Matroska / WebM (matroska.c demux + remux → existing backend) ──
 * The audio track is demuxed and losslessly repackaged into a stream one of the
 * decoders above already opens (Opus/Vorbis → Ogg; AAC → ADTS; MP3 → raw frames;
 * FLAC → native FLAC), then opened via that backend's CALLBACK path. This is done
 * lazily — see the container_stream / cstream_read machinery below: the inner
 * decoder pulls remux bytes on demand, so a multi-GB recording is never read or
 * buffered whole at open (that eager whole-file remux was the in-game mkv stall).
 * The CONTAINER handle holds the inner decoder + its streaming source; read/close
 * delegate, and seek rebuilds the source from offset 0. */

/* Growable byte buffer for the (incrementally produced) remux stream. The staging
 * buffer is compacted as the inner decoder consumes it (cstream_read), so it holds
 * only a page/frame + slack, never the whole track. The cap is a sanity ceiling on
 * a single pathological unit — normal staging stays in the low KB. */
#ifndef CMM_MKV_MAX_REMUX
#define CMM_MKV_MAX_REMUX ((size_t)96 * 1024 * 1024)
#endif
typedef struct { unsigned char* p; size_t len, cap; } bytebuf;
static int bb_grow(bytebuf* b, size_t need)
{
    if (b->len + need <= b->cap) return 1;
    if (b->len + need > CMM_MKV_MAX_REMUX) return 0;   /* refuse pathological tracks */
    size_t nc = b->cap ? b->cap : 65536;
    while (nc < b->len + need) nc *= 2;
    if (nc > CMM_MKV_MAX_REMUX) nc = b->len + need;    /* exact-fit near the cap */
    unsigned char* np = (unsigned char*)realloc(b->p, nc);
    if (!np) return 0;
    b->p = np; b->cap = nc; return 1;
}
static int bb_append(bytebuf* b, const void* data, size_t n)
{
    if (!bb_grow(b, n)) return 0;
    memcpy(b->p + b->len, data, n); b->len += n; return 1;
}

/* Random-access source over either an in-memory buffer or an owned FILE*.
 * The file path carries a read-ahead window so the demuxer's byte-at-a-time
 * EBML parse (thousands of 1-byte reads) doesn't turn into a fseek+fread
 * syscall storm — the diagnostic measured file-open ~90× slower than the same
 * demux over memory purely from that. Access is forward-monotonic, so one
 * sliding window refills ~once per window and serves the rest as memcpy. */
#define SRC_WIN_CAP (64 * 1024)
typedef struct {
    const unsigned char* buf; long long len; FILE* fp;
    unsigned char* win;   /* read-ahead window over fp (NULL on memory path/OOM) */
    long long      win_pos;
    int            win_len;
} src_reader;
static int src_cb(void* u, long long off, void* dst, int n)
{
    src_reader* s = (src_reader*)u;
    if (s->buf) {
        if (off < 0 || off > s->len) return -1;
        long long avail = s->len - off;
        int take = (long long)n < avail ? n : (int)avail;
        memcpy(dst, s->buf + off, (size_t)take);
        return take;
    }
    if (s->fp) {
        /* Serve from the window when the whole request fits inside it. */
        if (s->win && off >= s->win_pos && off + n <= s->win_pos + s->win_len) {
            memcpy(dst, s->win + (off - s->win_pos), (size_t)n);
            return n;
        }
        if (CMM_FSEEK(s->fp, off, SEEK_SET) != 0) return -1;
        /* Requests bigger than the window (large block payloads) read straight
         * through; small reads refill the window and serve from it. */
        if (s->win && n <= SRC_WIN_CAP) {
            int got = (int)fread(s->win, 1, (size_t)SRC_WIN_CAP, s->fp);
            s->win_pos = off; s->win_len = got;
            int take = n < got ? n : got;
            memcpy(dst, s->win, (size_t)take);
            return take;
        }
        return (int)fread(dst, 1, (size_t)n, s->fp);
    }
    return -1;
}

static void wr_le32(unsigned char* p, unsigned v)
{ p[0]=(unsigned char)v; p[1]=(unsigned char)(v>>8); p[2]=(unsigned char)(v>>16); p[3]=(unsigned char)(v>>24); }

/* Append every page currently pending in `os` to `out` (flush = force a page
 * boundary, used after the Opus/Vorbis setup headers). Returns 0 on OOM. */
static int ogg_drain(ogg_stream_state* os, bytebuf* out, int flush)
{
    ogg_page og;
    int got;
    while ((got = flush ? ogg_stream_flush(os, &og) : ogg_stream_pageout(os, &og)) != 0) {
        if (!bb_append(out, og.header, (size_t)og.header_len)) return 0;
        if (!bb_append(out, og.body,   (size_t)og.body_len))   return 0;
    }
    return 1;
}

/* The 11 ADTS sampling-frequency indices (index 15 = explicit rate, unused for
 * the music this targets). Maps an AAC AudioSpecificConfig rate index. */
static int adts_freq_index(int asc_index) { return (asc_index >= 0 && asc_index <= 12) ? asc_index : 4; }

/* ─────────────────────────────────────────────────────────────────────────
 * Container streaming source (lazy pull-remux)
 * ─────────────────────────────────────────────────────────────────────────
 * A .mkv/.webm audio track is repackaged into a framing an existing backend
 * reads (Opus/Vorbis → Ogg; AAC → ADTS; MP3 → raw frames; FLAC → native FLAC)
 * — but ON DEMAND: the inner decoder pulls bytes through cstream_read, which
 * demuxes + remuxes just enough of the track to satisfy each read. So the open
 * touches only the header region + first packet(s) and nothing is buffered
 * whole, however large the recording. The inner decoder owns PCM-level seeking
 * within what it has seen; a loop/seek-to-0 rebuilds the source from the start
 * (the demuxer is forward-only), which is the container_seek path below. */

struct cmm_decoder;                      /* fwd */
static int cmm_aac_decode_one_au(cmm_decoder* d);   /* fwd (defined above) */

typedef struct container_stream {
    src_reader     sr;          /* random-access source over fp / mem copy (src_cb) */
    FILE*          fp;          /* owned file handle (file open); NULL for a memory open */
    unsigned char* memcopy;     /* owned copy of source bytes (memory open); NULL for a file */
    mkv_demux*     mk;          /* demuxer over sr; re-openable from offset 0 for seek */
    long long      filesize;
    mkv_audio_info ai;          /* codec_private points into mk — refreshed on re-open */
    int            codec;

    bytebuf        stg;         /* produced-but-unconsumed remux bytes (compacted per read) */
    size_t         rd;          /* consume cursor into stg */
    long long      consumed;    /* total bytes handed out (the inner decoder's byte "tell") */
    int            eof;         /* producer drained the track */
    int            error;       /* producer / demux error latched */
    int            hdr_done;    /* setup headers already produced into stg */

    /* Ogg remux state (Opus / Vorbis) — kept live for incremental pageout. */
    ogg_stream_state os; int os_active;
    long long      granule, packetno;
    unsigned char* pend; int pendlen, pendcap, have; long long pend_tc;   /* one-packet lookahead */

    /* ADTS header fields precomputed from CodecPrivate (AAC). */
    int            aac_profile, aac_fi, aac_chan_cfg;
} container_stream;

static int cstream_read(void* ud, void* dst, int n);   /* fwd */

/* ── Per-codec incremental producers ─────────────────────────────────────────
 * Each appends at least one remux unit (header block or one packet's framing)
 * to cs->stg, or reaches EOF. Returns 1 = progressed, 0 = EOF (final flush done),
 * <0 = error. cstream_read loops these until a read is satisfiable or EOF. */

static int cs_produce_aac(container_stream* cs)
{
    const unsigned char* pkt; int plen; long long tc;
    int r = mkv_next_packet(cs->mk, &pkt, &plen, &tc);
    if (r < 0)  return -1;
    if (r == 0) { cs->eof = 1; return 0; }
    int frame_len = plen + 7;
    if (frame_len >= (1 << 13)) return -1;             /* exceeds the ADTS length field */
    unsigned char h[7];
    h[0] = 0xFF; h[1] = 0xF1;                            /* MPEG-4, layer 0, no CRC */
    h[2] = (unsigned char)((cs->aac_profile << 6) | (cs->aac_fi << 2) | ((cs->aac_chan_cfg >> 2) & 0x01));
    h[3] = (unsigned char)(((cs->aac_chan_cfg & 0x03) << 6) | ((frame_len >> 11) & 0x03));
    h[4] = (unsigned char)((frame_len >> 3) & 0xFF);
    h[5] = (unsigned char)(((frame_len & 0x07) << 5) | 0x1F);
    h[6] = 0xFC;
    if (!bb_append(&cs->stg, h, 7) || !bb_append(&cs->stg, pkt, (size_t)plen)) return -1;
    return 1;
}

static int cs_produce_concat(container_stream* cs, int with_prefix)
{
    if (with_prefix && !cs->hdr_done) {                 /* FLAC: emit "fLaC"+STREAMINFO once */
        cs->hdr_done = 1;
        if (cs->ai.codec_private && cs->ai.codec_private_len > 0 &&
            !bb_append(&cs->stg, cs->ai.codec_private, (size_t)cs->ai.codec_private_len)) return -1;
        return 1;
    }
    const unsigned char* pkt; int plen; long long tc;
    int r = mkv_next_packet(cs->mk, &pkt, &plen, &tc);
    if (r < 0)  return -1;
    if (r == 0) { cs->eof = 1; return 0; }
    if (!bb_append(&cs->stg, pkt, (size_t)plen)) return -1;
    return 1;
}

static int cs_produce_opus(container_stream* cs)
{
    if (!cs->hdr_done) {
        if (!cs->ai.codec_private || cs->ai.codec_private_len < 19) return -1;   /* need OpusHead */
        if (ogg_stream_init(&cs->os, 0x434D4D31)) return -1;                     /* serialno 'CMM1' */
        cs->os_active = 1;
        ogg_packet op;
        op.packet = (unsigned char*)cs->ai.codec_private; op.bytes = cs->ai.codec_private_len;
        op.b_o_s = 1; op.e_o_s = 0; op.granulepos = 0; op.packetno = 0;
        ogg_stream_packetin(&cs->os, &op);
        if (!ogg_drain(&cs->os, &cs->stg, 1)) return -1;                          /* OpusHead (BOS) */
        unsigned char tags[19];
        memcpy(tags, "OpusTags", 8); wr_le32(tags + 8, 3); memcpy(tags + 12, "CMM", 3); wr_le32(tags + 15, 0);
        op.packet = tags; op.bytes = 19; op.b_o_s = 0; op.granulepos = 0; op.packetno = 1;
        ogg_stream_packetin(&cs->os, &op);
        if (!ogg_drain(&cs->os, &cs->stg, 1)) return -1;                          /* OpusTags */
        cs->granule = 0; cs->packetno = 2; cs->hdr_done = 1;
        return 1;
    }
    const unsigned char* pkt; int plen; long long tc;
    int r = mkv_next_packet(cs->mk, &pkt, &plen, &tc);
    if (r < 0) return -1;
    if (r == 0) {                                       /* flush the pending packet as EOS */
        if (cs->have) {
            int ns = opus_packet_get_nb_samples(cs->pend, cs->pendlen, 48000); if (ns <= 0) ns = 960;
            cs->granule += ns;
            ogg_packet op; op.packet = cs->pend; op.bytes = cs->pendlen;
            op.b_o_s = 0; op.e_o_s = 1; op.granulepos = cs->granule; op.packetno = cs->packetno++;
            ogg_stream_packetin(&cs->os, &op);
            cs->have = 0;
        }
        if (!ogg_drain(&cs->os, &cs->stg, 1)) return -1;
        cs->eof = 1; return 0;
    }
    if (cs->have) {                                     /* previous packet is now known non-final */
        int ns = opus_packet_get_nb_samples(cs->pend, cs->pendlen, 48000); if (ns <= 0) ns = 960;
        cs->granule += ns;
        ogg_packet op; op.packet = cs->pend; op.bytes = cs->pendlen;
        op.b_o_s = 0; op.e_o_s = 0; op.granulepos = cs->granule; op.packetno = cs->packetno++;
        ogg_stream_packetin(&cs->os, &op);
        if (!ogg_drain(&cs->os, &cs->stg, 0)) return -1;
    }
    if (plen > cs->pendcap) {
        unsigned char* nb = (unsigned char*)realloc(cs->pend, (size_t)plen);
        if (!nb) return -1;
        cs->pend = nb; cs->pendcap = plen;
    }
    memcpy(cs->pend, pkt, (size_t)plen); cs->pendlen = plen; cs->have = 1;
    return 1;
}

static int cs_produce_vorbis(container_stream* cs)
{
    int sr = cs->ai.sample_rate > 0 ? cs->ai.sample_rate : 48000;
    if (!cs->hdr_done) {
        const unsigned char* cp = cs->ai.codec_private; int cpl = cs->ai.codec_private_len;
        if (!cp || cpl < 3 || cp[0] != 2) return -1;                             /* need 3 headers */
        int p = 1, s0 = 0, s1 = 0;
        while (p < cpl && cp[p] == 255) { s0 += 255; p++; } if (p < cpl) s0 += cp[p++];
        while (p < cpl && cp[p] == 255) { s1 += 255; p++; } if (p < cpl) s1 += cp[p++];
        long l0 = s0, l1 = s1, l2 = (long)cpl - (p + s0 + s1);
        if (l2 < 0 || (long)p + s0 + s1 > cpl) return -1;
        const unsigned char* h0 = cp + p, *h1 = cp + p + s0, *h2 = cp + p + s0 + s1;
        if (ogg_stream_init(&cs->os, 0x434D4D32)) return -1;                      /* serialno 'CMM2' */
        cs->os_active = 1;
        ogg_packet op;
        op.b_o_s = 1; op.e_o_s = 0; op.granulepos = 0; op.packetno = 0;
        op.packet = (unsigned char*)h0; op.bytes = l0; ogg_stream_packetin(&cs->os, &op);
        if (!ogg_drain(&cs->os, &cs->stg, 1)) return -1;                          /* id header (BOS) */
        op.b_o_s = 0; op.granulepos = 0;
        op.packet = (unsigned char*)h1; op.bytes = l1; op.packetno = 1; ogg_stream_packetin(&cs->os, &op);
        op.packet = (unsigned char*)h2; op.bytes = l2; op.packetno = 2; ogg_stream_packetin(&cs->os, &op);
        if (!ogg_drain(&cs->os, &cs->stg, 1)) return -1;                          /* comment + setup */
        cs->granule = 0; cs->packetno = 3; cs->hdr_done = 1;
        return 1;
    }
    const unsigned char* pkt; int plen; long long tc;
    int r = mkv_next_packet(cs->mk, &pkt, &plen, &tc);
    if (r < 0) return -1;
    if (r == 0) {                                       /* flush the pending packet as EOS */
        if (cs->have) {
            long long g = (long long)(cs->ai.duration_sec > 0 ? cs->ai.duration_sec * sr + 0.5 : 0);
            if (g <= cs->granule) g = cs->granule + 1;
            cs->granule = g;
            ogg_packet op; op.packet = cs->pend; op.bytes = cs->pendlen;
            op.b_o_s = 0; op.e_o_s = 1; op.granulepos = cs->granule; op.packetno = cs->packetno++;
            ogg_stream_packetin(&cs->os, &op);
            cs->have = 0;
        }
        if (!ogg_drain(&cs->os, &cs->stg, 1)) return -1;
        cs->eof = 1; return 0;
    }
    if (cs->have) {                                     /* granule from the pending block's timestamp */
        long long g = cs->pend_tc / 1000000000LL * sr + (cs->pend_tc % 1000000000LL) * sr / 1000000000LL;
        if (g < cs->granule) g = cs->granule;
        cs->granule = g;
        ogg_packet op; op.packet = cs->pend; op.bytes = cs->pendlen;
        op.b_o_s = 0; op.e_o_s = 0; op.granulepos = cs->granule; op.packetno = cs->packetno++;
        ogg_stream_packetin(&cs->os, &op);
        if (!ogg_drain(&cs->os, &cs->stg, 0)) return -1;
    }
    if (plen > cs->pendcap) {
        unsigned char* nb = (unsigned char*)realloc(cs->pend, (size_t)plen);
        if (!nb) return -1;
        cs->pend = nb; cs->pendcap = plen;
    }
    memcpy(cs->pend, pkt, (size_t)plen); cs->pendlen = plen; cs->pend_tc = tc; cs->have = 1;
    return 1;
}

static int cs_produce(container_stream* cs)
{
    switch (cs->codec) {
        case MKV_CODEC_OPUS:   return cs_produce_opus(cs);
        case MKV_CODEC_VORBIS: return cs_produce_vorbis(cs);
        case MKV_CODEC_AAC:    return cs_produce_aac(cs);
        case MKV_CODEC_MP3:    return cs_produce_concat(cs, 0);
        case MKV_CODEC_FLAC:   return cs_produce_concat(cs, 1);
        default: cs->error = 1; return -1;
    }
}

/* Forward-only byte read the inner decoders pull through. Produces just enough
 * remux to satisfy `n`, serves it, and compacts the consumed prefix so RAM stays
 * bounded to a page/frame + slack — never the whole track. 0 = end of stream. */
static int cstream_read(void* ud, void* dst, int n)
{
    container_stream* cs = (container_stream*)ud;
    if (n <= 0) return 0;
    while ((cs->stg.len - cs->rd) < (size_t)n && !cs->eof && !cs->error) {
        int r = cs_produce(cs);
        if (r < 0) { cs->error = 1; break; }
    }
    size_t avail = cs->stg.len - cs->rd;
    size_t take  = (size_t)n < avail ? (size_t)n : avail;
    if (take) memcpy(dst, cs->stg.p + cs->rd, take);
    cs->rd += take; cs->consumed += (long long)take;
    if (cs->rd == cs->stg.len) { cs->stg.len = 0; cs->rd = 0; }            /* fully drained */
    else if (cs->rd >= (1u << 20)) {                                        /* compact >1 MB consumed */
        memmove(cs->stg.p, cs->stg.p + cs->rd, cs->stg.len - cs->rd);
        cs->stg.len -= cs->rd; cs->rd = 0;
    }
    return (int)take;
}

/* ── Inner-decoder read/seek adapters over cstream ──────────────────────────
 * seek is forward-only (discard); the container relies on the stream head for
 * setup and rebuilds from 0 for any real seek (cmm_container_seek). */
static int cs_op_read(void* s, unsigned char* p, int n) { return cstream_read(s, p, n); }
static const OpusFileCallbacks CS_OP_CB = { cs_op_read, NULL, NULL, NULL };

static size_t cs_ov_read(void* p, size_t sz, size_t nm, void* s)
{
    if (sz == 0) return 0;
    int got = cstream_read(s, p, (int)(sz * nm));
    return got <= 0 ? 0 : (size_t)got / sz;
}
static const ov_callbacks CS_OV_CB = { cs_ov_read, NULL, NULL, NULL };

static int cs_discard_forward(container_stream* cs, int off)
{
    unsigned char tmp[4096];
    while (off > 0) {
        int want = off < (int)sizeof(tmp) ? off : (int)sizeof(tmp);
        int got = cstream_read(cs, tmp, want);
        if (got <= 0) return 0;
        off -= got;
    }
    return 1;
}
static size_t       cs_drf_read(void* ud, void* out, size_t n) { int g = cstream_read(ud, out, (int)n); return g <= 0 ? 0 : (size_t)g; }
static drflac_bool32 cs_drf_seek(void* ud, int off, drflac_seek_origin o)
{
    if (o == DRFLAC_SEEK_CUR && off >= 0) return cs_discard_forward((container_stream*)ud, off) ? DRFLAC_TRUE : DRFLAC_FALSE;
    return DRFLAC_FALSE;                                 /* unseekable: backward / absolute / end */
}
static drflac_bool32 cs_drf_tell(void* ud, drflac_int64* cur) { *cur = (drflac_int64)((container_stream*)ud)->consumed; return DRFLAC_TRUE; }

static size_t       cs_drmp3_read(void* ud, void* out, size_t n) { int g = cstream_read(ud, out, (int)n); return g <= 0 ? 0 : (size_t)g; }

/* Free an inner decoder via its codec-specific close. */
static void close_inner(cmm_decoder* in)
{
    if (!in) return;
    switch (in->kind) {
        case CMM_KIND_OPUS:   cmm_opus_close(in); break;
        case CMM_KIND_VORBIS: cmm_vorbis_close(in); break;
        case CMM_KIND_AAC:    cmm_aac_close(in); break;
        case CMM_KIND_MP3:    cmm_mp3_close(in); break;
        case CMM_KIND_FLAC:   cmm_flac_close(in); break;
        default: free(in); break;
    }
}

/* Open a streaming AAC (ADTS) inner decoder that pulls its bytes from `cs`.
 * The refill-fed ADTS branch in cmm_aac_decode_one_au does the framing. */
static cmm_decoder* cmm_aac_open_stream(container_stream* cs, int* channels,
                                        int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;
    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { if (err) *err = -1; return NULL; }
    d->kind = CMM_KIND_AAC;
    d->src_read = cstream_read; d->src_ud = cs;
    d->scap = 32 * 1024;
    d->sbuf = (unsigned char*)malloc((size_t)d->scap);
    d->i16  = (short*)malloc(sizeof(short) * CMM_AAC_MAX_SAMPLES);
    d->pcm  = (float*)malloc(sizeof(float) * CMM_AAC_MAX_SAMPLES);
    if (!d->sbuf || !d->i16 || !d->pcm) { cmm_aac_close(d); if (err) *err = -1; return NULL; }
    d->aac = aacDecoder_Open(TT_MP4_ADTS, 1);
    if (!d->aac) { cmm_aac_close(d); if (err) *err = -1; return NULL; }
    if (cmm_aac_decode_one_au(d) <= 0) { cmm_aac_close(d); if (err) *err = -1; return NULL; }
    int sr = 0; { CStreamInfo* info = aacDecoder_GetStreamInfo(d->aac); sr = info ? info->sampleRate : 0; }
    if (sr <= 0 || d->channels <= 0 || d->frame_size <= 0) { cmm_aac_close(d); if (err) *err = -1; return NULL; }
    if (channels)    *channels    = d->channels;
    if (sampleRate)  *sampleRate  = sr;
    if (totalFrames) *totalFrames = cs->ai.duration_sec > 0 ? (long long)(cs->ai.duration_sec * sr) : 0;
    return d;
}

/* Open the inner decoder for the selected codec over the streaming source. */
static cmm_decoder* open_inner_from_cs(container_stream* cs, int* channels,
                                       int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;
    switch (cs->codec) {
        case MKV_CODEC_AAC:
            return cmm_aac_open_stream(cs, channels, sampleRate, totalFrames, err);
        case MKV_CODEC_OPUS: {
            cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
            if (!d) { if (err) *err = -1; return NULL; }
            int e = 0;
            OggOpusFile* of = op_open_callbacks(cs, &CS_OP_CB, NULL, 0, &e);
            if (!of) { free(d); if (err) *err = e ? e : -1; return NULL; }
            d->kind = CMM_KIND_OPUS; d->of = of; d->channels = op_channel_count(of, -1);
            if (channels)    *channels    = d->channels;
            if (sampleRate)  *sampleRate  = 48000;
            if (totalFrames) *totalFrames = cs->ai.duration_sec > 0 ? (long long)(cs->ai.duration_sec * 48000.0) : 0;
            return d;
        }
        case MKV_CODEC_VORBIS: {
            cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
            if (!d) { if (err) *err = -1; return NULL; }
            d->kind = CMM_KIND_VORBIS;
            d->vorbis = (OggVorbis_File*)calloc(1, sizeof(OggVorbis_File));
            if (!d->vorbis) { free(d); if (err) *err = -1; return NULL; }
            int e = ov_open_callbacks(cs, d->vorbis, NULL, 0, CS_OV_CB);
            if (e < 0) { free(d->vorbis); free(d); if (err) *err = e; return NULL; }
            vorbis_info* vi = ov_info(d->vorbis, -1);
            d->channels = vi ? vi->channels : 0;
            if (channels)    *channels    = d->channels;
            if (sampleRate)  *sampleRate  = vi ? (int)vi->rate : 0;
            if (totalFrames) *totalFrames = (cs->ai.duration_sec > 0 && vi) ? (long long)(cs->ai.duration_sec * vi->rate) : 0;
            return d;
        }
        case MKV_CODEC_MP3: {
            cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
            if (!d) { if (err) *err = -1; return NULL; }
            drmp3* m = (drmp3*)malloc(sizeof(drmp3));
            if (!m) { free(d); if (err) *err = -1; return NULL; }
            /* NULL seek/tell → dr_mp3 runs in pure-sequential mode. With them set it
             * would seek to the stream END at init to scan for ID3v1/APE tags, which a
             * forward-only container source can't do (→ init fails). We decode forward
             * only anyway; the container rebuilds from 0 for any real seek. */
            if (!drmp3_init(m, cs_drmp3_read, NULL, NULL, NULL, cs, NULL)) {
                free(m); free(d); if (err) *err = -1; return NULL;
            }
            d->kind = CMM_KIND_MP3; d->mp3 = m; d->channels = (int)m->channels;
            if (channels)    *channels    = (int)m->channels;
            if (sampleRate)  *sampleRate  = (int)m->sampleRate;
            if (totalFrames) *totalFrames = cs->ai.duration_sec > 0 ? (long long)(cs->ai.duration_sec * m->sampleRate) : 0;
            return d;
        }
        case MKV_CODEC_FLAC: {
            cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
            if (!d) { if (err) *err = -1; return NULL; }
            drflac* f = drflac_open(cs_drf_read, cs_drf_seek, cs_drf_tell, cs, NULL);
            if (!f) { free(d); if (err) *err = -1; return NULL; }
            d->kind = CMM_KIND_FLAC; d->flac = f; d->channels = (int)f->channels;
            if (channels)    *channels    = (int)f->channels;
            if (sampleRate)  *sampleRate  = (int)f->sampleRate;
            if (totalFrames) *totalFrames = cs->ai.duration_sec > 0 ? (long long)(cs->ai.duration_sec * f->sampleRate)
                                                                    : (long long)f->totalPCMFrameCount;
            return d;
        }
        default: if (err) *err = -1; return NULL;
    }
}

/* Reset the producer state (keep the owned source: fp / memcopy / win). */
static void cs_teardown_producer(container_stream* cs)
{
    if (cs->os_active) { ogg_stream_clear(&cs->os); cs->os_active = 0; }
    if (cs->pend) { free(cs->pend); cs->pend = NULL; }
    cs->pendlen = cs->pendcap = cs->have = 0; cs->pend_tc = 0;
    if (cs->stg.p) { free(cs->stg.p); }
    cs->stg.p = NULL; cs->stg.len = cs->stg.cap = 0; cs->rd = 0;
    cs->consumed = 0; cs->eof = cs->error = cs->hdr_done = 0;
    cs->granule = cs->packetno = 0;
}

/* (Re)open the demuxer at offset 0 and (re)select the audio track. */
static int cs_open_demux(container_stream* cs)
{
    cs->sr.win_pos = -1; cs->sr.win_len = 0;
    if (cs->fp) CMM_FSEEK(cs->fp, 0, SEEK_SET);
    cs->mk = mkv_open(src_cb, &cs->sr, cs->filesize);
    if (!cs->mk) return -1;
    mkv_audio_info ai;
    if (mkv_get_audio(cs->mk, &ai) != 0) { mkv_close(cs->mk); cs->mk = NULL; return -1; }
    cs->ai = ai; cs->codec = ai.codec;
    if (ai.codec == MKV_CODEC_AAC) {                    /* precompute the ADTS header fields */
        const unsigned char* cp = ai.codec_private;
        if (!cp || ai.codec_private_len < 2) return -1;
        int obj = (cp[0] >> 3) & 0x1F;
        int fidx = ((cp[0] & 0x07) << 1) | ((cp[1] >> 7) & 0x01);
        int ch = (cp[1] >> 3) & 0x0F;
        if (obj < 1) obj = 2;
        if (ch < 1)  ch = ai.channels > 0 ? ai.channels : 2;
        cs->aac_profile  = (obj - 1) & 0x03;
        cs->aac_fi       = adts_freq_index(fidx);
        cs->aac_chan_cfg = ch;
    }
    return 0;
}

static void cs_free(container_stream* cs)
{
    if (!cs) return;
    cs_teardown_producer(cs);
    if (cs->mk)      mkv_close(cs->mk);
    if (cs->sr.win)  free(cs->sr.win);
    if (cs->fp)      fclose(cs->fp);
    if (cs->memcopy) free(cs->memcopy);
    free(cs);
}

/* Shared open: (lazily) demux + open the inner decoder over `cs`. On success the
 * returned handle is CONTAINER with d->inner + d->cs set (it takes ownership of cs). */
static cmm_decoder* container_open(container_stream* cs, int* channels,
                                   int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;
    if (cs_open_demux(cs) != 0) { if (err) *err = -1; return NULL; }
    int ierr = 0;
    cmm_decoder* in = open_inner_from_cs(cs, channels, sampleRate, totalFrames, &ierr);
    if (!in) { if (err) *err = ierr ? ierr : -1; return NULL; }
    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { close_inner(in); if (err) *err = -1; return NULL; }
    d->kind = CMM_KIND_CONTAINER; d->inner = in; d->cs = cs;
    return d;
}

CMM_API cmm_decoder* cmm_container_open_memory(const unsigned char* data, long long len,
                                               int* channels, int* sampleRate,
                                               long long* totalFrames, int* err)
{
    if (err) *err = 0;
    if (!data || len <= 0) { if (err) *err = -1; return NULL; }
    container_stream* cs = (container_stream*)calloc(1, sizeof(container_stream));
    if (!cs) { if (err) *err = -1; return NULL; }
    /* Own a copy: the streamed decode reads the source for the decoder's whole life. */
    cs->memcopy = (unsigned char*)malloc((size_t)len);
    if (!cs->memcopy) { free(cs); if (err) *err = -1; return NULL; }
    memcpy(cs->memcopy, data, (size_t)len);
    cs->sr.buf = cs->memcopy; cs->sr.len = len; cs->sr.fp = NULL;
    cs->sr.win = NULL; cs->sr.win_pos = -1; cs->sr.win_len = 0;
    cs->filesize = len;
    cmm_decoder* d = container_open(cs, channels, sampleRate, totalFrames, err);
    if (!d) cs_free(cs);
    return d;
}

CMM_API cmm_decoder* cmm_container_open_file(const wchar_t* path, int* channels,
                                             int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;
    /* Serialize the open (create + header read). Decode reads run unlocked on the
     * inner decoder's own FILE*, exactly like the AAC/MP4 file path. */
    cmm_openlock();
    FILE* fp = cmm_open_path(path);
    if (!fp) { cmm_openunlock(); if (err) *err = -1; return NULL; }
    long long filesize = 0;
    if (CMM_FSEEK(fp, 0, SEEK_END) == 0) { filesize = (long long)CMM_FTELL(fp); CMM_FSEEK(fp, 0, SEEK_SET); }
    if (filesize <= 0) { cmm_openunlock(); fclose(fp); if (err) *err = -1; return NULL; }
    container_stream* cs = (container_stream*)calloc(1, sizeof(container_stream));
    if (!cs) { cmm_openunlock(); fclose(fp); if (err) *err = -1; return NULL; }
    cs->fp = fp;
    cs->sr.buf = NULL; cs->sr.len = filesize; cs->sr.fp = fp;
    cs->sr.win = (unsigned char*)malloc(SRC_WIN_CAP);   /* OOM → NULL → direct reads, still correct */
    cs->sr.win_pos = -1; cs->sr.win_len = 0;
    cs->filesize = filesize;
    cmm_decoder* d = container_open(cs, channels, sampleRate, totalFrames, err);
    if (!d) cs_free(cs);                                 /* frees fp + win too */
    cmm_openunlock();
    return d;
}

CMM_API int cmm_container_read_float(cmm_decoder* d, float* out, int maxFrames)
{
    if (!d || !d->inner) return 0;
    switch (d->inner->kind) {
        case CMM_KIND_OPUS:   return cmm_opus_read_float(d->inner, out, maxFrames);
        case CMM_KIND_VORBIS: return cmm_vorbis_read_float(d->inner, out, maxFrames);
        case CMM_KIND_AAC:    return cmm_aac_read_float(d->inner, out, maxFrames);
        case CMM_KIND_MP3:    return cmm_mp3_read_float(d->inner, out, maxFrames);
        case CMM_KIND_FLAC:   return cmm_flac_read_float(d->inner, out, maxFrames);
        default: return 0;
    }
}

/* Seek by rebuilding the stream from the start (the demuxer is forward-only):
 * tear down the inner decoder + producer, re-open the demuxer at 0, re-open the
 * inner, then decode-and-discard forward to the target frame. frame 0 (the mixer's
 * loop path) is just the rebuild. Returns 0 on success, -1 on failure (the caller
 * treats a failure as "restart from 0" / "stop looping"). */
CMM_API int cmm_container_seek(cmm_decoder* d, long long frame)
{
    if (!d || !d->cs) return -1;
    if (frame < 0) frame = 0;
    if (d->inner) { close_inner(d->inner); d->inner = NULL; }
    cs_teardown_producer(d->cs);
    if (d->cs->mk) { mkv_close(d->cs->mk); d->cs->mk = NULL; }
    if (cs_open_demux(d->cs) != 0) return -1;
    int ierr = 0;
    cmm_decoder* in = open_inner_from_cs(d->cs, NULL, NULL, NULL, &ierr);
    if (!in) return -1;
    d->inner = in;
    if (frame > 0) {
        int ch = in->channels > 0 ? in->channels : 2;
        float tmp[4096];
        int chunk = (int)(sizeof(tmp) / sizeof(float)) / ch; if (chunk < 1) chunk = 1;
        long long todo = frame;
        while (todo > 0) {
            int want = todo < chunk ? (int)todo : chunk;
            int got = cmm_container_read_float(d, tmp, want);
            if (got <= 0) break;
            todo -= got;
        }
    }
    return 0;
}

CMM_API void cmm_container_close(cmm_decoder* d)
{
    if (!d) return;
    if (d->inner) close_inner(d->inner);
    if (d->cs)    cs_free(d->cs);
    free(d);
}
