using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using MusicUtils.Enums;

namespace CMM
{
    public static class CMMConfigLoader
    {
        public static string SettingsFile(string modPath) =>
            Path.Combine(modPath, "Config", "settings.xml");

        // DEVELOPER OVERRIDES, Debug deploys only. Sits at the FRONT of the chain so it
        // shadows the generated settings.xml without modifying it. Absent in every release,
        // and CMM never creates or writes it. See build/settings.dev.xml.
        public static string DevSettingsFile(string modPath) =>
            Path.Combine(modPath, "Config", "settings.dev.xml");

        // The radio and world-emitter settings live in their own file: they were 64 of the 113
        // entries and well over half the lines, which buried everything else.
        public static string RadioSettingsFile(string modPath) =>
            Path.Combine(modPath, "Config", "radio.xml");

        public static string ContextsFile(string modPath) =>
            Path.Combine(modPath, "Config", "contexts.xml");

        // Every setting is looked up across a CHAIN of files, first hit wins. That is what lets the
        // radio settings move to their own file without a migration step and without a flag day:
        // a settings.xml still carrying them keeps working exactly as before, because it is
        // searched first. Someone upgrading does not lose values they tuned, and someone starting
        // fresh gets the tidier layout. The alternative — read radio keys only from radio.xml —
        // would silently ignore an existing install's customisations, which is the kind of quiet
        // data loss that is very hard to notice and very annoying to diagnose.
        private static XmlElement Add(List<XmlElement> into, string path, string label)
        {
            if (!File.Exists(path)) { CMMConfig.D($"[CMM] Config/{label} not found — using defaults for it."); return null; }
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                if (doc.DocumentElement != null) { into.Add(doc.DocumentElement); return doc.DocumentElement; }
            }
            catch (Exception ex) { Log.Error($"[CMM] Failed to parse Config/{label}: {ex.Message}"); }
            return null;
        }

        public static void LoadSettings(string modPath)
        {
            var chain = new List<XmlElement>();
            Add(chain, DevSettingsFile(modPath), "settings.dev.xml");   // Debug only; usually absent
            Add(chain, SettingsFile(modPath), "settings.xml");
            XmlElement radioRoot = Add(chain, RadioSettingsFile(modPath), "radio.xml");
            if (chain.Count == 0)
            {
                CMMConfig.D("[CMM] no config files found — using defaults.");
                return;
            }
            XmlElement[] root = chain.ToArray();
            try
            {

                // Every plain setting is applied from the schema table now: one pass in
                // table order, first file in the chain wins, a setting the chain does not
                // mention keeps what it already holds. Same semantics the 130 hand-written
                // ReadBool/ReadFloat/... calls had — the table is just doing it instead, so
                // the default, the documentation and the parsing can no longer disagree.
                // See Harmony/CMMSettingsSchema.cs.
                int applied = CMMSettingsIO.Apply(root);

                // ── The handful that are not a plain field assignment ──────────────

                // A block element with children rather than a value.
                LoadDisabledVanillaSections(root);

                // No backing field of its own: the raw string is resolved against the mod
                // folder and lands in ContextMusicManager.MusicDirectory.
                string rawMusicPath = CMMSettingsIO.RawText(root, "MusicPath");
                if (!string.IsNullOrWhiteSpace(rawMusicPath))
                {
                    ContextMusicManager.MusicDirectory = Path.IsPathRooted(rawMusicPath)
                        ? rawMusicPath
                        : Path.Combine(modPath, rawMusicPath);
                    CMMConfig.D($"[CMM] MusicPath resolved to: {ContextMusicManager.MusicDirectory}");
                }

                // The mute STATE, not just the key, and it drives a live toggle. Applied on
                // every load including hot-reloads — which is safe precisely because the
                // keybind writes the file: a reload can only ever read back the value the
                // toggle just stored, so the two cannot disagree.
                ContextMusicManager.SetCombatMusicMuted(CMMConfig.CombatMusicMuted);
                ContextMusicManager.SetTensionMusicMuted(CMMConfig.TensionMusicMuted);

                // MusicPath may have just changed (settings hot-reload): the ban /
                // favourite mirrors hold absolute paths derived from it, so re-derive.
                LoudnessStore.RebuildDerivedPathSets();

                // Tiny anchor windows mean the station schedule re-anchors constantly.
                if (CMMConfig.RadioAnchorSeconds < 300f) CMMConfig.RadioAnchorSeconds = 300f;

                CMMConfig.D($"[CMM] Config loaded — {applied} settings applied from file.");
            }
            catch (Exception ex)
            {
                Log.Error($"[CMM] Failed to parse Config/settings.xml: {ex.Message}");
            }
        }

