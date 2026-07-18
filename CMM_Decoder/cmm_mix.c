/* cmm_mix.c — native audio MIXER (producer) for ContextMusicManager.
 * ─────────────────────────────────────────────────────────────────────────
 * The consumer side of playback (miniaudio's realtime callback, in cmm_out.c)
 * was already native and GC-immune. This file moves the PRODUCER — the
 * per-sample decode → convert → gain-ramp → sum → write loop — out of managed
 * C# (Harmony/CmmMixer.cs) and into native C, on a native thread.
 *
 * Why: the managed Pump was a C# thread, so Mono's stop-the-world GC (triggered
 * by the GAME's heavy allocation during chunk streaming) suspended it, and the
 * tight output ring underran → an audible stutter. No managed thread can opt out
 * of GC suspension, so the only fix is to not run managed code on the audio path.
 * Now the whole path is native: the GC can't touch it.
 *
 * Split: C# stays the BRAIN (which file, when to switch, fades, resume, loudness,
 * auto-advance — infrequent control calls). This is the HANDS: it owns a voice
 * list + a producer thread and reuses cmm_out (ring+device+consumer gain) and
 * cmm_dc (per-voice rate/channel converters). This is a faithful port of
 * CmmMixer.cs; see that file's comments for the design rationale of each piece.
 *
 * Threading: control calls (from C#) and the producer thread share the voice list
 * under one mutex, exactly as the managed mixer used a single lock. Decoders are
 * opened in C# (once per track, off the audio loop) and handed here via cmm_mix_add
 * together with a format `kind`; this file only READS/seeks/closes them — natively. */

#include "CMM_Decoder.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>

#ifdef _WIN32
  #include <windows.h>
  typedef CRITICAL_SECTION mix_mutex;
  static void mix_mutex_init(mix_mutex* m) { InitializeCriticalSection(m); }
  static void mix_lock(mix_mutex* m)       { EnterCriticalSection(m); }
  static void mix_unlock(mix_mutex* m)     { LeaveCriticalSection(m); }
  static void mix_sleep_ms(int ms)         { Sleep((DWORD)ms); }
  static int  mix_now_ms(void)             { return (int)GetTickCount(); }
#else
  #include <pthread.h>
  #include <unistd.h>
  #include <time.h>
  typedef pthread_mutex_t mix_mutex;
  static void mix_mutex_init(mix_mutex* m) { pthread_mutex_init(m, NULL); }
  static void mix_lock(mix_mutex* m)       { pthread_mutex_lock(m); }
  static void mix_unlock(mix_mutex* m)     { pthread_mutex_unlock(m); }
  static void mix_sleep_ms(int ms)         { usleep((useconds_t)ms * 1000); }
  static int  mix_now_ms(void) {
      struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
      return (int)(ts.tv_sec * 1000 + ts.tv_nsec / 1000000);
  }
#endif

/* ── Per-format decoder dispatch ──────────────────────────────────────────
 * kind matches AudioDecoder.ClassifyReader on the managed side. C# opens the
 * decoder (getting a cmm_decoder* + kind) and hands both to cmm_mix_add; the
 * mixer reads/seeks/closes through these tables — no per-read switch. */
typedef int  (*mix_read_fn)(cmm_decoder*, float*, int);
typedef int  (*mix_seek_fn)(cmm_decoder*, long long);
typedef void (*mix_close_fn)(cmm_decoder*);

enum { KIND_OPUS = 0, KIND_FLAC, KIND_VORBIS, KIND_MP3, KIND_WAV, KIND_AAC, KIND_CONTAINER, KIND_COUNT };

static const mix_read_fn  READ_FN[KIND_COUNT] = {
    cmm_opus_read_float, cmm_flac_read_float, cmm_vorbis_read_float,
    cmm_mp3_read_float,  cmm_wav_read_float,  cmm_aac_read_float, cmm_container_read_float
};
static const mix_seek_fn  SEEK_FN[KIND_COUNT] = {
    cmm_opus_seek, cmm_flac_seek, cmm_vorbis_seek,
    cmm_mp3_seek,  cmm_wav_seek,  cmm_aac_seek, cmm_container_seek
};
static const mix_close_fn CLOSE_FN[KIND_COUNT] = {
    cmm_opus_close, cmm_flac_close, cmm_vorbis_close,
    cmm_mp3_close,  cmm_wav_close,  cmm_aac_close, cmm_container_close
};

/* ── Tuning (mirrors CmmMixer.cs consts) ──────────────────────────────────*/
#define BUS_MAXFRAMES        2048   /* largest block produced per device write */
#define REFILL_FRAMES        2048   /* per-iteration input top-up for a converted voice */
#define MAX_VOICES              4   /* out+in crossfade needs 2; a few overlaps need 3 */
#define MAX_VOICES_HARD        12   /* array headroom: 4 music (+ pre-evict append) + up to
                                     * CMM_MAX_EMIT_VOICES stations × 2 (advance crossfade overlap) */
#define CALM_MS              3000
#define FLUSH_GRACE_MS        750
#define STINGER_RESTORE_SEC  0.6f
#define MAX_EMIT_PTS  CMM_MAX_EMIT_PTS  /* shared with cmm_out (the consumer-side spatializer) */
#define EMIT_MAX_GAIN        1.4f  /* cap on the summed spatial gain (anti-clip among many speakers) */

typedef struct mix_voice {
    long long id;
    cmm_decoder* dec;
    mix_read_fn  readfn;
    mix_seek_fn  seekfn;
    mix_close_fn closefn;
    int   srcRate, srcChannels;
    long long totalFrames;      /* <=0 = unknown (e.g. raw ADTS) */
    long long framesRead;       /* source frames consumed (≈ playhead) */
    cmm_dc* conv;               /* NULL when native format == bus */
    float* inBuf;               /* converter input scratch (srcChannels); NULL when no converter */
    int    inFill;              /* input frames buffered in inBuf (carried across ticks) */
    float  gain, targetGain, gainStep;              /* crossfade gain ramp */
    float  trackGain, trackGainTarget, trackGainStep; /* per-track loudness gain ramp */
    int    loop;
    int    ended;               /* reader hit EOF and isn't looping */
    int    isStinger;           /* PlayOnce overlay: restore ducked beds when it retires */
    int    ducked;              /* this voice was DUCKED (not killed) under a stinger */
    int    isEmitter;           /* independent positional layer: exempt from the music crossfade + eviction */
    int    emitSlot;            /* station render slot (0..CMM_MAX_EMIT_VOICES-1); voices sharing a slot sum */
    float  emitPts[MAX_EMIT_PTS * 3]; /* emitter: world positions of every speaker playing this station */
    float  emitPtGain[MAX_EMIT_PTS];  /* emitter: per-speaker gain multiplier (occlusion: 1=clear, <1=muffled) */
    int    emitPtFlags[MAX_EMIT_PTS]; /* emitter (v34): per-point render flags (bit0 = ham class) */
    int    emitPtCount;         /* number of active positions in emitPts */
    float  emitMinDist, emitMaxDist;  /* emitter: full volume within min; silent beyond max */
} mix_voice;

/* ── Mixer singleton state (the managed CmmMixer was a singleton too) ──────*/
static mix_mutex g_lock;
static int       g_started;
static volatile int g_running;

static mix_voice* g_voices[MAX_VOICES_HARD];
static int        g_voiceCount;
static long long  g_nextId;      /* 0 reserved for "none" */

static cmm_out* g_out;
static int      g_busRate = 48000, g_busChannels = 2;   /* bus is fixed stereo */
static int      g_closeWhenIdle;
static volatile int g_reinitRequested;
/* Headless mode (offline stress harness): the output "device" is a ring with NO
 * ma_device — the caller drains it via cmm_mix_pump_out. Everything else (producer
 * thread, voices, crossfades, retire/free churn) runs exactly as in-game, so the
 * mixer's native lifecycle is exercised at compressed speed for leak hunting. */
static int      g_headless;

static volatile float g_masterVol   = 1.0f;
/* Emitter (world-radio) master — DELIBERATELY SEPARATE from g_masterVol. The music master folds in
 * VolumeModifier and the current context's volume; the diegetic radio must NOT ride those (a quiet
 * music context shouldn't quiet the radio). Managed sets this to the game's music SLIDER × the Radio
 * context's own volume, so the radio scales only to the in-game slider (and its context level). */
static volatile float g_emMasterVol = 1.0f;
static volatile int   g_deep;        /* DeepBuffer (loading) */
static volatile int   g_extra;       /* ExtraCushion (post-spawn grace) — excluded from falling-edge flush */

/* Listener basis for emitter spatialization (world pos + forward + right unit vectors, straight
 * from the camera). Lock-free like g_masterVol: written by cmm_mix_set_listener from the control
 * thread, read by the producer per render block. A torn read (mid-update) at most yields one
 * block's slightly-off pan — inaudible. Right defaults to +X so a never-set listener still pans. */
static volatile float g_lisPos[3];
static volatile float g_lisFwd[3];
static volatile float g_lisRight[3] = { 1.0f, 0.0f, 0.0f };

/* Live emitter spatial-curve tuning (see cmm_mix_set_emitter_tuning). Lock-free like the listener;
 * pushed from config, relayed to the output spatializer each render block. Defaults match the shipped
 * settings so the first blocks before managed pushes are sane. */
static volatile float g_emMinDist     = 5.0f;
static volatile float g_emMaxDist     = 40.0f;
static volatile float g_emFalloffExp  = 1.5f;
static volatile float g_emMuffle      = 0.7f;
static volatile float g_emStereoWidth = 0.85f;

