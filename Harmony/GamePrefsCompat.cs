using System;

namespace CMM
{
    // Cross-version-safe GamePrefs access for enum members whose ORDINAL drifts between game
    // versions. C# bakes enum members into call sites as integer constants at compile time, and
    // the single shipped DLL is compiled against 3.0 — so a member like OptionsDynamicMusicEnabled
    // (170 on A20/A21, 175 on 3.0) silently reads a DIFFERENT pref on the alphas if referenced
    // directly. Resolving the member by NAME at runtime (against the loaded game assembly) always
    // yields the running version's ordinal. Invisible to every compile check — only decompile
    // comparison or runtime misbehaviour reveals it (cross-version divergence #10).
    //
    // Members that predate the drift are safe to reference directly; anything deeper into the enum
    // must go through here. The whole audio-volume block sits at the very front and is byte-stable
    // across A20 / A21 / 2.6 / 3.1 (verified in all four decompiles): OptionsAmbientVolumeLevel = 2,
    // OptionsMusicVolumeLevel = 3, OptionsMenuMusicVolumeLevel = 4, OptionsOverallAudioVolumeLevel
    // = 5 — CMMConfig reads all four directly. When adding a member, verify its name AND ordinal
    // exist in ALL supported versions' decompiles and give it a sensible fallback for when it doesn't.
    internal static class GamePrefsCompat
    {
        private static readonly EnumGamePrefs? _dynamicMusicEnabled = Resolve("OptionsDynamicMusicEnabled");
        private static readonly EnumGamePrefs? _dayNightLength = Resolve("DayNightLength");

        private static EnumGamePrefs? Resolve(string name)
        {
            try { return (EnumGamePrefs)Enum.Parse(typeof(EnumGamePrefs), name); }
            catch { return null; }
        }

        // Vanilla Dynamic Music System master toggle. Fallback TRUE: treating DMS as enabled on an
        // unknown version keeps the vanilla handoff path alive (the conservative choice — CMM yields
        // rather than silences).
        internal static bool DynamicMusicEnabled()
            => _dynamicMusicEnabled == null || GamePrefs.GetBool(_dynamicMusicEnabled.Value);

        // World-option day length in real minutes per in-game day. Fallback/invalid → 60 (the
        // game's default), matching the broadcast clock's existing guard.
        internal static int DayNightLengthMinutes()
        {
            if (_dayNightLength == null) return 60;
            int v = GamePrefs.GetInt(_dayNightLength.Value);
            return v > 0 ? v : 60;
        }
    }
}
