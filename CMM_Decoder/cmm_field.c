/* cmm_field — see cmm_field.h for the design and why it is shaped this way. */
#include "cmm_field.h"
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <time.h>

/* Portable microsecond clock: mingw's C11 timespec_get is not reliably present, and this is a
 * diagnostic counter rather than anything the model depends on. */
static long long now_us(void) { return (long long)clock() * (1000000LL / CLOCKS_PER_SEC); }

/* Cost is carried in QUARTER-DECIBEL buckets. A bucket queue beats a heap here because the edge
 * costs are small, bounded and mostly equal, which is exactly the case a monotone bucket queue is
 * for: no comparisons, no sift, and the whole solve is one linear sweep of the buckets. */
#define Q_PER_DB   4
#define MAX_Q      (120 * Q_PER_DB)      /* past this a cell is silent; the flood stops growing */
#define UNREACHED  0x7fffffff

struct cmm_fld {
    int nx, ny, nz, step;
    long long n;
    int ox, oy, oz;                 /* world CELL coords of the window's minimum corner */

    /* Geometry, indexed toroidally. Transmission through the face shared with the -X/-Y/-Z
     * neighbour, as dB (0 = open). Kept in dB rather than as the linear fraction the setter takes
     * because the flood adds it, and converting once on the way in beats a log per relaxation. */
    float *fnx, *fny, *fnz;
    float *fill;                    /* how solid the cell's own interior is, as dB to cross it */
    unsigned char *have;            /* 0 = geometry still pending: treated as opaque, never open */

    /* Flood results. `cur` is the field just solved, `prev` the one before, so the caller can
     * crossfade across a re-solve instead of stepping. */
    int *cur, *prev;
    float clx, cly, clz;            /* listener the CURRENT field was flooded from */

    /* Bucket queue (Dial's, with decrease-key). A cell is in AT MOST ONE bucket at a time --
     * relaxing it to a cheaper cost UNLINKS it from where it was and relinks it. The obvious
     * alternative, pushing a second entry and ignoring stale pops, needs an entry POOL: with one
     * next-pointer per cell the second push overwrites the first, and the bucket that held it
     * silently loses every cell after it in the chain. That is what the first version did, and the
     * flood reached 2 cells out of 8192 -- it read like a sealed world, which is exactly the kind
     * of failure that would have been blamed on the physics if it had been wired to audio first. */
    int *bnext, *bprev, *bhead, *binb;

    /* Fill order: window-relative offsets sorted nearest-centre-first, computed ONCE, walked with
     * a cursor. The first version rescanned outward from the centre on every call, which is fine
     * for a toy grid and quadratic death at the real one -- a 255k-cell window never finished
     * filling. Nearest-first still matters (a half-filled window is already correct close in,
     * where the sources are), it just must not be re-derived a quarter of a million times. */
    short *order;                   /* 3 shorts per cell: dx, dy, dz from the window centre */
    long long cursor;

    float dbPerBlock, maxDb;
    long long pending, reached, lastUs;
};

static inline long long widx(const cmm_fld *f, int cx, int cy, int cz) {
    /* Toroidal: the window scrolls, so a cell keeps its slot as the player walks and only the
     * slab that just came into range is invalidated. Positive modulo, because C's % is not. */
    int x = cx % f->nx; if (x < 0) x += f->nx;
    int y = cy % f->ny; if (y < 0) y += f->ny;
    int z = cz % f->nz; if (z < 0) z += f->nz;
    return (long long)x + (long long)f->nx * ((long long)z + (long long)f->nz * (long long)y);
}
static inline int in_win(const cmm_fld *f, int cx, int cy, int cz) {
    return cx >= f->ox && cx < f->ox + f->nx
        && cy >= f->oy && cy < f->oy + f->ny
        && cz >= f->oz && cz < f->oz + f->nz;
}
/* Linear transmission fraction -> dB of loss, clamped. 0 (sealed) becomes "silent", not infinity. */
static inline float t_db(float t) {
    if (t >= 1.0f)   return 0.0f;
    if (t <= 1e-7f)  return (float)MAX_Q / Q_PER_DB;
    float d = -10.0f * log10f(t);
    return d > (float)MAX_Q / Q_PER_DB ? (float)MAX_Q / Q_PER_DB : d;
}

int cmm_fld_abi(void) { return CMM_FLD_ABI; }

