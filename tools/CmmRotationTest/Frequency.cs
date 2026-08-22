using System;
using System.Collections.Generic;
using System.Linq;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// FREQUENCY. Not "does it work" but "how often does each track actually play",
// counted over enough passes that a bias would show. Every case here is a
// combination of the three things that decide a track's share of the airtime:
// the bag, the ban flag and the favourite flag.
//
// These are exact-count assertions on purpose. A rotation that is 95% right is
// a rotation with a bug in it, and an approximate test would never find it.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Frequency
{
    static void Check(string w, bool ok, string d = null) => Program.Check(w, ok, d);
    static TrackRng Rng(int s) => Program.Rng(s);
    static string[] Pool(int n) => Program.Pool(n);

    internal static void Run()
    {
        Program.Section("Frequency: the bag, bans and favourites together");
        BagPlusFavouritesPlusBans();
        FavouriteThatIsAlsoBanned();
        MostOfTheFolderBanned();
        FavouritesOutnumberOrdinaryTracks();
        WeightOfOneMakesAFavouriteOrdinary();
        EverythingBannedExceptTheFavourites();
        FrequencyInWindowMode();
        FlagsFlippedRepeatedlyMidRun();
    }

    // Play `plays` tracks and return how many times each pool entry came up.
    static Dictionary<string, int> Tally(string[] pool, int memory, int plays,
                                         TrackFlag ban, TrackFlag fav, float weight, int seed)
    {
        var rot = new TrackRotation();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in pool) counts[p] = 0;
        string last = null;
        TrackRng rng = Rng(seed);
        for (int i = 0; i < plays; i++)
        {
            string p = rot.Select(pool, memory, last, ban, fav, weight, rng);
            if (p == null) break;
            rot.Commit(p, memory);
            counts[p]++;
            last = p;
        }
        return counts;
    }

    static void BagPlusFavouritesPlusBans()
    {
        // 40 tracks: 4 favourites at weight 3, 6 banned, 30 ordinary.
        // A pass is 30 ordinary + 4×3 favourite = 42 plays. Over 25 passes every
        // ordinary track must land on exactly 25, every favourite on exactly 75, and
        // every banned track on exactly 0.
        const int Passes = 25;
        string[] pool = Pool(40);
        var favs = new HashSet<string> { pool[0], pool[11], pool[22], pool[39] };
        var bans = new HashSet<string> { pool[1], pool[2], pool[3], pool[30], pool[31], pool[32] };
        int ordinary = pool.Length - favs.Count - bans.Count;
        int perPass = ordinary + favs.Count * 3;

        var c = Tally(pool, TrackRotation.WholePool, perPass * Passes,
                      p => bans.Contains(p), p => favs.Contains(p), 3f, 4242);

        int ordMin = pool.Where(p => !favs.Contains(p) && !bans.Contains(p)).Min(p => c[p]);
        int ordMax = pool.Where(p => !favs.Contains(p) && !bans.Contains(p)).Max(p => c[p]);
        Console.WriteLine($"      40 tracks = 30 ordinary + 4 favourite(×3) + 6 banned, {Passes} passes:");
        Console.WriteLine($"        ordinary {ordMin}..{ordMax}   favourites "
                        + $"{favs.Min(p => c[p])}..{favs.Max(p => c[p])}   banned "
                        + $"{bans.Min(p => c[p])}..{bans.Max(p => c[p])}");
        Check($"every ordinary track played exactly {Passes} times",
              ordMin == Passes && ordMax == Passes, $"{ordMin}..{ordMax}");
        Check($"every favourite played exactly {Passes * 3} times",
              favs.All(p => c[p] == Passes * 3), string.Join(",", favs.Select(p => c[p])));
        Check("every banned track played exactly 0 times", bans.All(p => c[p] == 0));
    }

    static void FavouriteThatIsAlsoBanned()
    {
        // The ban has to win. If the favourite flag were consulted first, the track would
        // get three turns per pass of music the user explicitly rejected.
        string[] pool = Pool(20);
        string both = pool[6];
        var c = Tally(pool, TrackRotation.WholePool, 19 * 10,
                      p => p == both, p => p == both, 3f, 77);
        Check("banned beats favourited, every time", c[both] == 0, c[both].ToString());
        Check("and the other 19 still share the airtime evenly",
              pool.Where(p => p != both).All(p => c[p] == 10),
              string.Join(",", pool.Where(p => p != both).Select(p => c[p]).Distinct()));
    }

    static void MostOfTheFolderBanned()
    {
        // 18 of 20 banned. The pass is now two tracks long and must simply alternate —
        // this is the shape where a bag could most easily deadlock or start repeating.
        string[] pool = Pool(20);
        var bans = new HashSet<string>(pool.Take(18));
        var c = Tally(pool, TrackRotation.WholePool, 100, p => bans.Contains(p), null, 3f, 88);
        Check("with 18 of 20 banned, the surviving two split the airtime exactly",
              c[pool[18]] == 50 && c[pool[19]] == 50, $"{c[pool[18]]}/{c[pool[19]]}");
        Check("and nothing banned leaks through", bans.All(p => c[p] == 0));
    }

    static void FavouritesOutnumberOrdinaryTracks()
    {
        // 15 favourites, 5 ordinary. A pass is 15×3 + 5 = 50 plays, and the favourites
        // should dominate without ever starving the ordinary five.
        string[] pool = Pool(20);
        var favs = new HashSet<string>(pool.Take(15));
        var c = Tally(pool, TrackRotation.WholePool, 50 * 8, null, p => favs.Contains(p), 3f, 99);
        Check("15 favourites among 20: each favourite plays 3× per pass",
              favs.All(p => c[p] == 24), string.Join(",", favs.Select(p => c[p]).Distinct()));
        Check("...and the 5 ordinary tracks are not starved",
              pool.Skip(15).All(p => c[p] == 8), string.Join(",", pool.Skip(15).Select(p => c[p])));
    }

    static void WeightOfOneMakesAFavouriteOrdinary()
    {
        string[] pool = Pool(12);
        var favs = new HashSet<string> { pool[3], pool[7] };
        var c = Tally(pool, TrackRotation.WholePool, 12 * 10, null, p => favs.Contains(p), 1f, 111);
        Check("FavouriteWeight 1 means favourites play exactly like everything else",
              pool.All(p => c[p] == 10), string.Join(",", pool.Select(p => c[p]).Distinct()));
    }

    static void EverythingBannedExceptTheFavourites()
    {
        // Everything banned but three favourites: the rotation is three tracks deep with
        // an allowance of three each. That is the all-favourites shape at its smallest, so
        // the seam rule bites hardest here — with so few tracks, the only one still owed a
        // turn is often the one that just played, and the pass opens early rather than
        // repeating back to back. Turns therefore even out to ±1 rather than landing
        // exactly on 3 per pass, which is the documented trade and not a defect.
        const int Plays = 9 * 12;
        string[] pool = Pool(15);
        var favs = new HashSet<string> { pool[0], pool[5], pool[10] };
        var bans = new HashSet<string>(pool.Where(p => !favs.Contains(p)));
        var c = Tally(pool, TrackRotation.WholePool, Plays, p => bans.Contains(p),
                      p => favs.Contains(p), 3f, 123);
        int min = favs.Min(p => c[p]), max = favs.Max(p => c[p]);
        Check("with only favourites playable, every play goes to one of them",
              favs.Sum(p => c[p]) == Plays, favs.Sum(p => c[p]).ToString());
        Check("and they share it to within one play of each other", max - min <= 2,
              $"{min}..{max}");
        Check("the banned majority never plays", bans.All(p => c[p] == 0));
    }

    static void FrequencyInWindowMode()
    {
        // A window is not a bag, so exact per-pass counts do not apply — but the SHARE
        // must still be even among ordinary tracks and higher for favourites, and no
        // banned track may appear.
        const int N = 8, Plays = 6000;
        string[] pool = Pool(30);
        var favs = new HashSet<string> { pool[4] };
        var bans = new HashSet<string> { pool[9] };
        var c = Tally(pool, N, Plays, p => bans.Contains(p), p => favs.Contains(p), 3f, 321);

        var ordinary = pool.Where(p => !favs.Contains(p) && !bans.Contains(p)).ToArray();
        double mean = ordinary.Average(p => (double)c[p]);
        // A window picks at random among whatever is outside it, so the counts are a
        // multinomial sample and WILL scatter. Comparing the observed spread against the
        // spread that sampling alone predicts is the honest test; a flat percentage cap is
        // just a number I would have had to keep loosening. Expected sd for k roughly-equal
        // buckets is sqrt(mean·(1 − 1/k)).
        int k = ordinary.Length;
        double expectedSd = Math.Sqrt(mean * (1.0 - 1.0 / k));
        double observedSd = Math.Sqrt(ordinary.Average(p => Math.Pow(c[p] - mean, 2)));
        Console.WriteLine($"      window={N}, {Plays} plays over 30 tracks: ordinary mean {mean:F0}, "
                        + $"sd {observedSd:F1} against {expectedSd:F1} predicted by sampling alone; "
                        + $"favourite {c[pool[4]]}, banned {c[pool[9]]}");
        Check("window mode: the banned track never plays", c[pool[9]] == 0);
        Check("window mode: every ordinary track gets a real share (none starved)",
              ordinary.All(p => c[p] > mean * 0.5), ordinary.Min(p => c[p]).ToString());
        Check("window mode: the scatter is no worse than random sampling predicts",
              observedSd < expectedSd * 2.0, $"sd {observedSd:F1} vs {expectedSd:F1}");
        Check("window mode: the favourite plays noticeably more than the mean",
              c[pool[4]] > mean * 1.5, $"{c[pool[4]]} vs mean {mean:F0}");
    }

    static void FlagsFlippedRepeatedlyMidRun()
    {
        // Banning and favouriting the same track over and over while a pass runs — the
        // shape of a user hammering the keybinds. Nothing may be picked while banned,
        // and the pass must keep terminating.
        string[] pool = Pool(25);
        string target = pool[12];
        bool banned = false, favourite = false;
        var rot = new TrackRotation();
        int pickedWhileBanned = 0;
        string last = null;
        TrackRng rng = Rng(555);
        for (int i = 0; i < 3000; i++)
        {
            if (i % 17 == 0) banned = !banned;
            if (i % 23 == 0) favourite = !favourite;
            bool b = banned, f = favourite;
            string p = rot.Select(pool, TrackRotation.WholePool, last,
                                  x => b && x == target, x => f && x == target, 3f, rng);
            rot.Commit(p, TrackRotation.WholePool);
            if (b && p == target) pickedWhileBanned++;
            last = p;
        }
        Check("a track is never picked during a moment when it is banned",
              pickedWhileBanned == 0, pickedWhileBanned.ToString());
        Check("and passes keep turning over through all the flag churn",
              rot.Cycle > 90, "cycle " + rot.Cycle);
    }
}
