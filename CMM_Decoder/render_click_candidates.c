/* render_click_candidates -- audition sheet for the radio's synthesized clicks.
 *
 * Renders each candidate through cmm_mix_render_click in the SHIPPED DLL (the exact code the
 * game runs; there is no second copy of the synthesis) as one WAV per candidate: power ON, a
 * pause, eight dial detents at the spacing of a real spin, a pause, power OFF. Every click is a
 * fresh firing, so the per-fire variation is heard as it is in game.
 *
 * Also prints, per click kind, three numbers that track "metal vs plastic":
 *   centroid  where the energy sits (Hz) -- small metal parts live high, housings lower
 *   ring      time for the click to fall 40 dB from its peak (ms) -- a note rings, a knock stops
 *   zc        zero crossings per ms in the tail -- a clean tail at one pitch reads as tonal
 *
 * Build:  gcc -O2 -o render_click_candidates.exe render_click_candidates.c dist/CMM_Decoder.dll -lm
 * Run:    render_click_candidates.exe <output folder> [meat]   (meat = round 2) */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

extern int  cmm_mix_render_click(int kind, int rate, float* out, int maxFrames);
extern void cmm_mix_set_click_tone_kind(int which, float hz, float snap, float lenMs,
                                        float body, float bodyHz, float dbl, float vary);

#define RATE 48000
#define MAXF (RATE * 2)

typedef struct { const char* name; const char* note; float k[3][6]; float vary; } cand;
/* k[kind] = { Hz (tone cap), Snap, LengthMs, Body, BodyHz, Double }; kinds 0 on, 1 off, 2 dial */
static const cand FIRST[] = {
    { "A_current_metal", "the shipped defaults, fitted to the removed recordings",
      { { 8722, .72f, 60, .37f, 5100, .80f }, { 8722, .70f, 68, .40f, 4350, .80f }, { 8187, .84f, 44, .50f, 5949, .52f } }, .5f },
    { "B_bakelite", "old radio: hard plastic knob and a firm toggle",
      { { 4500, .50f, 18, .65f, 1200, .25f }, { 4300, .48f, 20, .65f, 1050, .25f }, { 5000, .55f, 12, .55f, 1800, .15f } }, .5f },
    { "C_light_plastic", "modern plastic: lighter, brighter tick",
      { { 6000, .65f, 12, .50f, 2000, .20f }, { 5800, .62f, 13, .50f, 1800, .20f }, { 6500, .70f,  8, .40f, 2600, .10f } }, .5f },
    { "D_soft_detent", "darker and softer, a knob turned slowly",
      { { 3200, .40f, 22, .70f,  800, .15f }, { 3000, .38f, 24, .70f,  720, .15f }, { 3500, .45f, 14, .60f, 1100, .05f } }, .5f },
    { "E_chunky_switch", "solid power switch, crisp detents",
      { { 5000, .55f, 26, .75f,  650, .35f }, { 4800, .52f, 28, .75f,  600, .30f }, { 4500, .60f, 10, .50f, 1500, .10f } }, .5f },
    { "F_muted", "felt through the hand more than heard: very soft",
      { { 2600, .35f, 16, .75f,  600, .10f }, { 2500, .33f, 18, .75f,  560, .10f }, { 2800, .35f, 10, .65f,  900, .00f } }, .5f },
};

/* Round 2 ("plastic is close but it needs more meat"): C, then four steps that each lower the
 * shell's pitch (BodyHz), give it more of the sound against the crack (Body up, Snap down), let
 * it ring a few ms longer and pull the tone cap in -- weight without going back to a ring. */
static const cand MEAT[] = {
    { "C0_light_plastic", "round-1 C, unchanged, for reference",
      { { 6000, .65f, 12, .50f, 2000, .20f }, { 5800, .62f, 13, .50f, 1800, .20f }, { 6500, .70f,  8, .40f, 2600, .10f } }, .5f },
    { "C1_meat",          "a little fuller",
      { { 5500, .55f, 15, .62f, 1700, .20f }, { 5300, .52f, 16, .62f, 1550, .20f }, { 6000, .60f, 10, .52f, 2100, .10f } }, .5f },
    { "C2_meat",          "fuller",
      { { 5000, .45f, 18, .72f, 1400, .22f }, { 4800, .42f, 19, .72f, 1300, .22f }, { 5500, .50f, 12, .62f, 1700, .12f } }, .5f },
    { "C3_meat",          "solid",
      { { 4600, .38f, 21, .80f, 1150, .25f }, { 4400, .36f, 22, .80f, 1050, .25f }, { 5000, .42f, 14, .70f, 1400, .12f } }, .5f },
    { "C4_meat",          "heaviest: thick plastic, close to the hand",
      { { 4200, .32f, 24, .88f,  950, .28f }, { 4000, .30f, 26, .88f,  880, .28f }, { 4600, .36f, 16, .78f, 1150, .15f } }, .5f },
    { "B_bakelite",       "round-1 B, for reference",
      { { 4500, .50f, 18, .65f, 1200, .25f }, { 4300, .48f, 20, .65f, 1050, .25f }, { 5000, .55f, 12, .55f, 1800, .15f } }, .5f },
};

