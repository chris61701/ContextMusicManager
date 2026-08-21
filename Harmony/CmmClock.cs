using System.Diagnostics;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // A MONOTONIC CLOCK THAT ANY THREAD MAY READ.
    //
    // `UnityEngine.Time.realtimeSinceStartup` is a main-thread-only API. Reading it
    // from a background thread is a managed-to-native call into UnityPlayer, and
    // while it usually appears to work — it is only reading a float — it is not
    // safe, and it is fatal during shutdown: the player tears its module down while
    // CMM's scheduler threads are still ticking, so the call lands in freed state
    // and the process dies with a native stack ending in
    // `Time_Get_Custom_PropRealtimeSinceStartup`.
    //
    // That is exactly what "enter the menu, quit, crash before exiting" was: the
    // occlusion field's 0.02 s scheduler tick reading Unity's clock 50 times a
    // second right through teardown.
    //
    // Stopwatch gives the same thing — monotonic seconds since startup — with no
    // Unity involvement, so it is safe from the scheduler lanes, the mixer thread
    // and the ThreadPool alike. Same float type and therefore the same precision
    // behaviour as the API it replaces, so no timing constant needs retuning.
    //
    // RULE: any timestamp that a scheduler-thread body can read or write must come
    // from here. Two timestamps compared against each other must come from the SAME
    // clock, so converting a reader means converting its writer as well.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class CmmClock
    {
        private static readonly Stopwatch _sw = Stopwatch.StartNew();

        internal static float Now => (float)_sw.Elapsed.TotalSeconds;
    }
}
