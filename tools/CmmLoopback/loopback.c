/* CmmLoopback -- capture what Windows is ACTUALLY sending to the default output device
 * and report whether it is clipping.
 *
 * WASAPI loopback taps the render endpoint after the audio engine has mixed every
 * application and run the device's effect chain, so what it records is what the DAC is
 * handed. That makes it the one measurement that can tell "the source is clipped",
 * "something in Windows is clipping it" and "the speakers are distorting a clean signal"
 * apart -- the first two show up here, the third does not.
 *
 * Volume-independence is the thing being tested: run it twice at different Windows
 * volumes. If the captured peak scales with the slider, the volume is upstream of the
 * clipping and the source is at fault. If it does not move, the clipping is downstream of
 * the slider -- an effect chain, or the device itself.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <windows.h>
#include "../../CMM_Decoder/third_party/miniaudio/miniaudio.h"

#define MAXSEC 60
static float* g_buf; static size_t g_cap, g_len; static int g_ch;

static void on_frames(ma_device* d, void* out, const void* in, ma_uint32 n) {
    (void)d; (void)out;
    const float* s = (const float*)in;
    size_t want = (size_t)n * (size_t)g_ch;
    if (g_len + want > g_cap) want = g_cap - g_len;
    if (want) { memcpy(g_buf + g_len, s, want * sizeof(float)); g_len += want; }
}

int main(int argc, char** argv) {
    double secs = (argc > 1) ? atof(argv[1]) : 10.0;
    const char* wavPath = (argc > 2) ? argv[2] : NULL;
    if (secs < 1) secs = 1; if (secs > MAXSEC) secs = MAXSEC;

    ma_device_config cfg = ma_device_config_init(ma_device_type_loopback);
    cfg.capture.format = ma_format_f32;
    cfg.capture.channels = 0;              /* native */
    cfg.sampleRate = 0;                    /* native */
    cfg.dataCallback = on_frames;

    ma_device dev;
    if (ma_device_init(NULL, &cfg, &dev) != MA_SUCCESS) {
        fprintf(stderr, "loopback init failed (WASAPI loopback needs a render endpoint)\n");
        return 1;
    }
    g_ch = (int)dev.capture.internalChannels;
    int rate = (int)dev.capture.internalSampleRate;
    g_cap = (size_t)(secs * rate) * (size_t)g_ch;
    g_buf = (float*)malloc(g_cap * sizeof(float));
    if (!g_buf) { fprintf(stderr, "oom\n"); return 1; }

    printf("capturing %.0fs from '%s'  (%d ch, %d Hz)\n", secs, dev.capture.name, g_ch, rate);
    fflush(stdout);
    ma_device_start(&dev);
    while (g_len < g_cap) Sleep(50);
    ma_device_stop(&dev);
    ma_device_uninit(&dev);

    /* ---- analysis ---------------------------------------------------------------- */
    size_t frames = g_len / (size_t)g_ch;
    double peak = 0, sum2 = 0;
    size_t atFull = 0, over = 0;
    /* A run of consecutive samples within a hair of the peak is FLAT-TOPPING: a clipped
     * waveform holds its ceiling, a loud clean one touches it and leaves. This is what
     * separates "hot" from "clipped", and it is the whole question here. */
    size_t runs = 0, longestRun = 0, run = 0;
    for (size_t i = 0; i < g_len; i++) {
        double v = g_buf[i], a = fabs(v);
        if (a > peak) peak = a;
        sum2 += v * v;
        if (a >= 1.0) over++;
    }
    double thresh = peak * 0.999;
    if (thresh < 1e-6) thresh = 1e-6;
    for (size_t i = 0; i < g_len; i++) {
        if (fabs(g_buf[i]) >= thresh) { atFull++; run++; if (run > longestRun) longestRun = run; }
        else { if (run >= 3) runs++; run = 0; }
    }
    if (run >= 3) runs++;
    double rms = sqrt(sum2 / (double)(g_len ? g_len : 1));

    printf("\n  frames        %zu  (%.1fs)\n", frames, (double)frames / rate);
    printf("  peak          %.6f   (%.2f dBFS)\n", peak, 20.0 * log10(peak + 1e-12));
    printf("  rms           %.6f   (%.2f dBFS)\n", rms, 20.0 * log10(rms + 1e-12));
    printf("  crest         %.2f dB\n", 20.0 * log10((peak + 1e-12) / (rms + 1e-12)));
    printf("  samples >=1.0 %zu\n", over);
    printf("  samples at pk %zu  (%.4f%%)\n", atFull, 100.0 * atFull / (double)(g_len ? g_len : 1));
    printf("  flat runs >=3 %zu   longest %zu samples (%.2f ms)\n",
           runs, longestRun, 1000.0 * longestRun / (g_ch * (double)rate));

    printf("\n  VERDICT: ");
    if (peak < 1e-5)                 printf("SILENCE -- nothing was playing. Start audio, then re-run.\n");
    else if (runs > 20 || over > 0)  printf("CLIPPED. The signal reaching the device is flat-topped.\n");
    else if (peak > 0.99)            printf("at full scale but not flat-topped -- hot, not clipped.\n");
    else                             printf("CLEAN. Peak is %.2f dB below full scale, no flat-topping.\n",
                                            -20.0 * log10(peak + 1e-12));

    if (wavPath) {
        FILE* f = fopen(wavPath, "wb");
        if (f) {
            int bytes = (int)(g_len * 2); int hdr = 44;
            unsigned char h[44]; int br = rate * g_ch * 2;
            memcpy(h, "RIFF", 4); *(int*)(h+4) = bytes + hdr - 8; memcpy(h+8, "WAVEfmt ", 8);
            *(int*)(h+16) = 16; *(short*)(h+20) = 1; *(short*)(h+22) = (short)g_ch;
            *(int*)(h+24) = rate; *(int*)(h+28) = br; *(short*)(h+32) = (short)(g_ch*2);
            *(short*)(h+34) = 16; memcpy(h+36, "data", 4); *(int*)(h+40) = bytes;
            fwrite(h, 1, 44, f);
            for (size_t i = 0; i < g_len; i++) {
                float v = g_buf[i]; if (v > 1) v = 1; if (v < -1) v = -1;
                short s = (short)(v * 32767.0f); fwrite(&s, 2, 1, f);
            }
            fclose(f);
            printf("\n  wrote %s\n", wavPath);
        }
    }
    free(g_buf);
    return 0;
}
