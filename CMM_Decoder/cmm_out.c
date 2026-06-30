/* cmm_out.c — native audio OUTPUT for ContextMusicManager (miniaudio).
 * ─────────────────────────────────────────────────────────────────────────
 * The decode side of this DLL turns compressed files into interleaved f32 PCM.
 * This file is the other end: it opens a playback device on the OS default
 * endpoint and lets the managed mixer thread push that PCM straight to the
 * speakers, completely independent of Unity's audio engine and its threads.
 *
 * Design (the important part is the thread split):
 *   - ONE ma_pcm_rb ring buffer (single-producer / single-consumer, lock-free).
 *   - The managed mixer thread is the sole PRODUCER: it decodes + mixes + applies
 *     gain, then calls cmm_out_write() to enqueue frames. Non-blocking.
 *   - miniaudio's realtime audio callback is the sole CONSUMER: it drains the ring
 *     into the device buffer (a memcpy). NO managed code, NO decode, NO malloc runs
 *     on that callback — so the GC can never stall the audio thread.
 *   - On underrun (producer fell behind) the callback emits silence for the missing
 *     frames rather than blocking; the ring should be sized to absorb scheduler jitter
 *     (~150-250ms is plenty for a music bed).
 *
 * Device config: default endpoint, SHARED mode. Sharing the endpoint means we appear
 * under the host process's existing entry in the Windows volume mixer (i.e. 7 Days'
 * own slider), which is what we want — not a separate application volume. Under Proton
 * this rides Wine's WASAPI → PulseAudio/PipeWire bridge, the same path the game's audio
 * already takes.
 *
 * A cmm_out handle is NOT thread-safe for concurrent writers; the contract is one
 * producer (the mixer thread) + miniaudio's callback, exactly as ma_pcm_rb requires. */

#include "CMM_Decoder.h"

/* Must match miniaudio_impl.c so the (gated) header declarations line up. Also
 * passed on the gcc command line; defining here too keeps a direct compile honest. */
#ifndef MA_NO_DECODING
#define MA_NO_DECODING
#endif
#ifndef MA_NO_ENCODING
#define MA_NO_ENCODING
#endif
#ifndef MA_NO_GENERATION
#define MA_NO_GENERATION
#endif
#ifndef MA_NO_RESOURCE_MANAGER
#define MA_NO_RESOURCE_MANAGER
#endif
#ifndef MA_NO_NODE_GRAPH
#define MA_NO_NODE_GRAPH
#endif
#include "miniaudio.h"

#include <stdlib.h>
#include <string.h>

struct cmm_out {
    ma_device device;
    ma_pcm_rb rb;
    int       channels;
    int       sampleRate;
    int       started;     /* device successfully started (uninit must stop it) */
    volatile ma_uint32 flush;  /* set by producer (cmm_out_reset); serviced + cleared by the callback */
    /* Consumer-side output gain (transition fades). Applied in the callback so a fade is
     * heard over real OUTPUT time regardless of how deep the producer's ring cushion is or
     * whether the producer thread is starved — neither of which the producer-side per-voice
     * gain can overcome (its ramp sits buried behind the queued cushion). gainTarget/gainStep
     * are written by the control thread (cmm_out_set_gain); gain is advanced only by the
     * callback. A benign float race at worst — fine for a volume ramp. */
    volatile float gain;        /* current applied gain (callback owns) */
    volatile float gainTarget;  /* where the ramp is heading */
    volatile float gainStep;    /* per-frame increment toward target (0 = steady) */
    /* Atomic "flush then restore gain": set by cmm_out_reset_gain so the callback discards the
     * stale queue and ONLY THEN snaps the gain. Without coupling, a separate reset()+set_gain()
     * lets a callback already mid-drain emit stale full-volume frames at the restored gain (an
     * audible bleed of the previous track when music restarts after a fade). */
    volatile float flushGain;
    volatile int   flushSetGain;
};

