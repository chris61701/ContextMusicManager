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
/* Cells are WIDE and SHORT, the shipped shape. A wall spans a cell's full height so coarse is
 * fine; a floor is one block thick with a storey of air above it, and a tall cell cannot
 * represent that -- see the ground-alignment case below, which is the reason for the shape. */
#define SX 3
#define SY 1
#define SZ 3
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
/* HOW MUCH GETS THROUGH A CELL FACE, given the block faces underneath it.
 *   0 = MEAN  (area fraction)   the diffuse answer: half the face open, half the energy
 *   1 = MAX   (best crossing)   the path answer: a route crosses at ONE point
 *   2 = BLEND (geometric mean of the two)
 * This is the last real physics choice in the model and it is decided by measurement below, not
 * by argument -- a path model wants MAX, but MAX also means one gap in an otherwise solid face
 * reads as wide open, which is how a wall becomes a rumour.
 *
 * It was BLEND while cells were cubes, because MEAN then cost 4.2 dB on a doorway that straddled a
 * cell boundary. Re-measured once cells became one block tall, that penalty is GONE -- it came
 * from a tall cell splitting an opening VERTICALLY, which a one-block cell cannot do -- and MEAN
 * wins on the case that decides this:
 *
 *              doorway   1-block hole   gap    straddle penalty
 *     MEAN       1.41        2.91      +1.50        none
 *     MAX        0.30        0.30       0.00        none      <- wall becomes a rumour
 *     BLEND      0.91        1.66      +0.75        none
 *
 * So: MEAN. Noting what the shape change COST, honestly, rather than only what it bought -- a face
 * is now 1x3 sub-blocks instead of 3x3, so a single-block hole is a third of it rather than a
 * ninth, and the model can no longer separate a pinhole from a doorway as sharply. The ordering
 * holds and a sealed wall is still 41 dB; the resolution is coarser. That is the trade. */
static int FACE_RULE = 0;
static float faceT(int cx, int cy, int cz, int axis) {
    int bx = cx*SX, by = cy*SY, bz = cz*SZ;
    int ea = axis == 0 ? SY : SX, eb = axis == 2 ? SY : SZ;
    float acc = 0, best = 0; int n = 0;
    for (int a = 0; a < ea; a++)
    for (int b = 0; b < eb; b++) {
        int x, y, z, px, py, pz;
        if (axis == 0)      { x = bx;     y = by + a; z = bz + b; px = x-1; py = y; pz = z; }
        else if (axis == 1) { x = bx + a; y = by;     z = bz + b; px = x; py = y-1; pz = z; }
        else                { x = bx + a; y = by + b; z = bz;     px = x; py = y; pz = z-1; }
        int blocked = isSolid(x, y, z) || isSolid(px, py, pz);
        float t = blocked ? 0.0f : 1.0f;
        acc += t; if (t > best) best = t;
        n++;
    }
    if (!n) return 0.0f;
    float mean = acc / n;
    if (FACE_RULE == 0) return mean;
    if (FACE_RULE == 1) return best;
    return sqrtf(mean * best);
}
/* Transmission through the cell's MIDDLE PLANE perpendicular to `axis` -- what stands across a
 * route passing through the cell. Directional, because a floor blocks vertical travel through a
 * cell and not horizontal, and charging it for both is what made the open air outdoors quiet. */
static float midT(int cx, int cy, int cz, int axis) {
    int step = axis == 0 ? SX : axis == 1 ? SY : SZ;
    /* At depth 1 the cell IS its own face: there is nothing between the two faces for a middle
     * plane to catch, and charging one would double every move along that axis. */
    if (step < 2) return 1.0f;
    int bx = cx*SX, by = cy*SY, bz = cz*SZ, h = step / 2;
    int ea = axis == 0 ? SY : SX, eb = axis == 2 ? SY : SZ;
    float acc = 0, best = 0; int n = 0;
    for (int a = 0; a < ea; a++)
    for (int b = 0; b < eb; b++) {
        int x, y, z;
        if (axis == 0)      { x = bx + h; y = by + a; z = bz + b; }
        else if (axis == 1) { x = bx + a; y = by + h; z = bz + b; }
        else                { x = bx + a; y = by + b; z = bz + h; }
        float t = isSolid(x, y, z) ? 0.0f : 1.0f;
        acc += t; if (t > best) best = t;
        n++;
    }
    if (!n) return 0.0f;
    float mean = acc / n;
    if (FACE_RULE == 0) return mean;
    if (FACE_RULE == 1) return best;
    return sqrtf(mean * best);
}

