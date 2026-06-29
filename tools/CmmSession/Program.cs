using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// CmmSession — synthetic long-gameplay-session benchmark (Arena C).
//
// Drives a deterministic, multi-day synthetic session through the REAL decision
// pipeline — linked ScriptHost (context.lua via MoonSharp) + the ported selection
// layer (Engine.cs) + real per-transition decode (AudioDecoder.OpenStreamingReader,
// optional one-time EBU-R128 sweep) — and reports BOTH:
//   • Memory: per-phase allocation (resolve / select / decode), GC gen0/1/2 counts,
//     allocation rate, and forced-GC managed-heap drift across the session (leak check).
//   • Latency: p50/p95/p99/max for per-tick resolve + select, and per-transition decode.
//
// Where Arena A (in-game CMMPerf) catches a single hitch and Arena B (CmmBench) times
// one decode, this compresses hours of play into seconds to surface slow accumulation —
// GC pressure and heap growth that only a long session reveals.
//
//   dotnet run --project tools/CmmSession -c Release -- [music-dir] [options]
//
// Options:
//   --hours <h>        simulated session length in game-world hours (default 24)
//   --tick <s>         scan cadence seconds (default 0.5 = ContextScanCooldown)
//   --day-minutes <m>  real minutes per in-game day (default 60)
//   --seed <n>         RNG seed for the timeline (default 1; deterministic)
//   --no-decode        skip per-transition decode (resolve+select+GC only)
//   --sweep            also EBU-R128 measure each track once on first play (background sweep)
//   --no-dms           Dynamic Music disabled (changes Vanilla/Silence fallbacks)
//   -v                 verbose (per-transition log + decoder splits)
// ─────────────────────────────────────────────────────────────────────────────

