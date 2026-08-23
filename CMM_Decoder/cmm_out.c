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

    /* ── Consumer-side emitter layer (v20; v31 = per-station SLOTS) ─────────
     * Each voiced STATION renders on its own slot: its own dry-mono ring, point set, position/
     * occlusion slews, distance LPF and coloration filter state — so up to CMM_MAX_EMIT_VOICES
     * stations play simultaneously, each spatialized here in the callback from the freshest relayed
     * state (pan/attenuation at output time is what makes head-turn panning feel real-time). The
     * listener basis, curve tuning, coloration params and the reverb ROOM shape stay GLOBAL — they
     * are properties of the listener, not of a station. State is written by the producer and read
     * by the callback; a torn read at worst mis-pans ONE callback (~10ms) — inaudible, so no lock. */
    struct cmm_emit_slot {
        ma_pcm_rb rb;               /* mono ring for this slot's dry signal */
        int       hasRing;          /* rb was initialised (uninit on close) */
        /* STARVATION, COUNTED. A short read here used to be zero-filled in silence, with no
         * counter anywhere: cmm_mix's g_underruns watches the MUSIC ring only, so every stat the
         * game could print said "underruns 0, queued at target" while the radios were dropping
         * out. Hours went into the occlusion model on the strength of a symptom this would have
         * identified in one line. Anything that can silently produce silence gets a counter. */
        volatile unsigned underFrames;   /* frames zero-filled because the ring was empty */
        volatile unsigned primes;        /* times this slot was re-cushioned from empty */
        volatile int   ptCount;     /* speakers audible now (0 = this slot silent this block) */
        volatile float pts[CMM_MAX_EMIT_PTS * 3];
        volatile float gains[CMM_MAX_EMIT_PTS];   /* per-speaker occlusion VIRTUAL DISTANCE (extra blocks; 0 = clear) */
        volatile int   ptFlags[CMM_MAX_EMIT_PTS]; /* v34 per-point render flags: bit0 = HAM CLASS (small
                                     * communications speaker — gets the ham coloration set instead of the
                                     * speaker set). Bits 1-2 reserved for the stereo-pair channel split
                                     * (0 = mono, 1 = left, 2 = right — not rendered yet). */
        float ptsSmooth[CMM_MAX_EMIT_PTS * 3]; /* per-point WORLD POSITION slewed at audio rate. Directional
                                     * occlusion re-aims a speaker to its air-route bearing; that target can flip
                                     * in one 4Hz tick when the line-of-sight opens/closes around a corner. Panning
                                     * from the raw target would SNAP; slewing the position eases it. Position (not
                                     * pan) so a head-turn still pans instantly. Big jump (point slot reassigned /
                                     * first sight) snaps rather than swooping the source across the world. */
        float gainsSmooth[CMM_MAX_EMIT_PTS];  /* per-point occlusion VIRTUAL DISTANCE slewed at audio rate (anti-pop) */
        int   prevPtCount;          /* point slots active last callback: one activating fresh INITIALIZES its
                                     * smooth state from the target instead of gliding from whatever stale
                                     * value the previous voice/point-set left behind (the "burst of audio
                                     * at the wrong level when a radio starts / re-enters range" bug). */
        /* Distance/occlusion low-pass state — callback-thread only, PER CLASS (v34: index 0 =
         * speaker points, 1 = ham points; a station can voice through both at once and each class
         * is an independently colored signal). lpf is the one-pole's running mono output; lpfCoef
         * its live coefficient (1 = open/bypass, small = muffled), slewed to avoid zipper clicks.
         * The spectral distance cue: far & occluded goes DULL, not just quiet. */
        float lpf[2];
        float lpfCoef[2];
        /* Radio coloration filter state (per slot per CLASS — each station is an independent
         * stream, and its ham points are colored separately from its speaker points). The
         * coloration PARAMS are global (emCol* for speakers, emColHam* for hams); cutoffs too. */
        float colLo[2];             /* one-pole low tracker (~250 Hz) — subtracted for the voicing HPF */
        float colHi[2];             /* one-pole low-pass (~4.2 kHz) — the voicing's top cap */
        unsigned int colRng;        /* LCG state for the static hiss (seeded per slot; shared by both
                                     * classes — they draw different samples, so no correlation) */
        /* Per-slot CARRY reverb send (v31): the managed side's seal-gated next-room level for THIS
         * station, and its air-route depth. The send is applied INPUT-side into the one shared carry
         * reverb (input scaling ≡ the old output wet gain for one station — the reverb is linear —
         * and it lets differently-sealed stations share the bus correctly). */
        volatile float revWet;      /* this station's carry-send level (0 = none) */
        volatile float revDepth;    /* air-route blocks to this station */
        float revSendSm;            /* slewed applied carry send (callback-thread; ~25ms glide) */
        float revDepthSm;           /* slewed depth (pushes land at 4Hz; this glides them) */
        volatile int flushReq;      /* v32: producer-raised when a DIFFERENT station acquires this
                                     * slot — the callback discards the queued dry ring and resets
                                     * the per-slot render state before its next drain, so the old
                                     * station's buffered audio never plays under the new one. */
        /* Per-chunk dry scratch for the interleaved sample loop (filled from rb each chunk). */
        float buf[512];
    } emSlots[CMM_MAX_EMIT_VOICES];
    volatile float emLisPos[3];
    volatile float emLisRight[3];
    volatile float emMinDist, emMaxDist;
    volatile float emFalloffExp;   /* plateau→range fade steepness (live tuning) */
    volatile float emMuffle;       /* distance low-pass strength 0..1 (live tuning) */
    volatile float emStereoWidth;  /* pan width 0..1: 1 = full hard pan, <1 keeps the far ear alive */
    volatile int emAcousticReset; /* producer-raised: clear ALL emitter render state (reverb tails, LPF,
                                 * pan/mix slews, slot smooth state) before the next block. Serviced on
                                 * the callback thread, which owns that state. Set when a NEW station
                                 * voice starts fresh (with no other station live) so a destroyed radio's
                                 * leftover reverb energy is never re-voiced under the replacement. */

    /* Radio coloration params ("through a speaker", v26) — applied to each slot's mono BEFORE the
     * distance LPF and the reverb feed, so muffle/occlusion/pan/reflections all act on the colored
     * signal. Relayed live from config (lock-free volatiles); per-slot filter STATE lives on the slot. */
    volatile float emColDrive;    /* soft-clip amount 0..1 (0 = clean) */
    volatile float emColStatic;   /* static-hiss bed level 0..1 (0 = none) */
    volatile float emColVoice;    /* band-pass voicing blend 0..1 (0 = full-range, 1 = small speaker) */
    /* v34 — the HAM class's own coloration set (a ham radio is a small communications speaker:
     * voiced narrower + driven harder than the shelf speakers, always slightly distorted). Same
     * semantics as the three above; per-point flags decide which set a point's signal gets. */
    volatile float emColHamDrive;
    volatile float emColHamStatic;
    volatile float emColHamVoice;
    float emColLoCoef, emColHiCoef;  /* fixed cutoffs, computed from the device rate at open */
    /* v42: drive the emitter's TONE from occlusion rather than from virtual distance. Off by
     * default, so the shipped occlusion model's carefully tuned door behaviour is bit-identical;
     * the native acoustic model turns it on, because it publishes an occlusion whose worth in
     * BLOCKS varies with distance even when the occlusion itself does not. */
    int   emOcclTone;
    volatile int emPrime;       /* cushion depth (frames) the slot rings should hold; see
                                 * cmm_out_write_emitter for why they had none of their own */
    float emOcclFullDb;              /* occlusion (dB) at which the tone is fully dulled */

    /* Reverb (room reflections) — ONE shared pair of buses for every slot (reverb is a property of
     * the listener's ROOM, not the station). Room-shape params relayed live from the managed probe;
     * the reverbs themselves are callback-thread-only. The CARRY wet is attenuated by REAL distance
     * only, NOT by occlusion — so behind a wall the dry drops but the reflections persist, which is
     * what reads as "the sound bounced / it's in another room". Per-station send levels live on the
     * slots (revWet/revDepth); these shape the shared room. */
    volatile float emRevRoom;   /* room size → decay/tail length 0..1 */
    volatile float emRevDamp;   /* HF damping 0..1 (dead/absorptive room = darker, shorter) */
    volatile float emRevRangeMult; /* wet audible range = emMaxDist × this (≥1: reverb reaches FARTHER than
                                    * the dry, so a brief step past the dry range in a connected cave doesn't
                                    * drop the tail; open ground is dry regardless since the sends ≈0 there) */
    volatile float emRevPan;    /* 0..1: how much the (diffuse) reverb is PANNED toward the source's traced
                                 * bearing. 0 = fully diffuse (surrounds you); 1 = fully located. Lets the
                                 * reflections still point the way at distance, where the wet dominates. */
    float emRevPanSmooth;       /* slewed applied wet pan (callback-thread; frozen during the out-of-range tail) */
    /* DEPTH GRADING (v27): the listener's acoustic depth from the DOMINANT station (air-route blocks)
     * grades the wet output — deeper ⇒ DARKER (output low-pass) and more damped, the way a reverberant
     * field decays down a real tunnel. Per-slot LEVEL grading is folded into each slot's send. */
    volatile float emRevDepthAmt; /* 0..1 grading strength */
    float emRevLpL, emRevLpR;     /* one-pole LPF state on the wet L/R output (the depth darkening) */
    float emRevLpCoef;            /* slewed LPF coefficient (1 = transparent at zero depth) */
    cmm_reverb reverb;
    /* Reverb tail keepalive (callback-thread only). When every slot's carry FEED stops (walked out of
     * range at mx), we keep ticking the reverb with (near-zero) input for this many more frames so
     * (a) the reflections ring OUT gracefully at their natural decay, and (b) the comb/allpass buffers
     * DRAIN to ~0. Without it the buffers freeze mid-tail with stale energy dumped as a click when
     * ticking resumes on re-entry. (v31: input-side sends make the tail level correct by construction —
     * the old held-gain fade is gone.) */
    float emRevHold;   /* frames of tail keepalive remaining (0 = reverb fully idle) */

    /* ── LOCAL reverb bus (v28) ─────────────────────────────────────────────
     * The two-bus split: the reverb above ("CARRY") is the deliberate next-room/down-the-tunnel cue —
     * fed the UN-occluded sources, seal-gated + depth-graded + route-panned. This SECOND bus is YOUR
     * OWN SPACE ringing off the sound that actually ARRIVES: its input is the sum of every slot's
     * post-attenuation, post-occlusion, muffled dry (y × arriving level), so walls, distance, water and
     * the hard-stop shape it exactly as they shape the dry — silence in ⇒ silence out, no seal knob.
     * Diffuse on purpose (no pan): your room surrounds you. */
    volatile float emRevLocWet;  /* local output level = ear-room wetness × master (managed; no seal/range terms) */
    float emRevLocMix;           /* slewed applied local gain (callback-thread) */
    float emRevLocHold;          /* local tail keepalive frames (rings out after the dry goes silent) */
    cmm_reverb reverbLocal;
};

