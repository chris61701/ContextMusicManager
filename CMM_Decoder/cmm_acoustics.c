/* ─────────────────────────────────────────────────────────────────────────
 * cmm_acoustics.c — scene arena, openness, thickness field
 * ─────────────────────────────────────────────────────────────────────────
 * Stage 1 of the port. Every constant and every rule here is lifted from
 * tools/CmmAcousticsJudge, and the comments say WHY rather than what, because the
 * why is what cost the time. Where a value looks arbitrary it is not: it was
 * measured, and the measurement is named.
 * ───────────────────────────────────────────────────────────────────────── */
#include "cmm_acoustics.h"
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <stdio.h>

/* ── model constants, identical to the JS ────────────────────────────────── */
/* Openness is a FRACTION, not a flag. A boolean test made the graph's topology
 * binary in a quantity that is not: a door 80% open still read as shut, so the
 * air path snapped into existence at the end of every swing instead of opening
 * with it. See-through blocks (railings, broken windows) already carry ~0 loss
 * from CellLoss, so a low loss value is the dump's way of saying sound passes. */
#define OPEN_LOSS 0.05f  /* at or below this loss, fully open */
#define OPEN_FULL 0.50f  /* at or above, fully closed; between, a fraction */
#define OPEN_EPS  0.02f  /* below this a voxel is not air at all */

/* CAP the thickness field. Beyond a few blocks of clearance a space is simply
 * "open" and there is nothing left to subdivide, but an uncapped field keeps
 * rising into the sky around a POI and generates hundreds of spurious local
 * maxima. One capture measured 85% air with a median core of 10.4 blocks, so most
 * of its 381 raw regions were subdivisions of empty sky, and greedy merging over
 * that many edges collapsed the entire POI into one region. Capping makes all
 * wide-open air a single plateau, hence one seed, and keeps the merge graph
 * small. */
#define THICK_CAP 6.0f

/* A room's core must be at least this open, or it JOINS a room rather than founding
 * one. Below it, voxels are the thinnest layer of air, which forms a connected SHELL
 * hugging every wall surface in the map -- as one region that shell bridges
 * everything, because its persistence against any neighbour is zero, and it glued a
 * whole POI into a single space. A wall-hugging skin is not a room. */
#define SEED_MIN 1.6f

/* Merge threshold, RELATIVE not absolute. An absolute depth cannot serve rooms of
 * wildly different size: 1.5 blocks of pinch is nothing beside 18 blocks of open sky
 * and everything beside a 2-block closet, so small rooms kept dissolving into the
 * outdoor volume. What matters is how much of its own openness a space loses at its
 * boundary. */
#define MERGE_H 0.62f

/* Share of a region's boundary that must open onto the world before it counts as
 * outdoors. Measured separation is total -- enclosed regions read 0.0%, open ones 12%
 * and up -- so this is not delicate. */
#define SKY_SPLIT 0.02f

/* Corridor subdivision. A tunnel has no pinch, so the merge correctly keeps it whole
 * -- and that is wrong acoustically, because the model assumes LINE OF SIGHT within a
 * region and you cannot see round a bend. Measured on the real cave capture: the whole
 * tunnel system came out as one 382k-voxel region and the longest air route found was
 * exactly the straight-line distance, ratio 1.00 everywhere. No tunnel routing happened
 * at all, which is why caves could never work. So a region much longer than it is thick
 * is cut along its long axis at intervals of a few times its own thickness. */
#define SEG_MIN_STEP 6.0f
#define SEG_STEP_MULT 2.5f

/* --- transmission and routing constants ---------------------------------- */
/* Per-voxel transmission loss is SUPERLINEAR in material density (the mass law):
 * dB = DB_MAT * frac^MAT_POW, so concrete at 0.9 costs about five times wood at 0.5.
 * Summed along the path as a LINE INTEGRAL weighted by the ray's length inside each
 * voxel -- counting whole voxels made a one-voxel wall cost 6 or 12 dB depending on
 * the ray's angle, and a single block of listener movement could swap them. That was
 * the root of the 10-16 dB/block cliffs, which is why fixing the candidate set three
 * different ways never removed them. */
/* RUNTIME, not compile-time, and the reason is a listening report. The author's first
 * in-game verdict on this model was "on the other side of a wall it sounds like there's
 * barely anything between me and that wall", and the measurement agreed: a shut wooden
 * wall costs about 11 dB, of which most of what remains never crossed the wall as a ray
 * at all. Which number is RIGHT is a judgement made by ear, in the game, on a real POI --
 * so the ear needs to be able to turn it there rather than wait for a rebuild per guess.
 * Defaults are the ported values, so an untouched build is bit-identical to the port. */
double cmm_ac_db_mat = 40.1;
double cmm_ac_mat_pow = 2.74;
#define DB_MAT cmm_ac_db_mat
#define MAT_POW cmm_ac_mat_pow
#define DB_VOXEL_MAX 60.0

/* A voxel crossed VERTICALLY stands for a whole floor assembly -- deck, joists,
 * ceiling below -- not for the thin partition a voxel means sideways, so the same
 * material must cost more. Scaled by the ray's verticality so it is continuous in
 * direction rather than switching on an axis test. Magnitude from the mass law: a
 * floor is roughly 3x a wall's mass, and 20*log10(3) is about 9.5 dB, which on wood's
 * 6 dB is a factor near 2.6. */
#define VERT_MULT 2.6

/* Distance attenuation, dB per block. Exponential rather than inverse-square: the
 * choice predates occlusion being expressed in dB and is the one architectural
 * inconsistency the offline work did not resolve -- 4P/R assumes inverse-square, and
 * at 12 blocks the two laws differ by 17 dB, which DIFFUSE_CAL patches at roughly one
 * distance. Ported as-is so the port reproduces the model; revisiting it is a model
 * question, not a port question. */
#define SLOPE 0.35

/* DISTANCE, AS A LAW RATHER THAN A SLOPE.
 *
 * SLOPE dB/block is exponential and at 0.35 nearly flat: eight blocks costs 2.8 dB where
 * inverse-square costs 18. Two consequences, both reported from the game and neither tunable:
 *
 *   "the speaker sounds far away no matter how close I get to it"; and
 *   "wall 100, speaker right behind the wall, still sounds like it isn't muffled at all" --
 *   because the sound is not coming THROUGH the wall, it is coming AROUND, and a detour three
 *   times as long cost 3.5 dB where the physical answer is 9.5. No material constant can reach
 *   a path the material is not on.
 *
 * A RADIATOR IS NOT A POINT, so 1/r alone will not do either: d/dr of 20*log10(r) is 8.7/r dB
 * per block, a cliff at close range sitting exactly where a listener walks. A finite radiator of
 * radius a is a plane wave for r << a and 1/r for r >> a, which sqrt(r^2 + a^2) writes exactly,
 * removing the singularity without touching the far field where the calibration lives.
 *
 * Safe to change only because the bridge transports a RATIO against this model's own free field,
 * so what matters is the law's shape rather than its scale. Routing still orders on SLOPE*length:
 * Dijkstra wants an additive metric, length is one, and the order is monotone either way. */
static double dist_db(double len, double rad)
{
    if (rad < 0.5) rad = 0.5;
    double r = sqrt(len * len + rad * rad);
    return 20.0 * log10(r < 1.0 ? 1.0 : r);
}

/* An opening's radius from its area: the scale over which it stops looking like a point. */
static double ap_rad(double area)
{
    double a = sqrt((area > 1.0 ? area : 1.0) / 3.14159265358979323846);
    return a < 0.5 ? 0.5 : a;
}

#define BEND_DB 6.0          /* full reversal through a fully-pinched portal */
#define MIN_APERTURE_DB (-30.0)  /* a pinhole is not infinite loss */
#define SEED_R 3             /* air pockets within this radius seed the solve */

/* --- diffuse field --------------------------------------------------------- */
/* Calibration for the field's absolute scale, applied ONCE per room where the field is
 * computed. Applied at the listener instead it multiplied every coupled term too, which
 * makes the coupling an amplifier -- a passivity test flagged 100% of voxels, correctly.
 * It is also a fudge over the exponential-vs-inverse-square mismatch: swept 0/6/10/16 it
 * costs three spec scenarios and doubles real-geometry roughness to remove, and never
 * touches the reach failures it was suspected of, so it stays until the distance law is
 * settled. */
/* 16 bridged an exponential direct path to a 4P/R diffuse term that assumes inverse-square.
 * With both on the same law that gap is gone; 7 is where all nine spec scenarios pass. */
double cmm_ac_diffuse_cal = 7.0;
#define DIFFUSE_CAL cmm_ac_diffuse_cal

/* A reverberant field needs a space many wavelengths across, so sound bounces many times
 * before being absorbed. The segmentation happily produces ONE-VOXEL regions, and 4P/R
 * applied to those is not merely inaccurate but explosive: surf 6, R 1.4, so the 4/R term
 * alone is +4.7 dB. The physical test is the mean free path 4V/S against a wavelength. */
#define WAVELEN_BLOCKS 1.7
#define MFP_MIN (0.5 * WAVELEN_BLOCKS)

/* And a reverberant field is a property of an ENCLOSURE. Sky exposure separates indoors
 * from outdoors with no overlap: enclosed rooms measure 0.0% of their boundary open to the
 * world, outdoor ones 43-57%. Gating only the direct term left the wall-coupling pass free
 * to hand the outdoors a field anyway, which is where it does the most damage. */
/* AN OPENING IS SKY, ACOUSTICALLY. The sky pair below says it exactly right -- where the
 * boundary is sky, energy leaves instead of reverberating -- and a DOORWAY is the same
 * statement, never applied to it. Openings were already counted in the absorption, correctly,
 * but a space whose boundary is mostly opening was still granted a FULL reverberant field, and
 * there is no such thing: a diffuse field needs sound to bounce many times before it is
 * absorbed, and if most of the boundary is a hole it leaves on the first pass.
 *
 * Measured on the bunker, rooms that were being given a full field:
 *     n=5789  surf=976  open=2808  -> boundary 74% opening, field -21.9 dB
 *     n=1603  surf=152  open=2309  -> boundary 94% opening, field -20.8 dB
 * Those passages produced the flat reverb floor a speaker in an OPEN FIELD sat under: their
 * fields radiated back out through the very openings that should have stopped them existing.
 * Past eight blocks that floor became the level, so the level stopped falling with distance and
 * occlusion switched off across the whole POI -- reported as "a hint quieter when closer", "I
 * can hear it very far away behind a wall", and "muffling is inconsistent", all three at once.
 *
 * TIGHT (0.10-0.40), and the first attempt at this got it backwards. A wide ramp was chosen to
 * improve a smoothness number, which meant barely gating anything -- and gating is precisely what
 * stops sound bleeding through walls. Measured through the store's walls, mean occlusion and the
 * share of positions where NO occlusion can be expressed at all:
 *
 *     ramp 0.20-0.95    8-16 blk  +1.8 dB (77%)   24-32  +6.0 dB (100%)
 *     ramp 0.10-0.40    8-16 blk  -3.7 dB ( 9%)   24-32  -3.9 dB ( 25%)
 *
 * Tighter is better on the smoothness metric too (store 5.45 against 6.36), so the earlier
 * conclusion came from comparing two values inside the wrong region. */
#define OPEN_SHARE_FULL 0.10
#define OPEN_SHARE_NONE 0.40
#define SKY_FULL 0.05
#define SKY_NONE 0.25

#define WALL_MAX_THICK 3     /* regions farther apart than this share no wall */

/* First-Fresnel-zone area in blocks^2 at typical room geometry: the coherent extent of
 * the wavefront an opening samples. An opening wider than this is not a constriction at
 * all, which is why openFrac saturates at 1. */
#define FRESNEL_AREA 20.0

/* --- listener evaluation -------------------------------------------------- */
/* The transmission disc sits AT THE BARRIER, never at the listener. Offsetting the
 * listener and asking what each ray crosses answers "what is directly between us", which
 * is the wrong question: behind a steel column in a glass wall it charges you for the
 * column, when what you hear is the glass beside it radiating into your space. */
/* THE ZONE'S RADIUS IS GEOMETRY, NOT A CONSTANT.
 * A flat 3.0 blocks wherever the barrier happens to be is wrong in the one place it matters
 * most. The first Fresnel zone's radius is sqrt(lambda*d1*d2/(d1+d2)) and COLLAPSES as the
 * barrier approaches either end of the path. A speaker sits inside its own block, so for the
 * near barrier -- its own casing -- d1 is a fraction of a block and the true radius is under
 * one, not three.
 *
 * At three blocks the disc reaches past the speaker entirely and samples the shelf it stands
 * on, the wall behind it and the ceiling above, then charges that geometry as occlusion between
 * the speaker and a listener standing right next to it. Measured on the real captures: -3.3 dB
 * in the bunker and -2.1 dB beside the store's shelf speakers with NOTHING in the way. With the
 * radius computed rather than fixed, -0.7 and -0.6.
 *
 * That is what "it gets muffled when I'm closer to it, especially while right on top of it"
 * was. It could never be seen in a synthetic scene, because a test room puts nothing beside the
 * speaker to sample.
 *
 * Mid-path the formula gives sqrt(1.7*d/4) -- 2.9 blocks over 20 -- so the old constant was
 * right for the case it was fitted on and wrong everywhere else. Floored at one block: the
 * geometry IS a voxel grid, so a disc narrower than a block samples below the resolution of its
 * own input and collapses toward a single ray. */
#define FRESNEL_LAMBDA 1.7
#define FRESNEL_R_MIN 1.0
#define FRESNEL_R_MAX 4.0
static double fresnel_r(double d1, double d2)
{
    double s = d1 + d2;
    double r = sqrt(FRESNEL_LAMBDA * d1 * d2 / (s > 1e-3 ? s : 1e-3));
    return r < FRESNEL_R_MIN ? FRESNEL_R_MIN : (r > FRESNEL_R_MAX ? FRESNEL_R_MAX : r);
}
#define PRUNE_DB 25.0        /* below the strongest is under 0.4% of the energy */
#define MIN_TRANSMIT_DB 3.0  /* under this a path is an opening, not a partition */
#define LEG_FAN_R 1.5        /* bundle radius for aperture-to-listener material */
#define DIFFUSE_EXTRA 9.0    /* blocks: the field has travelled further than the direct */
#define MAX_ARRIVALS 32

/* A raw watershed region, before merging. Centroid is accumulated as sums so it
 * needs no second pass. surf/absorb are the enclosure's area and area-weighted
 * absorption, which the diffuse field needs; sky counts faces onto the world. */
struct ac_region {
    int   n;                 /* voxels */
    double cx, cy, cz;       /* centroid sums (local coords) */
    float core;              /* thickness at the seed: the region's own openness */
    int   surf;              /* boundary faces onto non-open material */
    double absorb;           /* sum of per-face alpha over those faces */
    int   sky;               /* boundary faces onto the world (out of bounds) */
};

/* Contact between two differently-labelled regions: shared open area, its
 * area-weighted centre, and the narrowest clearance across it. */
