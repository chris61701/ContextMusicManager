using System;
using System.Collections.Generic;
using System.Linq;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// "IF ONE TRACK IN A THOUSAND IS A FAVOURITE, HOW OFTEN DOES IT PLAY?"
//
// The ratio is easy and misleading. A favourite gets FavouriteWeight turns per
// pass instead of one, so it plays three times as often as any particular track
// — and three times almost-never is still almost-never. What matters to someone
// who just pressed the favourite key is the GAP: how long until they hear it,
// in hours of play, not in ratios.
//
// This measures that gap directly and sweeps the weight, so the answer to "what
// should I set FavouriteWeight to" is a number off a table rather than a guess.
// ─────────────────────────────────────────────────────────────────────────────
internal static class FavouriteReach
{
    // A track change every ~3.5 minutes is a fair average for scored music.
    const double MinutesPerTrack = 3.5;

    internal static void Run()
    {
        Program.Section("One favourite in a thousand: how often does it actually play?");
        Measure();
    }

    struct Result
    {
        public int Plays, Passes, Picks;
        public double MeanGap, MinGap, MaxGap, MedianGap;
    }

    static Result Play(int size, int favourites, float weight, int memory, int picks, int seed)
    {
        string[] pool = Program.Pool(size);
        var favs = new HashSet<string>(pool.Take(favourites));
        TrackFlag fav = favourites > 0 ? (TrackFlag)(p => favs.Contains(p)) : null;
        string target = pool[0];

        var rot = new TrackRotation();
        TrackRng rng = Program.Rng(seed);
        string last = null;
        var hits = new List<int>();
        for (int i = 0; i < picks; i++)
        {
            string p = rot.Select(pool, memory, last, null, fav, weight, rng);
            rot.Commit(p, memory);
            last = p;
            if (p == target) hits.Add(i);
        }

        var gaps = new List<double>();
        for (int i = 1; i < hits.Count; i++) gaps.Add(hits[i] - hits[i - 1]);
        gaps.Sort();
        return new Result
        {
            Plays = hits.Count,
            Passes = rot.Cycle,
            Picks = picks,
            MeanGap   = gaps.Count > 0 ? gaps.Average() : 0,
            MinGap    = gaps.Count > 0 ? gaps.First()   : 0,
            MaxGap    = gaps.Count > 0 ? gaps.Last()    : 0,
            MedianGap = gaps.Count > 0 ? gaps[gaps.Count / 2] : 0,
        };
    }

    static string Hours(double tracks) => (tracks * MinutesPerTrack / 60.0).ToString("F1") + " h";

    static void Measure()
    {
        const int Size = 1000, Picks = 40000;   // ~40 passes

        Result r = Play(Size, 1, 3f, TrackRotation.WholePool, Picks, 4711);
        double perPass = (double)r.Plays / r.Passes;
        Console.WriteLine();
        Console.WriteLine($"   ONE favourite in {Size} tracks, FavouriteWeight 3 (the default),");
        Console.WriteLine($"   whole-pool anti-repeat, {Picks:N0} tracks played (~{r.Passes} passes):");
        Console.WriteLine();
        Console.WriteLine($"     it played           {r.Plays} times = {perPass:F1} per pass "
                        + $"({100.0 * r.Plays / Picks:F2}% of everything played)");
        Console.WriteLine($"     an ordinary track   ~{(double)r.Passes / 1:F0} times "
                        + $"({100.0 / Size:F2}% of everything played)");
        Console.WriteLine();
        Console.WriteLine($"     gap between plays   shortest {r.MinGap,6:F0} tracks = {Hours(r.MinGap)}");
        Console.WriteLine($"                         median   {r.MedianGap,6:F0} tracks = {Hours(r.MedianGap)}");
        Console.WriteLine($"                         mean     {r.MeanGap,6:F0} tracks = {Hours(r.MeanGap)}");
        Console.WriteLine($"                         longest  {r.MaxGap,6:F0} tracks = {Hours(r.MaxGap)}");

        Program.Check("the favourite really does get FavouriteWeight turns per pass",
                      Math.Abs(perPass - 3.0) < 0.15, perPass.ToString("F2"));
        Program.Check("which is 3x an ordinary track's share of the airtime",
                      Math.Abs((double)r.Plays / Picks * Size / 3.0 - 1.0) < 0.1,
                      (100.0 * r.Plays / Picks).ToString("F2") + "%");

        // What would you have to set the weight to?
        Console.WriteLine();
        Console.WriteLine($"   The same favourite at other FavouriteWeight settings:");
        Console.WriteLine();
        Console.WriteLine($"     {"weight",6} | {"plays/pass",10} | {"% of plays",10} | {"median gap",12} | {"longest gap",12}");
        Console.WriteLine("     " + new string('-', 62));
        foreach (float w in new[] { 1f, 3f, 10f, 30f, 100f, 300f })
        {
            Result x = Play(Size, 1, w, TrackRotation.WholePool, Picks, 4711);
            Console.WriteLine($"     {w,6:F0} | {(double)x.Plays / x.Passes,10:F1} | "
                            + $"{100.0 * x.Plays / Picks,9:F2}% | {Hours(x.MedianGap),12} | {Hours(x.MaxGap),12}");
        }

        // A recency window instead of the whole pool — the other lever.
        Console.WriteLine();
        Console.WriteLine($"   Or with TrackNoRepeatMemory set to a window instead of the whole folder");
        Console.WriteLine($"   (weight 3 throughout):");
        Console.WriteLine();
        Console.WriteLine($"     {"memory",8} | {"% of plays",10} | {"median gap",12} | {"longest gap",12}");
        Console.WriteLine("     " + new string('-', 50));
        foreach (int m in new[] { 50, 200, TrackRotation.WholePool })
        {
            Result x = Play(Size, 1, 3f, m, Picks, 4711);
            string label = m < 0 ? "whole" : m.ToString();
            Console.WriteLine($"     {label,8} | {100.0 * x.Plays / Picks,9:F2}% | "
                            + $"{Hours(x.MedianGap),12} | {Hours(x.MaxGap),12}");
        }

        // The worst case is structural and worth naming: the favourite can spend all its
        // turns early in one pass and late in the next, so the longest gap approaches two
        // passes even though the average is one third of a pass.
        Program.Check("the longest gap can approach two passes — that is inherent to a bag",
                      r.MaxGap > Size, r.MaxGap.ToString("F0"));
        Console.WriteLine();
    }
}