/* Realtime consumer: drain the ring into `output`. Runs on miniaudio's audio
 * thread — keep it to memcpy + memset only. */
static void cmm_out_data_callback(ma_device* dev, void* output, const void* input, ma_uint32 frameCount)
{
    cmm_out* o = (cmm_out*)dev->pUserData;
    float* out = (float*)output;
    ma_uint32 remaining = frameCount;
    (void)input;

    /* Flush request: discard every queued frame so freshly-written audio plays
     * immediately instead of behind a deep cushion. Done HERE (the consumer) so we
     * only ever move the read pointer from the thread that owns it — ma_pcm_rb is
     * SPSC, so advancing read from a foreign thread would be a data race. The
     * producer just raises o->flush; we drain to empty and clear it. This buffer
     * then underruns to silence (a few ms) and the producer refills with the new mix. */
    if (o->flush) {
        o->flush = 0;
        ma_uint32 avail = ma_pcm_rb_available_read(&o->rb);
        while (avail > 0) {
            ma_uint32 n = avail;
            void* p = NULL;
            if (ma_pcm_rb_acquire_read(&o->rb, &n, &p) != MA_SUCCESS || n == 0) break;
            ma_pcm_rb_commit_read(&o->rb, n);
            avail -= n;
        }
        /* Restore the gain ONLY after discarding the stale queue, so no stale frame is ever
         * emitted at the restored gain (see flushSetGain). A concurrent callback that didn't see
         * the flag drains stale frames at the OLD (faded ≈0) gain instead → silent, not a bleed. */
        if (o->flushSetGain) {
            o->flushSetGain = 0;
            o->gain = o->flushGain;
            o->gainTarget = o->flushGain;
            o->gainStep = 0.0f;
        }
    }

    while (remaining > 0) {
        ma_uint32 n = remaining;
        void* readPtr = NULL;
        if (ma_pcm_rb_acquire_read(&o->rb, &n, &readPtr) != MA_SUCCESS || n == 0)
            break;
        memcpy(out, readPtr, (size_t)n * o->channels * sizeof(float));
        ma_pcm_rb_commit_read(&o->rb, n);
        out += (size_t)n * o->channels;
        remaining -= n;
    }

    /* Underrun: fill the rest with silence so we glitch quietly instead of
     * replaying stale ring contents. */
    if (remaining > 0)
        memset(out, 0, (size_t)remaining * o->channels * sizeof(float));

    /* Consumer-side output gain ramp (transition fades). Applied over the WHOLE output
     * buffer (the underrun silence above is already 0, so scaling it is a no-op) so a fade
     * lands in real output time, not behind the producer's cushion. */
    {
        float g = o->gain, gt = o->gainTarget, gs = o->gainStep;
        if (gs != 0.0f || g != 1.0f) {
            ma_uint32 ch = (ma_uint32)o->channels;
            float* p = (float*)output;
            ma_uint32 f;
            for (f = 0; f < frameCount; f++) {
                ma_uint32 c;
                for (c = 0; c < ch; c++) *p++ *= g;
                if (gs != 0.0f) {
                    g += gs;
                    if ((gs > 0.0f && g >= gt) || (gs < 0.0f && g <= gt)) { g = gt; gs = 0.0f; }
                }
            }
            o->gain = g;
            o->gainStep = gs;
        }
    }
}

/* Open + start a playback device on the default endpoint.
 *   sampleRate / channels  — the interleaved-f32 format the mixer will feed.
 *   ringFrames             — ring capacity in frames (<=0 → ~200ms default).
 * Returns a handle, or NULL on failure with *err set to the ma_result (or a
 * small negative for argument/alloc errors). */
