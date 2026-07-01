
using UnityEngine;

namespace CMM
{
    public static class CMMConfig
    {
        public static float VolumeModifier = 1.0f;
        // A non-negative volumeOverride replaces the global VolumeModifier for this
        // call (used by contexts that set their own volume, e.g. POI_Cleared).
        public static float GetVolume(bool inGame, float volumeOverride = -1f)
        {
            float inGameVolume = GamePrefs.GetFloat(
                    inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);

            float modifier = volumeOverride >= 0f ? volumeOverride : VolumeModifier;
            return inGameVolume * modifier;
        }

        // ── Native audio output (miniaudio) ───────────────────────────────────
        // Music plays through CMM's OWN miniaudio output device + mixer thread
        // (CmmMixer) — decode, mix and output all run on threads CMM owns, off
        // Unity's main and DSP threads. This is the sole output path; the legacy
        // AudioSource/AudioClip path has been removed.
        // Output ring TIGHT target (ms): the cushion the mixer keeps queued in normal
        // play — also the output latency for volume/crossfade, so kept small (~200ms).
        public static int NativeOutputRingMs = 200;
        // Output ring MAX depth (ms): the ceiling the cushion can ADAPTIVELY grow to when
        // the producer is being starved (game world-gen / other programs hogging the CPU)
        // or proactively during loading. The ring is allocated at this size; the live fill
        // level floats between the tight target and this max, and snaps back tight on a
        // context change (see CmmMixer). 15s ≈ 5.8 MB of native ring at 48k stereo f32.
        public static int NativeOutputMaxRingMs = 15000;

        // ── Off-frame-loop control thread (experimental) ──────────────────────
        // When true, periodic housekeeping that can run off Unity's frame loop is paced
        // and executed on CMM's own CmmScheduler thread instead of as WaitForSeconds
        // coroutines, marshalling back to the main thread via MainThreadDispatcher only
        // for the parts that must observe game state or touch the lock-free structures.
        // Lets that work keep ticking through a main-thread freeze (world load/unload).
        // First migrated loop: the debounced loudness cache save. When false, the
        // original coroutine paths run instead (the fallback). See CmmScheduler.
        public static bool NativeScheduler = true;
        // Pre-stage the next track's fully-formed start request when the CURRENT one begins,
        // so the scheduler thread can dispatch the auto-advance straight to the start worker
        // at track-end WITHOUT a main-thread hop — the next track begins even while Unity's
        // main thread is frozen (world load/unload). The main-thread cache bookkeeping is
        // reconciled afterwards. Requires NativeScheduler. When false, auto-advance falls back
        // to marshalling StartNativeContext to the main thread (still off-frame-loop polled,
        // but the advance waits for a frame). See ContextMusicManager.PreStageNextAdvance.
        public static bool NativePreStageAdvance = true;

        // ── Loudness normalization (EBU R128) ─────────────────────────────────
        // When enabled, each track's measured integrated loudness yields a per-track
        // gain (target − measured, in dB) folded into the volume multiplier. See
        // LoudnessStore / R128Analyzer. Target sits on the quiet side so most
        // corrections are attenuations (always achievable) and boosts stay modest
        // given the AudioSource.volume ≤ 1 ceiling.
        public static bool LoudnessNormalizationEnabled = true;
        public static float LoudnessTargetLufs = -18f;
        // Cap on the AUTO boost (a near-silent track can't ask for unbounded gain).
        // Attenuation is uncapped. userTrim is applied on top and clamped separately.
        public static float LoudnessMaxBoostDb = 12f;

        // Loudness scanning model: a track is measured on demand the first time it's
        // needed — the track about to play (block-before-play) plus a look-ahead of the
        // CURRENT context's exact next track, measured during the preload lead window
        // (ScheduleNextCacheLoad) — so it only measures music about to be heard. On the
        // menu, one track per context is pre-measured (the bounded prewarm in
        // MenuPrescanDrain) so every context has a ready, normalized track for its first
        // play. Each track is only ever measured once (cached in Cache/loudness.xml).

        public static bool CrossfadeEnabled = true;
        // When re-entering a context that is still audibly fading out on the
        // inactive channel, reverse that fade (ramp it back up from its current
        // volume) instead of reloading it and fading in from zero. Set false in
        // Config/settings.xml to fall back to the old reload-from-resume behavior.
        public static bool ReverseFadeReclaim = true;

