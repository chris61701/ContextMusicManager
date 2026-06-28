using System;

// Thin stand-ins for the few non-audio symbols the linked decoder source
// references in-game. Kept deliberately minimal — only the members
// AudioDecoder / LoudnessAnalyzer actually touch.

namespace CMM
{
    // Mirror of the real CMMConfig members the decode path reads. Defaults chosen
    // so the bench exercises the same code with throttling OFF (so it measures raw
    // decode throughput, not sleep time). Set DebugLogging=true (bench -v) to surface
    // MeasureLoudness's per-file read/hash/index/decode split.
    internal static class CMMConfig
    {
        public static bool DebugLogging = false;
        public static void D(string msg) { if (DebugLogging) Console.WriteLine(msg); }

        public static long HotSwapMaxFileSizeBytes = 256L * 1024 * 1024;
    }

    internal static class Log
    {
        public static void Out(string m)     => Console.WriteLine(m);
        public static void Warning(string m) => Console.WriteLine("WARN: " + m);
        public static void Error(string m)   => Console.WriteLine("ERR:  " + m);
    }

    // Mirror of the real DecodedAudio (DataStructures.cs) — the full-PCM preload
    // payload. The bench never runs the preload path, but the linked decoder's
    // clip factories reference the type, so it must exist to compile.
    public sealed class DecodedAudio
    {
        public float[] Samples;
        public int Channels;
        public int Frequency;
    }

    // Only HashBytes is referenced by the decoder; copied verbatim from the real
    // LoudnessStore (FNV-1a) so the hash step's cost is represented faithfully.
    internal static class LoudnessStore
    {
        public static string HashBytes(byte[] data, int length)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong h = offset;
            for (int i = 0; i < length; i++)
            {
                h ^= data[i];
                h *= prime;
            }
            h ^= (ulong)length;
            h *= prime;
            return h.ToString("x16");
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

    internal static class Debug
    {
        public static void LogWarning(string m) => Console.WriteLine("WARN: " + m);
        public static void Log(string m)        => Console.WriteLine(m);
        public static void LogError(string m)   => Console.WriteLine("ERR:  " + m);
    }

    // Minimal stand-in so the linked AudioClip factory methods compile. The bench
    // never exercises the clip path (it only measures + opens streams), so these
    // are inert — Create just returns an instance and the callbacks are stored,
    // never invoked.
    public sealed class AudioClip
    {
        public delegate void PCMReaderCallback(float[] data);
        public delegate void PCMSetPositionCallback(int newPosition);

        public static AudioClip Create(string name, int lengthSamples, int channels,
            int frequency, bool stream) => new AudioClip();

        public static AudioClip Create(string name, int lengthSamples, int channels,
            int frequency, bool stream, PCMReaderCallback reader,
            PCMSetPositionCallback setPosition) => new AudioClip();

        public void SetData(float[] data, int offsetSamples) { }
    }
}