/* Live emitter reverb (room reflection) params, from the managed room probe. v31: the CARRY send
 * level + air-route depth are PER-SLOT (each station's seal/route differs); the room shape is shared. */
static volatile float g_emRevWet[CMM_MAX_EMIT_VOICES];    /* per-slot carry-send level (0 = off) */
static volatile float g_emRevDepth[CMM_MAX_EMIT_VOICES];  /* per-slot air-route blocks (v27 depth grading) */
static volatile float g_emRevRoom = 0.5f;
static volatile float g_emRevDamp = 0.5f;
static volatile float g_emRevRangeMult = 1.0f;   /* wet range = dry range × this (≥1) */
static volatile float g_emRevPan = 0.0f;         /* 0 = diffuse reverb, 1 = panned to source bearing */
static volatile float g_emRevDepthAmt = 0.0f;    /* 0..1: how strongly depth grades the wet (0 = flat) */
static volatile float g_emRevLocWet = 0.0f;      /* LOCAL bus level (v28): ear-room wetness × master */

/* Live radio-coloration ("through a speaker") params, from config (v26). All 0 = clean bypass.
 * Defaults match the shipped settings (coloration parked/disabled) so the first blocks before
 * managed pushes are sane. */
static volatile float g_emColDrive  = 0.0f;   /* soft-clip amount */
static volatile float g_emColStatic = 0.0f;   /* static-hiss bed level */
static volatile float g_emColVoice  = 0.0f;   /* band-pass "small speaker" voicing blend */
/* v34: the HAM class's own coloration set (per-point flags pick the set; managed defaults keep
 * hams always slightly distorted). */
static volatile float g_emColHamDrive  = 0.0f;
static volatile float g_emColHamStatic = 0.0f;
static volatile float g_emColHamVoice  = 0.0f;

/* ── Radio UI SFX (v35): tiny synthesized one-shots — tuning static, power on/off clicks ──
 * No files, no decoders: a click is a filtered noise ping over a low thump, the tuning
 * static is enveloped band-passed noise. Rendered by the producer into the MUSIC bus right
 * after the voice sum, so they ride the same master as the radio voice they belong to —
 * but OUTSIDE the voice list, so the music crossfade / stinger duck / eviction machinery
 * never sees them (an add can't fade a click out, a click can't steal a voice slot).
 * Control thread arms a slot under g_lock; the producer renders under the same lock. */
#define SFX_KIND_STATIC    1
#define SFX_KIND_CLICK_ON  2
#define SFX_KIND_CLICK_OFF 3
#define SFX_MAX 4
typedef struct {
    int kind;              /* 0 = idle slot */
    int pos, len;          /* frames rendered / total */
    int atkLen, relLen;    /* static envelope ramps (frames) */
    float gain;
    unsigned rng;          /* per-voice noise LCG */
    float lo, hi;          /* one-pole filter states (band-pass pair / tone cap) */
    float kLo, kHi;        /* their coefficients */
    float ping, pingK;     /* click: bright noise ping level + per-frame decay */
    float thump, thumpK;   /* click: low thud level + per-frame decay */
    /* v39 — dial-detent character: a BODY RESONATOR (damped two-pole ring at BodyHz — the knock
     * of the dial's shell) plus an optional SECOND micro-impact ~12ms after the first (the
     * detent's spring release then the stop landing — what reads as "mechanical"). */
    float pingInit, thumpInit; /* re-kick levels for the second strike */
    float body;                /* resonator mix 0..1 (0 = the pre-v39 click) */
    float rc1, rc2;            /* resonator coefficients (ring pitch + decay) */
    float ry1, ry2;            /* resonator state */
    int   strike2At;           /* frame of the second micro-impact (0 = single strike) */
    float strike2Lvl;          /* its level relative to the first */
    /* v37 — static CRACKLE: a sample-and-hold amplitude sputter (random level held for a random
     * 3..30ms, slewed) that turns the smooth hiss into radio-tuning sputter. Depth 0 = smooth. */
    float crackle;         /* modulation depth 0..1 (captured from the tone globals per ping) */
    int   modHold;         /* frames until the next random level draw */
    float modCur;          /* current drawn level target (biased toward dips) */
    float modLev;          /* slewed applied level (anti-click) */
} sfx_voice;
static sfx_voice g_sfx[SFX_MAX];
static unsigned  g_sfxSeed = 0x9E3779B9u;
/* v37 — live static TONE (from settings.xml via cmm_mix_set_static_tone; captured onto the voice
 * at every ping, so keepalive extension follows a hot-reloaded config mid-hash). Defaults mirror
 * the managed defaults so the first pings before a push are sane. */
static volatile float g_sfxStaticLoHz    = 350.0f;
static volatile float g_sfxStaticHiHz    = 2000.0f;
static volatile float g_sfxStaticCrackle = 0.8f;
/* v38 — live click TONE, per kind ([0] = power-on, [1] = power-off): tone cap Hz (bright tick ↔
 * dull thud), SNAP 0..1 (the bright-noise-ping vs low-thump balance: 1 = all snap, 0 = all
 * thud), and length ms (the thump's decay constant; the ping decays 4× faster and the voice
 * runs ~3.3× this). Defaults reproduce the v35 hardcoded clicks. */
static volatile float g_sfxClickHz[2]    = { 3200.0f, 1300.0f };
static volatile float g_sfxClickSnap[2]  = { 0.7f, 0.45f };
static volatile float g_sfxClickLenMs[2] = { 18.0f, 28.0f };
/* v39 — detent character: body-knock mix / its ring pitch / second-strike level (0 = single). */
static volatile float g_sfxClickBody[2]   = { 0.4f, 0.4f };
static volatile float g_sfxClickBodyHz[2] = { 1000.0f, 750.0f };
static volatile float g_sfxClickDouble[2] = { 0.35f, 0.2f };

/* adaptive ring state (see cmm_out.c / ComputeFillTargetLocked) */
static int g_tightFill, g_maxFill, g_lowWater, g_targetFill;
static int g_lastStarveMs, g_flushUntilMs;
static int g_prevDeep;
static float g_outGainTarget = 1.0f;
/* v33: while set, cmm_mix_add's output-gain revive is suppressed — the SP pause duck owns
 * the gain and a mid-pause voice add (music EOF advance, radio/emitter start) must start
 * SILENT under it instead of snapping the whole device back to full. */
static int g_outGainHold = 0;

/* Live buffer-health stats for the debug HUD (producer-updated, read+reset via
 * cmm_mix_get_output_stats). g_minQueued = lowest queued depth seen since the last read;
 * g_underruns = cumulative producer-observed ring-empty events during active playback. */
static volatile int g_minQueued = 0x7fffffff;
static volatile int g_underruns;
static int g_wasEmpty;

/* pinned scratch (producer thread only, so no lock needed for these) */
static float* g_bus;
static float* g_voiceStereo;
static float* g_convTmp;
static float* g_emitterMono;   /* per-SLOT dry mono for the consumer-side spatializer:
                                * CMM_MAX_EMIT_VOICES contiguous BUS_MAXFRAMES-float lanes */

/* ── helpers ──────────────────────────────────────────────────────────────*/
static float compute_step(float from, float to, float seconds) {
    if (seconds <= 0.0f) return 0.0f;
    float frames = seconds * (float)g_busRate;
    if (frames < 1.0f) frames = 1.0f;
    return (to - from) / frames;
}

static void retire_voice(mix_voice* v) {
    if (v->dec && v->closefn) v->closefn(v->dec);
    v->dec = NULL;
    if (v->conv) cmm_dc_close(v->conv);
    v->conv = NULL;
    if (v->inBuf) free(v->inBuf);
    v->inBuf = NULL;
}

static void remove_at(int i) {
    int j;
    for (j = i; j < g_voiceCount - 1; j++) g_voices[j] = g_voices[j + 1];
    g_voiceCount--;
}

/* Read wantFrames (of `ch` channels) into buf at frame offset offsetFrames,
 * restarting at 0 on EOF when the voice loops. Returns frames read; advances
 * the playhead. Mirrors CmmMixer.ReadFramesLoop. */
static int read_frames_loop(mix_voice* v, float* buf, int offsetFrames, int wantFrames, int ch) {
    int got = 0;
    while (got < wantFrames) {
        int r = v->readfn(v->dec, buf + (size_t)(offsetFrames + got) * ch, wantFrames - got);
        if (r <= 0) {
            if (v->loop && !v->ended) {
                if (v->seekfn(v->dec, 0) != 0) { v->ended = 1; break; }
                continue;
            }
            v->ended = 1;
            break;
        }
        got += r;
    }
    v->framesRead += got;
    return got;
}

/* Voice already at bus format: read straight into the stereo scratch. */
static int produce_direct(mix_voice* v, int n) {
    int frames = read_frames_loop(v, g_voiceStereo, 0, n, g_busChannels);
    if (frames < n)
        memset(g_voiceStereo + (size_t)frames * g_busChannels, 0,
               (size_t)(n - frames) * g_busChannels * sizeof(float));
    return frames;
}

/* Voice at some other rate/channels: pull input frames and run them through the
 * converter until n bus-stereo frames are produced (or it ends + drains). Leftover
 * unconsumed input is compacted to the front of inBuf. Mirrors ProduceStereoConv. */
