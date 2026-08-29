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
        // player-craftable `speaker` per the feature scope, and the trader `loudspeaker`
        // (it already voices trader PA announcements — may reactivate later).
        private static readonly string[] TargetNames =
        {
            "radioHam",
            "cntStoreShelfElectronicsSingleBottomSpeaker",
            "cntStoreElectronicsBookShelfSpeaker",
        };
        private static Dictionary<int, string> _targetIds;   // blockID → display name

        // A detected emitter block. Pos is the world block position; Dist is refreshed each poll.
        // StationHash is a stable positive hash the audio layer maps onto a station (hash % count):
        // POI emitters hash their PrefabInstance.id — so EVERY speaker in one POI shares a station,
        // stable across reload and MP-consistent for free (each client computes the same); player-
        // placed emitters (no POI) hash their block position — stable per placement, move to retune.
        // PoiId is the PrefabInstance.id of the building this speaker stands in, or 0 for one a
        // player placed out in the world. StationHash is derived from it, but the hash is lossy and
        // churns (the dedup tiebreak below rewrites it), so the id is carried separately: it is what
        // "retune this building" means, and the tuner UI needs a group identity that does not move
        // under it. See EmitterTuner.
        internal struct Emitter { public Vector3i Pos; public string Name; public float Dist; public int StationHash; public int PoiId; }

        // Knuth multiplicative hashes, masked positive. Derive, don't store: no replication, no
        // persistence, stable across sessions as long as the id/position is.
        private static int StationHashFromId(int id) => (int)(((uint)id * 2654435761u) & 0x7fffffff);
        private static int StationHashFromPos(Vector3i p) =>
            (int)(((uint)(p.x * 73856093 ^ p.y * 19349663 ^ p.z * 83492791) * 2654435761u) & 0x7fffffff);

        // Scan + cache a POI once we come within this many blocks of its bounding box; drop it from
        // the cache once we're farther than this. MUST cover the station's whole audible reach: this
        // was a flat 64 while WorldEmitterRange was 100 (reverb ×1.5 ⇒ 150), so POI speakers were
        // forgotten — and the station HARD-CUT (Count==0 → Stop) — at ~64 m while still clearly
        // audible, and popped into existence there on the way in. Now derived from live config each
        // poll: the dry range × the reverb reach, plus a small margin, floored at the old 64.
        private const float PoiScanRangeMin = 64f;
        private static float PoiKeepRange()
        {
            float r = CMMConfig.WorldEmitterRange;
            if (CMMConfig.WorldEmitterReverbEnabled)
                r *= Mathf.Max(1f, CMMConfig.WorldEmitterReverbRangeMult);
            return Mathf.Max(PoiScanRangeMin, r + 8f);
        }

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
        // Blocks the sweep found inside a POI too big to scan. They are cached like placed blocks
        // -- the POI scan will never claim them -- but they are not placed blocks, and hashing
        // their POSITION gave every speaker in a hospital its own station: stand between two and
        // you hear two stations, where the same building scanned normally is one. Remembering
        // which POI a swept block fell inside restores that, and costs one int per block.
        private static readonly Dictionary<Vector3i, int> _placedPoiId = new Dictionary<Vector3i, int>();
        private static readonly List<Vector3i> _placedDrop = new List<Vector3i>();     // scratch: placed entries to forget this rebuild
        // Positions already in the snapshot → their snapshot index. Dedups BOTH placed-vs-POI AND
        // POI-vs-POI: prefab volumes OVERLAP (a house POI + a part_ sub-prefab + tile pieces), so
        // one radio inside two boxes was scanned by both and entered the snapshot TWICE with two
        // different per-POI StationHashes — one physical radio audibly playing TWO stations at once
        // (log 2026-07-09 13:43: Enclave + Maz, identical nearest 78.3m tracking in lockstep).
        private static readonly Dictionary<Vector3i, int> _snapPos = new Dictionary<Vector3i, int>();

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
            // The throttle stays OUT of the measurement: wrapping the whole method would count
            // every frame that returned immediately and divide the real cost by ~15.
            using (CMMPerf.Measure(CMMPerf.Phase.EmitPoll)) PollInner();
        }

        private static void PollInner()
        {
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
            // Through the compat shim: 3.x fills a list, 2.6 returns one, and neither token may
            // appear at this call site (see EmitterCompat). A false return means the list could
            // not be read this poll -- the next one is 250 ms away.
            if (!EmitterCompat.GetWorldPrefabs(deco, _prefabScratch)) return;
            _nearIds.Clear();
            float now = Time.realtimeSinceStartup;
            float keepRange = PoiKeepRange();
            foreach (PrefabInstance pi in _prefabScratch)
            {
                if (DistanceToBox(pp, pi.boundingBoxPosition, pi.boundingBoxSize) > keepRange) continue;
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
            LogCensus();   // one line, only when what we found changes
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
                    if (_snapPos.TryGetValue(p, out int dupIdx))
                    {
                        // Same block already claimed by an overlapping POI: keep ONE entry, and
                        // break the tie by the SMALLER StationHash — arbitrary but deterministic
                        // (stable across rebuilds, sessions and MP clients), so the radio's station
                        // never flips with dictionary iteration order.
                        if (e.StationHash < Snapshot[dupIdx].StationHash)
                        {
                            // The identity moves as a PAIR. Keeping the winning hash but leaving the
                            // loser's PoiId behind would let the tuner group a speaker with one
                            // building while the audio layer plays it with another.
                            var keep = Snapshot[dupIdx];
                            keep.StationHash = e.StationHash; keep.PoiId = e.PoiId;
                            Snapshot[dupIdx] = keep;
                        }
                        continue;
                    }
                    Snapshot.Add(e);
                    _snapPos[p] = Snapshot.Count - 1;
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
                if (_snapPos.ContainsKey(p)) continue;                                 // already counted via a POI
                // A block inside an unscannable POI takes that POI's identity, so a big building's
                // speakers form ONE station exactly as a scannable building's do. Anything genuinely
                // placed by a player keeps its position hash: move it, retune it.
                int hash = _placedPoiId.TryGetValue(p, out int owningPoi)
                    ? StationHashFromId(owningPoi)
                    : StationHashFromPos(p);
                Snapshot.Add(new Emitter { Pos = p, Name = kv.Value, Dist = dist, StationHash = hash,
                                           PoiId = owningPoi });
                if (dist < NearestDist) { NearestDist = dist; NearestName = kv.Value; NearestPos = p; }
            }
            foreach (Vector3i p in _placedDrop) { _placedCache.Remove(p); _placedPoiId.Remove(p); }
        }

        // Sweep a small box around the player to DISCOVER target blocks that aren't inside a scannable
        // POI (placed radios, or anything in an oversized/skipped POI like a road tile). Newly-found ones
        // are added to _placedCache, which persists them beyond this box (revalidated in RebuildSnapshot)
        // so a placed radio has full range, not just this discovery radius. Cheap (small box).
        private static void ScanProximity(World world, Vector3 pp)
        {
            using (CMMPerf.Measure(CMMPerf.Phase.EmitProx)) ScanProximityInner(world, pp);
        }

        // The discovery box is a FIXED voxel count -- (2*ProxRadiusXZ+1)^2 * (2*ProxRadiusY+1),
        // 41*41*13 = 21,853 today -- so EmitProx's avg ms divided by that is nanoseconds per
        // world.GetBlock, which is the whole basis for costing a wider box. Log it once so the
        // reader of a perf line does not have to recompute it from the constants.
        private static void ScanProximityInner(World world, Vector3 pp)
        {
            if (!_proxSizeLogged)
            {
                _proxSizeLogged = true;
                int vox = (2 * ProxRadiusXZ + 1) * (2 * ProxRadiusXZ + 1) * (2 * ProxRadiusY + 1);
                CMMConfig.D($"[CMM PERF] EmitProx box: {2 * ProxRadiusXZ + 1}x{2 * ProxRadiusY + 1}x" +
                            $"{2 * ProxRadiusXZ + 1} = {vox} voxels/sweep at {1f / (PollInterval * 2f):0.#} Hz " +
                            "(divide EmitProx avg by that for ns/block-read).");
            }
            int px = Mathf.FloorToInt(pp.x), py = Mathf.FloorToInt(pp.y), pz = Mathf.FloorToInt(pp.z);
            int y0 = Mathf.Max(0, py - ProxRadiusY), y1 = Mathf.Min(256, py + ProxRadiusY + 1);
            // THE CHUNK LOOKUP IS THE COST, so it is hoisted out of the voxel loop. World.GetBlock
            // is `GetChunkFromWorldPos(x,y,z)` then `chunk.GetBlock(local)` -- one chunk resolve PER
            // VOXEL. Measured 2026-08-23 at ~225 ns a read: 21,853 voxels came to 5 ms average and
            // 11.7 ms worst, four times worse than the "~21k reads, a couple of ms" this file used
            // to claim, and the reason a wider discovery box looked unaffordable.
            //
            // Chunks are 16 wide and z is the inner loop, so the chunk changes about three times per
            // x row: ~120 resolves per sweep instead of 21,853. The block reads themselves stay.
            //
            // GetBlockNoDamage, NOT GetBlock -- IChunk.GetBlock EXISTS on A20/A21/2.6 and was
            // REMOVED in 3.1, and we compile against 3.1 and ship ONE DLL to all four. NoDamage is
            // on every version, and damage cannot change .isair or .type, which is all this reads.
            IChunk chunk = null;
            int curCX = int.MinValue, curCZ = int.MinValue;
            for (int x = px - ProxRadiusXZ; x <= px + ProxRadiusXZ; x++)
            {
                int cx = World.toChunkXZ(x), bx = World.toBlockXZ(x);
                for (int z = pz - ProxRadiusXZ; z <= pz + ProxRadiusXZ; z++)
                {
                    int cz = World.toChunkXZ(z), bz = World.toBlockXZ(z);
                    if (cx != curCX || cz != curCZ)
                    {
                        curCX = cx; curCZ = cz;
                        chunk = EmitterCompat.GetChunkSync(world, cx, cz);
                    }
                    if (chunk == null) continue;   // unloaded -- GetBlock would have returned Air
                    for (int y = y0; y < y1; y++)
                    {
                        BlockValue bv = chunk.GetBlockNoDamage(bx, World.toBlockY(y), bz);
                        if (bv.isair) continue;
                        if (_targetIds.TryGetValue(bv.type, out string name))
                        {
                            var pos = new Vector3i(x, y, z);
                            if (_placedCache.ContainsKey(pos)) continue;   // already discovered
                            // Inside a scannable POI's bounds, the POI scan owns the block (and its
                            // per-POI station identity). Pre-claiming it here gave a radio placed
                            // INSIDE a POI a position-hash station for its first ~8 s, then an
                            // audible station FLIP when the POI rescan took it over.
                            if (InsideScannablePoi(pos, out int poiId))
                            {
                                // A target block HERE that the POI's cache doesn't know about was
                                // JUST PLACED (or chunk-regenerated). Cache it PROVISIONALLY right
                                // now — with the POI's own station identity, so nothing flips when
                                // the scan takes over — and make the authoritative rescan due. Only
                                // forcing the rescan wasn't enough: the amortized walk of a big POI
                                // takes seconds to commit (3 ms/poll), which kept placement at a
                                // base inside POI bounds a ~5 s wait (log 2026-07-10 19:33).
                                if (!_poiCache.TryGetValue(poiId, out List<Emitter> known))
                                    _poiCache[poiId] = known = new List<Emitter>();
                                bool knownHere = false;
                                for (int k = 0; k < known.Count; k++)
                                    if (known[k].Pos.Equals(pos)) { knownHere = true; break; }
                                if (!knownHere)
                                {
                                    known.Add(new Emitter { Pos = pos, Name = name, PoiId = poiId,
                                                            StationHash = StationHashFromId(poiId) });
                                    _poiScanTime[poiId] = float.MinValue;
                                }
                                continue;
                            }
                            _placedCache[pos] = name;   // discover (kept until destroyed / far)
                            // Inside a POI the scan will never reach? Remember whose, so it joins
                            // that building's station instead of becoming one of its own.
                            if (InsideOversizedPoi(pos, out int bigPoiId)) _placedPoiId[pos] = bigPoiId;
                            else _placedPoiId.Remove(pos);
                        }
                    }
                }
            }
        }

        // Which oversized POI, if any, contains `p`. The mirror of InsideScannablePoi: that one
        // answers "will the scan claim this block", this one answers "is it in a building whose
        // scan will never run", which is the case that needs a station identity from somewhere.
        private static bool InsideOversizedPoi(Vector3i p, out int poiId)
        {
            poiId = 0;
            foreach (PrefabInstance pi in _prefabScratch)
            {
                if (!_oversizedIds.Contains(pi.id)) continue;
                Vector3i b = pi.boundingBoxPosition, sz = pi.boundingBoxSize;
                if (p.x < b.x || p.x >= b.x + sz.x) continue;
                if (p.z < b.z || p.z >= b.z + sz.z) continue;
                if (p.y < b.y || p.y >= b.y + sz.y) continue;
                poiId = pi.id;
                return true;
            }
            return false;
        }

        // ── What did we actually find? ──────────────────────────────────────────────────────
        // The registry logged almost nothing about its own results, so "the ham radio works but
        // the shelf speakers do not" was not a question the log could answer -- on any version.
        // One line, only when the census CHANGES, naming each block type and how it was found:
        // a POI scan (shares the building's station) or the proximity sweep (its own, unless it
        // sits in a building too big to scan).
        private static readonly Dictionary<string, int> _censusScan = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _censusSweep = new Dictionary<string, int>();
        private static string _lastCensus;

        private static void LogCensus()
        {
            if (!CMMConfig.DebugLogging) return;
            _censusScan.Clear(); _censusSweep.Clear();
            foreach (var kv in _poiCache)
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    string n = kv.Value[i].Name ?? "?";
                    _censusScan.TryGetValue(n, out int had); _censusScan[n] = had + 1;
                }
            foreach (var kv in _placedCache)
            {
                string n = kv.Value ?? "?";
                _censusSweep.TryGetValue(n, out int had); _censusSweep[n] = had + 1;
            }

            var sb = new System.Text.StringBuilder();
            foreach (string n in TargetNames)
            {
                _censusScan.TryGetValue(n, out int a);
                _censusSweep.TryGetValue(n, out int b);
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(n).Append(' ').Append(a + b);
                if (a + b > 0) sb.Append(" (poi ").Append(a).Append(", swept ").Append(b).Append(')');
            }
            string line = sb.ToString();
            if (line == _lastCensus) return;
            _lastCensus = line;
            CMMConfig.D($"[CMM] Emitter census: {line}");
        }

        // True when `p` lies inside a nearby, SCANNABLE POI's bounding box — that POI's scan/rescan
        // will claim the block, so the proximity sweep must not. Oversized (never-scanned) POIs and
        // POIs beyond keep range don't count: blocks in those are only ever found by the sweep.
        // _prefabScratch/_nearIds are refreshed by Poll step 1 right before the sweep runs.
        private static bool InsideScannablePoi(Vector3i p, out int poiId)
        {
            foreach (PrefabInstance pi in _prefabScratch)
            {
                if (!_nearIds.Contains(pi.id) || _oversizedIds.Contains(pi.id)) continue;
                Vector3i b = pi.boundingBoxPosition, s = pi.boundingBoxSize;
                if (p.x >= b.x && p.x < b.x + s.x &&
                    p.y >= b.y && p.y < b.y + s.y &&
                    p.z >= b.z && p.z < b.z + s.z) { poiId = pi.id; return true; }
            }
            poiId = 0;
            return false;
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
            if (!_scanActive) return;   // cheap, and outside the measurement on purpose
            using (CMMPerf.Measure(CMMPerf.Phase.EmitPoiScan)) ContinueScanInner(world);
        }

        private static bool _proxSizeLogged;

        private static void ContinueScanInner(World world)
        {
            _scanPolls++;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int colH = _scanY1 - _scanY0, sinceCheck = 0;
            // Same hoist as ScanProximity, and this loop gains MORE from it: it walks whole columns
            // (up to 256 tall), so World.GetBlock was resolving the same chunk once per Y step.
            // One resolve per column instead of colH of them. See ScanProximity for why NoDamage.
            IChunk chunk = null;
            int curCX = int.MinValue, curCZ = int.MinValue;
            while (_scanX < _scanX1)
            {
                int cx = World.toChunkXZ(_scanX), bx = World.toBlockXZ(_scanX);
                while (_scanZ < _scanZ1)
                {
                    int cz = World.toChunkXZ(_scanZ), bz = World.toBlockXZ(_scanZ);
                    if (cx != curCX || cz != curCZ)
                    {
                        curCX = cx; curCZ = cz;
                        chunk = EmitterCompat.GetChunkSync(world, cx, cz);
                    }
                    for (int y = _scanY0; chunk != null && y < _scanY1; y++)
                    {
                        BlockValue bv = chunk.GetBlockNoDamage(bx, World.toBlockY(y), bz);
                        if (bv.isair) continue;
                        if (_targetIds.TryGetValue(bv.type, out string name))
                            _scanHits.Add(new Emitter { Pos = new Vector3i(_scanX, y, _scanZ), Name = name,
                                                        PoiId = _scanPiId,
                                                        StationHash = StationHashFromId(_scanPiId) });
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
            // Carry over cached entries the walk didn't see whose block is still live: a radio placed
            // BEHIND this scan's cursor (provisionally cached by the sweep) must survive the commit,
            // or it blinks out of the snapshot until the sweep re-adds it. Truly-removed blocks fail
            // the GetBlock check and drop as before.
            if (_poiCache.TryGetValue(_scanPiId, out List<Emitter> prior))
            {
                for (int i = 0; i < prior.Count; i++)
                {
                    bool seen = false;
                    for (int j = 0; j < _scanHits.Count; j++)
                        if (_scanHits[j].Pos.Equals(prior[i].Pos)) { seen = true; break; }
                    Vector3i p = prior[i].Pos;
                    if (!seen && world.GetBlock(p.x, p.y, p.z).type == BlockIdFor(prior[i].Name))
                        _scanHits.Add(prior[i]);
                }
            }
            _poiCache[_scanPiId] = new List<Emitter>(_scanHits);
            _poiScanTime[_scanPiId] = Time.realtimeSinceStartup;
            _scanHits.Clear();
            _scanActive = false;
        }

        // Resolve target names → ids for THIS WORLD. Returns false until the block table can answer.
        //
        // This used to resolve once per PROCESS and cache the answer forever, which was wrong twice
        // over — and both failures presented the same way: a speaker is never detected, placing more
        // does not help, and only quitting the game to desktop clears it.
        //
        //   • EMPTY-CACHE. Block.InitStatic() creates `nameToBlock` EMPTY and blocks register into
        //     it one at a time afterwards. A call landing in that window passed the old `== null`
        //     guard, resolved all three names to null, cached an EMPTY dictionary, and
        //     `_targetIds != null` made that permanent. Every lookup missed from then on — and
        //     BlockIdFor() returns -1 on an empty table, so RebuildSnapshot's
        //     `type != BlockIdFor(...)` went always-true and actively PURGED the speakers already
        //     found.
        //   • STALE-IDS. Block.Cleanup() nulls the table on world exit and the next load reassigns
        //     every blockID (assignIdsLinear). The cached ids belonged to the PREVIOUS world. Not
        //     hypothetical: a client log here shows `Block IDs total` four times in one process with
        //     `terr` differing between those runs (29 then 30,30,30), i.e. the assignment really
        //     does differ per load. Whether the ids happened to line up is what made this
        //     intermittent.
        //
        // The POI scan and the placed-block proximity sweep both read this table, which is why a
        // bad resolve killed hand-placed radios too.
        //
        // Now: never bake in a failed or partial resolve, and ResetSession() drops the table on
        // world exit so ids are re-read per WORLD instead of per process.
        private const float ResolveGraceSeconds = 5f;   // let the block table finish filling before believing a miss
        private static float _resolveFirstMissAt = -1f;
        private static bool  _resolveFailLogged;

        private static bool EnsureTargetIds()
        {
            if (_targetIds != null) return true;
            // Count, not just null: InitStatic() hands out an allocated-but-empty dictionary.
            if (Block.nameToBlock == null || Block.nameToBlock.Count == 0) return false;

            var ids = new Dictionary<int, string>(TargetNames.Length);
            int missing = 0;
            foreach (string n in TargetNames)
            {
                Block b = Block.GetBlockByName(n, true);
                if (b != null) ids[b.blockID] = n;
                else missing++;
            }

            // A partial answer most likely means the table is still filling — retry rather than bake
            // it in. If it is STILL partial after the grace window the blocks genuinely are not in
            // this game version, so take what resolved and say so once.
            if (missing > 0)
            {
                if (_resolveFirstMissAt < 0f) _resolveFirstMissAt = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - _resolveFirstMissAt < ResolveGraceSeconds) return false;

                if (ids.Count == 0)
                {
                    // Never cache this. Returning false keeps retrying (three dictionary lookups on
                    // a 4 Hz poll), which is the whole point: an empty table cached forever is the
                    // bug being fixed here.
                    if (!_resolveFailLogged)
                    {
                        _resolveFailLogged = true;
                        Log.Warning($"[CMM] World emitters: none of the {TargetNames.Length} target blocks resolved in this world — emitters are off. Retrying.");
                    }
                    return false;
                }
                Log.Warning($"[CMM] World emitters: {missing} of {TargetNames.Length} target block(s) not found in this game version; running with {ids.Count}.");
            }

            _targetIds = ids;
            CMMConfig.D($"[CMM] World emitters: resolved {ids.Count} target block id(s) for this world.");
            return true;
        }

        // Forget everything tied to the world we are leaving. Called from the SaveAndCleanupWorld
        // teardown (Patch_GameEnded), because every cache below is keyed by something the next world
        // reassigns or reuses: `_targetIds` by blockID (reassigned per load — see EnsureTargetIds),
        // `_poiCache`/`_poiScanTime`/`_oversizedIds` by PrefabInstance.id, `_placedCache` by world
        // block position. Before this existed there was NO reset anywhere in the registry, so all of
        // it leaked into the next world.
        internal static void ResetSession()
        {
            _targetIds = null;
            _resolveFirstMissAt = -1f;
            _resolveFailLogged = false;

            _poiCache.Clear();
            _poiScanTime.Clear();
            _oversizedIds.Clear();
            _placedCache.Clear();
            _placedPoiId.Clear();

            Snapshot.Clear();
            _snapPos.Clear();
            NearestName = null;
            NearestDist = float.MaxValue;
            NearestPos = default(Vector3i);

            // Abandon any in-flight POI scan: its cursor and hits belong to the old world.
            _scanActive = false;
            _scanHits.Clear();
            _scanPiId = 0;
            _scanPolls = 0;

            _polledOnce = false;
            _nextPollTime = 0f;
            _proxToggle = false;
            _proxSizeLogged = false;
        }

        // Sweep support: is this block type one of the world-emitter target blocks? Resolves the
        // name→id table on first use (false until the block table exists). Lets the acoustics sweep
        // find real speakers in a POI volume without duplicating the target list.
        internal static bool TryGetTargetName(int blockType, out string name)
        {
            name = null;
            if (!EnsureTargetIds()) return false;
            return _targetIds.TryGetValue(blockType, out name);
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
