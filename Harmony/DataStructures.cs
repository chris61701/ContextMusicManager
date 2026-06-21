#define NAUDIO

using UnityEngine;
#if NAUDIO
using NAudio.Wave;
#endif

namespace CMM
{
    public enum TrackOrderMode { Random, RandomNoRepeat, Sequential }

    // Result of an NAudio decode operation, carried back to the main thread.
    public class DecodedAudio
    {
        public float[] Samples;   // interleaved PCM, range -1..+1
        public int Channels;
        public int Frequency;
        public float LengthSeconds => Channels > 0 && Frequency > 0
            ? (float)Samples.Length / Channels / Frequency : 0f;
    }

    // Groups an AudioClip with its backing WaveStream and optional decoded PCM
    // so current/next slots and the pending-disposal slot are each one field.
    // Call Release() AFTER Destroy(Clip) — the audio thread must be off the
    // clip before the WaveStream is disposed.
    public struct TrackSlot
    {
        public AudioClip  Clip;
#if NAUDIO
        public WaveStream Reader;
#endif
        public DecodedAudio Decoded;
        // True when Clip is a borrowed game-owned asset (a vanilla DMS section
        // clip we play on our own channel). We must NEVER Destroy() or dispose
        // it — the game's Dynamic Music System owns its lifetime. Release() just
        // drops our reference.
        public bool Borrowed;

        public void Release()
        {
            Clip = null;
            Borrowed = false;
#if NAUDIO
            if (Reader  != null) { try { Reader.Dispose(); } catch { } Reader  = null; }
#endif
            if (Decoded != null) { Decoded.Samples = null;               Decoded = null; }
        }
    }

    public class ContextCache
    {
        public TrackSlot Current;
        public TrackSlot Next;
        // For Opus pre-seek: AudioSource.time resets to 0 on Play() even when
        // the reader is at safeStart — add this offset to recover true position.
        public float resumePosition;
        public float playbackOffset;
        public float lastLeftTime = -1f;
        public string lastPlayedFile;
        public int sequentialIndex;
        public string[] directorySnapshot;
        public bool nextClipLoading;
        public bool isPreloading;
    }

    public class ActiveContext
    {
        public string ContextName;
        public int Priority;
        public float ActivatedAt;
        public bool Manual;
    }

    public class Contexts
    {
        public string Context { get; private set; }
        public string Path { get; private set; }
        public string Fallback { get; private set; }
        public bool CanBeResumed { get; private set; }
        public int TimeUntilContextStale { get; private set; }
        public bool Loop { get; private set; }
        public int Cooldown { get; private set; }
        public int WarmUp { get; private set; }
        public bool BypassesCooldown { get; private set; }
        public bool CanInterrupt { get; private set; }
        public int Priority { get; private set; }
        public bool PlayOnce { get; private set; }
        // Per-context replacement for the global VolumeModifier. Negative means
        // "unset" — fall back to CMMConfig.VolumeModifier.
        public float VolumeOverride { get; private set; }

        public Contexts(string context = "None", string path = "", string fallback = "None",
            bool canberesumed = false, int timeuntilcontextstale = 120,
            bool loop = false, int cooldown = 5, int warmup = 3,
            bool bypassescooldown = false, bool caninterrupt = false,
            int priority = 0, bool playOnce = false, float volumeOverride = -1f)
        {
            Context = context;
            Path = path;
            Fallback = fallback;
            CanBeResumed = canberesumed;
            TimeUntilContextStale = timeuntilcontextstale;
            Loop = loop;
            Cooldown = cooldown;
            WarmUp = warmup;
            BypassesCooldown = bypassescooldown;
            CanInterrupt = caninterrupt;
            Priority = priority;
            PlayOnce = playOnce;
            VolumeOverride = volumeOverride;
        }
    }
}
