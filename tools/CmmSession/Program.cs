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
// only hours of play reveal. It reports:
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
    enum MixerMode { Off, Headless, Realtime }

    // Loudness target the harness normalizes toward (mirrors the shipped policy's intent);
    // only used to derive a representative per-track gain to feed the mixer.
    const double LoudnessTargetLufs = -14.0;

    static int Main(string[] args)
    {
        AppDomain.MonitoringIsEnabled = true;   // precise per-AppDomain allocation tracking

        string musicDir = null;
        double hours = 24; float tick = 0.5f, dayMinutes = 60f; int seed = 1;
        bool decode = true, sweep = false, verbose = false, noLoudness = false, lookahead = true;
        double loudSlow = 1.0;
        MixerMode mixerMode = MixerMode.Headless;
        double reloadMin = 15.0;
        bool emitters = true; int emittersPerPoi = 4; float emitterHz = 4f;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--hours": hours = double.Parse(args[++i]); break;
                case "--tick": tick = float.Parse(args[++i]); break;
                case "--day-minutes": dayMinutes = float.Parse(args[++i]); break;
                case "--seed": seed = int.Parse(args[++i]); break;
                case "--mixer": mixerMode = ParseMixer(args[++i]); break;
                case "--reload-min": reloadMin = double.Parse(args[++i]); break;
                case "--no-decode": decode = false; break;
                case "--sweep": sweep = true; break;
                case "--no-dms": ContextMusicManager.DmsEnabled = false; break;
                case "--no-loudness": noLoudness = true; break;
                case "--no-lookahead": lookahead = false; break;
                case "--loud-slow": loudSlow = double.Parse(args[++i]); break;
                case "--no-emitters": emitters = false; break;
                case "--emitters-per-poi": emittersPerPoi = int.Parse(args[++i]); break;
                case "--emitter-hz": emitterHz = float.Parse(args[++i]); break;
                case "-v": verbose = true; break;
                case "-h": case "--help": Usage(); return 0;
                default: musicDir ??= args[i]; break;
            }
        }
        if (musicDir == null) musicDir = @"C:\_Games\CMM_TestMusic";
        CMMConfig.DebugLogging = verbose;
        Log.Quiet = !verbose;   // silence ScriptHost's per-load chatter unless -v

        string repoRoot = FindRepoRoot();
        bool haveLib = Directory.Exists(musicDir);

        // ── Reload virtualization: run ScriptHost against a TEMP copy of context.lua we can
        // safely flip between valid and invalid, so the real repo file is never touched. Falls
        // back to loading straight from the repo when virtualization is off / repo not found. ──
        var reload = ReloadHarness.Setup(reloadMin, repoRoot, tick);
        string loadDir = reload?.WorkDir ?? repoRoot ?? Directory.GetCurrentDirectory();

        // MoonSharp's static init logs a harmless NRE headless; mute just over Load.
        var savedOut = Console.Out; var savedErr = Console.Error;
        Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
        try { ScriptHost.Load(loadDir); }
        finally { Console.SetOut(savedOut); Console.SetError(savedErr); }
        if (!ScriptHost.Active)
        {
            Console.WriteLine("FATAL: no resolver loaded (MoonSharp/context.lua failed). Aborting.");
            return 1;
        }
        ContextMusicManager.InitContexts();
        ContextMusicManager.BuildFileIndex(haveLib ? musicDir : null);

        // ── Native mixer (the real cmm_mix_* producer/voice/retire loop) ──
        // headless: a null output device we drain on the sim clock → the mixer churns at
        //           compressed speed (leak/perf), no audio hardware, silent by nature.
        // realtime: the real device, MASTER VOLUME 0 (muted) → faithful underrun-under-GC
        //           timing, but bound to the wall clock (short soaks only).
        CmmMixer mixer = null;
        int pumpFramesPerTick = (int)(48000 * tick);
        if (mixerMode != MixerMode.Off && haveLib && decode)
        {
            if (mixerMode == MixerMode.Headless) CmmMixer.Headless = true;
            mixer = CmmMixer.GetOrCreate();
            if (mixerMode == MixerMode.Realtime) CmmMixer.MasterVolume = 0f;   // muted
        }
        else if (mixerMode != MixerMode.Off)
        {
            mixerMode = MixerMode.Off;   // no library / --no-decode → nothing to play
        }

        // World-emitter (Radio) workload: speakers "appear" in POIs (possibly several per POI) and
        // the acoustics (occlusion cost field + room probe) run per decision tick while in range —
        // the feature's MAIN-THREAD cost. Driven off the session's POI phase (Emitter.cs).
        var emitterSim = emitters ? new EmitterSim(seed, tick, emittersPerPoi, emitterHz) : null;

        int totalTicks = Math.Max(1, (int)(hours * 3600.0 / tick));
        Console.WriteLine($"CmmSession — {hours:F0}h session, tick {tick:F2}s ({totalTicks:N0} ticks), seed {seed}, " +
                          $"day={dayMinutes:F0}min, DMS={(ContextMusicManager.DmsEnabled ? "on" : "off")}");
        Console.WriteLine($"  script   : {(ScriptHost.UsingEmbeddedDefault ? "embedded default" : "Config/context.lua")}" +
                          $"{(reload != null ? $"  (reload flip every {reloadMin:F0} sim-min, valid↔invalid)" : "")}");
        Console.WriteLine($"  library  : {musicDir} ({(haveLib ? $"{ContextMusicManager.IndexedContexts} contexts / {ContextMusicManager.IndexedFiles} files" : "MISSING — resolve+GC only")})");
        Console.WriteLine($"  mixer    : {mixerMode.ToString().ToLower()}{(mixerMode == MixerMode.Realtime ? " (real device, muted, real-time)" : mixerMode == MixerMode.Headless ? " (null device, drained on sim clock)" : "")}");
        Console.WriteLine($"  decode   : {(decode && haveLib ? (sweep ? "per-transition native open + first-play R128 sweep" : "per-transition native open") : "off")}");
        Console.WriteLine($"  emitters : {(emitterSim != null ? $"on (≤{emittersPerPoi}/POI, {emitterHz:F0}Hz → {emitterSim.RunsPerTick}×/tick, occlusion+reverb)" : "off")}\n");

        // ── Preallocate sample buffers BEFORE the heap baseline so they don't skew drift ──
        var resolveMs = new double[totalTicks];
        var selectMs = new double[totalTicks];
        var decodeMs = new List<double>(totalTicks / 4 + 16);
        var decodeByExt = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var decodeErrByExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var measured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var transitionsByTop = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Crossfade-pressure tracking.
        var gaps = new List<double>(totalTicks / 16 + 16);
        var transitionTimes = new List<float>(totalTicks / 16 + 16);
        int jarring = 0;
        float prevTransTime = -1f, prevFadeWindow = 0f;

        // Per-track PLAY accounting: what played, and for how long (sim-seconds between the
        // transition that started it and the one that replaced it). Load time is tracked per
        // format (decodeByExt) + overall (decodeMs).
        var playStats = new Dictionary<string, PlayStat>(StringComparer.OrdinalIgnoreCase);
        string curFile = null; double curFileStart = 0;

        var gen = new SessionGenerator(seed, tick, dayMinutes);

        // ── Loudness-normalization model (mirrors the shipped policy, timing-aware) ──
        bool loudnessEnabled = !noLoudness && haveLib;
        var readyAt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var trackGain = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);  // file -> loudness gain (1 until measured)
        var failedMeas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double workerFree = 0.0;
        long playsTotal = 0, playsNormalized = 0;
        int unnormCold = 0, unnormFailed = 0;
        var unnormByExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unnormReasons = new Dictionary<string, int>();
        var unnormSamples = new List<string>();

        // Runs a measurement of `f` on the single background worker (or the menu prewarm when
        // background:false). Stores the derived loudness gain so a normalized play feeds the
        // mixer the real per-track gain. Returns true on success.
        bool Measure(string f, double atSim, bool background)
        {
            if (f == null) return false;
            if (readyAt.ContainsKey(f)) return true;
            if (failedMeas.Contains(f)) return false;
            long t0 = Stopwatch.GetTimestamp();
            TrackLoudness m;
            try { m = AudioDecoder.MeasureLoudness(f); }
            catch { m = new TrackLoudness { Ok = false }; }
            if (!m.Ok) { failedMeas.Add(f); return false; }
            trackGain[f] = LoudGain(m);
            if (background)
            {
                double dur = (Stopwatch.GetTimestamp() - t0) * loudSlow / Stopwatch.Frequency;
                double start = Math.Max(atSim, workerFree);
                workerFree = start + dur;
                readyAt[f] = workerFree;
            }
            else readyAt[f] = 0.0;  // prewarmed on the menu — ready from session start
            return true;
        }

        // Menu prewarm: measure one track per context before the session.
        if (loudnessEnabled)
            foreach (var kv in ContextMusicManager.Index)
                if (kv.Value.Length > 0) Measure(kv.Value[0], 0.0, background: false);

        // ── Multiplayer proximity tally ──
        long mpTicksAllies = 0, mpTicksStrangers = 0, mpTicksAny = 0, mpTicksInCombat = 0, mpTicksOutCombat = 0;
        int mpEnters = 0, mpMaxAllies = 0, mpMaxStrangers = 0;
        bool mpPrevAny = false;

        // Warm up the script + JIT + mixer so the baseline is steady-state.
        SimClock.Now = 0f;
        ScriptHost.Resolve(gen.Next());

        var proc = Process.GetCurrentProcess();
        long allocResolve = 0, allocSelect = 0, allocDecode = 0;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long heapBaseline = GC.GetTotalMemory(true);
        long rssBaseline = WorkingSet(proc);
        var heapCheckpoints = new List<(double pct, long heap, long rss, double simHour)>();
        int transitions = 0, decodes = 0, decodeErrors = 0;
        long allocStart = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        var wall = Stopwatch.StartNew();

        // Mixer output-health accumulation (meaningful in realtime mode; in headless the pump
        // outpaces the producer so "underruns" are an artifact — reported labelled per mode).
        int mixUnderruns = 0, mixMaxVoices = 0, mixMinQueuedMs = int.MaxValue;

        for (int t = 0; t < totalTicks; t++)
        {
            SimClock.Now = (t + 1) * tick;
            GameState gs = gen.Next();

            // ── Sim-time hot-reload flip (valid ↔ invalid context.lua + a config bump) ──
            reload?.MaybeFlip(SimClock.Now);

            // Phase 1 — resolve (linked ScriptHost: Lua marshal + decision).
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

            // Phase 2b — world-emitter acoustics (Radio speakers in POIs). Its own timing/alloc
            // accounting lives inside; kept separate from resolve/select so it doesn't skew them.
            emitterSim?.Tick(gs, SimClock.Now);

            // Phase 3 — decode + PLAY on a real transition to a file-backed context.
            if (r.Transitioned)
            {
                transitions++;
                transitionsByTop[r.Top] = transitionsByTop.TryGetValue(r.Top, out int c) ? c + 1 : 1;

                float nowT = SimClock.Now;
                if (prevTransTime >= 0f)
                {
                    double gap = nowT - prevTransTime;
                    gaps.Add(gap);
                    if (gap < prevFadeWindow) jarring++;
                }
                transitionTimes.Add(nowT);
                prevTransTime = nowT;
                var topCtx = ContextMusicManager.GetContext(r.Top);
                prevFadeWindow = Math.Max(ContextMusicManager.CrossFadeDuration, topCtx?.FadeInDuration ?? 0);

                if (haveLib && r.Top != "Vanilla" && r.Top != "Silence")
                {
                    string file = ContextMusicManager.PickFile(r.Top);
                    if (file != null)
                    {
                        // Close out the previously-playing track's duration (sim-seconds it was up).
                        AccountPlay(playStats, ref curFile, ref curFileStart, file, nowT);

                        // ── Loudness: was this track normalized when Play fired? ──
                        bool normalized = false;
                        if (loudnessEnabled)
                        {
                            playsTotal++;
                            double now = SimClock.Now;
                            string reason = null;
                            if (failedMeas.Contains(file)) { normalized = false; reason = "measurement FAILED — unity gain (every play)"; }
                            else if (readyAt.TryGetValue(file, out double ra) && ra <= now) normalized = true;
                            else if (readyAt.ContainsKey(file)) { normalized = false; reason = "look-ahead still measuring when Play fired (churn outran the worker)"; }
                            else { normalized = false; reason = "first need — not pre-measured, measured on demand after Play"; }

                            if (normalized) playsNormalized++;
                            else
                            {
                                string uext = Path.GetExtension(file).ToLowerInvariant();
                                unnormByExt[uext] = unnormByExt.TryGetValue(uext, out int uc) ? uc + 1 : 1;
                                unnormReasons[reason] = unnormReasons.TryGetValue(reason, out int rc) ? rc + 1 : 1;
                                if (reason.StartsWith("measurement FAILED")) unnormFailed++; else unnormCold++;
                                if (unnormSamples.Count < 25)
                                    unnormSamples.Add($"h{now / 3600.0,5:F1}  {r.Top,-24} {Path.GetFileName(file),-32} {reason}");
                            }

                            Measure(file, now, background: true);
                            if (lookahead) Measure(ContextMusicManager.PeekNextFile(r.Top), now, background: true);
                        }

                        if (decode)
                        {
                            long d0 = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                            ts = Stopwatch.GetTimestamp();
                            try
                            {
                                if (sweep && measured.Add(file))
                                    AudioDecoder.MeasureLoudness(file);

                                // Open the REAL native decoder handle (the same call the in-game
                                // control layer makes) — this is the per-transition LOAD cost.
                                int kind, ch, sr; long total;
                                IntPtr dec = AudioDecoder.OpenNativeHandle(file, out kind, out ch, out sr, out total);
                                double ms = Ms(ts);

                                if (dec == IntPtr.Zero) { decodeErrors++; BumpExt(decodeErrByExt, file); if (verbose) Console.WriteLine($"  OPEN FAIL {Path.GetFileName(file)}"); }
                                else
                                {
                                    if (mixer != null)
                                    {
                                        // Hand the decoder to the real mixer: it crossfades, owns + closes it.
                                        bool loop = topCtx?.Loop ?? true;
                                        float fade = Math.Max(ContextMusicManager.CrossFadeDuration, topCtx?.FadeInDuration ?? 0f);
                                        float duck = (topCtx?.PlayOnce ?? false) ? CmmMixer.StingerDuckLevel : -1f;
                                        float gain = normalized && trackGain.TryGetValue(file, out float gv) ? gv : 1f;
                                        mixer.Play(dec, kind, sr, ch, total, fade, loop, 0, gain, duck, snapTight: true);
                                    }
                                    else
                                    {
                                        AudioDecoder.CloseNativeHandle(dec, kind);   // off mode: measured open cost only
                                    }

                                    decodeMs.Add(ms);
                                    string ext = Path.GetExtension(file).ToLowerInvariant();
                                    if (!decodeByExt.TryGetValue(ext, out var l)) decodeByExt[ext] = l = new List<double>();
                                    l.Add(ms);
                                    decodes++;
                                    if (verbose) Console.WriteLine($"  [{r.Top}] {Path.GetFileName(file)} open {ms:F2}ms");
                                }
                            }
                            catch (Exception ex) { decodeErrors++; BumpExt(decodeErrByExt, file); if (verbose) Console.WriteLine($"  DECODE ERR {Path.GetFileName(file)}: {ex.Message}"); }
                            allocDecode += AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - d0;
                        }
                    }
                }
            }

            // ── Drive the mixer output ──
            if (mixerMode == MixerMode.Headless)
            {
                mixer.PumpOut(pumpFramesPerTick);              // drain the null ring on the sim clock
            }
            else if (mixerMode == MixerMode.Realtime)
            {
                // Pace the sim to the wall clock so Play cadence matches real playback, and
                // sample the real device's buffer health (underruns here are meaningful).
                mixer.GetOutputStats(out _, out _, out int minQ, out int voices, out _, out int under, out int busRate);
                if (under > mixUnderruns) mixUnderruns = under;
                if (voices > mixMaxVoices) mixMaxVoices = voices;
                if (voices > 0 && busRate > 0) { int mq = minQ * 1000 / busRate; if (mq < mixMinQueuedMs) mixMinQueuedMs = mq; }
                double targetMs = SimClock.Now * 1000.0;
                double behind = targetMs - wall.Elapsed.TotalMilliseconds;
                if (behind > 1) System.Threading.Thread.Sleep((int)behind);
            }

            // Heap + RSS checkpoints at 25/50/75/100% (forced GC → managed-heap leak signal;
            // WorkingSet drift → NATIVE leak signal, e.g. the mixer/decoders).
            if (t == totalTicks / 4 || t == totalTicks / 2 || t == 3 * totalTicks / 4 || t == totalTicks - 1)
            {
                if (mixerMode == MixerMode.Headless && mixer != null)
                {
                    mixer.GetOutputStats(out _, out _, out _, out int voices, out _, out int under, out _);
                    if (voices > mixMaxVoices) mixMaxVoices = voices;
                    mixUnderruns = under;   // cumulative (artifact in headless — see report)
                }
                proc.Refresh();
                heapCheckpoints.Add(((t + 1) * 100.0 / totalTicks, GC.GetTotalMemory(true), WorkingSet(proc), SimClock.Now / 3600.0));
            }
        }

        // Final track's play duration.
        AccountPlay(playStats, ref curFile, ref curFileStart, null, SimClock.Now);
        if (mixer != null) mixer.StopAllNow();
        wall.Stop();
        reload?.Cleanup();

        long allocTotal = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocStart;
        Report(totalTicks, tick, hours, gen.WorldDay, wall.Elapsed.TotalSeconds,
               transitions, decodes, decodeErrors, transitionsByTop,
               resolveMs, selectMs, decodeMs, decodeByExt,
               allocResolve, allocSelect, allocDecode, allocTotal,
               GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2,
               heapBaseline, rssBaseline, heapCheckpoints);

        PrintLoadHistogram(decodeMs, decodeErrByExt);
        PrintPlayAccounting(playStats, hours);
        PrintCrossfadePressure(gaps, jarring, transitionTimes);
        PrintMixer(mixerMode, mixUnderruns, mixMaxVoices, mixMinQueuedMs);
        PrintSharedCombat(gen, totalTicks);
        PrintMultiplayer(mpTicksAny, mpTicksAllies, mpTicksStrangers, mpTicksInCombat, mpTicksOutCombat,
                         mpEnters, mpMaxAllies, mpMaxStrangers, totalTicks, transitionsByTop);
        PrintReload(reload);
        PrintLoudness(loudnessEnabled, readyAt, failedMeas, playsTotal, playsNormalized,
                      unnormCold, unnormFailed, unnormReasons, unnormByExt, unnormSamples);
        PrintEmitters(emitterSim, totalTicks, tick, resolveMs, selectMs);
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

    static void BumpExt(Dictionary<string, int> d, string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        d[ext] = d.TryGetValue(ext, out int c) ? c + 1 : 1;
    }

    // ── Load-time distribution (ASCII histogram) ────────────────────────────────
    static void PrintLoadHistogram(List<double> loadMs, Dictionary<string, int> errByExt)
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
    static void PrintMixer(MixerMode mode, int underruns, int maxVoices, int minQueuedMs)
    {
        Console.WriteLine("\n── Native mixer (cmm_mix_*) output health ────────────────────────────");
        if (mode == MixerMode.Off) { Console.WriteLine("   (mixer off — resolve/select/decode only)"); return; }
        Console.WriteLine($"   mode: {mode.ToString().ToLower()}   ·   peak concurrent voices: {maxVoices}");
        if (mode == MixerMode.Realtime)
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

    static void PrintLoudness(bool enabled, Dictionary<string, double> readyAt, HashSet<string> failed,
        long playsTotal, long playsNormalized, int unnormCold, int unnormFailed,
        Dictionary<string, int> reasons, Dictionary<string, int> byExt, List<string> samples)
    {
        Console.WriteLine("\n── Loudness normalization (missing / un-normalized playback) ─────────");
        if (!enabled) { Console.WriteLine("   (normalization model off)"); return; }
        long unnorm = playsTotal - playsNormalized;
        Console.WriteLine($"   measured    : {readyAt.Count + failed.Count:N0} tracks ({failed.Count} FAILED)  ·  prewarm + single worker + look-ahead");
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
            var s = em.ScanMs.ToArray(); Array.Sort(s);
            Console.WriteLine($"   appearance scan (POI block sweep): n={s.Length}  p50 {Pct(s, 50):F3}ms  p95 {Pct(s, 95):F3}ms  max {s[s.Length - 1]:F3}ms");
        }

        // Steady per-run spatialisation cost — the per-frame main-thread hit while in range.
        if (em.RunMs.Count > 0)
        {
            var r = em.RunMs.ToArray(); Array.Sort(r);
            var occ = em.OccMs.ToArray(); Array.Sort(occ);
            var room = em.RoomMs.ToArray(); Array.Sort(room);
            Console.WriteLine($"   per run (occ+reverb): p50 {Pct(r, 50):F4}ms  p95 {Pct(r, 95):F4}ms  p99 {Pct(r, 99):F4}ms  max {r[r.Length - 1]:F4}ms  mean {r.Average():F4}ms");
            if (r[r.Length - 1] > 20 * Pct(r, 99))
                Console.WriteLine($"      (max ≫ p99 ⇒ a lone GC pause landed inside one timed span — p99 is the steady per-run cost)");
            Console.WriteLine($"      occlusion : p50 {Pct(occ, 50):F4}ms  p95 {Pct(occ, 95):F4}ms  max {occ[occ.Length - 1]:F4}ms");
            Console.WriteLine($"      room probe: p50 {Pct(room, 50):F4}ms  p95 {Pct(room, 95):F4}ms  max {room[room.Length - 1]:F4}ms");

            // Relative hit vs the existing per-tick decision cost (resolve + select).
            double emPerTick = r.Average() * em.RunsPerTick;
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
        double[] resolveMs, double[] selectMs, List<double> decodeMs, Dictionary<string, List<double>> decodeByExt,
        long allocResolve, long allocSelect, long allocDecode, long allocTotal,
        int g0, int g1, int g2, long heapBaseline, long rssBaseline,
        List<(double pct, long heap, long rss, double simHour)> chk)
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

        Console.WriteLine("\n── Memory drift (managed heap = GC leak · working set = NATIVE leak) ──");
        Console.WriteLine($"   baseline (post-warmup): heap {Mb(heapBaseline):F2} MB   ·   RSS {Mb(rssBaseline):F1} MB");
        foreach (var (pct, h, rss, simHour) in chk)
            Console.WriteLine($"   {pct,5:F0}%  (sim h{simHour,5:F1}) : heap {Mb(h),7:F2} MB (Δ{Mb(h - heapBaseline),6:F2})   RSS {Mb(rss),7:F1} MB (Δ{Mb(rss - rssBaseline),6:F1})");
        if (chk.Count >= 2)
        {
            long hEnd = chk[chk.Count - 1].heap, rEnd = chk[chk.Count - 1].rss;
            double heapDrift = Mb(hEnd - heapBaseline), rssDrift = Mb(rEnd - rssBaseline);
            double heapLate = Mb(hEnd - chk[chk.Count - 2].heap), rssLate = Mb(rEnd - chk[chk.Count - 2].rss);
            Console.WriteLine($"   heap: total Δ{Sign(heapDrift)} MB, final-quarter slope {Sign(heapLate)} MB  → {LeakVerdict(heapDrift, heapLate)}");
            Console.WriteLine($"   RSS : total Δ{Sign(rssDrift)} MB, final-quarter slope {Sign(rssLate)} MB  → {LeakVerdict(rssDrift, rssLate)}");
        }

        Console.WriteLine("\n── Top transition targets ────────────────────────────────────────────");
        foreach (var kv in byTop.OrderByDescending(k => k.Value).Take(12))
            Console.WriteLine($"   {kv.Value,6:N0}  {kv.Key}");
    }

    static string LeakVerdict(double drift, double lateSlope) =>
        lateSlope > 0.5 ? "STILL CLIMBING — leak suspect, investigate"
        : drift < 1.0 ? "FLAT — no leak"
        : "grew once then PLATEAUED (buffers/caches) — no ongoing leak";

    static void PrintLatency(string label, double[] samples)
    {
        if (samples.Length == 0) { Console.WriteLine($"   {label}: (none)"); return; }
        var s = (double[])samples.Clone(); Array.Sort(s);
        Console.WriteLine($"   {label}: p50 {Pct(s, 50),7:F3}ms  p95 {Pct(s, 95),7:F3}ms  p99 {Pct(s, 99),7:F3}ms  max {s[s.Length - 1],7:F3}ms  mean {s.Average(),7:F3}ms");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    static MixerMode ParseMixer(string s)
    {
        switch (s.ToLowerInvariant())
        {
            case "off": return MixerMode.Off;
            case "headless": return MixerMode.Headless;
            case "realtime": case "real": return MixerMode.Realtime;
            default: Console.WriteLine($"(unknown --mixer '{s}', using headless)"); return MixerMode.Headless;
        }
    }

    static float LoudGain(TrackLoudness m)
    {
        if (!m.Ok) return 1f;
        double g = Math.Pow(10.0, (LoudnessTargetLufs - m.Lufs) / 20.0);
        double peakLimit = m.Peak > 0.0 ? 1.0 / m.Peak : 4.0;   // linear-peak clip guard
        if (g > peakLimit) g = peakLimit;
        return (float)Math.Max(0.05, Math.Min(4.0, g));
    }

    static long WorkingSet(Process p) { p.Refresh(); return p.WorkingSet64; }

    static double Ms(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
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

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ContextMusicManager.csproj"))) return dir.FullName;
        return null;
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
