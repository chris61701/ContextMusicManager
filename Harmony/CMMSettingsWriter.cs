using System;
using System.Collections.Generic;
using System.Text;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // SETTINGS WRITER — renders a config file from the schema.
    //
    // The output has to read like the hand-maintained file it replaces, because
    // that is what it replaces: most of settings.xml is comments, and a user opens
    // it to find out what a setting does. So the schema carries the comments and
    // the blank lines, and this reproduces them in order.
    //
    // Format notes, all deliberate and all matching the files that shipped up to
    // now: CRLF, no BOM, two-space element indent, comment continuation lines
    // aligned under the text of "<!-- " (seven columns), and no XML escaping
    // beyond the four characters that actually need it — a config file full of
    // &quot; is a config file nobody wants to edit.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class CMMSettingsWriter
    {
        internal const string NL = "\r\n";
        private  const string IND = "  ";              // element indent
        private  const string CIND = "       ";        // "  " + "<!-- ".Length

        // Escape only what XML genuinely cannot carry in element text. '>' is legal
        // bare and '"'/'\'' are only special inside attributes, so leaving them alone
        // keeps paths and the door-override strings readable.
        private static string EscapeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.IndexOf('&') < 0 && s.IndexOf('<') < 0) return s;
            return s.Replace("&", "&amp;").Replace("<", "&lt;");
        }

        // A comment body must not contain "--" (illegal inside <!-- -->) and must not
        // end with '-'. This has bitten the project before in contexts.xml, so the
        // writer refuses to emit an invalid file rather than producing one that will
        // not parse on the next launch.
        private static string SanitizeComment(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            // Long box-drawing rules use '─' (U+2500), not '--', so this rarely fires.
            while (s.Contains("--")) s = s.Replace("--", "—");
            if (s.EndsWith("-")) s += " ";
            return s;
        }

        internal static string Render(IEnumerable<SchemaEntry> entries, string rootName, int schema)
        {
            var sb = new StringBuilder(16 * 1024);
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>").Append(NL);
            sb.Append('<').Append(rootName)
              .Append(" schema=\"").Append(schema).Append("\">").Append(NL);

            foreach (SchemaEntry e in entries)
            {
                if (e.Gap) { sb.Append(NL); continue; }

                if (e.Comment != null)
                {
                    string[] lines = SanitizeComment(e.Comment).Replace("\r\n", "\n").Split('\n');
                    sb.Append(IND).Append("<!-- ").Append(lines[0]);
                    for (int i = 1; i < lines.Length; i++)
                        sb.Append(NL).Append(CIND).Append(lines[i]);
                    sb.Append(" -->").Append(NL);
                    continue;
                }

                if (e.Raw != null)
                {
                    string[] lines = e.Raw.Replace("\r\n", "\n").Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                        sb.Append(IND).Append(lines[i]).Append(NL);
                    continue;
                }

                SettingDef s = e.Setting;
                if (s == null) continue;
                sb.Append(IND).Append('<').Append(s.Name).Append('>')
                  .Append(EscapeText(CMMSettingsIO.Format(s, s.Default)))
                  .Append("</").Append(s.Name).Append('>').Append(NL);
            }

            sb.Append("</").Append(rootName).Append('>').Append(NL);
            return sb.ToString();
        }

        internal static string RenderSettings() =>
            Render(CMMSettingsSchema.Settings, "CMMSettings", CMMSettingsSchema.CurrentSchema);

        internal static string RenderRadio() =>
            Render(CMMSettingsSchema.Radio, "CMMRadioSettings", CMMSettingsSchema.CurrentSchema);
    }
}
