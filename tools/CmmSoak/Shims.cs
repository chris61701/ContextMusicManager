using System;
using System.Collections.Generic;

// Stand-ins for the handful of game/Unity symbols the linked catalog references.
// Deliberately minimal, and deliberately confined: only BroadcastSeconds and
// EnsureCatalog's throttle touch any of this, and neither is under test — ResolveAt
// takes the broadcast second as a parameter, which is what makes the schedule
// testable without a game at all.

namespace UnityEngine
{
    internal static class Time
    {
        // A schedule test must never depend on wall-clock. Fixed unless a case moves it.
        internal static float realtimeSinceStartup = 0f;
    }

    internal static class Mathf
    {
        internal static float Max(float a, float b) => a > b ? a : b;
        internal static float Min(float a, float b) => a < b ? a : b;
        internal static int   Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        internal static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        internal static float Abs(float v) => v < 0 ? -v : v;
        internal static int   RoundToInt(float v) => (int)System.Math.Round(v);
    }
}

// The game's integer vector. The catalog only stores one (a station's nearest
// speaker position, owned by the emitter layer), never computes with it.
internal struct Vector3i
{
    public int x, y, z;
    public Vector3i(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
}

internal sealed class World
{
    // BroadcastSeconds reads this; it is not under test (ResolveAt takes the broadcast
    // second as a parameter, which is the whole reason the schedule is testable here).
    internal ulong worldTime = 0;
}

internal sealed class GameManager
{
    internal static GameManager Instance => null;   // "no world" — BroadcastSeconds is not under test
    internal World World => null;
    internal bool IsPaused() => false;
}

namespace CMM
{
    internal static class GamePrefsCompat
    {
        internal static int DayNightLengthMinutes() => 60;
    }

    internal static class CMMConfig
    {
        public static bool  DebugLogging                  = false;
        public static bool  LoudnessNormalizationEnabled  = true;
        public static float LoudnessTargetLufs            = -18f;
        public static float LoudnessMaxBoostDb            = 12f;
        public static float LoudnessUnmeasuredDb          = -5f;
        public static float LoudnessChunkSeconds          = 300f;
        public static float LoudnessProvisionalMaxBoostDb = 6f;
        public static float LoudnessRefineRampSeconds     = 3f;
        // The anchor window the whole schedule is built around. A test can shorten it to
        // walk many windows quickly without waiting an hour of broadcast time.
        public static float RadioAnchorSeconds            = 3600f;
        public static string WorldEmitterSource           = "";
        public static float  WorldEmitterFade              = 1.5f;
        public static bool   AddonMusicScanEnabled         = false;

        // Copied from the shipped defaults. A drifting shim constant is how the older
        // harness came to model a -14 LUFS target against a shipped -18 and report a
        // normalization rate that meant nothing, so these are read from CMMConfig.cs
        // rather than invented -- and CheckShimDrift asserts they still match.
        public static float CombatCooldown             = 10f;
        public static float CombatExitDelay            = 2f;
        public static int   CombatHighEnemyThreshold   = 4;
        public static int   CombatLowEnemyThreshold    = 1;
        public static float CombatShareRange           = 30f;
        public static bool  CombatShareRequireAlly     = false;
        public static float EnemyDetectRange           = 30f;
        public static float HomeDetectRange            = 70f;
        public static float PlayerDetectRange          = 30f;
        public static float PlayerHealthDangerThresholdPercent = 0.30f;
        // The shipped default, and it is load-bearing: WholePool is the strong
        // "everything plays once before anything repeats" guarantee, while any positive N
        // is a bounded window where an early repeat is CORRECT. Passing a literal 3 here
        // made the soak assert the strong promise against the weak configuration.
        public static int   TrackNoRepeatMemory        = TrackRotation.WholePool;
        public static float FavouriteWeight            = 2f;

        public static void D(string msg) { if (DebugLogging) Console.WriteLine(msg); }
    }

    // The decision layer times itself through this in-game. Nothing here measures.
    internal static class CMMPerf
    {
        internal enum Phase { Resolve, Select, Decode, ScanMarshal, ScanLuaCall }
        private sealed class Nothing : IDisposable { public void Dispose() { } }
        private static readonly Nothing _n = new Nothing();
        internal static IDisposable Measure(Phase p) => _n;
        internal static void Mark(string s) { }
    }

    internal static class Log
    {
        public static void Out(string m)     => Console.WriteLine(m);
        public static void Warning(string m) { if (CMMConfig.DebugLogging) Console.WriteLine("WARN: " + m); }
        public static void Error(string m)   => Console.WriteLine("ERR:  " + m);
    }

    // The catalog reads CmmMixer.Instance once, to look at a live voice during the debug
    // sync audit. Null is the honest answer here: there is no mixer.
    internal sealed class CmmMixer
    {
        internal static CmmMixer Instance => null;
        internal bool TryGetState(long id, out double pos, out double len, out bool playing)
        { pos = 0; len = 0; playing = false; return false; }
    }

    internal static class ContextMusicManager
    {
        public static string MusicDirectory = "";
        public static void RequestPrewarm(string file) { }
        // The catalog asks for the Radio context to read its source. Returning null makes it
        // fall back to CMMConfig.WorldEmitterSource, which is what the tests set.
        public static Contexts GetContext(string name) => null;
        // The script asks whether a context has anything to play. The soak keeps its own
        // per-context file sets (it is the thing driving selection), so this answers from
        // there; SoakWorld sets the delegate before the first resolve.
        public static Func<string, bool> HasFilesHook = c => true;
        public static bool ContextHasFiles(string contextName) => HasFilesHook(contextName);
    }

    // Only referenced by the config-file writer path, which no test drives.
    internal static class CMMSettingsWriter
    {
        internal static readonly string NL = Environment.NewLine;
        internal static void Write(string a, string b) { }
    }
}
