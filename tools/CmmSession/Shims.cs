using System;

// ─────────────────────────────────────────────────────────────────────────────
// Thin stand-ins for the non-game symbols the LINKED mod source touches, plus a
// simulated clock the ported selection layer reads instead of Time.realtimeSince-
// Startup. Deliberately minimal — only the members the linked decoder + ScriptHost
// actually reference. DecodedAudio / Contexts / WaveStream / etc. are NOT shimmed
// here: they come from the linked DataStructures.cs / WaveShim.cs so the model
// can't drift. (Mirrors tools/CmmBench/Shims.cs + tools/CmmScanTest/Shims.cs.)
// ─────────────────────────────────────────────────────────────────────────────

namespace CMM
{
    // Mirror of the real CMMConfig members the decode path AND ScriptHost.MarshalConfig
    // read. Decode-throttling fields are omitted (no throttling here — we measure raw
    // throughput). The numeric defaults match the shipped CMMConfig.cs so the Lua
    // decision logic sees the same thresholds it would in-game.
    internal static class CMMConfig
    {
        public static bool DebugLogging = false;
        public static void D(string msg) { if (DebugLogging) Console.WriteLine(msg); }

        // Decoder (CmmBench parity).
        public static long HotSwapMaxFileSizeBytes = 256L * 1024 * 1024;

        // Native mixer output-ring depths (CmmMixer reads these to size the headless ring).
        // Values match the shipped CMMConfig.cs defaults.
        public static int NativeOutputRingMs = 200;
        public static int NativeOutputMaxRingMs = 15000;

        // Read by ScriptHost.MarshalConfig → exposed to context.lua as cfg.*.
        // Values are the shipped CMMConfig.cs defaults.
        public static int   CombatLowEnemyThreshold = 1;
        public static int   CombatHighEnemyThreshold = 4;
        public static float EnemyDetectRange = 30f;
        public static float PlayerDetectRange = 30f;
        public static float CombatShareRange = 30f;
        public static bool  CombatShareRequireAlly = true;
        public static float HomeDetectRange = 70f;
        public static float PlayerHealthDangerThresholdPercent = 0.30f;
        public static float CombatExitDelay = 2f;
        public static float CombatCooldown = 10f;

        // World-emitter (Radio) acoustics — values match the shipped CMMConfig.cs defaults so the
        // ported EmitterAcoustics (tools/CmmSession/Emitter.cs) walks the same curve/flood the game
        // does. FloodRadius=1 ⇒ the default shipped path is the direct-ray occlusion (see the note
        // in EmitterAcoustics.cs). See [[world-emitter-audio]].
        public static float WorldEmitterRange                     = 100f;
        public static float WorldEmitterOcclusionWallDistance     = 18f;
        public static float WorldEmitterOcclusionMaxDistance      = 30f;
        public static bool  WorldEmitterPathRouting               = true;
        public static int   WorldEmitterFloodRadius               = 1;
        public static bool  WorldEmitterReverbEnabled             = true;
    }

    // No-op stand-in for the real CMMPerf (Harmony/CMMPerf.cs), which ScriptHost wraps its
    // marshal/Lua-call spans in. The harness does its own per-phase timing + allocation
    // accounting in Program.cs, so these spans must be inert here — and the real CMMPerf
    // pulls in CMMConfig.PerfTiming + a Unity-side dump path we don't want linked. Keep the
    // Phase enum in sync with CMMPerf.cs only insofar as ScriptHost references its members.
    internal static class CMMPerf
    {
        public enum Phase { ScanPoll, ScanResolve, ScanGate, ScanMarshal, ScanLuaCall, COUNT }
        public readonly struct Scope : IDisposable { public void Dispose() { } }
        public static Scope Measure(Phase phase) => new Scope();
    }

    internal static class Log
    {
        public static bool Quiet = false;
        public static void Out(string m)     { if (!Quiet) Console.WriteLine(m); }
        public static void Warning(string m) { if (!Quiet) Console.WriteLine("WARN: " + m); }
        public static void Error(string m)   => Console.WriteLine("ERR:  " + m);
    }

    // HashBytes + HashFile are referenced by the decoder; copied verbatim from the real
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

    // Simulated wall clock. The ported selection layer (Engine.cs) reads SimClock.Now
    // wherever the shipped ContextStack/ContextScanLoop code reads
    // Time.realtimeSinceStartup, so WarmUp dwell / eviction timing advance with the
    // synthetic session timeline instead of real time.
    internal static class SimClock
    {
        public static float Now;
    }
}

namespace UnityEngine
{
    // Minimal stand-in for the linked CmmMixer's emitter listener/position setters (only .x/.y/.z
    // are read). The harness's own ported acoustics (Emitter.cs) use plain floats, not this — this
    // exists solely so CmmMixer.SetListener / SetEmitterPos compile in the headless build.
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    internal static class Mathf
    {
        public static int   Clamp(int v, int min, int max)       => v < min ? min : (v > max ? max : v);
        public static long  Clamp(long v, long min, long max)    => v < min ? min : (v > max ? max : v);
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
        public static float Min(float a, float b)                => a < b ? a : b;
    }

    internal static class Debug
    {
        public static void LogWarning(string m) => Console.WriteLine("WARN: " + m);
        public static void Log(string m)        => Console.WriteLine(m);
        public static void LogError(string m)   => Console.WriteLine("ERR:  " + m);
    }

    // Minimal stand-in so the linked AudioClip factory methods compile. The harness
    // never exercises the clip path (it only opens streams + measures), so these are
    // inert — Create returns an instance, callbacks are stored but never invoked.
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
