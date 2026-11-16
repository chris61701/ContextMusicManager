/* tune_clicks -- fine-tune the radio's band-noise clicks by ear (CMM_Decoder v63 shapes).
 *
 * Every adjustment re-renders through cmm_mix_render_click in the SHIPPED DLL and plays it at
 * once: the exact code the game runs, so what you settle on here is what the game plays.
 *
 * It starts from the values in the game's Config\radio.xml when it can find one (so a session
 * picks up where the last left off), otherwise from the fitted defaults. W writes the three
 * RadioClick*Shape lines back into that radio.xml -- the game hot-reloads it, so the next click
 * in game already uses them -- and also to clicks.tuned.txt beside this program.
 *
 * Keys
 *   tab          next click: power ON -> power OFF -> DIAL
 *   up/down      pick a control       left/right  adjust      (hold shift: x5)
 *   space        play this click      S  eight in a row       A  power on, 8 dial clicks, off
 *   W            write                R  undo changes to this click (back to what was loaded)
 *   D            this click back to the shipped defaults       Q  quit
 *
 * Run:  tune_clicks.exe [path\to\Config\radio.xml]    (or set CMM_RADIO_XML) */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <conio.h>
#include <windows.h>
#include <mmsystem.h>

extern int  cmm_mix_render_click(int kind, int rate, float* out, int maxFrames);
extern int  cmm_mix_set_click_shape(int which, const float* p, int n);
extern void cmm_mix_set_click_tone_kind(int which, float hz, float snap, float lenMs,
                                        float body, float bodyHz, float dbl, float vary);

#define RATE 48000
#define MAXF (RATE * 4)
#define NS   36

static const char* KNAME[3] = { "power ON", "power OFF", "DIAL" };
static const char* KXML[3]  = { "RadioClickOnShape", "RadioClickOffShape", "RadioClickDialShape" };
static const char* BAND[7]  = { "60-250", "250-500", "500-1k", "1-2k", "2-4k", "4-8k", "8-20k" };

/* The shipped defaults (Harmony/CMMConfig.cs), fitted by fit_click.py. */
static const char* DEF_TXT[3] = {
    "levels -37.3 -28.9 -8.1 -7.8 -4.0 0.0 -2.6 | decay 75.3 47.2 10.4 10.4 7.3 5.8 3.2 | swell 2.0 | edge 2.0 | hits 0.0:0 2.8:-10 5.9:-16 10.4:-22 15.0:-27 17.7:-26 20.0:-23 24.5:-27 | release 177 -0.2 -2.4",
    "levels -20.3 -12.1 0.0 -22.0 -11.1 -9.5 -11.0 | decay 16.5 8.2 2.6 14.7 6.5 4.1 3.2 | swell 13.0 | edge 2.0 | hits 0.0:-0 2.2:-6 5.0:0 7.3:-5 9.8:-11 12.6:-18 14.9:-12 18.0:-21 | release 170 0.2 2.4",
    "levels -44.5 -75.4 -59.7 -4.4 -9.2 0.0 -5.4 | decay 59.6 59.6 9.2 3.6 5.2 4.1 2.9 | swell 0.0 | edge 2.0 | hits 0.0:-20 2.3:0 4.6:-8 6.8:-15 9.0:-18 11.7:-23 15.6:-26 | release 112 4.8 -2.4",
};

static float P[3][NS], LOADED[3][NS], vary = 0.5f;
static char  xmlPath[MAX_PATH];

/* ── the text form, same as Harmony/ClickShape.cs ── */
static void parse(const char* txt, float* p)
{
    memset(p, 0, sizeof(float) * NS); p[15] = 2.0f; p[19] = 1.0f;
    char buf[2048]; strncpy(buf, txt, sizeof buf - 1); buf[sizeof buf - 1] = 0;
    for (char* sec = strtok(buf, "|"); sec; sec = strtok(NULL, "|")) {
        char key[16] = { 0 }; int off = 0;
        if (sscanf(sec, " %15s%n", key, &off) != 1) continue;
        char* rest = sec + off;
        float v[8]; int n = 0, used;
        if (!strcmp(key, "hits")) {
            float t, d; int k = 0;
            while (k < 8 && sscanf(rest, " %f:%f%n", &t, &d, &used) == 2) { p[20 + k] = t; p[28 + k] = d; k++; rest += used; }
            if (k) p[19] = (float)k;
            continue;
        }
        while (n < 8 && sscanf(rest, " %f%n", &v[n], &used) == 1) { n++; rest += used; }
        if (!strcmp(key, "levels"))  for (int i = 0; i < 7 && i < n; i++) p[i] = v[i];
        if (!strcmp(key, "decay"))   for (int i = 0; i < 7 && i < n; i++) p[7 + i] = v[i];
        if (!strcmp(key, "swell")   && n) p[14] = v[0];
        if (!strcmp(key, "edge")    && n) p[15] = v[0];
        if (!strcmp(key, "release")) for (int i = 0; i < 3 && i < n; i++) p[16 + i] = v[i];
    }
}

