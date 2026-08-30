/* ─────────────────────────────────────────────────────────────────────────────────────
 * CmmEmitLab — render a world-speaker retune and a POCKET RADIO retune through the REAL
 * native code, and print when each one's static and music actually arrive.
 *
 * WHY THIS EXISTS. The emitter tuning was debugged for fourteen hours entirely by ear,
 * one report at a time, and the loop never converged: several "fixes" caused the next
 * symptom, and three separate causes turned out to be numbers nobody had measured (a
 * carrier 11 dB louder than the hiss it replaced; a 12 dB swing in the hiss depending on
 * whether the player had touched the pocket radio; a fade whose audible part was squeezed
 * into its own final third). Every one of those took minutes to find with a probe and
 * hours to chase by ear.
 *
 * The other consumer of the same receiver — PersonalRadio — sounds right, and is the
 * specification. So this renders BOTH through the same metering and prints them side by
 * side. "The emitter is 900 ms late and 12 dB quiet" is a fact you can act on; "it feels
 * wrong" is not.
 *
 * WHAT IS REAL AND WHAT IS NOT.
 *   REAL: cmm_mix.c and cmm_out.c verbatim (#included, so file-statics are observable),
 *         the actual producer thread, the actual receiver DSP, the actual spatialiser,
 *         the actual ring buffers. The call ORDER matches EmitterAudioLayer's.
 *   NOT:  the decoders (one synthetic noise source stands in), and the managed-side point
 *         fades, which live in C# and are mirrored here — see joinExtra(). If you change
 *         ApplyBirthFades/FadeExtra in EmitterAudioLayer.cs, change joinExtra too or this
 *         harness will quietly lie to you.
 *
 * HOW IT SEPARATES STATIC FROM MUSIC. Three passes with identical timing:
 *   silent   — the same sequence with the programme muted   => the STATIC envelope
 *   clear    — the same sequence with the receiver never armed => the MUSIC envelope
 *   both     — the real thing                                => what the player hears
 * The report is read off the first two; the third is there to catch the case where they
 * do not sum to what actually comes out.
 *
 * BUILD (from CMM_Decoder/, because it includes those sources):
 *   gcc -O1 -std=gnu99 -o ../tools/CmmEmitLab/emitlab.exe \
 *       ../tools/CmmEmitLab/emitlab.c miniaudio_impl.c \
 *       -Ithird_party/miniaudio -I. -DMA_NO_DECODING -DMA_NO_ENCODING -DMA_NO_GENERATION \
 *       -DMA_NO_RESOURCE_MANAGER -DMA_NO_NODE_GRAPH -DNDEBUG -lm \
 *       -fno-stack-protector -mstackrealign
 * or just: bash tools/CmmEmitLab/build.sh
 *
 * RUN:  emitlab.exe            both instruments, summary
 *       emitlab.exe --csv      + a millisecond timeline on stdout
 * ───────────────────────────────────────────────────────────────────────────────────── */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

#include "cmm_out.c"
#include "cmm_mix.c"

/* ── Synthetic decoder ─────────────────────────────────────────────────────────────────
 * cmm_mix.c's dispatch tables name the seven real decoders. One synthetic source is
 * registered under KIND_WAV (index 4) and the rest are stubs so the tables link. It emits
 * at bus rate/channels so cmm_dc_* is never entered (v->conv stays NULL). */
typedef struct probe_src { unsigned rng; float amp; } probe_src;

int cmm_wav_read_float(cmm_decoder* d, float* out, int maxFrames) {
    probe_src* s = (probe_src*)d;
    int i;
    for (i = 0; i < maxFrames * 2; i++) {
        s->rng = s->rng * 1664525u + 1013904223u;
        out[i] = (float)(int)s->rng * (1.0f / 2147483648.0f) * s->amp;
    }
    return maxFrames;
}
int  cmm_wav_seek(cmm_decoder* d, long long f) { (void)d; (void)f; return 0; }
void cmm_wav_close(cmm_decoder* d) { free(d); }

#define STUB(name) \
    int  name##_read_float(cmm_decoder* d, float* o, int m) { (void)d;(void)o;(void)m; return 0; } \
    int  name##_seek(cmm_decoder* d, long long f) { (void)d;(void)f; return -1; } \
    void name##_close(cmm_decoder* d) { (void)d; }
