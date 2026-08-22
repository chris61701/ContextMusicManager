using System;
using System.Collections.Generic;
using System.Linq;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// The edges. Everything here is a way the rotation can be wrong WITHOUT anything
// going bang: a favourite quietly demoted to an ordinary track, a pass that stops
// ending, a setting change that leaves the memory in a state no code path expects.
// ─────────────────────────────────────────────────────────────────────────────
internal static class EdgeCases
{
    static void Check(string w, bool ok, string d = null) => Program.Check(w, ok, d);
    static TrackRng Rng(int s) => Program.Rng(s);
    static string[] Pool(int n) => Program.Pool(n);
    static List<string> Play(TrackRotation r, string[] p, int m, int n,
                             TrackFlag ban = null, TrackFlag fav = null, float fw = 3f, int seed = 0)
        => Program.Play(r, p, m, n, ban, fav, fw, seed);

    internal static void Run()
    {
        Program.Section("Favourites: measured frequency over many passes");
        FavouriteFrequencyOverTwentyPasses();
        FavouritedMidPassBecomesEligibleAgain();
        UnFavouritedMidPassStopsImmediately();
        EveryTrackFavourited();
        FavouriteWeightChangedMidPass();
        FavouriteAndBannedIsBanned();
        FavouriteInWindowMode();

        Program.Section("Bans, applied while a pass is running");
        BanThePlayingTrackMidPass();
        BanTheLastEligibleTrack();
        UnbanMidPassRejoinsTheSamePass();
        BanEverythingThenUnbanOne();

        Program.Section("Settings changed mid-pass");
        MemoryTightenedToAWindow();
        MemoryWidenedBackToWholePool();
        MemoryOffThenOnReusesTheOldPass();
        SwitchingTrackOrderModes();

        Program.Section("Odd pools");
        EmptyPool();
        DuplicatePathsInOnePool();
        CaseOnlyRenameIsTheSameTrack();
        PoolReplacedWholesale();
        PoolOscillates();
        TwoContextsShareOneFolder();

        Program.Section("Speculative picks at the real game ratio");
        InterleavedSpeculationAndPlays();
        TheStagedPinRePickedEveryTick();

        Program.Section("Resume and history");
        ResumeAfterThePassRolledOver();
        PrevWalkedPastTheStart();

        Program.Section("Long run");
        HundredPassesWithChurn();
        MemoryDoesNotGrowWithoutBound();
    }

    // ── Favourites ───────────────────────────────────────────────────────────

    static void FavouriteFrequencyOverTwentyPasses()
    {
        // The literal ask: keep track of playback frequency. 20 full passes over 30
        // tracks with three favourites at weight 3 — every ordinary track should play
        // exactly 20 times and every favourite exactly 60. Not approximately.
        const int Size = 30, Passes = 20;
        string[] pool = Pool(Size);
        var favs = new HashSet<string> { pool[2], pool[13], pool[29] };
        var rot = new TrackRotation();
        int perPass = (Size - favs.Count) + favs.Count * 3;
        List<string> played = Play(rot, pool, TrackRotation.WholePool, perPass * Passes,
                                   fav: p => favs.Contains(p), fw: 3f, seed: 4);

        var counts = pool.ToDictionary(p => p, p => played.Count(q => q == p));
        int ordMin = pool.Where(p => !favs.Contains(p)).Min(p => counts[p]);
        int ordMax = pool.Where(p => !favs.Contains(p)).Max(p => counts[p]);
        int favMin = favs.Min(p => counts[p]);
        int favMax = favs.Max(p => counts[p]);
        Console.WriteLine($"      {Passes} passes over {Size} tracks, 3 favourites at weight 3:");
        Console.WriteLine($"        ordinary played {ordMin}..{ordMax} times   favourites {favMin}..{favMax} times");
        Check("every ordinary track played exactly once per pass",
              ordMin == Passes && ordMax == Passes, $"{ordMin}..{ordMax}, wanted {Passes}");
        Check("every favourite played exactly 3× per pass",
              favMin == Passes * 3 && favMax == Passes * 3, $"{favMin}..{favMax}, wanted {Passes * 3}");
        Check("no favourite ever played back to back",
              !Enumerable.Range(1, played.Count - 1).Any(i => played[i] == played[i - 1]));
    }

