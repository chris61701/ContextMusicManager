using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using MusicUtils.Enums;

namespace CMM
{
    public static class CMMConfigLoader
    {
        public static string SettingsFile(string modPath) =>
            Path.Combine(modPath, "Config", "settings.xml");

        public static string ContextsFile(string modPath) =>
            Path.Combine(modPath, "Config", "contexts.xml");

        public static void LoadSettings(string modPath)
        {
            string path = SettingsFile(modPath);
            if (!File.Exists(path))
            {
                CMMConfig.D("[CMM] Config/settings.xml not found — using defaults.");
                return;
            }
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                XmlElement root = doc.DocumentElement;

                CMMConfig.VolumeModifier   = ReadFloat(root, "VolumeModifier",   CMMConfig.VolumeModifier);
                CMMConfig.CrossfadeEnabled   = ReadBool (root, "CrossfadeEnabled",   CMMConfig.CrossfadeEnabled);
                CMMConfig.ReverseFadeReclaim = ReadBool (root, "ReverseFadeReclaim", CMMConfig.ReverseFadeReclaim);
                CMMConfig.DebugLogging       = ReadBool (root, "DebugLogging",       CMMConfig.DebugLogging);

                LoadDisabledVanillaSections(root);

                CMMConfig.LoudnessNormalizationEnabled = ReadBool (root, "LoudnessNormalizationEnabled", CMMConfig.LoudnessNormalizationEnabled);
                CMMConfig.LoudnessTargetLufs           = ReadFloat(root, "LoudnessTargetLufs",           CMMConfig.LoudnessTargetLufs);
                CMMConfig.LoudnessMaxBoostDb           = ReadFloat(root, "LoudnessMaxBoostDb",           CMMConfig.LoudnessMaxBoostDb);

                CMMConfig.EnemyDetectRange                = ReadFloat(root, "EnemyDetectRange",                 CMMConfig.EnemyDetectRange);
                CMMConfig.PlayerDetectRange                = ReadFloat(root, "PlayerDetectRange",                CMMConfig.PlayerDetectRange);
                CMMConfig.CombatShareRange                 = ReadFloat(root, "CombatShareRange",                 CMMConfig.CombatShareRange);
                CMMConfig.CombatShareRequireAlly           = ReadBool (root, "CombatShareRequireAlly",           CMMConfig.CombatShareRequireAlly);
                CMMConfig.HomeDetectRange                  = ReadFloat(root, "HomeDetectRange",                  CMMConfig.HomeDetectRange);
                CMMConfig.PlayerHealthDangerThresholdPercent = ReadFloat(root, "PlayerHealthDangerThresholdPercent", CMMConfig.PlayerHealthDangerThresholdPercent);
                CMMConfig.CombatExitDelay                  = ReadFloat(root, "CombatExitDelay",                  CMMConfig.CombatExitDelay);
                CMMConfig.CombatCooldown                   = ReadFloat(root, "CombatCooldown",                   CMMConfig.CombatCooldown);

                CMMConfig.CombatLowEnemyThreshold  = ReadInt(root, "CombatLowEnemyThreshold",  CMMConfig.CombatLowEnemyThreshold);
                CMMConfig.CombatHighEnemyThreshold = ReadInt(root, "CombatHighEnemyThreshold", CMMConfig.CombatHighEnemyThreshold);

                string rawMusicPath = ReadString(root, "MusicPath", null);
                if (!string.IsNullOrWhiteSpace(rawMusicPath))
                {
                    ContextMusicManager.MusicDirectory = Path.IsPathRooted(rawMusicPath)
                        ? rawMusicPath
                        : Path.Combine(modPath, rawMusicPath);
                    CMMConfig.D($"[CMM] MusicPath resolved to: {ContextMusicManager.MusicDirectory}");
                }

                ContextMusicManager.CrossFadeDuration           = ReadFloat(root, "CrossFadeDuration",           ContextMusicManager.CrossFadeDuration);
                ContextMusicManager.TrackTransitionFadeDuration = ReadFloat(root, "TrackTransitionFadeDuration", ContextMusicManager.TrackTransitionFadeDuration);
                ContextMusicManager.ContextScanCooldown         = ReadFloat(root, "ContextScanCooldown",         ContextMusicManager.ContextScanCooldown);
                ContextMusicManager.StalenessCheckInterval      = ReadFloat(root, "StalenessCheckInterval",      ContextMusicManager.StalenessCheckInterval);
                ContextMusicManager.TrackEndPollInterval        = ReadFloat(root, "TrackEndPollInterval",        ContextMusicManager.TrackEndPollInterval);
                ContextMusicManager.GlobalTrackOrder            = ReadEnum<TrackOrderMode>(root, "GlobalTrackOrder", ContextMusicManager.GlobalTrackOrder);

                CMMConfig.KeybindsEnabled    = ReadBool(root, "KeybindsEnabled",    CMMConfig.KeybindsEnabled);
                CMMConfig.KeybindRequireCtrl = ReadBool(root, "KeybindRequireCtrl", CMMConfig.KeybindRequireCtrl);
                CMMConfig.KeybindRequireAlt  = ReadBool(root, "KeybindRequireAlt",  CMMConfig.KeybindRequireAlt);
                CMMConfig.KeyNextTrack  = ReadEnum<UnityEngine.KeyCode>(root, "KeyNextTrack",  CMMConfig.KeyNextTrack);
                CMMConfig.KeyPrevTrack  = ReadEnum<UnityEngine.KeyCode>(root, "KeyPrevTrack",  CMMConfig.KeyPrevTrack);
                CMMConfig.KeyVolumeUp   = ReadEnum<UnityEngine.KeyCode>(root, "KeyVolumeUp",   CMMConfig.KeyVolumeUp);
                CMMConfig.KeyVolumeDown = ReadEnum<UnityEngine.KeyCode>(root, "KeyVolumeDown", CMMConfig.KeyVolumeDown);
                CMMConfig.UserTrimStep  = ReadFloat(root, "UserTrimStep", CMMConfig.UserTrimStep);

                CMMConfig.NativeOutputRingMs = ReadInt (root, "NativeOutputRingMs", CMMConfig.NativeOutputRingMs);
                CMMConfig.NativeOutputMaxRingMs = ReadInt (root, "NativeOutputMaxRingMs", CMMConfig.NativeOutputMaxRingMs);
                CMMConfig.NativeScheduler = ReadBool(root, "NativeScheduler", CMMConfig.NativeScheduler);
                CMMConfig.NativePreStageAdvance = ReadBool(root, "NativePreStageAdvance", CMMConfig.NativePreStageAdvance);

                CMMConfig.PerfTiming  = ReadBool(root, "PerfTiming",  CMMConfig.PerfTiming);
                CMMConfig.PerfSpikeMs = ReadFloat(root, "PerfSpikeMs", CMMConfig.PerfSpikeMs);
                CMMConfig.PerfFrameSpikeFactor = ReadFloat(root, "PerfFrameSpikeFactor", CMMConfig.PerfFrameSpikeFactor);
                CMMConfig.PerfFrameSpikeFloorMs = ReadFloat(root, "PerfFrameSpikeFloorMs", CMMConfig.PerfFrameSpikeFloorMs);

                CMMConfig.D("[CMM] Config/settings.xml loaded.");
            }
            catch (Exception ex)
            {
                Log.Error($"[CMM] Failed to parse Config/settings.xml: {ex.Message}");
            }
        }

