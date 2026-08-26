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
                sbyte den = world.GetDensity(x, y, z);
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

            if (b.IsSeeThrough(world, new Vector3i(x, y, z), bv))
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
        }

        // ── THE LEAF'S REAL MOTION, NOT THE CLIP'S CLOCK ────────────────────────────────────
        // Openness has always come from Animator.normalizedTime -- how far through the CLIP the
        // animation is, which is linear in time. The leaf's actual travel is not: an animator
        // curve eases in, eases out, holds, and on a cellar door overshoots and bounces before it
        // settles. At 50% of the clip a linear door is half open and an eased one may be a tenth
        // or nine tenths, so the number driving the sound is simply wrong -- and no threshold and
        // no lead can correct an input that does not describe the door.
        //
        // That shows up exactly where it was reported: "no graduation, its animation is a jarring
        // one" (roll-up 3x3) and "occlusion doesn't kick in until the animation is complete, and
        // it bounces and buckles before it settles" (cellar door). Every door with a plain linear
        // swing was already fine.
        //
        // The COLLIDERS are blanked mid-swing -- that is documented and true -- but the TRANSFORM
        // keeps moving, because the door is visibly moving. So follow its rotation from the shut
        // pose. The travel is normalised by the largest angle that door type has been seen to
        // reach, learned on the way through; until that is known the clip clock is used, so a
        // door type behaves exactly as before on its first swing and can never be made worse.
        private static readonly Dictionary<int, float> _doorMaxAngle = new Dictionary<int, float>();

        // Angle travelled from the shut pose, or -1 when it cannot be read.
        private static float LeafTravelAngle(Vector3i parent, int type)
        {
            if (!_leafPlanes.TryGetValue(parent, out LeafPlane p) || p.Type != type) return -1f;
            Transform o = p.Owner;
            if (o == null) return -1f;                  // prefab recycled, or never captured
            try { return Quaternion.Angle(p.ShutRot, o.localRotation); }
            catch { return -1f; }
        }
        private static readonly Dictionary<Vector3i, LeafPlane> _leafPlanes = new Dictionary<Vector3i, LeafPlane>();

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
            if (_leafPlanes.Count > 1024) _leafPlanes.Clear();
            bool isNew = !_leafPlanes.ContainsKey(parent);
            Transform owner = lbs[0].Owner;
            _leafPlanes[parent] = new LeafPlane
            {
                Type = type, Boxes = lbs, Owner = owner,
                ShutRot = owner != null ? owner.localRotation : Quaternion.identity
            };
            if (isNew && CMMConfig.DebugLogging)
            {
                LeafBox l0 = lbs[0];
                Vector3 e = l0.Oriented ? l0.Half * 2f : (l0.Max - l0.Min);
                Vector3 wmin = l0.Min, wmax = l0.Max;
                for (int i = 1; i < lbs.Length; i++)
                { wmin = Vector3.Min(wmin, lbs[i].Min); wmax = Vector3.Max(wmax, lbs[i].Max); }
                Block ab = (type > 0 && type < Block.list.Length) ? Block.list[type] : null;
                CMMConfig.D($"[CMM] Aperture learned {(ab != null ? ab.GetBlockName() : "?")} at {parent} " +
                            $"{lbs.Length} leaf/leaves, {(l0.Oriented ? "oriented" : "aabb")}, " +
                            $"first plate {e.x:0.00}x{e.y:0.00}x{e.z:0.00} " +
                            $"world {wmin.x:0.00},{wmin.y:0.00},{wmin.z:0.00} -> {wmax.x:0.00},{wmax.y:0.00},{wmax.z:0.00}");
            }
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
            string v = null;
            if (b.Properties != null && b.Properties.Values != null)
                b.Properties.Values.TryGetValue("MeshDamage", out v);
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
            public bool Opening; public float Openness; public bool Started; public int Ticks; public float Span;
            public float GapFull, GapFullOpen, GapClosed;   // per-door thresholds (globals + type overrides), resolved at registration
            public int Type;                                // block type — names the door in the DoorTrace log
            public float StartedAt;                         // realtime the swing was registered (trace time base)
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

        // The acoustic openness of a mid-swing door is driven by the physical GAP IN METERS, not the
        // fraction of the swing (v29.2). Fractions treated all doors alike, which can't be right at both
        // ends of the size spectrum: a 3 m garage door 25% from shut still has a ~0.75 m opening — a huge
        // aperture, full sound ("it doesn't make sense that the sound is already muffled with a meter to
        // go", user 2026-07-08) — while a ~1 m vault leaf 25% open is a hand-width crack. gap = swing
        // fraction × the door's largest dimension; sound is fully open above DoorGapFull metres of
        // opening and silent below DoorGapClosed (both config, hot). Size does the per-door work a
        // per-type table would have: big doors hold sound until genuinely nearly shut and come alive
        // almost immediately on open; small heavy leaves read as a slam. Symmetric in gap — the
        // asymmetries players feel emerge from door size, and CloseLeadSeconds still trims the tail.
        // Direction-asymmetric thresholds: CLOSING uses GapFull (fade begins early — the tuned "when
        // does the shut land" feel); OPENING uses the smaller GapFullOpen, because sound floods through
        // a real gap immediately — with one shared threshold, a close tuned to fade early made the open
        // take just as long to reach full ("takes a while for the audio to come in", user 2026-07-08).
        // Thresholds come from the DoorAnim (globals merged with per-type DoorGapOverrides at
        // registration), so one door type can be retuned without touching the rest.
        private static float DoorAcoustic(float physical, ref DoorAnim da)
        {
            // ── THRESHOLDS ARE FRACTIONS OF THE SWING ────────────────────────────────────────
            // They used to be metres of opening, with gap = swingFraction x span. The physics of
            // that is sound -- a 0.70 m gap IS acoustically open whatever the door's width -- but
            // what a player perceives is a FRACTION of the swing, and the conversion between the
            // two is the span. So the same threshold landed in a different perceptual place on
            // every door size: 70% of the travel on a one-metre door, 35% on a two-metre cellar
            // door, 14% on a five-metre gate. Every size needed its own override to feel the same,
            // the override table grew a line per door family, and it still did not cover them all.
            // "None feel right with either setting" was the end of that road.
            //
            // In fractions there is one unit and an override reads as a sentence you can check
            // against what you are watching: this door's transition runs from 8% to 45% of its
            // swing. Doors of different sizes agree by construction, which was the complaint.
            //
            // WHAT THIS GIVES UP, deliberately: a five-metre gate and a one-metre door now
            // transition at the same fraction, though the gate opens a physically larger hole
            // sooner. That reasoning was right and it produced a table nobody could tune.
            float full = Mathf.Clamp(da.Opening ? da.GapFullOpen : da.GapFull, 0.02f, 0.98f);
            float shut = Mathf.Clamp(da.GapClosed, 0f, full - 0.01f);
            return Mathf.Clamp01((physical - shut) / (full - shut));
        }

        // ── Per-door-type gap overrides ──────────────────────────────────────────────────────────────
        // Config WorldEmitterDoorGapOverrides: "match=full/fullOpen/closed;match=..." — match is a
        // case-insensitive SUBSTRING of the block name (catches wooden/steel/ironGarageDoor with one
        // "garageDoor" entry); "-" inherits the global. First matching entry wins. Cached per type.
        private struct GapOvr { public string Match; public float Full, FullOpen, Closed; }
        private static string _ovrCfgCache;
        private static GapOvr[] _gapOvrs = System.Array.Empty<GapOvr>();
        private static readonly Dictionary<int, int> _gapOvrByType = new Dictionary<int, int>();
        private static void ResolveDoorGaps(int type, out float full, out float fullOpen, out float closed)
        {
            full = CMMConfig.WorldEmitterDoorGapFull;
            fullOpen = CMMConfig.WorldEmitterDoorGapFullOpen;
            closed = CMMConfig.WorldEmitterDoorGapClosed;
            string cfg = CMMConfig.WorldEmitterDoorGapOverrides ?? "";
            if (!ReferenceEquals(cfg, _ovrCfgCache))   // config hot-reload swaps the string instance
            {
                _ovrCfgCache = cfg;
                _gapOvrByType.Clear();
                var list = new List<GapOvr>();
                foreach (string entry in cfg.Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = entry.IndexOf('=');
                    if (eq <= 0) continue;
                    string[] v = entry.Substring(eq + 1).Split('/');
                    GapOvr o = new GapOvr { Match = entry.Substring(0, eq).Trim(), Full = float.NaN, FullOpen = float.NaN, Closed = float.NaN };
                    if (v.Length > 0 && ParseOvr(v[0], out float f0)) o.Full = f0;
                    if (v.Length > 1 && ParseOvr(v[1], out float f1)) o.FullOpen = f1;
                    if (v.Length > 2 && ParseOvr(v[2], out float f2)) o.Closed = f2;
                    if (o.Match.Length > 0) list.Add(o);
                }
                _gapOvrs = list.ToArray();
            }
            if (_gapOvrs.Length == 0) return;
            if (!_gapOvrByType.TryGetValue(type, out int idx))
            {
                idx = -1;
                Block b = (type > 0 && type < Block.list.Length) ? Block.list[type] : null;
                string bn = b != null ? (b.GetBlockName() ?? "") : "";
                for (int i = 0; i < _gapOvrs.Length && idx < 0; i++)
                    if (bn.IndexOf(_gapOvrs[i].Match, System.StringComparison.OrdinalIgnoreCase) >= 0) idx = i;
                _gapOvrByType[type] = idx;
                if (idx >= 0) CMMConfig.D($"[CMM] DoorGapOverride {bn} ← {_gapOvrs[idx].Match}");
            }
            if (idx < 0) return;
            GapOvr ov = _gapOvrs[idx];
            if (!float.IsNaN(ov.Full)) full = ov.Full;
            if (!float.IsNaN(ov.FullOpen)) fullOpen = ov.FullOpen;
            if (!float.IsNaN(ov.Closed)) closed = ov.Closed;
        }
        private static bool ParseOvr(string s, out float v)
        {
            s = s.Trim();
            v = 0f;
            if (s.Length == 0 || s == "-") return false;
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out v);
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
                    float span = 1f;
                    Block db = (p.Type > 0 && p.Type < Block.list.Length) ? Block.list[p.Type] : null;
                    if (db != null && db.isMultiBlock && db.multiBlockPos != null)
                    {
                        Vector3i dim = db.multiBlockPos.dim;
                        span = Mathf.Max(1, Mathf.Max(dim.x, Mathf.Max(dim.y, dim.z)));
                    }
                    ResolveDoorGaps(p.Type, out float gFull, out float gFullOpen, out float gClosed);
                    _doors[p.Pos] = new DoorAnim { Opening = p.Opening, Openness = p.Opening ? 0f : 1f, Started = false, Ticks = 0,
                                                   Span = span, GapFull = gFull, GapFullOpen = gFullOpen, GapClosed = gClosed,
                                                   Type = p.Type, StartedAt = Time.realtimeSinceStartup };
                    // The latch's invalidation, moved here so it happens on the main thread.
                    // BeginDoorWait arms the stopwatch: from here the reader has to consume these
                    // exact cells before any flood can claim to have carried this door.
                    FieldOcclusion.BeginDoorWait();
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
            if (!anyEnabled) return false;   // every animator disabled ⇒ door at rest

            if (!da.Started)
            {
                // CAPTURE THE APERTURE WHILE THERE IS STILL SOMETHING TO MEASURE. No clip of this
                // swing has begun yet, so the leaf is at its PRE-TOGGLE pose and OnStateUpdate has
                // not yet blanked its colliders. Opening ⇒ that pose is shut, which is the one pose
                // that says where the doorway is; closing ⇒ it is parked open and worth nothing, so
                // only ask on the way out. Once the clip starts there is nothing readable for the
                // rest of the animation, which is precisely the window the sound has to be right in.
                //
                // This is also the ONLY capture that fires for a door opened from a distance. The
                // listener's own cell learns its plane by standing in it, which is no use at all
                // when the complaint is running through one that somebody already opened.
                if (da.Opening) RememberLeaf(bed, parent, da.Type);
                if (minNt >= 0.95f)   // no clip has begun (stale end-of-previous) → hold at start, keep waiting
                {
                    da.Openness = da.Opening ? 0f : 1f;
                    return da.Ticks < 12;   // give up after ~a few seconds if no clip ever plays
                }
                da.Started = true;
            }
            if (swingLen < 0f) { swingNt = minNt; swingLen = 0f; }   // no fresh clip this tick — fall back

            float t = Mathf.Clamp01(swingNt);

            // ── LEAD THE DOOR BY THE PIPELINE'S OWN LATENCY ─────────────────────────────────
            // Everything downstream describes the door as it was when this line ran, and takes
            // time to arrive: the geometry re-read, the flood, the emitter tick and the native
            // glide. Measured end to end on a block change, which travels the same road, that is
            // ~110-200 ms. Nothing compensated for it -- CloseLeadSeconds is a 0.15 s special case
            // at the very end of a close, not a general lead -- so the sound has always described
            // the door as it was a fifth of a second ago.
            //
            // That is a fixed TIME against swings of wildly different DURATION, which is why no
            // threshold could ever settle it:
            //
            //   interior door  0.53 s swing  ->  200 ms is 38% of it
            //   cellar door    2.60 s swing  ->  200 ms is  8% of it
            //
            // A fast door runs nearly half a swing behind while a slow one is barely late, and a
            // threshold moves the curve WITHIN the swing -- it cannot reach a lag measured in
            // seconds. Three rounds of tuning thresholds could not have worked.
            //
            // So sample where the door WILL BE. Dividing the lead by this door's own clip length
            // makes the correction large on a fast swing and small on a slow one automatically,
            // which is exactly the asymmetry that needs cancelling.
            float lead = Mathf.Max(0f, CMMConfig.WorldEmitterDoorLeadMs) * 0.001f;
            if (lead > 0f && swingLen > 0.05f)
                t = Mathf.Clamp01(t + lead / swingLen);

            // ── PREFER THE LEAF'S REAL TRAVEL OVER THE CLIP CLOCK ───────────────────────────
            // See LeafTravelAngle. The clip clock is linear; the leaf is not, and on the two doors
            // still reported wrong it is dramatically not. Learn the largest angle this type
            // reaches and read travel as a fraction of it. The lead still applies, projected onto
            // the same axis by the same clip fraction, because the pipeline delay is unchanged.
            //
            // Falls back silently whenever the angle cannot be read or the type's range is not yet
            // known, which is every door's FIRST swing -- so nothing that behaves today can be
            // made worse by this, only later swings improved.
            if (CMMConfig.WorldEmitterDoorUseLeafAngle)
            {
                float ang = LeafTravelAngle(parent, da.Type);
                if (ang >= 0f)
                {
                    float best;
                    _doorMaxAngle.TryGetValue(da.Type, out best);
                    if (ang > best) { best = ang; _doorMaxAngle[da.Type] = best; }
                    // Needs a real range before it can mean anything: a door barely off its stop
                    // has an angle, not a fraction.
                    if (best > 5f)
                    {
                        float travel = Mathf.Clamp01(ang / best);
                        // The lead, expressed in this door's own clip, carried onto the angle axis.
                        if (lead > 0f && swingLen > 0.05f)
                            travel = Mathf.Clamp01(travel + lead / swingLen);
                        t = da.Opening ? travel : 1f - travel;
                    }
                }
            }

            float physical = da.Opening ? t : 1f - t;
            da.Openness = DoorAcoustic(physical, ref da);
            // Mute a hair BEFORE the leaf finishes closing — its end-of-swing bounce shouldn't still be
            // audible. Time-based so it self-scales: a big lead on a fast swing (~0.5 s clip), negligible on
            // a slow garage (0.15 s of a 6-7 s close ⇒ invisible, garage feel untouched).
            if (!da.Opening && swingLen > 0f && (1f - t) * swingLen < CloseLeadSeconds) da.Openness = 0f;

            // DOOR REFERENCE TRACE: one line per frame of the swing. This is the tuned behaviour
            // captured as data — the gap-in-metres mapping, the open/close threshold asymmetry, the
            // per-type overrides and the close lead all land in `open`, and the matching [CMM] Occl
            // line (unthrottled during a swing) carries the resulting dB. Any change to the acoustic
            // model must reproduce these two series together.
            if (CMMConfig.DebugLogging)
            {
                Block dbb = (da.Type > 0 && da.Type < Block.list.Length) ? Block.list[da.Type] : null;
                float dbgAng = LeafTravelAngle(parent, da.Type);
                float dbgMax; _doorMaxAngle.TryGetValue(da.Type, out dbgMax);
                CMMConfig.D($"[CMM] DoorTrace {(da.Opening ? "OPEN " : "CLOSE")} {(dbb != null ? dbb.GetBlockName() : "?")} " +
                            $"ang={dbgAng:0.0}/{dbgMax:0.0} " +
                            $"t={Time.realtimeSinceStartup - da.StartedAt:0.000}s nt={t:0.000} len={swingLen:0.00}s " +
                            $"span={da.Span:0.##}m gap={physical * Mathf.Max(1f, da.Span):0.00}m " +
                            $"thresh={(da.Opening ? da.GapFullOpen : da.GapFull):0.00}/{da.GapClosed:0.00} open={da.Openness:0.000} " +
                            $"at {parent} cells={_doorCellsBlendedPrev}");
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
