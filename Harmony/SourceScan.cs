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
        // Moved here from the ContextMusicManager partial so MusicRoots (Unity-free, and
        // therefore linkable into the offline harnesses) can share it.
        internal static readonly string[] _noClaimPrefixes = new string[0];

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

        // ── Several folders, read as one ──────────────────────────────────────

        // The pool for a context that draws from MusicPath AND any number of add-on
        // mods' Music folders. Every folder is read FLAT: add-on folders never recurse
        // (see MusicRoots), and a recursive PRIMARY is the crawler's job, not this one.
        //
        // Order is deliberate and stable: each folder's own listing is sorted, and the
        // folders are concatenated in root order (primary first). Filesystem
        // enumeration order is not guaranteed to repeat across sessions, and
        // GlobalTrackOrder=Sequential indexes straight into this array — without the
        // sort a sequential context would silently reshuffle itself on a rehash.
        //
        // A folder that does not exist contributes nothing and is not an error: that
        // is how a pack with no folder for this context, and a pack whose folder was
        // deleted, both cost exactly one Directory.Exists.
        //
        // A folder that THROWS is skipped and the rest still count. One unreadable
        // add-on must cost you that add-on, never the context.
        internal static string[] EnumerateFlatPoolMulti(string[] dirs, string[] claimed,
                                                        out string[] playlistFiles)
        {
            playlistFiles = _noPlaylistFiles;
            if (dirs == null || dirs.Length == 0) return new string[0];

            var pool = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> allPlaylists = null;

            for (int i = 0; i < dirs.Length; i++)
            {
                string dir = dirs[i];
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                try
                {
                    string[] part = EnumerateFolderPoolRaw(dir, false, claimed, out string[] pls);
                    Array.Sort(part, StringComparer.OrdinalIgnoreCase);
                    foreach (string f in part)
                        if (seen.Add(FullKey(f))) pool.Add(f);
                    if (pls != null && pls.Length > 0)
                        (allPlaylists ?? (allPlaylists = new List<string>())).AddRange(pls);
                }
                catch { /* this folder is unreadable right now; the others still count */ }
            }

            if (allPlaylists != null) playlistFiles = allPlaylists.ToArray();
            return pool.ToArray();
        }

        // ── The change signature across several folders ───────────────────────

        // What a watch tick compares against last tick's value.
        //
        // A MAX OF TIMESTAMPS WOULD BE WRONG, and quietly so. A folder that does not
        // exist reports DateTime.MinValue, and MinValue is the identity element of a
        // max — so with one folder that sentinel is exactly what FORCES the rescan,
        // and folded across several it is absorbed and a deleted add-on folder is
        // never noticed. The pool then holds files that are gone, forever, while the
        // music keeps playing and nothing looks wrong.
        //
        // So this folds MEMBERSHIP as well as time: every folder's path and whether it
        // exists go into the hash, and a missing folder contributes a sentinel that no
        // real tick count can collide with.
        internal struct WatchSig
        {
            public long     Fold;
            public DateTime Newest;    // for the "touched in the last few seconds" heuristic only
            public bool     Faulted;   // IO threw: caller must skip WITHOUT storing this value
        }

        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime  = 1099511628211UL;

        private static void MixI64(ref ulong h, long v)
        {
            for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= FnvPrime; }
        }

        private static void MixString(ref ulong h, string s)
        {
            if (s == null) { h ^= 0xFF; h *= FnvPrime; return; }
            for (int i = 0; i < s.Length; i++) { h ^= char.ToLowerInvariant(s[i]); h *= FnvPrime; }
            h ^= 0x2F; h *= FnvPrime;                       // separator, so ["ab","c"] != ["a","bc"]
        }

        // "Touched within the last few seconds", so a coarse FAT/exFAT stamp that lands on
        // the same value it already had cannot hide an edit.
        //
        // The lower bound is not decoration. A FUTURE timestamp -- clock skew on a
        // removable or network volume, robocopy /DCOPY:T, a restored backup -- makes the
        // difference negative, which is also "< 6s", so the folder would read as
        // permanently just-touched and re-enumerate every single tick forever. With
        // several folders folded together, one bad stamp anywhere would do it.
        internal static bool RecentlyTouched(DateTime newest, double windowSec)
        {
            if (newest == DateTime.MinValue) return false;
            TimeSpan age = DateTime.UtcNow - newest;
            return age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(windowSec);
        }

        internal static WatchSig ContextWatchSignature(string[] dirs, bool[] sloppy,
                                                       bool primaryRecursive,
                                                       string[] folderPlaylistFiles)
        {
            var sig = new WatchSig { Newest = DateTime.MinValue };
            ulong h = FnvOffset;
            if (dirs == null) { sig.Fold = (long)h; return sig; }

            for (int i = 0; i < dirs.Length; i++)
            {
                string dir = dirs[i];
                MixString(ref h, dir);                       // membership + order

                bool exists;
                try { exists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir); }
                catch { sig.Faulted = true; return sig; }

                // -1 can never be a real tick count, so present→missing always moves the
                // fold even when another folder holds a later timestamp.
                long ticks = -1L;
                if (exists)
                {
                    try
                    {
                        // Only the PRIMARY may be recursive; add-on folders are flat.
                        DateTime t = ContextWriteSignatureUtc(dir, primaryRecursive && i == 0,
                                                              i == 0 ? folderPlaylistFiles : null);
                        ticks = t.Ticks;
                        if (t > sig.Newest) sig.Newest = t;
                    }
                    catch { sig.Faulted = true; return sig; }
                }
                MixI64(ref h, ticks);

                // Per FOLDER, not per context: a primary on NTFS and an add-on on exFAT
                // genuinely need different treatment, and gating on the primary alone
                // would leave the add-on's changes permanently undetected.
                if (exists && sloppy != null && i < sloppy.Length && sloppy[i])
                {
                    try { MixI64(ref h, FileSetContentHash(dir, primaryRecursive && i == 0)); }
                    catch { sig.Faulted = true; return sig; }
                }
            }

            sig.Fold = (long)h;
            return sig;
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