struct ac_port {
    int a, b;
    double area;
    double sx, sy, sz;       /* area-weighted position sums, WORLD coords */
    float thick;             /* widest clearance on the contact, for the pinch test */
};

/* A node-level boundary: the graph edge the routing solve walks. Carries the shared
 * open area, its area-weighted centre, the extent (so a listener beside a wide opening
 * is not charged the distance to its centroid), and the openness fraction. */
struct ac_edge {
    int a, b;                /* node ids */
    double area;
    double open_frac;        /* constriction: the open area against a Fresnel zone */
    double sx, sy, sz;       /* area-weighted position sums, WORLD */
    float thick;
    float x0, y0, z0, x1, y1, z1;   /* extent, WORLD */
    int   ra, rb;            /* the ROOMS either side, for the diffuse coupling */
};

/* A merged room: the unit that holds one reverberant field. */
struct ac_room {
    int n;                   /* voxels */
    int surf;                /* boundary faces onto material */
    double absorb;           /* area-weighted alpha over those faces */
    int sky;                 /* faces onto the world */
    double open_area;        /* total area of its openings -- an opening ABSORBS */
};

/* Where two rooms touch through MATERIAL rather than through an opening. The
 * segmentation records openings and says nothing about walls, and a sealed room's
 * reverberation is driven by its neighbour's field hitting the wall they share.
 * Computing that from a ray to the room's centroid instead put ~7 dB of pure distance
 * into a quantity that has no distance in it. */
struct ac_wall { int a, b; double area, tau; };

struct cmm_ac {
    int sx, sy, sz;          /* grid dimensions */
    int x0, y0, z0;          /* world origin of voxel (0,0,0) */
    long long n;             /* sx*sy*sz */

    /* One arena for everything. Sub-buffers point into it, so a scene is a single
     * allocation and a single free -- the managed side cannot leak part of one,
     * and the GC never sees any of it. */
    void *arena;
    size_t arena_bytes;

    float *loss;             /* n: per-voxel CellLoss as uploaded */
    unsigned char *flags;    /* n: CMM_AC_AIR etc. */
    float *open;             /* n: derived openness 0..1 */
    float *thick;            /* n: chamfer distance to the nearest non-open voxel */
    int   *label;            /* n: raw region id per voxel, -1 for solid */
    int   *root;             /* n: merged region id per voxel, -1 for solid */

    /* Regions and their contacts. These grow during BUILD, which is allowed: the
     * promise is that evaluation allocates nothing, not that a geometry rebuild
     * never does. A rebuild happens when a door swings, not every frame. */
    struct ac_region *reg; int nreg, cap_reg;
    struct ac_port   *prt; int nprt, cap_prt;
    int *ph; int ph_cap;     /* pair -> contact index, so contacts() is O(faces) */

    /* stage 3: corridor segments, nodes, node-level edges, rooms */
    int *node;               /* n: compact node id per voxel, -1 for solid */
    int nnodes;
    struct ac_edge *edge; int nedge, cap_edge;
    struct ac_room *room; int cap_room;
    int *eh; int eh_cap;     /* node-pair -> edge index */
    struct ac_wall *wall; int nwall, cap_wall;
    int *wh; int wh_cap;     /* room-pair -> wall index */
    int nrooms;              /* merged regions that survive, renumbered 0..nrooms-1 */
};

int cmm_ac_abi(void) { return CMM_AC_ABI; }

/* The model's own FREE-FIELD level at a distance: what a listener would get with nothing in
 * the way. Exposed (ABI 3) because the caller needs it as a REFERENCE, not as an answer.
 *
 * eval_db returns an absolute level whose distance dependence is this model's SLOPE -- 0.35
 * dB/block, about 2 dB over 8 blocks. Handing that absolute number to the audio layer makes it
 * REPLACE the game's own distance curve with a nearly flat one, and a speaker then sounds the
 * same distance away wherever you stand. Reported from the game in exactly those words: "the
 * speaker sounds far away no matter how close I get to it".
 *
 * Divide the two and the distance law cancels. What is left is the ratio the model actually
 * knows something about -- how much the geometry took away relative to a clear line at the same
 * range -- and the game's tuned curve keeps doing distance, which it was always better at. */
double cmm_ac_freefield_db(double dist)
{
    return -dist_db(dist > 0.0 ? dist : 0.0, 0.5);
}

/* Material loss and the diffuse field's absolute scale, live.
 *
 * WHICH ONE NEEDS A REBUILD, AND WHY IT MATTERS: db_mat and mat_pow feed the wall
 * transmission used when the SCENE's room coupling is built, so changing them leaves the
 * existing scene describing the old material until it is rebuilt. diffuse_cal is applied
 * where the room fields are computed, which is also scene time. So: all three are scene
 * inputs, and the caller must rebuild for a change to take full effect. Passing a
 * non-positive value leaves that parameter alone, so one knob can be turned at a time. */
void cmm_ac_set_tuning(double db_mat, double mat_pow, double diffuse_cal)
{
    if (db_mat > 0.0) cmm_ac_db_mat = db_mat;
    if (mat_pow > 0.0) cmm_ac_mat_pow = mat_pow;
    if (diffuse_cal >= 0.0) cmm_ac_diffuse_cal = diffuse_cal;
}

void cmm_ac_get_tuning(double *db_mat, double *mat_pow, double *diffuse_cal)
{
    if (db_mat) *db_mat = cmm_ac_db_mat;
    if (mat_pow) *mat_pow = cmm_ac_mat_pow;
    if (diffuse_cal) *diffuse_cal = cmm_ac_diffuse_cal;
}

/* index, matching the dump layout exactly so nothing needs reindexing */
static inline long long idx_of(const cmm_ac *ac, int x, int y, int z) {
    return (long long)x + (long long)ac->sx * ((long long)z + (long long)ac->sz * (long long)y);
}

cmm_ac *cmm_ac_create(int sx, int sy, int sz, int x0, int y0, int z0) {
    if (sx <= 0 || sy <= 0 || sz <= 0) return NULL;
    /* Guard against a dimension product that would overflow or exhaust memory
     * before it is multiplied out. The largest real capture is ~1.1M voxels; 64M
     * is far above anything a POI box produces and still allocatable. */
    double cells = (double)sx * (double)sy * (double)sz;
    if (cells > 64e6) return NULL;

    cmm_ac *ac = (cmm_ac *)calloc(1, sizeof(cmm_ac));
    if (!ac) return NULL;
    ac->sx = sx; ac->sy = sy; ac->sz = sz;
    ac->x0 = x0; ac->y0 = y0; ac->z0 = z0;
    ac->n = (long long)sx * sy * sz;

    /* loss + open + thick float, label + root int32, flags byte: 21 bytes/voxel.
     * The bunker's 1.09M voxels are therefore ~23 MB of scene -- a one-time
     * structure per POI, not a per-emitter field, and it replaces the shipped
     * design's per-listener flood entirely. */
    size_t nf = (size_t)ac->n;
    size_t bytes = nf * (3 * sizeof(float) + 3 * sizeof(int) + 1);
    ac->arena = malloc(bytes);
    if (!ac->arena) { free(ac); return NULL; }
    memset(ac->arena, 0, bytes);
    ac->arena_bytes = bytes;

    unsigned char *p = (unsigned char *)ac->arena;
    ac->loss  = (float *)p;         p += nf * sizeof(float);
    ac->open  = (float *)p;         p += nf * sizeof(float);
    ac->thick = (float *)p;         p += nf * sizeof(float);
    ac->label = (int *)p;           p += nf * sizeof(int);
    ac->root  = (int *)p;           p += nf * sizeof(int);
    ac->node  = (int *)p;           p += nf * sizeof(int);
    ac->flags = p;
    return ac;
}

void cmm_ac_destroy(cmm_ac *ac) {
    if (!ac) return;
    free(ac->reg);
    free(ac->prt);
    free(ac->ph);
    free(ac->edge);
    free(ac->room);
    free(ac->eh);
    free(ac->wall);
    free(ac->wh);
    free(ac->arena);
    free(ac);
}

int cmm_ac_set_voxels(cmm_ac *ac, const float *loss, const unsigned char *flags) {
    if (!ac || !loss || !flags) return 0;
    memcpy(ac->loss, loss, (size_t)ac->n * sizeof(float));
    memcpy(ac->flags, flags, (size_t)ac->n);
    return 1;
}

/* ── openness ─────────────────────────────────────────────────────────────
 * Game truth first (the air flag), then acoustically-open non-air. */
static void derive_open(cmm_ac *ac) {
    const long long n = ac->n;
    for (long long i = 0; i < n; i++) {
        if (ac->flags[i] & CMM_AC_AIR) { ac->open[i] = 1.0f; continue; }
        float l = ac->loss[i];
        if (l >= OPEN_FULL)      ac->open[i] = 0.0f;
        else if (l <= OPEN_LOSS) ac->open[i] = 1.0f;
        else                     ac->open[i] = 1.0f - (l - OPEN_LOSS) / (OPEN_FULL - OPEN_LOSS);
    }
}

/* ── thickness: two-sweep chamfer distance to the nearest non-open voxel ───
 * A local clearance measure, and the basis of the watershed: seeds go at its
 * local maxima, and regions merge or split on how much of it is lost at a
 * boundary. Two sweeps over a 26-neighbourhood is the standard approximation and
 * is close enough to Euclidean for this purpose.
 *
 * OUT OF BOUNDS COUNTS AS SOLID at the neighbour's own distance, matching the JS.
 * That is deliberate: the grid edge is not open sky, it is the end of what was
 * captured, and treating it as open would grow phantom clearance at every face. */
static void derive_thick(cmm_ac *ac) {
    const int sx = ac->sx, sy = ac->sy, sz = ac->sz;
    const long long n = ac->n;
    float *t = ac->thick;
    const float BIG = 1e9f;

    for (long long i = 0; i < n; i++) t[i] = ac->open[i] > OPEN_EPS ? BIG : 0.0f;

    /* The 26 neighbours, split by whether the forward sweep has already visited
     * them. Sweep order is y outer, z, x inner -- so "already seen" means a lower
     * y, or the same y and lower z, or the same y and z and lower x. */
    int dx[26], dy[26], dz[26]; float w[26]; int nnb = 0;
    for (int az = -1; az <= 1; az++)
        for (int ay = -1; ay <= 1; ay++)
            for (int ax = -1; ax <= 1; ax++) {
                if (!ax && !ay && !az) continue;
                dx[nnb] = ax; dy[nnb] = ay; dz[nnb] = az;
                w[nnb] = sqrtf((float)(ax * ax + ay * ay + az * az));
                nnb++;
            }

    for (int pass = 0; pass < 2; pass++) {
        const int fwd = (pass == 0);
        for (int yy = 0; yy < sy; yy++) {
            int y = fwd ? yy : sy - 1 - yy;
            for (int zz = 0; zz < sz; zz++) {
                int z = fwd ? zz : sz - 1 - zz;
                for (int xx = 0; xx < sx; xx++) {
                    int x = fwd ? xx : sx - 1 - xx;
                    long long i = idx_of(ac, x, y, z);
                    if (t[i] == 0.0f) continue;
                    float best = t[i];
                    for (int k = 0; k < nnb; k++) {
                        /* forward sweep uses the already-visited half, backward the rest */
                        int seen = (dy[k] < 0) || (dy[k] == 0 && dz[k] < 0)
                                || (dy[k] == 0 && dz[k] == 0 && dx[k] < 0);
                        if (seen != fwd) continue;
                        int nx = x + dx[k], ny = y + dy[k], nz = z + dz[k];
                        if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) {
                            if (w[k] < best) best = w[k];      /* edge of the capture = solid */
                            continue;
                        }
                        float v = t[idx_of(ac, nx, ny, nz)] + w[k];
                        if (v < best) best = v;
                    }
                    t[i] = best;
                }
            }
        }
    }

    for (long long i = 0; i < n; i++) if (t[i] > THICK_CAP) t[i] = THICK_CAP;
}

/* --- material along a ray, as a LINE INTEGRAL --------------------------------
 * skip_from / skip_to default to true for a source-to-listener ray: the speaker sits
 * inside a solid block of its own and must not be charged for it, and the listener's
 * own voxel is not a barrier between them. They must be FALSE when an endpoint is a
 * barrier patch, whose whole purpose is to sit inside material -- with both legs
 * skipping it the barrier vanished entirely and every patch read 0.0 dB through a
 * metre of concrete. */
static double seg_db_at(const cmm_ac *ac, double fx, double fy, double fz,
                        double tx, double ty, double tz, int skip_from, int skip_to) {
    double dx = tx - fx, dy = ty - fy, dz = tz - fz;
    double dist = sqrt(dx * dx + dy * dy + dz * dz);
    if (dist < 0.01) return 0.0;
    dx /= dist; dy /= dist; dz /= dist;

    int x = (int)floor(fx) - ac->x0, y = (int)floor(fy) - ac->y0, z = (int)floor(fz) - ac->z0;
    const int x0 = x, y0 = y, z0 = z;
    const int x1 = (int)floor(tx) - ac->x0, y1 = (int)floor(ty) - ac->y0, z1 = (int)floor(tz) - ac->z0;
    int sX = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
    int sY = dy > 0 ? 1 : (dy < 0 ? -1 : 0);
    int sZ = dz > 0 ? 1 : (dz < 0 ? -1 : 0);
    double lfx = fx - ac->x0, lfy = fy - ac->y0, lfz = fz - ac->z0;
    double tmx = fabs(dx) < 1e-6 ? 1e30 : (((sX > 0 ? x + 1 : x) - lfx) / dx);
    double tmy = fabs(dy) < 1e-6 ? 1e30 : (((sY > 0 ? y + 1 : y) - lfy) / dy);
    double tmz = fabs(dz) < 1e-6 ? 1e30 : (((sZ > 0 ? z + 1 : z) - lfz) / dz);
    double tDx = fabs(dx) < 1e-6 ? 1e30 : fabs(1.0 / dx);
    double tDy = fabs(dy) < 1e-6 ? 1e30 : fabs(1.0 / dy);
    double tDz = fabs(dz) < 1e-6 ? 1e30 : fabs(1.0 / dz);

    const double vert = 1.0 + (VERT_MULT - 1.0) * fabs(dy);
    double db = 0.0, tPrev = 0.0;
    for (int g = 0; g < 1024; g++) {
        double tNext = tmx < tmy ? tmx : tmy;
        if (tmz < tNext) tNext = tmz;
        if (dist < tNext) tNext = dist;
        double seg = tNext - tPrev;
        int atFrom = (x == x0 && y == y0 && z == z0);
        int atTo   = (x == x1 && y == y1 && z == z1);
        if (seg > 1e-6 && y >= 0 && y < ac->sy && x >= 0 && x < ac->sx && z >= 0 && z < ac->sz
            && !(skip_from && atFrom) && !(skip_to && atTo)) {
            double f = ac->loss[idx_of(ac, x, y, z)];
            if (f > 1e-4) {
                double v = DB_MAT * pow(f, MAT_POW) * vert;
                double cap = DB_VOXEL_MAX * vert;
                db += (v < cap ? v : cap) * seg;
            }
        }
        if (tNext >= dist - 1e-9) break;
        tPrev = tNext;
        if (tmx <= tmy && tmx <= tmz) { x += sX; tmx += tDx; }
        else if (tmy <= tmz)          { y += sY; tmy += tDy; }
        else                          { z += sZ; tmz += tDz; }
    }
    return db;
}

