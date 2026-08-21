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
/* Radius in BLOCKS of the seed sphere around the listener. Chosen by measuring the worst step in
 * excess for a hundredth-of-a-block of listener movement (test_field: "listener moving"); see the
 * seeding block in cmm_fld_solve for why a sphere and not the surrounding eight cells. */
#ifndef CMM_SEED_R
#define CMM_SEED_R 6.0f
#endif
#define MAX_Q      (120 * Q_PER_DB)      /* past this a cell is silent; the flood stops growing */
#define UNREACHED  0x7fffffff

struct cmm_fld {
    int nx, ny, nz;
    /* CELLS ARE NOT CUBES (v3). They are stepX x stepY x stepZ BLOCKS, and stepY is meant to be
     * smaller than the other two -- 1, in practice. Buildings are not isotropic: a wall spans a
     * cell's full height, so a coarse cell represents it perfectly, but a FLOOR is one block thick
     * with a storey of air above it, and a 3-block-tall cell cannot represent that at all. Worse
     * than reporting "one third solid" for the layer you stand in: a source standing on a floor
     * lands in a cell whose Y MIDDLE PLANE IS THE FLOOR, so reaching it from above is charged as
     * going through the floor.
     *
     * Measured before the fix, in FLAT OPEN AIR with nothing between listener and source. The only
     * variable is where the ground's top block fell relative to the cell grid:
     *
     *     ground top y=20  ->   0.00 dB excess
     *     ground top y=21  ->   1.09 dB
     *     ground top y=22  ->  29.42 dB       <-- open air, 29 dB down
     *     ground top y=23  ->   0.00 dB
     *
     * The same for every face rule (MAX gave 26.67), so it was never the face rule, the wall curve
     * or the detour cost. It was the cell being taller than the thing it had to represent. An
     * earlier probe read 0.00 across the board because it happened to pick an aligned ground
     * height; real terrain aligns to nothing.
     *
     * One-block-tall cells cost NOTHING in geometry reads -- those are proportional to the BLOCK
     * volume, which is unchanged -- and triple only the flood, which was 7 ms. */
    int stepX, stepY, stepZ;
    long long n;
    int ox, oy, oz;                 /* world CELL coords of the window's minimum corner */

    /* Geometry, indexed toroidally. Transmission through the face shared with the -X/-Y/-Z
     * neighbour, as dB (0 = open). Kept in dB rather than as the linear fraction the setter takes
     * because the flood adds it, and converting once on the way in beats a log per relaxation. */
    float *fnx, *fny, *fnz;
    /* v2 — the interior obstruction is DIRECTIONAL. One number for "how solid is this cell" made
     * sound travelling OVER a floor be attenuated BY the floor: a cell containing the ground is a
     * third solid, so every horizontal step near the ground paid for it, and the open air outdoors
     * came out 3.2 dB down for no reason a player could see. What matters is what stands across
     * the route, so each axis stores the obstruction of the cell's MIDDLE PLANE perpendicular to
     * it -- a floor blocks vertical travel through the cell and not horizontal, which is what a
     * floor does. */
    float *fillX, *fillY, *fillZ;
    unsigned char *have;            /* 0 = geometry still pending: treated as opaque, never open */