static int produce_conv(mix_voice* v, int n) {
    int produced = 0, guard = 0;
    while (produced < n) {
        if (v->inFill < REFILL_FRAMES && !v->ended)
            v->inFill += read_frames_loop(v, v->inBuf, v->inFill, REFILL_FRAMES - v->inFill, v->srcChannels);

        long long inC = 0, outW = 0;
        cmm_dc_process(v->conv, v->inBuf, v->inFill, &inC,
                       g_convTmp, n - produced, &outW);

        if (outW > 0)
            memcpy(g_voiceStereo + (size_t)produced * g_busChannels, g_convTmp,
                   (size_t)outW * g_busChannels * sizeof(float));
        produced += (int)outW;

        if (inC > 0) {
            int rem = v->inFill - (int)inC;
            if (rem > 0)
                memmove(v->inBuf, v->inBuf + (size_t)inC * v->srcChannels,
                        (size_t)rem * v->srcChannels * sizeof(float));
            v->inFill = rem;
        }

        if (v->ended && v->inFill == 0 && outW == 0) break;
        if (outW == 0 && inC == 0) { if (++guard > 8) break; } else guard = 0;
    }
    if (produced < n)
        memset(g_voiceStereo + (size_t)produced * g_busChannels, 0,
               (size_t)(n - produced) * g_busChannels * sizeof(float));
    return produced;
}

/* Produce n bus frames from v, apply its gain ramps, ADD into the bus. Returns 0
 * when the voice should be retired. Mirrors CmmMixer.RenderVoiceAdd (bus is stereo). */
static int render_voice_add(mix_voice* v, int n) {
    int frames = (v->conv == NULL) ? produce_direct(v, n) : produce_conv(v, n);
    /* Emitter voices are NOT spatialized here: they produce a DRY MONO signal (× fade/track gain)
     * into g_emitterMono, and the OUTPUT callback (cmm_out) applies the pan/distance/occlusion from
     * the freshest listener state, so head-turn panning is heard in ~10ms, not behind the ring
     * cushion. Music voices sum into the stereo bus exactly as before. */
    int emit = v->isEmitter;
    int f;

    for (f = 0; f < frames; f++) {
        if (v->gainStep != 0.0f) {
            v->gain += v->gainStep;
            if ((v->gainStep > 0.0f && v->gain >= v->targetGain) ||
                (v->gainStep < 0.0f && v->gain <= v->targetGain)) { v->gain = v->targetGain; v->gainStep = 0.0f; }
        }
        if (v->trackGainStep != 0.0f) {
            v->trackGain += v->trackGainStep;
            if ((v->trackGainStep > 0.0f && v->trackGain >= v->trackGainTarget) ||
                (v->trackGainStep < 0.0f && v->trackGain <= v->trackGainTarget)) { v->trackGain = v->trackGainTarget; v->trackGainStep = 0.0f; }
        }
        int o = f * g_busChannels;
        float g = v->gain * v->trackGain;
        if (emit) {
            /* Dry mono into this voice's SLOT lane (accumulated: two voices sharing a slot during
             * a track-advance crossfade sum; different stations land in different lanes). */
            g_emitterMono[(size_t)v->emitSlot * BUS_MAXFRAMES + f] +=
                0.5f * (g_voiceStereo[o] + g_voiceStereo[o + 1]) * g;
        } else {
            g_bus[o]     += g_voiceStereo[o]     * g;
            g_bus[o + 1] += g_voiceStereo[o + 1] * g;
        }
    }

    /* Advance the ramp across any frames this block couldn't produce (loop reseek /
     * converter priming / starved voice) so a fading-out voice still retires on time. */
    int silent = n - frames;
    if (silent > 0 && v->gainStep != 0.0f) {
        v->gain += v->gainStep * silent;
        if ((v->gainStep > 0.0f && v->gain >= v->targetGain) ||
            (v->gainStep < 0.0f && v->gain <= v->targetGain)) { v->gain = v->targetGain; v->gainStep = 0.0f; }
    }
    if (silent > 0 && v->trackGainStep != 0.0f) {
        v->trackGain += v->trackGainStep * silent;
        if ((v->trackGainStep > 0.0f && v->trackGain >= v->trackGainTarget) ||
            (v->trackGainStep < 0.0f && v->trackGain <= v->trackGainTarget)) { v->trackGain = v->trackGainTarget; v->trackGainStep = 0.0f; }
    }

    if (v->ended && frames < n) return 0;                 /* finished + drained */
    if (v->targetGain == 0.0f && v->gain <= 0.0f) return 0; /* crossfaded out */
    return 1;
}

/* Restore voices a stinger overlay ducked, back to full. Caller holds the lock. */
static void restore_ducked_locked(void) {
    int i;
    for (i = 0; i < g_voiceCount; i++) {
        mix_voice* v = g_voices[i];
        if (!v->ducked) continue;
        v->ducked = 0;
        v->targetGain = 1.0f;
        v->gainStep = compute_step(v->gain, 1.0f, STINGER_RESTORE_SEC);
    }
}

/* Decide how many frames to keep queued this tick. Caller holds the lock. */
static int compute_fill_target_locked(int queued) {
    int now = mix_now_ms();
    if (g_deep || g_extra)
        g_targetFill = g_maxFill;                     /* hold deep (load, or post-spawn grace) */
    else if (now - g_flushUntilMs < 0)
        g_targetFill = g_tightFill;                   /* just flushed — refill tight */
    else if (queued < g_lowWater) {
        g_targetFill = g_maxFill;                     /* starving — bank cushion to the ceiling */
        g_lastStarveMs = now;
    } else if (now - g_lastStarveMs > CALM_MS)
        g_targetFill = g_tightFill;                   /* calm — ease back (drains naturally) */
    return g_targetFill;
}

/* Snap the output cushion tight (discard queued frames). Caller holds the lock. */
static void flush_tight_locked(void) {
    if (!g_out) return;
    cmm_out_reset(g_out);
    g_targetFill = g_tightFill;
    g_flushUntilMs = mix_now_ms() + FLUSH_GRACE_MS;
}

/* Open (or reopen) the output device. Ring is allocated at the MAX depth; the live
 * cushion floats between tight and max. Caller holds the lock. */
static void open_device_locked(void) {
    g_lowWater = g_tightFill / 2;
    if (g_lowWater < g_busRate / 50) g_lowWater = g_busRate / 50;   /* >=20ms */
    g_targetFill = g_tightFill;
    g_flushUntilMs = mix_now_ms() + FLUSH_GRACE_MS;
    g_prevDeep = g_deep;
    g_outGainTarget = 1.0f;
    int err = 0;
    g_out = g_headless ? cmm_out_open_null(g_busRate, g_busChannels, g_maxFill, &err)
                       : cmm_out_open(g_busRate, g_busChannels, g_maxFill, &err);
}

static void ensure_device_locked(void) {
    if (g_out == NULL) open_device_locked();
}

static void teardown_device_locked(void) {
    int i;
    for (i = 0; i < g_voiceCount; i++) { retire_voice(g_voices[i]); free(g_voices[i]); }
    g_voiceCount = 0;
    memset(g_sfx, 0, sizeof g_sfx);   /* a half-rendered click must not resume into the NEXT device's first block */
    if (g_out) { cmm_out_close(g_out); g_out = NULL; }
}

/* Render every live SFX voice additively into g_bus (stereo, `n` frames). Producer thread,
 * under g_lock (the slots are armed from the control thread under the same lock). Mono synth
 * duplicated to both channels; the music master + clip guard downstream apply as to any voice. */
static void render_sfx_add(int n) {
    int s, f;
    for (s = 0; s < SFX_MAX; s++) {
        sfx_voice* v = &g_sfx[s];
        if (!v->kind) continue;
        for (f = 0; f < n && v->pos < v->len; f++, v->pos++) {
            v->rng = v->rng * 1664525u + 1013904223u;
            float noise = (float)(int)v->rng * (1.0f / 2147483648.0f);
            float x;
            if (v->kind == SFX_KIND_STATIC) {
                /* Band-passed noise (the same one-pole pair as the radio coloration voicing)
                 * under a linear attack/release envelope — the dial-between-stations hash. */
                v->lo += v->kLo * (noise - v->lo);
                float bp = noise - v->lo;
                v->hi += v->kHi * (bp - v->hi);
                x = v->hi * 0.9f;
                /* CRACKLE (v37): sample-and-hold amplitude sputter. A random level (squared —
                 * biased toward dips) holds for a random 3..30ms, slewed ~1ms so it never
                 * clicks; depth blends it in. 0 = the smooth hiss, 1 = full radio sputter. */
                if (v->crackle > 0.001f) {
                    if (--v->modHold <= 0) {
                        v->rng = v->rng * 1664525u + 1013904223u;
                        float u = (float)(v->rng >> 8) * (1.0f / 16777216.0f);
                        v->modCur = u * u;
                        v->rng = v->rng * 1664525u + 1013904223u;
                        float uh = (float)(v->rng >> 8) * (1.0f / 16777216.0f);
                        v->modHold = (int)((float)g_busRate * (0.003f + 0.027f * uh));
                        if (v->modHold < 1) v->modHold = 1;
                    }
                    v->modLev += (v->modCur - v->modLev) * 0.02f;
                    x *= 1.0f - v->crackle * (1.0f - v->modLev);
                }
                float env = 1.0f;
                if (v->pos < v->atkLen) env = (float)v->pos / (float)v->atkLen;
                int rem = v->len - v->pos;
                if (rem < v->relLen) { float e = (float)rem / (float)v->relLen; if (e < env) env = e; }
                x *= env;
            } else {
                /* Power click (v39: a dial detent): up to TWO micro-impacts — the spring release
                 * and, ~12ms later, the stop landing — each firing a bright noise ping, a low
                 * thump, and a knock into the BODY RESONATOR (damped two-pole ring at BodyHz,
                 * the dial's shell). Everything tone-capped by one LPF. Body/Double at 0 = the
                 * plain ping+thump click. */
                float excite = 0.0f;
                if (v->pos == 0) excite = 1.0f;
                else if (v->strike2At && v->pos == v->strike2At) {
                    excite = v->strike2Lvl;
                    v->ping  += v->pingInit  * v->strike2Lvl;
                    v->thump += v->thumpInit * v->strike2Lvl;
                }
                float ry = v->rc1 * v->ry1 + v->rc2 * v->ry2 + excite;
                v->ry2 = v->ry1; v->ry1 = ry;
                x = noise * v->ping + v->thump + ry * v->body;
                v->ping  *= v->pingK;
                v->thump *= v->thumpK;
                v->hi += v->kHi * (x - v->hi);
                x = v->hi;
            }
            x *= v->gain;
            g_bus[f * 2]     += x;
            g_bus[f * 2 + 1] += x;
        }
        if (v->pos >= v->len) v->kind = 0;
    }
}