/* One aperture's constriction loss, applied ONCE at the bottleneck. Power through a
 * chain of openings is limited by the narrowest one, paid once -- a corridor of
 * constant width does not keep charging. */
static double constrict_db(double f) {
    double v = 10.0 * log10(f > 1e-4 ? f : 1e-4);
    return v < MIN_APERTURE_DB ? MIN_APERTURE_DB : v;
}

/* --- watershed segmentation ------------------------------------------------
 * Bottom-up: seed at local maxima of the thickness field and flood in DECREASING
 * thickness, so boundaries land on the geometry rather than on a grid. Top-down
 * subdivision keeps the grid, and a grid puts arbitrary boundaries through real
 * features -- nearly every stubborn defect of the cell-grid era traced to that:
 * a wall inside a cell, a throat measured relative to a cell, discontinuities at
 * cell boundaries. */

/* Sort key for the flood order. Descending thickness, TIES BROKEN BY INDEX
 * ASCENDING -- which is not cosmetic. JavaScript's Array.sort is stable, so the JS
 * model resolves equal thickness by original index order, while qsort is not stable
 * and would resolve it arbitrarily. Ties are common (the field is capped, so all
 * wide-open air shares one value), and a different tie-break relabels regions and
 * silently diverges from the model it is supposed to reproduce. Putting the index in
 * the comparator gives a total order identical to the JS. */
struct ac_key { float t; int i; };
static int key_cmp(const void *pa, const void *pb) {
    const struct ac_key *a = (const struct ac_key *)pa, *b = (const struct ac_key *)pb;
    if (a->t > b->t) return -1;
    if (a->t < b->t) return 1;
    return a->i < b->i ? -1 : (a->i > b->i ? 1 : 0);
}

static int reg_push(cmm_ac *ac, float core) {
    if (ac->nreg == ac->cap_reg) {
        int cap = ac->cap_reg ? ac->cap_reg * 2 : 256;
        struct ac_region *r = (struct ac_region *)realloc(ac->reg, (size_t)cap * sizeof(*r));
        if (!r) return -1;
        ac->reg = r; ac->cap_reg = cap;
    }
    struct ac_region *R = &ac->reg[ac->nreg];
    memset(R, 0, sizeof(*R));
    R->core = core;
    return ac->nreg++;
}

static const int NB6[6][3] = {{1,0,0},{-1,0,0},{0,1,0},{0,-1,0},{0,0,1},{0,0,-1}};

static int segment(cmm_ac *ac) {
    const int sx = ac->sx, sy = ac->sy, sz = ac->sz;
    const long long n = ac->n;
    for (long long i = 0; i < n; i++) ac->label[i] = -1;
    ac->nreg = 0;

    long long nopen = 0;
    for (long long i = 0; i < n; i++) if (ac->open[i] > OPEN_EPS) nopen++;
    if (nopen == 0) return 1;
    struct ac_key *ord = (struct ac_key *)malloc((size_t)nopen * sizeof(*ord));
    if (!ord) return 0;
    long long k = 0;
    for (long long i = 0; i < n; i++)
        if (ac->open[i] > OPEN_EPS) { ord[k].t = ac->thick[i]; ord[k].i = (int)i; k++; }
    qsort(ord, (size_t)nopen, sizeof(*ord), key_cmp);

    for (long long q = 0; q < nopen; q++) {
        long long i = ord[q].i;
        int y = (int)(i / ((long long)sx * sz));
        long long rem = i - (long long)y * sx * sz;
        int z = (int)(rem / sx), x = (int)(rem - (long long)z * sx);

        /* Join the THICKEST labelled neighbour, not the first found. A voxel on the
         * skin of a room belongs to that room, and "first in the 6-neighbour scan
         * order" is arbitrary -- it produced regions with ragged edges that then
         * merged unpredictably. */
        int seen = -1; float seenT = -1.0f;
        for (int d = 0; d < 6; d++) {
            int nx = x + NB6[d][0], ny = y + NB6[d][1], nz = z + NB6[d][2];
            if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) continue;
            long long j = idx_of(ac, nx, ny, nz);
            if (ac->label[j] < 0) continue;
            if (ac->thick[j] > seenT) { seenT = ac->thick[j]; seen = ac->label[j]; }
        }
        if (seen < 0) {
            /* A local maximum founds a region. Below SEED_MIN it still becomes one --
             * it has no neighbour to join -- but it is not a room centre. */
            int id = reg_push(ac, ac->thick[i]);
            if (id < 0) { free(ord); return 0; }
            ac->label[i] = id;
        } else {
            ac->label[i] = seen;
        }

        struct ac_region *R = &ac->reg[ac->label[i]];
        R->n++; R->cx += x; R->cy += y; R->cz += z;

        /* Enclosure area and area-weighted absorption, which the diffuse field needs.
         * Dense hard material is REFLECTIVE, so alpha runs opposite to the loss
         * fraction. Out of bounds is OPEN SKY, not a wall: sound crossing it never comes
         * back, so it is a near-perfect absorber. Treating it as reflective gave the
         * outdoor region a room constant and a critical distance of 10.7 blocks, implying
         * a strong diffuse field outdoors where there is no enclosure at all. */
        for (int d = 0; d < 6; d++) {
            int nx = x + NB6[d][0], ny = y + NB6[d][1], nz = z + NB6[d][2];
            int oob = nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz;
            if (!oob && ac->open[idx_of(ac, nx, ny, nz)] > OPEN_EPS) continue;
            double alpha;
            if (oob) { alpha = 0.95; R->sky++; }
            else {
                double l = ac->loss[idx_of(ac, nx, ny, nz)];
                if (l > 1.0) l = 1.0;
                /* Absorption for a FURNISHED space. A bare-shell formula gave concrete
                 * alpha 0.06 -- correct for an empty concrete box, wrong for any room a
                 * player stands in, which lands at 0.15-0.35. An unrealistically
                 * reverberant test box makes every diffuse-field number unrepresentative. */
                alpha = 0.12 + 0.5 * (1.0 - l);
                if (alpha < 0.10) alpha = 0.10;
                if (alpha > 0.85) alpha = 0.85;
            }
            R->surf++; R->absorb += alpha;
        }
    }
    free(ord);
    return 1;
}

/* --- contacts between differently-labelled regions ------------------------
 * Area-weighted centre and the widest clearance across the contact. A boundary IS an
 * aperture: found, never detected by a heuristic. */
/* Open-addressed hash from a region PAIR to its contact index. The obvious linear
 * scan over the contact list costs O(faces x contacts), and on the bunker that is
 * 800k open voxels against ~1800 contacts: 949 ms, which is a visible hitch and a
 * door swing triggers a rebuild. Hashing makes it O(faces).
 *
 * Sized to a power of two well above the contact count and rebuilt per segmentation,
 * so it never grows during the walk and never needs a resize path. */
static unsigned pair_hash(int lo, int hi) {
    unsigned h = (unsigned)lo * 2654435761u ^ (unsigned)hi * 2246822519u;
    h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
    return h;
}

static int ph_init(cmm_ac *ac, int expect) {
    int cap = 1024;
    while (cap < expect * 4) cap <<= 1;
    if (ac->ph_cap != cap) {
        int *t = (int *)realloc(ac->ph, (size_t)cap * sizeof(int));
        if (!t) return 0;
        ac->ph = t; ac->ph_cap = cap;
    }
    for (int i = 0; i < ac->ph_cap; i++) ac->ph[i] = -1;
    return 1;
}

static int prt_find_or_add(cmm_ac *ac, int a, int b) {
    int lo = a < b ? a : b, hi = a < b ? b : a;
    if (ac->ph && ac->ph_cap > 0) {
        unsigned mask = (unsigned)ac->ph_cap - 1u;
        unsigned s = pair_hash(lo, hi) & mask;
        for (unsigned probe = 0; probe <= mask; probe++) {
            int slot = (int)((s + probe) & mask);
            int pi = ac->ph[slot];
            if (pi < 0) {
                /* free slot: create the contact and record it here */
                if (ac->nprt == ac->cap_prt) {
                    int cap = ac->cap_prt ? ac->cap_prt * 2 : 256;
                    struct ac_port *p = (struct ac_port *)realloc(ac->prt, (size_t)cap * sizeof(*p));
                    if (!p) return -1;
                    ac->prt = p; ac->cap_prt = cap;
                }
                struct ac_port *P = &ac->prt[ac->nprt];
                memset(P, 0, sizeof(*P));
                P->a = lo; P->b = hi;
                ac->ph[slot] = ac->nprt;
                return ac->nprt++;
            }
            if (ac->prt[pi].a == lo && ac->prt[pi].b == hi) return pi;
        }
        /* table full: fall through to the linear path rather than fail */
    }
    for (int i = 0; i < ac->nprt; i++) if (ac->prt[i].a == lo && ac->prt[i].b == hi) return i;
    if (ac->nprt == ac->cap_prt) {
        int cap = ac->cap_prt ? ac->cap_prt * 2 : 256;
        struct ac_port *p = (struct ac_port *)realloc(ac->prt, (size_t)cap * sizeof(*p));
        if (!p) return -1;
        ac->prt = p; ac->cap_prt = cap;
    }
    struct ac_port *P = &ac->prt[ac->nprt];
    memset(P, 0, sizeof(*P));
    P->a = lo; P->b = hi;
    return ac->nprt++;
}

static int contacts(cmm_ac *ac) {
    const int sx = ac->sx, sy = ac->sy, sz = ac->sz;
    ac->nprt = 0;
    /* Contacts are bounded in practice by a few per region; 8x the region count is a
     * generous expectation and the table falls back to a scan if it ever fills. */
    if (!ph_init(ac, ac->nreg * 8 + 64)) return 0;
    for (int y = 0; y < sy; y++) for (int z = 0; z < sz; z++) for (int x = 0; x < sx; x++) {
        long long i = idx_of(ac, x, y, z);
        if (ac->open[i] <= OPEN_EPS) continue;
        int a = ac->label[i];
        /* only +x/+y/+z, so each shared face is visited once */
        for (int d = 0; d < 3; d++) {
            int nx = x + (d == 0), ny = y + (d == 1), nz = z + (d == 2);
            if (nx >= sx || ny >= sy || nz >= sz) continue;
            long long j = idx_of(ac, nx, ny, nz);
            if (ac->open[j] <= OPEN_EPS) continue;
            int b = ac->label[j];
            if (b == a) continue;
            int pi = prt_find_or_add(ac, a, b);
            if (pi < 0) return 0;
            struct ac_port *P = &ac->prt[pi];
            double aw = ac->open[i] < ac->open[j] ? ac->open[i] : ac->open[j];
            float th = ac->thick[i] < ac->thick[j] ? ac->thick[i] : ac->thick[j];
            double cx = ac->x0 + x + (d == 0 ? 1.0 : 0.5);
            double cy = ac->y0 + y + (d == 1 ? 1.0 : 0.5);
            double cz = ac->z0 + z + (d == 2 ? 1.0 : 0.5);
            P->area += aw; P->sx += cx * aw; P->sy += cy * aw; P->sz += cz * aw;
            if (th > P->thick) P->thick = th;
        }
    }
    return 1;
}

/* --- merge ----------------------------------------------------------------
 * By PERSISTENCE, widest-relative-to-core first. A pairwise ratio test alone failed on
 * real geometry because it ignores TRANSITIVITY: most boundaries correctly pass it (the
 * thickness field is saturated flat across open sky, so the watershed splits a plateau
 * arbitrarily and those splits SHOULD merge), but that many union-find edges connect
 * every region into one component and the whole POI collapses -- the "keep" decisions
 * were irrelevant because other paths joined the same regions anyway. Processing
 * widest-first while carrying the merged core as the MAX is what stops the chaining: as
 * a region absorbs neighbours it gets deeper, so the bar for absorbing the next one
 * rises, and a genuine doorway is never crossed by accident. */
struct ac_medge { int p; double key; };
static int medge_cmp(const void *pa, const void *pb) {
    const struct ac_medge *a = (const struct ac_medge *)pa, *b = (const struct ac_medge *)pb;
    if (a->key > b->key) return -1;
    if (a->key < b->key) return 1;
    return a->p < b->p ? -1 : (a->p > b->p ? 1 : 0);
}

static int uf_find(int *parent, int a) {
    while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
    return a;
}

static int merge_regions(cmm_ac *ac) {
    const int nr = ac->nreg;
    ac->nrooms = 0;
    if (nr <= 0) return 1;
    int *parent = (int *)malloc((size_t)nr * sizeof(int));
    float *core = (float *)malloc((size_t)nr * sizeof(float));
    /* Sky exposure carried through the union as an accumulating average, so a merged
     * region's outdoor-ness reflects all of its parts rather than whichever one
     * happened to be the root. */
    double *skySum = (double *)malloc((size_t)nr * sizeof(double));
    double *skyCnt = (double *)malloc((size_t)nr * sizeof(double));
    struct ac_medge *me = (struct ac_medge *)malloc((size_t)(ac->nprt ? ac->nprt : 1) * sizeof(*me));
    int *remap = (int *)malloc((size_t)nr * sizeof(int));
    if (!parent || !core || !skySum || !skyCnt || !me || !remap) {
        free(parent); free(core); free(skySum); free(skyCnt); free(me); free(remap); return 0;
    }
    for (int i = 0; i < nr; i++) {
        parent[i] = i; core[i] = ac->reg[i].core;
        skySum[i] = ac->reg[i].surf > 0 ? (double)ac->reg[i].sky / (double)ac->reg[i].surf : 0.0;
        skyCnt[i] = 1.0;
    }
    for (int i = 0; i < ac->nprt; i++) {
        double ca = core[ac->prt[i].a], cb = core[ac->prt[i].b];
        double mn = ca < cb ? ca : cb;
        if (mn < 0.01) mn = 0.01;
        me[i].p = i; me[i].key = ac->prt[i].thick / mn;
    }
    qsort(me, (size_t)ac->nprt, sizeof(*me), medge_cmp);

    for (int q = 0; q < ac->nprt; q++) {
        const struct ac_port *P = &ac->prt[me[q].p];
        int ra = uf_find(parent, P->a), rb = uf_find(parent, P->b);
        if (ra == rb) continue;
        double mn = core[ra] < core[rb] ? core[ra] : core[rb];
        if (P->thick < MERGE_H * mn) continue;          /* a real pinch: keep them apart */
        /* AN ENCLOSURE NEVER MERGES WITH THE SKY. No pinch test can separate indoors from
         * outdoors, because a bunker entrance or a cave mouth is a perfectly wide opening
         * -- there IS no pinch to find. Without this the watershed merged 97.6% of one
         * capture's open space into a single region spanning sky, terrain AND tortuous
         * underground voids, which then had to share one reverberant field and one set of
         * sight lines: 40 dB steps between adjacent voxels with identical aperture sets.
         * Sky exposure separates the two with no overlap at all -- enclosed regions read
         * 0.0%, outdoor ones 43-57% -- so that boundary is an APERTURE between two spaces,
         * never a seam within one, however wide it happens to be. */
        int outA = (skySum[ra] / skyCnt[ra]) > SKY_SPLIT;
        int outB = (skySum[rb] / skyCnt[rb]) > SKY_SPLIT;
        if (outA != outB) continue;
        parent[rb] = ra;
        if (core[rb] > core[ra]) core[ra] = core[rb];   /* deeper now, harder to absorb the next */
        skySum[ra] += skySum[rb]; skyCnt[ra] += skyCnt[rb];
    }

    for (int i = 0; i < nr; i++) remap[i] = -1;
    int nrooms = 0;
    for (int i = 0; i < nr; i++) { int r = uf_find(parent, i); if (remap[r] < 0) remap[r] = nrooms++; }
    for (long long i = 0; i < ac->n; i++) {
        int l = ac->label[i];
        ac->root[i] = l < 0 ? -1 : remap[uf_find(parent, l)];
    }
    ac->nrooms = nrooms;

    free(parent); free(core); free(skySum); free(skyCnt); free(me); free(remap);
    return 1;
}