        public static void LoadContexts(string modPath)
        {
            string path = ContextsFile(modPath);
            if (!File.Exists(path))
            {
                CMMConfig.D("[CMM] Config/contexts.xml not found — using built-in defaults.");
                return;
            }
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);

                var built = new Dictionary<string, Contexts>(StringComparer.OrdinalIgnoreCase);
                bool sawDeprecatedPath = false;
                foreach (XmlNode node in doc.DocumentElement.ChildNodes)
                {
                    if (node.NodeType != XmlNodeType.Element || node.Name != "Context") continue;
                    var el = (XmlElement)node;
                    string name = el.GetAttribute("name");
                    if (string.IsNullOrEmpty(name)) continue;

                    // `source` is the current attribute (folder OR .m3u playlist). `path`
                    // is the pre-playlist name kept as a deprecated alias so existing
                    // configs keep loading; source wins when both are present.
                    string source = el.GetAttribute("source");
                    if (string.IsNullOrEmpty(source) && el.HasAttribute("path"))
                    {
                        source = el.GetAttribute("path");
                        sawDeprecatedPath = true;
                    }

                    built[name] = new Contexts(
                        context:               name,
                        source:                source,
                        fallback:              StrAttr(el, "fallback", "Stack"),
                        canberesumed:          BoolAttr(el, "resume",   true),
                        timeuntilcontextstale: IntAttr (el, "stale",    120),
                        loop:                  BoolAttr(el, "loop",     false),
                        cooldown:              IntAttr (el, "cool",     5),
                        fadeInDuration:        FloatAttr(el, "fadeintime", 5f),
                        bypassescooldown:      BoolAttr(el, "bypass",   false),
                        priority:              IntAttr (el, "priority", 0),
                        playOnce:              BoolAttr(el, "once",     false),
                        volumeOverride:        FloatAttr(el, "volume",  -1f),
                        warmup:                IntAttr (el, "warmup",   0),
                        warmGroup:             StrAttr (el, "warmgroup", null),
                        includeSubfolders:     BoolAttr(el, "subfolders", false),
                        link:                  StrAttr (el, "link",     null)
                    );
                }

