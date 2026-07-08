// Gated to 3.0 (GV_V3): DynamicPrefabDecorator's world-prefab enumeration diverges on older
// game versions (A21 has no GetWorldPrefabs), and the world-emitter feature targets 3.0 for now.
#if GV_V3
using System.Collections.Generic;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // WORLD-EMITTER REGISTRY  (Phase 2 — POI-scoped detection, NO audio yet)
    // ─────────────────────────────────────────────────────────────────────────
    // Tracks which in-world speaker/radio BLOCKS currently exist near the listener, so a later
    // phase can play a spatialized "station" through each. Supersedes the Phase-1 cube-sweep probe.
    //
    // Behavior model (settled with the user): ONE shared station for every emitter (per-type
    // routing comes later), every emitter is ALWAYS ON, and an emitter's lifetime == its block's
    // lifetime (destroyed or unloaded → gone). So the registry's whole job is "which target blocks
    // exist right now", revalidated so a destroyed/unloaded block drops out.
    //
    // Why POI-scoped: every target block lives inside a POI (none are player-placed — the craftable
    // `speaker` is deliberately excluded). So instead of sweeping voxels or whole chunks (most of
    // which are wilderness with zero speakers), we enumerate nearby POIs and scan only their bounds,
    // ONCE, caching the hits per POI. That skips the wilderness entirely and gives effectively
    // unlimited range at a fraction of the cost.
    //
    // Cost shape: GetWorldPrefabs copies a small (~hundreds) ref list each poll; the per-POI voxel
    // scan is one-time and throttled to one new POI per poll so several POIs streaming in at once
    // can't stack into a single frame; revalidation is O(cached emitters) — a handful. No audio and
    // no native/ABI change; only the debug HUD reads the snapshot today.
    internal static class EmitterRegistry
    {
        // Target block names (resolved to ids once the block table exists). Excludes the
        // player-craftable `speaker` per the feature scope.
        private static readonly string[] TargetNames =
        {
            "loudspeaker",
            "radioHam",
            "cntStoreShelfElectronicsSingleBottomSpeaker",
            "cntStoreElectronicsBookShelfSpeaker",
        };
        private static Dictionary<int, string> _targetIds;   // blockID → display name

        // A detected emitter block. Pos is the world block position; Dist is refreshed each poll.
        internal struct Emitter { public Vector3i Pos; public string Name; public float Dist; }

        // Scan + cache a POI once we come within this many blocks of its bounding box; drop it from
        // the cache once we're farther than this (it's out of any plausible audible range).
        private const float PoiScanRange = 64f;

        // Guard against a pathologically huge (modded) POI hitching the frame — skip scanning any
        // whose clamped voxel volume exceeds this. Vanilla traders sit well under it.
        private const long MaxScanVolume = 400_000L;

        // Re-enumerate/revalidate at most this often (Poll self-throttles; callers may spam it). 4 Hz:
        // the expensive POI voxel scan is one-per-poll + cached, so only the cheap revalidation +
        // snapshot rebuild run each tick — cheap enough to keep the in-range set responsive as you move.
        private const float PollInterval = 0.25f;
        private static float _nextPollTime;

        // Re-scan an already-cached POI this often even without an unload, so blocks that appear AFTER
        // the first scan — chunk reset/regeneration respawning speakers, admin edits — get picked up.
        // Rescans share the one-scan-per-poll budget with newly-streamed POIs.
        private const float RescanInterval = 8f;
        private static bool _proxToggle;   // ScanProximity runs on alternate polls (see Poll step 3)

        // Player-proximity scan (for placed emitters that live OUTSIDE any POI, e.g. a ham radio the
        // player sets down): each poll, sweep a small box around the player for target blocks. Half-
        // extents in blocks (XZ wider than Y — placed radios are near your level). This box only
        // DISCOVERS; a discovered emitter is then cached and stays tracked out to full range (like a
        // POI speaker), not re-limited to this box — otherwise a placed radio's range would be just
        // ProxRadiusXZ instead of WorldEmitterRange.
        private const int   ProxRadiusXZ      = 20;
        private const int   ProxRadiusY       = 6;
        private const float PlacedDiscardDist = 256f;   // forget a cached placed emitter once this far (chunks long unloaded); re-discovered on return

        // Per-POI cache of detected emitter positions, keyed by PrefabInstance.id. A present key
        // means "this POI has been scanned"; the value may be an empty list (scanned, no emitters).
        private static readonly Dictionary<int, List<Emitter>> _poiCache = new Dictionary<int, List<Emitter>>();
        private static readonly Dictionary<int, float> _poiScanTime = new Dictionary<int, float>();   // last scan time per POI (for rescan)
        private static readonly HashSet<int> _oversizedIds = new HashSet<int>();   // POIs too big to scan (skip + log once)

        // Reused scratch (no per-poll allocation).
        private static readonly List<PrefabInstance> _prefabScratch = new List<PrefabInstance>();
        private static readonly HashSet<int> _nearIds = new HashSet<int>();
        private static readonly List<int> _dropIds = new List<int>();
        private static readonly Dictionary<Vector3i, string> _placedCache = new Dictionary<Vector3i, string>();  // discovered player-placed emitters (pos→name), kept beyond the discovery box
        private static readonly List<Vector3i> _placedDrop = new List<Vector3i>();     // scratch: placed entries to forget this rebuild
        private static readonly HashSet<Vector3i> _snapPos = new HashSet<Vector3i>();  // positions already in the snapshot (dedup POI vs placed)

        // ── Public snapshot (rebuilt each poll; main-thread only) ──────────────
        // The live set of detected emitters and the nearest one, for the HUD now and audio later.
        internal static readonly List<Emitter> Snapshot = new List<Emitter>();
        internal static int Count => Snapshot.Count;
        internal static string  NearestName;
        internal static float   NearestDist;
        internal static Vector3i NearestPos;   // world block pos of the nearest emitter (valid when Count > 0)
        private static bool _polledOnce;

        // Rescan/revalidate on the throttle. Main thread only (reads live voxel + POI state). Cheap
        // no-op off the clock, outside a game, or before the block table / decorator exist.
        internal static void Poll()
        {
            if (Time.realtimeSinceStartup < _nextPollTime) return;
            _nextPollTime = Time.realtimeSinceStartup + PollInterval;

            World world = GameManager.Instance?.World;
            EntityPlayerLocal player = world?.GetPrimaryPlayer();
            if (world == null || player == null) return;

            if (!EnsureTargetIds()) return;                 // block table not built yet
            DynamicPrefabDecorator deco = GameManager.Instance?.GetDynamicPrefabDecorator();
            if (deco == null) return;

            Vector3 pp = player.position;

            // 0) Advance any in-progress amortized POI scan by one time slice. Scans used to walk a
            //    POI's whole volume (≤400k GetBlocks) in ONE frame — a 50-90 ms main-thread gulp every
            //    RescanInterval near big POIs (the rhythmic in-POI stutter, log 2026-07-07). Results
            //    commit only on completion; the old cache stays live meanwhile, so nothing flickers.
            ContinueScan(world);

            // 1) Enumerate world POIs; scan the ones we're near — either not yet cached, or cached but
            //    STALE (older than RescanInterval, so respawned/chunk-reset blocks get picked up). One
            //    scan in flight at a time, advanced a slice per poll.
            _prefabScratch.Clear();
            deco.GetWorldPrefabs(_prefabScratch);
            _nearIds.Clear();
            float now = Time.realtimeSinceStartup;
            foreach (PrefabInstance pi in _prefabScratch)
            {
                if (DistanceToBox(pp, pi.boundingBoxPosition, pi.boundingBoxSize) > PoiScanRange) continue;
                _nearIds.Add(pi.id);
                if (_oversizedIds.Contains(pi.id)) continue;        // known too big — never rescan/re-log
                if (_scanActive) continue;                          // budget: one scan in flight
                bool cached = _poiCache.ContainsKey(pi.id);
                bool stale  = cached && (!_poiScanTime.TryGetValue(pi.id, out float t) || now - t > RescanInterval);
                if (cached && !stale) continue;                     // fresh enough
                if (!world.IsChunkAreaLoaded(pi.boundingBoxPosition.ToVector3())) continue;  // wait until loaded

                if (StartScan(pi)) ContinueScan(world);             // first slice immediately (small POIs finish here)
            }

            // 2) Forget POIs we've moved away from (frees their cache; they rescan if we return).
            _dropIds.Clear();
            foreach (var kv in _poiCache)
                if (!_nearIds.Contains(kv.Key)) _dropIds.Add(kv.Key);
            foreach (int id in _dropIds) { _poiCache.Remove(id); _poiScanTime.Remove(id); }
            // Abort an in-flight scan whose POI left range (its commit would just be dropped anyway).
            if (_scanActive && !_nearIds.Contains(_scanPiId)) { _scanActive = false; _scanHits.Clear(); }

            // 3) Sweep a small box around the player for placed emitters that live outside any POI
            //    (a ham radio you set down). Every OTHER poll (2 Hz): the box is ~21k block reads, a
            //    couple of ms — halving the standing cost while keeping placement discovery prompt.
            _proxToggle = !_proxToggle;
            if (_proxToggle) ScanProximity(world, pp);

            // 4) Revalidate cached emitters and rebuild the snapshot. A cached block that is no
            //    longer a target id was DESTROYED → drop it; one whose area has UNLOADED → drop the
            //    whole POI cache so it rescans fresh on reload (don't mistake unload for destruction).
            RebuildSnapshot(world, pp);
            _polledOnce = true;
        }

        // Cheap per-frame revalidation of the ALREADY-tracked speakers: re-check their block types
        // (destroyed → drop), refresh distances, rebuild the snapshot. Skips the expensive POI
        // enumeration/scan (that stays on Poll's 4 Hz throttle), so a caller can run it every frame to
        // make destroying a speaker cut the station promptly instead of lagging a quarter-second. No-op
        // until the first Poll has populated the cache.
        internal static void RefreshSnapshot(World world, Vector3 playerPos)
        {
            if (!_polledOnce || world == null) return;
            RebuildSnapshot(world, playerPos);
        }

        private static void RebuildSnapshot(World world, Vector3 pp)
        {
            Snapshot.Clear();
            NearestName = null;
            NearestDist = float.MaxValue;
            _dropIds.Clear();
            _snapPos.Clear();

            foreach (var kv in _poiCache)
            {
                List<Emitter> list = kv.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    Vector3i p = list[i].Pos;
                    if (!world.IsChunkAreaLoaded(p.ToVector3()))
                    {
                        _dropIds.Add(kv.Key);      // unloaded → invalidate the POI so it rescans later
                        break;
                    }
                    if (world.GetBlock(p.x, p.y, p.z).type != BlockIdFor(list[i].Name))
                    {
                        list.RemoveAt(i);          // destroyed → drop just this block
                        continue;
                    }
                    float dist = Vector3.Distance(pp, new Vector3(p.x + 0.5f, p.y + 0.5f, p.z + 0.5f));
                    var e = list[i]; e.Dist = dist; list[i] = e;
                    Snapshot.Add(e);
                    _snapPos.Add(p);
                    if (dist < NearestDist) { NearestDist = dist; NearestName = e.Name; NearestPos = p; }
                }
            }
            foreach (int id in _dropIds) { _poiCache.Remove(id); _poiScanTime.Remove(id); }

            // Player-placed emitters (discovered by the proximity sweep, then cached here so their range
            // is the full WorldEmitterRange, not the discovery box). Revalidate + dedup vs POI hits.
            _placedDrop.Clear();
            foreach (var kv in _placedCache)
            {
                Vector3i p = kv.Key;
                float dist = Vector3.Distance(pp, new Vector3(p.x + 0.5f, p.y + 0.5f, p.z + 0.5f));
                if (dist > PlacedDiscardDist) { _placedDrop.Add(p); continue; }        // long gone — forget (re-discover on return)
                if (!world.IsChunkAreaLoaded(p.ToVector3())) continue;                 // unloaded → keep cached, just not audible now
                if (world.GetBlock(p.x, p.y, p.z).type != BlockIdFor(kv.Value)) { _placedDrop.Add(p); continue; }  // destroyed/picked up
                if (_snapPos.Contains(p)) continue;                                    // already counted via a POI
                Snapshot.Add(new Emitter { Pos = p, Name = kv.Value, Dist = dist });
                if (dist < NearestDist) { NearestDist = dist; NearestName = kv.Value; NearestPos = p; }
            }
            foreach (Vector3i p in _placedDrop) _placedCache.Remove(p);
        }

        // Sweep a small box around the player to DISCOVER target blocks that aren't inside a scannable
        // POI (placed radios, or anything in an oversized/skipped POI like a road tile). Newly-found ones
        // are added to _placedCache, which persists them beyond this box (revalidated in RebuildSnapshot)
        // so a placed radio has full range, not just this discovery radius. Cheap (small box).
        private static void ScanProximity(World world, Vector3 pp)
        {
            int px = Mathf.FloorToInt(pp.x), py = Mathf.FloorToInt(pp.y), pz = Mathf.FloorToInt(pp.z);
            int y0 = Mathf.Max(0, py - ProxRadiusY), y1 = Mathf.Min(256, py + ProxRadiusY + 1);
            for (int x = px - ProxRadiusXZ; x <= px + ProxRadiusXZ; x++)
                for (int z = pz - ProxRadiusXZ; z <= pz + ProxRadiusXZ; z++)
                    for (int y = y0; y < y1; y++)
                    {
                        BlockValue bv = world.GetBlock(x, y, z);   // null-safe → Air on unloaded
                        if (bv.isair) continue;
                        if (_targetIds.TryGetValue(bv.type, out string name))
                        {
                            var pos = new Vector3i(x, y, z);
                            if (!_placedCache.ContainsKey(pos)) _placedCache[pos] = name;   // discover (kept until destroyed / far)
                        }
                    }
        }

        // Scan a POI's bounding box (clamped to the world's Y range) for target blocks.
        // ── Amortized POI scan (one in flight, budget-sliced across polls) ─────────────────────────
        // A column cursor (x,z — full Y per column) resumes each poll and stops after ScanBudgetMs;
        // hits accumulate in _scanHits and commit to the cache only when the whole volume finished.
        // A typical store (~75k cells) completes in a few polls (~1 s); the 400k cap worst case ~5 s —
        // fine for the RescanInterval refresh, and discovery was already staggered one-POI-per-poll.
        private const float ScanBudgetMs = 3f;
        private static bool _scanActive;
        private static int _scanPiId;
        private static int _scanX0, _scanX1, _scanY0, _scanY1, _scanZ0, _scanZ1;   // [min, max) bounds
        private static int _scanX, _scanZ;                                          // resume cursor
        private static int _scanPolls;                                              // diagnostic
        private static readonly List<Emitter> _scanHits = new List<Emitter>();

        // Begin an amortized scan of `pi` (bounds + cursor). False = rejected (oversized/empty).
        private static bool StartScan(PrefabInstance pi)
        {
            Vector3i b = pi.boundingBoxPosition, s = pi.boundingBoxSize;
            int x0 = b.x, z0 = b.z, y0 = Mathf.Max(0, b.y);
            int x1 = b.x + s.x, z1 = b.z + s.z, y1 = Mathf.Min(256, b.y + s.y);
            long vol = (long)Mathf.Max(0, x1 - x0) * Mathf.Max(0, y1 - y0) * Mathf.Max(0, z1 - z0);
            if (vol <= 0 || vol > MaxScanVolume)
            {
                // Remember oversized POIs so Poll never rescans (or re-logs) them — otherwise a POI that
                // keeps flickering in/out of scan range spams the log every few seconds.
                if (vol > MaxScanVolume && _oversizedIds.Add(pi.id))
                    CMMConfig.D($"[CMM] Emitter scan skipped oversized POI '{pi.name}' (vol {vol}).");
                return false;
            }
            _scanPiId = pi.id;
            _scanX0 = x0; _scanX1 = x1; _scanY0 = y0; _scanY1 = y1; _scanZ0 = z0; _scanZ1 = z1;
            _scanX = x0; _scanZ = z0;
            _scanPolls = 0;
            _scanHits.Clear();
            _scanActive = true;
            return true;
        }

        // Advance the in-flight scan until the time budget elapses; commit on completion.
        private static void ContinueScan(World world)
        {
            if (!_scanActive) return;
            _scanPolls++;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int colH = _scanY1 - _scanY0, sinceCheck = 0;
            while (_scanX < _scanX1)
            {
                while (_scanZ < _scanZ1)
                {
                    for (int y = _scanY0; y < _scanY1; y++)
                    {
                        BlockValue bv = world.GetBlock(_scanX, y, _scanZ);   // null-safe → Air on unloaded
                        if (bv.isair) continue;
                        if (_targetIds.TryGetValue(bv.type, out string name))
                            _scanHits.Add(new Emitter { Pos = new Vector3i(_scanX, y, _scanZ), Name = name });
                    }
                    _scanZ++;
                    if ((sinceCheck += colH) >= 2048)
                    {
                        sinceCheck = 0;
                        if ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * toMs >= ScanBudgetMs)
                            return;   // budget spent — resume at this cursor next poll
                    }
                }
                _scanZ = _scanZ0;
                _scanX++;
            }
            // Complete → commit (replaces the old list ⇒ respawned blocks appear, removed ones drop).
            _poiCache[_scanPiId] = new List<Emitter>(_scanHits);
            _poiScanTime[_scanPiId] = Time.realtimeSinceStartup;
            _scanHits.Clear();
            _scanActive = false;
        }

        // Resolve target names → ids once the block table exists. Returns false until it's built.
        private static bool EnsureTargetIds()
        {
            if (_targetIds != null) return true;
            if (Block.nameToBlock == null) return false;
            _targetIds = new Dictionary<int, string>(TargetNames.Length);
            foreach (string n in TargetNames)
            {
                Block b = Block.GetBlockByName(n, true);
                if (b != null) _targetIds[b.blockID] = n;
            }
            return true;
        }

        // Block id for a resolved emitter name (reverse of _targetIds; the set is tiny).
        private static int BlockIdFor(string name)
        {
            foreach (var kv in _targetIds)
                if (kv.Value == name) return kv.Key;
            return -1;
        }

        // Euclidean distance from a point to an axis-aligned block box (0 when inside).
        private static float DistanceToBox(Vector3 p, Vector3i bpos, Vector3i bsize)
        {
            float dx = p.x - Mathf.Clamp(p.x, bpos.x, bpos.x + bsize.x);
            float dy = p.y - Mathf.Clamp(p.y, bpos.y, bpos.y + bsize.y);
            float dz = p.z - Mathf.Clamp(p.z, bpos.z, bpos.z + bsize.z);
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // One HUD line summarising the current registry state (called right after Poll, same thread).
        internal static string HudLine()
        {
            if (_targetIds != null && _targetIds.Count == 0)
                return "<color=#ff6b6b>no target blocks in this game version</color>";
            if (!_polledOnce) return "(scanning…)";
            if (Snapshot.Count == 0) return $"none in {_poiCache.Count} nearby POI(s)";

            int shown = Mathf.Min(4, Snapshot.Count);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"{Snapshot[i].Name} {Snapshot[i].Dist:0.0}m");
            }
            if (Snapshot.Count > shown) sb.Append($", +{Snapshot.Count - shown} more");
            return $"{Snapshot.Count} tracked  •  nearest {NearestName} @ {NearestDist:0.0}m  •  {sb}";
        }
    }
}
#endif
