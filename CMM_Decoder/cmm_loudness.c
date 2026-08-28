/* cmm_loudness.c — native EBU R128 / ITU-R BS.1770 integrated-loudness analyzer.
 * ─────────────────────────────────────────────────────────────────────────
 * A faithful port of the managed CMM.R128Analyzer (Harmony/LoudnessAnalyzer.cs) so the
 * loudness sweep decodes AND measures entirely in native memory — no managed-heap
 * allocation to trip Mono's stop-the-world GC and hitch the game's main thread.
 *
 * Pipeline (BS.1770-4):
 *   1. K-weighting  — two cascaded biquads per channel (a high-shelf head model + a
 *                     ~38 Hz high-pass), sample-rate-aware coefficients.
 *   2. 400 ms blocks— mean square per channel, channel-weighted sum, 75% overlap via
 *                     a 4-slot ring of 100 ms sub-blocks (allocation-free steady state).
 *   3. Gating       — drop blocks below −70 LUFS absolute, then below (mean−10 LU)
 *                     relative; integrated loudness = mean energy of survivors.
 *
 * Channels weighted 1.0 (mono/stereo; R128 surround weights only differ for ≥5 ch).
 *
 * The one-shot cmm_loudness_analyze uses the SAME C#→native decoder handoff as the mixer
 * (cmm_mix_add): the caller opens a cmm_decoder + reports a format `kind`, native reads it
 * to EOF via the per-kind read fn and takes ownership (closes it).
 */
#include "CMM_Decoder.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

/* ── Per-kind read/close dispatch (self-contained; mirrors cmm_mix's READ_FN/CLOSE_FN).
 * kind matches AudioDecoder.ClassifyReader: 0=Opus 1=FLAC 2=Vorbis 3=MP3 4=WAV 5=AAC 6=Container. */
typedef int  (*ld_read_fn)(cmm_decoder*, float*, int);
typedef void (*ld_close_fn)(cmm_decoder*);
enum { LD_KIND_COUNT = 7 };
static const ld_read_fn  LD_READ[LD_KIND_COUNT] = {
    cmm_opus_read_float, cmm_flac_read_float, cmm_vorbis_read_float,
    cmm_mp3_read_float,  cmm_wav_read_float,  cmm_aac_read_float, cmm_container_read_float
};
static const ld_close_fn LD_CLOSE[LD_KIND_COUNT] = {
    cmm_opus_close, cmm_flac_close, cmm_vorbis_close,
    cmm_mp3_close,  cmm_wav_close,  cmm_aac_close, cmm_container_close
};

/* ── One biquad (direct form I) with its own per-channel delay state ───────*/
typedef struct { double b0, b1, b2, a1, a2; double x1, x2, y1, y2; } biquad;

static double bq_process(biquad* q, double x) {
    double y = q->b0 * x + q->b1 * q->x1 + q->b2 * q->x2 - q->a1 * q->y1 - q->a2 * q->y2;
    q->x2 = q->x1; q->x1 = x;
    q->y2 = q->y1; q->y1 = y;
    return y;
}

#define SUBBLOCKS_PER_BLOCK 4          /* 4 × 100 ms = 400 ms */
#define ABS_GATE_LUFS   (-70.0)
#define REL_GATE_LU     (-10.0)
#define LOUDNESS_OFFSET (-0.691)       /* L = -0.691 + 10·log10(z) */

struct cmm_loudness {
    int     channels;
    int     samplesPerSub;             /* 100 ms of frames */
    double* chWeights;                 /* [channels] */
    biquad* filters;                   /* [channels*2] : [ch*2]=shelf, [ch*2+1]=high-pass */
    double* subSumSq;                  /* [channels] running Σ w² in the current sub-block */
    int     subFrames;
    double* ring;                      /* [SUBBLOCKS_PER_BLOCK*channels] flat sub-block ring */
    int     ringPos, ringFilled;
    double* blockE;                    /* per-400ms-block weighted mean-square (grows) */
    int     blockCount, blockCap;
    double  peak;
};

/* ── K-weighting biquad coefficients (sample-rate aware, libebur128 derivation) ──
 * Stage 1: high-frequency shelving boost (~+4 dB). */