STUB(cmm_opus) STUB(cmm_flac) STUB(cmm_vorbis) STUB(cmm_mp3) STUB(cmm_aac) STUB(cmm_container)

static cmm_decoder* mk_src(float amp) {
    probe_src* s = (probe_src*)calloc(1, sizeof(probe_src));
    s->rng = 12345u; s->amp = amp;
    return (cmm_decoder*)s;
}

/* ── Device stand-in ───────────────────────────────────────────────────────────────── */
#define RATE 48000
#define CH   2
#define BLK  480                     /* 10 ms — miniaudio's low-latency period */
#define MS_PER_BLK (1000 * BLK / RATE)

static ma_device g_fakeDev;
static float     g_outBuf[BLK * CH];

/* ── Metering ──────────────────────────────────────────────────────────────────────── */
static double db(double x) { return x > 1e-9 ? 20.0 * log10(x) : -999.0; }

#define MAXPT 600                    /* 6 s at 10 ms */
typedef struct { double rms[MAXPT]; int n; } track;

static void tr_push(track* t, const float* buf, int frames) {
    double sum = 0; int i;
    for (i = 0; i < frames * CH; i++) sum += (double)buf[i] * buf[i];
    if (t->n < MAXPT) t->rms[t->n++] = sqrt(sum / (frames * CH));
}

/* First moment the track rises above `floorDb`, in ms. -1 if never. */
static int t_onset(const track* t, double floorDb) {
    int i; for (i = 0; i < t->n; i++) if (db(t->rms[i]) > floorDb) return i * MS_PER_BLK;
    return -1;
}
/* Peak level, and the moment it is first reached. */
static double t_peak(const track* t, int* atMs) {
    double best = 0; int i, bi = 0;
    for (i = 0; i < t->n; i++) if (t->rms[i] > best) { best = t->rms[i]; bi = i; }
    if (atMs) *atMs = bi * MS_PER_BLK;
    return best;
}
/* First moment the track is within `withinDb` of its own peak and stays there. */
static int t_settle(const track* t, double withinDb) {
    double pk = t_peak(t, NULL); int i;
    if (pk <= 0) return -1;
    for (i = 0; i < t->n; i++) {
        if (db(t->rms[i] / pk) < -withinDb) continue;
        int j, ok = 1;
        for (j = i; j < t->n; j++) if (db(t->rms[j] / pk) < -withinDb) { ok = 0; break; }
        if (ok) return i * MS_PER_BLK;
    }
    return -1;
}
/* Last moment the track is above `floorDb` — where the static finally clears. */
static int t_end(const track* t, double floorDb) {
    int i, last = -1;
    for (i = 0; i < t->n; i++) if (db(t->rms[i]) > floorDb) last = i * MS_PER_BLK;
    return last;
}

/* ── Shipped defaults, mirrored from CMMConfig.cs ──────────────────────────────────────
 * Kept in one block so a drift is one diff away rather than scattered through the code. */
static const float LEAVE_MS   = 700.0f;   /* RadioTuneLeaveMs      */
static const float ARRIVE_MS  = 1000.0f;  /* RadioTuneArriveMs     */
static const float MINSTAT_MS = 100.0f;   /* RadioTuneMinStaticMs  */
static const float HOLD_MS    = 250.0f;   /* WorldEmitterSweepMs — the mandatory silence */
static const float RANGE      = 60.0f;
static const float MINDIST    = 1.0f;
static const float FALLOFF    = 2.5f;
static const float EM_MASTER  = 0.5f;     /* Overall 1 x Ambient 1 x WorldEmitterVolume */
static const float STATICTRIM = 2.0f;     /* WorldEmitterStaticTrim */
static const float LISTEN_D   = 2.0f;     /* blocks from the speaker */

/* THE MANAGED-SIDE POINT FADE, mirrored from EmitterAudioLayer.ApplyBirthFades +
 * FadeExtra. Held fully muted for the hold, then rising — and expressed as added VIRTUAL
 * DISTANCE with the falloff inverted, so the fade is linear in LEVEL rather than in
 * distance (a linear distance decay puts 72% of the fade below -19 dB). */