/* --- corridor split and node assembly -------------------------------------
 * The final node id is (room, corridor segment). Nodes are the routing units and a
 * straight line inside one is legitimate; rooms are the units that hold a reverberant
 * field. Keeping them distinct matters: a corridor cut is NOT a constriction and must
 * not radiate, and treating every cut as an aperture put phantom radiators at each one,
 * stepping the level by 8.5 dB as a listener crossed. */
static int eh_init(cmm_ac *ac, int expect) {
    int cap = 1024;
    while (cap < expect * 4) cap <<= 1;
    if (ac->eh_cap != cap) {
        int *t = (int *)realloc(ac->eh, (size_t)cap * sizeof(int));
        if (!t) return 0;
        ac->eh = t; ac->eh_cap = cap;
    }
    for (int i = 0; i < ac->eh_cap; i++) ac->eh[i] = -1;
    return 1;
}

static int edge_find_or_add(cmm_ac *ac, int a, int b) {
    int lo = a < b ? a : b, hi = a < b ? b : a;
    unsigned mask = (unsigned)ac->eh_cap - 1u;
    unsigned s = pair_hash(lo, hi) & mask;
    for (unsigned probe = 0; probe <= mask; probe++) {
        int slot = (int)((s + probe) & mask);
        int ei = ac->eh[slot];
        if (ei < 0) {
            if (ac->nedge == ac->cap_edge) {
                int cap = ac->cap_edge ? ac->cap_edge * 2 : 256;
                struct ac_edge *e = (struct ac_edge *)realloc(ac->edge, (size_t)cap * sizeof(*e));
                if (!e) return -1;
                ac->edge = e; ac->cap_edge = cap;
            }
            struct ac_edge *E = &ac->edge[ac->nedge];
            memset(E, 0, sizeof(*E));
            E->a = lo; E->b = hi;
            E->ra = E->rb = -1;
            ac->eh[slot] = ac->nedge;
            return ac->nedge++;
        }
        if (ac->edge[ei].a == lo && ac->edge[ei].b == hi) return ei;
    }
    return -1;
}

static int split_and_nodes(cmm_ac *ac) {
    const int sx = ac->sx, sy = ac->sy, sz = ac->sz;
    const int nr = ac->nrooms;
    if (nr <= 0) { ac->nnodes = 0; return 1; }

    /* per-room extent and mean thickness, to decide whether it is a corridor */
    int *x0 = (int *)malloc((size_t)nr * sizeof(int)), *x1 = (int *)malloc((size_t)nr * sizeof(int));
    int *y0 = (int *)malloc((size_t)nr * sizeof(int)), *y1 = (int *)malloc((size_t)nr * sizeof(int));
    int *z0 = (int *)malloc((size_t)nr * sizeof(int)), *z1 = (int *)malloc((size_t)nr * sizeof(int));
    double *tsum = (double *)calloc((size_t)nr, sizeof(double));
    long long *cnt = (long long *)calloc((size_t)nr, sizeof(long long));
    if (!x0 || !x1 || !y0 || !y1 || !z0 || !z1 || !tsum || !cnt) {
        free(x0); free(x1); free(y0); free(y1); free(z0); free(z1); free(tsum); free(cnt); return 0;
    }
    for (int r = 0; r < nr; r++) { x0[r] = y0[r] = z0[r] = 1 << 29; x1[r] = y1[r] = z1[r] = -(1 << 29); }
    for (int y = 0; y < sy; y++) for (int z = 0; z < sz; z++) for (int x = 0; x < sx; x++) {
        long long i = idx_of(ac, x, y, z);
        int r = ac->root[i];
        if (r < 0) continue;
        cnt[r]++; tsum[r] += ac->thick[i];
        if (x < x0[r]) x0[r] = x; if (x > x1[r]) x1[r] = x;
        if (y < y0[r]) y0[r] = y; if (y > y1[r]) y1[r] = y;
        if (z < z0[r]) z0[r] = z; if (z > z1[r]) z1[r] = z;
    }

    int *axis = (int *)malloc((size_t)nr * sizeof(int));
    double *step = (double *)malloc((size_t)nr * sizeof(double));
    int *lo = (int *)malloc((size_t)nr * sizeof(int));
    if (!axis || !step || !lo) { free(axis); free(step); free(lo); return 0; }
    for (int r = 0; r < nr; r++) {
        axis[r] = -1;
        if (cnt[r] <= 0) continue;
        int ex[3] = { x1[r] - x0[r] + 1, y1[r] - y0[r] + 1, z1[r] - z0[r] + 1 };
        int a = 0;
        if (ex[1] > ex[a]) a = 1;
        if (ex[2] > ex[a]) a = 2;
        double meanT = tsum[r] / (double)cnt[r];
        if (meanT < 1.0) meanT = 1.0;
        double st = meanT * SEG_STEP_MULT;
        if (st < SEG_MIN_STEP) st = SEG_MIN_STEP;
        if ((double)ex[a] / st < 2.0) continue;    /* compact enough: leave it whole */
        axis[r] = a; step[r] = st;
        lo[r] = a == 0 ? x0[r] : (a == 1 ? y0[r] : z0[r]);
    }

    /* segment index per voxel, then a compact node id per (room, segment) pair */
    for (int y = 0; y < sy; y++) for (int z = 0; z < sz; z++) for (int x = 0; x < sx; x++) {
        long long i = idx_of(ac, x, y, z);
        int r = ac->root[i];
        if (r < 0) { ac->node[i] = -1; continue; }
        int s = 0;
        if (axis[r] >= 0) {
            int cc = axis[r] == 0 ? x : (axis[r] == 1 ? y : z);
            s = (int)((double)(cc - lo[r]) / step[r]);
            if (s < 0) s = 0;
        }
        ac->node[i] = r * 4096 + s;              /* provisional, compacted below */
    }
    /* Compact each distinct provisional id to 0..nnodes-1 in FIRST-SEEN order, so the
     * numbering is deterministic across runs and platforms -- an id space that depends
     * on iteration order or hash layout would make every downstream parity check
     * meaningless even when the structure was identical. */
    ac->nnodes = 0;
    {
        /* hash provisional -> compact, so the 4096 stride never needs allocating */
        int hcap = 1024; while (hcap < nr * 8) hcap <<= 1;
        int *hk = (int *)malloc((size_t)hcap * sizeof(int));
        int *hv = (int *)malloc((size_t)hcap * sizeof(int));
        if (!hk || !hv) { free(hk); free(hv); free(axis); free(step); free(lo);
                          free(x0); free(x1); free(y0); free(y1); free(z0); free(z1);
                          free(tsum); free(cnt); return 0; }
        for (int i = 0; i < hcap; i++) hk[i] = -1;
        unsigned mask = (unsigned)hcap - 1u;
        for (long long i = 0; i < ac->n; i++) {
            int prov = ac->node[i];
            if (prov < 0) continue;
            unsigned s = pair_hash(prov, 0x9e37) & mask;
            for (unsigned probe = 0; probe <= mask; probe++) {
                int slot = (int)((s + probe) & mask);
                if (hk[slot] < 0) { hk[slot] = prov; hv[slot] = ac->nnodes++; ac->node[i] = hv[slot]; break; }
                if (hk[slot] == prov) { ac->node[i] = hv[slot]; break; }
            }
        }
        free(hk); free(hv);
    }

    free(axis); free(step); free(lo);
    free(x0); free(x1); free(y0); free(y1); free(z0); free(z1); free(tsum); free(cnt);
    return 1;
}

/* --- node-level edges, and rooms with absorption -------------------------- */
static int build_edges_and_rooms(cmm_ac *ac) {
    const int sx = ac->sx, sy = ac->sy, sz = ac->sz;
    ac->nedge = 0;
    if (!eh_init(ac, ac->nnodes * 8 + 64)) return 0;

    for (int y = 0; y < sy; y++) for (int z = 0; z < sz; z++) for (int x = 0; x < sx; x++) {
        long long i = idx_of(ac, x, y, z);
        if (ac->open[i] <= OPEN_EPS) continue;
        int a = ac->node[i];
        for (int d = 0; d < 3; d++) {
            int nx = x + (d == 0), ny = y + (d == 1), nz = z + (d == 2);
            if (nx >= sx || ny >= sy || nz >= sz) continue;
            long long j = idx_of(ac, nx, ny, nz);
            if (ac->open[j] <= OPEN_EPS) continue;
            int b = ac->node[j];
            if (b == a) continue;
            int ei = edge_find_or_add(ac, a, b);
            if (ei < 0) return 0;
            struct ac_edge *E = &ac->edge[ei];
            double aw = ac->open[i] < ac->open[j] ? ac->open[i] : ac->open[j];
            float th = ac->thick[i] < ac->thick[j] ? ac->thick[i] : ac->thick[j];
            double cx = ac->x0 + x + (d == 0 ? 1.0 : 0.5);
            double cy = ac->y0 + y + (d == 1 ? 1.0 : 0.5);
            double cz = ac->z0 + z + (d == 2 ? 1.0 : 0.5);
            E->area += aw; E->sx += cx * aw; E->sy += cy * aw; E->sz += cz * aw;
            if (th > E->thick) E->thick = th;
            E->ra = ac->root[i]; E->rb = ac->root[j];
        }
    }

    /* Constriction per edge, and the rule that a corridor CUT is not one. A cut exists
     * purely so the pathfinder does not treat a bend as line of sight -- it does not pinch
     * and it does not radiate, and charging it as an aperture put phantom radiators at every
     * cut, stepping the level 8.5 dB as a listener crossed one. */
    for (int e = 0; e < ac->nedge; e++) {
        struct ac_edge *E = &ac->edge[e];
        double f = E->area / FRESNEL_AREA;
        E->open_frac = f > 1.0 ? 1.0 : f;
        if (E->ra >= 0 && E->ra == E->rb) E->open_frac = 1.0;   /* a cut passes everything */
        /* THE OPENING'S EXTENT IS A UNIT BOX AT ITS CENTRE, not the true span of its faces.
         * That looks like an approximation and is deliberate: the true extent of a long
         * boundary -- a whole storefront, say -- would let a listener anywhere along it read
         * distance zero, which is not what "beside the opening" should mean for a coupling
         * term. Accumulating the real extent instead made the diffuse field 0.63 dB louder
         * than the model here, because the clamp brought every listener closer. Matching the
         * model exactly matters more than my opinion of which box is better, and the choice
         * is recorded so it is not "corrected" later by accident. */
        double cx = E->area > 0 ? E->sx / E->area : 0;
        double cy = E->area > 0 ? E->sy / E->area : 0;
        double cz = E->area > 0 ? E->sz / E->area : 0;
        E->x1 = (float)floor(cx); E->x0 = E->x1 - 1.0f;
        E->y1 = (float)floor(cy); E->y0 = E->y1 - 1.0f;
        E->z1 = (float)floor(cz); E->z0 = E->z1 - 1.0f;
    }

    /* rooms: area, absorption, sky exposure, accumulated over voxels rather than
     * aggregated from raw regions, so it matches the JS which recomputes them here */
    if (ac->cap_room < ac->nrooms) {
        struct ac_room *r = (struct ac_room *)realloc(ac->room, (size_t)(ac->nrooms + 1) * sizeof(*r));
        if (!r) return 0;
        ac->room = r; ac->cap_room = ac->nrooms + 1;
    }
    memset(ac->room, 0, (size_t)(ac->nrooms > 0 ? ac->nrooms : 1) * sizeof(struct ac_room));
    for (int y = 0; y < sy; y++) for (int z = 0; z < sz; z++) for (int x = 0; x < sx; x++) {
        long long i = idx_of(ac, x, y, z);
        int r = ac->root[i];
        if (r < 0) continue;
        struct ac_room *R = &ac->room[r];
        R->n++;
        for (int d = 0; d < 6; d++) {
            int nx = x + NB6[d][0], ny = y + NB6[d][1], nz = z + NB6[d][2];
            int oob = nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz;
            if (!oob && ac->open[idx_of(ac, nx, ny, nz)] > OPEN_EPS) continue;
            double alpha;
            if (oob) { alpha = 0.95; R->sky++; }
            else {
                double l = ac->loss[idx_of(ac, nx, ny, nz)];
                if (l > 1.0) l = 1.0;
                alpha = 0.12 + 0.5 * (1.0 - l);
                if (alpha < 0.10) alpha = 0.10;
                if (alpha > 0.85) alpha = 0.85;
            }
            R->surf++; R->absorb += alpha;
        }
    }

    /* AN OPENING IS AN ABSORBER. Sound leaving through a doorway does not come back,
     * which makes an opening the most absorbing surface a room has. Omitting it let
     * small rooms reach an absorption area of 1 against a 9-block opening, and the
     * room-to-room coupling gain S/A came out at 9.4 -- a room amplifying what enters
     * it ninefold, which is unphysical and divergent if iterated. */
    for (int e = 0; e < ac->nedge; e++) {
        const struct ac_edge *E = &ac->edge[e];
        if (E->ra >= 0 && E->rb >= 0 && E->ra != E->rb) {
            /* AREA TIMES OPENNESS, not raw area. A placeholder here added the whole contact
             * area, which overstates every room's absorption and so understates its field --
             * and it did so unevenly, since openness saturates at 1 for wide openings and
             * scales with area for narrow ones. A 3-voxel room came out at 7.25 against the
             * model's 1.326, which is exactly sum(area^2)/FRESNEL_AREA over its two openings. */
            double a = E->area * E->open_frac;
            ac->room[E->ra].open_area += a;
            ac->room[E->rb].open_area += a;
        }
    }
    return 1;
}

