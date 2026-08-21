using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // Every hand-edited XML file in the repo must actually parse.
    //
    // This exists because one specific mistake keeps recurring: '--' is illegal
    // inside an XML comment, and it is the most natural thing in the world to type
    // as a dash in prose. It has broken Config/contexts.xml twice and, while this
    // very guard was being written, ContextMusicManager.csproj as well — that one
    // shipped in a commit, because the edit was a comment change and comment
    // changes do not feel like they need a rebuild.
    //
    // MSBuild's failure mode is what makes it expensive: the project will not load
    // AT ALL, so the error is "MSB4025 line 39" rather than anything about the mod.
    //
    // The CMM writer already sanitizes what IT emits (CMMSettingsWriter), but these
    // files are written by hand, so they need checking rather than escaping.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class XmlHealth
    {
        // third_party is vendored upstream source (the opus tree carries IETF drafts that
        // use HTML entities and are not well-formed XML). Not ours to fix or to gate on.
        static readonly string[] SkipDirs =
            { "bin", "obj", "dist", "packages", ".git", ".vs", "temp", "third_party", "node_modules" };

        internal static int Run(string repo)
        {
            Console.WriteLine("── xml health " + new string('─', 40));
            int fail = 0, checkedCount = 0;

            foreach (string path in Files(repo))
            {
                checkedCount++;
                string rel = path.Substring(repo.Length).TrimStart(Path.DirectorySeparatorChar);
                try
                {
                    var doc = new XmlDocument();
                    doc.Load(path);
                }
                catch (Exception ex)
                {
                    fail++;
                    Console.WriteLine($"  FAIL  {rel}: {ex.Message}");
                    continue;
                }

                // Parsed fine, but warn on the shape that is one edit away from breaking:
                // a comment ending in '-' is legal only because of the space before '-->'.
                foreach (string note in RiskyComments(path))
                {
                    Console.WriteLine($"  note  {rel}: {note}");
                }
            }

            if (fail == 0) Console.WriteLine($"  ok    all {checkedCount} XML file(s) parse");
            return fail;
        }

        static IEnumerable<string> Files(string repo)
        {
            var pats = new[] { "*.xml", "*.csproj", "*.props", "*.targets", "*.config" };
            foreach (string pat in pats)
                foreach (string p in Directory.GetFiles(repo, pat, SearchOption.AllDirectories))
                {
                    string rel = p.Substring(repo.Length);
                    if (SkipDirs.Any(d => rel.IndexOf(Path.DirectorySeparatorChar + d,
                                                      StringComparison.OrdinalIgnoreCase) >= 0
                                       || rel.TrimStart(Path.DirectorySeparatorChar)
                                             .StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    yield return p;
                }
        }

        static IEnumerable<string> RiskyComments(string path)
        {
            string text;
            try { text = File.ReadAllText(path); } catch { yield break; }
            int i = 0;
            while ((i = text.IndexOf("<!--", i, StringComparison.Ordinal)) >= 0)
            {
                int end = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (end < 0) yield break;
                string body = text.Substring(i + 4, end - i - 4);
                if (body.EndsWith("-"))
                {
                    int line = text.Take(i).Count(c => c == '\n') + 1;
                    yield return $"line {line}: comment ends in '-' — one deleted space from MSB4025";
                }
                i = end + 3;
            }
        }
    }
}
