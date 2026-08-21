using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace CMM
{
    // Drives the REAL CMMSettingsFile.Ensure with synthetic schemas, so the
    // create / backfill / migrate rules are tested against the shipping
    // implementation rather than a paraphrase of it.
    internal static class FileTests
    {
        static int _fail;
        static string _dir;

        static void Ok(string m)   => Console.WriteLine("  ok    " + m);
        static void Bad(string m) { _fail++; Console.WriteLine("  FAIL  " + m); }
        static void Is(bool cond, string m) { if (cond) Ok(m); else Bad(m); }

        // ── a tiny schema we control ──────────────────────────────────────────
        // Mirrors the real builders: Since = the schema a setting first shipped in,
        // changedIn/was = the most recent default change.
        static SchemaEntry Set(string name, SettingKind kind, object def,
                               int since = 1, int changedIn = 0, object was = null)
            => new SchemaEntry { Setting = new SettingDef {
                   Name = name, Kind = kind, Target = null, Default = def,
                   Since = since, DefaultChangedIn = changedIn, WasDefault = was } };
        static SchemaEntry Cmt(string t) => new SchemaEntry { Comment = t };
        static SchemaEntry Gap() => new SchemaEntry { Gap = true };

        static string Path_(string n) => System.IO.Path.Combine(_dir, n);
        static string Read(string n) => File.ReadAllText(Path_(n));

        static Dictionary<string, string> Parse(string n)
        {
            var doc = new XmlDocument();
            doc.Load(Path_(n));
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XmlNode x in doc.DocumentElement.ChildNodes)
                if (x.NodeType == XmlNodeType.Element && x.SelectNodes("*").Count == 0)
                    d[x.Name] = x.InnerText;
            return d;
        }

        static int Stamp(string n)
        {
            var doc = new XmlDocument();
            doc.Load(Path_(n));
            return int.TryParse(doc.DocumentElement.GetAttribute("schema"), out int v) ? v : 0;
        }

        internal static int Run()
        {
            _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                          "cmmcfgtest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_dir);
            Console.WriteLine("── file lifecycle " + new string('─', 36));
            Console.WriteLine("  tmp " + _dir);

            var v1 = new[]
            {
                Cmt("How loud, 0..1"),
                Set("Volume", SettingKind.Float, 0.5f),
                Gap(),
                Cmt("Fade seconds"),
                Set("Fade", SettingKind.Float, 2f),
                Set("Enabled", SettingKind.Bool, true),
                Set("Mode", SettingKind.String, "Random"),
            };

            // ── 1. created from nothing ───────────────────────────────────────
            CMMSettingsFile.Ensure(Path_("a.xml"), "CMMSettings", v1, "a.xml", 1);
            Is(File.Exists(Path_("a.xml")), "missing file is created");
            Is(Stamp("a.xml") == 1, "created file carries the schema stamp");
            var vals = Parse("a.xml");
            Is(vals.Count == 4 && vals["Volume"] == "0.5" && vals["Enabled"] == "true",
               "created file holds every default");

            // ── 2. idempotent ─────────────────────────────────────────────────
            string before = Read("a.xml");
            CMMSettingsFile.Ensure(Path_("a.xml"), "CMMSettings", v1, "a.xml", 1);
            Is(Read("a.xml") == before, "re-running at the same schema changes nothing");

            // ── 3. user edits survive; schema 2 adds a setting ────────────────
            File.WriteAllText(Path_("a.xml"), before.Replace("<Volume>0.5</Volume>", "<Volume>0.9</Volume>"));
            var v2 = v1.ToList();
            v2.Add(Gap());
            v2.Add(Cmt("Brand new in v2"));
            v2.Add(Set("NewThing", SettingKind.Int, 7, since: 2));
            CMMSettingsFile.Ensure(Path_("a.xml"), "CMMSettings", v2.ToArray(), "a.xml", 2);
            vals = Parse("a.xml");
            Is(vals["Volume"] == "0.9", "a customised value is not touched by an upgrade");
            Is(vals.ContainsKey("NewThing") && vals["NewThing"] == "7", "a setting added in v2 is backfilled");
            Is(Stamp("a.xml") == 2, "file is re-stamped to the new schema");
            Is(Read("a.xml").Contains("How loud, 0..1"), "existing comments survive the rewrite");
            Is(Read("a.xml").Contains("Brand new in v2"), "the new setting arrives documented");
            Is(Read("a.xml").Contains("Added in schema 2"), "new settings sit under a banner");

            // ── 4. a DELETED key is not resurrected ──────────────────────────
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.Load(Path_("a.xml"));
            doc.DocumentElement.RemoveChild(doc.DocumentElement.SelectSingleNode("Fade"));
            doc.Save(Path_("a.xml"));
            var v3 = v2.ToList();
            v3.Add(Set("Another", SettingKind.Bool, false, since: 3));
            CMMSettingsFile.Ensure(Path_("a.xml"), "CMMSettings", v3.ToArray(), "a.xml", 3);
            vals = Parse("a.xml");
            Is(!vals.ContainsKey("Fade"), "a setting the user deleted stays deleted");
            Is(vals.ContainsKey("Another"), "...while a genuinely new one is still added");

            // ── 5. default migration, option B ───────────────────────────────
            // b.xml starts at schema 1 with Volume left at the old default 0.5 and
            // Fade deliberately customised to 9.
            var mig1 = new[] { Set("Volume", SettingKind.Float, 0.5f), Set("Fade", SettingKind.Float, 2f) };
            CMMSettingsFile.Ensure(Path_("b.xml"), "CMMSettings", mig1, "b.xml", 1);
            File.WriteAllText(Path_("b.xml"), Read("b.xml").Replace("<Fade>2.0</Fade>", "<Fade>9</Fade>"));

            var mig2 = new[]
            {
                Set("Volume", SettingKind.Float, 0.8f, since: 1, changedIn: 2, was: 0.5f),
                Set("Fade",   SettingKind.Float, 4f,   since: 1, changedIn: 2, was: 2f),
            };
            CMMSettingsFile.Ensure(Path_("b.xml"), "CMMSettings", mig2, "b.xml", 2);
            vals = Parse("b.xml");
            Is(vals["Volume"] == "0.8", "untouched setting takes the improved default");
            Is(vals["Fade"] == "9", "customised setting keeps the user's value");

            // ── 6. parsed, not textual, comparison ───────────────────────────
            // "0.50" and " true " are untouched settings written differently. A text
            // compare would call them customised and strand them forever.
            var t1 = new[] { Set("Volume", SettingKind.Float, 0.5f), Set("Enabled", SettingKind.Bool, true) };
            CMMSettingsFile.Ensure(Path_("c.xml"), "CMMSettings", t1, "c.xml", 1);
            File.WriteAllText(Path_("c.xml"), Read("c.xml")
                .Replace("<Volume>0.5</Volume>", "<Volume>0.50</Volume>")
                .Replace("<Enabled>true</Enabled>", "<Enabled> TRUE </Enabled>"));
            var t2 = new[]
            {
                Set("Volume",  SettingKind.Float, 0.7f, since: 1, changedIn: 2, was: 0.5f),
                Set("Enabled", SettingKind.Bool,  false, since: 1, changedIn: 2, was: true),
            };
            CMMSettingsFile.Ensure(Path_("c.xml"), "CMMSettings", t2, "c.xml", 2);
            vals = Parse("c.xml");
            Is(vals["Volume"] == "0.7", "'0.50' reads as the untouched 0.5 and migrates");
            Is(vals["Enabled"] == "false", "' TRUE ' reads as the untouched true and migrates");

            // ── 7. the unstamped file — everyone upgrading from a shipped XML ──
            File.WriteAllText(Path_("d.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<CMMSettings>\r\n"
              + "  <Volume>0.5</Volume>\r\n</CMMSettings>\r\n");
            var d2 = new[]
            {
                Set("Volume", SettingKind.Float, 0.6f, since: 1, changedIn: 2, was: 0.5f),
                Set("Extra",  SettingKind.Int,   3,    since: 2),
            };
            CMMSettingsFile.Ensure(Path_("d.xml"), "CMMSettings", d2, "d.xml", 2);
            vals = Parse("d.xml");
            Is(Stamp("d.xml") == 2, "an unstamped file gets stamped");
            Is(vals["Volume"] == "0.6", "unstamped reads as schema 0, so recorded changes apply");
            Is(vals.ContainsKey("Extra"), "and everything newer than schema 0 is backfilled");

            // ── 8. a corrupt file must not throw or be clobbered ─────────────
            File.WriteAllText(Path_("e.xml"), "this is not xml at all");
            try
            {
                CMMSettingsFile.Ensure(Path_("e.xml"), "CMMSettings", v1, "e.xml", 1);
                Ok("a corrupt file is survived without throwing");
                Is(Read("e.xml") == "this is not xml at all", "...and is left untouched, not clobbered");
            }
            catch (Exception ex) { Bad("corrupt file threw: " + ex.Message); }

            // ── 9. output stays well-formed and re-readable ──────────────────
            foreach (string f in new[] { "a.xml", "b.xml", "c.xml", "d.xml" })
            {
                try { new XmlDocument().Load(Path_(f)); }
                catch (Exception ex) { Bad(f + " is not well-formed after rewrites: " + ex.Message); }
            }
            Ok("every rewritten file still parses");

            try { Directory.Delete(_dir, true); } catch { }
            return _fail;
        }
    }
}