/* Compute the summed L/R spatial gain for ONE emitter slot from the live listener + speaker state,
 * plus a spectral BRIGHTNESS scalar [0..1] for the distance/occlusion low-pass. Mirrors what the
 * mixer used to do per-block, now at output time. Mono-in, so the caller applies gL/gR to each mono
 * sample. Equal-power pan by bearing to `right`, INVERSE-DISTANCE attenuation (physical proximity
 * feel, unlike the old flat-near ease-out), per-speaker occlusion gain, summed over all speakers and
 * magnitude-capped (anti-clip). Brightness comes from the DOMINANT (loudest) speaker so the tone
 * matches the sound you're mostly hearing: 1 = near & clear (open), →0 = far or wall-occluded (dull).
 * `wet` returns this slot's wet-range ATTENUATION (0..1, real distance — the caller multiplies the
 * station's carry send in); `wetPan` its nearest reverberant bearing. */
/* Effective distance (blocks) at which the tone is fully muffled (per the Muffle strength), independent
 * of the volume Range. ~24 ≈ "clearly across a room / behind a wall". */
#define CMM_EMIT_MUFFLE_REF 24.0f
/* Air-route depth (blocks) at which the depth-graded reverb reaches its full grading (per depthAmt) —
 * the wet keeps sinking gently past it (level curve caps at 2× this). ~48 ≈ "well down the tunnel";
 * a reverberant field carries much farther than the dry, hence 2× the muffle reference. */
#define CMM_EMIT_REV_DEPTH_REF 48.0f
/* Frames per sample-loop chunk (matches the slot scratch buf size). */
#define CMM_EMIT_CHUNK 512
/* v34: gains/brightness are accumulated PER CLASS (index 0 = speaker points, 1 = ham points —
 * ptFlags bit0) since each class renders an independently colored signal. `domClass` returns the
 * class of the single loudest point (the carry reverb feeds that class's signal — "the sound
 * you're mostly hearing"); wet level/pan stay slot-level (the room doesn't care which class). */
