using System;

// Stand-ins for the handful of non-audio symbols the linked LoudnessStore /
// AudioDecoder reference in-game. Deliberately minimal — only the members they
// actually touch — so nothing here can quietly diverge from the real thing.
// The CONFIG values are the real shipped defaults, because half of what this suite
// checks is how the gain rules behave at them.

namespace CMM
{
    internal static class CMMConfig
    {
        public static bool  DebugLogging                   = false;
        public static bool  LoudnessNormalizationEnabled   = true;
        public static float LoudnessTargetLufs             = -18f;
        public static float LoudnessMaxBoostDb             = 12f;
        public static float LoudnessUnmeasuredDb           = -5f;
        public static float LoudnessChunkSeconds           = 300f;
        public static float LoudnessProvisionalMaxBoostDb  = 6f;
        public static float LoudnessRefineRampSeconds      = 3f;

        public static void D(string msg) { if (DebugLogging) Console.WriteLine(msg); }
    }

    internal static class Log
    {
        public static void Out(string m)     => Console.WriteLine(m);
        public static void Warning(string m) { if (CMMConfig.DebugLogging) Console.WriteLine("WARN: " + m); }
        public static void Error(string m)   => Console.WriteLine("ERR:  " + m);
    }

    // LoudnessStore.ToRelKey keys paths relative to the music root when they are
    // under it. The suite works in absolute paths, so an empty root is correct here
    // and exercises the same "outside the music dir" branch a station on H:\ takes.
    internal static class ContextMusicManager
    {
        public static string MusicDirectory = "";
    }
}
