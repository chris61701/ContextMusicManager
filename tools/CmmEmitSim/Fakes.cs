using System;
using System.Collections.Generic;
using UnityEngine;

namespace CMM
{
    // ── THE CLOCK IS THE HARNESS'S ───────────────────────────────────────────────────────────
    // The one shipped file replaced outright. Everything in the emitter layer measures spans
    // against CmmClock.Now, so driving it is what turns a two-second tune-in into a few
    // milliseconds of wall time — and what makes two runs produce identical numbers.
    internal static class CmmClock
    {
        internal static float Now;
        internal static void Set(float t) { Now = t; UnityEngine.Time.realtimeSinceStartup = t; UnityEngine.Time.time = t; }
    }

    // ── THE SPEAKERS ─────────────────────────────────────────────────────────────────────────
    // A source of input, never a decision. The scenario writes Snapshot directly; nothing here
    // resolves a station or decides what a speaker should hear.
    internal static class EmitterRegistry
    {
        internal struct Emitter { public Vector3i Pos; public string Name; public float Dist; public int StationHash; public int PoiId; }
        internal static readonly List<Emitter> Snapshot = new List<Emitter>();
        internal static int Count => Snapshot.Count;
        internal static string   NearestName;
        internal static float    NearestDist = float.MaxValue;
        internal static Vector3i NearestPos;
        internal static void Poll() { }
        internal static void RefreshSnapshot(World world, Vector3 playerPos) { }
    }

    // THE DIAL IS THE REAL ONE. Harmony/EmitterTuner.cs is linked, not modelled, because a
    // fake of it lied: the real TryPin maps SilentPin to "" and returns TRUE, and a fake that
    // returned false instead invented a bug -- the harness reported a speaker sitting on the
    // off-air carrier after being switched off, which the game does not do. Its only game
    // dependency is the save directory, shimmed below, so there was never a reason to model it.
    //
    // Scenarios press it through EmitterTuner.Apply, exactly as the radial menu does.

    // ── THE FLOOD ────────────────────────────────────────────────────────────────────────────
    // Clear line, always. Occlusion has its own harnesses (CmmAcousticsJudge, and the acsweep
    // captures); putting a model of it here would only test the model, and every level this
    // harness asserts on is measured at the speaker with nothing in the way.
    internal static class EmitterAcoustics
    {
        internal static float ReverbSeal = 1f, ReverbSolid = 0f, ReverbDepth = 0f, AirRoomSize = 5f;
        internal static int   AirCellCount = 64;
        internal static int   StatBrR, StatBrB, StatBrF, StatSamples;
        internal static float StatJitterSum, StatJitterMax;
        internal static bool  AnyDoorAnimating => false;
        internal static void  StatReset() { StatBrR = StatBrB = StatBrF = StatSamples = 0; StatJitterSum = StatJitterMax = 0f; }
        internal static void  InvalidateFieldForAnimatingDoors() { }
        internal static void  PumpDoors(World world) { }
        internal static void  ComputeOcclusion(Vector3 ear, float[] pts, int count, float[] extra, World world)
        { for (int i = 0; i < count; i++) extra[i] = 0f; }
        internal static void  OcclusionBegin(Vector3 ear, World world) { }
        internal static bool  OcclusionStep(World world, float budgetMs) => true;
        internal static void  OcclusionFinish(Vector3 ear, float[] pts, int count, float[] extra, World world)
        { for (int i = 0; i < count; i++) extra[i] = 0f; }
        internal static void  ProbeRoom(Vector3 ear, World world, out float avgDist, out float roomSize,
                                        out float reflect, out float openFrac, out bool roofed)
        { avgDist = 3f; roomSize = 5f; reflect = 0.5f; openFrac = 0.2f; roofed = true; }
        internal static void  ComputeReverb(float roomSize, float reflect, float openFrac, bool roofed,
                                            out float wet, out float room, out float damp)
        { wet = 0.2f; room = 0.2f; damp = 0.45f; }
    }

    // ── THE NATIVE FIELD ─────────────────────────────────────────────────────────────────────
    // Same reasoning as the flood: it publishes clear line and never declines, so the tuning
    // path is exercised without an acoustic model in the way.
    internal static class AcousticBridge
    {
        internal static int    PublishedPoints, SaturatedPoints, GainPoints;
        internal static string LastStatus = "sim: clear line";
        internal static bool   GeometrySettling => false;
        internal static bool   EnsureScene(World world, Vector3 ear) => true;
        internal static void   FrameStep(World world) { }
        internal static void   WatchDoors(bool animating) { }
        internal static bool   ComputeOcclusion(Vector3 ear, float[] pts, int ptCount, float[] ptGain, World world)
        { for (int i = 0; i < ptCount; i++) ptGain[i] = 0f; return true; }
        internal static string DescribeNearest() => "sim: d=1.5 excess=0.0dB";
    }

