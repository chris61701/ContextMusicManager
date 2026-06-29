using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CMM;   // real Contexts type (linked DataStructures.cs)

// ─────────────────────────────────────────────────────────────────────────────
// CmmScaleBench — "hundreds of tracks in every context folder" stall finder.
//
// Generates a synthetic music library from the real Config/contexts.xml (one
// folder per context, --files tracks each, valid audio extensions) and measures
// the MAIN-THREAD work that scales with folder size — the part Unity runs on the
// game thread, where a stall becomes a frame hitch:
//
//   DISK  (against the generated tree)
//     • Presence rebuild   — RebuildContextsWithFiles (synchronous, mod-load).
//     • Recursive signature — ContextWriteSignatureUtc over the whole subtree
//                             (the FileWatchLoop mtime fast-path for subfolders).
//     • Recursive enumerate — EnumerateContextFiles(AllDirectories) (crawler /
//                             repoint full walk).
//
//   CPU   (in-memory, swept across folder sizes — no disk, so it isolates the
//          per-tick / per-pick managed cost from raw IO)
//     • PickNextFile  — the shipped selection: RandomNoRepeat does
//                       files.Where(f => f != avoid).ToArray() EVERY pick, plus
//                       an _invalidFiles filter copy. O(files) alloc per track.
//     • FileWatch diff — Select(norm).ToArray() ×2 + Except ×2, the post-scan
//                       work FileWatchLoop does ON THE MAIN THREAD (only the
//                       Directory.GetFiles is offloaded). norm = Path.GetFullPath.
//
// Algorithms are copied VERBATIM from Harmony/ContextMusicManager.AudioPlayer.cs
// and ContextMusicManager.cs (cited per-block), same convention as CmmScanTest.
//
//   dotnet run --project tools/CmmScaleBench -c Release -- [options]
//     --files N          tracks generated per context folder (default 300)
//     --sweep a,b,c      in-memory CPU sweep sizes (default 100,300,1000,3000,10000)
//     --invalid P        % of files to mark blacklisted for the _invalidFiles path (default 0)
//     --recursive        also enable subfolders on parents-of-contexts (combat/poi/…)
//                        and run the recursive disk paths
//     --out DIR          where to generate the tree (default: a temp dir, deleted after)
//     --keep             don't delete the generated tree
// ─────────────────────────────────────────────────────────────────────────────