static void cmm_emitter_gains(cmm_out* o, struct cmm_emit_slot* sl, float gL[2], float gR[2], float bright[2], float* wet, float* wetPan, int* domClass)
{
    /* ENERGY, not amplitude. Two speakers playing the SAME track were summed as amplitudes:
     *
     *     totalL += cos(theta)*att;   totalR += sin(theta)*att;
     *
     * A single point then has magnitude exactly att, but TWO points have |v1+v2| = 2cos(dTheta/2),
     * and because equal-power pan confines theta to [0, pi/2], that is between 1.41 and 2.00 --
     * ALWAYS above the 1.4 anti-clip cap below. So with two speakers audible the cap was
     * permanently engaged, and its scale factor depended on the angle between them IN PAN SPACE.
     * Pan position is a PROJECTION of the bearing onto the listener's right vector, so that angle
     * changes as the listener turns even when nothing moves: up to 3 dB of gain riding on which
     * way you face, applied hard, per render block, with no slew. Reported exactly as "just
     * turning around and not moving causes dips too", with two speakers nearby.
     *
     * Summing energy instead makes the total invariant under rotation by construction:
     *
     *     mag^2 = sum(cL_i^2) + sum(cR_i^2) = sum(att_i^2)     -- no theta in it at all
     *
     * It is also the better physical model. These points carry the same signal, so in a real room
     * they comb-filter, and averaged across frequency that is power addition (+3 dB for two equal
     * speakers) rather than the +6 dB of coherent amplitude addition. A single point is unchanged:
     * sqrt of one square is what it always was. */
    float energyL[2] = { 0.0f, 0.0f }, energyR[2] = { 0.0f, 0.0f };
    float totalL[2] = { 0.0f, 0.0f }, totalR[2] = { 0.0f, 0.0f };
    int count = sl->ptCount, p;
    if (count > CMM_MAX_EMIT_PTS) count = CMM_MAX_EMIT_PTS;
    float lx = o->emLisPos[0], ly = o->emLisPos[1], lz = o->emLisPos[2];
    float rx = o->emLisRight[0], ry = o->emLisRight[1], rz = o->emLisRight[2];
    float mn = o->emMinDist, mx = o->emMaxDist, span = mx - mn;
    if (span < 0.0001f) span = 0.0001f;
    float fexp = o->emFalloffExp, muffle = o->emMuffle;
    float bestContrib[2] = { -1.0f, -1.0f }, bestBright[2] = { 1.0f, 1.0f };
    float bestContribAll = -1.0f, bestWetAtt = 0.0f, bestWetPan = 0.0f;
    int   dom = 0;
    for (p = 0; p < count; p++) {
        /* Slew this speaker's WORLD position toward the relayed target, then pan/attenuate from the SLEWED
         * position — so a redirected bearing (directional occlusion routing the sound around a corner) EASES
         * over ~200ms instead of snapping when the air route flips. A big jump (>32 blocks: this slot now
         * holds a different speaker, or first sight) snaps instead of swooping the source across the world. */
        float tpx = sl->pts[p*3], tpy = sl->pts[p*3+1], tpz = sl->pts[p*3+2];
        float spx = sl->ptsSmooth[p*3], spy = sl->ptsSmooth[p*3+1], spz = sl->ptsSmooth[p*3+2];
        float jx = tpx - spx, jy = tpy - spy, jz = tpz - spz;
        /* A slot that was INACTIVE last callback carries stale smooth state from whatever point
         * (possibly a different speaker, a different voice entirely) last occupied it. Rendering
         * from that state played a burst at the WRONG level/position while the glide caught up.
         * Initialize a freshly-activated slot from its target instead — position AND gain. The
         * >32-block jump keeps its position snap; it now snaps the occlusion gain too (same
         * "this is a different speaker" signal — gliding the gain from another speaker's
         * occlusion was just a quieter version of the same bug). */
        int freshSlot = (p >= sl->prevPtCount);
        if (freshSlot || jx*jx + jy*jy + jz*jz > 1024.0f) {
            spx = tpx; spy = tpy; spz = tpz;
            sl->gainsSmooth[p] = sl->gains[p];
        }
        else { const float k = 0.06f; spx += jx*k; spy += jy*k; spz += jz*k; }       /* ~200ms ease */
        sl->ptsSmooth[p*3] = spx; sl->ptsSmooth[p*3+1] = spy; sl->ptsSmooth[p*3+2] = spz;

        float dx = spx - lx;
        float dy = spy - ly;
        float dz = spz - lz;
        float d  = sqrtf(dx*dx + dy*dy + dz*dz);

        /* OCCLUSION AS VIRTUAL DISTANCE: emGains[p] now carries not a gain but the extra blocks of
         * "distance" the walls / the go-around add (0 = clear line). Folding it into the distance means
         * a wall makes the radio sound FARTHER (quieter AND duller on the same smooth curve) instead of
         * applying a separate multiplicative cliff — which is what made it feel all-or-nothing. Slewed
         * at audio rate (exponential) so the change glides as you move, never pops. */
        float target = sl->gains[p], cur = sl->gainsSmooth[p];
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
        sl->gainsSmooth[p] = cur;
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
        int cls = sl->ptFlags[p] & 1;   /* v34: 0 = speaker, 1 = ham — independent colored signals */
        energyL[cls] += cL * cL;
        energyR[cls] += cR * cR;

        /* Track the loudest speaker PER CLASS; its EFFECTIVE distance sets that class's tone colour
         * (far/occluded = dull). The tone cue is measured against a FIXED reference distance
         * (CMM_EMIT_MUFFLE_REF blocks), NOT the full Range: normalizing to Range meant a large Range
         * (e.g. 100) diluted muffle to nothing at any audible distance, and diluted occlusion's
         * virtual distance too. With an absolute ref, the tone is fully dulled (per Muffle strength)
         * by ~ref blocks of effective distance regardless of how far the volume carries — and a
         * wall's added virtual distance now audibly dulls. */
        float contrib = cL * cL + cR * cR;
        if (contrib > bestContrib[cls]) {
            bestContrib[cls] = contrib;
            float nd;
            if (o->emOcclTone) {
                /* --- TONE FROM OCCLUSION, NOT FROM VIRTUAL DISTANCE ---
                 * dEff carries occlusion as ADDED BLOCKS, and how many blocks a given
                 * attenuation is worth depends on where you stand: near a speaker there is a
                 * whole Range of headroom to spend, far away there is almost none. So one
                 * physical wall dulled the tone far more up close than at distance, and a
                 * listener standing ON a speaker heard it heavily muffled with nothing in the
                 * way. Reported exactly so: "it gets muffled when I'm closer to it, especially
                 * while right on top of it."
                 *
                 * The level path is right -- the curve inversion reproduces the model's gain at
                 * any distance -- so the fault is only that TONE reads the same number. Recover
                 * the pure occlusion ratio by dividing this point's attenuation by what it would
                 * be with nothing in the way, which needs nothing the mixer does not already
                 * have, and drive the dullness from that. Distance still dulls on its own (air
                 * absorbs), but now it is the REAL distance rather than the virtual one. */
                float attClear;
                if (d <= mn) attClear = 1.0f;
                else if (d >= mx) attClear = 0.0f;
                else attClear = powf((mx - d) / span, fexp);
                float occlAmp = (attClear > 1e-6f) ? (att / attClear) : 1.0f;
                if (occlAmp > 1.0f) occlAmp = 1.0f;
                float occlDb = (occlAmp > 1e-6f) ? (-20.0f * log10f(occlAmp)) : 120.0f;
                nd = (d - mn) / CMM_EMIT_MUFFLE_REF + occlDb / o->emOcclFullDb;
            } else {
                nd = (dEff - mn) / CMM_EMIT_MUFFLE_REF;
            }
            if (nd < 0.0f) nd = 0.0f; else if (nd > 1.0f) nd = 1.0f;
            bestBright[cls] = 1.0f - muffle * nd;        /* farther / more-occluded ⇒ duller (one curve) */
        }
        if (contrib > bestContribAll) { bestContribAll = contrib; dom = cls; }

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
    sl->prevPtCount = count;   /* point slots ≥ count are inactive → re-init from target when they return */
    totalL[0] = sqrtf(energyL[0]); totalR[0] = sqrtf(energyR[0]);
    totalL[1] = sqrtf(energyL[1]); totalR[1] = sqrtf(energyR[1]);
    /* Anti-clip magnitude cap on the TOTAL across classes (both sum into the same output), scaling
     * both classes by the same factor so their balance is preserved. Still worth having for a
     * roomful of speakers, but it can no longer PUMP with the listener's facing: the magnitude it
     * tests is now sum(att^2) over the points, which has no bearing term in it. */
    {
        float tl = totalL[0] + totalL[1], tr = totalR[0] + totalR[1];
        float mag = sqrtf(tl*tl + tr*tr);
        if (mag > 1.4f) {
            float s = 1.4f / mag;
            totalL[0] *= s; totalR[0] *= s;
            totalL[1] *= s; totalR[1] *= s;
        }
    }
    {
        int c;
        for (c = 0; c < 2; c++) {
            if (bestBright[c] < 0.0f) bestBright[c] = 0.0f; else if (bestBright[c] > 1.0f) bestBright[c] = 1.0f;
            gL[c] = totalL[c]; gR[c] = totalR[c]; bright[c] = bestBright[c];
        }
    }
    *wet = bestWetAtt; *wetPan = bestWetPan; *domClass = dom;
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
        /* Flush the emitter slot rings too, so they snap tight in lockstep with the music ring. */
        {
            int s;
            for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
                struct cmm_emit_slot* sl = &o->emSlots[s];
                if (!sl->hasRing) continue;
                ma_uint32 ea = ma_pcm_rb_available_read(&sl->rb);
                while (ea > 0) {
                    ma_uint32 n = ea; void* p = NULL;
                    if (ma_pcm_rb_acquire_read(&sl->rb, &n, &p) != MA_SUCCESS || n == 0) break;
                    ma_pcm_rb_commit_read(&sl->rb, n);
                    ea -= n;
                }
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

    /* ── Emitter layer (v31: per-station slots): spatialize each slot's dry-mono ring at OUTPUT TIME ──
     * Pan/attenuation/occlusion is computed here from the freshest relayed listener state, so a
     * head-turn is heard within a callback (~10ms), not behind the producer's ring cushion. Each
     * slot's ring is drained in lockstep with the music; slots mix additively; the two shared room
     * reverbs are fed per-slot input sends; the summed result is clip-guarded. */
    {
        int s, anyRing = 0;
        for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) if (o->emSlots[s].hasRing) { anyRing = 1; break; }
        if (anyRing) {
        /* Acoustic-state reset (producer-raised when a NEW station voice starts fresh with no other
         * station live): clear the reverb tails + every render-state slew so nothing from a previous
         * voice — a destroyed radio's still-ringing reverb energy, a stale occlusion glide — bleeds
         * under the new one. Serviced here because this thread owns all of this state. The ~460KB of
         * comb/allpass memsets in cmm_reverb_init run well under a millisecond. */
        if (o->emAcousticReset) {
            o->emAcousticReset = 0;
            cmm_reverb_init(&o->reverb, o->sampleRate);
            cmm_reverb_init(&o->reverbLocal, o->sampleRate);
            o->emRevHold = 0.0f;
            o->emRevLocMix = 0.0f; o->emRevLocHold = 0.0f;
            o->emRevLpL = 0.0f; o->emRevLpR = 0.0f; o->emRevLpCoef = 1.0f;
            o->emRevPanSmooth = 0.0f;
            for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
                struct cmm_emit_slot* sl = &o->emSlots[s];
                sl->lpf[0] = sl->lpf[1] = 0.0f; sl->lpfCoef[0] = sl->lpfCoef[1] = 1.0f;
                sl->prevPtCount = 0;   /* every point slot re-inits its smooth state from target */
                sl->revSendSm = 0.0f; sl->revDepthSm = sl->revDepth;
            }
        }

        /* Per-slot flush (v32): a DIFFERENT station acquired this slot — discard the previous
         * station's queued dry mono and reset the slot's render state so nothing of it renders
         * under the new voice. (The new voice's first frames may already be queued behind the
         * residual; losing them is ≤ one producer block, masked by its fade-in.) */
        for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
            struct cmm_emit_slot* sl = &o->emSlots[s];
            if (!sl->flushReq) continue;
            sl->flushReq = 0;
            if (sl->hasRing) {
                ma_uint32 ea = ma_pcm_rb_available_read(&sl->rb);
                while (ea > 0) {
                    ma_uint32 n = ea; void* p = NULL;
                    if (ma_pcm_rb_acquire_read(&sl->rb, &n, &p) != MA_SUCCESS || n == 0) break;
                    ma_pcm_rb_commit_read(&sl->rb, n);
                    ea -= n;
                }
            }
            sl->lpf[0] = sl->lpf[1] = 0.0f; sl->lpfCoef[0] = sl->lpfCoef[1] = 1.0f;
            sl->colLo[0] = sl->colLo[1] = 0.0f; sl->colHi[0] = sl->colHi[1] = 0.0f;
            sl->prevPtCount = 0;      /* point smooth state re-inits from the new station's targets */
            sl->revSendSm = 0.0f;
        }

        /* Per-slot spatial gains, LPF targets and carry-send targets — once per callback, exactly as
         * the old single-station path did. The DOMINANT slot (loudest carry send) drives the shared
         * bus's pan, output darkening and damping bias. */
        /* v34: gains/level/LPF are PER CLASS (index 0 = speaker points, 1 = ham points) — each class
         * is an independently colored signal. domCls picks which class's signal feeds the carry. */
        float gL[CMM_MAX_EMIT_VOICES][2], gR[CMM_MAX_EMIT_VOICES][2];
        float aLvl[CMM_MAX_EMIT_VOICES][2], coef[CMM_MAX_EMIT_VOICES][2];
        int   clsAud[CMM_MAX_EMIT_VOICES][2];
        int   domCls[CMM_MAX_EMIT_VOICES];
        float sendT[CMM_MAX_EMIT_VOICES];
        int   audible[CMM_MAX_EMIT_VOICES];
        int   anyAudible = 0;
        float damt = o->emRevDepthAmt;
        float bestSend = 0.0f, bestPan = 0.0f, bestNdep1 = 0.0f;
        for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
            struct cmm_emit_slot* sl = &o->emSlots[s];
            float bright[2], attWet, wetPan;
            cmm_emitter_gains(o, sl, gL[s], gR[s], bright, &attWet, &wetPan, &domCls[s]);
            clsAud[s][0] = (sl->ptCount > 0) && (gL[s][0] != 0.0f || gR[s][0] != 0.0f);
            clsAud[s][1] = (sl->ptCount > 0) && (gL[s][1] != 0.0f || gR[s][1] != 0.0f);
            audible[s] = clsAud[s][0] || clsAud[s][1];
            if (audible[s]) anyAudible = 1;
            /* Arriving level for the LOCAL bus, per class: magnitude of the summed spatial gains
             * (equal-power pan preserves per-speaker energy ⇒ ≈ the distance+occlusion attenuation
             * applied). */
            {
                int c;
                for (c = 0; c < 2; c++) {
                    float al = sqrtf(gL[s][c]*gL[s][c] + gR[s][c]*gR[s][c]); if (al > 1.0f) al = 1.0f;
                    aLvl[s][c] = al;
                }
            }

            /* DEPTH GRADING (v27, per slot): sink this slot's carry send with its air-route depth
             * (slewed ~0.5s so 4Hz pushes glide). LEVEL decays exponentially (much slower than the
             * dry — a reverberant field carries far), normalized to a fixed reference like the muffle
             * so it's Range-independent; the range-edge taper in cmm_emitter_gains bounds the reach. */
            sl->revDepthSm += (sl->revDepth - sl->revDepthSm) * 0.02f;
            float ndep = (sl->revDepthSm - o->emMinDist) * (1.0f / CMM_EMIT_REV_DEPTH_REF);
            if (ndep < 0.0f) ndep = 0.0f; else if (ndep > 2.0f) ndep = 2.0f;
            float send = sl->revWet * attWet;
            if (damt > 0.0f) send *= expf(-1.2f * damt * ndep);
            sendT[s] = send;
            float ndep1 = ndep > 1.0f ? 1.0f : ndep;
            if (send > bestSend) { bestSend = send; bestPan = wetPan; bestNdep1 = ndep1; }

            /* Map brightness → this slot's one-pole LPF coefficient PER CLASS (1 = open/bypass,
             * small = muffled), then SLEW the live coefficient toward it (±0.05/callback) so a
             * distance/occlusion step doesn't click. bright² dulls faster with distance; the 0.03
             * floor keeps a little air even fully occluded so it never goes to pure DC. */
            {
                int c;
                for (c = 0; c < 2; c++) {
                    float bb = bright[c] * bright[c];
                    float ct = 0.03f + 0.97f * bb;
                    float cc = sl->lpfCoef[c];
                    float dc = ct - cc;
                    if (dc > 0.05f) dc = 0.05f; else if (dc < -0.05f) dc = -0.05f;
                    coef[s][c] = cc + dc;
                }
            }
        }

        /* BRIGHTNESS/darkening for the shared carry OUTPUT — from the dominant slot's depth: near =
         * bright splash (coef 1 = transparent), deep = dark rumble. Slewed like the dry muffle. Runs
         * on the tail too (out of range the darkness holds — the field you left doesn't re-brighten). */
        float rbb = 1.0f - damt * bestNdep1;
        float rcoefT = 0.05f + 0.95f * rbb * rbb;
        float rcoef = o->emRevLpCoef;
        float rdc = rcoefT - rcoef;
        if (rdc > 0.05f) rdc = 0.05f; else if (rdc < -0.05f) rdc = -0.05f;
        rcoef += rdc;
        o->emRevLpCoef = rcoef;

        int wetFeed = bestSend > 1e-4f;   /* some station in wet range → carry still fed new energy */

        float locWet = o->emRevLocWet;
        /* Tail keepalive: while feeding, recharge the hold timer. Once every feed stops (out of
         * range), count down and keep the reverb ticking — the per-slot sends slew to ~0 so the tail
         * decays at its natural level (v31: no held-gain fade needed) and the buffers DRAIN, so no
         * stale energy clicks out on re-entry. ~2s covers the longest room decay. */
        float revTailFrames = (float)o->sampleRate * 2.0f;
        if (wetFeed) o->emRevHold = revTailFrames;
        else if (o->emRevHold > 0.0f) {
            o->emRevHold -= (float)frameCount;
            if (o->emRevHold < 0.0f) o->emRevHold = 0.0f;
        }
        int wetOn = wetFeed || o->emRevHold > 0.0f;

        /* Local keepalive mirrors the carry's: once the dry goes silent, tick with zero input so the
         * local tail rings out and the buffers drain (no click on the next arrival). */
        int locFeed = anyAudible && locWet > 1e-4f;
        if (locFeed) o->emRevLocHold = revTailFrames;
        else if (o->emRevLocHold > 0.0f) {
            o->emRevLocHold -= (float)frameCount;
            if (o->emRevLocHold < 0.0f) o->emRevLocHold = 0.0f;
        }
        int locOn = locFeed || o->emRevLocHold > 0.0f;
        float locTarget = locFeed ? locWet : 0.0f;   /* slewed below; the tail also decays on its own */
        float mixCoef = 1.0f / (0.025f * (float)o->sampleRate);   /* ~25ms one-pole slew */
        if (mixCoef > 1.0f) mixCoef = 1.0f;

        /* DIRECTIONAL REVERB: bias the diffuse reflections toward the DOMINANT source's traced bearing
         * so at distance (where the wet dominates the faded dry) the reverb still points the way — in a
         * cave, DOWN THE TUNNEL toward the radio. Freeze the pan during the out-of-range tail (hold last
         * bearing), and slew it (~25ms) so it never steps. emRevPan scales it: 0 = fully diffuse
         * (surrounds you), 1 = fully located. Per-ear weights average to 1 (energy-preserving). */
        float panTarget = wetFeed ? bestPan : o->emRevPanSmooth;
        o->emRevPanSmooth += (panTarget - o->emRevPanSmooth) * mixCoef;
        float wp = o->emRevPanSmooth; if (wp < -1.0f) wp = -1.0f; else if (wp > 1.0f) wp = 1.0f;
        float wgL = 1.0f - o->emRevPan * wp;   /* source-left (wp<0) ⇒ >1 on L */
        float wgR = 1.0f + o->emRevPan * wp;

        /* Refresh reverb decay/damping from the live room probe (cheap coefficient update). Depth biases
         * the CARRY's damping up (each extra bounce down the tunnel sheds highs), on top of the output
         * LPF; the LOCAL bus is your own room — its input is already muffled, so no depth bias. */
        if (wetOn) {
            float dampEff = o->emRevDamp + 0.35f * damt * bestNdep1;
            if (dampEff > 1.0f) dampEff = 1.0f;
            cmm_reverb_set(&o->reverb, o->emRevRoom, dampEff);
        }
        if (locOn) cmm_reverb_set(&o->reverbLocal, o->emRevRoom, o->emRevDamp);

        /* Radio coloration per-block constants, PER CLASS (v34: [0] = speaker set, [1] = ham set;
         * shared params, per-slot-per-class filter STATE). The soft-clip is level-compensated at a
         * 0.25 reference (typical music peak after loudness normalization): quiet material passes
         * near-unity, peaks get crushed — grit without a volume jump. */
        float colDrive[2], colStatic[2], colVoice[2];
        float colPre[2], colMk[2], colNoiseAmp[2];
        int   colOn[2];
        colDrive[0] = o->emColDrive;    colStatic[0] = o->emColStatic;    colVoice[0] = o->emColVoice;
        colDrive[1] = o->emColHamDrive; colStatic[1] = o->emColHamStatic; colVoice[1] = o->emColHamVoice;
        {
            int c;
            for (c = 0; c < 2; c++) {
                colOn[c] = (colDrive[c] > 0.001f) || (colStatic[c] > 0.001f) || (colVoice[c] > 0.001f);
                colPre[c] = 1.0f + colDrive[c] * 5.0f;
                colMk[c] = 0.25f / tanhf(colPre[c] * 0.25f);
                colNoiseAmp[c] = colStatic[c] * 0.08f;
            }
        }

        /* Render whenever ANY dry is audible OR there's reverb to run. The carry wet is NOT occluded
         * (it carries around corners/through walls — the "it bounced / next room" cue), so it MUST
         * render even when every dry is fully occluded to silence. Rings are drained (and residual
         * discarded) regardless, keeping them in lockstep with the music ring. */
        int render = anyAudible || wetOn || locOn;

        ma_uint32 base = 0;
        while (base < frameCount) {
            ma_uint32 chunk = frameCount - base;
            if (chunk > CMM_EMIT_CHUNK) chunk = CMM_EMIT_CHUNK;
            /* Fill each slot's scratch from its ring; zero-fill on underrun / idle slot (an idle
             * slot's ring is empty — the producer only writes rings whose station produced audio). */
            for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
                struct cmm_emit_slot* sl = &o->emSlots[s];
                ma_uint32 got = 0;
                if (sl->hasRing) {
                    while (got < chunk) {
                        ma_uint32 n = chunk - got;
                        void* rp = NULL;
                        if (ma_pcm_rb_acquire_read(&sl->rb, &n, &rp) != MA_SUCCESS || n == 0) break;
                        memcpy(sl->buf + got, rp, (size_t)n * sizeof(float));
                        ma_pcm_rb_commit_read(&sl->rb, n);
                        got += n;
                    }
                }
                if (got < chunk) {
                    memset(sl->buf + got, 0, (size_t)(chunk - got) * sizeof(float));
                    /* ONLY COUNT A SLOT THAT IS SUPPOSED TO BE MAKING SOUND. An IDLE slot's ring is
                     * empty by design -- the producer writes only lanes whose station produced
                     * audio -- so counting every zero-fill made the counter climb forever on the
                     * five unused slots and read 11 million frames of "starvation" on a machine
                     * that sounded fine. A number that is always alarming is the same as no number
                     * at all, which is precisely the failure this counter was added to end.
                     * ptCount > 0 is the relay's own "this slot is audible this block" flag. */
                    if (sl->hasRing && sl->ptCount > 0)
                        sl->underFrames += (unsigned)(chunk - got);
                }
            }
            if (render) {
                float* op = (float*)output + (size_t)base * o->channels;
                ma_uint32 f;
                for (f = 0; f < chunk; f++) {
                    float carryIn = 0.0f, locIn = 0.0f;
                    for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
                        struct cmm_emit_slot* sl = &o->emSlots[s];
                        /* Slew this slot's carry send even when silent, so it decays cleanly to 0. */
                        sl->revSendSm += (sendT[s] - sl->revSendSm) * mixCoef;
                        int carrying = sl->revSendSm > 1e-5f;
                        if (!audible[s] && !carrying) continue;   /* nothing arrives, nothing carries */
                        /* Radio coloration PER CLASS (v34: 0 = speaker set, 1 = ham set) — BEFORE the
                         * distance LPF and the reverb feed, so muffle, pan, occlusion and reflections
                         * all act on the colored ("through a speaker") signal. A class is computed
                         * only when something consumes it: its own audible points, or the carry (fed
                         * the DOMINANT class's signal). All params 0 ⇒ x is the raw mono, exact bypass. */
                        float raw = sl->buf[f];
                        float xc[2];
                        int c;
                        for (c = 0; c < 2; c++) {
                            if (!clsAud[s][c] && !(carrying && domCls[s] == c)) continue;
                            float x = raw;
                            if (colOn[c]) {
                                /* Static bed first, so the hiss shares the voicing + drive (it comes out
                                 * of the same speaker — and downstream it's distance-faded/panned too). */
                                if (colNoiseAmp[c] > 0.0f) {
                                    sl->colRng = sl->colRng * 1664525u + 1013904223u;
                                    x += (float)(int)sl->colRng * (1.0f / 2147483648.0f) * colNoiseAmp[c];
                                }
                                /* Band-pass voicing: strip lows (~250 Hz) and highs (~4.2 kHz) — the
                                 * "small speaker" body — blended by `voice` (0 keeps full-range). */
                                if (colVoice[c] > 0.0f) {
                                    sl->colLo[c] += o->emColLoCoef * (x - sl->colLo[c]);
                                    float bp = x - sl->colLo[c];                 /* highs + mids (lows removed) */
                                    sl->colHi[c] += o->emColHiCoef * (bp - sl->colHi[c]); /* cap the top */
                                    x += (sl->colHi[c] - x) * colVoice[c];
                                }
                                /* Soft-clip drive (Padé tanh, exact ±1 at the ±3 clamp). */
                                if (colDrive[c] > 0.001f) {
                                    float t = x * colPre[c];
                                    if (t > 3.0f) t = 3.0f; else if (t < -3.0f) t = -3.0f;
                                    float t2 = t * t;
                                    x = t * (27.0f + t2) / (27.0f + 9.0f * t2) * colMk[c];
                                }
                            }
                            xc[c] = x;
                            if (clsAud[s][c]) {
                                sl->lpf[c] += coef[s][c] * (x - sl->lpf[c]);   /* one-pole distance/occlusion low-pass */
                                op[f * o->channels]     += sl->lpf[c] * gL[s][c];
                                op[f * o->channels + 1] += sl->lpf[c] * gR[s][c];
                                /* LOCAL bus input: the muffled arriving dry × its applied attenuation, so
                                 * occlusion/distance/water/hard-stop govern it exactly as the dry. */
                                locIn += sl->lpf[c] * aLvl[s][c];
                            }
                        }
                        /* CARRY input: the DOMINANT class's colored (pre-occlusion-LPF) mono × this
                         * slot's slewed send — NOT occluded, so reflections carry around/through walls
                         * at the seal-gated, depth-graded level the managed side computed for THIS
                         * station. */
                        if (carrying) carryIn += xc[domCls[s]] * sl->revSendSm;
                    }
                    if (wetOn) {
                        float wl, wr;
                        cmm_reverb_tick(&o->reverb, carryIn, &wl, &wr);
                        /* Depth darkening: one-pole on the wet output (coef 1 at zero depth). */
                        o->emRevLpL += rcoef * (wl - o->emRevLpL); wl = o->emRevLpL;
                        o->emRevLpR += rcoef * (wr - o->emRevLpR); wr = o->emRevLpR;
                        op[f * o->channels]     += wl * wgL;
                        op[f * o->channels + 1] += wr * wgR;
                    }
                    if (locOn) {
                        o->emRevLocMix += (locTarget - o->emRevLocMix) * mixCoef;
                        float ll, lr;
                        cmm_reverb_tick(&o->reverbLocal, locIn, &ll, &lr);
                        op[f * o->channels]     += ll * o->emRevLocMix;
                        op[f * o->channels + 1] += lr * o->emRevLocMix;
                    }
                }
            }
            base += chunk;
        }
        for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
            o->emSlots[s].lpfCoef[0] = coef[s][0];
            o->emSlots[s].lpfCoef[1] = coef[s][1];
        }
        /* Clip-guard the summed output (music + emitters can exceed unity). */
        if (render) {
            float* p = (float*)output;
            ma_uint32 total = frameCount * (ma_uint32)o->channels, i;
            for (i = 0; i < total; i++) {
                float sv = p[i];
                p[i] = sv > 1.0f ? 1.0f : (sv < -1.0f ? -1.0f : sv);
            }
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

    /* Per-station-slot rings for the emitters' dry MONO signals (spatialized in the callback). Same
     * frame depth as the music ring so the cushions track. Failure is non-fatal — a slot without a
     * ring simply never plays (hasRing stays 0) rather than losing music output. */
    o->emLisRight[0] = 1.0f;                 /* sane default until the producer relays a listener */
    o->emMinDist = 1.0f; o->emMaxDist = 2.0f;
    o->emFalloffExp = 1.5f; o->emMuffle = 0.7f;   /* sane curve until config is relayed */
    o->emStereoWidth = 0.85f;                     /* nearly full pan until config is relayed */
    o->emRevRoom = 0.5f; o->emRevDamp = 0.5f;     /* reverb off (sends 0) until the room probe relays */
    o->emRevRangeMult = 1.0f;   /* reverb range == dry range until config relays */
    o->emRevPan = 0.0f; o->emRevPanSmooth = 0.0f;   /* diffuse reverb until config relays */
    o->emRevHold = 0.0f;                            /* tail keepalive idle */
    o->emRevDepthAmt = 0.0f;                        /* depth grading off until relayed */
    o->emRevLpL = 0.0f; o->emRevLpR = 0.0f; o->emRevLpCoef = 1.0f;           /* wet LPF starts transparent */
    /* Coloration off (clean bypass) until config relays; fixed voicing cutoffs from the device rate. */
    o->emColDrive = 0.0f; o->emColStatic = 0.0f; o->emColVoice = 0.0f;
    o->emColHamDrive = 0.0f; o->emColHamStatic = 0.0f; o->emColHamVoice = 0.0f;
    o->emColLoCoef = 1.0f - expf(-6.2831853f * 250.0f  / (float)sampleRate);
    o->emColHiCoef = 1.0f - expf(-6.2831853f * 4200.0f / (float)sampleRate);
    o->emRevLocWet = 0.0f; o->emRevLocMix = 0.0f; o->emRevLocHold = 0.0f;    /* local bus idle until relayed */
    cmm_reverb_init(&o->reverb, sampleRate);
    cmm_reverb_init(&o->reverbLocal, sampleRate);
    {
        int s;
        for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
            struct cmm_emit_slot* sl = &o->emSlots[s];
            sl->lpf[0] = sl->lpf[1] = 0.0f;       /* LPF starts open (bypass) until a distance is relayed */
            sl->lpfCoef[0] = sl->lpfCoef[1] = 1.0f;
            sl->colRng = 0x2545F491u + (unsigned int)s * 0x9E3779B9u;   /* decorrelated hiss per slot */
            sl->revWet = 0.0f; sl->revDepth = 0.0f; sl->revSendSm = 0.0f; sl->revDepthSm = 0.0f;
            if (ma_pcm_rb_init(ma_format_f32, 1, (ma_uint32)ringFrames, NULL, NULL, &sl->rb) == MA_SUCCESS)
                sl->hasRing = 1;
        }
    }
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
/* Like cmm_out_drain, but COPIES the frames out instead of discarding them. This is what
 * makes the headless path useful for anything other than leak-hunting: the offline harness
 * gets the mixer's real output as samples, so a station change can be rendered to a WAV and
 * compared against the reference render instead of against somebody's memory of it.
 *
 * `dst` receives interleaved float frames and must hold frames * channels floats. Applies the
 * consumer output gain exactly as the realtime callback does, so a captured render and an
 * audible one are the same signal. */
