/* test_field — does the one field behave like sound, on geometry we can draw by hand?
 *
 * These are the properties the WHOLE design rests on, so they are checked before any of it is
 * wired to the audio. The old model was judged by a gate that turned out to be measuring
 * something nobody was hearing, three separate times; the answer to that is not a better gate,
 * it is assertions small enough to be obviously about the thing they name.
 */
#include "cmm_field.h"
#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include <string.h>

#define W 64                 /* blocks per side of the toy world */
#define H 16
#define STEP 2
static unsigned char solid[W][H][W];      /* 1 = a full opaque block */

static int fails = 0, checks = 0;
static void ck(const char *name, int ok, const char *fmt, ...) {
    checks++;
    va_list ap; (void)ap;
    printf("  %-46s %s   %s\n", name, ok ? "PASS" : "FAIL", fmt);
    if (!ok) fails++;
}
#include <stdarg.h>
static void ckf(const char *name, int ok, const char *fmt, ...) {
    char buf[256]; va_list ap; va_start(ap, fmt); vsnprintf(buf, sizeof buf, fmt, ap); va_end(ap);
    ck(name, ok, buf);
}

static int isSolid(int x, int y, int z) {
    if (x < 0 || x >= W || y < 0 || y >= H || z < 0 || z >= W) return 1;   /* outside = opaque */
    return solid[x][y][z];
}

/* Fill the field's geometry from `solid`, exactly the way the game side will: transmission across
 * a face is the ENERGY sum over its sub-faces, so one gap in an otherwise solid face dominates.
 * That is a doorway, and it comes out of the averaging rather than out of special-case code. */
static float faceT(int cx, int cy, int cz, int axis) {
    float acc = 0; int n = 0;
    for (int a = 0; a < STEP; a++)
    for (int b = 0; b < STEP; b++) {
        int x, y, z, px, py, pz;
        if (axis == 0) { x = cx*STEP;     y = cy*STEP + a; z = cz*STEP + b; px = x-1; py = y; pz = z; }
        else if (axis == 1) { x = cx*STEP + a; y = cy*STEP; z = cz*STEP + b; px = x; py = y-1; pz = z; }
        else { x = cx*STEP + a; y = cy*STEP + b; z = cz*STEP; px = x; py = y; pz = z-1; }
        int blocked = isSolid(x, y, z) || isSolid(px, py, pz);
        acc += blocked ? 0.0f : 1.0f;
        n++;
    }
    return n ? acc / n : 0.0f;
}
static float cellFill(int cx, int cy, int cz) {
    int s = 0, n = 0;
    for (int a = 0; a < STEP; a++) for (int b = 0; b < STEP; b++) for (int c = 0; c < STEP; c++) {
        s += isSolid(cx*STEP+a, cy*STEP+b, cz*STEP+c); n++;
    }
    return n ? (float)s / n : 1.0f;
}

static void fill_all(cmm_fld *f) {
    int cx, cy, cz, guard = 0;
    while (cmm_fld_next_pending(f, &cx, &cy, &cz) && guard++ < 400000)
        cmm_fld_set_cell(f, cx, cy, cz, faceT(cx,cy,cz,0), faceT(cx,cy,cz,1), faceT(cx,cy,cz,2),
                         cellFill(cx, cy, cz));
}

