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
    float emPtsSmooth[CMM_MAX_EMIT_PTS * 3]; /* per-point WORLD POSITION slewed at audio rate. Directional
                                    * occlusion re-aims a speaker to its air-route bearing; that target can flip
                                    * in one 4Hz tick when the line-of-sight opens/closes around a corner. Panning
                                    * from the raw target would SNAP; slewing the position eases it. Position (not
                                    * pan) so a head-turn still pans instantly. Big jump (slot reassigned / first
                                    * sight) snaps rather than swooping the source across the world. */

    /* Distance/occlusion low-pass state — callback-thread only (no volatile/lock needed).
     * emLpf is the one-pole's running mono output; emLpfCoef is its live smoothing coefficient
     * (1 = open/bypass, small = muffled), slewed toward the per-callback target to avoid zipper
     * clicks when distance/occlusion steps. This is the spectral distance cue: far & occluded
     * sound goes DULL, not just quiet, which is what makes an emitter localize "out there". */
    float emLpf;
    float emLpfCoef;

    /* Radio coloration ("through a speaker", v26) — applied to the emitter mono BEFORE the distance
     * LPF and the reverb feed, so muffle/occlusion/pan/reflections all act on the colored signal.
     * Params are relayed live from config (lock-free volatiles); the filter/noise state below them
     * is callback-thread only. */
    volatile float emColDrive;    /* soft-clip amount 0..1 (0 = clean) */
    volatile float emColStatic;   /* static-hiss bed level 0..1 (0 = none) */
    volatile float emColVoice;    /* band-pass voicing blend 0..1 (0 = full-range, 1 = small speaker) */
    float emColLo;                /* one-pole low tracker (~250 Hz) — subtracted for the voicing HPF */
    float emColHi;                /* one-pole low-pass (~4.2 kHz) — the voicing's top cap */
    float emColLoCoef, emColHiCoef;  /* fixed cutoffs, computed from the device rate at open */
    unsigned int emColRng;        /* LCG state for the static hiss */

    /* Reverb (room reflections) for the emitter. Params relayed live from the managed room probe;
     * the reverb itself is callback-thread-only. The WET path is attenuated by REAL distance only, NOT
     * by occlusion — so behind a wall the dry sound drops away but the reflections persist, which is
     * what reads as "the sound bounced / it's in another room". */
    volatile float emRevWet;    /* master wet level 0..1 (0 = reverb off) */
    volatile float emRevRoom;   /* room size → decay/tail length 0..1 */
    volatile float emRevDamp;   /* HF damping 0..1 (dead/absorptive room = darker, shorter) */
    volatile float emRevRangeMult; /* wet audible range = emMaxDist × this (≥1: reverb reaches FARTHER than
                                    * the dry, so a brief step past the dry range in a connected cave doesn't
                                    * drop the tail; open ground is dry regardless since emRevWet≈0 there) */
    volatile float emRevPan;    /* 0..1: how much the (diffuse) reverb is PANNED toward the source's traced
                                 * bearing. 0 = fully diffuse (surrounds you); 1 = fully located. Lets the
                                 * reflections still point the way at distance, where the wet dominates. */
    float emRevPanSmooth;       /* slewed applied wet pan (callback-thread; frozen during the out-of-range tail) */
    /* DEPTH GRADING (v27): the listener's acoustic depth from the station (air-route blocks, relayed
     * from the managed flood) grades the wet — deeper ⇒ quieter, DARKER (output low-pass) and more
     * damped, the way a reverberant field decays down a real tunnel. amt 0 = flat (pre-v27). */
    volatile float emRevDepth;    /* air-route blocks to the station */
    volatile float emRevDepthAmt; /* 0..1 grading strength */
    float emRevDepthSm;           /* slewed depth (callback-thread; pushes land at 4Hz, this glides them) */
    float emRevLpL, emRevLpR;     /* one-pole LPF state on the wet L/R output (the depth darkening) */
    float emRevLpCoef;            /* slewed LPF coefficient (1 = transparent at zero depth) */
    cmm_reverb reverb;
    /* Reverb tail keepalive (callback-thread only). When the wet FEED stops (walked out of range at
     * mx), we keep ticking the reverb with ZERO input for this many more frames so (a) the reflections
     * ring OUT gracefully instead of snapping to silence, and (b) the comb/allpass buffers DRAIN to ~0.
     * Without it the buffers freeze mid-tail with stale energy that gets dumped as a click the moment
     * ticking resumes on re-entry. emRevLast holds the last in-range wet gain so the draining tail stays
     * audible (and is linearly faded to 0 across the hold window). */
    float emRevHold;   /* frames of tail keepalive remaining (0 = reverb fully idle) */
    float emRevLast;   /* last in-range wet mix gain, held to voice the decaying tail */
    float emRevMix;    /* SLEWED applied wet gain — chases the target so re-entry glides, never steps */

    /* ── LOCAL reverb bus (v28) ─────────────────────────────────────────────
     * The two-bus split: the reverb above ("CARRY") is the deliberate next-room/down-the-tunnel cue —
     * fed the UN-occluded source, seal-gated + depth-graded + route-panned by the managed side. This
     * SECOND bus is YOUR OWN SPACE ringing off the sound that actually ARRIVES: its input is the
     * post-attenuation, post-occlusion, muffled dry (y × arriving level), so walls, distance, water and
     * the hard-stop shape it exactly as they shape the dry — silence in ⇒ silence out, no seal knob.
     * Diffuse on purpose (no pan): your room surrounds you. */
    volatile float emRevLocWet;  /* local output level = ear-room wetness × master (managed; no seal/range terms) */
    float emRevLocMix;           /* slewed applied local gain (callback-thread) */
    float emRevLocHold;          /* local tail keepalive frames (rings out after the dry goes silent) */
    cmm_reverb reverbLocal;
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
/* Air-route depth (blocks) at which the depth-graded reverb reaches its full grading (per depthAmt) —
 * the wet keeps sinking gently past it (level curve caps at 2× this). ~48 ≈ "well down the tunnel";
 * a reverberant field carries much farther than the dry, hence 2× the muffle reference. */