                if (!built.ContainsKey("Silence") || !built.ContainsKey("Vanilla"))
                {
                    Log.Error("[CMM] Config/contexts.xml is missing required sentinels (Silence / Vanilla) — aborting reload.");
                    return;
                }

                ContextMusicManager.contexts.Clear();
                foreach (var kv in built)
                    ContextMusicManager.contexts[kv.Key] = kv.Value;

                // Rebuild the vanilla section→context reverse map from the freshly loaded
                // Link attributes (covers both first load and the hot-reload path).
                ContextMusicManager.RebuildVanillaLinkMap();

                if (sawDeprecatedPath)
                    Log.Warning("[CMM] contexts.xml uses the deprecated 'path' attribute on one or " +
                                "more contexts; rename it to 'source' (a source may be a folder or a " +
                                ".m3u/.m3u8 playlist).");

                CMMConfig.D($"[CMM] Config/contexts.xml loaded: {built.Count} contexts.");
            }
            catch (Exception ex)
            {
                Log.Error($"[CMM] Failed to parse Config/contexts.xml: {ex.Message}");
            }
        }

        // Parses the <DisabledVanillaSections> block — one child element per vanilla DMS
        // section (named for its SectionType), whose value is true=disabled / false=enabled
        // (see CMMConfig.DisabledVanillaSections / GovernVanillaSection). Rebuilds the set
        // from scratch every load so flipping an entry back to false on hot-reload re-enables
        // its section. A missing block clears the set (nothing disabled); a child that isn't a
        // known section name is logged and skipped; None is ignored (silence, never disablable);
        // a missing/unparseable value counts as false (enabled).
        private static void LoadDisabledVanillaSections(XmlElement root)
        {
            CMMConfig.DisabledVanillaSections.Clear();

            if (!(root.SelectSingleNode("DisabledVanillaSections") is XmlElement parent)) return;

            foreach (XmlNode node in parent.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element) continue;
                var el = (XmlElement)node;
                if (!Enum.TryParse(el.Name, true, out SectionType section))
                {
                    Log.Warning($"[CMM] DisabledVanillaSections: unknown section \"{el.Name}\" — ignoring.");
                    continue;
                }
                if (section == SectionType.None) continue;
                if (bool.TryParse(el.InnerText.Trim(), out bool disabled) && disabled)
                    CMMConfig.DisabledVanillaSections.Add(section);
            }

            if (CMMConfig.DisabledVanillaSections.Count > 0)
                CMMConfig.D($"[CMM] Vanilla sections disabled: {string.Join(", ", CMMConfig.DisabledVanillaSections)}");
        }

        // ── Helpers: child-element readers ────────────────────────────────────

        private static string ReadString(XmlElement root, string tag, string fallback)
        {
            var n = root.SelectSingleNode(tag);
            return n != null ? n.InnerText : fallback;
        }

        private static float ReadFloat(XmlElement root, string tag, float fallback)
        {
            var n = root.SelectSingleNode(tag);
            return n != null && float.TryParse(n.InnerText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }

        private static int ReadInt(XmlElement root, string tag, int fallback)
        {
            var n = root.SelectSingleNode(tag);
            return n != null && int.TryParse(n.InnerText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int v) ? v : fallback;
        }

        private static bool ReadBool(XmlElement root, string tag, bool fallback)
        {
            var n = root.SelectSingleNode(tag);
            return n != null && bool.TryParse(n.InnerText, out bool v) ? v : fallback;
        }

        private static T ReadEnum<T>(XmlElement root, string tag, T fallback) where T : struct
        {
            var n = root.SelectSingleNode(tag);
            if (n == null) return fallback;
            return Enum.TryParse(n.InnerText, true, out T v) ? v : fallback;
        }

        // ── Helpers: attribute readers ────────────────────────────────────────

        private static string StrAttr(XmlElement el, string attr, string def)
        {
            string v = el.GetAttribute(attr);
            return string.IsNullOrEmpty(v) ? def : v;
        }

        private static bool BoolAttr(XmlElement el, string attr, bool def)
        {
            string v = el.GetAttribute(attr);
            return bool.TryParse(v, out bool result) ? result : def;
        }

        private static int IntAttr(XmlElement el, string attr, int def)
        {
            string v = el.GetAttribute(attr);
            return int.TryParse(v, out int result) ? result : def;
        }

        private static float FloatAttr(XmlElement el, string attr, float def)
        {
            string v = el.GetAttribute(attr);
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ? result : def;
        }
    }
}
