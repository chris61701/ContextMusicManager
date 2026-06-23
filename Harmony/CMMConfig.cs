#define NAUDIO

using UnityEngine;

namespace CMM
{
    public static class CMMConfig
    {
        public static float VolumeModifier = 0.5f;
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
#if NAUDIO
        public static bool UseNAudioDecoder = true;
#else
        public static bool UseNAudioDecoder = false;
#endif
        public static bool CrossfadeEnabled = true;
        // When re-entering a context that is still audibly fading out on the
        // inactive channel, reverse that fade (ramp it back up from its current
        // volume) instead of reloading it and fading in from zero. Set false in
        // Config/settings.xml to fall back to the old reload-from-resume behavior.
        public static bool ReverseFadeReclaim = true;
        public static bool PreloadCacheEnabled = false;
        public static bool UnityStreamingEnabled = true;

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

        // Set to false in Config/settings.xml to silence verbose console output.
        // Log.Warning and Log.Error always emit regardless of this flag.
        public static bool DebugLogging = true;
        public static void D(string msg) { if (DebugLogging) Log.Out(msg); }
    }
}