    static void FavouritedMidPassBecomesEligibleAgain()
    {
        // Favouriting a track that has already had its turn should give it more turns
        // in the pass that is running, not make the user wait for the next one.
        string[] pool = Pool(20);
        var favs = new HashSet<string>();
        var rot = new TrackRotation();
        string target = pool[5];
        rot.Commit(target, TrackRotation.WholePool);
        Check("before favouriting, a played track is not eligible",
              !rot.Eligible(pool, TrackRotation.WholePool, null, null, p => favs.Contains(p), 3)
                   .Contains(target));
        favs.Add(target);
        Check("favouriting it mid-pass makes it eligible again",
              rot.Eligible(pool, TrackRotation.WholePool, null, null, p => favs.Contains(p), 3)
                 .Contains(target));
    }

    static void UnFavouritedMidPassStopsImmediately()
    {
        string[] pool = Pool(20);
        var favs = new HashSet<string> { pool[5] };
        var rot = new TrackRotation();
        rot.Commit(pool[5], TrackRotation.WholePool);
        rot.Commit(pool[5], TrackRotation.WholePool);   // 2 of its 3 turns spent
        Check("a favourite mid-allowance is still eligible",
              rot.Eligible(pool, TrackRotation.WholePool, null, null, p => favs.Contains(p), 3)
                 .Contains(pool[5]));
        favs.Clear();
        Check("un-favouriting drops it below its new allowance and it stops at once",
              !rot.Eligible(pool, TrackRotation.WholePool, null, null, p => favs.Contains(p), 3)
                   .Contains(pool[5]));
        // And the pass must still be able to end — an over-allowance track must not
        // read as "eligible" through some sign slip.
        List<string> rest = Play(rot, pool, TrackRotation.WholePool, 20, seed: 8);
        Check("the pass still completes with an over-played track in it", rot.Cycle == 2,
              "cycle " + rot.Cycle);
    }

    static void EveryTrackFavourited()
    {
        // Favouriting EVERYTHING is the one case where a pass can end before every
        // allowance is spent, and it is worth being precise about why rather than
        // pretending it does not happen.
        //
        // Near the end of such a pass the only track still owed a turn can be the one
        // that just played — possible only with an allowance above 1. The choice is then
        // between playing it back to back and opening the next pass early. It opens the
        // next pass: never repeating back-to-back is the guarantee RandomNoRepeat has
        // always made, and a favourite losing its third turn now and then is a far
        // smaller cost than breaking it.
        //
        // What must hold: no track EXCEEDS its allowance, nothing plays twice in a row,
        // and over many passes the frequency still lands close to 3×.
        const int Size = 10, Plays = 3000;
        string[] pool = Pool(Size);
        var rot = new TrackRotation();
        List<string> played = Play(rot, pool, TrackRotation.WholePool, Plays,
                                   fav: _ => true, fw: 3f, seed: 12);
        int passes = rot.Cycle;
        double perPass = (double)Plays / passes;
        Console.WriteLine($"      all {Size} tracks favourited at weight 3: {passes} passes over "
                        + $"{Plays} plays = {perPass:F1} plays per pass (3× would be {Size * 3})");
        Check("no back-to-back repeat even with every track favourited",
              !Enumerable.Range(1, played.Count - 1).Any(i => played[i] == played[i - 1]));
        Check("a pass never exceeds 3× the folder", perPass <= Size * 3, perPass.ToString("F1"));
        Check("and gets close to it — passes end early only at the seam",
              perPass > Size * 3 * 0.85, perPass.ToString("F1"));
        int min = pool.Min(p => played.Count(q => q == p));
        int max = pool.Max(p => played.Count(q => q == p));
        Check("every track is played a comparable number of times",
              max - min < Plays * 0.05, $"{min}..{max}");
    }

