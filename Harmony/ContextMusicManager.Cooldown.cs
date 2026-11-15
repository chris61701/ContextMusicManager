using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // TRACK COOLDOWN — the game-side wiring. The rules are in TrackCooldown.cs; this
    // file only reports events to it and acts on its answers.
    //
    // Where it plugs in, and why each one:
    //   • StartNativeContext — THE gate. Every way music starts passes through it (a
    //     context switch, the radio handing back, a stinger returning, the file watcher's
    //     hot-swap, the convergence net, a failed-start retry), so a resting context is
    //     refused HERE, once, instead of at a dozen callers. The radio gate lives in the
    //     same place for the same reason.
    //   • Auto-advance (NativeAdvanceTick / NativeTrackLoop) — a context with a cooldown
    //     does NOT crossfade into its next track; it waits for the voice to end, then asks
    //     whether that was a real end (OnAwaitedTrackEnd).
    //   • ContextScanLoop — CooldownTick each pass (start a rest that ran out, POI-change
    //     resets), the eviction hook (cooldownreset), and the convergence net, which must
    //     not mistake a rest for a strand.
    //   • SkipTrack — Next/Prev overrides a rest.
    //   • Pause, death, world unload, a context's source changing.
    // ─────────────────────────────────────────────────────────────────────────
    public partial class ContextMusicManager
    {
        internal static readonly TrackCooldown _trackCooldown = new TrackCooldown(
            () => CmmClock.Now,
            EffectiveTrackCooldown,
            name => GetContext(name)?.CooldownReset ?? false);

        // Seconds of silence `name` takes between tracks right now. A loop never ends and a
        // stinger never advances, so neither can rest whatever the file says. The settings
        // master switch (TrackCooldownsEnabled=false) makes it 0 for everything; every rest
        // query goes through here, so flipping it re-times rests in progress live.
        internal static float EffectiveTrackCooldown(string name)
        {
            if (!CMMConfig.TrackCooldownsEnabled) return 0f;
            Contexts c = name != null ? GetContext(name) : null;
            if (c == null || c.Loop || c.PlayOnce || string.IsNullOrEmpty(c.Source)) return 0f;
            return TrackCooldown.Effective(c.TrackCooldown, CMMConfig.DefaultTrackCooldown);
        }

        // ── Scheduler-visible advance state ───────────────────────────────────
        // Set on the main thread when a track is armed (and refreshed every CooldownTick, so a
        // hot-reload that turns the cooldown on or off mid-track is honoured at its end): the
        // armed context rests between tracks, so the advance waits for the voice to END instead
        // of crossfading at the lead window.
        private static volatile bool _advanceAwaitsEnd;
        // Remaining seconds of the armed voice at the last poll that saw it alive. NaN = never
        // seen alive; negative = unknown length. Written by whichever path polls (scheduler
        // thread or the NativeTrackLoop coroutine), reset on arm. Read by IsNaturalEnd.
        private static double _advLastRemaining = double.NaN;

        // How close to its end a voice must have been last seen for its disappearance to count
        // as playing out. Two poll intervals plus the mixer's own chunking, floored at 1 s.
        private static double NaturalEndSlack => Math.Max(1.0, 3.0 * TrackEndPollInterval);

        private static void ArmAdvanceCooldown(string contextName, bool shouldAdvance)
        {
            Interlocked.Exchange(ref _advLastRemaining, double.NaN);
            _advanceAwaitsEnd = shouldAdvance && EffectiveTrackCooldown(contextName) > 0f;
        }

        // Poll body shared by both advance paths for a context that rests between tracks.
        // Returns true when the voice has gone and the caller should hand off to
        // OnAwaitedTrackEnd with `natural`; false while it is still playing.
        private static bool PollAwaitedEnd(bool got, bool active, double posSec, double lenSec, out bool natural)
        {
            natural = false;
            if (got && active)
            {
                double rem = lenSec > 0 ? Math.Max(0.0, lenSec - posSec) : -1.0;
                Interlocked.Exchange(ref _advLastRemaining, rem);
                return false;
            }
            natural = TrackCooldown.IsNaturalEnd(Interlocked.CompareExchange(ref _advLastRemaining, 0, 0),
                                                 NaturalEndSlack);
            return true;
        }

        // MAIN THREAD. The armed voice of `ctx` has gone. If it played out and `ctx` rests
        // between tracks, hold the music layer silent; otherwise advance at once, exactly as
        // before (an early death — decode error, device reset — is not an end).
        internal static void OnAwaitedTrackEnd(string ctx, int tid, bool natural)
        {
            _advancePending = false;
            if (tid != _transitionId
                || !string.Equals(_nativeActiveContext, ctx, StringComparison.OrdinalIgnoreCase))
                return;   // superseded by a real context change

            if (_trackCooldown.TrackEnded(ctx, natural, RestPoiId(ctx), OutputLatencySeconds()))
            {
                _restBeganAt[ctx] = Time.realtimeSinceStartup;   // cooldownyield's resume test
                if (cacheMap.TryGetValue(ctx, out ContextCache cache))
                {
                    cache.resumePosition = 0f;     // the track is over; nothing to resume
                    cache.lastLeftTime   = -1f;
                    cache.playingFile    = null;
                }
                CMMConfig.D($"[CMM] cooldown: {ctx} track ended — resting {_trackCooldown.Remaining(ctx):F0}s.");
                HoldSilentForRest(ctx, tid, fade: 0f);
                return;
            }

            if (!natural)
                CMMConfig.D($"[CMM] cooldown: {ctx} voice stopped early (last seen "
                          + $"{_advLastRemaining:F1}s from its end) — not a track end, advancing.");
            int newId = ++_transitionId;
            Instance.StartNativeContext(ctx, true, newId, null);
        }

        // The POI a rest of `ctx` is tied to, or null. Only POI music is: every context resets
        // when it LEAVES the stack, but "moved into a different POI" must not touch Ambient /
        // Home — an ambient track that ended while you stood in a music-less POI (fallback
        // Stack) would otherwise lose its rest the moment you walked back out.
        private static string RestPoiId(string ctx) =>
            ctx != null && ctx.StartsWith("POI", StringComparison.OrdinalIgnoreCase)
                ? ContextResolver.CurrentPoiId : null;

        // ── cooldownyield ─────────────────────────────────────────────────────
        // While non-empty: the resting context that is looking beneath itself, and its own
        // fallback chain (Home_Night → Home). ResolvePlayableContextCore treats a member as
        // having no music, so a resting Home_Night whose folder is empty never "yields" to
        // the very Home it falls back to. Filled and cleared inside RestYieldTarget only.
        internal static readonly HashSet<string> _restYieldExcluded =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // When each context's current rest began, on the clock ContextCache.lastLeftTime uses
        // (Time.realtimeSinceStartup) — so "was the music beneath interrupted AFTER this rest
        // began?" is a plain comparison. Read only while the context is resting.
        private static readonly Dictionary<string, float> _restBeganAt =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        // The resolver's hook for a context that HAS music. While `ctx` rests and carries
        // cooldownyield, it resolves to the context beneath it (the stack under it, its own
        // fallback chain excluded) — as though its folder were empty — when that context's
        // track is one of:
        //   • AUDIBLE right now: it carries on (walk home mid-ambient while Home rests);
        //   • INTERRUPTED AFTER THIS REST BEGAN and still resumable: it picks up where it left
        //     off (a fight at home cut into the ambient that was carrying on — agreed
        //     2026-11-07). The time test is what keeps a track Home ITSELF interrupted, before
        //     its own track ended, parked: Home's rest is then silence, not a resume.
        // Anything else (nothing beneath playing, a stale or finished track) → null: `ctx`
        // resolves to itself and the rest gate holds silence. Yielding never starts a NEW track.
        //
        // DERIVED on every resolve, never latched (see derive-dont-latch): the answer flips
        // back to `ctx` by itself the moment the conditions stop holding, and the scan loop's
        // playability check turns that flip into an ordinary context switch —
        //   • the rest runs out → `ctx` crossfades in over the playing track at once (agreed
        //     2026-11-07: Home does not wait for the track to end);
        //   • the carried track ENDS (and rests) → `ctx`, held silent.
        internal static string RestYieldTarget(string ctx)
        {
            if (_restYieldExcluded.Count > 0) return null;         // no yield inside a yield
            Contexts c = GetContext(ctx);
            if (c == null || !c.CooldownYield || !_trackCooldown.IsResting(ctx)) return null;
            string playing = _nativeActiveContext;
            if (string.Equals(playing, ctx, StringComparison.OrdinalIgnoreCase)) return null;

            string cur = ctx;
            for (int i = 0; i < 5 && cur != null; i++)
            {
                _restYieldExcluded.Add(cur);
                string fb = GetContext(cur)?.Fallback;
                if (string.IsNullOrEmpty(fb) || GetContext(fb) == null || fb == "Silence" || fb == "Vanilla")
                    break;   // Stack / VanillaIf* / a sentinel: the chain is over
                cur = fb;
            }
            string beneath;
            try { beneath = ResolveViaStack(); }
            finally { _restYieldExcluded.Clear(); }
            if (beneath == null || beneath == "Vanilla") return null;

            if (string.Equals(beneath, playing, StringComparison.OrdinalIgnoreCase) && NativeOutputBusy())
                return beneath;                                     // carry on

            // Interrupted after this rest began, and still resumable (ResumePolicy's rules).
            if (!_restBeganAt.TryGetValue(ctx, out float began)) return null;
            Contexts b = GetContext(beneath);
            if (b == null || !b.CanBeResumed || !cacheMap.TryGetValue(beneath, out ContextCache bc)) return null;
            if (bc.resumePosition <= 0f || bc.lastLeftTime < began || string.IsNullOrEmpty(bc.lastPlayedFile))
                return null;
            return Time.realtimeSinceStartup - bc.lastLeftTime < b.TimeUntilContextStale ? beneath : null;
        }

        // Seconds of audio still queued in the output after the mixer let go of a voice — the
        // part of the track the player has not heard yet. The rest is timed from what they
        // HEAR stop. ~0.2 s in game (the tight cushion); the deep cushion only applies on the
        // menu / while loading. (Reading the stats also resets the HUD's rolling min watermark
        // once per track end — harmless.)
        private static double OutputLatencySeconds()
        {
            var mx = CmmMixer.Instance;
            if (mx == null) return 0;
            mx.GetOutputStats(out int queued, out _, out _, out _, out _, out _, out int rate);
            return rate > 0 && queued > 0 ? (double)queued / rate : 0;
        }

        // The music layer belongs to `ctx` and stays silent until its rest runs out. Called
        // when its own track ended (fade 0: nothing is playing) and from the gate when a
        // context switch lands on a resting context — then whatever WAS playing must be faded
        // out here, because the only thing that ever retires the outgoing voice is an
        // incoming one, and there is none.
        private static void HoldSilentForRest(string ctx, int tid, float fade)
        {
            _nativeAdvanceArmed = false;
            _advanceAwaitsEnd = false;
            if (_nativeActiveContext != null) StopNativeIfActive(fade);
            _trackCooldown.BeginWait(ctx, tid);
        }

        // The gate, called by StartNativeContext before anything is chosen or dispatched.
        // True = refused: `ctx` is resting and the layer is now held silent for it.
        private static bool RestGate(string ctx, int tid, bool overrideRest)
        {
            if (overrideRest) { _trackCooldown.Override(ctx); return false; }
            if (!_trackCooldown.IsResting(ctx)) return false;
            CMMConfig.D($"[CMM] cooldown: {ctx} is resting ({_trackCooldown.Remaining(ctx):F0}s left) — holding silent.");
            HoldSilentForRest(ctx, tid, CrossFadeDuration);
            return true;
        }

        // The armed voice belongs to a context that rests between tracks, so its END is the
        // advance poll's to judge (rest, or advance at once). Until the poll has looked, a
        // silent gap after it is not a strand.
        // DERIVED, not latched: the armed flags outlive a voice that something else took away
        // (the radio's release, a stop to silence — both null _nativeActiveContext without
        // disarming), and a latched answer here locked the convergence net out for good — the
        // context never came back after the radio (CmmCooldownSim fuzz, 2375 strands). The
        // advance poll returns early on a null context, so with no context there is no poll
        // to wait for.
        internal static bool AwaitingTrackEnd
        {
            get
            {
                string ctx = _nativeActiveContext;
                return ctx != null && _nativeAdvanceArmed && EffectiveTrackCooldown(ctx) > 0f;
            }
        }

        // Whether the convergence net must leave the current context alone: it is silent on
        // purpose.
        internal static bool IsHeldForRest(string ctx) => _trackCooldown.IsWaitingOn(ctx, _transitionId);

        // MAIN THREAD, every ContextScanLoop pass (before its early-out).
        internal static void CooldownTick()
        {
            // A hot-reload can turn the armed context's cooldown on or off mid-track.
            if (_nativeAdvanceArmed && _nativeActiveContext != null)
                _advanceAwaitsEnd = EffectiveTrackCooldown(_nativeActiveContext) > 0f;

            List<string> cleared = _trackCooldown.PoiTick(ContextResolver.CurrentPoiId);
            if (cleared != null)
                CMMConfig.D($"[CMM] cooldown: left the POI — cleared {string.Join(", ", cleared.ToArray())}.");

            if (_pauseDucked || PersonalRadio.SuppressesMusic) return;
            string due = _trackCooldown.DueToStart(currentContext?.Context, _transitionId);
            if (due == null) return;

            _trackCooldown.EndWait();
            Contexts ctx = GetContext(due);
            if (ctx == null || !ContextHasFiles(due))
                return;   // nothing to play any more: the convergence net re-resolves it now
            CMMConfig.D($"[CMM] cooldown: {due} rest over — next track.");
            int id = ++_transitionId;
            Instance.StartNativeContext(due, isSameContextSwitchingToNextTrack: true, id, null,
                fadeOverride: ctx.FadeInDuration > 0f ? ctx.FadeInDuration : CrossFadeDuration);
        }

        // A context left the active stack (ContextScanLoop's eviction).
        internal static void NoteContextEvicted(string ctx)
        {
            List<string> cleared = _trackCooldown.Evicted(ctx, ResolveOwnChain(ctx));
            if (cleared.Count > 0)
                CMMConfig.D($"[CMM] cooldown: {ctx} left — cleared {string.Join(", ", cleared.ToArray())}.");
        }

        // The context whose music `ctx` plays through its OWN fallback chain (POI_T3 → POI),
        // or null. Deliberately NOT ResolvePlayableContext: that follows fallback="Stack" into
        // whatever else is active, so leaving a POI with no music of its own would "reset"
        // the Ambient that was playing underneath — and ambient would restart the moment you
        // walked out, which is the opposite of a rest.
        internal static string ResolveOwnChain(string ctx)
        {
            string cur = ctx;
            for (int i = 0; i < 5 && cur != null; i++)
            {
                if (ContextHasFiles(cur)) return cur;
                string fb = GetContext(cur)?.Fallback;
                if (string.IsNullOrEmpty(fb) || fb == cur || GetContext(fb) == null
                    || fb == "Silence" || fb == "Vanilla") return null;   // Stack / VanillaIf* / sentinels
                cur = fb;
            }
            return null;
        }

        internal static void ClearAllTrackCooldowns(string why)
        {
            _trackCooldown.ClearAll();
            CMMConfig.D($"[CMM] cooldown: all rests cleared ({why}).");
        }
    }
}