CMM_API int cmm_out_drain_into(cmm_out* o, float* dst, int frames)
{
    int consumed = 0;
    if (!o || !dst || frames <= 0) return 0;

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

    while (consumed < frames) {
        ma_uint32 n = (ma_uint32)(frames - consumed);
        void* readPtr = NULL;
        if (ma_pcm_rb_acquire_read(&o->rb, &n, &readPtr) != MA_SUCCESS || n == 0) break;
        memcpy(dst + (size_t)consumed * o->channels, readPtr,
               (size_t)n * o->channels * sizeof(float));
        ma_pcm_rb_commit_read(&o->rb, n);
        consumed += (int)n;
    }

    /* The same per-frame gain ramp the realtime callback applies. */
    {
        int f, c;
        for (f = 0; f < consumed; f++) {
            if (o->gainStep != 0.0f) {
                o->gain += o->gainStep;
                if ((o->gainStep > 0.0f && o->gain >= o->gainTarget) ||
                    (o->gainStep < 0.0f && o->gain <= o->gainTarget))
                { o->gain = o->gainTarget; o->gainStep = 0.0f; }
            }
            for (c = 0; c < o->channels; c++) dst[(size_t)f * o->channels + c] *= o->gain;
        }
    }
    return consumed;
}

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
    /* Keep the emitter slot rings drained in lockstep (harness has no emitter, so usually empty). */
    {
        int s;
        for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
            struct cmm_emit_slot* sl = &o->emSlots[s];
            if (!sl->hasRing) continue;
            int ec = 0;
            while (ec < consumed) {
                ma_uint32 n = (ma_uint32)(consumed - ec);
                void* rp = NULL;
                if (ma_pcm_rb_acquire_read(&sl->rb, &n, &rp) != MA_SUCCESS || n == 0) break;
                ma_pcm_rb_commit_read(&sl->rb, n);
                ec += (int)n;
            }
        }
    }
    return consumed;
}

