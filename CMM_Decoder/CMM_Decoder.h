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
 *        existing backend (Opus/Vorbis→Ogg, AAC→ADTS, MP3 raw, FLAC native).
 *   v8: + audio OUTPUT (miniaudio) — cmm_out_* plays interleaved-f32 PCM straight
 *        to the OS default endpoint, independent of Unity's audio engine.
 *   v9: + format converter (miniaudio) — cmm_dc_* resamples + channel-maps a voice
 *        to the mixer's fixed 48k-stereo bus so any-rate tracks crossfade.
 *   v10:+ cmm_out_queued (live cushion depth) + cmm_out_reset (consumer-side flush)
 *        for the adaptive output ring (grow under CPU starvation, snap tight on a
 *        context change).
 *   v11:+ cmm_out_set_gain (consumer-side output gain ramp so menu↔game transition fades
 *        are heard in real output time, not buried behind the ring cushion) + cmm_out_reset_gain
 *        (flush + restore gain atomically so restarting after a fade can't bleed the old track).
 *   v12:+ native MIXER (cmm_mix_*) — the per-sample decode→convert→gain→sum→write producer loop
 *        moved out of managed C# so the GC can't suspend it mid-stream (see cmm_mix.c).
 *   v13:+ native LOUDNESS analyzer (cmm_loudness_*) — the EBU R128 sweep's K-weighting + gating
 *        moved out of managed C# so the sweep allocates nothing on the GC heap (see cmm_loudness.c). */
#define CMM_DECODER_ABI 13
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

/* ── Audio output (miniaudio) ─────────────────────────────────────────────
 * Plays interleaved 32-bit-float PCM to the OS DEFAULT output endpoint in shared
 * mode — i.e. through the same device, and under the same Windows volume-mixer
 * entry, as the host process's other audio (so CMM shares 7 Days' volume slider
 * rather than appearing separately). This is the playback counterpart to the
 * decoders above: the managed mixer thread decodes/mixes/gains into f32 frames
 * and pushes them here, bypassing Unity's AudioSource/AudioClip entirely.
 *
 * Threading: a handle backs a single-producer/single-consumer ring buffer. The
 * caller's mixer thread is the only writer (cmm_out_write); miniaudio's realtime
 * audio thread is the only reader. Do NOT call cmm_out_write from two threads.
 * cmm_out_open starts the device immediately (it emits silence until fed). */
typedef struct cmm_out cmm_out;

/* Open + start a playback device. `ringFrames` is the ring capacity in frames
 * (<=0 selects ~200ms at the given rate). Returns NULL on failure with *err set
 * to the miniaudio ma_result, or a small negative for bad args / OOM. */
CMM_API cmm_out* cmm_out_open(int sampleRate, int channels, int ringFrames, int* err);

/* Enqueue up to `frames` interleaved-f32 frames (data holds frames*channels
 * floats). Non-blocking: returns frames actually accepted; retry the remainder
 * after a short sleep when the ring is full. */
CMM_API int cmm_out_write(cmm_out* o, const float* data, int frames);

/* Frames of free space in the ring right now (lets the producer pace itself). */
CMM_API int cmm_out_avail(cmm_out* o);

/* Frames currently queued for playback — the live cushion / output latency. The
 * producer targets a fill level from this rather than always topping the ring off. */
CMM_API int cmm_out_queued(cmm_out* o);

/* Request a flush: the next audio callback discards all queued frames, snapping the
 * cushion to empty (then it refills tight). Only raises a flag — safe from the
 * producer/control thread; the actual discard happens on the callback. */
CMM_API void cmm_out_reset(cmm_out* o);

/* Set the consumer-side output gain (0..1), optionally ramped over rampFrames output
 * frames (<=0 snaps). Applied by the audio callback, so a transition fade is heard in
 * real output time regardless of cushion depth / producer starvation. */
CMM_API void cmm_out_set_gain(cmm_out* o, float target, int rampFrames);

/* Flush the queued (stale) frames AND restore the output gain to `target` atomically inside
 * the callback — use when restarting music after a fade so no stale full-volume frame bleeds
 * through at the restored gain. */
CMM_API void cmm_out_reset_gain(cmm_out* o, float target);

/* Stop the device and free the handle. NULL-safe. */
CMM_API void cmm_out_close(cmm_out* o);

/* ── Format converter (resample + channel map) ────────────────────────────
 * Streaming f32→f32 sample-rate + channel converter (miniaudio's data
 * converter). The mixer gives each voice whose native format differs from the
 * 48k-stereo bus one of these, feeds it decoded input frames, and pulls bus-
 * format output frames — so tracks at any sample rate mix and crossfade on a
 * single fixed bus instead of forcing a device reopen on every rate change. */
typedef struct cmm_dc cmm_dc;

/* Open a converter from (inRate,inChannels) to (outRate,outChannels). f32 both
 * ends. Returns NULL on failure with *err set (ma_result, or small negative). */
CMM_API cmm_dc* cmm_dc_open(int inRate, int inChannels, int outRate, int outChannels, int* err);

/* Convert: consume up to inFrames input frames, produce up to outCap output
 * frames. *inConsumed / *outWritten report what actually happened (the converter
 * buffers internally, so neither need equal the request). inFrames=0 drains the
 * tail at end-of-stream. Returns 0 on success, <0 on error. */
CMM_API int cmm_dc_process(cmm_dc* c, const float* in, long long inFrames, long long* inConsumed,
                           float* out, long long outCap, long long* outWritten);

/* Free the converter. NULL-safe. */
CMM_API void cmm_dc_close(cmm_dc* c);

/* ── Native mixer (producer loop) ─────────────────────────────────────────
 * Moves the whole decode→convert→gain-ramp→sum→write producer loop into native
 * code on its own thread, so the game's GC (stop-the-world for managed threads)
 * can't suspend it and underrun the output ring. The managed side keeps DECIDING
 * what plays (which file, when to switch, fades, resume); it opens each decoder
 * (off the audio loop) and hands the cmm_decoder* + a format `kind` to the mixer,
 * which then reads/mixes it entirely natively. Reuses cmm_out (output) + cmm_dc
 * (per-voice converters). A faithful port of the managed CmmMixer; a singleton.
 *
 * `kind` matches the managed AudioDecoder.ClassifyReader:
 *   0=Opus 1=FLAC 2=Vorbis 3=MP3 4=WAV 5=AAC 6=Container(Matroska/WebM).
 * All control calls are thread-safe against the producer thread (one internal mutex). */

/* Create the mixer + start the producer thread (idempotent). busRate/busChannels =
 * the fixed mix bus (48000/2). tightFrames/maxFrames = the tight and deep output-ring
 * cushion depths in FRAMES (the managed side computes them from config). The device
 * opens lazily on the first cmm_mix_add. Returns 0 on success. */
CMM_API int cmm_mix_start(int busRate, int busChannels, int tightFrames, int maxFrames, int* err);

/* Install a track and crossfade to it. `dec`+`kind` name a decoder the caller opened;
 * srcRate/srcChannels/totalFrames describe it (totalFrames<=0 = unknown). fade = crossfade
 * seconds; loop restarts at EOF; startFrame seeks before play (resume, 0 = from start);
 * trackGain = per-track loudness gain; duckOthersTo>=0 marks a PlayOnce stinger overlay
 * (existing voices DUCK to that level instead of fading out, and swell back when it ends);
 * snapTight!=0 flushes a deep cushion (a context change). Takes ownership of `dec` (frees
 * it on retire). Returns a voice id (0 on failure) to poll via cmm_mix_get_state/remaining. */
CMM_API long long cmm_mix_add(cmm_decoder* dec, int kind, int srcRate, int srcChannels,
                              long long totalFrames, float fade, int loop, long long startFrame,
                              float trackGain, float duckOthersTo, int snapTight);

/* Fade ALL voices out over `fade`. closeWhenIdle!=0 closes the device once silent;
 * pass 0 to keep it open for a hand-off (e.g. a menu→game fade into the spawn stinger). */
CMM_API void cmm_mix_stop(float fade, int closeWhenIdle);

/* Retire + discard every voice immediately (no fade) and flush the queued output. */
CMM_API void cmm_mix_stop_all_now(void);

/* Master volume (music slider × modifier), applied to the summed bus. */
CMM_API void cmm_mix_set_master(float vol);

/* Update a live voice's per-track gain in place (loudness settle / trim keybind), ramped
 * over rampSeconds (<=0 snaps). Returns 0 if the voice is gone. */
CMM_API int cmm_mix_set_voice_gain(long long id, float trackGain, float rampSeconds);

/* Reverse-fade reclaim: ramp voice `id` back to full and fade the others out. Returns 0
 * when the voice is gone/inaudible (caller falls back to a fresh cmm_mix_add). */
CMM_API int cmm_mix_reclaim(long long id, float fade);

/* Playhead + length (seconds) of a voice; *active=0 (return 0) when the id is gone. */
CMM_API int cmm_mix_get_state(long long id, double* posSec, double* lenSec, int* active);

/* Seconds left in a voice: -1 gone, +Inf unknown length, else remaining play time. */
CMM_API double cmm_mix_remaining(long long id);

/* Consumer-side output-gain ramp over `seconds` (<=0 snaps) — menu↔game transition fades,
 * heard in real output time regardless of cushion depth (forwards to cmm_out_set_gain). */
CMM_API void cmm_mix_set_output_gain(float target, float seconds);

/* Proactive deep-cushion flags: deep = world loading (its falling edge snap-flushes tight),
 * extra = post-spawn/teleport grace (drains naturally, no flush → no track teleport). */
CMM_API void cmm_mix_set_cushion(int deep, int extra);

/* Ask the producer thread to reopen the output device (OS default endpoint changed). */
CMM_API void cmm_mix_request_reinit(void);

/* ── Loudness analyzer (EBU R128 / ITU-R BS.1770 integrated loudness) ──────
 * A native port of the managed CMM.R128Analyzer, so the loudness sweep decodes AND
 * measures entirely in native memory — no managed-heap churn to trip Mono's stop-the-
 * world GC and hitch the game's main thread. Two surfaces:
 *   - streaming (cmm_loudness_new/_feed/_finish/_free): feed interleaved-f32 PCM in any
 *     chunking, then finish once. Mirrors R128Analyzer; used by the self-test.
 *   - one-shot (cmm_loudness_analyze): the caller opens a decoder (as for cmm_mix_add) and
 *     hands the cmm_decoder* + a format `kind` (same 0..6 mapping as cmm_mix); this reads it
 *     to EOF, measures, and TAKES OWNERSHIP (closes the decoder). This is the identical
 *     C#→native decoder handoff the mixer uses. */
typedef struct cmm_loudness cmm_loudness;

/* Create a streaming analyzer for the given source rate/channels (coefficients are
 * sample-rate-derived). Free with cmm_loudness_free. NULL on allocation failure. */
CMM_API cmm_loudness* cmm_loudness_new(int sampleRate, int channels);

/* Feed `count` interleaved floats (frames × channels). Partial trailing frames ignored. */
CMM_API void cmm_loudness_feed(cmm_loudness* a, const float* data, int count);

/* Finalise: *outLufs = integrated LUFS (only meaningful when the return is 1), *outPeak =
 * highest absolute sample seen (always set). Returns 1 when a gated result exists, else 0
 * (track too short/quiet — caller treats as "no normalization"). */
CMM_API int cmm_loudness_finish(cmm_loudness* a, double* outLufs, double* outPeak);

CMM_API void cmm_loudness_free(cmm_loudness* a);

/* One-shot: read `dec` (opened by the caller; `kind` 0..6 as in cmm_mix) to EOF and measure.
 * TAKES OWNERSHIP of `dec` (closes it before returning). Same out-params/return as finish. */
CMM_API int cmm_loudness_analyze(cmm_decoder* dec, int kind, int srcRate, int srcChannels,
                                 double* outLufs, double* outPeak);

#ifdef __cplusplus
}
#endif
#endif /* CMM_DECODER_H */
