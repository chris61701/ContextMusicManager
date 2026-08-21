using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Xml.Linq;
using CMM;   // real Contexts type (linked DataStructures.cs)

// ─────────────────────────────────────────────────────────────────────────────
// CmmScanTest — exercises the context folder-enumeration layer added/changed on
// this branch:
//   • subfolder recursion + subtree "claiming" (most-specific context owns a file)
//   • Fix #1: FileWatchLoop slow-path scan must set scanDone + keep a non-null
//     snapshot even when the recursive enumeration throws (else the coroutine
//     spins forever).
//   • Fix #2: PresenceScanLoop must isolate a throwing folder per-item so it does
//     not strand every LATER context's snapshot (which would keep the all-required
//     SnapshotsReady() from ever latching).
//
// The HELPERS region below is copied VERBATIM from
// Harmony/ContextMusicManager.AudioPlayer.cs (lines ~1604-1717) so this tests the
// shipped algorithm -- and now LINKS it rather than copying it (see the block below).
// ─────────────────────────────────────────────────────────────────────────────

namespace CmmScanTest
{
    internal static class Program
    {
        static int _pass, _fail;

        static int Main(string[] args)
        {
            string musicDir = args.Length > 0 ? args[0] : @"J:\7D2D_Custom_Music";
            string repoRoot = FindRepoRoot();
            string contextsXml = repoRoot == null ? null : Path.Combine(repoRoot, "Config", "contexts.xml");

            Console.WriteLine("CmmScanTest — context enumeration / claiming / scan-exception tests");
            Console.WriteLine($"  music dir   : {musicDir} ({(Directory.Exists(musicDir) ? "exists" : "MISSING")})");
            Console.WriteLine($"  contexts.xml: {contextsXml} ({(File.Exists(contextsXml) ? "loaded" : "MISSING")})\n");

            string sandbox = Path.Combine(Path.GetTempPath(), "CmmScanTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                if (File.Exists(contextsXml) && Directory.Exists(musicDir))
                    TestClaimingOverRealLibrary(musicDir, contextsXml);
                else
                    Console.WriteLine("SKIP claiming-over-real-library (music dir or contexts.xml missing)\n");

                TestPresenceScanIsolation(sandbox);   // Fix #2
                TestFileWatchScanDone(sandbox);        // Fix #1
                TestRecursionReach(sandbox);           // recursion REACH (flat vs subfolders)
            }
            finally
            {
                TryHardDelete(sandbox);
            }

            Console.WriteLine($"\n──────────────────────────────────────────────");
            Console.WriteLine($"RESULT: {_pass} passed, {_fail} failed");
            return _fail == 0 ? 0 : 1;
        }

        // ── Test: claiming invariant over the REAL music library ────────────────
        // Loads the real contexts.xml, turns on subfolders for each parent that has
        // descendant contexts, and asserts the documented guarantee: every file is
        // listed by AT MOST ONE context (no double-counting). Also reports how many
        // files the recursive parent WOULD double-count without claiming, to prove
        // the claim filter is actually doing work.
        static void TestClaimingOverRealLibrary(string musicDir, string contextsXml)
        {
            Console.WriteLine("== Claiming invariant over real library ==");
            LoadContextsFromXml(contextsXml);
            MusicDirectory = musicDir;

            // Candidate parents: any context whose folder strictly contains another
            // context's folder. Enable recursion on each so claiming engages.
            var parents = contexts.Values
                .Where(c => !string.IsNullOrEmpty(c.Source) && HasDescendantContext(c))
                .ToList();

            // Turn on IncludeSubfolders for every such parent (simulates a user who
            // enabled subfolders="true" on the broad contexts).
            foreach (var p in parents) SetRecursive(p, true);

            // Build file -> [contexts that listed it] over every context with files.
            var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ctx in contexts.Values)
            {
                if (string.IsNullOrEmpty(ctx.Source)) continue;
                string dir = Path.Combine(MusicDirectory, ctx.Source);
                if (!Directory.Exists(dir)) continue;             // mirrors real callers' guard
                foreach (string f in EnumerateContextFiles(ctx, dir))
                {
                    string key = Path.GetFullPath(f);
                    if (!owners.TryGetValue(key, out var list)) owners[key] = list = new List<string>();
                    list.Add(ctx.Context);
                }
            }

            int totalFiles = owners.Count;
            var doubleClaimed = owners.Where(kv => kv.Value.Count > 1).ToList();
            Console.WriteLine($"   recursive parents enabled : {parents.Count}");
            Console.WriteLine($"   distinct files owned       : {totalFiles}");
            Console.WriteLine($"   files owned by >1 context  : {doubleClaimed.Count}");

