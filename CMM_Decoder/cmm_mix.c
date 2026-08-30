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
  /* ONE-SHOT INIT, and not an optimisation.
   *
   * A CRITICAL_SECTION cannot be statically initialised, so g_lock used to come into
   * existence inside cmm_mix_start. Every exported function that locks was therefore
   * unsafe to call before the mixer had started: EnterCriticalSection on zeroed memory
   * is undefined behaviour, and on Windows it faults deep inside ntdll rather than
   * raising anything the managed side can catch. A game died exactly that way -- the
   * emitter layer's 1 Hz diagnostic asked all 8 ring slots for their depth on a spawn
   * where nothing had ever played, so the mixer had never started.
   *
   * Guarding 20 call sites would have left the 21st. Initialising on first use fixes
   * the class. InitOnceExecuteOnce is an atomic load once initialised, and the audio
   * callback never takes this lock (cmm_out has no locks at all), so nothing that has
   * to be fast pays for it. */
  static CRITICAL_SECTION g_mixCs;
  /* 0 = not created, 1 = being created, 2 = ready. Interlocked rather than
   * InitOnceExecuteOnce: that one needs _WIN32_WINNT >= 0x0600 and mingw's headers do
   * not expose it here, so it compiled as an implicit declaration -- the compiler
   * inventing a prototype for a function it had never seen. It linked and appeared to
   * work, which is the worst possible outcome for a lock initialiser.
   * The loser of the race spins; the window is one InitializeCriticalSection, and it
   * can only ever happen once in the process. */
  static volatile LONG g_mixCsState = 0;
  static CRITICAL_SECTION* mix_cs(void) {
      if (g_mixCsState != 2) {
          if (InterlockedCompareExchange((LONG volatile*)&g_mixCsState, 1, 0) == 0) {
              InitializeCriticalSection(&g_mixCs);
              InterlockedExchange((LONG volatile*)&g_mixCsState, 2);
          } else {
              while (g_mixCsState != 2) Sleep(0);
          }
      }
      return &g_mixCs;
  }
  /* The mix_mutex parameter is vestigial on Windows: there is exactly one mixer mutex in
   * the process (g_lock), and routing every caller through mix_cs() is what makes
   * "locked before start" impossible by construction. Kept in the signature so the
   * POSIX branch and every call site stay identical. */
  static void mix_mutex_init(mix_mutex* m) { (void)m; (void)mix_cs(); }
  static void mix_lock(mix_mutex* m)       { (void)m; EnterCriticalSection(mix_cs()); }
  static void mix_unlock(mix_mutex* m)     { (void)m; LeaveCriticalSection(mix_cs()); }
  static void mix_sleep_ms(int ms)         { Sleep((DWORD)ms); }
  static int  mix_now_ms(void)             { return (int)GetTickCount(); }
#else
  #include <pthread.h>
  #include <unistd.h>
  #include <time.h>
  typedef pthread_mutex_t mix_mutex;
  /* Same guarantee as the Windows branch above, for free: a pthread mutex CAN be
   * statically initialised, so the single process-wide mixer mutex is valid from load
   * and no caller can enter it before it exists. */
  static pthread_mutex_t g_mixPm = PTHREAD_MUTEX_INITIALIZER;
  static void mix_mutex_init(mix_mutex* m) { (void)m; }
  static void mix_lock(mix_mutex* m)       { (void)m; pthread_mutex_lock(&g_mixPm); }
  static void mix_unlock(mix_mutex* m)     { (void)m; pthread_mutex_unlock(&g_mixPm); }
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
#define MAX_VOICES_HARD        20   /* array headroom: 4 music (+ pre-evict append) + up to
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

    /* ── THE RECEIVER (v46): one number, four destinations ───────────────────────────────────
     * `recvSig` is signal strength, 0..1, and it drives the station's gain, how much treble it
     * keeps, and how wide it is. The tuning static's level is the same number inverted, applied
     * on the managed side. Tying them together is the point: a station that merely gets quieter
     * sounds like a volume fade, and four separately-tuned curves can disagree with each other.
     *
     * Ramped like the other gains, and by RATE rather than duration -- interrupting a tune-in
     * half way must fade from half, taking half as long, or an early interrupt drags and a late
     * one snaps. Off by default (`recvOn` 0), so every voice that is not a radio is untouched
     * and pays one predictable branch. */
    int    recvOn;                                  /* 1 = this voice's audio is RADIO PROGRAMME */
    /* Everything else the receiver owns now lives at MIXER level, because there is one receiver
     * and it does not belong to a station. See g_recv* below. All that survives here is the
     * mangle's filter state, which genuinely IS per-voice: it acts on this voice's programme. */
    float  recvMlp[4], recvMhp[4];                  /* the mangle's two-pole pairs, per channel */
    float  recvCEnv;                                /* the mangle's squash follower */
} mix_voice;

/* ── Mixer singleton state (the managed CmmMixer was a singleton too) ──────*/
static mix_mutex g_lock;
/* Receiver character, shared by every radio voice -- they are all the same receiver. */
static float g_recvKnee = 0.45f, g_recvKneeW = 0.22f, g_recvNoiseTrim = 0.45f;
static float g_recvClickAmt = 0.22f, g_recvClickMax = 220.0f, g_recvClickDecay = 0.9975f;
static float g_recvHiFloor = 1800.0f, g_recvDeemK = 0.24f;
/* Settled by ear against real tracks, 2026-08-23 (tools render: leave 3.4s, search 4s, arrive 7s).
 * FloorDb is the one that mattered: pulling the station down 26 dB IS a fade, however much noise
 * is laid over it. A real receiver holds the programme at full level and lets the NOISE do all the
 * moving -- the station is buried and then revealed, never faded up. Five decibels is "barely
 * moves", and it was the difference between "sounds like a crossfade" and "sounds like a radio". */
static float g_recvFloorDb = 5.0f, g_recvMuteW = 5.0f, g_recvDrive = 0.15f;
static float g_recvWander = 0.55f, g_recvFlutter = 0.30f;
static float g_recvFlutK = 0.0018f, g_recvFlutSlowK = 0.00022f;   /* ~14 Hz / ~1.7 Hz at 48k */

/* v47 -- THE ARRIVAL. Everything above describes a receiver losing a station, and it was right:
 * "when it fades out, it's perfect". Coming back it was wrong for four consecutive rounds, and the
 * reason turned out to be that NOTHING was happening to the music. Measured, the deepest treatment
 * an arriving station ever received was -3 dB at 3.75 kHz with its bass, its dynamics and its
 * stereo image completely intact -- i.e. the source file, fading up, with noise laid over it.
 * Which is exactly what it sounded like.
 *
 * Two things were missing and both are here. The programme has to be MANGLED and then repaired
 * (g_recvRadio*), and the hiss has to outlast the tuning (g_recvNoiseHold) instead of clearing
 * while the station is still opening -- because a fade with nothing over it is just a fade. */
static float g_recvNoiseHold = 1.0f, g_recvNoiseFloorR = 0.06f;
static float g_recvWinA = 0.28f, g_recvWinB = 0.912f;   /* the treble window, as ramp positions */
static float g_recvLockU = 0.52f, g_recvLockMs = 9.0f;  /* where stereo latches, and how fast */
static float g_recvRadio = 1.0f, g_recvRadioMk = 9.0f;
static float g_recvRadioHp = 400.0f, g_recvRadioLp = 2600.0f;

/* ══ v48: ONE RECEIVER, AT THE BUS ══════════════════════════════════════════════════════════
 * The noise used to be generated per VOICE, which was right when a radio meant one voice and
 * wrong the moment a dial press meant two. Changing station overlaps the station being left with
 * the station being loaded, and the loading one HOLDS AT SIGNAL 0 -- which is maximum noise --
 * while it waits out the search floor. Signal does not attenuate a voice (g = gain x trackGain;
 * the receiver's own 5 dB level law owns that), so both voices emitted full-scale hiss at once,
 * from independent generators with independent wander and flutter. Two uncorrelated roars summing
 * is not a louder receiver, it is two receivers, and it was audibly both.
 *
 * A third noise existed as well: a separate synthesized hash pinged every 250 ms whenever no
 * voice existed at all, on a completely different generator (fixed band-pass, sample-and-hold
 * sputter, no de-emphasis, no wander, no clicks). Three sounds for one event.
 *
 * There is one receiver. It is not a property of a station -- stations are what it is tuned TO.
 * So: radio voices contribute PROGRAMME ONLY into g_radioBus, and one bus-level stage generates
 * the noise, sums, and applies the shared hi-cut and the shared width. It runs whether or not any
 * voice exists, which is what retires the hash: the voiceless window between stations is now
 * covered by the same static as everything either side of it.
 *
 * g_recvU is the per-frame signal, advanced ONCE per block before the voice loop so that every
 * voice and the bus stage read the same number for the same frame. That also deletes the old
 * per-voice "this voice under-produced, advance its ramp to match" compensation: there is one
 * ramp now, and it does not care what the voices did. */
static float* g_recvU;                          /* per-frame signal for this block */
static volatile int   g_recvActive;             /* 0 = radio off: the stage is a no-op */
static volatile float g_recvLevel = 1.0f;       /* the radio's own volume (context vol x duck) */
static float g_recvSig, g_recvSigTarget, g_recvSigStep;
static float g_recvDull = 0.85f, g_recvMono = 1.0f;
static unsigned g_recvRng = 22222u;
static float g_recvNzLp, g_recvNzLp2;
static float g_recvClk; static int g_recvClkNext;
static float g_recvWCur = 1.0f, g_recvWTgt = 1.0f; static int g_recvWHold;
static float g_recvFA, g_recvFB;
static float g_recvLp[2];
static int   g_recvLocked; static float g_recvLockW;

static int       g_started;
static volatile int g_running;

static mix_voice* g_voices[MAX_VOICES_HARD];
static int        g_voiceCount;
static long long  g_nextId;      /* 0 reserved for "none" */

static cmm_out* g_out;
static int      g_busRate = 48000, g_busChannels = 2;   /* bus is fixed stereo */
static int      g_closeWhenIdle;
static volatile int g_reinitRequested;
/* v40 — control threads no longer open the device themselves (ma_device_init/start can
 * block for seconds on a wedged WASAPI endpoint, and it used to run while HOLDING g_lock,
 * freezing every g_lock caller — including the game main thread at the spawn stinger).
 * They set this flag instead; the producer thread, the sole device-lifecycle owner, opens
 * and closes OUTSIDE g_lock. One open attempt per request (retry cadence = per add, as
 * before), so a missing endpoint never turns into an open storm. */
static volatile int g_deviceWanted;
/* Headless mode (offline stress harness): the output "device" is a ring with NO
 * ma_device — the caller drains it via cmm_mix_pump_out. Everything else (producer
 * thread, voices, crossfades, retire/free churn) runs exactly as in-game, so the
 * mixer's native lifecycle is exercised at compressed speed for leak hunting. */
static int      g_headless;

static volatile float g_masterVol   = 1.0f;
/* v41 — DE-ZIPPER for the music master. g_masterVol is a TARGET; the producer walks the
 * actually-applied gain toward it at a bounded per-frame rate (below). Applying the target
 * directly made every master change a step discontinuity at a block boundary — audible as a
 * click. The game's volume sliders move in 5% notches and managed re-pushes the master at
 * 10Hz, so dragging one produced a train of clicks (reported in-game once CMM started
 * honouring the Overall slider; the same latent step was always there on the music slider).
 * Producer-thread ONLY — never touched from the control thread, so no atomicity concern. */
static float g_masterApplied = 1.0f;
/* Full-scale (0->1) traverse time for the slew. 25ms is long enough that no step in it is
 * audible as a click and short enough to read as instant: one 5% notch lands in ~1.25ms. */
#define MASTER_SLEW_MS 25.0f
/* Emitter (world-radio) master — DELIBERATELY SEPARATE from g_masterVol. The music master folds in
 * VolumeModifier and the current context's volume; the diegetic radio must NOT ride those (a quiet
 * music context shouldn't quiet the radio). Managed sets this to the game's music SLIDER × the Radio
 * context's own volume, so the radio scales only to the in-game slider (and its context level). */
static volatile float g_emMasterVol = 1.0f;
/* v41 — de-zipper for the emitter master, mirroring g_masterApplied. Producer-thread only. */
static float g_emMasterApplied = 1.0f;
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
/* v51: the ONE occlusion/tone smoothing time constant, seconds. Was a per-callback coefficient
 * buried in cmm_out.c whose real time constant (328 ms at a 10 ms buffer) nobody chose and nobody
 * could see; the door model's lead is what cancels it, and a lead can only cancel a constant. */
static volatile float g_emOcclSlew = 0.05f;
/* v42: tone from occlusion rather than from virtual distance. Off by default so the shipped
 * occlusion model -- whose door behaviour is the most carefully tuned thing in the emitter
 * system -- is bit-identical; the native acoustic model turns it on. */
static volatile int   g_emOcclTone   = 0;
static volatile float g_emOcclFullDb = 8.0f;

/* Live emitter reverb (room reflection) params, from the managed room probe. v31: the CARRY send
 * level + air-route depth are PER-SLOT (each station's seal/route differs); the room shape is shared. */
/* == v54: WHERE A STATION'S SPEAKERS ARE IS A PROPERTY OF THE SLOT ==========================
 * The point set used to live on the VOICE, and the relay below had to hunt for "this slot's
 * newest emitter voice" to find it. That was serviceable while a slot without a voice was always
 * silent -- and it stopped being true the moment a slot could produce a sound of its own. A
 * receiver searching between stations has no voice by definition: the old station has gone and
 * the new one has not opened. With the positions on the voice there was nowhere in the world to
 * put that sound, so the hiss could only have come out of the listener's head, which is the
 * whole thing we set out to fix.
 *
 * It is also the same lesson as v48 one level down. The receiver is not a property of a station;
 * neither is where the speaker stands. Both belong to the slot, and moving the points here
 * DELETES the newest-voice search rather than adding an indirection. */
static float g_emSlotPts[CMM_MAX_EMIT_VOICES][MAX_EMIT_PTS * 3];
static float g_emSlotGain[CMM_MAX_EMIT_VOICES][MAX_EMIT_PTS];
static int   g_emSlotFlags[CMM_MAX_EMIT_VOICES][MAX_EMIT_PTS];
static int   g_emSlotCount[CMM_MAX_EMIT_VOICES];

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
/* v41 — the DIAL DETENT is its own sound, not the power-on click reused. Flipping a radio on is
 * a toggle: a lever going over centre, then the mechanism hitting its stop -- two impacts, a low
 * heavy shell. Turning the tuning knob one notch is a detent: a ball dropping into a groove --
 * one bright tick, a small tight shell, over in a few milliseconds. They are different objects
 * and reusing one for the other is why the dial did not read as a dial. */
