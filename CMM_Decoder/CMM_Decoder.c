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

/* Open a file by wide path. _wfopen keeps non-ASCII music paths working on
 * Windows (bare fopen is ANSI); on POSIX we down-convert (future Linux build). */
static FILE* cmm_open_path(const wchar_t* path)
{
    if (!path) return NULL;
#ifdef _WIN32
    return _wfopen(path, L"rb");
#else
    char buf[4096];
    if (wcstombs(buf, path, sizeof(buf)) == (size_t)-1) return NULL;
    return fopen(buf, "rb");
#endif
}

/* opusfile pulls in the opus + ogg headers it needs. We only use the
 * memory-stream + float-read API, so HTTP support is compiled out upstream. */
#include <opusfile.h>
/* dr_flac prototypes + drflac struct (implementation lives in dr_flac_impl.c). */
#include "dr_flac.h"
/* dr_mp3 / dr_wav prototypes + structs (implementations in dr_mp3_impl.c / dr_wav_impl.c). */
#include "dr_mp3.h"
#include "dr_wav.h"
/* stb_vorbis prototypes (implementation lives in stb_vorbis_impl.c). */
#define STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"
#undef STB_VORBIS_HEADER_ONLY

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

/* dr_mp3 / dr_wav init into a CALLER-allocated struct (unlike drflac_open, which
 * returns a heap handle), so we malloc one and store the pointer here. */
struct cmm_decoder {
    int            kind;   /* CMM_KIND_* */
    OggOpusFile*   of;     /* libopusfile handle (opus)    */
    drflac*        flac;   /* dr_flac handle (flac)        */
    stb_vorbis*    vorbis; /* stb_vorbis handle (vorbis)   */
    drmp3*         mp3;    /* dr_mp3 handle (mp3)          */
    drwav*         wav;    /* dr_wav handle (wav/aiff)     */
    unsigned char* buf;    /* owned copy of source bytes (memory opens; NULL for file opens) */
    FILE*          fp;     /* owned file handle (file opens; NULL for memory opens) */
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
    short*            i16;        /* one decoded frame, interleaved int16 (fdk output) */
    float*            pcm;        /* same frame converted to interleaved float */
    int               pcm_fill;   /* valid floats in pcm */
    int               pcm_pos;    /* floats already handed out from pcm */
    int               frame_size; /* output frame size per channel (from CStreamInfo) */
    long long         seek_skip;  /* output frames still to discard after a seek (to reach exact frame) */
    int               clr_hist;   /* 1 = pass AACDEC_CLRHIST on the next DecodeFrame (post-seek) */

    /* ── Container (Matroska/WebM) ────────────────────────────────────────
     * A .mkv/.webm handle owns a real decoder for the remuxed audio stream:
     * the audio track is demuxed (matroska.c) and repackaged into a framing an
     * existing backend reads, then opened here. read/seek/close delegate to it. */
    struct cmm_decoder* inner;    /* delegate decoder (kind != CONTAINER) */
};

/* Generous one-frame scratch: HE-AAC frameSize is up to 2048, up to 8 channels. */
#define CMM_AAC_MAX_SAMPLES (2048 * 8)

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

    drflac* f = drflac_open_memory(copy, (size_t)len, NULL);
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
    free(d);
}

/* ── Vorbis (stb_vorbis) ─────────────────────────────────────────────────── */

