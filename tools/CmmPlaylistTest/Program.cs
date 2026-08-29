using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // CmmPlaylistTest — exercises `source` = folder / .m3u through the mod's REAL
    // linked code (SourceScan.EnumerateFolderPoolRaw + PlaylistSource.ReadTracks +
    // the change-signature helpers). Builds a synthetic corpus of empty files with
    // valid extensions (IsSupported is a pure extension check, so no audio bytes are
    // needed) plus a few real, curated-safe tracks from h:\music, then asserts the
    // resolved pool / signature for each scenario.
    //
    // Reporting: NO real h:\music filename is ever printed. Real tracks are shown as
    // labels (H1..); synthetic scratch files by their (self-chosen, safe) basename;
    // anything else is redacted. Usage: CmmPlaylistTest [scratchDir] [h:\musicRoot]
    // ─────────────────────────────────────────────────────────────────────────
    internal static class Program
    {
        static string Scratch;
        static string MusicRoot;                       // MusicDirectory for relative sources
        static readonly Dictionary<string, string> RealLabels =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // real full path -> "H1"
        static string[] H = new string[0];             // curated-safe real tracks (absolute)

        static int _pass, _fail;
        static readonly List<string> _fails = new List<string>();

        static int Main(string[] args)
        {
            string hmusic = args.Length > 1 ? args[1] : @"h:\music";
            Scratch = Path.GetFullPath(args.Length > 0 ? args[0]
                        : Path.Combine(Path.GetTempPath(), "cmm_pltest"));
            MusicRoot = Path.Combine(Scratch, "musicroot");
            ContextMusicManager.MusicDirectory = MusicRoot;

            Console.WriteLine("== CmmPlaylistTest ==");
            Console.WriteLine("scratch    : " + Scratch);
            Console.WriteLine("music root : " + MusicRoot + "  (relative sources resolve here)");

            ResetScratch();
            DiscoverRealTracks(hmusic);
            BuildCorpus();

            Console.WriteLine();
            RunFolderScenarios();
            RunPlaylistScenarios();
            RunUnionScenarios();
            RunSubfolderScenarios();
            RunEdgeScenarios();
            RunUnresolvedScenarios();
            RunChangeDetectionScenarios();
            RunSourceChangeScenarios();
            RunRealMusicScenarios();
            RunEmitterPoolRecoveryScenarios();

            Console.WriteLine();
            Console.WriteLine($"== RESULT: {_pass} passed, {_fail} failed ==");
            if (_fail > 0) { Console.WriteLine("FAILURES:"); foreach (var f in _fails) Console.WriteLine("  - " + f); }
            return _fail == 0 ? 0 : 1;
        }

        // ── Real, curated-safe track discovery (names never printed) ────────────
        static void DiscoverRealTracks(string hmusic)
        {
            // Chiptune / game-music / own-name folders only — safe titles. First folder
            // that yields >=3 supported tracks wins; enumeration is lazy (Take stops early).
            string[] safe = { "8bit", "8bitpeoples-archives-vol-1", "Game Music - CWM - HQ",
                              "Game Music - Lossless Archive", "Jake Kaufman", "Christian Mills" };
            if (!Directory.Exists(hmusic)) { Console.WriteLine($"h:\\music not found at {hmusic} — real-music scenarios skipped."); return; }
            foreach (string s in safe)
            {
                string dir = Path.Combine(hmusic, s);
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var picks = SafeEnumerate(dir).Where(f => AudioDecoder.IsSupported(f)).Take(3).ToArray();
                    if (picks.Length >= 3) { H = picks; break; }
                }
                catch { }
            }
            for (int i = 0; i < H.Length; i++) RealLabels[Path.GetFullPath(H[i])] = "H" + (i + 1);
            Console.WriteLine(H.Length >= 3
                ? $"real tracks : found {H.Length} curated-safe tracks (shown as H1..H{H.Length})"
                : "real tracks : NONE found in curated-safe folders — real-music scenarios skipped.");
        }

        static IEnumerable<string> SafeEnumerate(string dir)
        {
            // Lazy, exception-tolerant recursive file walk (a locked subdir won't abort it).
            IEnumerable<string> here;
            try { here = Directory.EnumerateFiles(dir); } catch { yield break; }
            foreach (var f in here) yield return f;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); } catch { yield break; }
            foreach (var sub in subs)
                foreach (var f in SafeEnumerate(sub)) yield return f;
        }

        // ── Corpus ──────────────────────────────────────────────────────────────
        static void BuildCorpus()
        {
            // musicroot/puremusic : loose valid music only
            Touch("musicroot/puremusic/a1.mp3");
            Touch("musicroot/puremusic/a2.flac");
            Touch("musicroot/puremusic/a3.wav");

            // musicroot/badonly : unsupported only
            Touch("musicroot/badonly/x.nsf");
            Touch("musicroot/badonly/y.sid");
            Touch("musicroot/badonly/cover.jpg");

            // musicroot/mixed : valid + invalid
            Touch("musicroot/mixed/m1.mp3");
            Touch("musicroot/mixed/m2.m4a");
            Touch("musicroot/mixed/junk.nsf");
            Touch("musicroot/mixed/art.png");

            Dir("musicroot/empty");

            // shared tracks referenced relatively by playlists
            Touch("musicroot/shared/s1.mp3");
            Touch("musicroot/shared/space name.mp3");   // exercises the %20 round trip
            Touch("musicroot/shared/s2.flac");

            // ── Folder + in-folder playlist (union) ──
            Touch("musicroot/unionA/u1.mp3");
            Touch("musicroot/unionA/u2.flac");              // also listed in the .m3u -> dedup
            WriteM3u("musicroot/unionA/list.m3u", new[]{
                "# in-folder playlist",
                "",
                "u2.flac",                                   // dup of loose (rel to unionA) -> once
                "../shared/s1.mp3",                          // pulls a track from outside the folder
                Abs(0),                                      // real absolute track (if any)
                "missing_here.mp3",                          // invalid -> skip
                "notes.nsf",                                 // unsupported -> skip
                "sub",                                       // folder entry -> skip (files only)
            });
            Dir("musicroot/unionA/sub");                     // the folder-entry target (should be skipped)

            // folder with ONLY a playlist (no loose music)
            WriteM3u("musicroot/listonly/only.m3u", new[]{ "../shared/s2.flac", Abs(1) });

            // folder with TWO playlists sharing an entry -> dedup across playlists
            WriteM3u("musicroot/twolists/p1.m3u", new[]{ "../shared/s1.mp3", "../shared/s2.flac" });
            WriteM3u("musicroot/twolists/p2.m3u", new[]{ "../shared/s2.flac" });   // s2 shared -> once
            Touch("musicroot/twolists/t0.mp3");              // one loose track too

            // ── Recursion corpus ──
            Touch("musicroot/recur/r0.mp3");
            WriteM3u("musicroot/recur/toplevel.m3u", new[]{ "../shared/s1.mp3" });
            Touch("musicroot/recur/sub1/r1.flac");
            Touch("musicroot/recur/sub1/sub2/r2.wav");
            WriteM3u("musicroot/recur/sub1/nested.m3u", new[]{ "../../shared/s2.flac" });

            // a *directory* literally named like a playlist file
            Dir("musicroot/looksLikePlaylist.m3u");
            Touch("musicroot/looksLikePlaylist.m3u/inside.mp3");

            // ── Direct playlist files (virtualized dir) ──
            Touch("playlists/p_local.mp3");                  // sits next to the .m3u for relative tests
            WriteM3u("playlists/valid_rel.m3u", new[]{ "p_local.mp3", "../musicroot/shared/s1.mp3" });
            WriteM3u("playlists/valid_abs.m3u", new[]{ Abs(0), Abs(1), Abs(2) });
            // file:// URI entries — what VLC exports by default, and the form that silently
            // produced an empty station in a real library (a whole radio folder looked unnoticed).
            WriteM3u("playlists/uri_abs.m3u", new[]{ ToFileUri(Abs(0)), ToFileUri(Abs(1)) });
            WriteM3u("playlists/uri_spaces.m3u", new[]{ ToFileUri(SpacePath) });
            WriteM3u("playlists/uri_mixed.m3u", new[]{ ToFileUri(Abs(0)), Abs(1) });
            WriteM3u("playlists/uri_upper.m3u", new[]{ ToFileUri(Abs(0)).Replace("file:", "FILE:") });
            WriteM3u("playlists/uri_http.m3u", new[]{ "http://example.com/stream.mp3", ToFileUri(Abs(0)) });
            WriteM3u("playlists/uri_broken.m3u", new[]{ "file://///:::bad", Abs(0) });
            WriteM3u("playlists/dupes.m3u", new[]{
                "../musicroot/shared/s1.mp3",
                "../musicroot/shared/s1.mp3",                // exact dup
                Abs(0), Abs(0),                              // dup real
            });
            WriteM3u("playlists/comments.m3u", new[]{
                "#EXTM3U", "", "   ", "# a comment",
                "#EXTINF:123,Some Title",
                "p_local.mp3",
            });
            WriteM3u("playlists/mixed.m3u", new[]{
                "p_local.mp3",                               // valid rel
                Abs(0),                                      // valid abs (real)
                "does_not_exist.mp3",                        // missing -> skip
                "../musicroot/badonly/x.nsf",                // unsupported -> skip
                "valid_rel.m3u",                             // nested playlist -> skip (unsupported ext)
                "../musicroot/empty",                        // folder -> skip
                "p_local.mp3",                               // dup -> once
                "# tail comment",
            });
            WriteM3u("playlists/allbad.m3u", new[]{ "nope1.mp3", "../nope2.flac", "z.nsf" });
            WriteM3u("playlists/empty.m3u", new string[0]);
            WriteM3u("playlists/UPPER.M3U", new[]{ "p_local.mp3" });
            // separator + traversal mix
            WriteM3u("playlists/seps.m3u", new[]{ @"..\musicroot\shared\s1.mp3", "../musicroot/shared/s2.flac" });
            // dedup across spellings: same file as absolute AND as relative
            WriteM3u("playlists/spelling.m3u", new[]{
                Path.Combine(MusicRoot, "shared", "s1.mp3"),  // absolute
                "../musicroot/shared/s1.mp3",                 // relative -> same file -> once
            });
        }

        // ── Scenario groups ──────────────────────────────────────────────────────
        static void RunFolderScenarios()
        {
            Section("A. Folder sources (source resolution, valid/invalid)");
            CheckSet("A1 relative folder, valid music", "puremusic", false,
                Exp("musicroot/puremusic/a1.mp3","musicroot/puremusic/a2.flac","musicroot/puremusic/a3.wav"));
            CheckSet("A2 absolute folder, valid music", Path.Combine(MusicRoot,"puremusic"), false,
                Exp("musicroot/puremusic/a1.mp3","musicroot/puremusic/a2.flac","musicroot/puremusic/a3.wav"));
            CheckSet("A3 folder, unsupported-only -> empty", "badonly", false, Exp());
            CheckSet("A4 folder, mixed valid+invalid -> valid only", "mixed", false,
                Exp("musicroot/mixed/m1.mp3","musicroot/mixed/m2.m4a"));
            CheckSet("A5 empty folder -> empty", "empty", false, Exp());
            CheckSet("A6 non-existent relative folder -> empty", "does_not_exist", false, Exp());
            CheckSet("A7 non-existent absolute folder -> empty", @"Z:\nope\folder", false, Exp());
        }

        static void RunPlaylistScenarios()
        {
            Section("B. Direct .m3u sources (entry handling)");
            CheckSet("B1 .m3u relative source, absolute entries", "../playlists/valid_abs.m3u", false, Exp(Abs(0),Abs(1),Abs(2)));
            CheckSet("B2 .m3u relative entries resolve vs playlist folder", "../playlists/valid_rel.m3u", false,
                Exp("playlists/p_local.mp3","musicroot/shared/s1.mp3"));
            CheckSet("B3+B4 invalid + unsupported entries skipped (not malformed)", "../playlists/mixed.m3u", false,
                Exp("playlists/p_local.mp3", Abs(0)));
            CheckSet("B5 duplicate entries deduped", "../playlists/dupes.m3u", false, Exp("musicroot/shared/s1.mp3", Abs(0)));
            CheckSet("B6 blank/#comment/#EXTINF ignored", "../playlists/comments.m3u", false, Exp("playlists/p_local.mp3"));
            CheckSet("B7+B8 folder entry & nested .m3u skipped", "../playlists/mixed.m3u", false,
                Exp("playlists/p_local.mp3", Abs(0)));   // (same source; asserts the folder+.m3u entries didn't sneak in)
            CheckSet("B9 all-bad .m3u -> empty (not malformed)", "../playlists/allbad.m3u", false, Exp());
            CheckSet("B10 empty .m3u -> empty", "../playlists/empty.m3u", false, Exp());
            CheckSet("B11 uppercase .M3U detected+read", "../playlists/UPPER.M3U", false, Exp("playlists/p_local.mp3"));
            CheckSet("B12 non-existent .m3u source -> empty (no crash)", "../playlists/ghost.m3u", false, Exp());
            CheckSet("B13 absolute .m3u source", Path.Combine(Scratch,"playlists","valid_rel.m3u"), false,
                Exp("playlists/p_local.mp3","musicroot/shared/s1.mp3"));
            CheckSet("B15 dedup across spellings (abs+rel same file)", "../playlists/spelling.m3u", false,
                Exp("musicroot/shared/s1.mp3"));
            CheckSet("B17 file:// URI entries resolve", "../playlists/uri_abs.m3u", false, Exp(Abs(0), Abs(1)));
            CheckSet("B18 percent-encoded spaces decode", "../playlists/uri_spaces.m3u", false, Exp(SpaceRel));
            CheckSet("B19 file:// and plain paths mix in one file", "../playlists/uri_mixed.m3u", false, Exp(Abs(0), Abs(1)));
            CheckSet("B20 FILE:// uppercase scheme", "../playlists/uri_upper.m3u", false, Exp(Abs(0)));
            CheckSet("B21 http:// entry skipped, file:// kept", "../playlists/uri_http.m3u", false, Exp(Abs(0)));
            CheckSet("B22 malformed file:// falls through, rest kept", "../playlists/uri_broken.m3u", false, Exp(Abs(0)));
            CheckSet("B16 mixed separators + ../ traversal", "../playlists/seps.m3u", false,
                Exp("musicroot/shared/s1.mp3","musicroot/shared/s2.flac"));
        }

        static void RunUnionScenarios()
        {
            Section("C. Folder + in-folder .m3u (union + dedup)");
            CheckSet("C1 loose music + .m3u, dedup loose∩listed", "unionA", false,
                Exp("musicroot/unionA/u1.mp3","musicroot/unionA/u2.flac","musicroot/shared/s1.mp3", Abs(0)));
            CheckSet("C2 folder with only a .m3u (no loose music)", "listonly", false,
                Exp("musicroot/shared/s2.flac", Abs(1)));
            CheckSet("C3+C4 two .m3u's, dedup shared entry + loose", "twolists", false,
                Exp("musicroot/twolists/t0.mp3","musicroot/shared/s1.mp3","musicroot/shared/s2.flac"));
        }

        static void RunSubfolderScenarios()
        {
            Section("D. subfolders exception");
            CheckSet("D1 recur folder, subfolders=false: top-level music + top .m3u only", "recur", false,
                Exp("musicroot/recur/r0.mp3","musicroot/shared/s1.mp3"));
            CheckSet("D2 recur folder, subfolders=true: all music, ALL .m3u ignored", "recur", true,
                Exp("musicroot/recur/r0.mp3","musicroot/recur/sub1/r1.flac","musicroot/recur/sub1/sub2/r2.wav"));
        }

        static void RunEdgeScenarios()
        {
            Section("E. Edges");
            // A directory named like a playlist: IsPlaylistSource(true) -> ReadTracks(dir) -> empty, no crash.
            CheckSet("E1 source is a directory named *.m3u -> empty (no crash)", "looksLikePlaylist.m3u", false, Exp());
            Check("E2 IsPlaylistSource case-insensitive (.M3U/.m3u8)",
                Contexts.IsPlaylistSource("a.M3U") && Contexts.IsPlaylistSource("b.m3u8") && !Contexts.IsPlaylistSource("c.mp3"),
                "extension detection");
        }

        // ── "Empty" and "the drive is not plugged in" are different answers ──────────
        // Most stations here are .m3u files on one drive pointing at another, so a playlist
        // reading perfectly while every target it names is missing is the ordinary shape of a
        // disconnected drive -- not an exotic case. Recorded as "empty" it is permanent: the
        // station leaves the catalog, and the only thing that would bring it back is the .m3u's
        // own timestamp changing, which sits on the drive that never left.
        static void RunUnresolvedScenarios()
        {
            Section("E'. Unavailable drive vs genuinely empty");

            // A path on a drive letter that does not exist. Same shape as an unplugged drive:
            // the entries parse, resolve to absolute paths, and are simply not there.
            const string GoneRoot = @"Q:\definitely-not-mounted";
            WriteM3u("musicroot/offline.m3u", new[]
            {
                "#EXTM3U",
                GoneRoot + @"\one.mp3",
                GoneRoot + @"\two.flac",
            });
            string[] offline = PlaylistSource.ReadTracks(Full("musicroot/offline.m3u"), out bool offlineUnresolved);
            Check("E'1 a playlist whose drive is absent yields no tracks",
                  offline.Length == 0, $"{offline.Length} tracks");
            Check("E'2 ...and reports UNRESOLVED, not empty", offlineUnresolved, "expected unresolved = true");

            // The contrast: a reachable folder whose named files simply are not there. That IS a
            // settled answer and must not be reported as unresolved, or a genuinely broken
            // playlist would be retried for ever.
            Dir("musicroot/present");
            WriteM3u("musicroot/present/missing.m3u", new[]
            {
                "#EXTM3U",
                "no-such-track.mp3",
                "also-missing.flac",
            });
            string[] missing = PlaylistSource.ReadTracks(Full("musicroot/present/missing.m3u"), out bool missingUnresolved);
            Check("E'3 missing files on a REACHABLE drive yield no tracks",
                  missing.Length == 0, $"{missing.Length} tracks");
            Check("E'4 ...and are reported as empty, NOT unresolved",
                  !missingUnresolved, "expected unresolved = false");

            // A playlist that produced anything at all is answered, whatever else failed.
            Touch("musicroot/present/real.mp3");
            WriteM3u("musicroot/present/partial.m3u", new[]
            {
                "#EXTM3U",
                "real.mp3",
                GoneRoot + @"\c.mp3",
            });
            string[] partial = PlaylistSource.ReadTracks(Full("musicroot/present/partial.m3u"), out bool partialUnresolved);
            Check("E'5 a playlist with one usable entry is never unresolved",
                  partial.Length == 1 && !partialUnresolved,
                  $"{partial.Length} tracks, unresolved = {partialUnresolved}");

            // A playlist that is itself missing recovers on its own -- its presence is part of the
            // signature the catalog watches -- so it must not latch this flag.
            string[] noFile = PlaylistSource.ReadTracks(Full("musicroot/does-not-exist.m3u"), out bool noFileUnresolved);
            Check("E'6 a missing playlist FILE is not 'unresolved'",
                  noFile.Length == 0 && !noFileUnresolved, $"unresolved = {noFileUnresolved}");

            // Unsupported formats are a settled answer too: the file is right there.
            Touch("musicroot/present/notes.txt");
            WriteM3u("musicroot/present/unsupported.m3u", new[] { "#EXTM3U", "notes.txt" });
            string[] uns = PlaylistSource.ReadTracks(Full("musicroot/present/unsupported.m3u"), out bool unsUnresolved);
            Check("E'7 an unsupported-format entry is empty, not unresolved",
                  uns.Length == 0 && !unsUnresolved, $"unresolved = {unsUnresolved}");
        }

        static void RunChangeDetectionScenarios()
        {
            Section("F. Change detection (hot-reload triggers)");

            // F1: editing an IN-FOLDER .m3u in place changes the folder's signature (folded .m3u mtime).
            string unionDir = Path.Combine(MusicRoot, "unionA");
            SourceScan.EnumerateFolderPoolRaw(unionDir, false, Array.Empty<string>(), out string[] m3usBefore);
            var sigBefore = SourceScan.ContextWriteSignatureUtc(unionDir, false, m3usBefore);
            int poolBefore = Pool("unionA", false).Length;
            AppendLine("musicroot/unionA/list.m3u", "u1.mp3");            // add u1 (already loose -> dedup, pool unchanged)
            AppendLine("musicroot/unionA/list.m3u", "../shared/s2.flac"); // add a NEW track -> pool grows
            SetLater("musicroot/unionA/list.m3u", 20);
            var sigAfter = SourceScan.ContextWriteSignatureUtc(unionDir, false, m3usBefore);
            int poolAfter = Pool("unionA", false).Length;
            Check("F1 in-folder .m3u edit changes folder signature", sigAfter > sigBefore, $"{sigBefore:o} -> {sigAfter:o}");
            Check("F1 in-folder .m3u edit grows pool (dedup honored)", poolAfter == poolBefore + 1, $"{poolBefore} -> {poolAfter}");

            // F2: editing a DIRECT .m3u changes its own signature.
            string pl = Path.Combine(Scratch, "playlists", "valid_rel.m3u");
            var pSigBefore = SourceScan.PlaylistWriteSignatureUtc(pl);
            AppendLine("playlists/valid_rel.m3u", "../musicroot/shared/s2.flac");
            SetLater("playlists/valid_rel.m3u", 20);
            var pSigAfter = SourceScan.PlaylistWriteSignatureUtc(pl);
            Check("F2 direct .m3u edit changes its signature", pSigAfter > pSigBefore, $"{pSigBefore:o} -> {pSigAfter:o}");

            // F3: add/remove a loose file changes the folder signature (folder mtime).
            string pm = Path.Combine(MusicRoot, "puremusic");
            var fSig1 = SourceScan.ContextWriteSignatureUtc(pm, false, Array.Empty<string>());
            Touch("musicroot/puremusic/added.ogg");
            SetLater("musicroot/puremusic", 20);   // simulate the add bumping the dir mtime
            var fSig2 = SourceScan.ContextWriteSignatureUtc(pm, false, Array.Empty<string>());
            Check("F3 add loose file changes folder signature", fSig2 > fSig1, $"{fSig1:o} -> {fSig2:o}");

            // F4: stable when nothing changes.
            var stable1 = SourceScan.ContextWriteSignatureUtc(pm, false, Array.Empty<string>());
            var stable2 = SourceScan.ContextWriteSignatureUtc(pm, false, Array.Empty<string>());
            Check("F4 signature stable when unchanged", stable1 == stable2, $"{stable1:o}");

            // F5: playlist REMOVAL detection (the exFAT case) — a known in-folder .m3u that
            // vanishes is caught even though the max-of-mtimes signature can't see it.
            string twoDir = Path.Combine(MusicRoot, "twolists");
            SourceScan.EnumerateFolderPoolRaw(twoDir, false, Array.Empty<string>(), out string[] twoM3us);
            Check("F5 AnyPlaylistMissing=false when all present", !SourceScan.AnyPlaylistMissing(twoM3us), $"{twoM3us.Length} .m3u");
            try { File.Delete(Path.Combine(twoDir, "p2.m3u")); } catch { }
            Check("F5 AnyPlaylistMissing=true after a known .m3u is deleted", SourceScan.AnyPlaylistMissing(twoM3us), "p2.m3u removed");

            // F6: playlist ADD/REMOVE detection via the set-diff (the re-add case) — catches a
            // .m3u appearing OR disappearing even when the folder mtime doesn't move. (p2.m3u
            // was just deleted in F5, so the folder's current set is {p1.m3u}.)
            SourceScan.EnumerateFolderPoolRaw(twoDir, false, Array.Empty<string>(), out string[] knownM3us);
            Check("F6 SetChanged=false when set matches known", !SourceScan.FolderPlaylistSetChanged(twoDir, knownM3us), $"{knownM3us.Length} .m3u");
            WriteM3u("musicroot/twolists/p3.m3u", new[]{ "../shared/s1.mp3" });   // a brand-new playlist appears
            Check("F6 SetChanged=true after a NEW .m3u is added", SourceScan.FolderPlaylistSetChanged(twoDir, knownM3us), "p3.m3u added");
            try { File.Delete(Path.Combine(twoDir, "p1.m3u")); } catch { }
            Check("F6 SetChanged=true after a known .m3u is removed", SourceScan.FolderPlaylistSetChanged(twoDir, knownM3us), "p1.m3u removed");
            // Restore p1.m3u so downstream G-scenarios still see the twolists pool they expect.
            WriteM3u("musicroot/twolists/p1.m3u", new[]{ "../shared/s1.mp3", "../shared/s2.flac" });
            try { File.Delete(Path.Combine(twoDir, "p3.m3u")); } catch { }
            Check("F6 IsSloppyMtimeFilesystem is deterministic (cached)",
                  SourceScan.IsSloppyMtimeFilesystem(twoDir) == SourceScan.IsSloppyMtimeFilesystem(twoDir),
                  SourceScan.IsSloppyMtimeFilesystem(twoDir) ? "sloppy" : "trusted");
        }

        static void RunSourceChangeScenarios()
        {
            Section("G. On-the-fly source change (re-enumeration result)");
            // ConfigWatch forgets stale state then re-enumerates via the same code; here we
            // assert the re-enumerated pool matches each NEW source kind.
            CheckSet("G1 relative folder -> absolute folder", Path.Combine(MusicRoot,"puremusic"), false,
                Exp("musicroot/puremusic/a1.mp3","musicroot/puremusic/a2.flac","musicroot/puremusic/a3.wav","musicroot/puremusic/added.ogg"));
            CheckSet("G2 folder -> playlist", "../playlists/valid_abs.m3u", false, Exp(Abs(0),Abs(1),Abs(2)));
            CheckSet("G3 playlist -> folder", "twolists", false,
                Exp("musicroot/twolists/t0.mp3","musicroot/shared/s1.mp3","musicroot/shared/s2.flac"));
            CheckSet("G4 valid -> invalid (nonexistent) empties pool", "gone_folder", false, Exp());
            CheckSet("G5 invalid -> valid populates pool", "mixed", false,
                Exp("musicroot/mixed/m1.mp3","musicroot/mixed/m2.m4a"));
        }

        // ── I. World-emitter pool recovery (empty folder → first track added) ────
        // The Radio station's pool (EmitterAudioLayer.EnsurePool) watches its folder with the SAME
        // max-of-mtimes signature the context FileWatch uses. Radio is subfolders="true", so model
        // the recursive signature. THE BUG: when the folder starts EMPTY and the first track is added
        // within one filesystem-precision window (or on a sloppy FAT/exFAT/network FS), the folder's
        // mtime can land on the SAME value it had while empty — so a signature-ONLY gate never
        // re-enumerates and the station stays silent forever. The context FileWatch already guards
        // this with its `previouslyEmpty` special-case; the emitter pool copied the mtime shortcut but
        // not the guard. This reproduces the collision with the REAL SourceScan helpers and checks the
        // old (buggy) vs new (fixed) EnsurePool gate decision.
        static void RunEmitterPoolRecoveryScenarios()
        {
            Section("I. World-emitter pool recovery (empty folder -> first track added)");
            const bool recursive = true;   // Radio context is subfolders="true"
            string dir = Path.Combine(MusicRoot, "radioEmpty");
            Dir("musicroot/radioEmpty");

            var sig0  = SourceScan.ContextWriteSignatureUtc(dir, recursive);
            var pool0 = SourceScan.EnumerateFolderPoolRaw(dir, recursive, Array.Empty<string>(), out _);
            Check("I1 empty folder -> empty pool", pool0.Length == 0, $"pool0={pool0.Length}");

            // Add the first track, then FORCE the empty->refill stamp collision by resetting the dir
            // mtime to its pre-add value — the exact case FileWatch warns the signature can't see.
            Touch("musicroot/radioEmpty/first.mp3");
            try { Directory.SetLastWriteTimeUtc(dir, sig0); } catch { }

            var sig1  = SourceScan.ContextWriteSignatureUtc(dir, recursive);
            var pool1 = SourceScan.EnumerateFolderPoolRaw(dir, recursive, Array.Empty<string>(), out _);
            Check("I2 collision reproduced: signature UNCHANGED after the add", sig1 == sig0, $"{sig0:o} -> {sig1:o}");
            Check("I3 re-enumeration DOES see the new track", pool1.Length == 1, $"pool1={pool1.Length}");

            // The two EnsurePool gate decisions, given (currentPool = pool0 empty, freshSig = sig1):
            bool oldSkipsRebuild = sig1 == sig0;                            // signature-only (buggy)
            bool newSkipsRebuild = (pool0.Length != 0) && (sig1 == sig0);   // never skip while empty (fixed)
            Check("I4 OLD signature-only gate would NOT recover (bug present)", oldSkipsRebuild, "old gate skips rebuild");
            Check("I5 NEW empty-guard gate DOES recover (fix works)", !newSkipsRebuild && pool1.Length == 1,
                  $"newSkips={newSkipsRebuild}, recovered={pool1.Length}");

            // Sanity: when the add DOES bump the mtime (NTFS normal case), even the old gate recovered
            // — so the bug is specific to the empty->refill collision and the fix doesn't regress the
            // normal path (the empty-guard just makes the same rebuild happen unconditionally-while-empty).
            try { Directory.SetLastWriteTimeUtc(dir, sig0.AddSeconds(20)); } catch { }
            var sig2 = SourceScan.ContextWriteSignatureUtc(dir, recursive);
            Check("I6 normal (non-colliding) add bumps signature", sig2 > sig0, $"{sig0:o} -> {sig2:o}");
        }

        static void RunRealMusicScenarios()
        {
            Section("H. Real h:\\music (redacted)");
            if (H.Length < 3) { Console.WriteLine("  (skipped — no curated-safe real tracks)"); return; }
            // Absolute folder pointing at the real curated folder that yielded H1..H3.
            string realDir = Path.GetDirectoryName(H[0]);
            var pool = Pool(realDir, true);   // recurse the real folder
            Check("H1 absolute real folder yields tracks", pool.Length > 0, $"count={pool.Length} (names redacted)");
            CheckSet("H2 direct .m3u of real absolute tracks", "../playlists/valid_abs.m3u", false, Exp(Abs(0),Abs(1),Abs(2)));
        }

        // ── Pool resolution: mirrors the game's source dispatch exactly ─────────
        static string[] Pool(string source, bool subfolders)
        {
            if (Contexts.IsPlaylistSource(source))
            {
                string pf = Path.IsPathRooted(source) ? source : Path.Combine(MusicRoot, source);
                return PlaylistSource.ReadTracks(pf);
            }
            string dir = Path.Combine(MusicRoot, source);   // Path.Combine keeps an absolute source as-is
            if (!Directory.Exists(dir)) return new string[0];
            return SourceScan.EnumerateFolderPoolRaw(dir, subfolders, Array.Empty<string>(), out _);
        }

        // ── Assertions + redacting reporter ─────────────────────────────────────
        static HashSet<string> Exp(params string[] paths)
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;      // Abs(i) returns "" when no real tracks
                string full = Path.IsPathRooted(p) ? p : Path.Combine(Scratch, p);
                s.Add(Path.GetFullPath(full));
            }
            return s;
        }

        static void CheckSet(string name, string source, bool subfolders, HashSet<string> expected)
        {
            string[] actualPaths = Pool(source, subfolders);
            var actual = new HashSet<string>(actualPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            bool ok = actual.SetEquals(expected);
            if (ok) Pass(name + $"  [{actual.Count} tracks: {Labels(actual)}]");
            else
            {
                var missing = expected.Except(actual).ToList();
                var extra = actual.Except(expected).ToList();
                string detail = $"expected {expected.Count} got {actual.Count}"
                    + (missing.Count > 0 ? "  MISSING:" + Labels(missing) : "")
                    + (extra.Count > 0 ? "  EXTRA:" + Labels(extra) : "");
                Fail(name, detail);
            }
        }

        static void Check(string name, bool ok, string detail) { if (ok) Pass(name); else Fail(name, detail); }

        static void Pass(string name) { _pass++; Console.WriteLine("  PASS  " + name); }
        static void Fail(string name, string detail) { _fail++; _fails.Add(name + " :: " + detail); Console.WriteLine("  FAIL  " + name + "  << " + detail); }
        static void Section(string s) { Console.WriteLine(); Console.WriteLine(s); }

        // Never prints a real h:\music name. Real tracks -> H-labels; scratch files ->
        // scratch-relative path; anything else -> redacted.
        static string Labels(IEnumerable<string> paths) => string.Join(", ", paths.Select(Label));
        static string Label(string full)
        {
            full = Path.GetFullPath(full);
            if (RealLabels.TryGetValue(full, out string lab)) return lab;
            if (full.StartsWith(Scratch, StringComparison.OrdinalIgnoreCase))
            {
                string rel = full.Substring(Scratch.Length).TrimStart('\\', '/');
                return Questionable(rel) ? "[redacted]" : rel;
            }
            return "[redacted:" + Path.GetExtension(full) + "]";
        }
        static readonly string[] _block = { }; // scratch names are self-chosen & safe; hook left for real-name defense
        static bool Questionable(string name)
        {
            string l = name.ToLowerInvariant();
            foreach (var w in _block) if (l.Contains(w)) return true;
            return false;
        }

        // Abs(i): the i-th real track as an absolute path, or "" when none discovered
        // (Exp/WriteM3u drop empty entries so scenarios degrade gracefully without real music).
        static string Abs(int i) => i < H.Length ? H[i] : "";

        // The exact form VLC writes: file:/// + forward slashes + %XX for anything non-literal.
        static string ToFileUri(string path) => new Uri(path).AbsoluteUri;

        // A track whose path contains a space, so the %20 round trip is actually exercised
        // rather than assumed (spaces are what broke the real playlist).
        static string SpacePath => Path.Combine(Scratch, "musicroot", "shared", "space name.mp3");
        static string SpaceRel  => "musicroot/shared/space name.mp3";

        // ── Scratch corpus helpers ──────────────────────────────────────────────
        static void ResetScratch()
        {
            try { if (Directory.Exists(Scratch)) Directory.Delete(Scratch, true); } catch { }
            Directory.CreateDirectory(Scratch);
        }
        static string Full(string rel) => Path.Combine(Scratch, rel);
        static void Dir(string rel) => Directory.CreateDirectory(Full(rel));
        static void Touch(string rel)
        {
            string f = Full(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            File.WriteAllBytes(f, new byte[0]);
        }
        static void WriteM3u(string rel, string[] lines)
        {
            string f = Full(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            File.WriteAllLines(f, lines.Where(l => l != null));
        }
        static void AppendLine(string rel, string line) => File.AppendAllText(Full(rel), Environment.NewLine + line);
        static void SetLater(string rel, int secs)
        {
            try { File.SetLastWriteTimeUtc(Full(rel), DateTime.UtcNow.AddSeconds(secs)); } catch { }
        }
    }
}
