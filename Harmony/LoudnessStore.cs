
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // LOUDNESS STORE
    // ─────────────────────────────────────────────────────────────────────────
    // Persistent, content-addressed cache of per-track loudness measurements and
    // the derived volume gain. Two levels:
    //
    //   contentHash → Measurement        (source of truth: lufs, peak, metric
    //                                      version, userTrim, banned/favourite)
    //   relPath     → PathRecord          (fast play-time resolution: which hash,
    //                                      plus size + mtime for change detection)
    //
    // Content-hash keying means a renamed / moved / duplicated file keeps its
    // measurement (and its manual userTrim) for free; only a genuine content change
    // (size or mtime differs → recompute hash) forces a re-measure.
    //
    // The per-track BAN and FAVOURITE flags (keybind-toggled) ride the same records:
    // they get the rename/move/duplicate survival for free and share the debounced
    // save. Like the trim, they can only attach once a track is measured.
    //
    // PERSISTENCE POLICY: EVERY measurement in the store persists to loudness.xml
    // (debounced, off-thread). The store itself stays bounded — RetainOnly keeps only
    // the live working set (playing/staged/history per context, plus each radio
    // station's on-air + deterministic up-next) — so the file stays small while
    // anything measured recently starts at its normalized level next launch instead
    // of hot-then-settling. Radio broadcasts made that audible (2026-07-10): the world
    // clock freezes offline, so a relaunch rejoins the SAME track that was playing at
    // quit — under the old trim-only policy it re-measured (started hot) every time.
    // Manually-trimmed/flagged tracks are additionally EXEMPT FROM EVICTION
    // (IsPersistWorthy): user data is never dropped, measured or not.
    //
    // THREADING: this store is MAIN-THREAD ONLY. Background measurement threads
    // compute (hash, lufs, peak) and hand the result back to the main thread, which
    // is the sole caller of Store()/GetTrackGain()/Save(). The "should I measure
    // this file?" decision (HasCurrent) is likewise made on the main thread before
    // dispatching work, so the dictionaries never need locking.
    // ─────────────────────────────────────────────────────────────────────────

    public static class LoudnessStore
    {
        // Bump when the measurement algorithm changes (e.g. RMS→LUFS, or a K-weight
        // fix). Entries tagged with an older version are ignored on load and the
        // affected tracks are re-measured by the sweep.
        public const int MetricVersion = 1;

        // Final linear-gain safety ceiling (auto boost is already capped in dB; this
        // guards the product with a large manual userTrim from going absurd). The
        // AudioSource clamps to 1.0 anyway, so this only bounds the stored intent.
        private const float MaxLinearGain = 8f;

        public sealed class Measurement
        {
            public double Lufs;
            public double Peak;
            public int MetricVersion = LoudnessStore.MetricVersion;
            public float UserTrim = 1f;   // manual trim (±UserTrimStep, default 0.1); survives re-measure
            public bool Banned;           // keybind: never pick this track again
            public bool Favourite;        // keybind: pick this track FavouriteWeight× more often
        }

        private struct PathRecord
        {
            public string Hash;
            public long Size;
            public long MtimeTicks;       // file last-write UTC ticks (exact round-trip)
        }

        private static readonly Dictionary<string, Measurement> _byHash =
            new Dictionary<string, Measurement>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PathRecord> _byPath =
            new Dictionary<string, PathRecord>(StringComparer.Ordinal);

        private static string _filePath;
        // Read on the scheduler thread (CmmScheduler loudness-save task), written on the
        // main thread — volatile so the off-thread dirty check sees commits promptly.
        private static volatile bool _dirty;

        // ── Play-time lookup ───────────────────────────────────────────────────
        // O(1), no I/O, no hashing: relPath → hash → measurement → gain. Returns a
        // neutral 1.0 whenever normalization is off, the track is unmeasured, or the
        // entry is from an older metric version (a re-measure will replace it).
        public static float GetTrackGain(string absolutePath)
        {
            if (!CMMConfig.LoudnessNormalizationEnabled) return 1f;
            string key = ToRelKey(absolutePath);
            if (key == null) return 1f;

            if (!_byPath.TryGetValue(key, out PathRecord pr)) return 1f;
            if (!_byHash.TryGetValue(pr.Hash, out Measurement m)) return 1f;
            if (m.MetricVersion != MetricVersion) return 1f;
            return ComputeGain(m);
        }

        // Cheap, NO-I/O "do we already hold a usable measurement for this path?" check,
        // used by the track selector's prefer-measured bias. Unlike HasCurrent it does
        // NOT re-stat the file (no size/mtime compare) — a possibly-stale measurement is
        // fine for *selection* (the sweep re-measures genuinely changed files), and this
        // runs on the play hot path, so it must stay allocation- and disk-free.
        public static bool IsMeasured(string absolutePath)
        {
            string key = ToRelKey(absolutePath);
            if (key == null) return false;
            if (!_byPath.TryGetValue(key, out PathRecord pr)) return false;
            if (!_byHash.TryGetValue(pr.Hash, out Measurement m)) return false;
            return m.MetricVersion == MetricVersion;
        }

        // A measurement carrying USER DATA — a manual volume override (UserTrim differs
        // from the 1.0 neutral ceiling) or a ban/favourite flag. These are exempt from
        // RetainOnly eviction (the user can't reproduce them by re-measuring); plain
        // auto measurements are evicted normally but, while live, persist like any other
        // (see PERSISTENCE POLICY).
        private static bool IsPersistWorthy(Measurement m)
            => m != null && (Math.Abs(m.UserTrim - 1f) > 1e-4f || m.Banned || m.Favourite);

        private static float ComputeGain(Measurement m)
        {
            double gainDb = CMMConfig.LoudnessTargetLufs - m.Lufs;
            if (gainDb > CMMConfig.LoudnessMaxBoostDb) gainDb = CMMConfig.LoudnessMaxBoostDb;
            double linear = Math.Pow(10.0, gainDb / 20.0) * m.UserTrim;
            if (linear < 0f) linear = 0f;
            if (linear > MaxLinearGain) linear = MaxLinearGain;
            return (float)linear;
        }

        // ── Freshness check (sweep / block-before-play decide whether to measure) ─
        // True when we already hold a current measurement for this exact file
        // content. size + mtime guard against an in-place replacement that kept the
        // same name.
        public static bool HasCurrent(string absolutePath, long size, long mtimeTicks)
        {
            string key = ToRelKey(absolutePath);
            if (key == null) return false;
            if (!_byPath.TryGetValue(key, out PathRecord pr)) return false;
            if (pr.Size != size || pr.MtimeTicks != mtimeTicks) return false;
            if (!_byHash.TryGetValue(pr.Hash, out Measurement m)) return false;
            return m.MetricVersion == MetricVersion;
        }

        // If another path already carries a measurement for this content hash (the
        // file was copied/renamed), reuse it instead of re-decoding. Returns the
        // existing Measurement or null.
        public static Measurement TryReuseByHash(string hash)
        {
            if (hash != null && _byHash.TryGetValue(hash, out Measurement m)
                && m.MetricVersion == MetricVersion)
                return m;
            return null;
        }

        // ── Commit a measurement (main thread, after background work) ───────────
        // Upserts the path record and, unless an entry for this hash already exists,
        // the measurement. An existing entry's UserTrim is preserved (we only update
        // the measured values + version), so re-measuring never clobbers a manual trim.
        public static void Store(string absolutePath, long size, long mtimeTicks,
            string hash, double lufs, double peak)
        {
            string key = ToRelKey(absolutePath);
            if (key == null || hash == null) return;

            if (_byHash.TryGetValue(hash, out Measurement m))
            {
                m.Lufs = lufs;
                m.Peak = peak;
                m.MetricVersion = MetricVersion;
                // UserTrim left as-is.
            }
            else
            {
                m = new Measurement { Lufs = lufs, Peak = peak, MetricVersion = MetricVersion };
                _byHash[hash] = m;
            }

            _byPath[key] = new PathRecord { Hash = hash, Size = size, MtimeTicks = mtimeTicks };
            MirrorFlagsForPath(absolutePath, m);
            _dirty = true;   // every measurement persists (see PERSISTENCE POLICY)
        }

        // Bind a path to an already-known hash (the copy/rename dedup case) without
        // re-measuring. The Measurement must already be in _byHash.
        public static void BindPath(string absolutePath, long size, long mtimeTicks, string hash)
        {
            string key = ToRelKey(absolutePath);
            if (key == null || hash == null || !_byHash.TryGetValue(hash, out Measurement m)) return;
            _byPath[key] = new PathRecord { Hash = hash, Size = size, MtimeTicks = mtimeTicks };
            MirrorFlagsForPath(absolutePath, m);
            _dirty = true;   // the rebound path must reach disk with its measurement
        }

        // Adjust the manual trim for the track currently at this path, in linear
        // steps (the keybind passes ±0.1 by default). No-op if the track is unmeasured.
        // Returns the new trim, or -1 when there is nothing to adjust. Trim is a pure
        // ATTENUATION: 1.0 (the normalized level) is the ceiling, so the keys can only
        // turn a track down (to 0.1) — never above its normalized loudness.
        public static float AdjustUserTrim(string absolutePath, float delta)
        {
            string key = ToRelKey(absolutePath);
            if (key == null || !_byPath.TryGetValue(key, out PathRecord pr)) return -1f;
            if (!_byHash.TryGetValue(pr.Hash, out Measurement m)) return -1f;

            float t = m.UserTrim + delta;
            if (t < 0.1f) t = 0.1f;
            if (t > 1f) t = 1f;
            m.UserTrim = t;
            _dirty = true;
            return t;
        }

        // Read-only lookup of the manual trim for the track at this path (no I/O, no
        // mutation). Returns the stored UserTrim, or 1.0 (the neutral ceiling) when the
        // track is unmeasured or carries no trim. Used by the debug HUD to show the
        // trim only when it differs from the default.
        public static float GetUserTrim(string absolutePath)
        {
            string key = ToRelKey(absolutePath);
            if (key == null || !_byPath.TryGetValue(key, out PathRecord pr)) return 1f;
            if (!_byHash.TryGetValue(pr.Hash, out Measurement m)) return 1f;
            return m.UserTrim;
        }

        // ── Ban / favourite flags ──────────────────────────────────────────────
        // The flags live on the hash-keyed Measurement (see IsPersistWorthy — a
        // flagged track always persists and is never evicted), but the play-time
        // checks run per CANDIDATE inside PickNextFile, which can walk a very large
        // snapshot. So the flagged tracks are mirrored into absolute-path sets and
        // the hot-path checks are plain zero-alloc HashSet hits — no ToRelKey
        // (Path.GetFullPath + two string allocations) per file.
        private static readonly HashSet<string> _bannedAbs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _favouriteAbs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static int BannedCount => _bannedAbs.Count;
        public static int FavouriteCount => _favouriteAbs.Count;
        public static bool IsBannedPath(string absolutePath) => _bannedAbs.Contains(absolutePath);
        public static bool IsFavouritePath(string absolutePath) => _favouriteAbs.Contains(absolutePath);

        // Bumped whenever the banned set may have changed (a ban toggle, a load, a
        // derived-set rebuild). Lets per-context "is everything here banned?" checks
        // memoize their O(files) sweep and re-run it only when a ban actually moved
        // (see ContextMusicManager.AllTracksBanned).
        public static int BanRevision { get; private set; } = 1;

        // Flip the ban / favourite flag for the track at this path. Returns the new
        // state, or null when the track is unmeasured (no hash to attach the flag to
        // yet — same limitation as the volume trim; the sweep measures the playing
        // track within seconds). Main thread, like every other mutator here.
        public static bool? ToggleBanned(string absolutePath) => ToggleFlag(absolutePath, ban: true);
        public static bool? ToggleFavourite(string absolutePath) => ToggleFlag(absolutePath, ban: false);

        private static bool? ToggleFlag(string absolutePath, bool ban)
        {
            string key = ToRelKey(absolutePath);
            if (key == null || !_byPath.TryGetValue(key, out PathRecord pr)) return null;
            if (!_byHash.TryGetValue(pr.Hash, out Measurement m)) return null;

            bool now;
            if (ban) { m.Banned = !m.Banned; now = m.Banned; }
            else     { m.Favourite = !m.Favourite; now = m.Favourite; }
            _dirty = true;

            // Mirror into the derived set for EVERY path bound to this hash — a track
            // that exists in two contexts is banned/favourited in both. Toggles are
            // rare (a keypress), so the O(paths) walk is fine.
            HashSet<string> set = ban ? _bannedAbs : _favouriteAbs;
            foreach (var kv in _byPath)
            {
                if (!string.Equals(kv.Value.Hash, pr.Hash, StringComparison.Ordinal)) continue;
                string abs = KeyToAbsPath(kv.Key);
                if (abs == null) continue;
                if (now) set.Add(abs); else set.Remove(abs);
            }
            // Also the exact string the caller plays from, in case it isn't canonical.
            if (now) set.Add(absolutePath); else set.Remove(absolutePath);
            if (ban) BanRevision++;
            return now;
        }

        // Rebuilds the absolute-path mirrors from the dictionaries. Called after Load
        // and whenever MusicDirectory may have changed (settings reload) — the rel
        // keys are root-relative, so the derived absolute paths move with the root.
        public static void RebuildDerivedPathSets()
        {
            BanRevision++;
            _bannedAbs.Clear();
            _favouriteAbs.Clear();
            foreach (var kv in _byPath)
            {
                if (!_byHash.TryGetValue(kv.Value.Hash, out Measurement m)) continue;
                if (!m.Banned && !m.Favourite) continue;
                string abs = KeyToAbsPath(kv.Key);
                if (abs == null) continue;
                if (m.Banned) _bannedAbs.Add(abs);
                if (m.Favourite) _favouriteAbs.Add(abs);
            }
        }

        // Keep the mirrors current when a NEW path binds to an already-flagged hash
        // (Store on a re-measure, BindPath on the copy/rename dedup).
        private static void MirrorFlagsForPath(string absolutePath, Measurement m)
        {
            if (m == null || (!m.Banned && !m.Favourite)) return;
            if (m.Banned && _bannedAbs.Add(absolutePath)) BanRevision++;
            if (m.Favourite) _favouriteAbs.Add(absolutePath);
        }

        // Inverse of ToRelKey. Rel keys are lowercased '/'-separated paths under
        // MusicDirectory; absolute keys (external context folders) are stored whole.
        // The derived sets compare OrdinalIgnoreCase, so the lowercasing is moot.
        private static string KeyToAbsPath(string key)
        {
            try
            {
                if (key.Length > 1 && (key[1] == ':' || key[0] == '/' || key[0] == '\\'))
                    return Path.GetFullPath(key);
                string root = ContextMusicManager.MusicDirectory;
                if (string.IsNullOrEmpty(root)) return null;
                return Path.GetFullPath(Path.Combine(root, key));
            }
            catch { return null; }
        }

        // ── Eviction (keep the in-memory store bounded to the working set) ──────
        // Drop every measured PATH whose file is NOT in keepAbsolutePaths, and the
        // hash-keyed Measurement it referenced once nothing else keeps it. This is how
        // the store stays bounded to ~O(contexts) instead of accumulating one entry per
        // track ever heard this session: the caller passes the live working set (each
        // context's playing/last/previous/staged files, plus in-flight measurements),
        // and everything else is reclaimed. Two invariants hold no matter what's passed:
        //   • a manually-trimmed (persist-worthy) measurement is NEVER evicted — it's
        //     user data and tiny; and
        //   • a hash still referenced by a surviving path (a file shared by two contexts)
        //     is never dropped.
        // Anything evicted is re-measured via staging before it can be heard again, so
        // this never introduces a play-time stall. Main-thread only, like the rest.
        private static readonly HashSet<string> _retainKeep = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _retainLiveHashes = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<string> _retainDrop = new List<string>();
        public static void RetainOnly(ICollection<string> keepAbsolutePaths)
        {
            _retainKeep.Clear();
            foreach (string p in keepAbsolutePaths)
            {
                string k = ToRelKey(p);
                if (k != null) _retainKeep.Add(k);
            }

            // Remove path records not in the keep set (trimmed ones are always kept).
            _retainDrop.Clear();
            foreach (var kv in _byPath)
            {
                if (_retainKeep.Contains(kv.Key)) continue;
                if (_byHash.TryGetValue(kv.Value.Hash, out Measurement m) && IsPersistWorthy(m)) continue;
                _retainDrop.Add(kv.Key);
            }
            foreach (string k in _retainDrop) _byPath.Remove(k);

            // GC any hash no surviving path references and that carries no manual trim.
            _retainLiveHashes.Clear();
            foreach (var kv in _byPath) _retainLiveHashes.Add(kv.Value.Hash);
            _retainDrop.Clear();
            foreach (var kv in _byHash)
            {
                if (_retainLiveHashes.Contains(kv.Key) || IsPersistWorthy(kv.Value)) continue;
                _retainDrop.Add(kv.Key);
            }
            foreach (string h in _retainDrop) _byHash.Remove(h);
        }

        // ── Diagnostics (debug HUD) ─────────────────────────────────────────────
        // Number of distinct files that currently hold a loudness measurement in
        // memory this session (path records), and the unique-by-content track count.
        public static int MeasuredFileCount => _byPath.Count;
        public static int MeasuredTrackCount => _byHash.Count;

        // Approximate resident byte cost of the in-memory measurement cache. An
        // estimate only — string payload plus a flat per-entry allowance for the
        // dictionary slot and value; exact CLR layout is runtime-specific.
        public static long EstimateMemoryBytes()
        {
            long bytes = 0;
            // _byPath: rel-path key string + PathRecord (hash ref + two longs) + slot.
            foreach (var kv in _byPath)
                bytes += StringBytes(kv.Key) + 24 + 16;
            // _byHash: 16-char hash key + Measurement object (two doubles, int, float).
            foreach (var kv in _byHash)
                bytes += StringBytes(kv.Key) + 40 + 16;
            return bytes;
        }

        // UTF-16 payload (2 bytes/char) plus a fixed object-header + length allowance.
        private static long StringBytes(string s) => s == null ? 0L : 26L + 2L * s.Length;

        // ── Path key normalization ─────────────────────────────────────────────
        // Files under MusicDirectory key by their '/'-separated, lowercased relative
        // path. A context may also point at an ABSOLUTE folder outside the music dir
        // (settings.xml absolute context path) — those key by their full normalized
        // path so loudness/userTrim still work for external folders. A full-path key
        // (drive letter / leading slash) never collides with a relative one.
        public static string ToRelKey(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return null;

            string full;
            try { full = Path.GetFullPath(absolutePath); }
            catch { return null; }

            string root = ContextMusicManager.MusicDirectory;
            if (!string.IsNullOrEmpty(root))
            {
                string fullRoot = Path.GetFullPath(root);
                if (full.Length > fullRoot.Length &&
                    full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = full.Substring(fullRoot.Length).TrimStart('\\', '/');
                    return rel.Replace('\\', '/').ToLowerInvariant();
                }
            }

            // Outside the music dir (absolute-path context): key by the full path.
            return full.Replace('\\', '/').ToLowerInvariant();
        }

        // ── FNV-1a 64-bit content hash ─────────────────────────────────────────
        // Non-cryptographic — we only need to tell music files apart. Fast enough
        // (~1-3ms for 15 MB) and computed over bytes already read for decoding.
        public static string HashBytes(byte[] data, int length)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong h = offset;
            for (int i = 0; i < length; i++)
            {
                h ^= data[i];
                h *= prime;
            }
            // Fold the byte length in too, so two files that hash-collide but differ
            // in size are still distinguished.
            h ^= (ulong)length;
            h *= prime;
            return h.ToString("x16");
        }

        // Streaming twin of HashBytes: produces the BYTE-FOR-BYTE identical FNV-1a hash
        // (same byte order, same trailing length fold) without ever holding the whole file
        // in a managed array. MeasureLoudness uses this so a measurement no longer allocates
        // a multi-MB Large-Object-Heap buffer per track — the stop-the-world Gen2 GC that
        // hitched the game at transitions. The 64 KB read buffer is below the 85 KB LOH
        // threshold, so it lives in Gen0 and collects for free. Returns null on I/O error.
        public static string HashFile(string path)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong h = offset;
            long length = 0;
            try
            {
                var buf = new byte[65536];   // < 85 KB → Gen0, never LOH
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.Read, buf.Length, FileOptions.SequentialScan))
                {
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            h ^= buf[i];
                            h *= prime;
                        }
                        length += n;
                    }
                }
            }
            catch { return null; }
            h ^= (ulong)length;
            h *= prime;
            return h.ToString("x16");
        }

        // ── Persistence ────────────────────────────────────────────────────────
        public static string CacheFile(string modPath) =>
            Path.Combine(modPath, "Cache", "loudness.xml");

        public static void Load(string modPath)
        {
            _byHash.Clear();
            _byPath.Clear();
            _dirty = false;
            _filePath = CacheFile(modPath);

            if (!File.Exists(_filePath))
            {
                CMMConfig.D("[CMM] Cache/loudness.xml not found — starting empty.");
                return;
            }

            try
            {
                var doc = new XmlDocument();
                doc.Load(_filePath);
                XmlElement root = doc.DocumentElement;
                if (root == null) return;

                foreach (XmlNode node in root.ChildNodes)
                {
                    if (node.NodeType != XmlNodeType.Element) continue;
                    var el = (XmlElement)node;
                    if (el.Name == "Track")
                    {
                        string hash = el.GetAttribute("hash");
                        if (string.IsNullOrEmpty(hash)) continue;
                        int ver = ReadInt(el, "metric", 0);
                        float trim = (float)ReadDouble(el, "trim", 1.0);
                        bool ban = el.GetAttribute("ban") == "1";
                        bool fav = el.GetAttribute("fav") == "1";
                        // Stale algorithm → drop and re-measure, UNLESS the entry carries
                        // user data (trim / ban / favourite): those load with their stale
                        // version intact — the sweep re-measures the loudness (Store keeps
                        // the flags, same hash) instead of discarding the user's choices.
                        if (ver != MetricVersion && !(Math.Abs(trim - 1f) > 1e-4f || ban || fav))
                            continue;
                        _byHash[hash] = new Measurement
                        {
                            Lufs = ReadDouble(el, "lufs", -23.0),
                            Peak = ReadDouble(el, "peak", 0.0),
                            MetricVersion = ver,
                            UserTrim = trim,
                            Banned = ban,
                            Favourite = fav,
                        };
                    }
                    else if (el.Name == "Path")
                    {
                        string rel = el.GetAttribute("rel");
                        string hash = el.GetAttribute("hash");
                        if (string.IsNullOrEmpty(rel) || string.IsNullOrEmpty(hash)) continue;
                        _byPath[rel] = new PathRecord
                        {
                            Hash = hash,
                            Size = ReadLong(el, "size", 0),
                            MtimeTicks = ReadLong(el, "mtime", 0),
                        };
                    }
                }

                // Drop path records pointing at measurements we dropped (version skew).
                var orphans = new List<string>();
                foreach (var kv in _byPath)
                    if (!_byHash.ContainsKey(kv.Value.Hash)) orphans.Add(kv.Key);
                foreach (string k in orphans) _byPath.Remove(k);

                RebuildDerivedPathSets();
                CMMConfig.D($"[CMM] Cache/loudness.xml loaded: {_byHash.Count} tracks, {_byPath.Count} paths, " +
                            $"{_bannedAbs.Count} banned, {_favouriteAbs.Count} favourite(s).");
            }
            catch (Exception ex)
            {
                Log.Error($"[CMM] Failed to parse Cache/loudness.xml: {ex.Message}");
                _byHash.Clear();
                _byPath.Clear();
                _bannedAbs.Clear();
                _favouriteAbs.Clear();
            }
        }

        public static bool Dirty => _dirty;

        // Re-flag the cache for save. Used by the off-thread save path (CmmScheduler) to
        // request a retry when a background WriteSnapshot fails, without touching the
        // dictionaries from that thread.
        public static void MarkDirty() => _dirty = true;

        // A detached, thread-safe copy of exactly what gets persisted: built on the MAIN
        // thread (it reads the live dictionaries), then handed to WriteSnapshot to serialize
        // on any thread. This is what lets the debounced save run its slow XML + file IO off
        // Unity's frame loop (see CmmScheduler / ContextMusicManager.LoudnessSaveTick) while
        // the dictionary read stays main-thread-only, as the store's threading contract
        // requires.
        public sealed class SaveSnapshot
        {
            public string FilePath;
            public readonly List<TrackLine> Tracks = new List<TrackLine>();
            public readonly List<PathLine> Paths = new List<PathLine>();

            public struct TrackLine { public string Hash; public double Lufs, Peak; public int Metric; public float Trim; public bool Banned, Favourite; }
            public struct PathLine  { public string Rel, Hash; public long Size, Mtime; }
        }

        // MAIN THREAD: capture everything Save would persist into a detached snapshot and
        // clear the dirty flag. Returns null only when there is no file target. Selection =
        // EVERY live measurement (the dictionaries are already bounded to the working set
        // by RetainOnly) plus the path records that point at one.
        public static SaveSnapshot SnapshotForSave()
        {
            if (_filePath == null) return null;

            var snap = new SaveSnapshot { FilePath = _filePath };
            foreach (var kv in _byHash)
            {
                Measurement m = kv.Value;
                snap.Tracks.Add(new SaveSnapshot.TrackLine
                {
                    Hash = kv.Key, Lufs = m.Lufs, Peak = m.Peak,
                    Metric = m.MetricVersion, Trim = m.UserTrim,
                    Banned = m.Banned, Favourite = m.Favourite,
                });
            }
            foreach (var kv in _byPath)
            {
                PathRecord pr = kv.Value;
                // The file must never reference a hash that isn't in it.
                if (!_byHash.ContainsKey(pr.Hash))
                    continue;
                snap.Paths.Add(new SaveSnapshot.PathLine
                {
                    Rel = kv.Key, Hash = pr.Hash, Size = pr.Size, Mtime = pr.MtimeTicks,
                });
            }

            _dirty = false;
            return snap;
        }

        // ANY THREAD: serialize a previously-captured snapshot to disk. Atomic-ish — write a
        // temp file then replace — so a crash mid-write can't corrupt the cache. Touches no
        // shared state but `snap` (detached) and the filesystem, so it is safe to run on the
        // scheduler thread. Returns false on IO failure so the caller can re-flag dirty.
        public static bool WriteSnapshot(SaveSnapshot snap)
        {
            if (snap == null || snap.FilePath == null) return true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(snap.FilePath));

                string tmp = snap.FilePath + ".tmp";
                // Write through XmlWriter straight to the file with explicit UTF-8 (no
                // BOM) so the emitted <?xml encoding?> declaration matches the bytes on
                // disk — a StringWriter would force a utf-16 declaration and break reload.
                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    OmitXmlDeclaration = false,
                    Encoding = new UTF8Encoding(false),
                };
                using (var w = XmlWriter.Create(tmp, settings))
                {
                    w.WriteStartDocument();
                    w.WriteStartElement("Loudness");
                    w.WriteAttributeString("metricVersion", MetricVersion.ToString(CultureInfo.InvariantCulture));

                    foreach (var t in snap.Tracks)
                    {
                        w.WriteStartElement("Track");
                        w.WriteAttributeString("hash", t.Hash);
                        w.WriteAttributeString("lufs", t.Lufs.ToString("F3", CultureInfo.InvariantCulture));
                        w.WriteAttributeString("peak", t.Peak.ToString("F5", CultureInfo.InvariantCulture));
                        w.WriteAttributeString("metric", t.Metric.ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("trim", t.Trim.ToString("F3", CultureInfo.InvariantCulture));
                        if (t.Banned)    w.WriteAttributeString("ban", "1");
                        if (t.Favourite) w.WriteAttributeString("fav", "1");
                        w.WriteEndElement();
                    }

                    foreach (var p in snap.Paths)
                    {
                        w.WriteStartElement("Path");
                        w.WriteAttributeString("rel", p.Rel);
                        w.WriteAttributeString("hash", p.Hash);
                        w.WriteAttributeString("size", p.Size.ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("mtime", p.Mtime.ToString(CultureInfo.InvariantCulture));
                        w.WriteEndElement();
                    }

                    w.WriteEndElement();
                    w.WriteEndDocument();
                }

                if (File.Exists(snap.FilePath)) File.Delete(snap.FilePath);
                File.Move(tmp, snap.FilePath);
                CMMConfig.D($"[CMM] Cache/loudness.xml saved: {snap.Tracks.Count} trimmed/flagged track(s).");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[CMM] Failed to write Cache/loudness.xml: {ex.Message}");
                return false;
            }
        }

        // Synchronous save (snapshot + write on the calling thread). Retained for the
        // coroutine fallback path (LoudnessSaveLoop) and any main-thread caller; the
        // off-frame-loop path uses SnapshotForSave + WriteSnapshot directly so the IO
        // lands on the scheduler thread. Called on the main thread.
        public static void Save()
        {
            SaveSnapshot snap = SnapshotForSave();
            if (snap != null && !WriteSnapshot(snap)) _dirty = true;
        }

        // ── XML attribute readers ──────────────────────────────────────────────
        private static int ReadInt(XmlElement el, string a, int def)
            => int.TryParse(el.GetAttribute(a), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;
        private static long ReadLong(XmlElement el, string a, long def)
            => long.TryParse(el.GetAttribute(a), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : def;
        private static double ReadDouble(XmlElement el, string a, double def)
            => double.TryParse(el.GetAttribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;
    }
}