static void format(const float* p, char* out, size_t cap)
{
    int n = (int)lroundf(p[19]), w = 0;
    w += snprintf(out + w, cap - w, "levels");
    for (int i = 0; i < 7; i++) w += snprintf(out + w, cap - w, " %.1f", p[i]);
    w += snprintf(out + w, cap - w, " | decay");
    for (int i = 0; i < 7; i++) w += snprintf(out + w, cap - w, " %.1f", p[7 + i]);
    w += snprintf(out + w, cap - w, " | swell %.1f | edge %.2f | hits", p[14], p[15]);
    for (int k = 0; k < n; k++) w += snprintf(out + w, cap - w, " %.1f:%.0f", p[20 + k], p[28 + k]);
    if (p[16] > 0.5f) w += snprintf(out + w, cap - w, " | release %.0f %.1f %.1f", p[16], p[17], p[18]);
}

/* ── radio.xml ── */
static char* slurp(const char* path, long* len)
{
    FILE* f = fopen(path, "rb"); if (!f) return NULL;
    fseek(f, 0, SEEK_END); *len = ftell(f); fseek(f, 0, SEEK_SET);
    char* b = malloc(*len + 1); if (b && fread(b, 1, *len, f) != (size_t)*len) { free(b); b = NULL; }
    if (b) b[*len] = 0; fclose(f); return b;
}

static int read_xml_shape(const char* xml, const char* tag, char* out, size_t cap)
{
    char open[64], close[64]; snprintf(open, sizeof open, "<%s>", tag); snprintf(close, sizeof close, "</%s>", tag);
    const char* a = strstr(xml, open); if (!a) return 0; a += strlen(open);
    const char* b = strstr(a, close); if (!b || (size_t)(b - a) >= cap) return 0;
    memcpy(out, a, b - a); out[b - a] = 0; return 1;
}

static int write_xml(void)
{
    if (!xmlPath[0]) return 0;
    long len; char* xml = slurp(xmlPath, &len); if (!xml) return 0;
    {   /* the first write of a session keeps the file as it was, beside it */
        static int backedUp; char bak[MAX_PATH + 16];
        snprintf(bak, sizeof bak, "%s.before-tuner", xmlPath);
        if (!backedUp) { CopyFileA(xmlPath, bak, FALSE); backedUp = 1; }
    }
    size_t cap = len + 8192; char* out = malloc(cap); char* cur = xml;
    strcpy(out, xml);
    for (int k = 0; k < 3; k++) {
        char line[2048], open[64], close[64];
        format(P[k], line, sizeof line);
        snprintf(open, sizeof open, "<%s>", KXML[k]); snprintf(close, sizeof close, "</%s>", KXML[k]);
        char* a = strstr(out, open);
        char* nw = malloc(cap);
        if (a) {                                         /* replace the value in place */
            char* b = strstr(a, close);
            if (!b) { free(nw); continue; }
            size_t head = (a - out) + strlen(open);
            memcpy(nw, out, head); nw[head] = 0;
            strcat(nw, line); strcat(nw, b);
        } else {                                         /* add it before the root's closing tag */
            char* end = NULL; for (char* s = strstr(out, "</"); s; s = strstr(s + 2, "</")) end = s;
            if (!end) { free(nw); continue; }
            size_t head = end - out;
            memcpy(nw, out, head); nw[head] = 0;
            snprintf(nw + head, cap - head, "  %s%s%s\r\n%s", open, line, close, end);
        }
        free(out); out = nw;
    }
    (void)cur;
    FILE* f = fopen(xmlPath, "wb"); int ok = 0;
    if (f) { ok = fwrite(out, 1, strlen(out), f) == strlen(out); fclose(f); }
    free(out); free(xml); return ok;
}

