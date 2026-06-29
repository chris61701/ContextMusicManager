
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
        public static long HotSwapMaxFileSizeBytes = 100L * 1024 * 1024;

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
        public static bool PreloadCacheEnabled = false;

        // Number of player-targeting enemies required to trigger combat music.
        // Used only when the weighted CombatThreat model below is not configured;
        // Combat_Low fires at >= Low threshold, Combat_High at >= High threshold.
        public static int CombatLowEnemyThreshold = 1;
        public static int CombatHighEnemyThreshold = 4;

        // ── Context detection tuning (Config/settings.xml) ─────────────────────
        // Max distance (game units) an enemy is counted toward combat/tension state.
        public static float EnemyDetectRange = 30f;
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

        // Optional weighted combat-threat model (Config/settings.xml <CombatThreat>).
        // When configured (enabled + at least one tier), ResolveContext scores nearby
        // player-targeting enemies by type/state and maps the total to a combat tier,
        // replacing the flat CombatLow/HighEnemyThreshold count test. Never null —
        // an unconfigured instance reports IsConfigured = false and the legacy path runs.
        public static CombatThreatConfig CombatThreat = new CombatThreatConfig();

        // ── Playback keybinds (Config/settings.xml) ────────────────────────────
        // Live controls for whatever music is actually playing (menu or in-game).
        // Gated behind Ctrl+Alt by default so they don't collide with game/chat
        // input; clear the RequireCtrl/RequireAlt flags for bare-key bindings.
        public static bool KeybindsEnabled = true;
        public static bool KeybindRequireCtrl = true;
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
