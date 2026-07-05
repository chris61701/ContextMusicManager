using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // EMITTER WORKLOAD (Arena C add-on) — the world-emitter feature's MAIN-THREAD cost.
    //
    // The world-emitter feature ([Harmony/EmitterAudioLayer.cs] + [EmitterRegistry.cs] +
    // [EmitterAcoustics.cs]) pipes a radio "station" through in-world speaker BLOCKS as
    // spatialised diegetic audio. Its DECODE + MIX are off-thread (native), but two costs land
    // on the MAIN thread every gameplay tick and this harness measures them:
    //
    //   (1) APPEARANCE — when a POI streams in, the registry SCANS its block volume for speaker
    //       ids (EmitterRegistry.ScanPrefabForTargets, throttled one POI / poll). A POI may hold
    //       MULTIPLE speakers, so the scan collects several emitter points.
    //   (2) SPATIALISATION — while a voice is live, EmitterAcoustics runs per decision tick: a
    //       weighted-flood / direct-ray OCCLUSION cost field (ear→each speaker through the real
    //       voxel materials) + a 14-ray ROOM PROBE (reverb). This scales with speakers-in-range.
    //
    // EmitterAcoustics is #if GV_V3 and reads UnityEngine.World/Block/BlockValue, so — exactly as
    // Engine.cs PORTS the Unity-coupled selection layer — the acoustics algorithm is copied here
    // VERBATIM (cited per block) and run against a headless voxel POI (VoxWorld). Same CostScale /
    // NodeBudget / bucket-Dijkstra / DDA / probe-ray arithmetic ⇒ the CPU cost is faithful; the only
    // substitution is a dense byte[] chunk (what World.GetBlock indexes anyway) for the game world.
    // Keep in lockstep with EmitterAcoustics.cs (see [[cmmsession-harness-drift]]).
    // ─────────────────────────────────────────────────────────────────────────

    // Headless stand-in for the 7D2D voxel world the acoustics march through: a dense block-id
    // grid over one reused region (the game indexes a chunk array the same way) + a small
    // block-property table (material / see-through / solid-cube), the fields CellLoss reads.
    internal sealed class VoxWorld
    {
        public const int RX = 128, RY = 96, RZ = 128;   // reused region dims
        private readonly byte[] _cells = new byte[RX * RY * RZ];

        // Block table (id → properties). Ids: 0 air, 1 concrete, 2 wood, 3 metal, 4 glass,
        // 5 wood-floor, 6 shelf(partial wood), 7 speaker(deco). Materials chosen to exercise the
        // MaterialLoss cache, the see-through branch (glass) and the partial-shape ×0.5 (shelf).
        public static readonly bool[]   Air        = { true,  false, false, false, false, false, false, false };
        public static readonly bool[]   SeeThrough = { true,  false, false, false, true,  false, false, false };
        public static readonly bool[]   CollideMov = { false, true,  true,  true,  true,  true,  false, false };
        public static readonly bool[]   SolidCube  = { false, true,  true,  true,  true,  true,  false, false };
        public static readonly string[] Surface    = { null,  "concrete", "wood", "metal", "glass", "wood", "wood", "plastic" };

        public void Clear() => Array.Clear(_cells, 0, _cells.Length);

        public void Set(int x, int y, int z, byte id)
        {
            if ((uint)x >= RX || (uint)y >= RY || (uint)z >= RZ) return;
            _cells[x + y * RX + z * RX * RY] = id;
        }

        // The GetBlock the ported acoustics hammer (up to ~NodeBudget×6 + ray steps per tick).
        // Out-of-region reads as air, exactly as World.GetBlock returns Air on unstreamed chunks.
        public byte Get(int x, int y, int z)
        {
            if ((uint)x >= RX || (uint)y >= RY || (uint)z >= RZ) return 0;
            return _cells[x + y * RX + z * RX * RY];
        }
    }

    // Faithful port of Harmony/EmitterAcoustics.cs (occlusion cost field + direct-ray fallback +
    // room probe), operating on VoxWorld. Instance-held preallocated buffers ⇒ allocation-free per
    // tick, mirroring the real class's no-GC contract.
    internal sealed class AcousticsSim
    {
        private readonly VoxWorld _w;
        public AcousticsSim(VoxWorld w) { _w = w; }

        internal const int MaxPoints = 8;

        // ── ComputeOcclusion — fill extra[0..count) with each speaker's occlusion VIRTUAL DISTANCE.
        internal void ComputeOcclusion(float ex, float ey, float ez, float[] pts, int count, float[] extra)
        {
            if (count <= 0) return;
            float wallDist = CMMConfig.WorldEmitterOcclusionWallDistance;
            float maxDist  = CMMConfig.WorldEmitterOcclusionMaxDistance;

            if (!CMMConfig.WorldEmitterPathRouting)
            {
                for (int i = 0; i < count; i++)
                {
                    float e = SegmentLoss(ex, ey, ez, pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2]) * wallDist;
                    extra[i] = e > maxDist ? maxDist : e;
                }
                return;
            }

            RunField(ex, ey, ez, wallDist);

            int earX = Floor(ex), earY = Floor(ey), earZ = Floor(ez);
            for (int i = 0; i < count; i++)
            {
                int o = i * 3;
                int sx = Floor(pts[o]), sy = Floor(pts[o + 1]), sz = Floor(pts[o + 2]);

                float e;
                int arrivalScaled = SampleArrival(sx, sy, sz);
                if (arrivalScaled >= 0)
                {
                    float arrivalBlocks = arrivalScaled * (1f / CostScale);
                    int man = Math.Abs(sx - earX) + Math.Abs(sy - earY) + Math.Abs(sz - earZ);
                    e = arrivalBlocks - man;
                    if (e < 0f) e = 0f;
                }
                else e = SegmentLoss(ex, ey, ez, pts[o], pts[o + 1], pts[o + 2]) * wallDist;
                extra[i] = e > maxDist ? maxDist : e;
            }
        }

        // ── Per-cell acoustics (verbatim CellLoss, reading the VoxWorld block table) ──
        private float CellLoss(int x, int y, int z)
        {
            byte id = _w.Get(x, y, z);
            if (VoxWorld.Air[id]) return 0f;
            if (VoxWorld.SeeThrough[id]) return VoxWorld.CollideMov[id] ? 0.15f : 0f;
            float m = MaterialLoss(VoxWorld.Surface[id]);
            if (!VoxWorld.SolidCube[id]) m *= 0.5f;
            return m;
        }

        private readonly Dictionary<string, float> _lossCache = new Dictionary<string, float>();
        private float MaterialLoss(string cat)
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

        // ── Acoustic cost field: weighted flood (Dial's-algorithm bucket queue) from the ear ──
        private const int   MaxFloodRadius = 32;
        private const int   CostScale      = 4;
        private const int   NodeBudget     = 8000;
        private const int   MaxBucket      = 2048;
        private static readonly int Dim  = MaxFloodRadius * 2 + 1;
        private static readonly int Dim2 = Dim * Dim;

        private int[] _cost, _next, _bucketHead, _touched;
        private int   _touchCount;
        private int   _fMinX, _fMinY, _fMinZ, _fEffR, _wallCostScaled;
        private const int Inf = int.MaxValue;

        private void RunField(float ex, float ey, float ez, float wallDist)
        {
            if (_cost == null)
            {
                int n = Dim * Dim * Dim;
                _cost = new int[n];
                _next = new int[n];
                _touched = new int[NodeBudget * 7 + 8];
                _bucketHead = new int[MaxBucket + 1];
                for (int i = 0; i < n; i++) _cost[i] = Inf;
                for (int c = 0; c <= MaxBucket; c++) _bucketHead[c] = -1;
                _touchCount = 0;
            }

            for (int i = 0; i < _touchCount; i++) _cost[_touched[i]] = Inf;
            _touchCount = 0;
            for (int c = 0; c <= MaxBucket; c++) _bucketHead[c] = -1;

            _wallCostScaled = Round(wallDist * CostScale);
            if (_wallCostScaled < 0) _wallCostScaled = 0;

            int earX = Floor(ex), earY = Floor(ey), earZ = Floor(ez);
            _fMinX = earX - MaxFloodRadius; _fMinY = earY - MaxFloodRadius; _fMinZ = earZ - MaxFloodRadius;
            _fEffR = Clamp(CMMConfig.WorldEmitterFloodRadius, 1, MaxFloodRadius);

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
                    if (_cost[idx] == c)
                    {
                        settled++;
                        int lz = idx / Dim2, rem = idx - lz * Dim2, ly = rem / Dim, lx = rem - ly * Dim;
                        int x = lx + _fMinX, y = ly + _fMinY, z = lz + _fMinZ;
                        Relax(x + 1, y, z, earX, earY, earZ, c);
                        Relax(x - 1, y, z, earX, earY, earZ, c);
                        Relax(x, y + 1, z, earX, earY, earZ, c);
                        Relax(x, y - 1, z, earX, earY, earZ, c);
                        Relax(x, y, z + 1, earX, earY, earZ, c);
                        Relax(x, y, z - 1, earX, earY, earZ, c);
                    }
                    idx = nxt;
                }
            }
        }

        private void Relax(int x, int y, int z, int earX, int earY, int earZ, int c)
        {
            if (y < 0 || y >= 256) return;
            if (x < earX - _fEffR || x > earX + _fEffR ||
                y < earY - _fEffR || y > earY + _fEffR ||
                z < earZ - _fEffR || z > earZ + _fEffR) return;

            int lx = x - _fMinX, ly = y - _fMinY, lz = z - _fMinZ;
            int idx = lx + ly * Dim + lz * Dim2;

            int enter = CostScale + Round(_wallCostScaled * CellLoss(x, y, z));
            int nc = c + enter;
            if (nc > MaxBucket) return;
            if (nc >= _cost[idx]) return;

            if (_cost[idx] == Inf && _touchCount < _touched.Length)
                _touched[_touchCount++] = idx;
            _cost[idx] = nc;
            _next[idx] = _bucketHead[nc]; _bucketHead[nc] = idx;
        }

        private int SampleArrival(int sx, int sy, int sz)
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

        // ── Direct-line fallback (Amanatides–Woo voxel DDA, same CellLoss) ──
        internal float SegmentLoss(float fx, float fy, float fz, float tx, float ty, float tz)
        {
            float dx = tx - fx, dy = ty - fy, dz = tz - fz;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist < 0.01f) return 0f;
            dx /= dist; dy /= dist; dz /= dist;

            int x = Floor(fx), y = Floor(fy), z = Floor(fz);
            int ex = Floor(tx), ey = Floor(ty), ez = Floor(tz);
            int stepX = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
            int stepY = dy > 0 ? 1 : (dy < 0 ? -1 : 0);
            int stepZ = dz > 0 ? 1 : (dz < 0 ? -1 : 0);
            float tMaxX = AxisTMax(fx, dx, x, stepX), tDeltaX = AxisTDelta(dx);
            float tMaxY = AxisTMax(fy, dy, y, stepY), tDeltaY = AxisTDelta(dy);
            float tMaxZ = AxisTMax(fz, dz, z, stepZ), tDeltaZ = AxisTDelta(dz);

            float loss = 0f;
            for (int guard = 0; guard < 256; guard++)
            {
                if (x == ex && y == ey && z == ez) break;
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ) { x += stepX; tMaxX += tDeltaX; }
                else if (tMaxY <= tMaxZ)              { y += stepY; tMaxY += tDeltaY; }
                else                                  { z += stepZ; tMaxZ += tDeltaZ; }
                if (x == ex && y == ey && z == ez) break;
                if (y < 0 || y >= 256) continue;
                loss += CellLoss(x, y, z);
            }
            return loss;
        }

        // ── Room probe (drives reverb) ──
        private const float ProbeMaxDist = 32f;
        private static readonly float[] _probeDirs =
        {
             1, 0, 0,   -1, 0, 0,    0, 1, 0,    0,-1, 0,    0, 0, 1,    0, 0,-1,
             0.577f, 0.577f, 0.577f,  -0.577f, 0.577f, 0.577f,
             0.577f, 0.577f,-0.577f,  -0.577f, 0.577f,-0.577f,
             0.577f,-0.577f, 0.577f,  -0.577f,-0.577f, 0.577f,
             0.577f,-0.577f,-0.577f,  -0.577f,-0.577f,-0.577f,
        };

        internal void ProbeRoom(float ex, float ey, float ez, out float avgDist, out float reflect, out float openFrac)
        {
            int n = _probeDirs.Length / 3, hits = 0;
            float sumDist = 0f, sumReflect = 0f;
            for (int i = 0; i < n; i++)
            {
                if (ProbeRay(ex, ey, ez, _probeDirs[i * 3], _probeDirs[i * 3 + 1], _probeDirs[i * 3 + 2],
                             out float dist, out float rfl))
                { hits++; sumDist += dist; sumReflect += rfl; }
                else sumDist += ProbeMaxDist;
            }
            avgDist  = sumDist / n;
            reflect  = hits > 0 ? sumReflect / hits : 0f;
            openFrac = (float)(n - hits) / n;
        }

        private bool ProbeRay(float fx, float fy, float fz, float dxx, float dyy, float dzz,
                              out float dist, out float reflect)
        {
            dist = ProbeMaxDist; reflect = 0f;
            int x = Floor(fx), y = Floor(fy), z = Floor(fz);
            int stepX = dxx > 0 ? 1 : (dxx < 0 ? -1 : 0);
            int stepY = dyy > 0 ? 1 : (dyy < 0 ? -1 : 0);
            int stepZ = dzz > 0 ? 1 : (dzz < 0 ? -1 : 0);
            float tMaxX = AxisTMax(fx, dxx, x, stepX), tDeltaX = AxisTDelta(dxx);
            float tMaxY = AxisTMax(fy, dyy, y, stepY), tDeltaY = AxisTDelta(dyy);
            float tMaxZ = AxisTMax(fz, dzz, z, stepZ), tDeltaZ = AxisTDelta(dzz);

            for (int guard = 0; guard < 64; guard++)
            {
                float t;
                if (tMaxX <= tMaxY && tMaxX <= tMaxZ) { x += stepX; t = tMaxX; tMaxX += tDeltaX; }
                else if (tMaxY <= tMaxZ)              { y += stepY; t = tMaxY; tMaxY += tDeltaY; }
                else                                  { z += stepZ; t = tMaxZ; tMaxZ += tDeltaZ; }
                if (t > ProbeMaxDist) return false;
                if (y < 0 || y >= 256) continue;
                byte id = _w.Get(x, y, z);
                if (VoxWorld.Air[id]) continue;
                if (VoxWorld.SeeThrough[id]) continue;
                dist = t;
                reflect = MaterialReflect(VoxWorld.Surface[id]);
                return true;
            }
            return false;
        }

        private readonly Dictionary<string, float> _reflectCache = new Dictionary<string, float>();
        private float MaterialReflect(string cat)
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

        // ── Small math helpers (headless equivalents of the Mathf calls) ──
        private static float AxisTMax(float origin, float dir, int cell, int step)
        {
            if (Math.Abs(dir) < 1e-6f) return float.MaxValue;
            float boundary = step > 0 ? cell + 1 : cell;
            return (boundary - origin) / dir;
        }
        private static float AxisTDelta(float dir) => Math.Abs(dir) < 1e-6f ? float.MaxValue : Math.Abs(1f / dir);
        private static int Floor(float v) => (int)Math.Floor(v);
        private static int Round(float v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EmitterSim — drives the emitter workload off the session's POI phase and records the cost.
    //   • On POI ENTER (gs.PoiId changes to a POI): build a voxel building holding N speakers and
    //     time the registry APPEARANCE scan (block-volume sweep for speaker ids).
    //   • While IN the POI: each decision tick (emitter Hz), build the in-range point buffer
    //     (cull to MaxPoints nearest, as BuildPointBuffer does), then time ComputeOcclusion +
    //     ProbeRoom — the per-frame spatialisation hit — moving the listener so occlusion varies.
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class EmitterSim
    {
        private readonly Random _rng;
        private readonly float _tick;
        private readonly int _maxPerPoi;
        private readonly int _runsPerTick;   // acoustics runs per session tick (emitter Hz × tick)

        private readonly VoxWorld _world = new VoxWorld();
        private readonly AcousticsSim _ac;
        private readonly float[] _pts = new float[AcousticsSim.MaxPoints * 3];
        private readonly float[] _extra = new float[AcousticsSim.MaxPoints];
        private readonly float[] _bestD = new float[AcousticsSim.MaxPoints];

        // Current POI's speakers (world positions) + geometry, regenerated on enter.
        private readonly List<float[]> _speakers = new List<float[]>();
        private string _curPoi;
        private float _earX, _earY, _earZ;   // listener, wandering inside the building
        private float _phase;                // wander phase

        // ── Metrics ──
        public long TicksTotal, TicksInPoi, RunsTotal;
        public int  PoiVisits;
        public readonly List<double> RunMs   = new List<double>();   // per acoustics run (occ+room)
        public readonly List<double> OccMs   = new List<double>();
        public readonly List<double> RoomMs  = new List<double>();
        public readonly List<double> ScanMs  = new List<double>();   // per POI-appearance scan
        public long AllocCompute;   // alloc strictly around the per-run compute (the feature's own churn)
        public readonly int[] PointCountHist = new int[AcousticsSim.MaxPoints + 1];
        public long PointCountSum;
        public double MaxRunMs;

        public EmitterSim(int seed, float tick, int maxPerPoi, float emitterHz)
        {
            _rng = new Random(seed ^ 0x5313);
            _tick = tick;
            _maxPerPoi = Math.Max(1, maxPerPoi);
            _runsPerTick = Math.Max(1, (int)Math.Round(emitterHz * tick));
            _ac = new AcousticsSim(_world);
        }

        public int RunsPerTick => _runsPerTick;

        // Called once per session tick with the fresh snapshot.
        public void Tick(GameState gs, float now)
        {
            TicksTotal++;

            if (gs.InPoi && gs.PoiId != null)
            {
                if (gs.PoiId != _curPoi) EnterPoi(gs.PoiId);   // speakers "appear" + get scanned
                TicksInPoi++;

                for (int r = 0; r < _runsPerTick; r++)
                {
                    WanderListener();
                    RunsTotal++;

                    // Measure alloc strictly around the per-run COMPUTE (cull + occlusion + reverb) —
                    // the feature's own steady churn. The timing List.Add bookkeeping below is harness
                    // instrumentation, not the feature, so it's kept OUT of this window.
                    long ca0 = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                    int count = BuildPointBuffer();
                    double occ = 0, room = 0;
                    if (count > 0)
                    {
                        long ts = Stopwatch.GetTimestamp();
                        _ac.ComputeOcclusion(_earX, _earY, _earZ, _pts, count, _extra);
                        occ = Ms(ts);
                        if (CMMConfig.WorldEmitterReverbEnabled)
                        {
                            ts = Stopwatch.GetTimestamp();
                            _ac.ProbeRoom(_earX, _earY, _earZ, out _, out _, out _);
                            room = Ms(ts);
                        }
                    }
                    AllocCompute += AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - ca0;

                    PointCountHist[count]++;
                    PointCountSum += count;
                    if (count == 0) continue;
                    double total = occ + room;
                    OccMs.Add(occ); RoomMs.Add(room); RunMs.Add(total);
                    if (total > MaxRunMs) MaxRunMs = total;
                }
            }
            else _curPoi = null;   // left the POI → speakers unload
        }

        // ── POI appearance: build a voxel building with N speakers + time the detection scan ──
        private void EnterPoi(string poiId)
        {
            _curPoi = poiId;
            PoiVisits++;
            int count = 1 + Hash(poiId) % _maxPerPoi;   // 1..maxPerPoi speakers this POI
            BuildBuilding(poiId, count);

            // Registry appearance scan: sweep the building's block volume for speaker ids
            // (EmitterRegistry.ScanPrefabForTargets). Bounds match the built structure.
            long ts = Stopwatch.GetTimestamp();
            int found = ScanForSpeakers(Bx, By, Bz, Bw, Bh, Bw);
            ScanMs.Add(Ms(ts));
            if (found < 0) throw new Exception("scan sanity");   // keep JIT from eliding
        }

        // Building footprint (fixed world box inside the region, with flood margin on all sides).
        private const int Bx = 52, By = 40, Bz = 52, Bw = 24, Bh = 7;

        private void BuildBuilding(string poiId, int speakerCount)
        {
            var rng = new Random(Hash(poiId));
            _world.Clear();
            _speakers.Clear();

            // Perimeter walls (material varies per POI: concrete / wood / metal), floor + ceiling,
            // one interior partition with a doorway, a few glass windows. Enough geometry that some
            // speakers sit across a wall from the ear (occlusion > 0) and the room probe hits real
            // surfaces.
            byte wallMat = (byte)(1 + rng.Next(3));   // 1 concrete, 2 wood, 3 metal
            int x0 = Bx, x1 = Bx + Bw - 1, z0 = Bz, z1 = Bz + Bw - 1, y0 = By, y1 = By + Bh - 1;
            for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                _world.Set(x, y0, z, 5);   // floor
                _world.Set(x, y1, z, 5);   // ceiling
            }
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                _world.Set(x, y, z0, wallMat);
                _world.Set(x, y, z1, wallMat);
            }
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                _world.Set(x0, y, z, wallMat);
                _world.Set(x1, y, z, wallMat);
            }
            // Interior partition at mid-x with a doorway gap, plus glass windows in a side wall.
            int mx = Bx + Bw / 2;
            for (int y = y0; y <= y1; y++)
            for (int z = z0 + 1; z < z1; z++)
                if (z < Bz + Bw / 2 || z > Bz + Bw / 2 + 1)   // leave a 2-wide doorway
                    _world.Set(mx, y, z, 1);
            for (int y = y0 + 2; y <= y0 + 3; y++)
            for (int z = z0 + 3; z <= z1 - 3; z += 2)
                _world.Set(x0, y, z, 4);   // glass windows

            // Speakers: on shelves along interior walls, spread across BOTH rooms so several end up
            // occluded from a given ear position. Each is a shelf (partial) + a speaker deco block.
            int placed = 0, guard = 0;
            while (placed < speakerCount && guard++ < 200)
            {
                int sx = x0 + 1 + rng.Next(Bw - 2);
                int sz = z0 + 1 + rng.Next(Bw - 2);
                int sy = y0 + 1 + rng.Next(Math.Max(1, Bh - 3));
                if (sx == mx) continue;                       // not inside the partition
                _world.Set(sx, sy, sz, 6);                    // shelf
                _world.Set(sx, sy + 1, sz, 7);                // speaker
                _speakers.Add(new float[] { sx + 0.5f, sy + 1.5f, sz + 0.5f });
                placed++;
            }

            // Start the listener in the left room, mid-height.
            _earX = Bx + Bw * 0.25f; _earY = By + 1.6f; _earZ = Bz + Bw * 0.5f;
            _phase = 0f;
        }

        // Sweep a block volume for speaker ids (registry appearance-scan cost model).
        private int ScanForSpeakers(int ox, int oy, int oz, int sx, int sy, int sz)
        {
            int hits = 0;
            for (int x = ox; x < ox + sx; x++)
            for (int y = oy; y < oy + sy; y++)
            for (int z = oz; z < oz + sz; z++)
                if (_world.Get(x, y, z) == 7) hits++;
            return hits;
        }

        // Wander the listener inside the building so ear→speaker occlusion + distance vary tick to
        // tick (crosses the partition doorway between the two rooms).
        private void WanderListener()
        {
            _phase += 0.08f;
            float cx = Bx + Bw * 0.5f, cz = Bz + Bw * 0.5f;
            _earX = cx + (float)Math.Sin(_phase) * (Bw * 0.30f);
            _earZ = cz + (float)Math.Sin(_phase * 0.7f) * (Bw * 0.30f);
            _earY = By + 1.6f;
        }

        // BuildPointBuffer: nearest MaxPoints speakers within WorldEmitterRange (matches the real
        // culling before ComputeOcclusion). Returns the point count and fills _pts.
        private int BuildPointBuffer()
        {
            float range = CMMConfig.WorldEmitterRange;
            float r2 = range * range;
            int count = 0;
            // simple insertion by distance, capped at MaxPoints (preallocated scratch _bestD)
            float[] bestD = _bestD;
            for (int i = 0; i < _speakers.Count; i++)
            {
                var s = _speakers[i];
                float dx = s[0] - _earX, dy = s[1] - _earY, dz = s[2] - _earZ;
                float d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > r2) continue;
                if (count < AcousticsSim.MaxPoints)
                {
                    int j = count++;
                    while (j > 0 && bestD[j - 1] > d2)
                    { bestD[j] = bestD[j - 1]; Copy3(j, j - 1); j--; }
                    bestD[j] = d2; Set3(j, s);
                }
                else if (d2 < bestD[count - 1])
                {
                    int j = count - 1;
                    while (j > 0 && bestD[j - 1] > d2)
                    { bestD[j] = bestD[j - 1]; Copy3(j, j - 1); j--; }
                    bestD[j] = d2; Set3(j, s);
                }
            }
            return count;
        }

        private void Set3(int slot, float[] s) { int o = slot * 3; _pts[o] = s[0]; _pts[o + 1] = s[1]; _pts[o + 2] = s[2]; }
        private void Copy3(int to, int from) { int a = to * 3, b = from * 3; _pts[a] = _pts[b]; _pts[a + 1] = _pts[b + 1]; _pts[a + 2] = _pts[b + 2]; }

        // ── One-off scaling probe: how the per-run cost grows with speakers-in-range, on a fixed
        // representative POI + ear, for both the default direct-ray path and the heavy cost-field. ──
        public struct Scale { public int Count; public double Ms; public double MsFlood; }
        public List<Scale> ScaleProbe(int iters)
        {
            var res = new List<Scale>();
            BuildBuilding("scaleProbe#fixed", AcousticsSim.MaxPoints);
            // Fill _pts with 8 fixed speakers (some occluded).
            for (int i = 0; i < _speakers.Count && i < AcousticsSim.MaxPoints; i++) Set3(i, _speakers[i]);
            float ox = Bx + Bw * 0.28f, oy = By + 1.6f, oz = Bz + Bw * 0.5f;
            int savedFlood = CMMConfig.WorldEmitterFloodRadius;
            foreach (int c in new[] { 1, 2, 4, 8 })
            {
                CMMConfig.WorldEmitterFloodRadius = 1;
                double t1 = TimeRuns(ox, oy, oz, c, iters);
                CMMConfig.WorldEmitterFloodRadius = 16;   // cost-field expanded (worst case)
                double t2 = TimeRuns(ox, oy, oz, c, iters);
                res.Add(new Scale { Count = c, Ms = t1, MsFlood = t2 });
            }
            CMMConfig.WorldEmitterFloodRadius = savedFlood;
            return res;
        }

        private double TimeRuns(float ox, float oy, float oz, int count, int iters)
        {
            _ac.ComputeOcclusion(ox, oy, oz, _pts, count, _extra);   // warm
            long ts = Stopwatch.GetTimestamp();
            for (int i = 0; i < iters; i++)
            {
                _ac.ComputeOcclusion(ox, oy, oz, _pts, count, _extra);
                _ac.ProbeRoom(ox, oy, oz, out _, out _, out _);
            }
            return Ms(ts) / iters;
        }

        private static int Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
                return (int)(h & 0x7fffffff);
            }
        }
        private static double Ms(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
    }
}