cmm_fld *cmm_fld_create(int nx, int ny, int nz, int step) {
    if (nx <= 0 || ny <= 0 || nz <= 0 || step < 1) return NULL;
    cmm_fld *f = (cmm_fld *)calloc(1, sizeof(cmm_fld));
    if (!f) return NULL;
    f->nx = nx; f->ny = ny; f->nz = nz; f->step = step;
    f->n = (long long)nx * ny * nz;
    f->dbPerBlock = 0.35f;                     /* what a block of DETOUR costs */
    f->maxDb = 60.0f;
    f->fnx  = (float *)malloc(sizeof(float) * f->n);
    f->fny  = (float *)malloc(sizeof(float) * f->n);
    f->fnz  = (float *)malloc(sizeof(float) * f->n);
    f->fill = (float *)malloc(sizeof(float) * f->n);
    f->have = (unsigned char *)calloc(f->n, 1);
    f->cur  = (int *)malloc(sizeof(int) * f->n);
    f->prev = (int *)malloc(sizeof(int) * f->n);
    f->bnext= (int *)malloc(sizeof(int) * f->n);
    f->bprev= (int *)malloc(sizeof(int) * f->n);
    f->binb = (int *)malloc(sizeof(int) * f->n);
    f->bhead= (int *)malloc(sizeof(int) * (MAX_Q + 1));
    if (!f->fnx || !f->fny || !f->fnz || !f->fill || !f->have || !f->cur || !f->prev
        || !f->bnext || !f->bprev || !f->binb || !f->bhead) { cmm_fld_destroy(f); return NULL; }
    for (long long i = 0; i < f->n; i++) { f->cur[i] = UNREACHED; f->prev[i] = UNREACHED; }

    f->order = (short *)malloc(sizeof(short) * 3 * f->n);
    if (!f->order) { cmm_fld_destroy(f); return NULL; }
    {
        /* Bucket by squared distance rather than qsort: the values are small integers and this is
         * a one-off, but an O(n log n) sort of a quarter-million entries at scene creation is a
         * visible hitch and a counting sort is three passes of arithmetic. */
        int mx = nx / 2, my = ny / 2, mz = nz / 2;
        int maxd2 = mx * mx + my * my + mz * mz + 1;
        int *cnt = (int *)calloc(maxd2 + 1, sizeof(int));
        if (!cnt) { cmm_fld_destroy(f); return NULL; }
        for (int y = -my; y < ny - my; y++)
        for (int z = -mz; z < nz - mz; z++)
        for (int x = -mx; x < nx - mx; x++) {
            int d2 = x * x + y * y + z * z; if (d2 > maxd2) d2 = maxd2;
            cnt[d2]++;
        }
        for (int i = 1; i <= maxd2; i++) cnt[i] += cnt[i - 1];
        for (int i = maxd2; i > 0; i--) cnt[i] = cnt[i - 1];
        cnt[0] = 0;
        for (int y = -my; y < ny - my; y++)
        for (int z = -mz; z < nz - mz; z++)
        for (int x = -mx; x < nx - mx; x++) {
            int d2 = x * x + y * y + z * z; if (d2 > maxd2) d2 = maxd2;
            long long slot = cnt[d2]++;
            f->order[slot * 3 + 0] = (short)x;
            f->order[slot * 3 + 1] = (short)y;
            f->order[slot * 3 + 2] = (short)z;
        }
        free(cnt);
    }
    f->pending = f->n;
    f->cursor = 0;
    return f;
}

void cmm_fld_destroy(cmm_fld *f) {
    if (!f) return;
    free(f->fnx); free(f->fny); free(f->fnz); free(f->fill); free(f->have);
    free(f->cur); free(f->prev); free(f->bnext); free(f->bprev); free(f->binb); free(f->bhead);
    free(f->order);
    free(f);
}

void cmm_fld_set_tuning(cmm_fld *f, float dbPerBlock, float maxDb) {
    if (!f) return;
    if (dbPerBlock >= 0.0f) f->dbPerBlock = dbPerBlock;
    if (maxDb      >  0.0f) f->maxDb = maxDb;
}