static biquad make_high_shelf(int sampleRate) {
    const double f0 = 1681.974450955533;
    const double G  = 3.999843853973347;
    const double Q  = 0.7071752369554196;
    double K  = tan(M_PI * f0 / sampleRate);
    double Vh = pow(10.0, G / 20.0);
    double Vb = pow(Vh, 0.4996667741545416);
    double a0 = 1.0 + K / Q + K * K;
    biquad q;
    q.b0 = (Vh + Vb * K / Q + K * K) / a0;
    q.b1 = 2.0 * (K * K - Vh) / a0;
    q.b2 = (Vh - Vb * K / Q + K * K) / a0;
    q.a1 = 2.0 * (K * K - 1.0) / a0;
    q.a2 = (1.0 - K / Q + K * K) / a0;
    q.x1 = q.x2 = q.y1 = q.y2 = 0.0;
    return q;
}
/* Stage 2: ~38 Hz high-pass (RLB low-frequency roll-off). Numerator {1,-2,1} is used
 * un-normalised (matching libebur128 / R128Analyzer); only the a-coeffs divide by a0. */
static biquad make_high_pass(int sampleRate) {
    const double f0 = 38.13547087602444;
    const double Q  = 0.5003270373238773;
    double K  = tan(M_PI * f0 / sampleRate);
    double a0 = 1.0 + K / Q + K * K;
    biquad q;
    q.b0 = 1.0;
    q.b1 = -2.0;
    q.b2 = 1.0;
    q.a1 = 2.0 * (K * K - 1.0) / a0;
    q.a2 = (1.0 - K / Q + K * K) / a0;
    q.x1 = q.x2 = q.y1 = q.y2 = 0.0;
    return q;
}

/* Inverse of L = -0.691 + 10·log10(z)  →  z = 10^((L + 0.691) / 10). */
static double z_for_loudness(double lufs) {
    return pow(10.0, (lufs - LOUDNESS_OFFSET) / 10.0);
}

CMM_API cmm_loudness* cmm_loudness_new(int sampleRate, int channels) {
    if (sampleRate <= 0) sampleRate = 48000;
    if (channels   <= 0) channels   = 1;

    cmm_loudness* a = (cmm_loudness*)calloc(1, sizeof(cmm_loudness));
    if (!a) return NULL;

    a->channels      = channels;
    a->samplesPerSub = (int)(sampleRate * 0.1 + 0.5);   /* 100 ms */
    if (a->samplesPerSub < 1) a->samplesPerSub = 1;

    a->chWeights = (double*)malloc(sizeof(double) * channels);
    a->filters   = (biquad*)malloc(sizeof(biquad) * channels * 2);
    a->subSumSq  = (double*)calloc(channels, sizeof(double));
    a->ring      = (double*)calloc((size_t)SUBBLOCKS_PER_BLOCK * channels, sizeof(double));
    a->blockCap  = 1024;
    a->blockE    = (double*)malloc(sizeof(double) * a->blockCap);
    if (!a->chWeights || !a->filters || !a->subSumSq || !a->ring || !a->blockE) {
        cmm_loudness_free(a);
        return NULL;
    }

    for (int ch = 0; ch < channels; ch++) {
        a->chWeights[ch]     = 1.0;                     /* mono/stereo: all 1.0 */
        a->filters[ch*2 + 0] = make_high_shelf(sampleRate);
        a->filters[ch*2 + 1] = make_high_pass(sampleRate);
    }
    return a;
}

CMM_API void cmm_loudness_free(cmm_loudness* a) {
    if (!a) return;
    free(a->chWeights);
    free(a->filters);
    free(a->subSumSq);
    free(a->ring);
    free(a->blockE);
    free(a);
}

/* Close the current 100 ms sub-block into the ring; on a full ring emit one 400 ms block. */
static void close_sub_block(cmm_loudness* a) {
    double* slot = a->ring + (size_t)a->ringPos * a->channels;
    memcpy(slot, a->subSumSq, sizeof(double) * a->channels);
    a->ringPos = (a->ringPos + 1) % SUBBLOCKS_PER_BLOCK;
    if (a->ringFilled < SUBBLOCKS_PER_BLOCK) a->ringFilled++;

    memset(a->subSumSq, 0, sizeof(double) * a->channels);
    a->subFrames = 0;

    if (a->ringFilled < SUBBLOCKS_PER_BLOCK) return;

    int framesInBlock = SUBBLOCKS_PER_BLOCK * a->samplesPerSub;
    double z = 0.0;
    for (int ch = 0; ch < a->channels; ch++) {
        double perChannel = 0.0;
        for (int s = 0; s < SUBBLOCKS_PER_BLOCK; s++)
            perChannel += a->ring[(size_t)s * a->channels + ch];
        z += a->chWeights[ch] * (perChannel / framesInBlock);
    }

    if (a->blockCount >= a->blockCap) {
        int newCap = a->blockCap * 2;
        double* grown = (double*)realloc(a->blockE, sizeof(double) * newCap);
        if (!grown) return;                             /* keep prior blocks; drop this one */
        a->blockE = grown;
        a->blockCap = newCap;
    }
    a->blockE[a->blockCount++] = z;
}

