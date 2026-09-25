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
                // A file at the current schema normally needs nothing — EXCEPT that everything
                // written before the BOM fix is sitting on disk without one, and those only get
                // rewritten if a schema bump happens to come along. Rewrite once for the mark
                // alone so an existing install stops rendering its own comments as mojibake.
                bool needsBom = !HasUtf8Bom(path);
                if (fileSchema == currentSchema && !needsBom) return;   // nothing to do

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
                        && CMMSettingsIO.MatchesAnyFormerDefault(s, current))
                    {
                        // Report what the FILE held, not what the schema expected — with several
                        // former defaults in play those are not the same thing, and the one that
                        // tells you whether the migration did the right thing is the file's.
                        string had = CMMSettingsIO.Format(s, current);
                        el.InnerText = CMMSettingsIO.Format(s, s.Default);
                        migrated.Add($"{s.Name} {had}→{CMMSettingsIO.Format(s, s.Default)}");
                    }
                }

                if (added.Count > 0) AddNewSettings(doc, root, entries, added, currentSchema);
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

        // SLOT EACH NEW SETTING INTO ITS OWN GROUP, documented, where a fresh file would have it.
        //
        // This used to append them all at the END under an "Added in schema N" banner, on the
        // reasoning that nothing the user had would move. Nothing moves this way either -- every
        // existing node stays where it was and new ones are only inserted between them -- but a
        // setting now sits beside the ones it belongs with instead of hundreds of lines away.
        // What the banner answered ("what did this update add?") is kept by a short trailing
        // note on each inserted line.
        //
        // Placed AFTER the nearest earlier schema setting the file still has (and after that
        // setting's own trailing comments). When the file has none of the earlier ones, BEFORE
        // the nearest later one and its documentation. When it has neither, at the end, which
        // is all the old code ever did. Walked in schema order, so a setting inserted a moment
        // ago is the anchor for the next one.
        private static void AddNewSettings(XmlDocument doc, XmlElement root,
                                           SchemaEntry[] entries, List<SettingDef> added,
                                           int currentSchema)
        {
            string indent = DetectIndent(root);
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (SettingDef s in added) wanted.Add(s.Name);

            for (int i = 0; i < entries.Length; i++)
            {
                SettingDef s = entries[i].Setting;
                if (s == null || !wanted.Contains(s.Name)) continue;

                // The span of comments and blank lines between the previous table entry that is
                // a setting (or raw block) and this one: its documentation and its grouping.
                int first = i;
                while (first > 0 && entries[first - 1].Setting == null && entries[first - 1].Raw == null)
                    first--;

                XmlElement el = doc.CreateElement(s.Name);
                el.InnerText = CMMSettingsIO.Format(s, s.Default);

                XmlNode anchor = PreviousPresent(root, entries, i, out int anchorIdx);
                if (anchor != null)
                {
                    // A comment of this span already in the file after the anchor is a trailing
                    // hint of the anchor's (WorldEmitterSource has one) -- step past it, and do not
                    // write it a second time.
                    var present = new HashSet<string>(StringComparer.Ordinal);
                    XmlNode after = SkipTrailing(anchor, entries, anchorIdx, i, present);

                    var nodes = new List<XmlNode>();
                    for (int k = first; k < i; k++)
                    {
                        if (entries[k].Gap) nodes.Add(doc.CreateWhitespace(CMMSettingsWriter.NL));
                        else if (entries[k].Comment != null && !present.Contains(Squash(entries[k].Comment)))
                        {
                            nodes.Add(doc.CreateWhitespace(CMMSettingsWriter.NL + indent));
                            nodes.Add(doc.CreateComment(" " + Flatten(entries[k].Comment, indent) + " "));
                        }
                    }
                    nodes.Add(doc.CreateWhitespace(CMMSettingsWriter.NL + indent));
                    nodes.Add(el);
                    AddNote(doc, nodes, currentSchema);

                    // THE BREAK THAT FOLLOWED THE ANCHOR NOW FOLLOWS US, re-derived: the blank line
                    // (or not) the anchor had before whatever came next was about the anchor and
                    // that, and the new setting sits between them. Its own span above supplies
                    // the break before it; the table decides the one after it. Left alone at the
                    // end of the file, where it is the closing tag's line.
                    var ws = new List<XmlNode>();
                    XmlNode probe = after.NextSibling;
                    while (probe != null && (probe.NodeType == XmlNodeType.Whitespace
                                          || probe.NodeType == XmlNodeType.SignificantWhitespace))
                    { ws.Add(probe); probe = probe.NextSibling; }
                    if (probe != null)
                    {
                        foreach (XmlNode w in ws) root.RemoveChild(w);
                        NextPresent(root, entries, i, out int followIdx);
                        nodes.Add(doc.CreateWhitespace((GapBetween(entries, i, followIdx) ? CMMSettingsWriter.NL : "")
                                                       + CMMSettingsWriter.NL + indent));
                    }
                    foreach (XmlNode n in nodes) { root.InsertAfter(n, after); after = n; }
                    continue;
                }

                // Before everything it follows in the table: put it above the next setting's
                // documentation, not between that setting and its comment.
                var lead = new List<XmlNode>();
                for (int k = first; k < i; k++)
                    if (entries[k].Comment != null)
                    {
                        lead.Add(doc.CreateComment(" " + Flatten(entries[k].Comment, indent) + " "));
                        lead.Add(doc.CreateWhitespace(CMMSettingsWriter.NL + indent));
                    }
                lead.Add(el);
                AddNote(doc, lead, currentSchema);

                XmlNode next = NextPresent(root, entries, i, out int nextIdx);
                if (next != null)
                {
                    XmlNode before = SkipLeading(next, entries, i, nextIdx);
                    lead.Add(doc.CreateWhitespace((GapBetween(entries, i, nextIdx) ? CMMSettingsWriter.NL : "")
                                                  + CMMSettingsWriter.NL + indent));
                    foreach (XmlNode n in lead) root.InsertBefore(n, before);
                    continue;
                }

                // The file holds none of this schema's settings at all. End of the file.
                XmlNode trail = root.LastChild;
                bool haveTrail = trail != null && (trail.NodeType == XmlNodeType.Whitespace
                                                || trail.NodeType == XmlNodeType.SignificantWhitespace);
                lead.Insert(0, doc.CreateWhitespace(CMMSettingsWriter.NL + indent));
                foreach (XmlNode n in lead)
                {
                    if (haveTrail) root.InsertBefore(n, trail);
                    else           root.AppendChild(n);
                }
            }
        }

        // The trailing mark on an inserted line, and how placement recognises one -- so a setting
        // inserted after a line does not land between that line and its own note.
        private const string AddedNote = "added in schema";

        private static void AddNote(XmlDocument doc, List<XmlNode> nodes, int schema)
        {
            nodes.Add(doc.CreateWhitespace(" "));
            nodes.Add(doc.CreateComment(" " + AddedNote + " " + schema + " "));
        }

        // Whether the table puts a blank line anywhere strictly between two positions.
        private static bool GapBetween(SchemaEntry[] entries, int from, int to)
        {
            for (int k = from + 1; k < to && k < entries.Length; k++) if (entries[k].Gap) return true;
            return false;
        }

        private static XmlNode PreviousPresent(XmlElement root, SchemaEntry[] entries, int i, out int idx)
        {
            for (idx = i - 1; idx >= 0; idx--)
            {
                SettingDef s = entries[idx].Setting;
                if (s != null && root.SelectSingleNode(s.Name) is XmlElement el) return el;
            }
            return null;
        }

        private static XmlNode NextPresent(XmlElement root, SchemaEntry[] entries, int i, out int idx)
        {
            for (idx = i + 1; idx < entries.Length; idx++)
            {
                SettingDef s = entries[idx].Setting;
                if (s != null && root.SelectSingleNode(s.Name) is XmlElement el) return el;
            }
            return null;
        }

        // Past the anchor's own note and any schema comments that sit between the anchor and the
        // new setting. Stops at the first thing that belongs to whatever comes next.
        private static XmlNode SkipTrailing(XmlNode anchor, SchemaEntry[] entries, int from, int to,
                                            HashSet<string> present)
        {
            XmlNode after = anchor;
            for (XmlNode n = anchor.NextSibling; n != null; n = n.NextSibling)
            {
                if (n.NodeType == XmlNodeType.Whitespace || n.NodeType == XmlNodeType.SignificantWhitespace)
                    continue;
                if (n.NodeType != XmlNodeType.Comment) break;
                if (IsAddedNote(n)) { after = n; continue; }
                if (!IsSchemaComment(n, entries, from, to)) break;
                present.Add(Squash(n.Value));
                after = n;
            }
            return after;
        }

        // Back from the later setting over its own documentation, so the new setting goes above
        // that whole block.
        private static XmlNode SkipLeading(XmlNode next, SchemaEntry[] entries, int from, int to)
        {
            XmlNode before = next;
            for (XmlNode n = next.PreviousSibling; n != null; n = n.PreviousSibling)
            {
                if (n.NodeType == XmlNodeType.Whitespace || n.NodeType == XmlNodeType.SignificantWhitespace)
                    continue;
                if (n.NodeType != XmlNodeType.Comment || !IsSchemaComment(n, entries, from, to)) break;
                before = n;
            }
            return before;
        }

        private static bool IsAddedNote(XmlNode c)
        {
            string t = c.Value.Trim();
            return t.StartsWith(AddedNote, StringComparison.Ordinal)
                && int.TryParse(t.Substring(AddedNote.Length).Trim(), out _);
        }

        // Whether a comment in the file is one of the schema's own, strictly between two table
        // positions. Compared with all whitespace removed: the file's copy was re-indented when
        // it was written, and an editor may have re-wrapped it since.
        private static bool IsSchemaComment(XmlNode c, SchemaEntry[] entries, int from, int to)
        {
            string have = Squash(c.Value);
            for (int k = from + 1; k < to; k++)
                if (entries[k].Comment != null && Squash(entries[k].Comment) == have) return true;
            return false;
        }

        private static string Squash(string s)
        {
            if (s == null) return "";
            while (s.Contains("--")) s = s.Replace("--", "—");
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s) if (!char.IsWhiteSpace(ch)) sb.Append(ch);
            return sb.ToString();
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
        // WITH THE BOM. These files are UTF-8 and always were — the comments carry box-drawing
        // rules, µ, ×, arrows and en dashes — but written without a byte-order mark a Windows
        // editor has to GUESS the encoding, and it guesses Windows-1252. Every one of those
        // characters then opens as mojibake ("â”€â”€â”€" for a rule), which looks like a corrupted
        // file and is really a correct one being misread.
        //
        // Nothing here reads the mark: XmlDocument.Load handles its presence or absence either
        // way. It exists purely so the file opens correctly in whatever the user double-clicks.
        // Cheap byte peek — no decoding, no exceptions on a missing/locked file.
        private static bool HasUtf8Bom(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    int a = fs.ReadByte(), b = fs.ReadByte(), c = fs.ReadByte();
                    return a == 0xEF && b == 0xBB && c == 0xBF;
                }
            }
            catch { return true; }   // unreadable: do NOT trigger a rewrite on top of it
        }

        private static XmlWriterSettings WriterSettings() => new XmlWriterSettings
        {
            Encoding        = new UTF8Encoding(true),
            NewLineHandling = NewLineHandling.None,
            Indent          = false,
        };

        private static bool WriteAtomicXml(string path, XmlDocument doc, string label)
            => WriteVia(path, label, tmp =>
               {
                   using (XmlWriter w = XmlWriter.Create(tmp, WriterSettings())) doc.Save(w);
               });

        private static bool WriteAtomic(string path, string text, string label)
            => WriteVia(path, label, tmp => File.WriteAllText(tmp, text, new UTF8Encoding(true)));

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
