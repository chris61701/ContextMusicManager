using System;
using System.Collections.Generic;
using System.Linq;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// CmmRotationTest — does the anti-repeat rotation actually keep its promise?
//
// The promise is "every track plays once before any track plays twice". That is
// trivially easy to write and surprisingly easy to get wrong at the edges, and
// every one of those edges is silent: a bag that quietly stops refilling, a ban
// that stalls a cycle forever, a speculative pick that drains the bag without a
// note being heard. None of them produce an error, a warning, or a symptom you
// could hear inside an hour of play. So they are asserted here instead.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Program
{
    internal static int _checks, _failed;

    static int Main()
    {
        Console.WriteLine("CmmRotationTest — anti-repeat rotation");
        Console.WriteLine(new string('=', 74));

        Section("The core promise");
        BagPlaysEveryTrackOnce();
        BagRefillsAndCounts();
        NoRepeatAtTheCycleSeam();
        ManyCyclesNoIntraCycleRepeat();
        SingleTrackPool();
        TwoTrackPoolAlternates();
        LargePool();

        Section("The memory setting");
        MemoryZeroIsTheOldBehaviour();
        WindowRemembersExactlyN();
        WindowLargerThanPoolActsAsABag();

        Section("Bans and favourites");
        BannedAreNeverPicked();
        BansDoNotStallACycle();
        AllBannedReturnsNull();
        OnlyThePlayingTrackIsUnbanned();
        FavouritesGetMoreTurnsPerPass();
        FavouriteWeightBelowOneIsOneTurn();

        Section("Picking is not playing");
        SpeculativePicksDoNotDrainTheBag();
        ResumeDoesNotSpendASlot();
        PrevNextReplayDoesNotSpendASlot();

        Section("A library that changes underneath it");
        TracksAddedMidCycleGetATurn();
        TracksRemovedMidCycleDoNotStallIt();
        GrowingPoolDuringARecursiveCrawl();
        MovedTrackCountsAsNew();

        Section("Allocation");
        FreshCycleReturnsThePoolItself();
        SelectAllocatesNothing();
        SelectAndEligibleAgree();
        CacheDoesNotGoStale();

        EdgeCases.Run();
        Frequency.Run();
        FavouriteReach.Run();
        LiveFiles.Run();
        LiveChurn.Run();

        Section("What it actually changes");
        HowSoonDoesATrackComeBack();

        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        Console.WriteLine(_failed == 0
            ? $"PASS — {_checks} checks"
            : $"FAIL — {_failed} of {_checks} checks failed");
        return _failed == 0 ? 0 : 1;
    }

    // ── The core promise ─────────────────────────────────────────────────────

    static void BagPlaysEveryTrackOnce()
    {
        string[] pool = Pool(50);
        var rot = new TrackRotation();
        List<string> played = Play(rot, pool, TrackRotation.WholePool, 50);
        Check("50 picks over 50 tracks give 50 DISTINCT tracks",
              played.Distinct().Count() == 50, $"{played.Distinct().Count()} distinct");
        Check("all 50 picks landed", played.Count == 50, played.Count.ToString());
    }

    static void BagRefillsAndCounts()
    {
        string[] pool = Pool(50);
        var rot = new TrackRotation();
        Play(rot, pool, TrackRotation.WholePool, 50);
        Check("after a full pass the bag is still on pass 1 (it refills lazily)",
              rot.Cycle == 1, "cycle " + rot.Cycle);
        Play(rot, pool, TrackRotation.WholePool, 1, seedOffset: 99);
        Check("the 51st pick opens pass 2", rot.Cycle == 2, "cycle " + rot.Cycle);
        Check("pass 2 starts with 1 track played", rot.Plays == 1, rot.Plays.ToString());
    }

    static void NoRepeatAtTheCycleSeam()
    {
        // The seam is the one place a bag can repeat back-to-back: the last track of
        // a pass becomes eligible again the instant the pass refills. Run it 200 times
        // with different draws rather than once, because it only shows up when the
        // refill lands on the track that just played.
        int seams = 0, repeats = 0;
        for (int trial = 0; trial < 200; trial++)
        {
            string[] pool = Pool(12);
            var rot = new TrackRotation();
            List<string> played = Play(rot, pool, TrackRotation.WholePool, 25, seedOffset: trial);
            for (int i = 1; i < played.Count; i++)
            {
                if (i % 12 == 0) seams++;
                if (played[i] == played[i - 1]) repeats++;
            }
        }
        Check($"no back-to-back repeat in 200 runs across {seams} cycle seams",
              repeats == 0, repeats + " repeats");
    }

    static void ManyCyclesNoIntraCycleRepeat()
    {
        string[] pool = Pool(50);
        var rot = new TrackRotation();
        List<string> played = Play(rot, pool, TrackRotation.WholePool, 500);
        bool ok = true;
        for (int c = 0; c < 10; c++)
            if (played.Skip(c * 50).Take(50).Distinct().Count() != 50) ok = false;
        Check("10 consecutive passes are each 50 distinct tracks", ok);
        Check("10 passes = pass counter at 10", rot.Cycle == 10, "cycle " + rot.Cycle);
    }

    static void SingleTrackPool()
    {
        string[] pool = Pool(1);
        var rot = new TrackRotation();
        List<string> played = Play(rot, pool, TrackRotation.WholePool, 5);
        Check("a one-track folder just replays that track", played.Count == 5 && played.All(p => p == pool[0]));
        Check("and does not spin the pass counter", rot.Cycle == 1, "cycle " + rot.Cycle);
    }

    static void TwoTrackPoolAlternates()
    {
        string[] pool = Pool(2);
        var rot = new TrackRotation();
        List<string> played = Play(rot, pool, TrackRotation.WholePool, 10);
        bool alternates = true;
        for (int i = 1; i < played.Count; i++) if (played[i] == played[i - 1]) alternates = false;
        Check("a two-track folder strictly alternates", alternates);
    }

    static void LargePool()
    {
        string[] pool = Pool(3000);
        var rot = new TrackRotation();
        List<string> played = Play(rot, pool, TrackRotation.WholePool, 3000);
        Check("3000 tracks: one pass covers every one of them",
              played.Distinct().Count() == 3000, played.Distinct().Count().ToString());
    }

    // ── The memory setting ───────────────────────────────────────────────────

    static void MemoryZeroIsTheOldBehaviour()
    {
        string[] pool = Pool(50);
        var rot = new TrackRotation();
        List<string> played = Play(rot, pool, TrackRotation.Off, 50);
        Check("memory=0 remembers nothing", rot.Remembered == 0, rot.Remembered.ToString());
        Check("memory=0 repeats inside the first 50 picks (this is what was wrong before)",
              played.Distinct().Count() < 50, played.Distinct().Count() + " distinct of 50");
        bool backToBack = false;
        for (int i = 1; i < played.Count; i++) if (played[i] == played[i - 1]) backToBack = true;
        Check("memory=0 still never repeats back-to-back", !backToBack);
    }

    static void WindowRemembersExactlyN()
    {
        const int N = 10;
        string[] pool = Pool(50);
        var rot = new TrackRotation();
        List<string> played = new List<string>();
        string last = null;
        int maxRemembered = 0;
        for (int i = 0; i < 300; i++)
        {
            string p = rot.Select(pool, N, last, null, null, 3f, Rng(i));
            rot.Commit(p, N);
            played.Add(p); last = p;
            maxRemembered = Math.Max(maxRemembered, rot.Remembered);
        }
        Check($"window={N} never remembers more than {N}", maxRemembered == N, maxRemembered.ToString());
        bool violated = false;
        for (int i = N; i < played.Count; i++)
            if (played.Skip(i - N).Take(N).Contains(played[i])) violated = true;
        Check($"no track returns inside a window of {N}", !violated);
        Check("a window is NOT a bag — tracks do come back inside 300 picks",
              played.Distinct().Count() <= 50 && played.Count > played.Distinct().Count());
    }

    static void WindowLargerThanPoolActsAsABag()
    {
        // 500 remembered over 50 tracks cannot be satisfied. It must degrade to a bag
        // rather than running out of candidates and deadlocking or silently repeating.
        string[] pool = Pool(50);
        var rot = new TrackRotation();
        List<string> played = Play(rot, pool, 500, 150);
        Check("memory > pool size still plays every track", played.Count == 150);
        bool ok = true;
        for (int c = 0; c < 3; c++)
            if (played.Skip(c * 50).Take(50).Distinct().Count() != 50) ok = false;
        Check("memory > pool size behaves as a bag (3 clean passes)", ok);
    }

    // ── Bans and favourites ──────────────────────────────────────────────────

    static void BannedAreNeverPicked()
    {
        string[] pool = Pool(30);
        var banned = new HashSet<string>(pool.Take(10));
        var rot = new TrackRotation();
        List<string> played = Play(rot, pool, TrackRotation.WholePool, 200, ban: p => banned.Contains(p));
        Check("a banned track is never picked", !played.Any(banned.Contains));
    }

    static void BansDoNotStallACycle()
    {
        // The trap: banned tracks are in the pool but can never be played, so a cycle
        // that waits for "everything played" would never end and the rotation would
        // quietly stop rotating. Cycles must complete over the PLAYABLE subset.
        string[] pool = Pool(30);
        var banned = new HashSet<string>(pool.Take(10));
        var rot = new TrackRotation();
        List<string> played = Play(rot, pool, TrackRotation.WholePool, 200, ban: p => banned.Contains(p));
        Check("passes still complete over the 20 playable tracks",
              rot.Cycle == 10, "cycle " + rot.Cycle + " after 200 picks of a 20-track playable pool");
        bool ok = true;
        for (int c = 0; c < 10; c++)
            if (played.Skip(c * 20).Take(20).Distinct().Count() != 20) ok = false;
        Check("and each pass is still every playable track exactly once", ok);
    }

    static void AllBannedReturnsNull()
    {
        string[] pool = Pool(10);
        var rot = new TrackRotation();
        string p = rot.Select(pool, TrackRotation.WholePool, null, _ => true, null, 3f, Rng(0));
        Check("an entirely banned folder selects nothing", p == null, p ?? "null");
    }

    static void OnlyThePlayingTrackIsUnbanned()
    {
        // Ban everything except the track currently playing. There is nothing to switch
        // to, so it has to keep playing — not fall silent, and not return a banned track.
        // The old code needed a special retry for this; it now falls out of the filter,
        // which is worth an assertion rather than an assumption.
        string[] pool = Pool(10);
        var rot = new TrackRotation();
        string playing = pool[4];
        rot.Commit(playing, TrackRotation.WholePool);
        string p = rot.Select(pool, TrackRotation.WholePool, playing,
                              t => t != playing, null, 3f, Rng(1));
        Check("if only the playing track is unbanned, it replays rather than falling silent",
              p == playing, p ?? "null");
    }

    static void FavouritesGetMoreTurnsPerPass()
    {
        // A strict bag would silently kill the favourite keybind: one play per pass
        // each, exactly like everything else. A favourite has to stay in the bag longer.
        Check("FavouriteWeight 3.0 means 3 turns per pass", TrackRotation.Allowance(3f) == 3);
        string[] pool = Pool(20);
        string fav = pool[7];
        var rot = new TrackRotation();
        // One full pass = 19 ordinary turns + 3 for the favourite.
        List<string> played = Play(rot, pool, TrackRotation.WholePool, 22, fav: p => p == fav, favWeight: 3f);
        int favPlays = played.Count(p => p == fav);
        int maxOther = pool.Where(p => p != fav).Max(p => played.Count(q => q == p));
        Check("the favourite plays more than any other track in one pass",
              favPlays > maxOther, $"favourite {favPlays}, best other {maxOther}");
        Check("every other track still plays exactly once", maxOther == 1, maxOther.ToString());
    }

    static void FavouriteWeightBelowOneIsOneTurn()
    {
        Check("FavouriteWeight 0 clamps to one turn", TrackRotation.Allowance(0f) == 1);
        Check("FavouriteWeight 1 is one turn", TrackRotation.Allowance(1f) == 1);
        Check("FavouriteWeight 2.6 rounds to 3", TrackRotation.Allowance(2.6f) == 3);
    }

    // ── Picking is not playing ───────────────────────────────────────────────

    static void SpeculativePicksDoNotDrainTheBag()
    {
        // Three of the six PickNextFile callers are speculative — the staged pin, the
        // pre-staged advance, the missing-file replacement walk — and their picks are
        // routinely thrown away. If a PICK spent a bag slot, the bag would drain
        // without a note being heard and the guarantee would be worthless.
        string[] pool = Pool(50);
        var rot = new TrackRotation();
        for (int i = 0; i < 1000; i++)
            rot.Select(pool, TrackRotation.WholePool, null, null, null, 3f, Rng(i));
        Check("1000 picks with nothing played leave the bag untouched",
              rot.Remembered == 0 && rot.Plays == 0 && rot.Cycle == 1,
              $"remembered {rot.Remembered}, plays {rot.Plays}, cycle {rot.Cycle}");
        Check("and the next pass is still a full one",
              Play(rot, pool, TrackRotation.WholePool, 50).Distinct().Count() == 50);
    }

    static void ResumeDoesNotSpendASlot()
    {
        // Returning to a context resumes the track it left playing. CommitToHistory
        // sees the cursor land on itself and does nothing — which is what keeps the
        // resume from spending a fresh slot.
        var cache = new ContextCache();
        cache.CommitToHistory("a.ogg", TrackRotation.WholePool);
        cache.CommitToHistory("a.ogg", TrackRotation.WholePool);
        cache.CommitToHistory("a.ogg", TrackRotation.WholePool);
        Check("a resume of the same track counts once", cache.rotation.Plays == 1,
              cache.rotation.Plays.ToString());
    }

    static void PrevNextReplayDoesNotSpendASlot()
    {
        var cache = new ContextCache();
        cache.CommitToHistory("a.ogg", TrackRotation.WholePool);
        cache.CommitToHistory("b.ogg", TrackRotation.WholePool);
        cache.CommitToHistory("c.ogg", TrackRotation.WholePool);
        Check("three new tracks = three slots", cache.rotation.Plays == 3, cache.rotation.Plays.ToString());
        cache.CommitToHistory("b.ogg", TrackRotation.WholePool);   // Prev
        cache.CommitToHistory("a.ogg", TrackRotation.WholePool);   // Prev
        cache.CommitToHistory("b.ogg", TrackRotation.WholePool);   // Next
        cache.CommitToHistory("c.ogg", TrackRotation.WholePool);   // Next
        Check("walking Prev/Next through history spends nothing more",
              cache.rotation.Plays == 3, cache.rotation.Plays.ToString());
    }

    // ── A library that changes underneath it ─────────────────────────────────

    static void TracksAddedMidCycleGetATurn()
    {
        string[] small = Pool(50);
        string[] grown = small.Concat(Enumerable.Range(50, 10).Select(i => $"track_{i:D4}.ogg")).ToArray();
        var rot = new TrackRotation();
        Play(rot, small, TrackRotation.WholePool, 25);
        List<string> after = Play(rot, grown, TrackRotation.WholePool, 35, seedOffset: 7);
        var newOnes = grown.Skip(50).ToArray();
        Check("music added mid-pass is eligible immediately and all of it plays",
              newOnes.All(after.Contains), newOnes.Count(after.Contains) + " of 10");
        Check("the pass still ends after the remaining tracks, not sooner",
              rot.Cycle == 1, "cycle " + rot.Cycle);
    }

    static void TracksRemovedMidCycleDoNotStallIt()
    {
        string[] full = Pool(50);
        var rot = new TrackRotation();
        Play(rot, full, TrackRotation.WholePool, 25);
        // Half the folder disappears — including tracks that already played and tracks
        // still owed a turn. The pass must finish on what is left, not wait forever.
        string[] shrunk = full.Where((_, i) => i % 2 == 0).ToArray();
        List<string> after = Play(rot, shrunk, TrackRotation.WholePool, 60, seedOffset: 3);
        Check("music removed mid-pass does not stall the pass", after.Count == 60);
        Check("the pass completes and rolls over", rot.Cycle >= 2, "cycle " + rot.Cycle);
        Check("nothing that was deleted is ever picked", after.All(shrunk.Contains));
    }

    static void GrowingPoolDuringARecursiveCrawl()
    {
        // A subfolders="true" context on a huge tree is published INCREMENTALLY: the
        // crawler swaps in a bigger snapshot every so often, for minutes, while music is
        // already playing from the partial one. The pass must span that — a track that
        // only appears at publish 4 still has to get its turn in the pass that started
        // at publish 1, and nothing played from an earlier partial may come round again.
        int[] stages = { 500, 2000, 6000, 10000 };
        var rot = new TrackRotation();
        var played = new List<string>();
        string last = null;
        int pick = 0;
        foreach (int size in stages)
        {
            // Each publish is a NEW array holding a superset — exactly what the crawler does.
            string[] pool = Pool(size);
            for (int i = 0; i < 200; i++)
            {
                string p = rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, Rng(++pick));
                rot.Commit(p, TrackRotation.WholePool);
                played.Add(p); last = p;
            }
        }
        Check("800 picks across four incremental publishes: not one repeat",
              played.Distinct().Count() == played.Count,
              played.Count - played.Distinct().Count() + " repeats");
        Check("the pass survives the pool quadrupling under it", rot.Cycle == 1, "cycle " + rot.Cycle);
        Check("tracks that only appeared in a later publish do get picked",
              played.Any(p => Array.IndexOf(Pool(500), p) < 0));
    }

    static void MovedTrackCountsAsNew()
    {
        // The rotation is keyed on the file's PATH. The loudness store is keyed on content
        // hash and so survives a move; this deliberately does not. A track moved to a new
        // path is a track the pass has not seen, so it becomes eligible again — which is
        // the same answer the pass gives for any newly-appeared file, and the only one
        // that does not require hashing every candidate on the pick path.
        string[] before = Pool(20);
        var rot = new TrackRotation();
        rot.Commit(before[3], TrackRotation.WholePool);

        string[] after = (string[])before.Clone();
        after[3] = "renamed_" + before[3];
        string[] elig = rot.Eligible(after, TrackRotation.WholePool, null, null, null, 1);
        Check("a track moved mid-pass is eligible again under its new name",
              elig.Contains(after[3]) && elig.Length == after.Length);

        // And the entry left behind for the old path is inert: it is not in the pool, so
        // it cannot be picked and — the part that matters — it cannot stop the pass ending.
        // 21 picks, not 20: the 20th empties the bag, the 21st is what refills it.
        List<string> rest = Play(rot, after, TrackRotation.WholePool, 21, seedOffset: 11);
        Check("the stale entry does not stall the pass", rot.Cycle == 2, "cycle " + rot.Cycle);
        Check("...and never resurfaces as a pick", !rest.Contains(before[3]));
        Check("all 20 live tracks still got their turn before the refill",
              rest.Take(20).Distinct().Count() == 20, rest.Take(20).Distinct().Count().ToString());
    }

    // ── Allocation ───────────────────────────────────────────────────────────

    static void FreshCycleReturnsThePoolItself()
    {
        string[] pool = Pool(3000);
        var rot = new TrackRotation();
        string[] e = rot.Eligible(pool, TrackRotation.WholePool, null, null, null, 1);
        Check("a fresh pass hands back the pool itself, no copy", ReferenceEquals(e, pool));
        string[] off = rot.Eligible(pool, TrackRotation.Off, null, null, null, 1);
        Check("memory=0 hands back the pool itself, no copy", ReferenceEquals(off, pool));

        rot.Commit(pool[0], TrackRotation.WholePool);
        string[] mid = rot.Eligible(pool, TrackRotation.WholePool, pool[1], null, null, 1);
        Check("bag and avoid are filtered in one pass",
              mid.Length == pool.Length - 2 && !mid.Contains(pool[0]) && !mid.Contains(pool[1]),
              mid.Length + " of " + pool.Length);
    }

    // THE POINT OF THE WHOLE TWO-PASS SHAPE. Building a candidate array costs one copy of
    // the pool per pick; past ~10,600 tracks that copy is over the 85 KB large-object-heap
    // threshold, so a big music folder would drop an LOH object on every track change —
    // 781 KB at a hundred thousand tracks, measured. Select walks the pool instead. This
    // asserts the result directly rather than trusting the code to have stayed that way.
    static void SelectAllocatesNothing()
    {
        string[] pool = Pool(50000);
        var rot = new TrackRotation();
        string last = null;
        for (int i = 0; i < 20; i++)   // warm up: JIT, dictionary growth, first-pass state
        {
            last = rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, Rng(i));
            rot.Commit(last, TrackRotation.WholePool);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
            rot.Select(pool, TrackRotation.WholePool, last, null, null, 3f, Rng(500 + i));
        long perPick = (GC.GetAllocatedBytesForCurrentThread() - before) / 200;
        Console.WriteLine($"      50,000-track folder, 200 picks: {perPick} bytes allocated per pick");
        Check("a pick over a 50,000-track folder allocates essentially nothing",
              perPick < 256, perPick + " bytes");

        // And with a ban set present, where the count cannot be cached at all.
        var banned = new HashSet<string> { pool[7] };
        rot.Select(pool, TrackRotation.WholePool, last, p => banned.Contains(p), null, 3f, Rng(9));
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
            rot.Select(pool, TrackRotation.WholePool, last, p => banned.Contains(p), null, 3f, Rng(600 + i));
        perPick = (GC.GetAllocatedBytesForCurrentThread() - before) / 200;
        Check("...and still nothing when bans are in play", perPick < 256, perPick + " bytes");
    }

    // Select walks the pool and Eligible materialises it. They share the eligibility rule
    // but not the traversal, so this pins them together: everything Select can return must
    // be in Eligible, and everything in Eligible must be reachable by Select.
    static void SelectAndEligibleAgree()
    {
        string[] pool = Pool(40);
        var favs = new HashSet<string> { pool[3], pool[9] };
        var bans = new HashSet<string> { pool[1], pool[2] };
        TrackFlag ban = p => bans.Contains(p), fav = p => favs.Contains(p);
        var rot = new TrackRotation();
        for (int i = 0; i < 7; i++) rot.Commit(pool[10 + i], TrackRotation.WholePool);

        string avoid = pool[20];
        var expected = new HashSet<string>(
            rot.Eligible(pool, TrackRotation.WholePool, avoid, ban, fav, TrackRotation.Allowance(3f)));
        var actual = new HashSet<string>();
        for (int i = 0; i < 20000; i++)
            actual.Add(rot.Select(pool, TrackRotation.WholePool, avoid, ban, fav, 3f, Rng(7000 + i)));

        Check("Select never returns anything Eligible excludes",
              actual.All(expected.Contains),
              string.Join(",", actual.Where(a => !expected.Contains(a)).Select(System.IO.Path.GetFileName)));
        Check("Select can reach every track Eligible includes",
              expected.All(actual.Contains),
              string.Join(",", expected.Where(e => !actual.Contains(e)).Select(System.IO.Path.GetFileName)));
        Check("neither of them counted the banned or avoided tracks",
              !expected.Contains(pool[1]) && !expected.Contains(pool[2]) && !expected.Contains(avoid));
    }

    // The cached eligible COUNT is the one piece of state that can go stale and produce a
    // wrong pick rather than a slow one, so every way it can be invalidated is asserted —
    // through behaviour, since the count itself is not observable from outside.
    static void CacheDoesNotGoStale()
    {
        string[] pool = Pool(100);
        var rot = new TrackRotation();
        rot.Commit(pool[0], TrackRotation.WholePool);

        // A play must be visible to the very next pick.
        rot.Commit(pool[2], TrackRotation.WholePool);
        var seen = new HashSet<string>();
        for (int i = 0; i < 4000; i++)
            seen.Add(rot.Select(pool, TrackRotation.WholePool, pool[1], null, null, 3f, Rng(8000 + i)));
        Check("a play invalidates the cached count", !seen.Contains(pool[2]) && !seen.Contains(pool[0]));
        Check("and the avoid is honoured on every one of 4000 picks", !seen.Contains(pool[1]));

        // A rescanned pool must be picked up even though nothing has played since.
        string[] grown = pool.Concat(new[] { "brand_new.ogg" }).ToArray();
        var seen2 = new HashSet<string>();
        for (int i = 0; i < 4000; i++)
            seen2.Add(rot.Select(grown, TrackRotation.WholePool, pool[1], null, null, 3f, Rng(9000 + i)));
        Check("a rescanned pool invalidates it", seen2.Contains("brand_new.ogg"));

        rot.Reset();
        var seen3 = new HashSet<string>();
        for (int i = 0; i < 4000; i++)
            seen3.Add(rot.Select(grown, TrackRotation.WholePool, pool[1], null, null, 3f, Rng(9500 + i)));
        Check("a reset invalidates it", seen3.Contains(pool[0]) && seen3.Contains(pool[2]));

        // Changing the MEMORY invalidates it too — the same pool and the same plays give a
        // different answer under a window than under the whole pool.
        var rot2 = new TrackRotation();
        for (int i = 0; i < 30; i++) rot2.Commit(pool[i], 5);
        string wholePool = rot2.Select(pool, TrackRotation.WholePool, null, null, null, 3f, Rng(9900));
        bool windowCanReturnAnEarlyTrack = false;
        for (int i = 0; i < 2000 && !windowCanReturnAnEarlyTrack; i++)
            if (Array.IndexOf(pool, rot2.Select(pool, 5, null, null, null, 3f, Rng(9910 + i))) < 25)
                windowCanReturnAnEarlyTrack = true;
        Check("switching the memory setting invalidates it", windowCanReturnAnEarlyTrack);
    }

    // ── What it actually changes ─────────────────────────────────────────────

    static void HowSoonDoesATrackComeBack()
    {
        const int Size = 200, Trials = 400;
        Console.WriteLine();
        Console.WriteLine($"   A {Size}-track folder. Picks before some track is heard a second time:");
        foreach (int memory in new[] { TrackRotation.Off, 25, TrackRotation.WholePool })
        {
            var firsts = new List<int>();
            for (int t = 0; t < Trials; t++)
            {
                string[] pool = Pool(Size);
                var rot = new TrackRotation();
                var seen = new HashSet<string>();
                string last = null;
                for (int i = 1; i <= 4000; i++)
                {
                    string p = rot.Select(pool, memory, last, null, null, 3f, Rng(t * 5000 + i));
                    rot.Commit(p, memory); last = p;
                    if (!seen.Add(p)) { firsts.Add(i); break; }
                }
            }
            double mean = firsts.Average();
            string label = memory == TrackRotation.Off ? "0  (the old behaviour)"
                         : memory < 0 ? "-1 (whole pool, the new default)"
                         : memory + " (window)";
            Console.WriteLine($"     TrackNoRepeatMemory = {label,-32} "
                            + $"soonest {firsts.Min(),4}   average {mean,7:F1}");
            if (memory == TrackRotation.WholePool)
                Check($"whole-pool mode NEVER repeats before all {Size} have played",
                      firsts.All(f => f == Size + 1), "soonest " + firsts.Min());
            if (memory == TrackRotation.Off)
                Check("the old behaviour could repeat within the first handful of picks",
                      firsts.Min() < 20, "soonest " + firsts.Min());
        }
    }

    // ── Machinery ────────────────────────────────────────────────────────────

    // A fixed-seed rng so a failure is reproducible. Each call site derives its own
    // stream from an integer, which also keeps the trials independent.
    internal static TrackRng Rng(int seed)
    {
        uint s = (uint)(seed * 2654435761u) | 1u;
        return () =>
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            return (s >> 8) * (1.0 / 16777216.0);   // 24 bits → [0,1)
        };
    }

    internal static string[] Pool(int n) =>
        Enumerable.Range(0, n).Select(i => $"track_{i:D4}.ogg").ToArray();

    internal static List<string> Play(TrackRotation rot, string[] pool, int memory, int picks,
                             TrackFlag ban = null, TrackFlag fav = null, float favWeight = 3f,
                             int seedOffset = 0)
    {
        var played = new List<string>();
        // avoid = the track last SELECTED, which is what PickNextFile passes.
        string last = null;
        TrackRng rng = Rng(seedOffset + 1);
        for (int i = 0; i < picks; i++)
        {
            string p = rot.Select(pool, memory, last, ban, fav, favWeight, rng);
            if (p == null) break;
            rot.Commit(p, memory);
            played.Add(p);
            last = p;
        }
        return played;
    }

    internal static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("── " + title + " " + new string('─', Math.Max(0, 70 - title.Length)));
    }

    internal static void Check(string what, bool ok, string detail = null)
    {
        _checks++;
        if (!ok) _failed++;
        Console.WriteLine($"   [{(ok ? "ok" : "FAIL")}] {what}"
                        + (detail != null && !ok ? "   → " + detail : ""));
    }
}