    /* Flood results. `cur` is the field just solved, `prev` the one before, so the caller can
     * crossfade across a re-solve instead of stepping. */
    /* THREE buffers, not two. Geometry is read on the game's MAIN thread (chunk access is not
     * safe anywhere else) while the flood runs on the scheduler thread, so the flood must never
     * write the field anyone is sampling: it fills `nxt` and only rotates it in if the window did
     * not move under it. A recentre changes what every index MEANS, so a flood that raced one is
     * discarded rather than published -- 20 ms of wasted work, against a field that would
     * otherwise be indexed against an origin it was not built for. */
    int *cur, *prev, *nxt;
    /* The MATERIAL-only cost along the same winning route, carried alongside. Excess on its own
     * cannot tell you whether a speaker is quiet because of walls or because the route wandered,
     * and those want opposite fixes -- so the field reports both rather than leaving it to be
     * argued about. Follows the winner: whenever a cell's total improves, its material total is
     * overwritten from the same predecessor. */
    int *curMat, *nxtMat;
    float clx, cly, clz;            /* listener the CURRENT field was flooded from */
    float nlx, nly, nlz;            /* ...and the one `nxt` is being built from */
    unsigned gen;                   /* bumped by recentre; a flood carries the one it started on */

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

/* Block coordinate -> cell index on one axis. Floor division, because block -1 belongs to cell -1
 * and C's / truncates toward zero. */
static inline int b2c(int b, int s) { return (b >= 0 ? b : b - s + 1) / s; }

int cmm_fld_abi(void) { return CMM_FLD_ABI; }

cmm_fld *cmm_fld_create(int nx, int ny, int nz, int stepX, int stepY, int stepZ) {
    if (nx <= 0 || ny <= 0 || nz <= 0 || stepX < 1 || stepY < 1 || stepZ < 1) return NULL;
    cmm_fld *f = (cmm_fld *)calloc(1, sizeof(cmm_fld));
    if (!f) return NULL;
    f->nx = nx; f->ny = ny; f->nz = nz;
    f->stepX = stepX; f->stepY = stepY; f->stepZ = stepZ;
    f->n = (long long)nx * ny * nz;
    f->dbPerBlock = 0.35f;                     /* what a block of DETOUR costs */
    f->maxDb = 60.0f;
    f->fnx  = (float *)malloc(sizeof(float) * f->n);
    f->fny  = (float *)malloc(sizeof(float) * f->n);
    f->fnz  = (float *)malloc(sizeof(float) * f->n);
    f->fillX = (float *)malloc(sizeof(float) * f->n);
    f->fillY = (float *)malloc(sizeof(float) * f->n);
    f->fillZ = (float *)malloc(sizeof(float) * f->n);
    f->have = (unsigned char *)calloc(f->n, 1);
    f->cur  = (int *)malloc(sizeof(int) * f->n);
    f->prev = (int *)malloc(sizeof(int) * f->n);
    f->nxt  = (int *)malloc(sizeof(int) * f->n);
    f->curMat = (int *)malloc(sizeof(int) * f->n);
    f->nxtMat = (int *)malloc(sizeof(int) * f->n);
    f->bnext= (int *)malloc(sizeof(int) * f->n);
    f->bprev= (int *)malloc(sizeof(int) * f->n);
    f->binb = (int *)malloc(sizeof(int) * f->n);
    f->bhead= (int *)malloc(sizeof(int) * (MAX_Q + 1));
    if (!f->fnx || !f->fny || !f->fnz || !f->fillX || !f->fillY || !f->fillZ || !f->have || !f->cur || !f->prev || !f->nxt || !f->curMat || !f->nxtMat
        || !f->bnext || !f->bprev || !f->binb || !f->bhead) { cmm_fld_destroy(f); return NULL; }
    for (long long i = 0; i < f->n; i++) { f->cur[i] = UNREACHED; f->prev[i] = UNREACHED; f->nxt[i] = UNREACHED; }

    f->order = (short *)malloc(sizeof(short) * 3 * f->n);
    if (!f->order) { cmm_fld_destroy(f); return NULL; }
    {
        /* Bucket by squared distance rather than qsort: the values are small integers and this is
         * a one-off, but an O(n log n) sort of a quarter-million entries at scene creation is a
         * visible hitch and a counting sort is three passes of arithmetic. */
        int mx = nx / 2, my = ny / 2, mz = nz / 2;
        /* Nearest-first has to mean nearest in BLOCKS, not in cells. With a short stepY a cell one
         * step up is much closer than one step sideways, and ordering by cell index would read a
         * tall thin column before the ground the player is standing on. */
        int rx = mx * stepX, ry = my * stepY, rz = mz * stepZ;
        int maxd2 = rx * rx + ry * ry + rz * rz + 1;
        int *cnt = (int *)calloc(maxd2 + 1, sizeof(int));
        if (!cnt) { cmm_fld_destroy(f); return NULL; }
        for (int y = -my; y < ny - my; y++)
        for (int z = -mz; z < nz - mz; z++)
        for (int x = -mx; x < nx - mx; x++) {
            int bxx = x * stepX, byy = y * stepY, bzz = z * stepZ;
            int d2 = bxx * bxx + byy * byy + bzz * bzz; if (d2 > maxd2) d2 = maxd2;
            cnt[d2]++;
        }
        for (int i = 1; i <= maxd2; i++) cnt[i] += cnt[i - 1];
        for (int i = maxd2; i > 0; i--) cnt[i] = cnt[i - 1];
        cnt[0] = 0;
        for (int y = -my; y < ny - my; y++)
        for (int z = -mz; z < nz - mz; z++)
        for (int x = -mx; x < nx - mx; x++) {
            int bxx = x * stepX, byy = y * stepY, bzz = z * stepZ;
            int d2 = bxx * bxx + byy * byy + bzz * bzz; if (d2 > maxd2) d2 = maxd2;
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
    free(f->fnx); free(f->fny); free(f->fnz);
    free(f->fillX); free(f->fillY); free(f->fillZ); free(f->have);
    free(f->cur); free(f->prev); free(f->nxt);
    free(f->curMat); free(f->nxtMat); free(f->bnext); free(f->bprev); free(f->binb); free(f->bhead);
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
    int cx = b2c(bx, f->stepX), cy = b2c(by, f->stepY), cz = b2c(bz, f->stepZ);
    int nox = cx - f->nx / 2, noy = cy - f->ny / 2, noz = cz - f->nz / 2;
    if (nox == f->ox && noy == f->oy && noz == f->oz) return (int)f->pending;

    /* Only what LEFT the old window is unknown. Everything still covered keeps its geometry, which
     * is the point of the toroidal layout: walking costs a slab, never the window. */
    int oox = f->ox, ooy = f->oy, ooz = f->oz;
    int first = (f->pending == f->n);          /* nothing has ever been read: no old window at all */
    f->ox = nox; f->oy = noy; f->oz = noz;
    f->gen++;                       /* every index now means something else: see the buffer note */
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
                      float tnx, float tny, float tnz,
                      float midX, float midY, float midZ) {
    if (!f || !in_win(f, cx, cy, cz)) return;
    long long i = widx(f, cx, cy, cz);
    f->fnx[i]   = t_db(tnx);
    f->fny[i]   = t_db(tny);
    f->fnz[i]   = t_db(tnz);
    /* mid* are TRANSMISSIONS through the cell's middle plane perpendicular to each axis, in the
     * same units as the faces: 1 = you can walk through it, 0 = solid. */
    f->fillX[i] = t_db(midX);
    f->fillY[i] = t_db(midY);
    f->fillZ[i] = t_db(midZ);
    if (!f->have[i]) { f->have[i] = 1; if (f->pending > 0) f->pending--; }
}

void cmm_fld_dirty(cmm_fld *f, int bx, int by, int bz, int radiusBlocks) {
    if (!f) return;
    if (radiusBlocks < 0) radiusBlocks = 0;
    int x0 = bx - (radiusBlocks + f->stepX), x1 = bx + (radiusBlocks + f->stepX);
    int y0 = by - (radiusBlocks + f->stepY), y1 = by + (radiusBlocks + f->stepY);
    int z0 = bz - (radiusBlocks + f->stepZ), z1 = bz + (radiusBlocks + f->stepZ);
    for (int by2 = y0; by2 <= y1; by2 += f->stepY)
    for (int bz2 = z0; bz2 <= z1; bz2 += f->stepZ)
    for (int bx2 = x0; bx2 <= x1; bx2 += f->stepX) {
        int cx = b2c(bx2, f->stepX), cy = b2c(by2, f->stepY), cz = b2c(bz2, f->stepZ);
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
    /* The window's identity when this flood started. If a recentre lands while it runs, every
     * index it has written means something else and the result is thrown away rather than
     * published — see the buffer note on the struct. */
    unsigned gen0 = f->gen;

    /* The field just solved becomes the one to crossfade FROM. */
    { int *sw = f->prev; f->prev = f->cur; f->cur = sw; }
    /* Clear the buffer being BUILT, not the one being sampled. `cur` is live: the audio layer
     * reads it while this runs. And binb must be reset every flood -- it is the "which bucket is
     * this cell in" index, and stale entries make bq_set unlink from lists that no longer exist,
     * which corrupts the queue and silently strands the flood one cell from its seed. */
    for (long long i = 0; i < f->n; i++) { f->nxt[i] = UNREACHED; f->nxtMat[i] = 0; f->binb[i] = -1; }
    for (int c = 0; c <= MAX_Q; c++) f->bhead[c] = -1;

    f->nlx = lx; f->nly = ly; f->nlz = lz;
    int bx = (int)floorf(lx), by = (int)floorf(ly), bz = (int)floorf(lz);
    int scx = b2c(bx, f->stepX), scy = b2c(by, f->stepY), scz = b2c(bz, f->stepZ);
    if (!in_win(f, scx, scy, scz)) { f->reached = 0; return 0; }

    /* SEED THE EIGHT CELLS AROUND THE LISTENER, NOT THE ONE IT STANDS IN.
     *
     * The source end of this model has always been continuous: cmm_fld_sample_db interpolates
     * across the eight cells surrounding the source, so a source walking toward you gets a curve
     * and not a staircase. The LISTENER end had nothing of the kind. It picked the single cell
     * containing the ear -- a nearest-neighbour lookup -- so the instant the ear crossed a cell
     * boundary the flood started somewhere else and the WHOLE field changed at once.
     *
     * The ear is cameraTransform.position, which moves when the player turns (the camera is offset
     * from the pivot it rotates about) and drifts with head bob while standing still. So the field
     * could jump without the player going anywhere, which is "just turning around and not moving
     * causes dips too". No amount of smoothing downstream fixes a discontinuous seed; it only
     * turns a jump into a slow wander.
     *
     * Each of the eight starts at what it costs to GET there in a straight line -- its distance
     * from the true listener position, at the detour rate. No special case for the cell the ear is
     * actually in: giving that one zero would put a step back at every boundary. The result is
     * continuous in the listener's sub-cell position, and open air still comes out at exactly zero
     * excess, because the routed cost through the best-placed seed is the straight-line cost that
     * excess_db subtracts back off.
     *
     * Cells whose geometry is still pending are skipped, the same rule the flood uses: not knowing
     * is never the same as nothing being there. If that leaves nothing seeded -- the ear is in
     * unread space -- fall back to the containing cell so the flood is never empty. */
    {
        /* THE LISTENER IS SPREAD ACROSS THE EIGHT CELLS AROUND IT, BY THE SAME WEIGHTS THE SOURCE
         * END USES. This is the third attempt and the first that is both continuous and sound, so
         * the two that failed are worth recording:
         *
         *   ONE CELL (the original). A nearest-neighbour lookup. The instant the ear crossed a
         *   cell boundary the flood started somewhere else and the whole field moved. The ear is
         *   cameraTransform.position, which shifts when the player TURNS -- the camera is offset
         *   from the pivot it rotates about -- so the level could jump with the player standing
         *   still. That is "just turning around and not moving causes dips too".
         *
         *   THE EIGHT SURROUNDING CELLS, each at its straight-line cost. Continuous in each seed's
         *   COST, but not in the seed SET: crossing a boundary drops one cell and adds another,
         *   and if the one that left was the gateway to a good route the field still moved. 1.4 dB
         *   from four hundredths of a block.
         *
         *   A SPHERE of radius R around the ear, which fixes the set problem and measured well
         *   (0.11 dB at R=12) -- and is wrong. A seed is a claim that sound reaches that cell over
         *   a clear straight line, and at twelve blocks that line goes through walls. It would
         *   have made every wall leak, and it would have measured beautifully doing it.
         *
         * What actually works is to notice that the eight weights ALREADY go to zero at the set
         * boundary -- that is what makes trilinear interpolation continuous at the source end --
         * and to charge each seed for its weight, as if it were an aperture: a cell holding
         * fraction w of the listener costs -10*log10(w) dB to couple into. A cell entering or
         * leaving does so at w = 0, which is an infinite charge, so it contributes nothing at the
         * moment it appears and its influence rises smoothly from there. Nothing is seeded further
         * than one cell away, so no seed claims a line it does not have.
         *
         * Normalised by the LARGEST weight rather than used raw: with the ear at a cell corner all
         * eight weights are 1/8, and an un-normalised charge would put 9 dB of coupling loss on
         * every one of them -- open air would read 9 dB down for standing in the wrong place. The
         * best-placed seed always costs zero to couple into; the others pay for being off to one
         * side of it. */
        float gx = lx / (float)f->stepX - 0.5f;
        float gy = ly / (float)f->stepY - 0.5f;
        float gz = lz / (float)f->stepZ - 0.5f;
        int ix = (int)floorf(gx), iy = (int)floorf(gy), iz = (int)floorf(gz);
        float tx = gx - ix, ty = gy - iy, tz = gz - iz;
        float w[8]; long long idx[8]; float dst[8];
        int n = 0, ddx, ddy, ddz;
        float wmax = 0.0f;
        for (ddy = 0; ddy <= 1; ddy++)
        for (ddz = 0; ddz <= 1; ddz++)
        for (ddx = 0; ddx <= 1; ddx++) {
            int cx = ix + ddx, cy = iy + ddy, cz = iz + ddz;
            if (!in_win(f, cx, cy, cz)) continue;
            long long i = widx(f, cx, cy, cz);
            if (!f->have[i]) continue;               /* unread is opaque, never a seed */
            float ww = (ddx ? tx : 1.0f - tx) * (ddy ? ty : 1.0f - ty) * (ddz ? tz : 1.0f - tz);
            if (ww <= 0.0f) continue;
            float ex2 = (cx + 0.5f) * f->stepX - lx;
            float ey2 = (cy + 0.5f) * f->stepY - ly;
            float ez2 = (cz + 0.5f) * f->stepZ - lz;
            w[n] = ww; idx[n] = i;
            dst[n] = sqrtf(ex2 * ex2 + ey2 * ey2 + ez2 * ez2);
            if (ww > wmax) wmax = ww;
            n++;
        }
        int seeded = 0, s;
        for (s = 0; s < n; s++) {
            float rel = w[s] / wmax;                 /* 1 for the best-placed seed */
            float pen = -10.0f * log10f(rel);        /* couple-in loss, 0 dB at rel = 1 */
            if (pen > 40.0f) continue;               /* far enough into the corner to not matter */
            int q = (int)((dst[s] * f->dbPerBlock + pen) * Q_PER_DB + 0.5f);
            if (q > MAX_Q) continue;
            if (f->nxt[idx[s]] <= q) continue;
            f->nxt[idx[s]] = q; f->nxtMat[idx[s]] = 0; bq_set(f, idx[s], q);
            seeded++;
        }
        if (!seeded) {
            long long si = widx(f, scx, scy, scz);
            f->nxt[si] = 0; f->nxtMat[si] = 0; bq_set(f, si, 0);
        }
    }

    /* THE COST OF EACH OF THE 26 MOVES, in quarter-dB, precomputed. With cubic cells there were
     * only three distinct lengths (1, sqrt2, sqrt3 steps). With stepY != stepX the length depends
     * on WHICH axes a move uses, so going up costs stepY blocks of detour where going sideways
     * costs stepX. Charging the true Euclidean length is what stops the flood preferring staircase
     * routes through corners -- and with short cells, getting it wrong would make vertical travel
     * look nearly free and send every route climbing. */
    int qmove[3][3][3];
    for (int ax = -1; ax <= 1; ax++)
    for (int ay = -1; ay <= 1; ay++)
    for (int az = -1; az <= 1; az++) {
        float ddx = (float)(ax * f->stepX);
        float ddy = (float)(ay * f->stepY);
        float ddz = (float)(az * f->stepZ);
        float len = sqrtf(ddx * ddx + ddy * ddy + ddz * ddz);
        qmove[ax + 1][ay + 1][az + 1] = (int)(len * f->dbPerBlock * Q_PER_DB + 0.5f);
    }
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
    const float *FNX = f->fnx, *FNY = f->fny, *FNZ = f->fnz;
    const float *FLX = f->fillX, *FLY = f->fillY, *FLZ = f->fillZ;
    const unsigned char *HAVE = f->have;
    int *CUR = f->nxt;

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
                        float mat = 0.0f;
                        if (dx > 0) mat += FNX[ni]; else if (dx < 0) mat += mFromX;
                        if (dy > 0) mat += FNY[ni]; else if (dy < 0) mat += mFromY;
                        if (dz > 0) mat += FNZ[ni]; else if (dz < 0) mat += mFromZ;
                        /* ...and whatever stands ACROSS the route inside the cell being entered.
                         * Averaged over the axes the step actually moves along, so a diagonal is
                         * charged for the planes it genuinely crosses rather than for all three. */
                        { float fl = 0.0f; int fn = 0;
                          if (dx) { fl += FLX[ni]; fn++; }
                          if (dy) { fl += FLY[ni]; fn++; }
                          if (dz) { fl += FLZ[ni]; fn++; }
                          if (fn) mat += fl / fn; }

                        int q = c + qmove[dx + 1][dy + 1][dz + 1]
                                  + (int)(mat * Q_PER_DB + 0.5f);
                        if (q > maxQ || q > MAX_Q) continue;
                        if (CUR[ni] <= q) continue;
                        CUR[ni] = q;
                        f->nxtMat[ni] = f->nxtMat[i] + (int)(mat * Q_PER_DB + 0.5f);
                        bq_set(f, ni, q);
                    }
                }
            }
        }
    }

    f->lastUs = now_us() - t0;
    /* THE WINDOW MOVED UNDER US: throw away THIS flood, not the last good one.
     *
     * Zeroing `reached` here also marked the field unavailable, and the caller reads that as "no
     * field yet" and falls back to a different occlusion model for the next 300 ms -- an audible
     * volume step every time the listener happened to cross a cell boundary mid-flood. That is
     * pure loss: `cur` is exactly as valid as it was a moment ago. A recentre keeps every cell
     * still inside the window in the same toroidal slot, so the published field remains correct
     * for all of them; only the thin slab that just scrolled in carries its previous occupant's
     * value, and that slab is at the far edge of the window, past anything audible. Stale at the
     * rim for one flood interval beats silent everywhere. */
    if (f->gen != gen0) return -1;
    /* Publish: the field just solved becomes current, the previous one stays available for the
     * caller's crossfade, and the oldest buffer is recycled for the next flood. */
    { int *old = f->prev; f->prev = f->cur; f->cur = f->nxt; f->nxt = old; }
    { int *om = f->curMat; f->curMat = f->nxtMat; f->nxtMat = om; }
    f->clx = f->nlx; f->cly = f->nly; f->clz = f->nlz;
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
    float gx = x / (float)f->stepX - 0.5f;
    float gy = y / (float)f->stepY - 0.5f;
    float gz = z / (float)f->stepZ - 0.5f;
    int ix = (int)floorf(gx), iy = (int)floorf(gy), iz = (int)floorf(gz);
    float tx = gx - ix, ty = gy - iy, tz = gz - iz;

    /* BLEND ONLY WHAT THE FLOOD ACTUALLY REACHED, and renormalise.
     *
     * An unreached cell does not mean "very quiet here" -- it means no route was found to it, and
     * it carries maxDb as a placeholder. Blending that placeholder with real neighbours by plain
     * trilinear weights produces a number that is a route to nowhere: measured in game, a speaker
     * 6.7 blocks away flipped between 6 dB and 55 dB of excess at a FIXED listener position,
     * because a speaker is itself a solid block and the cells hugging it are frequently unreached.
     * A 50 dB step from a rounding boundary is exactly the class of defect this rebuild exists to
     * remove, so the interpolation now weighs only cells with an answer. If none of the eight has
     * one, the position really is unreachable and maxDb is the honest reply. */
    float acc = 0.0f, wsum = 0.0f;
    for (int dy = 0; dy <= 1; dy++)
    for (int dz = 0; dz <= 1; dz++)
    for (int dx = 0; dx <= 1; dx++) {
        float w = (dx ? tx : 1.0f - tx) * (dy ? ty : 1.0f - ty) * (dz ? tz : 1.0f - tz);
        if (w <= 0.0f) continue;
        int cx = ix + dx, cy = iy + dy, cz = iz + dz;
        if (!in_win(f, cx, cy, cz)) continue;
        int v = fld[widx(f, cx, cy, cz)];
        if (v == UNREACHED) continue;
        acc += w * ((float)v / Q_PER_DB);
        wsum += w;
    }
    return wsum > 1e-6f ? acc / wsum : f->maxDb;
}

