/* ─────────────────────────────────────────────────────────────────────────
 * cmm_acoustics — the world-emitter acoustic model, in native memory
 * ─────────────────────────────────────────────────────────────────────────
 * A port of tools/CmmAcousticsJudge (portals.js + regions.js), which is where the
 * model was developed and where its acceptance gates live. Native for one reason:
 * the solve touches millions of voxels and hundreds of graph nodes per rebuild,
 * and Unity's non-generational Boehm GC cannot absorb that. Everything here is
 * owned by ONE arena allocated per scene, so the managed side allocates nothing
 * per frame and nothing per listener.
 *
 * WHY A PARITY HARNESS EXISTS AND MUST BE USED: the single most expensive mistake
 * of the offline work was two systems being compared when they were not the same
 * system -- the renderer evaluated the cell grid for days while every gate ran the
 * region graph, so weeks of green numbers described something nobody was hearing.
 * This port is therefore validated stage by stage against the JS, on the same
 * voxels (tools/CmmAcousticsJudge/export-grid.js writes them), and never judged
 * only by its final output.
 *
 * Build order, each stage diffed before the next is written:
 *   1. openness + thickness field          <- this file, so far
 *   2. watershed segmentation + merge (incl. the indoor/outdoor barrier)
 *   3. corridor split into nodes, portals, rooms with absorption
 *   4. per-emitter Dijkstra over portals, room-field solve
 *   5. per-listener evaluation: transmission disc, aperture arrivals, diffuse
 *
 * Thread rules: one scene is single-writer. Build on the scheduler thread, then
 * evaluate read-only from the audio/decision thread. No locks inside.
 * ───────────────────────────────────────────────────────────────────────── */
#ifndef CMM_ACOUSTICS_H
#define CMM_ACOUSTICS_H

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#define CMM_AC_API __declspec(dllexport)
#else
#define CMM_AC_API __attribute__((visibility("default")))
#endif

typedef struct cmm_ac cmm_ac;

/* ABI for the acoustics surface, independent of the decoder's. Bumped on any
 * breaking change; the managed side checks it on load.
 *   v1: scene create/destroy, voxel upload, openness + thickness. */
#define CMM_AC_ABI 1
CMM_AC_API int cmm_ac_abi(void);

/* Voxel flag bits, matching the acsweep dump so the managed uploader can pass
 * them through unchanged. Only AIR is consumed by the model. */
#define CMM_AC_AIR 1u

/* Create a scene over a voxel box. Returns NULL if the dimensions are absurd or
 * the arena cannot be allocated. Allocates ONCE: nothing here mallocs again. */
CMM_AC_API cmm_ac *cmm_ac_create(int sx, int sy, int sz, int x0, int y0, int z0);
CMM_AC_API void cmm_ac_destroy(cmm_ac *ac);

/* Upload the grid. loss is per-voxel CellLoss (0..1+, values above 1 clamp),
 * flags carries CMM_AC_AIR. Both arrays are sx*sy*sz, indexed x + sx*(z + sz*y)
 * -- the dump's own layout, kept so no reindexing is needed anywhere. Copies in,
 * so the caller's buffers can be reused or freed immediately. */
CMM_AC_API int cmm_ac_set_voxels(cmm_ac *ac, const float *loss, const unsigned char *flags);

/* Derive openness and the thickness field. Idempotent; safe to call again after
 * a voxel upload when geometry changes (a door, a broken wall). */
CMM_AC_API int cmm_ac_build_fields(cmm_ac *ac);

/* Read-back for parity checking against the JS model. Returns 0 and writes
 * nothing if the index is out of range. */
CMM_AC_API float cmm_ac_open_at(const cmm_ac *ac, int wx, int wy, int wz);
CMM_AC_API float cmm_ac_thick_at(const cmm_ac *ac, int wx, int wy, int wz);

/* Aggregate stats, so a parity run compares numbers rather than eyeballing grids.
 * `what` selects: 0 voxels, 1 open voxels (>OPEN_EPS), 2 fully-open voxels,
 * 3 voxels at the thickness cap, 4 arena bytes. */
CMM_AC_API long long cmm_ac_stat(const cmm_ac *ac, int what);
/* Mean thickness over open voxels — a single number that catches a sweep-order
 * or boundary-handling divergence immediately. */
CMM_AC_API double cmm_ac_mean_thick(const cmm_ac *ac);

#ifdef __cplusplus
}
#endif
#endif /* CMM_ACOUSTICS_H */
