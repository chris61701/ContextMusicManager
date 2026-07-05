/* cmm_reverb.h — header-only Freeverb-style stereo reverb for the world-emitter voice.
 *
 * A compact port of the classic Freeverb (8 damped comb filters in parallel + 4 allpass in series,
 * per channel, with a small L/R delay spread for stereo width). Mono in, stereo out; produces the WET
 * signal only (the caller mixes it against the dry). It's driven per-tick from a room probe: room →
 * comb feedback (decay/tail length), damp → HF damping in the feedback (dead/absorptive rooms sound
 * darker & shorter). All state is callback-thread-only; buffers are fixed-size (embedded in cmm_out,
 * which is calloc'd, so they start zeroed) and lengths are scaled to the device sample rate at init.
 *
 * Everything is `static`/`static inline` so this header can just be #included into one .c file with no
 * addition to the build script. */
#ifndef CMM_REVERB_H
#define CMM_REVERB_H

#include <string.h>

#define CMM_RV_NCOMB     8
#define CMM_RV_NAP       4
#define CMM_RV_COMB_MAX  1800   /* longest comb at 48 kHz (1617·48000/44100 + spread) ≈ 1783 */
#define CMM_RV_AP_MAX    650    /* longest allpass at 48 kHz (556·48000/44100 + spread) ≈ 630 */

typedef struct {
    float combBufL[CMM_RV_NCOMB][CMM_RV_COMB_MAX];
    float combBufR[CMM_RV_NCOMB][CMM_RV_COMB_MAX];
    int   combLenL[CMM_RV_NCOMB], combLenR[CMM_RV_NCOMB];
    int   combIdxL[CMM_RV_NCOMB], combIdxR[CMM_RV_NCOMB];
    float combStoreL[CMM_RV_NCOMB], combStoreR[CMM_RV_NCOMB];   /* one-pole LPF store in each comb's feedback */

    float apBufL[CMM_RV_NAP][CMM_RV_AP_MAX];
    float apBufR[CMM_RV_NAP][CMM_RV_AP_MAX];
    int   apLenL[CMM_RV_NAP], apLenR[CMM_RV_NAP];
    int   apIdxL[CMM_RV_NAP], apIdxR[CMM_RV_NAP];

    float feedback;    /* comb feedback (tail length) — from room size */
    float damp1, damp2;/* comb-feedback low-pass coefficients — from damping */
    float apFeedback;  /* allpass feedback (fixed 0.5) */
    float inGain;      /* fixed input gain (Freeverb 0.015) */
} cmm_reverb;

/* Flush IEEE-754 denormals (exponent field == 0) to zero — a silent tail can otherwise drift into
 * denormal range and cost 10–100× CPU on the audio thread. */
static inline float cmm_rv_undenorm(float x)
{
    unsigned int u; memcpy(&u, &x, sizeof(u));
    return (u & 0x7f800000u) == 0u ? 0.0f : x;
}

static inline float cmm_rv_comb(float* buf, int len, int* idx, float* store,
                                float in, float feedback, float damp1, float damp2)
{
    float y = buf[*idx];
    *store = cmm_rv_undenorm(y * damp2 + *store * damp1);
    buf[*idx] = in + *store * feedback;
    if (++(*idx) >= len) *idx = 0;
    return y;
}

static inline float cmm_rv_allpass(float* buf, int len, int* idx, float in, float fb)
{
    float bufout = buf[*idx];
    float y = -in + bufout;
    buf[*idx] = in + bufout * fb;
    if (++(*idx) >= len) *idx = 0;
    return y;
}

/* room 0..1 → feedback (Freeverb: room·0.28+0.7); damp 0..1 → feedback low-pass. Cheap; call per block. */
static inline void cmm_reverb_set(cmm_reverb* r, float room, float damp)
{
    if (room < 0.0f) room = 0.0f; else if (room > 1.0f) room = 1.0f;
    if (damp < 0.0f) damp = 0.0f; else if (damp > 1.0f) damp = 1.0f;
    r->feedback = room * 0.28f + 0.7f;
    r->damp1    = damp * 0.4f;
    r->damp2    = 1.0f - r->damp1;
}

static inline void cmm_reverb_init(cmm_reverb* r, int sampleRate)
{
    static const int combT[CMM_RV_NCOMB] = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    static const int apT[CMM_RV_NAP]     = { 556, 441, 341, 225 };
    const int spread = 23;
    float scale = (sampleRate > 0 ? (float)sampleRate : 44100.0f) / 44100.0f;
    int i;

    memset(r->combBufL, 0, sizeof(r->combBufL));
    memset(r->combBufR, 0, sizeof(r->combBufR));
    memset(r->apBufL,   0, sizeof(r->apBufL));
    memset(r->apBufR,   0, sizeof(r->apBufR));

    for (i = 0; i < CMM_RV_NCOMB; i++) {
        int ll = (int)(combT[i]           * scale + 0.5f);
        int rl = (int)((combT[i] + spread) * scale + 0.5f);
        if (ll < 1) ll = 1; else if (ll > CMM_RV_COMB_MAX) ll = CMM_RV_COMB_MAX;
        if (rl < 1) rl = 1; else if (rl > CMM_RV_COMB_MAX) rl = CMM_RV_COMB_MAX;
        r->combLenL[i] = ll; r->combLenR[i] = rl;
        r->combIdxL[i] = 0;  r->combIdxR[i] = 0;
        r->combStoreL[i] = 0.0f; r->combStoreR[i] = 0.0f;
    }
    for (i = 0; i < CMM_RV_NAP; i++) {
        int ll = (int)(apT[i]           * scale + 0.5f);
        int rl = (int)((apT[i] + spread) * scale + 0.5f);
        if (ll < 1) ll = 1; else if (ll > CMM_RV_AP_MAX) ll = CMM_RV_AP_MAX;
        if (rl < 1) rl = 1; else if (rl > CMM_RV_AP_MAX) rl = CMM_RV_AP_MAX;
        r->apLenL[i] = ll; r->apLenR[i] = rl;
        r->apIdxL[i] = 0;  r->apIdxR[i] = 0;
    }
    r->apFeedback = 0.5f;
    r->inGain     = 0.015f;
    cmm_reverb_set(r, 0.5f, 0.5f);
}

/* One mono sample in → stereo wet out (unity level; caller applies the wet gain). */
static inline void cmm_reverb_tick(cmm_reverb* r, float in, float* outL, float* outR)
{
    float inp = in * r->inGain;
    float l = 0.0f, rr = 0.0f;
    int i;
    for (i = 0; i < CMM_RV_NCOMB; i++)
        l  += cmm_rv_comb(r->combBufL[i], r->combLenL[i], &r->combIdxL[i], &r->combStoreL[i],
                          inp, r->feedback, r->damp1, r->damp2);
    for (i = 0; i < CMM_RV_NCOMB; i++)
        rr += cmm_rv_comb(r->combBufR[i], r->combLenR[i], &r->combIdxR[i], &r->combStoreR[i],
                          inp, r->feedback, r->damp1, r->damp2);
    for (i = 0; i < CMM_RV_NAP; i++)
        l  = cmm_rv_allpass(r->apBufL[i], r->apLenL[i], &r->apIdxL[i], l,  r->apFeedback);
    for (i = 0; i < CMM_RV_NAP; i++)
        rr = cmm_rv_allpass(r->apBufR[i], r->apLenR[i], &r->apIdxR[i], rr, r->apFeedback);
    *outL = l;
    *outR = rr;
}

#endif /* CMM_REVERB_H */