int cmm_fld_recentre(cmm_fld *f, int bx, int by, int bz) {
    if (!f) return 0;
    int cx = (bx >= 0 ? bx : bx - f->step + 1) / f->step;
    int cy = (by >= 0 ? by : by - f->step + 1) / f->step;
    int cz = (bz >= 0 ? bz : bz - f->step + 1) / f->step;
    int nox = cx - f->nx / 2, noy = cy - f->ny / 2, noz = cz - f->nz / 2;
    if (nox == f->ox && noy == f->oy && noz == f->oz) return (int)f->pending;

    /* Only what LEFT the old window is unknown. Everything still covered keeps its geometry, which
     * is the point of the toroidal layout: walking costs a slab, never the window. */
    int oox = f->ox, ooy = f->oy, ooz = f->oz;
    int first = (f->pending == f->n);          /* nothing has ever been read: no old window at all */
    f->ox = nox; f->oy = noy; f->oz = noz;
    for (int y = noy; y < noy + f->ny; y++)
    for (int z = noz; z < noz + f->nz; z++)
    for (int x = nox; x < nox + f->nx; x++) {
        int wasIn = !first
                 && x >= oox && x < oox + f->nx
                 && y >= ooy && y < ooy + f->ny
                 && z >= ooz && z < ooz + f->nz;
        if (wasIn) continue;
        long long i = widx(f, x, y, z);
        if (f->have[i]) { f->have[i] = 0; f->pending++; }
    }
    if (f->pending > f->n) f->pending = f->n;
    f->cursor = 0;
    return (int)f->pending;
}

int cmm_fld_next_pending(cmm_fld *f, int *cx, int *cy, int *cz) {
    if (!f || f->pending <= 0) return 0;
    int mx = f->nx / 2, my = f->ny / 2, mz = f->nz / 2;
    for (long long pass = 0; pass < f->n; pass++) {
        if (f->cursor >= f->n) f->cursor = 0;
        long long k = f->cursor++;
        int x = f->ox + mx + f->order[k * 3 + 0];
        int y = f->oy + my + f->order[k * 3 + 1];
        int z = f->oz + mz + f->order[k * 3 + 2];
        if (!in_win(f, x, y, z)) continue;
        long long i = widx(f, x, y, z);
        if (f->have[i]) continue;
        *cx = x; *cy = y; *cz = z;
        return 1;
    }
    f->pending = 0;      /* nothing actually outstanding; the counter had drifted */
    return 0;
}

void cmm_fld_set_cell(cmm_fld *f, int cx, int cy, int cz,
                      float tnx, float tny, float tnz, float fill) {
    if (!f || !in_win(f, cx, cy, cz)) return;
    long long i = widx(f, cx, cy, cz);
    f->fnx[i]  = t_db(tnx);
    f->fny[i]  = t_db(tny);
    f->fnz[i]  = t_db(tnz);
    f->fill[i] = t_db(fill >= 1.0f ? 0.0f : 1.0f - fill);   /* fill 1 = solid = no transmission */
    if (!f->have[i]) { f->have[i] = 1; if (f->pending > 0) f->pending--; }
}

void cmm_fld_dirty(cmm_fld *f, int bx, int by, int bz, int radiusBlocks) {
    if (!f) return;
    if (radiusBlocks < 0) radiusBlocks = 0;
    int lo = radiusBlocks + f->step, x0 = bx - lo, x1 = bx + lo;
    int y0 = by - lo, y1 = by + lo, z0 = bz - lo, z1 = bz + lo;
    for (int by2 = y0; by2 <= y1; by2 += f->step)
    for (int bz2 = z0; bz2 <= z1; bz2 += f->step)
    for (int bx2 = x0; bx2 <= x1; bx2 += f->step) {
        int cx = (bx2 >= 0 ? bx2 : bx2 - f->step + 1) / f->step;
        int cy = (by2 >= 0 ? by2 : by2 - f->step + 1) / f->step;
        int cz = (bz2 >= 0 ? bz2 : bz2 - f->step + 1) / f->step;
        if (!in_win(f, cx, cy, cz)) continue;
        long long i = widx(f, cx, cy, cz);
        if (f->have[i]) { f->have[i] = 0; f->pending++; }
    }
    f->cursor = 0;
}

/* Unlink a cell from whatever bucket it is in, then put it in bucket q. */
static inline void bq_set(cmm_fld *f, long long i, int q) {
    int b = f->binb[i];
    if (b >= 0) {
        if (f->bprev[i] >= 0) f->bnext[f->bprev[i]] = f->bnext[i];
        else                  f->bhead[b]           = f->bnext[i];
        if (f->bnext[i] >= 0) f->bprev[f->bnext[i]] = f->bprev[i];
    }
    f->bprev[i] = -1;
    f->bnext[i] = f->bhead[q];
    if (f->bhead[q] >= 0) f->bprev[f->bhead[q]] = (int)i;
    f->bhead[q] = (int)i;
    f->binb[i] = q;
}

