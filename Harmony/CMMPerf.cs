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
            ScanResolve,     // ScriptHost.Resolve — Lua marshal + decision
            ScanGate,        // change-detection / stack rebuild gate
            TxChannelSetup,  // PlayFromCache channel selection (synchronous)
            TxClipCommit,    // PlayFromCache clip assign + Play() + time-set (DSP flush)
            COUNT
        }

        static readonly string[] Names =
        {
            "ScanPoll", "ScanResolve", "ScanGate", "TxChannelSetup", "TxClipCommit"
        };

        const int N = (int)Phase.COUNT;
        static readonly int[]    _count  = new int[N];
        static readonly double[] _totMs  = new double[N];
        static readonly double[] _maxMs  = new double[N];

        static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        // ── Scoped span timer ──────────────────────────────────────────────────
        public readonly struct Scope : IDisposable
        {
            readonly Phase _phase;
            readonly long  _start;
            internal Scope(Phase phase) { _phase = phase; _start = Stopwatch.GetTimestamp(); }
            public void Dispose() => Record(_phase, Stopwatch.GetTimestamp() - _start);
        }

        public static Scope Measure(Phase phase) => new Scope(phase);

        static void Record(Phase phase, long elapsedTicks)
        {
            if (!Enabled) return;
            double ms = elapsedTicks * TicksToMs;
            int i = (int)phase;
            _count[i]++;
            _totMs[i] += ms;
            if (ms > _maxMs[i]) _maxMs[i] = ms;

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

        public static void BeginTransition(string contextName)
        {
            if (!Enabled) return;
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
                sb.Append($" {Names[i]}(n={_count[i]} avg={_totMs[i] / _count[i]:F3} max={_maxMs[i]:F3})");
                _count[i] = 0; _totMs[i] = 0; _maxMs[i] = 0;
            }
            if (any) Log.Out(sb.ToString());
        }
    }
}