/* The routed cost includes the detour charge for the whole path, so a source in clear air still
 * reads a few dB just for being far away — and distance is the MIXER's job, which already has
 * both positions. Take the straight-line charge back out and what is left is EXCESS: what the
 * building added. Clear line of sight comes out at zero by construction rather than by tuning. */
/* WHAT THE SAME MEASUREMENT WOULD HAVE READ IN OPEN AIR.
 *
 * The obvious reference is the straight line, |L-S| * dbPerBlock -- and it was, until the listener
 * was spread over the eight surrounding cells to stop the field jumping when the player turns. A
 * route now starts at a cell CENTRE and pays a coupling charge to get there, so even with nothing
 * in the way it costs a little more than the straight line: measured, up to 0.9 dB of excess in
 * open air, which is a permanent quiet with no cause a player can see. That is the same class of
 * defect as the 29 dB one, just small enough to argue about.
 *
 * So the reference is not a straight line but the SAME SEEDING with nothing in the way: the best
 * of the eight seeds, each paying its real coupling charge and then travelling free. Open air
 * comes out at exactly zero by construction rather than by tuning, and every term that is an
 * artifact of how the listener is spread cancels instead of being carried. Mirrors the seeding in
 * cmm_fld_solve exactly -- if one changes, so must the other. */
