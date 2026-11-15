using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace CMM
{
    // Proves CMMContextsFile can reproduce the contexts.xml that shipped up to 1.0.5.
    //
    // The check is split in two so each half can be trusted on its own:
    //   * a scratchpad diff (see the 3ff8c77 commit message) proved the BUILT-IN TABLE
    //     now holds exactly the values the shipped file holds — 71 vs 71, zero drift.
    //   * this proves the WRITER turns those values back into that file.
    // Together they say render(InitContexts()) == the file that shipped, without this
    // harness needing to instantiate a MonoBehaviour to call InitContexts.
    internal static class ContextTests
    {
        static int _fail;
        static void Ok(string m)   => Console.WriteLine("  ok    " + m);
        static void Bad(string m) { _fail++; Console.WriteLine("  FAIL  " + m); }
        static void Is(bool c, string m) { if (c) Ok(m); else Bad(m); }

        internal static int Run(string repo)
        {
            Console.WriteLine("── contexts.xml " + new string('─', 38));
            string shipped = Path.Combine(repo, "Config", "contexts.xml");
            if (!File.Exists(shipped))
            {
                // Expected once contexts.xml stops shipping; the git history keeps the
                // reference and the schema layout keeps the prose.
                Console.WriteLine("  --    no shipped contexts.xml to compare against");
                return SelfChecks(null);
            }

            var doc = new XmlDocument();
            doc.Load(shipped);
            var table = new Dictionary<string, Contexts>(StringComparer.OrdinalIgnoreCase);
            var combat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            foreach (XmlNode n in doc.DocumentElement.ChildNodes)
            {
                if (n.NodeType != XmlNodeType.Element || n.Name != "Context") continue;
                var el = (XmlElement)n;
                string name = el.GetAttribute("name");
                if (string.IsNullOrEmpty(name)) continue;
                order.Add(name);
                if (el.GetAttribute("combat") == "true") combat.Add(name);
                table[name] = new Contexts(
                    context:               name,
                    source:                el.GetAttribute("source"),
                    fallback:              Str(el, "fallback", "Stack"),
                    canberesumed:          Bool(el, "resume", true),
                    timeuntilcontextstale: Int(el, "stale", 120),
                    loop:                  Bool(el, "loop", false),
                    reentry:               el.HasAttribute("reentry") ? Int(el, "reentry", 5) : Int(el, "cool", 5),
                    fadeInDuration:        Flt(el, "fadeintime", 5f),
                    bypassesreentry:       Bool(el, "bypass", false),
                    priority:              Int(el, "priority", 0),
                    playOnce:              Bool(el, "once", false),
                    volumeOverride:        Flt(el, "volume", -1f),
                    warmup:                Int(el, "warmup", 0),
                    warmGroup:             Str(el, "warmgroup", null),
                    includeSubfolders:     Bool(el, "subfolders", false),
                    link:                  Str(el, "link", null),
                    trackCooldown:         Flt(el, "cooldown", 0f),
                    cooldownReset:         Bool(el, "cooldownreset", true),
                    cooldownYield:         Bool(el, "cooldownyield", false));
            }

            string rendered = CMMContextsFile.Render(table, combat.Contains, CMMContextsSchema.CurrentSchema);

            // 1. well-formed, correct root, stamped
            var rdoc = new XmlDocument();
            try { rdoc.LoadXml(rendered); Ok("rendered output is well-formed XML"); }
            catch (Exception ex) { Bad("rendered output does not parse: " + ex.Message); return _fail; }
            Is(rdoc.DocumentElement.Name == "Contexts", "root element is <Contexts>");
            Is(rdoc.DocumentElement.GetAttribute("schema") == CMMContextsSchema.CurrentSchema.ToString(),
               "schema stamp present");

            // 2. no illegal comment bodies anywhere in the output
            int badc = CountBadComments(rendered);
            Is(badc == 0, badc == 0 ? "no comment contains '--' or ends in '-'"
                                    : badc + " illegal comment(s)");

            // 3. same contexts, same ORDER — order is what makes 71 of these readable
            var rorder = new List<string>();
            foreach (XmlNode n in rdoc.DocumentElement.ChildNodes)
                if (n.NodeType == XmlNodeType.Element && n.Name == "Context")
                    rorder.Add(((XmlElement)n).GetAttribute("name"));
            Is(rorder.Count == order.Count, $"context count {rorder.Count} == shipped {order.Count}");
            Is(rorder.SequenceEqual(order, StringComparer.OrdinalIgnoreCase),
               "contexts render in the shipped order");

            // 4. every attribute of every context round-trips, compared as VALUES
            int drift = 0;
            foreach (XmlNode n in rdoc.DocumentElement.ChildNodes)
            {
                if (n.NodeType != XmlNodeType.Element || n.Name != "Context") continue;
                var rel = (XmlElement)n;
                string name = rel.GetAttribute("name");
                var sel = doc.DocumentElement.SelectSingleNode($"Context[@name='{name}']") as XmlElement;
                if (sel == null) { Bad("no shipped context named " + name); drift++; continue; }
                foreach (string a in CMMContextsSchema.AttrOrder)
                {
                    string want = sel.HasAttribute(a) ? sel.GetAttribute(a) : null;
                    string got  = rel.HasAttribute(a) ? rel.GetAttribute(a) : null;
                    if (Same(a, want, got)) continue;
                    drift++;
                    Bad($"{name}.{a}: shipped '{want ?? "(absent)"}' rendered '{got ?? "(absent)"}'");
                }
            }
            Is(drift == 0, $"all attributes of all {order.Count} contexts reproduced");

            // 5. the comments actually survived
            int shippedComments = CountComments(File.ReadAllText(shipped));
            int renderedComments = CountComments(rendered);
            Is(renderedComments >= shippedComments,
               $"comments preserved ({renderedComments} rendered vs {shippedComments} shipped)");

            // 6. the sentinels the loader refuses to start without
            Is(rorder.Contains("Silence") && rorder.Contains("Vanilla"),
               "required sentinels Silence + Vanilla present");

            return SelfChecks(rendered);
        }

        // Checks that hold with or without a shipped file to diff against.
        static int SelfChecks(string rendered)
        {
            Is(CMMContextsSchema.Layout.Count(e => e.Name != null) > 0, "layout lists contexts");
            var dupes = CMMContextsSchema.Layout.Where(e => e.Name != null)
                        .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                        .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Is(dupes.Count == 0, dupes.Count == 0 ? "no duplicate names in the layout"
                                                  : "duplicate layout names: " + string.Join(", ", dupes));
            return _fail;
        }

        // Compare as the attribute's TYPE: "5" and "5.0" are the same fadeintime, and an
        // absent optional attribute equals an empty one.
        static bool Same(string attr, string a, string b)
        {
            if (attr == "combat" || attr == "link")
                return string.Equals(a ?? "", b ?? "", StringComparison.Ordinal)
                    || (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b));
            if (a == null || b == null)
                return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
            if (float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out float fa)
             && float.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out float fb))
                return fa == fb;
            return string.Equals(a, b, StringComparison.Ordinal);
        }

        static int CountComments(string s)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf("<!--", i, StringComparison.Ordinal)) >= 0) { n++; i += 4; }
            return n;
        }

        static int CountBadComments(string s)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf("<!--", i, StringComparison.Ordinal)) >= 0)
            {
                int e = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (e < 0) break;
                string body = s.Substring(i + 4, e - i - 4);
                if (body.Contains("--") || body.TrimEnd().EndsWith("-")) n++;
                i = e + 3;
            }
            return n;
        }

        static string Str(XmlElement e, string a, string d)
        { string v = e.GetAttribute(a); return string.IsNullOrEmpty(v) ? d : v; }
        static bool Bool(XmlElement e, string a, bool d)
        => bool.TryParse(e.GetAttribute(a), out bool v) ? v : d;
        static int Int(XmlElement e, string a, int d)
        => int.TryParse(e.GetAttribute(a), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : d;
        static float Flt(XmlElement e, string a, float d)
        => float.TryParse(e.GetAttribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : d;
    }
}
