using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // CMM SCHEDULER
    // ─────────────────────────────────────────────────────────────────────────
    // CMM-owned background threads that run the PACING and the pure / IO-bound
    // BODIES of periodic work that used to live on Unity's frame loop as
    // coroutines (`yield return new WaitForSeconds(...)`). The point of moving off
    // the frame loop is that these threads keep ticking even when Unity's main thread
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
    // TWO LANES, one thread each. A blocking Send parks the CALLING thread until the
    // main thread pumps — during a main-thread freeze that is the whole freeze. With a
    // single thread, FileWatchTick's or LoudnessSaveTick's Send therefore stalled
    // native-advance too, defeating advance-through-freeze exactly when it mattered.
    //   • Housekeeping — bodies MAY block on the main thread (Send). During a freeze
    //     this lane parks on the first Send and resumes when the main thread does.
    //   • Critical — bodies must NEVER block on the main thread (Post only). Carries
    //     the track-end advance poll, so the next track starts mid-freeze.
    //
    // Tasks may be registered before OR after Start(): registration takes the lane
    // lock and the run loop re-reads the task list under the same lock each tick, so a
    // late Every() is picked up on the next wake. Bodies run OUTSIDE the lock so a
    // blocking Send (or a slow file write) never stalls registration or another task's
    // pacing within its lane.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class CmmScheduler
    {
        internal enum Lane { Housekeeping = 0, Critical = 1 }

        private sealed class PeriodicTask
        {
            public string Name;
            public double IntervalSec;
            public double NextDueSec;     // monotonic-clock seconds (see _clock)
            public Action Body;
        }

        // One paced run loop + thread. All state is per-lane so the lanes never
        // contend: a body blocking in one lane cannot delay the other's pacing.
        private sealed class LaneRunner
        {
            private readonly object _lock = new object();
            private readonly List<PeriodicTask> _tasks = new List<PeriodicTask>();
            // Reused per-tick "due now" list — only ever touched by this lane's thread,
            // so it needs no lock and allocates nothing after warm-up.
            private readonly List<PeriodicTask> _due = new List<PeriodicTask>(8);
            private readonly string _threadName;
            private Thread _thread;

            public LaneRunner(string threadName) { _threadName = threadName; }

            public void Add(PeriodicTask t)
            {
                lock (_lock) _tasks.Add(t);
                // A task registered after Start() must still get a thread to run on.
                if (_running) EnsureThread();
            }

            public bool HasTasks { get { lock (_lock) return _tasks.Count > 0; } }

            // Idempotent: starts this lane's thread once.
            public void EnsureThread()
            {
                lock (_lock)
                {
                    if (_thread != null) return;
                    _thread = new Thread(Run)
                    {
                        IsBackground = true,
                        Name = _threadName,
                        Priority = ThreadPriority.BelowNormal,
                    };
                    _thread.Start();
                }
            }

            private void Run()
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

        private static readonly LaneRunner _housekeeping = new LaneRunner("CMM-Scheduler");
        private static readonly LaneRunner _critical = new LaneRunner("CMM-Scheduler-Advance");
        private static volatile bool _running;
        private static int _startedOnce;

        // A monotonic wall clock independent of Unity's Time / timeScale — these threads
        // must pace in real seconds even while the game is paused or frozen.
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static double Now => _clock.Elapsed.TotalSeconds;

        // Register a recurring task. `firstDelaySec` < 0 staggers the first run by one
        // full interval (the usual debounce behaviour); pass 0 to run almost immediately.
        // Housekeeping is the default lane; only a body that never blocks on the main
        // thread (Post-only, see the lane contract above) may ask for Lane.Critical.
        public static void Every(double intervalSec, Action body, string name,
            double firstDelaySec = -1, Lane lane = Lane.Housekeeping)
        {
            if (body == null || intervalSec <= 0) return;
            var t = new PeriodicTask
            {
                Name = name,
                IntervalSec = intervalSec,
                Body = body,
                NextDueSec = Now + (firstDelaySec >= 0 ? firstDelaySec : intervalSec),
            };
            (lane == Lane.Critical ? _critical : _housekeeping).Add(t);
        }

        // Idempotent: starts a thread for each lane that has tasks (a lane that gains
        // its first task later gets its thread from Add). Safe to call repeatedly.
        public static void Start()
        {
            _running = true;
            if (_housekeeping.HasTasks) _housekeeping.EnsureThread();
            if (_critical.HasTasks) _critical.EnsureThread();
            if (Interlocked.Exchange(ref _startedOnce, 1) == 0)
                Log.Out("[CMM] CmmScheduler started (off-frame-loop control threads: housekeeping + advance).");
        }

        public static void Stop() => _running = false;
    }
}
