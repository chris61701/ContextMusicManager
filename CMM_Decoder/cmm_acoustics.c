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

    /* loss + open + thick are float, flags is byte: 13 bytes per voxel. */
    size_t nf = (size_t)ac->n;
    size_t bytes = nf * (3 * sizeof(float) + 1);
    ac->arena = malloc(bytes);
    if (!ac->arena) { free(ac); return NULL; }
    memset(ac->arena, 0, bytes);
    ac->arena_bytes = bytes;

    unsigned char *p = (unsigned char *)ac->arena;
    ac->loss  = (float *)p;         p += nf * sizeof(float);
    ac->open  = (float *)p;         p += nf * sizeof(float);
    ac->thick = (float *)p;         p += nf * sizeof(float);
    ac->flags = p;
    return ac;
}

void cmm_ac_destroy(cmm_ac *ac) {
    if (!ac) return;
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