namespace CmmScaleBench
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            AppDomain.MonitoringIsEnabled = true;

            int filesPerFolder = 300;
            int[] sweep = { 100, 300, 1000, 3000, 10000 };
            double invalidPct = 0;
            bool recursive = false, keep = false;
            string outDir = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--files": filesPerFolder = int.Parse(args[++i]); break;
                    case "--sweep": sweep = args[++i].Split(',').Select(s => int.Parse(s.Trim())).ToArray(); break;
                    case "--invalid": invalidPct = double.Parse(args[++i]); break;
                    case "--recursive": recursive = true; break;
                    case "--out": outDir = args[++i]; break;
                    case "--keep": keep = true; break;
                    case "-h": case "--help": Usage(); return 0;
                }
            }

            string repoRoot = FindRepoRoot();
            string contextsXml = repoRoot == null ? null : Path.Combine(repoRoot, "Config", "contexts.xml");
            if (contextsXml == null || !File.Exists(contextsXml))
            {
                Console.WriteLine("FATAL: could not locate Config/contexts.xml from the repo root.");
                return 1;
            }

            LoadContextsFromXml(contextsXml);

            outDir ??= Path.Combine(Path.GetTempPath(), "CmmScaleBench_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            MusicDirectory = outDir;   // must precede EnableRecursiveParents (NormalizedContextDir reads it)

            if (recursive) EnableRecursiveParents();

            var flatCtx = contexts.Values.Where(c => !string.IsNullOrEmpty(c.Path)).ToList();
            int folderCount = flatCtx.Count;

            Console.WriteLine("CmmScaleBench — main-thread stall finder at folder scale");
            Console.WriteLine($"  contexts.xml : {contextsXml}");
            Console.WriteLine($"  contexts     : {contexts.Count} total, {folderCount} with a folder" +
                              (recursive ? $", {contexts.Values.Count(c => c.IncludeSubfolders)} recursive" : ""));
            Console.WriteLine($"  files/folder : {filesPerFolder:N0}  (≈ {(long)filesPerFolder * folderCount:N0} files total)");
            Console.WriteLine($"  blacklisted  : {invalidPct:F0}%");
            Console.WriteLine($"  tree         : {outDir}\n");

            try
            {
                GenerateTree(flatCtx, filesPerFolder);
                DiskPhase(flatCtx, recursive);
                CpuSweepPhase(sweep, invalidPct);
            }
            finally
            {
                if (!keep) { try { Directory.Delete(outDir, true); } catch { } }
                else Console.WriteLine($"\n(generated tree kept at {outDir})");
            }
            return 0;
        }

        // ── Library generation ──────────────────────────────────────────────────
        // Zero-byte files with valid audio extensions. The paths under test never
        // read file CONTENT — they enumerate, filter by extension, claim, diff, and
        // select — so empty files exercise every disk/CPU path faithfully while
        // keeping generation fast. (Decode cost is a separate, content-dependent
        // axis already covered by CmmBench / CmmSession against real audio.)
        static readonly string[] Exts = { ".mp3", ".ogg", ".flac", ".wav", ".opus" };

        static void GenerateTree(List<Contexts> ctxs, int filesPerFolder)
        {
            var sw = Stopwatch.StartNew();
            long made = 0;
            foreach (var ctx in ctxs)
            {
                string dir = Path.Combine(MusicDirectory, ctx.Path);
                Directory.CreateDirectory(dir);
                for (int i = 0; i < filesPerFolder; i++)
                {
                    string ext = Exts[i % Exts.Length];
                    // Realistic-ish, varied filenames (length affects Path.GetFullPath cost).
                    string name = $"{ctx.Context}_track_{i:D4}_{(i * 2654435761u) & 0xFFFFFF:x6}{ext}";
                    File.Create(Path.Combine(dir, name)).Dispose();
                    made++;
                }
            }
            sw.Stop();
            Console.WriteLine($"── Generated {made:N0} files in {sw.Elapsed.TotalSeconds:F1}s " +
                              $"({made / Math.Max(0.001, sw.Elapsed.TotalSeconds):N0} files/s)\n");
        }

        // ── DISK phase ──────────────────────────────────────────────────────────
        static void DiskPhase(List<Contexts> flatCtx, bool recursive)
        {
            Console.WriteLine("══ DISK (against the generated tree, main-thread synchronous work) ══");

            // (1) RebuildContextsWithFiles — the synchronous mod-load presence scan.
            //     Verbatim body from ContextMusicManager.AudioPlayer.cs:1900-1918.
            {
                GcDelta gc = GcDelta.Start();
                var sw = Stopwatch.StartNew();
                _contextsWithFiles.Clear();
                foreach (var ctx in contexts.Values)
                {
                    if (string.IsNullOrEmpty(ctx.Path)) continue;
                    if (ctx.IncludeSubfolders) continue;           // recursive handled by crawler
                    string d = Path.Combine(MusicDirectory, ctx.Path);
                    if (!Directory.Exists(d)) continue;
                    if (EnumerateContextFiles(ctx, d).Length > 0)
                        _contextsWithFiles.Add(ctx.Context);
                }
                sw.Stop();
                Console.WriteLine($"  RebuildContextsWithFiles (flat, all contexts) : " +
                                  $"{sw.Elapsed.TotalMilliseconds,8:F1} ms   alloc {gc.Mb():F1} MB   " +
                                  $"→ {_contextsWithFiles.Count} contexts with files");
                Console.WriteLine($"    {(sw.Elapsed.TotalMilliseconds > 16 ? "⚠ exceeds a 60fps frame budget (16.7ms) — load-time freeze scales with this" : "within a frame budget at this size")}");
            }

            // (2) Per-context single enumeration (what PickNextFile pays when a
            //     snapshot is cold, and what PresenceScanLoop/FileWatch offload).
            {
                var ctx = flatCtx.OrderByDescending(c => c.Path.Length).First();
                string d = Path.Combine(MusicDirectory, ctx.Path);
                var sw = Stopwatch.StartNew();
                int n = EnumerateContextFiles(ctx, d).Length;
                sw.Stop();
                Console.WriteLine($"  EnumerateContextFiles (one flat folder)        : " +
                                  $"{sw.Elapsed.TotalMilliseconds,8:F2} ms   ({n} files)");
            }

            if (recursive)
            {
                var rec = contexts.Values.Where(c => c.IncludeSubfolders && !string.IsNullOrEmpty(c.Path))
                                         .OrderByDescending(c => Subtree(c).Count).FirstOrDefault();
                if (rec != null)
                {
                    string d = Path.Combine(MusicDirectory, rec.Path);

                    // ContextWriteSignatureUtc — FileWatchLoop's recursive mtime fast-path.
                    var sw1 = Stopwatch.StartNew();
                    ContextWriteSignatureUtc(d, true);
                    sw1.Stop();

                    // Full recursive enumeration — crawler / repoint walk.
                    var sw2 = Stopwatch.StartNew();
                    int n = EnumerateContextFiles(rec, d).Length;
                    sw2.Stop();

                    Console.WriteLine($"  ContextWriteSignatureUtc (recursive '{rec.Context}'): " +
                                      $"{sw1.Elapsed.TotalMilliseconds,8:F1} ms   (mtime fast-path / 2s tick)");
                    Console.WriteLine($"  EnumerateContextFiles (recursive '{rec.Context}')   : " +
                                      $"{sw2.Elapsed.TotalMilliseconds,8:F1} ms   ({n} files after claiming)");
                }
            }
            Console.WriteLine();
        }

        // ── CPU sweep phase ─────────────────────────────────────────────────────
        // In-memory: build a synthetic snapshot of N file paths, then run the
        // shipped main-thread routines that operate on that array, scaling N.
        static void CpuSweepPhase(int[] sweep, double invalidPct)
        {
            Console.WriteLine("══ CPU (in-memory, swept by folder size — per-pick / per-tick main-thread cost) ══");
            Console.WriteLine("   (PickNextFile runs once PER TRACK CHANGE; FileWatch diff runs once per mtime change of a watched folder)\n");
            Console.WriteLine($"   {"files",8} | {"PickNextFile (RandomNoRepeat)",-34} | {"FileWatch diff (no change)",-30}");
            Console.WriteLine($"   {"",8} | {"per pick",12} {"alloc/pick",12} | {"per diff",12} {"alloc/diff",12}");
            Console.WriteLine("   " + new string('─', 80));

            const int iters = 200;
            foreach (int n in sweep)
            {
                string[] snapshot = BuildSnapshot(n);
                _invalidFiles.Clear();
                if (invalidPct > 0)
                    foreach (var f in snapshot.Where((_, i) => i % 100 < invalidPct)) _invalidFiles.Add(f);

                // ── PickNextFile ──
                UnityEngine.Random.InitState(7);
                // warm up JIT
                for (int w = 0; w < 5; w++) PickNextFile_Selection(snapshot, snapshot[w % n]);
                GcDelta gcP = GcDelta.Start();
                var swP = Stopwatch.StartNew();
                for (int it = 0; it < iters; it++)
                    PickNextFile_Selection(snapshot, snapshot[it % n]);
                swP.Stop();
                double pickMs = swP.Elapsed.TotalMilliseconds / iters;
                double pickAllocKb = gcP.Bytes() / 1024.0 / iters;

                // ── FileWatchLoop diff (steady state: current == previous, the common tick) ──
                string[] previous = snapshot;
                string[] current = (string[])snapshot.Clone();
                for (int w = 0; w < 3; w++) FileWatchDiff(current, previous);
                GcDelta gcD = GcDelta.Start();
                var swD = Stopwatch.StartNew();
                for (int it = 0; it < iters; it++)
                    FileWatchDiff(current, previous);
                swD.Stop();
                double diffMs = swD.Elapsed.TotalMilliseconds / iters;
                double diffAllocKb = gcD.Bytes() / 1024.0 / iters;

                Console.WriteLine($"   {n,8:N0} | {pickMs,9:F3} ms {pickAllocKb,9:F1} KB | {diffMs,9:F3} ms {diffAllocKb,9:F1} KB");
            }

            Console.WriteLine("\n   Reading this: multiply 'per pick' by your real transition rate (CmmSession");
            Console.WriteLine("   reports ~/sim-min) and 'per diff' by the number of watched folders that change");
            Console.WriteLine("   per FileWatchLoop tick (2s). Allocation feeds the GC — watch the KB columns climb.");
        }

        // Build N synthetic full paths shaped like the generated tree's names.
        static string[] BuildSnapshot(int n)
        {
            var a = new string[n];
            string baseDir = Path.Combine(MusicDirectory, "ambient");
            for (int i = 0; i < n; i++)
                a[i] = Path.Combine(baseDir, $"Ambient_track_{i:D4}_{(i * 2654435761u) & 0xFFFFFF:x6}.ogg");
            return a;
        }

        // ════════════════════════════════════════════════════════════════════════
        // VERBATIM-ISH copies of the shipped main-thread routines under test.
        // ════════════════════════════════════════════════════════════════════════

        // PickNextFile selection branch — ContextMusicManager.AudioPlayer.cs:1764-1791.
        // (Snapshot is non-null here, so we exercise the steady-state path: the
        // _invalidFiles filter copy + the RandomNoRepeat avoidFile .Where().ToArray().)
        static string PickNextFile_Selection(string[] snapshot, string avoidFile)
        {
            string[] files = snapshot;
            if (_invalidFiles.Count > 0)
                files = files.Where(f => !_invalidFiles.Contains(f)).ToArray();

            if (files.Length == 0) return null;

            // GlobalTrackOrder == RandomNoRepeat (the shipped default).
            if (files.Length == 1) return files[0];
            string[] candidates = avoidFile != null
                ? files.Where(f => f != avoidFile).ToArray()
                : files;
            if (candidates.Length == 0) candidates = files;
            return candidates[UnityEngine.Random.Range(0, candidates.Length)];
        }

        // FileWatchLoop slow-path diff — ContextMusicManager.cs:703-749. Everything
        // here runs on the MAIN THREAD (only the Directory enumeration that produced
        // `current` was offloaded to the ThreadPool).
        static readonly Func<string, string> _norm = p => string.IsNullOrEmpty(p) ? p
            : Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        static bool FileWatchDiff(string[] currentFiles, string[] previousFiles)
        {
            var currentNorm = currentFiles.Select(_norm).ToArray();
            var previousNorm = previousFiles.Select(_norm).ToArray();

            bool filesActuallyChanged =
                currentNorm.Length != previousNorm.Length ||
                currentNorm.Except(previousNorm, StringComparer.OrdinalIgnoreCase).Any() ||
                previousNorm.Except(currentNorm, StringComparer.OrdinalIgnoreCase).Any();
            return filesActuallyChanged;
        }

        // ── Enumeration / claiming — verbatim from AudioPlayer.cs:1606-1729 ──────
        static string MusicDirectory;
        static readonly Dictionary<string, Contexts> contexts =
            new Dictionary<string, Contexts>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _invalidFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> _contextsWithFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static Contexts GetContext(string name) => name != null && contexts.TryGetValue(name, out var c) ? c : null;
        static readonly string[] _noClaimPrefixes = new string[0];

        static SearchOption SearchOptionFor(Contexts ctx) =>
            ctx != null && ctx.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        static string[] GetClaimedSubtreePrefixes(string contextName)
        {
            Contexts self = GetContext(contextName);
            if (self == null || string.IsNullOrEmpty(self.Path) || !self.IncludeSubfolders) return _noClaimPrefixes;
            string selfDir = NormalizedContextDir(self.Path);
            if (selfDir == null) return _noClaimPrefixes;
            string selfPrefix = selfDir + Path.DirectorySeparatorChar;

            List<string> prefixes = null;
            foreach (Contexts other in contexts.Values)
            {
                if (other == self || string.IsNullOrEmpty(other.Path)) continue;
                if (string.Equals(other.Context, self.Context, StringComparison.OrdinalIgnoreCase)) continue;
                string otherDir = NormalizedContextDir(other.Path);
                if (otherDir == null) continue;
                if (otherDir.Length > selfDir.Length && otherDir.StartsWith(selfPrefix, StringComparison.OrdinalIgnoreCase))
                    (prefixes ?? (prefixes = new List<string>())).Add(otherDir + Path.DirectorySeparatorChar);
            }
            return prefixes == null ? _noClaimPrefixes : prefixes.ToArray();
        }

        static bool IsClaimedByDeeperContext(string file, string[] claimedPrefixes)
        {
            if (claimedPrefixes.Length == 0) return false;
            string full;
            try { full = Path.GetFullPath(file); } catch { return false; }
            foreach (string p in claimedPrefixes)
                if (full.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static DateTime ContextWriteSignatureUtc(string dir, bool includeSubfolders)
        {
            if (!Directory.Exists(dir)) return DateTime.MinValue;
            DateTime t = Directory.GetLastWriteTimeUtc(dir);
            if (includeSubfolders)
            {
                try
                {
                    foreach (string sub in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
                    {
                        DateTime st = Directory.GetLastWriteTimeUtc(sub);
                        if (st > t) t = st;
                    }
                }
                catch { }
            }
            return t;
        }

        static string NormalizedContextDir(string ctxPath)
        {
            try
            {
                return Path.GetFullPath(Path.Combine(MusicDirectory, ctxPath))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return null; }
        }

        static string[] EnumerateContextFiles(Contexts ctx, string dir)
        {
            string[] claimed = GetClaimedSubtreePrefixes(ctx.Context);
            return Directory.GetFiles(dir, "*", SearchOptionFor(ctx))
                .Where(f => IsSupported(f) && !_invalidFiles.Contains(f) && !IsClaimedByDeeperContext(f, claimed))
                .ToArray();
        }

        // AudioDecoder.IsSupported — AudioDecoder.cs:41-52.
        static readonly HashSet<string> _supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".wav", ".wave", ".aiff", ".aif", ".ogg", ".oga", ".opus", ".flac", ".m4a", ".mp4", ".aac", ".m4v", ".3gp", ".mkv", ".webm" };
        static bool IsSupported(string filePath) => _supportedExts.Contains(Path.GetExtension(filePath));

        // ════════════════════════════════════════════════════════════════════════
        // Harness plumbing
        // ════════════════════════════════════════════════════════════════════════
        static void LoadContextsFromXml(string xmlPath)
        {
            contexts.Clear();
            foreach (var el in XDocument.Load(xmlPath).Descendants("Context"))
            {
                string name = (string)el.Attribute("name");
                string path = (string)el.Attribute("path") ?? "";
                bool sub = ParseBool((string)el.Attribute("subfolders"));
                if (string.IsNullOrEmpty(name)) continue;
                contexts[name] = new Contexts(context: name, path: path, includeSubfolders: sub);
            }
        }

        // Flip subfolders="true" on every context whose folder strictly contains
        // another context's folder (combat/, poi/, ambient/, trader/, vehicle/, …),
        // simulating a recursive config so the claiming + crawler paths engage.
        static void EnableRecursiveParents()
        {
            foreach (var c in contexts.Values.ToList())
            {
                if (string.IsNullOrEmpty(c.Path)) continue;
                if (Subtree(c).Count == 0) continue;
                contexts[c.Context] = new Contexts(context: c.Context, path: c.Path, fallback: c.Fallback,
                    includeSubfolders: true);
            }
        }

        // The set of OTHER contexts nested strictly beneath this one.
        static List<Contexts> Subtree(Contexts self)
        {
            var list = new List<Contexts>();
            string selfDir = NormalizedContextDir(self.Path);
            if (selfDir == null) return list;
            string selfPrefix = selfDir + Path.DirectorySeparatorChar;
            foreach (var other in contexts.Values)
            {
                if (other == self || string.IsNullOrEmpty(other.Path)) continue;
                string od = NormalizedContextDir(other.Path);
                if (od != null && od.Length > selfDir.Length && od.StartsWith(selfPrefix, StringComparison.OrdinalIgnoreCase))
                    list.Add(other);
            }
            return list;
        }

        static bool ParseBool(string s) =>
            !string.IsNullOrEmpty(s) && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1");

        static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "ContextMusicManager.csproj"))) return dir.FullName;
            return null;
        }

        static void Usage() => Console.WriteLine(
            "CmmScaleBench — main-thread stall finder at folder scale\n\n" +
            "  dotnet run --project tools/CmmScaleBench -c Release -- [options]\n\n" +
            "  --files N        tracks per context folder (default 300)\n" +
            "  --sweep a,b,c    in-memory CPU sweep sizes (default 100,300,1000,3000,10000)\n" +
            "  --invalid P      % of files marked blacklisted (default 0)\n" +
            "  --recursive      enable subfolders on parent contexts + run recursive disk paths\n" +
            "  --out DIR        generate the tree here (default: temp dir, deleted after)\n" +
            "  --keep           keep the generated tree\n");

        // Per-thread allocation delta (AppDomain monitoring is process-wide but this
        // bench is single-threaded through each measured section).
        struct GcDelta
        {
            long _start;
            public static GcDelta Start() => new GcDelta { _start = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize };
            public long Bytes() => AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - _start;
            public double Mb() => Bytes() / 1024.0 / 1024.0;
        }
    }
}