/* ── Producer thread ──────────────────────────────────────────────────────*/
static void pump_body(void) {
    while (g_running) {
        cmm_out* dev; int freeFrames = 0, queued = 0, target = 0;
        int closeIdle = 0;

        mix_lock(&g_lock);
        if (g_reinitRequested) {
            g_reinitRequested = 0;
            if (g_out) { cmm_out_close(g_out); open_device_locked(); }
        }
        dev = g_out;
        if (dev) {
            int deep = g_deep;
            /* falling-edge flush keys on DeepBuffer ONLY (not ExtraCushion): leaving the
             * load window coincides with the spawn context change, so discarding is safe;
             * the post-spawn grace must instead drain naturally (no track teleport). */
            if (g_prevDeep && !deep && cmm_out_queued(dev) > g_tightFill * 2) flush_tight_locked();
            g_prevDeep = deep;
            queued = cmm_out_queued(dev);
            freeFrames = cmm_out_avail(dev);
            target = compute_fill_target_locked(queued);
            /* Buffer-health watermark for the HUD: only while actually producing (voices>0)
             * and outside the intentional post-flush empty window, so a context-change flush
             * isn't miscounted as an underrun. */
            if (g_voiceCount > 0 && (mix_now_ms() - g_flushUntilMs) >= 0) {
                if (queued < g_minQueued) g_minQueued = queued;
                if (queued == 0 && !g_wasEmpty) g_underruns++;   /* falling edge into empty */
                g_wasEmpty = (queued == 0);
            } else {
                g_wasEmpty = 0;
            }
        }
        mix_unlock(&g_lock);

        if (!dev) { mix_sleep_ms(5); continue; }
        if (queued >= target || freeFrames < 64) { mix_sleep_ms(2); continue; }

        int n = target - queued;
        if (n > freeFrames)    n = freeFrames;
        if (n > BUS_MAXFRAMES) n = BUS_MAXFRAMES;
        if (n < 1) { mix_sleep_ms(2); continue; }

        memset(g_bus, 0, (size_t)n * g_busChannels * sizeof(float));
        memset(g_emitterMono, 0, (size_t)CMM_MAX_EMIT_VOICES * BUS_MAXFRAMES * sizeof(float));

        int emActive[CMM_MAX_EMIT_VOICES];   /* per slot: an emitter voice produced this block (write its
                                              * mono; a retiring voice's last frames count) */
        {
            int s;
            for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) emActive[s] = 0;
        }
        mix_lock(&g_lock);
        {
            int i;
            for (i = 0; i < g_voiceCount; i++)
                if (g_voices[i]->isEmitter) emActive[g_voices[i]->emitSlot] = 1;
            for (i = g_voiceCount - 1; i >= 0; i--) {
                if (!render_voice_add(g_voices[i], n)) {
                    int wasStinger = g_voices[i]->isStinger;
                    retire_voice(g_voices[i]);
                    free(g_voices[i]);
                    remove_at(i);
                    if (wasStinger) restore_ducked_locked();
                }
            }

            /* Radio UI SFX (v35) sum in after the voices — same bus, same master, but never
             * part of the crossfade/duck/eviction machinery above. */
            render_sfx_add(n);

            /* Relay each slot's live spatial state to the output for consumer-side spatialization.
             * Per slot, pick its newest emitter voice (normally one; briefly two during a track-
             * advance overlap — the newer voice carries the fresher point set). A slot with no
             * voice relays count 0 (silent). dev is valid here (we hold the lock and any
             * closeWhenIdle teardown runs below). */
            if (dev) {
                int s;
                float lp[3] = { g_lisPos[0], g_lisPos[1], g_lisPos[2] };
                float lr[3] = { g_lisRight[0], g_lisRight[1], g_lisRight[2] };
                float wets[CMM_MAX_EMIT_VOICES], depths[CMM_MAX_EMIT_VOICES];
                int any = 0;
                for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
                    mix_voice* em = NULL;
                    for (i = 0; i < g_voiceCount; i++)
                        if (g_voices[i]->isEmitter && g_voices[i]->emitSlot == s) em = g_voices[i];
                    if (em) {
                        any = 1;
                        /* min/max come from the LIVE tuning globals (not the voice's baked-in values)
                         * so range + plateau are hot-tunable; falloff + muffle likewise. */
                        cmm_out_set_emitter_spatial(dev, s, lp, lr, em->emitPts, em->emitPtGain,
                                                    em->emitPtFlags, em->emitPtCount,
                                                    g_emMinDist, g_emMaxDist,
                                                    g_emFalloffExp, g_emMuffle, g_emStereoWidth);
                        wets[s] = g_emRevWet[s]; depths[s] = g_emRevDepth[s];
                    } else {
                        cmm_out_set_emitter_spatial(dev, s, NULL, NULL, NULL, NULL, NULL, 0,
                                                    g_emMinDist, g_emMaxDist,
                                                    g_emFalloffExp, g_emMuffle, g_emStereoWidth);
                        wets[s] = 0.0f; depths[s] = 0.0f;
                    }
                }
                if (any) {
                    cmm_out_set_emitter_reverb(dev, wets, depths, g_emRevRoom, g_emRevDamp,
                                               g_emRevRangeMult, g_emRevPan, g_emRevDepthAmt, g_emRevLocWet);
                    cmm_out_set_emitter_color(dev, g_emColDrive, g_emColStatic, g_emColVoice,
                                              g_emColHamDrive, g_emColHamStatic, g_emColHamVoice);
                } else {
                    cmm_out_set_emitter_reverb(dev, NULL, NULL, 0.5f, 0.5f, 1.0f, 0.0f, 0.0f, 0.0f);
                    cmm_out_set_emitter_color(dev, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f);
                }
            }
        }
        if (g_closeWhenIdle && g_voiceCount == 0) {
            teardown_device_locked();
            g_closeWhenIdle = 0;
            closeIdle = 1;
        }
        mix_unlock(&g_lock);

        if (closeIdle) continue;   /* device gone; loop picks up dev==NULL */

        /* Master volume + hard clip-guard (raw f32 device wraps past 0dBFS). */
        float master = g_masterVol;
        int total = n * g_busChannels, i;
        for (i = 0; i < total; i++) {
            float s = g_bus[i] * master;
            g_bus[i] = s > 1.0f ? 1.0f : (s < -1.0f ? -1.0f : s);
        }

        int pushed = 0;
        while (pushed < n) {
            int w = cmm_out_write(dev, g_bus + (size_t)pushed * g_busChannels, n - pushed);
            pushed += w;
            if (pushed < n) mix_sleep_ms(2);
        }

        /* Emitter DRY mono: apply master (spatialization + clip happen in the output callback) and
         * enqueue each SLOT's lane to its ring — ONLY slots whose station produced this block, so an
         * idle slot's ring stays empty and that station's audio onset isn't delayed behind a cushion
         * of zeros. No clip here (the callback clips the summed output). */
        {
            float emMaster = g_emMasterVol;   /* the radios' OWN master, not the music master */
            int s;
            for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
                if (!emActive[s]) continue;
                float* lane = g_emitterMono + (size_t)s * BUS_MAXFRAMES;
                for (i = 0; i < n; i++) lane[i] *= emMaster;
                cmm_out_write_emitter(dev, s, lane, n);
            }
        }
    }
}

#ifdef _WIN32
static DWORD WINAPI mix_thread_proc(LPVOID arg) { (void)arg; pump_body(); return 0; }
#else
static void* mix_thread_proc(void* arg) { (void)arg; pump_body(); return NULL; }
#endif

static void start_thread(void) {
#ifdef _WIN32
    HANDLE h = CreateThread(NULL, 0, mix_thread_proc, NULL, 0, NULL);
    if (h) { SetThreadPriority(h, THREAD_PRIORITY_ABOVE_NORMAL); CloseHandle(h); }
#else
    pthread_t t;
    if (pthread_create(&t, NULL, mix_thread_proc, NULL) == 0) pthread_detach(t);
#endif
}

/* ══ Public control ABI (called from the C# control layer) ═════════════════*/

/* Create the mixer + start the producer thread. Idempotent. tightFrames/maxFrames
 * are the tight/deep cushion depths in FRAMES (C# computes them from config).
 * headless != 0 opens a NULL output (no ma_device; drain via cmm_mix_pump_out) for
 * the offline stress harness. The device opens lazily on the first cmm_mix_add.
 * Returns 0. */