/* Enqueue up to `frames` interleaved-f32 frames. Non-blocking: writes only what
 * fits in the ring right now and returns the number of frames accepted (may be
 * 0 or < frames when the ring is full — the producer should retry the remainder
 * after a short sleep). */
/* v45 — MIX A SOUND INTO AUDIO THAT IS ALREADY QUEUED.
 *
 * The producer runs ahead of the speaker by the output cushion -- 200 ms in calm play, more while
 * it is banking against a main-thread freeze. That cushion is exactly what makes the music
 * survive a stutter, and it is also, unavoidably, the latency of anything armed normally: a click
 * fired now goes in BEHIND 200 ms of already-decided audio. For music that is invisible. For a
 * button it is the difference between a device and a delay, and it was reported as "the clicks
 * feel delayed" the first time anyone pressed one.
 *
 * So a UI sound does not queue. It is mixed straight into the frames already waiting, starting a
 * few milliseconds past the play head. The ring holds PRE-master audio -- the output gain, the
 * volume sliders and the pause duck are all applied in the callback on the way out -- so injected
 * audio is treated exactly like everything else and needs no gain of its own.
 *
 * `skip` is a safety margin: the callback may be reading the frames closest to the play head right
 * now, and adding to those would land half a click in one block and half in the next. Past the
 * margin the frames are queued but untouched.
 *
 * Returns frames injected, or 0 if the sound does not fit contiguously -- the ring wraps, and the
 * caller is expected to fall back to arming it normally rather than accept a truncated one. A late
 * click is a blemish; half a click is a defect. */
