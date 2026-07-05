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

        // Fill extra[0..count) with each speaker's occlusion VIRTUAL DISTANCE (extra blocks added to its
        // real distance; 0 = clear line). NOT a 0..1 gain — the native spatializer adds it to the
        // Euclidean distance (dEff = d + extra) so a wall reads as "farther" (gradual), not a cut.
        internal static void ComputeOcclusion(Vector3 ear, float[] pts, int count, float[] extra, World world)
        {
            if (count <= 0) return;
            float wallDist = CMMConfig.WorldEmitterOcclusionWallDistance;   // virtual blocks a FULL solid wall adds
            float maxDist  = CMMConfig.WorldEmitterOcclusionMaxDistance;    // cap (a buried speaker stays faintly audible)

            // PathRouting off ⇒ direct-line only (no geodesic go-around): the same per-cell material loss
            // summed straight ear→speaker. Kept as an escape hatch / A-B.
            if (!CMMConfig.WorldEmitterPathRouting)
            {
                for (int i = 0; i < count; i++)
                {
                    float e = SegmentLoss(ear, Pt(pts, i), world) * wallDist;
                    extra[i] = e > maxDist ? maxDist : e;
                }
                return;
            }

            RunField(ear, world, wallDist);   // one weighted flood from the ear serves every speaker

            int earX = Mathf.FloorToInt(ear.x), earY = Mathf.FloorToInt(ear.y), earZ = Mathf.FloorToInt(ear.z);
            for (int i = 0; i < count; i++)
            {
                int o = i * 3;
                int sx = Mathf.FloorToInt(pts[o]), sy = Mathf.FloorToInt(pts[o + 1]), sz = Mathf.FloorToInt(pts[o + 2]);

                float e;
                int arrivalScaled = SampleArrival(sx, sy, sz);
                if (arrivalScaled >= 0)
                {
                    // Cheapest path length (blocks) minus the straight-line block count ⇒ the extra the
                    // walls / detour cost. In open air the two are equal ⇒ ~0 (open-air feel unchanged).
                    float arrivalBlocks = arrivalScaled * (1f / CostScale);
                    int man = Mathf.Abs(sx - earX) + Mathf.Abs(sy - earY) + Mathf.Abs(sz - earZ);
                    e = arrivalBlocks - man;
                    if (e < 0f) e = 0f;
                }
                else
                {
                    // Unreached (outside the search box / budget). Fall back to the direct ray using the
                    // SAME per-cell loss, so the flood and the fallback agree where they meet (no pop).
                    e = SegmentLoss(ear, Pt(pts, i), world) * wallDist;
                }
                extra[i] = e > maxDist ? maxDist : e;
            }
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
            if (b.IsSeeThrough(world, new Vector3i(x, y, z), bv))
                return b.IsCollideMovement ? 0.15f : 0f;   // glass pane leaks slightly; open gap is free
            float m = MaterialLoss(b.blockMaterial != null ? b.blockMaterial.SurfaceCategory : null);
            if (b.shape != null && !b.shape.IsSolidCube) m *= 0.5f;   // half-blocks / trims transmit more
            return m;
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

        // TODO(remove): SLATED FOR REMOVAL. This flood (path routing) was our way of carrying sound
        // "around corners" before we had reverb. The reverb stage now does that job properly (its wet
        // path ignores occlusion and leaks through/around walls), so running both double-counts and, by
        // the user's ear, sounds worse than pure straight-line occlusion. Default is FloodRadius=1
        // (flood barely expands ⇒ every speaker falls back to the SegmentLoss direct ray). Once the
        // direct+reverb combo is confirmed, delete RunField / Relax / SampleArrival / the Dijkstra
        // buffers below and the PathRouting/FloodRadius config, leaving ComputeOcclusion = SegmentLoss.
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
            for (int i = 0; i < _touchCount; i++) _cost[_touched[i]] = Inf;
            _touchCount = 0;
            for (int c = 0; c <= MaxBucket; c++) _bucketHead[c] = -1;   // small, fixed — cheap to blanket-clear

            _wallCostScaled = Mathf.RoundToInt(wallDist * CostScale);
            if (_wallCostScaled < 0) _wallCostScaled = 0;

            int earX = Mathf.FloorToInt(ear.x), earY = Mathf.FloorToInt(ear.y), earZ = Mathf.FloorToInt(ear.z);
            _fMinX = earX - MaxFloodRadius; _fMinY = earY - MaxFloodRadius; _fMinZ = earZ - MaxFloodRadius;
            _fEffR = Mathf.Clamp(CMMConfig.WorldEmitterFloodRadius, 1, MaxFloodRadius);

            int startIdx = (earX - _fMinX) + (earY - _fMinY) * Dim + (earZ - _fMinZ) * Dim2;
            _cost[startIdx] = 0;
            _touched[_touchCount++] = startIdx;
            _bucketHead[0] = startIdx; _next[startIdx] = -1;

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
                        Relax(x + 1, y, z, earX, earY, earZ, c, world);
                        Relax(x - 1, y, z, earX, earY, earZ, c, world);
                        Relax(x, y + 1, z, earX, earY, earZ, c, world);
                        Relax(x, y - 1, z, earX, earY, earZ, c, world);
                        Relax(x, y, z + 1, earX, earY, earZ, c, world);
                        Relax(x, y, z - 1, earX, earY, earZ, c, world);
                    }
                    idx = nxt;
                }
            }
        }

        // Relax one neighbour: enter-cost from its block (air cheap, solid dear by material), insert /
        // decrease-key into the bucket queue. Everything is passable — a wall is expensive, never a hard
        // barrier — so the field is continuous and every in-box speaker gets a finite distance.
        private static void Relax(int x, int y, int z, int earX, int earY, int earZ, int c, World world)
        {
            if (y < 0 || y >= 256) return;
            // Keep the search local: a box of half-extent _fEffR around the ear (Chebyshev).
            if (x < earX - _fEffR || x > earX + _fEffR ||
                y < earY - _fEffR || y > earY + _fEffR ||
                z < earZ - _fEffR || z > earZ + _fEffR) return;

            int lx = x - _fMinX, ly = y - _fMinY, lz = z - _fMinZ;
            int idx = lx + ly * Dim + lz * Dim2;

            int enter = CostScale + Mathf.RoundToInt(_wallCostScaled * CellLoss(world.GetBlock(x, y, z), world, x, y, z));
            int nc = c + enter;
            if (nc > MaxBucket) return;                 // past the ceiling — treat as unreachable
            if (nc >= _cost[idx]) return;               // no improvement

            if (_cost[idx] == Inf && _touchCount < _touched.Length)
                _touched[_touchCount++] = idx;                       // record each cell once (guarded)
            _cost[idx] = nc;
            _next[idx] = _bucketHead[nc]; _bucketHead[nc] = idx;    // (lazy decrease-key: old entry left stale)
        }

        // Cheapest arrival cost at a speaker: the min over its 3×3×3 neighbourhood, so we read the cost
        // of sound reaching the speaker's SURFACE (an adjacent open cell) rather than the cost of the
        // last punch INTO its own solid block. Returns -1 if nothing in the box was reached.
        private static int SampleArrival(int sx, int sy, int sz)
        {
            int best = Inf;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = sx + dx, y = sy + dy, z = sz + dz;
                int lx = x - _fMinX, ly = y - _fMinY, lz = z - _fMinZ;
                if (lx < 0 || lx >= Dim || ly < 0 || ly >= Dim || lz < 0 || lz >= Dim) continue;
                int cst = _cost[lx + ly * Dim + lz * Dim2];
                if (cst < best) best = cst;
            }
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
        private const float ProbeMaxDist = 32f;   // blocks; a surface farther than this reads as "open"
        private static readonly Vector3[] _probeDirs =
        {
            new Vector3( 1, 0, 0), new Vector3(-1, 0, 0),
            new Vector3( 0, 1, 0), new Vector3( 0,-1, 0),
            new Vector3( 0, 0, 1), new Vector3( 0, 0,-1),
            // 8 diagonals (normalized) so a room's corners/ceiling/floor mix in, not just the 6 axes
            new Vector3( 0.577f, 0.577f, 0.577f), new Vector3(-0.577f, 0.577f, 0.577f),
            new Vector3( 0.577f, 0.577f,-0.577f), new Vector3(-0.577f, 0.577f,-0.577f),
            new Vector3( 0.577f,-0.577f, 0.577f), new Vector3(-0.577f,-0.577f, 0.577f),
            new Vector3( 0.577f,-0.577f,-0.577f), new Vector3(-0.577f,-0.577f,-0.577f),
        };

        internal static void ProbeRoom(Vector3 ear, World world, out float avgDist, out float reflect, out float openFrac)
        {
            int n = _probeDirs.Length, hits = 0;
            float sumDist = 0f, sumReflect = 0f;
            for (int i = 0; i < n; i++)
            {
                if (ProbeRay(ear, _probeDirs[i], world, out float dist, out float rfl))
                {
                    hits++; sumDist += dist; sumReflect += rfl;
                }
                else sumDist += ProbeMaxDist;   // open ray counts as a far "wall" for the size estimate
            }
            avgDist  = sumDist / n;
            reflect  = hits > 0 ? sumReflect / hits : 0f;
            openFrac = (float)(n - hits) / n;
        }

        // March until the first opaque (non-see-through) surface; report its distance + reflectivity.
        private static bool ProbeRay(Vector3 from, Vector3 dir, World world, out float dist, out float reflect)
        {
            dist = ProbeMaxDist; reflect = 0f;
            int x = Mathf.FloorToInt(from.x), y = Mathf.FloorToInt(from.y), z = Mathf.FloorToInt(from.z);
            int stepX = dir.x > 0 ? 1 : (dir.x < 0 ? -1 : 0);
            int stepY = dir.y > 0 ? 1 : (dir.y < 0 ? -1 : 0);
            int stepZ = dir.z > 0 ? 1 : (dir.z < 0 ? -1 : 0);
            float tMaxX = AxisTMax(from.x, dir.x, x, stepX), tDeltaX = AxisTDelta(dir.x);
            float tMaxY = AxisTMax(from.y, dir.y, y, stepY), tDeltaY = AxisTDelta(dir.y);
            float tMaxZ = AxisTMax(from.z, dir.z, z, stepZ), tDeltaZ = AxisTDelta(dir.z);

            for (int guard = 0; guard < 64; guard++)
            {
                float t;
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ) { x += stepX; t = tMaxX; tMaxX += tDeltaX; }
                else if (tMaxY <= tMaxZ)              { y += stepY; t = tMaxY; tMaxY += tDeltaY; }
                else                                  { z += stepZ; t = tMaxZ; tMaxZ += tDeltaZ; }
                if (t > ProbeMaxDist) return false;
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
        internal static void ComputeReverb(float avgDist, float reflect, float openFrac,
                                           out float wet, out float room, out float damp)
        {
            float enclosure = 1f - openFrac;                              // 0 = wide open, 1 = sealed room
            wet  = Mathf.Clamp01(enclosure * enclosure * reflect);        // needs BOTH walls and reflective surfaces
            room = Mathf.Clamp01((avgDist - 2f) / 24f);                   // ~2 blocks (closet) → 0, ~26 (hall) → 1
            damp = Mathf.Clamp01(0.25f + 0.7f * (1f - reflect));          // dead materials → darker/shorter tail
        }
    }
}
#endif
