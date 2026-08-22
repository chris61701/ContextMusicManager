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
#define SX 1
#define SY 1
#define SZ 1
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

/* AN EXPECTED FAILURE: a property we want, against a defect we have not fixed yet.
 *
 * The alternative -- weakening the assertion until it passes -- is what the pinhole check below
 * had to do, and the cost is that the gap stops being visible. This keeps the real property
 * written down and measured on every run without turning the suite red, and it goes HARD RED the
 * moment it starts passing, which is the prompt to promote it to an ordinary check. */
static int xfails = 0, xpasses = 0;
static void ckxf(const char *name, int ok, const char *fmt, ...) {
    char buf[256]; va_list ap; va_start(ap, fmt); vsnprintf(buf, sizeof buf, fmt, ap); va_end(ap);
    printf("  %-46s %s   %s\n", name, ok ? "XPASS" : "xfail", buf);
    if (ok) xpasses++; else xfails++;
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
/* Mirrors CMMConfig.WorldEmitterFieldAperturePow. A face that is PART open transmits
 * openFraction^this, not openFraction: linear area is too generous, and a face is now 1x3
 * sub-blocks rather than 3x3, so a one-block hole went from a ninth of it to a third. */
static float APERTURE_POW = 1.0f;
/* THE MATERIAL IN ONE HALF OF A CELL, as a linear transmission (1 = nothing there, 0 = sealed).
 *
 * Mirrors what FieldOcclusion.CellNumbers hands the game's field, and the mirroring is the point:
 * this harness once consulted BOTH sides of a face where the game consults only one, so it graded
 * a model that seals better than the one that ships and hid a defect that cost a night. A harness
 * kinder than production is worse than no harness.
 *
 * A column of the cross-section counts as blocked in a half if ANY sub-block in that half's slab
 * is solid -- thickness does not enter into it, a sliver plate blocks like a full block of the
 * same stuff. What the halves record is WHERE the material is, not how much of it there is.
 *
 * THE SPLIT is the part that is easy to get wrong. A route passing straight through crosses BOTH
 * halves and pays the sum, so the pair has to compose back to the cell's own material or every
 * wall in the world doubles. Both halves hold material => each reports sqrt(T). Only one holds it
 * => that one reports T and the other reports 1, which is what makes a half block cost a whole
 * wall to cross and nothing at all to stand beside. */
static float halfT(int cx, int cy, int cz, int axis, int positive) {
    int step = axis == 0 ? SX : axis == 1 ? SY : SZ;
    int nHi  = (step + 1) / 2 - 1;        /* negative half = slab [0, nHi]     */
    int pLo  = step - (step + 1) / 2;     /* positive half = slab [pLo, step-1] */
    int bx = cx*SX, by = cy*SY, bz = cz*SZ;
    int ea = axis == 0 ? SY : SX, eb = axis == 2 ? SY : SZ;
    int nCol = 0, blkNeg = 0, blkPos = 0, blkAny = 0;
    for (int a = 0; a < ea; a++)
    for (int b = 0; b < eb; b++) {
        int hitN = 0, hitP = 0;
        for (int d = 0; d < step; d++) {
            int x, y, z;
            if (axis == 0)      { x = bx + d; y = by + a; z = bz + b; }
            else if (axis == 1) { x = bx + a; y = by + d; z = bz + b; }
            else                { x = bx + a; y = by + b; z = bz + d; }
            if (isSolid(x, y, z)) { if (d <= nHi) hitN = 1; if (d >= pLo) hitP = 1; }
        }
        nCol++; blkNeg += hitN; blkPos += hitP; if (hitN || hitP) blkAny++;
    }
    if (!nCol) return 0.0f;
    int mine  = positive ? blkPos : blkNeg;
    int other = positive ? blkNeg : blkPos;
    if (!mine) return 1.0f;                       /* nothing in this half: free to cross */
    float mean = (float)(nCol - blkAny) / (float)nCol;
    float best = blkAny < nCol ? 1.0f : 0.0f;
    float T;
    if (FACE_RULE == 1)      T = best;
    else if (FACE_RULE == 2) T = sqrtf(mean * best);
    else                     T = (mean <= 0.0f || mean >= 1.0f) ? mean : powf(mean, APERTURE_POW);
    return other ? sqrtf(T) : T;                  /* split so the two halves compose back to T */
}

static void fill_all(cmm_fld *f) {
    int cx, cy, cz, guard = 0;
    while (cmm_fld_next_pending(f, &cx, &cy, &cz) && guard++ < 400000)
        cmm_fld_set_cell(f, cx, cy, cz,
                         halfT(cx,cy,cz,0,0), halfT(cx,cy,cz,1,0), halfT(cx,cy,cz,2,0),
                         halfT(cx,cy,cz,0,1), halfT(cx,cy,cz,1,1), halfT(cx,cy,cz,2,1));
}

int main(int argc, char** argv) {
    if (argc > 1) FACE_RULE = atoi(argv[1]);
    if (argc > 2) APERTURE_POW = (float)atof(argv[2]);
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
            fill_all(g);
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

    /* ── A SHUT DOOR SEALS A ROOM FROM ANYWHERE IN IT, INCLUDING AGAINST THE WALL ───────
     * Every scene in this file stands the listener in the middle of the room, and that single
     * habit hid the defect that cost most of a night. The flood is seeded into the eight cells
     * around the ear, which span two cells on each axis -- four blocks at the shipped size -- and
     * seeds used to be charged distance only, never the faces crossed to reach them. So standing
     * within about a block of a wall placed a seed on the FAR SIDE of it and the flood began
     * outside the room:
     *
     *     listener 1.5 blocks from the wall   door open 9.3 -> shut 57.2   = 47.9 dB
     *     listener 1.1 blocks from the wall   door open 5.8 -> shut  5.8   =  0.0 dB
     *
     * In a bathroom you are always within a block of a wall, so the door never did anything. The
     * property is therefore not "a shut door seals a room" but "a shut door seals a room WHEREVER
     * YOU ARE STANDING IN IT", and it is swept accordingly. */
    {
        float worstDelta = 1e9f; float atOff = 0;
        for (int k = 0; k < 4; k++) {
            float lx = 32.5f - k * 0.6f;                 /* walk the listener toward the wall */
            float got[2];
            for (int shut = 0; shut <= 1; shut++) {
                memset(solid, 0, sizeof solid);
                for (int x = 0; x < W; x++) for (int z = 0; z < W; z++) solid[x][0][z] = 1;
                /* a sealed box around the listener, one door block in the -X wall */
                for (int x = 29; x <= 35; x++) for (int z = 29; z <= 35; z++)
                    for (int y = 1; y <= 6; y++) solid[x][y][z] = 1;
                for (int x = 30; x <= 34; x++) for (int z = 30; z <= 34; z++)
                    for (int y = 1; y <= 5; y++) solid[x][y][z] = 0;
                if (!shut) { solid[29][1][32] = 0; solid[29][2][32] = 0; solid[29][3][32] = 0; }
                cmm_fld_dirty(f, 32, 4, 32, 4 * W);
                fill_all(f);
                cmm_fld_solve(f, lx, 2.7f, 32.5f);
                got[shut] = cmm_fld_sample_db(f, 22.5f, 2.5f, 32.5f);
            }
            float delta = got[1] - got[0];
            if (delta < worstDelta) { worstDelta = delta; atOff = lx; }
        }
        ckf("a shut door seals from ANYWHERE in the room", worstDelta > 20.0f,
            "worst open-vs-shut %.1f dB (listener x=%.1f) — was 0.0 against the wall", worstDelta, atOff);
    }

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
    // WHAT ONE-BLOCK CELLS COST, stated plainly because it is a real loss and the rest of this
    // change is a win.
    //
    // At one block per cell a one-block hole is a fully open cell face, so it reads 0 dB: exactly
    // like a doorway. The model no longer has anywhere to express "this opening is small". With
    // 3x3 sub-block faces a pinhole was a NINTH of a face and read ~6 dB; that arithmetic is gone
    // along with the sub-blocks, and it cannot be recovered by any knob, because there is nothing
    // left to take a fraction of.
    //
    // What is missing is an aperture/diffraction term -- attenuation from the absolute SIZE of an
    // opening rather than its share of a cell -- which the model has never had and which is the
    // honest way to model this. Until then a wall with a single block knocked out of it leaks as
    // freely as an open door, and that is a known gap rather than a passing test.
    //
    // So the assertion is reduced to what is still true and still worth guarding: a hole is
    // nothing like a sealed wall, and a sealed wall stays sealed.
    ckf("a 1-block hole is not a sealed wall", pin < thru * 0.2f,
        "%.2f dB (doorway %.1f, sealed %.1f)", pin, door, thru);
    ckxf("...and a 1-block hole is not a doorway either", pin - door > 3.0f,
         "hole %.2f vs doorway %.2f dB — no aperture-size term in the model", pin, door);

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

    /* ── THE PROPERTY THE WHOLE DESIGN IS FOR: a source moving is smooth ───────────────
     * THROUGH THE OPENING, at z=30.5, which is where the hole in this scene is. It used to sweep
     * z=32.5 while calling itself "through the doorway", so it actually walked the source straight
     * into solid wall -- where a fast change is CORRECT, not a defect. It passed anyway because
     * blending the surrounding cells in dB smeared the wall crossing into a ramp, and it started
     * failing the moment that blend was corrected to sum energy. A test that only passes while a
     * neighbouring bug is present is worse than no test.
     *
     * And on a DOORWAY, not the pinhole scene it inherited by being last in the file. A source
     * squeezing through a single 1x1 hole genuinely changes fast -- 8 dB a block -- and holding
     * that up as a smoothness failure would be measuring the sharpest geometry in the suite and
     * calling the answer wrong. The property worth guarding is that an ordinary opening, the thing
     * players actually walk through, produces a curve. */
    {
        memset(solid, 0, sizeof solid);
        for (int x = 0; x < W; x++) for (int z = 0; z < W; z++) solid[x][0][z] = 1;
        for (int y = 1; y < H; y++) for (int z = 0; z < W; z++) solid[40][y][z] = 1;
        for (int y = 1; y <= 4; y++) for (int z = 30; z <= 31; z++) solid[40][y][z] = 0;
        cmm_fld_dirty(f, 32, 8, 32, 4 * W);
        fill_all(f);
        cmm_fld_solve(f, 32.5f, 2.5f, 30.5f);
    }
    float worst = 0, prev = -1;
    for (int i = 0; i <= 240; i++) {
        float x = 34.0f + i * (14.0f / 240.0f);          /* walk a source through the opening */
        float v = cmm_fld_sample_db(f, x, 2.5f, 30.5f);
        if (prev >= 0) { float d = fabsf(v - prev) / (14.0f / 240.0f); if (d > worst) worst = d; }
        prev = v;
    }
    ckf("moving source: no step (dB per block)", worst < 6.0f, "worst %.2f dB/block", worst);

    /* ── TWO WALLS MEETING AT A CORNER: THE DIAGONAL SEAM ───────────────────────────
     * A diagonal move enters NEITHER of the two cells it passes between, so it read both their
     * faces as open and paid nothing -- sound went straight through a corner you cannot walk or
     * see through. Fixed by charging the SHOULDERS of a diagonal (the cheaper of the two cells a
     * two-step route would have had to cross), on an in-game measurement alone:
     *
     *     four sides blocked, corners open    excess  1.0 dB
     *     corners filled in as well           excess 21.7 dB
     *
     * That fix had no scene in this file. This is it, and it is built to isolate the SEAM and
     * nothing else: two half-plane walls run to the edges of the world, so the only direct join
     * between the two quadrants is the one diagonal at their corner and every other route has to
     * cross a full wall. Both wall cells are reachable only from their negative side, which keeps
     * the separate defect below out of this measurement. */
    {
        memset(solid, 0, sizeof solid);
        for (int x = 0; x < W; x++) for (int z = 0; z < W; z++) solid[x][0][z] = 1;
        for (int y = 1; y < H; y++) {
            for (int z = 0; z <= 32; z++) solid[33][y][z] = 1;    /* wall A: x=33, running -Z */
            for (int x = 0; x <= 32; x++) solid[x][y][33] = 1;    /* wall B: z=33, running -X */
        }
        cmm_fld *g = cmm_fld_create(W/SX, H/SY, W/SZ, SX, SY, SZ);
        cmm_fld_recentre(g, 32, 4, 32);
        fill_all(g);
        cmm_fld_solve(g, 32.5f, 2.5f, 32.5f);                     /* ear in the -X/-Z quadrant */
        float seam = cmm_fld_sample_db(g, 33.5f, 2.5f, 33.5f);    /* diagonally across the corner */
        float same = cmm_fld_sample_db(g, 30.5f, 2.5f, 30.5f);    /* same quadrant: must be open */
        cmm_fld_destroy(g);
        ckf("same quadrant is open (the scene is sane)", same < 1.0f, "%.2f dB", same);
        ckf("a corner seam is not a way through", seam > 40.0f,
            "%.1f dB across the diagonal — 0 without the shoulder charge", seam);
    }

    /* ── A ROUTE MUST NOT ENTER A SOLID BLOCK FOR FREE ──────────────────────────────
     * These two were written as expected failures against the model that shipped, and the change
     * to six half-voxels per cell (ABI 4) is what turned them green. Kept as ordinary checks now,
     * with what they caught, because it is the kind of defect that comes back the moment somebody
     * decides one number per cell would be simpler.
     *
     * A cell's material used to be stored on its three NEGATIVE faces, on the rule that whichever
     * cell contains the wall is the one that reports it. The face between C and C+X therefore
     * belonged to C+X, so for the block sitting in C:
     *
     *     entering C from -X   charged C's own face      -- correct
     *     entering C from +X   charged C+X's face (air)  -- FREE
     *     leaving  C toward +X charged C+X's face (air)  -- FREE
     *
     * A route that entered through one positive face and left through another paid NOTHING AT ALL
     * for the block it had just walked through. A plane wall survived it, because a plane is
     * always reached from its negative side -- which is why every other wall check in this file
     * passed throughout. Any wall you can get AROUND did not: the speaker below is walled in on
     * four sides with only its corners open, and the route went in through the +X face of one
     * side block and out through that same block's +Z face. Two straight moves, neither charged,
     * so the shoulder rule above never even got a chance to fire.
     *
     *     lone block, stood inside          0.10 dB  ->  58.60
     *     boxed speaker, corners open        0.1 dB  ->   58.6   (corners filled: 58.6 both ways)
     *
     * Same root cause as a half block or a shut door failing to block from the cell you are
     * standing in: the model recorded WHAT material a cell held and threw away WHERE. */
    {
        memset(solid, 0, sizeof solid);
        for (int x = 0; x < W; x++) for (int z = 0; z < W; z++) solid[x][0][z] = 1;
        for (int y = 1; y < H; y++) solid[36][y][32] = 1;         /* one lone block, air all round */
        cmm_fld *g = cmm_fld_create(W/SX, H/SY, W/SZ, SX, SY, SZ);
        cmm_fld_recentre(g, 32, 4, 32);
        fill_all(g);
        cmm_fld_solve(g, 32.5f, 2.5f, 32.5f);
        float inside = cmm_fld_sample_db(g, 36.5f, 2.5f, 32.5f);
        cmm_fld_destroy(g);
        ckf("a lone solid block costs a wall to enter", inside > 20.0f,
            "%.2f dB inside the block — was 0.10, reached free through its +X face", inside);
    }
    {
        float got[2];
        for (int corners = 0; corners <= 1; corners++) {
            memset(solid, 0, sizeof solid);
            for (int x = 0; x < W; x++) for (int z = 0; z < W; z++) solid[x][0][z] = 1;
            for (int y = 1; y < H; y++) {
                solid[35][y][32] = 1; solid[37][y][32] = 1;       /* the four sides */
                solid[36][y][31] = 1; solid[36][y][33] = 1;
                if (corners) {                                    /* ...and the four corners */
                    solid[35][y][31] = 1; solid[35][y][33] = 1;
                    solid[37][y][31] = 1; solid[37][y][33] = 1;
                }
            }
            cmm_fld *g = cmm_fld_create(W/SX, H/SY, W/SZ, SX, SY, SZ);
            cmm_fld_recentre(g, 32, 4, 32);
            fill_all(g);
            cmm_fld_solve(g, 32.5f, 2.5f, 32.5f);                      /* ear outside, 4 blocks off */
            got[corners] = cmm_fld_sample_db(g, 36.5f, 2.5f, 32.5f);   /* the boxed-in speaker */
            cmm_fld_destroy(g);
        }
        ckf("walling a speaker in seals it, corners or not", fabsf(got[1] - got[0]) < 6.0f,
            "corners open %.1f vs filled %.1f dB — was 0.1 vs 58.6", got[0], got[1]);
    }

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
    if (xfails)  printf("%d known defect(s) still open (xfail)\n", xfails);
    if (xpasses) printf("%d expected failure(s) now PASS \u2014 promote them\n", xpasses);
    return (fails || xpasses) ? 1 : 0;
}
