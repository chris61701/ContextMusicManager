
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
        public static float CrawlChunkInterval = 0.1f;  // pause between turns
        public static int   CrawlChunkDirs     = 48;     // max directories STARTED per turn
        public static int   CrawlChunkFiles    = 2000;   // max directory entries EXAMINED per turn (bounds a single huge folder)
        public static float CrawlPassPause     = 5f;     // idle between full sweeps
        public static float CrawlStartDelay    = 1f;     // let init settle before first sweep
        public static float CrawlPartialPublishInterval = 1.5f;  // republish the growing snapshot at most this often during a first sweep

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

        // A directory whose listing FINISHED this turn — ready to commit to the cache.
        private struct CompletedDir
        {
            public string   Dir;
            public DateTime Mtime;
            public string[] Files;
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

                // Round-robin the recursive contexts: one directory-chunk from ctx 1, then a
                // chunk from ctx 2, then ctx 3, then back to ctx 1. Fair progress — a single
                // gigantic folder can't starve another context's first snapshot — and, unlike
                // running the sweeps concurrently, only ONE directory scan is ever in flight, so
                // N giant contexts don't multiply disk IO: each gets a turn, then a shared pace
                // pause, holding total load at ~CrawlChunkDirs per CrawlChunkInterval no matter
                // how many contexts are being swept. Sweeps are cooperative (single main thread,
                // advancing only at yields) and each owns a distinct CrawlState / cache /
                // _contextsWithFiles entry, so there's no cross-context data race.
                var sweeps = new List<SweepState>();
                foreach (Contexts ctx in targets)
                {
                    SweepState s = BeginSweep(ctx);   // null = folder gone / nothing to walk
                    if (s != null) sweeps.Add(s);
                }

                while (sweeps.Count > 0)
                {
                    // Every surviving sweep still has work (a stacked dir or one mid-page).
                    for (int i = 0; i < sweeps.Count; i++)
                    {
                        yield return StartCoroutine(StepChunk(sweeps[i]));   // this context's turn
                        if (SweepFinished(sweeps[i]))
                            yield return StartCoroutine(FinishSweep(sweeps[i]));
                        yield return new WaitForSeconds(CrawlChunkInterval); // pace per turn, not per context
                    }
                    sweeps.RemoveAll(SweepFinished);   // drop the ones that finished this round
                }

                yield return new WaitForSeconds(CrawlPassPause);
            }
        }

        // Resumable per-context sweep state so the crawler can round-robin one turn at a time
        // across several contexts (BeginSweep → repeated StepChunk → FinishSweep). A turn is
        // bounded in BOTH directories started (CrawlChunkDirs) and directory entries examined
        // (CrawlChunkFiles), so a single folder holding hundreds of thousands of files is paged
        // across many turns via the Scan* cursor instead of listed in one blocking call.
        private sealed class SweepState
        {
            public string          ContextName;
            public string[]        Claimed;
            public CrawlState      State;
            public HashSet<string> Seen;
            public Stack<string>   Stack;
            public bool            Changed;         // any node added / re-listed / removed this sweep
            public bool            MarkedHasFiles;
            public bool            Incremental;     // publish growing partial snapshots this sweep
            public float           LastPublish;

            // Paged listing of one directory across turns. ScanDir != null ⇒ a directory is
            // mid-enumeration: ScanEnum is its open lazy file enumerator and ScanFiles the
            // matching files gathered so far. Left intact between turns when a turn's file
            // budget is hit before the directory is exhausted.
            public string              ScanDir;
            public DateTime            ScanMtime;
            public List<string>        ScanFiles;
            public IEnumerator<string> ScanEnum;
        }

        // A sweep is done only when nothing is stacked AND no directory is still being paged.
        private static bool SweepFinished(SweepState s) => s.Stack.Count == 0 && s.ScanDir == null;

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

        // ── Sweep setup (synchronous) ───────────────────────────────────────────
        // Prime one context's sweep. Returns null when there's nothing to walk (folder gone /
        // unresolved), having first cleared the context to empty as a side effect. Otherwise
        // returns a SweepState whose Stack holds the root, ready for the first StepChunk.
        private SweepState BeginSweep(Contexts ctx)
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
                return null;
            }

            if (!_crawl.TryGetValue(contextName, out CrawlState state))
                _crawl[contextName] = state = new CrawlState();

            // First-sweep incremental publish: when the snapshot is still empty, republish a
            // growing snapshot as directories are discovered so a massive context becomes
            // playable on a PARTIAL set instead of only when the whole tree has been walked.
            // Approximate — the authoritative (pruned) snapshot is still published from
            // state.Dirs in FinishSweep, so correctness doesn't depend on the partials.
            bool incremental =
                !cacheMap.TryGetValue(contextName, out ContextCache liveCache)
                || liveCache.directorySnapshot == null || liveCache.directorySnapshot.Length == 0;

            var s = new SweepState
            {
                ContextName = contextName,
                Claimed     = GetClaimedSubtreePrefixes(contextName),
                State       = state,
                Seen        = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                Stack       = new Stack<string>(),
                Incremental = incremental,
                LastPublish = Time.realtimeSinceStartup,
            };
            s.Stack.Push(root);
            return s;
        }

        // ── One turn of one context's sweep (its round-robin slot) ──────────────
        // Off the main thread, starts directories (up to CrawlChunkDirs) and pages their files
        // (up to CrawlChunkFiles entries total), resuming a directory left mid-page by a prior
        // turn. Then, on the main thread, commits finished directories to the per-dir cache and
        // does the throttled partial publish. The dual budget means neither a very wide tree nor
        // a single very large folder can make one turn unbounded — a folder with 300k files is
        // consumed CrawlChunkFiles at a time across many turns via the Scan* cursor. Leaves the
        // sweep "finished" (empty stack, no ScanDir) exactly when the whole tree is walked.
        private IEnumerator StepChunk(SweepState s)
        {
            if (SweepFinished(s)) yield break;

            CrawlState state = s.State;
            string[] claimed = s.Claimed;

            var completed = new List<CompletedDir>();
            bool done = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int fileBudget = CrawlChunkFiles;
                int dirBudget  = CrawlChunkDirs;
                while (fileBudget > 0)
                {
                    // Start the next directory unless one is already mid-page.
                    if (s.ScanDir == null)
                    {
                        if (dirBudget <= 0 || s.Stack.Count == 0) break;
                        string d = s.Stack.Pop();
                        dirBudget--;
                        s.Seen.Add(d);   // seen even if unreadable → FinishSweep keeps its prior node

                        DateTime mtime;
                        string[] subs;
                        try
                        {
                            mtime = Directory.GetLastWriteTimeUtc(d);
                            subs  = Directory.GetDirectories(d);
                        }
                        // One bad folder must not abort the sweep (ACL/long path/vanished):
                        // skip it, keep any prior node, retry on a later pass.
                        catch { continue; }

                        for (int i = 0; i < subs.Length; i++) s.Stack.Push(subs[i]);

                        // mtime unchanged since last sweep → skip the (possibly huge) listing.
                        if (state.Dirs.TryGetValue(d, out DirNode node) && node.Mtime == mtime)
                            continue;

                        s.ScanDir   = d;
                        s.ScanMtime = mtime;
                        s.ScanFiles = new List<string>();
                        try { s.ScanEnum = Directory.EnumerateFiles(d).GetEnumerator(); }
                        catch { s.ScanDir = null; s.ScanFiles = null; continue; }
                    }

                    // Page the current directory, spending the remaining file budget on it.
                    bool exhausted = false;
                    try
                    {
                        while (fileBudget > 0)
                        {
                            if (!s.ScanEnum.MoveNext()) { exhausted = true; break; }
                            fileBudget--;
                            string f = s.ScanEnum.Current;
                            if (AudioDecoder.IsSupported(f) && !IsClaimedByDeeperContext(f, claimed))
                                s.ScanFiles.Add(f);
                        }
                    }
                    // Enumeration threw mid-directory (deleted/locked): drop this listing,
                    // keep the prior node, move on.
                    catch
                    {
                        DisposeScan(s);
                        continue;
                    }

                    if (exhausted)
                    {
                        completed.Add(new CompletedDir { Dir = s.ScanDir, Mtime = s.ScanMtime, Files = s.ScanFiles.ToArray() });
                        DisposeScan(s);
                    }
                    else break;   // file budget hit mid-directory — resume this dir next turn
                }
                done = true;
            });
            while (!done) yield return null;

            // ── Apply on the main thread ────────────────────────────────────────
            // Commit directories whose listing finished this turn.
            for (int i = 0; i < completed.Count; i++)
            {
                CompletedDir cd = completed[i];
                state.Dirs[cd.Dir] = new DirNode { Mtime = cd.Mtime, Files = cd.Files };
                s.Changed = true;
                // Flip presence on as soon as the first file is seen, so a huge context becomes
                // playable mid-sweep instead of only at the end.
                if (!s.MarkedHasFiles && cd.Files.Length > 0)
                {
                    s.MarkedHasFiles = true;
                    _contextsWithFiles.Add(s.ContextName);
                }
            }

            // Throttled partial publish (first sweep only): assemble a growing snapshot from
            // every directory listed so far PLUS the in-progress directory's files, so a context
            // becomes playable long before its walk finishes — including the case where a SINGLE
            // directory holds hundreds of thousands of files and is still being paged. Assembly
            // is O(files-so-far) but throttled to CrawlPartialPublishInterval; PickNextFile reads
            // the reference locklessly, so swapping in the array is safe on the main thread.
            if (s.Incremental &&
                (Time.realtimeSinceStartup - s.LastPublish) >= CrawlPartialPublishInterval)
            {
                var files = new List<string>();
                foreach (DirNode dn in state.Dirs.Values)
                    if (dn.Files.Length > 0) files.AddRange(dn.Files);
                if (s.ScanFiles != null && s.ScanFiles.Count > 0) files.AddRange(s.ScanFiles);
                if (files.Count > 0 && cacheMap.TryGetValue(s.ContextName, out ContextCache pc))
                {
                    s.LastPublish = Time.realtimeSinceStartup;
                    pc.directorySnapshot = files.ToArray();
                    ApplyPresenceFromSnapshot(s.ContextName, pc);
                }
            }
        }

        // Close the open paged-listing cursor and clear it. Dispose can't be allowed to throw
        // out of the worker loop (it would abort the whole turn), so it's swallowed.
        private static void DisposeScan(SweepState s)
        {
            try { s.ScanEnum?.Dispose(); } catch { }
            s.ScanEnum  = null;
            s.ScanDir   = null;
            s.ScanFiles = null;
        }

        // ── Sweep finalize (once the tree is fully walked) ──────────────────────
        // Prunes directories that vanished since the last sweep, then reassembles and publishes
        // the authoritative flat snapshot from the per-directory cache.
        private IEnumerator FinishSweep(SweepState s)
        {
            CrawlState state = s.State;

            // Drop directories that have disappeared since the last sweep.
            if (state.Dirs.Count != s.Seen.Count)
            {
                var gone = state.Dirs.Keys.Where(k => !s.Seen.Contains(k)).ToList();
                if (gone.Count > 0)
                {
                    foreach (string g in gone) state.Dirs.Remove(g);
                    s.Changed = true;
                }
            }

            if (!s.Changed) yield break;

            // Reassemble the flat snapshot from the per-directory cache. References the
            // same string instances already held in DirNode.Files — no path is copied.
            var files = new List<string>();
            foreach (DirNode dn in state.Dirs.Values)
                if (dn.Files.Length > 0) files.AddRange(dn.Files);

            yield return ApplyCrawlSnapshot(s.ContextName, files.ToArray());
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
