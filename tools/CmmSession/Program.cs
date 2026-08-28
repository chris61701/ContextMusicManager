using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// CmmSession — synthetic long-gameplay-session STRESS BENCHMARK (Arena C).
//
// Drives a deterministic, multi-day synthetic session through the REAL addon code —
// linked ScriptHost (context.lua via MoonSharp) + the ported selection layer
// (Engine.cs) + real per-transition native decode (AudioDecoder.OpenNativeHandle) +
// the REAL native mixer (CmmMixer / cmm_mix_*, driven headless) — to surface slow
// accumulation (managed-heap AND native-RSS leaks) and performance regressions that
// only hours of play reveal. The per-tick drive lives in the shared SessionRunner
// (SessionRunner.cs), which the top-down viewer (tools/CmmSessionView) also uses; this
// file parses the CLI, drives the runner, and accumulates + prints the report:
//   • Memory: per-phase managed allocation (resolve / select / decode), GC counts,
//     managed-heap drift AND process working-set (RSS) drift — the latter catches
//     NATIVE leaks in the mixer/decoders that never touch the GC heap.
//   • Latency: p50/p95/p99/max for per-tick resolve + select, and per-transition load.
//   • Playback: which tracks played and for how long, load-time distribution, and
//     which plays went out un-normalized (missing loudness).
//   • Robustness: context.lua + config hot-reloaded between VALID and INVALID states
//     on a fixed sim-cadence, verifying the embedded-default fallback holds.
//   • MP shared combat: an oscillating in-range ally whose fight bleeds into ours.
//
//   dotnet run --project tools/CmmSession -c Release -- [music-dir] [options]
//
// Options (see Usage / the run script tools/CmmSession/run-stress.ps1):
//   --hours <h>        simulated session length in game-world hours (default 24)
//   --tick <s>         scan cadence seconds (default 0.5 = ContextScanCooldown)
//   --day-minutes <m>  real minutes per in-game day (default 60)
//   --seed <n>         RNG seed for the timeline (default 1; deterministic)
//   --mixer <mode>     off | headless | realtime   (default headless)
//   --reload-min <m>   sim-minutes between context.lua/config valid↔invalid flips (0=off, default 15)
//   --no-decode        skip per-transition decode (resolve+select+GC only)
//   --sweep            also EBU-R128 measure each track once on first play
//   --no-dms           Dynamic Music disabled
//   --no-loudness      skip the loudness-normalization model
//   --no-lookahead     disable the look-ahead pre-measure
//   --loud-slow <f>    x f the modeled measure time (simulate a CPU-starved worker)
//   -v                 verbose
// ─────────────────────────────────────────────────────────────────────────────

internal static class Program
{
    // Memory-drift sample count. Enough points that the back-half slope is a fit, not a guess.
    const int Checkpoints = 16;