int cmm_fld_solve(cmm_fld *f, float lx, float ly, float lz) {
    if (!f) return 0;
    long long t0 = now_us();

    /* The field just solved becomes the one to crossfade FROM. */
    { int *sw = f->prev; f->prev = f->cur; f->cur = sw; }
    for (long long i = 0; i < f->n; i++) { f->cur[i] = UNREACHED; f->binb[i] = -1; }
    for (int c = 0; c <= MAX_Q; c++) f->bhead[c] = -1;

    f->clx = lx; f->cly = ly; f->clz = lz;
    int bx = (int)floorf(lx), by = (int)floorf(ly), bz = (int)floorf(lz);
    int scx = (bx >= 0 ? bx : bx - f->step + 1) / f->step;
    int scy = (by >= 0 ? by : by - f->step + 1) / f->step;
    int scz = (bz >= 0 ? bz : bz - f->step + 1) / f->step;
    if (!in_win(f, scx, scy, scz)) { f->reached = 0; return 0; }

    long long si = widx(f, scx, scy, scz);
    f->cur[si] = 0; bq_set(f, si, 0);

    /* A step between cell centres is `step` blocks of detour; a diagonal is longer, and charging
     * the true length is what stops the flood from preferring staircase routes through corners. */
    const float stepDb  = f->dbPerBlock * (float)f->step;
    const int   qStep   = (int)(stepDb * Q_PER_DB + 0.5f);
    const int   qStep2  = (int)(stepDb * 1.41421356f * Q_PER_DB + 0.5f);
    const int   qStep3  = (int)(stepDb * 1.73205081f * Q_PER_DB + 0.5f);
    const int   maxQ    = (int)(f->maxDb * Q_PER_DB + 0.5f);
    long long reached = 0;

    /* The window's minimum corner, in SLOT space. Slot 0 is not the window edge -- the window
     * scrolls and the wrap point moves with it -- so a neighbour is outside the window when it
     * crosses this edge, not when its slot wraps. Held here so the inner loop can decide with two
     * comparisons instead of a modulo. */
    const int ex = ((f->ox % f->nx) + f->nx) % f->nx;
    const int ey = ((f->oy % f->ny) + f->ny) % f->ny;
    const int ez = ((f->oz % f->nz) + f->nz) % f->nz;
    const int NX = f->nx, NY = f->ny, NZ = f->nz;
    const float *FNX = f->fnx, *FNY = f->fny, *FNZ = f->fnz, *FILL = f->fill;
    const unsigned char *HAVE = f->have;
    int *CUR = f->cur;

    for (int c = 0; c <= MAX_Q && c <= maxQ; c++) {
        while (f->bhead[c] != -1) {
            int i = f->bhead[c];
            /* settle it: out of the queue for good, its cost cannot improve past this bucket */
            f->bhead[c] = f->bnext[i];
            if (f->bnext[i] >= 0) f->bprev[f->bnext[i]] = -1;
            f->binb[i] = -1; f->bnext[i] = -1; f->bprev[i] = -1;
            reached++;

            int sx = i % NX, rr = i / NX, sz = rr % NZ, sy = rr / NZ;
            int rx = sx - ex; if (rx < 0) rx += NX;
            int ry = sy - ey; if (ry < 0) ry += NY;
            int rz = sz - ez; if (rz < 0) rz += NZ;
            const float mFromX = FNX[i], mFromY = FNY[i], mFromZ = FNZ[i];

            for (int dy = -1; dy <= 1; dy++) {
                int ry2 = ry + dy; if (ry2 < 0 || ry2 >= NY) continue;
                int sy2 = sy + dy; if (sy2 < 0) sy2 += NY; else if (sy2 >= NY) sy2 -= NY;
                for (int dz = -1; dz <= 1; dz++) {
                    int rz2 = rz + dz; if (rz2 < 0 || rz2 >= NZ) continue;
                    int sz2 = sz + dz; if (sz2 < 0) sz2 += NZ; else if (sz2 >= NZ) sz2 -= NZ;
                    long long rowBase = (long long)NX * ((long long)sz2 + (long long)NZ * sy2);
                    for (int dx = -1; dx <= 1; dx++) {
                        if (!dx && !dy && !dz) continue;
                        int rx2 = rx + dx; if (rx2 < 0 || rx2 >= NX) continue;
                        int sx2 = sx + dx; if (sx2 < 0) sx2 += NX; else if (sx2 >= NX) sx2 -= NX;
                        long long ni = rowBase + sx2;
                        /* UNREAD SPACE IS OPAQUE. An unresident chunk read as open sky is what made
                         * the old scene irreproducible; the same rule applies to geometry we simply
                         * have not got to yet. Not knowing is never the same as nothing being there. */
                        if (!HAVE[ni]) continue;

                        /* Crossing the faces between the two cells. A diagonal move crosses more
                         * than one, and charging all of them is what keeps a route from slipping
                         * through the seam where two walls meet -- the thin-slab leak the old model
                         * had a whole class of bugs around. Each face is named on its own cell's
                         * negative side, so which cell owns it depends on the direction of travel. */
                        float mat = FILL[ni];
                        if (dx > 0) mat += FNX[ni]; else if (dx < 0) mat += mFromX;
                        if (dy > 0) mat += FNY[ni]; else if (dy < 0) mat += mFromY;
                        if (dz > 0) mat += FNZ[ni]; else if (dz < 0) mat += mFromZ;

                        int nd = (dx ? 1 : 0) + (dy ? 1 : 0) + (dz ? 1 : 0);
                        int q = c + (nd == 1 ? qStep : nd == 2 ? qStep2 : qStep3)
                                  + (int)(mat * Q_PER_DB + 0.5f);
                        if (q > maxQ || q > MAX_Q) continue;
                        if (CUR[ni] <= q) continue;
                        CUR[ni] = q;
                        bq_set(f, ni, q);
                    }
                }
            }
        }
    }

    f->lastUs = now_us() - t0;
    f->reached = reached;
    return (int)reached;
}

