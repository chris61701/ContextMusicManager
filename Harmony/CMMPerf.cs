using System;
using System.Diagnostics;
using System.Text;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // PERF INSTRUMENTATION  (context-change hot path)
    // ─────────────────────────────────────────────────────────────────────────
    // Lightweight, allocation-free timing for the per-tick scan and the per-
    // transition audio swap, so an in-game stutter can be pinned to a phase and
    // correlated with a GC. Everything is gated behind CMMConfig.PerfTiming
    // (default OFF) — when off, the only cost is two Stopwatch.GetTimestamp()
    // calls per measured span (~tens of ns), and nothing is logged.
    //
    // Usage:
    //   using (CMMPerf.Measure(CMMPerf.Phase.ScanResolve)) { ...synchronous work... }
    // A scope MUST wrap a span with NO `yield` inside it — it measures wall time
    // between construction and Dispose, which for a coroutine that yields would
    // wrongly fold in frames spent waiting. The transition wall timer
    // (BeginTransition/EndTransition) is the one place that spans yields, and it
    // is labelled "wall" precisely because it includes background-decode waits.
    //
    // Two signals, two purposes:
    //   • Per-phase rolling stats (n / avg / max ms) — dumped by StalenessCheckLoop.
    //   • SPIKE lines — any single synchronous span over PerfSpikeMs is logged the
    //     instant it happens, with the phase name. That immediate line, lined up
    //     against the TX (transition) GC line, is what actually fingers the hitch.

    internal static class CMMPerf
    {
        public static bool Enabled => CMMConfig.PerfTiming;

        // Fixed slots so accumulation is array-indexed (no dictionary, no boxing).
        // Keep in sync with Names below.
        public enum Phase
        {
            ScanPoll,        // ContextResolver.BuildSnapshot — game-state poll
            ScanResolve,     // ScriptHost.Resolve — whole Lua marshal + decision (parent of the two below)
            ScanGate,        // change-detection / stack rebuild gate
            ScanMarshal,     // ScriptHost: GameState/cfg → Lua tables (per-enemy table alloc lives here)
            ScanLuaCall,     // ScriptHost: _script.Call + result-table walk (MoonSharp's own alloc)
            // The emitter registry. EmitProx is the one the widen-the-discovery-box decision turns
            // on: its box is a KNOWN voxel count, so avg ms over that count is nanoseconds per
            // block read, which is the only number that says what a bigger box would actually
            // cost. It had never been measured -- the "~21k reads, a couple of ms" in
            // EmitterRegistry was an estimate someone wrote down, and estimates are what the
            // radio tuning spent weeks paying for.
            EmitPoll,        // EmitterRegistry.Poll — the whole 4 Hz registry tick
            EmitProx,        // EmitterRegistry.ScanProximity — the placed-radio discovery box (2 Hz)
            EmitPoiScan,     // EmitterRegistry.ContinueScan — amortised POI walk (ScanBudgetMs-capped)
            // The OCCLUSION DECISION, and the only phase WorldEmitterTickHz actually governs.
            // EmitPoll is a DIFFERENT loop: EmitterRegistry keeps its own PollInterval (4 Hz,
            // hardcoded), so raising the tick rate does not change it and its timings say nothing
            // about what the tick rate costs. That distinction was got wrong once already.
            EmitTick,        // EmitterAudioLayer.Tick, post-throttle — decide + publish point sets
            // DISK ON THE MAIN THREAD. The music lives on a removable exFAT SD card, where even a
            // one-file stat can queue behind a bulk read (a loudness decode, a folder scan) on the
            // same card. Every main-thread file call on the play path is timed here, so a stutter
            // can be pinned on a disk wait rather than argued about. See also QueueIo (background).
            MainIo,
            COUNT
        }

        static readonly string[] Names =
        {
            "ScanPoll", "ScanResolve", "ScanGate", "ScanMarshal", "ScanLuaCall",
            "EmitPoll", "EmitProx", "EmitPoiScan", "EmitTick", "MainIo"
        };

        const int N = (int)Phase.COUNT;
        static readonly int[]    _count  = new int[N];
        static readonly double[] _totMs  = new double[N];
        static readonly double[] _maxMs  = new double[N];
        // Per-phase allocation (bytes), accumulated between rolling dumps. Lets us see
        // WHICH phase generates garbage — the Resolver poll, our marshalling, MoonSharp's
        // call, the gate, or the transition — not just the lumped per-tick scan number.
        static readonly long[]   _allocB = new long[N];

        static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        // ── Scoped span timer + allocation probe ───────────────────────────────
        // Captures both wall time and the GC.GetTotalMemory delta across the span. The
        // alloc number is a main-thread estimate: a GC firing inside the window makes the
        // delta meaningless (negative or undercounted), so Record drops negative samples,
        // and a concurrent background-thread allocation can slightly inflate it. Good
        // enough to rank the phases by churn, which is the question.
        public readonly struct Scope : IDisposable
        {
            readonly Phase _phase;
            readonly long  _memStart;
            readonly long  _start;
            internal Scope(Phase phase)
            {
                _phase = phase;
                // Snapshot memory before the clock so GetTotalMemory's own cost isn't
                // billed to the span's wall time.
                _memStart = CMMConfig.PerfTiming ? GC.GetTotalMemory(false) : 0L;
                _start    = Stopwatch.GetTimestamp();
            }
            public void Dispose()
            {
                long ticks = Stopwatch.GetTimestamp() - _start;   // stop the clock first
                long memDelta = CMMConfig.PerfTiming ? GC.GetTotalMemory(false) - _memStart : 0L;
                Record(_phase, ticks, memDelta);
            }
        }

        public static Scope Measure(Phase phase) => new Scope(phase);

        static void Record(Phase phase, long elapsedTicks, long allocBytes)
        {
            if (!Enabled) return;
            double ms = elapsedTicks * TicksToMs;
            int i = (int)phase;
            _count[i]++;
            _totMs[i] += ms;
            if (ms > _maxMs[i]) _maxMs[i] = ms;
            if (allocBytes > 0) _allocB[i] += allocBytes;   // negatives = a GC fired mid-span
            if (phase == Phase.MainIo) _ioTicksThisFrame += elapsedTicks;

            if (ms >= CMMConfig.PerfSpikeMs)
                Log.Out($"[CMM PERF] SPIKE {Names[i]} {ms:F2} ms");
        }

        // ── Per-transition wall + GC correlation ───────────────────────────────
        // Begin at TriggerMusic (the moment a switch is decided); End when
        // StartNativeContext finishes committing. Wall time spans yields on purpose,
        // so it captures total transition latency including background-decode waits.
        static long _txStart;
        static int  _gc0, _gc1, _gc2;
        static long _txMem;
        static string _txCtx;
        static int  _txInterrupted;   // transitions cut short by a newer trigger (reset on dump)

        // True while a transition is mid-flight, for the frame watchdog's label.
        public static bool TransitionInFlight => _txStart != 0;
        public static string TransitionContext => _txCtx;

        public static void BeginTransition(string contextName)
        {
            if (!Enabled) return;
            // If the previous transition is still open, this new trigger INTERRUPTED it
            // (a newer _transitionId superseded it before EndTransition ran).
            // That is exactly the combat-churn case we care about, and the normal TX line
            // would otherwise be silently dropped — so log the partial here so the GC
            // pressure of interrupted transitions isn't invisible.
            if (_txStart != 0)
            {
                double partialMs = (Stopwatch.GetTimestamp() - _txStart) * TicksToMs;
                int pg0 = GC.CollectionCount(0) - _gc0;
                int pg1 = GC.CollectionCount(1) - _gc1;
                int pg2 = GC.CollectionCount(2) - _gc2;
                _txInterrupted++;
                Log.Out($"[CMM PERF] TX-INTERRUPT {_txCtx}→{contextName}: " +
                        $"partial {partialMs:F1} ms, GC gen0={pg0} gen1={pg1} gen2={pg2}");
            }
            _txCtx  = contextName;
            _gc0    = GC.CollectionCount(0);
            _gc1    = GC.CollectionCount(1);
            _gc2    = GC.CollectionCount(2);
            _txMem  = GC.GetTotalMemory(false);
            _txStart = Stopwatch.GetTimestamp();
        }

        public static void EndTransition()
        {
            if (!Enabled || _txStart == 0) return;
            double wallMs = (Stopwatch.GetTimestamp() - _txStart) * TicksToMs;
            int d0 = GC.CollectionCount(0) - _gc0;
            int d1 = GC.CollectionCount(1) - _gc1;
            int d2 = GC.CollectionCount(2) - _gc2;
            long memDelta = GC.GetTotalMemory(false) - _txMem;
            Log.Out($"[CMM PERF] TX {_txCtx}: wall {wallMs:F1} ms, " +
                    $"GC gen0={d0} gen1={d1} gen2={d2}, heapΔ {memDelta / 1024f:F0} KB");
            _txStart = 0;
        }

        // ── Per-frame CMM SELF time ────────────────────────────────────────────
        // The question every STUTTER line used to leave open: of that 300 ms frame, how much
        // was CMM? The phase spans above only cover the scan and the emitters; the rest of
        // CMM's main-thread work (the Update body, OnGUI and the debug HUD, the dispatcher
        // drain, every coroutine step) was never timed, so a stutter with no SPIKE beside it
        // could be CMM's or the game's and nothing could say which (2026-11-06: 510 of 575
        // stutters had no CMM span near them). Every main-thread entry point now reports its
        // span here; FrameTick folds the total into the frame it belongs to.
        //
        // Not nested: each entry point is a separate Unity callback, and the coroutine wrapper
        // times only its own MoveNext (a nested IEnumerator is run by Unity as its own coroutine
        // and timed by its own wrapper when it was started through ours).
        static long   _selfTicks;          // CMM main-thread ticks since the last FrameTick
        static long   _selfTopTicks;       // the largest single span among them
        static object _selfTopKey;         // a string label, or a coroutine's Type
        // Rolling (dumped with Phases).
        static long   _selfFrames;
        static double _selfTotMs, _selfMaxMs;
        static object _selfMaxKey;
        // Bursts.
        static double _burstSelfWorstFrameMs;   // CMM ms inside the burst's WORST frame
        static object _burstSelfWorstKey;
        static double _burstSelfSumMs;          // CMM ms across the whole burst
        static double _burstSelfMaxMs;          // largest CMM total in any frame of the burst
        static int    _burstDecodeFrames;       // frames that had a loudness decode running
        static int    _burstTrackStarts;        // voices started during the burst

        // Loudness decodes actually decoding right now (inside the serialize gate). Written by
        // the CMM-Loudness threads.
        static int _decodesRunning;
        public static void DecodeBegin() => System.Threading.Interlocked.Increment(ref _decodesRunning);
        public static void DecodeEnd()   => System.Threading.Interlocked.Decrement(ref _decodesRunning);

        // A music voice was started (StartNativeContext committed one) this frame.
        static int _trackStartsThisFrame;
        public static void NoteTrackStart() { if (Enabled) _trackStartsThisFrame++; }

        // Main-thread span: `long t0 = CMMPerf.SelfBegin(); ...; CMMPerf.SelfEnd(t0, "OnGUI");`
        // Spans can nest (Update starts a coroutine, whose first step runs synchronously inside
        // StartCoroutine; a coroutine step does `yield return StartCoroutine(inner)`), so only the
        // OUTERMOST span is added — otherwise the inner step would be counted twice.
        static int _selfDepth;
        public static long SelfBegin()
        {
            if (!Enabled) return 0L;
            _selfDepth++;
            return Stopwatch.GetTimestamp();
        }
        public static void SelfEnd(long t0, object key)
        {
            if (t0 == 0) return;
            if (--_selfDepth > 0) return;
            _selfDepth = 0;
            long d = Stopwatch.GetTimestamp() - t0;
            _selfTicks += d;
            if (d > _selfTopTicks) { _selfTopTicks = d; _selfTopKey = key; }
        }

        static string KeyName(object k)
        {
            if (k == null) return "-";
            if (k is string s) return s;
            if (k is Type t)
            {
                // "<ContextScanLoop>d__42" → "ContextScanLoop"
                string n = t.Name;
                int a = n.IndexOf('<'), b = n.IndexOf('>');
                return a >= 0 && b > a ? n.Substring(a + 1, b - a - 1) : n;
            }
            return k.ToString();
        }

        // Wraps a coroutine so each MoveNext is timed as a self span keyed by the coroutine's
        // type. Current passes straight through, so WaitForSeconds / nested IEnumerators /
        // Coroutine handles behave exactly as before. Stopping still works: callers stop by the
        // Coroutine handle, which is the wrapper's.
        public static System.Collections.IEnumerator Timed(System.Collections.IEnumerator inner)
        {
            Type key = inner.GetType();
            while (true)
            {
                long t0 = SelfBegin();
                bool more;
                try { more = inner.MoveNext(); }
                finally { SelfEnd(t0, key); }
                if (!more) yield break;
                yield return inner.Current;
            }
        }

        // ── Disk ───────────────────────────────────────────────────────────────
        // Main thread: time a single existence check (the per-track-start ones).
        public static bool TimedExists(string path)
        {
            using (Measure(Phase.MainIo)) return System.IO.File.Exists(path);
        }

        // Background: every CMM folder/playlist scan goes through here instead of a bare
        // ThreadPool.QueueUserWorkItem, so a stutter can say whether a scan was hitting the disk
        // at the time (bgio=), and the Phases line reports how long the scans take. Totals are
        // kept with Interlocked and only READ (and logged) on the main thread.
        public enum IoKind { FileWatchPing, FileWatchSig, FileWatchScan, Presence, Crawler, Reload, COUNT }
        static readonly string[] IoNames = { "FwPing", "FwSig", "FwScan", "Presence", "Crawler", "Reload" };
        static readonly long[] _ioJobs  = new long[(int)IoKind.COUNT];
        static readonly long[] _ioTicks = new long[(int)IoKind.COUNT];
        static readonly long[] _ioMax   = new long[(int)IoKind.COUNT];
        static int _bgIoRunning;
        static long _ioTicksThisFrame;          // main-thread MainIo ticks in the current frame
        static double _burstIoWorstFrameMs, _burstIoSumMs;
        static int _burstBgIoFrames;

        public static void QueueIo(IoKind kind, System.Threading.WaitCallback work)
        {
            if (!Enabled) { System.Threading.ThreadPool.QueueUserWorkItem(work); return; }
            System.Threading.ThreadPool.QueueUserWorkItem(s =>
            {
                System.Threading.Interlocked.Increment(ref _bgIoRunning);
                long t0 = Stopwatch.GetTimestamp();
                try { work(s); }
                finally
                {
                    long d = Stopwatch.GetTimestamp() - t0;
                    System.Threading.Interlocked.Decrement(ref _bgIoRunning);
                    int k = (int)kind;
                    System.Threading.Interlocked.Increment(ref _ioJobs[k]);
                    System.Threading.Interlocked.Add(ref _ioTicks[k], d);
                    long m;
                    while (d > (m = System.Threading.Interlocked.Read(ref _ioMax[k]))
                           && System.Threading.Interlocked.CompareExchange(ref _ioMax[k], d, m) != m) { }
                }
            });
        }

        // ── Per-frame stutter watchdog ─────────────────────────────────────────
        // Called once per frame from Update(). A SUSTAINED stutter is many anomalous
        // frames in a row; the per-span SPIKE probes can't see it because the cost
        // lives OUTSIDE our measured spans (per-frame crossfade lerps, leaked
        // coroutines pinning work, or a GC firing off our hot path).
        //
        // The threshold is RELATIVE to the device's own rolling baseline, not an
        // absolute ms — on a 30 fps machine every frame is ~33 ms, so an absolute
        // cut-off just logs the baseline framerate. We track an EMA of "normal"
        // frame time (spikes are excluded so one bad frame can't poison it) and flag
        // a frame only when it exceeds baseline × PerfFrameSpikeFactor AND clears an
        // absolute floor (PerfFrameSpikeFloorMs, so a momentary dip on a fast machine
        // isn't reported as a 2× "spike"). Each flagged frame is attributed:
        //   • gcΔ gen0/1/2 — a bump here means the frame ate a garbage collection.
        //   • tx — the transition in flight (or "idle"), so churn-correlated freezes
        //     are obvious. A stutter that is always tx=idle / GC=0 is NOT CMM's doing.
        //   • coro — count of CMM's live music coroutines (passed in by the caller),
        //     so a pile-up of un-stopped crossfades shows up as a rising number.
        // Consecutive flagged frames are coalesced into one STUTTER summary so a
        // multi-second hitch is one line, not a wall of them.
        static long   _frameLastTs;
        static int    _frameGc0, _frameGc1, _frameGc2;
        static double _frameBaselineMs;   // EMA of normal (non-spike) frame time
        const  double FrameEmaAlpha = 0.05;   // ~20-frame time constant; slow, stable
        // Active burst accumulator.
        static int    _burstFrames;
        static double _burstWorstMs, _burstSpanMs, _burstBaseSum;
        static int    _burstG0, _burstG1, _burstG2;
        static int    _burstMaxCoro;

        public static void FrameTick(int liveCoroutines)
        {
            if (!Enabled) return;

            long now = Stopwatch.GetTimestamp();
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

            if (_frameLastTs == 0)   // first frame: seed baselines, nothing to measure
            {
                _frameLastTs = now; _frameGc0 = g0; _frameGc1 = g1; _frameGc2 = g2;
                return;
            }

            double frameMs = (now - _frameLastTs) * TicksToMs;
            int dg0 = g0 - _frameGc0, dg1 = g1 - _frameGc1, dg2 = g2 - _frameGc2;
            _frameLastTs = now; _frameGc0 = g0; _frameGc1 = g1; _frameGc2 = g2;

            // CMM's own main-thread time in the frame that just ended.
            double selfMs = _selfTicks * TicksToMs;
            object selfTop = _selfTopKey;
            int starts = _trackStartsThisFrame;
            double ioMs = _ioTicksThisFrame * TicksToMs;
            _selfTicks = 0; _selfTopTicks = 0; _selfTopKey = null; _trackStartsThisFrame = 0;
            _ioTicksThisFrame = 0;
            _selfFrames++; _selfTotMs += selfMs;
            if (selfMs > _selfMaxMs) { _selfMaxMs = selfMs; _selfMaxKey = selfTop; }

            if (_frameBaselineMs == 0) _frameBaselineMs = frameMs;   // seed the EMA

            // Anomalous = well above this device's own recent baseline AND past the
            // absolute floor (the floor stops fast machines flagging trivial wobble).
            double threshold = Math.Max(_frameBaselineMs * CMMConfig.PerfFrameSpikeFactor,
                                        CMMConfig.PerfFrameSpikeFloorMs);
            bool spike = frameMs >= threshold;

            if (spike)
            {
                // Accumulate into the current burst (emitted when it ends). The baseline
                // is intentionally NOT updated here, so one hitch can't drag it up.
                _burstFrames++;
                _burstSpanMs += frameMs;
                _burstBaseSum += _frameBaselineMs;
                if (frameMs > _burstWorstMs)
                {
                    _burstWorstMs = frameMs;
                    _burstSelfWorstFrameMs = selfMs;
                    _burstSelfWorstKey = selfTop;
                    _burstIoWorstFrameMs = ioMs;
                }
                _burstIoSumMs += ioMs;
                if (System.Threading.Volatile.Read(ref _bgIoRunning) > 0) _burstBgIoFrames++;
                _burstSelfSumMs += selfMs;
                if (selfMs > _burstSelfMaxMs) _burstSelfMaxMs = selfMs;
                if (System.Threading.Volatile.Read(ref _decodesRunning) > 0) _burstDecodeFrames++;
                _burstTrackStarts += starts;
                _burstG0 += dg0; _burstG1 += dg1; _burstG2 += dg2;
                if (liveCoroutines > _burstMaxCoro) _burstMaxCoro = liveCoroutines;
            }
            else
            {
                // Normal frame: fold into the rolling baseline, and close any open burst.
                _frameBaselineMs += FrameEmaAlpha * (frameMs - _frameBaselineMs);
                if (_burstFrames > 0) FlushBurst();
            }
        }

        static void FlushBurst()
        {
            string tx = TransitionInFlight ? TransitionContext : "idle";
            double avgBase = _burstBaseSum / _burstFrames;
            double mult = avgBase > 0 ? _burstWorstMs / avgBase : 0;
            Log.Out($"[CMM PERF] STUTTER {_burstFrames} frame(s) over {_burstSpanMs:F0} ms, " +
                    $"worst {_burstWorstMs:F1} ms ({mult:F1}× baseline {avgBase:F1}), " +
                    $"GC gen0={_burstG0} gen1={_burstG1} gen2={_burstG2}, tx={tx}, coroMax={_burstMaxCoro}, " +
                    // cmm = CMM's own main-thread ms INSIDE the worst frame (and its biggest span);
                    // cmmΣ = across the burst. decode = frames with a loudness decode running;
                    // trk = music voices started during the burst.
                    $"cmm={_burstSelfWorstFrameMs:F1} ms ({KeyName(_burstSelfWorstKey)}) " +
                    $"cmmMax={_burstSelfMaxMs:F1} cmmΣ={_burstSelfSumMs:F0} ms, " +
                    $"decode={_burstDecodeFrames}/{_burstFrames}, trk={_burstTrackStarts}, " +
                    // io = main-thread disk ms inside the worst frame (ioΣ across the burst);
                    // bgio = frames during which a background folder/playlist scan was running.
                    $"io={_burstIoWorstFrameMs:F1} ioΣ={_burstIoSumMs:F1} ms, bgio={_burstBgIoFrames}/{_burstFrames}");
            _burstFrames = 0; _burstWorstMs = 0; _burstSpanMs = 0; _burstBaseSum = 0;
            _burstG0 = _burstG1 = _burstG2 = 0; _burstMaxCoro = 0;
            _burstSelfWorstFrameMs = 0; _burstSelfWorstKey = null; _burstSelfSumMs = 0; _burstSelfMaxMs = 0;
            _burstDecodeFrames = 0; _burstTrackStarts = 0;
            _burstIoWorstFrameMs = 0; _burstIoSumMs = 0; _burstBgIoFrames = 0;
        }

        // ── Rolling report ─────────────────────────────────────────────────────
        // Called from StalenessCheckLoop alongside the existing scan-churn line.
        public static void DumpAndReset()
        {
            if (!Enabled) return;
            var sb = new StringBuilder("[CMM PERF] Phases:");
            bool any = false;
            for (int i = 0; i < N; i++)
            {
                if (_count[i] == 0) continue;
                any = true;
                double allocKB = _allocB[i] / 1024.0;
                double allocPerTick = _allocB[i] / (double)_count[i];
                sb.Append($" {Names[i]}(n={_count[i]} avg={_totMs[i] / _count[i]:F3} max={_maxMs[i]:F3}" +
                          $" alloc={allocKB:F1}KB/{allocPerTick:F0}B)");
                _count[i] = 0; _totMs[i] = 0; _maxMs[i] = 0; _allocB[i] = 0;
            }
            if (_selfFrames > 0)
            {
                any = true;
                sb.Append($" Self(frames={_selfFrames} avg={_selfTotMs / _selfFrames:F3} max={_selfMaxMs:F2} [{KeyName(_selfMaxKey)}])");
                _selfFrames = 0; _selfTotMs = 0; _selfMaxMs = 0; _selfMaxKey = null;
            }
            for (int k = 0; k < (int)IoKind.COUNT; k++)
            {
                long jobs = System.Threading.Interlocked.Exchange(ref _ioJobs[k], 0);
                long tk   = System.Threading.Interlocked.Exchange(ref _ioTicks[k], 0);
                long mx   = System.Threading.Interlocked.Exchange(ref _ioMax[k], 0);
                if (jobs == 0) continue;
                any = true;
                sb.Append($" Bg{IoNames[k]}(n={jobs} avg={tk * TicksToMs / jobs:F1} max={mx * TicksToMs:F1})");
            }
            if (_txInterrupted > 0)
            {
                any = true;
                sb.Append($" Interrupted(n={_txInterrupted})");
                _txInterrupted = 0;
            }
            if (any) Log.Out(sb.ToString());
        }
    }
}