CMM_API cmm_decoder* cmm_vorbis_open_memory(const unsigned char* data, long long len,
                                            int* channels, int* sampleRate,
                                            long long* totalFrames, int* err)
{
    if (err) *err = 0;
    if (!data || len <= 0) { if (err) *err = -1; return NULL; }
    /* stb_vorbis_open_memory takes a 32-bit length; reject anything that overflows. */
    if (len > 0x7fffffffLL) { if (err) *err = -1; return NULL; }

    /* stb_vorbis keeps the buffer for the life of the decoder, so we own a copy —
     * the caller's buffer may be pooled/overwritten after this returns. */
    unsigned char* copy = (unsigned char*)malloc((size_t)len);
    if (!copy) { if (err) *err = -1; return NULL; }
    memcpy(copy, data, (size_t)len);

    int e = 0;
    stb_vorbis* v = stb_vorbis_open_memory(copy, (int)len, &e, NULL);
    if (!v) { free(copy); if (err) *err = e ? e : -1; return NULL; }

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { stb_vorbis_close(v); free(copy); if (err) *err = -1; return NULL; }

    stb_vorbis_info info = stb_vorbis_get_info(v);
    d->kind     = CMM_KIND_VORBIS;
    d->of       = NULL;
    d->flac     = NULL;
    d->vorbis   = v;
    d->buf      = copy;
    d->fp       = NULL;
    d->channels = info.channels;

    if (channels)    *channels    = info.channels;
    if (sampleRate)  *sampleRate  = (int)info.sample_rate;       /* native rate, preserved */
    if (totalFrames) *totalFrames = (long long)stb_vorbis_stream_length_in_samples(v);
    return d;
}

CMM_API int cmm_vorbis_read_float(cmm_decoder* d, float* out, int maxFrames)
{
    if (!d || !d->vorbis || !out || maxFrames <= 0) return 0;
    /* Returns FRAMES per channel decoded (0 at EOF), looping across Vorbis frames
     * internally. num_floats is total output capacity in floats. */
    return stb_vorbis_get_samples_float_interleaved(d->vorbis, d->channels, out,
                                                    maxFrames * d->channels);
}

CMM_API int cmm_vorbis_seek(cmm_decoder* d, long long frame)
{
    if (!d || !d->vorbis) return -1;
    /* stb_vorbis_seek returns nonzero on success, 0 on failure. */
    return stb_vorbis_seek(d->vorbis, (unsigned int)frame) ? 0 : -1;
}

CMM_API void cmm_vorbis_close(cmm_decoder* d)
{
    if (!d) return;
    if (d->vorbis) stb_vorbis_close(d->vorbis);
    if (d->buf)    free(d->buf);
    if (d->fp)     fclose(d->fp);
    free(d);
}

/* ── File-based opens (large-file path) ──────────────────────────────────────
 * Each opens a FILE* via cmm_open_path (wide/_wfopen for non-ASCII paths) and
 * hands it to the decoder; the cmm_decoder OWNS the FILE* and the matching
 * *_close fcloses it (the decoders are told NOT to close it themselves). The
 * returned handle is otherwise identical to a *_open_memory one — same read /
 * seek / close functions apply. */

/* opusfile FILE* callbacks (close=NULL: cmm_opus_close owns the handle). */
static int        cmm_op_read(void* s, unsigned char* p, int n) { return (int)fread(p, 1, (size_t)n, (FILE*)s); }
static int        cmm_op_seek(void* s, opus_int64 off, int whence) { return CMM_FSEEK((FILE*)s, off, whence); }
static opus_int64 cmm_op_tell(void* s) { return (opus_int64)CMM_FTELL((FILE*)s); }
static const OpusFileCallbacks CMM_OP_CB = { cmm_op_read, cmm_op_seek, cmm_op_tell, NULL };