CMM_API int cmm_mix_start_ex(int busRate, int busChannels, int tightFrames, int maxFrames,
                             int headless, int* err) {
    if (err) *err = 0;
    if (g_started) return 0;
    g_headless    = headless ? 1 : 0;
    g_busRate     = busRate > 0 ? busRate : 48000;
    g_busChannels = busChannels > 0 ? busChannels : 2;
    g_tightFill   = tightFrames > 0 ? tightFrames : g_busRate / 5;   /* ~200ms default */
    g_maxFill     = maxFrames > g_tightFill ? maxFrames : g_tightFill;
    g_masterVol   = 1.0f;
    g_emMasterVol = 1.0f;
    g_outGainTarget = 1.0f;
    g_outGainHold = 0;
    g_minQueued   = 0x7fffffff;
    g_underruns   = 0;
    g_wasEmpty    = 0;

    g_bus         = (float*)malloc((size_t)BUS_MAXFRAMES * g_busChannels * sizeof(float));
    g_voiceStereo = (float*)malloc((size_t)BUS_MAXFRAMES * g_busChannels * sizeof(float));
    g_convTmp     = (float*)malloc((size_t)BUS_MAXFRAMES * g_busChannels * sizeof(float));
    g_emitterMono = (float*)malloc((size_t)CMM_MAX_EMIT_VOICES * BUS_MAXFRAMES * sizeof(float));
    if (!g_bus || !g_voiceStereo || !g_convTmp || !g_emitterMono) { if (err) *err = -2; return -2; }

    mix_mutex_init(&g_lock);
    g_running = 1;
    g_started = 1;
    start_thread();
    return 0;
}

/* Back-compat entry: start a normal (real-device) mixer. */
CMM_API int cmm_mix_start(int busRate, int busChannels, int tightFrames, int maxFrames, int* err) {
    return cmm_mix_start_ex(busRate, busChannels, tightFrames, maxFrames, 0, err);
}

/* Headless drain: consume up to `frames` from the output ring (a no-op unless the
 * mixer was started with headless != 0). This is the harness's stand-in for the
 * realtime device callback — call it on the sim clock to let the producer keep
 * rendering (voices advance, fade, and retire → the native alloc/free churn cycles)
 * at compressed speed. Returns frames actually drained. */
CMM_API int cmm_mix_pump_out(int frames) {
    int drained;
    mix_lock(&g_lock);
    drained = (g_out && g_headless) ? cmm_out_drain(g_out, frames) : 0;
    mix_unlock(&g_lock);
    return drained;
}

/* Install a track. `dec` (a cmm_decoder* opened by C#) + `kind` name the decoder;
 * srcRate/srcChannels/total describe it. Everything already playing fades out (or
 * DUCKS, for a stinger) over `fade`. loop restarts at EOF; startFrame seeks before
 * play (resume); trackGain is the per-track loudness gain; duckOthersTo>=0 makes
 * this a PlayOnce stinger overlay; snapTight flushes a deep cushion on a context
 * change. Takes ownership of `dec`. Returns a voice id (0 on failure). */
CMM_API long long cmm_mix_add(cmm_decoder* dec, int kind, int srcRate, int srcChannels,
                              long long totalFrames, float fade, int loop, long long startFrame,
                              float trackGain, float duckOthersTo, int snapTight) {
    if (!dec) return 0;
    if (kind < 0 || kind >= KIND_COUNT) return 0;   /* leak-safe: unknown kind, caller keeps dec */

    mix_voice* v = (mix_voice*)calloc(1, sizeof(mix_voice));
    if (!v) { CLOSE_FN[kind](dec); return 0; }
    v->dec = dec; v->readfn = READ_FN[kind]; v->seekfn = SEEK_FN[kind]; v->closefn = CLOSE_FN[kind];
    v->srcRate = srcRate; v->srcChannels = srcChannels; v->totalFrames = totalFrames;
    v->loop = loop ? 1 : 0;
    v->trackGain = trackGain > 0.0f ? trackGain : 1.0f;
    v->trackGainTarget = v->trackGain;
    v->isStinger = duckOthersTo >= 0.0f;

    if (srcRate != g_busRate || srcChannels != g_busChannels) {
        int derr = 0;
        v->conv = cmm_dc_open(srcRate, srcChannels, g_busRate, g_busChannels, &derr);
        if (!v->conv) { v->closefn(dec); free(v); return 0; }
        v->inBuf = (float*)malloc((size_t)REFILL_FRAMES * srcChannels * sizeof(float));
        if (!v->inBuf) { cmm_dc_close(v->conv); v->closefn(dec); free(v); return 0; }
    }

    /* Resume seek (runs on the caller's control thread, off the audio path). */
    if (startFrame > 0) {
        if (totalFrames > 0 && startFrame >= totalFrames) startFrame = 0;
        if (v->seekfn(dec, startFrame) != 0) startFrame = 0;   /* seek failed → from 0 */
    } else startFrame = 0;
    v->framesRead = startFrame;

    mix_lock(&g_lock);
    ensure_device_locked();
    g_closeWhenIdle = 0;

    long long id = ++g_nextId;
    v->id = id;

    /* A transition fade left the output gain ramping toward silence; this is new music
     * that must be audible. Discard the faded queue + restore output gain atomically.
     * EXCEPT under the v33 hold: the pause duck owns the gain — start silent under it. */
    float reviveFrom = (g_out && g_outGainTarget != 1.0f && !g_outGainHold) ? g_outGainTarget : 1.0f;
    if (reviveFrom != 1.0f) {
        cmm_out_reset_gain(g_out, 1.0f);
        g_outGainTarget = 1.0f;
        g_flushUntilMs = mix_now_ms() + FLUSH_GRACE_MS;
    }

    int stinger = duckOthersTo >= 0.0f;
    {
        int i;
        for (i = 0; i < g_voiceCount; i++) {
            mix_voice* ev = g_voices[i];
            if (ev->isEmitter) continue;   /* emitters are an independent layer — a music change never fades them */
            if (reviveFrom != 1.0f) ev->gain *= reviveFrom;   /* keep audible level continuous across revive */
            float to = stinger ? duckOthersTo : 0.0f;
            ev->targetGain = to;
            /* fade<=0 must SNAP: compute_step returns 0 there, and a 0 gainStep freezes the
             * ramp (see mix loop) — the bed would stick at full instead of ducking/fading out.
             * Mirrors the incoming-voice instant path below. */
            if (fade <= 0.0f) { ev->gain = to; ev->gainStep = 0.0f; }
            else              ev->gainStep = compute_step(ev->gain, to, fade);
            ev->ducked     = (stinger && to > 0.0f) ? 1 : 0;
        }
    }

    if (fade <= 0.0f) { v->gain = 1.0f; v->gainStep = 0.0f; }
    else              { v->gain = 0.0f; v->gainStep = compute_step(0.0f, 1.0f, fade); }
    v->targetGain = 1.0f;

    g_voices[g_voiceCount++] = v;

    /* Bound overlap: never accumulate beyond MAX_VOICES MUSIC voices — drop the most-faded
     * extras. Emitter voices are an independent layer: they neither count toward the limit nor
     * are eligible for eviction here (the emitter layer manages its own voices). */
    for (;;) {
        int i, music = 0, q = -1; float low = 1e30f;
        for (i = 0; i < g_voiceCount; i++) {
            if (g_voices[i]->isEmitter) continue;
            music++;
            if (g_voices[i]->id != id && g_voices[i]->gain < low) { low = g_voices[i]->gain; q = i; }
        }
        if (music <= MAX_VOICES || q < 0) break;
        retire_voice(g_voices[q]); free(g_voices[q]); remove_at(q);
    }

    /* Context change: snap a deep cushion tight so the new context is heard promptly.
     * NOT for a same-context track switch (snapTight=0) — flushing would teleport the
     * outgoing track being crossfaded. */
    if (snapTight && g_out && cmm_out_queued(g_out) > g_tightFill * 2) flush_tight_locked();

    mix_unlock(&g_lock);
    return id;
}

/* v16 — add an EMITTER voice: an independent positional layer that plays ALONGSIDE the music.
 * Unlike cmm_mix_add it does NOT fade the other voices out and is exempt from the voice-count
 * eviction — it just appends a voice the emitter layer owns. Takes ownership of `dec` (frees it
 * on retire or on any failure here). v31: `slot` (0..CMM_MAX_EMIT_VOICES-1) pins the voice to a
 * station render slot; out-of-range fails. Returns an emitter voice id (0 on failure). */
