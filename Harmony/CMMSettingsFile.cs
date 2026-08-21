using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // CONFIG FILE LIFECYCLE — create, backfill, migrate, stamp.
    //
    // CMM no longer ships Config/settings.xml or Config/radio.xml. It writes them
    // on first run and, on upgrade, writes in the settings added since. That kills
    // the old failure where a mod update overwrote the file and silently reset
    // every setting the user had chosen.
    //
    // Which brings the problem this file exists to solve. Once a user HAS a file,
    // a changed default can never reach them again: their value is pinned forever.
    // That is not hypothetical — the POI stale 60→120 change would have reached
    // nobody. So the file carries a schema stamp, and the three questions are
    // answered against it:
    //
    //   BACKFILL — a setting is written in only when Since > fileSchema, i.e. it
    //     genuinely did not exist when this file was stamped. A setting the user
    //     DELETED from a schema they already had is left deleted, and goes on
    //     following the code default. Backfilling everything missing would erase
    //     that distinction permanently.
    //
    //   MIGRATE — when a default changes, the new value is applied only if the
    //     user's value still equals the default in force at their stamp. Someone
    //     who customised it keeps theirs; someone who never touched it gets the
    //     improvement. Compared as PARSED values, never as text: "5" and "5.0"
    //     and " true " are untouched settings, and a text compare would call them
    //     customised and strand them on the old value forever.
    //
    //   STAMP — after either, the file is stamped with CurrentSchema.
    //
    // A file with no stamp reads as schema 0: that is everyone upgrading from a
    // version that still shipped the XML. All keys present, no stamp, so every
    // recorded default change applies to whatever they never touched. Correct.
    //
    // RUNS AT STARTUP ONLY, never on hot-reload. Rewriting the file underneath
    // someone who is editing it is hostile, and a reload has nothing to migrate.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class CMMSettingsFile
    {
        private const string SettingsRoot = "CMMSettings";
        private const string RadioRoot    = "CMMRadioSettings";
        private const string StampAttr    = "schema";

        // True once a write has failed, so a read-only install logs once instead of
        // once per file per launch.
        private static bool _writeFailedReported;

        internal static void EnsureAll(string modPath)
        {
            Ensure(CMMConfigLoader.SettingsFile(modPath),      SettingsRoot, CMMSettingsSchema.Settings, "settings.xml");
            Ensure(CMMConfigLoader.RadioSettingsFile(modPath), RadioRoot,    CMMSettingsSchema.Radio,    "radio.xml");
        }

        // internal, not private: the offline harness drives this directly with synthetic
        // schemas so the backfill / migrate rules are tested against the REAL implementation
        // rather than a paraphrase of it.
        internal static void Ensure(string path, string rootName, SchemaEntry[] entries, string label,
                                    int currentSchema = -1)
        {
            if (currentSchema < 0) currentSchema = CMMSettingsSchema.CurrentSchema;
            try
            {
                if (!File.Exists(path))
                {
                    string rendered = CMMSettingsWriter.Render(entries, rootName, currentSchema);
                    if (WriteAtomic(path, rendered, label))
                        Log.Out($"[CMM] Config/{label} did not exist — written with default values.");
                    return;
                }

                var doc = new XmlDocument { PreserveWhitespace = true };
                doc.Load(path);
                XmlElement root = doc.DocumentElement;
                if (root == null)
                {
                    Log.Warning($"[CMM] Config/{label} has no root element — leaving it alone. "
                              + "Delete it to have CMM write a fresh one.");
                    return;
                }

                int fileSchema = ReadStamp(root);
                if (fileSchema == currentSchema) return;   // nothing to do

                var added = new List<SettingDef>();
                var migrated = new List<string>();

                foreach (SchemaEntry e in entries)
                {
                    SettingDef s = e.Setting;
                    if (s == null) continue;
                    XmlElement el = root.SelectSingleNode(s.Name) as XmlElement;

                    if (el == null)
                    {
                        // Only settings that did not exist at the file's stamp. A key the
                        // user deleted from a schema they already had stays deleted.
                        if (s.Since > fileSchema) added.Add(s);
                        continue;
                    }

                    if (s.DefaultChangedIn > fileSchema && s.WasDefault != null
                        && CMMSettingsIO.TryParse(s, el.InnerText, out object current)
                        && CMMSettingsIO.SameValue(s, current, s.WasDefault))
                    {
                        el.InnerText = CMMSettingsIO.Format(s, s.Default);
                        migrated.Add($"{s.Name} {CMMSettingsIO.Format(s, s.WasDefault)}"
                                   + $"→{CMMSettingsIO.Format(s, s.Default)}");
                    }
                }

                if (added.Count > 0) AppendNewSettings(doc, root, entries, added, fileSchema, currentSchema);
                root.SetAttribute(StampAttr, currentSchema.ToString());

                if (!WriteAtomicXml(path, doc, label)) return;

                if (added.Count > 0)
                    Log.Out($"[CMM] Config/{label}: added {added.Count} new setting(s) — "
                          + string.Join(", ", added.ConvertAll(a => a.Name).ToArray()));
                if (migrated.Count > 0)
                    Log.Out($"[CMM] Config/{label}: {migrated.Count} default(s) updated (you had not "
                          + "changed them) — " + string.Join(", ", migrated.ToArray()));
                if (added.Count == 0 && migrated.Count == 0)
                    CMMConfig.D($"[CMM] Config/{label}: re-stamped schema {fileSchema} → "
                              + currentSchema + ", no content change.");
            }
            catch (Exception ex)
            {
                // Never fatal. A config file we cannot manage still loads; the user just
                // does not get the new keys written in for them.
                Log.Warning($"[CMM] could not update Config/{label} ({ex.Message}). "
                          + "Defaults still apply; the file is unchanged.");
            }
        }

        private static int ReadStamp(XmlElement root)
        {
            string raw = root.GetAttribute(StampAttr);
            // No attribute = a file from a version that still shipped the XML. Schema 0.
            return int.TryParse(raw, out int v) && v > 0 ? v : 0;
        }

        // Append the new settings at the END, under a banner, each with its own
        // documentation from the schema. Appending rather than slotting each one into
        // its group keeps every existing byte where the user left it — and the banner
        // means "what did this update add?" is answerable by opening the file.
        private static void AppendNewSettings(XmlDocument doc, XmlElement root,
                                              SchemaEntry[] entries, List<SettingDef> added,
                                              int fileSchema, int currentSchema)
        {
            string indent = DetectIndent(root);
            var pending = new List<XmlNode>();

            pending.Add(doc.CreateWhitespace(CMMSettingsWriter.NL));
            pending.Add(doc.CreateWhitespace(CMMSettingsWriter.NL + indent));
            pending.Add(doc.CreateComment($" Added in schema {currentSchema} "
                                        + $"(this file was at {fileSchema}). "
                                        + "Edit freely — CMM will not rewrite these again. "));

            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (SettingDef s in added) wanted.Add(s.Name);

            // Walk the schema so a new setting carries the comment block that documents
            // it, in table order, rather than an undocumented bare element.
            string carried = null;
            foreach (SchemaEntry e in entries)
            {
                if (e.Comment != null) { carried = e.Comment; continue; }
                if (e.Gap) { continue; }
                if (e.Setting == null || !wanted.Contains(e.Setting.Name)) continue;

                if (carried != null)
                {
                    pending.Add(doc.CreateWhitespace(CMMSettingsWriter.NL + indent));
                    pending.Add(doc.CreateComment(" " + Flatten(carried, indent) + " "));
                    carried = null;
                }
                XmlElement el = doc.CreateElement(e.Setting.Name);
                el.InnerText = CMMSettingsIO.Format(e.Setting, e.Setting.Default);
                pending.Add(doc.CreateWhitespace(CMMSettingsWriter.NL + indent));
                pending.Add(el);
            }

            // Go in BEFORE the trailing whitespace so the closing tag keeps its own line.
            XmlNode trail = root.LastChild;
            bool haveTrail = trail != null && (trail.NodeType == XmlNodeType.Whitespace
                                            || trail.NodeType == XmlNodeType.SignificantWhitespace);
            foreach (XmlNode n in pending)
            {
                if (haveTrail) root.InsertBefore(n, trail);
                else           root.AppendChild(n);
            }
        }

        // Re-indent a multi-line schema comment for the position it is being inserted at,
        // and make sure it cannot contain the one sequence a comment may not hold.
        private static string Flatten(string comment, string indent)
        {
            string body = comment.Replace("\r\n", "\n");
            while (body.Contains("--")) body = body.Replace("--", "—");
            if (body.EndsWith("-")) body += " ";
            return body.Replace("\n", CMMSettingsWriter.NL + indent + "     ");
        }

        private static string DetectIndent(XmlElement root)
        {
            for (int i = root.ChildNodes.Count - 1; i >= 0; i--)
            {
                if (!(root.ChildNodes[i] is XmlElement)) continue;
                XmlNode ws = root.ChildNodes[i].PreviousSibling;
                if (ws != null && ws.NodeType == XmlNodeType.Whitespace)
                {
                    int nl = ws.Value.LastIndexOf('\n');
                    if (nl >= 0) return ws.Value.Substring(nl + 1);
                }
                break;
            }
            return "  ";
        }

        // ── Writing ───────────────────────────────────────────────────────────
        //
        // Both paths go through a temp file so a crash or a full disk cannot leave a
        // half-written config behind — the shape LoudnessStore already uses.
        //
        // The XML path serializes STRAIGHT TO THE FILE and never through a string.
        // XmlWriter takes its declared encoding from the sink, so building the text in
        // a StringBuilder makes it stamp encoding="utf-16" — which then gets written as
        // UTF-8 bytes, and the next launch fails with "There is no Unicode byte order
        // mark". The offline harness caught exactly that; it would have bricked every
        // upgraded config file on the following start.
        //
        // Not doc.Save(path) either: that overload emits a UTF-8 BOM and these files
        // have never had one. NewLineHandling.None leaves the CRLFs exactly as read,
        // and Indent=false because PreserveWhitespace already carries the user's layout.
        private static XmlWriterSettings WriterSettings() => new XmlWriterSettings
        {
            Encoding        = new UTF8Encoding(false),
            NewLineHandling = NewLineHandling.None,
            Indent          = false,
        };

        private static bool WriteAtomicXml(string path, XmlDocument doc, string label)
            => WriteVia(path, label, tmp =>
               {
                   using (XmlWriter w = XmlWriter.Create(tmp, WriterSettings())) doc.Save(w);
               });

        private static bool WriteAtomic(string path, string text, string label)
            => WriteVia(path, label, tmp => File.WriteAllText(tmp, text, new UTF8Encoding(false)));

        private static bool WriteVia(string path, string label, Action<string> writeTmp)
        {
            string tmp = path + ".tmp";
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                writeTmp(tmp);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);

                // Our own write, not a hand edit — do not let the watcher stage a reload
                // of everything a moment later.
                ContextMusicManager.NoteConfigWritten();
                return true;
            }
            catch (Exception ex)
            {
                if (!_writeFailedReported)
                {
                    _writeFailedReported = true;
                    Log.Warning($"[CMM] cannot write Config/{label} ({ex.Message}). Running on "
                              + "built-in defaults for anything the file does not already set. "
                              + "This is usually a read-only install folder or a mod manager overlay.");
                }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }
    }
}