#define SFX_KIND_CLICK_DIAL 4
#define SFX_CLICK_KINDS 3
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
    /* v41 — THREE damped modes, not one, and none of them DC. The old click summed a decaying
     * DC offset for its "thump", which is not a sound a switch makes: a decaying step is a
     * speaker pop, always the same polarity, with all of its energy below the pitch of anything
     * mechanical. Between that and a single 1 kHz ring it read as "tss-pop-beep" rather than as
     * a switch, which is what "wrong sound" was. A real toggle is a broadband snap plus a
     * SHELL that rings in a couple of inharmonic modes and dies fast. */
    float rc1[5], rc2[5];      /* per-mode two-pole coefficients (pitch + decay) */
    float ry1[5], ry2[5];      /* per-mode state */
    float mLev[5];             /* per-mode excitation level */
    /* v42 — THE EXCITER IS A NOISE BURST, NOT AN IMPULSE. Hitting a resonator with a single
     * impulse produces a decaying SINE: that is a note, and it is why the clicks came back as
     * "too tonal" (measured autocorrelation 0.84-0.92 out of 1.0 — essentially pitched). A real
     * impact drives the shell with a couple of milliseconds of broadband energy, which comes out
     * as a band of noise around each mode rather than a tone at it. Same modes, same pitches,
     * completely different character: a knock instead of a chime. */
    float exLev, exK;
    float snapHp;              /* one-pole HP state: keeps the noise snap out of the mud */
    float kSnapHp;
    float hi2;                 /* second pole of the tone cap (see sfx_click_sample) */
    /* v43 — A CLUSTER OF MICRO-IMPACTS, NOT A TIDY PAIR. Two cleanly spaced strikes is a
     * synthesizer patch; a mechanism CHATTERS. A detent ball does not drop into its groove once,
     * it lands and rebounds and the spring plate buzzes against the stop, and that ragged handful
     * of collisions inside the first few milliseconds is what the ear reads as "mechanical". With
     * exactly two you hear a designed sound, however good each one is -- which is why nothing
     * sounded gritty. */
    /* v44 — SAMPLE PLAYBACK. When a click kind has a recorded sound loaded, the voice plays that
     * instead of synthesizing, at a slightly different RATE every firing. Repeating one recording
     * verbatim is exactly what gives a UI sound away, and a small pitch/speed shift per fire is
     * the oldest fix in game audio because it is what actually happens: no two throws of a switch
     * are identical, and the ear reads the difference as a mechanism rather than a playback. */
    const float *pcm; int pcmLen; double pcmPos, pcmStep;
    int   strikeAt[6];         /* frames of each impact (index 0 is always 0) */
    float strikeLvl[6];        /* their levels relative to the first */
    int   strikeN, strikeNext; /* how many, and the cursor into them */
    /* v37 — static CRACKLE: a sample-and-hold amplitude sputter (random level held for a random
     * 3..30ms, slewed) that turns the smooth hiss into radio-tuning sputter. Depth 0 = smooth. */
    float crackle;         /* modulation depth 0..1 (captured from the tone globals per ping) */
    int   modHold;         /* frames until the next random level draw */
    float modCur;          /* current drawn level target (biased toward dips) */
    float modLev;          /* slewed applied level (anti-click) */
} sfx_voice;
static sfx_voice g_sfx[SFX_MAX];
static float sfx_click_sample(sfx_voice* v, float noise);   /* defined with the tone capture */


/* One recorded sound per click kind (0 = power on, 1 = power off, 2 = dial). NULL = synthesize,
 * which stays the fallback so a missing or unreadable file is a quieter mod, never a broken one. */
static float *g_sfxPcm[SFX_CLICK_KINDS];
static int    g_sfxPcmLen[SFX_CLICK_KINDS];
static int    g_sfxPcmRate[SFX_CLICK_KINDS];
static volatile float g_sfxRateJitter = 2000.0f;   /* +/- Hz around the file's own rate */

static unsigned  g_sfxSeed = 0x9E3779B9u;

/* Point a voice at a kind's recorded sound, at a rate drawn fresh for this firing. Returns 0 if
 * that kind has no sample, in which case the caller synthesizes. Shared by the live mixer and the
 * offline preview so an audition cannot differ from what the game plays -- every time this
 * project has kept two copies of the same decision they have drifted. */
static int sfx_arm_sample(sfx_voice* v, int ki) {
    if (ki < 0 || ki >= SFX_CLICK_KINDS) return 0;
    if (!g_sfxPcm[ki] || g_sfxPcmLen[ki] <= 1) return 0;
    /* A DIFFERENT RATE EVERY FIRING, drawn once so the whole click shifts together. +/-2000 Hz on
     * a 48 kHz file is about 4% -- a couple of semitones at most, heard as "another one of those"
     * rather than as a different sound, which is the whole trick. Varying it DURING a click would
     * be a wobble; varying it between clicks is a mechanism. */
    float j = g_sfxRateJitter; if (j < 0.0f) j = 0.0f;
    g_sfxSeed = g_sfxSeed * 1664525u + 1013904223u;
    float u = (float)((g_sfxSeed >> 9) & 0xffff) / 32768.0f - 1.0f;   /* -1..1 */
    float rate2 = (float)g_sfxPcmRate[ki] + j * u;
    if (rate2 < 1000.0f) rate2 = 1000.0f;
    v->pcm = g_sfxPcm[ki];
    v->pcmLen = g_sfxPcmLen[ki];
    v->pcmPos = 0.0;
    v->pcmStep = (double)rate2 / (double)(g_busRate > 0 ? g_busRate : 48000);
    v->len = (int)((double)v->pcmLen / v->pcmStep) + 1;
    return 1;
}
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
static volatile float g_sfxClickHz[SFX_CLICK_KINDS]    = { 8722.0f, 8722.0f, 8187.0f };
static volatile float g_sfxClickSnap[SFX_CLICK_KINDS]  = { 0.72f, 0.70f, 0.84f };
static volatile float g_sfxClickLenMs[SFX_CLICK_KINDS] = { 60.0f, 68.0f, 44.0f };
/* v39 — detent character: body-knock mix / its ring pitch / second-strike level (0 = single). */
static volatile float g_sfxClickBody[SFX_CLICK_KINDS]   = { 0.37f, 0.40f, 0.50f };
static volatile float g_sfxClickBodyHz[SFX_CLICK_KINDS] = { 5100.0f, 4350.0f, 5949.0f };
static volatile float g_sfxClickDouble[SFX_CLICK_KINDS] = { 0.80f, 0.80f, 0.52f };
/* How much each firing differs from the last, 0..1. A detent is a physical event and no two are
 * identical; a fixed waveform repeated on every notch is the thing that gives a UI sound away as
 * a sound EFFECT. Jitters pitch, level, the shell balance and the spacing of the second impact,
 * so a fast spin down the dial sounds like a mechanism rather than a loop. */
static volatile float g_sfxClickVary = 0.35f;

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
static float* g_radioBus;   /* radio PROGRAMME only; the receiver stage sums it out */
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
/* Flush IEEE-754 denormals to zero. Same helper and same reason as cmm_reverb.h's — a filter fed
 * silence drifts into denormal range and costs 10–100× on the audio thread from then on. Copied
 * rather than included: that header pulls in the whole reverb, and this is four lines. */
static inline float mix_undenorm(float x)
{
    unsigned int u; memcpy(&u, &x, sizeof(u));
    return (u & 0x7f800000u) == 0u ? 0.0f : x;
}

/* Advance the ONE receiver ramp across this block and publish it per frame, so every voice and
 * the bus stage below read the same signal for the same frame. Called before the voice loop. */
static void receiver_advance(int n) {
    int f;
    for (f = 0; f < n; f++) {
        if (g_recvSigStep != 0.0f) {
            g_recvSig += g_recvSigStep;
            if ((g_recvSigStep > 0.0f && g_recvSig >= g_recvSigTarget) ||
                (g_recvSigStep < 0.0f && g_recvSig <= g_recvSigTarget))
            { g_recvSig = g_recvSigTarget; g_recvSigStep = 0.0f; }
        }
        g_recvU[f] = g_recvSig < 0.0f ? 0.0f : (g_recvSig > 1.0f ? 1.0f : g_recvSig);
    }
}

/* == v54: A RECEIVER PER SPEAKER ==============================================================
 * The world speakers had no receiver at all. A station change was a decoder opening and a voice
 * starting, so retuning a radio across the room was silent until the music simply appeared -- and
 * the one sound that did cover it, the SFX tuning static, plays on the music bus, which has no
 * position. It arrived inside the listener's head instead of out of the radio.
 *
 * So each emitter SLOT gets its own receiver. Not one shared receiver: two speakers tuned at
 * different moments are two sets searching independently, and a single ramp would make retuning
 * one of them hiss out of all three. State is per slot; every PARAMETER above (g_recvKnee,
 * g_recvNoiseTrim, g_recvWander, the click set, the noise hold...) is READ, not copied, so the
 * radio.xml knobs that tune the personal radio tune these at the same time. One sound, two places
 * it comes out of.
 *
 * WHY THIS IS NOT render_receiver_add REFACTORED TO TAKE A POINTER. That function is stereo: its
 * last third is the decoder latch and the stereo width, which is most of its length and all of its
 * delicacy, and an emitter lane is MONO by construction (it is spatialized afterwards from a point
 * set). Splitting state from parameters across a function tuned by ear over four failed rounds, to
 * share a third of it and skip the rest, is a poor trade against writing the mono path out. The
 * parameters are shared, which is the part that matters for tuning; the stereo stage is the part
 * that does not apply.
 *
 * WHERE IT RUNS: on the slot's dry mono lane, after every voice has contributed to it and BEFORE
 * the emitter master, so the hiss inherits distance, occlusion, the door model, the room reverb
 * and the speaker's own coloration exactly as the programme does. It is the same receiver in the
 * same box. */
typedef struct {
    float sig, sigTarget, sigStep;   /* 0 = searching (full noise), 1 = locked */
    unsigned rng;
    float nzLp, nzLp2;               /* the de-emphasis pair (noise only) */
    float clk; int clkNext;          /* click event level + frames until the next one */
    float wCur, wTgt; int wHold;     /* AGC wander */
    float fA, fB;                    /* flutter, from band-limited noise */
    float lp;                        /* the shared hi-cut, one pole (mono) */
    int   armed;                     /* 0 = no receiver on this slot: the stage is a no-op */
} em_recv;
static em_recv g_emRx[CMM_MAX_EMIT_VOICES];

/* ── WHAT EACH LANE IS ACTUALLY DOING (v61) ───────────────────────────────────────────────
 * The managed layer can see what it ASKED for and nothing about what came out, which is why
 * "the static was not audible" and "the music came in hot" have both cost days: they are
 * statements about level, and level was the one thing never logged. This is the dry lane's
 * RMS after its receiver and before the master -- the last point where a lane is still one
 * identifiable thing. */
static float g_emLaneRms[CMM_MAX_EMIT_VOICES];



/* ── LANE MIRRORS (v56) ───────────────────────────────────────────────────────────────────────
 * Which lane, if any, a slot takes its dry audio FROM instead of from voices of its own.
 * -1 = none, the ordinary case.
 *
 * A station's lane is shared by every speaker playing it, and the receiver sits on the lane -- so
 * hissing ONE speaker of a shared station would hiss all of them. The old answer was to give the
 * retuned speaker a spare lane with no voice at all, which meant its static had nothing playing
 * underneath: no station to duck away, none to rise. Every "cuts to nothing" report came from that.
 *
 * A second decoder of the same file would drift and comb. But the lane is already decoded, so the
 * retuned speaker can simply take a COPY of it and run its own receiver over that. One decode, two
 * lanes, two independent tuning states, sample-aligned by construction. The other speakers on the
 * source lane are untouched, which is the whole requirement. */
static int g_emMirror[CMM_MAX_EMIT_VOICES];

/* ── THE MIRROR'S OWN LEVEL (v60) ─────────────────────────────────────────────────────────
 * A mirrored lane carries a copy of a station, and the RECEIVER on that same lane carries
 * the static. They are one signal by the time a point gain is applied, so fading the point
 * fades both -- which is why the mandatory silence before music was also silencing the
 * hiss it was supposed to be heard under.
 *
 * This scales the COPY, before the receiver runs, so a lane can fade its programme in and
 * out while its static does whatever it likes. That separation is the whole requirement:
 * "the speaker fades, not the stream". */
static float g_emMirrorAmp[CMM_MAX_EMIT_VOICES];
static float g_emMirrorTgt[CMM_MAX_EMIT_VOICES];
static float g_emMirrorStp[CMM_MAX_EMIT_VOICES];

CMM_API void cmm_mix_set_lane_mirror_gain(int slot, float gain, float rampSeconds) {
    if (slot < 0 || slot >= CMM_MAX_EMIT_VOICES) return;
    if (gain < 0.0f) gain = 0.0f; else if (gain > 1.0f) gain = 1.0f;
    g_emMirrorTgt[slot] = gain;
    if (rampSeconds <= 0.0001f) { g_emMirrorAmp[slot] = gain; g_emMirrorStp[slot] = 0.0f; return; }
    float frames = rampSeconds * (float)g_busRate;
    if (frames < 1.0f) frames = 1.0f;
    g_emMirrorStp[slot] = (gain - g_emMirrorAmp[slot]) / frames;
}

/* ── OFF-AIR TONE (v57) ───────────────────────────────────────────────────────────────────────
 * A steady tone on one lane, in place of both programme and hiss. What a station that will not
 * open gets, rather than silence: a set that finds nothing does not go quiet, it sits on the
 * carrier. Same idea as a channel that has stopped broadcasting for the night. Positional like
 * everything else on a lane, which the SFX bus could never be. */
#define EM_TONE_MAX 3
static float g_emToneHz [CMM_MAX_EMIT_VOICES][EM_TONE_MAX];
static float g_emToneLvl[CMM_MAX_EMIT_VOICES][EM_TONE_MAX];
static float g_emTonePh [CMM_MAX_EMIT_VOICES][EM_TONE_MAX];
static int   g_emToneN  [CMM_MAX_EMIT_VOICES];
/* One envelope over the whole chord, so the partials never drift apart in level. */
static float g_emToneAmp[CMM_MAX_EMIT_VOICES], g_emToneTgt[CMM_MAX_EMIT_VOICES];
static float g_emToneStep[CMM_MAX_EMIT_VOICES];

/* Up to EM_TONE_MAX partials sounded together (count <= 0, or every hz <= 0, fades to off).
 * The envelope RAMPS: a carrier that snapped on was a click and read as a fault rather than as a
 * station going off air. Phases reset only on a fade FROM silence, so retuning the chord while it
 * sounds does not restart it mid-cycle. */
CMM_API void cmm_mix_set_emitter_tone(int slot, const float* hz, const float* levels,
                                      int count, float rampSeconds) {
    int k;
    if (slot < 0 || slot >= CMM_MAX_EMIT_VOICES) return;
    if (count > EM_TONE_MAX) count = EM_TONE_MAX;
    if (count < 0) count = 0;

    int live = 0;
    for (k = 0; k < count; k++) if (hz && hz[k] > 0.0f) live++;

    if (live == 0) { g_emToneTgt[slot] = 0.0f; }
    else {
        /* AND ASK FOR THE OUTPUT, exactly as arming a receiver does. This was the only emitter
         * lane source that never did, so raising a carrier while the device was closed rendered
         * into nothing for its whole life -- measured at silence with g_deviceWanted still 0. */
        g_deviceWanted = 1;
        g_closeWhenIdle = 0;
        if (g_emToneAmp[slot] <= 0.0001f)
            for (k = 0; k < EM_TONE_MAX; k++) g_emTonePh[slot][k] = 0.0f;   /* from a zero crossing */
        for (k = 0; k < count; k++) {
            g_emToneHz [slot][k] = hz ? hz[k] : 0.0f;
            float l = levels ? levels[k] : 1.0f;
            g_emToneLvl[slot][k] = l < 0.0f ? 0.0f : (l > 1.0f ? 1.0f : l);
        }
        g_emToneN[slot] = count;
        g_emToneTgt[slot] = 1.0f;
    }
    if (rampSeconds <= 0.0001f) { g_emToneAmp[slot] = g_emToneTgt[slot]; g_emToneStep[slot] = 0.0f; return; }
    float frames = rampSeconds * (float)g_busRate;
    if (frames < 1.0f) frames = 1.0f;
    g_emToneStep[slot] = (g_emToneTgt[slot] - g_emToneAmp[slot]) / frames;
}