static void put16(FILE* f, int v) { fputc(v & 255, f); fputc((v >> 8) & 255, f); }
static void put32(FILE* f, int v) { put16(f, v & 0xffff); put16(f, (v >> 16) & 0xffff); }

static void stats(const float* s, int n, double* centroid, double* ringMs, double* zc)
{
    int pk = 0; float pv = 0;
    for (int i = 0; i < n; i++) if (fabsf(s[i]) > pv) { pv = fabsf(s[i]); pk = i; }
    int end = pk;                                           /* last sample above -40 dB of peak */
    for (int i = pk; i < n; i++) if (fabsf(s[i]) > pv * 0.01f) end = i;
    *ringMs = 1000.0 * (end - pk) / RATE;
    double num = 0, den = 0;                                /* centroid from a 1024-pt DFT at 32 Hz steps */
    int L = n < 2048 ? n : 2048;
    for (int b = 1; b < 750; b++) {
        double f = b * 32.0, re = 0, im = 0, w = 6.283185307 * f / RATE;
        for (int i = 0; i < L; i++) { re += s[i] * cos(w * i); im -= s[i] * sin(w * i); }
        double m = re * re + im * im; num += f * m; den += m;
    }
    *centroid = den > 0 ? num / den : 0;
    int crossings = 0, t0 = pk + RATE / 1000;               /* tail: from 1 ms after the peak */
    for (int i = t0 + 1; i <= end; i++) if ((s[i - 1] < 0) != (s[i] < 0)) crossings++;
    *zc = end > t0 ? crossings / (1000.0 * (end - t0) / RATE) : 0;
}

int main(int argc, char** argv)
{
    const char* dir = argc > 1 ? argv[1] : ".";
    int round2 = argc > 2 && !strcmp(argv[2], "meat");
    const cand* C = round2 ? MEAT : FIRST;
    int NC = round2 ? (int)(sizeof MEAT / sizeof MEAT[0]) : (int)(sizeof FIRST / sizeof FIRST[0]);
    static float clickbuf[MAXF], seq[RATE * 6];
    printf("%-16s %-5s %9s %8s %8s\n", "candidate", "kind", "centroid", "ring", "zc/ms");
    for (int c = 0; c < NC; c++) {
        for (int k = 0; k < 3; k++)
            cmm_mix_set_click_tone_kind(k, C[c].k[k][0], C[c].k[k][1], C[c].k[k][2], C[c].k[k][3], C[c].k[k][4], C[c].k[k][5], C[c].vary);
        memset(seq, 0, sizeof seq);
        int at = RATE / 5;
        /* ON, 8 dial detents 160 ms apart, OFF */
        int kinds[10] = { 0, 2, 2, 2, 2, 2, 2, 2, 2, 1 };
        int gap[10]   = { 700, 160, 160, 160, 160, 160, 160, 160, 900, 0 };
        for (int e = 0; e < 10; e++) {
            int n = cmm_mix_render_click(kinds[e] + 2, RATE, clickbuf, MAXF);   /* render kinds are 2/3/4 */
            for (int i = 0; i < n && at + i < RATE * 6; i++) seq[at + i] += clickbuf[i];
            if (e == 0 || e == 1 || e == 9) {
                double ce, ri, zc; stats(clickbuf, n, &ce, &ri, &zc);
                printf("%-16s %-5s %7.0f Hz %5.1f ms %8.1f\n", e == 0 ? C[c].name : "",
                       kinds[e] == 0 ? "on" : kinds[e] == 1 ? "off" : "dial", ce, ri, zc);
            }
            at += gap[e] * RATE / 1000;
        }
        int total = at + RATE / 2;
        char path[512]; snprintf(path, sizeof path, "%s/%s.wav", dir, C[c].name);
        FILE* f = fopen(path, "wb"); if (!f) { perror(path); return 1; }
        fwrite("RIFF", 1, 4, f); put32(f, 36 + total * 2); fwrite("WAVEfmt ", 1, 8, f);
        put32(f, 16); put16(f, 1); put16(f, 1); put32(f, RATE); put32(f, RATE * 2); put16(f, 2); put16(f, 16);
        fwrite("data", 1, 4, f); put32(f, total * 2);
        for (int i = 0; i < total; i++) { float v = seq[i] * 0.9f; if (v > 1) v = 1; if (v < -1) v = -1; put16(f, (int)lrintf(v * 32767)); }
        fclose(f);
    }
    printf("\n%d candidates written to %s\n", NC, dir);
    for (int c = 0; c < NC; c++) printf("  %-16s %s\n", C[c].name, C[c].note);
    return 0;
}
