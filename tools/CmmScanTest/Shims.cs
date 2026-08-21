using System;

// Minimal stand-ins for the only UnityEngine symbol the linked DataStructures.cs
// touches (AudioClip, via TrackSlot.Clip). The scan/claiming logic never uses it;
// it exists solely so DataStructures.cs compiles. DecodedAudio is defined in
// DataStructures.cs itself, so it is NOT shimmed here (that would double-define it).
namespace UnityEngine
{
    public sealed class AudioClip
    {
        public int samples;
        public int channels;
        public void SetData(float[] data, int offsetSamples) { }
    }
}

// The linked scan code (SourceScan / MusicRoots / PlaylistSource / AudioDecoder)
// reaches a handful of symbols the ContextMusicManager assembly provides. Kept as
// small as possible: every symbol shimmed here is one this harness is NOT testing
// against the real thing.
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
        internal static bool LoudnessNormalizationEnabled = false;
        internal static int  NativeOutputRingMs = 200;
    }

    // Only AudioDecoder's measurement path calls this, and nothing here decodes.
    internal static class LoudnessStore
    {
        public static string HashFile(string path)
            => throw new NotSupportedException("CmmScanTest never decodes; HashFile is unreachable.");
    }

    // MusicRoots.Describe() formats with the writer's newline constant.
    internal static class CMMSettingsWriter
    {
        internal const string NL = "\r\n";
    }

    // PlaylistSource.ResolveFullPath is the only linked member that reads this.
    internal static class ContextMusicManager
    {
        internal static string MusicDirectory
        {
            get => MusicRoots.Primary;
            set => MusicRoots.SetPrimary(value);
        }
    }
}
