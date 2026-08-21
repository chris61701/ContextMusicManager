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
        internal static void NoteConfigWritten() { }
    }

    internal static class CMMConfigLoader
    {
        internal static string SettingsFile(string modPath)      => System.IO.Path.Combine(modPath, "Config", "settings.xml");
        internal static string RadioSettingsFile(string modPath) => System.IO.Path.Combine(modPath, "Config", "radio.xml");
    }

    internal static class CMMConfig
    {
        internal static bool DebugLogging = false;
        internal static void D(string s) { if (DebugLogging) Console.WriteLine(s); }
    }
}