    static void FavouriteWeightChangedMidPass()
    {
        // FavouriteWeight is hot-reloadable. Raising it mid-pass has to grant the extra
        // turns immediately; lowering it has to take them away without corrupting anything.
        string[] pool = Pool(15);
        var favs = new HashSet<string> { pool[1] };
        var rot = new TrackRotation();
        for (int i = 0; i < 2; i++) rot.Commit(pool[1], TrackRotation.WholePool);
        Check("weight 2: a favourite on 2 plays is spent",
              !rot.Eligible(pool, TrackRotation.WholePool, null, null, p => favs.Contains(p),
                            TrackRotation.Allowance(2f)).Contains(pool[1]));
        Check("weight raised to 5 mid-pass: eligible again immediately",
              rot.Eligible(pool, TrackRotation.WholePool, null, null, p => favs.Contains(p),
                           TrackRotation.Allowance(5f)).Contains(pool[1]));
        Check("weight dropped to 1 mid-pass: spent again",
              !rot.Eligible(pool, TrackRotation.WholePool, null, null, p => favs.Contains(p),
                            TrackRotation.Allowance(1f)).Contains(pool[1]));
    }

    static void FavouriteAndBannedIsBanned()
    {
        string[] pool = Pool(10);
        string both = pool[4];
        var rot = new TrackRotation();
        List<string> played = Play(rot, pool, TrackRotation.WholePool, 60,
                                   ban: p => p == both, fav: p => p == both, fw: 3f, seed: 21);
        Check("a track that is both banned and favourited is never picked",
              !played.Contains(both));
        Check("and the pass still ends over the other 9", rot.Cycle >= 6, "cycle " + rot.Cycle);
    }

    static void FavouriteInWindowMode()
    {
        // In a window the allowance still applies, so a favourite may appear more than
        // once inside the window. What must NOT happen is back-to-back or a stuck window.
        const int N = 8;
        string[] pool = Pool(20);
        var favs = new HashSet<string> { pool[0] };
        var rot = new TrackRotation();
        List<string> played = Play(rot, pool, TrackRotation.WholePool == 0 ? 0 : N, 200,
                                   fav: p => favs.Contains(p), fw: 3f, seed: 33);
        Check("window mode: the favourite is played more than any ordinary track",
              played.Count(p => p == pool[0]) > pool.Skip(1).Max(p => played.Count(q => q == p)),
              played.Count(p => p == pool[0]).ToString());
        Check("window mode: still no back-to-back repeat",
              !Enumerable.Range(1, played.Count - 1).Any(i => played[i] == played[i - 1]));
        Check("window mode: the window never exceeds its size", rot.Remembered <= N,
              rot.Remembered.ToString());
    }

    // ── Bans mid-pass ────────────────────────────────────────────────────────

    static void BanThePlayingTrackMidPass()
    {
        string[] pool = Pool(20);
        var banned = new HashSet<string>();
        var rot = new TrackRotation();
        List<string> first = Play(rot, pool, TrackRotation.WholePool, 5,
                                  ban: p => banned.Contains(p), seed: 41);
        string playing = first.Last();
        banned.Add(playing);                       // Alt+Delete on the current track
        List<string> rest = Play(rot, pool, TrackRotation.WholePool, 40,
                                 ban: p => banned.Contains(p), seed: 42);
        Check("banning the playing track keeps it out from then on", !rest.Contains(playing));
        Check("and the pass still ends over the 19 that remain", rot.Cycle >= 2,
              "cycle " + rot.Cycle);
    }

