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

        int expectedTurns = TrackRotation.Allowance(TrackRotation.ScaledWeight(3f, Size));
        Program.Check($"a weight-3 favourite in {Size} tracks gets its scaled {expectedTurns} turns per pass",
                      Math.Abs(perPass - expectedTurns) < expectedTurns * 0.1,
                      perPass.ToString("F1") + " vs " + expectedTurns);

        // THE POINT OF THE SCALING: the same setting, across library sizes.
        Console.WriteLine();
        Console.WriteLine($"   ONE favourite at the default FavouriteWeight 3, across folder sizes.");
        Console.WriteLine($"   'unscaled' is what a raw multiplier gave — the same number meaning");
        Console.WriteLine($"   something completely different depending on how much music you have:");
        Console.WriteLine();
        Console.WriteLine($"     {"folder",7} | {"turns/pass",10} | {"% of plays",10} | {"median gap",11} | {"longest gap",11} | {"unscaled median",15}");
        Console.WriteLine("     " + new string('-', 82));
        var shares = new List<double>();
        foreach (int size in new[] { 10, 25, 50, 100, 250, 1000, 5000 })
        {
            int picks = Math.Max(20000, size * 25);
            Result x = Play(size, 1, 3f, TrackRotation.WholePool, picks, 4711);
            // What the old raw multiplier would have given: allowance fixed at 3.
            double unscaledGap = (size - 1 + 3.0) / 3.0;
            int turns = (int)Math.Round((double)x.Plays / x.Passes);
            double share = 100.0 * x.Plays / picks;
            if (size >= TrackRotation.WeightReferencePool) shares.Add(share);
            Console.WriteLine($"     {size,7:N0} | {turns,10} | {share,9:F2}% | "
                            + $"{Hours(x.MedianGap),11} | {Hours(x.MaxGap),11} | {Hours(unscaledGap),15}");
        }

        // THE WHOLE POINT, asserted: the setting has to mean the same thing at every size
        // at or above the reference. Below it the raw weight stands, which is why those
        // rows are excluded — a 10-track folder cannot give one track only 5.7% of the
        // airtime without giving it less than one turn per pass.
        Program.Check("from the reference size upward, one setting = one constant share of the music",
                      shares.Max() - shares.Min() < 0.3,
                      $"{shares.Min():F2}%..{shares.Max():F2}% across 50..5000 tracks");

        // What would you have to set the weight to, now that it scales?
        Console.WriteLine();
        Console.WriteLine($"   And the weight dial itself, in a {Size}-track folder:");
        Console.WriteLine();
        Console.WriteLine($"     {"weight",6} | {"turns/pass",10} | {"% of plays",10} | {"median gap",12} | {"longest gap",12}");
        Console.WriteLine("     " + new string('-', 62));
        foreach (float w in new[] { 1f, 2f, 3f, 6f, 12f, 25f })
        {
            Result x = Play(Size, 1, w, TrackRotation.WholePool, Picks, 4711);
            Console.WriteLine($"     {w,6:F0} | {(double)x.Plays / x.Passes,10:F0} | "
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

        // The tail is structural and worth naming rather than hiding. A favourite's turns
        // are drawn at random, so it can spend them early in one pass and late in the next
        // and go quiet in between — the median gap is half an hour, the worst is a day.
        // Evening that out would mean SCHEDULING the turns across the pass instead of
        // sampling them, which is a different design; this just pins the shape so a
        // regression that made the tail worse would show up.
        Program.Check("the median gap is far shorter than the worst gap — the tail is skewed",
                      r.MaxGap > r.MedianGap * 5, $"median {r.MedianGap:F0}, max {r.MaxGap:F0}");
        Program.Check("but the worst gap is still well inside a single pass",
                      r.MaxGap < Size, r.MaxGap.ToString("F0"));
        Console.WriteLine();
    }
}
