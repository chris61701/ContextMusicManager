using System;
using System.Collections.Generic;
using System.Threading;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // MAIN-THREAD DISPATCHER
    // ─────────────────────────────────────────────────────────────────────────
    // The marshalling boundary that lets work running on CmmScheduler (or any other
    // background thread) reach back onto Unity's main thread for the one thing it must
    // do there: observe game state, or mutate CMM's lock-free single-threaded structures
    // (contexts / cacheMap / _contextStack / the LoudnessStore dictionaries). Queued
    // closures are drained once per frame by the pump (CoRoutine.Update → Drain), so they
    // run on the main thread in submission order.
    //
    // Two primitives:
    //   • Post(action)  — fire-and-forget. Returns immediately; `action` runs on the next
    //                     Drain. Use this whenever the background task doesn't need the
    //                     result to keep going — it never blocks, so it survives a frozen
    //                     main thread (the work simply lands once the freeze clears).
    //   • Send(action)  — runs `action` on the main thread and BLOCKS the caller until it
    //                     completes (and re-throws anything it threw). Use only for a short
    //                     critical section whose result the caller needs NOW — e.g. taking a
    //                     consistent snapshot of a main-thread-only dictionary before doing
    //                     slow IO with it off-thread. DANGER: Send blocks the calling thread
    //                     for as long as the main thread is busy or frozen, so it forfeits
    //                     freeze-survival for that call. Prefer Post + thread-safe reads
    //                     where staying alive through a freeze matters.
    //
    // Drain() and the pump are the only main-thread parts; everything else here is callable
    // from any thread. A self-call (Send while already on the main thread) runs inline to
    // avoid deadlocking against a Drain that will never come until the caller returns.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class MainThreadDispatcher
    {
        private static readonly object _lock = new object();
        private static readonly Queue<Action> _queue = new Queue<Action>();
        private static volatile int _mainThreadId = -1;

        // Record which managed thread is "the main thread". Called once from Init (which
        // runs on it); Drain re-affirms it so the id is correct even if Init's call is ever
        // removed. Send uses it to detect a same-thread call and avoid self-deadlock.
        public static void CaptureMainThread() => _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        private static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        public static void Post(Action action)
        {
            if (action == null) return;
            lock (_lock) _queue.Enqueue(action);
        }

        public static void Send(Action action)
        {
            if (action == null) return;
            // Already on the main thread: run inline. Queueing here would block forever —
            // the Drain that would run it can't start until this call returns.
            if (OnMainThread) { action(); return; }

            using (var done = new ManualResetEvent(false))
            {
                Exception captured = null;
                lock (_lock)
                {
                    _queue.Enqueue(() =>
                    {
                        try { action(); }
                        catch (Exception ex) { captured = ex; }
                        finally { done.Set(); }
                    });
                }
                done.WaitOne();
                if (captured != null)
                    throw new Exception("MainThreadDispatcher.Send: dispatched action threw", captured);
            }
        }

        // MAIN THREAD ONLY (the per-frame pump). Runs every queued action in order. Each is
        // isolated so one throwing can't abort the rest or kill the pump. Snapshots the count
        // under the lock and dequeues one at a time, so an action that itself Posts more work
        // simply defers that work to the next frame rather than looping here unboundedly.
        public static void Drain()
        {
            if (_mainThreadId == -1) _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            int budget;
            lock (_lock) budget = _queue.Count;

            while (budget-- > 0)
            {
                Action a;
                lock (_lock)
                {
                    if (_queue.Count == 0) break;
                    a = _queue.Dequeue();
                }
                try { a(); }
                catch (Exception ex) { Log.Error($"[CMM] Main-thread dispatched action threw: {ex.Message}"); }
            }
        }
    }
}
