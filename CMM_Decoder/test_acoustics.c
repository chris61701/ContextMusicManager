/* ─────────────────────────────────────────────────────────────────────────
 * test_acoustics — parity harness for the acoustics port
 * ─────────────────────────────────────────────────────────────────────────
 * Reads a grid written by tools/CmmAcousticsJudge/export-grid.js, builds the
 * native fields, and prints the same aggregate numbers the JS side prints, so a
 * divergence shows up as a number rather than as a mystery three stages later.
 *
 * Build:  gcc -O2 -o test_acoustics.exe test_acoustics.c cmm_acoustics.c -lm
 * Run:    ./test_acoustics.exe grids/store_electronics_02.755.bin
 * ───────────────────────────────────────────────────────────────────────── */
#include "cmm_acoustics.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

int main(int argc, char **argv) {
    if (argc < 2) { fprintf(stderr, "usage: %s <grid.bin>\n", argv[0]); return 2; }
    FILE *f = fopen(argv[1], "rb");
    if (!f) { fprintf(stderr, "cannot open %s\n", argv[1]); return 2; }

    char magic[8];
    int hdr[6];
    if (fread(magic, 1, 8, f) != 8 || memcmp(magic, "CMMAC1", 6) != 0) {
        fprintf(stderr, "not a CMMAC1 grid\n"); fclose(f); return 2;
    }
    if (fread(hdr, sizeof(int), 6, f) != 6) { fprintf(stderr, "short header\n"); fclose(f); return 2; }
    int sx = hdr[0], sy = hdr[1], sz = hdr[2], x0 = hdr[3], y0 = hdr[4], z0 = hdr[5];
    long long n = (long long)sx * sy * sz;

    float *loss = (float *)malloc((size_t)n * sizeof(float));
    unsigned char *flags = (unsigned char *)malloc((size_t)n);
    if (!loss || !flags) { fprintf(stderr, "oom reading grid\n"); return 2; }
    if (fread(loss, sizeof(float), (size_t)n, f) != (size_t)n) { fprintf(stderr, "short loss\n"); return 2; }
    if (fread(flags, 1, (size_t)n, f) != (size_t)n) { fprintf(stderr, "short flags\n"); return 2; }
    fclose(f);

    printf("grid %dx%dx%d at (%d,%d,%d), %lld voxels   [abi %d]\n",
           sx, sy, sz, x0, y0, z0, n, cmm_ac_abi());

    cmm_ac *ac = cmm_ac_create(sx, sy, sz, x0, y0, z0);
    if (!ac) { fprintf(stderr, "create failed\n"); return 1; }
    if (!cmm_ac_set_voxels(ac, loss, flags)) { fprintf(stderr, "upload failed\n"); return 1; }

    clock_t t0 = clock();
    if (!cmm_ac_build_fields(ac)) { fprintf(stderr, "build failed\n"); return 1; }
    double ms = 1000.0 * (double)(clock() - t0) / CLOCKS_PER_SEC;

    long long open = cmm_ac_stat(ac, 1), full = cmm_ac_stat(ac, 2), capped = cmm_ac_stat(ac, 3);
    printf("open voxels      %lld  (%.1f%%)\n", open, 100.0 * (double)open / (double)n);
    printf("fully open       %lld  (%.1f%%)\n", full, 100.0 * (double)full / (double)n);
    printf("at thickness cap %lld  (%.1f%% of open)\n", capped,
           open ? 100.0 * (double)capped / (double)open : 0.0);
    printf("mean thickness   %.4f  (over open voxels)\n", cmm_ac_mean_thick(ac));
    printf("arena            %.1f MB\n", (double)cmm_ac_stat(ac, 4) / 1e6);
    printf("build            %.0f ms\n", ms);

    clock_t t1 = clock();
    if (!cmm_ac_build_segments(ac)) { fprintf(stderr, "segment failed\n"); return 1; }
    double ms2 = 1000.0 * (double)(clock() - t1) / CLOCKS_PER_SEC;
    printf("raw regions      %lld\n", cmm_ac_stat(ac, 5));
    printf("contacts         %lld\n", cmm_ac_stat(ac, 6));
    printf("merged rooms     %lld\n", cmm_ac_stat(ac, 7));
    printf("largest room     %lld voxels\n", cmm_ac_stat(ac, 8));
    printf("nodes            %lld\n", cmm_ac_stat(ac, 9));
    printf("node edges       %lld\n", cmm_ac_stat(ac, 10));
    printf("segment          %.0f ms\n", ms2);

    /* Routing solve from the first emitter listed alongside the grid, so the parity
     * check covers the source-dependent half too. */
    {
        char epath[1024];
        snprintf(epath, sizeof(epath), "%s", argv[1]);
        char *dot = strrchr(epath, '.');
        if (dot) strcpy(dot, ".emitters.txt");
        FILE *ef = fopen(epath, "r");
        int ex, ey, ez;
        if (ef && fscanf(ef, "%d %d %d", &ex, &ey, &ez) == 3) {
            clock_t t2 = clock();
            cmm_ac_src *src = cmm_ac_src_create(ac, (float)ex, (float)ey, (float)ez);
            double ms3 = 1000.0 * (double)(clock() - t2) / CLOCKS_PER_SEC;
            if (src) {
                printf("emitter          (%d,%d,%d)\n", ex, ey, ez);
                printf("seeded nodes     %lld\n", cmm_ac_src_stat(src, 1));
                printf("edges reached    %lld / %lld\n", cmm_ac_src_stat(src, 0), cmm_ac_stat(ac, 10));
                printf("solve            %.1f ms\n", ms3);
                clock_t t3 = clock();
                for (int k = 0; k < 20; k++) cmm_ac_src_move(src, (float)ex, (float)ey, (float)ez);
                double mv = 1000.0 * (double)(clock() - t3) / CLOCKS_PER_SEC;
                printf("move x20         %.1f ms  (%.2f ms each, allocation-free)\n", mv, mv / 20.0);
                /* Room fields, SORTED. Room numbering need not agree between the two
                 * implementations even when the structure does, so comparing sorted values
                 * tests the physics without depending on an id space. */
                int nr = (int)cmm_ac_stat(ac, 7);
                double *fv = (double *)malloc((size_t)(nr > 0 ? nr : 1) * sizeof(double));
                int nf = 0;
                for (int r = 0; r < nr; r++) {
                    double v = cmm_ac_src_field_db(src, r);
                    if (v > -900) fv[nf++] = v;
                }
                for (int a = 0; a < nf; a++) for (int b = a + 1; b < nf; b++)
                    if (fv[b] > fv[a]) { double tv = fv[a]; fv[a] = fv[b]; fv[b] = tv; }
                printf("room fields dB   %d with a field:", nf);
                for (int r = 0; r < nf && r < 8; r++) printf(" %.1f", fv[r]);
                printf("\n");
                free(fv);
                cmm_ac_src_destroy(src);
            }
        }
        if (ef) fclose(ef);
    }

    /* A few spot values, so a whole-grid statistic that happens to match cannot
     * hide a local divergence. Sampled on a coarse lattice for stability. */
    printf("spot open/thick:");
    int taken = 0;
    for (int y = sy / 2; y < sy && taken < 4; y += 7)
        for (int z = sz / 3; z < sz && taken < 4; z += 11)
            for (int x = sx / 3; x < sx && taken < 4; x += 13) {
                printf("  (%d,%d,%d)=%.3f/%.3f", x0 + x, y0 + y, z0 + z,
                       cmm_ac_open_at(ac, x0 + x, y0 + y, z0 + z),
                       cmm_ac_thick_at(ac, x0 + x, y0 + y, z0 + z));
                taken++;
            }
    printf("\n");

    cmm_ac_destroy(ac);
    free(loss); free(flags);
    return 0;
}