CMM_API void cmm_loudness_feed(cmm_loudness* a, const float* data, int count) {
    if (!a || !data || count <= 0) return;
    int ch = a->channels;
    int frames = count / ch;
    int idx = 0;

    for (int f = 0; f < frames; f++) {
        for (int c = 0; c < ch; c++) {
            double x = data[idx++];

            double ax = x < 0 ? -x : x;
            if (ax > a->peak) a->peak = ax;

            double w = bq_process(&a->filters[c*2 + 0], x);
            w = bq_process(&a->filters[c*2 + 1], w);
            a->subSumSq[c] += w * w;
        }
        if (++a->subFrames >= a->samplesPerSub) close_sub_block(a);
    }
}

CMM_API int cmm_loudness_finish(cmm_loudness* a, double* outLufs, double* outPeak) {
    if (outLufs) *outLufs = 0.0;
    if (outPeak) *outPeak = a ? a->peak : 0.0;
    if (!a || a->blockCount == 0) return 0;

    /* Stage 1 — absolute gate at −70 LUFS. */
    double absThresholdZ = z_for_loudness(ABS_GATE_LUFS);
    double sum = 0.0; int n = 0;
    for (int i = 0; i < a->blockCount; i++) {
        double z = a->blockE[i];
        if (z > absThresholdZ) { sum += z; n++; }
    }
    if (n == 0) return 0;

    /* Stage 2 — relative gate at (mean − 10 LU) of the absolute-gated set. */
    double meanZ = sum / n;
    double relThresholdLufs = LOUDNESS_OFFSET + 10.0 * log10(meanZ) + REL_GATE_LU;
    double relThresholdZ = z_for_loudness(relThresholdLufs);

    sum = 0.0; n = 0;
    for (int i = 0; i < a->blockCount; i++) {
        double z = a->blockE[i];
        if (z > absThresholdZ && z > relThresholdZ) { sum += z; n++; }
    }
    if (n == 0) return 0;

    if (outLufs) *outLufs = LOUDNESS_OFFSET + 10.0 * log10(sum / n);
    return 1;
}

CMM_API int cmm_loudness_analyze(cmm_decoder* dec, int kind, int srcRate, int srcChannels,
                                 double* outLufs, double* outPeak) {
    if (outLufs) *outLufs = 0.0;
    if (outPeak) *outPeak = 0.0;
    if (!dec) return 0;
    if (kind < 0 || kind >= LD_KIND_COUNT || srcChannels <= 0) {
        if (kind >= 0 && kind < LD_KIND_COUNT) LD_CLOSE[kind](dec);
        return 0;
    }

    cmm_loudness* a = cmm_loudness_new(srcRate, srcChannels);
    if (!a) { LD_CLOSE[kind](dec); return 0; }

    const int chunkFrames = 8192;
    float* buf = (float*)malloc(sizeof(float) * (size_t)chunkFrames * srcChannels);
    if (!buf) { cmm_loudness_free(a); LD_CLOSE[kind](dec); return 0; }

    ld_read_fn readfn = LD_READ[kind];
    for (;;) {
        int n = readfn(dec, buf, chunkFrames);
        if (n <= 0) break;
        cmm_loudness_feed(a, buf, n * srcChannels);
    }

    free(buf);
    int valid = cmm_loudness_finish(a, outLufs, outPeak);
    cmm_loudness_free(a);
    LD_CLOSE[kind](dec);
    return valid;
}