CMM_API cmm_decoder* cmm_opus_open_file(const wchar_t* path, int* channels,
                                        int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;
    FILE* fp = cmm_open_path(path);
    if (!fp) { if (err) *err = OP_EFAULT; return NULL; }

    int e = 0;
    OggOpusFile* of = op_open_callbacks(fp, &CMM_OP_CB, NULL, 0, &e);
    if (!of) { fclose(fp); if (err) *err = e; return NULL; }

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { op_free(of); fclose(fp); if (err) *err = OP_EFAULT; return NULL; }

    d->kind     = CMM_KIND_OPUS;
    d->of       = of;
    d->flac     = NULL;
    d->vorbis   = NULL;
    d->buf      = NULL;
    d->fp       = fp;
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

CMM_API cmm_decoder* cmm_flac_open_file(const wchar_t* path, int* channels,
                                        int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;
    FILE* fp = cmm_open_path(path);
    if (!fp) { if (err) *err = -1; return NULL; }

    drflac* f = drflac_open(cmm_drf_read, cmm_drf_seek, cmm_drf_tell, fp, NULL);
    if (!f) { fclose(fp); if (err) *err = -1; return NULL; }

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { drflac_close(f); fclose(fp); if (err) *err = -1; return NULL; }

    d->kind     = CMM_KIND_FLAC;
    d->of       = NULL;
    d->flac     = f;
    d->vorbis   = NULL;
    d->buf      = NULL;
    d->fp       = fp;
    d->channels = (int)f->channels;

    if (channels)    *channels    = d->channels;
    if (sampleRate)  *sampleRate  = (int)f->sampleRate;
    if (totalFrames) *totalFrames = (long long)f->totalPCMFrameCount;
    return d;
}

CMM_API cmm_decoder* cmm_vorbis_open_file(const wchar_t* path, int* channels,
                                          int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;
    FILE* fp = cmm_open_path(path);
    if (!fp) { if (err) *err = -1; return NULL; }

    int e = 0;
    /* close_on_close = 0: cmm_vorbis_close owns the FILE*. */
    stb_vorbis* v = stb_vorbis_open_file(fp, 0, &e, NULL);
    if (!v) { fclose(fp); if (err) *err = e ? e : -1; return NULL; }

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { stb_vorbis_close(v); fclose(fp); if (err) *err = -1; return NULL; }

    stb_vorbis_info info = stb_vorbis_get_info(v);
    d->kind     = CMM_KIND_VORBIS;
    d->of       = NULL;
    d->flac     = NULL;
    d->vorbis   = v;
    d->buf      = NULL;
    d->fp       = fp;
    d->channels = info.channels;

    if (channels)    *channels    = info.channels;
    if (sampleRate)  *sampleRate  = (int)info.sample_rate;
    if (totalFrames) *totalFrames = (long long)stb_vorbis_stream_length_in_samples(v);
    return d;
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

CMM_API cmm_decoder* cmm_mp3_open_file(const wchar_t* path, int* channels,
                                       int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;
    FILE* fp = cmm_open_path(path);
    if (!fp) { if (err) *err = -1; return NULL; }

    drmp3* m = (drmp3*)malloc(sizeof(drmp3));
    if (!m) { fclose(fp); if (err) *err = -1; return NULL; }
    if (!drmp3_init(m, cmm_drm_read, cmm_drm_seek, cmm_drm_tell, NULL, fp, NULL)) {
        free(m); fclose(fp); if (err) *err = -1; return NULL;
    }

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { drmp3_uninit(m); free(m); fclose(fp); if (err) *err = -1; return NULL; }

    d->kind     = CMM_KIND_MP3;
    d->mp3      = m;
    d->fp       = fp;
    d->channels = (int)m->channels;

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

CMM_API cmm_decoder* cmm_wav_open_file(const wchar_t* path, int* channels,
                                       int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;
    FILE* fp = cmm_open_path(path);
    if (!fp) { if (err) *err = -1; return NULL; }

    drwav* w = (drwav*)malloc(sizeof(drwav));
    if (!w) { fclose(fp); if (err) *err = -1; return NULL; }
    if (!drwav_init(w, cmm_drw_read, cmm_drw_seek, cmm_drw_tell, fp, NULL)) {
        free(w); fclose(fp); if (err) *err = -1; return NULL;
    }

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { drwav_uninit(w); free(w); fclose(fp); if (err) *err = -1; return NULL; }

    d->kind     = CMM_KIND_WAV;
    d->wav      = w;
    d->fp       = fp;
    d->channels = (int)w->channels;

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
    if (d->fp) {
        if (CMM_FSEEK(d->fp, (long long)offset, SEEK_SET) != 0) return 1;
        return fread(buffer, 1, size, d->fp) != size;
    }
    return 1;
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
    FILE* fp = cmm_open_path(path);
    if (!fp) { if (err) *err = -1; return NULL; }

    long long filesize = 0;
    if (CMM_FSEEK(fp, 0, SEEK_END) == 0) { filesize = (long long)CMM_FTELL(fp); CMM_FSEEK(fp, 0, SEEK_SET); }
    if (filesize <= 0) { fclose(fp); if (err) *err = -1; return NULL; }

    /* Container peek: MP4 streams from the FILE* (random-access via callback);
     * raw ADTS is read whole into memory (small; movies are always MP4). */
    unsigned char hdr[8];
    if (fread(hdr, 1, sizeof(hdr), fp) != sizeof(hdr)) { fclose(fp); if (err) *err = -1; return NULL; }
    int is_mp4 = (hdr[4] == 'f' && hdr[5] == 't' && hdr[6] == 'y' && hdr[7] == 'p');

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) { fclose(fp); if (err) *err = -1; return NULL; }
    d->kind = CMM_KIND_AAC;

    if (is_mp4) {
        d->fp = fp;                       /* demuxer reads via cmm_mp4_read */
    } else {
        unsigned char* copy = (unsigned char*)malloc((size_t)filesize);
        if (!copy) { fclose(fp); free(d); if (err) *err = -1; return NULL; }
        if (CMM_FSEEK(fp, 0, SEEK_SET) != 0 ||
            fread(copy, 1, (size_t)filesize, fp) != (size_t)filesize) {
            free(copy); fclose(fp); free(d); if (err) *err = -1; return NULL;
        }
        fclose(fp);
        d->buf = copy; d->srclen = filesize;   /* fp left NULL: ADTS works in memory */
    }

    if (cmm_aac_init(d, filesize, channels, sampleRate, totalFrames) != 0) {
        cmm_aac_close(d); if (err) *err = -1; return NULL;
    }
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
    if (d->aubuf) free(d->aubuf);
    if (d->i16)   free(d->i16);
    if (d->pcm)   free(d->pcm);
    if (d->buf)   free(d->buf);
    if (d->fp)    fclose(d->fp);
    free(d);
}

/* ── Container: Matroska / WebM (matroska.c demux + remux → existing backend) ──
 * The audio track is demuxed and losslessly repackaged into a stream one of the
 * decoders above already opens (Opus/Vorbis → Ogg; AAC → ADTS; MP3 → raw frames;
 * FLAC → native FLAC), then opened via the matching cmm_*_open_memory. The
 * container handle (kind CONTAINER) holds that inner decoder; read/seek/close
 * delegate to it. Forward-only demux is enough: the inner decoder owns seeking
 * over the fully-remuxed buffer, so no Cues index is needed. */

/* Growable byte buffer for the remuxed stream. */
typedef struct { unsigned char* p; size_t len, cap; } bytebuf;
static int bb_grow(bytebuf* b, size_t need)
{
    if (b->len + need <= b->cap) return 1;
    size_t nc = b->cap ? b->cap : 65536;
    while (nc < b->len + need) nc *= 2;
    unsigned char* np = (unsigned char*)realloc(b->p, nc);
    if (!np) return 0;
    b->p = np; b->cap = nc; return 1;
}
static int bb_append(bytebuf* b, const void* data, size_t n)
{
    if (!bb_grow(b, n)) return 0;
    memcpy(b->p + b->len, data, n); b->len += n; return 1;
}

/* Random-access source over either an in-memory buffer or an owned FILE*. */
typedef struct { const unsigned char* buf; long long len; FILE* fp; } src_reader;
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
        if (CMM_FSEEK(s->fp, off, SEEK_SET) != 0) return -1;
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

/* Remux a demuxed Opus track into an Ogg/Opus stream (OpusHead + OpusTags +
 * data pages with exact 48 kHz granulepos). Returns a malloc'd buffer (caller
 * frees) and sets *outlen, or NULL on failure. */
static unsigned char* remux_opus(mkv_demux* mk, const mkv_audio_info* ai, long long* outlen)
{
    if (!ai->codec_private || ai->codec_private_len < 19) return NULL;   /* need OpusHead */

    bytebuf out = {0};
    ogg_stream_state os;
    if (ogg_stream_init(&os, 0x434D4D31)) return NULL;   /* serialno 'CMM1' */

    ogg_packet op;
    /* OpusHead (BOS) — alone on the first page per the Ogg/Opus spec. */
    op.packet = (unsigned char*)ai->codec_private;
    op.bytes = ai->codec_private_len;
    op.b_o_s = 1; op.e_o_s = 0; op.granulepos = 0; op.packetno = 0;
    ogg_stream_packetin(&os, &op);
    if (!ogg_drain(&os, &out, 1)) goto fail;

    /* OpusTags — minimal: vendor "CMM", zero user comments. */
    {
        unsigned char tags[19];
        memcpy(tags, "OpusTags", 8);
        wr_le32(tags + 8, 3); memcpy(tags + 12, "CMM", 3); wr_le32(tags + 15, 0);
        op.packet = tags; op.bytes = (long)sizeof(tags);
        op.b_o_s = 0; op.e_o_s = 0; op.granulepos = 0; op.packetno = 1;
        ogg_stream_packetin(&os, &op);
        if (!ogg_drain(&os, &out, 1)) goto fail;
    }

    /* Audio pages. One-packet lookahead so the final packet carries e_o_s and
     * the running granulepos (exact, via opus_packet_get_nb_samples @48 kHz). */
    {
        long long granule = 0, packetno = 2;
        unsigned char* pend = NULL; int pendlen = 0, pendcap = 0, have = 0;
        const unsigned char* pkt; int plen; long long tc;
        int r;
        while ((r = mkv_next_packet(mk, &pkt, &plen, &tc)) == 1) {
            if (have) {
                int ns = opus_packet_get_nb_samples(pend, pendlen, 48000);
                if (ns <= 0) ns = 960;
                granule += ns;
                op.packet = pend; op.bytes = pendlen;
                op.b_o_s = 0; op.e_o_s = 0; op.granulepos = granule; op.packetno = packetno++;
                ogg_stream_packetin(&os, &op);
                if (!ogg_drain(&os, &out, 0)) { free(pend); goto fail; }
            }
            if (plen > pendcap) {
                unsigned char* nb = (unsigned char*)realloc(pend, (size_t)plen);
                if (!nb) { free(pend); goto fail; }
                pend = nb; pendcap = plen;
            }
            memcpy(pend, pkt, (size_t)plen); pendlen = plen; have = 1;
        }
        if (r < 0) { free(pend); goto fail; }
        if (have) {
            int ns = opus_packet_get_nb_samples(pend, pendlen, 48000);
            if (ns <= 0) ns = 960;
            granule += ns;
            op.packet = pend; op.bytes = pendlen;
            op.b_o_s = 0; op.e_o_s = 1; op.granulepos = granule; op.packetno = packetno++;
            ogg_stream_packetin(&os, &op);
        }
        free(pend);
        if (!ogg_drain(&os, &out, 1)) goto fail;   /* flush remainder incl. EOS page */
    }

    ogg_stream_clear(&os);
    *outlen = (long long)out.len;
    return out.p;

fail:
    ogg_stream_clear(&os);
    free(out.p);
    return NULL;
}

/* Remux a demuxed Vorbis track into an Ogg/Vorbis stream. The three setup
 * headers live in CodecPrivate (Xiph-laced: count-1, then 255-summed sizes for
 * all but the last). Vorbis has no cheap per-packet sample count, so granulepos
 * is derived from each block's Matroska timestamp (accurate to a block); the
 * final page is bumped to the track duration so the reported length is exact. */
static unsigned char* remux_vorbis(mkv_demux* mk, const mkv_audio_info* ai, long long* outlen)
{
    const unsigned char* cp = ai->codec_private;
    int cpl = ai->codec_private_len;
    if (!cp || cpl < 3 || cp[0] != 2) return NULL;   /* need exactly 3 headers */

    int p = 1, s0 = 0, s1 = 0;
    while (p < cpl && cp[p] == 255) { s0 += 255; p++; } if (p < cpl) s0 += cp[p++];
    while (p < cpl && cp[p] == 255) { s1 += 255; p++; } if (p < cpl) s1 += cp[p++];
    long l0 = s0, l1 = s1, l2 = (long)cpl - (p + s0 + s1);
    if (l2 < 0 || (long)p + s0 + s1 > cpl) return NULL;
    const unsigned char* h0 = cp + p;
    const unsigned char* h1 = cp + p + s0;
    const unsigned char* h2 = cp + p + s0 + s1;

    bytebuf out = {0};
    ogg_stream_state os;
    if (ogg_stream_init(&os, 0x434D4D32)) return NULL;   /* serialno 'CMM2' */

    ogg_packet op;
    op.b_o_s = 1; op.e_o_s = 0; op.granulepos = 0; op.packetno = 0;
    op.packet = (unsigned char*)h0; op.bytes = l0;
    ogg_stream_packetin(&os, &op);
    if (!ogg_drain(&os, &out, 1)) goto fail;             /* id header alone (BOS) */

    op.b_o_s = 0; op.granulepos = 0;
    op.packet = (unsigned char*)h1; op.bytes = l1; op.packetno = 1; ogg_stream_packetin(&os, &op);
    op.packet = (unsigned char*)h2; op.bytes = l2; op.packetno = 2; ogg_stream_packetin(&os, &op);
    if (!ogg_drain(&os, &out, 1)) goto fail;             /* comment + setup */

    {
        int sr = ai->sample_rate > 0 ? ai->sample_rate : 48000;
        long long granule = 0, packetno = 3;
        unsigned char* pend = NULL; int pendlen = 0, pendcap = 0, have = 0;
        long long pend_tc = 0;
        const unsigned char* pkt; int plen; long long tc;
        int r;
        while ((r = mkv_next_packet(mk, &pkt, &plen, &tc)) == 1) {
            if (have) {
                long long g = pend_tc / 1000000000LL * sr +
                              (pend_tc % 1000000000LL) * sr / 1000000000LL;
                if (g < granule) g = granule;
                granule = g;
                op.packet = pend; op.bytes = pendlen;
                op.b_o_s = 0; op.e_o_s = 0; op.granulepos = granule; op.packetno = packetno++;
                ogg_stream_packetin(&os, &op);
                if (!ogg_drain(&os, &out, 0)) { free(pend); goto fail; }
            }
            if (plen > pendcap) {
                unsigned char* nb = (unsigned char*)realloc(pend, (size_t)plen);
                if (!nb) { free(pend); goto fail; }
                pend = nb; pendcap = plen;
            }
            memcpy(pend, pkt, (size_t)plen); pendlen = plen; pend_tc = tc; have = 1;
        }
        if (r < 0) { free(pend); goto fail; }
        if (have) {
            long long g = (long long)(ai->duration_sec > 0 ? ai->duration_sec * sr + 0.5 : 0);
            if (g <= granule) g = granule + 1;
            granule = g;
            op.packet = pend; op.bytes = pendlen;
            op.b_o_s = 0; op.e_o_s = 1; op.granulepos = granule; op.packetno = packetno++;
            ogg_stream_packetin(&os, &op);
        }
        free(pend);
        if (!ogg_drain(&os, &out, 1)) goto fail;
    }

    ogg_stream_clear(&os);
    *outlen = (long long)out.len;
    return out.p;

fail:
    ogg_stream_clear(&os);
    free(out.p);
    return NULL;
}

/* Concatenate every demuxed packet, optionally prefixed by `prefix` bytes.
 * Serves MP3 (raw self-syncing frames → dr_mp3) and FLAC (the CodecPrivate
 * "fLaC"+STREAMINFO header followed by the raw FLAC frame packets → dr_flac). */
static unsigned char* remux_concat(mkv_demux* mk, const unsigned char* prefix, int prefix_len,
                                   long long* outlen)
{
    bytebuf out = {0};
    if (prefix_len > 0 && !bb_append(&out, prefix, (size_t)prefix_len)) { free(out.p); return NULL; }

    const unsigned char* pkt; int plen; long long tc;
    int r;
    while ((r = mkv_next_packet(mk, &pkt, &plen, &tc)) == 1) {
        if (!bb_append(&out, pkt, (size_t)plen)) { free(out.p); return NULL; }
    }
    if (r < 0 || out.len == 0) { free(out.p); return NULL; }
    *outlen = (long long)out.len;
    return out.p;
}

/* The 11 ADTS sampling-frequency indices (index 15 = explicit rate, unused for
 * the music this targets). Used to map an AAC AudioSpecificConfig rate index. */
static int adts_freq_index(int asc_index) { return (asc_index >= 0 && asc_index <= 12) ? asc_index : 4; }

/* Remux a demuxed AAC track to a raw ADTS stream: each access unit gets a 7-byte
 * ADTS header built from the AudioSpecificConfig in CodecPrivate (objectType,
 * sampling-frequency index, channel config). cmm_aac_open_memory then decodes it
 * through fdk-aac's TT_MP4_ADTS path exactly like a .aac file. */
static unsigned char* remux_aac(mkv_demux* mk, const mkv_audio_info* ai, long long* outlen)
{
    const unsigned char* cp = ai->codec_private;
    if (!cp || ai->codec_private_len < 2) return NULL;

    /* AudioSpecificConfig: 5 bits objectType, 4 bits freqIndex, 4 bits chanCfg. */
    int obj_type  = (cp[0] >> 3) & 0x1F;
    int freq_idx  = ((cp[0] & 0x07) << 1) | ((cp[1] >> 7) & 0x01);
    int chan_cfg  = (cp[1] >> 3) & 0x0F;
    if (obj_type < 1) obj_type = 2;                  /* default AAC-LC */
    if (chan_cfg < 1) chan_cfg = ai->channels > 0 ? ai->channels : 2;
    int profile = (obj_type - 1) & 0x03;             /* ADTS profile = objectType-1 */
    int fi = adts_freq_index(freq_idx);

    bytebuf out = {0};
    const unsigned char* pkt; int plen; long long tc;
    int r;
    while ((r = mkv_next_packet(mk, &pkt, &plen, &tc)) == 1) {
        int frame_len = plen + 7;
        if (frame_len >= (1 << 13)) { free(out.p); return NULL; }   /* exceeds ADTS field */
        unsigned char h[7];
        h[0] = 0xFF;
        h[1] = 0xF1;                                  /* MPEG-4, layer 0, no CRC */
        h[2] = (unsigned char)((profile << 6) | (fi << 2) | ((chan_cfg >> 2) & 0x01));
        h[3] = (unsigned char)(((chan_cfg & 0x03) << 6) | ((frame_len >> 11) & 0x03));
        h[4] = (unsigned char)((frame_len >> 3) & 0xFF);
        h[5] = (unsigned char)(((frame_len & 0x07) << 5) | 0x1F);
        h[6] = 0xFC;                                  /* buffer fullness VBR, 1 frame */
        if (!bb_append(&out, h, 7) || !bb_append(&out, pkt, (size_t)plen)) { free(out.p); return NULL; }
    }
    if (r < 0 || out.len == 0) { free(out.p); return NULL; }
    *outlen = (long long)out.len;
    return out.p;
}

/* Per-codec remux dispatch. */
static unsigned char* remux_track(mkv_demux* mk, const mkv_audio_info* ai, long long* outlen)
{
    switch (ai->codec) {
        case MKV_CODEC_OPUS:   return remux_opus(mk, ai, outlen);
        case MKV_CODEC_VORBIS: return remux_vorbis(mk, ai, outlen);
        case MKV_CODEC_AAC:    return remux_aac(mk, ai, outlen);
        case MKV_CODEC_MP3:    return remux_concat(mk, NULL, 0, outlen);
        case MKV_CODEC_FLAC:   return remux_concat(mk, ai->codec_private, ai->codec_private_len, outlen);
        default:               return NULL;
    }
}

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

/* Open the inner decoder on the remuxed buffer (it copies the bytes, so we free
 * our buffer immediately after). Fills the out-params from the inner decoder. */
static cmm_decoder* open_inner(int codec, unsigned char* buf, long long len,
                               int* channels, int* sampleRate, long long* totalFrames, int* err)
{
    cmm_decoder* in = NULL;
    switch (codec) {
        case MKV_CODEC_OPUS:   in = cmm_opus_open_memory(buf, len, channels, sampleRate, totalFrames, err); break;
        case MKV_CODEC_VORBIS: in = cmm_vorbis_open_memory(buf, len, channels, sampleRate, totalFrames, err); break;
        case MKV_CODEC_AAC:    in = cmm_aac_open_memory(buf, len, channels, sampleRate, totalFrames, err); break;
        case MKV_CODEC_MP3:    in = cmm_mp3_open_memory(buf, len, channels, sampleRate, totalFrames, err); break;
        case MKV_CODEC_FLAC:   in = cmm_flac_open_memory(buf, len, channels, sampleRate, totalFrames, err); break;
        default: if (err) *err = -1; break;
    }
    return in;
}

/* Shared open: demux from `sr`, remux, open the inner decoder. On success the
 * returned handle is CONTAINER with d->inner set. */
static cmm_decoder* container_open(src_reader* sr, long long filesize,
                                   int* channels, int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;
    mkv_demux* mk = mkv_open(src_cb, sr, filesize);
    if (!mk) { if (err) *err = -1; return NULL; }

    mkv_audio_info ai;
    mkv_get_audio(mk, &ai);

    long long rlen = 0;
    unsigned char* remux = remux_track(mk, &ai, &rlen);
    mkv_close(mk);
    if (!remux) { if (err) *err = -1; return NULL; }

    int ierr = 0;
    cmm_decoder* in = open_inner(ai.codec, remux, rlen, channels, sampleRate, totalFrames, &ierr);
    free(remux);
    if (!in) { if (err) *err = ierr ? ierr : -1; return NULL; }

    cmm_decoder* d = (cmm_decoder*)calloc(1, sizeof(cmm_decoder));
    if (!d) {
        close_inner(in);
        if (err) *err = -1;
        return NULL;
    }
    d->kind  = CMM_KIND_CONTAINER;
    d->inner = in;
    return d;
}

CMM_API cmm_decoder* cmm_container_open_memory(const unsigned char* data, long long len,
                                               int* channels, int* sampleRate,
                                               long long* totalFrames, int* err)
{
    if (err) *err = 0;
    if (!data || len <= 0) { if (err) *err = -1; return NULL; }
    src_reader sr; sr.buf = data; sr.len = len; sr.fp = NULL;
    return container_open(&sr, len, channels, sampleRate, totalFrames, err);
}

CMM_API cmm_decoder* cmm_container_open_file(const wchar_t* path, int* channels,
                                             int* sampleRate, long long* totalFrames, int* err)
{
    if (err) *err = 0;
    FILE* fp = cmm_open_path(path);
    if (!fp) { if (err) *err = -1; return NULL; }
    long long filesize = 0;
    if (CMM_FSEEK(fp, 0, SEEK_END) == 0) { filesize = (long long)CMM_FTELL(fp); CMM_FSEEK(fp, 0, SEEK_SET); }
    if (filesize <= 0) { fclose(fp); if (err) *err = -1; return NULL; }

    src_reader sr; sr.buf = NULL; sr.len = filesize; sr.fp = fp;
    cmm_decoder* d = container_open(&sr, filesize, channels, sampleRate, totalFrames, err);
    fclose(fp);   /* the whole track was read during remux; inner decodes from memory */
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

CMM_API int cmm_container_seek(cmm_decoder* d, long long frame)
{
    if (!d || !d->inner) return -1;
    switch (d->inner->kind) {
        case CMM_KIND_OPUS:   return cmm_opus_seek(d->inner, frame);
        case CMM_KIND_VORBIS: return cmm_vorbis_seek(d->inner, frame);
        case CMM_KIND_AAC:    return cmm_aac_seek(d->inner, frame);
        case CMM_KIND_MP3:    return cmm_mp3_seek(d->inner, frame);
        case CMM_KIND_FLAC:   return cmm_flac_seek(d->inner, frame);
        default: return -1;
    }
}

CMM_API void cmm_container_close(cmm_decoder* d)
{
    if (!d) return;
    close_inner(d->inner);
    free(d);
}
