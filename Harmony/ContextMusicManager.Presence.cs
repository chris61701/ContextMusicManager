using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

using static CMM.SourceScan;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // FILE PRESENCE / CONTEXT-FOLDER ENUMERATION (subtree claiming, snapshot,
    // background presence scan; split from AudioPlayer)
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager
    {
        // ── Subfolder recursion + subtree claiming ────────────────────────────
        // Shared by every context-folder enumeration site (PickNextFile, the
        // presence scans, and FileWatchLoop). Two independent concerns:
        //
        //   • REACH (IncludeSubfolders): a flat context lists only files directly in
        //     its folder; a subfolders="true" context lists its whole subtree.
        //   • OWNERSHIP (claiming): when a recursive context's subtree contains a
        //     MORE-SPECIFIC child context's folder, those files belong to the child,
        //     not the parent. Each track maps to exactly one context — the deepest
        //     one whose Path is a prefix — so a recursive parent never double-counts
        //     a child's library. Mirrors the resolver's "most-specific wins" rule.

        // One context's pre-resolved scan parameters, handed to the background
        // PresenceScanLoop thread so it never touches the shared `contexts` dict.
        private struct PresenceScanItem
        {
            public string Name;
            // Every folder this context reads from -- MusicPath's first, then one per add-on
            // mod. Resolved on the main thread with the rest of the item so the background
            // scan never touches shared state.
            public string[] Dirs;
            public SearchOption Option;    // applies to Dirs[0] only; add-on folders are flat
            public string[] Claimed;
            // Playlist source: gather the tracks LISTED in this .m3u file instead of
            // scanning Dir. When true, PlaylistFile holds the resolved playlist path and
            // Dir/Option/Claimed are unused (a playlist is a flat, self-listed pool).
            public bool IsPlaylist;
            public string PlaylistFile;
        }

        // Resolve one context's background-scan parameters (folder OR playlist). Must run
        // on the main thread — GetClaimedSubtreePrefixes reads the shared `contexts` dict.
        // The returned item is then enumerated off-thread by EnumerateScanItem.
        private static PresenceScanItem MakeScanItem(Contexts ctx)
        {
            if (ctx.IsPlaylist)
                return new PresenceScanItem
                {
                    Name         = ctx.Context,
                    IsPlaylist   = true,
                    PlaylistFile = PlaylistSource.ResolveFullPath(ctx.Source),
                };
            return new PresenceScanItem
            {
                Name    = ctx.Context,
                Dirs    = MusicRoots.DirsFor(ctx),
                Option  = SearchOptionFor(ctx),
                Claimed = GetClaimedSubtreePrefixes(ctx.Context),
            };
        }

        // A scan's output: the eligible tracks plus, for a flat folder, the top-level .m3u
        // files found (so the change signature can watch them for live edits).
        private struct ScanResult
        {
            public string[] Files;
            public string[] PlaylistFiles;   // flat folder only; empty otherwise
        }

        // Enumerate one scan item: a direct-playlist read or a folder scan. For a FLAT
        // folder the pool is the loose supported files UNIONED with the tracks listed by
        // every top-level .m3u in it (deduped); a recursive folder ignores playlists.
        // Pure IO + path string math with no shared-state access, so it is safe to call on
        // a ThreadPool thread. Never throws and never returns null (a bad folder / unreadable
        // playlist reads as "no files" and recovers on a later tick).
        // `cmm roots` — the support tool. Without it "my music pack is not playing" is
        // unanswerable: the roots are found at launch, the folders are matched by a
        // relative path, and every one of those steps is invisible from in-game.
        // With no argument: the roots. With a context name: what each root contributes
        // to it, which is what actually settles the question.
        // Make every context re-evaluate whether it has files. Used by `cmm scan`, where
        // the add-on folder list may have just changed under contexts that are not in the
        // watch set and would otherwise keep their old answer until they became relevant.
        internal static void InvalidateAllPresence()
        {
            _contextsWithFilesDirty = true;
            foreach (string name in contexts.Keys) _presenceDirty.Add(name);
        }

        internal static bool HasRecursiveContexts()
        {
            foreach (Contexts c in contexts.Values)
                if (c != null && c.IncludeSubfolders && !c.IsPlaylist
                    && !string.IsNullOrEmpty(c.Source)) return true;
            return false;
        }

        internal static string RootsReport(string contextFilter)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(MusicRoots.Describe());

            if (string.IsNullOrEmpty(contextFilter))
            {
                int supplying = 0;
                foreach (Contexts c in contexts.Values)
                {
                    if (c == null || string.IsNullOrEmpty(c.Source) || c.IsPlaylist) continue;
                    string[] dirs = MusicRoots.DirsFor(c);
                    for (int i = 1; i < dirs.Length; i++)
                        if (!string.IsNullOrEmpty(dirs[i]) && Directory.Exists(dirs[i])) { supplying++; break; }
                }
                sb.Append(CMMSettingsWriter.NL)
                  .Append("contexts fed by at least one add-on: ").Append(supplying)
                  .Append(CMMSettingsWriter.NL)
                  .Append("usage: cmm roots <context>   for the per-folder breakdown");
                return sb.ToString();
            }

            Contexts ctx = GetContext(contextFilter);
            if (ctx == null) return sb.Append(CMMSettingsWriter.NL)
                                      .Append("no context named '").Append(contextFilter).Append("'").ToString();

            sb.Append(CMMSettingsWriter.NL).Append("context ").Append(ctx.Context)
              .Append("  source=\"").Append(ctx.Source ?? "").Append("\"");
            if (ctx.IsPlaylist) sb.Append("  (playlist — add-ons never extend it)");
            else if (!string.IsNullOrEmpty(ctx.Source) && System.IO.Path.IsPathRooted(ctx.Source))
                sb.Append("  (absolute — add-ons never extend it)");
            if (ctx.IncludeSubfolders) sb.Append("  subfolders=true (primary only; add-ons stay flat)");

            string[] all = MusicRoots.DirsFor(ctx);
            if (all.Length == 0) { sb.Append(CMMSettingsWriter.NL).Append("  (no folders)"); return sb.ToString(); }

            int total = 0;
            for (int i = 0; i < all.Length; i++)
            {
                string d = all[i];
                sb.Append(CMMSettingsWriter.NL).Append("  ").Append(i == 0 ? "primary " : "add-on  ").Append(d);
                if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) { sb.Append("   (no folder)"); continue; }
                try
                {
                    bool rec = ctx.IncludeSubfolders && i == 0;
                    string[] pool = EnumerateFolderPoolRaw(d, rec, GetClaimedSubtreePrefixes(ctx.Context), out _);
                    total += pool.Length;
                    sb.Append("   ").Append(pool.Length).Append(rec ? " track(s), recursive" : " track(s)");
                }
                catch (Exception ex) { sb.Append("   UNREADABLE: ").Append(ex.Message); }
            }
            ContextCache cc = cacheMap.TryGetValue(ctx.Context, out var c2) ? c2 : null;
            sb.Append(CMMSettingsWriter.NL).Append("  = ").Append(total)
              .Append(" track(s) across ").Append(all.Length).Append(" folder(s); live pool holds ")
              .Append(cc?.directorySnapshot?.Length ?? 0);
            return sb.ToString();
        }

        private static ScanResult Multi(PresenceScanItem item)
        {
            string[] files = EnumerateFlatPoolMulti(item.Dirs, item.Claimed, out string[] pf);
            return new ScanResult { Files = files, PlaylistFiles = pf };
        }

        private static ScanResult EnumerateScanItem(PresenceScanItem item)
        {
            try
            {
                if (item.IsPlaylist)
                {
                    string[] tracks = PlaylistSource.ReadTracks(item.PlaylistFile, out bool plUnresolved);
                    // Unresolved: the entries name a volume that is not there. Publishing an
                    // empty snapshot would mark the context as having no music for the session.
                    // A null Files reads as "not scanned yet", which is exactly the truth.
                    if (plUnresolved) return new ScanResult { Files = null, PlaylistFiles = _noPlaylistFiles };
                    return new ScanResult { Files = tracks, PlaylistFiles = _noPlaylistFiles };
                }
                bool recursive = item.Option == SearchOption.AllDirectories;
                if (!recursive)
                    return Multi(item);
                // A recursive PRIMARY is walked here as before; its add-on folders are flat
                // and get unioned in on top. (In practice this path is unused -- recursive
                // contexts are owned by the crawler -- but ConfigWatch's repoint rescan can
                // still reach it, and silently dropping the add-on half would be a bug.)
                string[] prim = Directory.Exists(item.Dirs[0])
                    ? EnumerateFolderPoolRaw(item.Dirs[0], true, item.Claimed, out _)
                    : new string[0];
                if (item.Dirs.Length == 1)
                    return new ScanResult { Files = prim, PlaylistFiles = _noPlaylistFiles };
                var rest = new string[item.Dirs.Length - 1];
                Array.Copy(item.Dirs, 1, rest, 0, rest.Length);
                string[] addon = EnumerateFlatPoolMulti(rest, item.Claimed, out _);
                var merged = new List<string>(prim.Length + addon.Length);
                var seenM = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string f in prim)  if (seenM.Add(FullKey(f))) merged.Add(f);
                foreach (string f in addon) if (seenM.Add(FullKey(f))) merged.Add(f);
                return new ScanResult { Files = merged.ToArray(), PlaylistFiles = _noPlaylistFiles };
            }
            catch { return new ScanResult { Files = new string[0], PlaylistFiles = _noPlaylistFiles }; }
        }

        // NOTE: the pure folder/playlist enumeration (EnumerateFolderPoolRaw + FullKey) now
        // lives in SourceScan.cs so the offline test harness links the exact same code. It's
        // reached here unqualified via `using static CMM.SourceScan;`.

        private static SearchOption SearchOptionFor(Contexts ctx) =>
            ctx != null && ctx.IncludeSubfolders
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

        // Absolute, separator-terminated folder prefixes of every OTHER context whose
        // folder is nested strictly beneath this context's folder. A file under any of
        // these is claimed by the deeper context and must be dropped from this one.
        // Empty (the cheap, common case) unless this context recurses AND has at least
        // one child context inside it — so non-recursive contexts pay nothing.
        // Moved to MusicRoots so the offline harnesses can link the real thing instead
        // of the hand-ported copy they had been grading themselves against — that copy
        // had already drifted (it was missing the IsPlaylist guards below). Computed
        // against the PRIMARY root only: add-on folders never recurse, so nothing in
        // one can be swallowed by an outer context.
        private static string[] GetClaimedSubtreePrefixes(string contextName)
            => MusicRoots.ClaimPrefixes(GetContext(contextName), contexts.Values);

        // NOTE: IsClaimedByDeeperContext, ContextWriteSignatureUtc and PlaylistWriteSignatureUtc
        // now live in SourceScan.cs (linked into the offline test harness). Reached unqualified
        // here via `using static CMM.SourceScan;`.

        // Resolve a context Path (relative to MusicDirectory, or absolute) to a full,
        // non-separator-terminated directory path for prefix comparisons. Null on error.
        // The context's own folder under the PRIMARY root. Was doing double duty as both
        // the crawler's walk root and the claim-prefix base; both are primary-only, so
        // the one meaning survives — but it now goes through MusicRoots, which is also
        // what knows about the add-on folders this deliberately excludes.
        private static string NormalizedContextDir(string ctxPath)
            => MusicRoots.Combine(MusicRoots.Primary, ctxPath);

        // Enumerate a FOLDER context's eligible files: applies reach (flat vs recursive),
        // subtree claiming, and — for a flat folder — union with its top-level .m3u tracks,
        // in one place so every caller stays consistent. Returns full paths of supported,
        // non-blacklisted, non-claimed files. `dir` must exist.
        private static string[] EnumerateContextFiles(Contexts ctx, string dir) =>
            EnumerateContextFiles(ctx, dir, out _);

        // Overload that also outputs the flat folder's top-level .m3u files, so a caller
        // holding the context's cache can record them (cache.playlistFiles) for the
        // change-signature watch. Empty for recursive folders.
        // EVERY folder this context reads from, as one pool. The three callers that used
        // to resolve a single directory themselves now go through this, so a context is
        // never accidentally read from MusicPath alone. Flat only -- a recursive context
        // belongs to the crawler and its callers already return early for it.
        private static string[] EnumerateContextRoots(Contexts ctx, out string[] playlistFiles)
        {
            string[] pool = EnumerateFlatPoolMulti(MusicRoots.DirsFor(ctx),
                                                   GetClaimedSubtreePrefixes(ctx.Context),
                                                   out playlistFiles);
            return _invalidFiles.Count == 0
                ? pool
                : pool.Where(f => !_invalidFiles.Contains(f)).ToArray();
        }

        private static string[] EnumerateContextFiles(Contexts ctx, string dir, out string[] playlistFiles)
        {
            string[] pool = EnumerateFolderPoolRaw(dir, ctx.IncludeSubfolders,
                                                   GetClaimedSubtreePrefixes(ctx.Context), out playlistFiles);
            return _invalidFiles.Count == 0
                ? pool
                : pool.Where(f => !_invalidFiles.Contains(f)).ToArray();
        }

        // ── File presence check ───────────────────────────────────────────────
        // ContextHasFiles is now a pure in-memory read of _contextsWithFiles and
        // never touches the disk. The presence set is kept current off the main
        // thread by PresenceScanLoop (which does the Directory.GetFiles work on a
        // ThreadPool thread) and by FileWatchLoop's change detection. This removes
        // the synchronous directory enumeration that previously ran on the main
        // thread every time the resolver evaluated a context — including the
        // per-call disk hit for PlayOnce contexts (Death/Spawn) on the audio path.
        //
        // Trade-off: presence can lag a real folder edit by up to one scan tick
        // (PresenceScanInterval), versus the old "exact but main-thread-blocking"
        // behaviour. For a music mod that staleness window is harmless.
        public static bool ContextHasFiles(string contextName)
        {
            if (!_contextsWithFiles.Contains(contextName)) return false;
            // Files exist, but if the user banned every one of them the context is
            // treated as EMPTY: it resolves through its fallback chain (like the
            // disable-context keybind) instead of playing tracks the user rejected.
            return !AllTracksBanned(contextName);
        }

        // Memoized "every track in this context's snapshot is banned" check. The full
        // sweep is O(files) but re-runs only when a ban toggles (BanRevision) or the
        // snapshot array is replaced; every other call is two field compares, cheap
        // enough for the resolver's every-tick use. A context whose snapshot hasn't
        // been built yet reads as "not all banned" — the first pick populates the
        // snapshot and the next tick corrects it.
        private static bool AllTracksBanned(string contextName)
        {
            if (LoudnessStore.BannedCount == 0) return false;
            if (!cacheMap.TryGetValue(contextName, out ContextCache cache)) return false;
            string[] snap = cache.directorySnapshot;
            if (snap == null || snap.Length == 0) return false;

            if (cache.banEvalRevision != LoudnessStore.BanRevision
                || !ReferenceEquals(cache.banEvalSnapshot, snap))
            {
                bool all = true;
                for (int i = 0; i < snap.Length; i++)
                {
                    if (!LoudnessStore.IsBannedPath(snap[i])) { all = false; break; }
                }
                cache.allTracksBanned = all;
                cache.banEvalRevision = LoudnessStore.BanRevision;
                cache.banEvalSnapshot = snap;
            }
            return cache.allTracksBanned;
        }

        // Recomputes presence for one context from its in-memory directory snapshot
        // (no disk). Used by the FileWatchLoop callers that have just refreshed the
        // snapshot, so the presence set updates immediately and disk-free.
        private static void ApplyPresenceFromSnapshot(string contextName, ContextCache cache)
        {
            string[] snap = cache.directorySnapshot;
            bool has = snap != null && snap.Any(f => !_invalidFiles.Contains(f));
            if (has) _contextsWithFiles.Add(contextName);
            else _contextsWithFiles.Remove(contextName);
        }

        // Marked private so FileWatchLoop can call it for a single context.
        // Never performs disk I/O: if a current snapshot exists we derive presence
        // from it in memory; otherwise we queue the context for the background
        // PresenceScanLoop to (re)scan off the main thread.
        private static void InvalidateContextFileCache(string contextName = null)
        {
            if (contextName == null)
            {
                _contextsWithFilesDirty = true;
                return;
            }

            if (cacheMap.TryGetValue(contextName, out var cache) && cache.directorySnapshot != null)
                ApplyPresenceFromSnapshot(contextName, cache);
            else
                _presenceDirty.Add(contextName);
        }

        // A file we tried to play is gone from disk. Remove it from the context's
        // directory snapshot right now and re-derive presence, so the resolver and
        // PickNextFile stop re-selecting the same missing file on every scan tick.
        // Without this, a removed file that isn't blacklisted ("not on disk" is treated
        // as a transient move/lock, not corruption) keeps the context in a replay loop
        // until a background rescan happens to refresh the snapshot — which it won't,
        // because the failure path only re-derives presence from the STALE snapshot.
        // Also queues an authoritative disk rescan to catch any sibling changes.
        private static void PruneMissingFromSnapshot(string contextName, string filePath)
        {
            if (cacheMap.TryGetValue(contextName, out ContextCache cache))
            {
                string[] snap = cache.directorySnapshot;
                if (snap != null && snap.Length > 0)
                {
                    string[] pruned = snap.Where(f =>
                        !string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (pruned.Length != snap.Length)
                    {
                        cache.directorySnapshot = pruned;
                        ApplyPresenceFromSnapshot(contextName, cache);
                        CMMConfig.D($"[CMM] Pruned missing file from {contextName} snapshot: {Path.GetFileName(filePath)}");
                    }
                }
            }
            _presenceDirty.Add(contextName);
        }

        // Synchronous full rebuild. Disk-bound, so it is only used as the one-time
        // bootstrap during Init (mod load, before menu music starts) — never on a
        // gameplay frame. Ongoing updates go through the background PresenceScanLoop.
        private static void RebuildContextsWithFiles()
        {
            _contextsWithFiles.Clear();
            foreach (var ctx in contexts.Values)
            {
                if (string.IsNullOrEmpty(ctx.Source)) continue;
                if (ctx.IsPlaylist)
                {
                    // Playlist source: a cheap single-file read is fine at init.
                    if (PlaylistSource.ReadTracks(PlaylistSource.ResolveFullPath(ctx.Source)).Length > 0)
                        _contextsWithFiles.Add(ctx.Context);
                    continue;
                }
                // Recursive contexts are populated by RecursiveCrawlLoop's first sweep,
                // not by a synchronous full walk here — that walk would freeze mod load
                // on a tree with thousands of folders. Their presence flips on as soon
                // as the crawler sees the first file (a short delay after load).
                if (ctx.IncludeSubfolders) continue;
                // No Directory.Exists gate on the primary any more: a context whose music
                // lives ONLY in an add-on mod would have been skipped by it, and read as
                // empty for the whole first frame -- long enough for the menu to start on
                // the vanilla bed. EnumerateContextRoots skips missing folders itself.
                string[] files = EnumerateContextRoots(ctx, out string[] pf);
                // Record the in-folder .m3u list now so the change signature can watch it
                // from the first tick (before the background presence scan first runs).
                if (cacheMap.TryGetValue(ctx.Context, out ContextCache cache)) cache.playlistFiles = pf;
                if (files.Length > 0) _contextsWithFiles.Add(ctx.Context);
            }
            _contextsWithFilesDirty = false;
            _presenceDirty.Clear();
        }

        // ── Background presence scan ───────────────────────────────────────────
        // Keeps _contextsWithFiles current without ever calling Directory.GetFiles
        // on the main thread. Disk enumeration runs on a ThreadPool thread; results
        // are applied back on the main thread (the only writer of _contextsWithFiles
        // and directorySnapshot, so no locking is needed).
        private IEnumerator PresenceScanLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(PresenceScanInterval);
                if (_functionallyDormant) continue;   // A/B dormant arm

                // Gather the work for this tick. A full rebuild supersedes the
                // per-context dirty set. Resolve context → directory pairs here on
                // the main thread so the background thread never reads the shared
                // `contexts` dictionary (which ConfigWatchLoop may mutate).
                List<string> names;
                if (_contextsWithFilesDirty)
                {
                    names = contexts.Values
                        .Where(c => !string.IsNullOrEmpty(c.Source))
                        .Select(c => c.Context)
                        .ToList();
                    _contextsWithFilesDirty = false;
                    _presenceDirty.Clear();
                }
                else if (_presenceDirty.Count > 0)
                {
                    names = _presenceDirty.ToList();
                    _presenceDirty.Clear();
                }
                else continue;

                // Resolve directory, reach (flat/recursive) and claim prefixes here on
                // the main thread — the background thread must never read the shared
                // `contexts` dictionary. IsClaimedByDeeperContext (used below) only
                // does path string math, so it is safe off-thread.
                var toScan = new List<PresenceScanItem>(names.Count);
                foreach (string name in names)
                {
                    Contexts ctx = GetContext(name);
                    if (ctx == null || string.IsNullOrEmpty(ctx.Source)) continue;
                    // Recursive FOLDER contexts are owned by RecursiveCrawlLoop — never
                    // re-walk their whole tree here (that's the O(files) cost we're
                    // avoiding). Playlists are flat regardless of the subfolders flag.
                    if (!ctx.IsPlaylist && ctx.IncludeSubfolders) continue;
                    toScan.Add(MakeScanItem(ctx));
                }
                if (toScan.Count == 0) continue;

                // Enumerate the sources on a background thread. EnumerateScanItem never
                // throws and never returns null: one unreadable folder / playlist reads as
                // "no files" (an empty non-null snapshot) so a single failure can't strand
                // every LATER context in this scan or leave SnapshotsReady unable to latch.
                var results = new Dictionary<string, ScanResult>(StringComparer.OrdinalIgnoreCase);
                bool done = false;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    foreach (var item in toScan)
                        results[item.Name] = EnumerateScanItem(item);
                    done = true;
                });
                while (!done) yield return null;

                // Apply on the main thread: refresh the snapshot, the in-folder playlist
                // list (for the change signature), and the presence flag.
                foreach (var kv in results)
                {
                    if (cacheMap.TryGetValue(kv.Key, out var cache))
                    {
                        cache.directorySnapshot = kv.Value.Files;
                        cache.playlistFiles = kv.Value.PlaylistFiles;
                    }
                    bool has = kv.Value.Files.Any(f => !_invalidFiles.Contains(f));
                    if (has) _contextsWithFiles.Add(kv.Key);
                    else _contextsWithFiles.Remove(kv.Key);
                }
            }
        }

    }
}