CMM_API cmm_out* cmm_out_open(int sampleRate, int channels, int ringFrames, int* err)
{
    if (err) *err = 0;
    if (sampleRate <= 0 || channels <= 0) { if (err) *err = -1; return NULL; }

    cmm_out* o = (cmm_out*)calloc(1, sizeof(cmm_out));
    if (!o) { if (err) *err = -2; return NULL; }
    o->channels   = channels;
    o->sampleRate = sampleRate;
    o->gain = 1.0f; o->gainTarget = 1.0f; o->gainStep = 0.0f;

    if (ringFrames <= 0)
        ringFrames = sampleRate / 5;   /* ~200ms */

    ma_result r = ma_pcm_rb_init(ma_format_f32, (ma_uint32)channels,
                                 (ma_uint32)ringFrames, NULL, NULL, &o->rb);
    if (r != MA_SUCCESS) { if (err) *err = (int)r; free(o); return NULL; }

    ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
    cfg.playback.format   = ma_format_f32;
    cfg.playback.channels = (ma_uint32)channels;
    cfg.sampleRate        = (ma_uint32)sampleRate;
    cfg.dataCallback      = cmm_out_data_callback;
    cfg.pUserData         = o;
    /* pContext NULL + no pDeviceID = default endpoint, shared mode. */

    r = ma_device_init(NULL, &cfg, &o->device);
    if (r != MA_SUCCESS) {
        ma_pcm_rb_uninit(&o->rb);
        if (err) *err = (int)r;
        free(o);
        return NULL;
    }

    r = ma_device_start(&o->device);
    if (r != MA_SUCCESS) {
        ma_device_uninit(&o->device);
        ma_pcm_rb_uninit(&o->rb);
        if (err) *err = (int)r;
        free(o);
        return NULL;
    }
    o->started = 1;
    return o;
}

/* Enqueue up to `frames` interleaved-f32 frames. Non-blocking: writes only what
 * fits in the ring right now and returns the number of frames accepted (may be
 * 0 or < frames when the ring is full — the producer should retry the remainder
 * after a short sleep). */
CMM_API int cmm_out_write(cmm_out* o, const float* data, int frames)
{
    if (!o || !data || frames <= 0) return 0;
    int written = 0;
    while (written < frames) {
        ma_uint32 n = (ma_uint32)(frames - written);
        void* writePtr = NULL;
        if (ma_pcm_rb_acquire_write(&o->rb, &n, &writePtr) != MA_SUCCESS || n == 0)
            break;
        memcpy(writePtr, data + (size_t)written * o->channels,
               (size_t)n * o->channels * sizeof(float));
        ma_pcm_rb_commit_write(&o->rb, n);
        written += (int)n;
    }
    return written;
}

/* Frames of free space available to write right now (for the producer to pace
 * how much it decodes/mixes per tick). */
CMM_API int cmm_out_avail(cmm_out* o)
{
    if (!o) return 0;
    return (int)ma_pcm_rb_available_write(&o->rb);
}

/* Frames currently QUEUED for playback (the live cushion / latency). The producer
 * uses this to target a fill level rather than always topping the ring off. */
CMM_API int cmm_out_queued(cmm_out* o)
{
    if (!o) return 0;
    return (int)ma_pcm_rb_available_read(&o->rb);
}

/* Request a flush: the next callback discards all queued frames (see the callback).
 * Lets the producer snap latency tight at a context change after the cushion grew
 * deep, without reopening the device. Safe to call from any (single) producer/control
 * thread — it only raises a flag. */
CMM_API void cmm_out_reset(cmm_out* o)
{
    if (o) o->flush = 1;
}

/* Set the consumer-side output gain, optionally ramped over rampFrames output frames
 * (rampFrames <= 0 snaps immediately). Used for graceful menu↔game transition fades that
 * must be audible regardless of the producer's cushion depth or scheduling. The callback
 * advances the ramp and applies it; this only writes the target + step. */