CMM_API long long cmm_mix_add_emitter(cmm_decoder* dec, int kind, int srcRate, int srcChannels,
                                      long long totalFrames, float fadeIn, int loop, float trackGain,
                                      float posX, float posY, float posZ, float minDist, float maxDist,
                                      long long startFrame, int slot) {
    if (!dec) return 0;
    if (kind < 0 || kind >= KIND_COUNT) return 0;   /* leak-safe: unknown kind, caller keeps dec */
    if (slot < 0 || slot >= CMM_MAX_EMIT_VOICES) { CLOSE_FN[kind](dec); return 0; }

    mix_voice* v = (mix_voice*)calloc(1, sizeof(mix_voice));
    if (!v) { CLOSE_FN[kind](dec); return 0; }
    v->dec = dec; v->readfn = READ_FN[kind]; v->seekfn = SEEK_FN[kind]; v->closefn = CLOSE_FN[kind];
    v->srcRate = srcRate; v->srcChannels = srcChannels; v->totalFrames = totalFrames;
    v->loop = loop ? 1 : 0;

    /* Continuity seek: start at the station's virtual playhead so rejoining a station resumes mid-song,
     * not from 0. Runs on the caller's control thread, off the audio path (mirrors cmm_mix_add). */
    if (startFrame > 0) {
        if (totalFrames > 0 && startFrame >= totalFrames) startFrame = 0;
        if (v->seekfn(dec, startFrame) != 0) startFrame = 0;   /* seek failed → from 0 */
    } else startFrame = 0;
    v->framesRead = startFrame;
    v->trackGain = trackGain > 0.0f ? trackGain : 1.0f;
    v->trackGainTarget = v->trackGain;
    v->isEmitter = 1;
    v->emitSlot = slot;
    v->emitPts[0] = posX; v->emitPts[1] = posY; v->emitPts[2] = posZ;
    v->emitPtGain[0] = 1.0f;                   /* unoccluded until the layer sends real per-speaker gains */
    v->emitPtCount = 1;                        /* start with the one speaker; the layer adds the rest */
    v->emitMinDist = minDist > 0.0f ? minDist : 1.0f;
    v->emitMaxDist = maxDist > v->emitMinDist ? maxDist : v->emitMinDist + 1.0f;

    if (srcRate != g_busRate || srcChannels != g_busChannels) {
        int derr = 0;
        v->conv = cmm_dc_open(srcRate, srcChannels, g_busRate, g_busChannels, &derr);
        if (!v->conv) { v->closefn(dec); free(v); return 0; }
        v->inBuf = (float*)malloc((size_t)REFILL_FRAMES * srcChannels * sizeof(float));
        if (!v->inBuf) { cmm_dc_close(v->conv); v->closefn(dec); free(v); return 0; }
    }

    mix_lock(&g_lock);
    if (g_voiceCount >= MAX_VOICES_HARD) {   /* no room in the voice array — fail cleanly */
        mix_unlock(&g_lock);
        retire_voice(v); free(v);
        return 0;
    }
    ensure_device_locked();
    g_closeWhenIdle = 0;

    long long id = ++g_nextId;
    v->id = id;
    if (fadeIn <= 0.0f) { v->gain = 1.0f; v->gainStep = 0.0f; }
    else                { v->gain = 0.0f; v->gainStep = compute_step(0.0f, 1.0f, fadeIn); }
    v->targetGain = 1.0f;

    g_voices[g_voiceCount++] = v;
    mix_unlock(&g_lock);
    return id;
}

/* v16 — fade an emitter voice out over `fade` (<=0 snaps) so it retires next tick. A missing or
 * non-emitter id is a no-op (the music path never removes voices this way). */
CMM_API void cmm_mix_remove_emitter(long long id, float fade) {
    int i;
    if (id == 0) return;
    mix_lock(&g_lock);
    for (i = 0; i < g_voiceCount; i++) {
        mix_voice* v = g_voices[i];
        if (v->id != id || !v->isEmitter) continue;
        v->targetGain = 0.0f;
        if (fade <= 0.0f) { v->gain = 0.0f; v->gainStep = 0.0f; }
        else              v->gainStep = compute_step(v->gain, 0.0f, fade);
        break;
    }
    mix_unlock(&g_lock);
}

/* v30 — reset the emitter layer's ACOUSTIC render state (reverb tails, LPF/pan/mix slews, per-slot
 * smooth state) before a NEW station voice starts fresh. Without this a station stopped and restarted
 * within the reverb-tail window (destroy a radio, place another) re-voiced the OLD track's leftover
 * reverb energy at full wet gain under the new voice's fade-in. Not called on a track advance —
 * there the room is unchanged and the tail's continuity is exactly what we want. */
CMM_API void cmm_mix_emitter_reset_acoustics(void) {
    mix_lock(&g_lock);
    if (g_out) cmm_out_emitter_reset(g_out);
    mix_unlock(&g_lock);
}

/* v32 — flush one render slot's queued dry + render state (a DIFFERENT station acquired it). See
 * cmm_out_emitter_flush_slot; not used on a same-station track advance (the tail is continuity). */
CMM_API void cmm_mix_emitter_flush_slot(int slot) {
    mix_lock(&g_lock);
    if (g_out) cmm_out_emitter_flush_slot(g_out, slot);
    mix_unlock(&g_lock);
}

/* v17 — push the listener basis for emitter spatialization. Lock-free (see g_lisPos): the
 * producer reads these per render block. forward/right come straight from the camera transform. */
CMM_API void cmm_mix_set_listener(float px, float py, float pz,
                                  float fx, float fy, float fz,
                                  float rx, float ry, float rz) {
    g_lisPos[0] = px; g_lisPos[1] = py; g_lisPos[2] = pz;
    g_lisFwd[0] = fx; g_lisFwd[1] = fy; g_lisFwd[2] = fz;
    g_lisRight[0] = rx; g_lisRight[1] = ry; g_lisRight[2] = rz;
}

/* v21 — push the live emitter spatial-curve tuning (see header). Lock-free; the producer relays
 * these to the output spatializer each block. Clamped so bad config can't NaN/degenerate the curve. */
CMM_API void cmm_mix_set_emitter_tuning(float minDist, float maxDist, float falloffExp, float muffle,
                                        float stereoWidth) {
    if (minDist < 0.0f) minDist = 0.0f;
    if (maxDist < minDist + 0.1f) maxDist = minDist + 0.1f;   /* keep span positive */
    if (falloffExp < 0.05f) falloffExp = 0.05f; else if (falloffExp > 8.0f) falloffExp = 8.0f;
    if (muffle < 0.0f) muffle = 0.0f; else if (muffle > 1.0f) muffle = 1.0f;
    if (stereoWidth < 0.0f) stereoWidth = 0.0f; else if (stereoWidth > 1.0f) stereoWidth = 1.0f;
    g_emMinDist = minDist; g_emMaxDist = maxDist;
    g_emFalloffExp = falloffExp; g_emMuffle = muffle;
    g_emStereoWidth = stereoWidth;
}

/* Live emitter reverb params from the managed room probe (see EmitterAcoustics.ProbeRoom).
 * v31: wets/depths are PER-SLOT arrays (CMM_MAX_EMIT_VOICES floats; NULL = all 0) — each voiced
 * station's own seal-gated CARRY send level + air-route depth. room = size→decay, damp = HF damping,
 * pan, depthAmt, localWet (LOCAL bus level) describe the LISTENER's room and stay shared. */
CMM_API void cmm_mix_set_emitter_reverb(const float* wets, const float* depths,
                                        float room, float damp, float rangeMult, float pan,
                                        float depthAmt, float localWet) {
    int s;
    if (room < 0.0f) room = 0.0f; else if (room > 1.0f) room = 1.0f;
    if (damp < 0.0f) damp = 0.0f; else if (damp > 1.0f) damp = 1.0f;
    if (rangeMult < 1.0f) rangeMult = 1.0f; else if (rangeMult > 8.0f) rangeMult = 8.0f;
    if (pan  < 0.0f) pan  = 0.0f; else if (pan  > 1.0f) pan  = 1.0f;
    if (depthAmt < 0.0f) depthAmt = 0.0f; else if (depthAmt > 1.0f) depthAmt = 1.0f;
    if (localWet < 0.0f) localWet = 0.0f; else if (localWet > 1.0f) localWet = 1.0f;
    for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
        float w = wets   ? wets[s]   : 0.0f;
        float d = depths ? depths[s] : 0.0f;
        if (w < 0.0f) w = 0.0f; else if (w > 1.0f) w = 1.0f;
        if (d < 0.0f) d = 0.0f; else if (d > 512.0f) d = 512.0f;
        g_emRevWet[s] = w;
        g_emRevDepth[s] = d;
    }
    g_emRevRoom = room; g_emRevDamp = damp; g_emRevRangeMult = rangeMult; g_emRevPan = pan;
    g_emRevDepthAmt = depthAmt;
    g_emRevLocWet = localWet;
}

/* v26/v34 — live radio-coloration params from config (see header): the SPEAKER set and the HAM set
 * (per-point flags pick the set). All 0..1, all 0 = clean bypass. */
CMM_API void cmm_mix_set_emitter_color(float drive, float staticLvl, float voice,
                                       float hamDrive, float hamStatic, float hamVoice) {
    if (drive     < 0.0f) drive     = 0.0f; else if (drive     > 1.0f) drive     = 1.0f;
    if (staticLvl < 0.0f) staticLvl = 0.0f; else if (staticLvl > 1.0f) staticLvl = 1.0f;
    if (voice     < 0.0f) voice     = 0.0f; else if (voice     > 1.0f) voice     = 1.0f;
    if (hamDrive  < 0.0f) hamDrive  = 0.0f; else if (hamDrive  > 1.0f) hamDrive  = 1.0f;
    if (hamStatic < 0.0f) hamStatic = 0.0f; else if (hamStatic > 1.0f) hamStatic = 1.0f;
    if (hamVoice  < 0.0f) hamVoice  = 0.0f; else if (hamVoice  > 1.0f) hamVoice  = 1.0f;
    g_emColDrive = drive; g_emColStatic = staticLvl; g_emColVoice = voice;
    g_emColHamDrive = hamDrive; g_emColHamStatic = hamStatic; g_emColHamVoice = hamVoice;
}

/* v37 — capture the live static-tone globals onto a voice, clamped sane (low cut always below the
 * top cap). Runs at EVERY static ping — arm and keepalive extend — so editing settings.xml while
 * dialing is heard on the very next ping, mid-hash. */
