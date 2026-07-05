using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────
    // SessionRunner — the single per-tick drive of the synthetic session, shared by
    // the console bench (Program.cs) AND the top-down viewer (tools/CmmSessionView).
    //
    // It wraps the STATIC engine (ContextMusicManager + linked ScriptHost) — a session
    // is process-global by nature — and owns: setup (script load / context table / file
    // index / native mixer / EmitterSim / loudness prewarm / warmup), the per-tick core
    // (SimClock advance → gen.Next → reload flip → resolve → select → emitter → loudness
    // decision → PickFile → decode + Mixer.Play → headless PumpOut), the loudness model,
    // and the decode telemetry. Each Tick() returns a value-type TickOutcome carrying the
    // outcome + the per-phase timing/allocation the bench reports on; a renderer reads the
    // same outcome to draw.
    //
    // The bench's reporting accumulation (MP tally, crossfade pressure, transitionsByTop,
    // play accounting, loudness tally, memory checkpoints) stays in Program — this class
    // only drives the engine and measures the phases whose allocation windows must be
    // byte-exact (resolve / select / decode). See tools/CmmSession memory notes.
    // ─────────────────────────────────────────────────────────────────────────────

    internal enum SessionMixerMode { Off, Headless, Realtime }

    // Parsed run configuration. Defaults mirror Program's current CLI defaults so a
    // default-constructed SessionOptions reproduces the shipped bench behaviour.
    internal sealed class SessionOptions
    {
        public string MusicDir = null;
        public double Hours = 24;
        public float Tick = 0.5f;
        public float DayMinutes = 60f;
        public int Seed = 1;
        public bool Decode = true;
        public bool Sweep = false;
        public bool Verbose = false;
        public bool NoLoudness = false;
        public bool Lookahead = true;
        public double LoudSlow = 1.0;
        public SessionMixerMode MixerMode = SessionMixerMode.Headless;
        public double ReloadMin = 15.0;
        public bool DmsEnabled = true;
        public bool Emitters = true;
        public int EmittersPerPoi = 4;
        public float EmitterHz = 4f;
    }

    // Everything one advanced tick produced. A struct so Tick() adds no per-tick heap
    // allocation (the bench's allocTotal must stay identical). Timing/alloc fields carry
    // the measured spans the bench records; the rest describe the transition + playback.
    internal struct TickOutcome
    {
        public GameState State;          // the snapshot resolved this tick (reused pooled instance)
        public List<string> Tokens;      // active-context tokens ScriptHost.Resolve produced

        public double ResolveMs;         // ScriptHost.Resolve span
        public double SelectMs;          // ContextMusicManager.Tick span
        public long AllocResolve;        // bytes allocated inside the resolve span
        public long AllocSelect;         // bytes allocated inside the select span
        public long AllocDecode;         // bytes allocated inside the decode span (0 when no decode)

        public bool Transitioned;        // a real music transition fired this tick
        public string Top;               // resolved top context ("Vanilla"/"Silence" possible)

        public string PlayedFile;        // file PickFile chose (null: no file-backed transition)
        public bool Normalized;          // was PlayedFile loudness-normalized when Play fired
        public string UnnormReason;      // why not (null when Normalized or no loudness model)

        public bool Decoded;             // an OpenNativeHandle actually ran (decode enabled + file)
        public double DecodeMs;          // native open span (valid when Decoded)
        public bool DecodeError;         // open failed / threw
    }

    internal sealed class SessionRunner
    {
        // Loudness target the harness normalizes toward (mirrors the shipped policy's intent);
        // only used to derive a representative per-track gain to feed the mixer.
        const double LoudnessTargetLufs = -14.0;

        private readonly SessionOptions _o;
        private readonly SessionGenerator _gen;
        private int _tickIndex;   // 0 after warmup; first Tick() makes it 1 → SimClock = tick

        // ── Public run state / accessors (report + viewer) ──
        public SessionOptions Options => _o;
        public SessionGenerator Generator => _gen;
        public ReloadHarness Reload { get; }
        public CmmMixer Mixer { get; }
        public EmitterSim Emitter { get; }
        public SessionMixerMode MixerMode { get; private set; }
        public bool HaveLib { get; }
        public bool ScriptActive { get; }
        public bool LoudnessEnabled { get; }
        // Runtime decode gate (init from Options.Decode). The viewer flips this off in fast/
        // silent mode so a realtime mixer isn't fed transitions while the sim fast-forwards,
        // without tearing down + recreating the mixer. The bench never touches it → unchanged.
        public bool DecodeEnabled { get; set; }
        public int TotalTicks { get; }
        public int PumpFramesPerTick { get; }
        public string RepoRoot { get; }

        // Decode telemetry — owned here so its bookkeeping Add()s stay INSIDE the decode
        // allocation window (keeps the bench's decode-alloc number byte-exact).
        private readonly List<double> _decodeMs;
        private readonly Dictionary<string, List<double>> _decodeByExt;
        private readonly Dictionary<string, int> _decodeErrByExt;
        private readonly HashSet<string> _measured;
        public IReadOnlyList<double> DecodeMs => _decodeMs;
        public IReadOnlyDictionary<string, List<double>> DecodeByExt => _decodeByExt;
        public IReadOnlyDictionary<string, int> DecodeErrByExt => _decodeErrByExt;
        public int DecodeCount { get; private set; }
        public int DecodeErrors { get; private set; }

        // Loudness model (mirrors the shipped policy, timing-aware): prewarm + single
        // background worker + look-ahead. State advances as tracks are needed.
        private readonly Dictionary<string, double> _readyAt;
        private readonly Dictionary<string, float> _trackGain;
        private readonly HashSet<string> _failedMeas;
        private double _workerFree;
        public int ReadyCount => _readyAt.Count;
        public int FailedCount => _failedMeas.Count;

        // Latest tick outputs (for the viewer's live HUD).
        public TickOutcome LastOutcome { get; private set; }
        public List<string> LastTokens { get; private set; }

        public SessionRunner(SessionOptions o)
        {
            _o = o;
            CMMConfig.DebugLogging = o.Verbose;
            Log.Quiet = !o.Verbose;                       // silence ScriptHost's per-load chatter unless -v
            ContextMusicManager.DmsEnabled = o.DmsEnabled;

            RepoRoot = FindRepoRoot();
            HaveLib = !string.IsNullOrEmpty(o.MusicDir) && Directory.Exists(o.MusicDir);

            // ── Reload virtualization: run ScriptHost against a TEMP copy of context.lua we can
            // safely flip between valid and invalid, so the real repo file is never touched. ──
            Reload = ReloadHarness.Setup(o.ReloadMin, RepoRoot, o.Tick);
            string loadDir = Reload?.WorkDir ?? RepoRoot ?? Directory.GetCurrentDirectory();

            // MoonSharp's static init logs a harmless NRE headless; mute just over Load.
            var savedOut = Console.Out; var savedErr = Console.Error;
            Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
            try { ScriptHost.Load(loadDir); }
            finally { Console.SetOut(savedOut); Console.SetError(savedErr); }
            ScriptActive = ScriptHost.Active;
            if (!ScriptActive) return;                    // Program checks ScriptActive and aborts

            ContextMusicManager.InitContexts();
            ContextMusicManager.BuildFileIndex(HaveLib ? o.MusicDir : null);

            // ── Native mixer (the real cmm_mix_* producer/voice/retire loop) ──
            // headless: a null output device drained on the sim clock (leak/perf, silent).
            // realtime: the real device, MASTER VOLUME 0 (muted) → faithful timing, wall-bound.
            MixerMode = o.MixerMode;
            PumpFramesPerTick = (int)(48000 * o.Tick);
            if (MixerMode != SessionMixerMode.Off && HaveLib && o.Decode)
            {
                if (MixerMode == SessionMixerMode.Headless) CmmMixer.Headless = true;
                Mixer = CmmMixer.GetOrCreate();
                if (MixerMode == SessionMixerMode.Realtime) CmmMixer.MasterVolume = 0f;   // muted
            }
            else if (MixerMode != SessionMixerMode.Off)
            {
                MixerMode = SessionMixerMode.Off;         // no library / --no-decode → nothing to play
            }

            // World-emitter (Radio) acoustics workload, driven off the POI phase.
            Emitter = o.Emitters ? new EmitterSim(o.Seed, o.Tick, o.EmittersPerPoi, o.EmitterHz) : null;

            TotalTicks = Math.Max(1, (int)(o.Hours * 3600.0 / o.Tick));

            // Decode telemetry buffers — sized like Program's originals, allocated pre-baseline.
            _decodeMs = new List<double>(TotalTicks / 4 + 16);
            _decodeByExt = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            _decodeErrByExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _measured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Loudness model state.
            _readyAt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _trackGain = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            _failedMeas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _gen = new SessionGenerator(o.Seed, o.Tick, o.DayMinutes);

            DecodeEnabled = o.Decode;
            LoudnessEnabled = !o.NoLoudness && HaveLib;

            // Menu prewarm: measure one track per context before the session.
            if (LoudnessEnabled)
                foreach (var kv in ContextMusicManager.Index)
                    if (kv.Value.Length > 0) Measure(kv.Value[0], 0.0, background: false);

            // Warm up the script + JIT so the baseline is steady-state (consumes one gen tick).
            SimClock.Now = 0f;
            ScriptHost.Resolve(_gen.Next());
        }

        // Advances the session one tick through the real decision + decode + mixer path and
        // returns everything that happened. The bench reads timing/alloc off the result; the
        // viewer reads State/Tokens/Top/PlayedFile to render.
        //
        // `supplied` overrides the source snapshot: pass null (the bench + autopilot) to advance
        // the deterministic SessionGenerator; pass a hand-authored GameState (the viewer's manual
        // sandbox) to feed the real resolver an arbitrary state — the generator stays frozen so
        // autopilot resumes seamlessly. Everything downstream (resolve/select/emitter/decode/play)
        // is source-agnostic, so the tested decision path is unchanged either way.
        public TickOutcome Tick(GameState supplied = null)
        {
            var outcome = default(TickOutcome);
            _tickIndex++;
            SimClock.Now = _tickIndex * _o.Tick;          // reproduces Program's (t+1)*tick
            GameState gs = supplied ?? _gen.Next();
            outcome.State = gs;

            // ── Sim-time hot-reload flip (valid ↔ invalid context.lua + a config bump) ──
            Reload?.MaybeFlip(SimClock.Now);

            // Phase 1 — resolve (linked ScriptHost: Lua marshal + decision).
            long a0 = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            long ts = Stopwatch.GetTimestamp();
            List<string> tokens = ScriptHost.Resolve(gs);
            outcome.ResolveMs = Ms(ts);
            long a1 = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            outcome.AllocResolve = a1 - a0;
            outcome.Tokens = tokens;
            LastTokens = tokens;

            // Phase 2 — select (ported stack/fallback/warmup gate).
            ts = Stopwatch.GetTimestamp();
            var r = ContextMusicManager.Tick(tokens, gs);
            outcome.SelectMs = Ms(ts);
            long a2 = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            outcome.AllocSelect = a2 - a1;
            outcome.Transitioned = r.Transitioned;
            outcome.Top = r.Top;

            // Phase 2b — world-emitter acoustics (Radio speakers in POIs). Its own timing/alloc
            // accounting lives inside; kept separate from resolve/select so it doesn't skew them.
            Emitter?.Tick(gs, SimClock.Now);

            // Phase 3 — decode + PLAY on a real transition to a file-backed context.
            if (r.Transitioned && HaveLib && r.Top != "Vanilla" && r.Top != "Silence")
            {
                string file = ContextMusicManager.PickFile(r.Top);
                if (file != null)
                {
                    outcome.PlayedFile = file;
                    var topCtx = ContextMusicManager.GetContext(r.Top);

                    // ── Loudness: was this track normalized when Play fired? ──
                    bool normalized = false;
                    if (LoudnessEnabled)
                    {
                        double now = SimClock.Now;
                        string reason = null;
                        if (_failedMeas.Contains(file)) { normalized = false; reason = "measurement FAILED — unity gain (every play)"; }
                        else if (_readyAt.TryGetValue(file, out double ra) && ra <= now) normalized = true;
                        else if (_readyAt.ContainsKey(file)) { normalized = false; reason = "look-ahead still measuring when Play fired (churn outran the worker)"; }
                        else { normalized = false; reason = "first need — not pre-measured, measured on demand after Play"; }

                        outcome.Normalized = normalized;
                        outcome.UnnormReason = reason;

                        Measure(file, now, background: true);
                        if (_o.Lookahead) Measure(ContextMusicManager.PeekNextFile(r.Top), now, background: true);
                    }

                    if (DecodeEnabled)
                    {
                        long d0 = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                        ts = Stopwatch.GetTimestamp();
                        try
                        {
                            if (_o.Sweep && _measured.Add(file))
                                AudioDecoder.MeasureLoudness(file);

                            // Open the REAL native decoder handle (the same call the in-game
                            // control layer makes) — this is the per-transition LOAD cost.
                            int kind, ch, sr; long total;
                            IntPtr dec = AudioDecoder.OpenNativeHandle(file, out kind, out ch, out sr, out total);
                            double ms = Ms(ts);

                            if (dec == IntPtr.Zero) { DecodeErrors++; BumpExt(_decodeErrByExt, file); outcome.DecodeError = true; if (_o.Verbose) Console.WriteLine($"  OPEN FAIL {Path.GetFileName(file)}"); }
                            else
                            {
                                if (Mixer != null)
                                {
                                    // Hand the decoder to the real mixer: it crossfades, owns + closes it.
                                    bool loop = topCtx?.Loop ?? true;
                                    float fade = Math.Max(ContextMusicManager.CrossFadeDuration, topCtx?.FadeInDuration ?? 0f);
                                    float duck = (topCtx?.PlayOnce ?? false) ? CmmMixer.StingerDuckLevel : -1f;
                                    float gain = normalized && _trackGain.TryGetValue(file, out float gv) ? gv : 1f;
                                    Mixer.Play(dec, kind, sr, ch, total, fade, loop, 0, gain, duck, snapTight: true);
                                }
                                else
                                {
                                    AudioDecoder.CloseNativeHandle(dec, kind);   // off mode: measured open cost only
                                }

                                _decodeMs.Add(ms);
                                string ext = Path.GetExtension(file).ToLowerInvariant();
                                if (!_decodeByExt.TryGetValue(ext, out var l)) _decodeByExt[ext] = l = new List<double>();
                                l.Add(ms);
                                DecodeCount++;
                                outcome.Decoded = true;
                                outcome.DecodeMs = ms;
                                if (_o.Verbose) Console.WriteLine($"  [{r.Top}] {Path.GetFileName(file)} open {ms:F2}ms");
                            }
                        }
                        catch (Exception ex) { DecodeErrors++; BumpExt(_decodeErrByExt, file); outcome.DecodeError = true; if (_o.Verbose) Console.WriteLine($"  DECODE ERR {Path.GetFileName(file)}: {ex.Message}"); }
                        outcome.AllocDecode = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - d0;
                    }
                }
            }

            // ── Drive the mixer output (headless drains the null ring on the sim clock). The
            // realtime pacing + output-health sampling is the caller's job (wall-clock bound). ──
            if (MixerMode == SessionMixerMode.Headless)
                Mixer.PumpOut(PumpFramesPerTick);

            LastOutcome = outcome;
            return outcome;
        }

        // End-of-session teardown (mirrors Program's final StopAllNow + reload cleanup).
        public void Stop()
        {
            if (Mixer != null) Mixer.StopAllNow();
            Reload?.Cleanup();
        }

        // Runs a measurement of `f` on the single background worker (or the menu prewarm when
        // background:false). Stores the derived loudness gain so a normalized play feeds the
        // mixer the real per-track gain. Returns true on success.
        private bool Measure(string f, double atSim, bool background)
        {
            if (f == null) return false;
            if (_readyAt.ContainsKey(f)) return true;
            if (_failedMeas.Contains(f)) return false;
            long t0 = Stopwatch.GetTimestamp();
            TrackLoudness m;
            try { m = AudioDecoder.MeasureLoudness(f); }
            catch { m = new TrackLoudness { Ok = false }; }
            if (!m.Ok) { _failedMeas.Add(f); return false; }
            _trackGain[f] = LoudGain(m);
            if (background)
            {
                double dur = (Stopwatch.GetTimestamp() - t0) * _o.LoudSlow / Stopwatch.Frequency;
                double start = Math.Max(atSim, _workerFree);
                _workerFree = start + dur;
                _readyAt[f] = _workerFree;
            }
            else _readyAt[f] = 0.0;  // prewarmed on the menu — ready from session start
            return true;
        }

        private static float LoudGain(TrackLoudness m)
        {
            if (!m.Ok) return 1f;
            double g = Math.Pow(10.0, (LoudnessTargetLufs - m.Lufs) / 20.0);
            double peakLimit = m.Peak > 0.0 ? 1.0 / m.Peak : 4.0;   // linear-peak clip guard
            if (g > peakLimit) g = peakLimit;
            return (float)Math.Max(0.05, Math.Min(4.0, g));
        }

        private static void BumpExt(Dictionary<string, int> d, string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            d[ext] = d.TryGetValue(ext, out int c) ? c + 1 : 1;
        }

        private static double Ms(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "ContextMusicManager.csproj"))) return dir.FullName;
            return null;
        }
    }
}
