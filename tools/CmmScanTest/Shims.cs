using System;

// Minimal stand-ins for the only UnityEngine symbol the linked DataStructures.cs
// touches (AudioClip, via TrackSlot.Clip). The scan/claiming logic never uses it;
// it exists solely so DataStructures.cs compiles. DecodedAudio is defined in
// DataStructures.cs itself, so it is NOT shimmed here (that would double-define it).
namespace UnityEngine
{
    public sealed class AudioClip
    {
        public int samples;
        public int channels;
        public void SetData(float[] data, int offsetSamples) { }
    }
}
