using System;

namespace CMM
{
    // The two non-audio symbols the linked settings files reference. Kept as small
    // as possible on purpose: every symbol shimmed here is a symbol the harness is
    // NOT testing against the real thing.
    internal static class Log
    {
        internal static void Out(string s)     => Console.WriteLine(s);
        internal static void Warning(string s) => Console.WriteLine("WARN  " + s);
        internal static void Error(string s)   => Console.WriteLine("ERROR " + s);
    }

    // CMMSettingsFile re-stamps the config watcher after its own write.
    internal static class ContextMusicManager
    {
        internal static void NoteConfigWritten()   { }
        internal static void NoteContextsWritten() { }
    }

    internal static class CMMConfigLoader
    {
        internal static string SettingsFile(string modPath)      => System.IO.Path.Combine(modPath, "Config", "settings.xml");
        internal static string RadioSettingsFile(string modPath) => System.IO.Path.Combine(modPath, "Config", "radio.xml");
        internal static string ContextsFile(string modPath)      => System.IO.Path.Combine(modPath, "Config", "contexts.xml");
    }

    internal static class CMMConfig
    {
        internal static bool DebugLogging = false;
        internal static void D(string s) { if (DebugLogging) Console.WriteLine(s); }
    }
}

// DataStructures.cs (the real Contexts class, linked so the writer is tested against the
// type the game uses) carries `using UnityEngine;`. Same minimal stand-ins CmmPlaylistTest
// uses — nothing here is exercised by the contexts writer.
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