static int wall_find_or_add(cmm_ac *ac, int a, int b) {
    int lo = a < b ? a : b, hi = a < b ? b : a;
    unsigned mask = (unsigned)ac->wh_cap - 1u;
    unsigned s = pair_hash(lo, hi) & mask;
    for (unsigned probe = 0; probe <= mask; probe++) {
        int slot = (int)((s + probe) & mask);
        int wi = ac->wh[slot];
        if (wi < 0) {
            if (ac->nwall == ac->cap_wall) {
                int cap = ac->cap_wall ? ac->cap_wall * 2 : 256;
                struct ac_wall *w = (struct ac_wall *)realloc(ac->wall, (size_t)cap * sizeof(*w));
                if (!w) return -1;
                ac->wall = w; ac->cap_wall = cap;
            }
            struct ac_wall *W = &ac->wall[ac->nwall];
            memset(W, 0, sizeof(*W));
            W->a = lo; W->b = hi;
            ac->wh[slot] = ac->nwall;
            return ac->nwall++;
        }
        if (ac->wall[wi].a == lo && ac->wall[wi].b == hi) return wi;
    }
    return -1;
}

static int build_walls(cmm_ac *ac) {
    const int sx = ac->sx, sy = ac->sy, sz = ac->sz;
    ac->nwall = 0;
    int cap = 1024;
    while (cap < (ac->nrooms > 0 ? ac->nrooms : 1) * 16) cap <<= 1;
    if (ac->wh_cap != cap) {
        int *t = (int *)realloc(ac->wh, (size_t)cap * sizeof(int));
        if (!t) return 0;
        ac->wh = t; ac->wh_cap = cap;
    }
    for (int i = 0; i < ac->wh_cap; i++) ac->wh[i] = -1;

    for (int y = 0; y < sy; y++) for (int z = 0; z < sz; z++) for (int x = 0; x < sx; x++) {
        long long i = idx_of(ac, x, y, z);
        if (ac->open[i] <= OPEN_EPS) continue;
        int a = ac->root[i];
        if (a < 0) continue;
        for (int d = 0; d < 6; d++) {
            int nx = x + NB6[d][0], ny = y + NB6[d][1], nz = z + NB6[d][2];
            /* must START in material, or this is an opening and the aperture path has it */
            if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) continue;
            if (ac->open[idx_of(ac, nx, ny, nz)] > OPEN_EPS) continue;
            int steps = 0;
            while (steps < WALL_MAX_THICK) {
                nx += NB6[d][0]; ny += NB6[d][1]; nz += NB6[d][2]; steps++;
                if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) { steps = -1; break; }
                if (ac->open[idx_of(ac, nx, ny, nz)] > OPEN_EPS) break;
            }
            if (steps < 0 || steps >= WALL_MAX_THICK) continue;
            long long j = idx_of(ac, nx, ny, nz);
            if (ac->open[j] <= OPEN_EPS) continue;
            int b = ac->root[j];
            if (b < 0 || b == a) continue;
            double lossDb = seg_db_at(ac, ac->x0 + x + 0.5, ac->y0 + y + 0.5, ac->z0 + z + 0.5,
                                          ac->x0 + nx + 0.5, ac->y0 + ny + 0.5, ac->z0 + nz + 0.5, 1, 1);
            int wi = wall_find_or_add(ac, a, b);
            if (wi < 0) return 0;
            ac->wall[wi].area += 1.0;
            ac->wall[wi].tau  += pow(10.0, -lossDb / 10.0);
        }
    }
    /* area-weighted mean power transmission per link */
    for (int i = 0; i < ac->nwall; i++)
        if (ac->wall[i].area > 0) ac->wall[i].tau /= ac->wall[i].area;
    return 1;
}

int cmm_ac_build_segments(cmm_ac *ac) {
    if (!ac) return 0;
    if (!segment(ac)) return 0;
    if (!contacts(ac)) return 0;
    if (!merge_regions(ac)) return 0;
    if (!split_and_nodes(ac)) return 0;
    if (!build_edges_and_rooms(ac)) return 0;
    if (!build_walls(ac)) return 0;
    return 1;
}

int cmm_ac_build_fields(cmm_ac *ac) {
    if (!ac) return 0;
    derive_open(ac);
    derive_thick(ac);
    return 1;
}

/* ── read-back ───────────────────────────────────────────────────────────── */
static int in_bounds(const cmm_ac *ac, int wx, int wy, int wz) {
    int x = wx - ac->x0, y = wy - ac->y0, z = wz - ac->z0;
    return x >= 0 && x < ac->sx && y >= 0 && y < ac->sy && z >= 0 && z < ac->sz;
}

float cmm_ac_open_at(const cmm_ac *ac, int wx, int wy, int wz) {
    if (!ac || !in_bounds(ac, wx, wy, wz)) return 0.0f;
    return ac->open[idx_of(ac, wx - ac->x0, wy - ac->y0, wz - ac->z0)];
}

float cmm_ac_thick_at(const cmm_ac *ac, int wx, int wy, int wz) {
    if (!ac || !in_bounds(ac, wx, wy, wz)) return 0.0f;
    return ac->thick[idx_of(ac, wx - ac->x0, wy - ac->y0, wz - ac->z0)];
}

/* --- evaluation helpers --------------------------------------------------- */
static double sstep(double a, double b, double x) {
    if (b <= a) return x >= b ? 1.0 : 0.0;
    double t = (x - a) / (b - a);
    if (t < 0) t = 0; if (t > 1) t = 1;
    return t * t * (3.0 - 2.0 * t);
}

/* Openness at a FLOAT position, trilinear over voxel centres. A nearest-voxel lookup
 * would make the disc's solidity a step function of listener position, which is the exact
 * class of defect this model exists to remove. Out of bounds reads as open, so an unknown
 * region never invents a wall. */
static double open_at_f(const cmm_ac *ac, double x, double y, double z) {
    double gx = x - ac->x0 - 0.5, gy = y - ac->y0 - 0.5, gz = z - ac->z0 - 0.5;
    int bx = (int)floor(gx), by = (int)floor(gy), bz = (int)floor(gz);
    double tx = gx - bx, ty = gy - by, tz = gz - bz;
    double acc = 0.0;
    for (int i = 0; i < 8; i++) {
        int ex = i & 1, ey = (i >> 1) & 1, ez = (i >> 2) & 1;
        double w = (ex ? tx : 1 - tx) * (ey ? ty : 1 - ty) * (ez ? tz : 1 - tz);
        if (w <= 0) continue;
        int px = bx + ex, py = by + ey, pz = bz + ez;
        double v = 1.0;
        if (px >= 0 && px < ac->sx && py >= 0 && py < ac->sy && pz >= 0 && pz < ac->sz)
            v = ac->open[idx_of(ac, px, py, pz)];
        acc += w * v;
    }
    return acc;
}

/* Material along a leg measured as a BUNDLE, not a ray. A single ray steps the instant it
 * clips a corner, and a listener and an aperture on their own room's boundary are in the
 * same connected region, so a straight line clipping geometry is usually non-convexity
 * rather than obstruction. Energy-summing five rays asks how much of the wavefront gets
 * through instead of whether one line hits something. */
static double leg_db(const cmm_ac *ac, double ax, double ay, double az,
                     double lx, double ly, double lz) {
    double ux = lx - ax, uy = ly - ay, uz = lz - az;
    double ul = sqrt(ux * ux + uy * uy + uz * uz);
    if (ul < 1e-6) return 0.0;
    ux /= ul; uy /= ul; uz /= ul;
    double px = -uz, py = 0, pz = ux;
    double pl = sqrt(px * px + py * py + pz * pz);
    if (pl < 1e-3) { px = 1; py = 0; pz = 0; pl = 1; }
    px /= pl; py /= pl; pz /= pl;
    double qx = uy * pz - uz * py, qy = uz * px - ux * pz, qz = ux * py - uy * px;
    static const double FAN[5][2] = {{0,0},{1,0},{-1,0},{0,1},{0,-1}};
    double pw = 0.0;
    for (int k = 0; k < 5; k++) {
        double ox = (px * FAN[k][0] + qx * FAN[k][1]) * LEG_FAN_R;
        double oy = (py * FAN[k][0] + qy * FAN[k][1]) * LEG_FAN_R;
        double oz = (pz * FAN[k][0] + qz * FAN[k][1]) * LEG_FAN_R;
        pw += pow(10.0, -seg_db_at(ac, ax + ox, ay + oy, az + oz, lx, ly, lz, 1, 1) / 10.0) / 5.0;
    }
    return pw > 0 ? -10.0 * log10(pw) : 120.0;
}

/* Where the transmitting surface is: the midpoint of the material run NEAREST THE
 * LISTENER, and that run's thickness. Spanning first-to-last material instead put the
 * "barrier" in OPEN AIR partway along, because a speaker IS a solid block, and the disc
 * then sampled a sphere of air whose dominant patch had a free path through a solid floor.
 * Returns 0 when the line is clear. */
static int barrier_mid(const cmm_ac *ac, double fx, double fy, double fz,
                       double tx, double ty, double tz, double *bx, double *by, double *bz) {
    double dx = tx - fx, dy = ty - fy, dz = tz - fz;
    double dist = sqrt(dx * dx + dy * dy + dz * dz);
    if (dist < 0.01) return 0;
    dx /= dist; dy /= dist; dz /= dist;
    int x = (int)floor(fx) - ac->x0, y = (int)floor(fy) - ac->y0, z = (int)floor(fz) - ac->z0;
    int sX = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
    int sY = dy > 0 ? 1 : (dy < 0 ? -1 : 0);
    int sZ = dz > 0 ? 1 : (dz < 0 ? -1 : 0);
    double lfx = fx - ac->x0, lfy = fy - ac->y0, lfz = fz - ac->z0;
    double tmx = fabs(dx) < 1e-6 ? 1e30 : (((sX > 0 ? x + 1 : x) - lfx) / dx);
    double tmy = fabs(dy) < 1e-6 ? 1e30 : (((sY > 0 ? y + 1 : y) - lfy) / dy);
    double tmz = fabs(dz) < 1e-6 ? 1e30 : (((sZ > 0 ? z + 1 : z) - lfz) / dz);
    double tDx = fabs(dx) < 1e-6 ? 1e30 : fabs(1.0 / dx);
    double tDy = fabs(dy) < 1e-6 ? 1e30 : fabs(1.0 / dy);
    double tDz = fabs(dz) < 1e-6 ? 1e30 : fabs(1.0 / dz);
    /* A SPEAKER'S OWN BLOCK IS NOT A BARRIER BETWEEN IT AND YOU.
     * A speaker sits inside a solid block, so the first voxel of this walk is always material and
     * always opens a run. Standing one block away with clear air between, that run is the ONLY one
     * and becomes "the barrier" -- the disc then runs over the speaker's own casing and whatever it
     * is mounted on, and reports occlusion where there is none. Measured -1.19 dB at one block,
     * -0.62 at 1.5, crossing zero near two; and since anything positive clamps to no attenuation,
     * walking IN from three blocks to one ADDED 1.2 dB. Reported as "when standing very close it
     * still gets a hint quieter than when standing a few blocks away".
     *
     * seg_db_at already refuses to charge the source's own voxel on a source-to-listener ray (its
     * skip_from argument, for exactly this reason). Barrier-FINDING must agree with it, or the two
     * disagree about whether a barrier exists at all. */
    const int sVx = x, sVy = y, sVz = z;
    double runA = -1, runB = -1, lastA = -1, lastB = -1, tPrev = 0;
    for (int g = 0; g < 1024; g++) {
        double tNext = tmx < tmy ? tmx : tmy;
        if (tmz < tNext) tNext = tmz;
        if (dist < tNext) tNext = dist;
        if (tNext - tPrev > 1e-6 && x >= 0 && x < ac->sx && y >= 0 && y < ac->sy && z >= 0 && z < ac->sz) {
            int isSrcVox = (x == sVx && y == sVy && z == sVz);
            if (!isSrcVox && ac->loss[idx_of(ac, x, y, z)] > 0.05) {
                if (runA < 0) runA = tPrev;
                runB = tNext;
            } else if (runA >= 0) { lastA = runA; lastB = runB; runA = -1; }
        }
        if (tNext >= dist - 1e-9) break;
        tPrev = tNext;
        if (tmx <= tmy && tmx <= tmz) { x += sX; tmx += tDx; }
        else if (tmy <= tmz)          { y += sY; tmy += tDy; }
        else                          { z += sZ; tmz += tDz; }
    }
    if (runA >= 0) { lastA = runA; lastB = runB; }
    if (lastA < 0) return 0;
    double t = 0.5 * (lastA + lastB);
    *bx = fx + dx * t; *by = fy + dy * t; *bz = fz + dz * t;
    return 1;
}

/* Distance to an opening's EXTENT, not to its centre. On a storefront-sized boundary the
 * centroid can be tens of blocks from where a listener actually crosses. */
static double ap_dist(const struct ac_edge *E, double x, double y, double z) {
    double cx = x < E->x0 ? E->x0 : (x > E->x1 ? E->x1 : x);
    double cy = y < E->y0 ? E->y0 : (y > E->y1 ? E->y1 : y);
    double cz = z < E->z0 ? E->z0 : (z > E->z1 ? E->z1 : z);
    return sqrt((cx - x) * (cx - x) + (cy - y) * (cy - y) + (cz - z) * (cz - z));
}

/* --- a SOURCE bound to a scene ---------------------------------------------
 * Everything here is graph-sized, so move() is cheap and allocates nothing: the arena
 * is sized once at create from the scene's edge and room counts. That is the property
 * that makes moving speakers -- and later enemies -- a lookup rather than a rebuild. */
struct ac_cost { double add, bneck, len; int from; int set;
                 double inx, iny, inz, inlen; };   /* direction of arrival, for the bend */

struct cmm_ac_src {
    cmm_ac *sc;
    float x, y, z;
    void *arena; size_t arena_bytes;
    struct ac_cost *cost;    /* per node-edge */
    double *seed;            /* per node: cost to reach it from the source, or -1 */
    double *field, *absorb, *canrev, *pin, *direct;   /* per room */
    double *apcur, *apnxt;   /* pass-3 aperture-coupling Jacobi scratch, per room */
    int    *node_room;       /* per node -> its room */
    int    *heap;            /* edge ids */
    double *hkey;
    int nheap, heap_cap;
    int nedge_cap, nnode_cap, nroom_cap;
};

/* LAZY-DELETION DIJKSTRA PUSHES ON EVERY IMPROVEMENT, not once per edge, so the heap
 * must be sized well above the edge count. Sizing it to nedge segfaulted on the store
 * capture while the bunker happened to stay under -- the kind of difference that makes
 * a crash look geometry-specific when it is really a capacity bug. The guard drops a
 * push rather than overflowing if the bound is ever wrong: the cost table is already
 * updated, so the solve degrades rather than corrupting memory. */