CMM_API int cmm_out_inject(cmm_out* o, const float* mono, int frames, float gain, int skip)
{
    if (!o || !mono || frames <= 0) return 0;
    if (skip < 0) skip = 0;
    ma_uint32 n = (ma_uint32)(frames + skip);
    void* p = NULL;
    if (ma_pcm_rb_acquire_read(&o->rb, &n, &p) != MA_SUCCESS || p == NULL) return 0;
    if ((int)n < frames + skip) { ma_pcm_rb_commit_read(&o->rb, 0); return 0; }   /* wrapped: decline */
    float* f = (float*)p + (size_t)skip * o->channels;
    for (int i = 0; i < frames; i++) {
        float v = mono[i] * gain;
        for (int c = 0; c < o->channels; c++) f[(size_t)i * o->channels + c] += v;
    }
    ma_pcm_rb_commit_read(&o->rb, 0);          /* nothing consumed: we only added to it */
    return frames;
}

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

/* v20/v31 — enqueue up to `frames` MONO f32 frames of one SLOT's dry signal. Non-blocking; returns
 * frames accepted. SPSC: the producer (mixer thread) is the sole writer per ring, like cmm_out_write. */
int cmm_out_write_emitter(cmm_out* o, int slot, const float* mono, int frames)
{
    if (!o || slot < 0 || slot >= CMM_MAX_EMIT_VOICES || !mono || frames <= 0) return 0;
    struct cmm_emit_slot* sl = &o->emSlots[slot];
    if (!sl->hasRing) return 0;

    /* GIVE THE SLOT ITS OWN CUSHION.
     *
     * The producer decides how much to render from the MUSIC ring's deficit alone, then writes
     * that same frame count to every active slot. The consumer drains both by the same amount
     * every callback, so the difference between them is an INVARIANT, fixed at the moment the slot
     * ring was last empty. A station's voice starts into an already-cushioned device -- music at
     * ~200 ms, slot at zero -- so that slot then runs at ~zero cushion for its whole life. The
     * music has 200 ms of protection against a late producer; the radios have one poll interval.
     * Any hiccup -- a GC pause, a decode from the exFAT library, three stations wanting frames in
     * the same tick -- lands as a hole in the radio and nothing at all in the music.
     *
     * And it re-arms: every track advance empties the lane and restarts the invariant.
     *
     * So when the ring is found EMPTY, pad it up to the same depth the music is running at before
     * writing real audio. Self-healing rather than only-at-startup: whatever caused the lane to
     * run dry, it gets its cushion back. Costs one buffer of latency on that lane, which matches
     * what the music path has always had. */
    if (o->emPrime > 0 && ma_pcm_rb_available_read(&sl->rb) == 0) {
        ma_uint32 want = (ma_uint32)o->emPrime, done = 0;
        while (done < want) {
            ma_uint32 n = want - done;
            void* wp = NULL;
            if (ma_pcm_rb_acquire_write(&sl->rb, &n, &wp) != MA_SUCCESS || n == 0) break;
            memset(wp, 0, (size_t)n * sizeof(float));
            ma_pcm_rb_commit_write(&sl->rb, n);
            done += n;
        }
        if (done) sl->primes++;
    }

    int written = 0;
    while (written < frames) {
        ma_uint32 n = (ma_uint32)(frames - written);
        void* wp = NULL;
        if (ma_pcm_rb_acquire_write(&sl->rb, &n, &wp) != MA_SUCCESS || n == 0) break;
        memcpy(wp, mono + written, (size_t)n * sizeof(float));   /* mono → 1 float/frame */
        ma_pcm_rb_commit_write(&sl->rb, n);
        written += (int)n;
    }
    return written;
}