/* SUPERSEDED, and left here as the record of a trap. The mandatory silence USED to be
 * expressed as added virtual distance on the point -- and a point gain scales everything that
 * lane carries at that speaker, the station AND the receiver's static, because by then they
 * are one signal. So the silence before music silenced the hiss with it. The shipping code
 * now fades the VOICE instead, which is what this harness drives below.
 *
 * This is exactly the drift the header warns about: for one build this function mirrored code
 * that no longer existed, and the rig reported a static onset of 1100 ms that the game does
 * not have. A harness that mirrors managed logic has to be re-read whenever that logic moves. */
static float joinExtra_superseded(float ageMs) {
    if (ageMs < HOLD_MS) return RANGE;                    /* silent */
    float t = (ageMs - HOLD_MS) / ARRIVE_MS;
    if (t >= 1.0f) return 0.0f;
    float wanted = t;                                     /* level we want, 0..1 */
    return (1.0f - powf(wanted, 1.0f / FALLOFF)) * RANGE; /* ...as distance */
}

/* ── One mixer, for the whole process ──────────────────────────────────────────────────
 * cmm_mix_start_ex is not re-entrant: the producer thread and the device outlive it, so
 * calling it per run crashes. Start once, force the device open the way a station start
 * does, and point the stand-in device at it -- cmm_out_data_callback reads pUserData. */
static void mix_boot(void) {
    int err = 0;
    memset(&g_fakeDev, 0, sizeof(g_fakeDev));
    /* AS SHALLOW AS THE MIXER ALLOWS. The producer fills ahead and the callback drains
     * behind, so ring depth is LATENCY between a control call and the audio it shapes. At
     * 200 ms tight / 2 s max the callback was rendering audio produced up to two seconds
     * before the call that set it up -- which is why the static appeared to peak at the end
     * of the window rather than during the leave. One block keeps the two within ~10 ms. */
    cmm_mix_start_ex(RATE, CH, BLK, BLK * 2, 1, &err);
    mix_sleep_ms(60);
    if (!g_out) { mix_lock(&g_lock); g_deviceWanted = 1; mix_unlock(&g_lock); }
    for (int i = 0; i < 200 && !g_out; i++) mix_sleep_ms(5);
    if (!g_out) { fprintf(stderr, "emitlab: no output device opened\n"); exit(1); }
    g_fakeDev.pUserData = g_out;
}

/* Back to a known state between passes, without tearing the mixer down. */
static void mix_reset(void) {
    cmm_mix_stop_all_now();
    for (int s = 0; s < CMM_MAX_EMIT_VOICES; s++) {
        cmm_mix_clear_emitter_recv(s);
        cmm_mix_set_emitter_tone(s, NULL, NULL, 0, 0.0f);
        cmm_mix_set_lane_mirror(s, -1);
        cmm_mix_set_slot_points(s, NULL, NULL, NULL, 0);
    }
    mix_sleep_ms(40);
    /* AND RE-ACQUIRE THE DEVICE. stop_all_now drops the voice count to zero and the idle
     * teardown then closes the output, leaving the stand-in device pointing at freed
     * memory -- which renders as silence. The same lifetime the mod itself got wrong twice. */
    mix_lock(&g_lock); g_deviceWanted = 1; g_closeWhenIdle = 0; mix_unlock(&g_lock);
    for (int i = 0; i < 200 && !g_out; i++) mix_sleep_ms(5);
    g_fakeDev.pUserData = g_out;
}

/* ── The two sequences ─────────────────────────────────────────────────────────────────
 * Call ORDER and TIMING mirror the shipping code. `programme` mutes the music source;
 * `arm` disables the receiver. Together they give the three passes. */

static void push_receiver_defaults(void) {
    /* Exactly what EmitterAudioLayer.PushReceiverTuning and PersonalRadio.AimSignal push,
     * so neither instrument is measured on the C file's compiled defaults. */
    cmm_mix_set_receiver_shape(5.0f, 0.22f, 0.30f, 0.35f, 0.20f, 6.0f);
    cmm_mix_set_receiver_tone(0.90f, 0.22f, 0.15f, 0.5f, 12.0f, 1800.0f, 3200.0f);
    cmm_mix_set_receiver_arrival(0.25f, 0.35f, 0.55f, 0.85f, 0.92f, 220.0f);
    cmm_mix_set_receiver_radio(0.5f, 3.0f, 180.0f, 5200.0f);
}

