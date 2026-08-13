// Gated to 3.0 (GV_V3): same world APIs as EmitterAcoustics / EmitterAudioLayer.
#if GV_V3
using System.Collections.Generic;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // PER-SPEAKER ACOUSTIC FIELD CACHE ("Phase 2")
    // ─────────────────────────────────────────────────────────────────────────
    // The ear-anchored flood rebuilds the whole cost field around a MOVING listener 4×/s and
    // caps at NodeBudget ≈ a 12-block air sphere — so most of the audible field was really the
    // boundary-blend fallback, whose per-cell straight-line lottery is what the offline judge
    // measured as tens of thousands of >12 dB adjacent-cell jumps (the "walks don't sound
    // natural" defect; see tools/CmmAcousticsJudge, 2026-07-11).
    //
    // This inverts the anchor: ONE CACHED FIELD PER SPEAKER, seeded at the (static) speaker,
    // budget lifted so it fills its whole box. The listener then just SAMPLES a stationary
    // field as they move — spatial continuity by construction (adjacent cells differ by one
    // step's enter-cost). Prototyped as judge3d V5: dry cliffs −83…97% on buildings/bunker.
    //
    // Division of labour (settled with the user):
    //   • STEADY STATE (no door mid-swing, fields warm) → sample fields here.
    //   • DOOR MID-SWING → EmitterAudioLayer falls back to the legacy ear-anchored path, whose
    //     per-tick door-openness sampling the swing feel was tuned on. When the swing settles,
    //     every cached field is dropped (its door state is stale) and rebuilt; legacy covers
    //     the gap. Fields never need mid-swing accuracy.
    //   • WARM-UP (fields still building) → legacy path, flip when all audible are ready
    //     (the native gain slew smooths the flip).
    //   • Reverb ROOM measure stays listener-anchored: the field path zeroes AirCellCount so
    //     PushReverb takes its existing ray-probe fallback.
    //   • Future moving sources (enemies) keep the ear-anchored flood — that architecture is
    //     inherently per-listener; fields are the static-speaker specialization.
    //
    // Beyond a field's box (or unreached cells), the boundary blend runs WITH the two fixes the
    // judge validated: OWN-CELL anchoring (the 3×3×3 neighbourhood hop let a march point read a
    // flood cell THROUGH a thin roof — the slab was charged nowhere) and a 5-LINE FAN (adjacent
    // listener cells sharing most lines ⇒ no more single-line anchor lottery).
    //
    // Memory: dim³ × 2×Int16 per field (r=48 → 3.6 MB), ≤ MaxFields held, plus one shared build
    // scratch. Main-thread only (voxel reads); builds sliced per frame like the legacy flood.
    internal static class EmitterFieldCache
    {
        private const int MaxFields = 12;        // LRU cap (≥ MaxPoints so one POI's set fits)
        private const int FanLines = 5;          // boundary-blend march fan (1 = single line)
        private const float BuildBudgetMs = 3f;  // per-frame build slice
        private const int CostScale = 4;         // MUST match EmitterAcoustics.CostScale
        private const int MaxBucket = 2048;      //   "      "        "        .MaxBucket
        private const int Inf = int.MaxValue;

        private sealed class Field
        {
            public Vector3i Pos;                 // speaker cell (field seed)
            public int MinX, MinY, MinZ, Dim, EffR;
            public short[] Cost, Wall;           // -1 = unreached; scaled units (≤ MaxBucket fits short)
            public bool Ready;
            public float BuiltAt;                // realtime of last completed build
            public float WallDist;               // config captured at build (mismatch ⇒ stale)
            public float LastWanted;             // LRU touch
        }

        private static readonly Dictionary<Vector3i, Field> _fields = new Dictionary<Vector3i, Field>();
        private static readonly List<Vector3i> _evictScratch = new List<Vector3i>();

        // ── Shared build scratch (one build in flight) ────────────────────────
        private static int[] _bNext;             // dim³ bucket links
        private static int[] _bHead;             // MaxBucket+1
        private static short[] _bCost, _bWall;   // building into spares; swapped in on completion
        private static int _bDimAlloc;           // scratch allocation size (dim it was sized for)
        private static Field _building;          // in-flight target (null = idle)
        private static int _bCursorC, _bCursorIdx;
        private static bool _doorSeen;           // a swing was OBSERVED since the last invalidation

        // ── Warm-up instrumentation (DebugLogging only) ───────────────────────
        // The field path only engages when EVERY wanted speaker's field is ready, builds run ONE at
        // a time, and a door swing invalidates all of them. So "is the good path actually running?"
        // is really "do the builds finish faster than doors invalidate them?" — these expose that.
        // StatWanted/StatReady are a live snapshot; the counters accumulate until the layer's 1 Hz
        // summary drains them.
        internal static int   StatWanted, StatReady;    // live: wanted this tick / of those, ready
        internal static int   StatBuilds;               // builds COMPLETED since the last drain
        internal static int   StatDoorDrops;            // whole-cache invalidations since the last drain
        internal static float StatLastBuildMs;          // wall time of the last completed build (summed slices)
        internal static bool  StatBuilding => _building != null;
        internal static void  StatDrain() { StatBuilds = 0; StatDoorDrops = 0; }
        private static float _bMsTotal;                 // accumulating slice time of the in-flight build

        private static int Radius() => Mathf.Clamp(CMMConfig.WorldEmitterFieldRadius, 16, 48);

        // ── Maintenance (each 4 Hz tick, BEFORE sampling): ensure fields exist/fresh for the
        // wanted speaker cells, advance eviction, react to door-settle. Cheap when warm. ──────
        internal static void Maintain(World world, List<Vector3i> wanted)
        {
            // Door edges are handled in BuildStep (it runs every FRAME, so it can't miss a swing
            // the way this 4 Hz call can — v1 bug: the edge tracker lived here, but Maintain never
            // runs DURING a swing, so the falling edge was never seen and fields kept the
            // pre-swing door state baked in ["spotty" door acoustics, user 2026-07-11]).
            if (EmitterAcoustics.AnyDoorAnimating) { _doorSeen = true; return; }
            DrainDoorInvalidation();

            float now = Time.realtimeSinceStartup;
            int radius = Radius();
            float wallDist = CMMConfig.WorldEmitterOcclusionWallDistance;

            for (int i = 0; i < wanted.Count; i++)
            {
                if (!_fields.TryGetValue(wanted[i], out Field f))
                {
                    f = new Field { Pos = wanted[i] };
                    _fields[wanted[i]] = f;
                }
                f.LastWanted = now;
                // Radius/wallDist config moved ⇒ the field no longer answers the live question.
                if (f.Ready && (f.EffR != radius || f.WallDist != wallDist)) f.Ready = false;
            }

            if (CMMConfig.DebugLogging)
            {
                StatWanted = wanted.Count;
                StatReady = 0;
                for (int i = 0; i < wanted.Count; i++)
                    if (_fields.TryGetValue(wanted[i], out Field rf) && rf.Ready) StatReady++;
            }

            // Evict beyond the cap: least-recently-wanted first (never the in-flight build).
            if (_fields.Count > MaxFields)
            {
                _evictScratch.Clear();
                foreach (var kv in _fields) _evictScratch.Add(kv.Key);
                _evictScratch.Sort((a, b) => _fields[a].LastWanted.CompareTo(_fields[b].LastWanted));
                for (int i = 0; i < _evictScratch.Count && _fields.Count > MaxFields; i++)
                {
                    Field f = _fields[_evictScratch[i]];
                    if (f.LastWanted >= now) continue;           // wanted this very tick
                    if (ReferenceEquals(f, _building)) continue;
                    _fields.Remove(_evictScratch[i]);
                }
            }

            // Pick the next build: any wanted-but-not-ready field first, else the stalest
            // refresh-due one (stale fields stay READY + in use; the rebuild swaps in quietly).
            if (_building == null)
            {
                Field pick = null;
                foreach (var kv in _fields)
                {
                    Field f = kv.Value;
                    if (f.LastWanted < now) continue;            // only currently-wanted speakers
                    if (!f.Ready) { pick = f; break; }
                    if (now - f.BuiltAt > CMMConfig.WorldEmitterFieldRefresh &&
                        (pick == null || f.BuiltAt < pick.BuiltAt)) pick = f;
                }
                if (pick != null) BeginBuild(pick, radius, wallDist);
            }
        }

        internal static bool Ready(Vector3i pos)
            => _fields.TryGetValue(pos, out Field f) && f.Ready;

        // ── Sliced build (call once per FRAME while a voice is live) ──────────
        private static void BeginBuild(Field f, int radius, float wallDist)
        {
            int dim = radius * 2 + 1, n = dim * dim * dim;
            if (_bNext == null || _bDimAlloc != dim)
            {
                _bNext = new int[n];
                _bHead = new int[MaxBucket + 1];
                _bCost = new short[n];
                _bWall = new short[n];
                _bDimAlloc = dim;
            }
            for (int i = 0; i < n; i++) _bCost[i] = -1;
            for (int c = 0; c <= MaxBucket; c++) _bHead[c] = -1;

            f.EffR = radius; f.Dim = dim; f.WallDist = wallDist;
            f.MinX = f.Pos.x - radius; f.MinY = f.Pos.y - radius; f.MinZ = f.Pos.z - radius;
            int si = radius + radius * dim + radius * dim * dim;
            _bCost[si] = 0; _bWall[si] = 0; _bHead[0] = si; _bNext[si] = -1;
            _bCursorC = 0; _bCursorIdx = -2;
            _bMsTotal = 0f;
            _building = f;
        }

        // A door swing settled since we last looked ⇒ every field may bake a stale door state:
        // drop them all (not-ready ⇒ the layer stays on the legacy path until rebuilds land,
        // so the post-swing acoustics are always live, never a snapshot of the old leaf).
        private static void DrainDoorInvalidation()
        {
            if (!_doorSeen) return;
            _doorSeen = false;
            foreach (Field f in _fields.Values) f.Ready = false;
            _building = null;
            StatDoorDrops++;
            CMMConfig.D($"[CMM] FieldCache: door settled — all {_fields.Count} fields invalidated for rebuild.");
        }

        internal static void BuildStep(World world, float budgetMs = BuildBudgetMs)
        {
            // Per-FRAME door watch: a swing can start and settle entirely between the 4 Hz
            // Maintain calls; this is the reliable edge observer.
            if (EmitterAcoustics.AnyDoorAnimating) { _doorSeen = true; return; }
            DrainDoorInvalidation();
            if (_building == null) return;
            Field f = _building;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int dim = f.Dim, dim2 = dim * dim;
            int wcs = Mathf.RoundToInt(f.WallDist * CostScale);
            int c = _bCursorC, idx = _bCursorIdx, sinceCheck = 0;

            while (c <= MaxBucket)
            {
                if (idx == -2) idx = _bHead[c];
                while (idx != -1)
                {
                    int nxt = _bNext[idx];
                    if (_bCost[idx] == c)
                    {
                        int lz = idx / dim2, rem = idx - lz * dim2, ly = rem / dim, lx = rem - ly * dim;
                        int x = lx + f.MinX, y = ly + f.MinY, z = lz + f.MinZ;
                        int pw = _bWall[idx];
                        RelaxBuild(f, world, x + 1, y, z, c, pw, wcs);
                        RelaxBuild(f, world, x - 1, y, z, c, pw, wcs);
                        RelaxBuild(f, world, x, y + 1, z, c, pw, wcs);
                        RelaxBuild(f, world, x, y - 1, z, c, pw, wcs);
                        RelaxBuild(f, world, x, y, z + 1, c, pw, wcs);
                        RelaxBuild(f, world, x, y, z - 1, c, pw, wcs);
                        if (++sinceCheck >= 48)
                        {
                            sinceCheck = 0;
                            if ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * toMs >= budgetMs)
                            {
                                _bCursorC = c; _bCursorIdx = nxt;
                                _bMsTotal += (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * toMs);
                                return;   // resume next frame
                            }
                        }
                    }
                    idx = nxt;
                }
                c++; idx = -2;
            }

            // Complete: swap the built arrays in (the old ones become the next build's spares),
            // so a stale-but-ready field is never seen half-written.
            short[] oc = f.Cost, ow = f.Wall;
            f.Cost = _bCost; f.Wall = _bWall;
            _bCost = oc ?? new short[dim * dim * dim];
            _bWall = ow ?? new short[dim * dim * dim];
            if (_bCost.Length != dim * dim * dim) _bCost = new short[dim * dim * dim];
            if (_bWall.Length != dim * dim * dim) _bWall = new short[dim * dim * dim];
            f.Ready = true;
            f.BuiltAt = Time.realtimeSinceStartup;
            _building = null;
            _bMsTotal += (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * toMs);
            StatLastBuildMs = _bMsTotal;
            StatBuilds++;
            // cpuMs = CPU actually spent; the wall-clock cost is that spread over ~BuildBudgetMs
            // slices, one per frame — which is what decides whether builds outrun door invalidations.
            CMMConfig.D($"[CMM] FieldCache: built field @({f.Pos.x},{f.Pos.y},{f.Pos.z}) r={f.EffR} " +
                        $"cpuMs={_bMsTotal:0.0} ready={StatReady}/{StatWanted}");
        }

        private static void RelaxBuild(Field f, World world, int x, int y, int z, int c, int pw, int wcs)
        {
            if (y < 0 || y >= 256) return;
            int lx = x - f.MinX, ly = y - f.MinY, lz = z - f.MinZ;
            if (lx < 0 || lx >= f.Dim || ly < 0 || ly >= f.Dim || lz < 0 || lz >= f.Dim) return;
            int idx = lx + ly * f.Dim + lz * f.Dim * f.Dim;
            int wallEnter = Mathf.RoundToInt(wcs * EmitterAcoustics.DebugCellLoss(world.GetBlock(x, y, z), world, x, y, z));
            int nc = c + CostScale + wallEnter;
            if (nc > MaxBucket) return;
            if (_bCost[idx] != -1 && nc >= _bCost[idx]) return;
            _bCost[idx] = (short)nc;
            _bWall[idx] = (short)Mathf.Min(short.MaxValue, pw + wallEnter);
            _bNext[idx] = _bHead[nc]; _bHead[nc] = idx;
        }

        // ── Sampling: one station's speakers against their cached fields ─────────────────────
        // Fills extra[] (occlusion virtual distance), rotates pts[] toward the arrival route
        // (directional), and returns the station's reverb inputs — the same contract as
        // EmitterAcoustics.OcclusionFinish, just sourced from static fields. Returns false if
        // ANY speaker's field isn't ready (caller stays on the legacy path this tick).
        internal static bool SampleStation(World world, Vector3 ear, float[] pts, int count, float[] extra,
                                           out float seal, out float solid, out float depth)
        {
            seal = 1f; solid = float.MaxValue; depth = float.MaxValue;
            if (count <= 0) return false;
            for (int i = 0; i < count; i++)
                if (!Ready(PtCell(pts, i))) return false;

            int earX = Mathf.FloorToInt(ear.x), earY = Mathf.FloorToInt(ear.y), earZ = Mathf.FloorToInt(ear.z);
            float range = CMMConfig.WorldEmitterRange;

            for (int i = 0; i < count; i++)
            {
                Field f = _fields[PtCell(pts, i)];
                Vector3 spk = new Vector3(pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]);
                float d = (spk - ear).magnitude;
                int man = Mathf.Abs(earX - f.Pos.x) + Mathf.Abs(earY - f.Pos.y) + Mathf.Abs(earZ - f.Pos.z);
                int cheb = Mathf.Max(Mathf.Abs(earX - f.Pos.x), Mathf.Max(Mathf.Abs(earY - f.Pos.y), Mathf.Abs(earZ - f.Pos.z)));

                float raw, sol, solGate;
                int traceX = earX, traceY = earY, traceZ = earZ;   // descent start for the arrival trace
                bool canTrace = false;
                char branch = 'R';   // same tally alphabet as the legacy path (see EmitterAcoustics.StatNote)

                int earIdx = FieldIdx(f, earX, earY, earZ);
                int cEar = earIdx >= 0 ? f.Cost[earIdx] : -1;
                if (cEar >= 0 && cheb <= f.EffR)
                {
                    // ROUTED: the ear stands inside the speaker's field.
                    raw = Mathf.Max(0f, cEar * (1f / CostScale) - man);
                    sol = f.Wall[earIdx] * (1f / CostScale);
                    solGate = MinWall3x3(f, earX, earY, earZ) * (1f / CostScale);
                    canTrace = true;
                }
                else if (MarchFan(f, world, ear, earX, earY, earZ, man, range,
                                  out raw, out sol, out solGate, out traceX, out traceY, out traceZ))
                {
                    canTrace = true;   // boundary blend: descend from the anchor cell
                    branch = 'B';
                }
                else
                {
                    // No air route into the field at all: direct ray (charged), genuinely sealed.
                    raw = EmitterAcoustics.SegmentLoss(ear, spk, world) * CMMConfig.WorldEmitterOcclusionWallDistance;
                    sol = raw; solGate = raw;
                    branch = 'F';
                }

                extra[i] = EmitterAcoustics.ShapeOcclusion(raw, sol, solGate, range, out float gate);
                EmitterAcoustics.StatNote(PtCell(pts, i), extra[i], branch);
                if (gate < seal) seal = gate;
                if (solGate < solid) solid = solGate;
                float airDetour = raw - sol; if (airDetour < 0f) airDetour = 0f;
                if (d + airDetour < depth) depth = d + airDetour;

                // DIRECTIONAL: same blend as the legacy path — rotate the virtual source toward
                // the air route when the straight line runs through solid.
                if (CMMConfig.WorldEmitterOcclusionDirectional && canTrace)
                {
                    float directSolid = EmitterAcoustics.SegmentLoss(ear, spk, world);
                    float b = Smooth(0.35f, 1.5f, directSolid);
                    if (b > 0.001f && DescentBearing(f, traceX, traceY, traceZ, ear, out Vector3 adir))
                    {
                        Vector3 realDir = spk - ear;
                        float dist = realDir.magnitude;
                        if (dist > 0.01f)
                        {
                            realDir /= dist;
                            Vector3 blended = Vector3.Lerp(realDir, adir, b);
                            if (blended.sqrMagnitude > 1e-6f)
                            {
                                blended.Normalize();
                                Vector3 np = ear + blended * dist;
                                pts[i * 3] = np.x; pts[i * 3 + 1] = np.y; pts[i * 3 + 2] = np.z;
                            }
                        }
                    }
                }
            }
            if (solid == float.MaxValue) solid = 0f;
            if (depth == float.MaxValue) depth = 0f;
            return true;
        }

        // Boundary blend with the judge-validated fixes: 5 jittered lines from the ear toward
        // the speaker, anchored at the first cell the field REACHED ITSELF (own-cell — the
        // 3×3×3 hop is only valid at the speaker endpoint; at intermediate points it let the
        // route skip a thin slab's material entirely). Best (min shaped extra) candidate wins.
        private static bool MarchFan(Field f, World world, Vector3 ear, int earX, int earY, int earZ,
                                     int man, float range, out float raw, out float sol, out float solGate,
                                     out int ax, out int ay, out int az)
        {
            raw = sol = solGate = 0f; ax = earX; ay = earY; az = earZ;
            int adx = Mathf.Abs(f.Pos.x - earX), ady = Mathf.Abs(f.Pos.y - earY), adz = Mathf.Abs(f.Pos.z - earZ);
            int a1x = 0, a1y = 0, a1z = 0, a2x = 0, a2y = 0, a2z = 0;
            if (adx >= ady && adx >= adz) { a1y = 1; a2z = 1; }
            else if (ady >= adz)          { a1x = 1; a2z = 1; }
            else                          { a1x = 1; a2y = 1; }

            bool found = false; float bestExtra = float.MaxValue;
            for (int line = 0; line < FanLines; line++)
            {
                int sx = earX, sy = earY, sz = earZ;
                if (line == 1) { sx += a1x; sy += a1y; sz += a1z; }
                else if (line == 2) { sx -= a1x; sy -= a1y; sz -= a1z; }
                else if (line == 3) { sx += a2x; sy += a2y; sz += a2z; }
                else if (line == 4) { sx -= a2x; sy -= a2y; sz -= a2z; }

                int ddx = f.Pos.x - sx, ddy = f.Pos.y - sy, ddz = f.Pos.z - sz;
                int steps = Mathf.Max(Mathf.Abs(ddx), Mathf.Max(Mathf.Abs(ddy), Mathf.Abs(ddz)));
                for (int s = 1; s <= steps; s++)
                {
                    int px = sx + Mathf.RoundToInt((float)ddx * s / steps);
                    int py = sy + Mathf.RoundToInt((float)ddy * s / steps);
                    int pz = sz + Mathf.RoundToInt((float)ddz * s / steps);
                    int pid = FieldIdx(f, px, py, pz);
                    if (pid < 0 || f.Cost[pid] < 0) continue;

                    float routed = f.Cost[pid] * (1f / CostScale);
                    float bWall = f.Wall[pid] * (1f / CostScale);
                    int remMan = Mathf.Abs(earX - px) + Mathf.Abs(earY - py) + Mathf.Abs(earZ - pz);
                    float remWall = EmitterAcoustics.SegmentLoss(new Vector3(px + 0.5f, py + 0.5f, pz + 0.5f), ear, world)
                                    * CMMConfig.WorldEmitterOcclusionWallDistance;
                    float cRaw = Mathf.Max(0f, routed + remMan + remWall - man);
                    float cSol = bWall + remWall;
                    float cGate = bWall;   // measured solid only — the straight remainder can't seal
                    float ex = EmitterAcoustics.ShapeOcclusion(cRaw, cSol, cGate, range, out _);
                    if (ex < bestExtra)
                    {
                        bestExtra = ex; raw = cRaw; sol = cSol; solGate = cGate;
                        ax = px; ay = py; az = pz;
                        found = true;
                    }
                    break;   // first reached point on this line is its anchor
                }
            }
            return found;
        }

        // Steepest-descent walk down the speaker's field from a start cell (ear or anchor): a
        // few steps along the air route toward the speaker; the bearing from the EAR to that
        // point is the direction the sound arrives from. Mirrors the legacy ArrivalDir intent.
        private const int DescentSteps = 4;
        private static bool DescentBearing(Field f, int cx, int cy, int cz, Vector3 ear, out Vector3 dir)
        {
            dir = Vector3.zero;
            int cid = FieldIdx(f, cx, cy, cz);
            if (cid < 0 || f.Cost[cid] < 0) return false;
            for (int step = 0; step < DescentSteps; step++)
            {
                int best = -1, bestCost = f.Cost[cid], bx = cx, by = cy, bz = cz;
                for (int face = 0; face < 6; face++)
                {
                    int nx = cx + (face == 0 ? 1 : face == 1 ? -1 : 0);
                    int ny = cy + (face == 2 ? 1 : face == 3 ? -1 : 0);
                    int nz = cz + (face == 4 ? 1 : face == 5 ? -1 : 0);
                    int nid = FieldIdx(f, nx, ny, nz);
                    if (nid < 0 || f.Cost[nid] < 0) continue;
                    if (f.Cost[nid] < bestCost) { bestCost = f.Cost[nid]; best = nid; bx = nx; by = ny; bz = nz; }
                }
                if (best < 0) break;   // local minimum = the speaker cell
                cid = best; cx = bx; cy = by; cz = bz;
            }
            dir = new Vector3(cx + 0.5f - ear.x, cy + 0.5f - ear.y, cz + 0.5f - ear.z);
            if (dir.sqrMagnitude < 1e-6f) return false;
            dir.Normalize();
            return true;
        }

        private static int MinWall3x3(Field f, int cx, int cy, int cz)
        {
            int mn = int.MaxValue;
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int id = FieldIdx(f, cx + dx, cy + dy, cz + dz);
                if (id < 0 || f.Cost[id] < 0) continue;
                if (f.Wall[id] < mn) mn = f.Wall[id];
            }
            return mn == int.MaxValue ? 0 : mn;
        }

        private static int FieldIdx(Field f, int x, int y, int z)
        {
            int lx = x - f.MinX, ly = y - f.MinY, lz = z - f.MinZ;
            if (lx < 0 || lx >= f.Dim || ly < 0 || ly >= f.Dim || lz < 0 || lz >= f.Dim) return -1;
            return lx + ly * f.Dim + lz * f.Dim * f.Dim;
        }

        private static Vector3i PtCell(float[] pts, int i)
            => new Vector3i(Mathf.FloorToInt(pts[i * 3]), Mathf.FloorToInt(pts[i * 3 + 1]), Mathf.FloorToInt(pts[i * 3 + 2]));

        private static float Smooth(float a, float b, float x)
        {
            if (b <= a) return x >= b ? 1f : 0f;
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        // Drop everything (config toggle off, radius change, game exit).
        internal static void Clear()
        {
            _fields.Clear();
            _building = null;
        }

        // ── acsweep support: sliced build + raw export (virtual-test parity) ──────────────────
        // The sweep drives BuildStep itself with a bigger budget — v1 built synchronously in one
        // frame, which froze the client for seconds per field during `cmm acsweep here`.
        internal static bool BuildIdle => _building == null;
        internal static void DebugBeginBuild(Vector3i pos)
        {
            if (!_fields.TryGetValue(pos, out Field f))
            {
                f = new Field { Pos = pos };
                _fields[pos] = f;
            }
            BeginBuild(f, Radius(), CMMConfig.WorldEmitterOcclusionWallDistance);
        }
        internal static bool DebugExportField(Vector3i pos, out int effR, System.Action<int, int, int, int, int> perCell)
        {
            effR = 0;
            if (!_fields.TryGetValue(pos, out Field f) || !f.Ready) return false;
            effR = f.EffR;
            for (int i = 0; i < f.Cost.Length; i++)
            {
                if (f.Cost[i] < 0) continue;
                int lz = i / (f.Dim * f.Dim), rem = i - lz * f.Dim * f.Dim, ly = rem / f.Dim, lx = rem - ly * f.Dim;
                perCell(lx + f.MinX, ly + f.MinY, lz + f.MinZ, f.Cost[i], f.Wall[i]);
            }
            return true;
        }
    }
}
#endif
