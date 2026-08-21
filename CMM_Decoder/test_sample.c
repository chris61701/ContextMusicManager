/* Prove the recorded clicks load, play, and land at a different rate every firing — without the
 * game. Renders through cmm_mix_render_click, which now shares its arming with the live mixer. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
extern int  cmm_mix_set_sfx_sample(int which, const float* pcm, int frames, int rate);
extern void cmm_mix_set_sfx_rate_jitter(float hz);
extern int  cmm_mix_render_click(int kind, int rate, float* out, int maxFrames);

static float pcm[1 << 20]; static int pcmLen, pcmRate;
static int readwav(const char* p) {
    FILE* f = fopen(p, "rb"); if (!f) return 0;
    static unsigned char b[1 << 21];
    int n = (int)fread(b, 1, sizeof b, f); fclose(f);
    if (n < 44) return 0;
    int ch = 0, bits = 0, pos = 12, at = -1, len = 0;
    while (pos + 8 <= n) {
        int l = *(int*)(b + pos + 4);
        if (!memcmp(b + pos, "fmt ", 4)) { ch = *(short*)(b+pos+10); pcmRate = *(int*)(b+pos+12); bits = *(short*)(b+pos+22); }
        else if (!memcmp(b + pos, "data", 4)) { at = pos + 8; len = l; }
        pos += 8 + l + (l & 1);
    }
    if (at < 0 || bits != 16 || ch < 1) return 0;
    pcmLen = len / (2 * ch);
    for (int i = 0; i < pcmLen; i++) pcm[i] = ((short*)(b + at))[i * ch] / 32768.0f;
    return 1;
}
static float out[1 << 20];
int main(int argc, char** argv) {
    const char* path = argc > 1 ? argv[1] : "../Sounds/radio_dial.wav";
    if (!readwav(path)) { printf("could not read %s\n", path); return 1; }
    printf("  loaded %s: %d frames @ %d Hz (%.0f ms)\n", path, pcmLen, pcmRate, 1000.0*pcmLen/pcmRate);
    if (!cmm_mix_set_sfx_sample(2, pcm, pcmLen, pcmRate)) { printf("  upload FAILED\n"); return 1; }
    cmm_mix_set_sfx_rate_jitter(argc > 2 ? (float)atof(argv[2]) : 2000.0f);
    printf("  eight firings — length varies iff the rate is being jittered:\n");
    for (int i = 0; i < 8; i++) {
        int n = cmm_mix_render_click(4, 48000, out, 1 << 20);
        float pk = 0; for (int j = 0; j < n; j++) { float a = fabsf(out[j]); if (a > pk) pk = a; }
        printf("    #%d  %5d frames = %6.2f ms   implied rate %6.0f Hz   peak %.2f\n",
               i + 1, n, 1000.0 * n / 48000.0, 48000.0 * pcmLen / (double)n, pk);
    }
    cmm_mix_set_sfx_sample(2, NULL, 0, 0);
    int n = cmm_mix_render_click(4, 48000, out, 1 << 20);
    printf("  cleared -> falls back to the synth: %d frames\n", n);
    return 0;
}
