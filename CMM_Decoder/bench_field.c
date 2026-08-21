/* Is a whole-field re-flood actually cheap enough to just redo, several times a second, forever?
 * The entire design rests on "nothing persists long enough to go stale", and that is only true if
 * rebuilding is cheaper than keeping. Measured at the real window size rather than assumed. */
#include "cmm_field.h"
#include <stdio.h>
#include <stdlib.h>
#include <time.h>

static void run(int SX, int SY, int SZ, int NX, int NY, int NZ) {
    printf("--- cells %dx%dx%d blocks, window %dx%dx%d ---\n", SX, SY, SZ, NX, NY, NZ);
    cmm_fld *f = cmm_fld_create(NX, NY, NZ, SX, SY, SZ);
    cmm_fld_recentre(f, 0, 40, 0);

    /* A POI-ish world: mostly open, with walls often enough to make the flood work for it. */
    unsigned s = 12345; int cx, cy, cz, filled = 0;
    while (cmm_fld_next_pending(f, &cx, &cy, &cz)) {
        s = s * 1664525u + 1013904223u;
        int wall = ((s >> 16) & 15) == 0;                    /* ~6% of cells are wall */
        float t = wall ? 0.02f : 1.0f;
        cmm_fld_set_cell(f, cx, cy, cz, t, 1.0f, t, t, 1.0f, t);
        filled++;
    }
    printf("cells %lld  filled %d\n", cmm_fld_stat(f, 0), filled);

    const int N = 40;
    clock_t t0 = clock();
    long long reached = 0;
    for (int i = 0; i < N; i++) reached += cmm_fld_solve(f, 0.5f + i * 0.1f, 40.5f, 0.5f);
    clock_t t1 = clock();
    double ms = 1000.0 * (double)(t1 - t0) / CLOCKS_PER_SEC / N;
    printf("solve: %.2f ms each, %lld cells reached avg\n", ms, reached / N);

    cmm_fld_destroy(f);
}

int main(void) {
    /* What shipped (cubic), against what ships now (wide and short over the same block volume).
     * The flood is the only thing that grows: geometry reads track the BLOCK volume, unchanged. */
    run(3, 3, 3, 45, 17, 45);
    run(3, 1, 3, 45, 51, 45);
    run(3, 3, 3, 67, 17, 67);
    run(3, 1, 3, 67, 51, 67);
    return 0;
}
