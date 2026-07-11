using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // SOURCE SCAN — the pure (Unity-free) enumeration + change-signature logic that
    // turns a context's source into its track pool. Extracted from the ContextMusicManager
    // partial (ContextMusicManager.Presence.cs) so it can be LINKED into offline test
    // harnesses and exercised as the exact same code the game runs — no hand-ported copy
    // to drift out of sync. Everything here is System.IO + AudioDecoder + PlaylistSource +
    // Contexts only: no MonoBehaviour, no shared mutable state, safe on a ThreadPool thread.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class SourceScan
    {
        internal static readonly string[] _noPlaylistFiles = new string[0];

        // The eligible tracks for a FOLDER source. Recursive: every supported, non-claimed
        // file across the tree, with .m3u files ignored. Flat: the supported files directly
        // in the folder UNIONED with the tracks listed by each top-level .m3u (relative
        // entries resolve against the .m3u's own folder), deduped by full path. `dir` must
        // exist. Does NOT filter _invalidFiles (callers apply that as they always have) so it
        // stays shared-state-free and safe off the main thread. Outputs the top-level .m3u
        // files found (flat only; empty when recursive) for the change-signature watch.
        internal static string[] EnumerateFolderPoolRaw(string dir, bool recursive, string[] claimed, out string[] playlistFiles)
        {
            playlistFiles = _noPlaylistFiles;
            if (recursive)
            {
                // Recursion ignores playlists entirely: a .m3u in the tree is neither
                // expanded nor (being unsupported audio) played.
                return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => AudioDecoder.IsSupported(f) && !IsClaimedByDeeperContext(f, claimed))
                    .ToArray();
            }

            string[] all = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly);
            var pool = new List<string>(all.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> m3us = null;
            foreach (string f in all)
            {
                if (Contexts.IsPlaylistSource(f)) { (m3us ?? (m3us = new List<string>())).Add(f); continue; }
                if (AudioDecoder.IsSupported(f) && !IsClaimedByDeeperContext(f, claimed) && seen.Add(FullKey(f)))
                    pool.Add(f);
            }
            if (m3us != null)
            {
                foreach (string m3u in m3us)
                    foreach (string t in PlaylistSource.ReadTracks(m3u))
                        if (seen.Add(FullKey(t))) pool.Add(t);
                playlistFiles = m3us.ToArray();
            }
            return pool.ToArray();
        }

        // Case-insensitive full-path dedupe key. Falls back to the raw string if the path
        // can't be normalized (already absolute from the enumerations, so rare).
        internal static string FullKey(string path)
        {
            try { return Path.GetFullPath(path); } catch { return path; }
        }

        // True if the file lives under a deeper context's folder (so this context must
        // not enumerate it). Cheap no-op when there are no claim prefixes.
        internal static bool IsClaimedByDeeperContext(string file, string[] claimedPrefixes)
        {
            if (claimedPrefixes.Length == 0) return false;
            string full;
            try { full = Path.GetFullPath(file); }
            catch { return false; }
            foreach (string p in claimedPrefixes)
                if (full.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Folder-change signature for the FileWatchLoop mtime fast-path. A flat
        // context is just the folder's own write time. A recursive context must also
        // fold in every subdirectory's write time: adding or removing a file bumps
        // its PARENT directory's mtime but not any ancestor's, so the root mtime
        // alone would miss a change made two levels down. Walking directories (not
        // files) keeps this far cheaper than a full recursive file enumeration.
        internal static DateTime ContextWriteSignatureUtc(string dir, bool includeSubfolders) =>
            ContextWriteSignatureUtc(dir, includeSubfolders, null);

        // Overload that also folds in the mtimes of a flat folder's own top-level .m3u files
        // (folderPlaylistFiles, from cache.playlistFiles). A folder's mtime doesn't change
        // when a contained .m3u is edited IN PLACE, so without this an in-folder playlist edit
        // would go unnoticed until the folder itself next changed. Statting a handful of known
        // .m3u files is cheap and — crucially — only folders that actually contain playlists
        // ever have any to stat, so the common no-playlist folder keeps its O(1) mtime check.
        // Ignored when recursive (recursion doesn't process playlists).
        internal static DateTime ContextWriteSignatureUtc(string dir, bool includeSubfolders, string[] folderPlaylistFiles)
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
            else if (folderPlaylistFiles != null)
            {
                foreach (string pf in folderPlaylistFiles)
                {
                    try { if (File.Exists(pf)) { DateTime mt = File.GetLastWriteTimeUtc(pf); if (mt > t) t = mt; } }
                    catch { }
                }
            }
            return t;
        }

        // True if any of these (previously-discovered) in-folder .m3u files no longer exists.
        // Lets the folder watch notice a playlist REMOVAL even on filesystems that don't bump
        // the folder's mtime on delete (exFAT/FAT, some network shares): the signature is a
        // MAX of mtimes, so a non-newest file vanishing leaves it unchanged. Cheap — these are
        // the same handful of files ContextWriteSignatureUtc already stats — and self-clearing:
        // once the forced rescan refreshes cache.playlistFiles, the vanished entry is gone from
        // the list, so this stops firing. Superseded at runtime by FolderPlaylistSetChanged on
        // sloppy-mtime filesystems (which also catches ADDs); still used by the offline tests.
        internal static bool AnyPlaylistMissing(string[] folderPlaylistFiles)
        {
            if (folderPlaylistFiles == null) return false;
            foreach (string pf in folderPlaylistFiles)
            {
                try { if (!File.Exists(pf)) return true; } catch { }
            }
            return false;
        }

        // The folder's CURRENT top-level .m3u/.m3u8 files (full paths). Empty on error/missing.
        // Scoped to the .m3u pattern so it materializes only playlist paths, but note the OS
        // still walks the directory's entries — hence callers gate this to sloppy filesystems.
        internal static string[] ListFolderPlaylistFiles(string dir)
        {
            try
            {
                return Directory.GetFiles(dir, "*.m3u*", SearchOption.TopDirectoryOnly)
                    .Where(f => Contexts.IsPlaylistSource(f)).ToArray();
            }
            catch { return _noPlaylistFiles; }
        }

        // True if the folder's current top-level .m3u set differs (ADD **or** REMOVE) from the
        // last-scanned set (knownPlaylistFiles = cache.playlistFiles). This is the complement to
        // the mtime signature: it detects a playlist appearing/disappearing on a filesystem that
        // doesn't move the folder's write-time on add/remove. Requires an actual directory
        // listing (unlike AnyPlaylistMissing, which only re-stats known files), so runtime
        // callers invoke it ONLY when IsSloppyMtimeFilesystem(dir) is true — on NTFS the folder
        // mtime already covers both directions for free.
        internal static bool FolderPlaylistSetChanged(string dir, string[] knownPlaylistFiles)
        {
            string[] current = ListFolderPlaylistFiles(dir);
            string[] known = knownPlaylistFiles ?? _noPlaylistFiles;
            if (current.Length != known.Length) return true;
            if (current.Length == 0) return false;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in known) set.Add(FullKey(k));
            foreach (string f in current)
                if (!set.Contains(FullKey(f))) return true;
            return false;
        }

        // The LISTING-based change signature for sloppy-mtime filesystems: an FNV-1a hash over
        // every file's full path + mtime under `dir` (top-level or the whole tree). On exFAT/FAT/
        // removable/network volumes a folder's mtime does NOT move when a child is added, removed
        // or renamed, so the max-of-mtimes signatures are blind there — this catches all three
        // (path set changes on add/remove/rename; mtime covers in-place edits). Callers gate on
        // IsSloppyMtimeFilesystem (NTFS/ReFS keep their pure-stat fast path) and treat a THROW as
        // "no change" (transient IO mid-walk must not flap the caller). Never returns 0 — callers
        // use 0 as "not computed".
        internal static long FileSetContentHash(string dir, bool includeSubfolders)
        {
            if (!Directory.Exists(dir)) return 1;
            ulong h = 14695981039346656037UL;
            foreach (string f in Directory.EnumerateFiles(dir, "*",
                         includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                for (int i = 0; i < f.Length; i++)
                {
                    h ^= char.ToLowerInvariant(f[i]);
                    h *= 1099511628211UL;
                }
                ulong t = (ulong)File.GetLastWriteTimeUtc(f).Ticks;
                for (int b = 0; b < 8; b++)
                {
                    h ^= (byte)(t >> (b * 8));
                    h *= 1099511628211UL;
                }
            }
            long res = (long)h;
            return res == 0 ? 1 : res;
        }

        // Cache of drive-root → whether its filesystem is "sloppy" about folder mtimes: FAT/exFAT,
        // removable media, and network/undeterminable volumes may not bump a directory's write-time
        // when a file is added or removed, so the mtime fast-path can silently miss a playlist
        // appearing or vanishing. NTFS/ReFS bump reliably and are trusted (no per-tick listing).
        private static readonly Dictionary<string, bool> _sloppyMtimeByRoot =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _sloppyLock = new object();

        // True if `dir` lives on a filesystem whose folder mtime can't be trusted to move on
        // add/remove. Determined once per drive root via DriveInfo.DriveFormat and cached; a
        // UNC path, a removed/not-ready drive, or any failure is treated as sloppy (correctness
        // over the fast path — we'd rather do the extra listing than miss a change).
        internal static bool IsSloppyMtimeFilesystem(string dir)
        {
            string root;
            try { root = Path.GetPathRoot(Path.GetFullPath(dir)); }
            catch { return true; }
            if (string.IsNullOrEmpty(root)) return true;
            lock (_sloppyLock)
            {
                if (_sloppyMtimeByRoot.TryGetValue(root, out bool cached)) return cached;
                bool sloppy;
                try
                {
                    string fmt = new DriveInfo(root).DriveFormat;
                    sloppy = !(fmt.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                            || fmt.Equals("ReFS", StringComparison.OrdinalIgnoreCase));
                }
                catch { sloppy = true; }
                _sloppyMtimeByRoot[root] = sloppy;
                return sloppy;
            }
        }

        // Change signature for a playlist source: the playlist FILE's own mtime. Editing
        // the .m3u bumps it, so the next watch tick re-reads the list. A listed track
        // deleted elsewhere does NOT bump it — that's reconciled by the active context's
        // O(1) playing-file ping and prune-on-failed-open, exactly like any vanished file.
        internal static DateTime PlaylistWriteSignatureUtc(string playlistFile)
        {
            try
            {
                return File.Exists(playlistFile)
                    ? File.GetLastWriteTimeUtc(playlistFile) : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }
    }
}
