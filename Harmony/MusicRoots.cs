using System;
using System.Collections.Generic;
using System.IO;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // WHERE MUSIC COMES FROM.
    //
    // CMM used to have exactly one music folder: MusicPath. Now it has that one
    // plus the `Music` folder of every installed mod, so a soundtrack can ship as
    // its own MO2/Wabbajack mod instead of being copied over CMM's install.
    //
    // THE RULES, settled with the author:
    //
    //   * The PRIMARY root is MusicPath. It is always in the list, present or not —
    //     a missing primary must keep reading as "this context is empty", which is
    //     the behaviour every fallback chain is already tuned against.
    //   * ADD-ON roots are <mod>/Music for each loaded mod, found once at launch.
    //     The list is immutable for the session; `cmm scan` rebuilds it. A root that
    //     disappears needs no special handling — a folder that is not there
    //     contributes no tracks, and its absence is part of the change signature.
    //   * A context folder is looked up by the SAME relative path in every root, so
    //     a pack supplying POI_T3 ships Music/poi/t3/. The tree mirrors MusicPath's.
    //   * `subfolders="true"` applies ONLY to the primary. Add-on folders are always
    //     read flat. That keeps the incremental crawler single-rooted and keeps the
    //     "deepest context owns the file" rule out of add-on folders entirely — a
    //     pack's poi/ cannot swallow its own poi/t3/, because it never descends.
    //   * Playlist and absolute sources resolve to exactly one place. There is no
    //     relative path to append, so add-ons cannot extend them, and a broken one
    //     stays broken rather than being quietly filled from somewhere else.
    //
    // Unity-free on purpose, so the offline harnesses link THIS rather than a
    // hand-ported copy. Discovery, which needs the game's mod list, lives next door
    // in MusicRootsDiscovery.
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class MusicRoot
    {
        public string Path;        // absolute, normalized, no trailing separator
        public string Label;       // mod display name, for diagnostics; null = primary
        public bool   IsPrimary;
        // Whether this root's drive lies about folder timestamps (exFAT/FAT/network).
        // Resolved once per root instead of once per folder: every folder under a root
        // shares its volume, and the per-drive lookup takes a lock.
        public bool   Sloppy;

        public override string ToString() => IsPrimary ? Path + "  (MusicPath)" : Path + "  (" + Label + ")";
    }

    internal static class MusicRoots
    {
        internal static readonly string[] NoDirs = new string[0];

        // One immutable array, swapped whole. Readers take a local copy of the
        // reference and use it for the whole computation; nothing mutates in place.
        private static volatile MusicRoot[] _roots =
            new[] { new MusicRoot { Path = string.Empty, IsPrimary = true } };

        internal static MusicRoot[] All => _roots;
        internal static int Count => _roots.Length;
        internal static int AddonCount => _roots.Length - 1;

        internal static string Primary => _roots[0].Path;

        // Replaces the primary in place, keeping any add-on roots. Called whenever
        // MusicPath resolves (startup and every settings hot-reload).
        internal static void SetPrimary(string path)
        {
            MusicRoot[] cur = _roots;
            string norm = Normalize(path);
            if (string.Equals(cur[0].Path, norm, StringComparison.OrdinalIgnoreCase)) return;

            var next = new MusicRoot[cur.Length];
            next[0] = new MusicRoot
            {
                Path = norm, IsPrimary = true, Sloppy = SourceScan.IsSloppyMtimeFilesystem(norm),
            };
            Array.Copy(cur, 1, next, 1, cur.Length - 1);
            _roots = next;
        }

        // Publishes a freshly discovered add-on set. Main thread only (launch, cmm scan).
        internal static void SetAddons(IEnumerable<MusicRoot> addons)
        {
            MusicRoot[] cur = _roots;
            var list = new List<MusicRoot> { cur[0] };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { cur[0].Path };
            foreach (MusicRoot r in addons)
            {
                if (r == null || string.IsNullOrEmpty(r.Path)) continue;
                r.Path = Normalize(r.Path);
                // Dedup against the primary and each other: CMM's own Music folder IS the
                // default MusicPath, and a user may point MusicPath straight at a pack.
                if (!seen.Add(r.Path)) continue;
                r.IsPrimary = false;
                r.Sloppy = SourceScan.IsSloppyMtimeFilesystem(r.Path);
                list.Add(r);
            }
            _roots = list.ToArray();
        }

        internal static string Normalize(string p)
        {
            if (string.IsNullOrEmpty(p)) return string.Empty;
            try
            {
                return Path.GetFullPath(p)
                           .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return p; }
        }

        // ── Resolution ────────────────────────────────────────────────────────

        // Every folder this context reads from, primary first. Add-on folders are
        // included whether or not they exist: their existence is folded into the
        // change signature, so a pack's folder appearing or vanishing is noticed
        // without re-discovering anything. Enumeration skips the missing ones.
        //
        // Empty for a sentinel (no source) and for a playlist source — a playlist is
        // a file, and PlaylistSource owns it.
        internal static string[] DirsFor(Contexts ctx) => DirsFor(ctx, _roots);

        internal static string[] DirsFor(Contexts ctx, MusicRoot[] roots)
        {
            if (ctx == null || string.IsNullOrEmpty(ctx.Source) || ctx.IsPlaylist) return NoDirs;

            // An absolute source names one exact place. Path.Combine would return it
            // unchanged for every root, so this is also what stops N duplicates.
            if (Path.IsPathRooted(ctx.Source))
                return new[] { Normalize(ctx.Source) };

            var dirs = new string[roots.Length];
            for (int i = 0; i < roots.Length; i++)
                dirs[i] = Combine(roots[i].Path, ctx.Source);
            return dirs;
        }

        // The per-root "does this volume lie about folder timestamps" flags, aligned
        // with DirsFor's output so the signature can branch per folder.
        internal static bool[] SloppyFor(Contexts ctx) => SloppyFor(ctx, _roots);

        internal static bool[] SloppyFor(Contexts ctx, MusicRoot[] roots)
        {
            if (ctx == null || string.IsNullOrEmpty(ctx.Source) || ctx.IsPlaylist)
                return new bool[0];
            if (Path.IsPathRooted(ctx.Source))
                return new[] { SourceScan.IsSloppyMtimeFilesystem(ctx.Source) };

            var s = new bool[roots.Length];
            for (int i = 0; i < roots.Length; i++) s[i] = roots[i].Sloppy;
            return s;
        }

        // The primary folder alone — what the crawler walks, and what "the context's
        // own folder" means for anything that must not see add-on content.
        internal static string PrimaryDir(Contexts ctx)
        {
            if (ctx == null || string.IsNullOrEmpty(ctx.Source) || ctx.IsPlaylist) return null;
            return Path.IsPathRooted(ctx.Source)
                 ? Normalize(ctx.Source)
                 : Combine(_roots[0].Path, ctx.Source);
        }

        internal static string Combine(string root, string source)
        {
            try
            {
                return Path.GetFullPath(Path.Combine(root, source))
                           .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return null; }
        }

        // ── "Deepest context owns the file" ───────────────────────────────────

        // When one recursive context's folder contains another context's folder, the
        // inner one owns those files and the outer must not also list them. Computed
        // against the PRIMARY root only: recursion never applies to add-on folders,
        // so nothing there can be swallowed.
        internal static string[] ClaimPrefixes(Contexts self, IEnumerable<Contexts> all)
        {
            if (self == null || string.IsNullOrEmpty(self.Source)
                || self.IsPlaylist || !self.IncludeSubfolders)
                return SourceScan._noClaimPrefixes;

            string selfDir = PrimaryDir(self);
            if (selfDir == null) return SourceScan._noClaimPrefixes;
            string selfPrefix = selfDir + Path.DirectorySeparatorChar;

            List<string> prefixes = null;
            foreach (Contexts other in all)
            {
                if (other == null || other == self) continue;
                if (string.IsNullOrEmpty(other.Source) || other.IsPlaylist) continue;
                if (string.Equals(other.Context, self.Context, StringComparison.OrdinalIgnoreCase)) continue;

                string otherDir = PrimaryDir(other);
                if (otherDir == null) continue;
                if (otherDir.Length > selfDir.Length &&
                    otherDir.StartsWith(selfPrefix, StringComparison.OrdinalIgnoreCase))
                    (prefixes ?? (prefixes = new List<string>())).Add(otherDir + Path.DirectorySeparatorChar);
            }
            return prefixes == null ? SourceScan._noClaimPrefixes : prefixes.ToArray();
        }

        // ── Diagnostics ───────────────────────────────────────────────────────

        internal static string Describe()
        {
            MusicRoot[] r = _roots;
            var sb = new System.Text.StringBuilder();
            sb.Append("music roots: ").Append(r.Length)
              .Append(" (1 primary + ").Append(r.Length - 1).Append(" add-on)");
            for (int i = 0; i < r.Length; i++)
            {
                sb.Append(CMMSettingsWriter.NL).Append("  [").Append(i).Append("] ").Append(r[i].Path);
                if (!r[i].IsPrimary) sb.Append("   ").Append(r[i].Label);
                else sb.Append("   MusicPath");
                if (!Directory.Exists(r[i].Path)) sb.Append("   (MISSING)");
                if (r[i].Sloppy) sb.Append("   [no-mtime]");
            }
            return sb.ToString();
        }
    }
}
