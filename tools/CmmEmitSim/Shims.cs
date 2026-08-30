using System;
using System.Collections.Generic;

// ── Stand-ins for the voxel world ────────────────────────────────────────────────────────────
//
// Deliberately minimal, and deliberately confined to things that are NOT under test: the block
// registry, the radial menu and the acoustics flood. Everything the tuning state machine actually
// does — every tune, borrow, re-aim, carrier, steal and fade — runs as shipped.
//
// The rule that keeps this honest: a fake here may only ever be a SOURCE of input to the state
// machine, never a model of its behaviour. The moment a fake starts deciding something, it is a
// mirror again, and mirrors are why this harness had to be written.

namespace UnityEngine
{
    internal static class Mathf
    {
        internal const float Infinity = float.PositiveInfinity;
        internal static float Max(float a, float b) => a > b ? a : b;
        internal static float Min(float a, float b) => a < b ? a : b;
        internal static int   Max(int a, int b) => a > b ? a : b;
        internal static int   Min(int a, int b) => a < b ? a : b;
        internal static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        internal static int   Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        internal static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
        internal static float Abs(float v) => v < 0 ? -v : v;
        internal static float Pow(float a, float b) => (float)Math.Pow(a, b);
        internal static float Exp(float v) => (float)Math.Exp(v);
        internal static float Log(float v) => (float)Math.Log(v);
        internal static float Log10(float v) => (float)Math.Log10(v);
        internal static float Sqrt(float v) => (float)Math.Sqrt(v);
        internal static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        internal static int   FloorToInt(float v) => (int)Math.Floor(v);
        internal static int   CeilToInt(float v) => (int)Math.Ceiling(v);
        internal static int   RoundToInt(float v) => (int)Math.Round(v);
    }

    // DRIVEN, NOT WALL. Every clock in the harness advances by the tick, so a two-second tune-in
    // costs whatever the decode costs and two identical runs produce identical numbers.
    internal static class Time
    {
        internal static float realtimeSinceStartup = 0f;
        internal static float unscaledDeltaTime    = 0.05f;
        internal static float deltaTime            = 0.05f;
        internal static float time                 = 0f;
    }

    internal static class AudioListener { internal static float volume = 1f; internal static bool pause = false; }

    internal struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public float magnitude => (float)Math.Sqrt(x * x + y * y + z * z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public override string ToString() => $"({x:0.0},{y:0.0},{z:0.0})";
    }

    internal sealed class Transform
    {
        public Vector3 position;
        public Vector3 forward = new Vector3(0, 0, 1);
        public Vector3 right   = new Vector3(1, 0, 0);
        public Vector3 up      = new Vector3(0, 1, 0);
    }
}

internal struct Vector3i : IEquatable<Vector3i>
{
    public int x, y, z;
    public Vector3i(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
    public bool Equals(Vector3i o) => x == o.x && y == o.y && z == o.z;
    public override bool Equals(object o) => o is Vector3i v && Equals(v);
    public override int GetHashCode() => (x * 397) ^ (y * 31) ^ z;
    public override string ToString() => $"{x}, {y}, {z}";
}

internal sealed class Chunk { }

internal sealed class World
{
    internal ulong worldTime = 6000;
    internal EntityPlayerLocal Player = new EntityPlayerLocal();
    internal EntityPlayerLocal GetPrimaryPlayer() => Player;
}

internal sealed class EntityPlayerLocal
{
    internal UnityEngine.Vector3 position = new UnityEngine.Vector3(0, 0, 0);
    internal UnityEngine.Transform cameraTransform = new UnityEngine.Transform();
}

// The game's world-origin offset for the floating-origin shift. Zero here: the sim world does
// not move, and the emitter layer only ever adds it to a listener position.
internal static class Origin { internal static UnityEngine.Vector3 position = UnityEngine.Vector3.zero; }

internal sealed class GameManager
{
    internal static GameManager Instance = new GameManager();
    internal World World = new World();
    internal bool IsPaused() => false;
}

// Bindings and vanilla-section names: present so the real CMMConfig compiles, never read here.
// The keyboard is not an input to this harness; scenarios press the dial through EmitterTuner.
public enum KeyCode
{
    None = 0, Delete, DownArrow, End, Home, Insert, LeftArrow, PageDown, PageUp, RightArrow, UpArrow,
    Keypad5, Keypad6, Keypad7, Keypad8, Keypad9
}

namespace MusicUtils.Enums
{
    public enum SectionType { Undefined = 0, Menu, Exploring, Suspense, Combat, Death, Trader }
}

// The game's logger. Everything it is handed goes to the harness transcript, so a run's log
// reads like the game's own [CMM] lines and the same eyes work on both.
public static class Log
{
    public static void Out(string s)     => CMM.SimLog.Line(s);
    public static void Warning(string s) => CMM.SimLog.Line("WRN " + s);
    public static void Error(string s)   => CMM.SimLog.Line("ERR " + s);
}

// The game's audio sliders. Fixed at the values a default install has, and settable by a
// scenario that wants to prove the level maths responds to them.
public enum EnumGamePrefs
{
    OptionsOverallAudioVolumeLevel, OptionsAmbientVolumeLevel,
    OptionsMusicVolumeLevel, OptionsMenuMusicVolumeLevel
}

public static class GamePrefs
{
    public static float Overall = 1f, Ambient = 1f, Music = 0.5f, MenuMusic = 0.5f;
    public static float GetFloat(EnumGamePrefs p)
    {
        switch (p)
        {
            case EnumGamePrefs.OptionsOverallAudioVolumeLevel: return Overall;
            case EnumGamePrefs.OptionsAmbientVolumeLevel:      return Ambient;
            case EnumGamePrefs.OptionsMusicVolumeLevel:        return Music;
            default:                                           return MenuMusic;
        }
    }
    public static int GetInt(EnumGamePrefs p) => (int)GetFloat(p);
}
