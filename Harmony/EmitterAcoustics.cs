// Gated to 3.0 (GV_V3): depends on the same world APIs as EmitterRegistry / EmitterAudioLayer.
#if GV_V3
using System.Collections.Generic;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // EMITTER ACOUSTICS — sound as a shortest-path COST FIELD, weighted by the
    //                     game's OWN block acoustics (not our guesses)
    // ─────────────────────────────────────────────────────────────────────────
    // "How far, acoustically, is the speaker from my ear?" — a wall makes it FARTHER, not silent.
    // We answer it with a weighted flood (Dijkstra) outward from the ear, but every per-cell weight now
    // comes from the engine instead of a hand-rolled approximation:
    //   • PASSABILITY — `Block.IsSeeThrough(world, pos, blockValue)`, the SAME per-instance test the game
    //     uses for line-of-sight. It's stateful: an OPEN door reads see-through (BlockPoweredDoor
    //     overrides it with IsDoorOpen), a CLOSED one doesn't; broken windows, railings, ladders and
    //     chain-link fences all read see-through via !IsCollideSight. So sound leaks through exactly
    //     what you can see through, and doors change acoustically when they open.
    //   • HOW MUCH a solid wall attenuates — its MATERIAL: `blockMaterial.SurfaceCategory` (metal / stone
    //     / wood / glass …). A metal wall costs far more virtual distance than a wooden one; a partial
    //     (non-solid-cube) shape costs half. This is the "I can hear it through every wall equally" fix.
    // Stepping through open air costs 1 block; a solid cell costs 1 + WallDistance·materialLoss. The
    // cheapest path to each speaker is its EFFECTIVE distance; subtracting the straight-line block count
    // leaves the OCCLUSION EXTRA (0 in open air, growing smoothly with each wall / detour), which the
    // native spatializer folds into the distance curve so a wall reads as "farther" — quieter AND duller
    // on one smooth curve. Continuous everywhere (±1 per step, no cliff); every in-box speaker gets a
    // finite distance (no reach binary). The reachable set also measures the surrounding space, which the
    // reverb stage will reuse. Main-thread only (voxel reads); allocation-free per tick (preallocated
    // Dijkstra buffers, costs reset via a touched-list ⇒ no GC churn).
    internal static class EmitterAcoustics
    {
        internal const int MaxPoints = 8;   // matches EmitterAudioLayer.MaxPoints (native MAX_EMIT_PTS)

        // How SOLID-sealed the listener is from every audible speaker, 0 (at least one open AIR path — a
        // corridor around a bend) … 1 (all speakers walled off by material — a sealed vault). Set each
        // ComputeOcclusion from the MIN hard-stop gate across speakers (least-sealed = best path). PushReverb
        // uses it to fade the wet by WorldEmitterReverbOcclusion, so reflections carry around corners but
        // don't leak through a sealed wall — the split relies on the flood actually routing the air path.
        internal static float ReverbSeal = 1f;

        // CONNECTED-AIR region measured by the occlusion flood: the cells it reaches with ZERO solid cost are
        // the air space you're actually standing in — and because the flood walks around corners and down
        // tunnels, this captures a winding corridor's true extent that a straight ray probe can't see. Its
        // longest span drives the reverb TAIL (so a bent tunnel finally reads long, not a 5-block sightline).
        // Set every routed ComputeOcclusion; AirCellCount == 0 means no flood ran this tick (use the ray probe).
        internal static float AirRoomSize;
        internal static int   AirCellCount;
        internal static int   FloodSettled;   // cells the last flood settled (perf diagnostic)

        // Fill extra[0..count) with each speaker's occlusion VIRTUAL DISTANCE (extra blocks added to its
        // real distance; 0 = clear line). NOT a 0..1 gain — the native spatializer adds it to the
        // Euclidean distance (dEff = d + extra) so a wall reads as "farther" (gradual), not a cut.
        internal static void ComputeOcclusion(Vector3 ear, float[] pts, int count, float[] extra, World world)
        {
            if (count <= 0) return;
            RefreshDoorAnim(world);   // sample any mid-swing doors' openness before the flood/rays read CellLoss
            float wallDist = CMMConfig.WorldEmitterOcclusionWallDistance;   // virtual blocks a FULL solid wall adds

            // PathRouting off ⇒ direct-line only (no geodesic go-around): the same per-cell material loss
            // summed straight ear→speaker. A straight ray has no detour, so raw == solid there.
            if (!CMMConfig.WorldEmitterPathRouting)
            {
                float minGateOff = 1f;
                for (int i = 0; i < count; i++)
                {
                    float e = SegmentLoss(ear, Pt(pts, i), world) * wallDist;
                    extra[i] = ShapeOcclusion(e, e, e, EmitterRange(i), out float g);   // direct-line: gate on the measured line
                    if (g < minGateOff) minGateOff = g;
                }
                ReverbSeal = minGateOff;
                return;
            }

            long _floodT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            RunField(ear, world, wallDist);   // one weighted flood from the ear serves every speaker
            float floodMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - _floodT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            // Catch the intermittent frame spike: log immediately (unthrottled) whenever the flood is heavy.
            if (floodMs > 30f)
                CMMConfig.D($"[CMM] FloodSpike ms={floodMs:0.0} cells={FloodSettled} floodR={_fEffR} nearest={EmitterRegistry.NearestDist:0.0}m");

            int earX = Mathf.FloorToInt(ear.x), earY = Mathf.FloorToInt(ear.y), earZ = Mathf.FloorToInt(ear.z);
            float minGate = 1f;
            // Diagnostic capture of the DOMINANT (loudest = smallest effective distance) speaker's occlusion.
            float dbgDEff = float.MaxValue; char dbgBranch = '?';
            float dbgDist = 0f, dbgExtra = 0f, dbgSolid = 0f, dbgSolidGate = 0f, dbgGate = 0f;
            for (int i = 0; i < count; i++)
            {
                int o = i * 3;
                int sx = Mathf.FloorToInt(pts[o]), sy = Mathf.FloorToInt(pts[o + 1]), sz = Mathf.FloorToInt(pts[o + 2]);

                // rawExtra = total detour+solid virtual distance (drives the soft-knee muffle).
                // solid     = the SOLID-only portion of that path (drives the hard-stop gate). Separating the
                //             two is the point: a long OPEN-AIR go-around never seals, real material does.
                float rawExtra, solid;
                float solidGate;   // solid the flood MEASURED (drives true-silence hard-stop); excludes beyond-box straight-line
                char branch = 'R'; // diagnostic: R=reached by flood, B=beyond-box boundary blend, F=no route (direct ray)
                // Where the arrival-direction trace should START: the reachable cell the air route arrives at.
                // For an in-box speaker that's its own cell; for one BEYOND the box it's the boundary cell where
                // the route re-enters the flood (so a long corridor still gets a valid down-the-corridor bearing
                // instead of failing to the through-wall straight line). canTrace=false ⇒ no air route at all.
                int traceX = sx, traceY = sy, traceZ = sz;
                bool canTrace = true;
                int arrivalScaled = SampleArrival(sx, sy, sz, out int wallScaled);
                if (arrivalScaled >= 0)
                {
                    // Cheapest path length (blocks) minus the straight-line block count ⇒ the extra the
                    // walls / detour cost. In open air the two are equal ⇒ ~0 (open-air feel unchanged).
                    float arrivalBlocks = arrivalScaled * (1f / CostScale);
                    int man = Mathf.Abs(sx - earX) + Mathf.Abs(sy - earY) + Mathf.Abs(sz - earZ);
                    rawExtra = arrivalBlocks - man;
                    if (rawExtra < 0f) rawExtra = 0f;
                    solid = wallScaled * (1f / CostScale);
                    solidGate = solid;                          // fully measured by the flood ⇒ trust it for hard-stop
                }
                else
                {
                    // Unreached (outside the search box / budget). BOUNDARY BLEND: march from the speaker
                    // back to the search-box edge, take the routed cost there, then add the straight-ray
                    // remainder for ONLY the bit beyond the box — so the value joins the flood field
                    // continuously (no snap between routed and straight-ray). Falls back to a full direct
                    // ray only if the ray never re-enters the box (routing genuinely can't reach).
                    int costB = BoundaryToward(sx, sy, sz, earX, earY, earZ, out int bx, out int by, out int bz, out int bWallScaled);
                    if (costB >= 0)
                    {
                        float routedB = costB * (1f / CostScale);                       // routed ear→edge
                        int remMan = Mathf.Abs(sx - bx) + Mathf.Abs(sy - by) + Mathf.Abs(sz - bz);
                        float remWall = SegmentLoss(new Vector3(bx + 0.5f, by + 0.5f, bz + 0.5f),
                                                    Pt(pts, i), world) * wallDist;       // walls edge→speaker
                        int man = Mathf.Abs(sx - earX) + Mathf.Abs(sy - earY) + Mathf.Abs(sz - earZ);
                        rawExtra = routedB + remMan + remWall - man;
                        if (rawExtra < 0f) rawExtra = 0f;
                        solid = bWallScaled * (1f / CostScale) + remWall;               // solid routed-to-edge + solid beyond
                        solidGate = bWallScaled * (1f / CostScale);                     // MEASURED solid only — beyond-box straight-line (remWall) can't seal
                        traceX = bx; traceY = by; traceZ = bz;                          // trace the air route from the box edge
                        branch = 'B';
                    }
                    else
                    {
                        rawExtra = SegmentLoss(ear, Pt(pts, i), world) * wallDist;
                        solid = rawExtra;
                        solidGate = solid;                                             // NO air route at all ⇒ genuinely sealed, allow hard-stop
                        canTrace = false;                                              // no air route reached this speaker
                        branch = 'F';
                    }
                }
                extra[i] = ShapeOcclusion(rawExtra, solid, solidGate, EmitterRange(i), out float g);
                if (g < minGate) minGate = g;

                float ddist = (Pt(pts, i) - ear).magnitude;
                if (ddist + extra[i] < dbgDEff)
                {
                    dbgDEff = ddist + extra[i]; dbgBranch = branch;
                    dbgDist = ddist; dbgExtra = extra[i]; dbgSolid = solid; dbgSolidGate = solidGate; dbgGate = g;
                }

                // DIRECTIONAL: if the straight line to this speaker runs through solid rock, re-aim the
                // virtual source to the bearing the sound actually ARRIVES from — the air route the flood
                // walked (down the corridor), not the through-the-wall straight line. Blend by how much
                // rock the direct line crosses so line-of-sight is untouched and the swing is gradual.
                // Distance is preserved (only the direction rotates), so attenuation/occlusion are unchanged.
                if (CMMConfig.WorldEmitterOcclusionDirectional)
                {
                    float directSolid = SegmentLoss(ear, Pt(pts, i), world);   // solid blocks on the straight line
                    float b = SmoothStep(0.35f, 1.5f, directSolid);            // ~⅓ block → start, ~1½ → full
                    if (b > 0.001f && canTrace && ArrivalDir(traceX, traceY, traceZ, earX, earY, earZ, ear, out Vector3 adir))
                    {
                        Vector3 realDir = Pt(pts, i) - ear;
                        float dist = realDir.magnitude;
                        if (dist > 0.01f)
                        {
                            realDir /= dist;
                            Vector3 blended = Vector3.Lerp(realDir, adir, b);
                            if (blended.sqrMagnitude > 1e-6f)
                            {
                                blended.Normalize();
                                Vector3 np = ear + blended * dist;   // same distance, rotated toward the air route
                                pts[o] = np.x; pts[o + 1] = np.y; pts[o + 2] = np.z;
                            }
                        }
                    }
                }
            }
            ReverbSeal = minGate;

            // Diagnostic: the dominant speaker's occlusion breakdown, throttled. `br` = which path measured it
            // (R=reached by flood/stable, B=beyond-box boundary blend/jumpy, F=no air route). If `br` sits on B
            // and the numbers jump block-to-block, the radio is outside the flood's reach → raise FloodRadius.
            if (Time.realtimeSinceStartup >= _nextOcclLog)
            {
                _nextOcclLog = Time.realtimeSinceStartup + 0.2f;
                CMMConfig.D($"[CMM] Occl: br={dbgBranch} dist={dbgDist:0.0} extra={dbgExtra:0.0} dEff={dbgDEff:0.0}/{CMMConfig.WorldEmitterRange:0} " +
                            $"solid={dbgSolid:0.0} sGate={dbgSolidGate:0.0} gate={dbgGate:0.00} floodR={_fEffR} floodMs={floodMs:0.0} cells={FloodSettled}");
            }
        }
        private static float _nextOcclLog;

        // Trace the sound's ARRIVAL DIRECTION for one speaker: steepest-descent down the flood cost field
        // from the speaker's cheapest reached cell back to the ear (the shortest air route, reversed), then
        // read the bearing a few blocks up that route from the ear — i.e. which way sound enters the ear's
        // air space. Returns false if no reached start cell / degenerate path (caller keeps the real bearing).
        private static readonly int[] _arrPath = new int[512];
        private const int ArrivalSampleSteps = 4;   // blocks up the route to sample the bearing (smooths the 6-dir quantisation)

        private static bool ArrivalDir(int sx, int sy, int sz, int earX, int earY, int earZ, Vector3 ear, out Vector3 dir)
        {
            dir = Vector3.zero;

            // Cheapest reached cell in the speaker's 3×3×3 = where the route arrives at the speaker's surface.
            int startIdx = -1, startCost = Inf, startX = 0, startY = 0, startZ = 0;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = sx + dx, y = sy + dy, z = sz + dz;
                int lx = x - _fMinX, ly = y - _fMinY, lz = z - _fMinZ;
                if (lx < 0 || lx >= Dim || ly < 0 || ly >= Dim || lz < 0 || lz >= Dim) continue;
                int id = lx + ly * Dim + lz * Dim2;
                if (_cost[id] < startCost) { startCost = _cost[id]; startIdx = id; startX = x; startY = y; startZ = z; }
            }
            if (startIdx < 0 || startCost == Inf) return false;

            int earIdx = (earX - _fMinX) + (earY - _fMinY) * Dim + (earZ - _fMinZ) * Dim2;
            int cx = startX, cy = startY, cz = startZ, cid = startIdx;
            int len = 0;
            _arrPath[len++] = cid;
            int guard = 0;
            while (cid != earIdx && guard++ < 500 && len < _arrPath.Length)
            {
                int bestId = -1, bestCost = _cost[cid], bx = 0, by = 0, bz = 0;
                // 6-connected: step to the strictly-cheaper neighbour (Dijkstra field ⇒ this reaches the ear).
                for (int f = 0; f < 6; f++)
                {
                    int nx = cx + (f == 0 ? 1 : f == 1 ? -1 : 0);
                    int ny = cy + (f == 2 ? 1 : f == 3 ? -1 : 0);
                    int nz = cz + (f == 4 ? 1 : f == 5 ? -1 : 0);
                    int lx = nx - _fMinX, ly = ny - _fMinY, lz = nz - _fMinZ;
                    if (lx < 0 || lx >= Dim || ly < 0 || ly >= Dim || lz < 0 || lz >= Dim) continue;
                    int nid = lx + ly * Dim + lz * Dim2;
                    if (_cost[nid] < bestCost) { bestCost = _cost[nid]; bestId = nid; bx = nx; by = ny; bz = nz; }
                }
                if (bestId < 0) break;   // local min (shouldn't happen off the ear) → stop
                cid = bestId; cx = bx; cy = by; cz = bz;
                _arrPath[len++] = cid;
            }
            if (cid != earIdx || len < 2) return false;   // never reached the ear ⇒ keep the real bearing

            // Bearing to a cell a few steps up the route from the ear (path end is the ear cell).
            int sample = len - 1 - ArrivalSampleSteps;
            if (sample < 0) sample = 0;
            int pcid = _arrPath[sample];
            int plz = pcid / Dim2, prem = pcid - plz * Dim2, ply = prem / Dim, plx = prem - ply * Dim;
            dir = new Vector3((plx + _fMinX) + 0.5f - ear.x,
                              (ply + _fMinY) + 0.5f - ear.y,
                              (plz + _fMinZ) + 0.5f - ear.z);
            if (dir.sqrMagnitude < 1e-6f) return false;
            dir.Normalize();
            return true;
        }

        // Two curves turn raw occlusion into the virtual distance the native falloff sees — now SCALED to the
        // emitter's own audible `range` so occlusion stays balanced across per-emitter loudness:
        //  • The air DETOUR (going AROUND a wall) is REAL distance — range-independent, passed through as-is.
        //  • The WALL virtual distance and both thresholds (soft-knee ceiling, hard-stop) scale by k =
        //    range/refRange, so a louder (bigger-range) speaker punches through more wall and a quiet one
        //    seals fast — one set of absolute tunables stays in balance whatever the speaker's volume.
        //    k = 1 for every speaker TODAY (uniform Range), i.e. identical to the un-scaled behaviour, until
        //    per-emitter volume control lands and EmitterRange() starts returning Range·volume.
        //  • SOFT-KNEE (tanh) ⇒ diminishing returns toward the (scaled) ceiling = an audible floor: muffle/
        //    detour alone can never silence a speaker (stand behind a box, still hear it).
        //  • HARD-STOP gate on the SOLID-only part ⇒ enough real material pushes `extra` past this emitter's
        //    range so the falloff drives gain to 0 (true silence, e.g. a sealed vault). smoothstep from 55%
        //    of HardStop keeps the dead-zone edge from being a cliff; the native gain slew time-smooths it.
        // `gate` (out) = the SOLID hard-stop factor 0..1 (0 = open air / detour only, 1 = sealed by material).
        // Reused by the reverb to decide whether reflections carry (air path) or are blocked (walled).
        private static float ShapeOcclusion(float rawExtra, float solid, float solidGate, float range, out float gate)
        {
            float refRange = CMMConfig.WorldEmitterRange;
            float k = refRange > 0.001f ? range / refRange : 1f;             // per-emitter range scale (1 today)
            float detour = rawExtra - solid; if (detour < 0f) detour = 0f;   // open-air go-around = real distance
            solid *= k;                                                      // wall virtual distance scales w/ range
            rawExtra = detour + solid;
            float ceiling = CMMConfig.WorldEmitterOcclusionMaxDistance * k;
            if (ceiling < 0.001f) ceiling = 0.001f;
            float audible = ceiling * (float)System.Math.Tanh(rawExtra / ceiling);
            float hardStop = CMMConfig.WorldEmitterOcclusionHardStop * k;
            // TRUE-SILENCE hard-stop is driven ONLY by solid the flood actually MEASURED along a real air-
            // adjacent path (solidGate), NOT by the straight-line-through-rock estimate we fall back to for a
            // speaker BEYOND the flood box. In a winding tunnel that estimate crosses rock the true (unexplored)
            // air route goes around — which was slamming the gate to 1 and cutting the audio in/out as the route
            // flipped in and out of the box. The full `solid` still drives the muffle (audible) above, so a
            // genuinely sealed-but-distant speaker is heavily dulled — just not dead-silent-flickering.
            gate = SmoothStep(hardStop * 0.55f, hardStop, solidGate * k);
            return audible + gate * range;   // gate→1 pushes dEff past THIS emitter's range ⇒ silence
        }

        // Per-emitter audible range. Uniform (the global Range) TODAY; when per-emitter volume control lands
        // this returns each speaker's own range (louder ⇒ bigger range ≈ Range·volume) and ShapeOcclusion
        // rescales the wall occlusion to match — no re-tuning of the absolute knobs needed. Single swap point.
        private static float EmitterRange(int i) => CMMConfig.WorldEmitterRange;

        private static float SmoothStep(float a, float b, float x)
        {
            if (b <= a) return x >= b ? 1f : 0f;
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        // ── Per-cell acoustics (the engine's answer, not ours) ──────────────────────────────────────
        // How much one voxel blocks sound, as a FRACTION of a full solid wall (0 = acoustically open,
        // 1 = a full opaque wall of the densest material). Drives both the flood step cost and the
        // fallback ray. See-through cells (open doors / windows / railings / ladders / fences) ≈ open;
        // an intact see-through pane that still blocks movement (glass) muffles a touch; opaque solids
        // are weighted by their material and halved for partial (non-cube) shapes.
        private static float CellLoss(BlockValue bv, World world, int x, int y, int z)
        {
            if (bv.isair) return 0f;
            Block b = bv.Block;
            if (b == null) return 0f;

            // DOOR MID-SWING: if this cell belongs to a door the hook flagged as animating (openness sampled
            // this tick from its Animator), blend the shut-loss by how far it has PHYSICALLY opened — so the
            // occlusion tracks the swing instead of snapping when the logical isOpen flips (a garage door no
            // longer obstructs the instant you press close). Guarded by _doorOpenness.Count so cells pay
            // nothing while no door is animating; at rest the see-through branch below handles doors.
            if (_doors.Count > 0 && _doorTypes.Contains(bv.type))
            {
                Vector3i cell = new Vector3i(x, y, z);
                Vector3i parent = bv.ischild ? b.multiBlockPos.GetParentPos(cell, bv) : cell;
                if (_doors.TryGetValue(parent, out DoorAnim da))
                {
                    _doorCellsBlended++;
                    return SolidLoss(b) * (1f - da.Openness);   // open ⇒ 0 (air), shut ⇒ full, mid ⇒ proportional
                }
            }

            if (b.IsSeeThrough(world, new Vector3i(x, y, z), bv))
                // Per-INSTANCE movement test, not the static IsCollideMovement: an OPEN door isn't movement-
                // blocked ⇒ reads as free air (uniform across door types); an intact glass pane still blocks
                // ⇒ leaks only a little. (Static IsCollideMovement was type-level, so open garage doors —
                // whose block sets Collide=movement — wrongly muffled while wood doors didn't.)
                return b.IsMovementBlocked(world, new Vector3i(x, y, z), bv, BlockFace.None) ? 0.15f : 0f;

            // DECORATIVE TRIM / MOLDING / ARCH: an opaque PARTIAL (non-cube) shape you can still walk through is
            // oriented against the frame — it fills a voxel in our grid but leaves the opening clear, so sound
            // passes like you do. Only a partial that actually BLOCKS movement (rotated INTO the path, a half-
            // wall) keeps its solid share below. Per-instance + orientation-aware: the SAME trim block reads
            // near-open flush to the wall but solid when turned into the doorway — which is why smashing the
            // decor "let more sound through" (we were charging it as half a wall). Full cubes never reach here.
            if (b.shape != null && !b.shape.IsSolidCube &&
                !b.IsMovementBlocked(world, new Vector3i(x, y, z), bv, BlockFace.None))
                return MaterialLoss(b.blockMaterial != null ? b.blockMaterial.SurfaceCategory : null) * 0.1f;

            return SolidLoss(b);
        }

        // Transmission loss of an opaque solid (a wall, or a fully-shut door): its material, halved for a
        // partial (non-cube) shape. Factored out so the door-openness blend and the plain-solid path agree.
        private static float SolidLoss(Block b)
        {
            float m = MaterialLoss(b.blockMaterial != null ? b.blockMaterial.SurfaceCategory : null);
            if (b.shape != null && !b.shape.IsSolidCube) m *= 0.5f;   // half-blocks / trims transmit more
            return m;
        }

        // ── Door animation tracking (Tier 2: occlusion follows the physical swing) ───────────────────
        // Doors the hook flagged as mid-swing, keyed by PARENT block pos. Openness is the ACOUSTIC value
        // (0 = shut, 1 = open to the ear); sampled from the door's Unity Animator each occlusion tick
        // (RefreshDoorAnim, main thread — the flood is main-thread), then dropped once the swing settles so
        // the cheap at-rest see-through path takes back over.
        private struct DoorAnim { public bool Opening; public float Openness; public bool Started; public int Ticks; }
        private static readonly Dictionary<Vector3i, DoorAnim> _doors = new Dictionary<Vector3i, DoorAnim>();
        private static readonly HashSet<int> _doorTypes = new HashSet<int>();   // block types the hook has seen animate
        private static readonly List<Vector3i> _doorPrune = new List<Vector3i>();
        private static int _doorCellsBlended;   // diagnostic: cells the door branch blended last tick

        // The door hook can fire OFF the main thread (7D2D runs door animation/physics on job threads), so it
        // must NOT touch _doors/_doorTypes directly — doing so raced RefreshDoorAnim's enumeration and the
        // flood's reads ("Collection was modified", which aborted the whole emitter tick). Instead it enqueues
        // here under a lock; RefreshDoorAnim drains this on the main thread, so _doors/_doorTypes stay
        // single-threaded for every read (CellLoss) and iteration.
        private struct PendingDoor { public int Type; public Vector3i Pos; public bool Opening; }
        private static readonly List<PendingDoor> _doorPending = new List<PendingDoor>();
        private static readonly object _doorLock = new object();

        // Sound opens FASTER than the leaf: a door leaks the moment it cracks and is acoustically ~full well
        // before it finishes swinging (a 6-7 s garage door opens by ear in ~2-3 s; a fast swing starts sooner
        // too). physical swing 0..1 → acoustic openness, full at this fraction of the swing. On CLOSE this
        // holds sound until the leaf is most of the way shut, then fades.
        private const float DoorFullOpenFrac = 0.4f;
        private static float DoorAcoustic(float physical) => Mathf.Clamp01(physical / DoorFullOpenFrac);

        // Mute this many seconds before a CLOSE animation ends (skip the end-of-swing bounce). Time-based so
        // it's a big fraction of a fast swing but a negligible sliver of a slow garage/gate close.
        private const float CloseLeadSeconds = 0.15f;

        // True while any door is mid-swing — the emitter layer speeds its refresh so door transitions are
        // sampled tightly (fade starts promptly, close timing is precise) instead of in 250 ms 4 Hz steps.
        internal static bool AnyDoorAnimating => _doors.Count > 0 || _doorPending.Count > 0;

        // Called by the door hook (Patch_DoorAcousticChange) the instant a door toggles. `opening` is the
        // SetOpen target (the swing DIRECTION) — state-NAME-agnostic. Openness starts at the PRE-toggle
        // position so the door reads right until its clip actually begins (opening ⇒ shut 0; closing ⇒ open 1).
        internal static void RegisterAnimatingDoor(int blockType, Vector3i parentPos, bool opening)
        {
            // May be off-thread — just enqueue; RefreshDoorAnim applies it on the main thread.
            lock (_doorLock)
                _doorPending.Add(new PendingDoor { Type = blockType, Pos = parentPos, Opening = opening });
        }

        // Refresh every tracked door from its Animator; drop the ones that settled / can't be read. Runs at
        // the top of ComputeOcclusion, before any CellLoss. Main-thread only.
        private static void RefreshDoorAnim(World world)
        {
            _doorCellsBlended = 0;
            // Drain any registrations the hook queued (possibly off-thread) into the main-thread collections.
            lock (_doorLock)
            {
                for (int i = 0; i < _doorPending.Count; i++)
                {
                    PendingDoor p = _doorPending[i];
                    _doorTypes.Add(p.Type);
                    _doors[p.Pos] = new DoorAnim { Opening = p.Opening, Openness = p.Opening ? 0f : 1f, Started = false, Ticks = 0 };
                }
                _doorPending.Clear();
            }
            if (_doors.Count == 0) return;
            _doorPrune.Clear();
            foreach (var kv in _doors) _doorPrune.Add(kv.Key);
            for (int i = 0; i < _doorPrune.Count; i++)
            {
                Vector3i parent = _doorPrune[i];
                DoorAnim da = _doors[parent];
                if (UpdateDoor(world, parent, ref da)) _doors[parent] = da;   // still animating
                else _doors.Remove(parent);                                    // settled / unreadable → binary resumes
            }
        }

        // Advance one tracked door from its Animator. Returns false when it should stop being tracked (swing
        // finished / at rest / unreadable). Robust to state NAMES and to multi-layer / multi-animator door
        // rigs: takes the MINIMUM normalizedTime across all enabled animators+layers — the freshly-started
        // swing clip is the low one; stale finished clips sit at ≥1. While every layer still reads ≥1 the new
        // clip hasn't begun (the animator was just re-enabled) → HOLD at the pre-toggle position, don't settle
        // (that stale ≥1 was making garage doors settle+drop on the first sample = binary behaviour).
        private static bool UpdateDoor(World world, Vector3i parent, ref DoorAnim da)
        {
            da.Ticks++;
            BlockEntityData bed = world.ChunkCache != null ? world.ChunkCache.GetBlockEntity(parent) : null;
            if (bed == null || !bed.bHasTransform || bed.transform == null) return false;

            Animator[] anims = bed.transform.GetComponentsInChildren<Animator>();
            float minNt = float.MaxValue, minLen = 0f; bool anyEnabled = false;
            for (int a = 0; a < anims.Length; a++)
            {
                Animator an = anims[a];
                if (an == null || !an.isActiveAndEnabled) continue;
                anyEnabled = true;
                int lc = an.layerCount;
                for (int L = 0; L < lc; L++)
                {
                    AnimatorStateInfo si = an.GetCurrentAnimatorStateInfo(L);
                    if (si.normalizedTime < minNt) { minNt = si.normalizedTime; minLen = si.length; }   // fresh clip + its seconds
                }
            }
            if (!anyEnabled) return false;   // every animator disabled ⇒ door at rest

            if (!da.Started)
            {
                if (minNt >= 0.95f)   // clip hasn't begun (stale end-of-previous) → hold at start, keep waiting
                {
                    da.Openness = da.Opening ? 0f : 1f;
                    return da.Ticks < 12;   // give up after ~a few seconds if no clip ever plays
                }
                da.Started = true;
            }

            float t = Mathf.Clamp01(minNt);
            float physical = da.Opening ? t : 1f - t;
            da.Openness = DoorAcoustic(physical);
            // Mute a hair BEFORE the leaf finishes closing — its end-of-swing bounce shouldn't still be
            // audible. Time-based so it self-scales: a big lead on a fast swing (~0.5 s clip), negligible on
            // a slow garage (0.15 s of a 6-7 s close ⇒ invisible, garage feel untouched).
            if (!da.Opening && minLen > 0f && (1f - t) * minLen < CloseLeadSeconds) da.Openness = 0f;
            return minNt < 1f;   // still animating until the swing clip completes
        }

        // SurfaceCategory → transmission-loss fraction, cached per category string (few distinct; the
        // string work happens once per material, not per cell). Tolerant substring match so new/modded
        // categories fall through to a sensible mid value rather than reading as glass.
        private static readonly Dictionary<string, float> _lossCache = new Dictionary<string, float>();
        private static float MaterialLoss(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return 0.6f;
            if (_lossCache.TryGetValue(cat, out float v)) return v;
            string c = cat.ToLowerInvariant();
            float r;
            if (c.Contains("metal") || c.Contains("iron") || c.Contains("steel"))            r = 1.0f;
            else if (c.Contains("stone") || c.Contains("concrete") || c.Contains("brick")
                     || c.Contains("cobble") || c.Contains("rock") || c.Contains("cinder"))   r = 0.9f;
            else if (c.Contains("dirt") || c.Contains("earth") || c.Contains("soil")
                     || c.Contains("sand") || c.Contains("gravel") || c.Contains("clay")
                     || c.Contains("snow"))                                                    r = 0.7f;
            else if (c.Contains("wood") || c.Contains("lumber"))                              r = 0.5f;
            else if (c.Contains("plastic") || c.Contains("polymer") || c.Contains("rubber"))   r = 0.4f;
            else if (c.Contains("glass"))                                                     r = 0.3f;
            else if (c.Contains("cloth") || c.Contains("organic") || c.Contains("plant")
                     || c.Contains("hay") || c.Contains("leaves") || c.Contains("wool")
                     || c.Contains("cotton"))                                                  r = 0.25f;
            else                                                                              r = 0.6f;
            _lossCache[cat] = r;
            return r;
        }

        // BoundaryBlend anchor: march from an OUT-OF-BOX speaker back toward the ear along the straight
        // line and return the routed cost (scaled) of the first cell whose neighbourhood the flood
        // reached, plus that cell's world coords. The caller then straight-lines only the remainder, so a
        // speaker crossing the search-box edge no longer snaps between the routed and straight-ray values
        // (the old fallback punched a full direct ray THROUGH whatever the flood had just routed around).
        private static int BoundaryToward(int sx, int sy, int sz, int earX, int earY, int earZ,
                                          out int bx, out int by, out int bz, out int bWall)
        {
            bx = sx; by = sy; bz = sz; bWall = 0;
            int dx = earX - sx, dy = earY - sy, dz = earZ - sz;
            int steps = Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz)));
            for (int s = 1; s <= steps; s++)
            {
                int x = sx + Mathf.RoundToInt((float)dx * s / steps);
                int y = sy + Mathf.RoundToInt((float)dy * s / steps);
                int z = sz + Mathf.RoundToInt((float)dz * s / steps);
                int a = SampleArrival(x, y, z, out int w);   // min cost over its 3×3×3 ⇒ the reached surface near it
                if (a >= 0) { bx = x; by = y; bz = z; bWall = w; return a; }
            }
            return -1;
        }

        // ── Acoustic cost field: weighted flood (Dijkstra via a bucket queue) from the ear ──────────
        // Costs are quantised to CostScale units per block so a bucket queue (Dial's algorithm) processes
        // them in strict cost order without a heap. Air = CostScale/step; a solid adds
        // WallDistance·CellLoss·CostScale on top (sound passes, but pays). Bounded by the flood radius
        // (a box around the ear) and a node budget so a wide-open interior can't stall the frame.
        private const int   MaxFloodRadius = 32;                 // sizes the fixed buffers
        private const int   CostScale      = 4;                  // 0.25-block cost resolution
        private const int   NodeBudget     = 8000;               // max cells settled per flood
        private const int   MaxBucket      = 2048;               // cost ceiling; beyond ⇒ dropped (unreached)
        private static readonly int Dim  = MaxFloodRadius * 2 + 1;
        private static readonly int Dim2 = Dim * Dim;

        private static int[] _cost;                              // Dim^3, best cost to each cell (scaled); Inf = unseen
        private static int[] _wallCost;                          // Dim^3, SOLID-only cost carried down the cheapest path (scaled)
        private static int[] _next;                              // Dim^3, intrusive bucket linked-list
        private static int[] _bucketHead;                        // MaxBucket+1 heads (-1 = empty)
        private static int[] _touched;                           // cells first-set this flood (for O(touched) reset)
        private static int   _touchCount;
        private static int   _fMinX, _fMinY, _fMinZ;             // box origin (world → local)
        private static int   _fEffR;                             // live search radius within the box
        private static int   _wallCostScaled;                    // WallDistance·CostScale, set per flood
        private const int    Inf = int.MaxValue;

        private static void RunField(Vector3 ear, World world, float wallDist)
        {
            if (_cost == null)
            {
                int n = Dim * Dim * Dim;
                _cost = new int[n];
                _wallCost = new int[n];
                _next = new int[n];
                // Every settled cell (≤ NodeBudget) can FIRST-SEE up to 6 neighbours, each recorded once
                // here, so the touched set can reach ~7× the settle budget — size for that.
                _touched = new int[NodeBudget * 7 + 8];
                _bucketHead = new int[MaxBucket + 1];
                for (int i = 0; i < n; i++) _cost[i] = Inf;
                for (int c = 0; c <= MaxBucket; c++) _bucketHead[c] = -1;
                _touchCount = 0;
            }

            // Reset only what the last flood dirtied.
            for (int i = 0; i < _touchCount; i++) { _cost[_touched[i]] = Inf; _wallCost[_touched[i]] = 0; }
            _touchCount = 0;
            for (int c = 0; c <= MaxBucket; c++) _bucketHead[c] = -1;   // small, fixed — cheap to blanket-clear

            _wallCostScaled = Mathf.RoundToInt(wallDist * CostScale);
            if (_wallCostScaled < 0) _wallCostScaled = 0;

            int earX = Mathf.FloorToInt(ear.x), earY = Mathf.FloorToInt(ear.y), earZ = Mathf.FloorToInt(ear.z);
            _fMinX = earX - MaxFloodRadius; _fMinY = earY - MaxFloodRadius; _fMinZ = earZ - MaxFloodRadius;
            _fEffR = Mathf.Clamp(CMMConfig.WorldEmitterFloodRadius, 1, MaxFloodRadius);

            int startIdx = (earX - _fMinX) + (earY - _fMinY) * Dim + (earZ - _fMinZ) * Dim2;
            _cost[startIdx] = 0;
            _wallCost[startIdx] = 0;
            _touched[_touchCount++] = startIdx;
            _bucketHead[0] = startIdx; _next[startIdx] = -1;

            // Connected-air bounding box for the reverb room measure (cells reached with zero solid cost).
            int airN = 0, aMinX = int.MaxValue, aMinY = int.MaxValue, aMinZ = int.MaxValue,
                          aMaxX = int.MinValue, aMaxY = int.MinValue, aMaxZ = int.MinValue;

            int settled = 0;
            for (int c = 0; c <= MaxBucket && settled < NodeBudget; c++)
            {
                int idx = _bucketHead[c];
                while (idx != -1 && settled < NodeBudget)
                {
                    int nxt = _next[idx];
                    if (_cost[idx] == c)   // skip stale entries (a cheaper cost superseded this one)
                    {
                        settled++;
                        int lz = idx / Dim2, rem = idx - lz * Dim2, ly = rem / Dim, lx = rem - ly * Dim;
                        int x = lx + _fMinX, y = ly + _fMinY, z = lz + _fMinZ;
                        int pw = _wallCost[idx];   // solid-only cost of the path settled to this cell
                        if (pw == 0)               // reached through AIR only ⇒ part of the listener's room
                        {
                            airN++;
                            if (x < aMinX) aMinX = x; if (x > aMaxX) aMaxX = x;
                            if (y < aMinY) aMinY = y; if (y > aMaxY) aMaxY = y;
                            if (z < aMinZ) aMinZ = z; if (z > aMaxZ) aMaxZ = z;
                        }
                        Relax(x + 1, y, z, earX, earY, earZ, c, pw, world);
                        Relax(x - 1, y, z, earX, earY, earZ, c, pw, world);
                        Relax(x, y + 1, z, earX, earY, earZ, c, pw, world);
                        Relax(x, y - 1, z, earX, earY, earZ, c, pw, world);
                        Relax(x, y, z + 1, earX, earY, earZ, c, pw, world);
                        Relax(x, y, z - 1, earX, earY, earZ, c, pw, world);
                    }
                    idx = nxt;
                }
            }

            // Longest span of the connected-air box = how big the space "sounds". Follows bends (the flood
            // routed the air), so a winding tunnel reads its real length, not a straight sightline. Capped by
            // FloodRadius, which is fine — a space bigger than the flood is already plenty reverberant.
            AirCellCount = airN;
            FloodSettled = settled;
            if (airN > 0)
            {
                int dx = aMaxX - aMinX + 1, dy = aMaxY - aMinY + 1, dz = aMaxZ - aMinZ + 1;
                int longest = dx; if (dy > longest) longest = dy; if (dz > longest) longest = dz;
                AirRoomSize = longest;
            }
            else AirRoomSize = 0f;
        }

        // Relax one neighbour: enter-cost from its block (air cheap, solid dear by material), insert /
        // decrease-key into the bucket queue. Everything is passable — a wall is expensive, never a hard
        // barrier — so the field is continuous and every in-box speaker gets a finite distance.
        private static void Relax(int x, int y, int z, int earX, int earY, int earZ, int c, int pw, World world)
        {
            if (y < 0 || y >= 256) return;
            // Keep the search local: a box of half-extent _fEffR around the ear (Chebyshev).
            if (x < earX - _fEffR || x > earX + _fEffR ||
                y < earY - _fEffR || y > earY + _fEffR ||
                z < earZ - _fEffR || z > earZ + _fEffR) return;

            int lx = x - _fMinX, ly = y - _fMinY, lz = z - _fMinZ;
            int idx = lx + ly * Dim + lz * Dim2;

            int wallEnter = Mathf.RoundToInt(_wallCostScaled * CellLoss(world.GetBlock(x, y, z), world, x, y, z));
            int enter = CostScale + wallEnter;          // air is CostScale; the solid surcharge is wallEnter
            int nc = c + enter;
            if (nc > MaxBucket) return;                 // past the ceiling — treat as unreachable
            if (nc >= _cost[idx]) return;               // no improvement

            if (_cost[idx] == Inf && _touchCount < _touched.Length)
                _touched[_touchCount++] = idx;                       // record each cell once (guarded)
            _cost[idx] = nc;
            _wallCost[idx] = pw + wallEnter;            // carry the solid-only cost along the chosen (min-total) path
            _next[idx] = _bucketHead[nc]; _bucketHead[nc] = idx;    // (lazy decrease-key: old entry left stale)
        }

        // Cheapest arrival cost at a speaker: the min over its 3×3×3 neighbourhood, so we read the cost
        // of sound reaching the speaker's SURFACE (an adjacent open cell) rather than the cost of the
        // last punch INTO its own solid block. Returns -1 if nothing in the box was reached.
        private static int SampleArrival(int sx, int sy, int sz, out int wallAtBest)
        {
            int best = Inf, bestWall = 0;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = sx + dx, y = sy + dy, z = sz + dz;
                int lx = x - _fMinX, ly = y - _fMinY, lz = z - _fMinZ;
                if (lx < 0 || lx >= Dim || ly < 0 || ly >= Dim || lz < 0 || lz >= Dim) continue;
                int id = lx + ly * Dim + lz * Dim2;
                int cst = _cost[id];
                if (cst < best) { best = cst; bestWall = _wallCost[id]; }
            }
            wallAtBest = bestWall;
            return best == Inf ? -1 : best;
        }

        // ── Direct-line fallback (PathRouting off + unreached speakers) ─────────────────────────────
        // Sum the per-cell CellLoss along a proper voxel DDA (Amanatides–Woo) from ear to speaker, one
        // contribution per cell crossed, endpoints excluded. Uses the SAME CellLoss as the flood so the
        // two agree at their boundary. Result is in "full-wall" units (× WallDistance ⇒ virtual blocks).
        internal static float SegmentLoss(Vector3 from, Vector3 to, World world)
        {
            Vector3 d = to - from;
            float dist = d.magnitude;
            if (dist < 0.01f) return 0f;
            d /= dist;

            int x = Mathf.FloorToInt(from.x), y = Mathf.FloorToInt(from.y), z = Mathf.FloorToInt(from.z);
            int ex = Mathf.FloorToInt(to.x), ey = Mathf.FloorToInt(to.y), ez = Mathf.FloorToInt(to.z);
            int stepX = d.x > 0 ? 1 : (d.x < 0 ? -1 : 0);
            int stepY = d.y > 0 ? 1 : (d.y < 0 ? -1 : 0);
            int stepZ = d.z > 0 ? 1 : (d.z < 0 ? -1 : 0);
            float tMaxX = AxisTMax(from.x, d.x, x, stepX), tDeltaX = AxisTDelta(d.x);
            float tMaxY = AxisTMax(from.y, d.y, y, stepY), tDeltaY = AxisTDelta(d.y);
            float tMaxZ = AxisTMax(from.z, d.z, z, stepZ), tDeltaZ = AxisTDelta(d.z);

            float loss = 0f;
            for (int guard = 0; guard < 256; guard++)
            {
                if (x == ex && y == ey && z == ez) break;   // reached the speaker cell — stop before it
                // advance to the next cell
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ) { x += stepX; tMaxX += tDeltaX; }
                else if (tMaxY <= tMaxZ)              { y += stepY; tMaxY += tDeltaY; }
                else                                  { z += stepZ; tMaxZ += tDeltaZ; }
                if (x == ex && y == ey && z == ez) break;   // don't count the speaker's own block
                if (y < 0 || y >= 256) continue;
                loss += CellLoss(world.GetBlock(x, y, z), world, x, y, z);
            }
            return loss;
        }

        private static float AxisTMax(float origin, float dir, int cell, int step)
        {
            if (Mathf.Abs(dir) < 1e-6f) return float.MaxValue;
            float boundary = step > 0 ? cell + 1 : cell;
            return (boundary - origin) / dir;
        }

        private static float AxisTDelta(float dir)
        {
            return Mathf.Abs(dir) < 1e-6f ? float.MaxValue : Mathf.Abs(1f / dir);
        }

        private static Vector3 Pt(float[] pts, int i)
        {
            int o = i * 3;
            return new Vector3(pts[o], pts[o + 1], pts[o + 2]);
        }

        // ── Room probe (drives the reverb) ──────────────────────────────────────────────────────────
        // Cast a fixed spray of rays from the ear and see, for each, how far the first opaque surface is
        // and what it's made of. From that we estimate: how ENCLOSED the space is (rays that hit vs fly
        // off into the open), how BIG it is (mean hit distance), and how REFLECTIVE its surfaces are
        // (material). That's the cheap "shoebox" room estimate the reverb runs on — no per-echo tracing,
        // the way Steam Audio / Resonance fake it. World-space voxel march (same DDA as the occlusion),
        // so no Unity-Physics layer/handedness/render-space pitfalls. Allocation-free.
        // Reverb probe reach (blocks): a surface farther than this reads as "open sky". Live config
        // (WorldEmitterReverbRange) so big caverns can register as enclosed instead of going dry. Clamped
        // to a sane floor; the ray guard below scales to it so even diagonal rays can travel the full reach.
        // Clamped to a sane ceiling: a huge reach marches each of the (now 18) rays thousands of voxels into
        // unloaded chunks, stalling the probe and feeding garbage into the reverb (the "set it to 400 and the
        // audio cuts out" footgun). 160 blocks is bigger than any real cave; beyond that it's not physical.
        private static float ProbeMaxDist => Mathf.Clamp(CMMConfig.WorldEmitterReverbRange, 8f, 160f);
        private static readonly Vector3[] _probeDirs =
        {
            new Vector3( 1, 0, 0), new Vector3(-1, 0, 0),
            new Vector3( 0, 1, 0), new Vector3( 0,-1, 0),
            new Vector3( 0, 0, 1), new Vector3( 0, 0,-1),
            // 8 corner diagonals (normalized) so a room's corners/ceiling/floor mix in, not just the 6 axes
            new Vector3( 0.577f, 0.577f, 0.577f), new Vector3(-0.577f, 0.577f, 0.577f),
            new Vector3( 0.577f, 0.577f,-0.577f), new Vector3(-0.577f, 0.577f,-0.577f),
            new Vector3( 0.577f,-0.577f, 0.577f), new Vector3(-0.577f,-0.577f, 0.577f),
            new Vector3( 0.577f,-0.577f,-0.577f), new Vector3(-0.577f,-0.577f,-0.577f),
            // 4 HORIZONTAL diagonals (45° in the XZ plane, no Y tilt). Without these the ONLY horizontal rays
            // are the 4 cardinals (±X/±Z), so a corridor that doesn't run along X or Z has no ray down its
            // length and reads as a tiny box — its reverb never grows no matter the reach. These give
            // horizontal coverage every 45°, so an angled corridor's long axis is caught to ~cos(22.5°)=92%.
            new Vector3( 0.707f, 0f, 0.707f), new Vector3(-0.707f, 0f, 0.707f),
            new Vector3( 0.707f, 0f,-0.707f), new Vector3(-0.707f, 0f,-0.707f),
        };

        // Per-ray distance scratch for the robust room-size percentile (main-thread only, no GC).
        private static readonly float[] _probeDist = new float[32];
        // DIAGNOSTIC scratch (per-ray dump, throttled log) — remove once the reverb-size issue is nailed.
        private static readonly float[] _probeRawDist = new float[32];
        private static readonly bool[]  _probeRawHit  = new bool[32];
        private static float _nextProbeLog;

        internal static void ProbeRoom(Vector3 ear, World world, out float avgDist, out float roomSize,
                                       out float reflect, out float openFrac, out bool roofed)
        {
            int n = _probeDirs.Length, hits = 0;
            float sumDist = 0f, sumReflect = 0f;
            float reach = ProbeMaxDist;                 // read the live reach once so it's consistent this probe
            roofed = false;
            for (int i = 0; i < n; i++)
            {
                if (ProbeRay(ear, _probeDirs[i], world, reach, out float dist, out float rfl))
                {
                    hits++; sumDist += dist; sumReflect += rfl;
                    _probeDist[i] = dist;
                    _probeRawDist[i] = dist; _probeRawHit[i] = true;
                    if (i == 2) roofed = true;          // _probeDirs[2] = straight up: a hit ⇒ ceiling
                }
                else { sumDist += reach; _probeDist[i] = reach; _probeRawDist[i] = reach; _probeRawHit[i] = false; }   // open = far in that dir
            }

            // DIAGNOSTIC: dump every ray's direction + distance (o = open/miss), throttled to ~1/s. Tells us
            // exactly what the probe sees where the reverb is wrong. Remove once resolved.
            if (CMMConfig.DebugLogging && Time.realtimeSinceStartup >= _nextProbeLog)
            {
                _nextProbeLog = Time.realtimeSinceStartup + 1f;
                var sb = new System.Text.StringBuilder("[CMM] ProbeRays reach=");
                sb.Append(reach.ToString("0")).Append(" ear=(")
                  .Append(ear.x.ToString("0.0")).Append(',').Append(ear.y.ToString("0.0")).Append(',').Append(ear.z.ToString("0.0")).Append(") ");
                for (int i = 0; i < n; i++)
                {
                    Vector3 d = _probeDirs[i];
                    sb.Append('(').Append(d.x.ToString("0.0")).Append(',').Append(d.y.ToString("0.0")).Append(',')
                      .Append(d.z.ToString("0.0")).Append(")=").Append(_probeRawDist[i].ToString("0.0"))
                      .Append(_probeRawHit[i] ? " " : "o ");
                }
                CMMConfig.D(sb.ToString());
            }
            avgDist  = sumDist / n;
            reflect  = hits > 0 ? sumReflect / hits : 0f;
            openFrac = (float)(n - hits) / n;
            // ROOM SIZE for the tail = the space's LARGEST dimension (that's what rings). Average the LONGEST
            // QUARTER of the rays: in a cave/tunnel most rays hit the near floor/walls (2-3 blk), so a median
            // /percentile pinned the tail to ~0 and reverb sounded absent; the long axis (down the tunnel,
            // across the cavern) lives in the top few rays. Averaging the top quarter captures it while
            // staying robust to a single fluke ray (unlike a bare max). Sorts n floats in place.
            System.Array.Sort(_probeDist, 0, n);        // ascending
            int top = Mathf.Max(1, n / 4);
            float topSum = 0f;
            for (int i = n - top; i < n; i++) topSum += _probeDist[i];
            roomSize = topSum / top;
        }

        // March until the first opaque (non-see-through) surface; report its distance + reflectivity.
        private static bool ProbeRay(Vector3 from, Vector3 dir, World world, float reach, out float dist, out float reflect)
        {
            dist = reach; reflect = 0f;
            int x = Mathf.FloorToInt(from.x), y = Mathf.FloorToInt(from.y), z = Mathf.FloorToInt(from.z);
            int stepX = dir.x > 0 ? 1 : (dir.x < 0 ? -1 : 0);
            int stepY = dir.y > 0 ? 1 : (dir.y < 0 ? -1 : 0);
            int stepZ = dir.z > 0 ? 1 : (dir.z < 0 ? -1 : 0);
            float tMaxX = AxisTMax(from.x, dir.x, x, stepX), tDeltaX = AxisTDelta(dir.x);
            float tMaxY = AxisTMax(from.y, dir.y, y, stepY), tDeltaY = AxisTDelta(dir.y);
            float tMaxZ = AxisTMax(from.z, dir.z, z, stepZ), tDeltaZ = AxisTDelta(dir.z);

            // A normalized diagonal crosses up to ~1.73 voxel faces per block of travel, so size the guard to
            // ~2× the reach — otherwise a diagonal ray would give up (false = "open") well before `reach`.
            int maxSteps = Mathf.CeilToInt(reach * 2f) + 4;
            for (int guard = 0; guard < maxSteps; guard++)
            {
                float t;
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ) { x += stepX; t = tMaxX; tMaxX += tDeltaX; }
                else if (tMaxY <= tMaxZ)              { y += stepY; t = tMaxY; tMaxY += tDeltaY; }
                else                                  { z += stepZ; t = tMaxZ; tMaxZ += tDeltaZ; }
                if (t > reach) return false;
                if (y < 0 || y >= 256) continue;
                BlockValue bv = world.GetBlock(x, y, z);
                if (bv.isair) continue;
                Block b = bv.Block;
                if (b == null) continue;
                if (b.IsSeeThrough(world, new Vector3i(x, y, z), bv)) continue;   // window/doorway — sound passes, no reflection here
                dist = t;
                reflect = MaterialReflect(b.blockMaterial != null ? b.blockMaterial.SurfaceCategory : null);
                return true;
            }
            return false;
        }

        // SurfaceCategory → acoustic reflectivity (0 = dead/absorptive, 1 = hard/reflective), cached.
        // Distinct from transmission loss: glass blocks little sound but reflects a lot; cloth absorbs both.
        private static readonly Dictionary<string, float> _reflectCache = new Dictionary<string, float>();
        private static float MaterialReflect(string cat)
        {
            if (string.IsNullOrEmpty(cat)) return 0.6f;
            if (_reflectCache.TryGetValue(cat, out float v)) return v;
            string c = cat.ToLowerInvariant();
            float r;
            if (c.Contains("metal") || c.Contains("iron") || c.Contains("steel"))            r = 0.95f;
            else if (c.Contains("stone") || c.Contains("concrete") || c.Contains("brick")
                     || c.Contains("cobble") || c.Contains("rock") || c.Contains("cinder"))   r = 0.9f;
            else if (c.Contains("glass"))                                                     r = 0.85f;
            else if (c.Contains("plastic") || c.Contains("polymer") || c.Contains("rubber"))   r = 0.6f;
            else if (c.Contains("wood") || c.Contains("lumber"))                              r = 0.45f;
            else if (c.Contains("dirt") || c.Contains("earth") || c.Contains("soil")
                     || c.Contains("sand") || c.Contains("gravel") || c.Contains("clay")
                     || c.Contains("snow"))                                                    r = 0.3f;
            else if (c.Contains("cloth") || c.Contains("organic") || c.Contains("plant")
                     || c.Contains("hay") || c.Contains("leaves") || c.Contains("wool")
                     || c.Contains("cotton"))                                                  r = 0.15f;
            else                                                                              r = 0.6f;
            _reflectCache[cat] = r;
            return r;
        }

        // Map the room estimate → reverb params (all 0..1). wet: how much reverb — enclosed + reflective
        // spaces are wet, open/dead ones are dry. room: size → tail length (bigger space, longer decay).
        // damp: HF damping — absorptive materials dull and shorten the tail.
        internal static void ComputeReverb(float roomSize, float reflect, float openFrac, bool roofed,
                                           out float wet, out float room, out float damp)
        {
            float enclosure = 1f - openFrac;                              // 0 = wide open, 1 = sealed room
            float roof = roofed ? 1f : 0.5f;                             // open sky dries it — softly, not a 3× cliff on one up-ray
            // LEVEL: enclosed × reflective (no size penalty — a small stone room still rings). Linear in
            // enclosure (was enclosure², which crushed partially-open big spaces).
            wet  = Mathf.Clamp01(enclosure * reflect * roof);
            // TAIL LENGTH. A ray probe only measures the longest SIGHTLINE, which underestimates a winding or
            // tight tunnel: no straight line is long even though the sealed stone space rings for ages (the
            // length is hidden around the bends). So blend two cues and take the max: (1) sightline size — a
            // big open cavern gets its long tail here; (2) a "trapped" term — a fully ENCLOSED, hard
            // (reflective / low-absorption) space rings regardless of how far you can see, so a narrow sealed
            // stone tunnel finally gets a real tail. Absorptive (cloth/dirt) or open spaces stay dry.
            float sizeTail = (roomSize - 2f) / 18f;                                // long-sightline halls
            float trapTail = enclosure * Mathf.Clamp01((reflect - 0.4f) / 0.5f);   // sealed + hard ⇒ rings even if narrow
            room = Mathf.Clamp01(Mathf.Max(sizeTail, 0.6f * trapTail));
            damp = Mathf.Clamp01(0.25f + 0.7f * (1f - reflect));          // dead materials → darker/shorter tail
        }
    }
}
#endif