static float free_space_db(cmm_fld *f, float lx, float ly, float lz, float x, float y, float z) {
    float gx = lx / (float)f->stepX - 0.5f;
    float gy = ly / (float)f->stepY - 0.5f;
    float gz = lz / (float)f->stepZ - 0.5f;
    int ix = (int)floorf(gx), iy = (int)floorf(gy), iz = (int)floorf(gz);
    float tx = gx - ix, ty = gy - iy, tz = gz - iz;
    float wmax = 0.0f, best = 1e30f;
    int ddx, ddy, ddz;
    for (ddy = 0; ddy <= 1; ddy++)
    for (ddz = 0; ddz <= 1; ddz++)
    for (ddx = 0; ddx <= 1; ddx++) {
        float ww = (ddx ? tx : 1.0f - tx) * (ddy ? ty : 1.0f - ty) * (ddz ? tz : 1.0f - tz);
        if (ww > wmax) wmax = ww;
    }
    if (wmax <= 0.0f) return 0.0f;
    for (ddy = 0; ddy <= 1; ddy++)
    for (ddz = 0; ddz <= 1; ddz++)
    for (ddx = 0; ddx <= 1; ddx++) {
        float ww = (ddx ? tx : 1.0f - tx) * (ddy ? ty : 1.0f - ty) * (ddz ? tz : 1.0f - tz);
        if (ww <= 0.0f) continue;
        float pen = -10.0f * log10f(ww / wmax);
        if (pen > 40.0f) continue;
        float cxw = (ix + ddx + 0.5f) * f->stepX;
        float cyw = (iy + ddy + 0.5f) * f->stepY;
        float czw = (iz + ddz + 0.5f) * f->stepZ;
        float ax = cxw - lx, ay = cyw - ly, az = czw - lz;
        float bx2 = x - cxw, by2 = y - cyw, bz2 = z - czw;
        float cost = sqrtf(ax*ax + ay*ay + az*az) * f->dbPerBlock + pen
                   + sqrtf(bx2*bx2 + by2*by2 + bz2*bz2) * f->dbPerBlock;
        if (cost < best) best = cost;
    }
    return best < 1e29f ? best : 0.0f;
}

