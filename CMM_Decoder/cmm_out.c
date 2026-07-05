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
#include <math.h>

#include "cmm_reverb.h"

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

    /* ── Consumer-side emitter layer (v20) ──────────────────────────────────
     * A SECOND ring carrying the emitter's DRY MONO signal, spatialized here in the callback from
     * the live state below (relayed by the producer each pump iteration). Doing the pan/attenuation
     * at output time — not in the producer, behind the ~200ms ring cushion — is what makes head-turn
     * panning feel real-time. State is written by the producer and read by the callback; a torn read
     * at worst mis-pans ONE callback (~10ms) — inaudible, so no lock. */
    ma_pcm_rb emrb;             /* mono ring for the emitter's dry signal */
    int       hasEmRing;        /* emrb was initialised (uninit on close) */
    volatile int   emPtCount;   /* speakers audible now (0 = no emitter output this block) */
    volatile float emLisPos[3];
    volatile float emLisRight[3];
    volatile float emPts[CMM_MAX_EMIT_PTS * 3];
    volatile float emGains[CMM_MAX_EMIT_PTS];   /* per-speaker occlusion VIRTUAL DISTANCE (extra blocks; 0 = clear) */
    volatile float emMinDist, emMaxDist;
    volatile float emFalloffExp;   /* plateau→range fade steepness (live tuning) */
    volatile float emMuffle;       /* distance low-pass strength 0..1 (live tuning) */
    volatile float emStereoWidth;  /* pan width 0..1: 1 = full hard pan, <1 keeps the far ear alive */
    float emGainsSmooth[CMM_MAX_EMIT_PTS];  /* per-point occlusion VIRTUAL DISTANCE slewed at audio rate (anti-pop) */

    /* Distance/occlusion low-pass state — callback-thread only (no volatile/lock needed).
     * emLpf is the one-pole's running mono output; emLpfCoef is its live smoothing coefficient
     * (1 = open/bypass, small = muffled), slewed toward the per-callback target to avoid zipper
     * clicks when distance/occlusion steps. This is the spectral distance cue: far & occluded
     * sound goes DULL, not just quiet, which is what makes an emitter localize "out there". */
    float emLpf;
    float emLpfCoef;

    /* Reverb (room reflections) for the emitter. Params relayed live from the managed room probe;
     * the reverb itself is callback-thread-only. The WET path is attenuated by REAL distance only, NOT
     * by occlusion — so behind a wall the dry sound drops away but the reflections persist, which is
     * what reads as "the sound bounced / it's in another room". */
    volatile float emRevWet;    /* master wet level 0..1 (0 = reverb off) */
    volatile float emRevRoom;   /* room size → decay/tail length 0..1 */
    volatile float emRevDamp;   /* HF damping 0..1 (dead/absorptive room = darker, shorter) */
    cmm_reverb reverb;
};

/* Compute the summed L/R spatial gain for the emitter from the live listener + speaker state,
 * plus a spectral BRIGHTNESS scalar [0..1] for the distance/occlusion low-pass. Mirrors what the
 * mixer used to do per-block, now at output time. Mono-in, so the caller applies gL/gR to each mono
 * sample. Equal-power pan by bearing to `right`, INVERSE-DISTANCE attenuation (physical proximity
 * feel, unlike the old flat-near ease-out), per-speaker occlusion gain, summed over all speakers and
 * magnitude-capped (anti-clip). Brightness comes from the DOMINANT (loudest) speaker so the tone
 * matches the sound you're mostly hearing: 1 = near & clear (open), →0 = far or wall-occluded (dull). */
/* Effective distance (blocks) at which the tone is fully muffled (per the Muffle strength), independent
 * of the volume Range. ~24 ≈ "clearly across a room / behind a wall". */
