using System;
using System.Collections.Generic;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // PATCH-BODY EXCEPTION GUARD
    // ─────────────────────────────────────────────────────────────────────────
    // Every Harmony patch body runs INSIDE a vanilla game method. An exception
    // escaping a patch propagates into that method — on lifecycle seams (spawn,
    // world-load, save/cleanup) that stalls the game itself: an escape from the
    // AfterPlayerRespawn postfix leaves an MP client stuck on the loading screen
    // ("freeze on spawn"). Patch bodies are therefore wrapped in try/catch that
    // routes here. Success path: zero behavior change (one try/catch frame, no
    // allocation — call sites use inline try/catch + a Body method, never a
    // capturing lambda). Failure path: swallow, and log with per-tag rate
    // limiting so a per-frame patch that starts failing can't flood the log.
    //
    // Report must be callable from ANY thread and must itself never throw (it
    // runs inside catch blocks): timekeeping uses Environment.TickCount (not
    // main-thread-only UnityEngine.Time), state is lock-protected, and the whole
    // body is its own last-resort try/catch.
    internal static class CmmGuard
    {
        const int RelogMs = 30_000;   // full stack at most this often per tag
        static readonly object _lock = new object();
        static readonly Dictionary<string, int> _lastLog = new Dictionary<string, int>();
        static readonly Dictionary<string, int> _suppressed = new Dictionary<string, int>();

        public static void Report(string tag, Exception e)
        {
            try
            {
                int missed;
                lock (_lock)
                {
                    int now = Environment.TickCount;
                    int last;
                    bool logged = _lastLog.TryGetValue(tag, out last);
                    _suppressed.TryGetValue(tag, out missed);
                    // unchecked subtraction is wrap-safe across TickCount rollover
                    if (logged && unchecked(now - last) < RelogMs)
                    {
                        _suppressed[tag] = missed + 1;
                        return;
                    }
                    _lastLog[tag] = now;
                    _suppressed[tag] = 0;
                }
                Log.Error($"[CMM] Suppressed exception in {tag}" +
                          (missed > 0 ? $" ({missed} earlier occurrences suppressed)" : "") +
                          $": {e}");
            }
            catch { /* the guard itself must never throw */ }
        }
    }
}
