/* test_loudness.c — validate native cmm_loudness_* against EBU Tech 3341 reference
 * tones (mirrors Harmony/R128SelfTest.cs). Pure math via the streaming API — no files.
 *
 * Build (mingw, from CMM_Decoder dir):
 *   gcc -O2 -I. test_loudness.c dist/CMM_Decoder.dll -o test_loudness.exe
 * Run (CMM_Decoder.dll beside the exe):
 *   ./test_loudness.exe
 *
 * Reference values (see R128SelfTest.cs):
 *   stereo 1kHz @ -23dBFS → -23.0 LUFS   mono → -26.0 (R128 SUMS channels, +3dB for 2ch)
 *   stereo 1kHz @   0dBFS →   0.0 LUFS   silence → invalid
 *   peak of a -23dBFS sine ≈ 0.0708
 */
#include "CMM_Decoder.h"

#include <stdio.h>
#include <math.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

#define FS 48000
static const double TOL = 0.2;          /* LUFS */

/* Continuous-phase 1 kHz sine fed in channel-aligned chunks (filter state must carry). */
static void feed_sine(cmm_loudness* a, int channels, double dbfs, double seconds) {
    double amp = pow(10.0, dbfs / 20.0);
    double w   = 2.0 * M_PI * 1000.0 / FS;
    int total  = (int)(FS * seconds);
    int chunkFrames = 4096;
    float chunk[4096 * 2];              /* max 2 channels here */
    int n = 0;
    while (n < total) {
        int fc = (total - n) < chunkFrames ? (total - n) : chunkFrames;
        int idx = 0;
        for (int f = 0; f < fc; f++) {
            float s = (float)(amp * sin(w * (n + f)));
            for (int c = 0; c < channels; c++) chunk[idx++] = s;
        }
        cmm_loudness_feed(a, chunk, fc * channels);
        n += fc;
    }
}

static int check_tone(const char* label, int channels, double dbfs, double expect) {
    cmm_loudness* a = cmm_loudness_new(FS, channels);
    feed_sine(a, channels, dbfs, 4.0);
    double lufs = 0, peak = 0;
    int valid = cmm_loudness_finish(a, &lufs, &peak);
    cmm_loudness_free(a);

    if (!valid) { printf("  %-26s FAIL (valid=0, expect %.1f)\n", label, expect); return 0; }
    double err = fabs(lufs - expect);
    int pass = err <= TOL;
    printf("  %-26s %7.2f LUFS (expect %.1f, err %.3f) %s\n",
           label, lufs, expect, err, pass ? "ok" : "FAIL");
    return pass;
}

static int check_silence(void) {
    cmm_loudness* a = cmm_loudness_new(FS, 2);
    static float zeros[4096 * 2] = { 0 };
    for (int n = 0; n < FS; n += 4096) {
        int fc = (FS - n) < 4096 ? (FS - n) : 4096;
        cmm_loudness_feed(a, zeros, fc * 2);
    }
    double lufs = 0, peak = 0;
    int valid = cmm_loudness_finish(a, &lufs, &peak);
    cmm_loudness_free(a);
    int pass = !valid;
    printf("  %-26s valid=%d (expect 0) %s\n", "silence", valid, pass ? "ok" : "FAIL");
    return pass;
}

static int check_peak(void) {
    const double dbfs = -23.0;
    double expect = pow(10.0, dbfs / 20.0);
    cmm_loudness* a = cmm_loudness_new(FS, 2);
    feed_sine(a, 2, dbfs, 1.0);
    double lufs = 0, peak = 0;
    cmm_loudness_finish(a, &lufs, &peak);
    cmm_loudness_free(a);
    int pass = fabs(peak - expect) <= 0.001;
    printf("  %-26s %.4f (expect %.4f) %s\n", "peak", peak, expect, pass ? "ok" : "FAIL");
    return pass;
}

int main(void) {
    printf("── native R128 self-test ──────────────────────────\n");
    int ok = 1;
    ok &= check_tone("stereo 1kHz @ -23dBFS", 2, -23.0, -23.0);
    ok &= check_tone("mono   1kHz @ -23dBFS", 1, -23.0, -26.0);
    ok &= check_tone("stereo 1kHz @   0dBFS", 2,   0.0,   0.0);
    ok &= check_silence();
    ok &= check_peak();
    printf("native R128 self-test: %s\n", ok ? "PASS" : "FAIL");
    printf("───────────────────────────────────────────────────\n");
    return ok ? 0 : 1;
}
