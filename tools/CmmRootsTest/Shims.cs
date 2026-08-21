using System;

namespace CMM
{
    internal static class Log
    {
        internal static void Out(string s)     => Console.WriteLine(s);
        internal static void Warning(string s) => Console.WriteLine("WARN  " + s);
        internal static void Error(string s)   => Console.WriteLine("ERROR " + s);
    }

    internal static class CMMConfig
    {
        internal static bool DebugLogging = false;
        internal static void D(string s) { if (DebugLogging) Console.WriteLine(s); }
        // AudioDecoder reads these; nothing here decodes.
        internal static bool LoudnessNormalizationEnabled = false;
        internal static int  NativeOutputRingMs = 200;
    }

    // AudioDecoder is linked for the REAL IsSupported (extension matching is exactly
    // what the pool depends on). Its measurement path references LoudnessStore.HashFile,
    // which this harness never reaches -- nothing here decodes a byte. Stubbed rather
    // than copied, so there is no second implementation to drift.
    internal static class LoudnessStore
    {
        public static string HashFile(string path)
            => throw new NotSupportedException("CmmRootsTest never decodes; HashFile is unreachable.");
    }

    // MusicRoots.Describe() formats with the writer's newline constant.
    internal static class CMMSettingsWriter
    {
        internal const string NL = "\r\n";
    }

    // DataStructures.cs carries `using UnityEngine;`. Same minimal stand-ins the other
    // harnesses use; none of it is exercised by resolution or enumeration.
    internal static class ContextMusicManager
    {
        internal static string MusicDirectory
        {
            get => MusicRoots.Primary;
            set => MusicRoots.SetPrimary(value);
        }
    }
}

namespace UnityEngine
{
    internal static class Mathf
    {
        public static int   Clamp(int v, int min, int max)       => v < min ? min : (v > max ? max : v);
        public static long  Clamp(long v, long min, long max)    => v < min ? min : (v > max ? max : v);
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    }

    public sealed class AudioClip
    {
        public int samples;
        public int channels;
        public void SetData(float[] data, int offsetSamples) { }
    }
}
