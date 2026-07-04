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
        private static readonly string[] _noClaimPrefixes = new string[0];

        // One context's pre-resolved scan parameters, handed to the background
        // PresenceScanLoop thread so it never touches the shared `contexts` dict.
        private struct PresenceScanItem
        {
            public string Name;
            public string Dir;
            public SearchOption Option;
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
                Dir     = Path.Combine(MusicDirectory, ctx.Source),
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
        private static ScanResult EnumerateScanItem(PresenceScanItem item)
        {
            try
            {
                if (item.IsPlaylist)
                    return new ScanResult { Files = PlaylistSource.ReadTracks(item.PlaylistFile), PlaylistFiles = _noPlaylistFiles };
                if (!Directory.Exists(item.Dir))
                    return new ScanResult { Files = new string[0], PlaylistFiles = _noPlaylistFiles };
                string[] files = EnumerateFolderPoolRaw(item.Dir, item.Option == SearchOption.AllDirectories,
                                                        item.Claimed, out string[] pf);
                return new ScanResult { Files = files, PlaylistFiles = pf };
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
        private static string[] GetClaimedSubtreePrefixes(string contextName)
        {
            Contexts self = GetContext(contextName);
            // Claiming is a folder-recursion concern only; a playlist lists its files
            // explicitly, so it is never a claimer nor claimed-against.
            if (self == null || string.IsNullOrEmpty(self.Source) || self.IsPlaylist || !self.IncludeSubfolders)
                return _noClaimPrefixes;

            string selfDir = NormalizedContextDir(self.Source);
            if (selfDir == null) return _noClaimPrefixes;
            string selfPrefix = selfDir + Path.DirectorySeparatorChar;

            List<string> prefixes = null;
            foreach (Contexts other in contexts.Values)
            {
                if (other == self || string.IsNullOrEmpty(other.Source) || other.IsPlaylist) continue;
                if (string.Equals(other.Context, self.Context, StringComparison.OrdinalIgnoreCase)) continue;

                string otherDir = NormalizedContextDir(other.Source);
                if (otherDir == null) continue;
                // Strictly nested beneath self (a context at the SAME folder is a
                // pre-existing ambiguity claiming doesn't touch).
                if (otherDir.Length > selfDir.Length &&
                    otherDir.StartsWith(selfPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    (prefixes ?? (prefixes = new List<string>()))
                        .Add(otherDir + Path.DirectorySeparatorChar);
                }
            }
            return prefixes == null ? _noClaimPrefixes : prefixes.ToArray();
        }

        // NOTE: IsClaimedByDeeperContext, ContextWriteSignatureUtc and PlaylistWriteSignatureUtc
        // now live in SourceScan.cs (linked into the offline test harness). Reached unqualified
        // here via `using static CMM.SourceScan;`.

        // Resolve a context Path (relative to MusicDirectory, or absolute) to a full,
        // non-separator-terminated directory path for prefix comparisons. Null on error.
        private static string NormalizedContextDir(string ctxPath)
        {
            try
            {
                return Path.GetFullPath(Path.Combine(MusicDirectory, ctxPath))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return null; }
        }

        // Enumerate a FOLDER context's eligible files: applies reach (flat vs recursive),
        // subtree claiming, and — for a flat folder — union with its top-level .m3u tracks,
        // in one place so every caller stays consistent. Returns full paths of supported,
        // non-blacklisted, non-claimed files. `dir` must exist.
        private static string[] EnumerateContextFiles(Contexts ctx, string dir) =>
            EnumerateContextFiles(ctx, dir, out _);

        // Overload that also outputs the flat folder's top-level .m3u files, so a caller
        // holding the context's cache can record them (cache.playlistFiles) for the
        // change-signature watch. Empty for recursive folders.
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
            return _contextsWithFiles.Contains(contextName);
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
                string d = Path.Combine(MusicDirectory, ctx.Source);
                if (!Directory.Exists(d)) continue;
                string[] files = EnumerateContextFiles(ctx, d, out string[] pf);
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
