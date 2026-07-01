using System;

// Thin stand-ins for the few non-audio symbols the linked decoder source
// references in-game. Kept deliberately minimal — only the members
// AudioDecoder / NativeAudio actually touch.

namespace CMM
{
    // Mirror of the real CMMConfig members the decode path reads. Set
    // DebugLogging=true (bench -v) to surface MeasureLoudness's per-file
    // read/hash/index/decode split.
    internal static class CMMConfig
    {
        public static bool DebugLogging = false;
        public static void D(string msg) { if (DebugLogging) Console.WriteLine(msg); }
    }

    internal static class Log
    {
        public static void Out(string m)     => Console.WriteLine(m);
        public static void Warning(string m) => Console.WriteLine("WARN: " + m);
        public static void Error(string m)   => Console.WriteLine("ERR:  " + m);
    }

    // HashFile is referenced by MeasureLoudness; copied verbatim from the real
    // LoudnessStore (FNV-1a) so the hash step's cost is represented faithfully.
    internal static class LoudnessStore
    {
        public static string HashFile(string path)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong h = offset;
            long length = 0;
            try
            {
                var buf = new byte[65536];
                using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
                    System.IO.FileAccess.Read, System.IO.FileShare.Read, buf.Length, System.IO.FileOptions.SequentialScan))
                {
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        for (int i = 0; i < n; i++) { h ^= buf[i]; h *= prime; }
                        length += n;
                    }
                }
            }
            catch { return null; }
            h ^= (ulong)length;
            h *= prime;
            return h.ToString("x16");
        }
    }
}

// The linked sources carry `using UnityEngine;`, so the namespace must exist.
namespace UnityEngine
{
    internal static class Mathf
    {
        public static int   Clamp(int v, int min, int max)       => v < min ? min : (v > max ? max : v);
        public static long  Clamp(long v, long min, long max)    => v < min ? min : (v > max ? max : v);
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    }
}