/* ── sound ── */
static void push(void)
{
    for (int k = 0; k < 3; k++) {
        cmm_mix_set_click_tone_kind(k, 8000, .7f, 40, .5f, 2000, .5f, vary);   /* sets the shared vary */
        cmm_mix_set_click_shape(k, P[k], NS);
    }
}
static unsigned char wavbuf[44 + MAXF * 2];
static float tmp[MAXF], mixbuf[MAXF], lastPeak;
static void play(const float* s, int n)
{
    if (n > MAXF) n = MAXF;
    unsigned char* p = wavbuf; int dl = n * 2;
    memcpy(p, "RIFF", 4); *(int*)(p + 4) = 36 + dl; memcpy(p + 8, "WAVEfmt ", 8);
    *(int*)(p + 16) = 16; *(short*)(p + 20) = 1; *(short*)(p + 22) = 1; *(int*)(p + 24) = RATE;
    *(int*)(p + 28) = RATE * 2; *(short*)(p + 32) = 2; *(short*)(p + 34) = 16;
    memcpy(p + 36, "data", 4); *(int*)(p + 40) = dl;
    short* o = (short*)(p + 44); lastPeak = 0;
    for (int i = 0; i < n; i++) { float v = s[i]; float a = fabsf(v); if (a > lastPeak) lastPeak = a;
        if (v > 1) v = 1; else if (v < -1) v = -1; o[i] = (short)(v * 30000.0f); }
    PlaySound((LPCSTR)wavbuf, NULL, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
}
static void seq(const int* kinds, const int* gapMs, int count)
{
    memset(mixbuf, 0, sizeof mixbuf); int at = RATE / 20, total = 0;
    for (int i = 0; i < count; i++) {
        int n = cmm_mix_render_click(kinds[i] + 2, RATE, tmp, MAXF);
        for (int j = 0; j < n && at + j < MAXF; j++) mixbuf[at + j] += tmp[j];
        if (at + n > total) total = at + n;
        at += gapMs[i] * RATE / 1000;
    }
    play(mixbuf, total);
}
static void fire(int k)  { int ks[1] = { k }, g[1] = { 0 }; seq(ks, g, 1); }
static void spin(int k)  { int ks[8], g[8]; for (int i = 0; i < 8; i++) { ks[i] = k; g[i] = 160; } seq(ks, g, 8); }
static void radio(void)  { int ks[10] = { 0, 2, 2, 2, 2, 2, 2, 2, 2, 1 }, g[10] = { 700, 160, 160, 160, 160, 160, 160, 160, 900, 0 }; seq(ks, g, 10); }

/* ── the controls: plain-language moves over the 36 numbers ── */
typedef struct { const char* name; const char* help; } ctl;
static const ctl C[] = {
    { "Brightness",      "tilts every band: right = brighter, left = darker (1 dB/octave a step)" },
    { "Weight (thump)",  "the three low bands, 60 Hz-1 kHz: right = more body" },
    { "Crack",           "the two top bands, 4-20 kHz: right = sharper contact" },
    { "Length",          "every band's decay x1.1 a step: right = rings longer" },
    { "Low tail",        "decay of the low bands only: right = the thump lingers" },
    { "Swell ms",        "how late the low end peaks: right = rounder, later thump" },
    { "Edge",            "the hard transient at each contact" },
    { "Bounce spacing",  "time between landings x1.08 a step: right = slower rattle" },
    { "Rattle level",    "every landing after the second, 1 dB a step" },
    { "Bounces",         "how many landings (2-8)" },
    { "Release delay",   "ms to the second event (press/release); 0 = none" },
    { "Release level",   "the second event against the first, dB" },
    { "Release tilt",    "the second event brighter (right) or darker (left)" },
    { "Variation",       "how much each click differs from the last (all three clicks)" },
};
#define NC (int)(sizeof C / sizeof C[0])
#define NROWS (NC + 14)                               /* + 7 band levels + 7 band decays */

static void adjust(int k, int row, float d)
{
    float* p = P[k];
    if (row >= NC) {                                   /* detail rows */
        int b = (row - NC) % 7;
        if (row - NC < 7) p[b] += 0.5f * d;
        else { p[7 + b] *= powf(1.08f, d); if (p[7 + b] < 0.5f) p[7 + b] = 0.5f; if (p[7 + b] > 300) p[7 + b] = 300; }
        return;
    }
    switch (row) {
    case 0: for (int b = 0; b < 7; b++) p[b] += d * (b - 3) * 1.0f; break;
    case 1: for (int b = 0; b < 3; b++) p[b] += d; break;
    case 2: for (int b = 5; b < 7; b++) p[b] += d; break;
    case 3: for (int b = 0; b < 7; b++) p[7 + b] = fminf(300, fmaxf(0.5f, p[7 + b] * powf(1.1f, d))); break;
    case 4: for (int b = 0; b < 3; b++) p[7 + b] = fminf(300, fmaxf(0.5f, p[7 + b] * powf(1.1f, d))); break;
    case 5: p[14] = fminf(60, fmaxf(0, p[14] + d)); break;
    case 6: p[15] = fminf(12, fmaxf(0, p[15] + 0.25f * d)); break;
    case 7: for (int h = 1; h < 8; h++) p[20 + h] *= powf(1.08f, d); break;
    case 8: for (int h = 2; h < 8; h++) p[28 + h] += d; break;
    case 9: {
        int n = (int)lroundf(p[19]) + (d > 0 ? 1 : -1);
        if (n < 2) n = 2; if (n > 8) n = 8;
        int old = (int)lroundf(p[19]);
        for (int h = old; h < n; h++) {                /* a new landing continues the rattle */
            float gap = h >= 2 ? p[20 + h - 1] - p[20 + h - 2] : 3.0f;
            p[20 + h] = p[20 + h - 1] + gap; p[28 + h] = p[28 + h - 1] - 3.0f;
        }
        p[19] = (float)n; break;
    }
    case 10: p[16] = p[16] < 0.5f && d > 0 ? 100.0f : fmaxf(0, p[16] + 5 * d); if (p[16] < 5) p[16] = 0; break;
    case 11: p[17] += d; break;
    case 12: p[18] = fminf(6, fmaxf(-6, p[18] + 0.5f * d)); break;
    case 13: vary = fminf(1, fmaxf(0, vary + 0.05f * d)); break;
    }
}

static const char* value(int k, int row, char* b)
{
    float* p = P[k], * L = LOADED[k];
    if (row >= NC) { int bnd = (row - NC) % 7;
        if (row - NC < 7) sprintf(b, "%6.1f dB", p[bnd]); else sprintf(b, "%6.1f ms", p[7 + bnd]); return b; }
    switch (row) {
    case 0: sprintf(b, "%+5.1f", (p[6] - p[0] - (L[6] - L[0])) / 6.0f); break;
    case 1: sprintf(b, "%+5.1f dB", p[1] - L[1]); break;
    case 2: sprintf(b, "%+5.1f dB", p[6] - L[6]); break;
    case 3: sprintf(b, "x%.2f", p[12] / L[12]); break;
    case 4: sprintf(b, "x%.2f", p[7] / L[7]); break;
    case 5: sprintf(b, "%.0f ms", p[14]); break;
    case 6: sprintf(b, "%.2f", p[15]); break;
    case 7: sprintf(b, "x%.2f", p[19] > 1 ? p[21] / (L[21] > 0 ? L[21] : 1) : 1.0f); break;
    case 8: sprintf(b, "%+.0f dB", p[19] > 2 ? p[30] - L[30] : 0.0f); break;
    case 9: sprintf(b, "%.0f", p[19]); break;
    case 10: if (p[16] < 0.5f) sprintf(b, "off"); else sprintf(b, "%.0f ms", p[16]); break;
    case 11: sprintf(b, "%+.1f dB", p[17]); break;
    case 12: sprintf(b, "%+.1f", p[18]); break;
    case 13: sprintf(b, "%.2f", vary); break;
    }
    return b;
}

static void find_xml(int argc, char** argv)
{
    const char* cands[4] = { argc > 1 ? argv[1] : NULL, getenv("CMM_RADIO_XML"),
        "C:\\Program Files (x86)\\Steam\\steamapps\\common\\7 Days To Die\\Mods\\ContextMusicManager\\Config\\radio.xml",
        "C:\\_Games\\Steam\\steamapps\\common\\7 Days To Die\\Mods\\ContextMusicManager\\Config\\radio.xml" };
    for (int i = 0; i < 4; i++)
        if (cands[i] && GetFileAttributesA(cands[i]) != INVALID_FILE_ATTRIBUTES) { strncpy(xmlPath, cands[i], MAX_PATH - 1); return; }
}

int main(int argc, char** argv)
{
    find_xml(argc, argv);
    long len; char* xml = xmlPath[0] ? slurp(xmlPath, &len) : NULL;
    for (int k = 0; k < 3; k++) {
        char txt[2048];
        parse(xml && read_xml_shape(xml, KXML[k], txt, sizeof txt) ? txt : DEF_TXT[k], P[k]);
        memcpy(LOADED[k], P[k], sizeof P[k]);
    }
    free(xml);
    SetConsoleMode(GetStdHandle(STD_OUTPUT_HANDLE), 7);   /* ANSI colours */
    push();
    int kind = 2, row = 0, dirty = 0; const char* note = "space = play, A = whole radio";
    for (;;) {
        printf("\033[2J\033[H  RADIO CLICK TUNER    click: \033[1m%s\033[0m   (tab = next)\n", KNAME[kind]);
        printf("  %s\n\n", xmlPath[0] ? xmlPath : "no radio.xml found: W writes clicks.tuned.txt only");
        float* p = P[kind];
        printf("  band   "); for (int b = 0; b < 7; b++) printf("%9s", BAND[b]); printf("\n  level  ");
        for (int b = 0; b < 7; b++) printf("%9.1f", p[b]); printf("   dB\n  decay  ");
        for (int b = 0; b < 7; b++) printf("%9.1f", p[7 + b]); printf("   ms to -20 dB\n  hits   ");
        for (int h = 0; h < (int)lroundf(p[19]); h++) printf(" %.1f:%+.0f", p[20 + h], p[28 + h]);
        printf("\n\n");
        int first = row < 18 ? 0 : row - 17;
        for (int r = first; r < NROWS && r < first + 18; r++) {
            char vb[32]; const char* name; char nb[40];
            if (r < NC) name = C[r].name;
            else { snprintf(nb, sizeof nb, "%s %s", BAND[(r - NC) % 7], r - NC < 7 ? "level" : "decay"); name = nb; }
            printf("%s %-18s %12s%s\n", r == row ? "\033[7m>" : " ", name, value(kind, r, vb), r == row ? "\033[0m" : "");
        }
        printf("\n  %s\n", row < NC ? C[row].help : "one band on its own");
        printf("  last peak %.2f%s   %s%s\n", lastPeak, lastPeak > 0.98f ? "  CLIPPING" : "", note, dirty ? "   (unsaved)" : "");
        printf("  space play  S x8  A radio  W write  R undo  D defaults  Q quit\n");
        int c = _getch();
        if (c == 'q' || c == 'Q') {
            if (dirty) { printf("\n  write before quitting? (y/n) "); int y = _getch(); if (y == 'y' || y == 'Y') write_xml(); }
            break;
        }
        if (c == 9) { kind = (kind + 1) % 3; fire(kind); continue; }
        if (c == ' ') { fire(kind); note = "played"; continue; }
        if (c == 's' || c == 'S') { spin(kind); note = "eight in a row"; continue; }
        if (c == 'a' || c == 'A') { radio(); note = "power on, eight dial clicks, power off"; continue; }
        if (c == 'r' || c == 'R') { memcpy(P[kind], LOADED[kind], sizeof P[kind]); push(); fire(kind); note = "undone"; continue; }
        if (c == 'd' || c == 'D') { parse(DEF_TXT[kind], P[kind]); push(); fire(kind); note = "shipped defaults"; dirty = 1; continue; }
        if (c == 'w' || c == 'W') {
            FILE* f = fopen("clicks.tuned.txt", "w");
            if (f) { for (int k = 0; k < 3; k++) { char l[2048]; format(P[k], l, sizeof l); fprintf(f, "%s = %s\n", KXML[k], l); } fclose(f); }
            note = write_xml() ? "written to radio.xml (the game hot-reloads it) and clicks.tuned.txt"
                               : "written to clicks.tuned.txt";
            dirty = 0; continue;
        }
        if (c == 0 || c == 0xE0) {
            int k2 = _getch(); float mul = (GetKeyState(VK_SHIFT) & 0x8000) ? 5.0f : 1.0f;
            if (k2 == 72) row = row > 0 ? row - 1 : NROWS - 1;
            if (k2 == 80) row = (row + 1) % NROWS;
            if (k2 == 75 || k2 == 77) {
                adjust(kind, row, k2 == 77 ? mul : -mul); push(); dirty = 1;
                if (kind == 2 && (row == 7 || row == 8 || row == 9 || row == 13)) spin(kind); else fire(kind);
                note = "";
            }
        }
    }
    return 0;
}