        public static void LoadContexts(string modPath)
        {
            // Marks are re-derived from the file every load, so removing combat="true" by hand
            // takes effect on hot-reload instead of persisting until a restart.
            ContextMusicManager.ClearCombatMarks();
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
                    // Explicitly-marked FIGHT music, for the combat-music toggle. Carried in its
                    // own set rather than on Contexts because adding a constructor parameter
                    // means touching all nine construction sites including the offline harness,
                    // and this is one boolean the resolver reads by name anyway.
                    if (BoolAttr(el, "combat", false)) ContextMusicManager.MarkCombatContext(name);

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
                        // `reentry` was `cool` until schema 2; the old name still loads.
                        reentry:               el.HasAttribute("reentry") ? IntAttr(el, "reentry", 5)
                                                                          : IntAttr(el, "cool", 5),
                        fadeInDuration:        FloatAttr(el, "fadeintime", 5f),
                        bypassesreentry:       BoolAttr(el, "bypass",   false),
                        priority:              IntAttr (el, "priority", 0),
                        playOnce:              BoolAttr(el, "once",     false),
                        volumeOverride:        FloatAttr(el, "volume",  -1f),
                        warmup:                IntAttr (el, "warmup",   0),
                        warmGroup:             StrAttr (el, "warmgroup", null),
                        includeSubfolders:     BoolAttr(el, "subfolders", false),
                        link:                  StrAttr (el, "link",     null),
                        // Absent = off. The schema-2 upgrade writes it into existing files.
                        trackCooldown:         FloatAttr(el, "cooldown", 0f),
                        cooldownReset:         BoolAttr(el, "cooldownreset", true),
                        cooldownYield:         BoolAttr(el, "cooldownyield", false)
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
                // Link attributes, and the combat-context set the mute keybind covers
                // (covers both first load and the hot-reload path).
                ContextMusicManager.RebuildVanillaLinkMap();
                ContextMusicManager.RebuildCombatContextSet();

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
        private static void LoadDisabledVanillaSections(XmlElement[] roots)
        {
            CMMConfig.DisabledVanillaSections.Clear();

            if (!(Find(roots, "DisabledVanillaSections") is XmlElement parent)) return;

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

        private static XmlNode Find(XmlElement[] roots, string tag)
        {
            for (int i = 0; i < roots.Length; i++)
            {
                var n = roots[i].SelectSingleNode(tag);
                if (n != null) return n;
            }
            return null;
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

        // ── Writing one setting back to settings.xml ────────────────────────────────────────
        // For settings a KEYBIND changes, so the change outlives the session. The file is a
        // documented, hand-edited artifact — most of it is comments — so this must not rewrite
        // it: PreserveWhitespace keeps every byte of formatting and every comment exactly where
        // it was, and only the one element's text changes. A missing element is appended with
        // the same indentation as its neighbours rather than jammed against the root tag.
        //
        // Returns false and logs a WARNING (not a silent swallow) when the file cannot be
        // written — read-only media, a mod manager's overlay, an antivirus lock. The caller
        // keeps the in-memory change, so the toggle still does what the player pressed it for;
        // they are just told it will not survive the restart.
        public static bool PersistSetting(string modPath, string name, string value)
        {
            string path = SettingsFile(modPath);
            try
            {
                if (!File.Exists(path))
                {
                    Log.Warning($"[CMM] cannot persist {name}: Config/settings.xml is missing.");
                    return false;
                }

                var doc = new XmlDocument { PreserveWhitespace = true };
                doc.Load(path);
                XmlElement root = doc.DocumentElement;
                if (root == null) { Log.Warning($"[CMM] cannot persist {name}: settings.xml has no root."); return false; }

                var el = root.SelectSingleNode(name) as XmlElement;
                if (el != null)
                {
                    if (el.InnerText == value) return true;      // already what we would write
                    el.InnerText = value;
                }
                else
                {
                    // Append, matching the indentation of the last real element so a
                    // hand-maintained file does not suddenly grow a ragged line.
                    string indent = "  ";
                    for (int i = root.ChildNodes.Count - 1; i >= 0; i--)
                    {
                        if (!(root.ChildNodes[i] is XmlElement)) continue;
                        var ws = root.ChildNodes[i].PreviousSibling;
                        if (ws != null && ws.NodeType == XmlNodeType.Whitespace)
                        {
                            const char LF = (char)10;   // no escape: this file is edited by tools
                            int nl = ws.Value.LastIndexOf(LF);
                            if (nl >= 0) indent = ws.Value.Substring(nl + 1);
                        }
                        break;
                    }
                    el = doc.CreateElement(name);
                    el.InnerText = value;
                    var pad = doc.CreateWhitespace(Environment.NewLine + indent);
                    // Go in BEFORE the blank line that precedes </CMMSettings>, not after it:
                    // appending at the very end puts the new element and the closing tag on the
                    // same line. This is the branch every EXISTING install takes on first press,
                    // so it is the one that has to look hand-written, not the shipped-file one.
                    XmlNode trail = root.LastChild;
                    if (trail != null && (trail.NodeType == XmlNodeType.Whitespace
                                       || trail.NodeType == XmlNodeType.SignificantWhitespace))
                    {
                        root.InsertBefore(el, trail);
                        root.InsertBefore(pad, el);
                    }
                    else { root.AppendChild(pad); root.AppendChild(el); }
                }

                // Save through an explicit writer, NOT doc.Save(path): that overload writes a
                // UTF-8 BOM, and settings.xml ships without one. Nothing would break, but the
                // file would silently differ from the shipped copy the first time anyone pressed
                // the key, and a config file that rewrites itself in ways the author did not ask
                // for is how trust in a config file is lost. NewLineHandling.None leaves the
                // CRLFs exactly as they were read.
                var xs = new XmlWriterSettings
                {
                    // WITH the BOM. PersistSetting rewrites settings.xml on every keybind that
                    // persists (Alt+Home most often), and writing it BOM-less here silently
                    // undid CMMSettingsFile's BOM the first time anyone pressed one -- observed
                    // on the RC1 clean install, where radio.xml kept its mark and settings.xml
                    // had lost it. Same encoding either way; only the mark differs.
                    Encoding = new UTF8Encoding(true),
                    NewLineHandling = NewLineHandling.None,
                    Indent = false,               // PreserveWhitespace already carries the layout
                };
                using (var w = XmlWriter.Create(path, xs)) doc.Save(w);
                // Tell the config watcher this write was ours, so it does not treat it as a
                // hand edit and stage a reload of everything a moment later.
                ContextMusicManager.NoteConfigWritten();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] could not save {name} to Config/settings.xml ({ex.Message}). "
                          + "The change applies for this session only.");
                return false;
            }
        }
    }
}