    static int Main(string[] args)
    {
        AppDomain.MonitoringIsEnabled = true;   // precise per-AppDomain allocation tracking

        var opt = new SessionOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hours": opt.Hours = double.Parse(args[++i]); break;
                case "--tick": opt.Tick = float.Parse(args[++i]); break;
                case "--day-minutes": opt.DayMinutes = float.Parse(args[++i]); break;
                case "--seed": opt.Seed = int.Parse(args[++i]); break;
                case "--mixer": opt.MixerMode = ParseMixer(args[++i]); break;
                case "--reload-min": opt.ReloadMin = double.Parse(args[++i]); break;
                case "--no-decode": opt.Decode = false; break;
                case "--sweep": opt.Sweep = true; break;
                case "--no-dms": opt.DmsEnabled = false; break;
                case "--no-loudness": opt.NoLoudness = true; break;
                case "--no-lookahead": opt.Lookahead = false; break;
                case "--loud-slow": opt.LoudSlow = double.Parse(args[++i]); break;
                case "--no-emitters": opt.Emitters = false; break;
                case "--emitters-per-poi": opt.EmittersPerPoi = int.Parse(args[++i]); break;
                case "--emitter-hz": opt.EmitterHz = float.Parse(args[++i]); break;
                case "-v": opt.Verbose = true; break;
                case "-h": case "--help": Usage(); return 0;
                default: opt.MusicDir ??= args[i]; break;
            }
        }
        if (opt.MusicDir == null) opt.MusicDir = @"C:\_Games\CMM_TestMusic";

        // ── Build the session (setup + warmup happen in the ctor, before the baseline below) ──
        var runner = new SessionRunner(opt);
        if (!runner.ScriptActive)
        {
            Console.WriteLine("FATAL: no resolver loaded (MoonSharp/context.lua failed). Aborting.");
            return 1;
        }

        int totalTicks = runner.TotalTicks;
        float tick = opt.Tick;
        double hours = opt.Hours;
        var mixerMode = runner.MixerMode;

        Console.WriteLine($"CmmSession — {hours:F0}h session, tick {tick:F2}s ({totalTicks:N0} ticks), seed {opt.Seed}, " +
                          $"day={opt.DayMinutes:F0}min, DMS={(ContextMusicManager.DmsEnabled ? "on" : "off")}");
        Console.WriteLine($"  script   : {(ScriptHost.UsingEmbeddedDefault ? "embedded default" : "Config/context.lua")}" +
                          $"{(runner.Reload != null ? $"  (reload flip every {opt.ReloadMin:F0} sim-min, valid↔invalid)" : "")}");
        Console.WriteLine($"  library  : {opt.MusicDir} ({(runner.HaveLib ? $"{ContextMusicManager.IndexedContexts} contexts / {ContextMusicManager.IndexedFiles} files" : "MISSING — resolve+GC only")})");
        Console.WriteLine($"  mixer    : {mixerMode.ToString().ToLower()}{(mixerMode == SessionMixerMode.Realtime ? " (real device, muted, real-time)" : mixerMode == SessionMixerMode.Headless ? " (null device, drained on sim clock)" : "")}");
        Console.WriteLine($"  decode   : {(opt.Decode && runner.HaveLib ? (opt.Sweep ? "per-transition native open + first-play R128 sweep" : "per-transition native open") : "off")}");
        Console.WriteLine($"  emitters : {(runner.Emitter != null ? $"on (≤{opt.EmittersPerPoi}/POI, {opt.EmitterHz:F0}Hz → {runner.Emitter.RunsPerTick}×/tick, occlusion+reverb)" : "off")}\n");

        // ── Preallocate sample buffers BEFORE the heap baseline so they don't skew drift ──
        // (decode telemetry + the loudness model live in the runner, allocated in its ctor above.)
        var resolveMs = new double[totalTicks];
        var selectMs = new double[totalTicks];
        var transitionsByTop = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Crossfade-pressure tracking.
        var gaps = new List<double>(totalTicks / 16 + 16);
        var transitionTimes = new List<float>(totalTicks / 16 + 16);
        int jarring = 0;
        float prevTransTime = -1f, prevFadeWindow = 0f;

        // Per-track PLAY accounting: what played, and for how long (sim-seconds between the
        // transition that started it and the one that replaced it). Load time is tracked per
        // format (runner.DecodeByExt) + overall (runner.DecodeMs).
        var playStats = new Dictionary<string, PlayStat>(StringComparer.OrdinalIgnoreCase);
        string curFile = null; double curFileStart = 0;

        // ── Loudness report tally (the timing-aware model itself lives in the runner) ──
        long playsTotal = 0, playsNormalized = 0;
        int unnormCold = 0, unnormFailed = 0;
        var unnormByExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unnormReasons = new Dictionary<string, int>();
        var unnormSamples = new List<string>();

        // ── Multiplayer proximity tally ──
        long mpTicksAllies = 0, mpTicksStrangers = 0, mpTicksAny = 0, mpTicksInCombat = 0, mpTicksOutCombat = 0;
        int mpEnters = 0, mpMaxAllies = 0, mpMaxStrangers = 0;
        bool mpPrevAny = false;

        var proc = Process.GetCurrentProcess();
        long allocResolve = 0, allocSelect = 0, allocDecode = 0;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long heapBaseline = GC.GetTotalMemory(true);
        NativeOpus.TryGetNativeMemory(out long natBaseline, out _, out _, out _, out _);
        long rssBaseline = WorkingSet(proc);
        var heapCheckpoints = new List<(double pct, long heap, long rss, long nat, double simHour)>();
        int transitions = 0;
        long allocStart = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        var wall = Stopwatch.StartNew();

        // Mixer output-health accumulation (meaningful in realtime mode; in headless the pump
        // outpaces the producer so "underruns" are an artifact — reported labelled per mode).
        int mixUnderruns = 0, mixMaxVoices = 0, mixMinQueuedMs = int.MaxValue;

        for (int t = 0; t < totalTicks; t++)
        {
            // ── Advance one tick through the real decision + decode + mixer path ──
            TickOutcome o = runner.Tick();
            GameState gs = o.State;

            resolveMs[t] = o.ResolveMs;
            selectMs[t] = o.SelectMs;
            allocResolve += o.AllocResolve;
            allocSelect += o.AllocSelect;
            allocDecode += o.AllocDecode;

            // ── Multiplayer proximity tally ──
            int allies = gs.AlliesNearby, strangers = gs.StrangersNearby;
            bool anyPlayer = allies + strangers > 0;
            bool inCombat = gs.IsPanicked;
            if (allies > 0) mpTicksAllies++;
            if (strangers > 0) mpTicksStrangers++;
            if (anyPlayer)
            {
                mpTicksAny++;
                if (inCombat) mpTicksInCombat++; else mpTicksOutCombat++;
                if (!mpPrevAny) mpEnters++;
            }
            if (allies > mpMaxAllies) mpMaxAllies = allies;
            if (strangers > mpMaxStrangers) mpMaxStrangers = strangers;
            mpPrevAny = anyPlayer;

            // ── Transition bookkeeping + play/loudness accounting on a real transition ──
            if (o.Transitioned)
            {
                transitions++;
                transitionsByTop[o.Top] = transitionsByTop.TryGetValue(o.Top, out int c) ? c + 1 : 1;

                float nowT = SimClock.Now;
                if (prevTransTime >= 0f)
                {
                    double gap = nowT - prevTransTime;
                    gaps.Add(gap);
                    if (gap < prevFadeWindow) jarring++;
                }
                transitionTimes.Add(nowT);
                prevTransTime = nowT;
                var topCtx = ContextMusicManager.GetContext(o.Top);
                prevFadeWindow = Math.Max(ContextMusicManager.CrossFadeDuration, topCtx?.FadeInDuration ?? 0);

                if (o.PlayedFile != null)
                {
                    // Close out the previously-playing track's duration (sim-seconds it was up).
                    AccountPlay(playStats, ref curFile, ref curFileStart, o.PlayedFile, nowT);

                    // ── Loudness: was this track normalized when Play fired? (decision made in the runner) ──
                    if (runner.LoudnessEnabled)
                    {
                        playsTotal++;
                        if (o.Normalized) playsNormalized++;
                        else
                        {
                            string reason = o.UnnormReason;
                            string uext = Path.GetExtension(o.PlayedFile).ToLowerInvariant();
                            unnormByExt[uext] = unnormByExt.TryGetValue(uext, out int uc) ? uc + 1 : 1;
                            unnormReasons[reason] = unnormReasons.TryGetValue(reason, out int rc) ? rc + 1 : 1;
                            if (reason.StartsWith("measurement FAILED")) unnormFailed++; else unnormCold++;
                            if (unnormSamples.Count < 25)
                                unnormSamples.Add($"h{SimClock.Now / 3600.0,5:F1}  {o.Top,-24} {Path.GetFileName(o.PlayedFile),-32} {reason}");
                        }
                    }
                }
            }

            // ── Drive the realtime mixer pacing + output-health sampling (headless drains inside Tick) ──
            if (mixerMode == SessionMixerMode.Realtime)
            {
                // Pace the sim to the wall clock so Play cadence matches real playback, and
                // sample the real device's buffer health (underruns here are meaningful).
                runner.Mixer.GetOutputStats(out _, out _, out int minQ, out int voices, out _, out int under, out int busRate);
                if (under > mixUnderruns) mixUnderruns = under;
                if (voices > mixMaxVoices) mixMaxVoices = voices;
                if (voices > 0 && busRate > 0) { int mq = minQ * 1000 / busRate; if (mq < mixMinQueuedMs) mixMinQueuedMs = mq; }
                double targetMs = SimClock.Now * 1000.0;
                double behind = targetMs - wall.Elapsed.TotalMilliseconds;
                if (behind > 1) System.Threading.Thread.Sleep((int)behind);
            }

            // Heap + RSS checkpoints (forced GC → managed-heap leak signal; WorkingSet drift →
            // NATIVE leak signal, e.g. the mixer/decoders).
            //
            // Sampled at 1/CHECKPOINTS intervals, not the original four. Four points cannot tell a
            // rising line from a noisy flat one, and RSS is noisy by nature (Windows trims a working
            // set on its own schedule): the same seed run twice gave 28.6/39.9/62.3/66.4 and
            // 28.6/37.4/70.8/66.6 — monotone in one, peaking mid-run in the other, same work. With
            // enough points the drift report can fit a SLOPE over the back half instead of reading
            // one difference and guessing.
            if (t == totalTicks - 1 || (t > 0 && t % Math.Max(1, totalTicks / Checkpoints) == 0))
            {
                if (mixerMode == SessionMixerMode.Headless && runner.Mixer != null)
                {
                    runner.Mixer.GetOutputStats(out _, out _, out _, out int voices, out _, out int under, out _);
                    if (voices > mixMaxVoices) mixMaxVoices = voices;
                    mixUnderruns = under;   // cumulative (artifact in headless — see report)
                }
                proc.Refresh();
                NativeOpus.TryGetNativeMemory(out long natLive, out _, out _, out _, out _);
                heapCheckpoints.Add(((t + 1) * 100.0 / totalTicks, GC.GetTotalMemory(true), WorkingSet(proc), natLive, SimClock.Now / 3600.0));
            }
        }

        // Final track's play duration.
        AccountPlay(playStats, ref curFile, ref curFileStart, null, SimClock.Now);
        // Native-heap QUIESCENT reading. The per-checkpoint `live` figure is dominated by
        // in-flight decoder handles -- one FLAC handle peaks near 9.6 MB -- so whether the sample
        // lands with 0, 1 or 2 voices open swings it by more than any plausible leak, and its
        // back-half slope wanders the same way RSS does (+0.23 / +2.29 / -0.11 / -1.94 MB/sim-h
        // over four seeds of identical code). With every voice stopped and every handle closed
        // there is nothing legitimate left to hold, so THIS is the number that convicts: whatever
        // is still allocated here was leaked.
        NativeOpus.TryGetNativeMemory(out long natPre, out _, out long natAllocsPre, out long natFreesPre, out _);
        runner.Stop();
        System.Threading.Thread.Sleep(250);   // let the producer thread finish releasing
        NativeOpus.TryGetNativeMemory(out long natFinal, out long natPeak, out long natAllocs, out long natFrees, out long natChurn);
        wall.Stop();

        long allocTotal = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocStart;
        Report(totalTicks, tick, hours, runner.Generator.WorldDay, wall.Elapsed.TotalSeconds,
               transitions, runner.DecodeCount, runner.DecodeErrors, transitionsByTop,
               resolveMs, selectMs, runner.DecodeMs, runner.DecodeByExt,
               allocResolve, allocSelect, allocDecode, allocTotal,
               GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2,
               heapBaseline, rssBaseline, natBaseline, natFinal, natPeak, natAllocs, natFrees, natChurn, heapCheckpoints);

        PrintLoadHistogram(runner.DecodeMs, runner.DecodeErrByExt);
        PrintPlayAccounting(playStats, hours);
        PrintCrossfadePressure(gaps, jarring, transitionTimes);
        PrintMixer(mixerMode, mixUnderruns, mixMaxVoices, mixMinQueuedMs);
        PrintSharedCombat(runner.Generator, totalTicks);
        PrintMultiplayer(mpTicksAny, mpTicksAllies, mpTicksStrangers, mpTicksInCombat, mpTicksOutCombat,
                         mpEnters, mpMaxAllies, mpMaxStrangers, totalTicks, transitionsByTop);
        PrintReload(runner.Reload);
        PrintLoudness(runner.LoudnessEnabled, runner.ReadyCount, runner.FailedCount, playsTotal, playsNormalized,
                      unnormCold, unnormFailed, unnormReasons, unnormByExt, unnormSamples);
        PrintEmitters(runner.Emitter, totalTicks, tick, resolveMs, selectMs);
        return 0;
    }

    // ── Per-track play accounting ───────────────────────────────────────────────
    struct PlayStat { public int Plays; public double SimSeconds; }

    static void AccountPlay(Dictionary<string, PlayStat> stats, ref string curFile, ref double curStart,
                            string newFile, double now)
    {
        if (curFile != null)
        {
            var s = stats.TryGetValue(curFile, out var e) ? e : default;
            s.SimSeconds += Math.Max(0, now - curStart);
            stats[curFile] = s;
        }
        if (newFile != null)
        {
            var s = stats.TryGetValue(newFile, out var e) ? e : default;
            s.Plays++;
            stats[newFile] = s;
            curFile = newFile;
            curStart = now;
        }
        else curFile = null;
    }

    static void PrintPlayAccounting(Dictionary<string, PlayStat> stats, double hours)
    {
        Console.WriteLine("\n── Track playback (what played, and for how long) ────────────────────");
        if (stats.Count == 0) { Console.WriteLine("   (nothing played — no library / --no-decode)"); return; }
        double totalSecs = stats.Values.Sum(s => s.SimSeconds);
        int totalPlays = stats.Values.Sum(s => s.Plays);
        Console.WriteLine($"   distinct tracks played: {stats.Count:N0}   ·   {totalPlays:N0} plays   ·   {totalSecs / 3600.0:F1} sim-hours of music");
        Console.WriteLine("   top 15 by total sim-time on air (file  plays  total  avg/play):");
        foreach (var kv in stats.OrderByDescending(k => k.Value.SimSeconds).Take(15))
            Console.WriteLine($"     {Path.GetFileName(kv.Key),-40} {kv.Value.Plays,4}×  {FmtDur(kv.Value.SimSeconds),8}  {FmtDur(kv.Value.Plays > 0 ? kv.Value.SimSeconds / kv.Value.Plays : 0),8}");
    }

    // ── Load-time distribution (ASCII histogram) ────────────────────────────────
    static void PrintLoadHistogram(IReadOnlyList<double> loadMs, IReadOnlyDictionary<string, int> errByExt)
    {
        Console.WriteLine("\n── Load-time distribution (native decoder open, per transition) ──────");
        if (errByExt.Count > 0)
            Console.WriteLine("   DECODE ERRORS by format: " + string.Join("   ", errByExt.OrderByDescending(k => k.Value).Select(k => $"{k.Key} ×{k.Value}")));
        if (loadMs.Count == 0) { Console.WriteLine("   (no decodes)"); return; }
        var a = loadMs.ToArray(); Array.Sort(a);
        double avg = a.Average();
        Console.WriteLine($"   n={a.Length}  avg {avg:F2}ms  p50 {Pct(a, 50):F2}ms  p95 {Pct(a, 95):F2}ms  p99 {Pct(a, 99):F2}ms  max {a[a.Length - 1]:F2}ms");
        double[] edges = { 1, 2, 5, 10, 20, 50, 100, double.PositiveInfinity };
        string[] labels = { "  <1ms", " 1-2ms", " 2-5ms", "5-10ms", "10-20", "20-50", "50-100", " >100" };
        int[] buckets = new int[edges.Length];
        foreach (double v in a) { for (int i = 0; i < edges.Length; i++) if (v < edges[i]) { buckets[i]++; break; } }
        int max = buckets.Max();
        for (int i = 0; i < buckets.Length; i++)
        {
            int bars = max > 0 ? (int)Math.Round(40.0 * buckets[i] / max) : 0;
            Console.WriteLine($"   {labels[i]} | {new string('█', bars)} {buckets[i]}");
        }
    }

    // ── Mixer output health ─────────────────────────────────────────────────────
    static void PrintMixer(SessionMixerMode mode, int underruns, int maxVoices, int minQueuedMs)
    {
        Console.WriteLine("\n── Native mixer (cmm_mix_*) output health ────────────────────────────");
        if (mode == SessionMixerMode.Off) { Console.WriteLine("   (mixer off — resolve/select/decode only)"); return; }
        Console.WriteLine($"   mode: {mode.ToString().ToLower()}   ·   peak concurrent voices: {maxVoices}");
        if (mode == SessionMixerMode.Realtime)
        {
            Console.WriteLine($"   min queued depth: {(minQueuedMs == int.MaxValue ? "n/a" : minQueuedMs + "ms")}   ·   underruns: {underruns}" +
                              (underruns > 0 ? "  ← ring ran dry under load (investigate)" : "  ← never starved"));
        }
        else
        {
            Console.WriteLine($"   (headless: the sim-clock pump intentionally outpaces the producer, so ring-empty");
            Console.WriteLine($"    counts are an artifact — underrun/starvation validation is the realtime mode's job.");
            Console.WriteLine($"    This run exercised the Play→crossfade→retire→free lifecycle for leak/RSS tracking.)");
        }
    }

    // ── Shared-combat (squad) summary ───────────────────────────────────────────
    static void PrintSharedCombat(SessionGenerator gen, int totalTicks)
    {
        Console.WriteLine("\n── Squad shared-combat (oscillating in-range ally) ───────────────────");
        if (gen.SharedCombatTicks == 0)
        {
            Console.WriteLine("   (the shared-combat ally never triggered — no horde nights or flagged POIs in this run)");
            return;
        }
        Console.WriteLine($"   shared-combat live: {gen.SharedCombatTicks:N0} ticks ({100.0 * gen.SharedCombatTicks / totalTicks:F1}% of session)" +
                          $"  ·  {gen.SharedCombatEnters:N0} enter events (ally crossed into share range while fighting)");
        Console.WriteLine("   → the ally's zombies bled into our snapshot as targeting enemies, driving Combat while");
        Console.WriteLine("     nothing was on us directly (the MP squad-shared-combat path, oscillated in/out of range).");
    }

    // ── Reload robustness ───────────────────────────────────────────────────────
    static void PrintReload(ReloadHarness reload)
    {
        Console.WriteLine("\n── Hot-reload robustness (context.lua valid ↔ invalid) ───────────────");
        if (reload == null) { Console.WriteLine("   (reload virtualization off)"); return; }
        Console.WriteLine($"   flips: {reload.Flips:N0}   →   {reload.ToInvalid:N0} to INVALID (embedded fallback expected), {reload.ToValid:N0} back to VALID");
        Console.WriteLine($"   embedded-fallback engagements observed: {reload.EmbeddedEngagements:N0}   ·   config re-marshals: {reload.ConfigBumps:N0}");
        Console.WriteLine($"   final resolver: {(ScriptHost.UsingEmbeddedDefault ? "embedded default" : "on-disk context.lua")}   ·   resolver still active: {ScriptHost.Active}");
        if (reload.ToInvalid > 0 && reload.EmbeddedEngagements == 0)
            Console.WriteLine("   ⚠ never fell back to embedded despite invalid flips — the fallback path may not be exercising.");
        else
            Console.WriteLine("   → invalid scripts were rejected and the embedded baseline held; valid scripts reloaded cleanly.");
    }

    static void PrintMultiplayer(long any, long allies, long strangers, long inCombat, long outCombat,
        int enters, int maxA, int maxS, int totalTicks, Dictionary<string, int> byTop)
    {
        Console.WriteLine("\n── Multiplayer proximity (simulated nearby players) ──────────────────");
        if (any == 0) { Console.WriteLine("   (no nearby players were simulated)"); return; }
        double pct(long n) => 100.0 * n / totalTicks;
        Console.WriteLine($"   ticks with a player nearby : {any:N0} ({pct(any):F1}%)  ·  {enters:N0} separate arrivals");
        Console.WriteLine($"   allies nearby   : {allies:N0} ticks ({pct(allies):F1}%)  max {maxA} at once");
        Console.WriteLine($"   strangers nearby: {strangers:N0} ticks ({pct(strangers):F1}%)  max {maxS} at once");
        Console.WriteLine($"   while nearby    : {inCombat:N0} ticks IN combat ({pct(inCombat):F1}%)  ·  {outCombat:N0} OUT ({pct(outCombat):F1}%)");
        int os = byTop.TryGetValue("Online_Stranger", out int a) ? a : 0;
        int of = byTop.TryGetValue("Online_Friend", out int b) ? b : 0;
        Console.WriteLine($"   music fired     : Online_Stranger ×{os}   Online_Friend ×{of}");
    }

    static void PrintLoudness(bool enabled, int readyCount, int failedCount,
        long playsTotal, long playsNormalized, int unnormCold, int unnormFailed,
        Dictionary<string, int> reasons, Dictionary<string, int> byExt, List<string> samples)
    {
        Console.WriteLine("\n── Loudness normalization (missing / un-normalized playback) ─────────");
        if (!enabled) { Console.WriteLine("   (normalization model off)"); return; }
        long unnorm = playsTotal - playsNormalized;
        Console.WriteLine($"   measured    : {readyCount + failedCount:N0} tracks ({failedCount} FAILED)  ·  prewarm + single worker + look-ahead");
        Console.WriteLine($"   track plays : {playsTotal:N0}   normalized {playsNormalized:N0} ({(playsTotal > 0 ? 100.0 * playsNormalized / playsTotal : 0):F1}%)   MISSING {unnorm:N0} ({(playsTotal > 0 ? 100.0 * unnorm / playsTotal : 0):F1}%)");
        Console.WriteLine($"   split       : {unnormCold:N0} cold (normalized on later plays)  ·  {unnormFailed:N0} measurement FAILED (unity gain forever)");
        foreach (var kv in reasons.OrderByDescending(k => k.Value))
            Console.WriteLine($"      {kv.Value,6:N0}  {kv.Key}");
        if (byExt.Count > 0)
            Console.WriteLine("   by format   : " + string.Join("   ", byExt.OrderByDescending(k => k.Value).Select(k => $"{k.Key} ×{k.Value}")));
        if (samples.Count > 0)
        {
            Console.WriteLine($"   first {samples.Count} missing (simhour  context  file  reason):");
            foreach (var s in samples) Console.WriteLine("     " + s);
        }
        else Console.WriteLine("   → every track was normalized at Play time (prewarm + look-ahead kept up; no failures).");
    }

    // ── World-emitter (Radio) main-thread cost ──────────────────────────────────
    static void PrintEmitters(EmitterSim em, int totalTicks, float tick, double[] resolveMs, double[] selectMs)
    {
        Console.WriteLine("\n── World-emitter acoustics (Radio speakers in POIs) ──────────────────");
        if (em == null) { Console.WriteLine("   (emitters off — run without --no-emitters to measure)"); return; }

        double pctInPoi = 100.0 * em.TicksInPoi / Math.Max(1, totalTicks);
        Console.WriteLine($"   POI visits with speakers: {em.PoiVisits:N0}   ·   in-POI ticks: {em.TicksInPoi:N0} ({pctInPoi:F1}% of session)");
        double avgPts = em.RunsTotal > 0 ? (double)em.PointCountSum / em.RunsTotal : 0;
        int maxPts = 0; for (int i = em.PointCountHist.Length - 1; i >= 0; i--) if (em.PointCountHist[i] > 0) { maxPts = i; break; }
        Console.WriteLine($"   acoustics runs: {em.RunsTotal:N0} ({em.RunsPerTick}×/in-POI-tick)   ·   speakers in range/run: avg {avgPts:F1}, max {maxPts}");

        // Appearance (registry POI block-scan when speakers stream in).
        if (em.ScanMs.Count > 0)
        {
            var s = em.ScanMs.Sorted();
            Console.WriteLine($"   appearance scan (POI block sweep): n={em.ScanMs.Count:N0}  p50 {Pct(s, 50):F3}ms  p95 {Pct(s, 95):F3}ms  max {em.ScanMs.Max:F3}ms");
        }

        // Steady per-run spatialisation cost — the per-frame main-thread hit while in range.
        if (em.RunMs.Count > 0)
        {
            var r = em.RunMs.Sorted();
            var occ = em.OccMs.Sorted();
            var room = em.RoomMs.Sorted();
            // n is the retained SAMPLE when the reservoir capped; Count/Sum/Max stay exact.
            string sn = em.RunMs.Sampled ? $" (percentiles from a {r.Length:N0}-sample reservoir of {em.RunMs.Count:N0} runs)" : "";
            Console.WriteLine($"   per run (occ+reverb): p50 {Pct(r, 50):F4}ms  p95 {Pct(r, 95):F4}ms  p99 {Pct(r, 99):F4}ms  max {em.RunMs.Max:F4}ms  mean {em.RunMs.Average:F4}ms{sn}");
            if (em.RunMs.Max > 20 * Pct(r, 99))
                Console.WriteLine($"      (max ≫ p99 ⇒ a lone GC pause landed inside one timed span — p99 is the steady per-run cost)");
            Console.WriteLine($"      occlusion : p50 {Pct(occ, 50):F4}ms  p95 {Pct(occ, 95):F4}ms  max {em.OccMs.Max:F4}ms");
            Console.WriteLine($"      room probe: p50 {Pct(room, 50):F4}ms  p95 {Pct(room, 95):F4}ms  max {em.RoomMs.Max:F4}ms");

            // Relative hit vs the existing per-tick decision cost (resolve + select).
            double emPerTick = em.RunMs.Average * em.RunsPerTick;
            double baseline = resolveMs.Average() + selectMs.Average();
            Console.WriteLine($"   added main-thread cost: {emPerTick:F4}ms per in-POI tick  ·  resolve+select baseline {baseline:F4}ms/tick  →  +{100.0 * emPerTick / Math.Max(1e-9, baseline):F1}%");
        }

        Console.WriteLine($"   compute allocation: {Mb(em.AllocCompute):F2} MB total over {em.RunsTotal:N0} runs  (≈one-time flood-buffer init on first run; ~0 B/run steady ⇒ no per-run GC)");

        // Scaling: how the per-run cost grows with speakers-in-range, direct-ray (shipped default,
        // FloodRadius=1) vs the heavy cost-field path (FloodRadius=16, if a user turns routing up).
        var scale = em.ScaleProbe(4000);
        Console.WriteLine("   scaling by speakers-in-range (per run, fixed POI):");
        Console.WriteLine("      speakers   direct-ray(default)   cost-field(flood=16)");
        foreach (var sc in scale)
            Console.WriteLine($"        {sc.Count,-8}   {sc.Ms,10:F4}ms          {sc.MsFlood,10:F4}ms");
        Console.WriteLine("   → decode + mix are OFF-thread (native); this is the whole main-thread footprint.");
    }

    // ── Crossfade pressure (jarring transitions) ────────────────────────────────
    static void PrintCrossfadePressure(List<double> gaps, int jarring, List<float> times)
    {
        Console.WriteLine("\n── Crossfade pressure (jarring = interrupts an in-flight fade) ────────");
        if (gaps.Count == 0) { Console.WriteLine("   (fewer than 2 transitions — nothing to space)"); return; }
        var g = gaps.ToArray(); Array.Sort(g);
        double cf = ContextMusicManager.CrossFadeDuration;
        int under(double s) => CountUnder(g, s);
        Console.WriteLine($"   gap between transitions: min {g[0]:F2}s  p1 {Pct(g, 1):F2}s  p5 {Pct(g, 5):F2}s  p50 {Pct(g, 50):F2}s  (CrossFadeDuration {cf:F1}s)");
        Console.WriteLine($"   tight gaps: <0.5s {under(0.5)}   <1s {under(1.0)}   <2s {under(2.0)}   <{cf:F1}s(fade) {jarring}");
        Console.WriteLine($"   JARRING (gap < prior fade window): {jarring} of {gaps.Count}  ({100.0 * jarring / gaps.Count:F1}%)");
        int best = 1, lo = 0;
        for (int hi = 0; hi < times.Count; hi++)
        {
            while (times[hi] - times[lo] > cf) lo++;
            if (hi - lo + 1 > best) best = hi - lo + 1;
        }
        Console.WriteLine($"   worst burst: {best} transitions inside a single {cf:F1}s window" +
                          (best >= 3 ? "  ← rapid-interrupt stress" : ""));
    }

    static int CountUnder(double[] sorted, double s)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi) { int m = (lo + hi) / 2; if (sorted[m] < s) lo = m + 1; else hi = m; }
        return lo;
    }

    // ── Core report (latency + allocation + heap/RSS drift) ─────────────────────
    static void Report(int ticks, float tick, double hours, int worldDay, double wallSec,
        int transitions, int decodes, int decodeErrors, Dictionary<string, int> byTop,
        double[] resolveMs, double[] selectMs, IReadOnlyList<double> decodeMs, IReadOnlyDictionary<string, List<double>> decodeByExt,
        long allocResolve, long allocSelect, long allocDecode, long allocTotal,
        int g0, int g1, int g2, long heapBaseline, long rssBaseline, long natBaseline,
        long natFinal, long natPeak, long natAllocs, long natFrees, long natChurn,
        List<(double pct, long heap, long rss, long nat, double simHour)> chk)
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
            PrintLatency("load (open)   ", dec);
            foreach (var g in decodeByExt.OrderBy(g => g.Key))
            {
                var arr = g.Value.ToArray(); Array.Sort(arr);
                Console.WriteLine($"      {g.Key,-6} n={g.Value.Count,-5} p50 {Pct(arr, 50),7:F2}ms  p95 {Pct(arr, 95),7:F2}ms  max {arr[arr.Length - 1],7:F2}ms");
            }
        }

        Console.WriteLine("\n── Allocation & GC (the long-session pressure) ───────────────────────");
        Console.WriteLine($"   per-phase total : resolve {Mb(allocResolve),8:F1} MB   select {Mb(allocSelect),8:F1} MB   decode {Mb(allocDecode),8:F1} MB");
        Console.WriteLine($"   per tick avg    : resolve {allocResolve / (double)ticks,8:F0} B    select {allocSelect / (double)ticks,8:F0} B");
        Console.WriteLine($"   alloc rate      : {Mb(allocTotal) / simMinutes:F2} MB / sim-minute   ({Mb(allocTotal):F0} MB total managed alloc)");
        Console.WriteLine($"   GC collections  : gen0 {g0}   gen1 {g1}   gen2 {g2}   ({g0 / simMinutes:F1} gen0/sim-min)");

        Console.WriteLine("\n── Memory drift (managed heap · native heap · working set) ───────────");
        Console.WriteLine($"   baseline (post-warmup): heap {Mb(heapBaseline):F2} MB   ·   native {Mb(natBaseline):F2} MB   ·   RSS {Mb(rssBaseline):F1} MB");
        // 16 points are sampled; print a quarter of them so the table stays readable.
        int every = Math.Max(1, chk.Count / 4);
        for (int i = 0; i < chk.Count; i++)
        {
            if (i % every != 0 && i != chk.Count - 1) continue;
            var (pct, h, rss, nat, simHour) = chk[i];
            Console.WriteLine($"   {pct,5:F0}%  (sim h{simHour,5:F1}) : heap {Mb(h),7:F2} (Δ{Mb(h - heapBaseline),6:F2})   native {Mb(nat),7:F2} (Δ{Mb(nat - natBaseline),6:F2})   RSS {Mb(rss),7:F1} (Δ{Mb(rss - rssBaseline),6:F1})");
        }
        if (chk.Count >= 4)
        {
            var simHours = chk.Select(c => c.simHour).ToList();
            var heapD = chk.Select(c => Mb(c.heap - heapBaseline)).ToList();
            var rssD = chk.Select(c => Mb(c.rss - rssBaseline)).ToList();
            var natD = chk.Select(c => Mb(c.nat - natBaseline)).ToList();
            double heapDrift = heapD[heapD.Count - 1], rssDrift = rssD[rssD.Count - 1];
            double heapSlope = BackHalfSlope(simHours, heapD), rssSlope = BackHalfSlope(simHours, rssD);
            Console.WriteLine($"   heap: total Δ{Sign(heapDrift)} MB, back-half slope {Sign(heapSlope)} MB/sim-h  → {LeakVerdict(heapDrift, heapSlope)}");
            // The NATIVE line is the one that can actually convict: it counts real malloc/free
            // inside CMM_Decoder.dll (ABI 52) rather than inferring from the working set, which
            // on this workload swings by tens of MB for identical code.
            double natDrift = natD[natD.Count - 1];
            double natSlope = BackHalfSlope(simHours, natD);
            bool natInstrumented = natBaseline > 0 || natD.Exists(v => v != 0);
            Console.WriteLine(natInstrumented
                ? $"   native: total Δ{Sign(natDrift)} MB, back-half slope {Sign(natSlope)} MB/sim-h  → {LeakVerdict(natDrift, natSlope)}"
                : "   native: not instrumented (CMM_Decoder.dll predates ABI 52 — rebuild it to measure the native heap)");
            Console.WriteLine($"   RSS : total Δ{Sign(rssDrift)} MB, back-half slope {Sign(rssSlope)} MB/sim-h  → {LeakVerdict(rssDrift, rssSlope)}");
            Console.WriteLine($"   ({chk.Count} samples; the slope is a least-squares fit over the back half — one difference cannot separate a rise from RSS jitter)");

            if (natInstrumented)
            {
                Console.WriteLine($"   native QUIESCENT (all voices stopped, all handles closed): {Mb(natFinal):F2} MB held, "
                                + $"{natAllocs - natFrees:N0} block(s) outstanding");
                // A single run CANNOT tell a leak from a fixture, so do not claim it does. A leak
                // grows with the work done; one-time state does not. Measured here: 14.10 MB in 12
                // blocks after teardown, IDENTICAL at 3 h and 48 h (16x the work, 670k+ allocations,
                // 9 GB churned) and identical across four seeds -- that is the mixer/output holding
                // its persistent buffers until a full close, not a leak. Report the residue and the
                // discriminator; let two runs settle it.
                double held = Mb(natFinal - natBaseline);
                Console.WriteLine($"      → {Sign(held)} MB vs baseline; peak {Mb(natPeak):F1} MB, "
                                + $"{natChurn / 1048576.0:N0} MB churned through {natAllocs:N0} allocations");
                Console.WriteLine("      → a LEAK grows with session length: re-run at a different --hours and compare");
                Console.WriteLine("        this line. Identical residue = one-time native state, not a leak.");            }
        }

        Console.WriteLine("\n── Top transition targets ────────────────────────────────────────────");
        foreach (var kv in byTop.OrderByDescending(k => k.Value).Take(12))
            Console.WriteLine($"   {kv.Value,6:N0}  {kv.Key}");
    }

    // Least-squares slope of `y` against `x` over the BACK HALF of the series, in units of
    // y-per-x (MB per sim-hour). A leak is a persistent rate, so a rate is what should be
    // reported; the previous rule read the single last-quarter difference, which cannot tell a
    // rising line from a noisy flat one. RSS is noisy by nature — Windows trims a working set on
    // its own schedule — and the same seed run twice gave 28.6/39.9/62.3/66.4 and
    // 28.6/37.4/70.8/66.6 MB above baseline for identical work. Fitting the back half over 16
    // samples answers what one difference over 4 could only guess at.
    static double BackHalfSlope(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        int n = x.Count, from = n / 2, m = n - from;
        if (m < 2) return 0;
        double sx = 0, sy = 0, sxy = 0, sxx = 0;
        for (int i = from; i < n; i++) { sx += x[i]; sy += y[i]; sxy += x[i] * y[i]; sxx += x[i] * x[i]; }
        double den = m * sxx - sx * sx;
        return Math.Abs(den) < 1e-9 ? 0 : (m * sxy - sx * sy) / den;
    }

    // A leak is a RATE that keeps paying out, not a one-off. Growth that stops is a cache.
    static string LeakVerdict(double drift, double slopePerHour) =>
        slopePerHour > 0.25 && drift > 1.0 ? "STILL CLIMBING — leak suspect, investigate"
        : drift < 1.0 ? "FLAT — no leak"
        : "grew once then PLATEAUED (buffers/caches) — no ongoing leak";

    static void PrintLatency(string label, IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) { Console.WriteLine($"   {label}: (none)"); return; }
        var s = samples.ToArray(); Array.Sort(s);
        Console.WriteLine($"   {label}: p50 {Pct(s, 50),7:F3}ms  p95 {Pct(s, 95),7:F3}ms  p99 {Pct(s, 99),7:F3}ms  max {s[s.Length - 1],7:F3}ms  mean {s.Average(),7:F3}ms");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    static SessionMixerMode ParseMixer(string s)
    {
        switch (s.ToLowerInvariant())
        {
            case "off": return SessionMixerMode.Off;
            case "headless": return SessionMixerMode.Headless;
            case "realtime": case "real": return SessionMixerMode.Realtime;
            default: Console.WriteLine($"(unknown --mixer '{s}', using headless)"); return SessionMixerMode.Headless;
        }
    }

    static long WorkingSet(Process p) { p.Refresh(); return p.WorkingSet64; }

    static double Mb(long bytes) => bytes / 1024.0 / 1024.0;
    static string Sign(double v) => (v >= 0 ? "+" : "") + v.ToString("F2");

    static string FmtDur(double secs)
    {
        if (secs < 60) return $"{secs:F0}s";
        if (secs < 3600) return $"{secs / 60.0:F1}m";
        return $"{secs / 3600.0:F1}h";
    }

    static double Pct(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        int idx = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Max(0, Math.Min(sorted.Length - 1, idx))];
    }

    static void Usage() => Console.WriteLine(
        "CmmSession — synthetic long-gameplay-session stress benchmark\n\n" +
        "  dotnet run --project tools/CmmSession -c Release -- [music-dir] [options]\n\n" +
        "  --hours <h>        simulated game-world hours (default 24)\n" +
        "  --tick <s>         scan cadence seconds (default 0.5)\n" +
        "  --day-minutes <m>  real minutes per in-game day (default 60)\n" +
        "  --seed <n>         timeline RNG seed (default 1)\n" +
        "  --mixer <mode>     off | headless | realtime (default headless)\n" +
        "  --reload-min <m>   sim-minutes between context.lua/config valid↔invalid flips (0=off, default 15)\n" +
        "  --no-decode        resolve+select+GC only (skip decode + mixer)\n" +
        "  --sweep            EBU-R128 measure each track once on first play\n" +
        "  --no-dms           Dynamic Music disabled\n" +
        "  --no-loudness      skip the loudness-normalization model\n" +
        "  --no-lookahead     disable the look-ahead pre-measure\n" +
        "  --loud-slow <f>    x f the modeled measure time (CPU-starved worker)\n" +
        "  --no-emitters      skip the world-emitter (Radio-in-POI) acoustics workload\n" +
        "  --emitters-per-poi <n>  max speakers a POI may hold (default 4)\n" +
        "  --emitter-hz <h>   emitter decision cadence (default 4)\n" +
        "  -v                 verbose\n");
}
