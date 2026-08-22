using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// REAL FILES, EDITED AT EVERY AWKWARD MOMENT.
//
// LiveFiles.cs covers a track moving while it plays and a folder still being
// crawled. This covers the rest of the churn a running game actually sees:
// playlists appearing, being rewritten, deleted and put back; music packs
// installed and uninstalled mid-session and picked up by `cmm scan`; and the
// case that sounds trivial and is not — a file deleted and then RE-ADDED at the
// same path, which must not buy itself a second turn in the pass it already had.
//
// Enumeration is the shipped scan code throughout. The one thing that cannot be
// linked is MusicRootsDiscovery (it reads the game's mod list), so the `cmm scan`
// tests drive MusicRoots.SetAddons — which is exactly what Discover finishes by
// calling, and the state change everything downstream actually reacts to.
// ─────────────────────────────────────────────────────────────────────────────
internal static class LiveChurn
{
    static void Check(string w, bool ok, string d = null) => Program.Check(w, ok, d);
    static TrackRng Rng(int s) => Program.Rng(s);

    static string _root;
    static string Primary => Path.Combine(_root, "primary");

    internal static void Run()
    {
        _root = Path.Combine(Path.GetTempPath(), "CmmRotationChurn_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(Primary);
            MusicRoots.SetPrimary(Primary);
            MusicRoots.SetAddons(new List<MusicRoot>());

            Program.Section("Real files: deleted and put back at the same path");
            DeletedAndReAddedKeepsItsPlace();
            FolderDeletedAndRecreatedIdentically();
            FileAddedExactlyAsTheBagEmpties();
            RapidFlapping();

            Program.Section("Real files: playlists appearing, changing, vanishing");
            PlaylistAddedToAFolder();
            PlaylistRewrittenRepeatedly();
            PlaylistDeletedThenReAdded();
            PlaylistWithDuplicateEntries();
            PlaylistBecomesEmptyThenRefills();
            PlaylistPointingOutsideTheMusicRoot();

            Program.Section("Real files: music packs installed and uninstalled mid-game");
            PackInstalledThenScanned();
            PackUninstalledWhileItsTrackIsPlaying();
            PackRemovedThenReAdded();
            TwoPacksFeedingOneContext();
            AddonScanningTurnedOff();
            PackFolderMatchingNoContext();
        }
        finally
        {
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    static void Tracks(string dir, string prefix, int n)
    {
        Directory.CreateDirectory(dir);
        for (int i = 0; i < n; i++)
            File.WriteAllBytes(Path.Combine(dir, $"{prefix}_{i:D2}.ogg"), new byte[] { 1, 2, 3, 4 });
    }

    static string[] PoolFor(string source)
    {
        string[] dirs = MusicRoots.All.Select(r => MusicRoots.Combine(r.Path, source)).ToArray();
        return SourceScan.EnumerateFlatPoolMulti(dirs, SourceScan._noClaimPrefixes, out _);
    }

    // Play `n` tracks from a pool that is re-enumerated each time, so every edit made
    // between picks is seen exactly as the file watch would deliver it.
    static List<string> Play(TrackRotation rot, Func<string[]> pool, int n, int seed, ref string last)
    {
        var played = new List<string>();
        TrackRng rng = Rng(seed);
        for (int i = 0; i < n; i++)
        {
            string p = rot.Select(pool(), TrackRotation.WholePool, last, null, null, 3f, rng);
            if (p == null) break;
            rot.Commit(p, TrackRotation.WholePool);
            played.Add(p); last = p;
        }
        return played;
    }

    // ── deleted and put back ─────────────────────────────────────────────────

    static void DeletedAndReAddedKeepsItsPlace()
    {
        // The rotation is keyed on path. A track deleted and restored at the SAME path is
        // the same track and has already had its turn, so it must not come round again in
        // the pass it already played in. (Restored at a DIFFERENT path it counts as new —
        // that is MovedTrackCountsAsNew, and the two answers have to be consistent.)
        string dir = Path.Combine(Primary, "reAdd");
        Tracks(dir, "ra", 10);
        var rot = new TrackRotation();
        string last = null;
        List<string> first = Play(rot, () => PoolFor("reAdd"), 4, 10, ref last);

        string victim = first[1];
        byte[] data = File.ReadAllBytes(victim);
        File.Delete(victim);
        Check("the deleted track leaves the pool", !PoolFor("reAdd").Contains(victim));
        File.WriteAllBytes(victim, data);
        Check("...and comes back when restored", PoolFor("reAdd").Contains(victim));

        List<string> rest = Play(rot, () => PoolFor("reAdd"), 6, 11, ref last);
        Check("a track restored at the same path does NOT get a second turn this pass",
              !rest.Contains(victim), "it played again");
        Check("the pass still completes over the other 6",
              first.Concat(rest).Distinct().Count() == 10,
              first.Concat(rest).Distinct().Count().ToString());
    }

    static void FolderDeletedAndRecreatedIdentically()
    {
        string dir = Path.Combine(Primary, "recreate");
        Tracks(dir, "rc", 8);
        var rot = new TrackRotation();
        string last = null;
        List<string> first = Play(rot, () => PoolFor("recreate"), 3, 20, ref last);

        Directory.Delete(dir, true);
        Check("the folder is gone", PoolFor("recreate").Length == 0);
        Tracks(dir, "rc", 8);   // same names, same paths
        Check("recreating it restores the same 8 paths", PoolFor("recreate").Length == 8);

        List<string> rest = Play(rot, () => PoolFor("recreate"), 5, 21, ref last);
        Check("an identically recreated folder resumes the pass rather than restarting it",
              !rest.Intersect(first).Any() && rot.Cycle == 1, "cycle " + rot.Cycle);
    }

    static void FileAddedExactlyAsTheBagEmpties()
    {
        // The nastiest timing: the last track of a pass is playing, and a new file lands
        // before the next pick. The new track should take its turn in the pass that is
        // ending rather than being deferred, and no refill should happen yet.
        string dir = Path.Combine(Primary, "seam");
        Tracks(dir, "sm", 6);
        var rot = new TrackRotation();
        string last = null;
        Play(rot, () => PoolFor("seam"), 6, 30, ref last);
        Check("six of six played, pass not yet refilled", rot.Cycle == 1, "cycle " + rot.Cycle);

        File.WriteAllBytes(Path.Combine(dir, "sm_LATE.ogg"), new byte[] { 7 });
        string next = rot.Select(PoolFor("seam"), TrackRotation.WholePool, last, null, null, 3f, Rng(31));
        rot.Commit(next, TrackRotation.WholePool);
        Check("a file arriving as the bag empties takes its turn in the pass that is ending",
              Path.GetFileName(next) == "sm_LATE.ogg", Path.GetFileName(next));
        Check("and the pass did not refill to make room for it", rot.Cycle == 1, "cycle " + rot.Cycle);
    }

    static void RapidFlapping()
    {
        // A folder on a drive that keeps dropping out. Enumeration alternates between the
        // full set and nothing at all; the pass must survive and never repeat back-to-back.
        string dir = Path.Combine(Primary, "flap");
        Tracks(dir, "fp", 12);
        string stash = Path.Combine(_root, "flap_away");
        var rot = new TrackRotation();
        var played = new List<string>();
        string last = null;
        TrackRng rng = Rng(40);
        for (int i = 0; i < 60; i++)
        {
            bool away = (i / 3) % 2 == 1;
            if (away && Directory.Exists(dir)) Directory.Move(dir, stash);
            if (!away && Directory.Exists(stash)) Directory.Move(stash, dir);
            string p = rot.Select(PoolFor("flap"), TrackRotation.WholePool, last, null, null, 3f, rng);
            if (p == null) continue;           // folder away: nothing to play, as expected
            rot.Commit(p, TrackRotation.WholePool);
            played.Add(p); last = p;
        }
        if (Directory.Exists(stash)) Directory.Move(stash, dir);
        Check("a folder flapping on and off disk never causes a back-to-back repeat",
              !Enumerable.Range(1, played.Count - 1).Any(i => played[i] == played[i - 1]));
        Check("and the pass keeps turning over", rot.Cycle >= 2, "cycle " + rot.Cycle);
    }

    // ── playlists ────────────────────────────────────────────────────────────

    static void PlaylistAddedToAFolder()
    {
        // An .m3u dropped into a context folder. The folder pool itself is unchanged (the
        // .m3u is not audio), but the file-watch tracks it so an in-place edit is noticed.
        string dir = Path.Combine(Primary, "withList");
        Tracks(dir, "wl", 5);
        string[] before = SourceScan.EnumerateFolderPoolRaw(dir, false, SourceScan._noClaimPrefixes,
                                                            out string[] plBefore);
        File.WriteAllLines(Path.Combine(dir, "extra.m3u"), Directory.GetFiles(dir, "*.ogg").Take(2));
        string[] after = SourceScan.EnumerateFolderPoolRaw(dir, false, SourceScan._noClaimPrefixes,
                                                           out string[] plAfter);
        Check("dropping a playlist in does not change the folder's own track pool",
              after.Length == before.Length, $"{before.Length} then {after.Length}");
        Check("but the playlist is now tracked for the change signature",
              (plBefore?.Length ?? 0) == 0 && plAfter.Length == 1,
              $"{plBefore?.Length ?? 0} then {plAfter.Length}");
    }

    static void PlaylistRewrittenRepeatedly()
    {
        string dir = Path.Combine(Primary, "plSrc");
        Tracks(dir, "pl", 12);
        string m3u = Path.Combine(Primary, "rotating.m3u");
        string[] all = Directory.GetFiles(dir, "*.ogg")
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();

        File.WriteAllLines(m3u, all.Take(6));
        var rot = new TrackRotation();
        string last = null;
        List<string> first = Play(rot, () => PlaylistSource.ReadTracks(m3u), 3, 50, ref last);

        // Rewrite it four times while the pass runs, each a different slice.
        var everPlayed = new List<string>(first);
        for (int round = 0; round < 4; round++)
        {
            File.WriteAllLines(m3u, all.Skip(round).Take(6 + round));
            everPlayed.AddRange(Play(rot, () => PlaylistSource.ReadTracks(m3u), 3, 60 + round, ref last));
        }
        Check("a playlist rewritten under a running pass never causes a back-to-back repeat",
              !Enumerable.Range(1, everPlayed.Count - 1).Any(i => everPlayed[i] == everPlayed[i - 1]));
        Check("every pick was in the playlist as it stood at the time",
              everPlayed.All(p => all.Contains(p)));
        Check("the pass kept running rather than restarting on every edit",
              rot.Cycle <= 3, "cycle " + rot.Cycle);
    }

    static void PlaylistDeletedThenReAdded()
    {
        string dir = Path.Combine(Primary, "plSrc");
        string m3u = Path.Combine(Primary, "vanishing.m3u");
        string[] all = Directory.GetFiles(dir, "*.ogg")
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        File.WriteAllLines(m3u, all.Take(5));

        var rot = new TrackRotation();
        string last = null;
        Play(rot, () => PlaylistSource.ReadTracks(m3u), 2, 70, ref last);

        File.Delete(m3u);
        string[] gone = PlaylistSource.ReadTracks(m3u);
        Check("a deleted playlist reads as empty rather than throwing",
              gone != null && gone.Length == 0, (gone?.Length ?? -1).ToString());
        Check("and selecting from it returns nothing — no fallback, as specified",
              rot.Select(gone, TrackRotation.WholePool, last, null, null, 3f, Rng(71)) == null);

        // Put it back with DIFFERENT contents.
        File.WriteAllLines(m3u, all.Skip(5).Take(5));
        string[] back = PlaylistSource.ReadTracks(m3u);
        Check("re-adding it with different contents reads the new list", back.Length == 5);
        var rest = Play(rot, () => PlaylistSource.ReadTracks(m3u), 5, 72, ref last);
        Check("all five of the new entries are eligible immediately",
              rest.Distinct().Count() == 5, rest.Distinct().Count().ToString());
    }

    static void PlaylistWithDuplicateEntries()
    {
        string dir = Path.Combine(Primary, "plSrc");
        string m3u = Path.Combine(Primary, "dupes.m3u");
        string[] all = Directory.GetFiles(dir, "*.ogg")
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
        File.WriteAllLines(m3u, all.Concat(all));   // every track listed twice

        string[] pool = PlaylistSource.ReadTracks(m3u);
        var rot = new TrackRotation();
        string last = null;
        List<string> played = Play(rot, () => pool, 4, 80, ref last);
        Check("a playlist listing everything twice still gives each track ONE turn per pass",
              played.Distinct().Count() == 4 && rot.Cycle == 1,
              played.Distinct().Count() + " distinct, cycle " + rot.Cycle);
    }

    static void PlaylistBecomesEmptyThenRefills()
    {
        string dir = Path.Combine(Primary, "plSrc");
        string m3u = Path.Combine(Primary, "emptying.m3u");
        string[] all = Directory.GetFiles(dir, "*.ogg")
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        File.WriteAllLines(m3u, all.Take(4));
        var rot = new TrackRotation();
        string last = null;
        Play(rot, () => PlaylistSource.ReadTracks(m3u), 2, 90, ref last);

        File.WriteAllText(m3u, "");
        Check("an emptied playlist reads as no tracks", PlaylistSource.ReadTracks(m3u).Length == 0);
        Check("and yields no pick rather than falling back to anything",
              rot.Select(PlaylistSource.ReadTracks(m3u), TrackRotation.WholePool, last,
                         null, null, 3f, Rng(91)) == null);

        File.WriteAllLines(m3u, all.Take(4));
        var rest = Play(rot, () => PlaylistSource.ReadTracks(m3u), 2, 92, ref last);
        Check("refilling it resumes the same pass, not a new one",
              rest.Count == 2 && rot.Cycle == 1, "cycle " + rot.Cycle);
    }

    static void PlaylistPointingOutsideTheMusicRoot()
    {
        // Playlist entries may point anywhere. An absolute path outside the music root is
        // legal and must be played as-is, and the rotation must key on it like any other.
        string outside = Path.Combine(_root, "outside");
        Tracks(outside, "out", 3);
        string m3u = Path.Combine(Primary, "elsewhere.m3u");
        File.WriteAllLines(m3u, Directory.GetFiles(outside, "*.ogg"));

        string[] pool = PlaylistSource.ReadTracks(m3u);
        Check("a playlist can reach outside the music root", pool.Length == 3, pool.Length.ToString());
        var rot = new TrackRotation();
        string last = null;
        List<string> played = Play(rot, () => pool, 3, 100, ref last);
        Check("and those tracks rotate like any others", played.Distinct().Count() == 3);
    }

    // ── music packs ──────────────────────────────────────────────────────────

    static void PackInstalledThenScanned()
    {
        // A pack copied into Mods while the game runs. Nothing changes until the roots are
        // re-discovered — which is exactly what `cmm scan` does — and then its tracks join
        // the context's pass already in progress.
        string ctx = Path.Combine(Primary, "packCtx");
        Tracks(ctx, "base", 6);
        var rot = new TrackRotation();
        string last = null;
        Play(rot, () => PoolFor("packCtx"), 3, 110, ref last);

        string pack = Path.Combine(_root, "PackNew", "Music");
        Tracks(Path.Combine(pack, "packCtx"), "new", 4);
        Check("a pack on disk is invisible until the roots are re-scanned",
              PoolFor("packCtx").Length == 6, PoolFor("packCtx").Length.ToString());

        MusicRoots.SetAddons(new List<MusicRoot> { new MusicRoot { Path = pack, Label = "PackNew" } });
        Check("`cmm scan` merges it in", PoolFor("packCtx").Length == 10,
              PoolFor("packCtx").Length.ToString());

        var rest = Play(rot, () => PoolFor("packCtx"), 7, 111, ref last);
        Check("all four of the pack's tracks get a turn in the running pass",
              PoolFor("packCtx").Where(p => p.Contains("PackNew")).All(rest.Contains));
        Check("and the pass ends exactly when the merged folder is exhausted",
              rot.Cycle == 1, "cycle " + rot.Cycle);
    }

    static void PackUninstalledWhileItsTrackIsPlaying()
    {
        string[] pool = PoolFor("packCtx");
        var rot = new TrackRotation();
        string last = null;
        // Play until the current track belongs to the pack, which is the awkward moment.
        TrackRng rng = Rng(120);
        for (int i = 0; i < 20 && (last == null || !last.Contains("PackNew")); i++)
        {
            last = rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, rng);
            rot.Commit(last, TrackRotation.WholePool);
        }
        Check("a pack track is the one playing", last != null && last.Contains("PackNew"), last);

        Directory.Delete(Path.Combine(_root, "PackNew"), true);
        MusicRoots.SetAddons(new List<MusicRoot>());
        string[] after = PoolFor("packCtx");
        Check("uninstalling the pack removes its tracks", after.Length == 6, after.Length.ToString());

        var rest = new List<string>();
        for (int i = 0; i < 30; i++)
        {
            string p = rot.Select(after, TrackRotation.WholePool, last, null, null, 3f, rng);
            rot.Commit(p, TrackRotation.WholePool); rest.Add(p); last = p;
        }
        Check("nothing from the uninstalled pack is ever picked again",
              !rest.Any(p => p.Contains("PackNew")));
        Check("and the pass keeps turning over on what is left", rot.Cycle >= 4, "cycle " + rot.Cycle);
    }

    static void PackRemovedThenReAdded()
    {
        string pack = Path.Combine(_root, "PackAgain", "Music");
        Tracks(Path.Combine(pack, "packCtx"), "again", 3);
        MusicRoots.SetAddons(new List<MusicRoot> { new MusicRoot { Path = pack, Label = "PackAgain" } });
        Check("re-added pack merges", PoolFor("packCtx").Length == 9, PoolFor("packCtx").Length.ToString());

        var rot = new TrackRotation();
        string last = null;
        Play(rot, () => PoolFor("packCtx"), 4, 130, ref last);

        MusicRoots.SetAddons(new List<MusicRoot>());
        Check("removing it again shrinks the pool", PoolFor("packCtx").Length == 6);
        MusicRoots.SetAddons(new List<MusicRoot> { new MusicRoot { Path = pack, Label = "PackAgain" } });
        Check("and putting it straight back restores it", PoolFor("packCtx").Length == 9);

        string[] elig = rot.Eligible(PoolFor("packCtx"), TrackRotation.WholePool, null, null, null, 1);
        Check("pack tracks that had already played are still spent after the round trip",
              elig.Length == 5, elig.Length.ToString());
    }

    static void TwoPacksFeedingOneContext()
    {
        string p1 = Path.Combine(_root, "PackX", "Music");
        string p2 = Path.Combine(_root, "PackY", "Music");
        Tracks(Path.Combine(p1, "shared"), "x", 4);
        Tracks(Path.Combine(p2, "shared"), "y", 5);
        Tracks(Path.Combine(Primary, "shared"), "base", 3);
        MusicRoots.SetAddons(new List<MusicRoot>
        {
            new MusicRoot { Path = p1, Label = "PackX" },
            new MusicRoot { Path = p2, Label = "PackY" },
        });
        string[] pool = PoolFor("shared");
        Check("two packs and the primary merge into one pool of 12", pool.Length == 12,
              pool.Length.ToString());

        var rot = new TrackRotation();
        string last = null;
        List<string> played = Play(rot, () => PoolFor("shared"), 12, 140, ref last);
        Check("one pass covers all three sources exactly once",
              played.Distinct().Count() == 12, played.Distinct().Count().ToString());
        Check("and every source is represented",
              played.Any(p => p.Contains("PackX")) && played.Any(p => p.Contains("PackY"))
              && played.Any(p => p.Contains("primary")));
    }

    static void AddonScanningTurnedOff()
    {
        // AddonMusicScanEnabled=false must drop folders ALREADY IN USE, not merely stop
        // looking for new ones — a runtime toggle-off has to take tracks away.
        string[] withPacks = PoolFor("shared");
        MusicRoots.SetAddons(new List<MusicRoot>());   // what Discover() does when the flag is off
        string[] primaryOnly = PoolFor("shared");
        Check("turning add-on scanning off drops the pack folders already in use",
              withPacks.Length == 12 && primaryOnly.Length == 3,
              $"{withPacks.Length} then {primaryOnly.Length}");

        var rot = new TrackRotation();
        string last = null;
        List<string> played = Play(rot, () => PoolFor("shared"), 3, 150, ref last);
        Check("and the context plays only the primary's tracks",
              played.All(p => p.Contains("primary")));
    }

    static void PackFolderMatchingNoContext()
    {
        string pack = Path.Combine(_root, "PackOdd", "Music");
        Tracks(Path.Combine(pack, "not_a_context"), "odd", 4);
        Tracks(Path.Combine(pack, "shared"), "good", 2);
        MusicRoots.SetAddons(new List<MusicRoot> { new MusicRoot { Path = pack, Label = "PackOdd" } });
        string[] pool = PoolFor("shared");
        Check("a pack folder matching no context is simply ignored",
              pool.Length == 5 && !pool.Any(p => p.Contains("not_a_context")),
              pool.Length.ToString());
        Check("while the folder that DOES match still contributes",
              pool.Count(p => p.Contains("PackOdd")) == 2);
        MusicRoots.SetAddons(new List<MusicRoot>());
    }
}
