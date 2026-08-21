/* cmm_field — ONE occlusion field, centred on the listener, read by every source.
 * ---------------------------------------------------------------------------------------------
 * There is exactly one pair of ears in the game and there may be fifty things making noise, some
 * of them walking around. So nothing here is ever computed per source. A single field is flooded
 * outward from the listener a few times a second, and a source — a speaker, a zombie, anything
 * added later — finds out how muffled it is by looking up where it happens to be standing.
 * Attenuation is symmetric, so a field built from the listener answers for a source exactly as a
 * field built from the source would have.
 *
 * That is the whole design, and everything below follows from it:
 *
 *   MOVING SOURCES ARE FREE. A zombie two rooms away needs no work of its own; it reads a
 *   different cell of a field that already exists. Adding the hundredth source costs one lookup.
 *
 *   NOTHING PERSISTS LONG ENOUGH TO GO STALE. The field is rebuilt whole, several times a second,
 *   so a door that opens is simply in the next one. There is no patching, no "has the rebuild
 *   landed", no handover at the wrong moment, and no second model to fall back to — a class of
 *   bug that cost a week, deleted by being unable to occur rather than by being fixed.
 *
 *   COARSE WHERE THAT IS CHEAP, EXACT WHERE IT MATTERS. Cells are STEP blocks across, so the
 *   flood is tens of thousands of cells rather than millions, but what a cell REMEMBERS about its
 *   faces is measured from the real blocks underneath. Transmission across a face is summed as
 *   ENERGY over the sub-faces, so one gap in an otherwise solid face dominates it — which is what
 *   a doorway is, obtained for free instead of from separate aperture machinery.
 *
 *   THE WINDOW SCROLLS. Cell coordinates are toroidal, so walking never invalidates the field:
 *   only the thin slab that just came into range needs reading. Geometry is expensive to read and
 *   cheap to keep; the flood is cheap to redo and never kept.
 *
 * The field carries EXCESS attenuation only — what the walls and the detour add over a clear
 * straight line. Distance itself belongs to the mixer's falloff curve, which already has the
 * listener and the source positions. Mixing the two is what made the old numbers untunable.
 */
#ifndef CMM_FIELD_H
#define CMM_FIELD_H

#ifdef __cplusplus
extern "C" {
#endif

#ifdef _WIN32
#  define CMM_FLD_API __declspec(dllexport)
#else
#  define CMM_FLD_API __attribute__((visibility("default")))
#endif

/* Bumped on any breaking change; the managed side checks it on load. */
#define CMM_FLD_ABI 3
CMM_FLD_API int cmm_fld_abi(void);

typedef struct cmm_fld cmm_fld;

/* A window of nx*ny*nz cells, each stepX x stepY x stepZ BLOCKS. Each step must be >= 1.
 *
 * CELLS ARE NOT CUBES, and that is the point (v3). stepY is meant to be SHORT -- 1 in practice --
 * because buildings are not isotropic. A wall spans a cell's full height, so a coarse cell
 * represents it exactly; a FLOOR is one block thick with a storey of air above it, and a 3-block
 * cell cannot represent that at all. A source standing on a floor lands in a cell whose Y middle
 * plane IS the floor, so reaching it from above gets charged as going through the floor: measured
 * at 29 dB of excess in FLAT OPEN AIR, varying between 0 and 29 dB purely with where the ground's
 * top block fell relative to the cell grid.
 *
 * Short cells cost nothing in geometry reads -- those track the BLOCK volume, which is unchanged
 * -- and multiply only the flood. */
CMM_FLD_API cmm_fld *cmm_fld_create(int nx, int ny, int nz, int stepX, int stepY, int stepZ);
CMM_FLD_API void     cmm_fld_destroy(cmm_fld *f);

/* Centre the window on a block position. Cells that scrolled out of range are marked needing
 * geometry; everything still inside keeps what it knows. Returns how many cells are pending. */
CMM_FLD_API int cmm_fld_recentre(cmm_fld *f, int bx, int by, int bz);

/* Next cell whose geometry the reader still owes us, in world CELL coordinates (multiply by step
 * for blocks). Returns 0 when nothing is pending. Nearest-first, so the geometry that matters
 * most is filled first and a half-read window is still usable close in. */
CMM_FLD_API int cmm_fld_next_pending(cmm_fld *f, int *cx, int *cy, int *cz);

/* Report one cell, all six numbers as linear transmission in 0..1 (1 = open air, 0 = sealed).
 * tnx/tny/tnz are the faces this cell shares with its -X/-Y/-Z neighbours. midX/midY/midZ are the
 * cell's MIDDLE PLANE perpendicular to each axis -- what stands across a route passing through,
 * which is what stops one cutting through a wall that sits inside a cell rather than on its edge.
 * Directional (ABI 2) because a single "how solid is this cell" made sound travelling OVER a
 * floor be attenuated BY the floor: outdoors came out 3.2 dB down for no visible reason. */
CMM_FLD_API void cmm_fld_set_cell(cmm_fld *f, int cx, int cy, int cz,
                                  float tnx, float tny, float tnz,
                                  float midX, float midY, float midZ);

/* Mark the cells covering a block region as needing geometry again (a door swung, a wall broke,
 * a chunk arrived). Cheap: it only queues a re-read, it never touches the field. */
CMM_FLD_API void cmm_fld_dirty(cmm_fld *f, int bx, int by, int bz, int radiusBlocks);

/* Flood from the listener. Cells whose geometry is still pending are treated as opaque, so an
 * unread region is never mistaken for open sky — the same rule the voxel reader uses for chunks
 * that are not resident. Returns the number of cells reached. */
CMM_FLD_API int cmm_fld_solve(cmm_fld *f, float lx, float ly, float lz);

/* EXCESS attenuation at a world position, in dB, >= 0. Trilinear across the surrounding cells, so
 * a source walking toward you gets a curve and not a staircase. Unreachable reads as `maxDb`. */
CMM_FLD_API float cmm_fld_sample_db(cmm_fld *f, float x, float y, float z);

/* The MATERIAL half of the excess: how much is walls rather than the route going the long way
 * round. Excess alone cannot distinguish them and they want opposite fixes. */
CMM_FLD_API float cmm_fld_sample_mat_db(cmm_fld *f, float x, float y, float z);

/* Blend factor for the crossfade between successive floods, 0..1 applied by the caller. The
 * listener moving is the only thing that shifts the whole field at once, and one flood interval
 * of blend covers it; a source moving is already smooth by the trilinear sample. */
CMM_FLD_API float cmm_fld_sample_db_blended(cmm_fld *f, float x, float y, float z, float mix);

/* Tuning. dbPerBlock is what a block of DETOUR costs — the only reason going the long way round
 * is worse than going straight. maxDb is where the field gives up and calls it silent. Both are
 * taste, and there are deliberately no others: everything else comes from the blocks. */
CMM_FLD_API void cmm_fld_set_tuning(cmm_fld *f, float dbPerBlock, float maxDb);

/* Diagnostics: 0 cells, 1 pending, 2 reached last solve, 3 last solve microseconds. */
CMM_FLD_API long long cmm_fld_stat(cmm_fld *f, int what);

#ifdef __cplusplus
}
#endif
#endif