#define CMM_EMIT_MUFFLE_REF 24.0f
static void cmm_emitter_gains(cmm_out* o, float* gL, float* gR, float* bright, float* wet)
{
    float totalL = 0.0f, totalR = 0.0f;
    int count = o->emPtCount, p;
    if (count > CMM_MAX_EMIT_PTS) count = CMM_MAX_EMIT_PTS;
    float lx = o->emLisPos[0], ly = o->emLisPos[1], lz = o->emLisPos[2];
    float rx = o->emLisRight[0], ry = o->emLisRight[1], rz = o->emLisRight[2];
    float mn = o->emMinDist, mx = o->emMaxDist, span = mx - mn;
    if (span < 0.0001f) span = 0.0001f;
    float fexp = o->emFalloffExp, muffle = o->emMuffle;
    float bestContrib = -1.0f, bestBright = 1.0f, bestWet = 0.0f;
    for (p = 0; p < count; p++) {
        float dx = o->emPts[p*3]   - lx;
        float dy = o->emPts[p*3+1] - ly;
        float dz = o->emPts[p*3+2] - lz;
        float d  = sqrtf(dx*dx + dy*dy + dz*dz);

        /* OCCLUSION AS VIRTUAL DISTANCE: emGains[p] now carries not a gain but the extra blocks of
         * "distance" the walls / the go-around add (0 = clear line). Folding it into the distance means
         * a wall makes the radio sound FARTHER (quieter AND duller on the same smooth curve) instead of
         * applying a separate multiplicative cliff — which is what made it feel all-or-nothing. Slewed
         * at audio rate (exponential) so the change glides as you move, never pops. */
        float target = o->emGains[p], cur = o->emGainsSmooth[p];
        cur += (target - cur) * 0.06f;       /* ~0.5 s glide; auto-scales to the jump size */
        o->emGainsSmooth[p] = cur;
        float dEff = d + cur;                /* effective (acoustic) distance */

        /* Plateau + power fade on the EFFECTIVE distance: full inside mn, then att = ((mx−dEff)/span)^fexp
         * to silence at mx. mn/mx/fexp live-tunable. */
        float att;
        if (dEff <= mn) att = 1.0f;
        else if (dEff >= mx) att = 0.0f;
        else att = powf((mx - dEff) / span, fexp);

        /* Pan uses the REAL bearing to the speaker (direction is unchanged by occlusion). */
        float px = 0.0f;
        if (d > 1e-4f) {
            float inv = 1.0f / d;
            px = (dx*rx + dy*ry + dz*rz) * inv;
            if (px < -1.0f) px = -1.0f; else if (px > 1.0f) px = 1.0f;
        }
        /* Stereo width: scale the pan position so a hard-left/right source doesn't NULL the far ear
         * (equal-power puts theta at π/2 ⇒ cos=0). width<1 pulls px toward centre so both ears keep a
         * share — like real hearing, where a source at 90° is still audible on the far side, quieter. */
        px *= o->emStereoWidth;
        float theta = (px + 1.0f) * 0.78539816f;

        float cL = cosf(theta) * att;
        float cR = sinf(theta) * att;
        totalL += cL;
        totalR += cR;

        /* Track the loudest speaker; its EFFECTIVE distance sets the tone colour (far/occluded = dull).
         * The tone cue is measured against a FIXED reference distance (CMM_EMIT_MUFFLE_REF blocks), NOT
         * the full Range: normalizing to Range meant a large Range (e.g. 100) diluted muffle to nothing
         * at any audible distance, and diluted occlusion's virtual distance too. With an absolute ref,
         * the tone is fully dulled (per Muffle strength) by ~ref blocks of effective distance regardless
         * of how far the volume carries — and a wall's added virtual distance now audibly dulls. */
        float contrib = cL * cL + cR * cR;
        if (contrib > bestContrib) {
            bestContrib = contrib;
            float nd = (dEff - mn) / CMM_EMIT_MUFFLE_REF;
            if (nd < 0.0f) nd = 0.0f; else if (nd > 1.0f) nd = 1.0f;
            bestBright = 1.0f - muffle * nd;             /* farther / more-occluded ⇒ duller (one curve) */

            /* Wet (reverb) level for the dominant speaker: scaled by REAL distance (d, NOT dEff) so
             * occlusion doesn't attenuate the reflections — behind a wall the dry drops but the wet
             * stays, which is the "it bounced" cue. Same plateau/power curve as the dry. */
            float attReal;
            if (d <= mn) attReal = 1.0f;
            else if (d >= mx) attReal = 0.0f;
            else attReal = powf((mx - d) / span, fexp);
            bestWet = o->emRevWet * attReal;
        }
    }
    float mag = sqrtf(totalL*totalL + totalR*totalR);
    if (mag > 1.4f) { float s = 1.4f / mag; totalL *= s; totalR *= s; }
    if (bestBright < 0.0f) bestBright = 0.0f; else if (bestBright > 1.0f) bestBright = 1.0f;
    *gL = totalL; *gR = totalR; *bright = bestBright; *wet = bestWet;
}

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
        /* Flush the emitter ring too, so it snaps tight in lockstep with the music ring. */
        if (o->hasEmRing) {
            ma_uint32 ea = ma_pcm_rb_available_read(&o->emrb);
            while (ea > 0) {
                ma_uint32 n = ea; void* p = NULL;
                if (ma_pcm_rb_acquire_read(&o->emrb, &n, &p) != MA_SUCCESS || n == 0) break;
                ma_pcm_rb_commit_read(&o->emrb, n);
                ea -= n;
            }
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

    /* ── Emitter layer: spatialize the dry-mono ring into the output at OUTPUT TIME ──────────
     * The pan/attenuation/occlusion is computed here from the freshest relayed listener state, so a
     * head-turn is heard within a callback (~10ms), not behind the producer's ring cushion. The mono
     * ring is drained in lockstep with the music each callback. */
    if (o->hasEmRing) {
        float gL, gR, bright, wet;
        cmm_emitter_gains(o, &gL, &gR, &bright, &wet);
        int audible = (o->emPtCount > 0) && (gL != 0.0f || gR != 0.0f);
        int wetOn   = wet > 1e-4f;
        /* Refresh reverb decay/damping from the live room probe (cheap coefficient update). */
        if (wetOn) cmm_reverb_set(&o->reverb, o->emRevRoom, o->emRevDamp);

        /* Map brightness → one-pole LPF coefficient (1 = open/bypass, small = muffled), then SLEW
         * the live coefficient toward it so a distance/occlusion step doesn't click. bright*bright
         * dulls faster with distance (more "presence"); the 0.03 floor keeps a little air even
         * fully occluded so it never goes to pure DC. */
        float bb = bright * bright;
        float coefTarget = 0.03f + 0.97f * bb;
        float coef = o->emLpfCoef;
        float dc = coefTarget - coef;
        if (dc > 0.05f) dc = 0.05f; else if (dc < -0.05f) dc = -0.05f;  /* max step per callback */
        coef += dc;

        ma_uint32 got = 0;
        while (got < frameCount) {
            ma_uint32 n = frameCount - got;
            void* rp = NULL;
            if (ma_pcm_rb_acquire_read(&o->emrb, &n, &rp) != MA_SUCCESS || n == 0) break;
            if (audible) {
                const float* m = (const float*)rp;
                float* op = (float*)output + (size_t)got * o->channels;
                float y = o->emLpf;
                ma_uint32 f;
                for (f = 0; f < n; f++) {
                    y += coef * (m[f] - y);            /* one-pole distance/occlusion low-pass (mono) */
                    op[f * o->channels]     += y * gL;
                    op[f * o->channels + 1] += y * gR;
                    if (wetOn) {
                        /* Reverb feeds off the DRY mono (pre-occlusion-LPF) and mixes in at the wet
                         * gain, which is NOT occluded — so reflections carry around/through walls. */
                        float wl, wr;
                        cmm_reverb_tick(&o->reverb, m[f], &wl, &wr);
                        op[f * o->channels]     += wl * wet;
                        op[f * o->channels + 1] += wr * wet;
                    }
                }
                o->emLpf = y;
            }
            ma_pcm_rb_commit_read(&o->emrb, n);   /* commit even when inaudible → discard residual */
            got += n;
        }
        o->emLpfCoef = coef;
        /* Clip-guard the summed output (music + emitter can exceed unity). */
        if (audible) {
            float* p = (float*)output;
            ma_uint32 total = frameCount * (ma_uint32)o->channels, i;
            for (i = 0; i < total; i++) {
                float s = p[i];
                p[i] = s > 1.0f ? 1.0f : (s < -1.0f ? -1.0f : s);
            }
        }
    }

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
/* Allocate a cmm_out + init its ring, WITHOUT opening any device (started stays 0).
 * Shared by cmm_out_open (which then inits + starts a real device) and
 * cmm_out_open_null (which stops here). */
static cmm_out* cmm_out_alloc_ring(int sampleRate, int channels, int ringFrames, int* err)
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

    /* Second ring for the emitter's dry MONO signal (spatialized in the callback). Same frame depth
     * as the music ring so both cushions track. Failure here is non-fatal — we just run without the
     * emitter layer (hasEmRing stays 0) rather than losing music output. */
    o->emLisRight[0] = 1.0f;                 /* sane default until the producer relays a listener */
    o->emMinDist = 1.0f; o->emMaxDist = 2.0f;
    o->emFalloffExp = 1.5f; o->emMuffle = 0.7f;   /* sane curve until config is relayed */
    o->emStereoWidth = 0.85f;                     /* nearly full pan until config is relayed */
    o->emLpf = 0.0f; o->emLpfCoef = 1.0f;    /* LPF starts open (bypass) until a distance is relayed */
    o->emRevWet = 0.0f; o->emRevRoom = 0.5f; o->emRevDamp = 0.5f;   /* reverb off until the room probe relays */
    cmm_reverb_init(&o->reverb, sampleRate);
    if (ma_pcm_rb_init(ma_format_f32, 1, (ma_uint32)ringFrames, NULL, NULL, &o->emrb) == MA_SUCCESS)
        o->hasEmRing = 1;
    return o;
}