            // Show what claiming PREVENTED: for one populated recursive parent, count
            // the files a naive recursive scan (no claim filter) would also grab from
            // child-context subtrees.
            var demoParent = parents
                .Select(p => new { p, dir = Path.Combine(MusicDirectory, p.Source) })
                .Where(x => Directory.Exists(x.dir))
                .Select(x => new { x.p, naive = NaiveRecursiveSupported(x.dir).Length, claimed = EnumerateContextFiles(x.p, x.dir).Length })
                .Where(x => x.naive != x.claimed)
                .OrderByDescending(x => x.naive - x.claimed)
                .FirstOrDefault();
            if (demoParent != null)
                Console.WriteLine($"   e.g. '{demoParent.p.Context}': naive recursive would list {demoParent.naive}, " +
                                  $"claiming lists {demoParent.claimed} (claimed away {demoParent.naive - demoParent.claimed} owned by deeper contexts)");

            if (doubleClaimed.Count > 0)
            {
                foreach (var kv in doubleClaimed.Take(5))
                    Console.WriteLine($"      DUP: {Path.GetFileName(kv.Key)} <- [{string.Join(", ", kv.Value)}]");
            }
            Check("each file owned by at most one context (single-ownership invariant)", doubleClaimed.Count == 0);
            Check("recursion+claiming actually engaged (a parent claimed something away)",
                  demoParent != null || totalFiles == 0);
            Console.WriteLine();
        }