static void heap_push(cmm_ac_src *s, int eid, double key) {
    if (s->nheap >= s->heap_cap) return;
    int i = s->nheap++;
    s->heap[i] = eid; s->hkey[i] = key;
    while (i > 0) {
        int p = (i - 1) >> 1;
        if (s->hkey[p] <= s->hkey[i]) break;
        int te = s->heap[p]; double tk = s->hkey[p];
        s->heap[p] = s->heap[i]; s->hkey[p] = s->hkey[i];
        s->heap[i] = te; s->hkey[i] = tk;
        i = p;
    }
}

static int heap_pop(cmm_ac_src *s, double *key) {
    int top = s->heap[0]; *key = s->hkey[0];
    s->nheap--;
    if (s->nheap > 0) {
        s->heap[0] = s->heap[s->nheap]; s->hkey[0] = s->hkey[s->nheap];
        int i = 0;
        for (;;) {
            int l = 2 * i + 1, r = l + 1, m = i;
            if (l < s->nheap && s->hkey[l] < s->hkey[m]) m = l;
            if (r < s->nheap && s->hkey[r] < s->hkey[m]) m = r;
            if (m == i) break;
            int te = s->heap[m]; double tk = s->hkey[m];
            s->heap[m] = s->heap[i]; s->hkey[m] = s->hkey[i];
            s->heap[i] = te; s->hkey[i] = tk;
            i = m;
        }
    }
    return top;
}

static void src_solve(cmm_ac_src *s) {
    cmm_ac *ac = s->sc;
    for (int e = 0; e < ac->nedge; e++) { s->cost[e].set = 0; }
    for (int nd = 0; nd < ac->nnodes; nd++) s->seed[nd] = -1.0;
    s->nheap = 0;
    if (ac->nnodes <= 0 || ac->nedge <= 0) return;

    /* Seed every distinct air pocket within SEED_R, each paying for the material
     * between the speaker and it. Radius 1 finds the void INSIDE the furniture rather
     * than the room: a speaker on a shelf seeded its own cavity and every shelf speaker
     * would have been inaudible. */
    int sxi = (int)floorf(s->x), syi = (int)floorf(s->y), szi = (int)floorf(s->z);
    for (int dz = -SEED_R; dz <= SEED_R; dz++)
    for (int dy = -SEED_R; dy <= SEED_R; dy++)
    for (int dx = -SEED_R; dx <= SEED_R; dx++) {
        int wx = sxi + dx, wy = syi + dy, wz = szi + dz;
        int lx = wx - ac->x0, ly = wy - ac->y0, lz = wz - ac->z0;
        if (lx < 0 || lx >= ac->sx || ly < 0 || ly >= ac->sy || lz < 0 || lz >= ac->sz) continue;
        int nd = ac->node[idx_of(ac, lx, ly, lz)];
        if (nd < 0) continue;
        double d = sqrt((double)(dx * dx + dy * dy + dz * dz));
        double cst = SLOPE * d + seg_db_at(ac, sxi + 0.5, syi + 0.5, szi + 0.5,
                                           wx + 0.5, wy + 0.5, wz + 0.5, 1, 1);
        if (s->seed[nd] < 0 || cst < s->seed[nd]) s->seed[nd] = cst;
    }

    /* from the source into every edge bounding a seeded pocket */
    double spx = sxi + 0.5, spy = syi + 0.5, spz = szi + 0.5;
    for (int nd = 0; nd < ac->nnodes; nd++) {
        if (s->seed[nd] < 0) continue;
        for (int e = 0; e < ac->nedge; e++) {
            const struct ac_edge *E = &ac->edge[e];
            if (E->a != nd && E->b != nd) continue;
            double px = E->sx / (E->area > 0 ? E->area : 1);
            double py = E->sy / (E->area > 0 ? E->area : 1);
            double pz = E->sz / (E->area > 0 ? E->area : 1);
            double leg = sqrt((px - spx) * (px - spx) + (py - spy) * (py - spy) + (pz - spz) * (pz - spz));
            double bneck = E->open_frac;              /* node throat is 1, so the edge decides */
            double add = s->seed[nd] + SLOPE * leg;
            double key = add - constrict_db(bneck);
            if (!s->cost[e].set || (s->cost[e].add - constrict_db(s->cost[e].bneck)) > key) {
                s->cost[e].add = add; s->cost[e].bneck = bneck; s->cost[e].len = leg;
                s->cost[e].from = nd; s->cost[e].set = 1;
                s->cost[e].inx = px - spx; s->cost[e].iny = py - spy; s->cost[e].inz = pz - spz;
                s->cost[e].inlen = leg;
                heap_push(s, e, key);
            }
        }
    }

    while (s->nheap > 0) {
        double key;
        int eid = heap_pop(s, &key);
        const struct ac_cost *live = &s->cost[eid];
        if (!live->set) continue;
        if (fabs((live->add - constrict_db(live->bneck)) - key) > 1e-9) continue;  /* superseded */
        const struct ac_edge *E = &ac->edge[eid];
        int far = (E->a == live->from) ? E->b : E->a;
        double px = E->sx / (E->area > 0 ? E->area : 1);
        double py = E->sy / (E->area > 0 ? E->area : 1);
        double pz = E->sz / (E->area > 0 ? E->area : 1);
        for (int q = 0; q < ac->nedge; q++) {
            if (q == eid) continue;
            const struct ac_edge *Q = &ac->edge[q];
            if (Q->a != far && Q->b != far) continue;
            double qx = Q->sx / (Q->area > 0 ? Q->area : 1);
            double qy = Q->sy / (Q->area > 0 ? Q->area : 1);
            double qz = Q->sz / (Q->area > 0 ? Q->area : 1);
            double lx = qx - px, ly = qy - py, lz = qz - pz;
            double leg = sqrt(lx * lx + ly * ly + lz * lz);
            if (leg < 1e-6) continue;
            /* Bend is charged AT this portal, gated by how pinched it is: turning in open
             * space is free, because a source radiates every way at once. Energy is lost
             * diffracting round an EDGE, and only where there is an edge to diffract round. */
            double bend = 0.0;
            if (live->inlen > 1e-6) {
                double cosang = (live->inx * lx + live->iny * ly + live->inz * lz) / (live->inlen * leg);
                if (cosang > 1.0) cosang = 1.0;
                if (cosang < -1.0) cosang = -1.0;
                bend = BEND_DB * (1.0 - cosang) / 2.0 * (1.0 - E->open_frac);
            }
            double nadd = live->add + SLOPE * leg + bend;
            /* the bottleneck is the narrowest opening on the route, paid ONCE */
            double nb = live->bneck < Q->open_frac ? live->bneck : Q->open_frac;
            double nkey = nadd - constrict_db(nb);
            if (!s->cost[q].set || (s->cost[q].add - constrict_db(s->cost[q].bneck)) > nkey) {
                s->cost[q].add = nadd; s->cost[q].bneck = nb; s->cost[q].len = live->len + leg;
                s->cost[q].from = far; s->cost[q].set = 1;
                s->cost[q].inx = lx; s->cost[q].iny = ly; s->cost[q].inz = lz; s->cost[q].inlen = leg;
                heap_push(s, q, nkey);
            }
        }
    }
}

/* --- the room-field solve -------------------------------------------------
 *     P_diffuse = 4 * P_entering / A,   A = absorption incl. the openings
 * Power entering a room is what its inlets deliver; the field it sustains is that power
 * spread over its absorption. Then neighbours drive it through shared WALLS. */
static void src_fields(cmm_ac_src *s) {
    cmm_ac *ac = s->sc;
    const int nr = ac->nrooms;
    for (int r = 0; r < nr; r++) s->field[r] = 0.0;
    if (nr <= 0) return;

    /* absorption including the openings, and how much field each room can sustain */
    for (int r = 0; r < nr; r++) {
        const struct ac_room *R = &ac->room[r];
        double al = R->surf > 0 ? R->absorb / (double)R->surf : 0.5;
        if (al < 0.02) al = 0.02;
        if (al > 0.95) al = 0.95;
        double roomR = (double)R->surf * al / (1.0 - al);
        if (roomR < 1.0) roomR = 1.0;
        s->absorb[r] = roomR + R->open_area;
        double mfp = 4.0 * (double)(R->n > 0 ? R->n : 1) / (double)(R->surf > 0 ? R->surf : 1);
        double v1 = (mfp - MFP_MIN) / (WAVELEN_BLOCKS - MFP_MIN);
        if (v1 < 0) v1 = 0; if (v1 > 1) v1 = 1;
        v1 = v1 * v1 * (3.0 - 2.0 * v1);
        /* A room with NO enclosure surface cannot hold a reverberant field: 4P/R needs an R,
         * and a space with no boundary has none. Defaulting its sky fraction to 0 instead
         * treated it as perfectly enclosed, which is the opposite of the truth.
         *
         * Two bunker rooms reach surf == 0 here where the model gets surf == 1 -- a total of
         * 2 sky faces out of 95,964, an 0.002% accumulation difference whose cause is NOT
         * yet explained and is recorded rather than papered over. The guard is right
         * independently of it, and with it the room fields match the model exactly. */
        if (R->surf <= 0) { s->canrev[r] = 0.0; continue; }
        double sky = R->surf > 0 ? (double)R->sky / (double)R->surf : 0.0;
        double v2 = (sky - SKY_FULL) / (SKY_NONE - SKY_FULL);
        if (v2 < 0) v2 = 0; if (v2 > 1) v2 = 1;
        v2 = v2 * v2 * (3.0 - 2.0 * v2);
        /* how much of this room's boundary is opening rather than surface */
        double bnd = (double)(R->surf > 0 ? R->surf : 1) + R->open_area;
        double osh = bnd > 0.0 ? R->open_area / bnd : 0.0;
        double v3 = (osh - OPEN_SHARE_FULL) / (OPEN_SHARE_NONE - OPEN_SHARE_FULL);
        if (v3 < 0) v3 = 0; if (v3 > 1) v3 = 1;
        v3 = v3 * v3 * (3.0 - 2.0 * v3);
        s->canrev[r] = v1 * (1.0 - v2) * (1.0 - v3);
    }

    /* pass 1: power arriving at each room's inlets */
    double *pin = s->pin;
    for (int r = 0; r < nr; r++) pin[r] = 0.0;
    for (int nd = 0; nd < ac->nnodes; nd++) {
        int rm = s->node_room[nd];
        if (rm < 0) continue;
        double p = 0.0;
        if (s->seed[nd] >= 0) p += pow(10.0, -s->seed[nd] / 10.0);
        for (int e = 0; e < ac->nedge; e++) {
            const struct ac_edge *E = &ac->edge[e];
            if (E->a != nd && E->b != nd) continue;
            int other = (E->a == nd) ? E->b : E->a;
            if (s->node_room[other] == rm) continue;   /* a routing cut inside a room is not an inlet */
            if (!s->cost[e].set) continue;
            p += pow(10.0, -(s->cost[e].add - constrict_db(s->cost[e].bneck)) / 10.0);
        }
        pin[rm] += p;
    }
    /* ENERGY CONSERVATION. The loop above walks every NODE of a room and, per node, sums
     * over all its inlets, so the same sound can be counted more than once. A room cannot
     * receive more power than the source radiates; levels here are relative to the source,
     * so that bound is unity. Working back from an unbounded version, one room implied
     * +11.6 dB of input -- more than the speaker emits -- which is how its reverberation
     * came to be 54 dB louder than the air route feeding it. */
    for (int r = 0; r < nr; r++) if (pin[r] > 1.0) pin[r] = 1.0;

    double *direct = s->direct;
    for (int r = 0; r < nr; r++)
        direct[r] = pin[r] > 0 ? s->canrev[r] * 4.0 * pin[r] / s->absorb[r] : 0.0;

    /* pass 2: power driven through shared WALLS by the neighbouring rooms. Power incident
     * on a surface of area S in a diffuse field is P*S/A, not field*S -- the standard
     * relation carries a factor of four cancelling the one in 4P/A, and getting that wrong
     * made the receiving room LOUDER than its source. One Jacobi iteration suffices: wall
     * transmission is a large loss, so the coupling is weak. */
    for (int r = 0; r < nr; r++) {
        double pwall = 0.0;
        for (int w = 0; w < ac->nwall; w++) {
            const struct ac_wall *W = &ac->wall[w];
            int other = -1;
            if (W->a == r) other = W->b;
            else if (W->b == r) other = W->a;
            else continue;
            double src = direct[other];
            if (src <= 0) continue;
            double pOther = src * s->absorb[other] / 4.0;
            pwall += pOther * W->area / s->absorb[other] * W->tau;
        }
        s->field[r] = s->canrev[r] * (direct[r] + (pwall > 0 ? 4.0 * pwall / s->absorb[r] : 0.0))
                    * pow(10.0, DIFFUSE_CAL / 10.0);
    }

    /* --- PASS 3: APERTURE COUPLING, SOLVED TO CONVERGENCE ---
     * Rooms exchange energy through their OPENINGS as well as their walls, and that exchange
     * belongs here -- a small linear system over 13-55 rooms -- not at the listener, where it was
     * 47 separate ray-cast terms per position. A bundle cast from the listener to an opening 50
     * blocks away flips from blocked to clear in ONE block, so the sum of 47 of them lurches: all
     * eight store speakers moved 3-5.6 dB together across a single step, the reverberant term
     * jumping a consistent +5.5 dB. Room ambience does not change 5 dB when you take one step, and
     * it cannot be made not to while it is computed that way. Solved here it is a property of the
     * ROOMS, uniform inside each, and cannot lurch.
     *
     * Power leaving room j through an opening of area a is (field_j / 4) * a; it arrives in room i
     * and joins i's field as 4*P/absorb -- the same relation the wall pass uses. Jacobi to
     * convergence rather than the wall pass's single iteration, because an opening is NOT a large
     * loss: energy crosses it repeatedly, and one pass badly under-counts a suite of connected
     * rooms. It contracts because every crossing pays the receiving room's absorption, which
     * includes its own openings. */
    {
        double *cur = s->apcur, *nxt = s->apnxt;
        for (int r = 0; r < nr; r++) cur[r] = s->field[r];
        for (int it = 0; it < 24; it++) {
            for (int r = 0; r < nr; r++) nxt[r] = s->field[r];
            for (int e = 0; e < ac->nedge; e++) {
                const struct ac_edge *E = &ac->edge[e];
                if (E->ra < 0 || E->rb < 0 || E->ra == E->rb) continue;
                if (E->ra >= nr || E->rb >= nr) continue;
                double of = E->open_frac; if (of > 1.0) of = 1.0; if (of < 0.0) of = 0.0;
                double a = E->area * of; if (a < 1e-6) a = 1e-6;
                if (cur[E->rb] > 0.0)
                    nxt[E->ra] += s->canrev[E->ra] * 4.0 * ((cur[E->rb] / 4.0) * a) / s->absorb[E->ra];
                if (cur[E->ra] > 0.0)
                    nxt[E->rb] += s->canrev[E->rb] * 4.0 * ((cur[E->ra] / 4.0) * a) / s->absorb[E->rb];
            }
            double moved = 0.0;
            for (int r = 0; r < nr; r++) {
                double d = nxt[r] - cur[r]; if (d < 0) d = -d;
                if (d > moved) moved = d;
                cur[r] = nxt[r];
            }
            if (moved < 1e-12) break;
        }
        for (int r = 0; r < nr; r++) s->field[r] = cur[r];
    }
}

