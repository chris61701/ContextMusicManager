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
#define MAX_VOICES_HARD         8   /* array headroom (add appends before eviction trims) */
#define CALM_MS              3000
#define FLUSH_GRACE_MS        750
#define STINGER_RESTORE_SEC  0.6f

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
static volatile int   g_deep;        /* DeepBuffer (loading) */
static volatile int   g_extra;       /* ExtraCushion (post-spawn grace) — excluded from falling-edge flush */

/* adaptive ring state (see cmm_out.c / ComputeFillTargetLocked) */
static int g_tightFill, g_maxFill, g_lowWater, g_targetFill;
static int g_lastStarveMs, g_flushUntilMs;
static int g_prevDeep;
static float g_outGainTarget = 1.0f;

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
        g_bus[o]     += g_voiceStereo[o]     * g;
        g_bus[o + 1] += g_voiceStereo[o + 1] * g;
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
    if (g_out) { cmm_out_close(g_out); g_out = NULL; }
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

        mix_lock(&g_lock);
        {
            int i;
            for (i = g_voiceCount - 1; i >= 0; i--) {
                if (!render_voice_add(g_voices[i], n)) {
                    int wasStinger = g_voices[i]->isStinger;
                    retire_voice(g_voices[i]);
                    free(g_voices[i]);
                    remove_at(i);
                    if (wasStinger) restore_ducked_locked();
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
    g_outGainTarget = 1.0f;
    g_minQueued   = 0x7fffffff;
    g_underruns   = 0;
    g_wasEmpty    = 0;

    g_bus         = (float*)malloc((size_t)BUS_MAXFRAMES * g_busChannels * sizeof(float));
    g_voiceStereo = (float*)malloc((size_t)BUS_MAXFRAMES * g_busChannels * sizeof(float));
    g_convTmp     = (float*)malloc((size_t)BUS_MAXFRAMES * g_busChannels * sizeof(float));
    if (!g_bus || !g_voiceStereo || !g_convTmp) { if (err) *err = -2; return -2; }

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
     * that must be audible. Discard the faded queue + restore output gain atomically. */
    float reviveFrom = (g_out && g_outGainTarget != 1.0f) ? g_outGainTarget : 1.0f;
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
            if (reviveFrom != 1.0f) ev->gain *= reviveFrom;   /* keep audible level continuous across revive */
            float to = stinger ? duckOthersTo : 0.0f;
            ev->targetGain = to;
            ev->gainStep   = compute_step(ev->gain, to, fade);
            ev->ducked     = (stinger && to > 0.0f) ? 1 : 0;
        }
    }

    if (fade <= 0.0f) { v->gain = 1.0f; v->gainStep = 0.0f; }
    else              { v->gain = 0.0f; v->gainStep = compute_step(0.0f, 1.0f, fade); }
    v->targetGain = 1.0f;

    g_voices[g_voiceCount++] = v;

    /* Bound overlap: never accumulate beyond MAX_VOICES — drop the most-faded extras. */
    while (g_voiceCount > MAX_VOICES) {
        int q = -1, i; float low = 1e30f;
        for (i = 0; i < g_voiceCount; i++)
            if (g_voices[i]->id != id && g_voices[i]->gain < low) { low = g_voices[i]->gain; q = i; }
        if (q < 0) break;
        retire_voice(g_voices[q]); free(g_voices[q]); remove_at(q);
    }

    /* Context change: snap a deep cushion tight so the new context is heard promptly.
     * NOT for a same-context track switch (snapTight=0) — flushing would teleport the
     * outgoing track being crossfaded. */
    if (snapTight && g_out && cmm_out_queued(g_out) > g_tightFill * 2) flush_tight_locked();

    mix_unlock(&g_lock);
    return id;
}

/* Fade ALL voices out over `fade`. closeWhenIdle!=0 tears the device down once silent
 * (pass 0 to keep it open for a hand-off, e.g. menu→game). Mirrors CmmMixer.Stop. */
CMM_API void cmm_mix_stop(float fade, int closeWhenIdle) {
    int i;
    mix_lock(&g_lock);
    for (i = 0; i < g_voiceCount; i++) {
        g_voices[i]->targetGain = 0.0f;
        g_voices[i]->gainStep   = compute_step(g_voices[i]->gain, 0.0f, fade);
    }
    g_closeWhenIdle = closeWhenIdle ? 1 : 0;
    mix_unlock(&g_lock);
}

/* Synchronously retire + discard every voice now (no fade) and flush the queue. */
CMM_API void cmm_mix_stop_all_now(void) {
    int i;
    mix_lock(&g_lock);
    for (i = 0; i < g_voiceCount; i++) { retire_voice(g_voices[i]); free(g_voices[i]); }
    g_voiceCount = 0;
    if (g_out) cmm_out_reset(g_out);
    mix_unlock(&g_lock);
}

CMM_API void cmm_mix_set_master(float vol) { g_masterVol = vol; }

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
