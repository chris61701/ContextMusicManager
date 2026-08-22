// The two UnityEngine members DataStructures.cs's `using UnityEngine;` pulls in.
// Nothing here is exercised by the rotation tests — they exist so the real
// ContextCache compiles into this harness unmodified.
namespace UnityEngine
{
    internal static class Mathf
    {
        public static int   Clamp(int v, int min, int max)       => v < min ? min : (v > max ? max : v);
        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    }

    public sealed class AudioClip
    {
        public int samples;
        public int channels;
    }
}
