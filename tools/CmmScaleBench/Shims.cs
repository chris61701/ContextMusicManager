using System;

// Minimal UnityEngine stand-ins. AudioClip is required by the linked
// DataStructures.cs (TrackSlot.Clip). Random mirrors UnityEngine.Random.Range
// so the VERBATIM-copied PickNextFile compiles and runs unchanged; seeded for
// determinism so the bench is repeatable.
namespace UnityEngine
{
    public sealed class AudioClip
    {
        public int samples;
        public int channels;
        public void SetData(float[] data, int offsetSamples) { }
    }

    public static class Random
    {
        private static System.Random _r = new System.Random(12345);
        public static void InitState(int seed) => _r = new System.Random(seed);
        // UnityEngine.Random.Range(int, int) is max-EXCLUSIVE, same as System.Random.Next.
        public static int Range(int minInclusive, int maxExclusive) => _r.Next(minInclusive, maxExclusive);
    }
}
