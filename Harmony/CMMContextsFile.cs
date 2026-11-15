using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
    //
    // THE ONE EXCEPTION is a schema migration of the FILE FORMAT (an attribute
    // renamed, or a new one added that every line needs). Those run once, when the
    // file is behind, keep every value the player wrote, and keep a .bak — see
    // MigrateTo2.
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
                case "reentry":    return I(c.Reentry);
                case "cooldown":   return F(c.TrackCooldown);
                case "cooldownreset": return B(c.CooldownReset);
                case "cooldownyield": return B(c.CooldownYield);
                case "fadeintime": return F(c.FadeInDuration);
                case "warmup":     return I(c.WarmUp);
                case "warmgroup":  return c.WarmGroup ?? string.Empty;
                case "bypass":     return B(c.BypassesReentry);
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
        //
        // TEXT-LEVEL, NOT XmlDocument. The file is column-aligned by hand (and by
        // RenderContext), and XmlDocument.Save rewrites every start tag with single
        // spaces between attributes — PreserveWhitespace keeps whitespace NODES, not
        // the padding inside a tag. Any re-stamp through it flattened the whole file.
        // So everything below edits the text directly: the player's file comes back
        // byte-for-byte except for the tokens an upgrade actually changes, and the
        // result is parsed before it is written so a bad edit can never land.

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

                string text = File.ReadAllText(path);   // strips a BOM; Write puts one back
                string upgraded = Upgrade(text, table, isCombat, currentSchema, out string summary);
                if (upgraded == null) return;            // current, newer, or not ours to touch

                // A one-off copy of what the player had before CMM edited it.
                string bak = path + ".schema" + SchemaOf(text) + ".bak";
                try { if (!File.Exists(bak)) File.Copy(path, bak); } catch { }

                if (!Write(path, upgraded)) return;
                Log.Out("[CMM] Config/contexts.xml upgraded: " + summary);
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] could not update Config/contexts.xml ({ex.Message}). "
                          + "The file is unchanged and still loads normally.");
            }
        }

        // The whole upgrade as a pure text → text function (tools/CmmConfigTest drives it).
        // Returns null when there is nothing to do: the file is at or past currentSchema,
        // or it does not parse / has no root (left alone, as before).
        internal static string Upgrade(string text, IDictionary<string, Contexts> table,
                                       Func<string, bool> isCombat, int currentSchema,
                                       out string summary)
        {
            summary = null;
            var doc = new XmlDocument();
            try { doc.LoadXml(text); }
            catch (XmlException ex)
            {
                Log.Warning($"[CMM] Config/contexts.xml does not parse ({ex.Message}) — leaving it alone.");
                return null;
            }
            XmlElement root = doc.DocumentElement;
            if (root == null || root.Name != RootName)
            {
                Log.Warning("[CMM] Config/contexts.xml has no <Contexts> root — leaving it alone. "
                          + "Delete it to have CMM write a fresh one.");
                return null;
            }

            int fileSchema = SchemaOf(text);
            if (fileSchema >= currentSchema) return null;

            string NL = text.Contains("\r\n") ? "\r\n" : "\n";
            var notes = new List<string>();

            if (fileSchema < 2)
            {
                text = MigrateTo2(text, table, NL, out int touched);
                notes.Add($"`cool` renamed `reentry`, `cooldown`/`cooldownreset`/`cooldownyield` added ({touched} context line(s) changed)");
            }

            // New contexts the file has never had.
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode n in root.ChildNodes)
                if (n.NodeType == XmlNodeType.Element && n.Name == "Context")
                    have.Add(((XmlElement)n).GetAttribute("name"));
            var added = new List<string>();
            foreach (ContextEntry e in CMMContextsSchema.Layout)
                if (e.Name != null && !have.Contains(e.Name) && table.ContainsKey(e.Name) && !added.Contains(e.Name))
                    added.Add(e.Name);
            foreach (var kv in table)
                if (!have.Contains(kv.Key) && !added.Contains(kv.Key)) added.Add(kv.Key);
            if (added.Count > 0)
            {
                text = AppendContexts(text, table, isCombat, added, fileSchema, currentSchema, NL);
                notes.Add($"added {added.Count} new context(s): " + string.Join(", ", added.ToArray()));
            }

            text = StampSchema(text, currentSchema);

            // Never write something that will not load.
            try { new XmlDocument().LoadXml(text); }
            catch (XmlException ex)
            {
                Log.Warning($"[CMM] Config/contexts.xml upgrade produced invalid XML ({ex.Message}) — "
                          + "not written. The file is unchanged.");
                return null;
            }

            summary = $"schema {fileSchema} → {currentSchema}"
                    + (notes.Count > 0 ? "; " + string.Join("; ", notes.ToArray()) : "");
            return text;
        }

        private static readonly Regex RootTag = new Regex(@"<Contexts\b[^>]*>", RegexOptions.Compiled);

        internal static int SchemaOf(string text)
        {
            Match m = RootTag.Match(text);
            if (!m.Success) return 0;
            Match s = Regex.Match(m.Value, @"\bschema\s*=\s*""(\d+)""");
            return s.Success && int.TryParse(s.Groups[1].Value, out int v) && v > 0 ? v : 0;
        }

        private static string StampSchema(string text, int schema)
        {
            Match m = RootTag.Match(text);
            if (!m.Success) return text;
            string tag = m.Value;
            string stamped = Regex.IsMatch(tag, @"\bschema\s*=")
                ? Regex.Replace(tag, @"\bschema\s*=\s*""[^""]*""", $"schema=\"{schema}\"")
                : tag.Insert("<Contexts".Length, $" schema=\"{schema}\"");
            return text.Substring(0, m.Index) + stamped + text.Substring(m.Index + m.Length);
        }

        // ── Schema 2: `cool` → `reentry`, + `cooldown` / `cooldownreset` / `cooldownyield`
        //
        // The player's own values are KEPT: `cool="7"` becomes `reentry="7"`. The new
        // attributes take the built-in value for a context CMM ships (Ambient/Home/POI get
        // cooldown="-1"), and 0 / false for one the player made — the feature is opt-in
        // for anything that is not ours. A line that already has an attribute keeps it.
        // The header comment is CMM's own documentation, so it is replaced with the
        // current one (only when it is recognisably ours).
        internal static string MigrateTo2(string text, IDictionary<string, Contexts> table,
                                          string NL, out int touched)
        {
            touched = 0;
            text = ReplaceHeader(text, NL);

            var sb = new StringBuilder(text.Length + 4096);
            int i = 0;
            while (i < text.Length)
            {
                int ctx = NextContextTag(text, i);
                if (ctx < 0) { sb.Append(text, i, text.Length - i); break; }
                int com = text.IndexOf("<!--", i, StringComparison.Ordinal);
                if (com >= 0 && com < ctx)
                {
                    // A commented-out line is the player's business — copy it untouched.
                    int end = text.IndexOf("-->", com + 4, StringComparison.Ordinal);
                    end = end < 0 ? text.Length : end + 3;
                    sb.Append(text, i, end - i);
                    i = end;
                    continue;
                }
                int close = TagEnd(text, ctx);
                if (close < 0) { sb.Append(text, i, text.Length - i); break; }
                sb.Append(text, i, ctx - i);
                string tag = text.Substring(ctx, close - ctx);
                string fixedTag = MigrateTag2(tag, table);
                if (!ReferenceEquals(fixedTag, tag)) touched++;
                sb.Append(fixedTag);
                i = close;
            }
            return sb.ToString();
        }

        // Index of the next `<Context` START TAG (not `<Contexts`) at or after `from`, or -1.
        private static int NextContextTag(string text, int from)
        {
            int i = from;
            while (true)
            {
                i = text.IndexOf("<Context", i, StringComparison.Ordinal);
                if (i < 0) return -1;
                int after = i + "<Context".Length;
                if (after < text.Length && char.IsWhiteSpace(text[after])) return i;
                i = after;
            }
        }

        // One past the `>` closing the tag that starts at `start`, honouring quotes.
        private static int TagEnd(string text, int start)
        {
            bool q = false;
            for (int k = start; k < text.Length; k++)
            {
                char c = text[k];
                if (c == '"') q = !q;
                else if (c == '>' && !q) return k + 1;
            }
            return -1;
        }

        private static readonly Regex AttrTok =
            new Regex(@"(?<=\s)(?<n>[A-Za-z_][\w.-]*)\s*=\s*""(?<v>[^""]*)""(?<ws>\s*)", RegexOptions.Compiled);

        private static string MigrateTag2(string tag, IDictionary<string, Contexts> table)
        {
            // cooldownyield always lands right after cooldownreset (the player's or the one
            // inserted below), whatever else the line needed.
            string withPair = MigrateTag2Pair(tag, table, out bool hasYield, out Contexts builtIn, out bool aligned);
            if (hasYield) return withPair;
            string yieldTok = "cooldownyield=\"" + (builtIn != null ? B(builtIn.CooldownYield) : "false") + "\"";
            if (aligned)
            {
                CMMContextsSchema.AttrWidth.TryGetValue("cooldownyield", out int w);
                yieldTok = yieldTok.PadRight(Math.Max(w, yieldTok.Length));
            }
            foreach (Match m in AttrTok.Matches(withPair))
            {
                if (m.Groups["n"].Value != "cooldownreset") continue;
                int at = m.Index + m.Length;
                string sep = m.Groups["ws"].Value.Length > 0 ? "" : " ";
                return withPair.Substring(0, at) + sep + yieldTok + " " + withPair.Substring(at);
            }
            return withPair;   // unreachable: the pair step always leaves a cooldownreset
        }

        private static string MigrateTag2Pair(string tag, IDictionary<string, Contexts> table,
                                              out bool hasYield, out Contexts builtIn, out bool aligned)
        {
            Match nameTok = null, coolTok = null, reentryTok = null, loopTok = null;
            bool hasCooldown = false, hasReset = false;
            hasYield = false; aligned = false; builtIn = null;
            foreach (Match m in AttrTok.Matches(tag))
            {
                // Column-aligned (as CMM writes it) or hand-compacted? Any padded column says
                // aligned. Not just the name column: a name as long as the column itself
                // (Ambient_Burnt_Forest_Night) is followed by a single space.
                if (m.Groups["ws"].Value.Length > 1) aligned = true;
                switch (m.Groups["n"].Value)
                {
                    case "name":          nameTok = m; break;
                    case "cool":          coolTok = m; break;
                    case "reentry":       reentryTok = m; break;
                    case "loop":          loopTok = m; break;
                    case "cooldown":      hasCooldown = true; break;
                    case "cooldownreset": hasReset = true; break;
                    case "cooldownyield": hasYield = true; break;
                }
            }
            bool rename = coolTok != null && reentryTok == null;
            if (!rename && hasCooldown && hasReset) return tag;          // nothing to do

            bool al = aligned;
            string Tok(string n, string v)
            {
                string t = n + "=\"" + v + "\"";
                if (!al) return t + " ";
                CMMContextsSchema.AttrWidth.TryGetValue(n, out int w);
                return t.PadRight(Math.Max(w, t.Length)) + " ";
            }

            string name = nameTok?.Groups["v"].Value;
            if (name != null) table.TryGetValue(name, out builtIn);
            string inserts = (hasCooldown ? "" : Tok("cooldown", builtIn != null ? F(builtIn.TrackCooldown) : "0"))
                           + (hasReset ? "" : Tok("cooldownreset", builtIn != null ? B(builtIn.CooldownReset) : "true"));

            if (rename)
            {
                // Rename in place, keeping the value, with the new pair right after it.
                int rest = coolTok.Index + coolTok.Length;               // past its trailing whitespace
                return tag.Substring(0, coolTok.Index)
                     + Tok("reentry", coolTok.Groups["v"].Value)
                     + inserts
                     + tag.Substring(rest);
            }

            // No `cool` to rename: the pair goes after `reentry` if present, else after
            // `loop`, else just before the tag closes.
            Match anchor = reentryTok ?? loopTok;
            if (anchor != null)
            {
                int at = anchor.Index + anchor.Length;
                string sep = anchor.Groups["ws"].Value.Length > 0 ? "" : " ";
                return tag.Substring(0, at) + sep + inserts + tag.Substring(at);
            }
            int end = tag.EndsWith("/>") ? tag.Length - 2 : tag.Length - 1;
            return tag.Substring(0, end).TrimEnd() + " " + inserts + tag.Substring(end);
        }

        // Swap CMM's own header comment (the first comment, if it is ours) for the current one.
        private static string ReplaceHeader(string text, string NL)
        {
            int c0 = text.IndexOf("<!--", StringComparison.Ordinal);
            int root = text.IndexOf("<Contexts", StringComparison.Ordinal);
            if (c0 < 0 || (root >= 0 && c0 > root)) return text;
            int c1 = text.IndexOf("-->", c0 + 4, StringComparison.Ordinal);
            if (c1 < 0) return text;
            string body = text.Substring(c0, c1 - c0);
            if (body.IndexOf("Context definitions for Context Music Manager", StringComparison.Ordinal) < 0)
                return text;
            string fresh = "<!--" + NL + "  "
                         + Sanitize(CMMContextsSchema.Header).Replace("\n", NL + "  ")
                         + NL + "-->";
            return text.Substring(0, c0) + fresh + text.Substring(c1 + 3);
        }

        // New contexts, appended as rendered lines just above </Contexts>.
        private static string AppendContexts(string text, IDictionary<string, Contexts> table,
                                             Func<string, bool> isCombat, List<string> added,
                                             int fileSchema, int currentSchema, string NL)
        {
            int close = text.LastIndexOf("</" + RootName, StringComparison.Ordinal);
            if (close < 0) return text;
            int lineStart = text.LastIndexOf('\n', close) + 1;
            var sb = new StringBuilder();
            sb.Append(NL).Append("  <!-- Added in schema ").Append(currentSchema)
              .Append(" (this file was at ").Append(fileSchema)
              .Append("). Edit freely — CMM will not touch these again. -->").Append(NL);
            foreach (string name in added)
            {
                if (!table.TryGetValue(name, out Contexts c) || c == null) continue;
                sb.Append(RenderContext(c, isCombat(name))).Append(NL);
            }
            return text.Insert(lineStart, sb.ToString());
        }

        // ── Writing (atomic, with the BOM, straight to file) ──────────────────

        private static bool Write(string path, string text)
            => Via(path, tmp => File.WriteAllText(tmp, text, new UTF8Encoding(true)));

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