/* WORLD SPEAKER: leave on the station's own lane, handover under the noise, arrive. */
static void run_emitter(track* t, int programme, int arm) {
    mix_reset();
    push_receiver_defaults();
    cmm_mix_set_emitter_master(EM_MASTER);
    cmm_mix_set_emitter_recv_trim(STATICTRIM);
    cmm_mix_set_emitter_tuning(MINDIST, RANGE, FALLOFF, 0.9f, 0.8f);

    float lis[3] = { 0, 0, 0 }, rgt[3] = { 1, 0, 0 };
    float pts[3] = { 0, 0, LISTEN_D };
    float gains[1] = { 0 }; int flags[1] = { 0 };

    long long vOld = cmm_mix_add_emitter(mk_src(programme ? 0.06f : 0.0f), 4, RATE, CH,
                                         RATE * 600, 0.0f, 1, 1.0f,
                                         pts[0], pts[1], pts[2], MINDIST, RANGE, 0, 0);
    long long vNew = 0;
    int handoverMs = (int)(LEAVE_MS + MINSTAT_MS);
    int joinAtMs = -1, lockedFired = 0;

    /* settle the outgoing station so we measure a retune, not a start */
    for (int i = 0; i < 60; i++) {
        cmm_out_set_emitter_spatial(g_out, 0, lis, rgt, pts, gains, flags, 1,
                                    MINDIST, RANGE, FALLOFF, 0.9f, 0.8f);
        memset(g_outBuf, 0, sizeof(g_outBuf));
        cmm_out_data_callback(&g_fakeDev, g_outBuf, NULL, BLK);
    }

    if (arm) cmm_mix_set_emitter_recv(0, 0.0f, LEAVE_MS / 1000.0f);   /* LEAVE */

    for (int ms = 0; ms < MAXPT * MS_PER_BLK; ms += MS_PER_BLK) {
        if (ms >= handoverMs && vNew == 0) {                          /* HANDOVER */
            cmm_mix_remove_emitter(vOld, 0.12f);
            vNew = cmm_mix_add_emitter(mk_src(programme ? 0.06f : 0.0f), 4, RATE, CH,
                                       RATE * 600, ARRIVE_MS / 1000.0f, 1, 1.0f,
                                       pts[0], pts[1], pts[2], MINDIST, RANGE, 0, 0);
            joinAtMs = ms;
            if (arm) cmm_mix_set_emitter_recv(0, 1.0f, ARRIVE_MS / 1000.0f);  /* ARRIVE */
            lockedFired = 1;
        }
        /* The point is OCCLUSION ONLY now. Clear line at two blocks, unchanging. */
        gains[0] = 0.0f;
        /* ...and the mandatory silence is on the VOICE: muted when it lands, held, then up
         * over the arrive. Mirrors MaintainTune's phase 2. */
        if (vNew != 0 && joinAtMs >= 0) {
            /* A FLOOR from the handover, not an addition after the voice lands. */
            float age = (float)(ms - joinAtMs);
            if (age < HOLD_MS)                   cmm_mix_set_voice_gain(vNew, 0.0f, 0.0f);
            else if (age < HOLD_MS + MS_PER_BLK) cmm_mix_set_voice_gain(vNew, 1.0f, ARRIVE_MS / 1000.0f);
        }
        cmm_out_set_emitter_spatial(g_out, 0, lis, rgt, pts, gains, flags, 1,
                                    MINDIST, RANGE, FALLOFF, 0.9f, 0.8f);
        mix_sleep_ms(2);                 /* let the producer top the shallow ring back up */
        memset(g_outBuf, 0, sizeof(g_outBuf));
        cmm_out_data_callback(&g_fakeDev, g_outBuf, NULL, BLK);
        tr_push(t, g_outBuf, BLK);
    }
    (void)lockedFired;
}