cmm_ac_src *cmm_ac_src_create(cmm_ac *scene, float x, float y, float z) {
    if (!scene) return NULL;
    cmm_ac_src *s = (cmm_ac_src *)calloc(1, sizeof(cmm_ac_src));
    if (!s) return NULL;
    s->sc = scene; s->x = x; s->y = y; s->z = z;
    int ne = scene->nedge > 0 ? scene->nedge : 1;
    int nn = scene->nnodes > 0 ? scene->nnodes : 1;
    int hc = ne * 32 + 1024;
    int nrm = scene->nrooms > 0 ? scene->nrooms : 1;
    size_t bytes = (size_t)ne * sizeof(struct ac_cost)
                 + (size_t)nn * sizeof(double)
                 + (size_t)hc * sizeof(int)
                 + (size_t)hc * sizeof(double)
                 + (size_t)nrm * 7 * sizeof(double)   /* +2: pass-3 Jacobi scratch */
                 + (size_t)nn * sizeof(int);
    s->arena = malloc(bytes);
    if (!s->arena) { free(s); return NULL; }
    memset(s->arena, 0, bytes);
    s->arena_bytes = bytes;
    unsigned char *p = (unsigned char *)s->arena;
    s->cost = (struct ac_cost *)p; p += (size_t)ne * sizeof(struct ac_cost);
    s->seed = (double *)p;         p += (size_t)nn * sizeof(double);
    s->heap = (int *)p;            p += (size_t)hc * sizeof(int);
    s->hkey = (double *)p;         p += (size_t)hc * sizeof(double);
    s->field  = (double *)p;       p += (size_t)nrm * sizeof(double);
    s->absorb = (double *)p;       p += (size_t)nrm * sizeof(double);
    s->canrev = (double *)p;       p += (size_t)nrm * sizeof(double);
    s->pin    = (double *)p;       p += (size_t)nrm * sizeof(double);
    s->direct = (double *)p;       p += (size_t)nrm * sizeof(double);
    s->apcur  = (double *)p;       p += (size_t)nrm * sizeof(double);
    s->apnxt  = (double *)p;       p += (size_t)nrm * sizeof(double);
    s->node_room = (int *)p;
    s->heap_cap = hc;
    s->nedge_cap = ne; s->nnode_cap = nn; s->nroom_cap = nrm;
    /* node -> room, once: the mapping is scene geometry and does not change with position */
    for (int nd = 0; nd < nn; nd++) s->node_room[nd] = -1;
    for (long long i = 0; i < scene->n; i++) {
        int nd = scene->node[i];
        if (nd >= 0 && nd < nn && s->node_room[nd] < 0) s->node_room[nd] = scene->root[i];
    }
    src_solve(s);
    src_fields(s);
    return s;
}

void cmm_ac_src_destroy(cmm_ac_src *s) {
    if (!s) return;
    free(s->arena);
    free(s);
}

int cmm_ac_src_move(cmm_ac_src *s, float x, float y, float z) {
    if (!s) return 0;
    /* ALLOCATES NOTHING. This is the call a moving speaker makes every tick it moves,
     * and later every enemy: the arena was sized at create from the scene's graph, so
     * N moving sources cost N graph solves and zero allocation. */
    if (s->sc->nedge > s->nedge_cap || s->sc->nnodes > s->nnode_cap) return 0;
    s->x = x; s->y = y; s->z = z;
    src_solve(s);
    src_fields(s);
    return 1;
}

int cmm_ac_src_rebind(cmm_ac_src *s) {
    if (!s) return 0;
    if (s->sc->nedge <= s->nedge_cap && s->sc->nnodes <= s->nnode_cap) { src_solve(s); return 1; }
    /* the graph grew past what this source was sized for: reallocate once, then re-solve */
    int ne = s->sc->nedge > 0 ? s->sc->nedge : 1;
    int nn = s->sc->nnodes > 0 ? s->sc->nnodes : 1;
    int hc = ne * 32 + 1024;
    size_t bytes = (size_t)ne * sizeof(struct ac_cost) + (size_t)nn * sizeof(double)
                 + (size_t)hc * sizeof(int) + (size_t)hc * sizeof(double);
    void *a = realloc(s->arena, bytes);
    if (!a) return 0;
    s->arena = a; s->arena_bytes = bytes;
    unsigned char *p = (unsigned char *)s->arena;
    s->cost = (struct ac_cost *)p; p += (size_t)ne * sizeof(struct ac_cost);
    s->seed = (double *)p;         p += (size_t)nn * sizeof(double);
    s->heap = (int *)p;            p += (size_t)hc * sizeof(int);
    s->hkey = (double *)p;
    s->heap_cap = hc;
    s->nedge_cap = ne; s->nnode_cap = nn;
    src_solve(s);
    return 1;
}

/* Fresnel disc sample points: rings at 0, 0.5R and 1.0R with area shares summing to 1,
 * staggered so samples do not line up. An AREA INTEGRAL, so adding samples refines it
 * rather than shifting it -- unlike max (steps at feature edges), mean (divides by sample
 * count and misreports a barrier whose patches differ) or a plain sum over overlapping
 * rays (over-counts). */
static void fresnel_pt(int i, double *rf, double *ca, double *sa, double *w) {
    static const double RING_R[3] = {0.0, 0.5, 1.0};
    static const int    RING_N[3] = {1, 6, 8};
    static const double RING_W[3] = {0.0625, 0.5, 0.4375};
    int r = 0, k = i;
    while (r < 3 && k >= RING_N[r]) { k -= RING_N[r]; r++; }
    if (r >= 3) { *rf = 0; *ca = 1; *sa = 0; *w = 0; return; }
    double ang = ((double)k / RING_N[r]) * 6.283185307179586 + RING_R[r] * 1.7;
    *rf = RING_R[r]; *ca = cos(ang); *sa = sin(ang); *w = RING_W[r] / RING_N[r];
}

