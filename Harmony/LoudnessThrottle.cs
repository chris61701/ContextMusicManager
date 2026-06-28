
using System;
using System.Diagnostics;
using System.Threading;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // ADAPTIVE FRAME-TIME THROTTLE (background loudness sweep)
    // ─────────────────────────────────────────────────────────────────────────
    // The sweep decodes on a BelowNormal worker thread, but a single file is
    // otherwise decoded flat-out — enough CPU / memory-bandwidth pressure to
    // stretch the occasional frame on the (main) render thread, which reads as a
    // stutter. A FIXED duty cycle would smooth that but either wastes headroom
    // when the game is idle or still hitches when it's busy.
    //
    // Instead this closes the loop: the main thread reports its frame time every
    // frame (SampleFrame); the worker consults that smoothed value between decode
    // chunks (ThrottleChunk) and sleeps only when frames are running over budget.
    // Healthy frames ⇒ no sleep, so a measurement finishes as fast as the spare
    // CPU allows; degraded frames ⇒ progressively more sleep until they recover.
    // Self-tuning, so it adapts to whatever framerate the machine actually holds
    // rather than assuming a target rate.
    //
    // THREADING: SampleFrame is main-thread only; ThrottleChunk is worker-thread
    // only. They communicate through a single volatile float (the smoothed frame
    // time) and a timestamp — no locks. The worker only ever reads, the main
    // thread only ever writes, so a torn read just costs one slightly-stale
    // throttle decision, which is harmless.
    // ─────────────────────────────────────────────────────────────────────────
    public static class LoudnessThrottle
    {
        // Smoothed main-thread frame time (ms) and when it was last written.
        private static volatile float _frameMs;
        private static long _lastSampleTicks;

        // EMA weight: low enough that a single hitch doesn't whipsaw the worker,
        // high enough that a sustained drop is reflected within a handful of frames.
        private const float Alpha = 0.15f;

        // If the worker hasn't seen a fresh frame sample in this long, the main
        // thread isn't ticking (loading / hung) — fall back to a mild fixed duty
        // so we neither stall the sweep forever nor hammer a struggling machine.
        private const float StaleAfterMs = 500f;

        // ── Main thread: report the frame just rendered ────────────────────────
        public static void SampleFrame(float unscaledDeltaSeconds)
        {
            float ms = unscaledDeltaSeconds * 1000f;
            // Clamp pathological gaps (alt-tab, breakpoints, first frame) so they
            // don't poison the average and pin the throttle wide open afterwards.
            if (ms > 1000f) ms = 1000f;
            else if (ms < 0f) ms = 0f;

            float prev = _frameMs;
            _frameMs = prev <= 0f ? ms : prev + Alpha * (ms - prev);
            _lastSampleTicks = Stopwatch.GetTimestamp();
        }

        // ── Worker thread: back off after a decoded chunk ──────────────────────
        // `workMs` is the wall-clock the just-finished chunk (decode + analyze)
        // took; the sleep is scaled relative to it so the DUTY CYCLE bends with
        // frame pressure rather than an absolute rate — cheap formats sleep less
        // in absolute terms, costly ones (Opus) more, for the same smoothness.
        // Returns the milliseconds actually slept (for diagnostics).
        public static int ThrottleChunk(float workMs)
        {
            if (!CMMConfig.LoudnessAdaptiveThrottle) return 0;

            float frameMs = _frameMs;
            float ageMs = (Stopwatch.GetTimestamp() - _lastSampleTicks) * 1000f / Stopwatch.Frequency;

            // No data yet / main thread not ticking: mild 1:1 duty (sleep ≈ work).
            if (frameMs <= 0f || ageMs > StaleAfterMs)
                return Sleep((int)Math.Min(workMs, CMMConfig.LoudnessThrottleMaxSleepMs));

            float over = frameMs - CMMConfig.LoudnessThrottleTargetFrameMs;
            if (over <= 0f) return 0; // frames healthy → run flat out

            // Sleep ratio ramps with how far over budget we are, capped so a bad
            // spell can't park the worker indefinitely.
            float ratio = Math.Min(
                over / CMMConfig.LoudnessThrottleTargetFrameMs * CMMConfig.LoudnessThrottleGain,
                CMMConfig.LoudnessThrottleMaxRatio);
            int sleepMs = (int)Math.Round(Math.Min(workMs * ratio, CMMConfig.LoudnessThrottleMaxSleepMs));
            return Sleep(sleepMs);
        }

        private static int Sleep(int ms)
        {
            if (ms <= 0) return 0;
            Thread.Sleep(ms);
            return ms;
        }

        // Smoothed frame time the throttle is currently reacting to (for logging).
        public static float SmoothedFrameMs => _frameMs;
    }
}
