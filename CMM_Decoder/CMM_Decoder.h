/* ─────────────────────────────────────────────────────────────────────────
 * CMM_Decoder — native audio decode shim for ContextMusicManager
 * ─────────────────────────────────────────────────────────────────────────
 * A thin C ABI over battle-tested native decoders, so the managed mod can
 * decode without the heavy managed-heap allocation that fights Unity's
 * non-generational (Boehm) GC. Backends:
 *   - Ogg/Opus via libopusfile (pulls in libopus + libogg)  [phase 1]
 *   - FLAC via dr_flac (single-header, public domain)        [phase 2]
 * Decoders allocate their working memory natively (malloc), invisible to the
 * .NET GC; the only managed allocation left is the reusable output buffer the
 * caller owns.
 *
 * The ABI is format-agnostic in shape so vorbis/mp3 backends (stb_vorbis /
 * dr_mp3) can be added later behind the same handle pattern without disturbing
 * the managed P/Invoke surface.
 *
 * All functions are thread-compatible but NOT thread-safe on a single handle:
 * the caller serialises access to one cmm_decoder (the managed reader holds a
 * lock, exactly as the old SeekableOpusReader did).
 * ───────────────────────────────────────────────────────────────────────── */
#ifndef CMM_DECODER_H
#define CMM_DECODER_H

#include <wchar.h>   /* wchar_t for the *_open_file path params */

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#define CMM_API __declspec(dllexport)
#else
#define CMM_API __attribute__((visibility("default")))
#endif

typedef struct cmm_decoder cmm_decoder;

/* ABI version, bumped on any breaking change to the surface below. The managed
 * side checks this on load so a mismatched DLL fails loudly instead of crashing.
 *   v1: Opus.   v2: + FLAC.   v3: + Vorbis.   v4: + open_file (large-file path).
 *   v5: + MP3 (dr_mp3) and WAV/AIFF (dr_wav) — the last managed-decoder holdouts.
 *   v6: + AAC (fdk-aac + minimp4) — AAC-LC / HE-AAC in MP4/M4A or raw ADTS.
 *   v7: + container (Matroska/WebM) — demux one audio track and remux it into an
 *        existing backend (Opus/Vorbis→Ogg, AAC→ADTS, MP3 raw, FLAC native). */
#define CMM_DECODER_ABI 7
CMM_API int CMM_Decoder_abi(void);

/* ── File-based opens (large-file path) ───────────────────────────────────
 * The *_open_memory functions copy the whole file into RAM; for files too big
 * to buffer the managed side calls these instead, which stream from disk via a
 * native FILE* the decoder owns for its lifetime. `path` is a wide (UTF-16 on
 * Windows) string — opened with _wfopen so non-ASCII music paths work (bare
 * fopen is ANSI on Windows). Same out-params / read / seek / close contract as
 * the matching *_open_memory + *_read_float + *_seek + *_close functions: a
 * file-opened handle is used and freed through the exact same family (e.g. open
 * with cmm_flac_open_file, read with cmm_flac_read_float, free with
 * cmm_flac_close). Returns NULL on error (file missing / decode-init failure). */
CMM_API cmm_decoder* cmm_opus_open_file(const wchar_t* path, int* channels,
                                        int* sampleRate, long long* totalFrames, int* err);
CMM_API cmm_decoder* cmm_flac_open_file(const wchar_t* path, int* channels,
                                        int* sampleRate, long long* totalFrames, int* err);
CMM_API cmm_decoder* cmm_vorbis_open_file(const wchar_t* path, int* channels,
                                          int* sampleRate, long long* totalFrames, int* err);
CMM_API cmm_decoder* cmm_mp3_open_file(const wchar_t* path, int* channels,
                                       int* sampleRate, long long* totalFrames, int* err);
CMM_API cmm_decoder* cmm_wav_open_file(const wchar_t* path, int* channels,
                                       int* sampleRate, long long* totalFrames, int* err);
