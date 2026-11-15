using System;
using System.Collections.Generic;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // TRACK COOLDOWN — a context's silence between one track ENDING and the next
    // one starting (contexts.xml `cooldown`, `cooldownreset`).
    //
    // Not to be confused with `reentry` (formerly `cool`), which is a lockout on a
    // context firing again after you LEAVE it. This is the opposite situation: you
    // stay where you are, a track finishes, and the context rests before it plays
    // another. Ambient, Home and POI music ship with it on, so exploration has
    // room to breathe instead of being wall-to-wall.
    //
    // WHY IT IS ITS OWN CLASS. Every start path in CMM assumed that a context which
    // is current and has files should be audible: the convergence net, the file
    // watcher's hot-swap, a stinger returning to the bed, the radio handing the
    // music back, a context switch. A deliberate silence breaks that assumption in
    // every one of them. So the rules live HERE, in one place with no UnityEngine,
    // and the game side only reports events and asks questions at its choke points.
    // tools/CmmCooldownSim links this file and drives it through the edge cases
    // (the same arrangement as ResumePolicy / CmmResumeTest).
    //
    // THE RULES (agreed 2026-11-06):
    //   • cooldown: 0 = off, -1 = the settings.xml default, > 0 = seconds.
    //   • The rest starts when a track ENDS — genuinely ends, at its audible end.
    //     A voice that dies early (decode error, device reset) is not an end: the
    //     next track starts at once, as it always did.
    //   • A rest is per CONTEXT. Night falling, a biome change, or another context
    //     outranking this one does not break or move it.
    //   • The clock keeps running while another context plays over it; when you
    //     come back you only wait out what is left.
    //   • The clock stops while the game is paused.
    //   • Next/Prev ends the rest and plays now.
    //   • Death, world unload: every rest is cleared.
    //   • cooldownreset (default true; Home and Ambient ship false): the rest clears when the context leaves the
    //     active stack (you walk out of the POI, change biome), or — for POI music,
    //     whose rest the caller ties to a POI id — when you move to a different POI
    //     while it rests. Being outranked while still inside (combat) is not
    //     leaving. With a fallback, the context whose music was actually playing
    //     is the one cleared.
    //   • cooldownyield (Home ships it): while the context rests, music already
    //     playing beneath it carries on instead of being faded out, and a track of
    //     it that something interrupted DURING the rest resumes afterwards; when the
    //     rest ends the context crossfades in. It never starts a new track. Lives in
    //     the resolver (ContextMusicManager.RestYieldTarget), not here.
    //   • The rest is derived from the end time plus the CURRENT cooldown value,
    //     so a hot-reload of contexts.xml or settings.xml re-times it live.
    //
    // MAIN-THREAD ONLY, except IsNaturalEnd, which is a pure function.
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class TrackCooldown
    {
        private sealed class Rest
        {
            public double EndedAt;   // rest-clock seconds of the track's AUDIBLE end
            public string PoiId;     // the POI instance the rest began in (null/"None" = none)
        }

        // Inputs, supplied by the caller so the offline harness can drive them.
        //   RawClock     — monotonic seconds (CmmClock.Now in game, a sim clock offline).
        //   CooldownOf   — EFFECTIVE seconds for a context right now (0 = off). Read on
        //                  every query, which is what makes hot-reload re-time a rest.
        //   ResetsOnLeave— the context's cooldownreset flag.
        private readonly Func<double> _raw;
        private readonly Func<string, float> _cooldownOf;
        private readonly Func<string, bool> _resetsOnLeave;

        private readonly Dictionary<string, Rest> _rests =
            new Dictionary<string, Rest>(StringComparer.OrdinalIgnoreCase);

        // The context holding the music layer in silence right now: it is current, it
        // was asked to play, and the gate refused because it is resting. Valid only for
        // the transition it was entered under — any real context change supersedes it.
        private string _waiting;
        private int _waitingTid;

        // Pause bookkeeping for the rest clock.
        private double _pausedTotal;
        private double _pausedAt = -1;

        internal TrackCooldown(Func<double> rawClock, Func<string, float> cooldownOf,
                               Func<string, bool> resetsOnLeave)
        {
            _raw = rawClock;
            _cooldownOf = cooldownOf;
            _resetsOnLeave = resetsOnLeave;
        }

        // ── Value semantics ───────────────────────────────────────────────────

        // The attribute as written → seconds. 0 = off; any negative = the default
        // (the loader also folds -2, -3 … to -1 so the file stays readable); > 0 as-is.
        internal static float Effective(float attribute, float defaultSeconds)
        {
            if (attribute > 0f) return attribute;
            if (attribute < 0f) return defaultSeconds > 0f ? defaultSeconds : 0f;
            return 0f;
        }

        // ── The clock ─────────────────────────────────────────────────────────

        // Seconds on a clock that does not move while the game is paused.
        internal double Now
        {
            get
            {
                double raw = _pausedAt >= 0 ? _pausedAt : _raw();
                return raw - _pausedTotal;
            }
        }

        internal bool Paused => _pausedAt >= 0;

        internal void SetPaused(bool paused)
        {
            if (paused)
            {
                if (_pausedAt < 0) _pausedAt = _raw();
            }
            else if (_pausedAt >= 0)
            {
                _pausedTotal += _raw() - _pausedAt;
                _pausedAt = -1;
            }
        }

        // ── Queries ───────────────────────────────────────────────────────────

        internal bool IsResting(string ctx) => Remaining(ctx) > 0;

        // Seconds of silence left for `ctx`; 0 when it is not resting.
        internal double Remaining(string ctx)
        {
            if (ctx == null || !_rests.TryGetValue(ctx, out Rest r)) return 0;
            float cd = _cooldownOf(ctx);
            if (cd <= 0f) return 0;
            double left = r.EndedAt + cd - Now;
            return left > 0 ? left : 0;
        }

        // Whether the music layer is being held silent for `ctx` under `tid`.
        internal bool IsWaitingOn(string ctx, int tid) =>
            _waiting != null && _waitingTid == tid
            && string.Equals(_waiting, ctx, StringComparison.OrdinalIgnoreCase);

        internal string Waiting => _waiting;

        // Every context with a rest on record, and what is left of it (for the HUD).
        internal IEnumerable<KeyValuePair<string, double>> Snapshot()
        {
            foreach (var kv in _rests)
            {
                double left = Remaining(kv.Key);
                if (left > 0) yield return new KeyValuePair<string, double>(kv.Key, left);
            }
        }

        // ── Events ────────────────────────────────────────────────────────────

        // A track in `ctx` reached its end. `natural` = it played out (see IsNaturalEnd);
        // `latency` = seconds of it still queued in the output when the mixer let go of
        // it, so the silence is timed from what the player HEARS stop. Returns true when
        // a rest began (the caller then holds the layer silent instead of advancing).
        internal bool TrackEnded(string ctx, bool natural, string poiId, double latency)
        {
            if (!natural || ctx == null) return false;
            if (_cooldownOf(ctx) <= 0f) return false;
            _rests[ctx] = new Rest
            {
                EndedAt = Now + (latency > 0 ? latency : 0),
                PoiId = poiId,
            };
            return true;
        }

        // A track started in `ctx` — its rest, if any, is spent.
        internal void TrackStarted(string ctx)
        {
            if (ctx == null) return;
            _rests.Remove(ctx);
            if (string.Equals(_waiting, ctx, StringComparison.OrdinalIgnoreCase)) _waiting = null;
        }

        // The gate refused `ctx` because it rests; it now holds the layer silent.
        internal void BeginWait(string ctx, int tid)
        {
            _waiting = ctx;
            _waitingTid = tid;
        }

        // Per-tick: the context whose rest just ran out and which should start now, or
        // null. A wait that a real context change superseded is dropped here — it is
        // derived from (current context, transition id), never latched past them.
        internal string DueToStart(string currentCtx, int currentTid)
        {
            if (_waiting == null) return null;
            if (_waitingTid != currentTid
                || !string.Equals(_waiting, currentCtx, StringComparison.OrdinalIgnoreCase))
            {
                _waiting = null;
                return null;
            }
            if (Paused) return null;
            return IsResting(_waiting) ? null : _waiting;
        }

        internal void EndWait() => _waiting = null;

        // `ctx` left the active stack. If it resets on leave, clear its own rest and the
        // rest of whatever it was resolving to (the context whose music was playing).
        // Returns the names actually cleared, for the log.
        internal List<string> Evicted(string ctx, string resolvedTo)
        {
            var cleared = new List<string>();
            if (ctx == null || !_resetsOnLeave(ctx)) return cleared;
            if (_rests.Remove(ctx)) cleared.Add(ctx);
            if (!string.IsNullOrEmpty(resolvedTo) && _rests.Remove(resolvedTo)) cleared.Add(resolvedTo);
            return cleared;
        }

        // Per-tick: a rest that began inside one POI is cleared once the player is no
        // longer in that POI, for contexts that reset on leave. This is what catches
        // walking straight from one POI into an adjacent one of the same tier, where the
        // context name never leaves the stack.
        internal List<string> PoiTick(string currentPoiId)
        {
            List<string> cleared = null;
            foreach (var kv in _rests)
            {
                string pid = kv.Value.PoiId;
                if (string.IsNullOrEmpty(pid) || pid == "None") continue;
                if (string.Equals(pid, currentPoiId, StringComparison.Ordinal)) continue;
                if (!_resetsOnLeave(kv.Key)) continue;
                (cleared ?? (cleared = new List<string>())).Add(kv.Key);
            }
            if (cleared != null) foreach (string c in cleared) _rests.Remove(c);
            return cleared;
        }

        // Next/Prev pressed: the player asked for music, so the rest is over.
        internal void Override(string ctx)
        {
            if (ctx == null) return;
            _rests.Remove(ctx);
            if (string.Equals(_waiting, ctx, StringComparison.OrdinalIgnoreCase)) _waiting = null;
        }

        // The context's source changed (a different folder or playlist) — it is not the
        // music that just ended any more.
        internal void Clear(string ctx)
        {
            if (ctx == null) return;
            _rests.Remove(ctx);
        }

        // Death, world unload.
        internal void ClearAll()
        {
            _rests.Clear();
            _waiting = null;
        }

        // ── Natural-end classification (pure, any thread) ─────────────────────

        // Whether a voice that has disappeared from the mixer PLAYED OUT, judged from the
        // last remaining-seconds observed while it was still there.
        //   NaN  → never observed alive: it failed, it did not end.
        //   < 0  → unknown length (raw ADTS and the like): EOF is the only end it has.
        //   ≤ slack → it was at its end.
        //   otherwise → it was retired early (decode error, device reset).
        internal static bool IsNaturalEnd(double lastRemaining, double slack)
        {
            if (double.IsNaN(lastRemaining)) return false;
            if (lastRemaining < 0) return true;
            return lastRemaining <= slack;
        }
    }
}