/* Routed cost at a cell, in dB, or maxDb if it was never reached. */
static inline float cell_db(const cmm_fld *f, const int *fld, int cx, int cy, int cz) {
    if (!in_win(f, cx, cy, cz)) return f->maxDb;
    int v = fld[widx(f, cx, cy, cz)];
    if (v == UNREACHED) return f->maxDb;
    return (float)v / Q_PER_DB;
}

static float sample_field(cmm_fld *f, const int *fld, float x, float y, float z) {
    /* Cell CENTRES sit half a cell in, so the sample point maps to a fractional cell index and the
     * eight surrounding centres are blended. This is what makes a source moving through the world
     * produce a curve instead of a staircase, and it costs eight lookups. */
    float s = (float)f->step;
    float gx = x / s - 0.5f, gy = y / s - 0.5f, gz = z / s - 0.5f;
    int ix = (int)floorf(gx), iy = (int)floorf(gy), iz = (int)floorf(gz);
    float tx = gx - ix, ty = gy - iy, tz = gz - iz;

    float acc = 0.0f;
    for (int dy = 0; dy <= 1; dy++)
    for (int dz = 0; dz <= 1; dz++)
    for (int dx = 0; dx <= 1; dx++) {
        float w = (dx ? tx : 1.0f - tx) * (dy ? ty : 1.0f - ty) * (dz ? tz : 1.0f - tz);
        if (w <= 0.0f) continue;
        acc += w * cell_db(f, fld, ix + dx, iy + dy, iz + dz);
    }
    return acc;
}

/* The routed cost includes the detour charge for the whole path, so a source in clear air still
 * reads a few dB just for being far away — and distance is the MIXER's job, which already has
 * both positions. Take the straight-line charge back out and what is left is EXCESS: what the
 * building added. Clear line of sight comes out at zero by construction rather than by tuning. */
static float excess_db(cmm_fld *f, const int *fld, float lx, float ly, float lz,
                       float x, float y, float z) {
    float routed = sample_field(f, fld, x, y, z);
    float dx = x - lx, dy = y - ly, dz = z - lz;
    float direct = sqrtf(dx * dx + dy * dy + dz * dz) * f->dbPerBlock;
    float e = routed - direct;
    if (e < 0.0f) e = 0.0f;
    if (e > f->maxDb) e = f->maxDb;
    return e;
}

float cmm_fld_sample_db(cmm_fld *f, float x, float y, float z) {
    if (!f) return 0.0f;
    return excess_db(f, f->cur, f->clx, f->cly, f->clz, x, y, z);
}

float cmm_fld_sample_db_blended(cmm_fld *f, float x, float y, float z, float mix) {
    if (!f) return 0.0f;
    if (mix >= 1.0f) return cmm_fld_sample_db(f, x, y, z);
    if (mix < 0.0f) mix = 0.0f;
    float a = excess_db(f, f->cur,  f->clx, f->cly, f->clz, x, y, z);
    float b = excess_db(f, f->prev, f->clx, f->cly, f->clz, x, y, z);
    return b + (a - b) * mix;
}

long long cmm_fld_stat(cmm_fld *f, int what) {
    if (!f) return -1;
    switch (what) {
        case 0: return f->n;
        case 1: return f->pending;
        case 2: return f->reached;
        case 3: return f->lastUs;
        default: return -1;
    }
}
