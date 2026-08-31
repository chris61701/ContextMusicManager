using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace CMM
{
    // Renders both config files from the REAL schema + writer and checks them
    // against the files that shipped. Exit code 0 = safe to stop shipping the XML.
    internal static class Program
    {
        static int _fail;

        static void Fail(string msg)   { _fail++; Console.WriteLine("  FAIL  " + msg); }
        static void Pass(string msg)   { Console.WriteLine("  ok    " + msg); }

        static int Main(string[] args)
        {
            string repo = args.Length > 0 ? args[0] : FindRepo();
            if (repo == null) { Console.WriteLine("repo root not found"); return 2; }
            string cfg = Path.Combine(repo, "Config");

            Console.WriteLine("CmmConfigTest — repo " + repo);
            Console.WriteLine();

            Check("settings.xml", CMMSettingsWriter.RenderSettings(),
                  Path.Combine(cfg, "settings.xml"), "CMMSettings", CMMSettingsSchema.Settings);
            Console.WriteLine();
            Check("radio.xml", CMMSettingsWriter.RenderRadio(),
                  Path.Combine(cfg, "radio.xml"), "CMMRadioSettings", CMMSettingsSchema.Radio);

            Console.WriteLine();
            _fail += FileTests.Run();

            Console.WriteLine();
            _fail += SchemaGuard.Run(Path.Combine(repo, "tools", "CmmConfigTest"),
                                     args.Contains("--accept"));

            Console.WriteLine();
            _fail += ContextTests.Run(repo);
            _fail += UpgradeMatrix.Run(repo);
            Console.WriteLine();
            _fail += RealUpgrade.Run(repo);

            Console.WriteLine();
            _fail += XmlHealth.Run(repo);

            Console.WriteLine();
            if (args.Contains("--dump"))
            {
                string outDir = Path.Combine(Path.GetTempPath(), "cmmconfigtest");
                Directory.CreateDirectory(outDir);
                File.WriteAllText(Path.Combine(outDir, "settings.xml"), CMMSettingsWriter.RenderSettings());
                File.WriteAllText(Path.Combine(outDir, "radio.xml"), CMMSettingsWriter.RenderRadio());
                Console.WriteLine("rendered files written to " + outDir);
            }

            Console.WriteLine(_fail == 0 ? "ALL CHECKS PASSED" : _fail + " CHECK(S) FAILED");
            return _fail == 0 ? 0 : 1;
        }

        static void Check(string label, string rendered, string shippedPath,
                          string rootName, SchemaEntry[] entries)
        {
            Console.WriteLine("── " + label + " " + new string('─', Math.Max(0, 50 - label.Length)));

            // 1. the rendered text must be well-formed XML at all
            var rdoc = new XmlDocument();
            try { rdoc.LoadXml(rendered); Pass("rendered output is well-formed XML"); }
            catch (Exception ex) { Fail("rendered output does not parse: " + ex.Message); return; }

            if (rdoc.DocumentElement.Name != rootName)
                Fail($"root is <{rdoc.DocumentElement.Name}>, expected <{rootName}>");
            else Pass($"root element is <{rootName}>");

            // 2. the schema stamp must be there — the whole upgrade path depends on it
            string stamp = rdoc.DocumentElement.GetAttribute("schema");
            if (stamp == CMMSettingsSchema.CurrentSchema.ToString()) Pass("schema stamp = " + stamp);
            else Fail($"schema stamp is '{stamp}', expected {CMMSettingsSchema.CurrentSchema}");

            // 3. no illegal comment bodies (the '--' trap that has broken contexts.xml twice)
            int badComments = 0;
            foreach (XmlNode n in rdoc.DocumentElement.ChildNodes)
                if (n.NodeType == XmlNodeType.Comment && (n.Value.Contains("--") || n.Value.TrimEnd().EndsWith("-")))
                    badComments++;
            if (badComments == 0) Pass("no comment contains '--' or ends in '-'");
            else Fail(badComments + " comment(s) illegal inside <!-- -->");

            // 4. every schema setting appears exactly once, with its default
            var rendVals = Values(rdoc);
            var schemaSettings = entries.Where(e => e.Setting != null).Select(e => e.Setting).ToList();
            int mismatched = 0, absent = 0;
            foreach (SettingDef s in schemaSettings)
            {
                if (!rendVals.TryGetValue(s.Name, out string txt)) { absent++; Fail("missing from render: " + s.Name); continue; }
                string want = CMMSettingsIO.Format(s, s.Default);
                if (!SameText(s, txt, want)) { mismatched++; Fail($"{s.Name}: rendered '{txt}', default '{want}'"); }
            }
            if (absent == 0 && mismatched == 0)
                Pass($"all {schemaSettings.Count} settings rendered with their default");

            // 5. duplicates would make the loader's first-wins silently ambiguous
            var dupes = schemaSettings.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                                      .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count == 0) Pass("no duplicate setting names");
            else Fail("duplicate names: " + string.Join(", ", dupes));

            // 6. against the file that shipped — the real question
            if (!File.Exists(shippedPath)) { Console.WriteLine("  --    no shipped file to compare"); return; }
            var sdoc = new XmlDocument();
            sdoc.Load(shippedPath);
            var shipVals = Values(sdoc);

            var byName = schemaSettings.ToDictionary(s => s.Name, StringComparer.Ordinal);
            int drift = 0;
            var added = new List<string>();
            var removed = new List<string>();

            foreach (var kv in shipVals)
            {
                if (!rendVals.ContainsKey(kv.Key)) { removed.Add(kv.Key); continue; }
                if (!byName.TryGetValue(kv.Key, out SettingDef s)) continue;   // raw block
                if (!SameText(s, kv.Value, rendVals[kv.Key]))
                {
                    drift++;
                    Fail($"{kv.Key}: shipped '{kv.Value}' but renders '{rendVals[kv.Key]}'");
                }
            }
            foreach (var kv in rendVals)
                if (!shipVals.ContainsKey(kv.Key)) added.Add(kv.Key);

            if (drift == 0) Pass($"all {shipVals.Count} shipped values reproduced exactly");
            if (removed.Count > 0) Fail("dropped from the shipped file: " + string.Join(", ", removed));
            else Pass("nothing the shipped file had was dropped");
            if (added.Count > 0)
                Console.WriteLine("  note  newly documented (were code-only): " + string.Join(", ", added));

            // 7. the raw block survived with its children
            foreach (SchemaEntry e in entries.Where(x => x.Raw != null))
            {
                string tag = e.Raw.Substring(1, e.Raw.IndexOf('>') - 1);
                XmlNode rn = rdoc.DocumentElement.SelectSingleNode(tag);
                XmlNode sn = sdoc.DocumentElement.SelectSingleNode(tag);
                int rc = rn?.ChildNodes.Cast<XmlNode>().Count(c => c.NodeType == XmlNodeType.Element) ?? -1;
                int sc = sn?.ChildNodes.Cast<XmlNode>().Count(c => c.NodeType == XmlNodeType.Element) ?? -1;
                if (rc == sc && rc > 0) Pass($"<{tag}> block intact ({rc} children)");
                else Fail($"<{tag}> block: rendered {rc} children, shipped {sc}");
            }
        }

        // Compare as the setting's own TYPE, not as text: "0.30" and "0.3" are the
        // same float and must not read as drift. This is the same rule the upgrade
        // path uses to decide whether a user customised a value.
        static bool SameText(SettingDef s, string a, string b)
        {
            if (s == null) return string.Equals(a?.Trim(), b?.Trim(), StringComparison.Ordinal);
            switch (s.Kind)
            {
                case SettingKind.Float:
                    return float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out float fa)
                        && float.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out float fb)
                        && fa == fb;
                case SettingKind.Int:
                    return int.TryParse(a, out int ia) && int.TryParse(b, out int ib) && ia == ib;
                case SettingKind.Bool:
                    return bool.TryParse(a, out bool ba) && bool.TryParse(b, out bool bb) && ba == bb;
                default:
                    return string.Equals(a?.Trim(), b?.Trim(), StringComparison.Ordinal);
            }
        }

        static Dictionary<string, string> Values(XmlDocument doc)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XmlNode n in doc.DocumentElement.ChildNodes)
                if (n.NodeType == XmlNodeType.Element && n.SelectNodes("*").Count == 0)
                    d[n.Name] = n.InnerText;
            return d;
        }

        static string FindRepo()
        {
            string d = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && d != null; i++)
            {
                if (File.Exists(Path.Combine(d, "ContextMusicManager.csproj"))) return d;
                d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar));
            }
            return null;
        }
    }
}