static void sfx_capture_static_tone(sfx_voice* v) {
    float rate = (float)g_busRate;
    float lo = g_sfxStaticLoHz, hi = g_sfxStaticHiHz, cr = g_sfxStaticCrackle;
    if (lo < 50.0f) lo = 50.0f; else if (lo > 4000.0f) lo = 4000.0f;
    if (hi < 400.0f) hi = 400.0f; else if (hi > 12000.0f) hi = 12000.0f;
    if (hi < lo + 100.0f) hi = lo + 100.0f;
    if (cr < 0.0f) cr = 0.0f; else if (cr > 1.0f) cr = 1.0f;
    v->kLo = 1.0f - expf(-6.2831853f * lo / rate);
    v->kHi = 1.0f - expf(-6.2831853f * hi / rate);
    v->crackle = cr;
}

/* v37 — set the live static TONE from config: band edges in Hz (low cut / top cap) + crackle
 * depth 0..1 (0 = smooth hiss, 1 = full sputter). Lock-free volatiles, clamped at capture;
 * takes effect on the next ping, including mid-hash keepalive extensions. */
CMM_API void cmm_mix_set_static_tone(float lowHz, float highHz, float crackle) {
    g_sfxStaticLoHz = lowHz; g_sfxStaticHiHz = highHz; g_sfxStaticCrackle = crackle;
}

/* v38 — capture the live click-tone globals onto a voice (`which`: 0 = on, 1 = off), clamped
 * sane. Snap maps to the ping/thump pair so its extremes stay musical (never fully silent):
 * ping = 0.4 + 0.7·snap, thump = 1.0 − 0.55·snap — the v35 hardcoded clicks sit at snap
 * 0.7 (on) and 0.45 (off). Length is the thump decay; the ping decays 4× faster. */
static void sfx_capture_click_tone(sfx_voice* v, int which) {
    float rate = (float)g_busRate;
    float hz = g_sfxClickHz[which], snap = g_sfxClickSnap[which], ms = g_sfxClickLenMs[which];
    float body = g_sfxClickBody[which], bodyHz = g_sfxClickBodyHz[which], dbl = g_sfxClickDouble[which];
    if (hz < 300.0f) hz = 300.0f; else if (hz > 10000.0f) hz = 10000.0f;
    if (snap < 0.0f) snap = 0.0f; else if (snap > 1.0f) snap = 1.0f;
    if (ms < 5.0f) ms = 5.0f; else if (ms > 120.0f) ms = 120.0f;
    if (body < 0.0f) body = 0.0f; else if (body > 1.0f) body = 1.0f;
    if (bodyHz < 200.0f) bodyHz = 200.0f; else if (bodyHz > 3000.0f) bodyHz = 3000.0f;
    if (dbl < 0.0f) dbl = 0.0f; else if (dbl > 1.0f) dbl = 1.0f;
    float sec = ms * 0.001f;
    v->kHi   = 1.0f - expf(-6.2831853f * hz / rate);
    v->ping  = 0.4f + 0.7f * snap;   v->pingK  = expf(-1.0f / (0.25f * sec * rate));
    v->thump = 1.0f - 0.55f * snap;  v->thumpK = expf(-1.0f / (sec * rate));
    v->pingInit = v->ping; v->thumpInit = v->thump;
    /* Body resonator: damped two-pole ring at BodyHz, decaying at half the thump's constant —
     * the knock rings the shell briefly, it doesn't hum. */
    v->body = body;
    {
        float w = 6.2831853f * bodyHz / rate;
        float R = expf(-1.0f / (0.5f * sec * rate));
        v->rc1 = 2.0f * R * cosf(w);
        v->rc2 = -(R * R);
    }
    v->ry1 = v->ry2 = 0.0f;
    /* Second micro-impact ~12ms after the first — the detent's "cl-ick". */
    v->strike2At  = dbl > 0.001f ? (int)(rate * 0.012f) : 0;
    v->strike2Lvl = dbl;
    v->len = (int)(rate * (sec * 3.3f + (v->strike2At ? 0.012f : 0.0f)));
    if (v->len < 1) v->len = 1;
}

/* v38/v39 — set the live click TONE from config, both kinds at once (see the globals above).
 * Lock-free volatiles, clamped at capture; the next click plays with the new character. */
CMM_API void cmm_mix_set_click_tone(float onHz, float onSnap, float onLenMs,
                                    float onBody, float onBodyHz, float onDouble,
                                    float offHz, float offSnap, float offLenMs,
                                    float offBody, float offBodyHz, float offDouble) {
    g_sfxClickHz[0] = onHz;  g_sfxClickSnap[0] = onSnap;  g_sfxClickLenMs[0] = onLenMs;
    g_sfxClickHz[1] = offHz; g_sfxClickSnap[1] = offSnap; g_sfxClickLenMs[1] = offLenMs;
    g_sfxClickBody[0] = onBody;  g_sfxClickBodyHz[0] = onBodyHz;  g_sfxClickDouble[0] = onDouble;
    g_sfxClickBody[1] = offBody; g_sfxClickBodyHz[1] = offBodyHz; g_sfxClickDouble[1] = offDouble;
}

/* v35 — fire one synthesized radio-UI one-shot (SFX_KIND_*: 1 = tuning static burst, 2 = power-on
 * click, 3 = power-off click) at `gain` (0..1, pre-master). Ensures the device like cmm_mix_add
 * (a power-on click may precede the radio voice's own add). Steals the most-finished slot when all
 * are busy. Does NOT touch the output gain (a click during the SP pause duck stays ducked). */
CMM_API void cmm_mix_play_sfx(int kind, float gain) {
    if (kind < SFX_KIND_STATIC || kind > SFX_KIND_CLICK_OFF) return;
    if (gain <= 0.0f) return;
    if (gain > 1.0f) gain = 1.0f;
    mix_lock(&g_lock);
    if (!g_started) { mix_unlock(&g_lock); return; }
    ensure_device_locked();
    if (!g_out) { mix_unlock(&g_lock); return; }
    /* v36 — the STATIC is KEEPALIVE, not one-shot: a ping while a static is already live EXTENDS
     * the live hash (env stays flat until pings stop, then the release runs) instead of layering
     * a second burst on top — retriggered bursts audibly double-fired whenever a station join
     * outlived one burst. The caller pings at its tick rate; cmm_mix_stop_sfx releases early. */
    if (kind == SFX_KIND_STATIC) {
        int i;
        for (i = 0; i < SFX_MAX; i++) {
            sfx_voice* live = &g_sfx[i];
            if (live->kind != SFX_KIND_STATIC) continue;
            live->len  = live->pos + (int)((float)g_busRate * 0.40f);  /* hold ~0.4s past this ping */
            live->gain = gain;                                          /* follow a hot-reloaded volume */
            sfx_capture_static_tone(live);                              /* tone edits land mid-hash too */
            mix_unlock(&g_lock);
            return;
        }
    }
    {
        int i, pick = 0; float bestProg = -1.0f;
        for (i = 0; i < SFX_MAX; i++) {
            if (!g_sfx[i].kind) { pick = i; break; }
            float prog = g_sfx[i].len > 0 ? (float)g_sfx[i].pos / (float)g_sfx[i].len : 1.0f;
            if (prog > bestProg) { bestProg = prog; pick = i; }
        }
        sfx_voice* v = &g_sfx[pick];
        memset(v, 0, sizeof *v);
        g_sfxSeed = g_sfxSeed * 1664525u + 1013904223u;
        v->rng = g_sfxSeed | 1u;
        v->kind = kind;
        v->gain = gain;
        float rate = (float)g_busRate;
        switch (kind) {
        case SFX_KIND_STATIC:
            /* Band-passed hash (tone + crackle from the live globals — cmm_mix_set_static_tone),
             * 10ms in / 90ms out. KEEPALIVE (v36): each ping extends the live hash ~0.4s (see
             * above), so ping at any rate ≥ ~2 Hz = one seamless "searching" noise that releases
             * shortly after the pings stop (or on cmm_mix_stop_sfx). */
            v->len    = (int)(rate * 0.40f);
            v->atkLen = (int)(rate * 0.010f);
            v->relLen = (int)(rate * 0.090f);
            sfx_capture_static_tone(v);
            v->modLev = 1.0f;   /* crackle modulator starts open (first draw lands within ~30ms) */
            break;
        case SFX_KIND_CLICK_ON:
            /* Power-on click — tone/snap/length from the live globals (cmm_mix_set_click_tone). */
            sfx_capture_click_tone(v, 0);
            break;
        case SFX_KIND_CLICK_OFF:
            /* Power-off clunk — same, its own parameter set. */
            sfx_capture_click_tone(v, 1);
            break;
        }
        if (v->len < 1) v->len = 1;
        if (v->atkLen < 1) v->atkLen = 1;
        if (v->relLen < 1) v->relLen = 1;
    }
    mix_unlock(&g_lock);
}

/* v36 — release every live SFX voice of `kind` NOW: its length shortens to "current position +
 * its release ramp", so a keepalive static ends cleanly (~90ms) the moment its reason ends (the
 * station voice landed) instead of running out its hold over the music. Never extends. */
CMM_API void cmm_mix_stop_sfx(int kind) {
    int i;
    mix_lock(&g_lock);
    for (i = 0; i < SFX_MAX; i++) {
        sfx_voice* v = &g_sfx[i];
        if (v->kind != kind) continue;
        int rel = v->relLen > 0 ? v->relLen : 1;
        if (v->len > v->pos + rel) v->len = v->pos + rel;
    }
    mix_unlock(&g_lock);
}

/* v17 — set a live emitter voice to a SINGLE world position. Missing/non-emitter id = no-op. */
CMM_API void cmm_mix_set_emitter_pos(long long id, float x, float y, float z) {
    int i;
    if (id == 0) return;
    mix_lock(&g_lock);
    for (i = 0; i < g_voiceCount; i++) {
        mix_voice* v = g_voices[i];
        if (v->id != id || !v->isEmitter) continue;
        v->emitPts[0] = x; v->emitPts[1] = y; v->emitPts[2] = z;
        v->emitPtCount = 1;
        break;
    }
    mix_unlock(&g_lock);
}