/* v20/v31/v34 — relay one SLOT's live spatial state (plus the shared listener + curve tuning). Lock-free
 * volatile writes (a torn read mis-pans at most one ~10ms callback). count clamps to CMM_MAX_EMIT_PTS.
 * v34: `flags` = per-point render flags (bit0 = ham class; NULL = all 0 = speaker class). */
/* v42: see the tone rule in cmm_emitter_gains. Off restores the shipped behaviour exactly. */
void cmm_out_set_emitter_occl_tone(cmm_out* o, int enable, float fullDb)
{
    if (!o) return;
    o->emOcclTone = enable ? 1 : 0;
    o->emOcclFullDb = (fullDb > 0.5f) ? fullDb : 8.0f;
}

void cmm_out_set_emitter_spatial(cmm_out* o, int slot, const float* lisPos3, const float* lisRight3,
                                 const float* pts, const float* gains, const int* flags, int count,
                                 float minDist, float maxDist, float falloffExp, float muffle,
                                 float stereoWidth)
{
    if (!o || slot < 0 || slot >= CMM_MAX_EMIT_VOICES) return;
    struct cmm_emit_slot* sl = &o->emSlots[slot];
    if (count < 0) count = 0;
    if (count > CMM_MAX_EMIT_PTS) count = CMM_MAX_EMIT_PTS;
    if (lisPos3)   { o->emLisPos[0]   = lisPos3[0];   o->emLisPos[1]   = lisPos3[1];   o->emLisPos[2]   = lisPos3[2]; }
    if (lisRight3) { o->emLisRight[0] = lisRight3[0]; o->emLisRight[1] = lisRight3[1]; o->emLisRight[2] = lisRight3[2]; }
    o->emMinDist = minDist; o->emMaxDist = maxDist;
    o->emFalloffExp = falloffExp; o->emMuffle = muffle;
    o->emStereoWidth = stereoWidth;
    int k;
    for (k = 0; k < count * 3; k++) sl->pts[k]    = pts   ? pts[k]   : 0.0f;
    for (k = 0; k < count;     k++) sl->gains[k]  = gains ? gains[k] : 1.0f;
    for (k = 0; k < count;     k++) sl->ptFlags[k] = flags ? flags[k] : 0;
    sl->ptCount = count;   /* publish count LAST so the callback never reads points for a larger count */
}

