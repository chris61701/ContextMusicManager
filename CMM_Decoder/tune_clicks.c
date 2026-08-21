/* tune_clicks — dial the radio's click sounds in by ear, with the sound in your ears while you
 * turn the knob.
 *
 * It calls cmm_mix_render_click in the SHIPPED DLL. Not a port, not a model of it, not a
 * JavaScript twin: the exact code the game runs. Every time this project has built a second
 * implementation of "the same" thing — an offline judge, a reference model, a spectrum analyser —
 * the two have drifted and the copy has been the one telling the lies. So there is no copy.
 *
 * Keys
 *   up/down      pick a parameter          left/right   adjust      shift+left/right  x5
 *   space        fire the selected click   S            eight in a row (a dial spin)
 *   tab          switch kind: on / off / dial
 *   1..5         load one of the five dial characters (A=current .. E=ratchet)
 *   W            write the XML to clicks.tuned.xml (paste into Config/radio.xml)
 *   R            reset this kind to the shipped defaults
 *   Q            quit
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <conio.h>
#include <windows.h>
#include <mmsystem.h>

extern int  cmm_mix_render_click(int kind, int rate, float* out, int maxFrames);
extern void cmm_mix_set_click_tone_kind(int which, float hz, float snap, float lenMs,
                                        float body, float bodyHz, float dbl, float vary);

#define RATE 48000
#define MAXF (RATE * 3)

static const char* KIND[3] = { "power ON  (toggle over)", "power OFF (toggle back)", "DIAL     (detent)" };
static const char* PNAME[7] = { "Hz (tone cap)", "Snap (crack vs shell)", "LengthMs",
                                "Body (shell mix)", "BodyHz (shell pitch)", "Double (chatter)",
                                "Vary (all kinds)" };
static const char* PXML[6]  = { "Hz", "Snap", "LengthMs", "Body", "BodyHz", "Double" };
static const char* KXML[3]  = { "On", "Off", "Dial" };
static const float STEP[7]  = { 200.f, 0.02f, 1.f, 0.02f, 50.f, 0.05f, 0.05f };
static const float LO[7]    = { 300.f, 0.f,   5.f, 0.f,   200.f, 0.f,  0.f };
static const float HI[7]    = { 16000.f, 1.f, 120.f, 1.f, 8000.f, 1.f, 1.f };

/* [kind][0..5] + a shared vary */
/* Fitted to two reference recordings the author supplied -- a mouse switch for the dial and a
 * keyboard-style click for the power toggle -- by searching the parameter space for the closest
 * match on octave-band energy, spectral centroid and ring time. */
static float P[3][6] = {
    { 8722.f, 0.72f, 60.f, 0.37f, 5100.f, 0.80f },
    { 8722.f, 0.70f, 68.f, 0.40f, 4350.f, 0.80f },
    { 8187.f, 0.84f, 44.f, 0.50f, 5949.f, 0.52f },
};
static const float DEF[3][6] = {
    { 8722.f, 0.72f, 60.f, 0.37f, 5100.f, 0.80f },
    { 8722.f, 0.70f, 68.f, 0.40f, 4350.f, 0.80f },
    { 8187.f, 0.84f, 44.f, 0.50f, 5949.f, 0.52f },
};
static float vary = 0.5f;

/* The five dial characters that were rendered for the listening choice, so the tuner starts from
 * one of them instead of from nowhere. Order is by brightness, which is the axis that turned out
 * to matter: the shipped A puts 92% of its energy in one band at 926 Hz, and D spreads a third
 * of it above 3 kHz. */
static const char* PRESET_NAME[5] = { "fitted", "darker", "old A", "brighter", "ratchet" };
static const float PRESET[5][6] = {
    {  8187.f, 0.84f, 44.f, 0.50f, 5949.f, 0.52f },   /* the fit to your mouse-click reference */
    {  7000.f, 0.80f, 36.f, 0.45f, 3400.f, 0.45f },
    {  4200.f, 0.80f, 24.f, 0.30f,  950.f, 0.15f },   /* where this started, for comparison */
    { 10000.f, 0.86f, 44.f, 0.52f, 7200.f, 0.55f },
    {  9000.f, 0.78f, 50.f, 0.55f, 6400.f, 0.85f },
};

static void push(void) {
    for (int k = 0; k < 3; k++)
        cmm_mix_set_click_tone_kind(k, P[k][0], P[k][1], P[k][2], P[k][3], P[k][4], P[k][5], vary);
}

/* Play a mono float buffer through the default device, from memory — no temp file, no device
 * bookkeeping, and it can be re-fired while the previous one is still sounding. */
