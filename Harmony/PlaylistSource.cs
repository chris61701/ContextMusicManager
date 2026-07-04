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
        public static string[] ReadTracks(string playlistFullPath)
        {
            if (string.IsNullOrEmpty(playlistFullPath)) return new string[0];

            string[] lines;
            try { lines = File.ReadAllLines(playlistFullPath); }
            catch { return new string[0]; }   // playlist gone / locked / unreadable

            string baseDir;
            try { baseDir = System.IO.Path.GetDirectoryName(playlistFullPath); }
            catch { baseDir = null; }

            var tracks = new List<string>(lines.Length);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in lines)
            {
                string line = raw?.Trim();
                if (string.IsNullOrEmpty(line)) continue;   // blank
                if (line[0] == '#') continue;               // comment / #EXTM3U / #EXTINF

                string full;
                try
                {
                    // Absolute entry → as-is. Relative entry → against the playlist's own
                    // folder (m3u convention), NOT MusicDirectory.
                    full = System.IO.Path.IsPathRooted(line)
                        ? System.IO.Path.GetFullPath(line)
                        : baseDir != null
                            ? System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, line))
                            : null;
                }
                catch { full = null; }   // malformed path entry
                if (full == null) continue;

                if (!AudioDecoder.IsSupported(full)) continue;
                if (!File.Exists(full)) continue;
                if (seen.Add(full)) tracks.Add(full);   // de-dupe repeated entries
            }
            return tracks.ToArray();
        }
    }
}