/* Sounding, or still fading out of it. */
static int em_tone_live(int s) { return g_emToneAmp[s] > 0.0001f || g_emToneTgt[s] > 0.0f; }

/* The accessor lives here, below the mirror and carrier state it reports on. */
CMM_API float cmm_mix_emitter_lane_stat(int slot, int what) {
    if (slot < 0 || slot >= CMM_MAX_EMIT_VOICES) return -1.0f;
    switch (what) {
        case 0: return g_emRx[slot].armed ? g_emRx[slot].sig : -1.0f;  /* <0 = not armed */
        case 1: return g_emLaneRms[slot];                              /* linear RMS */
        case 2: return g_emMirror[slot] >= 0 ? g_emMirrorAmp[slot] : -1.0f;
        case 3: return em_tone_live(slot) ? g_emToneAmp[slot] : -1.0f;
        default: return -1.0f;
    }
}


/* dst takes its audio from src (src < 0 clears). Self-mirroring is refused; so is a src that is
 * itself a mirror, which keeps the copy one level deep and the ordering below trivially correct. */
CMM_API void cmm_mix_set_lane_mirror(int dst, int src) {
    if (dst < 0 || dst >= CMM_MAX_EMIT_VOICES) return;
    if (src >= CMM_MAX_EMIT_VOICES) return;
    if (src == dst) return;
    g_emMirror[dst] = src < 0 ? -1 : src;
}

/* THE EMITTER'S NOISE SITS BELOW THE PERSONAL RADIO'S, and it has to be its own number rather
 * than a smaller g_recvNoiseTrim, because that one is shared and turning it down would quieten
 * the radio in your pocket at the same time.
 *
 * They are not comparable levels to begin with. The bus receiver scales its noise by g_recvLevel
 * -- the radio's own context volume -- and the programme arrives already carrying its voice gain.
 * An emitter lane has neither: it is pre-master, pre-spatialization, pre-occlusion, so noise
 * written at the bus's amplitude arrives at the ear far hotter than the same figure does through
 * the music bus. Reported as simply too loud, halved here. */
static volatile float g_emRecvNoise = 0.5f;

CMM_API void cmm_mix_set_emitter_recv_trim(float trim) {
    if (trim < 0.0f) trim = 0.0f; else if (trim > 4.0f) trim = 4.0f;
    g_emRecvNoise = trim;
}

/* Arm/steer one slot's receiver. signal 0 = searching, 1 = locked; ramped over rampSeconds so a
 * retune is a dial being turned rather than a switch. Called from managed under no lock: these are
 * plain floats read by the producer, and a torn ramp target is inaudible where a lock is not. */
/* Any emitter slot that would render with no voice behind it — see the idle teardown. */
static int em_tone_live(int s);

/* Anything that would render on an emitter lane with no voice behind it -- see the idle teardown.
 * MUST agree with the lane gate in the producer, which is
 *     if (!emActive[s] && !g_emRx[s].armed && !em_tone_live(s)) continue;
 * It did not: this tested `armed` alone, so a speaker sitting on the OFF-AIR CARRIER -- which has
 * no voice and no armed receiver by definition -- was the one emitter sound with nothing holding
 * the device open, and the teardown closed it underneath. Measured: tone raised, device torn down,
 * nothing renders. */
static int emitters_armed(void) {
    int s;
    for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) if (g_emRx[s].armed || em_tone_live(s)) return 1;
    return 0;
}

CMM_API void cmm_mix_set_emitter_recv(int slot, float signal, float rampSeconds) {
    if (slot < 0 || slot >= CMM_MAX_EMIT_VOICES) return;
    em_recv* r = &g_emRx[slot];
    if (signal < 0.0f) signal = 0.0f; else if (signal > 1.0f) signal = 1.0f;
    /* Heading DOWN toward a search, rather than up toward a lock. */
    int startingSearch = (signal < 0.5f) && (!r->armed || r->sig > signal + 0.05f);
    if (!r->armed) {
        /* First arm: start where we are told to, not from a stale ramp. RNG is seeded per slot so
         * three speakers searching at once are three different roars, not one in triplicate. */
        r->armed = 1;
        /* AND ASK FOR THE OUTPUT. g_deviceWanted is set only by cmm_mix_add / _add_emitter /
         * _reinit -- that is, only by ADDING A VOICE -- so a borrow arriving while the device was
         * already closed could never open one, and hissed into nothing for its whole life. Set
         * after r->armed so the producer cannot win the race and tear down between the two. */
        g_deviceWanted = 1;
        g_closeWhenIdle = 0;
        r->rng = 22222u + (unsigned)(slot * 2654435761u);
        r->nzLp = r->nzLp2 = r->clk = r->fA = r->fB = r->lp = 0.0f;
        /* FIRST ARM STARTS AT FULLY TUNED, not at the target -- the same fix the BUS receiver
         * carries (see cmm_mix_set_receiver_signal) and the emitter one never got:
         *
         *   "Setting the signal to `sg` here and then computing the step against it -- now
         *    equal -- made the step ZERO, so the very first arm always jumped and silently
         *    ignored ratePerSec. A receiver nobody has tuned is by definition fully in."
         *
         * Every lane ClearEmitterRecv has ever reaped comes back through here, which is the
         * ordinary case, so the ordinary retune SNAPPED to full static instead of sliding there
         * over the leave. The station sinking away never happened at all. */
        r->sig = 1.0f;
    }
    if (startingSearch) {
        /* RESEED THE WANDER ON EVERY SEARCH, not just the first. It is an AGC breathing as a
         * signal fades, with a time constant built for a receiver that sits between stations for a
         * while -- and a sweep is a quarter of a second. Left running, each sweep began wherever
         * the previous one's random walk had wandered to, so the same retune was a different
         * loudness every time. Reported as the static's volume changing. The RNG stream is
         * deliberately NOT reset, so consecutive tunes still sound like different noise. */
        r->wCur = r->wTgt = 1.0f;
        r->wHold = 0;
    }
    r->sigTarget = signal;
    if (rampSeconds <= 0.0001f) { r->sig = signal; r->sigStep = 0.0f; return; }
    /* A RATE, NOT A DURATION -- again matching the bus, and for the reason its own comment gives:
     *
     *   "Interrupting is the whole reason this is a rate rather than a duration. Press again half
     *    way through a tune-in and the leave starts from a half -- not from full, which would
     *    swell upward before falling, and not over a fixed 260 ms, which would drag."
     *
     * rampSeconds is read as the time for a FULL 0->1 traverse, so a half-finished ramp reverses
     * in half the time instead of dragging out the whole span from wherever it happened to be. */
    float frames = rampSeconds * (float)g_busRate;
    if (frames < 1.0f) frames = 1.0f;
    float step = 1.0f / frames;
    r->sigStep = (signal > r->sig) ? step : (signal < r->sig ? -step : 0.0f);
    if (r->sigStep == 0.0f) r->sig = signal;
}

/* Where this slot's speakers are, their per-speaker occlusion gain and render flags. Replaces
 * cmm_mix_set_emitter_points: the caller now names the SLOT it is describing rather than a voice
 * that happens to be playing on it, so the description outlives any particular track -- and a
 * slot with no voice at all still has somewhere to put its receiver's noise. */
CMM_API void cmm_mix_set_slot_points(int slot, const float* xyz, const float* gains,
                                     const int* flags, int count) {
    if (slot < 0 || slot >= CMM_MAX_EMIT_VOICES) return;
    if (count < 0) count = 0;
    if (count > MAX_EMIT_PTS) count = MAX_EMIT_PTS;
    int k;
    if (count > 0 && xyz) {
        for (k = 0; k < count * 3; k++) g_emSlotPts[slot][k] = xyz[k];
        for (k = 0; k < count; k++) {
            g_emSlotGain[slot][k]  = gains ? gains[k] : 1.0f;
            g_emSlotFlags[slot][k] = flags ? flags[k] : 0;
        }
    }
    g_emSlotCount[slot] = count;
}

/* Disarm: the slot goes back to plain programme with no receiver in the path. */
CMM_API void cmm_mix_clear_emitter_recv(int slot) {
    if (slot < 0 || slot >= CMM_MAX_EMIT_VOICES) return;
    g_emRx[slot].armed = 0;
    g_emRx[slot].sig = g_emRx[slot].sigTarget = 1.0f;
    g_emRx[slot].sigStep = 0.0f;
}

/* The stage. Mono, in place, over one slot's dry lane. Mirrors render_receiver_add's noise:
 * the same knee into SNR, the same wander and flutter, the same de-emphasised (dark, not bright)
 * hiss, the same click law, the same hold that makes the noise outlast the tuning -- and the same
 * refusal to fade the programme, because a real set holds the station at level and lets the NOISE
 * do the moving. Reading the same globals is what keeps the two in step. */
static void emitter_receiver_mono(int slot, float* lane, int n) {
    em_recv* r = &g_emRx[slot];
    if (!r->armed) return;
    int f;
    for (f = 0; f < n; f++) {
        if (r->sigStep != 0.0f) {
            r->sig += r->sigStep;
            if ((r->sigStep > 0.0f && r->sig >= r->sigTarget) ||
                (r->sigStep < 0.0f && r->sig <= r->sigTarget))
            { r->sig = r->sigTarget; r->sigStep = 0.0f; }
        }
        float u = r->sig < 0.0f ? 0.0f : (r->sig > 1.0f ? 1.0f : r->sig);

        float q;
        if      (u <= 0.0005f) q = 0.0f;
        else if (u >= 0.9995f) q = 1.0f;
        else q = 1.0f / (1.0f + expf(-(u - g_recvKnee) * (4.0f / g_recvKneeW)));
        float snr = -4.0f + 64.0f * q;
        int rising = (r->sigStep > 0.0f);

        /* Wander and flutter: see render_receiver_add. A rate you can hear counting is a
         * metronome, hence band-limited noise rather than an oscillator. */
        if (--r->wHold <= 0) {
            r->rng = r->rng * 1664525u + 1013904223u;
            float uw = (float)(r->rng >> 8) * (1.0f / 16777216.0f);
            r->wTgt = 1.0f + g_recvWander * (uw * 2.0f - 1.0f);
            if (r->wTgt < 0.05f) r->wTgt = 0.05f;
            r->rng = r->rng * 1664525u + 1013904223u;
            float uh = (float)(r->rng >> 8) * (1.0f / 16777216.0f);
            r->wHold = (int)((float)g_busRate * (0.18f + 0.9f * uh));
            if (r->wHold < 1) r->wHold = 1;
        }
        r->wCur += (r->wTgt - r->wCur) * 0.00012f;
        r->rng = r->rng * 1664525u + 1013904223u;
        float fw = (float)(int)r->rng * (1.0f / 2147483648.0f);
        r->fA = mix_undenorm(r->fA + g_recvFlutK * (fw - r->fA));
        r->fB = mix_undenorm(r->fB + g_recvFlutSlowK * (r->fA - r->fB));
        float fmod = (r->fA - r->fB) * 46.0f;
        if (fmod > 1.4f) fmod = 1.4f; else if (fmod < -1.4f) fmod = -1.4f;
        float flut = 1.0f + g_recvFlutter * fmod;
        if (flut < 0.05f) flut = 0.05f;

        float nLvl = powf(10.0f, -snr * 0.05f);
        if (g_recvNoiseHold > 0.001f && rising) {
            float na = 0.22f, nb = 0.55f + 0.42f * g_recvNoiseHold;
            float nf = 1.0f - (u - na) / (nb - na);
            if (nf < 0.0f) nf = 0.0f; else if (nf > 1.0f) nf = 1.0f;
            nf = nf * nf * (3.0f - 2.0f * nf);
            float held = g_recvNoiseFloorR + (1.0f - g_recvNoiseFloorR) * nf;
            if (held > nLvl) nLvl = held;
        }
        float nAmp = nLvl * g_recvNoiseTrim * g_emRecvNoise * r->wCur * flut;
        if (nAmp > 1.6f) nAmp = 1.6f;

        r->rng = r->rng * 1664525u + 1013904223u;
        float wn = (float)(int)r->rng * (1.0f / 2147483648.0f);
        r->nzLp  = mix_undenorm(r->nzLp  + g_recvDeemK * (wn - r->nzLp));
        r->nzLp2 = mix_undenorm(r->nzLp2 + g_recvDeemK * (r->nzLp - r->nzLp2));
        float nz = r->nzLp2 * nAmp * 2.0f;

        if (g_recvClickAmt > 0.001f && snr < 14.0f) {
            if (--r->clkNext <= 0) {
                float rate = g_recvClickMax * powf(10.0f, -(snr + 4.0f) * 0.1667f);
                if (rate < 0.05f) rate = 0.05f;
                r->rng = r->rng * 1664525u + 1013904223u;
                float uu = (float)(r->rng >> 8) * (1.0f / 16777216.0f);
                if (uu < 1e-4f) uu = 1e-4f;
                r->clkNext = (int)(-logf(uu) * (float)g_busRate / rate);
                if (r->clkNext < 1) r->clkNext = 1;
                r->rng = r->rng * 1664525u + 1013904223u;
                float sgn = ((r->rng >> 8) & 1u) ? 1.0f : -1.0f;
                r->rng = r->rng * 1664525u + 1013904223u;
                float mag = 0.4f + 0.6f * ((float)(r->rng >> 8) * (1.0f / 16777216.0f));
                r->clk = sgn * mag * g_recvClickAmt * (nAmp > 0.05f ? nAmp : 0.05f);
            }
            r->clk *= g_recvClickDecay;
            nz += r->clk;
        }

        /* THE PROGRAMME IS BURIED, NOT FADED. g_recvFloorDb is five decibels on purpose -- the
         * comment above it is the whole finding, and it applies identically here. */
        float bury = powf(10.0f, -(1.0f - q) * g_recvFloorDb * 0.05f);
        float y = lane[f] * bury + nz;

        /* Shared hi-cut, keyed to ramp position exactly as the bus stage keys it. */
        if (u < 0.9995f && g_recvDull > 0.001f) {
            float t = 1.0f - (u - g_recvWinA) / (g_recvWinB - g_recvWinA);
            if (t < 0.0f) t = 0.0f; else if (t > 1.0f) t = 1.0f;
            t *= g_recvDull;
            float fc = 15000.0f * powf(g_recvHiFloor / 15000.0f, t);
            float ny = (float)g_busRate * 0.45f;
            if (fc > ny) fc = ny;
            float k = 1.0f - expf(-6.2831853f * fc / (float)g_busRate);
            r->lp = mix_undenorm(r->lp + k * (y - r->lp));
            y = r->lp;
        } else r->lp = y;

        lane[f] = y;
    }
}

/* THE RECEIVER. One of them. Runs whether or not a station voice exists -- which is the whole
 * point, because the window between "the old station has gone" and "the new one has opened" is
 * the longest and most exposed part of a dial press, and it used to be filled by a completely
 * different noise generator. The programme arrives in g_radioBus already mangled and levelled;
 * everything from here down is shared, because in a real set the hiss and the programme come out
 * of the same detector and cannot have different bandwidths or different stereo images. */