/* POCKET RADIO: one bus receiver, leave as a RATE, swap under the noise, arrive. */
static void run_radio(track* t, int programme, int arm) {
    mix_reset();
    push_receiver_defaults();
    cmm_mix_set_master(1.0f);

    long long vOld = cmm_mix_add(mk_src(programme ? 0.06f : 0.0f), 4, RATE, CH,
                                 RATE * 600, 0.0f, 1, 0, 1.0f, -1.0f, 0);
    cmm_mix_set_voice_radio(vOld, 1);
    if (arm) cmm_mix_set_receiver_signal(1.0f, 0.0f, 0.85f, 1.0f, 1, 1.0f);

    /* THROUGH THE SAME DEVICE AS THE EMITTER. pump_capture and the device callback are
     * alternative drains of one bus, and a device is open here -- so pumping returned
     * silence. The music bus feeds cmm_out too, so one path measures both instruments and
     * they are directly comparable. */
    for (int i = 0; i < 60; i++) {
        memset(g_outBuf, 0, sizeof(g_outBuf));
        cmm_out_data_callback(&g_fakeDev, g_outBuf, NULL, BLK);
    }

    if (arm) cmm_mix_set_receiver_signal(0.0f, 1000.0f / LEAVE_MS, 0.85f, 1.0f, 1, 1.0f);

    int swapMs = (int)(LEAVE_MS + MINSTAT_MS * 0.5f);   /* the radio swaps MID-floor */
    long long vNew = 0;
    for (int ms = 0; ms < MAXPT * MS_PER_BLK; ms += MS_PER_BLK) {
        if (ms >= swapMs && vNew == 0) {
            /* No explicit remove: cmm_mix_add retires the outgoing voice itself, which IS
             * the pocket radio's model -- "the add is what retires the outgoing station". */
            vNew = cmm_mix_add(mk_src(programme ? 0.06f : 0.0f), 4, RATE, CH,
                               RATE * 600, 0.25f, 1, 0, 1.0f, -1.0f, 0);
            cmm_mix_set_voice_radio(vNew, 1);
            if (arm) cmm_mix_set_receiver_signal(1.0f, 1000.0f / ARRIVE_MS, 0.85f, 1.0f, 1, 1.0f);
        }
        mix_sleep_ms(2);
        memset(g_outBuf, 0, sizeof(g_outBuf));
        cmm_out_data_callback(&g_fakeDev, g_outBuf, NULL, BLK);
        tr_push(t, g_outBuf, BLK);
    }
}

/* ── Report ────────────────────────────────────────────────────────────────────────── */
static void report(const char* who, track* stat, track* mus, track* both) {
    int pkS, pkM;
    double peakS = t_peak(stat, &pkS), peakM = t_peak(mus, &pkM);
    printf("\n%s\n", who);
    printf("  static   onset %5d ms   peak %7.1f dBFS at %4d ms   clears %5d ms\n",
           t_onset(stat, -60.0), db(peakS), pkS, t_end(stat, -45.0));
    printf("  music    onset %5d ms   peak %7.1f dBFS at %4d ms   within 3 dB at %5d ms\n",
           t_onset(mus, -60.0), db(peakM), pkM, t_settle(mus, 3.0));
    printf("  static is %+.1f dB relative to the music it covers\n", db(peakS) - db(peakM));
    printf("  composite peak %7.1f dBFS\n", db(t_peak(both, NULL)));
}

static void csv(const char* who, track* stat, track* mus, track* both) {
    printf("\n# %s\nms,static_dBFS,music_dBFS,both_dBFS\n", who);
    for (int i = 0; i < stat->n; i++)
        printf("%d,%.1f,%.1f,%.1f\n", i * MS_PER_BLK,
               db(stat->rms[i]), db(mus->rms[i]), db(both->rms[i]));
}

/* -- ONE PROCESS PER PASS ------------------------------------------------------------
 * Six passes through one mixer contaminate each other: cmm_mix_start_ex is not re-entrant,
 * stop_all_now triggers an idle teardown that reallocates the output, and the receiver, the
 * tone envelopes and the ring depth all carry state across. Running the same numbers twice
 * gave a 650 ms swing in the static onset -- larger than most of what we are measuring.
 *
 * So the parent re-executes itself once per pass and collates. Each child runs exactly one
 * sequence in a fresh address space, prints one machine-readable line, and exits. Slower,
 * and deterministic, which is the whole reason to have a rig instead of an opinion. */
