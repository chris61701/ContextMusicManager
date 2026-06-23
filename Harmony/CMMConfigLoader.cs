using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

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

                CMMConfig.EnemyDetectRange                 = ReadFloat(root, "EnemyDetectRange",                 CMMConfig.EnemyDetectRange);
                CMMConfig.HomeDetectRange                  = ReadFloat(root, "HomeDetectRange",                  CMMConfig.HomeDetectRange);
                CMMConfig.PlayerHealthDangerThresholdPercent = ReadFloat(root, "PlayerHealthDangerThresholdPercent", CMMConfig.PlayerHealthDangerThresholdPercent);
                CMMConfig.CombatExitDelay                  = ReadFloat(root, "CombatExitDelay",                  CMMConfig.CombatExitDelay);
                CMMConfig.CombatCooldown                   = ReadFloat(root, "CombatCooldown",                   CMMConfig.CombatCooldown);

                CMMConfig.CombatLowEnemyThreshold  = ReadInt(root, "CombatLowEnemyThreshold",  CMMConfig.CombatLowEnemyThreshold);
                CMMConfig.CombatHighEnemyThreshold = ReadInt(root, "CombatHighEnemyThreshold", CMMConfig.CombatHighEnemyThreshold);

                CMMConfig.CombatThreat = ParseCombatThreat(root);

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
                ContextMusicManager.TrackTransitionPreloadLead  = ReadFloat(root, "TrackTransitionPreloadLead",  ContextMusicManager.TrackTransitionPreloadLead);
                ContextMusicManager.ContextScanCooldown         = ReadFloat(root, "ContextScanCooldown",         ContextMusicManager.ContextScanCooldown);
                ContextMusicManager.StalenessCheckInterval      = ReadFloat(root, "StalenessCheckInterval",      ContextMusicManager.StalenessCheckInterval);
                ContextMusicManager.TrackEndPollInterval        = ReadFloat(root, "TrackEndPollInterval",        ContextMusicManager.TrackEndPollInterval);
                ContextMusicManager.GlobalTrackOrder            = ReadEnum<TrackOrderMode>(root, "GlobalTrackOrder", ContextMusicManager.GlobalTrackOrder);

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
                foreach (XmlNode node in doc.DocumentElement.ChildNodes)
                {
                    if (node.NodeType != XmlNodeType.Element || node.Name != "Context") continue;
                    var el = (XmlElement)node;
                    string name = el.GetAttribute("name");
                    if (string.IsNullOrEmpty(name)) continue;

                    built[name] = new Contexts(
                        context:               name,
                        path:                  el.GetAttribute("path"),
                        fallback:              StrAttr(el, "fallback", "Stack"),
                        canberesumed:          BoolAttr(el, "resume",   true),
                        timeuntilcontextstale: IntAttr (el, "stale",    120),
                        loop:                  BoolAttr(el, "loop",     false),
                        cooldown:              IntAttr (el, "cool",     5),
                        fadeInDuration:        IntAttr (el, "fadeintime", 5),
                        bypassescooldown:      BoolAttr(el, "bypass",   false),
                        caninterrupt:          true,
                        priority:              IntAttr (el, "priority", 0),
                        playOnce:              BoolAttr(el, "once",     false),
                        volumeOverride:        FloatAttr(el, "volume",  -1f),
                        warmup:                IntAttr (el, "warmup",   0)
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

                CMMConfig.D($"[CMM] Config/contexts.xml loaded: {built.Count} contexts.");
            }
            catch (Exception ex)
            {
                Log.Error($"[CMM] Failed to parse Config/contexts.xml: {ex.Message}");
            }
        }

        // ── Weighted combat-threat parser ─────────────────────────────────────
        // Reads the optional <CombatThreat> block. Absent block ⇒ an unconfigured
        // (IsConfigured == false) instance, so ResolveContext keeps the legacy
        // enemy-count behavior. Match keys are lowercased so the runtime compare
        // against the lowercased entity class name is a plain substring test.
        private static CombatThreatConfig ParseCombatThreat(XmlElement root)
        {
            var cfg = new CombatThreatConfig();
            if (!(root.SelectSingleNode("CombatThreat") is XmlElement node)) return cfg;

            cfg.Enabled       = BoolAttr (node, "enabled",       true);
            cfg.DefaultWeight = FloatAttr(node, "defaultWeight", 1f);

            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType != XmlNodeType.Element) continue;
                var el = (XmlElement)child;
                switch (el.Name)
                {
                    case "Enemy":
                    {
                        string m = el.GetAttribute("match").ToLowerInvariant();
                        if (!string.IsNullOrEmpty(m))
                            cfg.Weights.Add(new KeyValuePair<string, float>(m, FloatAttr(el, "weight", cfg.DefaultWeight)));
                        break;
                    }
                    case "State":
                    {
                        string m = el.GetAttribute("match").ToLowerInvariant();
                        if (!string.IsNullOrEmpty(m))
                            cfg.States.Add(new KeyValuePair<string, float>(m, FloatAttr(el, "multiplier", 1f)));
                        break;
                    }
                    case "Tier":
                    {
                        string ctx = el.GetAttribute("context");
                        if (!string.IsNullOrEmpty(ctx))
                            cfg.Tiers.Add(new TierBand { Context = ctx, MinScore = FloatAttr(el, "minScore", 0f) });
                        break;
                    }
                }
            }

            cfg.Tiers.Sort((a, b) => a.MinScore.CompareTo(b.MinScore));
            CMMConfig.D($"[CMM] CombatThreat: enabled={cfg.Enabled}, {cfg.Weights.Count} weights, " +
                    $"{cfg.States.Count} states, {cfg.Tiers.Count} tiers.");
            return cfg;
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
