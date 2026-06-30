using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

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
            COUNT
        }

        static readonly string[] Names =
        {
            "ScanPoll", "ScanResolve", "ScanGate", "ScanMarshal", "ScanLuaCall"
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

            if (ms >= CMMConfig.PerfSpikeMs)
                Log.Out($"[CMM PERF] SPIKE {Names[i]} {ms:F2} ms");
        }

        // ── Per-transition wall + GC correlation ───────────────────────────────
        // Begin at TriggerMusic (the moment a switch is decided); End when
        // PlayFromCache finishes committing. Wall time spans yields on purpose, so
        // it captures total transition latency including background-decode waits.
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
            // (TriggerMusic StopCoroutine'd its PlayFromCache before EndTransition ran).
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

        // ── Audio-thread PCM callback probe ────────────────────────────────────
        // The default playback path is a STREAMING AudioClip whose PCMReaderCallback
        // runs on Unity's AUDIO thread and does on-demand decode + disk paging from the
        // music library (an exFAT SD card in the field). If a callback can't keep up,
        // audio glitches AND the contended CPU starves the render thread on a core-light
        // device — a frame stutter our other probes can't see (it isn't a GC, a measured
        // span, or a transition). The audio thread must stay cheap and must NOT call
        // Unity's Log (not thread-safe off the main thread), so this only accumulates via
        // Interlocked; the main-thread DumpAndReset reports max/avg/slow-count. Feed it
        // the ReadFloats duration in Stopwatch ticks from the callback.
        static long _pcmCount, _pcmTotTicks, _pcmMaxTicks, _pcmSlowCount;

        public static void PcmCallback(long elapsedTicks)
        {
            if (!Enabled) return;
            Interlocked.Increment(ref _pcmCount);
            Interlocked.Add(ref _pcmTotTicks, elapsedTicks);
            // Racy max — acceptable for a diagnostic; avoids a CAS loop on the audio thread.
            if (elapsedTicks > Interlocked.Read(ref _pcmMaxTicks)) _pcmMaxTicks = elapsedTicks;
            if (elapsedTicks * TicksToMs >= CMMConfig.PerfSpikeMs)
                Interlocked.Increment(ref _pcmSlowCount);
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
                if (frameMs > _burstWorstMs) _burstWorstMs = frameMs;
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
                    $"GC gen0={_burstG0} gen1={_burstG1} gen2={_burstG2}, tx={tx}, coroMax={_burstMaxCoro}");
            _burstFrames = 0; _burstWorstMs = 0; _burstSpanMs = 0; _burstBaseSum = 0;
            _burstG0 = _burstG1 = _burstG2 = 0; _burstMaxCoro = 0;
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
            if (_txInterrupted > 0)
            {
                any = true;
                sb.Append($" Interrupted(n={_txInterrupted})");
                _txInterrupted = 0;
            }
            // Audio-thread PCM callback stats (snapshot + reset). A high max/slow count
            // means the streaming decode/disk read is stalling the audio thread — the
            // CPU-contention path that starves the render thread on a core-light device.
            long pc = Interlocked.Exchange(ref _pcmCount, 0);
            if (pc > 0)
            {
                long tot = Interlocked.Exchange(ref _pcmTotTicks, 0);
                long mx  = Interlocked.Exchange(ref _pcmMaxTicks, 0);
                long slow = Interlocked.Exchange(ref _pcmSlowCount, 0);
                any = true;
                sb.Append($" PCMcb(n={pc} avg={(tot * TicksToMs) / pc:F3} max={mx * TicksToMs:F3} slow={slow})");
            }
            if (any) Log.Out(sb.ToString());
        }
    }
}
