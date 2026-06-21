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
        // Combat_Low fires at >= Low threshold, Combat_High at >= High threshold.
        public static int CombatLowEnemyThreshold = 1;
        public static int CombatHighEnemyThreshold = 4;

        // Set to false in Config/settings.xml to silence verbose console output.
        // Log.Warning and Log.Error always emit regardless of this flag.
        public static bool DebugLogging = true;
        public static void D(string msg) { if (DebugLogging) Log.Out(msg); }
    }
}
