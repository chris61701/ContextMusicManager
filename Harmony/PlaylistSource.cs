using System;
using System.Collections.Generic;
using System.IO;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // PLAYLIST SOURCES (.m3u / .m3u8)
    // ─────────────────────────────────────────────────────────────────────────
    // A context whose `source` names a .m3u/.m3u8 file draws its tracks from the
    // files LISTED in that playlist instead of from a folder scan. The playlist is a
    // standard, file-only m3u: one track path per line; blank lines and '#'-comment
    // lines (including the #EXTM3U / #EXTINF directives) ignored; relative entries
    // resolved against the PLAYLIST FILE'S OWN folder (the m3u convention); absolute
    // entries used as-is.
    //
    // The output is the same shape every folder-scan site produces — an array of
    // full track paths that becomes ContextCache.directorySnapshot — so everything
    // downstream (PickNextFile, loudness, resume, presence, the native mixer) is
    // unchanged. Only which tracks are gathered differs.
    //
    // Whether a source IS a playlist is Contexts.IsPlaylistSource (in DataStructures.cs,
    // linked into the tools too); this file holds the disk-touching read that depends on
    // AudioDecoder, so it lives with the mod.
    internal static class PlaylistSource
    {
        // Resolve a context source (folder OR playlist file) to a full path: rooted →
        // as-is; relative → under MusicDirectory. Mirrors the Path.Combine the folder
        // scan sites already do, kept here so playlist and folder resolution match.
        public static string ResolveFullPath(string source) =>
            System.IO.Path.IsPathRooted(source)
                ? source
                : System.IO.Path.Combine(ContextMusicManager.MusicDirectory, source);

        // Read a playlist file and return the full paths of the supported, existing
        // tracks it lists, IN FILE ORDER (so Sequential track-order follows the list).
        // Pure IO + string math and touches no shared state, so it is safe to call off
        // the main thread — the same way the folder Directory.GetFiles scans already run
        // on the ThreadPool. A missing/unreadable playlist, or an entry that is missing,
        // unsupported, or unresolvable, is simply skipped; the result is never null.
        // ── file:// entries ──────────────────────────────────────────────────────────
        // VLC exports playlists as URIs by default, and it is far and away the most common
        // way people build one:
        //     file:///H:/Music/Audio%20Overview/Rise/Track.m4a
        // Without this the line is not Path.IsPathRooted (it begins "file:"), so it was taken
        // for a RELATIVE path and resolved against the playlist's own folder, producing
        // "...\Lit FM\file:\H:\Music\...", which never exists. Every entry dropped, the
        // station came out empty, and an empty station is simply absent from the catalog — so
        // it looked like the new folder had not been noticed at all.
        //
        // Uri.LocalPath does the whole job: it percent-decodes (%20 and UTF-8 sequences alike)
        // and yields "H:\Music\Audio Overview\Rise\Track.m4a", handling UNC
        // (file://server/share/x) as \\server\share\x too. Gated on the scheme actually being
        // file:, because a bare Windows path parses as a Uri as well — "C:\x" has scheme "c" —
        // and must keep going down the plain-path route.
        //
        // http(s):// is deliberately NOT accepted: nothing downstream can stream a URL, and
        // silently returning null keeps such an entry out rather than half-supporting it.
        // Is the volume this path lives on actually there? Deliberately not File.Exists, which
        // cannot tell "deleted" from "unplugged" — the distinction this whole overload exists to
        // draw. Same helper, same reasoning, as RadioStationCatalog's.
        private static bool RootReachable(string file)
        {
            try
            {
                if (string.IsNullOrEmpty(file)) return true;
                string root = System.IO.Path.GetPathRoot(file);
                return string.IsNullOrEmpty(root) || Directory.Exists(root);
            }
            catch { return true; }   // can't tell ⇒ don't claim the drive is missing
        }

        internal static string FromFileUri(string line)
        {
            if (line == null || line.Length < 6) return null;
            if (!line.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var u = new Uri(line);
                return u.IsFile ? u.LocalPath : null;
            }
            catch { return null; }   // malformed URI → fall through to the plain-path route
        }

        public static string[] ReadTracks(string playlistFullPath)
            => ReadTracks(playlistFullPath, out _);

        // `unresolved` distinguishes the two ways a playlist comes back with nothing:
        //
        //   EMPTY      — the entries are genuinely unusable. A settled answer; act on it.
        //   UNRESOLVED — the entries name files on a volume that is not currently there. That
        //                is not an answer at all, and recording it as one is how a momentary
        //                absence becomes permanent.
        //
        // The second is the common case here rather than the exotic one: most stations are .m3u
        // files on one drive pointing at another, so the playlist reads fine while every target
        // it names is missing. Read one of those with the target drive away and the station has
        // no tracks, is therefore absent from the catalog, and stays absent — because the only
        // thing that triggers a re-read is the .m3u's own timestamp, which sits on the drive that
        // never left. The drive coming back does not bring the station back.
        //
        // Costs nothing in normal operation: the reachability check runs only after a playlist
        // has already yielded zero usable tracks.
        public static string[] ReadTracks(string playlistFullPath, out bool unresolved)
        {
            // Default before any early return. An unreadable or missing .m3u is NOT unresolved:
            // that one recovers on its own, because the playlist file's presence is part of the
            // signature the catalog watches, so it rebuilds when the file goes and again when it
            // returns. The case this flag exists for is the opposite -- a playlist that reads
            // perfectly well while everything it points at is on a drive that is not there.
            unresolved = false;
            if (string.IsNullOrEmpty(playlistFullPath)) return new string[0];

            string[] lines;
            try { lines = File.ReadAllLines(playlistFullPath); }
            catch { return new string[0]; }   // playlist gone / locked / unreadable

            string baseDir;
            try { baseDir = System.IO.Path.GetDirectoryName(playlistFullPath); }
            catch { baseDir = null; }

            var tracks = new List<string>(lines.Length);
            int notFound = 0, unsupported = 0;
            string firstMissingFull = null;
            string firstBad = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines)
            {
                string line = raw?.Trim();
                if (string.IsNullOrEmpty(line)) continue;   // blank
                if (line[0] == '#') continue;               // comment / #EXTM3U / #EXTINF

                string full;
                try
                {
                    // file:// URI → a real path first; VLC and several other players export m3u
                    // that way and it is a legitimate form of the format. Absolute entry → as-is.
                    // Relative entry → against the playlist's own folder (m3u convention), NOT
                    // MusicDirectory.
                    string entry = FromFileUri(line) ?? line;
                    full = System.IO.Path.IsPathRooted(entry)
                        ? System.IO.Path.GetFullPath(entry)
                        : baseDir != null
                            ? System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, entry))
                            : null;
                }
                catch { full = null; }   // malformed path entry
                if (full == null) continue;

                if (!AudioDecoder.IsSupported(full)) { unsupported++; if (firstBad == null) firstBad = line; continue; }
                if (!File.Exists(full))
                {
                    notFound++;
                    if (firstBad == null) firstBad = line;
                    if (firstMissingFull == null) firstMissingFull = full;
                    continue;
                }
                if (seen.Add(full)) tracks.Add(full);   // de-dupe repeated entries
            }

            // SAY SOMETHING WHEN A PLAYLIST YIELDS NOTHING. A station whose pool is empty is
            // simply absent from the catalog, so a playlist none of whose entries resolve looks
            // exactly like a folder that was never noticed -- which is how a real one (a VLC
            // export full of file:// URIs) cost an hour to diagnose, with nothing in the log to
            // go on. Named entries, so the FORM of the failure is visible at a glance.
            if (tracks.Count == 0 && (notFound > 0 || unsupported > 0))
            {
                // Is this "these files are not usable" or "this drive is not plugged in"? Only ask
                // once the playlist has already come back empty — the answer costs a directory
                // stat and cannot matter on any path where it produced tracks.
                unresolved = notFound > 0 && !RootReachable(firstMissingFull);
                if (unresolved)
                    Log.Warning($"[CMM] Playlist '{System.IO.Path.GetFileName(playlistFullPath)}': all "
                              + $"{notFound} entr{(notFound == 1 ? "y is" : "ies are")} on a drive that is not "
                              + $"currently available ({System.IO.Path.GetPathRoot(firstMissingFull)}). "
                              + "Leaving it unresolved rather than empty, so it comes back when the drive does.");
                else
                    Log.Warning($"[CMM] Playlist '{System.IO.Path.GetFileName(playlistFullPath)}': none of its "
                              + $"{notFound + unsupported} entr{(notFound + unsupported == 1 ? "y" : "ies")} could be used "
                              + $"({notFound} not found, {unsupported} unsupported format). First: {firstBad}");
            }

            return tracks.ToArray();
        }
    }
}
