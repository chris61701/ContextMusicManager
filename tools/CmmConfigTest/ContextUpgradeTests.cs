using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace CMM
{
    // The contexts.xml schema 1 → 2 upgrade (1.0.5b): `cool` renamed `reentry` keeping its
    // value, `cooldown` + `cooldownreset` added, done TEXT-LEVEL so the player's column
    // alignment survives. Driven against fixtures/contexts.schema1.xml, a real install's
    // file frozen at schema 1, plus hand-written edge-case lines.
    internal static class ContextUpgradeTests
    {
        static int _fail;
        static void Ok(string m)   => Console.WriteLine("  ok    " + m);
        static void Bad(string m) { _fail++; Console.WriteLine("  FAIL  " + m); }
        static void Is(bool c, string m) { if (c) Ok(m); else Bad(m); }

        // The built-in values the upgrade copies in, as InitContexts sets them: every
        // Ambient*, Home* and POI* context except the POI_Cleared stinger rests (-1), and
        // the POI ones reset on leave.
        static bool ShipsCooldown(string n) =>
            (n.StartsWith("Ambient") || n.StartsWith("Home") || n.StartsWith("POI")) && n != "POI_Cleared";
        // cooldownreset defaults to true; Home* and Ambient* ship false (2026-11-07).
        static bool ShipsReset(string n) => !(n.StartsWith("Ambient") || n.StartsWith("Home"));
        static bool ShipsYield(string n) => n == "Home" || n == "Home_Night";

        static Dictionary<string, Contexts> TableFrom(XmlDocument doc)
        {
            var t = new Dictionary<string, Contexts>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode n in doc.DocumentElement.ChildNodes)
            {
                if (n.NodeType != XmlNodeType.Element || n.Name != "Context") continue;
                string name = ((XmlElement)n).GetAttribute("name");
                t[name] = new Contexts(context: name, source: ((XmlElement)n).GetAttribute("source"),
                                      trackCooldown: ShipsCooldown(name) ? -1f : 0f,
                                      cooldownReset: ShipsReset(name),
                                      cooldownYield: ShipsYield(name));
            }
            return t;
        }

        internal static int Run(string repo)
        {
            Console.WriteLine("── contexts.xml schema 2 upgrade " + new string('─', 21));
            string fx = Path.Combine(repo, "tools", "CmmConfigTest", "fixtures", "contexts.schema1.xml");
            if (!File.Exists(fx)) { Bad("fixture missing: " + fx); return _fail; }

            string before = File.ReadAllText(fx);
            var bdoc = new XmlDocument(); bdoc.LoadXml(before);
            var table = TableFrom(bdoc);

            string after = CMMContextsFile.Upgrade(before, table, _ => false, 2, out string summary);
            Is(after != null, "a schema-1 file is upgraded");
            if (after == null) return _fail;
            Console.WriteLine("        " + summary);

            var adoc = new XmlDocument();
            try { adoc.LoadXml(after); Ok("upgraded file parses"); }
            catch (Exception ex) { Bad("upgraded file does not parse: " + ex.Message); return _fail; }
            Is(adoc.DocumentElement.GetAttribute("schema") == "2", "stamped schema=\"2\"");
            Is(CMMContextsFile.SchemaOf(after) == 2, "SchemaOf reads 2 back");

            // Values: every line's cool survives as reentry; nothing else drifts.
            int ctxs = 0, valueDrift = 0, leftCool = 0, cdWrong = 0, rstWrong = 0, yldWrong = 0, otherDrift = 0;
            foreach (XmlNode n in adoc.DocumentElement.ChildNodes)
            {
                if (n.NodeType != XmlNodeType.Element || n.Name != "Context") continue;
                ctxs++;
                var a = (XmlElement)n;
                string name = a.GetAttribute("name");
                var b = (XmlElement)bdoc.DocumentElement.SelectSingleNode($"Context[@name='{name}']");
                if (a.HasAttribute("cool")) leftCool++;
                if (b.GetAttribute("cool") != a.GetAttribute("reentry")) { valueDrift++; Bad($"{name}: cool {b.GetAttribute("cool")} → reentry {a.GetAttribute("reentry")}"); }
                if (a.GetAttribute("cooldown") != (ShipsCooldown(name) ? "-1" : "0")) { cdWrong++; Bad($"{name}: cooldown={a.GetAttribute("cooldown")}"); }
                if (a.GetAttribute("cooldownreset") != (ShipsReset(name) ? "true" : "false")) { rstWrong++; Bad($"{name}: cooldownreset={a.GetAttribute("cooldownreset")}"); }
                if (a.GetAttribute("cooldownyield") != (ShipsYield(name) ? "true" : "false")) { yldWrong++; Bad($"{name}: cooldownyield={a.GetAttribute("cooldownyield")}"); }
                foreach (XmlAttribute att in b.Attributes)
                {
                    if (att.Name == "cool") continue;
                    if (a.GetAttribute(att.Name) != att.Value) { otherDrift++; Bad($"{name}.{att.Name} changed"); }
                }
            }
            Is(ctxs == 71, $"all {ctxs} contexts still present");
            Is(leftCool == 0, "no `cool` attribute left");
            Is(valueDrift == 0, "every cool value carried into reentry");
            Is(cdWrong == 0, "cooldown = -1 on the 28 shipped contexts, 0 elsewhere");
            Is(rstWrong == 0, "cooldownreset = false on Home*/Ambient*, true elsewhere");
            Is(yldWrong == 0, "cooldownyield = true on Home and Home_Night only");
            Is(otherDrift == 0, "no other attribute changed");
            int cdOn = adoc.SelectNodes("//Context[@cooldown='-1']").Count;
            Is(cdOn == 28, $"{cdOn} contexts rest by default (want 28)");
            int rsOn = adoc.SelectNodes("//Context[@cooldownreset='true']").Count;
            int all = adoc.SelectNodes("//Context").Count;
            Is(rsOn == all - 14, $"{rsOn} of {all} contexts reset on leave (want all but the 2 Home + 12 Ambient)");
            int ydOn = adoc.SelectNodes("//Context[@cooldownyield='true']").Count;
            Is(ydOn == 2, $"{ydOn} contexts yield while resting (want 2)");
            int ydAll = adoc.SelectNodes("//Context[@cooldownyield]").Count;
            Is(ydAll == all, $"cooldownyield written on {ydAll} of {all} lines");

            // Alignment: every line's columns after the new pair move right by the SAME amount,
            // so whatever alignment the file had (the shipped one is not perfectly uniform —
            // combat="true" lines sit further right) is exactly what it keeps.
            Func<string, Dictionary<string, int>> fadeCol = s =>
            {
                var d = new Dictionary<string, int>();
                foreach (string line in s.Split('\n'))
                {
                    if (!line.TrimStart().StartsWith("<Context ")) continue;
                    int q0 = line.IndexOf("name=\"", StringComparison.Ordinal) + 6;
                    string nm = line.Substring(q0, line.IndexOf('"', q0) - q0);
                    d[nm] = line.IndexOf("fadeintime=", StringComparison.Ordinal);
                }
                return d;
            };
            var c0 = fadeCol(before); var c1 = fadeCol(after);
            var shifts = new HashSet<int>(c0.Keys.Select(k => c1[k] - c0[k]));
            Is(shifts.Count == 1, shifts.Count == 1 ? $"every line shifted by the same {shifts.First()} columns — alignment kept"
                                                    : "lines shifted unevenly: " + string.Join(",", shifts));

            // Everything outside the header and the <Context> tags is byte-identical: same
            // line count past the header, same non-Context lines in the same order.
            Func<string, List<string>> body = s => s.Replace("\r\n", "\n").Split('\n')
                .SkipWhile(l => !l.StartsWith("<Contexts")).Where(l => !l.TrimStart().StartsWith("<Context "))
                .Select(l => l.StartsWith("<Contexts") ? "<Contexts>" : l).ToList();
            Is(body(before).SequenceEqual(body(after)), "every non-Context line after the header is untouched");
            Is(after.Contains("\r\n") && !after.Replace("\r\n", "").Contains("\n"), "CRLF line endings kept");
            Is(after.Contains("cooldownreset - true = leaving this context"), "header comment documents the new attributes");
            Is(!after.Contains("  cool      - seconds"), "old `cool` doc line is gone from the header");

            // Idempotent.
            Is(CMMContextsFile.Upgrade(after, table, _ => false, 2, out _) == null, "a schema-2 file is left alone");
            Is(CMMContextsFile.Upgrade(after.Replace("schema=\"2\"", "schema=\"3\""), table, _ => false, 2, out _) == null,
               "a NEWER file is left alone");

            EdgeCases(table);
            return _fail;
        }

        static string Wrap(string lines, string root = "<Contexts schema=\"1\">") =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root + "\n" + lines + "\n</Contexts>\n";

        static XmlElement One(string xml, string name)
        {
            var d = new XmlDocument(); d.LoadXml(xml);
            return (XmlElement)d.DocumentElement.SelectSingleNode($"Context[@name='{name}']");
        }

        static void EdgeCases(Dictionary<string, Contexts> builtIn)
        {
            var t = new Dictionary<string, Contexts>(builtIn, StringComparer.OrdinalIgnoreCase);
            const string sentinels = "  <Context name=\"Silence\" source=\"\" />\n  <Context name=\"Vanilla\" source=\"\" />";

            // A hand-compacted line keeps single spacing and its own value.
            string compact = Wrap("  <Context name=\"Mine\" source=\"x\" cool=\"7\" priority=\"3\"/>\n" + sentinels);
            string up = CMMContextsFile.Upgrade(compact, t, _ => false, 2, out _);
            Is(up != null && up.Contains("<Context name=\"Mine\" source=\"x\" reentry=\"7\" cooldown=\"0\" cooldownreset=\"true\" cooldownyield=\"false\" priority=\"3\"/>"),
               "compact player context: reentry=7, cooldown 0, reset true, yield false (the defaults), single-spaced");

            // The player already wrote cooldown (e.g. from the docs) — theirs wins.
            string own = Wrap("  <Context name=\"Ambient\" source=\"ambient\" cool=\"5\" cooldown=\"45\" priority=\"10\"/>\n" + sentinels);
            up = CMMContextsFile.Upgrade(own, t, _ => false, 2, out _);
            var e = up != null ? One(up, "Ambient") : null;
            Is(e != null && e.GetAttribute("cooldown") == "45" && e.GetAttribute("reentry") == "5"
               && e.GetAttribute("cooldownreset") == "false" && e.GetAttribute("cooldownyield") == "false"
               && !e.HasAttribute("cool"),
               "an existing cooldown=\"45\" is kept; the missing reset/yield get Ambient's shipped values");

            // Both names present: reentry wins at load; the line is left as written.
            string both = Wrap("  <Context name=\"X\" source=\"x\" cool=\"9\" reentry=\"2\" cooldown=\"0\" cooldownreset=\"false\"/>\n" + sentinels);
            up = CMMContextsFile.Upgrade(both, t, _ => false, 2, out _);
            Is(up != null && up.Contains("cool=\"9\" reentry=\"2\" cooldown=\"0\" cooldownreset=\"false\" cooldownyield=\"false\""),
               "a line with both cool and reentry keeps both; only the missing yield is added, after the reset");

            // No cool at all (it defaulted): the pair goes after loop.
            string nocool = Wrap("  <Context name=\"Y\" source=\"y\" loop=\"false\" priority=\"1\"/>\n" + sentinels);
            up = CMMContextsFile.Upgrade(nocool, t, _ => false, 2, out _);
            Is(up != null && up.Contains("loop=\"false\" cooldown=\"0\" cooldownreset=\"true\" cooldownyield=\"false\" priority=\"1\""),
               "no cool: cooldown pair inserted after loop");

            // No anchors at all: before the close.
            string bare = Wrap("  <Context name=\"Z\" source=\"z\"/>\n" + sentinels);
            up = CMMContextsFile.Upgrade(bare, t, _ => false, 2, out _);
            e = up != null ? One(up, "Z") : null;
            Is(e != null && e.GetAttribute("cooldown") == "0" && e.GetAttribute("cooldownreset") == "true"
               && e.GetAttribute("cooldownyield") == "false",
               "a bare line gets the three before it closes");

            // Already has all three: untouched (a hand-written schema-1 line from the docs).
            string all3 = Wrap("  <Context name=\"W\" source=\"w\" reentry=\"1\" cooldown=\"0\" cooldownreset=\"true\" cooldownyield=\"true\"/>\n" + sentinels);
            up = CMMContextsFile.Upgrade(all3, t, _ => false, 2, out _);
            Is(up != null && up.Contains("reentry=\"1\" cooldown=\"0\" cooldownreset=\"true\" cooldownyield=\"true\"/>"),
               "a line that already has all three is left as written");

            // A commented-out context is the player's — untouched.
            string commented = Wrap("  <!-- <Context name=\"Old\" source=\"o\" cool=\"4\"/> -->\n" + sentinels);
            up = CMMContextsFile.Upgrade(commented, t, _ => false, 2, out _);
            Is(up != null && up.Contains("<!-- <Context name=\"Old\" source=\"o\" cool=\"4\"/> -->"),
               "a commented-out line is not migrated");

            // Unstamped (pre-schema) file: stamped.
            string unstamped = Wrap("  <Context name=\"Q\" source=\"q\" cool=\"1\"/>\n" + sentinels, "<Contexts>");
            up = CMMContextsFile.Upgrade(unstamped, t, _ => false, 2, out _);
            Is(up != null && up.Contains("<Contexts schema=\"2\">") && One(up, "Q").GetAttribute("reentry") == "1",
               "an unstamped file is migrated and stamped");

            // A '>' inside a value does not end the tag early.
            string gt = Wrap("  <Context name=\"G\" source=\"a>b\" cool=\"6\"/>\n" + sentinels);
            up = CMMContextsFile.Upgrade(gt, t, _ => false, 2, out _);
            Is(up != null && One(up, "G").GetAttribute("reentry") == "6" && One(up, "G").GetAttribute("source") == "a>b",
               "a '>' inside an attribute value is handled");

            // Broken XML is never touched.
            Is(CMMContextsFile.Upgrade("<Contexts schema=\"1\"><Context name=", t, _ => false, 2, out _) == null,
               "a file that does not parse is left alone");

            // A fresh render round-trips through the loader shape with the new attributes.
            var fresh = new Dictionary<string, Contexts>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = new Contexts(context: "A", source: "a", reentry: 4, trackCooldown: 120.5f, cooldownReset: true,
                                     cooldownYield: true),
                ["Silence"] = new Contexts(context: "Silence"),
                ["Vanilla"] = new Contexts(context: "Vanilla"),
            };
            string r = CMMContextsFile.Render(fresh, _ => false, 2);
            e = One(r, "A");
            Is(e.GetAttribute("reentry") == "4" && e.GetAttribute("cooldown") == "120.5" && e.GetAttribute("cooldownreset") == "true"
               && e.GetAttribute("cooldownyield") == "true"
               && !e.HasAttribute("cool"), "a fresh file writes reentry/cooldown/cooldownreset/cooldownyield, not cool");
            Is(new Contexts(trackCooldown: -7f).TrackCooldown == -1f, "any negative cooldown reads as -1 (the default)");
        }
    }
}
