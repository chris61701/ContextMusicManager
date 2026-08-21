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
                    _doorCellsBlended++;
                    // Swing and battle damage both open the door; the LARGER opening wins (holes don't
                    // add anything once the leaf has swung past them). An undamaged SEAL-class door
                    // (vault) blends toward AIRTIGHT density, so the hard-stop gate engages while the
                    // leaf is still visibly closing — the slam lands early, not after the animation.
                    float dmgO = DamageOpenness(b, bv, world, x, y, z);
                    // REMAP THE SWING so the sound arrives with the act rather than with the
                    // animation. Openness is scaled up and clamped, so a door is acoustically shut
                    // once its leaf is WorldEmitterDoorSwingFrac of the way closed and acoustically
                    // open once it is that far open. Measured latch-to-field is 33-79 ms, so what
                    // reads as delay is this ramp, not the pipeline. Damage openness is a static
                    // hole and is not remapped -- there is no swing to be early for.
                    float frac = CMMConfig.WorldEmitterDoorSwingFrac;
                    float swing = da.Openness;
                    if (frac > 0.001f && frac < 0.999f) swing = Mathf.Clamp01(swing / frac);
                    else if (frac <= 0.001f) swing = da.Openness > 0.5f ? 1f : 0f;
                    float open = Mathf.Max(swing, dmgO);
                    float shutLoss = (dmgO <= 0f && IsSealDoor(b, bv.type)) ? AirtightLoss : SolidLoss(b, bv);
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
                if (IsSwingingLeaf(b, bv.type)
                    && !b.IsMovementBlocked(world, new Vector3i(x, y, z), bv, BlockFace.None)) return 0f;
                float mdmg = DamageOpenness(b, bv, world, x, y, z);
                if (mdmg <= 0f && IsSealDoor(b, bv.type)) return AirtightLoss;
                return SolidLoss(b, bv) * (1f - mdmg);
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

            // DECORATIVE TRIM / MOLDING / ARCH: an opaque PARTIAL (non-cube) shape you can still walk through is
            // oriented against the frame — it fills a voxel in our grid but leaves the opening clear, so sound
            // passes like you do. Only a partial that actually BLOCKS movement (rotated INTO the path, a half-
            // wall) keeps its solid share below. Per-instance + orientation-aware: the SAME trim block reads
            // near-open flush to the wall but solid when turned into the doorway — which is why smashing the
            // decor "let more sound through" (we were charging it as half a wall). Full cubes never reach here.
            // ...BUT A FLOOR IS NOT TRIM, AND MOVEMENT IS THE WRONG QUESTION FOR ONE.
            // "Does it block the player" is a good proxy for a wall and a useless one for a floor:
            // you never walk THROUGH a floor, you stand ON it, so every partial-shape floor plate in
            // the game answered "no" and took the 90% discount. Measured on the captures, floor
            // voxels that separate two storeys and are acoustically OPEN:
            //
            //     store    176 of 721   (24.4%)   -- 21.5% more open than shut
            //     bunker  2762 of 3217  (85.9%)   -- 36.0% more open than shut
            //
            // Concrete at 0.9 discounted to 0.09 reads as 91% open, so a bunker floor barely existed.
            // That is "it's bleeding through the floor", and standing under the speaker's room the
            // game measured 3.4 dB of occlusion where the same geometry offline gives 11-13.
            //
            // A partial with air both above and below is separating two spaces -- it is structure,
            // whatever it does to movement. Trim sits flush against something; a floor has room on
            // both sides. Two extra block reads on a branch that only partial shapes reach.
            //
            // The trade this makes, stated so it is not a surprise later: a partial shape genuinely
            // suspended in mid-air -- a hanging sign, a pipe run -- now falls through to solid and is
            // over-charged. That is rarer in a POI than floor plates are, and the alternative was a
            // 90% discount on every floor in the game. A grate or catwalk floor is unaffected: it has
            // a genuinely open centre and is caught by the branch below, which is the correct answer
            // for one -- you can see and hear straight through a grate.
            if (b.shape != null && !b.shape.IsSolidCube &&
                !b.IsMovementBlocked(world, new Vector3i(x, y, z), bv, BlockFace.None)
                && !(world.GetBlock(x, y + 1, z).isair && world.GetBlock(x, y - 1, z).isair))
                return MaterialLoss(b.blockMaterial != null ? b.blockMaterial.SurfaceCategory : null) * 0.1f;

            // OPEN-CENTRE FRAMES: deco frame/arch/trim pieces are solid at the FACES (they mate with the
            // blocks around them) with a pass-through hole in the middle — block-level movement reads
            // "blocked" (the rim's collision), but the player and the sound go through the hole. Ask the
            // shape's own collision boxes: if the centre is clear along ALL THREE axes, it's a true
            // frame ⇒ acoustically ~air (the rim casts a sliver of shadow). The three-axis test is what
            // keeps this away from look-alikes: a wall plate blocks its face axis, a floor slab blocks
            // the vertical, a centre pillar blocks everything — they all keep their partial loss below.
            // Model-entity shapes (doors/hatches/furniture) are excluded — a closed door is a thin slab
            // whose cell-centre is clear, and its opening is the damage/swing logic above.
            if (b.shape != null && !b.shape.IsSolidCube && !(b.shape is BlockShapeModelEntity) &&
                HasOpenCenter(b, bv))
                return MaterialLoss(b.blockMaterial != null ? b.blockMaterial.SurfaceCategory : null) * 0.05f;

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
                if (dmgOpen <= 0f && IsSealDoor(b, bv.type)) return AirtightLoss;
                return SolidLoss(b, bv) * (1f - dmgOpen);
            }
            return SolidLoss(b, bv);
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

        // Does this shape's collision leave the cell's centre clear along every axis? Cached per
        // (type, rotation) — GetBounds is rotation-aware and shapes are immutable at runtime.
        private static readonly Dictionary<int, bool> _openCenterCache = new Dictionary<int, bool>();
        private static bool HasOpenCenter(Block b, BlockValue bv)
        {
            int key = (bv.type << 6) | (bv.rotation & 63);
            if (_openCenterCache.TryGetValue(key, out bool open)) return open;
            Bounds[] arr = null;
            try { arr = b.shape.GetBounds(bv); } catch { /* modded shape misbehaving ⇒ treat as closed */ }
            open = arr != null && arr.Length > 0 &&
                   CenterColumnClear(arr, 0) && CenterColumnClear(arr, 1) && CenterColumnClear(arr, 2);
            _openCenterCache[key] = open;
            return open;
        }
        private static bool CenterColumnClear(Bounds[] arr, int axis)
        {
            // A thin rod through the cell centre along `axis` (shape bounds are local 0..1 space).
            Vector3 mn = new Vector3(0.4f, 0.4f, 0.4f), mx = new Vector3(0.6f, 0.6f, 0.6f);
            mn[axis] = 0.02f; mx[axis] = 0.98f;
            Bounds col = new Bounds((mn + mx) * 0.5f, mx - mn);
            for (int i = 0; i < arr.Length; i++)
                if (arr[i].Intersects(col)) return false;
            return true;
        }

        // Transmission loss of an opaque solid (a wall, or a fully-shut door): its material, halved for a
        // partial (non-cube) shape. Factored out so the door-openness blend and the plain-solid path agree.
        // Does this block's VARIANT actually fill its voxel? b.shape.IsSolidCube answers for the shape
        // CLASS, and every shapes.xml block shares the generic BlockShapeNew class -- so it returns
        // false for woodShapes:cube, a block literally named "cube". The variant's real geometry is in
        // GetBounds(bv), which HasOpenCenter already reads for the same reason.
        //
        // This is the identical trap the lightOpacity comment below describes ("BlockShapeNew never
        // receives the shapes.xml value"), one line further on: ask the CLASS and you get the default,
        // ask the BLOCK and you get the truth.
        private static readonly Dictionary<int, bool> _fullCubeCache = new Dictionary<int, bool>();
        private static bool IsFullCube(Block b, BlockValue bv)
        {
            if (b.shape == null) return true;
            if (b.shape.IsSolidCube) return true;
            int key = (bv.type << 6) | (bv.rotation & 63);
            if (_fullCubeCache.TryGetValue(key, out bool full)) return full;
            Bounds[] arr = null;
            try { arr = b.shape.GetBounds(bv); } catch { /* modded shape misbehaving ⇒ treat as partial */ }
            full = false;
            if (arr != null)
                for (int i = 0; i < arr.Length; i++)
                {
                    Vector3 mn = arr[i].min, mx = arr[i].max;
                    const float E = 0.02f;
                    if (mn.x <= E && mn.y <= E && mn.z <= E
                        && mx.x >= 1f - E && mx.y >= 1f - E && mx.z >= 1f - E) { full = true; break; }
                }
            _fullCubeCache[key] = full;
            return full;
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
        private static bool IsSwingingLeaf(Block b, int type)
        {
            if (_swingCache.TryGetValue(type, out bool s)) return s;
            BlockPlacement p = null;
            try { p = b.BlockPlacementHelper; } catch { /* modded block without one ⇒ not a door */ }
            s = p is BlockPlacementDoor || p is BlockPlacementDrawBridge;
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
            // ...AND A MODEL ENTITY'S BOUNDS ARE NOT ITS GEOMETRY, so it can never earn this discount.
            // BlockShapeModelEntity.GetBounds falls straight through to BlockShape's default boundsArr
            // unless the block sets isCustomBounds, which almost none do -- the real shape of a closet, a
            // fridge, a bookcase or a door leaf lives in a Unity PREFAB the block layer cannot see. So
            // IsFullCube answers "partial" for all of them from a DEFAULT rather than from evidence, and
            // every door and every stick of furniture in the game was charged half its material.
            //
            // How far off that is, measured against this project's own spec: synth.js builds the scenario
            // scenes that define what a shut wood door should sound like and writes that door as wood 0.5 --
            // full material. In game the same door was 0.25. The acceptance gate could not see the gap
            // because the dumps bake CellLoss (see [[acoustics-parity-blind-spot]]), so the spec passed
            // 9/9 while the game ran every door at half strength.
            //
            // Charging furniture its full material is safe precisely because the disc exists: the Fresnel
            // integral already prices PARTIAL obstruction geometrically, patch by patch, so a path that
            // clips the corner of a chair pays for the fraction it clips. A blanket ×0.5 was doing that
            // job badly and twice. Light-passing model entities (bar gates, chain-link, fence doors --
            // lightOpacity 0) are genuinely open frames and keep their discount.
            if (b.shape != null && !IsFullCube(b, bv)
                && !(b.shape is BlockShapeModelEntity && b.lightOpacity != 0))
            {
                // Partial shapes split by the artists' LightOpacity marker (shapes.xml, 0|255):
                // 0 = marked as passing light — plates/trim, poles, frames, holed cubes, "destroyed"
                // wall pieces (526 of 2263 shapes) — thin or holed geometry that blocks movement but
                // mostly passes sound. Opaque partials (solid halves, wedges, ramps — unset ⇒ 255)
                // keep the half-wall rate.
                // READ THE BLOCK's resolved lightOpacity, NOT shape.LightOpacity: BlockShapeNew never
                // receives the shapes.xml value (only billboard/grass/water shape CLASSES hardcode 0);
                // the shape XML's property is merged into the variant BLOCK's properties and lands on
                // Block.lightOpacity at Init (decompile: ShapesFromXml.LoadProperties(props, shapeElem)
                // → Block.Init reads PropLightOpacity). Reading the shape field made this discount DEAD
                // for every shapes.xml partial — a woodShapes:plateHalf trim plate flat on a wall was
                // charged half a wood wall PER CELL (8.75 virtual blocks at WallDistance 35), which is
                // why decorative trim audibly blocked sound (user report 2026-07-08).
                // Model entities (doors/hatches) are excluded regardless of their block XML: a thin
                // door leaf SEALS its opening — its acoustics are the damage/swing logic, never the
                // light marker.
                m *= (b.lightOpacity == 0 && !(b.shape is BlockShapeModelEntity)) ? 0.2f : 0.5f;
            }
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
        private static int _doorCellsBlended;   // diagnostic: cells the door branch blended last tick

        // The door hook can fire OFF the main thread (7D2D runs door animation/physics on job threads), so it
        // must NOT touch _doors/_doorTypes directly — doing so raced RefreshDoorAnim's enumeration and the
        // flood's reads ("Collection was modified", which aborted the whole emitter tick). Instead it enqueues
        // here under a lock; RefreshDoorAnim drains this on the main thread, so _doors/_doorTypes stay
        // single-threaded for every read (CellLoss) and iteration.
        private struct PendingDoor { public int Type; public Vector3i Pos; public bool Opening; }
        private static readonly List<PendingDoor> _doorPending = new List<PendingDoor>();
        private static readonly object _doorLock = new object();

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
            float gap  = physical * Mathf.Max(1f, da.Span);
            float full = Mathf.Max(0.05f, da.Opening ? da.GapFullOpen : da.GapFull);
            float shut = Mathf.Clamp(da.GapClosed, 0f, full - 0.01f);
            return Mathf.Clamp01((gap - shut) / (full - shut));
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
        internal static void InvalidateFieldForAnimatingDoors()
        {
#if GV_V3
            foreach (var kv in _doors) FieldOcclusion.Invalidate(kv.Key, 3);
#endif
        }

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
                if (minNt >= 0.95f)   // no clip has begun (stale end-of-previous) → hold at start, keep waiting
                {
                    da.Openness = da.Opening ? 0f : 1f;
                    return da.Ticks < 12;   // give up after ~a few seconds if no clip ever plays
                }
                da.Started = true;
            }
            if (swingLen < 0f) { swingNt = minNt; swingLen = 0f; }   // no fresh clip this tick — fall back

            float t = Mathf.Clamp01(swingNt);
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
                CMMConfig.D($"[CMM] DoorTrace {(da.Opening ? "OPEN " : "CLOSE")} {(dbb != null ? dbb.GetBlockName() : "?")} " +
                            $"t={Time.realtimeSinceStartup - da.StartedAt:0.000}s nt={t:0.000} len={swingLen:0.00}s " +
                            $"span={da.Span:0.##}m gap={physical * Mathf.Max(1f, da.Span):0.00}m " +
                            $"thresh={(da.Opening ? da.GapFullOpen : da.GapFull):0.00}/{da.GapClosed:0.00} open={da.Openness:0.000} " +
                            $"cells={_doorCellsBlended}");
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