/* v18/v19/v34 — set the FULL set of speaker positions playing this station (interleaved xyz, `count`
 * points, clamped to MAX_EMIT_PTS) plus a per-speaker gain (occlusion: 1=clear, <1=muffled by walls;
 * `gains` may be NULL for all-clear) and per-speaker render flags (v34: bit0 = ham class — that
 * point's signal gets the HAM coloration set; `flags` may be NULL for all-speaker). The render sums
 * every point's pan+attenuation×gain, so all the speakers are heard at once. count<=0 (or xyz NULL)
 * silences the spatial output until the next update. Missing/non-emitter id = no-op. */
CMM_API void cmm_mix_set_emitter_points(long long id, const float* xyz, const float* gains,
                                        const int* flags, int count) {
    int i, k;
    if (id == 0) return;
    if (count < 0 || xyz == NULL) count = 0;
    if (count > MAX_EMIT_PTS) count = MAX_EMIT_PTS;
    mix_lock(&g_lock);
    for (i = 0; i < g_voiceCount; i++) {
        mix_voice* v = g_voices[i];
        if (v->id != id || !v->isEmitter) continue;
        for (k = 0; k < count * 3; k++) v->emitPts[k] = xyz[k];
        for (k = 0; k < count; k++)     v->emitPtGain[k] = gains ? gains[k] : 1.0f;
        for (k = 0; k < count; k++)     v->emitPtFlags[k] = flags ? flags[k] : 0;
        v->emitPtCount = count;
        break;
    }
    mix_unlock(&g_lock);
}

/* Fade ALL voices out over `fade`. closeWhenIdle!=0 tears the device down once silent
 * (pass 0 to keep it open for a hand-off, e.g. menu→game). Mirrors CmmMixer.Stop. */
CMM_API void cmm_mix_stop(float fade, int closeWhenIdle) {
    int i;
    mix_lock(&g_lock);
    for (i = 0; i < g_voiceCount; i++) {
        if (g_voices[i]->isEmitter) continue;   /* independent layer — the emitter layer owns its lifecycle */
        g_voices[i]->targetGain = 0.0f;
        g_voices[i]->gainStep   = compute_step(g_voices[i]->gain, 0.0f, fade);
    }
    g_closeWhenIdle = closeWhenIdle ? 1 : 0;
    mix_unlock(&g_lock);
}

/* Synchronously retire + discard every MUSIC voice now (no fade) and flush the queue. Emitter
 * voices survive (they are an independent layer removed via cmm_mix_remove_emitter); a real device
 * teardown still frees them via teardown_device_locked. */
CMM_API void cmm_mix_stop_all_now(void) {
    int i;
    mix_lock(&g_lock);
    /* Defensive hold clear (v33): this is the menu↔game teardown path — a pause hold that
     * somehow survived to here would leave the NEXT menu Play unable to revive the gain. */
    g_outGainHold = 0;
    for (i = g_voiceCount - 1; i >= 0; i--) {
        if (g_voices[i]->isEmitter) continue;
        retire_voice(g_voices[i]); free(g_voices[i]); remove_at(i);
    }
    if (g_out) cmm_out_reset(g_out);
    mix_unlock(&g_lock);
}

CMM_API void cmm_mix_set_master(float vol) { g_masterVol = vol; }

/* v24 — set the EMITTER (world-radio) master, separate from the music master (g_masterVol). Managed
 * pushes the game's music slider × the Radio context's own volume, so the radio scales only to the
 * slider (and its context level), not VolumeModifier or the current music context's volume. */
CMM_API void cmm_mix_set_emitter_master(float vol) { g_emMasterVol = vol < 0.0f ? 0.0f : vol; }

/* Update a live voice's per-track gain in place (loudness settle / trim key). */
CMM_API int cmm_mix_set_voice_gain(long long id, float trackGain, float rampSeconds) {
    int i, found = 0;
    float g = trackGain > 0.0f ? trackGain : 0.0f;
    mix_lock(&g_lock);
    for (i = 0; i < g_voiceCount; i++) {
        if (g_voices[i]->id != id) continue;
        g_voices[i]->trackGainTarget = g;
        if (rampSeconds <= 0.0f) { g_voices[i]->trackGain = g; g_voices[i]->trackGainStep = 0.0f; }
        else                     g_voices[i]->trackGainStep = compute_step(g_voices[i]->trackGain, g, rampSeconds);
        found = 1; break;
    }
    mix_unlock(&g_lock);
    return found;
}

/* Reverse-fade reclaim: ramp voice `id` back to full, fade every other out. Returns 0
 * when the voice is gone/inaudible so the caller falls back to a normal add. */
CMM_API int cmm_mix_reclaim(long long id, float fade) {
    int i, ok = 0;
    mix_lock(&g_lock);
    mix_voice* keep = NULL;
    for (i = 0; i < g_voiceCount; i++) if (g_voices[i]->id == id) { keep = g_voices[i]; break; }
    if (keep && !keep->ended && keep->gain > 0.0001f) {
        for (i = 0; i < g_voiceCount; i++) {
            mix_voice* v = g_voices[i];
            if (v->isEmitter) continue;   /* independent layer — a music reclaim never fades the world-emitter voice out (that was retiring the station mid-song on context changes) */
            float to = (v == keep) ? 1.0f : 0.0f;
            v->targetGain = to;
            v->gainStep   = compute_step(v->gain, to, fade);
            if (v == keep) v->ducked = 0;
        }
        ok = 1;
    }
    mix_unlock(&g_lock);
    return ok;
}

/* Playhead + length (seconds) of a voice. active=0 (and return 0) when the id is gone. */
CMM_API int cmm_mix_get_state(long long id, double* posSec, double* lenSec, int* active) {
    int i, found = 0;
    if (posSec) *posSec = 0; if (lenSec) *lenSec = 0; if (active) *active = 0;
    if (id == 0) return 0;
    mix_lock(&g_lock);
    for (i = 0; i < g_voiceCount; i++) {
        mix_voice* v = g_voices[i];
        if (v->id != id) continue;
        if (active) *active = 1;
        if (v->srcRate > 0 && posSec) *posSec = (double)v->framesRead / v->srcRate;
        if (v->srcRate > 0 && v->totalFrames > 0 && lenSec) *lenSec = (double)v->totalFrames / v->srcRate;
        found = 1; break;
    }
    mix_unlock(&g_lock);
    return found;
}

/* Seconds left in a voice: -1 gone, +Inf unknown length, else remaining. */
CMM_API double cmm_mix_remaining(long long id) {
    int i; double result = -1.0;
    if (id == 0) return -1.0;
    mix_lock(&g_lock);
    for (i = 0; i < g_voiceCount; i++) {
        mix_voice* v = g_voices[i];
        if (v->id != id) continue;
        if (v->srcRate <= 0 || v->totalFrames <= 0) result = INFINITY;
        else { double x = (double)(v->totalFrames - v->framesRead) / v->srcRate; result = x < 0 ? 0 : x; }
        break;
    }
    mix_unlock(&g_lock);
    return result;
}

/* Consumer-side output gain ramp (menu↔game transition fades). */
CMM_API void cmm_mix_set_output_gain(float target, float seconds) {
    mix_lock(&g_lock);
    g_outGainTarget = target;
    if (g_out) cmm_out_set_gain(g_out, target, seconds > 0.0f ? (int)(seconds * g_busRate) : 0);
    mix_unlock(&g_lock);
}

/* v33 — see cmm_decoder.h: suppress cmm_mix_add's output-gain revive while the SP pause
 * duck owns the gain. Set on the pause edge, cleared on unpause (and defensively by
 * cmm_mix_start / cmm_mix_stop_all_now). */
CMM_API void cmm_mix_hold_output(int hold) {
    mix_lock(&g_lock);
    g_outGainHold = hold ? 1 : 0;
    mix_unlock(&g_lock);
}

/* DeepBuffer (load) / ExtraCushion (post-spawn grace) — see compute_fill_target_locked. */
CMM_API void cmm_mix_set_cushion(int deep, int extra) { g_deep = deep ? 1 : 0; g_extra = extra ? 1 : 0; }

/* Ask the producer thread to reopen the output device (default endpoint changed). */
CMM_API void cmm_mix_request_reinit(void) { g_reinitRequested = 1; }

/* Live output-ring health for the debug HUD. queued/target/minQueued in FRAMES; minQueued
 * is the lowest queued depth since the LAST call (reset to the current depth on read) — a
 * min near 0 means the ring nearly/actually underran. voices = live mix voices (2 during a
 * crossfade). deep = DeepBuffer active. underruns = cumulative producer-observed ring-empty
 * events during playback. Any out-pointer may be NULL. */
CMM_API void cmm_mix_get_output_stats(int* queued, int* target, int* minQueued,
                                      int* voices, int* deep, int* underruns, int* busRate) {
    mix_lock(&g_lock);
    int q = g_out ? cmm_out_queued(g_out) : 0;
    if (queued)    *queued    = q;
    if (target)    *target    = g_targetFill;
    if (minQueued) *minQueued = (g_minQueued == 0x7fffffff) ? q : g_minQueued;
    if (voices)    *voices    = g_voiceCount;
    if (deep)      *deep      = g_deep;
    if (underruns) *underruns = g_underruns;
    if (busRate)   *busRate   = g_busRate;
    g_minQueued = q;   /* reset the watermark to the current depth */
    mix_unlock(&g_lock);
}
