// Gated to 3.0 (GV_V3): rides the same world APIs as EmitterAcoustics / EmitterRegistry.
#if GV_V3
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // ACOUSTICS SWEEP (`cmm acsweep`) — game-truth exporter for the offline judge
    // ─────────────────────────────────────────────────────────────────────────
    // Dumps everything the acoustics model KNOWS about one POI to a JSON file, so the headless
    // judge (tools/CmmAcousticsJudge) can replay the model over REAL 3D geometry instead of
    // hand-drawn 2D grids:
    //   • the per-voxel acoustic classification — the REAL CellLoss the flood would pay, straight
    //     from the live engine (IsSeeThrough / material / density / shape), plus air/water/see-
    //     through/terrain flags and the block name per cell;
    //   • the emitters — real speaker blocks found in the volume, topped up with VIRTUAL emitter
    //     placements (deterministic, structure-adjacent air cells) when the POI has none;
    //   • parity floods — for a handful of ears, the SHIPPED ComputeOcclusion run synchronously and
    //     its raw settled cost/wall field exported cell-for-cell, so the offline port can be
    //     validated against the exact code that plays in-game (kills port drift), along with the
    //     published reverb inputs (seal/depth/solid/room probe).
    //
    // Designed to run HEADLESS on the dedicated server (no player needed): a temporary chunk
    // observer force-loads the POI's chunks, the sweep walks the volume budget-sliced off the
    // ordinary frame loop, and the dump lands in <mod>/Cache/acsweep/. Works identically on a
    // client (`cmm acsweep here` uses the local player's POI).
    internal static class EmitterSweep
    {
        // ── Tunables ─────────────────────────────────────────────────────────
        private const int   MarginXZ   = 6;     // blocks of context beyond the POI box (outdoor cells)
        private const int   MarginUp   = 8;     // headroom above (roof edge / sky openness)
        private const int   MarginDown = 2;     // just below the floor slab
        private const long  MaxCells   = 2_000_000; // refuse pathological volumes (a vanilla skyscraper is ~400k)
        private const float GridBudgetMs = 8f;  // per-frame slice for the voxel walk
        private const int   VirtualTarget = 4;  // ensure at least this many emitters (real + virtual)
        private const int   ListenerEars  = 4;  // PRNG listener positions (parity floods run at these + each emitter)
        private const int   MinPickSpacing = 6; // blocks between PRNG picks
        private const float LoadTimeoutSec = 90f;

        // Cell flag bits (mirrors what the offline judge needs to segment the space).
        private const byte FlagAir = 1, FlagWater = 2, FlagSeeThrough = 4, FlagTerrain = 8, FlagSolidCube = 16, FlagEmitter = 32;

        private enum Phase { Idle, WaitChunks, Grid, Floods, FieldBuild, Write }
        private static Phase _phase = Phase.Idle;
        private const int FieldExportCap = 2;    // raw field dumps are ~6 MB each; 2 validate the build
        private static int _fieldCursor;
        private static bool _fieldBuildStarted;
        private static readonly StringBuilder _fieldJson = new StringBuilder();
        private static readonly StringBuilder _fieldSampleJson = new StringBuilder();

        // Target POI + dump volume.
        private static string _poiName;
        private static int    _poiId;
        private static Vector3i _poiPos, _poiSize;              // original POI box
        private static int _x0, _y0, _z0, _sx, _sy, _sz;        // dump box (POI + margins, Y-clamped)

        // Grid data (allocated per sweep, released after write).
        private static byte[]   _loss;      // round(CellLoss × LossScale), clamped to byte
        private static byte[]   _flags;
        private static ushort[] _blockIdx;
        private const int LossScale = 32;   // 1.0 (full densest wall) → 32; AirtightLoss 4.0 → 128
        private static readonly List<string> _names = new List<string>();
        private static readonly Dictionary<string, int> _nameToIdx = new Dictionary<string, int>();

        private struct SweepEmitter { public Vector3i Pos; public string Name; public bool Virtual; }
        private static readonly List<SweepEmitter> _emitters = new List<SweepEmitter>();

        // Sweep cursor + phase state.
        private static int _gx, _gz;                 // grid column cursor (full Y per column)
        private static float _deadline;              // chunk-load timeout (realtime)
        private static object _observer;             // ChunkManager.ChunkObserver (typed loosely for teardown)
        private static readonly List<Vector3i> _ears = new List<Vector3i>();
        private static int _earCursor;
        private static readonly StringBuilder _floodJson = new StringBuilder();
        private static int _floodCellCount;          // per-ear cell counter (for the byte buffer)
        private static byte[] _floodBuf;              // 7 bytes/cell: dx+32, dy+32, dz+32, cost u16 LE, wall u16 LE

        internal static bool Active => _phase != Phase.Idle;

        // ── Console entry (`cmm acsweep …`) — returns the immediate reply; work continues in Tick ──
        internal static string Command(List<string> args)
        {
            string sub = args.Count > 1 ? args[1] : "";
            if (sub.Length == 0 || sub == "help")
                return "usage: cmm acsweep <poi-name|here|list [filter]|status|cancel>";
            if (sub == "status")
                return _phase == Phase.Idle ? "no sweep running" : StatusLine();
            if (sub == "cancel")
            {
                if (_phase == Phase.Idle) return "no sweep running";
                Abort("cancelled");
                return "sweep cancelled";
            }

            World world = GameManager.Instance?.World;
            DynamicPrefabDecorator deco = GameManager.Instance?.GetDynamicPrefabDecorator();
            if (world == null || deco == null) return "no world loaded";
            if (_phase != Phase.Idle) return "a sweep is already running (`cmm acsweep status` / `cancel`)";

            var prefabs = new List<PrefabInstance>();
            deco.GetWorldPrefabs(prefabs);

            if (sub == "list")
            {
                string filter = args.Count > 2 ? args[2] : null;
                var sb = new StringBuilder();
                int shown = 0;
                foreach (PrefabInstance pi in prefabs)
                {
                    if (filter != null && pi.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (shown++ >= 40) { sb.Append("… (more; narrow the filter)\n"); break; }
                    Vector3i b = pi.boundingBoxPosition, s = pi.boundingBoxSize;
                    sb.Append($"{pi.name}  @({b.x},{b.y},{b.z})  size {s.x}x{s.y}x{s.z}\n");
                }
                return shown == 0 ? "no placed POI matches" : sb.ToString().TrimEnd();
            }

            // Resolve the target POI: `here` = the box containing (else nearest to) the local player;
            // anything else = exact placed-POI name, falling back to first substring match.
            PrefabInstance target = null;
            if (sub == "here")
            {
                EntityPlayerLocal player = world.GetPrimaryPlayer();
                if (player == null) return "no local player — use `cmm acsweep <poi-name>` (see `cmm acsweep list`)";
                Vector3 pp = player.position;
                float bestDist = 64f;
                foreach (PrefabInstance pi in prefabs)
                {
                    float d = DistanceToBox(pp, pi.boundingBoxPosition, pi.boundingBoxSize);
                    if (d < bestDist) { bestDist = d; target = pi; }
                }
                if (target == null) return "no POI within 64 blocks";
            }
            else
            {
                foreach (PrefabInstance pi in prefabs)
                    if (string.Equals(pi.name, sub, StringComparison.OrdinalIgnoreCase)) { target = pi; break; }
                if (target == null)
                    foreach (PrefabInstance pi in prefabs)
                        if (pi.name.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0) { target = pi; break; }
                if (target == null) return $"no placed POI matches '{sub}' (try `cmm acsweep list {sub}`)";
            }

            return Begin(target) ?? $"sweep of '{_poiName}' started — progress in the log; `cmm acsweep status`";
        }

        // Set up the volume, request chunks, arm the state machine. Returns an error string or null.
        private static string Begin(PrefabInstance pi)
        {
            Vector3i b = pi.boundingBoxPosition, s = pi.boundingBoxSize;
            _x0 = b.x - MarginXZ; _z0 = b.z - MarginXZ;
            _y0 = Mathf.Max(0, b.y - MarginDown);
            int y1 = Mathf.Min(255, b.y + s.y + MarginUp);
            _sx = s.x + MarginXZ * 2; _sz = s.z + MarginXZ * 2; _sy = y1 - _y0 + 1;
            long cells = (long)_sx * _sy * _sz;
            if (cells <= 0 || cells > MaxCells)
                return $"volume {cells} cells exceeds the {MaxCells} cap — POI too large";

            _poiName = pi.name; _poiId = pi.id; _poiPos = b; _poiSize = s;
            _loss = new byte[cells]; _flags = new byte[cells]; _blockIdx = new ushort[cells];
            _names.Clear(); _nameToIdx.Clear();
            NameIndex("air");   // index 0 = air by construction
            _emitters.Clear(); _ears.Clear(); _floodJson.Length = 0; _earCursor = 0;
            _fieldJson.Length = 0; _fieldSampleJson.Length = 0; _fieldCursor = 0; _fieldBuildStarted = false;
            _gx = _x0; _gz = _z0;

            // Force-load the volume with a temporary observer (radius in chunk rings, +1 slack ring).
            Vector3 center = new Vector3(b.x + s.x * 0.5f, b.y + s.y * 0.5f, b.z + s.z * 0.5f);
            int viewDim = Mathf.Clamp((Mathf.Max(_sx, _sz) / 2 + 16) / 16 + 1, 3, 16);
            _observer = GameManager.Instance.AddChunkObserver(center, false, viewDim, -1);

            _deadline = Time.realtimeSinceStartup + LoadTimeoutSec;
            _phase = Phase.WaitChunks;
            CMMConfig.D($"[CMM] AcSweep '{_poiName}': {_sx}x{_sy}x{_sz} = {cells} cells, observer viewDim {viewDim}, waiting for chunks…");
            return null;
        }

        // ── Frame driver (called from the mod's Update, same cadence as EmitterAudioLayer.Tick) ──
        internal static void Tick()
        {
            if (_phase == Phase.Idle) return;
            World world = GameManager.Instance?.World;
            if (world == null) { Abort("world unloaded"); return; }

            switch (_phase)
            {
                case Phase.WaitChunks:
                    if (ChunksReady(world)) { _phase = Phase.Grid; CMMConfig.D("[CMM] AcSweep: chunks loaded, walking the grid…"); }
                    else if (Time.realtimeSinceStartup > _deadline) Abort("chunk load timed out");
                    break;

                case Phase.Grid:
                    if (GridSlice(world))
                    {
                        PickEmittersAndEars();
                        _phase = Phase.Floods;
                        CMMConfig.D($"[CMM] AcSweep: grid done, {_emitters.Count} emitter(s), {_ears.Count} parity flood(s)…");
                    }
                    break;

                case Phase.Floods:
                    // One synchronous flood per frame; never interleave with the live layer's
                    // amortized flood (shared static buffers — see EmitterAcoustics).
                    if (EmitterAcoustics.FloodInProgress) break;
                    if (_earCursor >= _ears.Count || _emitters.Count == 0)
                    {
                        _phase = (CMMConfig.WorldEmitterFieldCache && _emitters.Count > 0)
                                 ? Phase.FieldBuild : Phase.Write;
                        break;
                    }
                    RunParityFlood(world, _ears[_earCursor++]);
                    break;

                case Phase.FieldBuild:
                    // Phase-2 parity data: build each emitter's REAL cached field SLICED (8 ms per
                    // frame — the v1 synchronous build froze the client seconds per field during
                    // `cmm acsweep here`), dump the first FieldExportCap raw, then sample every
                    // ear through EmitterFieldCache exactly as the live layer would.
                    if (_fieldCursor < Mathf.Min(_emitters.Count, EmitterAcoustics.MaxPoints))
                    {
                        SweepEmitter e = _emitters[_fieldCursor];
                        if (!_fieldBuildStarted)
                        {
                            EmitterFieldCache.DebugBeginBuild(e.Pos);
                            _fieldBuildStarted = true;
                        }
                        EmitterFieldCache.BuildStep(world, 8f);
                        if (EmitterFieldCache.BuildIdle)
                        {
                            if (_fieldCursor < FieldExportCap) ExportField(e.Pos);
                            _fieldCursor++;
                            _fieldBuildStarted = false;
                        }
                    }
                    else
                    {
                        ExportFieldSamples(world);
                        _phase = Phase.Write;
                    }
                    break;

                case Phase.Write:
                    WriteDump();
                    break;
            }
        }

        private static string StatusLine()
        {
            switch (_phase)
            {
                case Phase.WaitChunks: return $"'{_poiName}': waiting for chunks ({(int)(_deadline - Time.realtimeSinceStartup)}s to timeout)";
                case Phase.Grid:       return $"'{_poiName}': grid {(100f * (_gx - _x0) / Mathf.Max(1, _sx)):0}%";
                case Phase.Floods:     return $"'{_poiName}': parity floods {_earCursor}/{_ears.Count}";
                default:               return $"'{_poiName}': writing dump";
            }
        }

        private static void Abort(string why)
        {
            Log.Out($"[CMM] AcSweep '{_poiName}' aborted: {why}");
            Cleanup();
        }

        private static void Cleanup()
        {
            if (_observer != null)
            {
                try { GameManager.Instance?.RemoveChunkObserver((ChunkManager.ChunkObserver)_observer); } catch { }
                _observer = null;
            }
            _loss = null; _flags = null; _blockIdx = null; _floodBuf = null;
            _names.Clear(); _nameToIdx.Clear(); _emitters.Clear(); _ears.Clear();
            _floodJson.Length = 0; _fieldJson.Length = 0; _fieldSampleJson.Length = 0;
            _phase = Phase.Idle;
        }

        // Every chunk column the dump box touches must be resident before voxel reads mean anything
        // (GetBlock returns air on unloaded chunks — a silent all-air dump would judge nothing).
        private static bool ChunksReady(World world)
        {
            int cx0 = _x0 >> 4, cx1 = (_x0 + _sx - 1) >> 4;
            int cz0 = _z0 >> 4, cz1 = (_z0 + _sz - 1) >> 4;
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cz = cz0; cz <= cz1; cz++)
                    if (EmitterCompat.GetChunkSync(world, cx, cz) == null) return false;
            return true;
        }

        // Walk the volume column-by-column under a per-frame time budget. True = complete.
        private static bool GridSlice(World world)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int sinceCheck = 0;
            while (_gx < _x0 + _sx)
            {
                while (_gz < _z0 + _sz)
                {
                    for (int y = _y0; y < _y0 + _sy; y++)
                        ClassifyCell(world, _gx, y, _gz);
                    _gz++;
                    if ((sinceCheck += _sy) >= 2048)
                    {
                        sinceCheck = 0;
                        if ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * toMs >= GridBudgetMs) return false;
                    }
                }
                _gz = _z0;
                _gx++;
            }
            return true;
        }

        private static void ClassifyCell(World world, int x, int y, int z)
        {
            long idx = CellIndex(x, y, z);
            BlockValue bv = world.GetBlock(x, y, z);

            float loss = EmitterAcoustics.DebugCellLoss(bv, world, x, y, z);
            int q = Mathf.RoundToInt(loss * LossScale);
            _loss[idx] = (byte)Mathf.Clamp(q, 0, 255);

            byte flags = 0;
            bool water = world.IsWater(x, y, z);
            if (bv.isair)
            {
                if (water) flags |= FlagWater; else flags |= FlagAir;
                _blockIdx[idx] = 0;
            }
            else
            {
                Block b = bv.Block;
                if (b == null) { _blockIdx[idx] = 0; _flags[idx] = FlagAir; return; }
                if (water || (b.blockMaterial != null && b.blockMaterial.IsLiquid)) flags |= FlagWater;
                if (b.IsSeeThrough(world, new Vector3i(x, y, z), bv)) flags |= FlagSeeThrough;
                if (b.shape is BlockShapeTerrain) flags |= FlagTerrain;
                if (b.shape != null && b.shape.IsSolidCube) flags |= FlagSolidCube;
                _blockIdx[idx] = (ushort)NameIndex(b.GetBlockName() ?? "?");

                // Real speaker block (parent cells only — multiblock children carry no identity).
                if (!bv.ischild && EmitterRegistry.TryGetTargetName(bv.type, out string tname))
                {
                    flags |= FlagEmitter;
                    _emitters.Add(new SweepEmitter { Pos = new Vector3i(x, y, z), Name = tname, Virtual = false });
                }
            }
            _flags[idx] = flags;
        }

        // ── Emitter / ear placement (deterministic — same POI ⇒ same dump) ───────────────────────
        // Candidates are STRUCTURE-ADJACENT AIR inside the original POI box with air overhead —
        // where a speaker could plausibly sit / a player plausibly stand (mirrors the 2D judge's
        // pickEmitters). Virtual emitters top the real ones up to VirtualTarget; ears are a separate
        // spread of the same kind of cell, plus every emitter gets a flood of its own (its field
        // doubles as the whole-POI loudness heatmap for the visualizer).
        private static void PickEmittersAndEars()
        {
            var candidates = new List<Vector3i>();
            int px1 = _poiPos.x + _poiSize.x, py1 = Mathf.Min(_y0 + _sy - 1, _poiPos.y + _poiSize.y), pz1 = _poiPos.z + _poiSize.z;
            for (int y = Mathf.Max(_y0 + 1, _poiPos.y); y < py1; y++)
                for (int z = _poiPos.z; z < pz1; z++)
                    for (int x = _poiPos.x; x < px1; x++)
                    {
                        if ((_flags[CellIndex(x, y, z)] & FlagAir) == 0) continue;
                        if ((_flags[CellIndex(x, y + 1, z)] & FlagAir) == 0) continue;   // headroom
                        // beside real structure (≥ ~0.2 of a full wall), not floating in a void
                        if (LossAt(x + 1, y, z) < 7 && LossAt(x - 1, y, z) < 7 &&
                            LossAt(x, y, z + 1) < 7 && LossAt(x, y, z - 1) < 7 &&
                            LossAt(x, y - 1, z) < 7) continue;
                        candidates.Add(new Vector3i(x, y, z));
                    }

            uint seed = (uint)_poiName.GetHashCode() | 1u;
            PickSpread(candidates, ref seed, VirtualTarget - _emitters.Count, p =>
                _emitters.Add(new SweepEmitter { Pos = p, Name = "virtual", Virtual = true }));

            foreach (SweepEmitter e in _emitters) _ears.Add(e.Pos);
            PickSpread(candidates, ref seed, ListenerEars, p => { if (!_ears.Contains(p)) _ears.Add(p); });
        }

        // Pick up to `n` candidates ≥ MinPickSpacing apart from each other AND everything picked so far.
        private static readonly List<Vector3i> _picked = new List<Vector3i>();
        private static void PickSpread(List<Vector3i> candidates, ref uint seed, int n, Action<Vector3i> accept)
        {
            _picked.Clear();
            foreach (SweepEmitter e in _emitters) _picked.Add(e.Pos);
            foreach (Vector3i e in _ears) _picked.Add(e);
            for (int k = 0; k < n && candidates.Count > 0; k++)
            {
                Vector3i pick = default; bool found = false;
                for (int attempt = 0; attempt < 64 && !found; attempt++)
                {
                    Vector3i c = candidates[(int)(XorShift(ref seed) % (uint)candidates.Count)];
                    found = true;
                    foreach (Vector3i p in _picked)
                        if (Mathf.Abs(p.x - c.x) + Mathf.Abs(p.y - c.y) + Mathf.Abs(p.z - c.z) < MinPickSpacing)
                        { found = false; break; }
                    if (found) pick = c;
                }
                if (!found) break;
                _picked.Add(pick);
                accept(pick);
            }
        }

        private static uint XorShift(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }

        // ── Parity flood: run the SHIPPED ComputeOcclusion at one ear, dump everything it made ─────
        private static void RunParityFlood(World world, Vector3i earCell)
        {
            Vector3 ear = new Vector3(earCell.x + 0.5f, earCell.y + 0.5f, earCell.z + 0.5f);
            int count = Mathf.Min(_emitters.Count, EmitterAcoustics.MaxPoints);
            var pts = new float[count * 3];
            var extra = new float[count];
            for (int i = 0; i < count; i++)
            {
                pts[i * 3 + 0] = _emitters[i].Pos.x + 0.5f;
                pts[i * 3 + 1] = _emitters[i].Pos.y + 0.5f;
                pts[i * 3 + 2] = _emitters[i].Pos.z + 0.5f;
            }

            // The synchronous path: full flood settle + FinishSpeakers (occlusion, redirect, reverb
            // seal/depth published to the statics). The live layer isn't mid-flood (Tick checked).
            EmitterAcoustics.ComputeOcclusion(ear, pts, count, extra, world);

            EmitterAcoustics.ProbeRoom(ear, world, out float avgDist, out float roomSize,
                                       out float reflect, out float openFrac, out bool roofed);
            EmitterAcoustics.ComputeReverb(roomSize, reflect, openFrac, roofed,
                                           out float wet, out float room, out float damp);

            var sb = _floodJson;
            if (sb.Length > 0) sb.Append(",\n");
            sb.Append("  {\"ear\":[").Append(earCell.x).Append(',').Append(earCell.y).Append(',').Append(earCell.z)
              .Append("],\"seal\":").F(EmitterAcoustics.ReverbSeal)
              .Append(",\"depth\":").F(EmitterAcoustics.ReverbDepth)
              .Append(",\"solidMin\":").F(EmitterAcoustics.ReverbSolid)
              .Append(",\"airRoom\":").F(EmitterAcoustics.AirRoomSize)
              .Append(",\"airCells\":").Append(EmitterAcoustics.AirCellCount)
              .Append(",\"settled\":").Append(EmitterAcoustics.FloodSettled)
              .Append(",\"probe\":{\"avgDist\":").F(avgDist).Append(",\"roomSize\":").F(roomSize)
              .Append(",\"reflect\":").F(reflect).Append(",\"openFrac\":").F(openFrac)
              .Append(",\"roofed\":").Append(roofed ? "true" : "false")
              .Append(",\"wet\":").F(wet).Append(",\"room\":").F(room).Append(",\"damp\":").F(damp)
              .Append("},\"speakers\":[");
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(',');
                // extra = shaped occlusion virtual distance; redir = the (possibly rotated) virtual
                // source position ComputeOcclusion left in pts (directional arrival).
                sb.Append("{\"extra\":").F(extra[i])
                  .Append(",\"redir\":[").F(pts[i * 3]).Append(',').F(pts[i * 3 + 1]).Append(',').F(pts[i * 3 + 2]).Append("]}");
            }
            sb.Append("],\"cells\":\"");

            // Raw settled field, 7 bytes/cell: dx+32, dy+32, dz+32 (offsets from the ear; the flood
            // box is ±32), cost u16 LE, wall u16 LE — both in CostScale units exactly as measured.
            // PathRouting off ⇒ ComputeOcclusion ran direct-line only and the static field still
            // holds whatever flooded LAST (possibly the live layer at another ear) — export nothing.
            if (_floodBuf == null) _floodBuf = new byte[65 * 65 * 65 * 7];
            _floodCellCount = 0;
            if (CMMConfig.WorldEmitterPathRouting)
            {
                EmitterAcoustics.DebugExportFlood((x, y, z, cost, wall) =>
                {
                    int o = _floodCellCount * 7;
                    if (o + 7 > _floodBuf.Length) return;
                    _floodBuf[o]     = (byte)(x - earCell.x + 32);
                    _floodBuf[o + 1] = (byte)(y - earCell.y + 32);
                    _floodBuf[o + 2] = (byte)(z - earCell.z + 32);
                    _floodBuf[o + 3] = (byte)(cost & 0xFF); _floodBuf[o + 4] = (byte)(cost >> 8);
                    _floodBuf[o + 5] = (byte)(wall & 0xFF); _floodBuf[o + 6] = (byte)(wall >> 8);
                    _floodCellCount++;
                });
            }
            sb.Append(Convert.ToBase64String(_floodBuf, 0, _floodCellCount * 7));
            sb.Append("\"}");
        }

        // ── Phase-2 field export (dump v2) ────────────────────────────────────
        // Raw field cells, 7 bytes each: dx+128, dy+128, dz+128 (offsets from the SPEAKER, field
        // half-extent ≤ 48 so ±48 fits), cost u16 LE, wall u16 LE — scaled units as stored.
        private static void ExportField(Vector3i pos)
        {
            var buf = new List<byte>(1 << 20);
            bool ok = EmitterFieldCache.DebugExportField(pos, out int effR, (x, y, z, cost, wall) =>
            {
                buf.Add((byte)(x - pos.x + 128)); buf.Add((byte)(y - pos.y + 128)); buf.Add((byte)(z - pos.z + 128));
                buf.Add((byte)(cost & 0xFF)); buf.Add((byte)(cost >> 8));
                buf.Add((byte)(wall & 0xFF)); buf.Add((byte)(wall >> 8));
            });
            if (!ok) return;
            if (_fieldJson.Length > 0) _fieldJson.Append(",\n");
            _fieldJson.Append("  {\"pos\":[").Append(pos.x).Append(',').Append(pos.y).Append(',').Append(pos.z)
                      .Append("],\"effR\":").Append(effR).Append(",\"cells\":\"")
                      .Append(Convert.ToBase64String(buf.ToArray())).Append("\"}");
        }

        // Sample every ear against ALL emitters through the live Phase-2 path (extras + the
        // directional redirect + station reverb inputs) — what the judge validates its V5
        // evaluator against.
        private static void ExportFieldSamples(World world)
        {
            int count = Mathf.Min(_emitters.Count, EmitterAcoustics.MaxPoints);
            if (count == 0) return;
            var pts = new float[count * 3];
            var extra = new float[count];
            foreach (Vector3i earCell in _ears)
            {
                for (int i = 0; i < count; i++)
                {
                    pts[i * 3] = _emitters[i].Pos.x + 0.5f;
                    pts[i * 3 + 1] = _emitters[i].Pos.y + 0.5f;
                    pts[i * 3 + 2] = _emitters[i].Pos.z + 0.5f;
                }
                Vector3 ear = new Vector3(earCell.x + 0.5f, earCell.y + 0.5f, earCell.z + 0.5f);
                if (!EmitterFieldCache.SampleStation(world, ear, pts, count, extra,
                                                     out float seal, out float solid, out float depth))
                    continue;
                if (_fieldSampleJson.Length > 0) _fieldSampleJson.Append(",\n");
                _fieldSampleJson.Append("  {\"ear\":[").Append(earCell.x).Append(',').Append(earCell.y).Append(',').Append(earCell.z)
                                .Append("],\"seal\":").F(seal).Append(",\"solid\":").F(solid).Append(",\"depth\":").F(depth)
                                .Append(",\"speakers\":[");
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) _fieldSampleJson.Append(',');
                    _fieldSampleJson.Append("{\"extra\":").F(extra[i])
                        .Append(",\"redir\":[").F(pts[i * 3]).Append(',').F(pts[i * 3 + 1]).Append(',').F(pts[i * 3 + 2]).Append("]}");
                }
                _fieldSampleJson.Append("]}");
            }
        }

        // ── Dump writer ───────────────────────────────────────────────────────
        private static void WriteDump()
        {
            string path = null;
            try
            {
                string dir = Path.Combine(ContextMusicManager.ModPath, "Cache", "acsweep");
                Directory.CreateDirectory(dir);
                string safe = _poiName;
                foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                path = Path.Combine(dir, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.json");

                var sb = new StringBuilder(1 << 20);
                sb.Append("{\n\"version\":2,\n");
                sb.Append("\"poi\":\"").Append(Escape(_poiName)).Append("\",\"poiId\":").Append(_poiId).Append(",\n");
                sb.Append($"\"poiBounds\":{{\"x\":{_poiPos.x},\"y\":{_poiPos.y},\"z\":{_poiPos.z},\"sx\":{_poiSize.x},\"sy\":{_poiSize.y},\"sz\":{_poiSize.z}}},\n");
                sb.Append($"\"bounds\":{{\"x\":{_x0},\"y\":{_y0},\"z\":{_z0},\"sx\":{_sx},\"sy\":{_sy},\"sz\":{_sz}}},\n");
                sb.Append("\"order\":\"index = x + sx*(z + sz*y); coords relative to bounds origin\",\n");
                sb.Append("\"lossScale\":").Append(LossScale).Append(",\n");
                sb.Append("\"flagBits\":{\"air\":1,\"water\":2,\"seeThrough\":4,\"terrain\":8,\"solidCube\":16,\"emitter\":32},\n");
                sb.Append("\"config\":{")
                  .Append("\"wallDistance\":").F(CMMConfig.WorldEmitterOcclusionWallDistance)
                  .Append(",\"range\":").F(CMMConfig.WorldEmitterRange)
                  .Append(",\"minDistance\":").F(CMMConfig.WorldEmitterMinDistance)
                  .Append(",\"falloff\":").F(CMMConfig.WorldEmitterFalloff)
                  .Append(",\"maxDistance\":").F(CMMConfig.WorldEmitterOcclusionMaxDistance)
                  .Append(",\"hardStop\":").F(CMMConfig.WorldEmitterOcclusionHardStop)
                  .Append(",\"floodRadius\":").Append(CMMConfig.WorldEmitterFloodRadius)
                  .Append(",\"pathRouting\":").Append(CMMConfig.WorldEmitterPathRouting ? "true" : "false")
                  .Append(",\"directional\":").Append(CMMConfig.WorldEmitterOcclusionDirectional ? "true" : "false")
                  .Append(",\"reverbRange\":").F(CMMConfig.WorldEmitterReverbRange)
                  .Append(",\"reverbWet\":").F(CMMConfig.WorldEmitterReverbWet)
                  .Append(",\"reverbCarry\":").F(CMMConfig.WorldEmitterReverbCarry)
                  .Append(",\"costScale\":").Append(EmitterAcoustics.DebugCostScale)
                  .Append(",\"fieldCache\":").Append(CMMConfig.WorldEmitterFieldCache ? "true" : "false")
                  .Append(",\"fieldRadius\":").Append(CMMConfig.WorldEmitterFieldRadius)
                  .Append("},\n");

                sb.Append("\"emitters\":[");
                for (int i = 0; i < _emitters.Count; i++)
                {
                    SweepEmitter e = _emitters[i];
                    if (i > 0) sb.Append(',');
                    sb.Append($"{{\"x\":{e.Pos.x},\"y\":{e.Pos.y},\"z\":{e.Pos.z},\"name\":\"{Escape(e.Name)}\",\"virtual\":{(e.Virtual ? "true" : "false")}}}");
                }
                sb.Append("],\n");

                sb.Append("\"blocks\":[");
                for (int i = 0; i < _names.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(Escape(_names[i])).Append('"');
                }
                sb.Append("],\n");

                sb.Append("\"loss\":\"").Append(Convert.ToBase64String(_loss)).Append("\",\n");
                sb.Append("\"flags\":\"").Append(Convert.ToBase64String(_flags)).Append("\",\n");
                byte[] blockBytes = new byte[_blockIdx.Length * 2];
                Buffer.BlockCopy(_blockIdx, 0, blockBytes, 0, blockBytes.Length);
                sb.Append("\"block\":\"").Append(Convert.ToBase64String(blockBytes)).Append("\",\n");

                sb.Append("\"floods\":[\n").Append(_floodJson).Append("\n],\n");
                sb.Append("\"fields\":[\n").Append(_fieldJson).Append("\n],\n");
                sb.Append("\"fieldSamples\":[\n").Append(_fieldSampleJson).Append("\n]\n}\n");
                File.WriteAllText(path, sb.ToString());
                Log.Out($"[CMM] AcSweep '{_poiName}' complete: {_sx}x{_sy}x{_sz} cells, " +
                        $"{_emitters.Count} emitter(s), {_ears.Count} flood(s) → {path}");
            }
            catch (Exception e)
            {
                Log.Error($"[CMM] AcSweep failed writing {path ?? "<path>"}: {e.Message}");
            }
            Cleanup();
        }

        // ── Small helpers ─────────────────────────────────────────────────────
        private static long CellIndex(int x, int y, int z)
            => (x - _x0) + (long)_sx * ((z - _z0) + (long)_sz * (y - _y0));

        private static int LossAt(int x, int y, int z)
        {
            if (x < _x0 || x >= _x0 + _sx || y < _y0 || y >= _y0 + _sy || z < _z0 || z >= _z0 + _sz) return 255;
            return _loss[CellIndex(x, y, z)];
        }

        private static int NameIndex(string name)
        {
            if (_nameToIdx.TryGetValue(name, out int i)) return i;
            i = _names.Count;
            if (i > ushort.MaxValue) return 0;
            _names.Add(name);
            _nameToIdx[name] = i;
            return i;
        }

        private static string Escape(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static float DistanceToBox(Vector3 p, Vector3i bpos, Vector3i bsize)
        {
            float dx = p.x - Mathf.Clamp(p.x, bpos.x, bpos.x + bsize.x);
            float dy = p.y - Mathf.Clamp(p.y, bpos.y, bpos.y + bsize.y);
            float dz = p.z - Mathf.Clamp(p.z, bpos.z, bpos.z + bsize.z);
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    // Culture-safe float append (dumps must parse the same on any locale).
    internal static class SweepSbExt
    {
        internal static StringBuilder F(this StringBuilder sb, float v)
            => sb.Append(v.ToString("0.####", CultureInfo.InvariantCulture));
    }
}
#endif