static void render_receiver_add(int n) {
    int f;
    if (!g_recvActive) {
        /* Not armed. THE PROGRAMME STILL HAS TO COME OUT. Returning here instead of summing was
         * the bug that made this "completely broken": radio voices contribute to g_radioBus and
         * nothing else, so an unarmed receiver silently threw their audio away -- and the arm was
         * being pushed from a spot in Tick() that sits below seven early returns, so during
         * ordinary playback it barely ran. Anything that routes audio somewhere must sum it back
         * unconditionally; gating a ROUTE on a state flag is how you lose audio, gating an EFFECT
         * on one is fine.
         *
         * Reset the parts that would otherwise resume mid-roar on the next power-on; leave the
         * RNG alone so successive tunes do not repeat the same noise. */
        g_recvLp[0] = g_recvLp[1] = 0.0f;
        g_recvNzLp = g_recvNzLp2 = g_recvClk = 0.0f;
        g_recvLocked = 0; g_recvLockW = 0.0f;
        for (f = 0; f < n; f++) {
            int o = f * g_busChannels;
            g_bus[o]     += g_radioBus[o];
            g_bus[o + 1] += g_radioBus[o + 1];
        }
        return;
    }
    for (f = 0; f < n; f++) {
        int o = f * g_busChannels;
        float u = g_recvU[f];
        float q;
        if      (u <= 0.0005f) q = 0.0f;
        else if (u >= 0.9995f) q = 1.0f;
        else q = 1.0f / (1.0f + expf(-(u - g_recvKnee) * (4.0f / g_recvKneeW)));
        float snr = -4.0f + 64.0f * q;
        int rising = (g_recvSigStep > 0.0f);

        /* WANDER: a slow random walk on the noise level -- a real AGC breathing as the signal
         * fades, and why interstation noise never sits still. FLUTTER on top, from BAND-LIMITED
         * NOISE rather than an oscillator: two fixed sines gave the wobble a rate, and a rate is
         * a metronome you can hear counting. */
        if (--g_recvWHold <= 0) {
            g_recvRng = g_recvRng * 1664525u + 1013904223u;
            float uw = (float)(g_recvRng >> 8) * (1.0f / 16777216.0f);
            g_recvWTgt = 1.0f + g_recvWander * (uw * 2.0f - 1.0f);
            if (g_recvWTgt < 0.05f) g_recvWTgt = 0.05f;
            g_recvRng = g_recvRng * 1664525u + 1013904223u;
            float uh = (float)(g_recvRng >> 8) * (1.0f / 16777216.0f);
            g_recvWHold = (int)((float)g_busRate * (0.18f + 0.9f * uh));
            if (g_recvWHold < 1) g_recvWHold = 1;
        }
        g_recvWCur += (g_recvWTgt - g_recvWCur) * 0.00012f;
        g_recvRng = g_recvRng * 1664525u + 1013904223u;
        float fw = (float)(int)g_recvRng * (1.0f / 2147483648.0f);
        g_recvFA = mix_undenorm(g_recvFA + g_recvFlutK * (fw - g_recvFA));
        g_recvFB = mix_undenorm(g_recvFB + g_recvFlutSlowK * (g_recvFA - g_recvFB));
        float fmod = (g_recvFA - g_recvFB) * 46.0f;
        if (fmod > 1.4f) fmod = 1.4f; else if (fmod < -1.4f) fmod = -1.4f;
        float flut = 1.0f + g_recvFlutter * fmod;
        if (flut < 0.05f) flut = 0.05f;

        /* THE HISS MUST OUTLAST THE TUNING. Tied to the signal alone it was gone while the
         * station was still opening up, leaving clean music fading in with nothing over it --
         * and a fade with nothing over it is just a fade. Plus a residual bed that never quite
         * goes: hitting absolute zero says "sound effect ended", not "receiver". */
        float nLvl = powf(10.0f, -snr * 0.05f);
        if (g_recvNoiseHold > 0.001f && rising) {
            float na = 0.22f, nb = 0.55f + 0.42f * g_recvNoiseHold;
            float nf = 1.0f - (u - na) / (nb - na);
            if (nf < 0.0f) nf = 0.0f; else if (nf > 1.0f) nf = 1.0f;
            nf = nf * nf * (3.0f - 2.0f * nf);
            float held = g_recvNoiseFloorR + (1.0f - g_recvNoiseFloorR) * nf;
            if (held > nLvl) nLvl = held;
        }
        float nAmp = nLvl * g_recvNoiseTrim * g_recvWCur * flut;
        if (nAmp > 1.6f) nAmp = 1.6f;

        g_recvRng = g_recvRng * 1664525u + 1013904223u;
        float wn = (float)(int)g_recvRng * (1.0f / 2147483648.0f);
        /* De-emphasis, NOISE ONLY -- it is a matched pair, so for the programme it cancels and
         * only the noise eats the curve. With no carrier the demodulator's noise is FLAT, so
         * after this it is a dark, full ROAR; a weak carrier gives a bright thin hiss instead.
         * The off-station sound being the DARKER of the two is the opposite of most people's
         * intuition and much of why synthetic versions do not convince. */
        g_recvNzLp = mix_undenorm(g_recvNzLp + g_recvDeemK * (wn - g_recvNzLp));
        g_recvNzLp2 = mix_undenorm(g_recvNzLp2 + g_recvDeemK * (g_recvNzLp - g_recvNzLp2));
        float nz = g_recvNzLp2 * nAmp * 2.0f;      /* x2: two poles cost about 6 dB */

        /* CLICKS. Below threshold the noise phasor overtakes the carrier and every overtake is a
         * phase step the demodulator renders as a tick -- isolated at first, merging into a
         * rattle as the signal goes. EVENTS: additive and bipolar, rising ABOVE the bed. */
        if (g_recvClickAmt > 0.001f && snr < 14.0f) {
            if (--g_recvClkNext <= 0) {
                float rate = g_recvClickMax * powf(10.0f, -(snr + 4.0f) * 0.1667f);
                if (rate < 0.05f) rate = 0.05f;
                g_recvRng = g_recvRng * 1664525u + 1013904223u;
                float uu = (float)(g_recvRng >> 8) * (1.0f / 16777216.0f);
                if (uu < 1e-4f) uu = 1e-4f;
                g_recvClkNext = (int)(-logf(uu) * (float)g_busRate / rate);
                if (g_recvClkNext < 1) g_recvClkNext = 1;
                g_recvRng = g_recvRng * 1664525u + 1013904223u;
                float sgn = ((g_recvRng >> 8) & 1u) ? 1.0f : -1.0f;
                g_recvRng = g_recvRng * 1664525u + 1013904223u;
                float mag = 0.4f + 0.6f * ((float)(g_recvRng >> 8) * (1.0f / 16777216.0f));
                g_recvClk = sgn * mag * g_recvClickAmt * (nAmp > 0.05f ? nAmp : 0.05f);
            }
            g_recvClk *= g_recvClickDecay;
            nz += g_recvClk;
        }

        /* --- THE SUM. From here down there is one signal. ---------------------------------
         * THE LEVEL SCALES THE NOISE ONLY. The programme arrived in g_radioBus already carrying
         * its voice gain -- crossfade x loudness -- so multiplying the SUM by the same loudness
         * figure applied it to the programme a second time: the station came out squared, a
         * median 5 dB below where it belonged and moving with every track. The noise is the part
         * with no voice to inherit from, so the noise is the part that has to be told. */
        float l = g_radioBus[o] + nz * g_recvLevel;
        float r = g_radioBus[o + 1] + nz * 0.85f * g_recvLevel;   /* noise is worse in the side */

        /* SHARED hi-cut, 15 kHz down to g_recvHiFloor, keyed to RAMP POSITION rather than SNR --
         * snr saturates near its maximum long before the station is merely audible, so anything
         * read off it fires early and lands inside the knee, which made the treble a step. */
        if (u < 0.9995f && g_recvDull > 0.001f) {
            float t = 1.0f - (u - g_recvWinA) / (g_recvWinB - g_recvWinA);
            if (t < 0.0f) t = 0.0f; else if (t > 1.0f) t = 1.0f;
            t *= g_recvDull;
            float fc = 15000.0f * powf(g_recvHiFloor / 15000.0f, t);
            float ny = (float)g_busRate * 0.45f;
            if (fc > ny) fc = ny;
            float k = 1.0f - expf(-6.2831853f * fc / (float)g_busRate);
            g_recvLp[0] = mix_undenorm(g_recvLp[0] + k * (l - g_recvLp[0])); l = g_recvLp[0];
            g_recvLp[1] = mix_undenorm(g_recvLp[1] + k * (r - g_recvLp[1])); r = g_recvLp[1];
        } else { g_recvLp[0] = l; g_recvLp[1] = r; }

        /* A DECODER LATCHES; IT DOES NOT GLIDE, and it latches on ramp position -- keyed to snr
         * it fired while the station was still buried, so the station arrived already in stereo,
         * which is the one thing a real decoder never does. Stereo is the LAST thing to come
         * good, after the treble. Hysteresis: a lock is harder to acquire than to keep. And no
         * decoder clunk -- authentic, and measured as the largest discontinuity in the whole
         * transition, larger than any musical transient. */
        if (g_recvMono > 0.001f) {
            if (g_recvLocked) { if (u < g_recvLockU - 0.14f) g_recvLocked = 0; }
            else if (u > g_recvLockU) g_recvLocked = 1;
            float kSnap = 1.0f - expf(-1.0f / (0.001f * g_recvLockMs * (float)g_busRate));
            g_recvLockW = mix_undenorm(g_recvLockW + ((g_recvLocked ? 1.0f : 0.0f) - g_recvLockW) * kSnap);
            float w = 1.0f - g_recvMono * (1.0f - g_recvLockW);
            float m = 0.5f * (l + r), sd = 0.5f * (l - r) * w;
            l = m + sd; r = m - sd;
        }

        /* The receiver's own volume. The programme carried its voice gain in, but the NOISE has
         * no voice to inherit from -- and between stations there is no voice at all -- so the
         * radio's level (context volume x duck) is pushed in from managed and applied here. */
        g_bus[o]     += l;
        g_bus[o + 1] += r;
    }
}

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
        /* --- THE RECEIVER --------------------------------------------------------------
         * ONE SIGNAL, NOT TWO. The noise is generated HERE, inside the station's own path, and
         * summed with the programme BEFORE the shared low-pass and the shared width. That is the
         * whole difference between "a radio going out of tune" and "a hiss fading up next to some
         * music", and the previous version was the second one: the static was a separate voice,
         * band-limited to a fixed 50-2100 Hz, dual-mono, its level stepped at 4 Hz, summed at the
         * bus. Measured, the two sat FOUR OCTAVES apart at the moment they were supposed to be
         * trading places (noise centroid 3651 Hz, station 177 Hz at s=0.1) -- the ear separates
         * sources on exactly that cue, so it heard two objects and was right to.
         *
         * Everything below the sum is shared, which is the point. One filter corner, moving at
         * one instant, over both. One width. In a receiver the hiss and the programme come out of
         * the same detector; they cannot have different bandwidths or different stereo images,
         * and hi-cut trading treble for hiss is a trade you are supposed to HEAR.
         *
         * The one stage that must NOT be shared is de-emphasis. It is a matched pair -- the
         * transmitter pre-emphasises and the receiver de-emphasises, so for the programme they
         * cancel and only the noise, which was never pre-emphasised, eats the curve. So the noise
         * gets its own one-pole and the programme does not. */
        if (v->recvOn && !emit) {
            /* PROGRAMME ONLY. The noise, the clicks, the hi-cut and the width all moved to the
             * bus stage -- one receiver, not one per station. What is left here are the stages
             * that act on THIS voice's audio: the level law and the mangle. */
            float u = g_recvU[f];
            float q;
            if      (u <= 0.0005f) q = 0.0f;
            else if (u >= 0.9995f) q = 1.0f;
            else q = 1.0f / (1.0f + expf(-(u - g_recvKnee) * (4.0f / g_recvKneeW)));
            float snr = -4.0f + 64.0f * q;
            int rising = (g_recvSigStep > 0.0f);

            /* PROGRAMME LEVEL BARELY MOVES -- soft mute only, and a real chip's rule is 2 dB of
             * cut per dB of SNR below 4, floored well before the noise takes over anyway. */
            float mute = (g_recvFloorDb / g_recvMuteW) * (14.0f - snr);
            if (mute < 0.0f) mute = 0.0f; else if (mute > g_recvFloorDb) mute = g_recvFloorDb;
            float progG = mute > 0.0f ? powf(10.0f, -mute * 0.05f) : 1.0f;
            float pl = g_voiceStereo[o] * progG, pr = g_voiceStereo[o + 1] * progG;

            /* ===== THE MUSIC ITSELF CHANGES SHAPE ==========================================
             * Every earlier attempt added things ON TOP of a full-fidelity source, and a
             * full-fidelity source with things on top of it is precisely what it sounded like:
             * "I get no sense that we're tuning into anything." A weak station is not clean audio
             * under noise -- it is MANGLED audio. Squeezed at both ends by a narrow IF and a small
             * speaker, driven into distortion, and flattened by the station's own processing on
             * top of the receiver's AGC. Broadcast has no micro-dynamics at all, and micro-dynamics
             * are a large part of what "full fidelity" sounds like.
             *
             * Order is load-bearing. The low cut comes BEFORE the saturator: with bass still in it
             * a soft clipper only makes mush; with the bass gone the same drive becomes midrange
             * grit. That is the whole difference between "distorted" and "muddy", and it is why
             * g_recvDrive at 0.15 had been inaudible -- it was working on a full-range signal.
             *
             * Two poles at each end, not one. A one-pole is a 6 dB/oct tilt, which is a tone
             * control; two is an EDGE, which is a bandwidth. Every previous EQ round used one pole
             * and was heard, correctly, as an EQ.
             *
             * ARRIVAL ONLY -- the departure was already right and must not move. */
            if (g_recvRadio > 0.001f && rising) {
                float ra = 1.0f - (u - g_recvWinA) / (g_recvWinB - g_recvWinA);
                if (ra < 0.0f) ra = 0.0f; else if (ra > 1.0f) ra = 1.0f;
                ra = ra * ra * (3.0f - 2.0f * ra) * g_recvRadio;

                float fhp = 20.0f * powf(g_recvRadioHp / 20.0f, ra);
                float kh = 1.0f - expf(-6.2831853f * fhp / (float)g_busRate);
                v->recvMhp[0] = mix_undenorm(v->recvMhp[0] + kh * (pl - v->recvMhp[0]));
                { float a = pl - v->recvMhp[0];
                  v->recvMhp[2] = mix_undenorm(v->recvMhp[2] + kh * (a - v->recvMhp[2]));
                  pl = a - v->recvMhp[2]; }
                v->recvMhp[1] = mix_undenorm(v->recvMhp[1] + kh * (pr - v->recvMhp[1]));
                { float b = pr - v->recvMhp[1];
                  v->recvMhp[3] = mix_undenorm(v->recvMhp[3] + kh * (b - v->recvMhp[3]));
                  pr = b - v->recvMhp[3]; }

                { float dr = 1.0f + ra * 9.0f;              /* grit, not mud: the bass has gone */
                  pl = tanhf(pl * dr) / dr; pr = tanhf(pr * dr) / dr; }

                { float flp = 15000.0f * powf(g_recvRadioLp / 15000.0f, ra);
                  float ny = (float)g_busRate * 0.45f; if (flp > ny) flp = ny;
                  float kl = 1.0f - expf(-6.2831853f * flp / (float)g_busRate);
                  v->recvMlp[0] = mix_undenorm(v->recvMlp[0] + kl * (pl - v->recvMlp[0]));
                  v->recvMlp[2] = mix_undenorm(v->recvMlp[2] + kl * (v->recvMlp[0] - v->recvMlp[2]));
                  pl = v->recvMlp[2];
                  v->recvMlp[1] = mix_undenorm(v->recvMlp[1] + kl * (pr - v->recvMlp[1]));
                  v->recvMlp[3] = mix_undenorm(v->recvMlp[3] + kl * (v->recvMlp[1] - v->recvMlp[3]));
                  pr = v->recvMlp[3]; }

                { float av = fabsf(pl) > fabsf(pr) ? fabsf(pl) : fabsf(pr);
                  float ka = 1.0f - expf(-1.0f / (0.005f * (float)g_busRate));
                  float kr = 1.0f - expf(-1.0f / (0.060f * (float)g_busRate));
                  v->recvCEnv = mix_undenorm(v->recvCEnv + (av > v->recvCEnv ? ka : kr) * (av - v->recvCEnv));
                  if (v->recvCEnv > 0.06f) {
                      float gg = powf(0.06f / v->recvCEnv, 0.875f * ra);   /* 8:1 */
                      pl *= gg; pr *= gg;
                  } }

                /* Makeup. Taking out everything below 400 Hz and above 2.6 kHz costs real level,
                 * and a station that gets QUIETER as it arrives is the fade that four rounds were
                 * spent killing. Put it back -- measured flat to within half a dB of lock. */
                { float mk = powf(10.0f, g_recvRadioMk * ra * 0.05f); pl *= mk; pr *= mk; }
            }

            if (g_recvDrive > 0.001f) {
                /* Damage, not just dulling: clean music behind a blanket reads as a fader however
                 * dull you make it. tanh(x*d)/d, so quiet parts pass at UNITY and only loud ones
                 * get squashed -- an earlier version compensated with a hand-picked curve
                 * unrelated to the drive and put the station through fourteen decibels hot.
                 *
                 * WET/DRY, and it is not cosmetic. `d` falls to 1 as the station locks, and
                 * tanh(x)/1 IS NOT A BYPASS -- it is a soft clipper with its knee at unity. So
                 * this stage never switched off: every radio track, fully tuned, sat under a
                 * permanent waveshaper, and the damage scaled with how hot the track was.
                 * Measured on a brickwalled master playing unnormalised (peak 1.0): 6.7% THD and
                 * the output peak pinned flat at tanh(1) = 0.762 in every window. It happens
                 * BEFORE the master, so turning the volume down did not remove it -- which is
                 * what "overdriven" sounds like as opposed to "too loud".
                 *
                 * Blending by the same `amt` that opens `d` makes the stage an EXACT identity at
                 * full lock and leaves it untouched at full static (amt = 1, d = 8), so the
                 * tuning character that was settled by ear is preserved at the end that was
                 * being judged. */
                float amt = 1.0f - q;
                if (amt > 0.0005f) {
                    float d = 1.0f + g_recvDrive * 7.0f * amt;
                    pl += amt * (tanhf(pl * d) / d - pl);
                    pr += amt * (tanhf(pr * d) / d - pr);
                }
            }
            g_voiceStereo[o] = pl; g_voiceStereo[o + 1] = pr;
        }
        if (emit) {
            /* Dry mono into this voice's SLOT lane (accumulated: two voices sharing a slot during
             * a track-advance crossfade sum; different stations land in different lanes). */
            g_emitterMono[(size_t)v->emitSlot * BUS_MAXFRAMES + f] +=
                0.5f * (g_voiceStereo[o] + g_voiceStereo[o + 1]) * g;
        } else if (v->recvOn) {
            /* Radio programme goes to the receiver's own bus, never straight out: the noise has
             * to be summed with it BEFORE the shared filter and the shared width, or they are two
             * objects again. */
            g_radioBus[o]     += g_voiceStereo[o]     * g;
            g_radioBus[o + 1] += g_voiceStereo[o + 1] * g;
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
    /* The receiver's ramp used to need catching up here when a voice under-produced. It is a
     * mixer-level ramp now, advanced once per block regardless of what any voice managed to
     * decode, so there is nothing left to keep in step. */

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

/* v40 — publish an ALREADY-OPENED device (the producer opened it outside g_lock). Ring is
 * allocated at the MAX depth; the live cushion floats between tight and max. Caller holds
 * the lock. */
static void device_publish_locked(cmm_out* o) {
    g_lowWater = g_tightFill / 2;
    if (g_lowWater < g_busRate / 50) g_lowWater = g_busRate / 50;   /* >=20ms */
    g_targetFill = g_tightFill;
    g_flushUntilMs = mix_now_ms() + FLUSH_GRACE_MS;
    g_prevDeep = g_deep;
    g_outGainTarget = 1.0f;
    /* Snap the slewed master to its target on device attach: the first block of a fresh device
     * must open AT the level managed already pushed, not ramp up to it from the last device's
     * residual value (which would fade the first ~25ms of every track start). */
    g_masterApplied = g_masterVol;
    g_emMasterApplied = g_emMasterVol;
    g_out = o;
}

/* v40 — retire every voice and DETACH the device (returned for the caller — the producer —
 * to close outside g_lock; cmm_out_close blocks on the audio backend). Caller holds the lock. */
static cmm_out* teardown_detach_locked(void) {
    int i;
    cmm_out* out = g_out;
    for (i = 0; i < g_voiceCount; i++) { retire_voice(g_voices[i]); free(g_voices[i]); }
    g_voiceCount = 0;
    memset(g_sfx, 0, sizeof g_sfx);   /* a half-rendered click must not resume into the NEXT device's first block */
    g_out = NULL;
    return out;
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
            } else if (v->pcm) {
                /* Recorded click, resampled by linear interpolation. The step is fixed when the
                 * voice starts, so one firing is a consistent pitch -- it is the NEXT one that
                 * differs. Varying it within a single click would be a wobble, not a variation. */
                double pp = v->pcmPos;
                int i0 = (int)pp;
                if (i0 >= v->pcmLen - 1) { x = 0.0f; v->pos = v->len; }
                else {
                    float fr = (float)(pp - i0);
                    x = v->pcm[i0] * (1.0f - fr) + v->pcm[i0 + 1] * fr;
                }
                v->pcmPos = pp + v->pcmStep;
            } else {
                /* Power click (v39: a dial detent): up to TWO micro-impacts — the spring release
                 * and, ~12ms later, the stop landing — each firing a bright noise ping, a low
                 * thump, and a knock into the BODY RESONATOR (damped two-pole ring at BodyHz,
                 * the dial's shell). Everything tone-capped by one LPF. Body/Double at 0 = the
                 * plain ping+thump click. */
                x = sfx_click_sample(v, noise);
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
        cmm_out* toClose = NULL;
        int wantOpen = 0, openRate = 0, openCh = 0, openMaxFill = 0, openHeadless = 0;

        mix_lock(&g_lock);
        if (g_reinitRequested) {
            g_reinitRequested = 0;
            /* Detach under the lock, close+reopen OUTSIDE it (both block on the backend). */
            if (g_out) { toClose = g_out; g_out = NULL; g_deviceWanted = 1; }
        }
        if (g_deviceWanted) {
            g_deviceWanted = 0;   /* one attempt per request — no open storm without an endpoint */
            if (g_out == NULL) {
                wantOpen = 1;
                openRate = g_busRate; openCh = g_busChannels;
                openMaxFill = g_maxFill; openHeadless = g_headless;
            }
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
            /* Tell the slot rings how deep to sit. They are written from THIS deficit, so without
             * being told they inherit zero cushion — see cmm_out_write_emitter. */
            cmm_out_set_emitter_prime(dev, target);
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

        /* v40 — device lifecycle, outside g_lock: a wedged ma_device_init/uninit now stalls
         * only this thread (which has nothing to render without a device) while every
         * g_lock ABI on the game's threads keeps returning immediately. */
        if (toClose) cmm_out_close(toClose);
        if (wantOpen) {
            int err = 0;
            cmm_out* o = openHeadless ? cmm_out_open_null(openRate, openCh, openMaxFill, &err)
                                      : cmm_out_open(openRate, openCh, openMaxFill, &err);
            if (o) {
                mix_lock(&g_lock);
                device_publish_locked(o);
                mix_unlock(&g_lock);
                continue;   /* re-enter with the fresh device — no 5ms idle sleep first */
            }
        }

        if (!dev) { mix_sleep_ms(5); continue; }
        if (queued >= target || freeFrames < 64) { mix_sleep_ms(2); continue; }

        int n = target - queued;
        if (n > freeFrames)    n = freeFrames;
        if (n > BUS_MAXFRAMES) n = BUS_MAXFRAMES;
        if (n < 1) { mix_sleep_ms(2); continue; }

        memset(g_bus, 0, (size_t)n * g_busChannels * sizeof(float));
        memset(g_radioBus, 0, (size_t)n * g_busChannels * sizeof(float));
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
            receiver_advance(n);     /* ONE ramp, before anybody reads it */
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

            /* The receiver sums the radio programme out, with its noise, after every voice
             * has contributed and BEFORE the UI SFX -- the clicks are the dial's own body, not
             * something coming out of the speaker, so they do not go through the detector. */
            render_receiver_add(n);

            /* Radio UI SFX (v35) sum in after the voices — same bus, same master, but never
             * part of the crossfade/duck/eviction machinery above. */
            render_sfx_add(n);

            /* Relay each slot's live spatial state to the output for consumer-side spatialization.
             * v54: read the SLOT's own point set. This used to hunt for the slot's newest emitter
             * voice and read the points off that, which meant a voiceless slot relayed count 0 and
             * was therefore silent -- correct while a slot could only ever reproduce a track, and
             * wrong now that it can also be a receiver searching between stations. A slot is
             * relayed whenever it HAS SPEAKERS, whether or not anything is playing through them.
             * dev is valid here (we hold the lock and any closeWhenIdle teardown runs below). */
            if (dev) {
                int s;
                float lp[3] = { g_lisPos[0], g_lisPos[1], g_lisPos[2] };
                float lr[3] = { g_lisRight[0], g_lisRight[1], g_lisRight[2] };
                float wets[CMM_MAX_EMIT_VOICES], depths[CMM_MAX_EMIT_VOICES];
                int any = 0;
                for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
                    if (g_emSlotCount[s] > 0) {
                        any = 1;
                        /* min/max come from the LIVE tuning globals (not the voice's baked-in values)
                         * so range + plateau are hot-tunable; falloff + muffle likewise. */
                        cmm_out_set_emitter_occl_tone(dev, g_emOcclTone, g_emOcclFullDb);
                        cmm_out_set_emitter_slew(dev, g_emOcclSlew);
                        cmm_out_set_emitter_spatial(dev, s, lp, lr, g_emSlotPts[s], g_emSlotGain[s],
                                                    g_emSlotFlags[s], g_emSlotCount[s],
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
        cmm_out* idleDetached = NULL;
        /* A SEARCHING RECEIVER IS SOMETHING PLAYING, even though it is not a voice. g_voiceCount
         * alone tore the device down underneath a sweep: the borrow renders hiss on a slot with
         * NO voice by design, so the last real voice retiring during one took the output with it.
         * Nothing cleared g_emRx[].armed, so the managed side saw a perfectly healthy borrow --
         * timers running, log lines firing -- for its full duration while nothing rendered at all.
         * Then the arriving station's cmm_mix_add_emitter reopened the device and the station
         * landed with no sweep in front of it: "static doesn't come in at all, and stations tune
         * in immediately". Measured headless at -999 dBFS with the borrow armed and no voice.
         *
         * `armed` is the exact term the lane gate below uses to decide a voiceless slot renders,
         * so testing it here makes the teardown agree with the renderer. Deliberately NOT
         * g_emSlotCount: a stopped station can leave points published, and keying on those would
         * hold the device open for ever. */
        if (g_closeWhenIdle && g_voiceCount == 0 && !emitters_armed()) {
            idleDetached = teardown_detach_locked();
            g_closeWhenIdle = 0;
            closeIdle = 1;
        }
        mix_unlock(&g_lock);

        if (closeIdle) {
            if (idleDetached) cmm_out_close(idleDetached);   /* blocks on the backend — outside g_lock */
            continue;   /* device gone; loop picks up dev==NULL */
        }

        /* Master volume + hard clip-guard (raw f32 device wraps past 0dBFS). The master is
         * SLEWED per frame toward its target rather than applied as a per-block constant —
         * see g_masterApplied. A block whose gain is already settled costs one compare. */
        float master = g_masterVol;
        int total = n * g_busChannels, i;
        if (g_masterApplied == master) {
            for (i = 0; i < total; i++) {
                float s = g_bus[i] * master;
                g_bus[i] = s > 1.0f ? 1.0f : (s < -1.0f ? -1.0f : s);
            }
        } else {
            /* Max gain change per FRAME (not per sample — every channel of a frame must share
             * one gain or the stereo image shifts during the ramp). */
            float step = 1.0f / (MASTER_SLEW_MS * 0.001f * (float)g_busRate);
            float cur = g_masterApplied;
            int f, ch;
            for (f = 0; f < n; f++) {
                float d = master - cur;
                if (d > step)       cur += step;
                else if (d < -step) cur -= step;
                else                cur = master;   /* arrived: exact, no residual creep */
                for (ch = 0; ch < g_busChannels; ch++) {
                    int k = f * g_busChannels + ch;
                    float s = g_bus[k] * cur;
                    g_bus[k] = s > 1.0f ? 1.0f : (s < -1.0f ? -1.0f : s);
                }
            }
            g_masterApplied = cur;
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
            float emStart  = g_emMasterApplied;
            int s;
            /* SLEWED like the music master (see g_masterApplied) — the emitter master now tracks
             * the game's Ambient/Overall sliders, so dragging one steps this value and an
             * unramped step is an audible click. Every slot must ramp from the SAME start, so
             * the end value is computed once here rather than carried out of the slot loop. */
            float emStep = 1.0f / (MASTER_SLEW_MS * 0.001f * (float)g_busRate);
            float emEnd  = emStart;
            if (emStart != emMaster) {
                float d = emMaster - emStart, span = emStep * (float)n;
                if (d > span)       emEnd = emStart + span;
                else if (d < -span) emEnd = emStart - span;
                else                emEnd = emMaster;   /* arrives within this block */
            }
            /* MIRROR BEFORE THE RECEIVERS, AFTER THE VOICES. Every voice has landed in its own
             * lane by now and no receiver has run, so this is the one moment a lane holds clean
             * dry audio. Copy it, and the two lanes then take their own receivers below --
             * the source lane keeps playing untouched for everyone on it, while the copy is the
             * retuned speaker's alone to duck, hiss over and bring back up. */
            for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
                int src = g_emMirror[s];
                if (src < 0 || src >= CMM_MAX_EMIT_VOICES) continue;
                if (g_emMirror[src] >= 0) continue;          /* one level deep only */
                {
                    float* dstL = g_emitterMono + (size_t)s * BUS_MAXFRAMES;
                    const float* srcL = g_emitterMono + (size_t)src * BUS_MAXFRAMES;
                    float a = g_emMirrorAmp[s], st = g_emMirrorStp[s], tg = g_emMirrorTgt[s];
                    if (st == 0.0f && a == 1.0f) memcpy(dstL, srcL, (size_t)n * sizeof(float));
                    else {
                        for (i = 0; i < n; i++) {
                            if (st != 0.0f) {
                                a += st;
                                if ((st > 0.0f && a >= tg) || (st < 0.0f && a <= tg)) { a = tg; st = 0.0f; }
                            }
                            dstL[i] = srcL[i] * a;
                        }
                        g_emMirrorAmp[s] = a; g_emMirrorStp[s] = st;
                    }
                }
                emActive[s] = emActive[src];                 /* renders exactly when its source does */
            }

            for (s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
                /* An ARMED slot renders even with no voice: that is a receiver searching, and its
                 * lane is pure noise. The lanes are memset at the top of every block, so a
                 * voiceless armed slot starts from silence and the stage writes the hiss into it
                 * -- no stale audio, no special case. Unarmed and voiceless stays skipped, so an
                 * idle station's ring keeps its onset free of a cushion of zeros. */
                if (!emActive[s] && !g_emRx[s].armed && !em_tone_live(s)) {
                    /* A SKIPPED LANE IS SILENT, AND MUST SAY SO. g_emLaneRms is only written
                     * where a lane renders, so a skipped one held its last value for ever and
                     * the readout reported -23.8 dBFS from a lane with no voice, no receiver,
                     * no mirror and no carrier. A diagnostic that keeps reporting the past is
                     * worse than none: it sends you looking for audio that stopped minutes ago. */
                    g_emLaneRms[s] = 0.0f;
                    continue;
                }
                float* lane = g_emitterMono + (size_t)s * BUS_MAXFRAMES;
                static float toneBuf[BUS_MAXFRAMES];
                int toneOn = 0;
                /* THE CARRIER IS ADDED, NOT SUBSTITUTED.
                 *
                 * It used to REPLACE the lane, so the tone and the search could never overlap and
                 * a set that found nothing had to cut from one to the other. What it should do --
                 * and what a real receiver does -- is hold the hiss while it hunts, bring the
                 * carrier up underneath it, and then let the hiss fall away, leaving the tone.
                 * Three things at once, which substitution cannot express.
                 *
                 * Additive costs nothing and makes the whole sequence available: the receiver runs
                 * first (at signal 0 that is pure noise, since a carrier lane has no voice), the
                 * tone is summed on top, and taking the signal to 1 removes the noise and leaves
                 * the tone alone. It also retires the "armed outranks the tone" special case,
                 * which existed only because the two were fighting over one lane. */
                if (em_tone_live(s)) {
                    /* OFF AIR. Replaces the lane outright -- programme and hiss both -- because it
                     * is not a layer over a station, it is what is there instead of one. */
                    int k;
                    float inc[EM_TONE_MAX];
                    for (k = 0; k < g_emToneN[s] && k < EM_TONE_MAX; k++)
                        inc[k] = 6.28318531f * g_emToneHz[s][k] / (float)g_busRate;
                    for (i = 0; i < n; i++) {
                        float a = g_emToneAmp[s], v = 0.0f;
                        if (g_emToneStep[s] != 0.0f) {
                            a += g_emToneStep[s];
                            if ((g_emToneStep[s] > 0.0f && a >= g_emToneTgt[s]) ||
                                (g_emToneStep[s] < 0.0f && a <= g_emToneTgt[s])) { a = g_emToneTgt[s]; g_emToneStep[s] = 0.0f; }
                            g_emToneAmp[s] = a;
                        }
                        for (k = 0; k < g_emToneN[s] && k < EM_TONE_MAX; k++) {
                            if (g_emToneHz[s][k] <= 0.0f) continue;
                            v += sinf(g_emTonePh[s][k]) * g_emToneLvl[s][k];
                            g_emTonePh[s][k] += inc[k];
                            if (g_emTonePh[s][k] > 6.28318531f) g_emTonePh[s][k] -= 6.28318531f;
                        }
                        toneBuf[i] = v * a;
                    }
                    toneOn = 1;
                }
                /* THE RECEIVER, BEFORE THE MASTER. Running it here rather than after means the
                 * hiss goes through everything the programme goes through -- distance, occlusion,
                 * the door model, the room reverb, the speaker's own coloration -- because from
                 * this point down the lane IS the speaker. Put it after the master and it would
                 * be a noise laid over the mix instead of a sound coming out of a radio. */
                emitter_receiver_mono(s, lane, n);
                /* ...and the carrier ON TOP of it, so the two can overlap: hiss while hunting,
                 * tone rising underneath, then the hiss falling away to leave the tone. */
                if (toneOn) for (i = 0; i < n; i++) lane[i] += toneBuf[i];
                if (emStart == emMaster) {
                    for (i = 0; i < n; i++) lane[i] *= emMaster;
                } else {
                    float cur = emStart;
                    for (i = 0; i < n; i++) {
                        float d = emMaster - cur;
                        if (d > emStep)       cur += emStep;
                        else if (d < -emStep) cur -= emStep;
                        else                  cur = emMaster;
                        lane[i] *= cur;
                    }
                }
                {   /* the lane's own level, measured where it is still one thing */
                    double sum = 0; int k;
                    for (k = 0; k < n; k++) sum += (double)lane[k] * lane[k];
                    g_emLaneRms[s] = n > 0 ? (float)sqrt(sum / n) : 0.0f;
                }
                cmm_out_write_emitter(dev, s, lane, n);
            }
            g_emMasterApplied = emEnd;
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
    g_masterApplied = 1.0f;
    g_emMasterVol = 1.0f;
    g_emMasterApplied = 1.0f;
    g_outGainTarget = 1.0f;
    g_outGainHold = 0;
    g_minQueued   = 0x7fffffff;
    g_underruns   = 0;
    g_wasEmpty    = 0;

    g_bus         = (float*)malloc((size_t)BUS_MAXFRAMES * g_busChannels * sizeof(float));
    g_voiceStereo = (float*)malloc((size_t)BUS_MAXFRAMES * g_busChannels * sizeof(float));
    g_convTmp     = (float*)malloc((size_t)BUS_MAXFRAMES * g_busChannels * sizeof(float));
    /* NO MIRRORS UNTIL ASKED. Static storage zeroes, and zero is a VALID slot index -- left
     * alone, every lane would claim to mirror lane 0. */
    { int mi; for (mi = 0; mi < CMM_MAX_EMIT_VOICES; mi++) {
          g_emMirror[mi] = -1;
          g_emMirrorAmp[mi] = g_emMirrorTgt[mi] = 1.0f;   /* full unless asked otherwise */
          g_emMirrorStp[mi] = 0.0f;
      } }
    g_emitterMono = (float*)malloc((size_t)CMM_MAX_EMIT_VOICES * BUS_MAXFRAMES * sizeof(float));
    g_radioBus    = (float*)malloc((size_t)BUS_MAXFRAMES * g_busChannels * sizeof(float));
    g_recvU       = (float*)malloc((size_t)BUS_MAXFRAMES * sizeof(float));
    if (!g_bus || !g_voiceStereo || !g_convTmp || !g_emitterMono || !g_radioBus || !g_recvU)
    { if (err) *err = -2; return -2; }

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
/* v49 -- the headless pump, but it gives you the audio. cmm_mix_pump_out throws the frames
 * away, which is all a leak-hunt needs; rendering a station change to a file needs them back.
 * dst holds frames * channels interleaved floats. Headless only. */
CMM_API int cmm_mix_pump_capture(float* dst, int frames) {
    int drained;
    mix_lock(&g_lock);
    drained = (g_out && g_headless) ? cmm_out_drain_into(g_out, dst, frames) : 0;
    mix_unlock(&g_lock);
    return drained;
}

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
    g_deviceWanted = 1;   /* v40: producer opens outside g_lock (was ensure_device_locked) */
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
    g_deviceWanted = 1;   /* v40: producer opens outside g_lock (was ensure_device_locked) */
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

/* v51 -- the occlusion/tone smoothing time constant, in MILLISECONDS. One number, applied to every
 * speaker point and to the muffle that rides on it, independent of the door, the listener's
 * distance and the audio device's buffer size. Lock-free; call whenever config changes. */
CMM_API void cmm_mix_set_emitter_slew(float occlMs) {
    if (occlMs < 1.0f) occlMs = 1.0f; else if (occlMs > 2000.0f) occlMs = 2000.0f;
    g_emOcclSlew = occlMs * 0.001f;
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
/* Overall click trim. The three-mode shell sums louder than the old single ring, and at Body
 * 0.55-0.60 the toggles rendered at 0.98 peak -- fine at the default SFX volume and one bad
 * setting away from clipping the bus against loud music. Headroom is cheaper than a limiter. */
/* Retrimmed for the noise exciter (v42). The sin(w) mode normalisation is derived for an
 * IMPULSE; driving the same modes with a millisecond and a half of noise pours far more energy
 * in, and the first render after the change clipped at +11.9 dBFS. Worth stating plainly because
 * a clipped click is not merely loud -- clipping generates harmonics, which are periodic, so the
 * tonality measurement came back barely changed and would have read as "the fix did not work". */
#define CLICK_TRIM 0.32f
/* How hard the contact hits, relative to the noise around it. This is the difference between a
 * click and a rustle; it is not a config knob because it is what makes a click a click -- Snap
 * already controls how much of the sound is crack versus shell. */
#define SNAP_IMPULSE 5.0f
/* Saturation drive and its make-up gain (the shaper's slope at zero is 1, so unity in = ~unity
 * out once divided back down). Enough to round the peak and generate harmonics, not enough to
 * turn the click into a buzz. */
/* Make-up is set from the shaper's CEILING, not its slope at zero. d/(1+0.75|d|) asymptotes to
 * 1/0.75 = 1.333, so 0.62 puts a hard strike near full scale while leaving headroom. Normalising
 * by 1/drive instead -- the small-signal gain -- is right only for signals that never approach
 * the knee, and a click is nothing but knee: it cost 11 dB. */
#define CLICK_DRIVE      3.2f
#define CLICK_DRIVE_NORM 0.62f

/* One frame of a click. Shared by the live mixer and the offline preview render so what gets
 * tuned and what gets heard cannot drift apart -- the single most expensive recurring mistake in
 * this project has been two implementations of "the same" thing. */
static float sfx_click_sample(sfx_voice* v, float noise) {
    float hit = 0.0f;
    if (v->strikeNext < v->strikeN && v->pos == v->strikeAt[v->strikeNext]) {
        hit = v->strikeLvl[v->strikeNext++];
        v->exLev += hit;
        v->ping  += v->pingInit * hit;
    }
    float excite = noise * v->exLev;
    v->exLev *= v->exK;
    /* THE CONTACT ITSELF: a definite transient, not a lucky noise sample.
     * Making the shell exciter a noise burst fixed the tonality but took the hard hit with it --
     * the energy that used to arrive at t=0 got spread over a millisecond and a half, so the
     * click gained texture and lost its edge ("the texture is nice, but that initial hit is
     * missing"). Noise alone cannot supply it: its first sample is a random value, so the attack
     * is a different height every time and is often near zero. A deterministic impulse rides ON
     * TOP of the noise -- the strike is always the same hard event, the character around it is
     * always different. High-passed with the noise so it stays a crack instead of a thud. */
    float drive = noise + SNAP_IMPULSE * hit;
    v->snapHp += v->kSnapHp * (drive - v->snapHp);
    float snap = (drive - v->snapHp) * v->ping;
    v->ping *= v->pingK;

    /* The shell: three damped modes, deliberately inharmonic so it reads as a lump of plastic
     * being knocked rather than as a note. */
    float body = 0.0f;
    for (int m = 0; m < 5; m++) {
        float ry = v->rc1[m] * v->ry1[m] + v->rc2[m] * v->ry2[m] + excite * v->mLev[m];
        v->ry2[m] = v->ry1[m]; v->ry1[m] = ry;
        body += ry;
    }
    float x = snap + body * v->thump;
    /* SOFT SATURATION -- the other half of "gritty". Two surfaces colliding is not a linear
     * process: they deform, the contact area changes under load, and the result is harmonic
     * distortion concentrated in the transient. A sum of linear resonators, however well tuned,
     * comes out CLEAN, and clean is the one thing an impact never is. This shapes hardest exactly
     * where the energy is -- at the strike -- and does nothing to the tail. */
    /* SATURATE FIRST, THEN FILTER -- and give the filter two poles.
     *
     * I moved the shaper AFTER the tone cap so its harmonics would survive, and that was wrong in
     * a way worth recording: a hard nonlinearity on a fifth-of-a-millisecond transient generates
     * energy all the way past Nyquist, and with nothing downstream of it that energy both stays
     * and ALIASES. Measured in octave bands, two thirds of the click ended up above 7 kHz. That
     * is not grit, it is fizz, and the folded-back part is the harsh digital kind that no amount
     * of tuning removes.
     *
     * So the nonlinearity goes back where it physically belongs -- at the contact, upstream of
     * the radiating shell -- and the answer to "the harmonics get filtered away" is to put the
     * cap somewhere they survive rather than to remove the cap. Two poles instead of one, because
     * 6 dB/oct leaves the strike's ultrasonic tail only ~12 dB down an octave up. */
    { float d = x * CLICK_DRIVE;
      x = (d / (1.0f + fabsf(d) * 0.75f)) * CLICK_DRIVE_NORM; }
    v->hi  += v->kHi * (x - v->hi);
    v->hi2 += v->kHi * (v->hi - v->hi2);
    return v->hi2;
}

static void sfx_capture_click_tone(sfx_voice* v, int which) {
    float rate = (float)g_busRate;
    float hz = g_sfxClickHz[which], snap = g_sfxClickSnap[which], ms = g_sfxClickLenMs[which];
    float body = g_sfxClickBody[which], bodyHz = g_sfxClickBodyHz[which], dbl = g_sfxClickDouble[which];
    if (hz < 300.0f) hz = 300.0f; else if (hz > 10000.0f) hz = 10000.0f;
    if (snap < 0.0f) snap = 0.0f; else if (snap > 1.0f) snap = 1.0f;
    if (ms < 5.0f) ms = 5.0f; else if (ms > 120.0f) ms = 120.0f;
    if (body < 0.0f) body = 0.0f; else if (body > 1.0f) body = 1.0f;
    /* Ceiling raised 3000 -> 9000. Real small switches resonate far higher than the old clamp
     * allowed: measured on two reference recordings, a mouse switch's dominant mode sits at
     * 6.9 kHz and a keyboard-style click at 2.4 kHz, where the model could not place a shell at
     * all. The clamp was quietly deciding the character, which is the worst kind of limit --
     * invisible, and it makes every OTHER knob look like the problem. */
    if (bodyHz < 200.0f) bodyHz = 200.0f; else if (bodyHz > 9000.0f) bodyHz = 9000.0f;
    if (dbl < 0.0f) dbl = 0.0f; else if (dbl > 1.0f) dbl = 1.0f;
    /* PER-FIRE VARIATION. Drawn once, here, so every parameter of one click moves together and
     * the result is a slightly different object each time rather than a smeared average. */
    float vary = g_sfxClickVary;
    if (vary < 0.0f) vary = 0.0f; else if (vary > 1.0f) vary = 1.0f;
    float j1 = 0.0f, j2 = 0.0f, j3 = 0.0f, j4 = 0.0f, j5 = 0.0f;
    if (vary > 0.001f) {
        g_sfxSeed = g_sfxSeed * 1664525u + 1013904223u; j1 = (float)((g_sfxSeed >> 9) & 0xffff) / 32768.0f - 1.0f;
        g_sfxSeed = g_sfxSeed * 1664525u + 1013904223u; j2 = (float)((g_sfxSeed >> 9) & 0xffff) / 32768.0f - 1.0f;
        g_sfxSeed = g_sfxSeed * 1664525u + 1013904223u; j3 = (float)((g_sfxSeed >> 9) & 0xffff) / 32768.0f - 1.0f;
        g_sfxSeed = g_sfxSeed * 1664525u + 1013904223u; j4 = (float)((g_sfxSeed >> 9) & 0xffff) / 32768.0f - 1.0f;
        g_sfxSeed = g_sfxSeed * 1664525u + 1013904223u; j5 = (float)((g_sfxSeed >> 9) & 0xffff) / 32768.0f - 1.0f;
    }
    /* TEXTURE, NOT LOUDNESS. j5 tilts the crack against the shell in OPPOSITE directions, so what
     * changes between one detent and the next is the character -- this one caught more of the
     * metal, that one more of the housing -- while the level stays put. Varying the level instead
     * just sounds like an inconsistent volume control, which is a different and worse impression. */
    float tiltSnap = 1.0f + 0.35f * vary * j5;
    float tiltBody = 1.0f - 0.35f * vary * j5;
    bodyHz *= 1.0f + 0.22f * vary * j1;        /* the shell is never struck in quite the same place */
    /* HOW LONG IT RINGS VARIES, AND IT VARIES ASYMMETRICALLY. +/-9% was inaudible; a mechanism
     * does not merely differ in tone from one throw to the next, it differs in how long it goes
     * on for. The skew is deliberate: most detents are about the nominal length and the occasional
     * one catches and rattles a little longer, which is what real hardware does. A symmetric
     * spread gives you as many unnaturally CURTAILED clicks as long ones, and short outliers read
     * as a dropout rather than as variety. Length drives the mode decay, so this is genuinely how
     * long the shell rings, not just when the voice is freed. */
    ms     *= 1.0f + vary * (0.22f * j2 + 0.38f * (j2 > 0.0f ? j2 : 0.0f));
    snap    = snap + 0.15f * vary * j3; if (snap < 0.0f) snap = 0.0f; else if (snap > 1.0f) snap = 1.0f;

    float sec = ms * 0.001f;
    v->kHi   = 1.0f - expf(-6.2831853f * hz / rate);
    /* The snap is SHORT -- a couple of milliseconds, not a quarter of the click. Decaying noise
     * over tens of milliseconds is a hiss; a switch is a crack. */
    /* EQUAL-POWER between the crack and the shell, so Snap changes the CHARACTER and not the
     * volume. The old pair (0.4 + 0.7*snap against 1 - 0.55*snap) did not hold power constant, so
     * every tonal adjustment was also a loudness adjustment and the three clicks drifted 7 dB
     * apart from each other while being tuned. A knob that moves two things at once cannot be
     * turned deliberately. */
    v->ping  = sqrtf(0.12f + 0.88f * snap) * tiltSnap * CLICK_TRIM;
    /* ...and it has to DIE fast. At a millisecond the crack is a short chirp; a contact is over
     * in a couple of hundred microseconds and what follows it is the shell, not more crack. */
    v->pingK = expf(-1.0f / ((0.00012f + 0.00060f * (1.0f - snap)) * rate));
    v->pingInit = v->ping;
    /* High-pass the noise well below the tone cap so the snap has bite without mud. */
    {
        float hp = hz * 0.25f; if (hp < 400.0f) hp = 400.0f; else if (hp > 3000.0f) hp = 3000.0f;
        v->kSnapHp = 1.0f - expf(-6.2831853f * hp / rate);
    }
    /* `thump` is now the SHELL's level rather than a decaying DC term -- same knob, same
     * direction (more snap = less shell), but it drives something that oscillates. */
    v->thump = sqrtf(1.0f - 0.88f * snap) * tiltBody * CLICK_TRIM;
    v->thumpInit = v->thump;
    v->body = body;
    /* Three inharmonic modes around BodyHz. The ratios are deliberately not simple: 1, 1.71 and
     * 2.63 land nowhere near a harmonic series, so the ear hears a knock instead of a pitch.
     * Higher modes are quieter and die faster, which is what small stiff objects do. Body mixes
     * the two upper modes in, so Body = 0 leaves the fundamental alone and still sounds like a
     * switch -- where before it left a bare DC pop. */
    /* The exciter's own decay: about a millisecond and a half of broadband drive. Long enough to
     * spread each mode into a BAND rather than a line, short enough to still be an impact. */
    v->exLev = 0.0f;
    v->exK = expf(-1.0f / (0.0015f * rate));
    {
        /* FIVE modes, at ratios that are not merely inharmonic but IRREGULARLY spaced, so the ear
         * cannot fuse them into one pitch the way it does with a tidy series. Damping is now a
         * small fraction of the click length rather than half of it: a shell knocked with a
         * fingernail rings for a handful of cycles, not fifty. Between this and the noise
         * exciter, tonality drops from ~0.9 to the region where the ear stops hearing a note. */
        const float ratio[5] = { 1.0f, 1.38f, 1.93f, 2.61f, 3.47f };
        const float lev[5]   = { 1.0f, 0.62f, 0.44f, 0.28f, 0.17f };
        const float damp[5]  = { 0.11f, 0.08f, 0.06f, 0.045f, 0.035f };
        /* ...and normalise the mode set, so Body is a timbre control rather than a second volume
         * control. Without this, richer settings were simply louder settings. */
        float msum = 0.0f;
        for (int m = 0; m < 5; m++) { float L = lev[m] * (0.35f + 0.65f * body); msum += L * L; }
        float mnorm = msum > 1e-9f ? 1.0f / sqrtf(msum) : 1.0f;
        for (int m = 0; m < 5; m++) {
            /* Detune each mode independently per firing. A shell struck twice never rings the
             * same set of frequencies, and detuning as a GROUP (which a single bodyHz jitter
             * does) just transposes the same chord — it is the ratios drifting that stops it
             * being recognisable as one sound repeated. */
            g_sfxSeed = g_sfxSeed * 1664525u + 1013904223u;
            float dt = ((float)((g_sfxSeed >> 9) & 0xffff) / 32768.0f - 1.0f) * 0.09f * vary;
            float fq = bodyHz * ratio[m] * (1.0f + dt);
            if (fq > rate * 0.45f) fq = rate * 0.45f;
            float w = 6.2831853f * fq / rate;
            float R = expf(-1.0f / (damp[m] * sec * rate));
            v->rc1[m] = 2.0f * R * cosf(w);
            v->rc2[m] = -(R * R);
            /* NORMALISE THE MODE FOR NOISE DRIVE, NOT FOR AN IMPULSE. This resonator's impulse response is R^n * sin((n+1)w)/sin(w),
             * so a unit impulse peaks at about 1/sin(w) -- roughly 7.7x at 1 kHz, and worse the
             * lower the mode. Summing three of them unnormalised rendered the click at +16.6 dBFS:
             * it would have shipped as "now it is distorted" and been heard as another wrong sound
             * rather than as a gain bug. Caught by printing the peak in the preview render, which
             * is exactly what the preview is for. */
            /* sin(w) equalises the IMPULSE PEAK of each mode. Under a noise burst what matters
             * is energy, and a resonator's noise gain goes as 1/sqrt(1-R^2) -- so the slow low
             * mode accumulates far more than the fast high ones. With sin(w) the fundamental
             * swamped the rest: measured on the shipped dial, 98% of the click's energy sat in a
             * single band at 953 Hz with NOTHING above 1.2 kHz. A narrowband thump, which is why
             * no amount of tuning made it read as metal -- the band the ear listens to for
             * "small hard object" was empty. sqrt(1-R^2) equalises ENERGY instead, so the upper
             * modes actually arrive and the shell has a spectrum rather than a pitch. */
            float sw = sqrtf(1.0f - R * R); if (sw < 1e-4f) sw = 1e-4f;
            /* Body scales the WHOLE shell now, not just the upper modes. Leaving the fundamental
             * at full level regardless meant the one knob named after the tonal part could not
             * remove it — "too tonal" had no dial to turn. Body 0 is now a bare tick. */
            v->mLev[m] = lev[m] * (0.35f + 0.65f * body) * mnorm * sw;
            v->ry1[m] = v->ry2[m] = 0.0f;
        }
    }
    v->snapHp = 0.0f;
    /* Second micro-impact after the first — the lever hitting its stop, or the detent's
     * "cl-ick". Its SPACING jitters hardest: on a real mechanism that gap is exactly what varies
     * throw to throw, and it is the cue the ear uses to tell a mechanism from a recording. */
    /* Build the impact cluster. `Double` now buys COUNT as well as level: at 0 it is a single
     * clean strike, and as it rises the mechanism chatters more. Spacings are irregular and drawn
     * fresh every firing -- evenly spaced repeats read as a flam or a delay, and the ear is very
     * good at spotting a rhythm. Levels fall away but not monotonically, because a rebound can
     * land harder than the one before it. */
    v->strikeAt[0] = 0; v->strikeLvl[0] = 1.0f;
    v->strikeN = 1; v->strikeNext = 0;
    if (dbl > 0.001f) {
        int n = 2 + (int)(dbl * 3.4f); if (n > 6) n = 6;
        float t = 0.0f;
        for (int k = 1; k < n; k++) {
            g_sfxSeed = g_sfxSeed * 1664525u + 1013904223u;
            float u1 = (float)((g_sfxSeed >> 9) & 0xffff) / 65536.0f;
            g_sfxSeed = g_sfxSeed * 1664525u + 1013904223u;
            float u2 = (float)((g_sfxSeed >> 9) & 0xffff) / 65536.0f;
            t += (0.0011f + 0.0068f * u1) * (1.0f + 0.5f * vary * (u2 - 0.5f));
            if (t > sec * 2.0f) break;
            v->strikeAt[v->strikeN]  = (int)(rate * t);
            v->strikeLvl[v->strikeN] = dbl * (0.9f - 0.12f * k) * (0.55f + 0.75f * u2);
            v->strikeN++;
        }
    }
    float gap = v->strikeN > 1 ? (float)v->strikeAt[v->strikeN - 1] / rate : 0.0f;
    v->len = (int)(rate * (sec * 3.3f + gap));
    if (v->len < 1) v->len = 1;
}

/* v44 — hand a recorded sound to a click kind (0 = power on, 1 = power off, 2 = dial). The PCM is
 * COPIED, so the caller's array can be a managed temporary and needs no pinning beyond the call.
 * frames <= 0 or pcm == NULL clears the kind back to synthesis, which stays the fallback: a
 * missing or unreadable file makes the mod quieter, never broken. */
CMM_API int cmm_mix_set_sfx_sample(int which, const float* pcm, int frames, int rate) {
    if (which < 0 || which >= SFX_CLICK_KINDS) return 0;
    float* copy = NULL;
    if (pcm && frames > 1 && rate > 0) {
        copy = (float*)malloc(sizeof(float) * (size_t)frames);
        if (!copy) return 0;
        memcpy(copy, pcm, sizeof(float) * (size_t)frames);
    }
    /* LOCK ONLY IF THE MIXER IS RUNNING. g_lock is a CRITICAL_SECTION initialised inside
     * cmm_mix_start, so entering it beforehand is undefined behaviour -- it segfaults, which a
     * game mod must never do on a configuration path. Before start there is no producer thread
     * and no audio callback, so nothing can be reading these pointers and the swap is safe
     * unlocked. (The same hazard exists in every other exported setter that locks; this one is
     * reachable earlier than most, because loading a sound file has nothing to do with whether
     * any music is playing yet.) */
    int locked = g_started;
    if (locked) mix_lock(&g_lock);
    /* Any voice still playing the old buffer must stop before it is freed -- a click is at most a
     * couple of hundred milliseconds, but "at most" is not "never", and this frees memory the
     * audio callback reads. */
    for (int i = 0; i < SFX_MAX; i++)
        if (g_sfx[i].pcm == g_sfxPcm[which]) { g_sfx[i].pcm = NULL; g_sfx[i].kind = 0; }
    free(g_sfxPcm[which]);
    g_sfxPcm[which] = copy;
    g_sfxPcmLen[which] = copy ? frames : 0;
    g_sfxPcmRate[which] = copy ? rate : 0;
    if (locked) mix_unlock(&g_lock);
    return 1;
}

/* v44 — how far the playback rate wanders from one firing to the next, in Hz either side of the
 * file's own rate. 0 = every firing identical. */
CMM_API void cmm_mix_set_sfx_rate_jitter(float hz) {
    g_sfxRateJitter = hz < 0.0f ? 0.0f : hz;
}

/* v41 — render ONE click in isolation, no device, no bus: `kind` 2 = power-on, 3 = power-off, at
 * the CURRENT tone settings. Exists so a click can be listened to without launching the game --
 * "wrong sound" is a judgement only ears can make, and making that judgement should not cost a
 * world load. It runs the same sfx_click_sample the mixer runs, so what is auditioned and what
 * ships cannot drift apart. Returns frames written (mono). */
CMM_API int cmm_mix_render_click(int kind, int rate, float* out, int maxFrames) {
    if (!out || maxFrames <= 0 || rate <= 0) return 0;
    if (kind < SFX_KIND_CLICK_ON || kind > SFX_KIND_CLICK_DIAL) return 0;
    int savedRate = g_busRate;
    g_busRate = rate;
    sfx_voice v;
    memset(&v, 0, sizeof v);
    v.kind = kind; v.pos = 0; v.gain = 1.0f; v.rng = 0x1234567u;
    /* Same decision the live mixer makes, through the same function: recorded sound if the kind
     * has one, synthesis otherwise. An audition that could differ from what ships would be the
     * fifth copy-of-the-same-thing in this project to start lying. */
    if (!sfx_arm_sample(&v, kind - SFX_KIND_CLICK_ON))
        sfx_capture_click_tone(&v, kind - SFX_KIND_CLICK_ON);
    g_busRate = savedRate;
    int n = v.len < maxFrames ? v.len : maxFrames;
    for (int i = 0; i < n; i++) {
        if (v.pcm) {
            int i0 = (int)v.pcmPos;
            if (i0 >= v.pcmLen - 1) { n = i; break; }
            float fr = (float)(v.pcmPos - i0);
            out[i] = v.pcm[i0] * (1.0f - fr) + v.pcm[i0 + 1] * fr;
            v.pcmPos += v.pcmStep;
        } else {
            v.rng = v.rng * 1664525u + 1013904223u;
            float noise = (float)((int)(v.rng >> 9) - 0x400000) * (1.0f / 0x400000);
            out[i] = sfx_click_sample(&v, noise);
        }
        v.pos++;
    }
    return n;
}

/* v41 — set one click kind's TONE from config: 0 = power-on toggle, 1 = power-off toggle,
 * 2 = dial detent. Replaces the old both-kinds-at-once entry point, which would have needed
 * eighteen positional floats to gain a third kind -- a signature nobody can call correctly
 * twice. `vary` is shared by all kinds (see g_sfxClickVary). Lock-free volatiles, clamped at
 * capture; the next click of that kind plays with the new character. */
CMM_API void cmm_mix_set_click_tone_kind(int which, float hz, float snap, float lenMs,
                                         float body, float bodyHz, float dbl, float vary) {
    if (which < 0 || which >= SFX_CLICK_KINDS) return;
    g_sfxClickHz[which] = hz;   g_sfxClickSnap[which] = snap;  g_sfxClickLenMs[which] = lenMs;
    g_sfxClickBody[which] = body; g_sfxClickBodyHz[which] = bodyHz; g_sfxClickDouble[which] = dbl;
    if (vary >= 0.0f) g_sfxClickVary = vary;
}

/* v35 — fire one synthesized radio-UI one-shot (SFX_KIND_*: 1 = tuning static burst, 2 = power-on
 * click, 3 = power-off click) at `gain` (0..1, pre-master). Ensures the device like cmm_mix_add
 * (a power-on click may precede the radio voice's own add). Steals the most-finished slot when all
 * are busy. Does NOT touch the output gain (a click during the SP pause duck stays ducked). */
CMM_API void cmm_mix_play_sfx(int kind, float gain) {
    if (kind < SFX_KIND_STATIC || kind > SFX_KIND_CLICK_DIAL) return;
    if (gain <= 0.0f) return;
    if (gain > 1.0f) gain = 1.0f;
    mix_lock(&g_lock);
    if (!g_started) { mix_unlock(&g_lock); return; }
    /* v40: producer opens outside g_lock (was ensure_device_locked + !g_out early-out). The
     * voice arms immediately and renders when the device arrives a few ms later — a click no
     * longer drops during the open window (it only stays silent if no endpoint ever opens). */
    g_deviceWanted = 1;

    /* v45 — A BUTTON DOES NOT QUEUE. The producer runs ahead of the speaker by the output cushion
     * (200 ms in calm play, more while it is banking against a freeze), so a click armed the normal
     * way is heard a fifth of a second after the key. That is invisible on music and is the whole
     * difference between a device and a delay on a button -- reported as "the clicks feel delayed"
     * the first time one was pressed.
     *
     * So a click is rendered here, whole, and mixed straight into the audio already waiting. It is
     * a few thousand multiply-adds; the tuning static is left on the normal path because it is a
     * sustained keepalive rather than an event, and its onset being a cushion late is inaudible.
     *
     * Falls back to arming a voice if the ring wraps mid-click or no device is open yet. A late
     * click is a blemish; half a click is a defect. */
    if (kind >= SFX_KIND_CLICK_ON && kind <= SFX_KIND_CLICK_DIAL && g_out) {
        static float clickBuf[48000];          /* under the lock; a click is at most ~250 ms */
        int cap = (int)(sizeof clickBuf / sizeof *clickBuf);
        int n = cmm_mix_render_click(kind, g_busRate, clickBuf, cap);
        if (n > 0) {
            int skip = g_busRate / 200;        /* ~5 ms clear of whatever the callback is reading */
            if (cmm_out_inject(g_out, clickBuf, n, gain, skip) == n) {
                mix_unlock(&g_lock);
                return;
            }
        }
    }

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
            live->gain = gain;   /* ...and so does LEVEL, so the hash can fade under an arrival */
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
        case SFX_KIND_CLICK_ON:      /* toggle over          */
        case SFX_KIND_CLICK_OFF:     /* toggle back          */
        case SFX_KIND_CLICK_DIAL: {  /* one notch of the dial */
            if (sfx_arm_sample(v, kind - SFX_KIND_CLICK_ON)) break;
            /* Each kind has its own parameter set (cmm_mix_set_click_tone_kind) and draws its own
             * per-fire variation, so a fast spin down the dial is a run of related-but-different
             * detents rather than the same waveform repeated. */
            sfx_capture_click_tone(v, kind - SFX_KIND_CLICK_ON);
            break;
        }
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
/* v42: tone from occlusion. enable=0 restores the shipped behaviour exactly. fullDb is the
 * occlusion at which the tone reaches full dullness -- 8 dB is about what the shipped model's
 * virtual distance produced for a wall at mid range, so a wall keeps sounding like a wall. */
CMM_API void cmm_mix_set_emitter_occl_tone(int enable, float fullDb)
{
    if (fullDb < 0.5f) fullDb = 8.0f; else if (fullDb > 60.0f) fullDb = 60.0f;
    g_emOcclTone = enable ? 1 : 0;
    g_emOcclFullDb = fullDb;
}

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

/* v46 — arm or update a voice's RECEIVER stage: signal strength 0..1 driving gain, treble and
 * stereo width together, so a station leaves and arrives the way a radio does instead of just
 * changing level. See the mix_voice fields for why the three are welded to one number.
 *
 * `ratePerSec` is a RATE, not a duration: 1.0 means a full 0->1 move takes one second, and a move
 * from 0.5 takes half of it. That is what makes interrupting a half-finished tune-in sound right;
 * a fixed duration drags on an early interrupt and snaps on a late one. Pass <= 0 to jump.
 *
 * Idempotent, so managed can push it every tick without re-arming anything. Returns 0 if the voice
 * has already retired, which is the normal race when a station is abandoned mid-load. */
/* v45 -- the receiver's CHARACTER, shared by every radio voice because they are all the same
 * receiver. Split from the per-voice call on purpose: signal is per-station and moves per
 * sample, while these are per-set and change only when somebody edits radio.xml.
 *
 *   knee/kneeW  where the break-up happens and how WIDE it is, as a fraction of the ramp.
 *               Narrow is correct -- the whole audible event in real FM is about eight
 *               decibels of carrier-to-noise, and everything either side of it is a plateau.
 *   noiseTrim   how loud the hiss gets relative to the programme. The old separate static
 *               topped out 5.9 dB QUIETER than the music it was supposed to bury.
 *   clickAmt    the height of a demodulator tick above the bed. 0 disables them.
 *   clickMax    ticks per second at the very bottom; the rate falls about a decade per dB.
 *   hiFloor     where the shared hi-cut lands when the signal is gone.
 *   deemHz      the noise's own de-emphasis corner. 2122 = 75 us (US), 3183 = 50 us (EU). */
CMM_API void cmm_mix_set_receiver_tone(float knee, float kneeW, float noiseTrim,
                                       float clickAmt, float clickMax, float hiFloor,
                                       float deemHz) {
    if (knee      > 0.02f && knee      < 0.98f)   g_recvKnee      = knee;
    if (kneeW     > 0.01f && kneeW     < 1.0f)    g_recvKneeW     = kneeW;
    if (noiseTrim >= 0.0f && noiseTrim < 16.0f)   g_recvNoiseTrim = noiseTrim;
    if (clickAmt  >= 0.0f && clickAmt  <= 4.0f)   g_recvClickAmt  = clickAmt;
    if (clickMax  > 1.0f  && clickMax  < 20000.0f) g_recvClickMax = clickMax;
    if (hiFloor   > 200.0f && hiFloor  < 12000.0f) g_recvHiFloor  = hiFloor;
    if (deemHz    > 200.0f && deemHz   < 12000.0f)
        g_recvDeemK = 1.0f - expf(-6.2831853f * deemHz / (float)g_busRate);
}

/* v46 -- the receiver's LEVEL LAW and its movement, settled by ear against real tracks.
 *
 *   floorDb  how far the station is allowed to sink, in dB. SMALL IS RIGHT. Pulling it down 26 dB
 *            is a fade whatever is layered over it; a real receiver holds the programme at full
 *            level and lets the noise do the moving, so the station is buried and then revealed.
 *   muteW    the SNR span that sink happens over -- wide enough that the station is dropping
 *            WHILE the noise rises, not holding still and then stopping.
 *   drive    how hard a weak station is squashed. Unity small-signal gain, so it dirties rather
 *            than boosting.
 *   wander   slow random drift on the noise level -- AGC breathing.
 *   flutter  faster movement on top, from BAND-LIMITED NOISE so it has no rate to lock onto.
 *   flutHz   the top of that movement's speed range. */
CMM_API void cmm_mix_set_receiver_shape(float floorDb, float muteW, float drive,
                                        float wander, float flutter, float flutHz) {
    if (floorDb >= 0.0f && floorDb < 60.0f)   g_recvFloorDb = floorDb;
    if (muteW   > 0.5f  && muteW   < 60.0f)   g_recvMuteW   = muteW;
    if (drive   >= 0.0f && drive   <= 2.0f)   g_recvDrive   = drive;
    if (wander  >= 0.0f && wander  <= 2.0f)   g_recvWander  = wander;
    if (flutter >= 0.0f && flutter <= 2.0f)   g_recvFlutter = flutter;
    if (flutHz  > 0.5f  && flutHz  < 200.0f)
        g_recvFlutK = 1.0f - expf(-6.2831853f * flutHz / (float)g_busRate);
}

/* v47 -- THE ARRIVAL, part one: what the noise does on the way back in, where the treble window
 * sits, and when the stereo decoder latches.
 *
 *   noiseHold   how far into the arrival the hiss lasts. Tied to signal alone it cleared while the
 *               station was still opening, and clean music fading in under nothing is a fade.
 *   noiseFloor  the residual bed that never fully goes. Absolute zero reads as "effect ended".
 *   winA/winB   the treble window as positions on the 0..1 ramp, shared by both directions.
 *   lockU       where on the ramp the stereo decoder latches. LATE -- after the treble.
 *   lockMs      how fast the image opens once it does. Fast enough to be an event. */
CMM_API void cmm_mix_set_receiver_arrival(float noiseHold, float noiseFloor, float winA,
                                          float winB, float lockU, float lockMs) {
    if (noiseHold  >= 0.0f && noiseHold  <= 4.0f)  g_recvNoiseHold   = noiseHold;
    if (noiseFloor >= 0.0f && noiseFloor <= 1.0f)  g_recvNoiseFloorR = noiseFloor;
    if (winA >= 0.0f && winB > winA + 0.02f && winB <= 1.0f) { g_recvWinA = winA; g_recvWinB = winB; }
    if (lockU  > 0.02f && lockU  < 0.98f)          g_recvLockU  = lockU;
    if (lockMs > 0.2f  && lockMs < 2000.0f)        g_recvLockMs = lockMs;
}

/* v47 -- THE ARRIVAL, part two: how badly the arriving station is mangled before it is repaired.
 * This is the stage that was missing entirely, and its absence is why four rounds of tuning the
 * noise could not fix "it still sounds like audio at its full fidelity fading in".
 *
 *   amount      master depth, 0 = untouched (the old behaviour), 1 = full.
 *   makeupDb    level put back, because band-limiting costs real loudness and a station that gets
 *               quieter as it arrives is a fade.
 *   lowCutHz    where the low cut lands at full depth. This is the one nothing had ever touched,
 *               and bass is most of what makes a source sound hi-fi.
 *   highCutHz   where the high cut lands at full depth. Together they are a telephone. */
CMM_API void cmm_mix_set_receiver_radio(float amount, float makeupDb,
                                        float lowCutHz, float highCutHz) {
    if (amount    >= 0.0f  && amount    <= 1.0f)     g_recvRadio   = amount;
    if (makeupDb  >= -6.0f && makeupDb  <= 24.0f)    g_recvRadioMk = makeupDb;
    if (lowCutHz  >= 20.0f && lowCutHz  <= 2000.0f)  g_recvRadioHp = lowCutHz;
    if (highCutHz > 300.0f && highCutHz <= 15000.0f) g_recvRadioLp = highCutHz;
}

/* v48 -- MARK A VOICE AS RADIO PROGRAMME. That is all it does now. The voice no longer owns a
 * signal, a noise generator, a filter or a stereo image: there is one receiver and it belongs to
 * the mixer. See cmm_mix_set_receiver_signal. */
CMM_API int cmm_mix_set_voice_radio(long long id, int on) {
    int i, found = 0;
    mix_lock(&g_lock);
    for (i = 0; i < g_voiceCount; i++) {
        mix_voice* v = g_voices[i];
        if (v->id != id) continue;
        if (on && !v->recvOn) {
            int k;
            for (k = 0; k < 4; k++) { v->recvMlp[k] = 0.0f; v->recvMhp[k] = 0.0f; }
            v->recvCEnv = 0.0f;
        }
        v->recvOn = on ? 1 : 0;
        found = 1; break;
    }
    mix_unlock(&g_lock);
    return found;
}

/* v48 -- THE RECEIVER'S SIGNAL, and it is not attached to anything. 1 = fully tuned, 0 = nothing
 * there. Ramped by RATE rather than duration, so interrupting a half-finished tune-in fades from
 * half and takes half as long; a fixed duration drags on an early interrupt and snaps on a late
 * one.
 *
 * `active` is the radio being ON. While it is set the receiver produces noise whether or not any
 * station voice exists -- which is the point of moving it here, because the window between the
 * old station retiring and the new one opening is the longest part of a dial press and it used to
 * be filled by an entirely different noise generator. Clear it and the stage is a no-op.
 *
 * `level` is the radio's own volume (context volume x duck). The programme carries its voice gain
 * in with it, but the noise has no voice to inherit from -- and between stations there is no voice
 * at all -- so it is pushed in here. */
/* v48 -- ARM/LEVEL ONLY, safe to push every tick. The signal must NOT be re-pushed on a timer:
 * it is ramped natively, and re-stating the target with rate 0 would snap it. So arming and the
 * level get their own call.
 *
 * `level` matters because ducking is per-voice and the receiver is not a voice. The noise used to
 * ride its station's gain and duck for free; at bus level it has to be told. Between stations
 * there is no voice to ride anyway, which is exactly when the noise is loudest and most exposed. */
CMM_API void cmm_mix_set_receiver_active(int active, float level) {
    mix_lock(&g_lock);
    if (active && !g_recvActive) {
        g_recvSig = 1.0f; g_recvSigStep = 0.0f; g_recvSigTarget = 1.0f;
        g_recvWCur = g_recvWTgt = 1.0f; g_recvWHold = 1; g_recvClkNext = 1;
    }
    g_recvActive = active ? 1 : 0;
    g_recvLevel = level < 0.0f ? 0.0f : (level > 4.0f ? 4.0f : level);
    mix_unlock(&g_lock);
}

CMM_API void cmm_mix_set_receiver_signal(float signal, float ratePerSec, float dullness,
                                         float mononess, int active, float level) {
    float sg = signal < 0.0f ? 0.0f : (signal > 1.0f ? 1.0f : signal);
    mix_lock(&g_lock);
    /* FIRST ARM STARTS AT FULLY TUNED, not at the target. Setting the signal to `sg` here and
     * then computing the step against it -- now equal -- made the step ZERO, so the very first
     * arm always jumped and silently ignored ratePerSec. A receiver nobody has tuned is by
     * definition fully in. */
    if (active && !g_recvActive) {
        g_recvSig = 1.0f; g_recvSigStep = 0.0f;
        g_recvWCur = g_recvWTgt = 1.0f; g_recvWHold = 1; g_recvClkNext = 1;
    }
    g_recvActive = active ? 1 : 0;
    g_recvDull  = dullness < 0.0f ? 0.0f : (dullness > 1.0f ? 1.0f : dullness);
    g_recvMono  = mononess < 0.0f ? 0.0f : (mononess > 1.0f ? 1.0f : mononess);
    g_recvLevel = level < 0.0f ? 0.0f : (level > 4.0f ? 4.0f : level);
    g_recvSigTarget = sg;
    if (ratePerSec <= 0.0f) { g_recvSig = sg; g_recvSigStep = 0.0f; }
    else {
        float per = ratePerSec / (float)g_busRate;      /* units of signal per frame */
        g_recvSigStep = (sg > g_recvSig) ? per : (sg < g_recvSig ? -per : 0.0f);
    }
    mix_unlock(&g_lock);
}

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

/* v43 — one EMITTER slot's ring health: 0 = frames queued, 1 = frames zero-filled through
 * starvation, 2 = times re-cushioned from empty. The music ring's counters above say nothing
 * about these, which is how the radios dropped out for hours behind an "underruns 0" readout. */
CMM_API int cmm_mix_emitter_ring_stat(int slot, int what) {
    mix_lock(&g_lock);
    int v = g_out ? cmm_out_emitter_stat(g_out, slot, what) : -1;
    mix_unlock(&g_lock);
    return v;
}

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