static void fill_all(cmm_fld *f) {
    int cx, cy, cz, guard = 0;
    while (cmm_fld_next_pending(f, &cx, &cy, &cz) && guard++ < 400000)
        cmm_fld_set_cell(f, cx, cy, cz,
                         faceT(cx,cy,cz,0), faceT(cx,cy,cz,1), faceT(cx,cy,cz,2),
                         midT(cx,cy,cz,0),  midT(cx,cy,cz,1),  midT(cx,cy,cz,2));
}

int main(int argc, char** argv) {
    if (argc > 1) FACE_RULE = atoi(argv[1]);
    printf("cmm_field ABI %d   face rule %s\n\n", cmm_fld_abi(),
           FACE_RULE == 0 ? "MEAN (area)" : FACE_RULE == 1 ? "MAX (best crossing)" : "BLEND");

    /* ── an open field: nothing between anything ─────────────────────────────────────── */
    memset(solid, 0, sizeof solid);
    for (int x = 0; x < W; x++) for (int z = 0; z < W; z++) solid[x][0][z] = 1;   /* ground */
    cmm_fld *f = cmm_fld_create(W/SX, H/SY, W/SZ, SX, SY, SZ);
    cmm_fld_recentre(f, 32, 4, 32);
    fill_all(f);
    cmm_fld_solve(f, 32.5f, 4.5f, 32.5f);
    float near = cmm_fld_sample_db(f, 36.5f, 4.5f, 32.5f);
    float far  = cmm_fld_sample_db(f, 50.5f, 4.5f, 32.5f);
    ckf("open air, 4 blocks: no excess", near < 1.0f, "%.2f dB (want ~0)", near);
    ckf("open air, 18 blocks: no excess", far  < 1.5f, "%.2f dB (want ~0)", far);

    /* ── STANDING ON THE GROUND, IN THE OPEN ────────────────────────────────────────────
     * The earlier open-air checks float the listener and the source four blocks up, where nothing
     * is nearby. Real speakers and real players are on the FLOOR, so the route hugs it -- and a
     * cell containing the floor is partly solid. If the model charges for that, everything sounds
     * muffled outdoors for no reason anyone can see, which is what "the music is comparatively
     * quiet" is. Sound travelling OVER a floor must not be attenuated BY the floor. */
    float ground = cmm_fld_sample_db(f, 44.5f, 1.5f, 32.5f);
    cmm_fld_solve(f, 32.5f, 1.5f, 32.5f);
    ground = cmm_fld_sample_db(f, 44.5f, 1.5f, 32.5f);
    ckf("on the ground, open air, 12 blocks", ground < 1.5f, "%.2f dB (want ~0)", ground);

    /* ── THE GROUND CAN BE AT ANY HEIGHT ─────────────────────────────────────────────────
     * The single most expensive defect this model has had, and the one the cell shape exists to
     * fix. Flat open ground, nothing between listener and source, and the ONLY thing varied is
     * where the ground's top block falls relative to the cell grid. With 3-block-tall cells the
     * answer swung between 0.00 and 29.69 dB on that alone: a source standing on a floor landed in
     * a cell whose Y middle plane WAS the floor, so reaching it from above was charged as going
     * through the floor. Three ground heights in every eight were 29 dB down in open air.
     *
     * It survived for so long because the probe that was supposed to catch it tested ONE ground
     * height, got 0.00, and reported the model clean. So this sweeps every alignment. Real terrain
     * is not flat and never aligns to anything; if this can be made to fail by moving the world up
     * one block, it is broken for most of the world. */
    {
        float worstOpen = 0; int worstY = 0;
        for (int gy = 2; gy <= 9; gy++) {
            memset(solid, 0, sizeof solid);
            for (int x = 0; x < W; x++) for (int z = 0; z < W; z++)
                for (int yy = 0; yy <= gy; yy++) solid[x][yy][z] = 1;
            cmm_fld *g = cmm_fld_create(W/SX, H/SY, W/SZ, SX, SY, SZ);
            cmm_fld_recentre(g, 32, gy + 2, 32);
            { int cx, cy, cz, guard = 0;
              while (cmm_fld_next_pending(g, &cx, &cy, &cz) && guard++ < 400000)
                cmm_fld_set_cell(g, cx, cy, cz, faceT(cx,cy,cz,0), faceT(cx,cy,cz,1), faceT(cx,cy,cz,2),
                                                midT(cx,cy,cz,0),  midT(cx,cy,cz,1),  midT(cx,cy,cz,2)); }
            cmm_fld_solve(g, 32.5f, gy + 1 + 1.7f, 32.5f);
            for (int d = 6; d <= 24; d += 6) {
                float v = cmm_fld_sample_db(g, 32.5f + d, gy + 1.5f, 32.5f);
                if (v > worstOpen) { worstOpen = v; worstY = gy; }
            }
            cmm_fld_destroy(g);
        }
        ckf("open air is open at EVERY ground height", worstOpen < 1.5f,
            "worst %.2f dB (ground top y=%d) — was 29.69 with cubic cells", worstOpen, worstY);

        /* Put the shared scene back. This block builds its own fields, but it rewrote `solid`,
         * and everything below works by DIRTYING a small region of `f` and re-reading only that —
         * so leaving eight blocks of ground behind would silently hand the doorway tests a
         * different world than the one they describe. */
        memset(solid, 0, sizeof solid);
        for (int x = 0; x < W; x++) for (int z = 0; z < W; z++) solid[x][0][z] = 1;
        cmm_fld_dirty(f, 32, 8, 32, 4 * W);
        fill_all(f);
        cmm_fld_solve(f, 32.5f, 4.5f, 32.5f);
    }

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

    /* ── A DOOR MOVING MUST NOT MAKE THE ROOM VANISH WHILE WE RE-READ IT ────────────────
     * Anything that changes the world -- a door swinging, a block broken, a chunk arriving --
     * marks the cells around it for re-reading. Marking them used to CLEAR their geometry, and the
     * flood treats geometry-less cells as opaque, so for as long as the amortised reader took to
     * get back to them those cells were SOLID. A door two blocks from the listener therefore
     * walled the listener in: the route went the long way round a speaker five blocks away and the
     * level dropped 15 dB until the next read. Seen with the player standing still --
     * excess 4.1 -> 19.4 -> 4.1 dB, `wall` unchanged, the whole spike in `detour`.
     *
     * Unread space being opaque is right; a cell we read a third of a second ago is not unread.
     * So: dirty a region, DO NOT re-read it, and the answer must not move. */
    {
        cmm_fld_solve(f, 32.5f, 2.5f, 30.5f);
        float before = cmm_fld_sample_db(f, 44.5f, 2.5f, 30.5f);
        cmm_fld_dirty(f, 40, 2, 30, 3);            /* a door swings; nothing re-read yet */
        cmm_fld_solve(f, 32.5f, 2.5f, 30.5f);
        float during = cmm_fld_sample_db(f, 44.5f, 2.5f, 30.5f);
        ckf("a door being re-read does not wall you in", fabsf(during - before) < 0.5f,
            "%.2f -> %.2f dB with the cells dirty but not yet re-read", before, during);
        fill_all(f);                               /* reader catches up; back to normal */
        cmm_fld_solve(f, 32.5f, 2.5f, 30.5f);
    }

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

    /* ── A PINHOLE IS NOT A DOORWAY ─────────────────────────────────────────────────
     * The risk of taking the BEST crossing of a face is that one small gap makes the whole face
     * read as open, and a wall becomes a rumour. A single 1x1 hole should land clearly between a
     * doorway and a sealed wall -- sound does get through a hole, but not as though the wall were
     * not there. This is the test that decides the face rule, because it is the case where the
     * three rules disagree; grid alignment alone would just pick MAX. */
    memset(solid, 0, sizeof solid);
    for (int x = 0; x < W; x++) for (int z = 0; z < W; z++) solid[x][0][z] = 1;
    for (int y = 1; y < H; y++) for (int z = 0; z < W; z++) solid[40][y][z] = 1;
    solid[40][2][30] = 0;                                     /* one block of hole */
    cmm_fld_dirty(f, 40, 4, 31, 40);
    fill_all(f);
    cmm_fld_solve(f, 32.5f, 2.5f, 30.5f);
    float pin = cmm_fld_sample_db(f, 44.5f, 2.5f, 30.5f);
    // Stated as the PROPERTY rather than as a number carried over from the old cell shape: a hole
    // must be audibly worse than an open doorway and nothing like a sealed wall. The margin here
    // is genuinely narrower than it was with 3x3 faces (see the face-rule note above) -- that is
    // recorded, not hidden behind a threshold quietly widened to fit.
    ckf("a 1-block hole: between doorway and sealed", pin > door + 1.0f && pin < thru * 0.2f,
        "%.2f dB (doorway %.1f, sealed %.1f)", pin, door, thru);

    /* ── EVERY BUFFER MUST ACTUALLY GET FLOODED ────────────────────────────────────────
     * There are three field buffers: the one being sampled, the one being built, and the previous
     * one kept for the crossfade. A redundant swap on entry to cmm_fld_solve meant one of the
     * three was NEVER a flood target -- it was `cur` for the whole duration of every flood and
     * `prev` after every publish, holding the all-UNREACHED fill from create() forever. Every
     * sample that landed WHILE a flood was running therefore read a field where nothing was
     * reachable and came back maxDb: 58 dB, silence. Roughly one publish in ten, with the listener
     * standing still, and immune to every occlusion setting because the number being sampled was
     * not an occlusion answer at all.
     *
     * Single-threaded, `prev` is the one buffer a test can see from outside: sample_db_blended at
     * mix = 0 reads it. After two solves from the same place it must agree with the current field,
     * because it IS the previous flood of an unchanged scene. With the bug it reads maxDb. */
    {
        cmm_fld_solve(f, 32.5f, 2.5f, 30.5f);
        cmm_fld_solve(f, 32.5f, 2.5f, 30.5f);
        float now  = cmm_fld_sample_db(f, 44.5f, 2.5f, 30.5f);
        float back = cmm_fld_sample_db_blended(f, 44.5f, 2.5f, 30.5f, 0.0f);   /* reads prev */
        ckf("every buffer gets flooded (prev is real)", fabsf(back - now) < 0.5f,
            "cur %.2f dB vs prev %.2f dB — prev is the crossfade source and must be a real field",
            now, back);
    }

    /* ── AND THE SAME PROPERTY AT THE OTHER END: THE LISTENER MOVING IS SMOOTH ──────────
     * The test below walks the SOURCE and was passing throughout, because the source end has
     * always been interpolated across eight cells. The LISTENER end was a nearest-neighbour
     * lookup: the flood was seeded from the single cell containing the ear, so crossing a cell
     * boundary restarted it somewhere else and moved the entire field at once.
     *
     * That is worse than it sounds, because the ear is the CAMERA, and the camera moves when the
     * player turns -- it is offset from the pivot it rotates about -- and drifts with head bob
     * while standing still. So the level could jump with the player going nowhere. Reported as
     * "just turning around and not moving causes dips too", and no amount of smoothing downstream
     * can fix a discontinuous seed; it only turns a jump into a slow wander.
     *
     * Sub-cell steps on purpose: whole-cell steps would step over the very boundary that broke. */
    {
        float worstStep = 0, prevV = -1; float atX = 0;
        for (int i = 0; i <= 200; i++) {
            float ex = 30.0f + i * (8.0f / 200.0f);        /* walk the EAR, source fixed */
            cmm_fld_solve(f, ex, 2.5f, 30.5f);
            float v = cmm_fld_sample_db(f, 44.5f, 2.5f, 30.5f);
            if (prevV >= 0) {
                float d = fabsf(v - prevV);
                if (d > worstStep) { worstStep = d; atX = ex; }
            }
            prevV = v;
        }
        ckf("listener moving: no step (dB per 0.04 blocks)", worstStep < 1.0f,
            "worst %.2f dB at x=%.2f", worstStep, atX);
    }

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