CMM_API void cmm_out_set_gain(cmm_out* o, float target, int rampFrames)
{
    if (!o) return;
    if (target < 0.0f) target = 0.0f;
    o->gainTarget = target;
    if (rampFrames <= 0) { o->gain = target; o->gainStep = 0.0f; }
    else                 { o->gainStep = (target - o->gain) / (float)rampFrames; }
}

/* Flush the queue AND snap the output gain to `target`, atomically w.r.t. the callback:
 * the callback discards the (stale) queue, then sets the gain — so restarting music after a
 * fade can't bleed a hint of the previous track at full volume. Set the gain payload before
 * raising the flush flag the callback polls. */
CMM_API void cmm_out_reset_gain(cmm_out* o, float target)
{
    if (!o) return;
    if (target < 0.0f) target = 0.0f;
    o->flushGain    = target;
    o->flushSetGain = 1;
    o->flush        = 1;
}

/* Stop the device and free everything. NULL-safe. */
CMM_API void cmm_out_close(cmm_out* o)
{
    if (!o) return;
    if (o->started) ma_device_uninit(&o->device);   /* stops the callback, then frees */
    ma_pcm_rb_uninit(&o->rb);
    free(o);
}

/* ── Format converter (resample + channel map), f32 in/out ────────────────
 * One per mixer voice whose native rate/channels differ from the 48k-stereo mix
 * bus. Wraps miniaudio's ma_data_converter (linear resampler with a low-pass +
 * channel remap). Streaming: the caller feeds input frames and pulls output
 * frames; the converter buffers internally and reports how much it consumed. */
struct cmm_dc {
    ma_data_converter conv;
};

/* Open a converter from (inRate,inChannels) to (outRate,outChannels), f32 both
 * ends. Returns NULL on failure with *err = ma_result (or small negative). */
CMM_API cmm_dc* cmm_dc_open(int inRate, int inChannels, int outRate, int outChannels, int* err)
{
    if (err) *err = 0;
    if (inRate <= 0 || outRate <= 0 || inChannels <= 0 || outChannels <= 0) { if (err) *err = -1; return NULL; }

    cmm_dc* c = (cmm_dc*)calloc(1, sizeof(cmm_dc));
    if (!c) { if (err) *err = -2; return NULL; }

    ma_data_converter_config cfg = ma_data_converter_config_init(
        ma_format_f32, ma_format_f32,
        (ma_uint32)inChannels, (ma_uint32)outChannels,
        (ma_uint32)inRate,     (ma_uint32)outRate);

    ma_result r = ma_data_converter_init(&cfg, NULL, &c->conv);
    if (r != MA_SUCCESS) { if (err) *err = (int)r; free(c); return NULL; }
    return c;
}

/* Convert up to outCap output frames, consuming up to inFrames input frames.
 * On return *inConsumed and *outWritten report what actually happened (the
 * converter may consume input without emitting yet, or emit from its internal
 * buffer with no input). Pass inFrames=0 to drain the tail at end-of-stream.
 * Returns 0 on success, <0 on error. */
CMM_API int cmm_dc_process(cmm_dc* c, const float* in, long long inFrames, long long* inConsumed,
                           float* out, long long outCap, long long* outWritten)
{
    if (inConsumed) *inConsumed = 0;
    if (outWritten) *outWritten = 0;
    if (!c) return -1;

    ma_uint64 ic = (ma_uint64)(inFrames > 0 ? inFrames : 0);
    ma_uint64 oc = (ma_uint64)(outCap   > 0 ? outCap   : 0);
    ma_result r = ma_data_converter_process_pcm_frames(&c->conv, in, &ic, out, &oc);
    if (inConsumed) *inConsumed = (long long)ic;
    if (outWritten) *outWritten = (long long)oc;
    return r == MA_SUCCESS ? 0 : -1;
}

/* Free the converter. NULL-safe. */
CMM_API void cmm_dc_close(cmm_dc* c)
{
    if (!c) return;
    ma_data_converter_uninit(&c->conv, NULL);
    free(c);
}