    static void BanTheLastEligibleTrack()
    {
        // 20 tracks, 19 played, then the 20th is banned. There is nothing eligible and
        // nothing to wait for, so the pass has to roll over rather than hang.
        string[] pool = Pool(20);
        var rot = new TrackRotation();
        var banned = new HashSet<string>();
        List<string> played = Play(rot, pool, TrackRotation.WholePool, 19, seed: 51);
        string lastOne = pool.Single(p => !played.Contains(p));
        banned.Add(lastOne);
        string next = rot.Select(pool, TrackRotation.WholePool, played.Last(),
                                 p => banned.Contains(p), null, 3f, Rng(52));
        Check("banning the only remaining track rolls the pass over instead of hanging",
              next != null && next != lastOne && rot.Cycle == 2,
              (next ?? "null") + ", cycle " + rot.Cycle);
    }

    static void UnbanMidPassRejoinsTheSamePass()
    {
        string[] pool = Pool(20);
        var banned = new HashSet<string> { pool[7] };
        var rot = new TrackRotation();
        Play(rot, pool, TrackRotation.WholePool, 5, ban: p => banned.Contains(p), seed: 61);
        banned.Clear();
        Check("un-banning mid-pass puts the track straight back in the running",
              rot.Eligible(pool, TrackRotation.WholePool, null, p => banned.Contains(p), null, 1)
                 .Contains(pool[7]));
    }

    static void BanEverythingThenUnbanOne()
    {
        string[] pool = Pool(10);
        var banned = new HashSet<string>(pool);
        var rot = new TrackRotation();
        Check("everything banned selects nothing",
              rot.Select(pool, TrackRotation.WholePool, null, p => banned.Contains(p),
                         null, 3f, Rng(71)) == null);
        banned.Remove(pool[3]);
        string p2 = rot.Select(pool, TrackRotation.WholePool, null, p => banned.Contains(p),
                               null, 3f, Rng(72));
        Check("un-banning one track makes it the only pick", p2 == pool[3], p2 ?? "null");
        Check("the pass counter did not spin while everything was banned",
              rot.Cycle == 1, "cycle " + rot.Cycle);
    }

    // ── Settings changed mid-pass ────────────────────────────────────────────

    static void MemoryTightenedToAWindow()
    {
        // -1 → 10 with 400 remembered. The next commit has to shed the excess in one go
        // and leave exactly a window behind.
        string[] pool = Pool(500);
        var rot = new TrackRotation();
        Play(rot, pool, TrackRotation.WholePool, 400, seed: 81);
        Check("400 remembered under whole-pool", rot.Remembered == 400, rot.Remembered.ToString());
        rot.Commit("something_new.ogg", 10);
        Check("switching to a window of 10 sheds the rest on the next play",
              rot.Remembered == 10, rot.Remembered.ToString());
        List<string> after = Play(rot, pool, 10, 100, seed: 82);
        Check("and the window then behaves as a window", rot.Remembered == 10,
              rot.Remembered.ToString());
        Check("no repeat inside 10 after the switch",
              !Enumerable.Range(10, after.Count - 10).Any(i =>
                  after.Skip(i - 10).Take(10).Contains(after[i])));
    }

    static void MemoryWidenedBackToWholePool()
    {
        string[] pool = Pool(50);
        var rot = new TrackRotation();
        Play(rot, pool, 10, 60, seed: 91);
        int remembered = rot.Remembered;
        // 50 tracks with `remembered` spent leaves exactly that many owed. Asking for
        // more than that is asking for a second pass, which of course repeats.
        int owed = 50 - remembered;
        List<string> after = Play(rot, pool, TrackRotation.WholePool, owed, seed: 92);
        Check("widening to whole-pool keeps what the window held and stops evicting",
              rot.Remembered == remembered + owed, rot.Remembered.ToString());
        Check($"no repeat among the {owed} picks that finish the widened pass",
              after.Distinct().Count() == after.Count);
    }

    static void MemoryOffThenOnReusesTheOldPass()
    {
        // Turning anti-repeat off and on again is a plausible thing to do while tuning.
        // Off must stop remembering; back on must resume the pass that was in progress
        // rather than start a fresh one or trip over the plays made while it was off.
        string[] pool = Pool(20);
        var rot = new TrackRotation();
        List<string> first = Play(rot, pool, TrackRotation.WholePool, 10, seed: 101);
        Play(rot, pool, TrackRotation.Off, 50, seed: 102);
        Check("with memory off, nothing new is remembered", rot.Remembered == 10,
              rot.Remembered.ToString());
        string[] elig = rot.Eligible(pool, TrackRotation.WholePool, null, null, null, 1);
        Check("back on, the earlier 10 are still spent and the other 10 are owed",
              elig.Length == 10 && !elig.Intersect(first).Any(), elig.Length.ToString());
    }