int cmm_ac_eval(cmm_ac_src *s, float lxf, float lyf, float lzf,
                cmm_ac_arrival *out, int max_out) {
    if (!s || !out || max_out <= 0) return -1;
    cmm_ac *ac = s->sc;
    /* THE LISTENER IS A WORLD POSITION, NOT A VOXEL INDEX.
     * This added half a block to each axis, because the offline harness and the JS model both feed
     * INTEGER voxel indices and convert with wx+0.5. The game does not: the bridge passes the
     * player's actual float ear position, so every listener was displaced by (0.5,0.5,0.5) -- 0.87
     * blocks diagonally.
     *
     * At range that is nothing; at arm's length it is everything. Standing 1.2 blocks from a
     * speaker the model measured 2.0, which is 20*log10(2.06/1.30) = 4.0 dB of loss out of thin
     * air. Straight from the game log:
     *
     *     d=1.2 model=-6.3 free=-2.2 OCCL=-4.1 extra=17.0 [dir -6.6 ...] pathlen 2
     *
     * -- four decibels of "occlusion" and seventeen virtual blocks of it, standing next to a
     * speaker with nothing in the way. That is the "quieter when standing near the speaker" report,
     * and it survived a week of investigation because every offline test passes integers, where the
     * convention is self-consistent and the parity harness agreed to 0.02 dB. The bug lives only at
     * the boundary between the two, which is the one place nothing was measuring.
     *
     * The SOURCE above is convention-agnostic on purpose -- floor(x)+0.5 snaps to the containing
     * voxel's centre whether it is given 12 or 12.5 -- so only the listener needed correcting. The
     * listener must NOT be snapped: the model takes float positions precisely so a moving ear does
     * not produce a staircase. */
    double lx = lxf, ly = lyf, lz = lzf;
    double sx5 = floor(s->x) + 0.5, sy5 = floor(s->y) + 0.5, sz5 = floor(s->z) + 0.5;
    double d = sqrt((lx - sx5) * (lx - sx5) + (ly - sy5) * (ly - sy5) + (lz - sz5) * (lz - sz5));

    /* --- the transmission disc, at the barrier --- */
    double bx, by, bz;
    int has_barrier = barrier_mid(ac, sx5, sy5, sz5, lx, ly, lz, &bx, &by, &bz);
    double fan_pow = 0.0, fan_open = 0.0;
    if (!has_barrier) {
        fan_pow = pow(10.0, -dist_db(d, 0.5) / 10.0);
        fan_open = fan_pow;
    } else {
        double ux = lx - sx5, uy = ly - sy5, uz = lz - sz5;
        double ul = sqrt(ux * ux + uy * uy + uz * uz); if (ul < 1e-9) ul = 1;
        ux /= ul; uy /= ul; uz /= ul;
        double axx = 0, ayy = 1, azz = 0;
        if (fabs(uy) > 0.9) { axx = 1; ayy = 0; azz = 0; }
        double e1x = uy * azz - uz * ayy, e1y = uz * axx - ux * azz, e1z = ux * ayy - uy * axx;
        double e1l = sqrt(e1x * e1x + e1y * e1y + e1z * e1z); if (e1l < 1e-9) e1l = 1;
        e1x /= e1l; e1y /= e1l; e1z /= e1l;
        double e2x = uy * e1z - uz * e1y, e2y = uz * e1x - ux * e1z, e2z = ux * e1y - uy * e1x;
        for (int i = 0; i < 15; i++) {
            double rf, ca, sa, w;
            fresnel_pt(i, &rf, &ca, &sa, &w);
            if (w <= 0) continue;
            /* radius from where the barrier actually sits, not a constant */
            double fzr = fresnel_r(sqrt((bx - sx5) * (bx - sx5) + (by - sy5) * (by - sy5) + (bz - sz5) * (bz - sz5)),
                                   sqrt((bx - lx) * (bx - lx) + (by - ly) * (by - ly) + (bz - lz) * (bz - lz)));
            double pr = rf * fzr;
            double qx = bx + (e1x * ca + e2x * sa) * pr;
            double qy = by + (e1y * ca + e2y * sa) * pr;
            double qz = bz + (e1z * ca + e2z * sa) * pr;
            /* EVERY patch counts, blocked or clear. Skipping the clear ones threw away the
             * unblocked share of the zone, so the instant a sight line clipped a corner the
             * level fell off a cliff -- measured at -3.3 dB one block and -21.5 the next. */
            double m = seg_db_at(ac, sx5, sy5, sz5, qx, qy, qz, 1, 0)
                     + seg_db_at(ac, qx, qy, qz, lx, ly, lz, 0, 1);
            double len = sqrt((qx - sx5) * (qx - sx5) + (qy - sy5) * (qy - sy5) + (qz - sz5) * (qz - sz5))
                       + sqrt((qx - lx) * (qx - lx) + (qy - ly) * (qy - ly) + (qz - lz) * (qz - lz));
            /* THE DISC MEASURES TRANSMISSION. DISTANCE IS THE DIRECT PATH'S JOB.
             * Each patch was charged for its OWN path, source->patch->listener, longer than the
             * direct line by the patch's offset. Under the shipped flat law that was worth ~0.3 dB.
             * Under a physical law it is fatal: a speaker sits inside its own block, so barrier_mid
             * always finds a "barrier" at the source, the disc integrates patches up to three
             * blocks off-axis, and their paths cost 15 dB against a free field of 0. Measured half
             * a block from a speaker in open air with nothing in the way: -12.6 dB of occlusion.
             * Reported from the game as "it gets muffled when I'm closer to it, especially while
             * right on top of it".
             *
             * The path DIFFERENCE across a Fresnel zone is a PHASE difference -- it is what makes
             * the zones interfere -- and this sum is incoherent, over power, so it carries no
             * energy cost here. What the disc measures is the FRACTION of the wavefront that gets
             * through. Charge the direct distance once and let the patches differ only by the
             * material they cross: with no material anywhere the disc then sums to exactly free
             * field, which is the answer it always should have given. */
            double pw = w * pow(10.0, -(dist_db(d, 0.5) + m) / 10.0);
            fan_pow += pw;
            /* "Came through air" is a property of the PATH, not of where the sample sits: a
             * patch in air on the FAR side of a barrier still had to cross it. */
            fan_open += (1.0 - sstep(0, 2 * MIN_TRANSMIT_DB, m)) * pw;
        }
        if (fan_pow <= 0) {
            fan_pow = pow(10.0, -(dist_db(d, 0.5) + seg_db_at(ac, sx5, sy5, sz5, lx, ly, lz, 1, 1)) / 10.0);
            fan_open = 0;
        }
    }
    double fan_db = fan_pow > 0 ? -10.0 * log10(fan_pow) : 120.0;
    double open_frac = fan_pow > 0 ? fan_open / fan_pow : 1.0;
    if (open_frac < 0) open_frac = 0; if (open_frac > 1) open_frac = 1;
    double center_mat = seg_db_at(ac, sx5, sy5, sz5, lx, ly, lz, 1, 1);

    int lxi = (int)floor(lxf) - ac->x0, lyi = (int)floor(lyf) - ac->y0, lzi = (int)floor(lzf) - ac->z0;
    int nd = -1;
    if (lxi >= 0 && lxi < ac->sx && lyi >= 0 && lyi < ac->sy && lzi >= 0 && lzi < ac->sz)
        nd = ac->node[idx_of(ac, lxi, lyi, lzi)];
    int my_room = (nd >= 0 && nd < ac->nnodes) ? s->node_room[nd] : -1;

    int n = 0;
    double pow_tot = 0.0, best_db = -1e30;

    /* --- the source arrival, split where it physically splits --- */
    double min_seed = 1e30;
    for (int i = 0; i < ac->nnodes; i++) if (s->seed[i] >= 0 && s->seed[i] < min_seed) min_seed = s->seed[i];
    if (min_seed > 1e29) min_seed = 0;
    double src_db = -(min_seed + fan_db);
    if (src_db > -110 && n < max_out) {
        if (open_frac > 1e-4) {
            out[n].db = (float)(src_db + 10.0 * log10(open_frac));
            out[n].px = (float)sx5; out[n].py = (float)sy5; out[n].pz = (float)sz5;
            out[n].fc = (float)(20000.0 * exp(-d / 140.0));
            out[n].len = (float)d; out[n].weight = 1.0f; out[n].kind = 0;
            pow_tot += pow(10.0, out[n].db / 10.0);
            if (out[n].db > best_db) best_db = out[n].db;
            n++;
        }
        if (open_frac < 1 - 1e-4 && n < max_out) {
            double px = has_barrier ? bx : sx5, py = has_barrier ? by : sy5, pz = has_barrier ? bz : sz5;
            out[n].db = (float)(src_db + 10.0 * log10(1.0 - open_frac));
            out[n].px = (float)px; out[n].py = (float)py; out[n].pz = (float)pz;
            out[n].fc = (float)(20000.0 * pow(10.0, -(center_mat > 0 ? center_mat : 0) / 26.0));
            out[n].len = (float)d; out[n].weight = 1.0f; out[n].kind = 1;
            pow_tot += pow(10.0, out[n].db / 10.0);
            if (out[n].db > best_db) best_db = out[n].db;
            n++;
        }
    }

    /* --- aperture arrivals, over the FIXED candidate set ---
     * TWO CAPACITY FAULTS LIVED HERE, and both silently changed the physics:
     *
     * The loop stopped at max_out, so on the store its 81 apertures filled all 32 slots and
     * the DIFFUSE FIELD -- the loudest component by far, and the term the whole model leans
     * on to keep levels smooth -- was never added at all. Measured -10.9 dB against the
     * model's -0.65. A capacity limit must never decide which physics runs, so the diffuse
     * now has a reserved slot.
     *
     * And when full, the loop kept the FIRST candidates by edge index rather than the
     * loudest, which is an arbitrary choice dressed up as a limit. It now displaces the
     * weakest arrival when a louder one arrives, so the cap costs accuracy in the tail
     * instead of wherever the edge numbering happened to fall. */
    const int ap_cap = max_out - 1;          /* one reserved for the diffuse field */

    /* --- AN OPENING IS NOT A SOURCE: THE OPENINGS SHARE ONE SPEAKER ---
     * Every aperture was given its own near-full copy of the speaker and the copies were then
     * energy-summed, so a room with many openings delivered several speakers' worth of power.
     * Measured over the store capture, 58% of standable positions came out ABOVE 0 dB per
     * speaker -- the listener receiving more energy than the speaker emits. The C# bridge
     * cannot express gain as an attenuation, so it clamped to fully unoccluded, and occlusion
     * was switched OFF across most of a POI. That is what the author heard as "on the other
     * side of a wall it sounds like there's barely anything between me and that wall", and why
     * no material constant changed it: those points had already been clamped.
     *
     * A speaker radiates into 4*pi and each opening intercepts the solid angle it subtends,
     * area/dist^2. Those patches are disjoint, so the shares can only add up to the whole --
     * geometry, not a clamp.
     *
     * Normalised AMONG the apertures rather than against 4*pi, deliberately: this model charges
     * spreading through an exponential SLOPE rather than inverse-square, so the absolute form
     * double-charges it. The relative form fixes the double COUNT, which is the defect, and is
     * exactly neutral with one aperture -- every single-opening spec scenario is untouched
     * (verified against the JS model: spec 9/9, all six synthetic routes +0.00). */
    double omega_sum = 0.0;
    for (int e = 0; e < ac->nedge; e++) {
        const struct ac_edge *E = &ac->edge[e];
        if (E->ra < 0 || E->rb < 0 || E->ra == E->rb) continue;
        double ex = E->sx / (E->area > 0 ? E->area : 1);
        double ey = E->sy / (E->area > 0 ? E->area : 1);
        double ez = E->sz / (E->area > 0 ? E->area : 1);
        double dd = (ex - sx5) * (ex - sx5) + (ey - sy5) * (ey - sy5) + (ez - sz5) * (ez - sz5);
        omega_sum += (E->area > 0 ? E->area : 1.0) / (dd > 1.0 ? dd : 1.0);
    }

    for (int e = 0; e < ac->nedge; e++) {
        const struct ac_edge *E = &ac->edge[e];
        if (E->ra < 0 || E->rb < 0 || E->ra == E->rb) continue;   /* a cut is not an aperture */
        double px = E->sx / (E->area > 0 ? E->area : 1);
        double py = E->sy / (E->area > 0 ? E->area : 1);
        double pz = E->sz / (E->area > 0 ? E->area : 1);
        /* An aperture's cost is the CHEAPEST way for sound to arrive AT it, which is the best
         * of its own node's portals plus the hop from there -- not merely this edge's own
         * routed cost. Using the edge's cost alone overstates it whenever a neighbouring
         * portal offers a shorter route into the same node, and the aperture then reads too
         * quiet. Its own constriction is charged after, since anything using it must squeeze
         * through. */
        int an = E->a;
        double best = 1e30, best_len = 0;
        if (an >= 0 && an < ac->nnodes && s->seed[an] >= 0) {
            double h = sqrt((px - sx5) * (px - sx5) + (py - sy5) * (py - sy5) + (pz - sz5) * (pz - sz5));
            best = s->seed[an] + SLOPE * h;
            best_len = h;
        }
        for (int q = 0; q < ac->nedge; q++) {
            if (!s->cost[q].set) continue;
            const struct ac_edge *Q = &ac->edge[q];
            if (Q->a != an && Q->b != an) continue;
            double qx = Q->sx / (Q->area > 0 ? Q->area : 1);
            double qy = Q->sy / (Q->area > 0 ? Q->area : 1);
            double qz = Q->sz / (Q->area > 0 ? Q->area : 1);
            double hop = sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy) + (pz - qz) * (pz - qz));
            double cand = s->cost[q].add - constrict_db(s->cost[q].bneck) + SLOPE * hop;
            if (cand < best) { best = cand; best_len = s->cost[q].len + hop; }
        }
        if (best > 1e29) continue;
        double cost = best - constrict_db(E->open_frac);
        if (omega_sum > 0.0) {
            double dd2 = (px - sx5) * (px - sx5) + (py - sy5) * (py - sy5) + (pz - sz5) * (pz - sz5);
            double omega = (E->area > 0 ? E->area : 1.0) / (dd2 > 1.0 ? dd2 : 1.0);
            cost -= 10.0 * log10(omega / omega_sum);       /* this opening's share of the sphere */
        }
        double route_len = best_len;
        double leg = sqrt((px - lx) * (px - lx) + (py - ly) * (py - ly) + (pz - lz) * (pz - lz));
        double legmat = leg_db(ac, px, py, pz, lx, ly, lz);
        /* The route accumulated its geometry as SLOPE*length while Dijkstra ordered on it.
         * Take that back out and charge the whole path -- route plus final leg -- once under the
         * law, so a detour costs what a detour costs. */
        double db = -((cost - SLOPE * route_len) + dist_db(route_len + leg, ap_rad(E->area)) + legmat);
        /* A candidate whose route is barely longer than the straight line describes the SAME
         * physical path as the direct sound, not an alternative -- the Fresnel criterion, and
         * why a wide-open doorway directly ahead adds nothing on top of the direct arrival.
         * Measured GEOMETRICALLY: the routed polyline bends through portal centroids and
         * overestimates, which made a doorway on the line of sight look like a detour. */
        double geo = sqrt((px - sx5) * (px - sx5) + (py - sy5) * (py - sy5) + (pz - sz5) * (pz - sz5))
                   + leg - d;
        double w = sstep(0, 2, geo);
        if (w <= 1e-4 || db <= -110) continue;
        int slot = n;
        if (n >= ap_cap) {
            /* full: displace the weakest APERTURE arrival, and only if this one beats it */
            int worst = -1;
            for (int i = 0; i < n; i++)
                if (out[i].kind == 2 && (worst < 0 || out[i].db < out[worst].db)) worst = i;
            if (worst < 0 || out[worst].db >= db) continue;
            pow_tot -= out[worst].weight * pow(10.0, out[worst].db / 10.0);
            slot = worst;
        } else n++;
        out[slot].db = (float)db;
        out[slot].px = (float)px; out[slot].py = (float)py; out[slot].pz = (float)pz;
        out[slot].fc = (float)(20000.0 * exp(-(route_len + leg) / 140.0));
        out[slot].len = (float)(route_len + leg);
        out[slot].weight = (float)w; out[slot].kind = 2;
        pow_tot += w * pow(10.0, db / 10.0);
        if (db > best_db) best_db = db;
    }

    if (getenv("CMM_ONE")) {
        fprintf(stderr, "  [dbg] node=%d room=%d ownField=%.3g nedge=%d openFrac=%.3f\n",
                nd, my_room, my_room >= 0 ? s->field[my_room] : -1.0, ac->nedge, open_frac);
    }
    /* --- the diffuse field, which always gets its slot --- */
    if (my_room >= 0 && n < max_out) {
        double ptot = s->field[my_room];
        for (int e = 0; e < ac->nedge; e++) {
            const struct ac_edge *E = &ac->edge[e];
            if (E->ra < 0 || E->rb < 0 || E->ra == E->rb) continue;
            /* NO room-membership gate: topology decides how much arrives, never whether. */
            double pf = 0;
            if (E->ra != my_room) pf += s->field[E->ra];
            if (E->rb != my_room) pf += s->field[E->rb];
            if (pf <= 0) continue;
            double px = E->sx / (E->area > 0 ? E->area : 1);
            double py = E->sy / (E->area > 0 ? E->area : 1);
            double pz = E->sz / (E->area > 0 ? E->area : 1);
            double leg = ap_dist(E, lx, ly, lz);
            double lm = leg_db(ac, px, py, pz, lx, ly, lz);
            /* HOW MUCH OF THE OPENING YOU ACTUALLY SEE.
             * Constriction was a fixed penalty charged to everyone outside a room and to nobody
             * inside it, so it landed as a STEP at the boundary -- and the model's own continuity
             * argument ("approaching an opening the neighbour's arrival tends to full strength;
             * cross over and the two swap roles, and the SUM is symmetric") only holds if that
             * penalty vanishes at the opening. It did not. Measured on the store: crossing into a
             * 122-voxel enclosed pocket jumped the diffuse field 14 dB in one block, identically
             * for all eight speakers -- the worst standable step in the capture.
             *
             * Constriction and spreading are one fact told twice. An opening of radius a seen from
             * r subtends 2*pi*(1 - r/sqrt(r^2+a^2)) of the hemisphere, and that IS the fraction of
             * the neighbour's field that reaches you: 1 with your nose in the doorway, so the
             * boundary is continuous by construction rather than by argument, and area/(2*pi*r^2)
             * far away, which is the constriction and the spreading together. */
            double arad = ap_rad(E->area);
            ptot += pf * (1.0 - leg / sqrt(leg * leg + arad * arad)) * pow(10.0, -lm / 10.0);
        }
        if (getenv("CMM_ONE")) fprintf(stderr, "  [dbg] diffuse ptot=%.6g\n", ptot);
        if (ptot > 1e-12) {
            double ddb = 10.0 * log10(ptot);
            if (ddb > -110) {
                out[n].db = (float)ddb;
                out[n].px = (float)lx; out[n].py = (float)ly; out[n].pz = (float)lz;
                out[n].fc = 3500.0f;
                out[n].len = (float)(d + DIFFUSE_EXTRA);
                out[n].weight = 1.0f; out[n].kind = 3;
                pow_tot += pow(10.0, ddb / 10.0);
                n++;
            }
        }
    }

    /* prune by CONTRIBUTION only -- never by topology or search radius, which would make the
     * set a step function of position and put a cliff in the field */
    int keep = 0;
    for (int i = 0; i < n; i++)
        if (out[i].kind == 3 || out[i].db >= best_db - PRUNE_DB) out[keep++] = out[i];
    return keep;
}

/* Total level at a listener, dB relative to the source: the energy sum of every arrival. */
double cmm_ac_eval_db(cmm_ac_src *s, float lx, float ly, float lz) {
    cmm_ac_arrival a[MAX_ARRIVALS];
    int n = cmm_ac_eval(s, lx, ly, lz, a, MAX_ARRIVALS);
    if (n <= 0) return -120.0;
    double p = 0;
    for (int i = 0; i < n; i++) p += a[i].weight * pow(10.0, a[i].db / 10.0);
    return p > 1e-12 ? 10.0 * log10(p) : -120.0;
}

/* parity read-back: how many edges the solve reached, and the best routed cost */
long long cmm_ac_src_stat(const cmm_ac_src *s, int what) {
    if (!s) return -1;
    if (what == 0) { long long c = 0; for (int e = 0; e < s->sc->nedge; e++) if (s->cost[e].set) c++; return c; }
    if (what == 1) { long long c = 0; for (int n = 0; n < s->sc->nnodes; n++) if (s->seed[n] >= 0) c++; return c; }
    return -1;
}
/* Per-room diagnostics, so a parity failure names the ROOM and the TERM rather than
 * showing two sorted lists that cannot say which one diverged. Fields, in order:
 * voxels, surf, sky, open_area, absorption, canreverb, pin, direct, field(dB). */
int cmm_ac_src_room_info(const cmm_ac_src *s, int room, double *out, int nout) {
    if (!s || room < 0 || room >= s->sc->nrooms || nout < 9) return 0;
    const struct ac_room *R = &s->sc->room[room];
    out[0] = (double)R->n;
    out[1] = (double)R->surf;
    out[2] = (double)R->sky;
    out[3] = R->open_area;
    out[4] = s->absorb[room];
    out[5] = s->canrev[room];
    out[6] = s->pin[room];
    out[7] = s->direct[room];
    out[8] = s->field[room] > 1e-12 ? 10.0 * log10(s->field[room]) : -999.0;
    return 1;
}

double cmm_ac_src_field_db(const cmm_ac_src *s, int room) {
    if (!s || room < 0 || room >= s->sc->nrooms) return -999.0;
    double v = s->field[room];
    return v > 1e-12 ? 10.0 * log10(v) : -999.0;
}
double cmm_ac_src_cost(const cmm_ac_src *s, int edge) {
    if (!s || edge < 0 || edge >= s->sc->nedge || !s->cost[edge].set) return -1.0;
    return s->cost[edge].add;
}

long long cmm_ac_stat(const cmm_ac *ac, int what) {
    if (!ac) return -1;
    long long c = 0;
    switch (what) {
        case 0: return ac->n;
        case 1: for (long long i = 0; i < ac->n; i++) if (ac->open[i] > OPEN_EPS) c++; return c;
        case 2: for (long long i = 0; i < ac->n; i++) if (ac->open[i] >= 0.999f) c++; return c;
        case 3: for (long long i = 0; i < ac->n; i++) if (ac->thick[i] >= THICK_CAP) c++; return c;
        case 4: return (long long)ac->arena_bytes;
        case 5: return ac->nreg;      /* raw watershed regions, before merging */
        case 6: return ac->nprt;      /* contacts between raw regions */
        case 7: return ac->nrooms;    /* merged regions */
        case 8: {                     /* voxels in the largest merged region */
            if (ac->nrooms <= 0) return 0;
            long long *cnt = (long long *)calloc((size_t)ac->nrooms, sizeof(long long));
            if (!cnt) return -1;
            for (long long i = 0; i < ac->n; i++) if (ac->root[i] >= 0) cnt[ac->root[i]]++;
            long long mx = 0;
            for (int r = 0; r < ac->nrooms; r++) if (cnt[r] > mx) mx = cnt[r];
            free(cnt); return mx;
        }
        case 9:  return ac->nnodes;   /* routing nodes: rooms cut into corridor segments */
        case 10: return ac->nedge;    /* node-level boundaries */
        default: return -1;
    }
}

double cmm_ac_mean_thick(const cmm_ac *ac) {
    if (!ac) return 0.0;
    double s = 0.0; long long c = 0;
    for (long long i = 0; i < ac->n; i++)
        if (ac->open[i] > OPEN_EPS) { s += ac->thick[i]; c++; }
    return c ? s / (double)c : 0.0;
}
