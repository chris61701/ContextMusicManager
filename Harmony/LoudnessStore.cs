
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
    //                                      version, userTrim)
    //   relPath     → PathRecord          (fast play-time resolution: which hash,
    //                                      plus size + mtime for change detection)
    //
    // Content-hash keying means a renamed / moved / duplicated file keeps its
    // measurement (and its manual userTrim) for free; only a genuine content change
    // (size or mtime differs → recompute hash) forces a re-measure.
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
            public float UserTrim = 1f;   // manual ±0.05 trim; survives re-measure
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
        private static bool _dirty;

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
            _dirty = true;
        }

        // Bind a path to an already-known hash (the copy/rename dedup case) without
        // re-measuring. The Measurement must already be in _byHash.
        public static void BindPath(string absolutePath, long size, long mtimeTicks, string hash)
        {
            string key = ToRelKey(absolutePath);
            if (key == null || hash == null || !_byHash.ContainsKey(hash)) return;
            _byPath[key] = new PathRecord { Hash = hash, Size = size, MtimeTicks = mtimeTicks };
            _dirty = true;
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
                        if (ver != MetricVersion) continue; // stale algorithm → drop, re-measure
                        _byHash[hash] = new Measurement
                        {
                            Lufs = ReadDouble(el, "lufs", -23.0),
                            Peak = ReadDouble(el, "peak", 0.0),
                            MetricVersion = ver,
                            UserTrim = (float)ReadDouble(el, "trim", 1.0),
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

                CMMConfig.D($"[CMM] Cache/loudness.xml loaded: {_byHash.Count} tracks, {_byPath.Count} paths.");
            }
            catch (Exception ex)
            {
                Log.Error($"[CMM] Failed to parse Cache/loudness.xml: {ex.Message}");
                _byHash.Clear();
                _byPath.Clear();
            }
        }

        public static bool Dirty => _dirty;

        // Atomic-ish write: serialize to a temp file then replace, so a crash mid-write
        // can't corrupt the cache. Called on the main thread (debounced by the caller).
        public static void Save()
        {
            if (_filePath == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath));

                string tmp = _filePath + ".tmp";
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

                    foreach (var kv in _byHash)
                    {
                        Measurement m = kv.Value;
                        w.WriteStartElement("Track");
                        w.WriteAttributeString("hash", kv.Key);
                        w.WriteAttributeString("lufs", m.Lufs.ToString("F3", CultureInfo.InvariantCulture));
                        w.WriteAttributeString("peak", m.Peak.ToString("F5", CultureInfo.InvariantCulture));
                        w.WriteAttributeString("metric", m.MetricVersion.ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("trim", m.UserTrim.ToString("F3", CultureInfo.InvariantCulture));
                        w.WriteEndElement();
                    }

                    foreach (var kv in _byPath)
                    {
                        PathRecord pr = kv.Value;
                        w.WriteStartElement("Path");
                        w.WriteAttributeString("rel", kv.Key);
                        w.WriteAttributeString("hash", pr.Hash);
                        w.WriteAttributeString("size", pr.Size.ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("mtime", pr.MtimeTicks.ToString(CultureInfo.InvariantCulture));
                        w.WriteEndElement();
                    }

                    w.WriteEndElement();
                    w.WriteEndDocument();
                }

                if (File.Exists(_filePath)) File.Delete(_filePath);
                File.Move(tmp, _filePath);
                _dirty = false;
                CMMConfig.D($"[CMM] Cache/loudness.xml saved: {_byHash.Count} tracks.");
            }
            catch (Exception ex)
            {
                Log.Error($"[CMM] Failed to write Cache/loudness.xml: {ex.Message}");
            }
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
