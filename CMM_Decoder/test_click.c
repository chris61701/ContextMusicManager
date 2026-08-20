/* Render the radio's clicks to a WAV so they can be JUDGED BY EAR without loading a world.
 * "Wrong sound" is not a measurement, and the only instrument for it is a person listening --
 * so the loop between changing the synthesis and hearing the result should be seconds, not a
 * world load. Writes the on click, the off click, and a short dial sequence (on, three detents,
 * off) so they can be heard in the order they actually occur. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

extern int  cmm_mix_render_click(int kind, int rate, float* out, int maxFrames);
extern void cmm_mix_set_click_tone_kind(int which, float hz, float snap, float lenMs,
                                        float body, float bodyHz, float dbl, float vary);

#define RATE 48000
static void wr16(FILE* f, int v){ fputc(v & 255, f); fputc((v >> 8) & 255, f); }
static void wr32(FILE* f, int v){ wr16(f, v & 0xffff); wr16(f, (v >> 16) & 0xffff); }

static void wav(const char* path, const float* s, int n) {
    FILE* f = fopen(path, "wb");
    if (!f) { printf("cannot write %s\n", path); return; }
    fwrite("RIFF", 1, 4, f); wr32(f, 36 + n * 2); fwrite("WAVE", 1, 4, f);
    fwrite("fmt ", 1, 4, f); wr32(f, 16); wr16(f, 1); wr16(f, 1);
    wr32(f, RATE); wr32(f, RATE * 2); wr16(f, 2); wr16(f, 16);
    fwrite("data", 1, 4, f); wr32(f, n * 2);
    for (int i = 0; i < n; i++) {
        float v = s[i]; if (v > 1) v = 1; else if (v < -1) v = -1;
        wr16(f, (int)(v * 32000.0f) & 0xffff);
    }
    fclose(f);
    printf("  wrote %-22s %6.2f s\n", path, (double)n / RATE);
}

static float buf[RATE * 8];

/* Mix one click in at `at` seconds, at `gain`. */
static int put(float* dst, int cap, double at, int kind, float gain) {
    static float tmp[RATE];
    int n = cmm_mix_render_click(kind, RATE, tmp, RATE);
    int off = (int)(at * RATE);
    for (int i = 0; i < n && off + i < cap; i++) dst[off + i] += tmp[i] * gain;
    return off + n;
}

int main(int argc, char** argv) {
    /* The shipped defaults, straight out of Config/radio.xml. Toggle for power, detent for the
     * dial -- different objects, different sounds. */
    const float VARY = 0.5f;
    float onHz=2600, onSn=0.55f, onLen=18, onBd=0.45f, onBHz=520, onDb=0.55f;
    float ofHz=2100, ofSn=0.48f, ofLen=22, ofBd=0.50f, ofBHz=420, ofDb=0.50f;
    float dlHz=4200, dlSn=0.80f, dlLen=12, dlBd=0.30f, dlBHz=950, dlDb=0.15f;
    if (argc >= 7) { onHz=atof(argv[1]); onSn=atof(argv[2]); onLen=atof(argv[3]);
                     onBd=atof(argv[4]); onBHz=atof(argv[5]); onDb=atof(argv[6]); }
    if (argc >= 13){ ofHz=atof(argv[7]); ofSn=atof(argv[8]); ofLen=atof(argv[9]);
                     ofBd=atof(argv[10]); ofBHz=atof(argv[11]); ofDb=atof(argv[12]); }
    if (argc >= 19){ dlHz=atof(argv[13]); dlSn=atof(argv[14]); dlLen=atof(argv[15]);
                     dlBd=atof(argv[16]); dlBHz=atof(argv[17]); dlDb=atof(argv[18]); }
    cmm_mix_set_click_tone_kind(0, onHz, onSn, onLen, onBd, onBHz, onDb, VARY);
    cmm_mix_set_click_tone_kind(1, ofHz, ofSn, ofLen, ofBd, ofBHz, ofDb, VARY);
    cmm_mix_set_click_tone_kind(2, dlHz, dlSn, dlLen, dlBd, dlBHz, dlDb, VARY);

    int n;
    memset(buf, 0, sizeof buf);
    n = put(buf, RATE * 8, 0.05, 2, 1.0f);
    wav("click_on.wav", buf, n + RATE / 10);

    memset(buf, 0, sizeof buf);
    n = put(buf, RATE * 8, 0.05, 3, 1.0f);
    wav("click_off.wav", buf, n + RATE / 10);

    memset(buf, 0, sizeof buf);
    n = put(buf, RATE * 8, 0.05, 4, 1.0f);
    wav("click_dial.wav", buf, n + RATE / 10);

    /* EIGHT DETENTS IN A ROW. The point of the per-fire variation is that this should sound like
     * a knob being spun, not like one sample retriggered eight times -- so this is the file that
     * actually tests it. */
    memset(buf, 0, sizeof buf);
    for (int i = 0; i < 8; i++) put(buf, RATE * 8, 0.10 + i * 0.16, 4, 1.0f);
    wav("click_dial_spin.wav", buf, (int)(1.6 * RATE));

    /* The sequence you actually hear: power on, three station changes, power off. */
    memset(buf, 0, sizeof buf);
    put(buf, RATE * 8, 0.10, 2, 1.0f);
    put(buf, RATE * 8, 0.70, 4, 1.0f);
    put(buf, RATE * 8, 1.05, 4, 1.0f);
    put(buf, RATE * 8, 1.40, 4, 1.0f);
    put(buf, RATE * 8, 2.10, 3, 1.0f);
    wav("click_sequence.wav", buf, (int)(2.7 * RATE));

    /* Peak, so a "wrong sound" complaint is never actually a clipping complaint. */
    float pk = 0;
    for (int i = 0; i < (int)(2.7 * RATE); i++) { float a = fabsf(buf[i]); if (a > pk) pk = a; }
    printf("\n  sequence peak %.3f (%.1f dBFS)\n", pk, 20.0 * log10(pk > 0 ? pk : 1e-9));
    return 0;
}