#define CMM_EMIT_REV_DEPTH_REF 48.0f
static void cmm_emitter_gains(cmm_out* o, float* gL, float* gR, float* bright, float* wet, float* wetPan)
{
    float totalL = 0.0f, totalR = 0.0f;
    int count = o->emPtCount, p;
    if (count > CMM_MAX_EMIT_PTS) count = CMM_MAX_EMIT_PTS;
    float lx = o->emLisPos[0], ly = o->emLisPos[1], lz = o->emLisPos[2];
    float rx = o->emLisRight[0], ry = o->emLisRight[1], rz = o->emLisRight[2];
    float mn = o->emMinDist, mx = o->emMaxDist, span = mx - mn;
    if (span < 0.0001f) span = 0.0001f;
    float fexp = o->emFalloffExp, muffle = o->emMuffle;
    float bestContrib = -1.0f, bestBright = 1.0f, bestWet = 0.0f, bestWetAtt = 0.0f, bestWetPan = 0.0f;
    for (p = 0; p < count; p++) {
        /* Slew this speaker's WORLD position toward the relayed target, then pan/attenuate from the SLEWED
         * position — so a redirected bearing (directional occlusion routing the sound around a corner) EASES
         * over ~200ms instead of snapping when the air route flips. A big jump (>32 blocks: this slot now
         * holds a different speaker, or first sight) snaps instead of swooping the source across the world. */
        float tpx = o->emPts[p*3], tpy = o->emPts[p*3+1], tpz = o->emPts[p*3+2];
        float spx = o->emPtsSmooth[p*3], spy = o->emPtsSmooth[p*3+1], spz = o->emPtsSmooth[p*3+2];
        float jx = tpx - spx, jy = tpy - spy, jz = tpz - spz;
        if (jx*jx + jy*jy + jz*jz > 1024.0f) { spx = tpx; spy = tpy; spz = tpz; }   /* >32 blocks → snap */
        else { const float k = 0.06f; spx += jx*k; spy += jy*k; spz += jz*k; }       /* ~200ms ease */
        o->emPtsSmooth[p*3] = spx; o->emPtsSmooth[p*3+1] = spy; o->emPtsSmooth[p*3+2] = spz;

        float dx = spx - lx;
        float dy = spy - ly;
        float dz = spz - lz;
        float d  = sqrtf(dx*dx + dy*dy + dz*dz);

        /* OCCLUSION AS VIRTUAL DISTANCE: emGains[p] now carries not a gain but the extra blocks of
         * "distance" the walls / the go-around add (0 = clear line). Folding it into the distance means
         * a wall makes the radio sound FARTHER (quieter AND duller on the same smooth curve) instead of
         * applying a separate multiplicative cliff — which is what made it feel all-or-nothing. Slewed
         * at audio rate (exponential) so the change glides as you move, never pops. */
        float target = o->emGains[p], cur = o->emGainsSmooth[p];
        /* ADAPTIVE glide: rate scales with the SIZE of the pending change. A small nudge (walking near an
         * occluder) glides slow & smooth at the tuned 0.03 — no movement stepping. A LARGE jump — a swing
         * door snapping open/shut, a block breaking, crossing a threshold — glides fast so the sound tracks
         * the event instead of mushing in over ~1 s ("real sound doesn't fade in slowly"). The gap shrinks
         * as cur catches up, so it eases out naturally (no click). A slow garage door ramps its occlusion in
         * small per-tick steps ⇒ it stays in the slow regime ⇒ still gradual (unchanged). */
        float gap = target - cur; if (gap < 0.0f) gap = -gap;
        float gg = (gap - 4.0f) * (1.0f / 12.0f);   /* 4 blocks → 16 blocks maps to 0..1 */
        if (gg < 0.0f) gg = 0.0f; else if (gg > 1.0f) gg = 1.0f;
        gg = gg * gg * (3.0f - 2.0f * gg);           /* smoothstep */
        float glide = 0.03f + 0.20f * gg;            /* 0.03 (smooth) → 0.23 (snappy) */
        cur += (target - cur) * glide;
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
        }

        /* Wet (reverb) level, tracked as the MAX over speakers = the NEAREST reverberant one. Kept SEPARATE
         * from the dry `contrib` pick on purpose: past the dry range the dry att is 0, so a contrib-gated wet
         * would degenerate to "first point" — the max here always follows the closest speaker instead.
         * Scaled by REAL distance (d, NOT dEff) so occlusion never attenuates the reflections (behind a wall
         * the dry drops but the wet carries — the "it bounced" cue). Its range is emMaxDist × emRevRangeMult,
         * so the reverb reaches FARTHER than the dry: a brief step past the dry range in a connected cave keeps
         * the tail alive. Open ground stays dry regardless — emRevWet≈0 with no enclosure, so a bigger reach
         * multiplies nothing. v29: power decay over the WHOLE reach at HALF the dry falloff exponent (the
         * reverberant field decays slower than the direct, but it DECAYS — the old flat-until-85% curve left
         * a constant-loudness wash for ~10 blocks after the dry died: "music gone, ghost hum follows you"). */
        {
            float rmx = mx * o->emRevRangeMult;
            float attWet;
            if (d >= rmx || rmx <= 0.0f) attWet = 0.0f;
            else attWet = powf((rmx - d) / rmx, fexp * 0.5f);
            /* Track the NEAREST reverberant speaker's bearing too, so the mix can PAN the (otherwise diffuse)
             * reverb toward it — the traced air-route direction, so at distance the reflections still point
             * the way to the source (down the tunnel) instead of washing out non-directionally. */
            if (attWet > bestWetAtt) { bestWetAtt = attWet; bestWetPan = px; }
        }
    }
    bestWet = o->emRevWet * bestWetAtt;
    float mag = sqrtf(totalL*totalL + totalR*totalR);
    if (mag > 1.4f) { float s = 1.4f / mag; totalL *= s; totalR *= s; }
    if (bestBright < 0.0f) bestBright = 0.0f; else if (bestBright > 1.0f) bestBright = 1.0f;
    *gL = totalL; *gR = totalR; *bright = bestBright; *wet = bestWet; *wetPan = bestWetPan;
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
        float gL, gR, bright, wet, wetPan;
        cmm_emitter_gains(o, &gL, &gR, &bright, &wet, &wetPan);
        int audible = (o->emPtCount > 0) && (gL != 0.0f || gR != 0.0f);

        /* DEPTH GRADING (v27): sink the wet with the listener's acoustic depth from the station (air-route
         * blocks from the managed flood — walks around bends, ignores walls). LEVEL decays exponentially
         * (much slower than the dry — a reverberant field carries far), normalized to a fixed reference
         * like the muffle so it's Range-independent; the range-edge taper in cmm_emitter_gains still
         * bounds the reach. Depth is slewed (~0.5s) so 4Hz pushes glide. amt 0 ⇒ everything here is a
         * no-op and the wet is exactly the pre-v27 flat behaviour. */
        o->emRevDepthSm += (o->emRevDepth - o->emRevDepthSm) * 0.02f;
        float ndep = (o->emRevDepthSm - o->emMinDist) * (1.0f / CMM_EMIT_REV_DEPTH_REF);
        if (ndep < 0.0f) ndep = 0.0f; else if (ndep > 2.0f) ndep = 2.0f;
        float damt = o->emRevDepthAmt;
        if (damt > 0.0f) wet *= expf(-1.2f * damt * ndep);
        float ndep1 = ndep > 1.0f ? 1.0f : ndep;
        /* BRIGHTNESS: coefficient for a one-pole LPF on the wet OUTPUT — near = bright splash (coef 1 =
         * transparent), deep = dark rumble. Slewed per callback (±0.05) like the dry muffle so it never
         * zipper-clicks. Runs on the tail too (out of range the darkness holds — correct: the field you
         * left doesn't re-brighten). */
        float rb = 1.0f - damt * ndep1;
        float rcoefT = 0.05f + 0.95f * rb * rb;
        float rcoef = o->emRevLpCoef;
        float rdc = rcoefT - rcoef;
        if (rdc > 0.05f) rdc = 0.05f; else if (rdc < -0.05f) rdc = -0.05f;
        rcoef += rdc;
        o->emRevLpCoef = rcoef;

        int wetFeed = wet > 1e-4f;   /* in range → still feeding the CARRY reverb new energy */

        /* LOCAL bus (v28): total ARRIVING level = magnitude of the summed spatial gains (equal-power pan
         * preserves per-speaker energy, so this ≈ the distance+occlusion attenuation actually applied).
         * The local reverb's input is y (the muffled arriving mono) × this — walls/water/hard-stop shape
         * it exactly as they shape the dry. */
        float aLvl = sqrtf(gL * gL + gR * gR); if (aLvl > 1.0f) aLvl = 1.0f;
        float locWet = o->emRevLocWet;
        /* Tail keepalive: while feeding, recharge the hold timer and remember the live wet gain.
         * Once the feed stops (out of range), count the timer down and keep the reverb running so
         * the tail rings out and the buffers drain. ~2s covers the longest room decay. */
        float revTailFrames = (float)o->sampleRate * 2.0f;
        if (wetFeed) { o->emRevHold = revTailFrames; o->emRevLast = wet; }
        else if (o->emRevHold > 0.0f) {
            o->emRevHold -= (float)frameCount;
            if (o->emRevHold < 0.0f) o->emRevHold = 0.0f;
        }
        int wetOn = wetFeed || o->emRevHold > 0.0f;

        /* Local keepalive mirrors the carry's: once the dry goes silent, tick with zero input so the
         * local tail rings out and the buffers drain (no click on the next arrival). */
        int locFeed = audible && locWet > 1e-4f;
        if (locFeed) o->emRevLocHold = revTailFrames;
        else if (o->emRevLocHold > 0.0f) {
            o->emRevLocHold -= (float)frameCount;
            if (o->emRevLocHold < 0.0f) o->emRevLocHold = 0.0f;
        }
        int locOn = locFeed || o->emRevLocHold > 0.0f;
        float locTarget = locFeed ? locWet : 0.0f;   /* slewed below; the tail also decays on its own */
        /* Held tail gain: last in-range wet, linearly faded to 0 across the keepalive window so the
         * decaying reflections glide to silence (on top of the reverb's own decay) instead of snapping. */
        float tailGain = (revTailFrames > 0.0f) ? o->emRevLast * (o->emRevHold / revTailFrames) : 0.0f;
        /* The applied wet gain is SLEWED toward this target (live wet in range, decaying tail out of
         * range) so crossing the boundary EITHER way glides. Without the slew, re-entry snapped the gain
         * from the decayed tail straight to full wet in one callback — an audible click/swell. */
        float mixTarget = wetFeed ? wet : tailGain;
        float mixCoef = 1.0f / (0.025f * (float)o->sampleRate);   /* ~25ms one-pole slew */
        if (mixCoef > 1.0f) mixCoef = 1.0f;

        /* DIRECTIONAL REVERB: bias the diffuse reflections toward the source's traced bearing so at distance
         * (where the wet dominates the faded dry) the reverb still points the way — in a cave, DOWN THE TUNNEL
         * toward the radio. wetPan is the nearest reverberant speaker's (redirected, position-slewed) pan.
         * Freeze the pan during the out-of-range tail (hold last bearing), and slew it (~25ms) so it never
         * steps. emRevPan scales it: 0 = fully diffuse (surrounds you), 1 = fully located. The per-ear weights
         * average to 1 (energy-preserving): source-left ⇒ boost L, cut R. */
        float panTarget = wetFeed ? wetPan : o->emRevPanSmooth;   /* hold last bearing when out of range */
        o->emRevPanSmooth += (panTarget - o->emRevPanSmooth) * mixCoef;
        float wp = o->emRevPanSmooth; if (wp < -1.0f) wp = -1.0f; else if (wp > 1.0f) wp = 1.0f;
        float wgL = 1.0f - o->emRevPan * wp;   /* source-left (wp<0) ⇒ >1 on L */
        float wgR = 1.0f + o->emRevPan * wp;

        /* Refresh reverb decay/damping from the live room probe (cheap coefficient update). Depth biases
         * the CARRY's damping up (each extra bounce down the tunnel sheds highs), on top of the output
         * LPF; the LOCAL bus is your own room — its input is already muffled, so no depth bias. */
        if (wetOn) {
            float dampEff = o->emRevDamp + 0.35f * damt * ndep1;
            if (dampEff > 1.0f) dampEff = 1.0f;
            cmm_reverb_set(&o->reverb, o->emRevRoom, dampEff);
        }
        if (locOn) cmm_reverb_set(&o->reverbLocal, o->emRevRoom, o->emRevDamp);

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

        /* Radio coloration per-block constants. The soft-clip is level-compensated at a 0.25
         * reference (typical music peak after loudness normalization): quiet material passes
         * near-unity, peaks get crushed — grit without a volume jump. Params can change between
         * callbacks (live tuning); a one-block step is inaudible. */
        float colDrive = o->emColDrive, colStatic = o->emColStatic, colVoice = o->emColVoice;
        int colOn = (colDrive > 0.001f) || (colStatic > 0.001f) || (colVoice > 0.001f);
        float colPre = 1.0f + colDrive * 5.0f;
        float colMk = 0.25f / tanhf(colPre * 0.25f);
        float colNoiseAmp = colStatic * 0.08f;

        ma_uint32 got = 0;
        while (got < frameCount) {
            ma_uint32 n = frameCount - got;
            void* rp = NULL;
            if (ma_pcm_rb_acquire_read(&o->emrb, &n, &rp) != MA_SUCCESS || n == 0) break;
            /* Enter if EITHER the dry is audible OR there's reverb to render. The wet is NOT occluded (it
             * carries around corners / through walls — the "it bounced / next room" cue), so it MUST render
             * even when the dry is fully occluded to silence. Gating the whole block on `audible` (the
             * occluded dry gain) silently skipped the reverb in any occluded space (e.g. a tight corridor
             * where the radio is around a bend) — the bug that made reverb inaudible until FloodRadius was
             * cranked up to un-occlude the dry. */
            if (audible || wetOn || locOn) {
                const float* m = (const float*)rp;
                float* op = (float*)output + (size_t)got * o->channels;
                float y = o->emLpf;
                float cLo = o->emColLo, cHi = o->emColHi;
                unsigned int cRng = o->emColRng;
                ma_uint32 f;
                for (f = 0; f < n; f++) {
                    /* Radio coloration — runs BEFORE the distance LPF and the reverb feed, so the
                     * muffle, pan, occlusion and reflections all act on the colored ("through a
                     * speaker") signal. All params 0 ⇒ x is the raw mono, exact bypass. */
                    float x = m[f];
                    if (colOn) {
                        /* Static bed first, so the hiss shares the voicing + drive (it comes out of
                         * the same speaker — and downstream it's distance-faded/panned like the music). */
                        if (colNoiseAmp > 0.0f) {
                            cRng = cRng * 1664525u + 1013904223u;
                            x += (float)(int)cRng * (1.0f / 2147483648.0f) * colNoiseAmp;
                        }
                        /* Band-pass voicing: strip lows (~250 Hz) and highs (~4.2 kHz) — the "small
                         * speaker" body — blended in by `voice` so 0 keeps the full-range signal. */
                        if (colVoice > 0.0f) {
                            cLo += o->emColLoCoef * (x - cLo);
                            float bp = x - cLo;                 /* highs + mids (lows removed) */
                            cHi += o->emColHiCoef * (bp - cHi); /* cap the top */
                            x += (cHi - x) * colVoice;
                        }
                        /* Soft-clip drive (Padé tanh, exact ±1 at the ±3 clamp): crushes peaks into
                         * speaker breakup; colMk keeps the loudness at the reference level flat. */
                        if (colDrive > 0.001f) {
                            float t = x * colPre;
                            if (t > 3.0f) t = 3.0f; else if (t < -3.0f) t = -3.0f;
                            float t2 = t * t;
                            x = t * (27.0f + t2) / (27.0f + 9.0f * t2) * colMk;
                        }
                    }
                    if (audible) {
                        y += coef * (x - y);               /* one-pole distance/occlusion low-pass (mono) */
                        op[f * o->channels]     += y * gL;
                        op[f * o->channels + 1] += y * gR;
                    }
                    if (wetOn) {
                        /* Reverb feeds off the COLORED mono (pre-occlusion-LPF) and mixes in at the wet
                         * gain, which is NOT occluded — so reflections carry around/through walls.
                         * Past range (feed stopped) we tick with ZERO input so the tail decays out and
                         * the buffers drain, voicing it at the fading held gain rather than the live wet. */
                        float rin = wetFeed ? x : 0.0f;
                        o->emRevMix += (mixTarget - o->emRevMix) * mixCoef;   /* glide, never step */
                        float wl, wr;
                        cmm_reverb_tick(&o->reverb, rin, &wl, &wr);
                        /* Depth darkening: one-pole on the wet output (coef 1 at zero depth = transparent). */
                        o->emRevLpL += rcoef * (wl - o->emRevLpL); wl = o->emRevLpL;
                        o->emRevLpR += rcoef * (wr - o->emRevLpR); wr = o->emRevLpR;
                        op[f * o->channels]     += wl * o->emRevMix * wgL;
                        op[f * o->channels + 1] += wr * o->emRevMix * wgR;
                    }
                    if (locOn) {
                        /* LOCAL bus: your space ringing off what ARRIVES. Input = the muffled dry (y)
                         * × the applied spatial attenuation — so occlusion/distance/water/hard-stop
                         * govern it exactly as the dry (fully occluded ⇒ nothing to ring). Un-panned
                         * (diffuse: your room surrounds you); no depth LPF (the input is already dull). */
                        float lin = audible ? y * aLvl : 0.0f;
                        o->emRevLocMix += (locTarget - o->emRevLocMix) * mixCoef;
                        float ll, lr;
                        cmm_reverb_tick(&o->reverbLocal, lin, &ll, &lr);
                        op[f * o->channels]     += ll * o->emRevLocMix;
                        op[f * o->channels + 1] += lr * o->emRevLocMix;
                    }
                }
                if (audible) o->emLpf = y;
                o->emColLo = cLo; o->emColHi = cHi; o->emColRng = cRng;
            }
            ma_pcm_rb_commit_read(&o->emrb, n);   /* commit even when inaudible → discard residual */
            got += n;
        }
        o->emLpfCoef = coef;
        /* Clip-guard the summed output (music + emitter can exceed unity). */
        if (audible || wetOn || locOn) {
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
    o->emRevRangeMult = 1.0f;   /* reverb range == dry range until config relays */
    o->emRevPan = 0.0f; o->emRevPanSmooth = 0.0f;   /* diffuse reverb until config relays */
    o->emRevHold = 0.0f; o->emRevLast = 0.0f; o->emRevMix = 0.0f;   /* tail keepalive idle */
    o->emRevDepth = 0.0f; o->emRevDepthAmt = 0.0f; o->emRevDepthSm = 0.0f;   /* depth grading off until relayed */
    o->emRevLpL = 0.0f; o->emRevLpR = 0.0f; o->emRevLpCoef = 1.0f;           /* wet LPF starts transparent */
    /* Coloration off (clean bypass) until config relays; fixed voicing cutoffs from the device rate. */
    o->emColDrive = 0.0f; o->emColStatic = 0.0f; o->emColVoice = 0.0f;
    o->emColLoCoef = 1.0f - expf(-6.2831853f * 250.0f  / (float)sampleRate);
    o->emColHiCoef = 1.0f - expf(-6.2831853f * 4200.0f / (float)sampleRate);
    o->emColRng = 0x2545F491u;
    o->emRevLocWet = 0.0f; o->emRevLocMix = 0.0f; o->emRevLocHold = 0.0f;    /* local bus idle until relayed */
    cmm_reverb_init(&o->reverb, sampleRate);
    cmm_reverb_init(&o->reverbLocal, sampleRate);
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

/* v23 — relay the live reverb (room reflection) parameters from the managed room probe. wet 0 = off.
 * rangeMult (≥1) extends the wet's audible distance past the dry range so the tail survives a brief step
 * out of range in a connected cave. v27 adds depth (air-route blocks) + depthAmt (grading strength).
 * v28 splits the buses: `wet` is the CARRY level (seal-gated, depth-graded, route-panned next-room cue);
 * `localWet` is the LOCAL level (your room ringing off the arriving occluded signal; no seal/range). */
void cmm_out_set_emitter_reverb(cmm_out* o, float wet, float room, float damp, float rangeMult, float pan,
                                float depth, float depthAmt, float localWet)
{
    if (!o) return;
    if (wet  < 0.0f) wet  = 0.0f; else if (wet  > 1.0f) wet  = 1.0f;
    if (room < 0.0f) room = 0.0f; else if (room > 1.0f) room = 1.0f;
    if (damp < 0.0f) damp = 0.0f; else if (damp > 1.0f) damp = 1.0f;
    if (rangeMult < 1.0f) rangeMult = 1.0f; else if (rangeMult > 8.0f) rangeMult = 8.0f;
    if (pan  < 0.0f) pan  = 0.0f; else if (pan  > 1.0f) pan  = 1.0f;
    if (depth < 0.0f) depth = 0.0f; else if (depth > 512.0f) depth = 512.0f;
    if (depthAmt < 0.0f) depthAmt = 0.0f; else if (depthAmt > 1.0f) depthAmt = 1.0f;
    if (localWet < 0.0f) localWet = 0.0f; else if (localWet > 1.0f) localWet = 1.0f;
    o->emRevWet = wet; o->emRevRoom = room; o->emRevDamp = damp; o->emRevRangeMult = rangeMult; o->emRevPan = pan;
    o->emRevDepth = depth; o->emRevDepthAmt = depthAmt;
    o->emRevLocWet = localWet;
}

/* v26 — relay the live radio-coloration params from config. All 0..1; all 0 = clean bypass. Lock-free
 * volatile writes like the rest of the emitter state (a torn read colors one ~10ms callback oddly). */
void cmm_out_set_emitter_color(cmm_out* o, float drive, float staticLvl, float voice)
{
    if (!o) return;
    if (drive     < 0.0f) drive     = 0.0f; else if (drive     > 1.0f) drive     = 1.0f;
    if (staticLvl < 0.0f) staticLvl = 0.0f; else if (staticLvl > 1.0f) staticLvl = 1.0f;
    if (voice     < 0.0f) voice     = 0.0f; else if (voice     > 1.0f) voice     = 1.0f;
    o->emColDrive = drive; o->emColStatic = staticLvl; o->emColVoice = voice;
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