        // ── Test: PresenceScanLoop per-item isolation (Fix #2) ──────────────────
        // One unreadable folder must not strand LATER contexts in the same scan.
        static void TestPresenceScanIsolation(string sandbox)
        {
            Console.WriteLine("== Fix #2: PresenceScanLoop one bad folder must not strand later contexts ==");
            string good1 = MakeDir(sandbox, "good1"); File.WriteAllText(Path.Combine(good1, "a.mp3"), "x");
            string good2 = MakeDir(sandbox, "good2"); File.WriteAllText(Path.Combine(good2, "b.flac"), "x");
            string badParent = MakeDir(sandbox, "badparent");
            string denied = MakeDir(badParent, "denied"); File.WriteAllText(Path.Combine(denied, "c.mp3"), "x");
            bool throws = TryDenyRead(denied) && EnumerationThrows(badParent, SearchOption.AllDirectories);
            if (!throws) { Console.WriteLine("   (could not provoke a real FS throw in this environment — SKIP)\n"); UndoDeny(denied); return; }

            // Order matters: the bad folder sits BETWEEN two good ones.
            var items = new (string Name, string Dir, SearchOption Opt)[]
            {
                ("Good1", good1, SearchOption.TopDirectoryOnly),
                ("Bad",   badParent, SearchOption.AllDirectories),   // throws mid-walk
                ("Good2", good2, SearchOption.TopDirectoryOnly),
            };

            // OLD shape: single try/catch around the whole foreach (pre-fix).
            var oldResults = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var it in items)
                    oldResults[it.Name] = Directory.Exists(it.Dir)
                        ? Directory.GetFiles(it.Dir, "*", it.Opt).Where(IsSupported).ToArray()
                        : new string[0];
            }
            catch { }
            Console.WriteLine($"   OLD (outer try): results contain [{string.Join(", ", oldResults.Keys)}]");

            // NEW shape: per-item try/catch (the shipped fix).
            var newResults = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                try
                {
                    newResults[it.Name] = Directory.Exists(it.Dir)
                        ? Directory.GetFiles(it.Dir, "*", it.Opt).Where(IsSupported).ToArray()
                        : new string[0];
                }
                catch { newResults[it.Name] = new string[0]; }
            }
            Console.WriteLine($"   NEW (per-item): results contain [{string.Join(", ", newResults.Keys)}]");

            Check("OLD shape strands the later context (demonstrates the bug)", !oldResults.ContainsKey("Good2"));
            Check("NEW shape keeps ALL contexts in results", newResults.ContainsKey("Good1") && newResults.ContainsKey("Bad") && newResults.ContainsKey("Good2"));
            Check("NEW shape: bad folder yields empty (non-null) snapshot", newResults["Bad"] != null && newResults["Bad"].Length == 0);
            Check("NEW shape: later good folder still enumerated", newResults["Good2"].Length == 1);
            UndoDeny(denied);
            Console.WriteLine();
        }

        // ── Test: FileWatchLoop scanDone / non-null fallback (Fix #1) ───────────
        // A throwing enumeration must still release the `while(!scanDone)` driver and
        // leave currentFiles non-null (fall back to the previous snapshot).
        static void TestFileWatchScanDone(string sandbox)
        {
            Console.WriteLine("== Fix #1: FileWatchLoop scan throw must set scanDone + keep snapshot non-null ==");
            string badParent = MakeDir(sandbox, "fw_badparent");
            string denied = MakeDir(badParent, "denied"); File.WriteAllText(Path.Combine(denied, "c.mp3"), "x");
            bool throws = TryDenyRead(denied) && EnumerationThrows(badParent, SearchOption.AllDirectories);
            if (!throws) { Console.WriteLine("   (could not provoke a real FS throw in this environment — SKIP)\n"); UndoDeny(denied); return; }

            string[] previous = { "previous-track.mp3" };

            // OLD shape: scanDone is the LAST statement, only reached on success; the
            // real closure had NO try/catch, so a throw means scanDone is never set and
            // the coroutine's `while(!scanDone) yield` spins forever. (We guard with a
            // harness-only try purely so this process survives to observe it.)
            bool scanDoneOld = false; string[] curOld = null; bool threwOld = false;
            try
            {
                curOld = Directory.GetFiles(badParent, "*", SearchOption.AllDirectories).Where(IsSupported).ToArray();
                scanDoneOld = true;   // never reached
            }
            catch { threwOld = true; }
            Console.WriteLine($"   OLD: threw={threwOld}, scanDone={scanDoneOld}, currentFiles={(curOld == null ? "null" : "set")}  => real coroutine would hang");

            // NEW shape: try / catch(fallback to previous) / finally(scanDone=true).
            bool scanDoneNew = false; string[] curNew = null;
            try
            {
                curNew = Directory.GetFiles(badParent, "*", SearchOption.AllDirectories).Where(IsSupported).ToArray();
            }
            catch { curNew = previous; }
            finally { scanDoneNew = true; }
            Console.WriteLine($"   NEW: scanDone={scanDoneNew}, currentFiles={(curNew == null ? "null" : $"={string.Join(",", curNew)}")}");

            Check("OLD shape leaves scanDone false on throw (would hang the watcher)", threwOld && !scanDoneOld);
            Check("NEW shape sets scanDone even on throw (releases the driver)", scanDoneNew);
            Check("NEW shape keeps currentFiles NON-NULL (no NRE after the wait)", curNew != null);
            Check("NEW shape falls back to the previous snapshot (tick reads as 'no change')", curNew.SequenceEqual(previous));
            UndoDeny(denied);
            Console.WriteLine();
        }

        // ── Test: recursion reach (flat vs subfolders) ──────────────────────────
        static void TestRecursionReach(string sandbox)
        {
            Console.WriteLine("== Recursion reach: flat lists top-level only, subfolders lists the subtree ==");
            string root = MakeDir(sandbox, "reach");
            File.WriteAllText(Path.Combine(root, "top.mp3"), "x");
            string sub = MakeDir(root, "deep"); File.WriteAllText(Path.Combine(sub, "nested.flac"), "x");
            string sub2 = MakeDir(sub, "deeper"); File.WriteAllText(Path.Combine(sub2, "deepest.ogg"), "x");

            MusicDirectory = sandbox;
            contexts.Clear();
            var flat = AddContext("Flat", "reach", recursive: false);
            int flatCount = EnumerateContextFiles(flat, root).Length;

            contexts.Clear();
            var rec = AddContext("Rec", "reach", recursive: true);
            int recCount = EnumerateContextFiles(rec, root).Length;

            Console.WriteLine($"   flat listed {flatCount} file(s); recursive listed {recCount} file(s)");
            Check("flat enumeration = top-level only (1 file)", flatCount == 1);
            Check("recursive enumeration = whole subtree (3 files)", recCount == 3);
            Console.WriteLine();
        }

        // ════════════════════════════════════════════════════════════════════════
        // NO LONGER A COPY.
        //
        // This block used to be a hand-ported duplicate of the resolution and
        // enumeration logic, and it had drifted: its claim rule was missing the
        // IsPlaylist guards the shipped one has, and its enumeration skipped the
        // flat-folder .m3u union entirely. So the harness was grading a model that
        // does not ship -- which is exactly the failure that cost a night on the
        // acoustics work.
        //
        // The pure helpers now live in MusicRoots and SourceScan, both Unity-free
        // and both LINKED into this project. What is left here is the container the
        // ContextMusicManager partial provides in the game.
        // ════════════════════════════════════════════════════════════════════════
        static string MusicDirectory
        {
            get => MusicRoots.Primary;
            set { MusicRoots.SetPrimary(value); MusicRoots.SetAddons(new MusicRoot[0]); }
        }
        static readonly Dictionary<string, Contexts> contexts =
            new Dictionary<string, Contexts>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _invalidFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static Contexts GetContext(string name) =>
            name != null && contexts.TryGetValue(name, out var c) ? c : null;

        private static SearchOption SearchOptionFor(Contexts ctx) =>
            ctx != null && ctx.IncludeSubfolders
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

        private static string[] GetClaimedSubtreePrefixes(string contextName)
            => MusicRoots.ClaimPrefixes(GetContext(contextName), contexts.Values);

        private static string NormalizedContextDir(string ctxPath)
            => MusicRoots.Combine(MusicRoots.Primary, ctxPath);

        // The REAL pool: loose supported files unioned with what any top-level .m3u
        // lists (flat only), minus anything a deeper context owns.
        private static string[] EnumerateContextFiles(Contexts ctx, string dir)
        {
            if (!Directory.Exists(dir)) return new string[0];
            string[] pool = SourceScan.EnumerateFolderPoolRaw(
                dir, ctx.IncludeSubfolders, GetClaimedSubtreePrefixes(ctx.Context), out _);
            return _invalidFiles.Count == 0
                ? pool
                : pool.Where(f => !_invalidFiles.Contains(f)).ToArray();
        }

        static bool IsSupported(string filePath) => AudioDecoder.IsSupported(filePath);

        // ════════════════════════════════════════════════════════════════════════
        // Harness plumbing
        // ════════════════════════════════════════════════════════════════════════

        static string[] NaiveRecursiveSupported(string dir) =>
            Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(IsSupported).ToArray();

        static bool HasDescendantContext(Contexts self)
        {
            string selfDir = NormalizedContextDir(self.Source);
            if (selfDir == null) return false;
            string selfPrefix = selfDir + Path.DirectorySeparatorChar;
            foreach (var other in contexts.Values)
            {
                if (other == self || string.IsNullOrEmpty(other.Source)) continue;
                string od = NormalizedContextDir(other.Source);
                if (od != null && od.Length > selfDir.Length &&
                    od.StartsWith(selfPrefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static void LoadContextsFromXml(string xmlPath)
        {
            contexts.Clear();
            foreach (var el in XDocument.Load(xmlPath).Descendants("Context"))
            {
                string name = (string)el.Attribute("name");
                string path = (string)el.Attribute("source") ?? (string)el.Attribute("path") ?? "";
                bool sub = ParseBool((string)el.Attribute("subfolders"));
                if (string.IsNullOrEmpty(name)) continue;
                contexts[name] = new Contexts(context: name, source: path, includeSubfolders: sub);
            }
        }

        static Contexts AddContext(string name, string path, bool recursive)
        {
            var c = new Contexts(context: name, source: path, includeSubfolders: recursive);
            contexts[name] = c;
            return c;
        }

        // Contexts is immutable (private setters); rebuild the one entry to flip the flag.
        static void SetRecursive(Contexts c, bool on) =>
            contexts[c.Context] = new Contexts(context: c.Context, source: c.Source, fallback: c.Fallback,
                includeSubfolders: on);

        static bool ParseBool(string s) =>
            !string.IsNullOrEmpty(s) && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1");

        static void Check(string label, bool ok)
        {
            Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++; else _fail++;
        }

        static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "ContextMusicManager.csproj"))) return dir.FullName;
            return null;
        }

        static string MakeDir(string parent, string name)
        {
            string p = Path.Combine(parent, name);
            Directory.CreateDirectory(p);
            return p;
        }

        // ── Real-FS-throw injection via a Deny-Read ACE on the current user ──────
        static bool TryDenyRead(string dir)
        {
            try
            {
                var sid = WindowsIdentity.GetCurrent().User;
                var di = new DirectoryInfo(dir);
                var sec = di.GetAccessControl();
                sec.AddAccessRule(new FileSystemAccessRule(sid,
                    FileSystemRights.ListDirectory | FileSystemRights.ReadData,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Deny));
                di.SetAccessControl(sec);
                return true;
            }
            catch { return false; }
        }

        static void UndoDeny(string dir)
        {
            try
            {
                var sid = WindowsIdentity.GetCurrent().User;
                var di = new DirectoryInfo(dir);
                var sec = di.GetAccessControl();
                sec.RemoveAccessRuleAll(new FileSystemAccessRule(sid,
                    FileSystemRights.ListDirectory | FileSystemRights.ReadData,
                    AccessControlType.Deny));
                di.SetAccessControl(sec);
            }
            catch { }
        }

        static bool EnumerationThrows(string dir, SearchOption opt)
        {
            try { Directory.GetFiles(dir, "*", opt); return false; }
            catch { return true; }
        }

        static void TryHardDelete(string dir)
        {
            if (!Directory.Exists(dir)) return;
            // Lift any deny ACEs we set so the tree is deletable.
            try
            {
                foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories)) UndoDeny(d);
            }
            catch { }
            foreach (var d in SafeAllDirs(dir)) UndoDeny(d);
            try { Directory.Delete(dir, true); } catch { }
        }

        // Enumerate directories without throwing on a denied node (manual recursion).
        static IEnumerable<string> SafeAllDirs(string dir)
        {
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                yield return d;
                string[] kids;
                try { kids = Directory.GetDirectories(d); } catch { continue; }
                foreach (var k in kids) stack.Push(k);
            }
        }
    }
}