internal static class Program
{
    static int Main(string[] args)
    {
        AppDomain.MonitoringIsEnabled = true;   // precise per-AppDomain allocation tracking

        string musicDir = null;
        double hours = 24; float tick = 0.5f, dayMinutes = 60f; int seed = 1;
        bool decode = true, sweep = false, verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hours": hours = double.Parse(args[++i]); break;
                case "--tick": tick = float.Parse(args[++i]); break;
                case "--day-minutes": dayMinutes = float.Parse(args[++i]); break;
                case "--seed": seed = int.Parse(args[++i]); break;
                case "--no-decode": decode = false; break;
                case "--sweep": sweep = true; break;
                case "--no-dms": ContextMusicManager.DmsEnabled = false; break;
                case "-v": verbose = true; break;
                case "-h": case "--help": Usage(); return 0;
                default: musicDir ??= args[i]; break;
            }
        }
        if (musicDir == null) musicDir = @"J:\7D2D_Custom_Music";
        CMMConfig.DebugLogging = verbose;
        Log.Quiet = !verbose;   // silence ScriptHost's per-load chatter unless -v

        string repoRoot = FindRepoRoot();
        bool haveLib = Directory.Exists(musicDir);

        // ── Load the real decision script + context table + library index ──
        // MoonSharp's static init tries to register a Unity asset loader and logs a
        // harmless NRE when running headless; mute stdout/stderr just over Load so it
        // doesn't pollute the report. ScriptHost catches it internally either way.
        var savedOut = Console.Out; var savedErr = Console.Error;
        Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
        try { ScriptHost.Load(repoRoot ?? Directory.GetCurrentDirectory()); }
        finally { Console.SetOut(savedOut); Console.SetError(savedErr); }
        if (!ScriptHost.Active)
        {
            Console.WriteLine("FATAL: no resolver loaded (MoonSharp/context.lua failed). Aborting.");
            return 1;
        }
        ContextMusicManager.InitContexts();
        ContextMusicManager.BuildFileIndex(haveLib ? musicDir : null);

        int totalTicks = Math.Max(1, (int)(hours * 3600.0 / tick));
        Console.WriteLine($"CmmSession — {hours:F0}h session, tick {tick:F2}s ({totalTicks:N0} ticks), seed {seed}, " +
                          $"day={dayMinutes:F0}min, DMS={(ContextMusicManager.DmsEnabled ? "on" : "off")}");
        Console.WriteLine($"  script   : {(ScriptHost.UsingEmbeddedDefault ? "embedded default" : "Config/context.lua")}");
        Console.WriteLine($"  library  : {musicDir} ({(haveLib ? $"{ContextMusicManager.IndexedContexts} contexts / {ContextMusicManager.IndexedFiles} files" : "MISSING — resolve+GC only")})");
        Console.WriteLine($"  decode   : {(decode && haveLib ? (sweep ? "per-transition open + first-play R128 sweep" : "per-transition open") : "off")}\n");

        // ── Preallocate sample buffers BEFORE the heap baseline so they don't skew drift ──
        var resolveMs = new double[totalTicks];
        var selectMs = new double[totalTicks];
        var decodeMs = new List<double>(totalTicks / 4 + 16);
        var decodeByExt = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var measured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scratch = new AudioDecoder.MeasureScratch();
        var transitionsByTop = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Crossfade-pressure tracking: sim-time of each transition + the gap to the previous
        // one, and whether that gap was shorter than the previous transition's fade window
        // (→ it interrupted an in-flight crossfade = jarring).
        var transitionTimes = new List<float>(totalTicks / 16 + 16);
        var gaps = new List<double>(totalTicks / 16 + 16);
        int jarring = 0;
        float prevTransTime = -1f, prevFadeWindow = 0f;

        var gen = new SessionGenerator(seed, tick, dayMinutes);

        // Warm up the script + JIT (first Resolve compiles paths) so the baseline is steady-state.
        SimClock.Now = 0f;
        ScriptHost.Resolve(gen.Next());

        long allocResolve = 0, allocSelect = 0, allocDecode = 0;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long heapBaseline = GC.GetTotalMemory(true);
        var heapCheckpoints = new List<(double pct, long heap, double simHour)>();
        int transitions = 0, decodes = 0, decodeErrors = 0;
        long allocStart = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        var wall = Stopwatch.StartNew();

        for (int t = 0; t < totalTicks; t++)
        {
            SimClock.Now = (t + 1) * tick;
            GameState gs = gen.Next();

            // Phase 1 — resolve (linked ScriptHost: Lua marshal + decision; the alloc suspect).
            long a0 = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            long ts = Stopwatch.GetTimestamp();
            List<string> tokens = ScriptHost.Resolve(gs);
            resolveMs[t] = Ms(ts);
            long a1 = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            allocResolve += a1 - a0;

            // Phase 2 — select (ported stack/fallback/warmup gate).
            ts = Stopwatch.GetTimestamp();
            var r = ContextMusicManager.Tick(tokens, gs);
            selectMs[t] = Ms(ts);
            long a2 = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            allocSelect += a2 - a1;

            // Phase 3 — decode on a real transition to a file-backed context.
            if (r.Transitioned)
            {
                transitions++;
                transitionsByTop[r.Top] = transitionsByTop.TryGetValue(r.Top, out int c) ? c + 1 : 1;

                // Inter-transition spacing vs the previous transition's fade window.
                float nowT = SimClock.Now;
                if (prevTransTime >= 0f)
                {
                    double gap = nowT - prevTransTime;
                    gaps.Add(gap);
                    if (gap < prevFadeWindow) jarring++;
                }
                transitionTimes.Add(nowT);
                prevTransTime = nowT;
                // Fade window for THIS transition = max(crossfade, the incoming context's FadeInDuration).
                var topCtx = ContextMusicManager.GetContext(r.Top);
                prevFadeWindow = Math.Max(ContextMusicManager.CrossFadeDuration, topCtx?.FadeInDuration ?? 0);
                if (decode && haveLib && r.Top != "Vanilla" && r.Top != "Silence")
                {
                    string file = ContextMusicManager.PickFile(r.Top);
                    if (file != null)
                    {
                        long d0 = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                        ts = Stopwatch.GetTimestamp();
                        try
                        {
                            if (sweep && measured.Add(file))
                                AudioDecoder.MeasureLoudness(file, scratch);
                            using (var rdr = AudioDecoder.OpenStreamingReader(file)) { }
                            double ms = Ms(ts);
                            decodeMs.Add(ms);
                            string ext = Path.GetExtension(file).ToLowerInvariant();
                            if (!decodeByExt.TryGetValue(ext, out var l)) decodeByExt[ext] = l = new List<double>();
                            l.Add(ms);
                            decodes++;
                            if (verbose) Console.WriteLine($"  [{r.Top}] {Path.GetFileName(file)} open {ms:F2}ms");
                        }
                        catch (Exception ex) { decodeErrors++; if (verbose) Console.WriteLine($"  DECODE ERR {Path.GetFileName(file)}: {ex.Message}"); }
                        allocDecode += AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - d0;
                    }
                }
            }

            // Heap checkpoints at 25/50/75/100% (forced GC → managed-heap leak signal).
            if (t == totalTicks / 4 || t == totalTicks / 2 || t == 3 * totalTicks / 4 || t == totalTicks - 1)
                heapCheckpoints.Add(((t + 1) * 100.0 / totalTicks, GC.GetTotalMemory(true), SimClock.Now / 3600.0));
        }

        wall.Stop();
        long allocTotal = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocStart;
        Report(totalTicks, tick, hours, gen.WorldDay, wall.Elapsed.TotalSeconds,
               transitions, decodes, decodeErrors, transitionsByTop,
               resolveMs, selectMs, decodeMs, decodeByExt,
               allocResolve, allocSelect, allocDecode, allocTotal,
               GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2,
               heapBaseline, heapCheckpoints);
        PrintCrossfadePressure(gaps, jarring, transitionTimes);
        return 0;
    }

    // ── Crossfade pressure (jarring transitions) ──────────────────────────────
    // A transition arriving before the previous one's fade window elapsed interrupts an
    // in-flight crossfade (the game does StopCoroutine(_crossFadeCoroutine) and starts a
    // new fade from a partially-faded state) — the audible "jarring" case. Reports the gap
    // distribution, the jarring count/rate, sub-second buckets, and the worst burst (most
    // transitions inside one CrossFadeDuration window).
    static void PrintCrossfadePressure(List<double> gaps, int jarring, List<float> times)
    {
        Console.WriteLine("\n── Crossfade pressure (jarring = interrupts an in-flight fade) ────────");
        if (gaps.Count == 0) { Console.WriteLine("   (fewer than 2 transitions — nothing to space)"); return; }
        var g = gaps.ToArray(); Array.Sort(g);
        double cf = ContextMusicManager.CrossFadeDuration;
        int under(double s) => CountUnder(g, s);
        Console.WriteLine($"   gap between transitions: min {g[0]:F2}s  p1 {Pct(g, 1):F2}s  p5 {Pct(g, 5):F2}s  p50 {Pct(g, 50):F2}s  (CrossFadeDuration {cf:F1}s)");
        Console.WriteLine($"   tight gaps: <0.5s {under(0.5)}   <1s {under(1.0)}   <2s {under(2.0)}   <{cf:F1}s(fade) {jarring}");
        Console.WriteLine($"   JARRING (gap < prior fade window): {jarring} of {gaps.Count}  ({100.0 * jarring / gaps.Count:F1}% of transitions)");

        // Worst burst: most transitions within any CrossFadeDuration-wide sliding window.
        int best = 1, lo = 0;
        for (int hi = 0; hi < times.Count; hi++)
        {
            while (times[hi] - times[lo] > cf) lo++;
            if (hi - lo + 1 > best) best = hi - lo + 1;
        }
        Console.WriteLine($"   worst burst: {best} transitions inside a single {cf:F1}s window" +
                          (best >= 3 ? "  ← rapid-interrupt stress (combat↔tension↔combat churn)" : ""));
    }

    static int CountUnder(double[] sorted, double s)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi) { int m = (lo + hi) / 2; if (sorted[m] < s) lo = m + 1; else hi = m; }
        return lo;
    }

    // ── Reporting ─────────────────────────────────────────────────────────────
    static void Report(int ticks, float tick, double hours, int worldDay, double wallSec,
        int transitions, int decodes, int decodeErrors, Dictionary<string, int> byTop,
        double[] resolveMs, double[] selectMs, List<double> decodeMs, Dictionary<string, List<double>> decodeByExt,
        long allocResolve, long allocSelect, long allocDecode, long allocTotal,
        int g0, int g1, int g2, long heapBaseline, List<(double pct, long heap, double simHour)> heap)
    {
        double simMinutes = hours * 60.0;
        Console.WriteLine("══════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"SESSION  {hours:F0}h sim ({worldDay} world-days) in {wallSec:F1}s wall  ·  {ticks:N0} ticks  ·  {ticks / wallSec:N0} ticks/s");
        Console.WriteLine($"         {transitions:N0} transitions ({transitions / simMinutes:F1}/sim-min)  ·  {decodes:N0} decodes" +
                          (decodeErrors > 0 ? $"  ·  {decodeErrors} decode errors" : ""));

        Console.WriteLine("\n── Latency (per tick / per transition) ───────────────────────────────");
        PrintLatency("resolve (Lua)", resolveMs);
        PrintLatency("select (stack)", selectMs);
        var dec = decodeMs.ToArray();
        if (dec.Length > 0)
        {
            PrintLatency("decode (open) ", dec);
            foreach (var g in decodeByExt.OrderBy(g => g.Key))
            {
                var a = g.Value.ToArray(); Array.Sort(a);
                Console.WriteLine($"      {g.Key,-6} n={g.Value.Count,-4} p50 {Pct(a, 50),7:F2}ms  p95 {Pct(a, 95),7:F2}ms  max {a[a.Length - 1],7:F2}ms");
            }
        }

        Console.WriteLine("\n── Allocation & GC (the long-session pressure) ───────────────────────");
        Console.WriteLine($"   per-phase total : resolve {Mb(allocResolve),8:F1} MB   select {Mb(allocSelect),8:F1} MB   decode {Mb(allocDecode),8:F1} MB");
        Console.WriteLine($"   per tick avg    : resolve {allocResolve / (double)ticks,8:F0} B    select {allocSelect / (double)ticks,8:F0} B");
        Console.WriteLine($"   alloc rate      : {Mb(allocTotal) / simMinutes:F2} MB / sim-minute   ({Mb(allocTotal):F0} MB total managed alloc)");
        Console.WriteLine($"   GC collections  : gen0 {g0}   gen1 {g1}   gen2 {g2}   ({g0 / simMinutes:F1} gen0/sim-min)");

        Console.WriteLine("\n── Managed heap drift (forced GC — leak check) ───────────────────────");
        Console.WriteLine($"   baseline (post-warmup): {Mb(heapBaseline):F2} MB");
        foreach (var (pct, h, simHour) in heap)
            Console.WriteLine($"   {pct,5:F0}%  (sim h{simHour,5:F1}) : {Mb(h),7:F2} MB   Δ {Mb(h - heapBaseline),7:F2} MB");
        if (heap.Count >= 2)
        {
            long end = heap[heap.Count - 1].heap;
            double driftMb = Mb(end - heapBaseline);
            // Leak signal is the LATE slope, not the absolute drift: a reused buffer
            // (e.g. MeasureScratch) legitimately grows once to a high-water mark and
            // then plateaus. A genuine leak keeps climbing in the final quarter.
            double lateSlopeMb = Mb(end - heap[heap.Count - 2].heap);
            string verdict = lateSlopeMb > 0.5 ? "STILL CLIMBING in the final quarter — leak suspect, investigate"
                           : driftMb < 1.0 ? "FLAT — no leak"
                           : "grew once then PLATEAUED (reused buffers / one-time caches) — no ongoing leak";
            Console.WriteLine($"   total Δ {(driftMb >= 0 ? "+" : "")}{driftMb:F2} MB over {hours:F0}h   ·   final-quarter slope {(lateSlopeMb >= 0 ? "+" : "")}{lateSlopeMb:F2} MB");
            Console.WriteLine($"   → {verdict}");
        }

        Console.WriteLine("\n── Top transition targets ────────────────────────────────────────────");
        foreach (var kv in byTop.OrderByDescending(k => k.Value).Take(12))
            Console.WriteLine($"   {kv.Value,6:N0}  {kv.Key}");
    }

    static void PrintLatency(string label, double[] samples)
    {
        if (samples.Length == 0) { Console.WriteLine($"   {label}: (none)"); return; }
        var s = (double[])samples.Clone(); Array.Sort(s);
        Console.WriteLine($"   {label}: p50 {Pct(s, 50),7:F3}ms  p95 {Pct(s, 95),7:F3}ms  p99 {Pct(s, 99),7:F3}ms  max {s[s.Length - 1],7:F3}ms  mean {s.Average(),7:F3}ms");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    static double Ms(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
    static double Mb(long bytes) => bytes / 1024.0 / 1024.0;

    static double Pct(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        int idx = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Max(0, Math.Min(sorted.Length - 1, idx))];
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ContextMusicManager.csproj"))) return dir.FullName;
        return null;
    }

    static void Usage() => Console.WriteLine(
        "CmmSession — synthetic long-gameplay-session benchmark\n\n" +
        "  dotnet run --project tools/CmmSession -c Release -- [music-dir] [options]\n\n" +
        "  --hours <h>        simulated game-world hours (default 24)\n" +
        "  --tick <s>         scan cadence seconds (default 0.5)\n" +
        "  --day-minutes <m>  real minutes per in-game day (default 60)\n" +
        "  --seed <n>         timeline RNG seed (default 1)\n" +
        "  --no-decode        resolve+select+GC only (skip per-transition decode)\n" +
        "  --sweep            EBU-R128 measure each track once on first play\n" +
        "  --no-dms           Dynamic Music disabled\n" +
        "  -v                 verbose\n");
}
