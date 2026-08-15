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

/* ── TWO OBJECTS, AND THE SPLIT IS LOAD-BEARING ────────────────────────────
 * cmm_ac      A SCENE. Geometry only: voxels, openness, thickness, regions,
 *             portals, rooms, absorption. Contains nothing about any source and
 *             is rebuilt only when the WORLD changes -- a door swings, a wall
 *             breaks, a chunk loads. Expensive: voxel-sized work.
 * cmm_ac_src  A SOURCE bound to a scene. The routed solve from one position:
 *             Dijkstra over portals, the room-field solve, the candidate set.
 *             Cheap: graph-sized work, not voxel-sized. On the largest real
 *             capture that is 60 portals and 33 rooms, so microseconds.
 *
 * WHY IT IS SPLIT THIS WAY, stated now because retrofitting it later is the
 * expensive path. Speakers will move -- vehicle-mounted ones are a planned
 * multiplayer feature, and the same engine is intended for enemies and the wider
 * sound engine later. None of that is in scope now, but the shape has to admit it
 * or the shape has to be thrown away. So:
 *
 *   - A scene is SOURCE-INDEPENDENT. Moving a source must be a lookup against an
 *     unchanged scene, never a rebuild. This is the requirement MODEL.md records
 *     as structural, and it is the one the shipped ear-anchored flood violates --
 *     that design rebuilds its field around the LISTENER, which is why it cannot
 *     scale to many sources and why its cost grows with movement rather than with
 *     geometry.
 *   - cmm_ac_src_move() recomputes in place and ALLOCATES NOTHING. A source's
 *     arena is sized once at create from the scene's graph, so N moving sources
 *     cost N graph solves per tick and zero allocation -- which is what makes
 *     enemies affordable later without redesign.
 *   - Many sources may share one scene concurrently for reading. The scene is
 *     single-writer: rebuild it on the scheduler thread while no source solve is
 *     running against it.
 *
 * Tuning for moving sources is therefore a matter of how OFTEN move() is called
 * and how many sources exist, not of the model's structure. That is the property
 * being bought here.
 * ───────────────────────────────────────────────────────────────────────── */
typedef struct cmm_ac cmm_ac;
typedef struct cmm_ac_src cmm_ac_src;

/* ABI for the acoustics surface, independent of the decoder's. Bumped on any
 * breaking change; the managed side checks it on load.
 *   v1: scene create/destroy, voxel upload, openness + thickness. */
#define CMM_AC_ABI 3
CMM_AC_API int cmm_ac_abi(void);

/* Live tuning (ABI 2). Scene inputs: rebuild for a change to take full effect. A
 * non-positive argument leaves that parameter unchanged, so one can be turned alone. */
CMM_AC_API void cmm_ac_set_tuning(double db_mat, double mat_pow, double diffuse_cal);
CMM_AC_API void cmm_ac_get_tuning(double *db_mat, double *mat_pow, double *diffuse_cal);

/* Free-field level at a distance, in the model's own units (ABI 3). The reference eval_db must
 * be divided by, so the caller gets OCCLUSION rather than an absolute level carrying this
 * model's distance law. */
CMM_AC_API double cmm_ac_freefield_db(double dist);

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

/* Segment the scene: watershed regions, their contacts, and the merge that turns
 * them into rooms -- including the indoor/outdoor barrier, which no pinch test can
 * substitute for. Call after cmm_ac_build_fields. */
CMM_AC_API int cmm_ac_build_segments(cmm_ac *ac);

/* Read-back for parity checking against the JS model. Returns 0 and writes
 * nothing if the index is out of range. */
CMM_AC_API float cmm_ac_open_at(const cmm_ac *ac, int wx, int wy, int wz);
CMM_AC_API float cmm_ac_thick_at(const cmm_ac *ac, int wx, int wy, int wz);

/* Aggregate stats, so a parity run compares numbers rather than eyeballing grids.
 * `what` selects: 0 voxels, 1 open voxels (>OPEN_EPS), 2 fully-open voxels,
 * 3 voxels at the thickness cap, 4 arena bytes, 5 raw regions, 6 contacts,
 * 7 merged rooms, 8 voxels in the largest room, 9 routing nodes, 10 node edges. */
CMM_AC_API long long cmm_ac_stat(const cmm_ac *ac, int what);
/* Mean thickness over open voxels — a single number that catches a sweep-order
 * or boundary-handling divergence immediately. */
CMM_AC_API double cmm_ac_mean_thick(const cmm_ac *ac);

/* ── source solves (stage 4; declared now so the split is fixed) ───────────
 * create binds a source to a scene and allocates its arena from the scene's graph
 * size. move() re-solves in place for a new position and allocates nothing, so it
 * is the call a moving speaker -- or an enemy -- makes every tick it moves. A
 * scene rebuild invalidates every source bound to it: call cmm_ac_src_rebind()
 * after one, which resizes the arena only if the graph actually grew. */
CMM_AC_API cmm_ac_src *cmm_ac_src_create(cmm_ac *scene, float x, float y, float z);
CMM_AC_API void cmm_ac_src_destroy(cmm_ac_src *src);
CMM_AC_API int cmm_ac_src_move(cmm_ac_src *src, float x, float y, float z);
CMM_AC_API int cmm_ac_src_rebind(cmm_ac_src *src);

/* Parity read-back for the routing solve: 0 = edges reached, 1 = seeded nodes. */
CMM_AC_API long long cmm_ac_src_stat(const cmm_ac_src *src, int what);
CMM_AC_API double cmm_ac_src_cost(const cmm_ac_src *src, int edge);
CMM_AC_API double cmm_ac_src_field_db(const cmm_ac_src *src, int room);
/* voxels, surf, sky, open_area, absorption, canreverb, pin, direct, field dB */
CMM_AC_API int cmm_ac_src_room_info(const cmm_ac_src *src, int room, double *out, int nout);

/* Evaluate one listener against one source. Writes the arrival set the audio layer
 * needs -- level, direction, filter cutoff, reverb send -- into caller-owned
 * storage, so evaluation allocates nothing on either side of the boundary.
 * Returns the number of arrivals written, or -1 on error. */
typedef struct {
    float db;              /* level, dB relative to the source */
    float px, py, pz;      /* where it radiates FROM, for panning */
    float fc;              /* lowpass cutoff, Hz: a wall is a filter, not a fader */
    float len;             /* path length in blocks, for delay */
    float weight;          /* share, so one path counted once is not counted twice */
    int   kind;            /* 0 direct, 1 through a barrier, 2 aperture, 3 diffuse */
} cmm_ac_arrival;

CMM_AC_API int cmm_ac_eval(cmm_ac_src *src, float lx, float ly, float lz,
                           cmm_ac_arrival *out, int max_out);

/* The energy sum of every arrival: total level at a listener, dB relative to the source.
 * The convenience form for callers that want a number rather than a set. */
CMM_AC_API double cmm_ac_eval_db(cmm_ac_src *src, float lx, float ly, float lz);

#ifdef __cplusplus
}
#endif
#endif /* CMM_ACOUSTICS_H */
