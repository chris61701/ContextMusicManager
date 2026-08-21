using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // contexts.xml — rendered from the live context table, then created / topped up
    // on disk. Same story as settings.xml (see CMMSettingsFile): shipping the file
    // meant a mod update overwrote it, so anyone who had added or retuned a context
    // lost it.
    //
    // The difference is where the values come from. Settings have a schema table of
    // their own; contexts do not need one, because InitContexts() already IS the
    // table. CMMContextsSchema carries only what the code cannot: the prose and the
    // grouping. So this renders NAMES AND ORDER from the layout and VALUES from
    // ContextMusicManager.contexts.
    //
    // UPGRADE RULE — deliberately narrower than the settings one. A context is
    // added if the user's file does not have it and the file is behind the current
    // schema. Attributes of a context that already exists are NEVER touched. A
    // context is a thing the user owns and edits as a unit — silently changing the
    // priority or fallback of one they have kept would reorder their stack under
    // them, which is a much bigger surprise than nudging a numeric setting.
    // Built-in attribute changes therefore reach existing users only if they delete
    // that context and let it come back.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class CMMContextsFile
    {
        private const string RootName  = "Contexts";
        private const string StampAttr = "schema";

        private static bool _writeFailedReported;

        // ── Rendering ─────────────────────────────────────────────────────────

        // One <Context .../> line, padded to the shipped file's columns.
        internal static string RenderContext(Contexts c, bool combat)
        {
            var sb = new StringBuilder(220);
            sb.Append("  <Context ");
            foreach (string a in CMMContextsSchema.AttrOrder)
            {
                string v = AttrValue(c, combat, a);
                if (v == null) continue;               // omitted entirely
                string tok = a + "=\"" + Esc(v) + "\"";
                CMMContextsSchema.AttrWidth.TryGetValue(a, out int w);
                sb.Append(tok.PadRight(Math.Max(w, tok.Length))).Append(' ');
            }
            sb.Append("/>");
            return sb.ToString();
        }

        // null = do not emit this attribute at all. combat and link are only written
        // when they say something; everything else is written always, because the
        // shipped file did and a fully-spelled-out line is easier to edit than one
        // where half the knobs are invisible defaults.
        private static string AttrValue(Contexts c, bool combat, string a)
        {
            switch (a)
            {
                case "name":       return c.Context;
                case "source":     return c.Source ?? string.Empty;
                case "subfolders": return B(c.IncludeSubfolders);
                case "fallback":   return c.Fallback ?? "Stack";
                case "combat":     return combat ? "true" : null;
                case "resume":     return B(c.CanBeResumed);
                case "stale":      return I(c.TimeUntilContextStale);
                case "loop":       return B(c.Loop);
                case "cool":       return I(c.Cooldown);
                case "fadeintime": return F(c.FadeInDuration);
                case "warmup":     return I(c.WarmUp);
                case "warmgroup":  return c.WarmGroup ?? string.Empty;
                case "bypass":     return B(c.BypassesCooldown);
                case "priority":   return I(c.Priority);
                case "once":       return B(c.PlayOnce);
                case "volume":     return F(c.VolumeOverride);
                case "link":       return string.IsNullOrEmpty(c.Link) ? null : c.Link;
                default:           return null;
            }
        }

        private static string B(bool v) => v ? "true" : "false";
        private static string I(int v)  => v.ToString(CultureInfo.InvariantCulture);
        // "5" not "5.0", "0.1" not "0.100" — the shipped file's shape. The '.' stops the
        // zero-trim, so "100.0" trims to "100" and never to "1".
        private static string F(float v)
        {
            string s = v.ToString("0.0###", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
            return s.Length == 0 ? "0" : s;
        }

        private static string Esc(string s) =>
            string.IsNullOrEmpty(s) ? string.Empty
            : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;");

        // The whole file: header comment, root, then the layout — comments and blank
        // lines from the schema, values from the live table. Any context in the table
        // that the layout does not mention is appended, so a new context can never be
        // silently dropped just because nobody added a layout line for it.
        internal static string Render(IDictionary<string, Contexts> table,
                                      Func<string, bool> isCombat, int schema)
        {
            var sb = new StringBuilder(32 * 1024);
            string NL = CMMSettingsWriter.NL;

            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>").Append(NL);
            if (!string.IsNullOrEmpty(CMMContextsSchema.Header))
                sb.Append("<!--").Append(NL).Append("  ")
                  .Append(Sanitize(CMMContextsSchema.Header).Replace("\n", NL + "  "))
                  .Append(NL).Append("-->").Append(NL);
            sb.Append('<').Append(RootName).Append(" schema=\"").Append(schema).Append("\">").Append(NL);

            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ContextEntry e in CMMContextsSchema.Layout)
            {
                if (e.Gap) { sb.Append(NL); continue; }
                if (e.Comment != null)
                {
                    string[] lines = Sanitize(e.Comment).Split('\n');
                    sb.Append("  <!-- ").Append(lines[0]);
                    for (int i = 1; i < lines.Length; i++)
                        sb.Append(NL).Append("       ").Append(lines[i]);
                    sb.Append(" -->").Append(NL);
                    continue;
                }
                if (e.Name == null) continue;
                if (!table.TryGetValue(e.Name, out Contexts c) || c == null) continue;  // removed from code
                sb.Append(RenderContext(c, isCombat(e.Name))).Append(NL);
                written.Add(e.Name);
            }

            var strays = new List<string>();
            foreach (var kv in table)
                if (!written.Contains(kv.Key)) strays.Add(kv.Key);
            if (strays.Count > 0)
            {
                strays.Sort(StringComparer.OrdinalIgnoreCase);
                sb.Append(NL).Append("  <!-- Not in the documented layout above. Still loaded normally. -->").Append(NL);
                foreach (string n in strays)
                    sb.Append(RenderContext(table[n], isCombat(n))).Append(NL);
            }

            sb.Append("</").Append(RootName).Append('>').Append(NL);
            return sb.ToString();
        }

        // '--' cannot appear inside an XML comment and '-' cannot end one. This has
        // broken contexts.xml by hand twice; the writer will not do it a third time.
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r\n", "\n");
            while (s.Contains("--")) s = s.Replace("--", "—");
            if (s.EndsWith("-")) s += " ";
            return s;
        }

        // ── Create / top up ───────────────────────────────────────────────────

        internal static void Ensure(string modPath, IDictionary<string, Contexts> table,
                                    Func<string, bool> isCombat, int currentSchema = -1)
        {
            if (currentSchema < 0) currentSchema = CMMContextsSchema.CurrentSchema;
            string path = CMMConfigLoader.ContextsFile(modPath);
            try
            {
                if (!File.Exists(path))
                {
                    if (Write(path, Render(table, isCombat, currentSchema)))
                        Log.Out("[CMM] Config/contexts.xml did not exist — written from the built-in table "
                              + $"({table.Count} contexts).");
                    return;
                }

                var doc = new XmlDocument { PreserveWhitespace = true };
                doc.Load(path);
                XmlElement root = doc.DocumentElement;
                if (root == null)
                {
                    Log.Warning("[CMM] Config/contexts.xml has no root element — leaving it alone. "
                              + "Delete it to have CMM write a fresh one.");
                    return;
                }

                int fileSchema = int.TryParse(root.GetAttribute(StampAttr), out int v) && v > 0 ? v : 0;
                if (fileSchema == currentSchema) return;

                var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (XmlNode n in root.ChildNodes)
                    if (n.NodeType == XmlNodeType.Element && n.Name == "Context")
                        have.Add(((XmlElement)n).GetAttribute("name"));

                var added = new List<string>();
                foreach (ContextEntry e in CMMContextsSchema.Layout)
                {
                    if (e.Name == null || have.Contains(e.Name)) continue;
                    if (!table.ContainsKey(e.Name)) continue;
                    added.Add(e.Name);
                }
                foreach (var kv in table)
                    if (!have.Contains(kv.Key) && !added.Contains(kv.Key)) added.Add(kv.Key);

                if (added.Count > 0) Append(doc, root, table, isCombat, added, fileSchema, currentSchema);
                root.SetAttribute(StampAttr, currentSchema.ToString());

                if (!WriteXml(path, doc)) return;

                if (added.Count > 0)
                    Log.Out($"[CMM] Config/contexts.xml: added {added.Count} new context(s) — "
                          + string.Join(", ", added.ToArray()));
                else
                    CMMConfig.D($"[CMM] Config/contexts.xml: re-stamped schema {fileSchema} → {currentSchema}.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] could not update Config/contexts.xml ({ex.Message}). "
                          + "The file is unchanged and still loads normally.");
            }
        }

        private static void Append(XmlDocument doc, XmlElement root, IDictionary<string, Contexts> table,
                                   Func<string, bool> isCombat, List<string> added,
                                   int fileSchema, int currentSchema)
        {
            string NL = CMMSettingsWriter.NL;
            var pending = new List<XmlNode>();
            pending.Add(doc.CreateWhitespace(NL));
            pending.Add(doc.CreateWhitespace(NL + "  "));
            pending.Add(doc.CreateComment($" Added in schema {currentSchema} (this file was at {fileSchema}). "
                                        + "Edit freely — CMM will not touch these again. "));

            foreach (string name in added)
            {
                if (!table.TryGetValue(name, out Contexts c) || c == null) continue;
                // Parse the rendered line back into a node so PreserveWhitespace keeps
                // the rest of the user's file byte-for-byte.
                var frag = new XmlDocument();
                frag.LoadXml(RenderContext(c, isCombat(name)).Trim());
                XmlNode imported = doc.ImportNode(frag.DocumentElement, true);
                pending.Add(doc.CreateWhitespace(NL + "  "));
                pending.Add(imported);
            }

            XmlNode trail = root.LastChild;
            bool haveTrail = trail != null && (trail.NodeType == XmlNodeType.Whitespace
                                            || trail.NodeType == XmlNodeType.SignificantWhitespace);
            foreach (XmlNode n in pending)
            {
                if (haveTrail) root.InsertBefore(n, trail);
                else           root.AppendChild(n);
            }
        }

        // ── Writing (mirrors CMMSettingsFile: atomic, no BOM, straight to file) ──

        private static bool WriteXml(string path, XmlDocument doc)
            => Via(path, tmp =>
               {
                   var xs = new XmlWriterSettings
                   {
                       Encoding = new UTF8Encoding(false),
                       NewLineHandling = NewLineHandling.None,
                       Indent = false,
                   };
                   using (XmlWriter w = XmlWriter.Create(tmp, xs)) doc.Save(w);
               });

        private static bool Write(string path, string text)
            => Via(path, tmp => File.WriteAllText(tmp, text, new UTF8Encoding(false)));

        private static bool Via(string path, Action<string> writeTmp)
        {
            string tmp = path + ".tmp";
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                writeTmp(tmp);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                ContextMusicManager.NoteContextsWritten();
                return true;
            }
            catch (Exception ex)
            {
                if (!_writeFailedReported)
                {
                    _writeFailedReported = true;
                    Log.Warning($"[CMM] cannot write Config/contexts.xml ({ex.Message}). Running on the "
                              + "built-in context table. Usually a read-only install or a mod manager overlay.");
                }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
        }
    }
}