/* ── Progressive (chunked) measurement ─────────────────────────────────────
 * The one-shot above has to reach EOF before it can say anything, so a 47-minute
 * podcast plays at neutral gain for the ~15 seconds it takes to decode — and if the
 * measurement is not persisted across launches (it is not; only the user's own trims
 * and bans reach disk) that happens on every rejoin. This surface answers the same
 * question incrementally: decode a bounded slice, then report the integrated loudness
 * of everything fed SO FAR. The managed side publishes each answer and glides the live
 * voice toward it, so the level converges over the first minute of a long track
 * instead of arriving all at once at the end.
 *
 * Two properties make that sound, rather than just work:
 *   - cmm_loudness_finish is READ-ONLY over the block list, so calling it mid-stream
 *     costs one pass over the blocks and does not disturb the accumulating state. The
 *     value at EOF is bit-identical to what the one-shot would have produced.
 *   - the gating is over ALL blocks so far, not per-chunk, so successive answers are
 *     refinements of one measurement rather than a sequence of independent ones. Errors
 *     shrink monotonically in practice (worst observed vs. final: 2.19 dB at 60 s,
 *     1.15 dB at 5 min, 0.60 dB at 10 min) — small enough after the first slice that
 *     the remaining corrections are inaudible under a slow ramp.
 *
 * Chunking is by DECODED SECONDS, not by bytes or blocks, so a slice costs about the
 * same wall-clock on any format (measured: ~0.09 s of CPU per minute of MP3, ~0.20 s
 * for AAC). Takes ownership of `dec` exactly like the one-shot — free the handle and
 * the decoder is closed with it, whether or not the stream was read to EOF.
 */
struct cmm_lprog {
    cmm_decoder*  dec;
    cmm_loudness* an;
    float*        buf;
    int           kind;
    int           channels;
    int           rate;
    int           bufFrames;
    int           eof;
    long long     framesFed;
};

CMM_API cmm_lprog* cmm_lprog_begin(cmm_decoder* dec, int kind, int srcRate, int srcChannels) {
    if (!dec) return NULL;
    if (kind < 0 || kind >= LD_KIND_COUNT || srcChannels <= 0) {
        if (kind >= 0 && kind < LD_KIND_COUNT) LD_CLOSE[kind](dec);
        return NULL;
    }
    if (srcRate <= 0) srcRate = 48000;

    cmm_lprog* p = (cmm_lprog*)calloc(1, sizeof(cmm_lprog));
    if (!p) { LD_CLOSE[kind](dec); return NULL; }

    p->bufFrames = 8192;
    p->an  = cmm_loudness_new(srcRate, srcChannels);
    p->buf = (float*)malloc(sizeof(float) * (size_t)p->bufFrames * srcChannels);
    if (!p->an || !p->buf) {
        cmm_loudness_free(p->an);
        free(p->buf);
        free(p);
        LD_CLOSE[kind](dec);
        return NULL;
    }

    p->dec      = dec;
    p->kind     = kind;
    p->channels = srcChannels;
    p->rate     = srcRate;
    return p;
}

CMM_API int cmm_lprog_step(cmm_lprog* p, double chunkSeconds,
                           double* outLufs, double* outPeak,
                           double* outSecondsFed, int* outEof) {
    if (outLufs)        *outLufs = 0.0;
    if (outPeak)        *outPeak = 0.0;
    if (outSecondsFed)  *outSecondsFed = 0.0;
    if (outEof)         *outEof = 1;
    if (!p) return 0;

    /* A slice ends when enough frames have been fed OR the stream runs out. Reads are
     * whole decoder blocks, so the boundary lands a little past the target rather than
     * exactly on it — which is fine, and cheaper than asking the decoder for a partial.
     *
     * want < 0 means UNLIMITED: read to eof in this one step, which is how the caller asks
     * for the plain whole-file measurement. That has to be a real case rather than "pass a
     * huge number", because seconds × rate overflows a long long long before a double runs
     * out of range, and an overflowed target lands negative, clamps to 1, and turns the
     * whole file into one frame per slice. (It did: 5868 slices on an 18-minute podcast.) */
    long long want;
    if (chunkSeconds <= 0.0 || chunkSeconds > 1e9) {
        want = -1;
    } else {
        want = (long long)(chunkSeconds * p->rate);
        if (want < 1) want = 1;
    }

    ld_read_fn readfn = LD_READ[p->kind];
    long long fedThisStep = 0;
    while (!p->eof && (want < 0 || fedThisStep < want)) {
        int n = readfn(p->dec, p->buf, p->bufFrames);
        if (n <= 0) { p->eof = 1; break; }
        cmm_loudness_feed(p->an, p->buf, n * p->channels);
        fedThisStep  += n;
        p->framesFed += n;
    }

    if (outEof)        *outEof = p->eof;
    if (outSecondsFed) *outSecondsFed = p->rate > 0 ? (double)p->framesFed / p->rate : 0.0;
    return cmm_loudness_finish(p->an, outLufs, outPeak);
}

CMM_API void cmm_lprog_free(cmm_lprog* p) {
    if (!p) return;
    if (p->dec) LD_CLOSE[p->kind](p->dec);
    cmm_loudness_free(p->an);
    free(p->buf);
    free(p);
}