static float excess_db(cmm_fld *f, const int *fld, float lx, float ly, float lz,
                       float x, float y, float z) {
    float routed = sample_field(f, fld, x, y, z);
    float direct = free_space_db(f, lx, ly, lz, x, y, z);
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

/* The MATERIAL half of the excess at a world position: how much of it is walls rather than the
 * route going the long way round. Same interpolation as the total, over the same cells. */
CMM_FLD_API float cmm_fld_sample_mat_db(cmm_fld *f, float x, float y, float z) {
    if (!f) return 0.0f;
    float gx = x / (float)f->stepX - 0.5f;
    float gy = y / (float)f->stepY - 0.5f;
    float gz = z / (float)f->stepZ - 0.5f;
    int ix = (int)floorf(gx), iy = (int)floorf(gy), iz = (int)floorf(gz);
    float tx = gx - ix, ty = gy - iy, tz = gz - iz;
    float acc = 0.0f, wsum = 0.0f;
    for (int dy = 0; dy <= 1; dy++)
    for (int dz = 0; dz <= 1; dz++)
    for (int dx = 0; dx <= 1; dx++) {
        float w = (dx ? tx : 1.0f - tx) * (dy ? ty : 1.0f - ty) * (dz ? tz : 1.0f - tz);
        if (w <= 0.0f) continue;
        int cx = ix + dx, cy = iy + dy, cz = iz + dz;
        if (!in_win(f, cx, cy, cz)) continue;
        long long i = widx(f, cx, cy, cz);
        if (f->cur[i] == UNREACHED) continue;
        acc += w * ((float)f->curMat[i] / Q_PER_DB);
        wsum += w;
    }
    return wsum > 1e-6f ? acc / wsum : 0.0f;
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
