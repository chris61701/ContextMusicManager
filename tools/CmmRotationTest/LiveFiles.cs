using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// THE SAME QUESTIONS, ON A REAL FILESYSTEM.
//
// Everything in Program.cs and EdgeCases.cs hands the rotation an array. That is
// the right way to test the algorithm and the wrong way to believe it works,
// because in the game the array is produced by SourceScan walking a disk that is
// changing underneath it. So this builds a real tree — a flat folder, a recursive
// one, a playlist, and two add-on packs — enumerates it with the SHIPPED scan
// code, and then moves, renames, adds and deletes files between picks.
//
// The pool arrays here are the ones the game would actually get, including the
// add-on merge and the claim rule.
// ─────────────────────────────────────────────────────────────────────────────
internal static class LiveFiles
{
    static void Check(string w, bool ok, string d = null) => Program.Check(w, ok, d);
    static TrackRng Rng(int s) => Program.Rng(s);

    static string _root;

    internal static void Run()
    {
        _root = Path.Combine(Path.GetTempPath(), "CmmRotationLive_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Build();
            Program.Section("Real files: a track moved while it is playing");
            PlayingTrackMovedOut();
            PreStagedTrackMovedBeforeItPlays();

            Program.Section("Real files: a folder still being crawled");
            RecursiveFolderStillBeingScanned();

            Program.Section("Real files: adding, deleting, renaming under a running pass");
            FilesAddedMidPass();
            FilesDeletedMidPass();
            CaseOnlyRenameOnDisk();
            WholeFolderMovedAwayAndBack();

            Program.Section("Real files: playlists and add-on packs");
            PlaylistEditedMidPass();
            AddOnPackArrivesMidPass();
            AddOnPackRemovedMidPass();
        }
        finally
        {
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    // ── The tree ─────────────────────────────────────────────────────────────

    static string Primary => Path.Combine(_root, "primary");
    static string PackA    => Path.Combine(_root, "PackA", "Music");
    static string PackB    => Path.Combine(_root, "PackB", "Music");

    static void Build()
    {
        // A flat context, a recursive one with real depth, and a playlist.
        MakeTracks(Path.Combine(Primary, "ambient"), "amb", 12);
        MakeTracks(Path.Combine(Primary, "poi"), "poi", 4);
        MakeTracks(Path.Combine(Primary, "poi", "t3"), "t3", 6);
        MakeTracks(Path.Combine(Primary, "poi", "t5", "deep"), "deep", 5);
        MakeTracks(Path.Combine(Primary, "listed"), "lst", 8);

        MusicRoots.SetPrimary(Primary);
        MusicRoots.SetAddons(new List<MusicRoot>());   // packs arrive later, deliberately
    }

    static void MakeTracks(string dir, string prefix, int n)
    {
        Directory.CreateDirectory(dir);
        for (int i = 0; i < n; i++)
            File.WriteAllBytes(Path.Combine(dir, $"{prefix}_{i:D2}.ogg"), new byte[] { 1, 2, 3, 4 });
    }

    // The pool the game would build for a flat context, add-on roots merged in.
    static string[] FlatPool(string source)
    {
        string[] dirs = MusicRoots.All.Select(r => MusicRoots.Combine(r.Path, source)).ToArray();
        return SourceScan.EnumerateFlatPoolMulti(dirs, SourceScan._noClaimPrefixes, out _);
    }

    // The pool for a recursive context: the primary is walked deep, add-ons stay flat.
    static string[] RecursivePool(string source, params string[] claimed)
    {
        var all = new List<string>();
        MusicRoot[] roots = MusicRoots.All;
        for (int i = 0; i < roots.Length; i++)
        {
            string dir = MusicRoots.Combine(roots[i].Path, source);
            if (!Directory.Exists(dir)) continue;
            all.AddRange(SourceScan.EnumerateFolderPoolRaw(dir, i == 0, claimed, out _));
        }
        return all.ToArray();
    }

    // ── A track moved while playing ──────────────────────────────────────────

    static void PlayingTrackMovedOut()
    {
        string[] pool = FlatPool("ambient");
        var rot = new TrackRotation();
        var played = new List<string>();
        string last = null;
        for (int i = 0; i < 5; i++)
        {
            string p = rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, Rng(1000 + i));
            rot.Commit(p, TrackRotation.WholePool);
            played.Add(p); last = p;
        }

        // Move the file that is "playing" out of the folder, exactly as the hot-swap path
        // finds it: gone from disk, still named by lastPlayedFile.
        string playing = last;
        string moved = Path.Combine(_root, "moved_" + Path.GetFileName(playing));
        File.Move(playing, moved);
        Check("the playing file really is gone from disk", !File.Exists(playing));

        // PickExistingReplacement picks up to 8 times and throws away every miss. None of
        // those may spend a slot — that is the whole reason the commit is at play time.
        string[] pruned = FlatPool("ambient");
        int playsBefore = rot.Plays;
        for (int attempt = 0; attempt < 8; attempt++)
            rot.Select(pruned, TrackRotation.WholePool, playing, null, null, 3f, Rng(1100 + attempt));
        Check("the 8-attempt replacement walk spends no slots",
              rot.Plays == playsBefore, $"{rot.Plays} vs {playsBefore}");

        string repl = rot.Select(pruned, TrackRotation.WholePool, playing, null, null, 3f, Rng(1200));
        rot.Commit(repl, TrackRotation.WholePool);
        Check("the replacement is a track that still exists", File.Exists(repl), repl);
        Check("and one that has not played this pass", !played.Take(5).Contains(repl), repl);

        // Finish the pass. 12 tracks, one gone, 6 played -> 5 owed before the rollover.
        var rest = new List<string>();
        string cur = repl;
        for (int i = 0; i < 5; i++)
        {
            string p = rot.Select(pruned, TrackRotation.WholePool, cur, null, null, 3f, Rng(1300 + i));
            rot.Commit(p, TrackRotation.WholePool); rest.Add(p); cur = p;
        }
        Check("the pass finishes over the 11 that remain, with no repeat",
              played.Take(5).Concat(new[] { repl }).Concat(rest).Distinct().Count() == 11,
              played.Take(5).Concat(new[] { repl }).Concat(rest).Distinct().Count().ToString());
        Check("the moved file never comes back as a pick", !rest.Contains(playing));

        File.Move(moved, playing);   // put it back for the later tests
    }

    static void PreStagedTrackMovedBeforeItPlays()
    {
        // The pre-staged next track is chosen while the current one is still playing, so
        // there is a whole track's worth of time in which it can be moved away.
        string[] pool = FlatPool("ambient");
        var rot = new TrackRotation();
        string staged = rot.Select(pool, TrackRotation.WholePool, null, null, null, 3f, Rng(1400));
        string moved = Path.Combine(_root, "staged_" + Path.GetFileName(staged));
        File.Move(staged, moved);

        string[] after = FlatPool("ambient");
        Check("the staged file has left the pool", !after.Contains(staged));
        Check("nothing was spent on the pick that never played", rot.Plays == 0);
        string actual = rot.Select(after, TrackRotation.WholePool, null, null, null, 3f, Rng(1401));
        rot.Commit(actual, TrackRotation.WholePool);
        Check("the context plays something that exists instead", File.Exists(actual), actual);
        File.Move(moved, staged);
    }

    // ── Still being crawled ──────────────────────────────────────────────────

    static void RecursiveFolderStillBeingScanned()
    {
        // RecursiveCrawlLoop publishes a bigger snapshot every so often. Reproduced here
        // with the REAL enumerator over progressively deeper reach, which is what the
        // partial publishes amount to: poi/ alone, then poi + t3, then the whole tree.
        string[] shallow = SourceScan.EnumerateFolderPoolRaw(
            Path.Combine(Primary, "poi"), false, SourceScan._noClaimPrefixes, out _);
        string[] full = SourceScan.EnumerateFolderPoolRaw(
            Path.Combine(Primary, "poi"), true, SourceScan._noClaimPrefixes, out _);
        Check("the partial publish is a strict subset of the finished one",
              shallow.Length == 4 && full.Length == 15 && shallow.All(full.Contains),
              $"{shallow.Length} then {full.Length}");

        var rot = new TrackRotation();
        var played = new List<string>();
        string last = null;
        for (int i = 0; i < 4; i++)     // play the whole partial snapshot
        {
            string p = rot.Select(shallow, TrackRotation.WholePool, last, null, null, 3f, Rng(1500 + i));
            rot.Commit(p, TrackRotation.WholePool); played.Add(p); last = p;
        }
        Check("the partial snapshot is swept without repeating", played.Distinct().Count() == 4);
        Check("but the pass has NOT rolled over — there is more coming", rot.Cycle == 1,
              "cycle " + rot.Cycle);

        for (int i = 0; i < 11; i++)    // the finished snapshot lands mid-pass
        {
            string p = rot.Select(full, TrackRotation.WholePool, last, null, null, 3f, Rng(1600 + i));
            rot.Commit(p, TrackRotation.WholePool); played.Add(p); last = p;
        }
        Check("the pass continues into the deeper tracks and covers all 15 exactly once",
              played.Count == 15 && played.Distinct().Count() == 15,
              played.Distinct().Count() + " distinct of " + played.Count);
        Check("the tracks the crawler had not reached yet all got a turn",
              full.Except(shallow).All(played.Contains));
    }

    // ── Adding, deleting, renaming ───────────────────────────────────────────

    static void FilesAddedMidPass()
    {
        string dir = Path.Combine(Primary, "ambient");
        string[] pool = FlatPool("ambient");
        var rot = new TrackRotation();
        var played = new List<string>();
        string last = null;
        for (int i = 0; i < 6; i++)
        {
            string p = rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, Rng(1700 + i));
            rot.Commit(p, TrackRotation.WholePool); played.Add(p); last = p;
        }
        for (int i = 0; i < 3; i++)
            File.WriteAllBytes(Path.Combine(dir, $"added_{i}.ogg"), new byte[] { 9 });

        string[] grown = FlatPool("ambient");
        Check("the watch picks up the three new files", grown.Length == pool.Length + 3,
              grown.Length.ToString());
        var rest = new List<string>();
        for (int i = 0; i < 9; i++)
        {
            string p = rot.Select(grown, TrackRotation.WholePool, last, null, null, 3f, Rng(1800 + i));
            rot.Commit(p, TrackRotation.WholePool); rest.Add(p); last = p;
        }
        Check("all three new tracks get a turn in the pass already running",
              grown.Except(pool).All(rest.Contains));
        Check("and nothing repeats while they do",
              played.Concat(rest).Distinct().Count() == 15,
              played.Concat(rest).Distinct().Count().ToString());
        foreach (string f in Directory.GetFiles(dir, "added_*.ogg")) File.Delete(f);
    }

