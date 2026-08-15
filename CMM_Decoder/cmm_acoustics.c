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
    int nrooms;              /* merged regions that survive, renumbered 0..nrooms-1 */
};

int cmm_ac_abi(void) { return CMM_AC_ABI; }

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
            E->x0 = E->y0 = E->z0 = 1e30f; E->x1 = E->y1 = E->z1 = -1e30f;
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
            if (cx < E->x0) E->x0 = (float)cx; if (cx > E->x1) E->x1 = (float)cx;
            if (cy < E->y0) E->y0 = (float)cy; if (cy > E->y1) E->y1 = (float)cy;
            if (cz < E->z0) E->z0 = (float)cz; if (cz > E->z1) E->z1 = (float)cz;
            E->ra = ac->root[i]; E->rb = ac->root[j];
        }
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
            double a = E->area * (E->thick > 0 ? 1.0 : 1.0);
            ac->room[E->ra].open_area += a;
            ac->room[E->rb].open_area += a;
        }
    }
    return 1;
}

int cmm_ac_build_segments(cmm_ac *ac) {
    if (!ac) return 0;
    if (!segment(ac)) return 0;
    if (!contacts(ac)) return 0;
    if (!merge_regions(ac)) return 0;
    if (!split_and_nodes(ac)) return 0;
    if (!build_edges_and_rooms(ac)) return 0;
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
