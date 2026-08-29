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
        // hard-gates the CARRY reverb bus with it (carry ×= 1−seal), so the next-room cue carries around
        // corners but never leaks through a sealed wall; the LOCAL bus needs no gate — its input is the
        // already-occluded arriving signal.
        internal static float ReverbSeal = 1f;

        // SOLID material (virtual blocks, flood-measured) on the LEAST-blocked route to any audible
        // speaker — the same per-speaker solidGate the hard-stop uses, before the gate's smoothstep.
        // PushReverb damps the CARRY bus proportionally by it (≈ halved per half wall-equivalent), so a
        // single closed door audibly reduces the next-room reverb long before the all-or-nothing seal
        // window (0.55·HardStop → HardStop) engages. Door mid-swing blends smoothly for free (the door's
        // openness already scales its cell cost). 0 = a clear air route exists.
        internal static float ReverbSolid;

        // ACOUSTIC DEPTH for the reverb: the AIR-ROUTE distance (blocks) to the closest-by-air speaker —
        // real distance plus the open-air detour the flood walked (around bends, down the tunnel), but
        // EXCLUDING wall/solid virtual distance (a thin wall doesn't make the reverberant field "deep";
        // the seal gate handles walls). Drives the depth-graded wet: the deeper into the cave you are from
        // the station, the quieter, darker and more damped the reverb reads. Set each ComputeOcclusion.
        internal static float ReverbDepth;

        // CONNECTED-AIR region measured by the occlusion flood: the cells it reaches with ZERO solid cost are
        // the air space you're actually standing in — and because the flood walks around corners and down
        // tunnels, this captures a winding corridor's true extent that a straight ray probe can't see. Its
        // longest span drives the reverb TAIL (so a bent tunnel finally reads long, not a 5-block sightline).
        // Set every routed ComputeOcclusion; AirCellCount == 0 means no flood ran this tick (use the ray probe).
        internal static float AirRoomSize;
        internal static int   AirCellCount;
        internal static int   FloodSettled;   // cells the last flood settled (perf diagnostic)

        // ── Occlusion stability instrumentation (DebugLogging only) ──────────────────────────────
        // Answers the two questions the acoustic rework hinges on: WHICH estimator is measuring each
        // speaker (routed / boundary-blend / direct-ray — the boundary blend is the documented
        // jittery one), and HOW MUCH the raw measurement moves between consecutive measurements of
        // the SAME speaker. A large jitter on branch B means the symptom is the estimator, not the
        // distance curve. Tallies accumulate until EmitterAudioLayer's 1 Hz summary drains them.
        internal static int   StatBrR, StatBrB, StatBrF;   // per-speaker branch tallies
        internal static int   StatSamples;                 // speakers measured
        internal static float StatJitterSum, StatJitterMax;// |Δ raw virtual distance| vs previous measurement
        private static readonly Dictionary<Vector3i, float> _statPrev = new Dictionary<Vector3i, float>();

        // Record one speaker's freshly-measured occlusion. `branch`: R = reached by the field,
        // B = boundary blend (the lottery), F = no route at all (direct ray). Called by BOTH the
        // legacy flood and the field-cache path so the tallies compare like with like.
        internal static void StatNote(Vector3i cell, float extra, char branch)
        {
            if (!CMMConfig.DebugLogging) return;
            StatSamples++;
            if (branch == 'R') StatBrR++; else if (branch == 'B') StatBrB++; else StatBrF++;
            if (_statPrev.TryGetValue(cell, out float prev))
            {
                float d = extra - prev; if (d < 0f) d = -d;
                StatJitterSum += d;
                if (d > StatJitterMax) StatJitterMax = d;
            }
            if (_statPrev.Count > 64) _statPrev.Clear();   // speaker set churned — debug-only, just reset
            _statPrev[cell] = extra;
        }

        internal static void StatReset()
        {
            StatBrR = StatBrB = StatBrF = StatSamples = 0;
            StatJitterSum = StatJitterMax = 0f;
        }

        // The CURRENT distance curve, in dB, for a given effective distance — the same formula the
        // native spatializer applies (cmm_emitter_gains). Logged so a captured trace states the
        // AUDIBLE consequence directly, which is what any replacement curve has to reproduce.
        internal static float CurveDb(float dEff)
        {
            float mn = CMMConfig.WorldEmitterMinDistance, mx = CMMConfig.WorldEmitterRange;
            float span = mx - mn; if (span < 0.0001f) span = 0.0001f;
            if (dEff <= mn) return 0f;
            if (dEff >= mx) return -120f;
            float att = Mathf.Pow((mx - dEff) / span, CMMConfig.WorldEmitterFalloff);
            return att <= 1e-6f ? -120f : 20f * Mathf.Log10(att);
        }

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
                float minGateOff = 1f, minDepthOff = float.MaxValue, minSolidOff = float.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    float e = SegmentLoss(ear, Pt(pts, i), world) * wallDist;
                    extra[i] = ShapeOcclusion(e, e, e, EmitterRange(i), out float g);   // direct-line: gate on the measured line
                    if (g < minGateOff) minGateOff = g;
                    if (e < minSolidOff) minSolidOff = e;   // straight line: everything on it is "solid" cost
                    // No routing info ⇒ the air route is the straight line (detour unknown).
                    float dOff = (Pt(pts, i) - ear).magnitude;
                    if (dOff < minDepthOff) minDepthOff = dOff;
                }
                ReverbSeal = minGateOff;
                ReverbDepth = minDepthOff;
                ReverbSolid = minSolidOff;
                return;
            }

            // SYNCHRONOUS fallback: settle the whole flood in one call, then finish. The live game path
            // does NOT come through here — EmitterAudioLayer drives OcclusionBegin / OcclusionStep /
            // OcclusionFinish instead, slicing the settle a few ms per frame, because the full flood
            // measured ~10 ms average (135 ms worst) on the main thread: a metronomic frame spike on
            // every 4 Hz tick (the "felt performance issues" of 2026-07-07).
            FloodBegin(ear, wallDist);
            FloodStep(world, float.MaxValue);
            FinishSpeakers(ear, pts, count, extra, world);
        }

        // ── Amortized occlusion (the live path) ──────────────────────────────────────────────────
        // Driven per-frame by EmitterAudioLayer while a station voice is live: Begin (re-samples doors,
        // seeds the flood) → Step every frame until it reports complete (each call settles cells only
        // until budgetMs elapses) → Finish (per-speaker occlusion / directional redirect / reverb
        // seal+depth from the completed field). The field is anchored at Begin's ear — the caller must
        // pass the SAME ear to Finish, and must not interleave another Begin before Finish.
        internal static void OcclusionBegin(Vector3 ear, World world)
        {
            RefreshDoorAnim(world);   // sample mid-swing doors once per flood; CellLoss reads them across slices
            FloodBegin(ear, CMMConfig.WorldEmitterOcclusionWallDistance);
        }
        internal static bool OcclusionStep(World world, float budgetMs) => FloodStep(world, budgetMs);
        internal static void OcclusionFinish(Vector3 ear, float[] pts, int count, float[] extra, World world)
            => FinishSpeakers(ear, pts, count, extra, world);

        // Per-speaker occlusion from the COMPLETED flood field: sample each speaker's arrival cost
        // (routed / boundary-blend / direct-ray fallback), shape it into virtual distance, aim the
        // directional redirect, publish ReverbSeal/ReverbDepth. Cheap next to the flood itself.
        private static void FinishSpeakers(Vector3 ear, float[] pts, int count, float[] extra, World world)
        {
            float wallDist = _fWallDist;   // the weights the field was built with (config may have moved since)
            // The whole-flood spike log (summed across slices; slices themselves are budget-bounded).
            if (_floodMsTotal > 30f)
                CMMConfig.D($"[CMM] FloodSpike ms={_floodMsTotal:0.0} slices={_floodSlices} cells={FloodSettled} floodR={_fEffR} nearest={EmitterRegistry.NearestDist:0.0}m");

            int earX = Mathf.FloorToInt(ear.x), earY = Mathf.FloorToInt(ear.y), earZ = Mathf.FloorToInt(ear.z);
            float minGate = 1f, minDepth = float.MaxValue, minSolid = float.MaxValue;
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
                int arrivalScaled = SampleArrival(sx, sy, sz, out int wallScaled, out int wallMinScaled);
                if (arrivalScaled >= 0)
                {
                    // Cheapest path length (blocks) minus the straight-line block count ⇒ the extra the
                    // walls / detour cost. In open air the two are equal ⇒ ~0 (open-air feel unchanged).
                    float arrivalBlocks = arrivalScaled * (1f / CostScale);
                    int man = Mathf.Abs(sx - earX) + Mathf.Abs(sy - earY) + Mathf.Abs(sz - earZ);
                    rawExtra = arrivalBlocks - man;
                    if (rawExtra < 0f) rawExtra = 0f;
                    solid = wallScaled * (1f / CostScale);
                    solidGate = wallMinScaled * (1f / CostScale);   // least wall over ANY reached route ⇒ flap-free hard-stop
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
                StatNote(new Vector3i(sx, sy, sz), extra[i], branch);
                if (g < minGate) minGate = g;
                if (solidGate < minSolid) minSolid = solidGate;   // flood-MEASURED solid only (same basis as the gate)

                float ddist = (Pt(pts, i) - ear).magnitude;
                // Air-route distance for the reverb depth: real distance + the OPEN-AIR share of the
                // detour (rawExtra minus its solid portion). A wall adds no depth (the seal gate covers
                // walls); a route down a winding tunnel adds its true walked length.
                float airDetour = rawExtra - solid; if (airDetour < 0f) airDetour = 0f;
                float airRoute = ddist + airDetour;
                if (airRoute < minDepth) minDepth = airRoute;
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
            ReverbDepth = minDepth;
            ReverbSolid = minSolid;

            // Diagnostic: the dominant speaker's occlusion breakdown, throttled. `br` = which path measured it
            // (R=reached by flood/stable, B=beyond-box boundary blend/jumpy, F=no air route). If `br` sits on B
            // and the numbers jump block-to-block, the radio is outside the flood's reach → raise FloodRadius.
            if (Time.realtimeSinceStartup >= _nextOcclLog)
            {
                // During a door swing, log EVERY tick (the layer runs at 20 Hz then): these lines ARE
                // the door reference trace any acoustic rework has to reproduce, so the trajectory
                // has to be dense enough to fit a curve to. Throttled normally.
                _nextOcclLog = Time.realtimeSinceStartup + (AnyDoorAnimating ? 0f : 0.2f);
                float dbDry = CurveDb(dbgDist), dbOcc = CurveDb(dbgDEff);
                CMMConfig.D($"[CMM] Occl: br={dbgBranch} dist={dbgDist:0.0} extra={dbgExtra:0.0} dEff={dbgDEff:0.0}/{CMMConfig.WorldEmitterRange:0} " +
                            $"dry={dbDry:0.0}dB occl={dbOcc:0.0}dB wall={dbOcc - dbDry:0.0}dB " +
                            $"solid={dbgSolid:0.0} sGate={dbgSolidGate:0.0} gate={dbgGate:0.00} floodR={_fEffR} floodMs={_floodMsTotal:0.0}x{_floodSlices} cells={FloodSettled}");
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

        // Two curves turn raw occlusion into the virtual distance the native falloff sees — SCALED to the
        // emitter's audible `range` so a wall costs the same FRACTION of loudness at any range:
        //  • The air DETOUR (going AROUND a wall) is REAL distance — range-independent, passed through as-is
        //    (scaling it would re-break the twisty-cave case: 20 extra blocks of route IS 20 blocks).
        //  • The WALL virtual distance and the soft-knee ceiling scale by k = range/RefRangeBlocks. Walls are
        //    TRANSMISSION loss — a fixed dB cut, not fixed meters. Unscaled, a stone wall's ~24 virtual blocks
        //    was −46 dB at Range 30 but only −7 dB at Range 100 ("high Range almost ignores occlusion",
        //    user-reported door test 2026-07-08). The reference is the FIXED 30-block design range every
        //    absolute knob (WallDistance/MaxDist) was tuned at — NOT the live global Range (scaling against
        //    the global made k≡1 and only balanced future per-emitter volumes, which k still does: a louder
        //    speaker's bigger range punches through proportionally more wall).
        //  • SOFT-KNEE (tanh) ⇒ diminishing returns toward the (scaled) ceiling = an audible floor: muffle/
        //    detour alone can never silence a speaker (stand behind a box, still hear it).
        //  • HARD-STOP gate on the SOLID-only part ⇒ enough real material pushes `extra` past this emitter's
        //    range so the falloff drives gain to 0 (true silence, e.g. a sealed vault). smoothstep from 55%
        //    of HardStop keeps the dead-zone edge from being a cliff; the native gain slew time-smooths it.
        //    hardStop and solidGate both carry ×k, so the gate itself is range-INDEPENDENT: how much material
        //    seals completely stays an absolute-material question at any Range.
        // `gate` (out) = the SOLID hard-stop factor 0..1 (0 = open air / detour only, 1 = sealed by material).
        // Reused by the reverb to decide whether reflections carry (air path) or are blocked (walled).
        private const float RefRangeBlocks = 30f;   // the range all absolute occlusion knobs are tuned at
        // internal: EmitterFieldCache shapes its field-sampled occlusion through the SAME curves.
        internal static float ShapeOcclusion(float rawExtra, float solid, float solidGate, float range, out float gate)
        {
            float k = range / RefRangeBlocks;
            if (k < 0.05f) k = 0.05f;
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
        // Acoustic transmission loss of one cell of WATER. Water is a strong attenuator in air-borne
        // acoustics (the air↔water impedance mismatch reflects almost all energy at the surface, and the
        // voxel model charges that per submerged cell): 0.2 × WallDistance per cell ⇒ at WallDistance 35
        // a couple of metres underwater is heavily muffled and ~10 m reaches the hard-stop. Water feeds
        // the same solid-cost accumulator as walls, so it drives the muffle, the true-silence gate and
        // the reverb carry-seal alike.
        private const float WaterLoss = 0.2f;

        private static float CellLoss(BlockValue bv, World world, int x, int y, int z)
        {
            // In 3.0 water lives in the chunk's fluid-sim layer, NOT the block layer — a water cell's
            // BlockValue is usually AIR, which is why music used to path through lakes untouched. Check
            // the water mask on the air path (World.IsWater is null-safe on unloaded chunks → false).
            if (bv.isair) return world.IsWater(x, y, z) ? WaterLoss : 0f;
            Block b = bv.Block;
            if (b == null) return 0f;

            // Legacy/POI liquid BLOCKS (BlockLiquidv2 etc.) — same acoustics as sim water.
            if (b.blockMaterial != null && b.blockMaterial.IsLiquid) return WaterLoss;

            // TERRAIN DENSITY: marching-cubes terrain is PARTIAL at every natural or carved surface —
            // the chunk stores a per-voxel density (−128 = full rock … −1 = a sliver; ≥0 = air-side of
            // the isosurface). Charging every terrain cell the full material loss made a nearly-dug-
            // through wall read as solid rock until the exact tick the voxel broke. Scale by the voxel's
            // actual fullness: digging toward a radio now opens the sound gradually, and cave-wall
            // surface cells (always partial) stop over-counting. Interior cells (−128) are unchanged.
            if (b.shape is BlockShapeTerrain)
            {
                sbyte den = EmitterCompat.GetDensity(world, x, y, z);
                float frac = den < 0 ? -(float)den * (1f / 128f) : (1f / 128f);
                return MaterialLoss(b.blockMaterial != null ? b.blockMaterial.SurfaceCategory : null) * frac;
            }

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
                    // The leaf swings; the cells it never occupies do not. See LeafKnownAbsentFromCell.
                    if (LeafKnownAbsentFromCell(cell, parent, bv.type)) return 0f;
                    _doorCellsBlended++;
                    // Swing and battle damage both open the door; the LARGER opening wins (holes don't
                    // add anything once the leaf has swung past them). An undamaged SEAL-class door
                    // (vault) blends toward AIRTIGHT density, so the hard-stop gate engages while the
                    // leaf is still visibly closing — the slam lands early, not after the animation.
                    float dmgO = DamageOpenness(b, bv, world, x, y, z);
                    // REMAP THE SWING so the sound arrives with the act rather than with the
                    // animation. Openness is scaled up and clamped, so a door is acoustically shut
                    // once its leaf is WorldEmitterDoorSwingFrac of the way closed and acoustically
                    // open once it is that far open. The "measured latch-to-field is 33-79 ms" that
                    // used to justify this number came from a counter that reported the first flood
                    // finishing after a latch WITHOUT checking the door's cells had been re-read --
                    // so it was frequently timing a flood solved from pre-toggle geometry. The
                    // counter now verifies that (see FieldOcclusion's door stopwatch) and prints
                    // read/wait/solve separately. TREAT THIS KNOB AS UNCALIBRATED until it has been
                    // re-tuned against a VERIFIED number. Damage openness is a static
                    // hole and is not remapped -- there is no swing to be early for.
                    float frac = CMMConfig.WorldEmitterDoorSwingFrac;
                    float swing = da.Openness;
                    if (frac > 0.001f && frac < 0.999f) swing = Mathf.Clamp01(swing / frac);
                    else if (frac <= 0.001f) swing = da.Openness > 0.5f ? 1f : 0f;
                    float open = Mathf.Max(swing, dmgO);
                    float shutLoss = DoorShutLoss(b, bv, dmgO);
                    return shutLoss * (1f - open);   // open ⇒ 0 (air), shut ⇒ full, mid ⇒ proportional
                }
            }

            // A MODEL-ENTITY BLOCK THAT STOPS LIGHT IS MATTER, WHATEVER `Collide` SAYS.
            // Furniture, shelves, closets, appliances and containers are BlockShapeModelEntity: their real
            // geometry and real collision live in a Unity PREFAB, so blocks.xml only lists the raycast
            // channels the voxel takes part in. cntModularClosetOpen declares Collide="melee,rocket" — no
            // "sight", no "movement" — which makes Block.IsSeeThrough (== !IsCollideSight) answer TRUE for a
            // solid wooden wardrobe, and IsMovementBlocked answer FALSE. The see-through branch below then
            // returned 0f: FREE AIR. Straight from `cmm acoustics column`, standing behind a wall of them:
            //
            //     -251, 45, -567   cntModularClosetOpen   loss 0.000   open 1.00   BlockShapeModelEntity
            //
            // Every wall of shelving in the game was a hole, which is "the shelves enclose the room and it
            // bleeds through like I'm standing next to it" (user report 2026-08-20). Those two flags are
            // statements about bullets and pathing; lightOpacity is the one that is about the OBJECT, and a
            // model entity that stops light is a lump of its material. Blocks that genuinely pass light
            // (railings, bars, fence doors, chain-link — lightOpacity 0) fall through to the branches below
            // unchanged, and so do ladders, whose whole point is that you travel through their cells.
            if (b.shape is BlockShapeModelEntity && b.lightOpacity != 0 && !b.IsElevator())
            {
                // A DOOR IS THE ONE MODEL ENTITY WHOSE MATTER LEAVES THE VOXEL. Its leaf swings out, so
                // "not movement-blocked" really does mean the cell is now air — that is the at-rest open-door
                // fix, and it must stay scoped to things that open. Furniture never moves, so the same test
                // applied to a closet was reading decoration as a doorway. Asked of the game's own placement
                // helper (Place="Door"/"DrawBridge" in blocks.xml), which covers doors, hatches, garage doors
                // and drawbridges in every supported version without naming any of them.
                if (IsSwingingLeaf(b, bv.type))
                {
                    Vector3i mcell = new Vector3i(x, y, z);
                    Vector3i tpar = bv.ischild && b.multiBlockPos != null
                                  ? b.multiBlockPos.GetParentPos(mcell, bv) : mcell;
                    // TOGGLED THIS INSTANT, NOT YET DRAINED: answer from the PRE-toggle pose, not
                    // from the latch that has already flipped. See TryGetPendingDoor.
                    if (TryGetPendingDoor(tpar, out bool tOpening))
                    {
                        if (tOpening)                                  // was shut, leaf has not moved
                            return DoorShutLoss(b, bv, DamageOpenness(b, bv, world, x, y, z));
                        return 0f;                                     // was open, still open
                    }
                    if (!b.IsMovementBlocked(world, mcell, bv, BlockFace.None)) return 0f;
                    // SHUT, and every cell of the multiblock says so because the predicate answers
                    // for the PARENT. Only the cells the leaf actually occupies are material.
                    if (LeafKnownAbsentFromCell(mcell, tpar, bv.type)) return 0f;
                }
                float mdmg = DamageOpenness(b, bv, world, x, y, z);
                return DoorShutLoss(b, bv, mdmg) * (1f - mdmg);
            }

            if (EmitterCompat.IsSeeThrough(b, world, new Vector3i(x, y, z), bv))
            {
                // CLIMBABLES (ladders, any material) are open FRAMES, not walls — and you travel THROUGH
                // their cells, so even a small per-cell loss compounds along a ladder shaft into wall-
                // grade cost (a 1-wide ladder tunnel read as sealed concrete, 2026-07-07). The game's own
                // primitive for them is IsElevator (BlockLadder overrides it).
                if (b.IsElevator()) return 0f;
                // Per-INSTANCE movement test, not the static IsCollideMovement: an OPEN door isn't movement-
                // blocked ⇒ reads as free air (uniform across door types). (Static IsCollideMovement was
                // type-level, so open garage doors — whose block sets Collide=movement — wrongly muffled
                // while wood doors didn't.)
                if (!b.IsMovementBlocked(world, new Vector3i(x, y, z), bv, BlockFace.None)) return 0f;
                // Movement-blocked see-through: distinguish a THIN SHEET from an OPEN FRAME by whether
                // bullets collide. An intact glass pane stops bullets ⇒ it's a continuous membrane that
                // leaks a little per pane crossed; bars / chain-link / railings pass bullets through the
                // gaps ⇒ acoustically ~air, however many cells the path runs along.
                return b.IsCollideBullets ? 0.15f : 0f;
            }

            // TRIM, FLOOR PLATES AND OPEN-CENTRE FRAMES USED TO BE GUESSED AT HERE.
            //
            // Three branches stood in this spot, each a proxy for geometry this function could not
            // see: partial-shape-you-can-walk-through ⇒ ×0.1 (trim), with an exception forcing
            // floor plates back to solid because "does it block the player" is a useless question
            // about a floor; and shape-with-a-clear-centre ⇒ ×0.05 (frames). They existed because a
            // cell carried ONE number, so WHERE a shape sat inside its voxel had nowhere to live
            // and had to be smuggled into HOW MUCH it blocked.
            //
            // The field now reads that directly — CoverOf() below returns how much of each face a
            // shape covers, per axis and per half — so the same trim plate comes out near-open when
            // you travel along the wall it is stuck to and solid when you cross it, from its actual
            // bounds instead of from a movement test. A floor plate needs no exception: it covers
            // its whole horizontal cross-section and a tenth of its vertical ones, which is what a
            // floor IS. Keeping the multipliers as well would price the same geometry twice.
            //
            // What arrives below is therefore the block's MATERIAL, full strength, and the shape is
            // priced separately by the reader.

            // A battle-damaged door/hatch/gate at rest: sound passes through the holes its CURRENT
            // damage-state mesh actually shows (the game swaps meshes at per-block health thresholds).
            // An undamaged SEAL-class door (vault) at rest is AIRTIGHT — gate-class solid, true silence;
            // the first visible damage state breaches the seal back to ordinary material acoustics.
            // Only LIGHT-PASSING model entities (lightOpacity 0 — bar gates, fence doors, chain-link) and
            // climbables still arrive here; the opaque ones are handled far above, before IsSeeThrough gets
            // a chance to call a wooden closet a window. The unqualified movement test is right for what is
            // left: these ARE open frames, so a leaf out of the way really does leave air.
            if (b.shape is BlockShapeModelEntity)
            {
                // AN OPEN DOOR AT REST IS OPEN. This branch only ever asked about DAMAGE, so a door
                // standing wide open with nothing in the doorway still returned full solid loss --
                // sound could not pass an open door unless it happened to be MOVING at the time,
                // because the mid-swing branch above is the only place openness was consulted.
                // Reported exactly so: "when doors are animating, air sound travels through them;
                // when they are not moving, sound doesn't seem to, even when it is open."
                //
                // The per-instance movement test is the right question and is already used six lines
                // up for see-through doors, with the comment "an OPEN door isn't movement-blocking".
                // A solid wooden door is not see-through, so it never reached that test and fell
                // through to here. Same test, applied where it was missing.
                if (!b.IsMovementBlocked(world, new Vector3i(x, y, z), bv, BlockFace.None)) return 0f;
                float dmgOpen = DamageOpenness(b, bv, world, x, y, z);
                return DoorShutLoss(b, bv, dmgOpen) * (1f - dmgOpen);
            }
            return SolidLoss(b, bv);
        }

        // WHAT A SHUT DOOR IS MADE OF. The three branches that price one -- mid-swing, model-entity
        // and at-rest -- all asked SolidLoss and all exempted the seal class, so they now share this.
        //
        // Two of them are reached by things that are NOT doors (the model-entity branch prices every
        // closet and bookcase in the game), so the cap is gated on IsSwingingLeaf. A metal WALL must
        // stay at 1.0; it is only a metal DOOR that is being told it is not one. See
        // WorldEmitterDoorMaxLoss for the measurement that prompted it.
        private static float DoorShutLoss(Block b, BlockValue bv, float damageOpenness)
        {
            if (damageOpenness <= 0f && IsSealDoor(b, bv.type)) return AirtightLoss;
            float loss = SolidLoss(b, bv);
            if (!IsSwingingLeaf(b, bv.type)) return loss;        // furniture: not a door, no cap
            // SOME DOORS REALLY ARE WALLS. The cap exists so a metal DOOR is not priced as a metal
            // WALL, and it is right for a hinged leaf you could shoulder through. It is wrong for a
            // raised drawbridge, which is a slab of timber and iron standing in a gateway: measured
            // in game, one capped cell published 6.0 dB to a speaker four metres away, through a
            // barrier a player can see is massive ("the bridge between me and the speaker did not
            // occlude"). The cellar door, uncapped by comparison, publishes 20.2 dB.
            if (IsFullLossDoor(b, bv.type)) return loss;
            float cap = CMMConfig.WorldEmitterDoorMaxLoss;
            return (cap > 0f && loss > cap) ? cap : loss;
        }

        // ── Doors exempt from the cap (config name-prefix list, same shape as the seal list) ─────
        private static string _fullLossCfgCache;
        private static string[] _fullLossPrefixes = System.Array.Empty<string>();
        private static readonly Dictionary<int, bool> _fullLossTypeCache = new Dictionary<int, bool>();
        private static bool IsFullLossDoor(Block b, int type)
        {
            string cfg = CMMConfig.WorldEmitterDoorFullLossTypes ?? "";
            if (!ReferenceEquals(cfg, _fullLossCfgCache))   // config hot-reload swaps the instance
            {
                _fullLossCfgCache = cfg;
                _fullLossPrefixes = cfg.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < _fullLossPrefixes.Length; i++)
                    _fullLossPrefixes[i] = _fullLossPrefixes[i].Trim();
                _fullLossTypeCache.Clear();
            }
            if (_fullLossPrefixes.Length == 0) return false;
            if (_fullLossTypeCache.TryGetValue(type, out bool f)) return f;
            string bn = b != null ? (b.GetBlockName() ?? "") : "";
            f = false;
            for (int i = 0; i < _fullLossPrefixes.Length && !f; i++)
                f = _fullLossPrefixes[i].Length > 0
                 && bn.IndexOf(_fullLossPrefixes[i], System.StringComparison.OrdinalIgnoreCase) >= 0;
            _fullLossTypeCache[type] = f;
            if (f) CMMConfig.D($"[CMM] FullLossDoor {bn} = uncapped (priced as its own material)");
            return f;
        }

        // ── Seal-class doors (airtight when shut+undamaged; config name-prefix list) ─────────────────
        // AirtightLoss is in full-wall units: ×WallDistance(35) ⇒ 140 solid-blocks-equivalent — past any
        // sane HardStop, so the gate reads a shut vault door as sealed at ANY range, and the (1−openness)
        // blend crosses the gate threshold while the leaf is still closing (silence lands mid-swing).
        private const float AirtightLoss = 4f;
        private static string _sealCfgCache;
        private static string[] _sealPrefixes = System.Array.Empty<string>();
        private static readonly Dictionary<int, bool> _sealTypeCache = new Dictionary<int, bool>();
        private static bool IsSealDoor(Block b, int type)
        {
            string cfg = CMMConfig.WorldEmitterDoorSealTypes ?? "";
            if (!ReferenceEquals(cfg, _sealCfgCache))   // config hot-reload swaps the string instance
            {
                _sealCfgCache = cfg;
                _sealPrefixes = cfg.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < _sealPrefixes.Length; i++) _sealPrefixes[i] = _sealPrefixes[i].Trim();
                _sealTypeCache.Clear();
            }
            if (_sealTypeCache.TryGetValue(type, out bool s)) return s;
            string bn = b.GetBlockName() ?? "";
            s = false;
            for (int i = 0; i < _sealPrefixes.Length && !s; i++)
                s = _sealPrefixes[i].Length > 0 && bn.StartsWith(_sealPrefixes[i], System.StringComparison.OrdinalIgnoreCase);
            _sealTypeCache[type] = s;
            // One-shot diagnostic per type: proves whether the airtight class is actually being applied
            // to the door under test (its cells then read ~140 solid / gate=1.00 in the [CMM] Occl line).
            if (s) CMMConfig.D($"[CMM] SealDoor {bn} = AIRTIGHT when shut+undamaged");
            return s;
        }

        // ── WHERE THE MATERIAL SITS INSIDE THE VOXEL ────────────────────────────────────────────
        // Nine numbers per block variant: how much of the cross-section perpendicular to each axis
        // is covered by the shape's geometry, measured in the voxel's NEGATIVE half of that axis,
        // in its POSITIVE half, and in either (which is what a route passing straight through
        // crosses). Indices [0..2] = -X -Y -Z, [3..5] = +X +Y +Z, [6..8] = either.
        //
        // WHAT THIS IS FOR. The field charges a move for the half it leaves through plus the half
        // it enters through, so this is what lets a half block block from the side it is actually
        // on and a sliver CEILING block upward while staying open downward. Before it, a cell held
        // one number and the model knew a block was there but not where — which is why standing in
        // a half block's cell, or on the closed side of a door in its cell, occluded nothing.
        //
        // THICKNESS IS DELIBERATELY ABSENT. A sliver plate and a full block of the same material
        // block the same; what varies is how much of the OPENING is covered and from which side.
        // That is also what retired the ×0.5 / ×0.2 / ×0.1 / ×0.05 shape discounts — they were all
        // standing in for this, badly.
        //
        // Rasterised on an 8×8 grid rather than solved as a union of rectangles: shapes have a
        // handful of boxes, 1/64 of a face is finer than anything downstream can use, and the
        // answer is cached per (type, rotation) for the life of the process, because GetBounds
        // is rotation-aware and shapes are immutable at runtime. This replaced IsFullCube and
        // HasOpenCenter, which asked narrower questions of the same bounds and are now gone.
        private const int CoverGrid = 8;
        internal static readonly float[] FullCover = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
        // The opposite: this cell's material is somewhere else, so no half of it is covered.
        // Published as 1 - cov * (1 - t), so zero cover is transparent whatever t says.
        internal static readonly float[] NoCover  = { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };
        private static readonly Dictionary<int, float[]> _coverCache = new Dictionary<int, float[]>();

        internal static float[] CoverOf(Block b, BlockValue bv)
        {
            // ANYTHING WITHOUT HONEST BOUNDS FILLS ITS VOXEL, which is the answer that changes
            // nothing. Model entities are the important case: a door leaf, a closet, a fridge and
            // a bookcase keep their real geometry in a Unity PREFAB, and GetBounds falls straight
            // through to BlockShape's default cube unless the block sets isCustomBounds, which
            // almost none do. Asking would return "full" from a DEFAULT rather than from evidence,
            // and it is better to say so here than to look like a measurement. Doors therefore
            // stay full-voxel, which is also what keeps the shut-door calibration where it is.
            if (b == null || b.shape == null) return FullCover;
            if (b.shape is BlockShapeModelEntity) return ModelEntityCover(b, bv);
            if (b.shape.IsSolidCube) return FullCover;
            int key = (bv.type << 6) | (bv.rotation & 63);
            if (_coverCache.TryGetValue(key, out float[] hit)) return hit;
            float[] cov = ComputeCover(b, bv);
            _coverCache[key] = cov;
            return cov;
        }

        // ── A MODEL ENTITY'S OPACITY IS THE ONLY EXTENT SIGNAL IT HAS ───────────────────────────
        // Its real geometry is in a Unity prefab, so GetBounds returns a default cube and there is
        // nothing to measure. But the game already carries a number for how much of the voxel is
        // actually filled, because it has to light the thing: lightOpacity.
        //
        // Measured with `cmm acoustics column` in a POI, which is what settled this:
        //
        //     plantAloePottedCornerPurple   loss 0.700   mat earth   opa 0     <- light goes through
        //     cntBookcaseFull               loss 0.500   mat wood    opa 255
        //     cntStore...BookShelfSpeaker   loss 0.080   mat cloth   opa 4
        //
        // A decorative aloe was pricing ABOVE a bookcase — 34 dB of occlusion from a pot plant at
        // the shipped wall curve, measured at 31.6 dB excess with one in the way. It falls to the
        // plain SolidLoss at the bottom of CellLoss and gets charged its POT: earth, 0.7. The
        // material table has a plant rule and never reaches it, because the game says this block
        // is made of soil, and it is.
        //
        // Opacity separates them where nothing else does. The rule this replaces — "a model entity
        // that stops light is matter, whatever Collide says" — was right and is kept; this is the
        // other half of it, that one which does NOT stop light is not matter either.
        //
        // DOORS ARE EXEMPT. They are the calibrated case (the whole native model is fitted to a
        // 4-9 dB shut door), their leaf really does fill its doorway, and a leaf that read a low
        // opacity would silently unblock every door in the game. Not worth the risk for a class
        // that has an answer already.
        private static readonly Dictionary<int, float[]> _entityCoverCache = new Dictionary<int, float[]>();
        private static float[] ModelEntityCover(Block b, BlockValue bv)
        {
            if (b.lightOpacity >= 255 || IsSwingingLeaf(b, bv.type)) return FullCover;
            if (_entityCoverCache.TryGetValue(bv.type, out float[] hit)) return hit;
            float o = b.lightOpacity <= 0 ? 0f : b.lightOpacity / 255f;
            float[] cov = { o, o, o, o, o, o, o, o, o };
            _entityCoverCache[bv.type] = cov;
            return cov;
        }

        private static float[] ComputeCover(Block b, BlockValue bv)
        {
            Bounds[] arr = null;
            try { arr = b.shape.GetBounds(bv); } catch { /* modded shape misbehaving ⇒ treat as full */ }
            if (arr == null || arr.Length == 0) return FullCover;
            var cov = new float[9];
            bool full = true;
            for (int axis = 0; axis < 3; axis++)
            {
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                int cn = 0, cp = 0, ca = 0;
                for (int iu = 0; iu < CoverGrid; iu++)
                    for (int iv = 0; iv < CoverGrid; iv++)
                    {
                        float fu = (iu + 0.5f) / CoverGrid, fv = (iv + 0.5f) / CoverGrid;
                        bool hitN = false, hitP = false;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            Vector3 mn = arr[i].min, mx = arr[i].max;
                            if (fu < mn[u] || fu > mx[u] || fv < mn[v] || fv > mx[v]) continue;
                            // Which half (or both) this box reaches into along the axis of travel.
                            if (mn[axis] < 0.5f) hitN = true;
                            if (mx[axis] > 0.5f) hitP = true;
                        }
                        if (hitN) cn++;
                        if (hitP) cp++;
                        if (hitN || hitP) ca++;
                    }
                const float inv = 1f / (CoverGrid * CoverGrid);
                cov[axis] = cn * inv; cov[axis + 3] = cp * inv; cov[axis + 6] = ca * inv;
                if (cov[axis] < 0.999f || cov[axis + 3] < 0.999f) full = false;
            }
            return full ? FullCover : cov;   // share the common answer, so callers can compare by reference
        }

        // ── THE SAME QUESTION, ASKED ABOUT WHERE YOU ARE STANDING ───────────────────────────────
        // Coverage split at an arbitrary plane instead of at the voxel's middle, for the one cell
        // that knows where the listener is: the one holding the ear.
        //
        // Every other cell has to split at 0.5, and for a door that is exactly wrong — a leaf sits
        // ACROSS the middle, so both halves hold it and leaving the cell crosses one of them
        // whichever way you go. Measured in game: 10.1 dB in every direction, a 1.7 dB room
        // becoming 12.6 dB just by standing in the doorway. Split at the ear and the leaf is
        // wholly behind you or wholly in front, which is what it physically is.
        //
        // Uncached on purpose — the split plane moves with the player, and it is one cell per
        // flood, about 200 point tests.
        internal static float[] CoverSplitAt(Block b, BlockValue bv, float fx, float fy, float fz)
        {
            Bounds[] arr = ShapeBounds(b, bv);
            // NOTHING TO SPLIT IS NOT THE SAME AS A FULL VOXEL. Falling back to FullCover here was
            // a hole exactly the size of the ear's own cell: a pot plant reads coverage 0 from its
            // opacity everywhere in the world, and then read a solid voxel the moment you stood in
            // it. Measured at the plant, one build apart: 0.0 dB of wall four blocks away, 34.0 dB
            // standing in its cell. Ask the ordinary path instead — it has an answer for shapes
            // with no bounds, and the split cannot improve on it.
            return arr == null ? CoverOf(b, bv) : CoverSplitAtBounds(arr, fx, fy, fz);
        }

        internal static float[] CoverSplitAtBounds(Bounds[] arr, float fx, float fy, float fz)
        {
            if (arr == null || arr.Length == 0) return FullCover;
            var cov = new float[9];
            float[] at = { fx, fy, fz };
            for (int axis = 0; axis < 3; axis++)
            {
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                float split = at[axis] < 0.001f ? 0.001f : (at[axis] > 0.999f ? 0.999f : at[axis]);
                int cn = 0, cp = 0, ca = 0;
                for (int iu = 0; iu < CoverGrid; iu++)
                    for (int iv = 0; iv < CoverGrid; iv++)
                    {
                        float fu = (iu + 0.5f) / CoverGrid, fv2 = (iv + 0.5f) / CoverGrid;
                        bool hitN = false, hitP = false;
                        for (int i = 0; i < arr.Length; i++)
                        {
                            Vector3 mn = arr[i].min, mx = arr[i].max;
                            if (fu < mn[u] || fu > mx[u] || fv2 < mn[v] || fv2 > mx[v]) continue;
                            if (mn[axis] < split) hitN = true;
                            if (mx[axis] > split) hitP = true;
                        }
                        if (hitN) cn++;
                        if (hitP) cp++;
                        if (hitN || hitP) ca++;
                    }
                const float inv = 1f / (CoverGrid * CoverGrid);
                cov[axis] = cn * inv; cov[axis + 3] = cp * inv; cov[axis + 6] = ca * inv;
            }
            return cov;
        }

        // ── THE DOOR LEAF, MEASURED RATHER THAN INVENTED ────────────────────────────────────────
        // Four attempts at this failed because every one of them reasoned about a number nobody had
        // ever read. A door is BlockShapeModelEntity, so GetBounds returns a unit cube — and always
        // will: the game only lifts custom bounds from a collider on the model's ROOT
        // (BlockShapeModelEntity.PoolCreateOnceToAllCallBack uses GetComponent, not
        // GetComponentInChildren), and a door's collider is on the animated child. So the leaf's
        // position was assumed to be the middle of the voxel, and it is not.
        //
        // It can be measured. The spawned prefab is reachable through ChunkCache.GetBlockEntity at
        // the PARENT position, and its child colliders' bounds are the leaf's real volume IN ITS
        // CURRENT POSE. That is the game's own idiom — AnimatorDrawbridgeState reads exactly these
        // bounds to test for obstructions.
        //
        // Measuring rather than modelling buys three things at once:
        //   * the real position, including the doors whose prefabs are deliberately offset
        //     (houseSlidingDoorWhite 0,0,0.525; commercialGlassDoor 0,0,-0.525; bathroomStallDoor
        //     0.5,0,-0.5) which no centre assumption could ever have covered;
        //   * the real AXIS, instead of guessing it from which neighbours look emptiest — a guess
        //     that ties at zero for a free-standing frame, for an unloaded chunk, and for the upper
        //     course of a door, and then breaks the tie by array index;
        // WHAT IT DOES NOT BUY IS THE SWING. That was the claim here and it is false, measured in
        // game 2026-08-22 on a garage door with the player standing still: `LEAF measured` at rest,
        // `LEAF rotation` at nt=0.295 with the leaf still risen only 0.89 m of 3 m and therefore
        // still fully spanning the cell. AnimatorDoorState.OnStateUpdate calls EnableColliders(off)
        // for the whole span collideOffPercent..collideOnPercent, which default to 0 and 0.99 —
        // every door, every swing, obstruction or not. There is nothing to read while it moves.
        //
        // Returns false when the prefab is not reachable (chunk not resident, pooled object not yet
        // handed out) or its colliders have been blanked. Bounds are in WORLD BLOCKS, not cell
        // local, so one measurement can be remembered and re-projected into any cell.
        // THE LEAF, WITH ITS ORIENTATION KEPT. Collider.bounds is an axis-aligned box in WORLD
        // space, which describes a leaf faithfully only when the leaf is axis-aligned. A cellar
        // door's leaf lies at an ANGLE: its world AABB is fat on all three axes, so the thin-one-way
        // test below rejected it, nothing else looked like a leaf either, and the whole prefab got
        // unioned into a box that filled all 2x2x2 cells ("occludes while standing in block on the
        // other side", user 2026-08-24). Carrying the oriented box lets a tilted plate be
        // recognised AND tested as the thin thing it actually is.
        internal struct LeafBox
        {
            public Vector3 Min, Max;        // world-block AABB - always valid, conservative
            public bool Oriented;           // Center/Half/Rot below describe the real plate
            public Vector3 Center, Half;
            public Quaternion Rot;
            public Transform Owner;         // the animated child; null once the prefab is recycled
        }

        internal static bool MeasureLeafWorld(BlockEntityData bed, out Vector3 wmin, out Vector3 wmax)
        {
            wmin = Vector3.zero; wmax = Vector3.zero;
            if (!MeasureLeafBoxes(bed, out LeafBox[] bx)) return false;
            wmin = bx[0].Min; wmax = bx[0].Max;
            for (int i = 1; i < bx.Length; i++)
            {
                wmin = Vector3.Min(wmin, bx[i].Min);
                wmax = Vector3.Max(wmax, bx[i].Max);
            }
            return true;
        }

        // A DOOR CAN HAVE MORE THAN ONE LEAF. Keeping only the biggest leaf-shaped collider was
        // right for an interior door and wrong for every double door, garage door and roll-up in
        // the game: the second leaf, or the other panels, were never recorded, so once cells began
        // trusting the measurement the cells behind THOSE read as open air. In game that is
        // "standing on the left side behind the door it occludes; one cell across, still behind the
        // door, it does not" (user 2026-08-25) - the left leaf was measured and the right one did
        // not exist as far as the model was concerned.
        //
        // Collect every collider that is leaf-shaped ON ITS OWN and keep them apart. This is not
        // the union that was removed: a rail or a frame still fails the shape test and is still
        // excluded, so two thin plates never become one fat box.
        private const int MaxLeaves = 12;

        internal static bool MeasureLeafBoxes(BlockEntityData bed, out LeafBox[] boxes)
        {
            boxes = null;
            if (bed == null || !bed.bHasTransform || bed.transform == null) return false;
            Collider[] cols;
            try { cols = bed.transform.GetComponentsInChildren<Collider>(true); }
            catch { return false; }
            if (cols == null || cols.Length == 0) return false;

            // includeInactive, and `enabled` is deliberately ignored: the game DISABLES a shut
            // door's colliders when a player is standing in the doorway (AnimatorDoorState's
            // obstacle check), which is how you can stand exactly where the leaf is. We want the
            // geometry, not whether it is currently pushing anybody.
            // A LEAF BELONGS TO ITS OWN DOOR. includeInactive is deliberate -- the game disables a
            // shut door's colliders while a player stands in the doorway -- but an INACTIVE child
            // has never been posed, so its bounds sit at the local origin and describe nothing.
            // The strict test rejected those as fat by luck; the relative test accepted them, and
            // a drawbridge at (485,38,-490) recorded its aperture at world (-2.5,1.3,0.0) and
            // therefore occluded nothing anywhere ("against the deck while the bridge is up, no
            // occlusion from either side"). Distance from the block is the check that catches it
            // whichever test let it through.
            Vector3 anchor;
            float leash;
            try
            {
                anchor = new Vector3(bed.pos.x, bed.pos.y, bed.pos.z);
                Block ab = Block.list[bed.blockValue.type];
                int dim = 1;
                if (ab != null && ab.isMultiBlock && ab.multiBlockPos != null)
                {
                    Vector3i d = ab.multiBlockPos.dim;
                    dim = Mathf.Max(d.x, Mathf.Max(d.y, d.z));
                }
                leash = dim + 3f;             // the whole multiblock, plus swing room
            }
            catch { anchor = Vector3.zero; leash = float.MaxValue; }

            List<LeafBox> found = null;
            var pending = new List<PendingLeaf>(8);   // rejects, for the relative second pass
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] == null) continue;
                Bounds wb = cols[i].bounds;                 // world, in RENDER space
                if (wb.size.sqrMagnitude <= 1e-6f) continue;

                // SHAPE-TEST THE PLATE, NOT ITS SHADOW. For a BoxCollider the true extents are its
                // own size scaled by the transform, which is rotation-independent; wb.size is that
                // box's world SHADOW and grows as it tilts. Testing the shadow is what made a
                // tilted cellar-door leaf unrecognisable. Non-box colliders have no cheap oriented
                // extent, so they keep the old test.
                Vector3 oe = wb.size; bool oriented = false;
                Vector3 oc = wb.center, oh = wb.extents; Quaternion orot = Quaternion.identity;
                BoxCollider bc = cols[i] as BoxCollider;
                if (bc != null)
                {
                    Vector3 ls = bc.transform.lossyScale;
                    oe = new Vector3(Mathf.Abs(bc.size.x * ls.x), Mathf.Abs(bc.size.y * ls.y),
                                     Mathf.Abs(bc.size.z * ls.z));
                    oh = oe * 0.5f;
                    oc = bc.transform.TransformPoint(bc.center);
                    orot = bc.transform.rotation;
                    oriented = true;
                }
                // Off its own door ⇒ an unposed inactive child, not this door's leaf.
                Vector3 wc = wb.center + Origin.position;
                if (leash < float.MaxValue
                    && (Mathf.Abs(wc.x - anchor.x) > leash
                     || Mathf.Abs(wc.y - anchor.y) > leash
                     || Mathf.Abs(wc.z - anchor.z) > leash)) continue;
                if (!IsLeafShaped(oe)) { pending.Add(new PendingLeaf { Box = MakeBox(wb, oriented, oc, oh, orot, cols[i].transform), Ext = oe }); continue; }
                if (found == null) found = new List<LeafBox>(4);
                found.Add(new LeafBox
                {
                    Min = wb.min + Origin.position,          // render space -> world blocks
                    Max = wb.max + Origin.position,
                    Oriented = oriented,
                    Center = oc + Origin.position,
                    Half = oh,
                    Rot = orot,
                    Owner = cols[i].transform
                });
                if (found.Count >= MaxLeaves) break;
            }

            // NEVER UNION THE WHOLE PREFAB. The union of a plate and its two side rails is a BOX,
            // and a box is on both sides of you at once - the isotropic lump the ear split exists
            // to remove, reintroduced by the measurement. Falling back to it anyway is how a
            // drawbridge (multiblock 5x6x7 = 210 cells) came to charge every cell on BOTH sides of
            // itself. If nothing here looks like a leaf we do not know where the door is, and
            // saying so lets the caller keep its own per-cell answer.
            // ── SECOND PASS: THIN RELATIVE TO ITS OWN SPAN ──────────────────────────────────
            // The absolute test asks for a plate under 0.25 blocks thick, which is right for a
            // door leaf and wrong for a drawbridge: its deck is timber, measured in game at 0.71
            // and 0.78 thick over spans of 3.5 and 3.2. It is a plate, just a chunky one, and
            // rejecting it dropped the whole 5x6x7 multiblock -- 210 cells -- into the isotropic
            // lump ("standing under the entire patch under the door occludes almost everything").
            //
            // Only reached when the strict pass found NOTHING, so every door that works today
            // takes the first path and cannot be changed by this. Against the drawbridge's own
            // measured colliders: the two deck sections pass at ratios 0.20 and 0.25, the housing
            // (6.19x4.99x1.74) fails at 0.35, and the edge rail (4.94x0.37x0.21) fails because a
            // rail is thin in TWO axes and its middle extent is nowhere near a block.
            if (found == null && pending.Count > 0)
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    Vector3 e = pending[i].Ext;
                    float big = -1f, mid = -1f, small = float.MaxValue;
                    for (int k = 0; k < 3; k++)
                    {
                        float v = e[k];
                        if (v > big) { mid = big; big = v; } else if (v > mid) mid = v;
                        if (v < small) small = v;
                    }
                    if (small >= 1.0f) continue;               // genuinely thick: not a plate
                    if (mid <= 1.0f) continue;                 // a rail, not a plate
                    if (small >= 0.30f * mid) continue;        // not thin against its own span
                    if (found == null) found = new List<LeafBox>(4);
                    found.Add(pending[i].Box);
                    if (found.Count >= MaxLeaves) break;
                }
                if (found != null)
                    CMMConfig.D($"[CMM] Aperture: {found.Count} chunky plate(s) accepted on the "
                              + "relative test (nothing passed the thin-plate test)");
            }

            if (found == null) { ReportNoLeaf(bed, cols); return false; }
            boxes = found.ToArray();
            return true;
        }

        private struct PendingLeaf { public LeafBox Box; public Vector3 Ext; }

        private static LeafBox MakeBox(Bounds wb, bool oriented, Vector3 oc, Vector3 oh, Quaternion orot, Transform owner)
            => new LeafBox
            {
                Min = wb.min + Origin.position,
                Max = wb.max + Origin.position,
                Oriented = oriented,
                Center = oc + Origin.position,
                Half = oh,
                Rot = orot,
                Owner = owner
            };

        // A DOOR THAT LEARNS NOTHING FALLS BACK TO THE ISOTROPIC LUMP, AND SAYS NOTHING ABOUT IT.
        // The drawbridge does exactly that: 5x6x7 = 210 cells of solid material on both sides of
        // itself and under the span, and the only evidence in the log was the ABSENCE of an
        // "Aperture learned" line -- which is not evidence anyone reads. Print the collider
        // inventory that failed the shape test so the next question is answerable instead of
        // guessable. Once per block type; a door that never learns would otherwise say it every
        // cell, every frame.
        private static readonly HashSet<int> _noLeafReported = new HashSet<int>();
        private static void ReportNoLeaf(BlockEntityData bed, Collider[] cols)
        {
            if (!CMMConfig.DebugLogging || bed == null) return;
            int type = 0;
            try { type = bed.blockValue.type; } catch { }
            if (!_noLeafReported.Add(type)) return;
            Block b = (type > 0 && type < Block.list.Length) ? Block.list[type] : null;
            var sb = new System.Text.StringBuilder();
            sb.Append("[CMM] Aperture NOT learned ").Append(b != null ? b.GetBlockName() : "?")
              .Append(" - ").Append(cols.Length).Append(" collider(s), none leaf-shaped:");
            int shown = 0;
            for (int i = 0; i < cols.Length && shown < 6; i++)
            {
                if (cols[i] == null) continue;
                Bounds wb = cols[i].bounds;
                if (wb.size.sqrMagnitude <= 1e-6f) continue;
                Vector3 oe = wb.size; string kind = cols[i].GetType().Name;
                BoxCollider bc = cols[i] as BoxCollider;
                if (bc != null)
                {
                    Vector3 ls = bc.transform.lossyScale;
                    oe = new Vector3(Mathf.Abs(bc.size.x * ls.x), Mathf.Abs(bc.size.y * ls.y),
                                     Mathf.Abs(bc.size.z * ls.z));
                }
                sb.Append($" [{kind} {oe.x:0.00}x{oe.y:0.00}x{oe.z:0.00}]");
                shown++;
            }
            sb.Append("  (need thinnest < 0.25 and middle > 0.50)");
            CMMConfig.D(sb.ToString());
        }

        private static bool MeasureLeafWorld(World world, Vector3i parent, out Vector3 wmin, out Vector3 wmax)
            => MeasureLeafWorld(world.ChunkCache != null ? world.ChunkCache.GetBlockEntity(parent) : null,
                                out wmin, out wmax);

        // A world-block AABB projected into one CELL's local 0..1, or null if it does not reach it.
        // Clip every leaf that reaches this cell. Returns null when none does, which is the
        // caller's signal that the door's material is elsewhere.
        private static Bounds[] ClipBoxes(LeafBox[] boxes, Vector3i cell)
        {
            if (boxes == null || boxes.Length == 0) return null;
            List<Bounds> outb = null;
            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i].Oriented)
                {
                    if (outb == null) outb = new List<Bounds>(16);
                    AppendOrientedClip(boxes[i], cell, outb);
                    continue;
                }
                Bounds[] c = ClipToCell(boxes[i].Min, boxes[i].Max, cell);
                if (c == null) continue;
                if (outb == null) outb = new List<Bounds>(boxes.Length);
                outb.AddRange(c);
            }
            return (outb == null || outb.Count == 0) ? null : outb.ToArray();
        }

        // A TILTED PLATE IS NOT ITS BOUNDING BOX. The cellar door's leaf is 0.07 thick and lies at
        // an angle, so its axis-aligned box fills the cell it is in -- and the half-voxel split
        // then finds "leaf" in BOTH halves of every axis and charges both. Measured in game:
        //
        //   cell 488,38,-484  cellarDoorDoubleWood  LEAF measured  split 0.95,0.86,0.64
        //   published  -X 8.7  -Y 8.7  -Z 20.2   +X 8.7  +Y 8.7  +Z 20.2
        //
        // Six faces, symmetric: a door occluding equally in every direction, which is the report
        // ("occluding speakers on both sides of the door"). The AABB was never the leaf; it was
        // the smallest box containing a diagonal.
        //
        // So follow the tilt. Subdivide the cell and keep the sub-boxes the ORIENTED plate actually
        // passes through -- a diagonal band, not a solid block. The split then sees material only
        // where the plate is. One cell per frame, at most 64 SAT tests, and the plate's thinness
        // means most are rejected on the first axis.
        private const int OrientedSubdiv = 4;
        private static void AppendOrientedClip(LeafBox lb, Vector3i cell, List<Bounds> outb)
        {
            const float step = 1f / OrientedSubdiv;
            Vector3 half = new Vector3(step, step, step) * 0.5f;
            for (int i = 0; i < OrientedSubdiv; i++)
                for (int j = 0; j < OrientedSubdiv; j++)
                    for (int k = 0; k < OrientedSubdiv; k++)
                    {
                        Vector3 mn = new Vector3(i * step, j * step, k * step);
                        Vector3 centreWorld = new Vector3(cell.x, cell.y, cell.z) + mn + half;
                        if (!ObbOverlapsBox(lb, centreWorld, half)) continue;
                        outb.Add(new Bounds(mn + half, new Vector3(step, step, step)));
                    }
        }

        private static Bounds[] ClipToCell(Vector3 wmin, Vector3 wmax, Vector3i cell)
        {
            Vector3 mn = wmin - new Vector3(cell.x, cell.y, cell.z);
            Vector3 mx = wmax - new Vector3(cell.x, cell.y, cell.z);
            for (int k = 0; k < 3; k++)
            {
                if (mx[k] < 0f || mn[k] > 1f) return null;  // does not reach this cell
                mn[k] = Mathf.Clamp01(mn[k]); mx[k] = Mathf.Clamp01(mx[k]);
                if (mx[k] - mn[k] < 0.001f)                 // keep a sliver the rasteriser can hit
                { float c = Mathf.Clamp(0.5f * (mn[k] + mx[k]), 0.0005f, 0.9995f); mn[k] = c - 0.0005f; mx[k] = c + 0.0005f; }
            }
            return new[] { new Bounds((mn + mx) * 0.5f, mx - mn) };
        }

        // ── THE APERTURE: FIXED GEOMETRY, MOVING NUMBER ─────────────────────────────────────────
        // A door's leaf cannot be read while it swings, so stop trying to. What a door contributes
        // acoustically is an OPENING — fixed position, fixed orientation — and a scalar saying how
        // much of it is currently blocked. The leaf's measured pose supplies the first, once, while
        // it can still be read; the openness ramp already supplied the second all along.
        //
        // This is what every shipped system does (Wwise portals, FMOD polygons, Project Acoustics'
        // per-portal open-area fraction). None of them voxelise the plate.
        //
        // THE FALLBACK THIS REPLACES WAS NOT A SAFETY NET, IT WAS A COIN FLIP. RotationLeafBounds
        // synthesised a slab across the middle of the voxel from the parent's rotation. Measured in
        // game on woodenGarageDoor3x3White, player stationary, one frame apart:
        //
        //     shut      LEAF measured    published  -Z 20.2   +Z  .      speaker 14.9 dB occluded
        //     nt=0.295  LEAF rotation    published  -Z  .     +Z 14.2    speaker  0.0 dB, WIDE OPEN
        //
        // The real leaf sits at z < 0.48 in its voxel (the prefab offsets are real: commercialGlass
        // Door 0,0,-0.525; houseSlidingDoorWhite 0,0,0.525; bathroomStallDoor 0.5,0,-0.5). The
        // assumed plane at 0.5, against a body at 0.480, landed on the OTHER SIDE of the listener,
        // so the door jumped from behind you to in front of you and released a far-side speaker by
        // 15 dB — and by symmetry, wrongly charged whatever was behind. That is both halves of
        // "running through a door that is still opening dips" in one mechanism.
        //
        // So a door with no remembered plane gets NO plane. Falling through to the isotropic lump
        // is blunt, but it is the same in every direction; a guessed plane is confidently wrong in
        // one, and one of the two rooms is always the one you are listening to.
        private struct LeafPlane
        {
            public int Type; public LeafBox[] Boxes;
            public Transform Owner;         // the animated child at the SHUT pose
            public Quaternion ShutRot;      // its local rotation there
            public Vector3 ShutPos;         // and its local position

            // -- THE APERTURE AS A RECTANGLE, AND THE LEAVES AS THINGS THAT COVER IT --------
            // Everything below exists to answer one question every tick: how many square metres
            // of this doorway are currently NOT blocked. See LeafOpenArea.
            public bool HasAp;
            public Vector3 ApCentre;        // world blocks
            public Vector3 ApU, ApV, ApN;   // unit basis; N is the leaf's thin axis
            public float ApHalfU, ApHalfV;
            public float ADoor;             // m^2 of the opening, measured once at the shut pose
            public float AShutOpen;         // ...and what still reads open when it is SHUT: seams
                                            // between panels, the hinge gap, rasterisation bias.
                                            // Subtracted from every later reading, so a shut door
                                            // is zero by construction on every door in the game
                                            // rather than by a tolerance somebody had to guess.
            public LeafRel[] Rel;           // one per box: how to re-pose it from its live owner
        }

        // A leaf box, expressed so it can be re-posed from whatever is actually animating it.
        // TWO independent ways, because a door has two ways of being driven and only one of them
        // shows up in the collider node's own transform. See LeafOpenArea.
        private struct LeafRel
        {
            public Transform Owner;         // the collider's node
            public Vector3 RelPos;          // box centre in Owner space, at the shut pose
            public Quaternion RelRot;       // box rotation relative to Owner, at the shut pose
            public Vector3 Half;            // half extents, constant
            public Vector3 OwnShutPos;      // Owner's own local pose at the shut pose, so "has it
            public Quaternion OwnShutRot;   // moved at all" is answerable with no learned range
            public Renderer Rend;           // the visual for this leaf, if one could be found
            public Vector3 RendShutCtr;     // and its bounds at the shut pose, render space
            public Vector3 RendShutSize;
        }

        // The per-TYPE learned travel range that used to live here is gone. It was a
        // process-lifetime ratchet shared by every instance of a door type, and it selected the
        // measurement AXIS as well as scaling it -- so one bad reading on one door permanently
        // disabled the clip-time fallback for all of them. See LeafOpenArea for what replaced it.

        private static readonly Dictionary<Vector3i, LeafPlane> _leafPlanes = new Dictionary<Vector3i, LeafPlane>();
        // Doors whose aperture provably cannot be read, so the clip clock is used from the FIRST
        // tick of every later swing. Learned per INSTANCE, never per type -- a type-wide latch is
        // the exact mistake the old learned range made. Costs one swing, once, per door.
        private static readonly Dictionary<Vector3i, bool> _deadMeasure = new Dictionary<Vector3i, bool>();

        // A LEAF IS THIN. That one gate does a surprising amount of work: it gives a hatch its
        // horizontal aperture by measurement rather than by a name list (a hatch is thin on Y, and
        // deriving the axis from rotation could only ever produce a vertical plane — a shut hatch
        // that does not block a floor), it keeps the deliberate prefab offsets, and it rejects a
        // prefab whose collider union swallowed the frame rather than recording the frame as a door.
        private static bool IsThinLeaf(Vector3 wmin, Vector3 wmax) => IsLeafShaped(wmax - wmin);

        // Thin ONE way, broad the other TWO. A leaf passes; a rail, a post or a handle is thin two
        // ways and fails; a frame or a housing is broad three ways and fails.
        private static bool IsLeafShaped(Vector3 e)
        {
            float big = -1f, mid = -1f, small = float.MaxValue;
            for (int k = 0; k < 3; k++)
            {
                float v = e[k];
                if (v > big) { mid = big; big = v; } else if (v > mid) mid = v;
                if (v < small) small = v;
            }
            return small < 0.25f && mid > 0.5f;
        }

        // Record the aperture. Callers must only offer a SHUT, AT-REST leaf — that is the one pose
        // that says where the doorway is. An open leaf is just as thin, and remembering it would
        // pin the aperture to wherever the leaf was parked.
        internal static void RememberLeaf(BlockEntityData bed, Vector3i parent, int type)
        {
            // Every box in here already passed the shape test on its own, so there is nothing
            // further to gate on: a prefab with no leaf simply does not get remembered.
            if (!MeasureLeafBoxes(bed, out LeafBox[] lbs)) return;
            // Bounded: doors are per-world and this only ever holds ones the listener has met, but
            // a long session across many POIs should not accumulate for ever. Dropping the lot is
            // fine — each one is re-learned the next time its door is stood in or opened.
            if (_leafPlanes.Count > 1024) { _leafPlanes.Clear(); _deadMeasure.Clear(); }
            bool isNew = !_leafPlanes.ContainsKey(parent);
            Transform owner = lbs[0].Owner;
            LeafPlane lp = new LeafPlane
            {
                Type = type, Boxes = lbs, Owner = owner,
                ShutRot = owner != null ? owner.localRotation : Quaternion.identity,
                ShutPos = owner != null ? owner.localPosition : Vector3.zero
            };
            BuildAperture(bed, ref lp);
            _leafPlanes[parent] = lp;
            if (isNew && CMMConfig.DebugLogging)
            {
                LeafBox l0 = lbs[0];
                Vector3 e = l0.Oriented ? l0.Half * 2f : (l0.Max - l0.Min);
                Vector3 wmin = l0.Min, wmax = l0.Max;
                for (int i = 1; i < lbs.Length; i++)
                { wmin = Vector3.Min(wmin, lbs[i].Min); wmax = Vector3.Max(wmax, lbs[i].Max); }
                Block ab = (type > 0 && type < Block.list.Length) ? Block.list[type] : null;
                int nRend = 0;
                if (lp.Rel != null)
                    for (int i = 0; i < lp.Rel.Length; i++) if (lp.Rel[i].Rend != null) nRend++;
                CMMConfig.D($"[CMM] Aperture learned {(ab != null ? ab.GetBlockName() : "?")} at {parent} " +
                            $"{lbs.Length} leaf/leaves ({nRend} with renderers), {(l0.Oriented ? "oriented" : "aabb")}, " +
                            $"first plate {e.x:0.00}x{e.y:0.00}x{e.z:0.00} " +
                            $"opening {lp.ADoor:0.00}m2 (shut leak {lp.AShutOpen:0.00}m2) " +
                            $"world {wmin.x:0.00},{wmin.y:0.00},{wmin.z:0.00} -> {wmax.x:0.00},{wmax.y:0.00},{wmax.z:0.00}");
            }
        }

        // ---- THE APERTURE, AND HOW MUCH OF IT IS OPEN -------------------------------------
        // Six models asked "how far through its travel is this door", normalised that against a
        // range LEARNED AT RUNTIME, and tuned by ear against thresholds expressed as a fraction
        // of it. Every one of them failed, and the measurement is why:
        //
        //   * the learned range was keyed by BLOCK TYPE while the pose it was measured from was
        //     per INSTANCE, so two garage doors in one POI shared -- and poisoned -- one number;
        //   * it was a process-lifetime ratchet that also SELECTED THE AXIS, so one bad reading
        //     (ironGarageDoorGrey recorded 83.9 degrees once) permanently disabled the working
        //     clip-time fallback for every door of that type and pinned openness near zero;
        //   * a fraction of the swing makes a 3 m and a 7 m roll-up behave identically in percent
        //     while their real openings differ by four metres, which is "every other rolling door
        //     feels linear" exactly;
        //   * and the thresholds were matched by SUBSTRING, so "garageDoor=" governed the 3x2 and
        //     the 4x3 together. Tuning one really was tuning the other.
        //
        // So stop asking for a fraction. Ask for the thing sound actually cares about: HOW MANY
        // SQUARE METRES OF THIS DOORWAY ARE OPEN. That number needs no learned range, cannot be
        // poisoned by another instance, is directly comparable between a hatch and a drawbridge,
        // and is the same question for a leaf that swings, a panel that slides and a curtain that
        // rolls -- all three merely cover the rectangle differently.
        //
        // The rectangle is measured ONCE, at the shut pose, where the leaves by definition fill
        // it. After that the leaves are re-posed from whatever is animating them, and the
        // rectangle is sampled to see what is left.
        private const float SlabHalf = 0.15f;      // a leaf is thin; give it this much grace on the plane
        private const float SamplePitch = 0.08f;   // metres between aperture samples

        private static void BuildAperture(BlockEntityData bed, ref LeafPlane lp)
        {
            lp.HasAp = false;
            LeafBox[] lbs = lp.Boxes;
            if (lbs == null || lbs.Length == 0) return;

            // Basis from the BIGGEST plate: its own thin axis is the doorway's normal. Taking it
            // from the plate rather than from world axes is what lets a tilted cellar door have a
            // rectangle at all.
            int bi = 0; float bestArea = -1f;
            for (int i = 0; i < lbs.Length; i++)
            {
                Vector3 e = lbs[i].Oriented ? lbs[i].Half * 2f : (lbs[i].Max - lbs[i].Min);
                float a = Mathf.Max(e.x, Mathf.Max(e.y, e.z)) * Mid3(e);
                if (a > bestArea) { bestArea = a; bi = i; }
            }
            LeafBox b0 = lbs[bi];
            Vector3 e0 = b0.Oriented ? b0.Half * 2f : (b0.Max - b0.Min);
            int thin = (e0.x <= e0.y && e0.x <= e0.z) ? 0 : (e0.y <= e0.z ? 1 : 2);
            Quaternion rot = b0.Oriented ? b0.Rot : Quaternion.identity;
            Vector3 ax = rot * Vector3.right, ay = rot * Vector3.up, az = rot * Vector3.forward;
            Vector3 n = thin == 0 ? ax : (thin == 1 ? ay : az);
            Vector3 u = thin == 0 ? ay : ax;
            Vector3 v = thin == 2 ? ay : az;

            // Extents: every plate's corners, projected onto that basis. A double door's two
            // leaves become one rectangle, which is the doorway -- that is the point.
            float uMin = float.MaxValue, uMax = float.MinValue;
            float vMin = float.MaxValue, vMax = float.MinValue;
            float nMin = float.MaxValue, nMax = float.MinValue;
            for (int i = 0; i < lbs.Length; i++)
            {
                LeafBox lb = lbs[i];
                Vector3 c = lb.Oriented ? lb.Center : (lb.Min + lb.Max) * 0.5f;
                Vector3 h = lb.Oriented ? lb.Half : (lb.Max - lb.Min) * 0.5f;
                Quaternion r = lb.Oriented ? lb.Rot : Quaternion.identity;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 o = new Vector3((k & 1) == 0 ? -h.x : h.x,
                                            (k & 2) == 0 ? -h.y : h.y,
                                            (k & 4) == 0 ? -h.z : h.z);
                    Vector3 pp = c + r * o;
                    float pu = Vector3.Dot(pp, u), pv = Vector3.Dot(pp, v), pn = Vector3.Dot(pp, n);
                    if (pu < uMin) uMin = pu;
                    if (pu > uMax) uMax = pu;
                    if (pv < vMin) vMin = pv;
                    if (pv > vMax) vMax = pv;
                    if (pn < nMin) nMin = pn;
                    if (pn > nMax) nMax = pn;
                }
            }
            float hu = 0.5f * (uMax - uMin), hv = 0.5f * (vMax - vMin);
            if (hu < 0.05f || hv < 0.05f) return;              // not a rectangle worth sampling

            lp.ApU = u; lp.ApV = v; lp.ApN = n;
            lp.ApHalfU = hu; lp.ApHalfV = hv;
            lp.ApCentre = u * (0.5f * (uMin + uMax)) + v * (0.5f * (vMin + vMax)) + n * (0.5f * (nMin + nMax));
            lp.ADoor = 4f * hu * hv;

            // Each plate, expressed relative to whatever might move it. BOTH routes are recorded
            // now, because which one is live cannot be known until the door actually swings.
            var rel = new LeafRel[lbs.Length];
            Vector3 org = Origin.position;
            for (int i = 0; i < lbs.Length; i++)
            {
                LeafBox lb = lbs[i];
                Vector3 c = lb.Oriented ? lb.Center : (lb.Min + lb.Max) * 0.5f;
                Vector3 h = lb.Oriented ? lb.Half : (lb.Max - lb.Min) * 0.5f;
                Quaternion r = lb.Oriented ? lb.Rot : Quaternion.identity;
                Vector3 cRender = c - org;
                Transform ow = lb.Owner;
                LeafRel lr = new LeafRel { Owner = ow, Half = h };
                if (ow != null)
                {
                    lr.RelPos = ow.InverseTransformPoint(cRender);
                    lr.RelRot = Quaternion.Inverse(ow.rotation) * r;
                    lr.OwnShutPos = ow.localPosition;
                    lr.OwnShutRot = ow.localRotation;
                }
                else { lr.RelPos = cRender; lr.RelRot = r; lr.OwnShutRot = Quaternion.identity; }
                lr.Rend = FindLeafRenderer(bed != null ? bed.transform : null, cRender, h);
                if (lr.Rend != null)
                {
                    Bounds rb = lr.Rend.bounds;
                    lr.RendShutCtr = rb.center; lr.RendShutSize = rb.size;
                }
                rel[i] = lr;
            }
            lp.Rel = rel;
            lp.HasAp = true;

            // AND WHAT READS OPEN WHILE IT IS SHUT. The plates are at their shut pose right now,
            // so whatever the rasteriser finds here is the door's own seams -- and on a four-panel
            // garage door or a pair of leaves with a hinge gap that is not a small number. Without
            // this a shut door would publish a permanent leak and every knob would be tuned around
            // it. Measured per instance, so it costs nothing to be exactly right.
            int shutSrc = 0;
            for (int i = 0; i < rel.Length && shutSrc == 0; i++)
            {
                if (rel[i].Owner != null) shutSrc = 1;
                else if (rel[i].Rend != null) shutSrc = 2;
            }
            lp.AShutOpen = shutSrc == 0 ? 0f : Mathf.Min(RasterOpen(ref lp, false), lp.ADoor * 0.5f);
        }

        private static float Mid3(Vector3 e)
        {
            float big = Mathf.Max(e.x, Mathf.Max(e.y, e.z));
            float small = Mathf.Min(e.x, Mathf.Min(e.y, e.z));
            return e.x + e.y + e.z - big - small;
        }

        // A CURTAIN IS NOT ITS COLLIDER, AND IT IS NOT ITS COLLIDER'S ANCESTOR EITHER. Walking up
        // from the collider node found renderers on the closet doors and NONE on the garage doors
        // or any roll-up -- the very doors that need one, because their collider node does not
        // move. Their visuals live in a sibling subtree, so search the whole prefab and match each
        // plate to the renderer that is actually sitting on it.
        private static readonly List<Renderer> _rendScratch = new List<Renderer>(32);
        private static Renderer FindLeafRenderer(Transform root, Vector3 centreRender, Vector3 half)
        {
            if (root == null) return null;
            _rendScratch.Clear();
            try { root.GetComponentsInChildren<Renderer>(true, _rendScratch); }
            catch { return null; }
            float plateVol = Mathf.Max(0.001f, 8f * half.x * half.y * half.z);
            Renderer best = null; float bestD = float.MaxValue;
            for (int i = 0; i < _rendScratch.Count; i++)
            {
                Renderer r = _rendScratch[i];
                if (r == null) continue;
                Bounds b;
                try { b = r.bounds; } catch { continue; }
                Vector3 e = b.size;
                if (e.sqrMagnitude <= 1e-6f) continue;
                // Not the whole building, and not a bolt. A leaf's own visual is the same order of
                // size as the leaf; anything eight times its volume is the frame or the prefab.
                float vol = Mathf.Max(0.001f, e.x * e.y * e.z);
                if (vol > plateVol * 8f) continue;
                float d = Vector3.Distance(b.center, centreRender);
                if (d < bestD && d < 2f) { bestD = d; best = r; }
            }
            return best;
        }

        internal static string LastPlateTag = "";
        private static readonly System.Text.StringBuilder _plateSb = new System.Text.StringBuilder(16);
        private static bool[] _useRend;
        private static Vector3[] _poseC, _poseH;
        private static Quaternion[] _poseR;
        private static bool[] _poseOk;

        // A leaf is thinner than the grace the plane needs. Without this a plate lying exactly in
        // the aperture can miss its own samples to float error and read as a wide open door.
        private static Vector3 ThickenThin(Vector3 h)
        {
            int k = (h.x <= h.y && h.x <= h.z) ? 0 : (h.y <= h.z ? 1 : 2);
            if (h[k] < SlabHalf) h[k] = SlabHalf;
            return h;
        }

        // The doorway's total area as measured at the shut pose, or 0 if it was never measured.
        private static float ApertureArea(Vector3i parent, int type)
        {
            if (_leafPlanes.TryGetValue(parent, out LeafPlane p) && p.Type == type && p.HasAp) return p.ADoor;
            return 0f;
        }

        // Square metres of this doorway currently unblocked. "src" reports which route answered:
        // 1 = the collider's own transform, 2 = its renderer's bounds, 0 = nothing moved and the
        // caller must fall back to the clip clock. "prefer" latches a route for the rest of a
        // swing so it cannot flap halfway through one.
        private static bool LeafOpenArea(Vector3i parent, int type, int prefer,
                                         out float aOpen, out float aDoor, out int src)
        {
            aOpen = 0f; aDoor = 0f; src = 0;
            if (!_leafPlanes.TryGetValue(parent, out LeafPlane p) || p.Type != type
                || !p.HasAp || p.Rel == null || p.Rel.Length == 0) return false;
            aDoor = p.ADoor;

            // PER PLATE, NOT PER DOOR. A rig can have one child that jitters and another that
            // carries the actual leaf, and choosing one route for the whole door then let the
            // jittering one decide -- which is how both closet doors came to report src=xform and
            // an aperture that never opened: the route said "the transforms are live", and the
            // plates that cover the doorway sat still at their shut pose for the entire swing.
            int nrel = p.Rel.Length;
            if (_useRend == null || _useRend.Length < nrel) _useRend = new bool[nrel];
            _plateSb.Length = 0;
            bool xformMoved = false, rendMoved = false, haveX = false, haveR = false;
            for (int i = 0; i < nrel; i++)
            {
                LeafRel lr = p.Rel[i];
                bool mx = false, mr = false;
                if (lr.Owner != null)
                {
                    haveX = true;
                    if (Vector3.Distance(lr.Owner.localPosition, lr.OwnShutPos) > 0.02f
                        || Quaternion.Angle(lr.Owner.localRotation, lr.OwnShutRot) > 1.0f) mx = true;
                }
                if (lr.Rend != null)
                {
                    haveR = true;
                    Bounds rb;
                    try { rb = lr.Rend.bounds; }
                    catch { _useRend[i] = false; xformMoved |= mx; continue; }
                    if (Vector3.Distance(rb.center, lr.RendShutCtr) > 0.05f
                        || Vector3.Distance(rb.size, lr.RendShutSize) > 0.05f) mr = true;
                }
                // THE RENDERER IS WHAT THE PLAYER SEES, AND ON MOST DOOR RIGS THE COLLIDER LIES.
                // The log settles this. Of fourteen door types, the only ones whose collider
                // transform tracks the visible leaf are the garage doors and the drawbridge.
                // Every hinged door does one of two wrong things instead: the closet doors, iron
                // door and iron hatch FREEZE their collider at the shut pose for the whole swing
                // (aperture reads 100% covered from end to end -- a sealed door standing visibly
                // open), while vaultDoor01, vaultHatch01 and the cellar door SNAP theirs to the
                // target pose the moment the latch flips (aperture reads fully open at nt=0.1 and
                // stays there). Both look like motion to a "did this transform move" test, which
                // is why preferring the transform could never have worked.
                //
                // So prefer the renderer whenever it is moving. It is the thing being animated,
                // it is what the door looks like, and the player is judging by eye and ear. The
                // collider transform is the fallback for rigs with no renderer of their own --
                // the garage doors and the drawbridge, which are exactly the ones it reads right.
                _useRend[i] = mr;
                xformMoved |= mx; rendMoved |= mr;
                _plateSb.Append(mx ? 'x' : (mr ? 'r' : '.'));
            }
            LastPlateTag = _plateSb.ToString();
            if (prefer == 1 && haveX) src = 1;
            else if (prefer == 2 && haveR) src = 2;
            else src = xformMoved ? 1 : (rendMoved ? 2 : 0);
            if (src == 0) return false;

            float raw = RasterOpen(ref p, true);
            float span = Mathf.Max(0.01f, aDoor - p.AShutOpen);
            aOpen = Mathf.Clamp(raw - p.AShutOpen, 0f, span);
            aDoor = span;               // report the range the door can actually travel over
            return true;
        }

        // The rasteriser: how many square metres of the aperture no plate is currently standing in.
        private static float RasterOpen(ref LeafPlane p, bool perPlate)
        {
            // Pose the plates as they are RIGHT NOW, in render space (where transforms live).
            int nb = p.Rel.Length;
            if (_poseC == null || _poseC.Length < nb)
            {
                _poseC = new Vector3[nb]; _poseH = new Vector3[nb];
                _poseR = new Quaternion[nb]; _poseOk = new bool[nb];
            }
            for (int i = 0; i < nb; i++)
            {
                LeafRel lr = p.Rel[i];
                _poseOk[i] = false;
                try
                {
                    bool viaRend = perPlate && _useRend != null && i < _useRend.Length && _useRend[i];
                    if (!viaRend && lr.Owner != null)
                    {
                        Transform ow = lr.Owner;
                        _poseC[i] = ow.TransformPoint(lr.RelPos);
                        _poseR[i] = ow.rotation * lr.RelRot;
                        _poseH[i] = ThickenThin(lr.Half);
                    }
                    else if (lr.Rend != null)
                    {
                        Bounds rb = lr.Rend.bounds;
                        _poseC[i] = rb.center;
                        _poseR[i] = Quaternion.identity;
                        _poseH[i] = ThickenThin(rb.extents);
                    }
                    else
                    {
                        // No owner and no renderer: it can only be where it was measured.
                        _poseC[i] = lr.RelPos;
                        _poseR[i] = lr.RelRot;
                        _poseH[i] = ThickenThin(lr.Half);
                    }
                    _poseOk[i] = true;
                }
                catch { }
            }

            int nu = Mathf.Clamp(Mathf.CeilToInt(2f * p.ApHalfU / SamplePitch), 4, 64);
            int nv = Mathf.Clamp(Mathf.CeilToInt(2f * p.ApHalfV / SamplePitch), 4, 32);
            Vector3 org = Origin.position;
            Vector3 baseP = p.ApCentre - org;
            int covered = 0;
            for (int iu = 0; iu < nu; iu++)
            {
                float fu = (iu + 0.5f) / nu * 2f - 1f;
                Vector3 rowP = baseP + p.ApU * (fu * p.ApHalfU);
                for (int iv = 0; iv < nv; iv++)
                {
                    float fv = (iv + 0.5f) / nv * 2f - 1f;
                    Vector3 pt = rowP + p.ApV * (fv * p.ApHalfV);
                    for (int i = 0; i < nb; i++)
                    {
                        if (!_poseOk[i]) continue;
                        Vector3 d = Quaternion.Inverse(_poseR[i]) * (pt - _poseC[i]);
                        Vector3 h = _poseH[i];
                        if (Mathf.Abs(d.x) <= h.x && Mathf.Abs(d.y) <= h.y && Mathf.Abs(d.z) <= h.z)
                        { covered++; break; }
                    }
                }
            }
            return Mathf.Max(0f, p.ADoor * (1f - (float)covered / (nu * nv)));
        }

        // The reader's one entry point for "where is this door's material, inside this cell".
        internal static Bounds[] LeafBoundsForCell(World world, Vector3i cell, Vector3i parent, int type,
                                                  bool animating, bool shut, out string how)
        {
            how = null;
            LeafPlane held; bool haveHeld = _leafPlanes.TryGetValue(parent, out held) && held.Type == type;

            // MID-SWING THE APERTURE IS THE ANSWER, even on the doors whose colliders survive.
            // Where the opening is does not move; only how much of it is blocked does, and that is
            // the ramp. Reading the leaf here would price the same swing twice — once as geometry
            // leaving the voxel, once as the scalar shrinking.
            if (animating && haveHeld) { how = "held"; return ClipBoxes(held.Boxes, cell); }

            BlockEntityData bed = world.ChunkCache != null ? world.ChunkCache.GetBlockEntity(parent) : null;
            if (MeasureLeafBoxes(bed, out LeafBox[] bx))
            {
                if (!animating && shut) RememberLeaf(bed, parent, type);
                how = "measured";
                return ClipBoxes(bx, cell);
            }
            if (haveHeld) { how = "held"; return ClipBoxes(held.Boxes, cell); }
            return null;
        }

        // ── A DOOR IS A LEAF, NOT A BLOCK OF MATERIAL THE SIZE OF ITS MULTIBLOCK ────────────────
        // Every cell of a multiblock door answers the same question the same way, because
        // Block.IsMovementBlocked redirects a CHILD to its PARENT and returns the parent's answer.
        // So a shut 3x2x2 garage door charged all twelve cells, a 2x2x2 cellar door all eight, and
        // a 5x6x7 drawbridge all TWO HUNDRED AND TEN - both sides of itself and the space beneath
        // it (user, 2026-08-24). The leaf is one thin plate somewhere inside that volume; the rest
        // is the air it swings through.
        //
        // Once the aperture has been learned we know exactly where the plate is, so a cell it does
        // not reach is AIR and should say so. Pure data - a dictionary probe and a box test, no
        // Unity calls and no allocation - because this is called per cell from the geometry fill.
        // Returns false when there is no remembered plane: not knowing is not the same as knowing
        // it is absent, and the caller must keep its own answer.
        internal static bool LeafKnownAbsentFromCell(Vector3i cell, Vector3i parent, int type)
        {
            if (!_leafPlanes.TryGetValue(parent, out LeafPlane p) || p.Boxes == null) return false;
            if (p.Type != type) return false;
            // AIR ONLY IF EVERY LEAF MISSES. One leaf of a double door covering this cell is
            // enough to make it material; asking only the biggest is what left the cells behind
            // the other leaf reading as open air.
            for (int i = 0; i < p.Boxes.Length; i++)
                if (LeafReachesCell(p.Boxes[i], cell)) return false;
            return true;
        }

        private static bool LeafReachesCell(LeafBox lb, Vector3i cell)
        {
            // Cheap conservative reject on the AABB first; it is exact for an axis-aligned leaf.
            if (lb.Max.x <= cell.x || lb.Min.x >= cell.x + 1f) return false;
            if (lb.Max.y <= cell.y || lb.Min.y >= cell.y + 1f) return false;
            if (lb.Max.z <= cell.z || lb.Min.z >= cell.z + 1f) return false;
            if (!lb.Oriented) return true;
            // Tilted plate: the AABB overlaps but the plate itself may not.
            return ObbOverlapsCell(lb, cell);
        }

        // SAT: axis-aligned unit cell vs oriented box. Both are convex boxes, so 3 cell axes +
        // 3 box axes + 9 cross products decide it.
        private static bool ObbOverlapsCell(LeafBox lb, Vector3i cell)
            => ObbOverlapsBox(lb, new Vector3(cell.x + 0.5f, cell.y + 0.5f, cell.z + 0.5f),
                              new Vector3(0.5f, 0.5f, 0.5f));

        private static bool ObbOverlapsBox(LeafBox lb, Vector3 ca, Vector3 ce)
        {
            Vector3 d = lb.Center - ca;
            Vector3[] ax = { lb.Rot * Vector3.right, lb.Rot * Vector3.up, lb.Rot * Vector3.forward };
            float[] he = { lb.Half.x, lb.Half.y, lb.Half.z };

            for (int i = 0; i < 3; i++)                       // cell's own axes
            {
                Vector3 L = i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;
                if (Separated(L, d, ce, ax, he)) return false;
            }
            for (int i = 0; i < 3; i++)                       // box's own axes
                if (Separated(ax[i], d, ce, ax, he)) return false;
            for (int i = 0; i < 3; i++)                       // cross products
            {
                Vector3 L0 = i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward;
                for (int j = 0; j < 3; j++)
                {
                    Vector3 L = Vector3.Cross(L0, ax[j]);
                    if (L.sqrMagnitude < 1e-8f) continue;     // parallel: covered by a face axis
                    if (Separated(L.normalized, d, ce, ax, he)) return false;
                }
            }
            return true;
        }

        private static bool Separated(Vector3 L, Vector3 d, Vector3 ce, Vector3[] ax, float[] he)
        {
            float rCell = ce.x * Mathf.Abs(L.x) + ce.y * Mathf.Abs(L.y) + ce.z * Mathf.Abs(L.z);
            float rBox = he[0] * Mathf.Abs(Vector3.Dot(L, ax[0]))
                       + he[1] * Mathf.Abs(Vector3.Dot(L, ax[1]))
                       + he[2] * Mathf.Abs(Vector3.Dot(L, ax[2]));
            return Mathf.Abs(Vector3.Dot(L, d)) > rCell + rBox;
        }

        // Is this door mid-swing? Main thread only, same discipline as every other _doors read.
        internal static bool IsDoorAnimating(Vector3i parent) => _doors.ContainsKey(parent);

        // OPPORTUNISTIC CAPTURE, FROM THE CELL READER. The other two capture points are "you are
        // standing in it" and "it has just begun to open", and neither fires for the door you have
        // never touched — which is exactly "run through a door somebody else already opened", the
        // case that started this. The reader walks every cell in the window anyway, so let it
        // learn on the way past: a door beside you within a rolling near-refresh, everything else
        // within a full sweep.
        //
        // Once per door for the process. The prefab walk allocates a Collider[], so the guards run
        // cheapest-first — a type dictionary probe, then the have-we-already probe — and after the
        // first success there is nothing new to find. Main thread only (Transmission is reached
        // from FrameStep and PublishEarCell, both of which read the world).
        internal static void LearnAperture(World world, Vector3i cell, BlockValue bv)
        {
            Block b = bv.Block;
            if (b == null || !IsSwingingLeaf(b, bv.type)) return;
            Vector3i parent = bv.ischild && b.multiBlockPos != null
                            ? b.multiBlockPos.GetParentPos(cell, bv) : cell;
            if (_leafPlanes.TryGetValue(parent, out LeafPlane have) && have.Type == bv.type) return;
            if (_doors.ContainsKey(parent)) return;         // mid-swing: not a pose worth keeping
            bool shut;
            try { shut = b.IsMovementBlocked(world, cell, bv, BlockFace.None); } catch { return; }
            if (!shut) return;                              // only a shut leaf defines the doorway
            RememberLeaf(world.ChunkCache != null ? world.ChunkCache.GetBlockEntity(parent) : null,
                         parent, bv.type);
        }

        private static Bounds[] ShapeBounds(Block b, BlockValue bv)
        {
            if (b == null || b.shape == null || b.shape is BlockShapeModelEntity) return null;
            if (b.shape.IsSolidCube) return null;
            try { Bounds[] a = b.shape.GetBounds(bv); return (a != null && a.Length > 0) ? a : null; }
            catch { return null; }
        }

        // The reader's entry point: what a cell is made of, and where inside the voxel it sits.
        internal static float[] DebugCellCover(BlockValue bv)
        {
            if (bv.isair) return FullCover;
            return CoverOf(bv.Block, bv);
        }

        // Does this block's geometry SWING OUT of its voxel when it opens? Doors, hatches, garage doors
        // and drawbridges do; a closet, a shelf, a fridge and a bookcase do not. That distinction is what
        // licenses the "not movement-blocked ⇒ free air" shortcut, and applying it to every model entity
        // turned furniture into doorways (see CellLoss).
        //
        // Asked of BlockPlacementHelper rather than a class or a name: doors are Class=CompositeTileEntity
        // in 3.x and BlockDoor in A20/A21, so no single Block subclass identifies them across versions --
        // but blocks.xml has said Place="Door" for all 78 of them (plus everything that Extends one) since
        // A20, and BlockPlacementDoor/BlockPlacementDrawBridge exist in every supported assembly.
        private static readonly Dictionary<int, bool> _swingCache = new Dictionary<int, bool>();
        // Public face of IsSwingingLeaf, for the reader. NOT gated on IsMovementBlocked: that flips
        // at the LATCH (the hook is a postfix on TEFeatureDoor.SetOpen), so gating on it switched
        // the leaf off for the whole of every opening swing — the cell then fell back to blocking
        // uniformly, which is "running through a door that is still opening occludes". Where the
        // leaf actually is during the swing is now measured, so there is nothing to gate.
        internal static bool IsSwingingLeafDebug(Block b, BlockValue bv) => IsSwingingLeaf(b, bv.type);

        private static bool IsSwingingLeaf(Block b, int type)
        {
            if (_swingCache.TryGetValue(type, out bool s)) return s;
            BlockPlacement p = null;
            try { p = b.BlockPlacementHelper; } catch { /* modded block without one ⇒ not a door */ }
            // The placement helper catches ordinary doors and drawbridges -- the only two door
            // helpers the game has -- but not every openable declares one. BlockPoweredDoor and
            // BlockTrapDoor are their own classes and can carry a different Place, and without
            // them a trapdoor standing open read as SOLID: its matter has plainly left the voxel
            // and the model insisted otherwise. The class test is the backstop for exactly the
            // things the helper test cannot see.
            s = p is BlockPlacementDoor || p is BlockPlacementDrawBridge
                || b is BlockPoweredDoor || b is BlockTrapDoor;
            _swingCache[type] = s;
            return s;
        }

        private static float SolidLoss(Block b, BlockValue bv)
        {
            float m = MaterialLoss(b.blockMaterial != null ? b.blockMaterial.SurfaceCategory : null);
            // A VARIANT THAT FILLS ITS VOXEL IS A WALL, whatever the shape class says. Gating this on
            // shape.IsSolidCube charged woodShapes:cube -- the commonest structural block in the game --
            // HALF a wall: 0.5 material became 0.25, which the router reads as 56% OPEN. Straight from
            // `cmm acoustics column`, standing under a speaker with a floor between:
            //
            //     -250,43,-565  woodShapes:cube  loss 0.250  open 0.56  BlockShapeNew partial
            //
            // Walls and floors built from shapes.xml were therefore half-transparent everywhere, which
            // is the bleeding through both -- and it applies to the SHIPPED model too, since this is
            // the shared block reader.
            // NO PARTIAL DISCOUNT. This used to halve a partial shape's material, or take it to a
            // fifth when the artists' lightOpacity marker said the shape passes light — 526 of the
            // 2263 shapes. That marker lumps plates, poles, frames and holed cubes together, and
            // they are not acoustically alike: a plate crossed face-on is a wall, a pole is
            // nothing, and a holed cube is whatever its hole is. One multiplier could not say
            // which, so it was wrong for two of the three whichever value it took.
            //
            // Thickness does not enter into it — the shipped rule is that a sliver plate and a full
            // block of the same material block the same — and the rest is WHERE the shape sits,
            // which CoverOf() answers per axis and per half from the block's own bounds. So the
            // material is charged at full strength here and the geometry is priced once, by the
            // reader, from evidence rather than from a marker.

            return m;
        }

        // ── Damage-state acoustics (holes in doors let sound through) ────────────────────────────────
        // Doors/hatches/gates swap VISIBLE damage meshes via the block's `MeshDamage` property —
        // "name,health" pairs, e.g. oldWoodDoor "Door/DMG0,1000,Door/DMG1,750,Door/DMG2,500,Door/DMG3,250,-,1".
        // The game's rule (BlockShapeModelEntity.GetDamageStateIndex, identical A20/A21/3.0): with
        // remaining = MaxDamage − damage, state i is active while remaining > Health[i+1], else the last.
        // A final "-" name means the mesh is HIDDEN at that state (≤1 hp: the door is visually gone).
        // We key the acoustic opening off the SAME data, so sound tracks exactly what the player sees:
        // state 0 (pristine) = shut; later states open with the SQUARE ROOT of the visible-state
        // fraction — aperture acoustics: once a panel has ANY through-hole, transmission is dominated
        // by the opening, not the remaining material, so the FIRST damaged state already leaks about
        // half (an s² curve made mid-state doors inaudibly different from intact — user-reported).
        // Caps at MaxDamageOpen (frame + remaining panels still shadow a little); "-" mesh ≈ no door.
        private const float MaxDamageOpen = 0.75f;   // opening at the last VISIBLE damage state
        private const float GoneOpen      = 0.95f;   // opening once the mesh is hidden ("-" state)

        private sealed class DmgStates { public float[] Health; public bool LastGone; public bool NoHoles; }
        private static readonly Dictionary<int, DmgStates> _dmgStates = new Dictionary<int, DmgStates>();

        // Blocks whose damaged meshes DENT rather than HOLE (user-verified by eye, art-side fact the
        // XML can't express): their visible damage states stay acoustically shut — only the hidden-mesh
        // "-" (frame) state opens. Name-prefix match; extend as more no-hole art is spotted in game.
        private static readonly string[] _noHoleMeshPrefixes = { "ironHatch" };

        private static DmgStates GetDmgStates(Block b, int type)
        {
            if (_dmgStates.TryGetValue(type, out DmgStates ds)) return ds;
            // Through the compat shim: the Values FIELD is a different type on 2.6, so a typed
            // access does not resolve there -- and it threw inside the field reader, which is
            // caught and logged at debug level, so the whole occlusion field silently stayed
            // empty and every speaker went inaudible.
            string v = EmitterCompat.BlockProperty(b, "MeshDamage");
            if (!string.IsNullOrEmpty(v))
            {
                string[] a = v.Split(',');
                int n = a.Length / 2;              // pairs; a trailing lone "," parses clean
                if (n >= 2)                        // a single-state door (DMG0 only) never opens
                {
                    float[] h = new float[n];
                    bool ok = true;
                    for (int i = 0; i < n && ok; i++)
                        ok = float.TryParse(a[i * 2 + 1].Trim(), System.Globalization.NumberStyles.Float,
                                            System.Globalization.CultureInfo.InvariantCulture, out h[i]);
                    if (ok)
                    {
                        string bn = b.GetBlockName() ?? "";
                        bool noHoles = false;
                        for (int i = 0; i < _noHoleMeshPrefixes.Length && !noHoles; i++)
                            noHoles = bn.StartsWith(_noHoleMeshPrefixes[i], System.StringComparison.OrdinalIgnoreCase);
                        ds = new DmgStates { Health = h, LastGone = a[(n - 1) * 2].Trim() == "-", NoHoles = noHoles };
                        CMMConfig.D($"[CMM] DoorDmg {bn} states={n} lastGone={ds.LastGone} noHoles={ds.NoHoles}");
                    }
                }
            }
            _dmgStates[type] = ds;                 // null = no damage states, cached too
            return ds;
        }

        private static float DamageOpenness(Block b, BlockValue bv, World world, int x, int y, int z)
        {
            // Cheap pre-filter before any dictionary work: only model-entity shapes carry MeshDamage,
            // and a pristine non-multiblock cell can't be open (children hold no damage — the parent does).
            if (!(bv.damage > 0 || bv.ischild) || !(b.shape is BlockShapeModelEntity)) return 0f;
            DmgStates ds = GetDmgStates(b, bv.type);
            if (ds == null) return 0f;

            int dmg = bv.damage;
            if (bv.ischild)   // multiblock (garage doors, doubles): damage lives on the parent cell
            {
                Vector3i parent = b.multiBlockPos.GetParentPos(new Vector3i(x, y, z), bv);
                dmg = world.GetBlock(parent.x, parent.y, parent.z).damage;
                if (dmg <= 0) return 0f;
            }

            float remaining = b.MaxDamage - dmg;
            int last = ds.Health.Length - 1;
            int idx = last;
            for (int i = 0; i < last; i++)
                if (remaining > ds.Health[i + 1]) { idx = i; break; }
            if (idx == 0) return 0f;
            if (ds.LastGone && idx == last) return GoneOpen;     // mesh hidden — effectively no door
            if (ds.NoHoles) return 0f;                           // dented-not-holed art: shut until the mesh hides
            int lastVisible = ds.LastGone ? last - 1 : last;     // fraction over the states you can SEE
            if (lastVisible <= 0) return 0f;
            float s = (float)idx / lastVisible;
            return MaxDamageOpen * Mathf.Sqrt(s);
        }

        // ── Door animation tracking (Tier 2: occlusion follows the physical swing) ───────────────────
        // Doors the hook flagged as mid-swing, keyed by PARENT block pos. Openness is the ACOUSTIC value
        // (0 = shut, 1 = open to the ear); sampled from the door's Unity Animator each occlusion tick
        // (RefreshDoorAnim, main thread — the flood is main-thread), then dropped once the swing settles so
        // the cheap at-rest see-through path takes back over.
        private struct DoorAnim
        {
            public bool Opening; public float Openness; public bool Started; public int Ticks;
            public float Span, SpanB;                       // the two largest multiblock dims, metres
            public int Type;                                // block type — names the door in the DoorTrace log
            public float StartedAt;                         // realtime the swing was registered (trace time base)

            // The aperture measurement, carried across the swing. Src latches which route is
            // reading this door (1 transform, 2 renderer, 3 clip clock) so it cannot change
            // halfway through one. PrevOpen/PrevAt are the per-tick derivative the lead rides on,
            // which is what replaced dividing a fixed lead by an animator clip length that was
            // observed to read 0.00 on real doors.
            public int Src, LastSrc;
            public float ADoor, AOpen;
            public float PrevOpen, PrevAt;
            // A DOOR THAT NEVER MOVES ITS OWN APERTURE IS NOT A SHUT DOOR, IT IS A BAD READING.
            // Both closet doors reported a live transform and an aperture that stayed 100% covered
            // for the whole swing, which as a published number means "sealed" -- a hard regression
            // on a door you can see standing open. Watch the spread; if the measurement never
            // moves while the clip plainly does, it is dead and the clip clock takes over.
            public float MinOpen, MaxOpen; public bool DeadMeasure;
        }
        private static readonly Dictionary<Vector3i, DoorAnim> _doors = new Dictionary<Vector3i, DoorAnim>();
        private static readonly HashSet<int> _doorTypes = new HashSet<int>();   // block types the hook has seen animate
        private static readonly List<Vector3i> _doorPrune = new List<Vector3i>();
        private static int _doorCellsBlended;       // door-branch cell reads, THIS tick (still counting)
        private static int _doorCellsBlendedPrev;   // ...and the finished count, which is the one to print:
                                                    // DoorTrace is emitted from inside RefreshDoorAnim, which
                                                    // has just zeroed the live counter, so printing that always
                                                    // read `cells=0` and looked like the branch never ran.

        // The door hook can fire OFF the main thread (7D2D runs door animation/physics on job threads), so it
        // must NOT touch _doors/_doorTypes directly — doing so raced RefreshDoorAnim's enumeration and the
        // flood's reads ("Collection was modified", which aborted the whole emitter tick). Instead it enqueues
        // here under a lock; RefreshDoorAnim drains this on the main thread, so _doors/_doorTypes stay
        // single-threaded for every read (CellLoss) and iteration.
        private struct PendingDoor { public int Type; public Vector3i Pos; public bool Opening; }
        private static readonly List<PendingDoor> _doorPending = new List<PendingDoor>();
        private static readonly object _doorLock = new object();

        // ── THE LATCH FLIPS BEFORE WE KNOW THE DOOR MOVED ───────────────────────────────────────
        // The hook is a POSTFIX on TEFeatureDoor.SetOpen, so by the time it runs isOpen has already
        // flipped, and the door does not enter _doors until RefreshDoorAnim drains this queue on the
        // main thread. In that window CellLoss finds nothing in _doors and falls through to the
        // at-rest branch, whose only source of truth is IsMovementBlocked == !isOpen -- the value
        // that just flipped and that the leaf has not caught up with yet.
        //
        // That is the reported "quickly block a little sound, then not, then starts to dampen as
        // they close": one or more reads charging a CLOSING door as fully shut, then the drain
        // landing with Openness = 1 and dropping it to nothing, then the real ramp. The OPENING
        // case is the same fault mirrored -- the latch says open, the at-rest branch returns air,
        // and sound floods through a leaf that has not moved.
        //
        // The drain already knows the answer: "Openness starts at the PRE-toggle position so the
        // door reads right until its clip actually begins". The window just never got it. Counter
        // is volatile so the per-cell fast path costs one read and never takes the lock -- it is
        // non-zero for a frame or two per toggle.
        private static volatile int _doorPendingCount;
        internal static bool TryGetPendingDoor(Vector3i parent, out bool opening)
        {
            opening = false;
            if (_doorPendingCount == 0) return false;
            lock (_doorLock)
            {
                for (int i = _doorPending.Count - 1; i >= 0; i--)   // latest wins
                    if (_doorPending[i].Pos == parent) { opening = _doorPending[i].Opening; return true; }
            }
            return false;
        }

        // ---- PER-DOOR: HOW MUCH OF THE CLIP THE APERTURE ACTUALLY USES --------------------
        // Some rigs keep animating after the leaf has finished moving. The wood cellar door
        // overshoots and bounces before it settles; the vault hatch runs a 3.1 s open clip. The
        // clip clock keeps climbing through all of that, so the sound goes on opening after the
        // doorway has stopped -- reported as "too slow to open" on exactly those doors while every
        // other door was called almost perfect.
        //
        // One number per door type: the FRACTION OF THE CLIP over which the aperture completes.
        // 1 (the default, and every door not listed) is the whole clip and changes nothing. 0.5
        // means the doorway is fully open half way through the animation and the rest is settle.
        //
        // This is deliberately NOT the old override table. That one carried five fields per door,
        // in units nobody could check, and existed because the base model could not fit any door.
        // The base model now fits eleven of thirteen; this is a per-rig fact about an animation,
        // it is one number, and you can verify it by watching the door and counting.
        private static string _spanCfgCache;
        private static string[] _spanKeys = System.Array.Empty<string>();
        private static float[] _spanVals = System.Array.Empty<float>();
        private static readonly Dictionary<int, float> _spanByType = new Dictionary<int, float>();
        private static float DoorSwingSpan(int type)
        {
            string cfg = CMMConfig.WorldEmitterDoorSwingSpans ?? "";
            if (!ReferenceEquals(cfg, _spanCfgCache))
            {
                _spanCfgCache = cfg;
                _spanByType.Clear();
                var keys = new List<string>(); var vals = new List<float>();
                foreach (string entry in cfg.Split(new[] { ';', ',' }, System.StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = entry.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = entry.Substring(0, eq).Trim();
                    if (k.Length == 0) continue;
                    if (!float.TryParse(entry.Substring(eq + 1).Trim(), System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out float v)) continue;
                    keys.Add(k); vals.Add(Mathf.Clamp(v, 0.05f, 1f));
                }
                _spanKeys = keys.ToArray(); _spanVals = vals.ToArray();
            }
            if (_spanKeys.Length == 0) return 1f;
            if (_spanByType.TryGetValue(type, out float cached)) return cached;
            float found = 1f;
            Block b = (type > 0 && type < Block.list.Length) ? Block.list[type] : null;
            string bn = b != null ? (b.GetBlockName() ?? "") : "";
            for (int i = 0; i < _spanKeys.Length; i++)
                if (bn.IndexOf(_spanKeys[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                { found = _spanVals[i]; break; }
            _spanByType[type] = found;
            if (found < 0.999f) CMMConfig.D("[CMM] DoorSwingSpan " + bn + " = " + found.ToString("0.00"));
            return found;
        }

        // ---- OPENNESS FROM OPEN AREA ------------------------------------------------------
        // Openness is not a fraction of the swing and no longer has thresholds. It is a soft knee
        // on the doorway's ACTUAL OPEN AREA in square metres, plus a residual term that guarantees
        // a fully-open door reads fully open.
        //
        //     u  = pow(aOpen / A50, Knee)
        //     og = u / (1 + u)                 // 0 at shut, asymptotic to 1, no clamps
        //     o  = 1 - (1 - og) * (1 - travel) // the two residuals multiply
        //
        // A50 is the size of hole that gets you halfway to hearing through, and it is ABSOLUTE.
        // That is the whole difference from every model before it, and the user's own words are
        // the calibration: "a door that is open half a metre wide is not occluding audio any more,
        // but it looks like the door is open 1.5 metres before occlusion finally fades." The
        // fraction model put the knee at 0.55 of a 3 m garage door -- 1.65 m, which is that
        // sentence's second half exactly. An absolute knee puts it where the first half asks.
        //
        // The residual product is what stops a 7 m roll-up feeling binary. The knee alone would
        // have it wide open within a metre of lift and then flat for six more seconds; multiplying
        // by (1 - travel) keeps a shrinking amount of muffle right up to the end of the animation,
        // so the tail stretches over the door's own duration without any per-door constant.
        //
        // WHAT THIS DELETES: GapFull, GapFullOpen, GapClosed, Curve, the whole substring-matched
        // override table, and the open/close threshold asymmetry. The asymmetry was compensating
        // for the pipeline lag that the lead now cancels properly.
        //
        // ONE CORRECTION TO THE PREMISE OF THE LAST MODEL: openness is NOT linear in amplitude.
        // It drives virtual distance through CurveDb, and for a capped wooden door the dB already
        // released at openness .25/.5/.75/1 runs 30%/56%/79%/100%. Openness has always been very
        // nearly "fraction of this door's dB that has arrived". The mapping was never the fault;
        // the geometry feeding it was.
        private static float DoorAcoustic(float aOpen, float aDoor, float travel)
        {
            // THE KNEE HAS TO KNOW HOW BIG THE DOOR IS. A purely absolute knee put the
            // half-way point of a seven-metre roll-up at 0.35 m2 -- which over 7 m of width is
            // FIVE CENTIMETRES of lift. So it went bright the instant it moved ("opening is too
            // bright too quickly") and, closing, held ~100% open until the last five centimetres
            // and then fell off a cliff ("goes from 0 to 100 when it is close to closing, nothing
            // gradual about the last metre, it just cuts too hard").
            //
            // A purely relative knee is the fraction model that failed six times. So take the
            // larger of the two: small doors keep the absolute knee that makes half a metre of
            // daylight sound like half a metre of daylight, and big doors get one proportional to
            // the hole they are, which is the only thing that can make the last metre of a 7 m
            // door gradual. At the default 0.15 the crossover sits around a 2.3 m2 doorway --
            // between a closet door and a garage door, which is where the two complaints divide.
            float a50 = Mathf.Max(0.01f, Mathf.Max(CMMConfig.WorldEmitterDoorHalfArea,
                                                   CMMConfig.WorldEmitterDoorHalfAreaFrac * Mathf.Max(0f, aDoor)));
            float knee = Mathf.Clamp(CMMConfig.WorldEmitterDoorKnee, 0.2f, 5f);
            float x = Mathf.Max(0f, aOpen) / a50;
            float u = (Mathf.Abs(knee - 1f) < 0.01f) ? x : Mathf.Pow(x, knee);
            float og = u / (1f + u);
            float t = Mathf.Clamp01(travel);
            return Mathf.Clamp01(1f - (1f - og) * (1f - t));
        }

        // Mute this many seconds before a CLOSE animation ends (skip the end-of-swing bounce). Time-based so
        // it's a big fraction of a fast swing but a negligible sliver of a slow garage/gate close.
        private const float CloseLeadSeconds = 0.15f;

        // True while any door is mid-swing — the emitter layer speeds its refresh so door transitions are
        // sampled tightly (fade starts promptly, close timing is precise) instead of in 250 ms 4 Hz steps.
        internal static bool AnyDoorAnimating => _doors.Count > 0 || _doorPending.Count > 0;

        // The parent positions of the doors currently animating, appended to the caller's list.
        // Exists so a listener of the door edge can re-read just those blocks instead of the whole
        // scene: the native acoustic scene is ~2M voxels and about seven seconds to read, and a door
        // is one block. Main-thread only, like every other reader of _doors.
        internal static void CollectAnimatingDoors(List<Vector3i> into)
        {
            if (into == null) return;
            foreach (var kv in _doors) into.Add(kv.Key);
            lock (_doorLock)
                for (int i = 0; i < _doorPending.Count; i++) into.Add(_doorPending[i].Pos);
        }

        // Called by the door hook (Patch_DoorAcousticChange) the instant a door toggles. `opening` is the
        // SetOpen target (the swing DIRECTION) — state-NAME-agnostic. Openness starts at the PRE-toggle
        // position so the door reads right until its clip actually begins (opening ⇒ shut 0; closing ⇒ open 1).
        // Mark every mid-swing door's cells for re-reading. Called per frame while any door is
        // animating; the field then follows the leaf rather than snapping when it stops.
        // ASK FOR THE DOOR, AND ONLY FOR THE DOOR. cmm_fld_dirty grows the box by (radius + step)
        // on every side, so a request costs (2r+3)^3 cells at step 1, and it stays in the PRIORITY
        // LANE only while that is <= FLD_PRIO_CAP (8192). Past it the call is treated as a sweep,
        // which rewinds the cursor -- the exact full-window wait the lane was built to avoid.
        //
        // r = 3 + maxDim overshot: a 7x4 roll-up asked for r=10, i.e. 23^3 = 12167 cells, fell out
        // of the lane, and its door latency went 45-108 ms -> 73-151 ms. Measured, in the log, one
        // build after the change. r = maxDim already covers the whole multiblock (the box spans
        // +/-(r+1) from the parent), and 8 is the largest radius that still fits: 19^3 = 6859.
        private const int MinDoorInvalidateRadius = 3;
        // Past this many cells a leaf is not worth walking one call at a time; fall back to the box.
        private const int MaxLeafInvalidateCells = 96;

        // Mark just the cells the door's leaves occupy. cmm_fld_dirty has no per-cell entry point,
        // so radius 0 is the smallest request there is (it still grows by one step on each side,
        // which is wanted -- the half-voxel split reads neighbours). Returns false when no aperture
        // has been learned yet, which is the caller's signal to fall back to the box.
        private static bool InvalidateLeafCells(Vector3i parent, int type, bool await)
        {
#if GV_V3
            if (!_leafPlanes.TryGetValue(parent, out LeafPlane p) || p.Boxes == null || p.Type != type)
                return false;
            int total = 0;
            for (int i = 0; i < p.Boxes.Length; i++)
            {
                LeafBox lb = p.Boxes[i];
                int x0 = Mathf.FloorToInt(lb.Min.x), x1 = Mathf.FloorToInt(lb.Max.x);
                int y0 = Mathf.FloorToInt(lb.Min.y), y1 = Mathf.FloorToInt(lb.Max.y);
                int z0 = Mathf.FloorToInt(lb.Min.z), z1 = Mathf.FloorToInt(lb.Max.z);
                total += (x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
                if (total > MaxLeafInvalidateCells) return false;
            }
            for (int i = 0; i < p.Boxes.Length; i++)
            {
                LeafBox lb = p.Boxes[i];
                int x0 = Mathf.FloorToInt(lb.Min.x), x1 = Mathf.FloorToInt(lb.Max.x);
                int y0 = Mathf.FloorToInt(lb.Min.y), y1 = Mathf.FloorToInt(lb.Max.y);
                int z0 = Mathf.FloorToInt(lb.Min.z), z1 = Mathf.FloorToInt(lb.Max.z);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            Vector3i c = new Vector3i(x, y, z);
                            FieldOcclusion.Invalidate(c, 0);
                            if (await) FieldOcclusion.AwaitCell(c);
                        }
            }
            return true;
#else
            return false;
#endif
        }

        // The box fallback, with every cell it dirties awaited, so a door with no learned
        // aperture is measured as honestly as one that has it.
        private static void InvalidateBoxAwaited(Vector3i centre, int radius)
        {
#if GV_V3
            FieldOcclusion.Invalidate(centre, radius);
            int r = radius + 1;                 // cmm_fld_dirty grows the box by one step a side
            for (int x = centre.x - r; x <= centre.x + r; x++)
                for (int y = centre.y - r; y <= centre.y + r; y++)
                    for (int z = centre.z - r; z <= centre.z + r; z++)
                        FieldOcclusion.AwaitCell(new Vector3i(x, y, z));
#endif
        }

        // A CONSTANT RADIUS CANNOT COVER A DOOR THAT IS BIGGER THAN IT. Radius 3 around the parent
        // covers a single door and a cellar door, and does NOT cover a 5x4 roll-up, a 7x4, or a
        // 5x6x7 drawbridge -- whose outer cells were then only re-read by the next FULL sweep,
        // which is seconds away. That is the "giant delay" on the big doors: not the pipeline, just
        // cells nobody had asked to be refreshed. Ask for the door's own extent plus the same
        // margin, so every cell the leaf can occupy is in the targeted lane.
        internal static void InvalidateFieldForAnimatingDoors()
        {
#if GV_V3
            foreach (var kv in _doors)
            {
                // ASK FOR THE LEAF, NOT FOR A BOX ROUND THE WHOLE DOOR. Only the leaf's material
                // changes as it swings, and it is a thin plate: tens of cells. A box big enough to
                // contain a 7x4 door is 6859 cells -- and this runs EVERY FRAME, against a reader
                // that manages roughly 1700 a frame. FieldOcclusion's own near-ring note calls
                // 6859 cells "about a third of the budget" for a 0.4s refresh; asking for it 30
                // times a second is four times oversubscribed, and that note also records what
                // oversubscription does: "six percent is enough to never converge", with a shut
                // door publishing its correct value on two ticks out of seventy.
                if (InvalidateLeafCells(kv.Key, kv.Value.Type, await: false)) continue;
                FieldOcclusion.Invalidate(kv.Key, MinDoorInvalidateRadius);   // no aperture yet
            }
#endif
        }

        internal static void RegisterAnimatingDoor(int blockType, Vector3i parentPos, bool opening)
        {
            // May be off-thread — just enqueue; RefreshDoorAnim applies it on the main thread.
            lock (_doorLock)
            {
                _doorPending.Add(new PendingDoor { Type = blockType, Pos = parentPos, Opening = opening });
                _doorPendingCount = _doorPending.Count;
            }
        }

        // Refresh every tracked door from its Animator; drop the ones that settled / can't be read. Runs at
        // the top of ComputeOcclusion, before any CellLoss. Main-thread only.
        private static void RefreshDoorAnim(World world)
        {
            _doorCellsBlendedPrev = _doorCellsBlended; _doorCellsBlended = 0;
            // Drain any registrations the hook queued (possibly off-thread) into the main-thread collections.
            lock (_doorLock)
            {
                for (int i = 0; i < _doorPending.Count; i++)
                {
                    PendingDoor p = _doorPending[i];
                    _doorTypes.Add(p.Type);
                    // The door's physical span (largest multiblock dimension, metres) — the gap-based
                    // acoustic mapping scales the swing fraction by it. Main thread here, Block.list safe.
                    float span = 1f, spanB = 1f;
                    Block db = (p.Type > 0 && p.Type < Block.list.Length) ? Block.list[p.Type] : null;
                    if (db != null && db.isMultiBlock && db.multiBlockPos != null)
                    {
                        Vector3i dim = db.multiBlockPos.dim;
                        // The two LARGEST dims, because a doorway is a rectangle and its area is
                        // the fallback aperture when the leaf itself could never be measured.
                        int a = dim.x, b = dim.y, c = dim.z;
                        int big = Mathf.Max(a, Mathf.Max(b, c));
                        int small = Mathf.Min(a, Mathf.Min(b, c));
                        span = Mathf.Max(1, big);
                        spanB = Mathf.Max(1, a + b + c - big - small);
                    }
                    _doors[p.Pos] = new DoorAnim { Opening = p.Opening, Openness = p.Opening ? 0f : 1f, Started = false, Ticks = 0,
                                                   Span = span, SpanB = spanB, Type = p.Type,
                                                   StartedAt = Time.realtimeSinceStartup,
                                                   Src = 0, PrevOpen = -1f, PrevAt = 0f };
                    // The latch's invalidation, moved here so it happens on the main thread.
                    // BeginDoorWait arms the stopwatch: from here the reader has to consume these
                    // exact cells before any flood can claim to have carried this door.
                    FieldOcclusion.BeginDoorWait();
                    EmitterBlockHook.ArmDoor(p.Pos);   // ...and the same clock, run to the mixer
                    if (!InvalidateLeafCells(p.Pos, p.Type, await: true))
                        InvalidateBoxAwaited(p.Pos, MinDoorInvalidateRadius);
                }
                _doorPending.Clear();
                _doorPendingCount = 0;
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
            // Track the LONGEST fresh clip — that's the leaf's swing. Fancy doors (garage, vault) run
            // short auxiliary clips alongside it (latch/chain/handle, ~0.3 s); taking the MIN normalized
            // time latched onto whichever clip was youngest, so a 0.3 s latch racing 0→1 read a 6 s
            // garage as fully open in a quarter second (log-confirmed 2026-07-08: solid 17.3→0 in 0.25 s,
            // the user's "it is audio the moment it is open"). Stale finished clips (nt ≥ ~1) are the
            // previous swing sitting at end — excluded; among FRESH clips the longest wins.
            float swingNt = float.MaxValue, swingLen = -1f, minNt = float.MaxValue;
            bool anyEnabled = false;
            for (int a = 0; a < anims.Length; a++)
            {
                Animator an = anims[a];
                if (an == null || !an.isActiveAndEnabled) continue;
                anyEnabled = true;
                int lc = an.layerCount;
                for (int L = 0; L < lc; L++)
                {
                    AnimatorStateInfo si = an.GetCurrentAnimatorStateInfo(L);
                    if (si.normalizedTime < minNt) minNt = si.normalizedTime;   // freshest, for the not-yet-started hold
                    if (si.normalizedTime < 0.95f && si.length > swingLen) { swingLen = si.length; swingNt = si.normalizedTime; }
                }
            }
            if (!anyEnabled) return false;   // every animator disabled => door at rest

            if (!da.Started)
            {
                if (minNt >= 0.95f)
                {
                    // NO CLIP OF THIS SWING HAS BEGUN, which is the only moment the leaf is
                    // provably still at its pre-toggle pose. Opening => that pose is SHUT, and a
                    // shut leaf is the one pose that says where the doorway is.
                    //
                    // This capture used to sit ABOVE this test, so a first tick that already saw a
                    // running clip recorded a MID-SWING pose as the shut one -- and the aperture,
                    // and every travel reading taken from it, was wrong for the life of the door.
                    if (da.Opening) RememberLeaf(bed, parent, da.Type);
                    da.Openness = da.Opening ? 0f : 1f;
                    return da.Ticks < 12;   // give up after ~a few seconds if no clip ever plays
                }
                da.Started = true;
            }
            if (swingLen < 0f) { swingNt = minNt; swingLen = 0f; }   // no fresh clip this tick

            float nt = Mathf.Clamp01(swingNt);
            float aDoorFallback = Mathf.Max(1f, da.Span * Mathf.Max(1f, da.SpanB));

            // ---- HOW OPEN IS IT, IN SQUARE METRES ----------------------------------------
            float leadS = Mathf.Max(0f, CMMConfig.WorldEmitterDoorLeadMs) * 0.001f;
            bool leadApplied = false;
            float travel, aOpen, aDoor;
            int src;
            if (CMMConfig.WorldEmitterDoorMeasureLeaf && !da.DeadMeasure && !_deadMeasure.ContainsKey(parent)
                && LeafOpenArea(parent, da.Type, da.Src == 3 ? 0 : da.Src, out aOpen, out aDoor, out src)
                && aDoor > 0.01f)
            {
                da.Src = src;
                travel = Mathf.Clamp01(aOpen / aDoor);
                // Watch the spread. A real swing moves the aperture; a dead reading does not.
                if (da.MaxOpen <= 0f && da.MinOpen <= 0f) { da.MinOpen = aOpen; da.MaxOpen = aOpen; }
                else { if (aOpen < da.MinOpen) da.MinOpen = aOpen; if (aOpen > da.MaxOpen) da.MaxOpen = aOpen; }
                if (da.Ticks > 3 && nt > 0.15f && (da.MaxOpen - da.MinOpen) < 0.05f * aDoor)
                {
                    // Half the clip has played and the doorway has not changed by a twentieth.
                    // Whatever this rig does, we are not reading it. Say so once, out loud, and
                    // hand the rest of the swing to the clip clock rather than publishing a
                    // sealed door that is visibly open.
                    da.DeadMeasure = true;
                    _deadMeasure[parent] = true;   // and never pay for it again on this door
                    if (CMMConfig.DebugLogging)
                    {
                        Block bb = (da.Type > 0 && da.Type < Block.list.Length) ? Block.list[da.Type] : null;
                        CMMConfig.D("[CMM] DoorMeasureDead " + (bb != null ? bb.GetBlockName() : "?")
                                    + " at " + parent + " plates=" + LastPlateTag
                                    + " spread=" + (da.MaxOpen - da.MinOpen).ToString("0.00")
                                    + "m2 of " + aDoor.ToString("0.00") + " -- falling back to clip time");
                    }
                }
            }
            else
            {
                // THE CLIP CLOCK, WITH THE EASE TAKEN OUT. Every hand-authored door clip in this
                // game eases in, so normalizedTime OVERSTATES how far the leaf has actually gone
                // early in the swing -- a smoothstep is about 3*nt*nt near zero. An exponent
                // between 1 and 2 is the right shape and 1.5 is the conservative middle. This is a
                // first-class route, not a scrap: it is where a door goes whose leaf genuinely
                // cannot be read, and it must still produce square metres like the others.
                da.Src = 3;
                // The ease describes how far the LEAF has got, so it applies to the clip fraction
                // itself -- and the direction is applied after. Easing the already-inverted value
                // would make a closing door read as further shut than it is, which is the exact
                // "occludes too early on close" complaint, manufactured.
                // THE LEAD GOES ON THE CLOCK, NOT ON THE AREA'S DERIVATIVE. Taking the
                // derivative was wrong at precisely the moments that matter, and Chris's table is
                // the proof. d(nt^1.5)/dt is ZERO AT nt=0 -- so on an OPENING door the lead
                // contributed nothing for the first part of the swing, which is exactly when the
                // door is opening and exactly what "opens late" means. It is largest at the END of
                // the swing, which is why the same doors' CLOSES came back fine.
                //
                // And a derivative needs a previous sample, so the first tick of every swing got
                // no lead at all. On a 0.33 s close that is a third of the whole animation, and
                // with a 350 ms pipeline the swing is over before the second tick's sound lands.
                // No value of LeadMs could fix a door that short; the lead has to apply from the
                // FIRST tick, and only the clock can do that.
                //
                // Advancing nt by leadS/swingLen is exact for a linear clock and self-scales:
                // 450 ms is 67% of a 0.67 s hatch and 6% of a 7.3 s roll-up, which is the
                // difference between the doors that complain and the ones that never did.
                float ease = Mathf.Clamp(CMMConfig.WorldEmitterDoorClipEase, 0.5f, 3f);
                float ntLed = nt;
                if (leadS > 0f && swingLen > 0.05f)
                { ntLed = nt + leadS / swingLen; leadApplied = true; }
                // The aperture may finish before the clip does. Dividing here rather than scaling
                // the lead separately is the same thing: (nt + lead/len) / span.
                float swSpan = DoorSwingSpan(da.Type);
                if (swSpan < 0.999f) ntLed /= swSpan;
                ntLed = Mathf.Clamp01(ntLed);
                // THE EASE BELONGS ON THE CLOSE ONLY, and the reason is which end of the clip
                // the acoustically decisive part of the ramp sits at.
                //
                // The knee puts almost all the audible change in the first ~30% of TRAVEL -- small
                // apertures. On an OPEN that region is at the START of the clip, and pow(nt, 1.5)
                // halves it: a quarter through the animation the model calls the door 12% open
                // instead of 25%. Reported, on every long door, as "the audio opens up too
                // slowly". On a CLOSE the same region is at the END, where 1 - pow(nt, 1.5) is
                // steep, and that is why closes came back right -- including the roll-up 5x4,
                // which Chris calls perfect. So do not touch the close.
                //
                // This asymmetry is not arbitrary. The old symmetric form made the decisive
                // segment about 2.1x longer on an open than on a close, and while the native
                // smoother was adaptive that difference got amplified sevenfold. The smoother is
                // constant now, so what is left is just the model's own bias -- and it only ever
                // pointed one way.
                float prog = Mathf.Clamp01(ntLed);
                if (da.Opening) travel = prog;
                else
                {
                    if (Mathf.Abs(ease - 1f) > 0.01f) prog = Mathf.Pow(prog, ease);
                    travel = 1f - prog;
                }
                aDoor = ApertureArea(parent, da.Type);
                if (aDoor <= 0.01f) aDoor = aDoorFallback;
                aOpen = travel * aDoor;
            }

            // ---- LEAD THE DOOR BY THE PIPELINE'S OWN LATENCY -----------------------------
            // Everything downstream describes the door as it was when this line ran, and takes
            // time to arrive: the geometry re-read, the flood, the emitter tick and the native
            // glide. Measured end to end, that is ~165-200 ms.
            //
            // The lead used to be a fixed time DIVIDED BY THE ANIMATOR CLIP LENGTH. Two doors in
            // the test set report a clip length of 0.00 s -- steelGarageDoor4x3 closing and
            // ironDoorGrey -- and the divide was guarded, so on those the lead silently did not
            // apply AT ALL and they ran the full pipeline latency late. That is "Grey Steel Garage
            // Door 4x3 is late" with no acoustics involved.
            //
            // So take the derivative of the thing that actually matters instead. How fast is the
            // opening growing, in square metres per second, right now -- and where will it be when
            // this reaches the ear. It needs no clip length, so it cannot be skipped, and it
            // self-scales: a fast door is moving fast and gets a big correction, a slow one small.
            float now = Time.realtimeSinceStartup;
            float rawOpen = aOpen;
            if (da.Src != da.LastSrc) { da.PrevOpen = -1f; da.LastSrc = da.Src; }   // routes are not comparable
            // Fallback only: the measured-leaf route, and any swing whose clip length is
            // unreadable (two doors in this POI report 0.00 s). Same failure modes as above, so it
            // is never preferred -- but a bad lead beats none on a door with no clock at all.
            if (!leadApplied && leadS > 0f && da.PrevOpen >= 0f)
            {
                float dt = now - da.PrevAt;
                if (dt > 0.001f && dt < 1f)
                {
                    // AND ONLY EVER FORWARD. A lead is an anticipation of where the door is
                    // GOING; letting it run backwards means a closing door can be published as
                    // more open than it measures, which is a door that bleeds while it shuts.
                    float rate = (rawOpen - da.PrevOpen) / dt;
                    float led = rawOpen + rate * leadS;
                    led = da.Opening ? Mathf.Max(led, rawOpen) : Mathf.Min(led, rawOpen);
                    aOpen = Mathf.Clamp(led, 0f, aDoor);
                    travel = Mathf.Clamp01(aOpen / Mathf.Max(0.01f, aDoor));
                }
            }
            da.PrevOpen = rawOpen;   // the RAW reading, never the led one, or the lead compounds
            da.PrevAt = now;

            da.ADoor = aDoor; da.AOpen = aOpen;
            float o = DoorAcoustic(aOpen, aDoor, travel);

            // A SWING GOES ONE WAY. Openness may not move against the door within a swing, and
            // the tail of a close is where that stops being pedantry:
            //
            //   nt=0.942 len=4.57 open=0.000
            //   nt=0.956 len=0.00 open=0.909   <- one tick, on a door 96% shut
            //   nt=0.967 len=0.00 open=0.000
            //
            // which is "audio bleeds through right after it shuts for a brief moment" exactly.
            // Once the swing clip finishes, swingLen falls back to 0 and swingNt to minNt, so the
            // clock jumps around among whatever stale clips the rig still has running; on the
            // steel garage 4x3 that briefly read 0.78 m2 of opening, and the derivative lead --
            // the only one left once swingLen is 0 -- extrapolated it 450 ms forward into 6.8 m2.
            //
            // Both halves are now fixed at the source, but the invariant is worth stating outright
            // rather than trusting every clip in every rig to be well behaved: a door that is
            // closing can only get more shut.
            if (da.Started && da.Ticks > 1)
                o = da.Opening ? Mathf.Max(o, da.Openness) : Mathf.Min(o, da.Openness);
            da.Openness = o;
            // Mute a hair BEFORE the leaf finishes closing - its end-of-swing bounce shouldn't
            // still be audible. Time-based so it self-scales, and skipped when the clip length is
            // unreadable rather than guessed.
            if (!da.Opening && swingLen > 0f && (1f - nt) * swingLen < CloseLeadSeconds) da.Openness = 0f;

            // DOOR REFERENCE TRACE: one line per frame of the swing. src says which route read the
            // leaf (xform / rend / clip), which is the first thing to check when a door feels
            // wrong -- a door on "clip" is being estimated, not measured.
            if (CMMConfig.DebugLogging)
            {
                Block dbb = (da.Type > 0 && da.Type < Block.list.Length) ? Block.list[da.Type] : null;
                string ss = da.Src == 1 ? "xform" : (da.Src == 2 ? "rend " : "clip ");
                CMMConfig.D("[CMM] DoorTrace " + (da.Opening ? "OPEN  " : "CLOSE ")
                            + (dbb != null ? dbb.GetBlockName() : "?")
                            + " src=" + ss + " plates=" + (da.Src == 3 ? "-" : LastPlateTag)
                            + " t=" + (Time.realtimeSinceStartup - da.StartedAt).ToString("0.000") + "s"
                            + " nt=" + nt.ToString("0.000")
                            + " len=" + swingLen.ToString("0.00") + "s"
                            + " area=" + aOpen.ToString("0.00") + "/" + aDoor.ToString("0.00") + "m2"
                            + " travel=" + travel.ToString("0.000")
                            + " open=" + da.Openness.ToString("0.000")
                            + " at " + parent + " cells=" + _doorCellsBlendedPrev);
            }
            return swingNt < 1f;   // still animating until the swing clip completes
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
            // Cloth is acoustically near-transparent (a real curtain costs a few dB at most) — 0.25
            // had POI drapes and awnings muffling like plywood.
            else if (c.Contains("cloth") || c.Contains("organic") || c.Contains("plant")
                     || c.Contains("hay") || c.Contains("leaves") || c.Contains("wool")
                     || c.Contains("cotton"))                                                  r = 0.08f;
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
                // min cost over its 3×3×3 ⇒ the reached surface near it. bWall feeds the caller's
                // solidGate, so hand back the flap-free neighbourhood min-wall, not the min-cost path's.
                int a = SampleArrival(x, y, z, out _, out int wMin);
                if (a >= 0) { bx = x; by = y; bz = z; bWall = wMin; return a; }
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

        // ── Incremental flood state ──────────────────────────────────────────────────────────────
        // The settle loop is SUSPENDABLE: relaxing a cell in bucket c only ever inserts into buckets
        // > c (enter cost ≥ CostScale), so saving (bucket, next-node) between frames is race-free —
        // later slices see any nodes added to not-yet-visited buckets exactly as a single pass would.
        // World reads (CellLoss) spread across a few frames; a block changing mid-flood skews that one
        // field slightly and the next flood corrects it (the native gain slew hides the step).
        private static bool  _floodRunning;
        private static int   _fCursorC;        // resume bucket
        private static int   _fCursorIdx;      // next node to examine in that bucket (-2 = take the bucket head)
        private static int   _fSettled;
        private static float _fWallDist;       // WallDistance captured at begin (finish reuses the field's weights)
        private static float _floodMsTotal;    // summed slice time of the current/last flood (diagnostic)
        private static int   _floodSlices;
        private static int   _airN, _aMinX, _aMinY, _aMinZ, _aMaxX, _aMaxY, _aMaxZ;   // connected-air accumulators

        // Reset the buffers and seed a fresh flood at `ear`. Settle nothing yet — call FloodStep.
        private static void FloodBegin(Vector3 ear, float wallDist)
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
            _fWallDist = wallDist;

            int earX = Mathf.FloorToInt(ear.x), earY = Mathf.FloorToInt(ear.y), earZ = Mathf.FloorToInt(ear.z);
            _fMinX = earX - MaxFloodRadius; _fMinY = earY - MaxFloodRadius; _fMinZ = earZ - MaxFloodRadius;
            _fEffR = Mathf.Clamp(CMMConfig.WorldEmitterFloodRadius, 1, MaxFloodRadius);

            int startIdx = (earX - _fMinX) + (earY - _fMinY) * Dim + (earZ - _fMinZ) * Dim2;
            _cost[startIdx] = 0;
            _wallCost[startIdx] = 0;
            _touched[_touchCount++] = startIdx;
            _bucketHead[0] = startIdx; _next[startIdx] = -1;

            // Connected-air bounding box for the reverb room measure (cells reached with zero solid cost).
            _airN = 0; _aMinX = _aMinY = _aMinZ = int.MaxValue; _aMaxX = _aMaxY = _aMaxZ = int.MinValue;

            _fCursorC = 0; _fCursorIdx = -2; _fSettled = 0;
            _floodMsTotal = 0f; _floodSlices = 0;
            _floodRunning = true;
        }

        // Settle cells until `budgetMs` elapses (checked every 48 settles) or the flood completes.
        // Returns true when the field is COMPLETE (results published to AirCellCount/FloodSettled/
        // AirRoomSize and safe to Finish); false = call again next frame.
        private static bool FloodStep(World world, float budgetMs)
        {
            if (!_floodRunning) return true;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int earX = _fMinX + MaxFloodRadius, earY = _fMinY + MaxFloodRadius, earZ = _fMinZ + MaxFloodRadius;
            int c = _fCursorC, idx = _fCursorIdx, sinceCheck = 0;

            while (c <= MaxBucket && _fSettled < NodeBudget)
            {
                if (idx == -2) idx = _bucketHead[c];
                while (idx != -1 && _fSettled < NodeBudget)
                {
                    int nxt = _next[idx];
                    if (_cost[idx] == c)   // skip stale entries (a cheaper cost superseded this one)
                    {
                        _fSettled++;
                        int lz = idx / Dim2, rem = idx - lz * Dim2, ly = rem / Dim, lx = rem - ly * Dim;
                        int x = lx + _fMinX, y = ly + _fMinY, z = lz + _fMinZ;
                        int pw = _wallCost[idx];   // solid-only cost of the path settled to this cell
                        if (pw == 0)               // reached through AIR only ⇒ part of the listener's room
                        {
                            _airN++;
                            if (x < _aMinX) _aMinX = x; if (x > _aMaxX) _aMaxX = x;
                            if (y < _aMinY) _aMinY = y; if (y > _aMaxY) _aMaxY = y;
                            if (z < _aMinZ) _aMinZ = z; if (z > _aMaxZ) _aMaxZ = z;
                        }
                        Relax(x + 1, y, z, earX, earY, earZ, c, pw, world);
                        Relax(x - 1, y, z, earX, earY, earZ, c, pw, world);
                        Relax(x, y + 1, z, earX, earY, earZ, c, pw, world);
                        Relax(x, y - 1, z, earX, earY, earZ, c, pw, world);
                        Relax(x, y, z + 1, earX, earY, earZ, c, pw, world);
                        Relax(x, y, z - 1, earX, earY, earZ, c, pw, world);

                        // Budget check — only on settles (the expensive part), every 48 so the Stopwatch
                        // read itself stays cheap. Suspend by saving the cursor at the NEXT node.
                        if (++sinceCheck >= 48)
                        {
                            sinceCheck = 0;
                            if ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * toMs >= budgetMs)
                            {
                                _fCursorC = c; _fCursorIdx = nxt;
                                _floodMsTotal += (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * toMs);
                                _floodSlices++;
                                return false;
                            }
                        }
                    }
                    idx = nxt;
                }
                c++; idx = -2;
            }

            _floodMsTotal += (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * toMs);
            _floodSlices++;

            // Longest span of the connected-air box = how big the space "sounds". Follows bends (the flood
            // routed the air), so a winding tunnel reads its real length, not a straight sightline. Capped by
            // FloodRadius, which is fine — a space bigger than the flood is already plenty reverberant.
            AirCellCount = _airN;
            FloodSettled = _fSettled;
            if (_airN > 0)
            {
                int dx = _aMaxX - _aMinX + 1, dy = _aMaxY - _aMinY + 1, dz = _aMaxZ - _aMinZ + 1;
                int longest = dx; if (dy > longest) longest = dy; if (dz > longest) longest = dz;
                AirRoomSize = longest;
            }
            else AirRoomSize = 0f;
            _floodRunning = false;
            return true;
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
        //
        // wallAtBest rides the MIN-COST path — fine for the soft-knee/depth split, but WRONG for the
        // hard-stop gate: Dijkstra minimises cost (distance + wall), so two near-equal-cost routes with
        // very different material composition can flip which one wins as the ear moves one block, and
        // the gate's smoothstep amplifies that wall flip into an audible on/off toggle (the "gate flap"
        // the 2D bench caught in water). wallMin is the fix: the least wall along ANY reached route into
        // the neighbourhood — the question the gate actually asks ("can sound get here through less than
        // HardStop of material at all?") — and it's stable because adjacent speakers' neighbourhoods
        // overlap, so it can't jump more than the local material between them.
        private static int SampleArrival(int sx, int sy, int sz, out int wallAtBest, out int wallMin)
        {
            int best = Inf, bestWall = 0, minWall = int.MaxValue;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = sx + dx, y = sy + dy, z = sz + dz;
                int lx = x - _fMinX, ly = y - _fMinY, lz = z - _fMinZ;
                if (lx < 0 || lx >= Dim || ly < 0 || ly >= Dim || lz < 0 || lz >= Dim) continue;
                int id = lx + ly * Dim + lz * Dim2;
                int cst = _cost[id];
                if (cst == Inf) continue;
                if (cst < best) { best = cst; bestWall = _wallCost[id]; }
                if (_wallCost[id] < minWall) minWall = _wallCost[id];
            }
            wallAtBest = bestWall;
            wallMin = minWall == int.MaxValue ? 0 : minWall;
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
                if (bv.isair)
                {
                    // Water (fluid-sim layer; the BlockValue is air) — acoustically a HARD surface: the
                    // air↔water impedance mismatch reflects nearly everything, so a lake/pool bounds the
                    // room like a stone floor instead of reading as open space.
                    if (world.IsWater(x, y, z)) { dist = t; reflect = 0.85f; return true; }
                    continue;
                }
                Block b = bv.Block;
                if (b == null) continue;
                if (b.blockMaterial != null && b.blockMaterial.IsLiquid) { dist = t; reflect = 0.85f; return true; }
                if (EmitterCompat.IsSeeThrough(b, world, new Vector3i(x, y, z), bv)) continue;   // window/doorway — sound passes, no reflection here
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

        // ── Sweep/debug accessors (EmitterAcousticsSweep only) ──────────────────────────────────────
        // The acoustics sweep exporter needs the model's raw ingredients — per-cell loss, the settled
        // flood field, the cost quantisation — to write game-truth dumps the offline judge replays.
        // Read-only views; nothing here changes model behaviour.
        internal static bool FloodInProgress => _floodRunning;
        internal static int  DebugCostScale  => CostScale;

        internal static float DebugCellLoss(BlockValue bv, World world, int x, int y, int z)
            => CellLoss(bv, world, x, y, z);

        // Enumerate the COMPLETED flood field (call right after a synchronous ComputeOcclusion; the
        // buffers stay valid until the next FloodBegin). Emits world x,y,z + the SCALED cost / solid-
        // only cost exactly as the field holds them (÷ CostScale for blocks).
        internal static void DebugExportFlood(System.Action<int, int, int, int, int> perCell)
        {
            for (int i = 0; i < _touchCount; i++)
            {
                int idx = _touched[i];
                if (_cost[idx] == Inf) continue;
                int lz = idx / Dim2, rem = idx - lz * Dim2, ly = rem / Dim, lx = rem - ly * Dim;
                perCell(lx + _fMinX, ly + _fMinY, lz + _fMinZ, _cost[idx], _wallCost[idx]);
            }
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
