
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // RECURSIVE CRAWLER
    // ─────────────────────────────────────────────────────────────────────────
    // Contexts with IncludeSubfolders="true" can sit over thousands of folders and
    // tens of thousands of files. The old model walked the WHOLE tree for a change
    // signature every FileWatchLoop tick (GetDirectories(AllDirectories), O(folders),
    // every 2s) and rebuilt + diffed the entire file list on any change (a multi-
    // hundred-MB transient). Neither scales.
    //
    // RecursiveCrawlLoop replaces both for these contexts. It walks the tree on a
    // background thread in small, throttled chunks, statting each directory and only
    // re-listing the ones whose mtime actually changed (per-directory mtime skip).
    // The flat directorySnapshot PickNextFile consumes is reassembled from the per-
    // directory cache only when something changed. Steady state is therefore mostly
    // cheap directory stats spread thinly over time — no per-tick full-tree walk and
    // no big rebuild/diff spike. The trade-off is detection latency: a change is
    // noticed within roughly one sweep, not instantly.
    //
    // The one fast reaction kept on the 2s tick (in FileWatchLoop) is the O(1) ping
    // of the ACTIVE context's playing file, so a deleted current track still hot-
    // swaps immediately rather than waiting for the crawler to reach its folder.
    //
    // `cmm rescan <ctx|all>` (CmmConsole) forces a context's cached state to be
    // discarded so the next sweep re-lists every folder from scratch.
    public partial class ContextMusicManager
    {
        // ── Tunables (seconds / counts) ────────────────────────────────────────
        public static float CrawlChunkInterval = 0.1f;  // pause between directory chunks
        public static int   CrawlChunkDirs     = 48;     // directories statted per chunk
        public static float CrawlPassPause     = 5f;     // idle between full sweeps
        public static float CrawlStartDelay    = 1f;     // let init settle before first sweep

        // Per-directory cache: mtime + the supported, non-claimed files sitting
        // DIRECTLY in that directory (top level, not recursive).
        private sealed class DirNode
        {
            public DateTime Mtime;
            public string[] Files;
        }

        private sealed class CrawlState
        {
            public readonly Dictionary<string, DirNode> Dirs =
                new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);
        }

        // contextName → its directory cache. Touched only on the main thread (the
        // crawler coroutine), so no locking is needed for it.
        private static readonly Dictionary<string, CrawlState> _crawl =
            new Dictionary<string, CrawlState>(StringComparer.OrdinalIgnoreCase);

        // Manual rescan requests from the console command. Written off the crawler
        // thread (console Execute), drained by the crawler — hence the lock.
        private static readonly object _rescanLock = new object();
        private static readonly HashSet<string> _rescanRequests =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _rescanAll;

        // One directory's worth of off-thread disk results.
        private struct DirScanResult
        {
            public bool Ok;            // false = unreadable (ACL/long path/vanished); keep prior node
            public DateTime Mtime;
            public string[] Files;     // null = mtime unchanged, reuse cached node's Files
            public string[] Subdirs;   // immediate children to enqueue
        }

        // ── Main loop ──────────────────────────────────────────────────────────
        private IEnumerator RecursiveCrawlLoop()
        {
            yield return new WaitForSeconds(CrawlStartDelay);

            while (true)
            {
                // Resolve the recursive contexts to sweep on the main thread (reads
                // the shared `contexts` dict).
                var targets = new List<Contexts>();
                foreach (Contexts c in contexts.Values)
                    if (c != null && c.IncludeSubfolders && !string.IsNullOrEmpty(c.Path))
                        targets.Add(c);

                // Drain manual rescan requests: forget the cached state so the sweep
                // re-lists every folder (mtime skip can't short-circuit unknown dirs).
                DrainRescanRequests();

                foreach (Contexts ctx in targets)
                    yield return StartCoroutine(CrawlOneContext(ctx));

                yield return new WaitForSeconds(CrawlPassPause);
            }
        }

        private static void DrainRescanRequests()
        {
            bool all;
            List<string> reqs = null;
            lock (_rescanLock)
            {
                all = _rescanAll;
                _rescanAll = false;
                if (_rescanRequests.Count > 0)
                {
                    reqs = new List<string>(_rescanRequests);
                    _rescanRequests.Clear();
                }
            }
            if (all) _crawl.Clear();
            else if (reqs != null) foreach (string n in reqs) _crawl.Remove(n);
        }

        // ── One context's throttled sweep ──────────────────────────────────────
        private IEnumerator CrawlOneContext(Contexts ctx)
        {
            string contextName = ctx.Context;
            string root = NormalizedContextDir(ctx.Path);
            if (root == null || !Directory.Exists(root))
            {
                // Folder gone (or unresolved): clear the context out so it reads empty.
                _crawl.Remove(contextName);
                if (cacheMap.TryGetValue(contextName, out ContextCache empty) &&
                    (empty.directorySnapshot == null || empty.directorySnapshot.Length > 0))
                {
                    empty.directorySnapshot = new string[0];
                    ApplyPresenceFromSnapshot(contextName, empty);
                }
                yield break;
            }

            string[] claimed = GetClaimedSubtreePrefixes(contextName);
            if (!_crawl.TryGetValue(contextName, out CrawlState state))
                _crawl[contextName] = state = new CrawlState();

            var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(root);
            bool changed     = false;   // any node added / re-listed / removed this sweep
            bool markedHasFiles = false;

            while (stack.Count > 0)
            {
                // Pull a chunk off the traversal stack.
                int take = Math.Min(CrawlChunkDirs, stack.Count);
                var batch = new List<string>(take);
                for (int i = 0; i < take; i++) batch.Add(stack.Pop());

                // Read known mtimes for the batch (main thread) so the worker can
                // decide per directory whether a re-list is needed.
                int n = batch.Count;
                var known    = new DateTime[n];
                var hadNode  = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    if (state.Dirs.TryGetValue(batch[i], out DirNode dn))
                    {
                        known[i]   = dn.Mtime;
                        hadNode[i] = true;
                    }
                }

                // Disk work off the main thread (stat + list changed dirs + children).
                var res  = new DirScanResult[n];
                bool done = false;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    for (int i = 0; i < n; i++)
                    {
                        string d = batch[i];
                        var r = new DirScanResult();
                        try
                        {
                            r.Mtime   = Directory.GetLastWriteTimeUtc(d);
                            r.Subdirs = Directory.GetDirectories(d);
                            if (!hadNode[i] || r.Mtime != known[i])
                                r.Files = Directory.GetFiles(d)
                                    .Where(f => AudioDecoder.IsSupported(f)
                                                && !IsClaimedByDeeperContext(f, claimed))
                                    .ToArray();
                            r.Ok = true;
                        }
                        // One bad folder must not abort the sweep: leave its prior node
                        // intact and retry it on a later pass (mirrors the presence-scan
                        // and FileWatchLoop hardening).
                        catch { r.Ok = false; }
                        res[i] = r;
                    }
                    done = true;
                });
                while (!done) yield return null;

                // Apply on the main thread: update the per-dir cache, enqueue children.
                for (int i = 0; i < n; i++)
                {
                    string d = batch[i];
                    DirScanResult r = res[i];
                    seen.Add(d);
                    if (!r.Ok) continue;

                    if (r.Files != null)
                    {
                        state.Dirs[d] = new DirNode { Mtime = r.Mtime, Files = r.Files };
                        changed = true;
                    }

                    // Flip presence on as soon as the first file is seen, so a huge
                    // context becomes playable mid-sweep instead of only at the end.
                    if (!markedHasFiles &&
                        state.Dirs.TryGetValue(d, out DirNode cur) && cur.Files.Length > 0)
                    {
                        markedHasFiles = true;
                        _contextsWithFiles.Add(contextName);
                    }

                    if (r.Subdirs != null)
                        for (int s = 0; s < r.Subdirs.Length; s++) stack.Push(r.Subdirs[s]);
                }

                yield return new WaitForSeconds(CrawlChunkInterval);
            }

            // Drop directories that have disappeared since the last sweep.
            if (state.Dirs.Count != seen.Count)
            {
                var gone = state.Dirs.Keys.Where(k => !seen.Contains(k)).ToList();
                if (gone.Count > 0)
                {
                    foreach (string g in gone) state.Dirs.Remove(g);
                    changed = true;
                }
            }

            if (!changed) yield break;

            // Reassemble the flat snapshot from the per-directory cache. References the
            // same string instances already held in DirNode.Files — no path is copied.
            var files = new List<string>();
            foreach (DirNode dn in state.Dirs.Values)
                if (dn.Files.Length > 0) files.AddRange(dn.Files);

            yield return ApplyCrawlSnapshot(contextName, files.ToArray());
        }

        // Publish a freshly-assembled snapshot and react where the crawler is the only
        // thing that can: an ACTIVE context riding a borrowed vanilla bed whose custom
        // files have just reappeared swaps back to the real track. Every other reaction
        // is already covered — presence changes drive ContextScanLoop, and a removed
        // PLAYING file is caught by FileWatchLoop's O(1) ping.
        private IEnumerator ApplyCrawlSnapshot(string contextName, string[] snapshot)
        {
            if (!cacheMap.TryGetValue(contextName, out ContextCache cache)) yield break;

            cache.directorySnapshot = snapshot;
            ApplyPresenceFromSnapshot(contextName, cache);

            // MENU reclaim (the crawler is the ONLY thing that can do it for a recursive
            // Menu). On the menu screen ContextScanLoop doesn't run, and a subfolder context
            // never takes FileWatchLoop's flat "File change in Menu → SendContextUpdate"
            // reclaim path (it skips the folder walk after the O(1) playing-file ping). So
            // when custom files reappear for Menu while it has surrendered to the vanilla
            // bed, nothing else brings its music back. Route through SendContextUpdate so the
            // vanilla hand-off (DeactivateVanilla + fade) is handled like the flat path.
            bool menuVanillaRefill =
                !inGame && snapshot.Length > 0 &&
                string.Equals(contextName, "Menu", StringComparison.OrdinalIgnoreCase) &&
                (currentContext == null ||
                 !string.Equals(currentContext.Context, "Menu", StringComparison.OrdinalIgnoreCase));
            if (menuVanillaRefill)
            {
                CMMConfig.D($"[CMM] Crawler: custom files appeared for Menu on the vanilla bed — reclaiming.");
                if (_pendingContext == "Menu") _pendingContext = null;
                SendContextUpdate("Menu");
            }
        }

        // ── Console command surface (called from CmmConsole.ExecuteImpl) ────────
        // Returns a human-readable status line for the console.
        public static string RescanCommand(string target)
        {
            if (string.IsNullOrEmpty(target) ||
                target.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                List<string> names = RecursiveContextNames();
                if (names.Count == 0)
                    return "no subfolder (IncludeSubfolders) contexts are configured.";
                lock (_rescanLock) { _rescanAll = true; }
                return $"queued rescan of {names.Count} subfolder context(s): {string.Join(", ", names)}";
            }

            Contexts ctx = GetContext(target);
            if (ctx == null)
                return $"no context named '{target}'. Subfolder contexts: " +
                       string.Join(", ", RecursiveContextNames());

            if (string.IsNullOrEmpty(ctx.Path))
                return $"context '{ctx.Context}' has no folder to scan.";

            if (ctx.IncludeSubfolders)
            {
                lock (_rescanLock) { _rescanRequests.Add(ctx.Context); }
                return $"queued rescan of subfolder context '{ctx.Context}'.";
            }

            // Flat context: drop its snapshot and route through the presence scan loop.
            if (cacheMap.TryGetValue(ctx.Context, out ContextCache c)) c.directorySnapshot = null;
            _presenceDirty.Add(ctx.Context);
            return $"queued rescan of flat context '{ctx.Context}'.";
        }

        private static List<string> RecursiveContextNames()
        {
            var list = new List<string>();
            foreach (Contexts c in contexts.Values)
                if (c != null && c.IncludeSubfolders && !string.IsNullOrEmpty(c.Path))
                    list.Add(c.Context);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }
}