CMM_API cmm_decoder* cmm_aac_open_file(const wchar_t* path, int* channels,
                                       int* sampleRate, long long* totalFrames, int* err);

/* Open an Ogg/Opus stream from an in-memory buffer. The shim COPIES `data` into
 * its own native allocation, so the caller's (possibly pooled/reused) buffer can
 * be overwritten or freed immediately after this returns.
 *
 * On success returns a non-NULL handle and fills:
 *   channels    — decoded channel count
 *   sampleRate  — always 48000 for Opus
 *   totalFrames — total frames per channel (may be -1 for unseekable/unknown)
 * On failure returns NULL and sets *err to the libopusfile error code (negative).
 * Any out-param pointer may be NULL if the caller doesn't want it. */
CMM_API cmm_decoder* cmm_opus_open_memory(const unsigned char* data, long long len,
                                          int* channels, int* sampleRate,
                                          long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (which must hold at least maxFrames*channels floats). Loops internally
 * to fill the request, minimising P/Invoke crossings. Returns the number of
 * frames actually written: 0 at end-of-stream, <0 on a decode error. */
CMM_API int cmm_opus_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_opus_seek(cmm_decoder* d, long long frame);

/* Free the decoder and the copied source buffer. NULL-safe. */
CMM_API void cmm_opus_close(cmm_decoder* d);

/* ── FLAC (dr_flac) ───────────────────────────────────────────────────────
 * Same handle/lifetime contract as the Opus functions. Unlike Opus, FLAC
 * preserves its NATIVE sample rate (e.g. 44100/48000/96000), so *sampleRate is
 * read from the stream rather than fixed. The shim COPIES `data` (dr_flac reads
 * the buffer on demand for the life of the decoder, so the caller's pooled
 * buffer may be overwritten/freed immediately after this returns). */
CMM_API cmm_decoder* cmm_flac_open_memory(const unsigned char* data, long long len,
                                          int* channels, int* sampleRate,
                                          long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (>= maxFrames*channels floats). Returns frames written: 0 at EOF. */
CMM_API int cmm_flac_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_flac_seek(cmm_decoder* d, long long frame);

/* Free the decoder and the copied source buffer. NULL-safe. */
CMM_API void cmm_flac_close(cmm_decoder* d);

/* ── Vorbis (stb_vorbis) ──────────────────────────────────────────────────
 * Same handle/lifetime contract as the others. Like FLAC, Vorbis preserves its
 * NATIVE sample rate, so *sampleRate is read from the stream. stb_vorbis copies
 * nothing of `data` lazily — it keeps the buffer for the life of the decoder —
 * so the shim COPIES it (the caller's pooled buffer may be overwritten/freed
 * immediately after this returns). NOTE: stb_vorbis takes a 32-bit length, so
 * buffers >2 GiB are rejected (no real-world Vorbis music file is that large). */
CMM_API cmm_decoder* cmm_vorbis_open_memory(const unsigned char* data, long long len,
                                            int* channels, int* sampleRate,
                                            long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (>= maxFrames*channels floats). Returns frames written: 0 at EOF. */
CMM_API int cmm_vorbis_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_vorbis_seek(cmm_decoder* d, long long frame);

/* Free the decoder and the copied source buffer. NULL-safe. */
CMM_API void cmm_vorbis_close(cmm_decoder* d);

/* ── MP3 (dr_mp3) ─────────────────────────────────────────────────────────
 * Same handle/lifetime contract as the others; preserves the stream's native
 * sample rate. The shim COPIES `data` (dr_mp3 reads it on demand for the life of
 * the decoder). Total frame count is computed at open by a one-pass scan (as
 * NAudio's Mp3FileReader indexed at construction), so *totalFrames is exact. */
CMM_API cmm_decoder* cmm_mp3_open_memory(const unsigned char* data, long long len,
                                         int* channels, int* sampleRate,
                                         long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (>= maxFrames*channels floats). Returns frames written: 0 at EOF. */
CMM_API int cmm_mp3_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_mp3_seek(cmm_decoder* d, long long frame);

/* Free the decoder and the copied source buffer. NULL-safe. */
CMM_API void cmm_mp3_close(cmm_decoder* d);

/* ── WAV / AIFF (dr_wav) ──────────────────────────────────────────────────
 * One backend covers both RIFF/WAVE and AIFF containers (dr_wav detects the
 * container and converts any sample format to f32 on read). Same handle/lifetime
 * contract; preserves the native sample rate. The shim COPIES `data`. */
CMM_API cmm_decoder* cmm_wav_open_memory(const unsigned char* data, long long len,
                                         int* channels, int* sampleRate,
                                         long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (>= maxFrames*channels floats). Returns frames written: 0 at EOF. */
CMM_API int cmm_wav_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_wav_seek(cmm_decoder* d, long long frame);

/* Free the decoder and the copied source buffer. NULL-safe. */
CMM_API void cmm_wav_close(cmm_decoder* d);

/* ── AAC (fdk-aac + minimp4) ──────────────────────────────────────────────
 * Covers AAC-LC and HE-AAC in either an MP4/M4A container (demuxed by minimp4,
 * which also locates the audio track inside an MP4 that carries video) or a raw
 * ADTS .aac stream. A single open detects the container from the leading bytes;
 * same handle/lifetime contract as the others. fdk-aac decodes to INT16 PCM
 * which the shim converts to interleaved f32, and the native sample rate is
 * preserved (HE-AAC's SBR rate is reported after the first frame is decoded).
 * The shim COPIES `data` for memory opens; file opens stream an MP4 from disk
 * (raw ADTS is read whole into memory — those files are small). For ADTS, which
 * has no seek index, *totalFrames is estimated by walking the frame headers;
 * seeking restarts from the stream origin and decodes forward. */
CMM_API cmm_decoder* cmm_aac_open_memory(const unsigned char* data, long long len,
                                         int* channels, int* sampleRate,
                                         long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (>= maxFrames*channels floats). Returns frames written: 0 at EOF. */
CMM_API int cmm_aac_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_aac_seek(cmm_decoder* d, long long frame);

/* Free the decoder and the copied source buffer. NULL-safe. */
CMM_API void cmm_aac_close(cmm_decoder* d);

/* ── Container (Matroska / WebM) ──────────────────────────────────────────
 * Demux ONE audio track from a .mkv/.webm and losslessly remux it into a
 * stream an existing backend opens (Opus/Vorbis → Ogg, AAC → ADTS, MP3 raw,
 * FLAC native), then decode through that backend. The whole track is demuxed
 * once at open, so the file open streams a large movie from disk without
 * buffering the video; the in-memory open is for small files / the loudness
 * sweep. Same handle/lifetime contract as the other backends — use the handle
 * only through the cmm_container_* family. Returns NULL if the file is not
 * Matroska/WebM, has no audio track, or uses an unsupported audio codec. */
CMM_API cmm_decoder* cmm_container_open_memory(const unsigned char* data, long long len,
                                               int* channels, int* sampleRate,
                                               long long* totalFrames, int* err);
CMM_API cmm_decoder* cmm_container_open_file(const wchar_t* path, int* channels,
                                             int* sampleRate, long long* totalFrames, int* err);

/* Decode up to `maxFrames` frames (per channel) of interleaved float PCM into
 * `out` (>= maxFrames*channels floats). Returns frames written: 0 at EOF. */
CMM_API int cmm_container_read_float(cmm_decoder* d, float* out, int maxFrames);

/* Seek to absolute frame `frame` (per channel). Returns 0 on success, <0 on error. */
CMM_API int cmm_container_seek(cmm_decoder* d, long long frame);

/* Free the decoder (and the inner backend it owns). NULL-safe. */
CMM_API void cmm_container_close(cmm_decoder* d);

#ifdef __cplusplus
}
#endif
#endif /* CMM_DECODER_H */