        // Enemy-count thresholds exposed to the decision script as cfg.combatLowThreshold
        // / cfg.combatHighThreshold. The stock context.lua does its combat tiering via
        // weighted scoring instead, so these only matter to a custom script that reads them.
        public static int CombatLowEnemyThreshold = 1;
        public static int CombatHighEnemyThreshold = 4;

        // ── Context detection tuning (Config/settings.xml) ─────────────────────
        // Max distance (game units) an enemy is counted toward combat/tension state.
        public static float EnemyDetectRange = 30f;
        // Max distance (game units) another player counts as "with you" for the
        // multiplayer social signals: an enemy aggroed onto a party member / friend
        // within this range counts as your combat, and allies within it scale combat
        // intensity (see ContextResolver player scan). Independent of EnemyDetectRange
        // so "fighting alongside" can be tuned wider/narrower than "an enemy is near".
        public static float PlayerDetectRange = 30f;
        // Squad-shared combat: how close (game units) another player must be for an
        // enemy fighting THEM to count as your combat too. A fully independent setting from
        // PlayerDetectRange, with its own default — the "share a squadmate's fight" radius is
        // tuned separately from the ally/stranger proximity counts that scale combat intensity.
        public static float CombatShareRange = 30f;
        // Whether squad-shared combat requires the other player to be an ally (in your
        // party or an ACL friend). When false, ANY nearby living player's fight counts as
        // yours. Defaults to true (party/friend only), matching the original behavior.
        public static bool CombatShareRequireAlly = true;
        // Distance (game units) from a bedroll or land-claim that counts as "home".
        public static float HomeDetectRange = 70f;
        // Health fraction (0–1) below which combat escalates to Low_Health_Panic.
        public static float PlayerHealthDangerThresholdPercent = 0.30f;
        // CombatExitDelay: with no nearby aggro for this long (seconds), stop playing
        // combat music. CombatCooldown: how long the encounter's tier stays REMEMBERED
        // after that — re-aggro within this window resumes the previous (latched)
        // intensity instead of restarting at the bottom tier; past it the encounter is
        // forgotten and the next fight starts fresh. The tier latches upward only and
        // never steps back down mid-encounter.
        public static float CombatExitDelay = 2f;
        public static float CombatCooldown = 10f;

        // ── Playback keybinds (Config/settings.xml) ────────────────────────────
        // Live controls for whatever music is actually playing (menu or in-game).
        // Gated behind Alt by default so they don't collide with game/chat input.
        // Ctrl is avoided because it's the game's crouch toggle; Alt is unbound in
        // 7D2D. Set RequireCtrl/clear RequireAlt or both for other bindings.
        public static bool KeybindsEnabled = true;
        public static bool KeybindRequireCtrl = false;
        public static bool KeybindRequireAlt = true;
        public static KeyCode KeyNextTrack  = KeyCode.RightArrow;
        public static KeyCode KeyPrevTrack  = KeyCode.LeftArrow;
        public static KeyCode KeyVolumeUp   = KeyCode.UpArrow;
        public static KeyCode KeyVolumeDown = KeyCode.DownArrow;
        // Per-press change to the current track's manual volume trim (linear factor),
        // persisted per-track in Cache/loudness.xml. Folded into the loudness gain, so
        // it only takes audible effect when LoudnessNormalizationEnabled is true.
        public static float UserTrimStep = 0.1f;

        // Set to false in Config/settings.xml to silence verbose console output.
        // Log.Warning and Log.Error always emit regardless of this flag.
        public static bool DebugLogging = true;
        public static void D(string msg) { if (DebugLogging) Log.Out(msg); }

        // ── Perf instrumentation (see CMMPerf) ─────────────────────────────────
        // Off by default. When on, the context-change hot path is timed per-phase,
        // each transition logs a wall + GC line, and any synchronous span over
        // PerfSpikeMs logs a SPIKE line the instant it happens.
        public static bool  PerfTiming = false;
        public static float PerfSpikeMs = 1.5f;
        // Per-frame stutter watchdog (CMMPerf.FrameTick). The threshold is RELATIVE to
        // the device's own rolling baseline frame time, because on a 30 fps machine an
        // absolute ms cut-off just logs the baseline framerate. A frame is flagged when
        // it exceeds baseline × PerfFrameSpikeFactor AND clears PerfFrameSpikeFloorMs
        // (the floor stops fast machines flagging trivial wobble as a "2× spike").
        // Only consulted when PerfTiming is on.
        public static float PerfFrameSpikeFactor = 2.5f;
        public static float PerfFrameSpikeFloorMs = 50f;
    }
}