/* v23/v31 — relay the live reverb (room reflection) parameters from the managed room probe.
 * wets/depths = PER-SLOT carry-send levels + air-route depths (CMM_MAX_EMIT_VOICES floats; NULL = all 0).
 * room/damp/pan/localWet describe the LISTENER's room (shared). rangeMult (≥1) extends the wet's audible
 * distance past the dry range so the tail survives a brief step out of range in a connected cave. */
void cmm_out_set_emitter_reverb(cmm_out* o, const float* wets, const float* depths,
                                float room, float damp, float rangeMult, float pan,
                                float depthAmt, float localWet)
{
    if (!o) return;
    if (room < 0.0f) room = 0.0f; else if (room > 1.0f) room = 1.0f;
    if (damp < 0.0f) damp = 0.0f; else if (damp > 1.0f) damp = 1.0f;
    if (rangeMult < 1.0f) rangeMult = 1.0f; else if (rangeMult > 8.0f) rangeMult = 8.0f;
    if (pan  < 0.0f) pan  = 0.0f; else if (pan  > 1.0f) pan  = 1.0f;
    if (depthAmt < 0.0f) depthAmt = 0.0f; else if (depthAmt > 1.0f) depthAmt = 1.0f;
    if (localWet < 0.0f) localWet = 0.0f; else if (localWet > 1.0f) localWet = 1.0f;
    {
        int s;
        for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
            float w = wets   ? wets[s]   : 0.0f;
            float d = depths ? depths[s] : 0.0f;
            if (w < 0.0f) w = 0.0f; else if (w > 1.0f) w = 1.0f;
            if (d < 0.0f) d = 0.0f; else if (d > 512.0f) d = 512.0f;
            o->emSlots[s].revWet = w;
            o->emSlots[s].revDepth = d;
        }
    }
    o->emRevRoom = room; o->emRevDamp = damp; o->emRevRangeMult = rangeMult; o->emRevPan = pan;
    o->emRevDepthAmt = depthAmt;
    o->emRevLocWet = localWet;
}

/* v30 — request a full emitter acoustic-state reset (reverb tails, LPF/pan/mix slews, per-slot
 * smooth state). Just raises a flag; the audio callback — which owns that state — services it
 * before the next block. Raised when a NEW station voice starts fresh (not a track advance), so
 * a destroyed radio's still-ringing reverb energy is never re-voiced under the replacement. */
void cmm_out_emitter_reset(cmm_out* o)
{
    if (!o) return;
    o->emAcousticReset = 1;
}

/* v32 — request ONE slot's flush (see the flushReq field). Just raises a flag; the audio callback —
 * which owns the ring's read side and the render state — services it before its next drain. */
void cmm_out_emitter_flush_slot(cmm_out* o, int slot)
{
    if (!o || slot < 0 || slot >= CMM_MAX_EMIT_VOICES) return;
    o->emSlots[slot].flushReq = 1;
}

/* v26/v34 — relay the live radio-coloration params from config: the SPEAKER set and the HAM set
 * (per-point flags pick which set a point's signal gets). All 0..1; all 0 = clean bypass. Lock-free
 * volatile writes like the rest of the emitter state (a torn read colors one ~10ms callback oddly). */
void cmm_out_set_emitter_color(cmm_out* o, float drive, float staticLvl, float voice,
                               float hamDrive, float hamStatic, float hamVoice)
{
    if (!o) return;
    if (drive     < 0.0f) drive     = 0.0f; else if (drive     > 1.0f) drive     = 1.0f;
    if (staticLvl < 0.0f) staticLvl = 0.0f; else if (staticLvl > 1.0f) staticLvl = 1.0f;
    if (voice     < 0.0f) voice     = 0.0f; else if (voice     > 1.0f) voice     = 1.0f;
    if (hamDrive  < 0.0f) hamDrive  = 0.0f; else if (hamDrive  > 1.0f) hamDrive  = 1.0f;
    if (hamStatic < 0.0f) hamStatic = 0.0f; else if (hamStatic > 1.0f) hamStatic = 1.0f;
    if (hamVoice  < 0.0f) hamVoice  = 0.0f; else if (hamVoice  > 1.0f) hamVoice  = 1.0f;
    o->emColDrive = drive; o->emColStatic = staticLvl; o->emColVoice = voice;
    o->emColHamDrive = hamDrive; o->emColHamStatic = hamStatic; o->emColHamVoice = hamVoice;
}

/* Frames of free space available to write right now (for the producer to pace
 * how much it decodes/mixes per tick). */
/* How deep the slots should be cushioned. Pushed by the producer each pass from the music ring's
 * own depth, so the two stay matched without the slots needing their own pacing loop. */
CMM_API void cmm_out_set_emitter_prime(cmm_out* o, int frames)
{
    if (!o) return;
    if (frames < 0) frames = 0;
    o->emPrime = frames;
}

/* Per-slot diagnostics: 0 = frames queued, 1 = frames zero-filled through starvation, 2 = times
 * re-cushioned. The first number nobody had. */
CMM_API int cmm_out_emitter_stat(cmm_out* o, int slot, int what)
{
    if (!o || slot < 0 || slot >= CMM_MAX_EMIT_VOICES) return -1;
    struct cmm_emit_slot* sl = &o->emSlots[slot];
    if (!sl->hasRing) return -1;
    switch (what) {
        case 0: return (int)ma_pcm_rb_available_read(&sl->rb);
        case 1: return (int)sl->underFrames;
        case 2: return (int)sl->primes;
        default: return -1;
    }
}

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
    {
        int s;
        for (s = 0; s < CMM_MAX_EMIT_VOICES; s++)
            if (o->emSlots[s].hasRing) ma_pcm_rb_uninit(&o->emSlots[s].rb);
    }
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