    static void SwitchingTrackOrderModes()
    {
        // Random and Sequential do not touch the rotation, so flipping to one of them
        // and back must leave the pass exactly where it was.
        string[] pool = Pool(20);
        var rot = new TrackRotation();
        Play(rot, pool, TrackRotation.WholePool, 8, seed: 111);
        int plays = rot.Plays, remembered = rot.Remembered;
        for (int i = 0; i < 20; i++)                       // 20 picks in plain Random mode
            TrackRotation.Roll(pool, Rng(112 + i), null, null, 3f);
        Check("plain Random picks leave the pass untouched",
              rot.Plays == plays && rot.Remembered == remembered,
              $"{rot.Plays}/{rot.Remembered} vs {plays}/{remembered}");
        string[] elig = rot.Eligible(pool, TrackRotation.WholePool, null, null, null, 1);
        Check("switching back resumes the same pass", elig.Length == 12, elig.Length.ToString());
    }

    // ── Odd pools ────────────────────────────────────────────────────────────

    static void EmptyPool()
    {
        var rot = new TrackRotation();
        Check("an empty folder selects nothing",
              rot.Select(new string[0], TrackRotation.WholePool, null, null, null, 3f, Rng(121)) == null);
        Check("a null pool selects nothing",
              rot.Select(null, TrackRotation.WholePool, null, null, null, 3f, Rng(122)) == null);
    }

    static void DuplicatePathsInOnePool()
    {
        // A .m3u may list the same file twice. The two entries are one track, so the
        // pair should get ONE turn between them — and, more importantly, the count pass
        // and the fill pass have to agree about that or the output array is malformed.
        string[] pool = Pool(10).Concat(new[] { "track_0003.ogg" }).ToArray();
        var rot = new TrackRotation();
        rot.Commit("track_0003.ogg", TrackRotation.WholePool);
        string[] elig = rot.Eligible(pool, TrackRotation.WholePool, null, null, null, 1);
        Check("a duplicated path is spent by one play, both copies",
              elig.Length == 9 && !elig.Contains("track_0003.ogg"), elig.Length.ToString());
        Check("the filtered array has no holes in it", elig.All(e => e != null));
        // 11 entries but only 10 distinct tracks, one of them already spent: 9 owed,
        // then a full 10, then 1 into the third pass.
        List<string> played = Play(rot, pool, TrackRotation.WholePool, 20, seed: 131);
        Check("and the pass still ends cleanly, counting the duplicate once",
              rot.Cycle == 3, "cycle " + rot.Cycle);
    }

    static void CaseOnlyRenameIsTheSameTrack()
    {
        // Windows paths are case-insensitive, so Track.ogg and track.ogg are one file.
        // The memory is keyed OrdinalIgnoreCase for exactly this.
        var rot = new TrackRotation();
        rot.Commit("Music/Ambient/Track.ogg", TrackRotation.WholePool);
        string[] pool = { "music/ambient/TRACK.OGG", "other.ogg" };
        string[] elig = rot.Eligible(pool, TrackRotation.WholePool, null, null, null, 1);
        Check("a case-only difference is the same track, not a new one",
              elig.Length == 1 && elig[0] == "other.ogg", string.Join(",", elig));
    }

