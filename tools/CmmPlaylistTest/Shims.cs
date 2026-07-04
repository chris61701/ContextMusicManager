using System;

// Thin stand-ins for the non-audio symbols the linked mod source references — the
// same minimal set CmmBench uses, plus a ContextMusicManager.MusicDirectory that
// PlaylistSource.ResolveFullPath reads. None of the scan/playlist logic under test
// touches anything else.

namespace CMM
{
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

    // Only HashFile is referenced by the linked decoder (MeasureLoudness); copied
    // verbatim from the real LoudnessStore so linking succeeds. Unused by this test.
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

    // Minimal stand-in for the Unity-coupled ContextMusicManager: PlaylistSource
    // .ResolveFullPath is the only linked member that reads MusicDirectory.
    internal static class ContextMusicManager
    {
        public static string MusicDirectory;
    }
}

// The linked sources carry `using UnityEngine;`.
namespace UnityEngine
{
    internal static class Mathf
    {
        public static int   Clamp(int v, int min, int max)       => v < min ? min : (v > max ? max : v);
        public static long  Clamp(long v, long min, long max)    => v < min ? min : (v > max ? max : v);
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    }

    // DataStructures.cs references AudioClip via a nested type; harmless stand-in.
    public sealed class AudioClip
    {
        public int samples;
        public int channels;
        public void SetData(float[] data, int offsetSamples) { }
    }
}