    static void FilesDeletedMidPass()
    {
        string dir = Path.Combine(Primary, "ambient");
        string[] pool = FlatPool("ambient");
        var rot = new TrackRotation();
        string last = null;
        for (int i = 0; i < 4; i++)
        {
            string p = rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, Rng(1900 + i));
            rot.Commit(p, TrackRotation.WholePool); last = p;
        }
        // Delete four tracks that have NOT played yet — the case that could strand a pass
        // waiting for tracks that will never arrive.
        var backup = new List<(string path, byte[] data)>();
        foreach (string f in pool.Where(p => p != last).Take(4))
        {
            backup.Add((f, File.ReadAllBytes(f)));
            File.Delete(f);
        }
        string[] shrunk = FlatPool("ambient");
        var rest = new List<string>();
        for (int i = 0; i < 30; i++)
        {
            string p = rot.Select(shrunk, TrackRotation.WholePool, last, null, null, 3f, Rng(2000 + i));
            rot.Commit(p, TrackRotation.WholePool); rest.Add(p); last = p;
        }
        Check("deleting unplayed tracks does not strand the pass", rot.Cycle >= 4,
              "cycle " + rot.Cycle);
        Check("and none of the deleted files is ever picked",
              !rest.Any(p => backup.Any(b => b.path == p)));
        foreach (var b in backup) File.WriteAllBytes(b.path, b.data);
    }

    static void CaseOnlyRenameOnDisk()
    {
        string dir = Path.Combine(Primary, "ambient");
        string[] pool = FlatPool("ambient");
        var rot = new TrackRotation();
        string target = pool.First();
        rot.Commit(target, TrackRotation.WholePool);

        string upper = Path.Combine(dir, Path.GetFileName(target).ToUpperInvariant());
        string tmp = target + ".tmp";
        File.Move(target, tmp); File.Move(tmp, upper);   // two-step: Windows needs it

        string[] renamed = FlatPool("ambient");
        string[] elig = rot.Eligible(renamed, TrackRotation.WholePool, null, null, null, 1);
        Check("a case-only rename on disk is still the same played track",
              elig.Length == renamed.Length - 1, $"{elig.Length} of {renamed.Length}");

        File.Move(upper, tmp); File.Move(tmp, target);
    }

    static void WholeFolderMovedAwayAndBack()
    {
        string dir = Path.Combine(Primary, "listed");
        string[] pool = FlatPool("listed");
        var rot = new TrackRotation();
        string last = null;
        for (int i = 0; i < 4; i++)
        {
            string p = rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, Rng(2100 + i));
            rot.Commit(p, TrackRotation.WholePool); last = p;
        }
        string stash = Path.Combine(_root, "listed_stashed");
        Directory.Move(dir, stash);
        string[] gone = FlatPool("listed");
        Check("a folder that has been moved away enumerates as empty", gone.Length == 0,
              gone.Length.ToString());
        Check("and selecting from it returns nothing rather than throwing",
              rot.Select(gone, TrackRotation.WholePool, last, null, null, 3f, Rng(2200)) == null);

        Directory.Move(stash, dir);
        string[] back = FlatPool("listed");
        Check("moving it back restores the pool", back.Length == pool.Length, back.Length.ToString());
        string[] elig = rot.Eligible(back, TrackRotation.WholePool, null, null, null, 1);
        Check("and the pass picks up where it left off — the 4 played are still spent",
              elig.Length == pool.Length - 4, elig.Length.ToString());
    }

    // ── Playlists and packs ──────────────────────────────────────────────────

    static void PlaylistEditedMidPass()
    {
        string m3u = Path.Combine(Primary, "mix.m3u");
        string[] listed = Directory.GetFiles(Path.Combine(Primary, "listed"), "*.ogg")
                                   .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        File.WriteAllLines(m3u, listed.Take(5));

        string[] pool = PlaylistSource.ReadTracks(m3u);
        Check("the playlist reads back its five tracks", pool.Length == 5, pool.Length.ToString());

        var rot = new TrackRotation();
        string last = null;
        for (int i = 0; i < 3; i++)
        {
            string p = rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, Rng(2300 + i));
            rot.Commit(p, TrackRotation.WholePool); last = p;
        }
        // Rewrite it: two of the five are dropped, three new ones appended.
        File.WriteAllLines(m3u, listed.Skip(2).Take(6));
        string[] edited = PlaylistSource.ReadTracks(m3u);
        string[] elig = rot.Eligible(edited, TrackRotation.WholePool, null, null, null, 1);
        Check("an edited playlist is re-read and its new entries are eligible at once",
              edited.Except(pool).All(elig.Contains), string.Join(",", edited.Except(pool).Select(Path.GetFileName)));
        Check("tracks that survived the edit and had already played are still spent",
              elig.Length < edited.Length, $"{elig.Length} of {edited.Length}");

        // A playlist pointed at nothing must fail, not fall back — the shipped rule.
        File.WriteAllLines(m3u, new[] { Path.Combine(Primary, "does_not_exist.ogg") });
        Check("a playlist whose entries are all missing reads as empty",
              PlaylistSource.ReadTracks(m3u).Length == 0);
    }

    static void AddOnPackArrivesMidPass()
    {
        // A music pack installed while the game runs, found by `cmm scan`. Its tracks
        // merge into the same context and must join the pass already in progress.
        string[] before = FlatPool("ambient");
        var rot = new TrackRotation();
        string last = null;
        for (int i = 0; i < 6; i++)
        {
            string p = rot.Select(before, TrackRotation.WholePool, last, null, null, 3f, Rng(2400 + i));
            rot.Commit(p, TrackRotation.WholePool); last = p;
        }

        MakeTracks(Path.Combine(PackA, "ambient"), "packA", 5);
        MusicRoots.SetAddons(new List<MusicRoot> { new MusicRoot { Path = PackA, Label = "PackA" } });
        string[] merged = FlatPool("ambient");
        Check("the pack's tracks merge into the same context",
              merged.Length == before.Length + 5, merged.Length.ToString());

        var rest = new List<string>();
        for (int i = 0; i < 11; i++)
        {
            string p = rot.Select(merged, TrackRotation.WholePool, last, null, null, 3f, Rng(2500 + i));
            rot.Commit(p, TrackRotation.WholePool); rest.Add(p); last = p;
        }
        Check("every one of the pack's tracks gets a turn in the running pass",
              merged.Except(before).All(rest.Contains));
        Check("the pass ends exactly when the merged folder is exhausted",
              rot.Cycle == 1 && rest.Distinct().Count() == 11,
              "cycle " + rot.Cycle + ", " + rest.Distinct().Count() + " distinct");
    }

    static void AddOnPackRemovedMidPass()
    {
        MakeTracks(Path.Combine(PackB, "ambient"), "packB", 4);
        MusicRoots.SetAddons(new List<MusicRoot>
        {
            new MusicRoot { Path = PackA, Label = "PackA" },
            new MusicRoot { Path = PackB, Label = "PackB" },
        });
        string[] pool = FlatPool("ambient");
        var rot = new TrackRotation();
        string last = null;
        for (int i = 0; i < 5; i++)
        {
            string p = rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, Rng(2600 + i));
            rot.Commit(p, TrackRotation.WholePool); last = p;
        }
        Directory.Delete(Path.Combine(_root, "PackB"), true);
        string[] after = FlatPool("ambient");
        Check("uninstalling a pack removes its tracks from the pool",
              after.Length == pool.Length - 4, after.Length.ToString());
        var rest = new List<string>();
        for (int i = 0; i < 40; i++)
        {
            string p = rot.Select(after, TrackRotation.WholePool, last, null, null, 3f, Rng(2700 + i));
            rot.Commit(p, TrackRotation.WholePool); rest.Add(p); last = p;
        }
        Check("nothing from the removed pack is ever picked again",
              !rest.Any(p => p.IndexOf("PackB", StringComparison.OrdinalIgnoreCase) >= 0));
        Check("and the pass keeps turning over without it", rot.Cycle >= 3, "cycle " + rot.Cycle);
    }
}