static unsigned char wavbuf[44 + MAXF * 2];
static void play(const float* s, int n) {
    if (n > MAXF) n = MAXF;
    unsigned char* p = wavbuf;
    int datalen = n * 2;
    memcpy(p, "RIFF", 4); *(int*)(p+4) = 36 + datalen; memcpy(p+8, "WAVEfmt ", 8);
    *(int*)(p+16) = 16; *(short*)(p+20) = 1; *(short*)(p+22) = 1;
    *(int*)(p+24) = RATE; *(int*)(p+28) = RATE * 2; *(short*)(p+32) = 2; *(short*)(p+34) = 16;
    memcpy(p+36, "data", 4); *(int*)(p+40) = datalen;
    short* o = (short*)(p + 44);
    for (int i = 0; i < n; i++) {
        float v = s[i]; if (v > 1) v = 1; else if (v < -1) v = -1;
        o[i] = (short)(v * 30000.0f);
    }
    PlaySound((LPCSTR)wavbuf, NULL, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
}

static float tmp[MAXF], mixbuf[MAXF];

static float fire(int kind) {
    int n = cmm_mix_render_click(kind + 2, RATE, tmp, MAXF);
    play(tmp, n);
    float pk = 0; for (int i = 0; i < n; i++) { float a = fabsf(tmp[i]); if (a > pk) pk = a; }
    return pk;
}

/* Eight in a row — the only way to judge whether the per-fire variation is doing its job. */
static float spin(int kind) {
    memset(mixbuf, 0, sizeof mixbuf);
    int total = 0;
    for (int i = 0; i < 8; i++) {
        int n = cmm_mix_render_click(kind + 2, RATE, tmp, MAXF);
        int off = (int)(i * 0.16f * RATE);
        for (int j = 0; j < n && off + j < MAXF; j++) mixbuf[off + j] += tmp[j];
        if (off + n > total) total = off + n;
    }
    play(mixbuf, total);
    float pk = 0; for (int i = 0; i < total; i++) { float a = fabsf(mixbuf[i]); if (a > pk) pk = a; }
    return pk;
}

static void writexml(void) {
    FILE* f = fopen("clicks.tuned.xml", "w");
    if (!f) { printf("  could not write clicks.tuned.xml\n"); return; }
    for (int k = 0; k < 3; k++) {
        for (int i = 0; i < 6; i++)
            fprintf(f, "  <RadioClick%s%s>%g</RadioClick%s%s>\n",
                    KXML[k], PXML[i], P[k][i], KXML[k], PXML[i]);
        fprintf(f, "\n");
    }
    fprintf(f, "  <RadioClickVary>%g</RadioClickVary>\n", vary);
    fclose(f);
}

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD mode; if (GetConsoleMode(h, &mode)) SetConsoleMode(h, mode | 0x0004);

    int kind = 2, sel = 0;
    float lastPeak = 0; const char* note = "space = fire, S = spin, tab = next kind";
    push();
    for (;;) {
        printf("\033[H\033[J");
        printf("  CMM radio clicks — live, through the same code the game runs\n");
        printf("  ---------------------------------------------------------------\n");
        printf("  kind:  %s        (tab to change)\n\n", KIND[kind]);
        for (int i = 0; i < 7; i++) {
            float v = (i == 6) ? vary : P[kind][i];
            printf("   %s %-22s %8.2f  ", i == sel ? "\033[7m>" : " ", PNAME[i], v);
            int bars = (int)(28.0f * (v - LO[i]) / (HI[i] - LO[i]));
            for (int b = 0; b < 28; b++) putchar(b < bars ? '#' : '.');
            printf("%s\n", i == sel ? "\033[0m" : "");
        }
        printf("\n  last peak %.2f%s\n", lastPeak, lastPeak > 0.95f ? "   *** CLIPPING ***" : "");
        printf("\n  %s\n", note);
        printf("  up/down pick   left/right adjust (shift = x5)   W write xml   R reset   Q quit\n");
        printf("  dial presets:  ");
        for (int i = 0; i < 5; i++) printf("%d=%s   ", i + 1, PRESET_NAME[i]);
        printf("\n");

        int c = _getch();
        if (c == 'q' || c == 'Q') break;
        if (c == 9) { kind = (kind + 1) % 3; continue; }
        if (c == ' ') { lastPeak = fire(kind); note = "fired"; continue; }
        if (c == 's' || c == 'S') { lastPeak = spin(kind); note = "eight in a row"; continue; }
        if (c == 'w' || c == 'W') { writexml(); note = "written to clicks.tuned.xml"; continue; }
        if (c == 'r' || c == 'R') { memcpy(P[kind], DEF[kind], sizeof P[kind]); push(); note = "reset"; continue; }
        if (c >= '1' && c <= '5') {
            memcpy(P[2], PRESET[c - '1'], sizeof P[2]);
            kind = 2; push(); lastPeak = spin(2);
            note = PRESET_NAME[c - '1'];
            continue;
        }
        if (c == 0 || c == 0xE0) {
            int k2 = _getch();
            float mul = (GetKeyState(VK_SHIFT) & 0x8000) ? 5.0f : 1.0f;
            if (k2 == 72) sel = (sel + 6) % 7;                  /* up   */
            else if (k2 == 80) sel = (sel + 1) % 7;             /* down */
            else if (k2 == 75 || k2 == 77) {                    /* left / right */
                float d = (k2 == 77 ? STEP[sel] : -STEP[sel]) * mul;
                float* t = (sel == 6) ? &vary : &P[kind][sel];
                *t += d;
                if (*t < LO[sel]) *t = LO[sel];
                if (*t > HI[sel]) *t = HI[sel];
                push();
                lastPeak = fire(kind);                          /* hear every adjustment */
                note = "adjusted";
            }
        }
    }
    printf("\033[H\033[J");
    writexml();
    printf("  wrote clicks.tuned.xml — paste it into Config/radio.xml (it hot-reloads)\n\n");
    for (int k = 0; k < 3; k++)
        printf("  %-4s  Hz %6.0f  Snap %.2f  Len %5.1f  Body %.2f  BodyHz %6.0f  Double %.2f\n",
               KXML[k], P[k][0], P[k][1], P[k][2], P[k][3], P[k][4], P[k][5]);
    printf("  Vary  %.2f\n", vary);
    return 0;
}