int main(void) {
    printf("cmm_field ABI %d\n\n", cmm_fld_abi());

    /* ── an open field: nothing between anything ─────────────────────────────────────── */
    memset(solid, 0, sizeof solid);
    for (int x = 0; x < W; x++) for (int z = 0; z < W; z++) solid[x][0][z] = 1;   /* ground */
    cmm_fld *f = cmm_fld_create(W/STEP, H/STEP, W/STEP, STEP);
    cmm_fld_recentre(f, 32, 4, 32);
    fill_all(f);
    cmm_fld_solve(f, 32.5f, 4.5f, 32.5f);
    float near = cmm_fld_sample_db(f, 36.5f, 4.5f, 32.5f);
    float far  = cmm_fld_sample_db(f, 50.5f, 4.5f, 32.5f);
    ckf("open air, 4 blocks: no excess", near < 1.0f, "%.2f dB (want ~0)", near);
    ckf("open air, 18 blocks: no excess", far  < 1.5f, "%.2f dB (want ~0)", far);

    /* ── a wall right across, no way round: must be a lot ────────────────────────────── */
    for (int y = 1; y < H; y++) for (int z = 0; z < W; z++) solid[40][y][z] = 1;
    cmm_fld_dirty(f, 40, 8, 32, 40);
    fill_all(f);
    cmm_fld_solve(f, 32.5f, 4.5f, 32.5f);
    float thru = cmm_fld_sample_db(f, 44.5f, 4.5f, 32.5f);
    ckf("sealed wall between: heavily occluded", thru > 25.0f, "%.2f dB (want > 25)", thru);

    /* ── cut a doorway in it, straight ahead: the same spot must open right up ───────── */
    for (int y = 1; y <= 4; y++) for (int z = 30; z <= 31; z++) solid[40][y][z] = 0;
    cmm_fld_dirty(f, 40, 2, 31, 12);
    fill_all(f);
    cmm_fld_solve(f, 32.5f, 2.5f, 30.5f);
    float door = cmm_fld_sample_db(f, 44.5f, 2.5f, 30.5f);
    ckf("clear line through a doorway: barely occluded", door < 3.0f, "%.2f dB (want < 3)", door);
    ckf("...and far less than the sealed wall", thru - door > 25.0f,
        "sealed %.1f vs doorway %.1f dB", thru, door);

    /* ── round a corner, off the sight line: more than the doorway, less than the wall ── */
    float around = cmm_fld_sample_db(f, 44.5f, 2.5f, 44.5f);
    ckf("off to the side of the doorway: in between", around > door && around < thru,
        "%.2f dB (doorway %.1f, sealed %.1f)", around, door, thru);

    /* ── WHAT THE COARSE GRID COSTS, MEASURED RATHER THAN ASSUMED ─────────────────────
     * Cells are STEP blocks across, so an opening that straddles a cell boundary is spread over
     * four faces and each reads only part-open, where the same opening aligned to the grid reads
     * one face fully open. That is the price of flooding tens of thousands of cells instead of
     * millions, and it is a real effect, not a rounding error -- so it is pinned here. If it ever
     * grows past this bound the grid has stopped being a shortcut and started being the model. */
    memset(solid, 0, sizeof solid);
    for (int x = 0; x < W; x++) for (int z = 0; z < W; z++) solid[x][0][z] = 1;
    for (int y = 1; y < H; y++) for (int z = 0; z < W; z++) solid[40][y][z] = 1;
    for (int y = 1; y <= 4; y++) for (int z = 31; z <= 32; z++) solid[40][y][z] = 0;  /* straddles */
    cmm_fld_dirty(f, 40, 4, 32, 40);
    fill_all(f);
    cmm_fld_solve(f, 32.5f, 2.5f, 31.5f);
    float straddle = cmm_fld_sample_db(f, 44.5f, 2.5f, 31.5f);
    ckf("same doorway, straddling the grid: costs some", straddle - door < 9.0f,
        "aligned %.1f vs straddling %.1f dB (gap %.1f)", door, straddle, straddle - door);

    /* ── THE PROPERTY THE WHOLE DESIGN IS FOR: a source moving is smooth ─────────────── */
    float worst = 0, prev = -1;
    for (int i = 0; i <= 240; i++) {
        float x = 34.0f + i * (14.0f / 240.0f);          /* walk a source through the doorway */
        float v = cmm_fld_sample_db(f, x, 2.5f, 32.5f);
        if (prev >= 0) { float d = fabsf(v - prev) / (14.0f / 240.0f); if (d > worst) worst = d; }
        prev = v;
    }
    ckf("moving source: no step (dB per block)", worst < 6.0f, "worst %.2f dB/block", worst);

    /* ── and it never vanishes: unreachable reads as silent, not as absent ───────────── */
    memset(solid, 1, sizeof solid);                       /* seal the listener in solid rock */
    for (int y = 1; y <= 2; y++) solid[32][y][32] = 0;
    cmm_fld_dirty(f, 32, 4, 32, 40);
    fill_all(f);
    cmm_fld_solve(f, 32.5f, 1.5f, 32.5f);
    float sealed = cmm_fld_sample_db(f, 44.5f, 1.5f, 32.5f);
    ckf("sealed in rock: reads max, never 0", sealed > 40.0f, "%.2f dB", sealed);

    printf("\n  cells %lld   reached %lld   solve %lld us\n",
           cmm_fld_stat(f, 0), cmm_fld_stat(f, 2), cmm_fld_stat(f, 3));
    cmm_fld_destroy(f);
    printf("\n%d/%d checks passed\n", checks - fails, checks);
    return fails ? 1 : 0;
}