    // ── THE MOD ROOT ─────────────────────────────────────────────────────────────────────────
    internal static class ContextMusicManager
    {
        internal static int    LoudnessInFlight = 0;
        internal static float  SettleRamp = 0.35f;
        internal static string MusicDirectory = "";
        internal static bool   inGame = false;
        internal static void   RequestPrewarm(string file) { }

        // The Radio context, as contexts.xml declares it. A real Contexts, because the catalog
        // reads Source off it to find the station folders — the one place this harness must hand
        // shipped code a genuine object rather than a stub.
        internal static Contexts Radio =
            new Contexts(context: "Radio", source: "Radio", fallback: "None");
        internal static Contexts GetContext(string name) =>
            string.Equals(name, "Radio", StringComparison.OrdinalIgnoreCase) ? Radio : null;
    }

    // Version-neutral game-prefs lookup. Fixed here; the sliders live on the GamePrefs shim.
    internal static class GamePrefsCompat
    {
        internal static int DayNightLengthMinutes() => 60;
    }

    // Only NL is reached from linked code.
    internal static class CMMSettingsWriter { internal static readonly string NL = System.Environment.NewLine; }

    // ── PERF ─────────────────────────────────────────────────────────────────────────────────
    internal static class CMMPerf
    {
        internal enum Phase { EmitTick, EmitPoll, EmitProx, EmitPoiScan, ScanPoll, ScanResolve }
        private sealed class Nul : IDisposable { public void Dispose() { } }
        private static readonly Nul _n = new Nul();
        internal static IDisposable Measure(Phase p) => _n;
    }
}

namespace CMM
{
    // ── THE PER-SPEAKER FIELD CACHE ──────────────────────────────────────────────────────────
    // Ready everywhere, clear line everywhere. Same reasoning as the flood it caches: it is an
    // acoustics optimisation, not part of the tuning sequence, and it has its own coverage.
    internal static class EmitterFieldCache
    {
        internal static int   StatBuilds, StatDoorDrops, StatReady, StatWanted;
        internal static void  StatDrain() { StatBuilds = 0; StatDoorDrops = 0; }
        internal static bool  StatBuilding => false;
        internal static float StatLastBuildMs = 0f;
        internal static void  Maintain(World world, List<Vector3i> wanted) { StatWanted = wanted?.Count ?? 0; StatReady = StatWanted; }
        internal static void  BuildStep(World world, float budgetMs = 0f) { }
        internal static bool  Ready(Vector3i pos) => true;
        internal static bool  SampleStation(World world, Vector3 ear, float[] pts, int count, float[] extra,
                                            out float seal, out float solid, out float depth)
        {
            for (int i = 0; i < count; i++) extra[i] = 0f;
            seal = 1f; solid = 0f; depth = 512f; return true;
        }
    }

    // ── BLOCK CHANGE WATCHER ─────────────────────────────────────────────────────────────────
    internal static class EmitterBlockHook
    {
        internal static bool RecentChange => false;
        internal static void NoteTickEntered() { }
        internal static void NoteGainProgress(float published, float target, bool clamped) { }
    }

    // ── THE LISTENER-ANCHORED FIELD ─────────────────────────────────────────────────────────
    // Clear line, always ready. This is the model the game actually uses; it has its own judge
    // (tools/CmmAcousticsJudge) and its own captures, and a copy of it here would only ever
    // assert that the copy agrees with itself.
    internal static class FieldOcclusion
    {
        internal static float  DoorReadAt => 0f;
        internal static bool   DoorWaitUsable => true;
        internal static bool   Ready => true;
        internal static string Status() => "sim: clear line";
        internal static void   Reset() { }
        internal static void   FrameStep(EntityPlayerLocal player, World world, float budgetMs) { }
        internal static bool   ComputeOcclusion(Vector3 ear, float[] pts, int ptCount, float[] ptGain)
        { for (int i = 0; i < ptCount; i++) ptGain[i] = 0f; return true; }
        internal static string DescribeNearest() => "sim: d=1.5 excess=0.0dB";
    }

    // The world emitters are supported on every version this harness builds for.
    internal static class EmitterCompat { internal static bool RuntimeSupported => true; }

    // Where every log line from shipped code lands. The runner tags each with the sim clock, so
    // a failure reads as a timeline rather than a pile.
    internal static class SimLog
    {
        internal static readonly List<string> Lines = new List<string>();
        internal static bool Echo = false;
        internal static void Line(string s)
        {
            string tagged = $"{CmmClock.Now,7:0.000}  {s}";
            Lines.Add(tagged);
            if (Echo) Console.WriteLine(tagged);
        }
    }
}

namespace CMM
{
    // The pocket radio's dial click. The real tuner fires one on every press; the sound itself is
    // not what this harness measures, and loading the samples needs a mod folder.
    internal static class PersonalRadio { internal static void TuneSfxDial() { } }
}

// The game's save directory. Empty here, which is the answer the tuner's own guard expects
// outside a world: pins live in memory for the run and nothing is written to disk.
internal static class GameIO { internal static string GetSaveGameDir() => ""; }