static void emit_result(track* t) {
    int pk; double peak = t_peak(t, &pk);
    printf("RESULT %d %.1f %d %d %d\n",
           t_onset(t, -60.0), db(peak), pk, t_end(t, -45.0), t_settle(t, 3.0));
}

typedef struct { int onset; double peakDb; int peakMs; int endMs; int settleMs; } passres;

static passres run_child(const char* self, const char* pass) {
    passres r; char cmd[1024], line[512]; FILE* f;
    r.onset = -1; r.peakDb = -999.0; r.peakMs = 0; r.endMs = -1; r.settleMs = -1;
    snprintf(cmd, sizeof(cmd), "\"%s\" --pass %s", self, pass);
    f = _popen(cmd, "r");
    if (!f) return r;
    while (fgets(line, sizeof(line), f))
        if (strncmp(line, "RESULT ", 7) == 0)
            sscanf(line + 7, "%d %lf %d %d %d",
                   &r.onset, &r.peakDb, &r.peakMs, &r.endMs, &r.settleMs);
    _pclose(f);
    return r;
}

int main(int argc, char** argv) {
    setvbuf(stdout, NULL, _IONBF, 0);

    /* child: one pass, one line */
    if (argc >= 3 && strcmp(argv[1], "--pass") == 0) {
        static track t;
        const char* w = argv[2];
        mix_boot();
        if      (!strcmp(w, "es")) run_emitter(&t, 0, 1);
        else if (!strcmp(w, "em")) run_emitter(&t, 1, 0);
        else if (!strcmp(w, "eb")) run_emitter(&t, 1, 1);
        else if (!strcmp(w, "rs")) run_radio  (&t, 0, 1);
        else if (!strcmp(w, "rm")) run_radio  (&t, 1, 0);
        else if (!strcmp(w, "rb")) run_radio  (&t, 1, 1);
        else return 2;
        emit_result(&t);
        return 0;
    }

    /* parent: six children, one table */
    {
    passres eS = run_child(argv[0], "es"), eM = run_child(argv[0], "em"),
            eB = run_child(argv[0], "eb"), rS = run_child(argv[0], "rs"),
            rM = run_child(argv[0], "rm"), rB = run_child(argv[0], "rb");

    printf("CmmEmitLab - one retune, both instruments, shipped defaults\n");
    printf("  leave %.0f  hold %.0f  minStatic %.0f  arrive %.0f ms   listener %.0f blocks\n",
           LEAVE_MS, HOLD_MS, MINSTAT_MS, ARRIVE_MS, LISTEN_D);

    printf("\nWORLD SPEAKER\n");
    printf("  static  onset %5d ms  peak %7.1f dBFS at %4d ms  clears    %5d ms\n",
           eS.onset, eS.peakDb, eS.peakMs, eS.endMs);
    printf("  music   onset %5d ms  peak %7.1f dBFS at %4d ms  within3dB %5d ms\n",
           eM.onset, eM.peakDb, eM.peakMs, eM.settleMs);
    printf("  static %+.1f dB over the music it covers;  composite peak %.1f dBFS\n",
           eS.peakDb - eM.peakDb, eB.peakDb);

    printf("\nPOCKET RADIO\n");
    printf("  static  onset %5d ms  peak %7.1f dBFS at %4d ms  clears    %5d ms\n",
           rS.onset, rS.peakDb, rS.peakMs, rS.endMs);
    printf("  music   onset %5d ms  peak %7.1f dBFS at %4d ms  within3dB %5d ms\n",
           rM.onset, rM.peakDb, rM.peakMs, rM.settleMs);
    printf("  static %+.1f dB over the music it covers;  composite peak %.1f dBFS\n",
           rS.peakDb - rM.peakDb, rB.peakDb);

    printf("\nDIFFERENCE (emitter - radio)\n");
    printf("  static onset  %+5d ms\n", eS.onset - rS.onset);
    printf("  music  onset  %+5d ms\n", eM.onset - rM.onset);
    printf("  music  settle %+5d ms\n", eM.settleMs - rM.settleMs);
    printf("  static/music  %+5.1f dB\n",
           (eS.peakDb - eM.peakDb) - (rS.peakDb - rM.peakDb));
    }
    return 0;
}