CMM_API cmm_out* cmm_out_open(int sampleRate, int channels, int ringFrames, int* err)
{
    cmm_out* o = cmm_out_alloc_ring(sampleRate, channels, ringFrames, err);
    if (!o) return NULL;

    ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
    cfg.playback.format   = ma_format_f32;
    cfg.playback.channels = (ma_uint32)channels;
    cfg.sampleRate        = (ma_uint32)sampleRate;
    cfg.dataCallback      = cmm_out_data_callback;
    cfg.pUserData         = o;
    /* pContext NULL + no pDeviceID = default endpoint, shared mode. */

    ma_result r = ma_device_init(NULL, &cfg, &o->device);
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

/* Open a HEADLESS output: a ring with NO audio device (started stays 0, so
 * cmm_out_close skips ma_device_uninit). Used by the offline stress harness
 * (tools/CmmSession) to run the REAL mixer producer at compressed speed — the
 * caller drains the ring itself via cmm_out_drain instead of a realtime device
 * callback, so voices decode / render / retire (exercising the mixer's native
 * alloc/free churn for leak hunting) without audio hardware or realtime pacing. */
CMM_API cmm_out* cmm_out_open_null(int sampleRate, int channels, int ringFrames, int* err)
{
    return cmm_out_alloc_ring(sampleRate, channels, ringFrames, err);
}

/* Manually consume up to `frames` from the ring (the headless counterpart to the
 * realtime callback, minus audio output): service a pending flush, then advance the
 * read pointer, discarding the frames. Returns the number of frames consumed. SPSC:
 * call from ONE consumer thread only (the harness pump), exactly as the device
 * callback is the sole reader in the realtime path. No-op / 0 on a real (started)
 * device — that path has its own callback consumer. */
CMM_API int cmm_out_drain(cmm_out* o, int frames)
{
    if (!o || frames <= 0) return 0;

    if (o->flush) {
        o->flush = 0;
        ma_uint32 avail = ma_pcm_rb_available_read(&o->rb);
        while (avail > 0) {
            ma_uint32 n = avail; void* p = NULL;
            if (ma_pcm_rb_acquire_read(&o->rb, &n, &p) != MA_SUCCESS || n == 0) break;
            ma_pcm_rb_commit_read(&o->rb, n);
            avail -= n;
        }
        if (o->flushSetGain) {
            o->flushSetGain = 0;
            o->gain = o->flushGain; o->gainTarget = o->flushGain; o->gainStep = 0.0f;
        }
    }

    int consumed = 0;
    while (consumed < frames) {
        ma_uint32 n = (ma_uint32)(frames - consumed);
        void* readPtr = NULL;
        if (ma_pcm_rb_acquire_read(&o->rb, &n, &readPtr) != MA_SUCCESS || n == 0) break;
        ma_pcm_rb_commit_read(&o->rb, n);
        consumed += (int)n;
    }
    /* Keep the emitter ring drained in lockstep (harness has no emitter, so usually empty). */
    if (o->hasEmRing) {
        int ec = 0;
        while (ec < consumed) {
            ma_uint32 n = (ma_uint32)(consumed - ec);
            void* rp = NULL;
            if (ma_pcm_rb_acquire_read(&o->emrb, &n, &rp) != MA_SUCCESS || n == 0) break;
            ma_pcm_rb_commit_read(&o->emrb, n);
            ec += (int)n;
        }
    }
    return consumed;
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

/* v20 — enqueue up to `frames` MONO f32 frames of the emitter's dry signal. Non-blocking; returns
 * frames accepted. SPSC: the producer (mixer thread) is the sole writer, like cmm_out_write. */
int cmm_out_write_emitter(cmm_out* o, const float* mono, int frames)
{
    if (!o || !o->hasEmRing || !mono || frames <= 0) return 0;
    int written = 0;
    while (written < frames) {
        ma_uint32 n = (ma_uint32)(frames - written);
        void* wp = NULL;
        if (ma_pcm_rb_acquire_write(&o->emrb, &n, &wp) != MA_SUCCESS || n == 0) break;
        memcpy(wp, mono + written, (size_t)n * sizeof(float));   /* mono → 1 float/frame */
        ma_pcm_rb_commit_write(&o->emrb, n);
        written += (int)n;
    }
    return written;
}

/* v20 — relay the live spatial state the callback spatializes with. Lock-free volatile writes
 * (a torn read mis-pans at most one ~10ms callback). count clamps to CMM_MAX_EMIT_PTS. */
void cmm_out_set_emitter_spatial(cmm_out* o, const float* lisPos3, const float* lisRight3,
                                 const float* pts, const float* gains, int count,
                                 float minDist, float maxDist, float falloffExp, float muffle,
                                 float stereoWidth)
{
    if (!o) return;
    if (count < 0) count = 0;
    if (count > CMM_MAX_EMIT_PTS) count = CMM_MAX_EMIT_PTS;
    if (lisPos3)   { o->emLisPos[0]   = lisPos3[0];   o->emLisPos[1]   = lisPos3[1];   o->emLisPos[2]   = lisPos3[2]; }
    if (lisRight3) { o->emLisRight[0] = lisRight3[0]; o->emLisRight[1] = lisRight3[1]; o->emLisRight[2] = lisRight3[2]; }
    o->emMinDist = minDist; o->emMaxDist = maxDist;
    o->emFalloffExp = falloffExp; o->emMuffle = muffle;
    o->emStereoWidth = stereoWidth;
    int k;
    for (k = 0; k < count * 3; k++) o->emPts[k]   = pts   ? pts[k]   : 0.0f;
    for (k = 0; k < count;     k++) o->emGains[k] = gains ? gains[k] : 1.0f;
    o->emPtCount = count;   /* publish count LAST so the callback never reads points for a larger count */
}

/* v23 — relay the live reverb (room reflection) parameters from the managed room probe. wet 0 = off. */
void cmm_out_set_emitter_reverb(cmm_out* o, float wet, float room, float damp)
{
    if (!o) return;
    if (wet  < 0.0f) wet  = 0.0f; else if (wet  > 1.0f) wet  = 1.0f;
    if (room < 0.0f) room = 0.0f; else if (room > 1.0f) room = 1.0f;
    if (damp < 0.0f) damp = 0.0f; else if (damp > 1.0f) damp = 1.0f;
    o->emRevWet = wet; o->emRevRoom = room; o->emRevDamp = damp;
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
    if (o->hasEmRing) ma_pcm_rb_uninit(&o->emrb);
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
