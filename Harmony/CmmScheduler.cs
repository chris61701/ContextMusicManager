using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // CMM SCHEDULER
    // ─────────────────────────────────────────────────────────────────────────
    // A single CMM-owned background thread that runs the PACING and the pure /
    // IO-bound BODIES of periodic work that used to live on Unity's frame loop as
    // coroutines (`yield return new WaitForSeconds(...)`). The point of moving off
    // the frame loop is that this thread keeps ticking even when Unity's main thread
    // is frozen (world load/unload), so the music engine's housekeeping survives the
    // freeze instead of stalling with it.
    //
    // HARD RULE: a task body running here is on a BACKGROUND thread. It may only
    // touch data that is safe to read/write off the main thread (volatiles, its own
    // locals, thread-safe APIs). Anything that observes Unity game state or mutates
    // CMM's lock-free single-threaded structures (contexts / cacheMap / _contextStack
    // / the LoudnessStore dictionaries) MUST be marshalled back to the main thread via
    // MainThreadDispatcher — Post for fire-and-forget, Send when the body needs the
    // result before it can continue. See MainThreadDispatcher for the trade-offs.
    //
    // Tasks may be registered before OR after Start(): registration takes the lock and
    // the run loop re-reads the task list under the same lock each tick, so a late
    // Every() is picked up on the next wake. Bodies run OUTSIDE the lock so a blocking
    // Send (or a slow file write) never stalls registration or another task's pacing.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class CmmScheduler
    {
        private sealed class PeriodicTask
        {
            public string Name;
            public double IntervalSec;
            public double NextDueSec;     // monotonic-clock seconds (see _clock)
            public Action Body;
        }

        private static readonly object _lock = new object();
        private static readonly List<PeriodicTask> _tasks = new List<PeriodicTask>();
        // Reused per-tick "due now" list — only ever touched by the scheduler thread,
        // so it needs no lock and allocates nothing after warm-up.
        private static readonly List<PeriodicTask> _due = new List<PeriodicTask>(8);

        private static Thread _thread;
        private static volatile bool _running;

        // A monotonic wall clock independent of Unity's Time / timeScale — this thread
        // must pace in real seconds even while the game is paused or frozen.
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static double Now => _clock.Elapsed.TotalSeconds;

        // Register a recurring task. `firstDelaySec` < 0 staggers the first run by one
        // full interval (the usual debounce behaviour); pass 0 to run almost immediately.
        public static void Every(double intervalSec, Action body, string name, double firstDelaySec = -1)
        {
            if (body == null || intervalSec <= 0) return;
            var t = new PeriodicTask
            {
                Name = name,
                IntervalSec = intervalSec,
                Body = body,
                NextDueSec = Now + (firstDelaySec >= 0 ? firstDelaySec : intervalSec),
            };
            lock (_lock) _tasks.Add(t);
        }

        // Idempotent: starts the scheduler thread once. Safe to call after some tasks
        // are already registered, or to call repeatedly (later calls are no-ops).
        public static void Start()
        {
            lock (_lock)
            {
                if (_thread != null) return;
                _running = true;
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "CMM-Scheduler",
                    Priority = ThreadPriority.BelowNormal,
                };
                _thread.Start();
            }
            Log.Out("[CMM] CmmScheduler started (off-frame-loop control thread).");
        }

        public static void Stop() => _running = false;

        private static void Run()
        {
            while (_running)
            {
                double now = Now;

                // Collect everything due under the lock, but run the bodies OUTSIDE it.
                _due.Clear();
                lock (_lock)
                {
                    for (int i = 0; i < _tasks.Count; i++)
                        if (now >= _tasks[i].NextDueSec) _due.Add(_tasks[i]);
                }

                for (int i = 0; i < _due.Count; i++)
                {
                    PeriodicTask t = _due[i];
                    try { t.Body(); }
                    catch (Exception ex) { Log.Error($"[CMM] Scheduler task '{t.Name}' threw: {ex.Message}"); }
                    // Re-base off the CURRENT time so a long body (slow IO / a blocked
                    // Send) doesn't immediately re-fire — fixed delay, not fixed rate.
                    t.NextDueSec = Now + t.IntervalSec;
                }

                // Sleep until the next task is due, capped so Stop() stays responsive and
                // a freshly-registered task can't wait more than ~1s for its first run.
                double nextWake = Now + 1.0;
                lock (_lock)
                {
                    for (int i = 0; i < _tasks.Count; i++)
                        if (_tasks[i].NextDueSec < nextWake) nextWake = _tasks[i].NextDueSec;
                }
                double sleep = nextWake - Now;
                if (sleep < 0.005) sleep = 0.005;
                if (sleep > 1.0) sleep = 1.0;
                Thread.Sleep((int)(sleep * 1000));
            }
        }
    }
}