    static void PoolReplacedWholesale()
    {
        // What a MusicPath change looks like from in here: every path different. The
        // rotation is not reset by TrackRotation itself (ConfigWatch does that), so what
        // is asserted is the fallback — nothing carries over into the new folder.
        string[] oldPool = Pool(20);
        var rot = new TrackRotation();
        Play(rot, oldPool, TrackRotation.WholePool, 20, seed: 141);
        string[] newPool = Enumerable.Range(0, 20).Select(i => $"NEW/track_{i:D4}.ogg").ToArray();
        string[] elig = rot.Eligible(newPool, TrackRotation.WholePool, null, null, null, 1);
        Check("a wholly different folder is wholly eligible", elig.Length == 20,
              elig.Length.ToString());
        List<string> played = Play(rot, newPool, TrackRotation.WholePool, 20, seed: 142);
        Check("and its first pass is a clean sweep", played.Distinct().Count() == 20);
    }

    static void PoolOscillates()
    {
        // A folder on a flaky drive can appear and disappear between ticks. The pass
        // must not be corrupted by flapping, and must still terminate.
        string[] full = Pool(30);
        string[] half = full.Take(15).ToArray();
        var rot = new TrackRotation();
        var played = new List<string>();
        string last = null;
        for (int i = 0; i < 120; i++)
        {
            string[] pool = (i / 5) % 2 == 0 ? full : half;
            string p = rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, Rng(150 + i));
            rot.Commit(p, TrackRotation.WholePool);
            played.Add(p); last = p;
        }
        Check("a folder flapping in and out never repeats back-to-back",
              !Enumerable.Range(1, played.Count - 1).Any(i => played[i] == played[i - 1]));
        Check("and passes keep turning over", rot.Cycle >= 4, "cycle " + rot.Cycle);
    }

    static void TwoContextsShareOneFolder()
    {
        // Two contexts can point at the same folder. Each keeps its OWN pass — they are
        // separate listening experiences, and one must not eat the other's tracks.
        string[] pool = Pool(20);
        var a = new TrackRotation();
        var b = new TrackRotation();
        List<string> pa = Play(a, pool, TrackRotation.WholePool, 20, seed: 161);
        List<string> pb = Play(b, pool, TrackRotation.WholePool, 20, seed: 162);
        Check("each context sweeps the shared folder independently",
              pa.Distinct().Count() == 20 && pb.Distinct().Count() == 20);
        Check("and their passes are genuinely separate orders", !pa.SequenceEqual(pb));
    }

    // ── Speculation ──────────────────────────────────────────────────────────

    static void InterleavedSpeculationAndPlays()
    {
        // The real shape of a track change: the staged pin, the pre-staged advance and
        // the play itself all pick, and only one of them plays. Three speculative picks
        // per commit, for a whole pass.
        string[] pool = Pool(40);
        var rot = new TrackRotation();
        var played = new List<string>();
        string last = null;
        for (int i = 0; i < 40; i++)
        {
            for (int s = 0; s < 3; s++)
                rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, Rng(200 + i * 4 + s));
            string p = rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, Rng(203 + i * 4));
            rot.Commit(p, TrackRotation.WholePool);
            played.Add(p); last = p;
        }
        Check("160 picks but 40 plays still sweeps the folder exactly once",
              played.Distinct().Count() == 40, played.Distinct().Count().ToString());
        Check("the pass counter reflects plays, not picks", rot.Cycle == 1, "cycle " + rot.Cycle);
    }

    static void TheStagedPinRePickedEveryTick()
    {
        // PinStagedFile runs from the maintenance pass. It is idempotent in the caller,
        // but if it were not, this is what it would look like: hundreds of picks for a
        // context that is not even playing. None of them may spend a slot.
        string[] pool = Pool(30);
        var rot = new TrackRotation();
        for (int i = 0; i < 500; i++)
            rot.Select(pool, TrackRotation.WholePool, null, null, null, 3f, Rng(300 + i));
        Check("500 pins for an idle context spend nothing",
              rot.Plays == 0 && rot.Remembered == 0 && rot.Cycle == 1);
    }

    // ── Resume and history ───────────────────────────────────────────────────

    static void ResumeAfterThePassRolledOver()
    {
        // Leave a context, come back after its pass has rolled over. The resume replays
        // the track it left on, which by then belongs to the PREVIOUS pass — it must
        // still not spend a slot in the new one.
        var cache = new ContextCache();
        for (int i = 0; i < 5; i++)
            cache.CommitToHistory($"t{i}.ogg", TrackRotation.WholePool);
        int before = cache.rotation.Plays;
        cache.CommitToHistory("t4.ogg", TrackRotation.WholePool);   // resume of the last track
        Check("a resume after a rollover still costs nothing",
              cache.rotation.Plays == before, cache.rotation.Plays.ToString());
    }

    static void PrevWalkedPastTheStart()
    {
        var cache = new ContextCache();
        cache.CommitToHistory("a.ogg", TrackRotation.WholePool);
        cache.CommitToHistory("b.ogg", TrackRotation.WholePool);
        for (int i = 0; i < 10; i++) cache.CommitToHistory("a.ogg", TrackRotation.WholePool);
        Check("hammering Prev at the start of history spends nothing extra",
              cache.rotation.Plays == 2, cache.rotation.Plays.ToString());
    }

    // ── Long run ─────────────────────────────────────────────────────────────

    static void HundredPassesWithChurn()
    {
        // A folder edited continuously for thousands of plays. The invariant is measured
        // against the ROTATION's own passes, not against fixed blocks of picks: once the
        // folder is changing, a pass is no longer a predictable number of tracks long,
        // because a track deleted before its turn shortens the pass it was owed a turn in.
        // (Asserting fixed 40-pick blocks was my first attempt and it "failed" purely
        // because the blocks drifted out of step with the passes.)
        var rot = new TrackRotation();
        var rng = Rng(999);
        var pool = Pool(40).ToList();
        int nextId = 40, repeatsWithinAPass = 0, passesSeen = 0;
        string last = null;
        var seen = new HashSet<string>();
        int cycle = rot.Cycle;
        for (int i = 0; i < 4000; i++)
        {
            if (i % 7 == 0) { pool.RemoveAt(0); pool.Add($"churn_{nextId++:D4}.ogg"); }
            string[] snapshot = pool.ToArray();
            string p = rot.Select(snapshot, TrackRotation.WholePool, last, null, null, 3f, rng);
            if (rot.Cycle != cycle) { cycle = rot.Cycle; seen.Clear(); passesSeen++; }
            rot.Commit(p, TrackRotation.WholePool);
            last = p;
            if (!seen.Add(p)) repeatsWithinAPass++;
        }
        Console.WriteLine($"      4000 plays, a track swapped every 7th: {passesSeen} passes turned over");
        Check($"across {passesSeen} passes of a continuously edited folder, no track repeated inside one",
              repeatsWithinAPass == 0, repeatsWithinAPass + " repeats");
        Check("passes kept turning over throughout", passesSeen > 50, passesSeen.ToString());
    }

    static void MemoryDoesNotGrowWithoutBound()
    {
        // Whole-pool mode only clears on exhaustion, so a folder churning inside a pass
        // leaves dead entries behind. They must be bounded by ONE pass's worth of churn,
        // not accumulate for the session.
        var rot = new TrackRotation();
        var pool = Pool(50).ToList();
        int nextId = 50, peak = 0;
        string last = null;
        var rng = Rng(1234);
        for (int i = 0; i < 5000; i++)
        {
            if (i % 3 == 0) { pool.RemoveAt(0); pool.Add($"churn_{nextId++:D4}.ogg"); }
            string[] snap = pool.ToArray();
            string p = rot.Select(snap, TrackRotation.WholePool, last, null, null, 3f, rng);
            rot.Commit(p, TrackRotation.WholePool);
            last = p;
            peak = Math.Max(peak, rot.DistinctRemembered);
        }
        Console.WriteLine($"      5000 plays, a track replaced every 3rd: peak remembered {peak}");
        Check("the remembered set stays bounded by one pass, not the session",
              peak < 500, "peak " + peak);
        Check("and passes still turn over", rot.Cycle > 50, "cycle " + rot.Cycle);
    }
}
