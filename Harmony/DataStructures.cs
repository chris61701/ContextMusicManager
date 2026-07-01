
using System.Collections.Generic;
using UnityEngine;

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

    public class ContextCache
    {
        public float resumePosition;
        public float lastLeftTime = -1f;
        // The file most recently SELECTED for this context. Advances to the next track as
        // soon as the auto-advance picks it (≈end of the current track, or immediately on a
        // skip) — so it is NOT necessarily the audible file. Used for next-track avoidance,
        // loudness measurement, and previous-track memory.
        public string lastPlayedFile;
        // The file the native mixer is actually VOICING right now (set when StartNativeContext
        // dispatches it). Volume/normalization keys off this, not lastPlayedFile, so the
        // upcoming track's gain is never applied to the track still playing.
        public string playingFile;
        // The track that played immediately before lastPlayedFile in this context.
        // One-deep (no full history) — drives the "previous track" keybind, which
        // toggles between the two. Captured in StartNativeContext on every advance.
        public string previousPlayedFile;
        // The single track pinned to play the next time this context starts FRESH (a
        // switch-in, not a resume). It is the ONLY track pre-measured for an inactive
        // context, and StartNativeContext consumes it so the track we start is exactly
        // the one we normalized — no just-in-time loudness stall. null when not yet
        // staged; kept measured by the loudness keep-set. See StageNextFile / PinStagedFile.
        public string stagedFile;
        public int sequentialIndex;
        public string[] directorySnapshot;
    }

    public class ActiveContext
    {
        public string ContextName;
        public int Priority;
        public float ActivatedAt;
        public bool Manual;
        // When this entry first entered the stack. Unlike ActivatedAt (refreshed
        // every scan while the context stays active), this is set once on insertion
        // and drives the WarmUp dwell gate. A context that leaves and re-enters gets
        // a fresh ActiveContext, so the dwell restarts from zero.
        public float FirstSeenAt;
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
        public int FadeInDuration { get; private set; }
        public bool BypassesCooldown { get; private set; }
        public bool CanInterrupt { get; private set; }
        public int Priority { get; private set; }
        public bool PlayOnce { get; private set; }
        // Per-context replacement for the global VolumeModifier. Negative means
        // "unset" — fall back to CMMConfig.VolumeModifier.
        public float VolumeOverride { get; private set; }
        // Seconds this context must remain continuously active before it becomes
        // eligible to trigger music. 0 = trigger immediately. Acts as an anti-flicker
        // dwell so momentary contexts don't interrupt what's already playing.
        public int WarmUp { get; private set; }
        // Optional warmup-group tag. Contexts sharing a non-empty WarmGroup carry the
        // dwell across a handoff: when one joins the stack while a sibling is still
        // present, it inherits the group's oldest FirstSeenAt instead of restarting
        // its WarmUp from zero. Null/empty = not grouped (the default). See
        // ContextMusicManager.InheritedFirstSeen.
        public string WarmGroup { get; private set; }
        // When true, this context's music pool includes tracks found in subfolders
        // of its Path (recursive enumeration), not just files sitting directly in
        // the folder. Default false = the historical flat, one-level behavior.
        // Tracks living under a more-specific child context's folder are claimed by
        // that child and excluded here, so each file belongs to exactly one context
        // (see ContextMusicManager.GetClaimedSubtreePrefixes).
        public bool IncludeSubfolders { get; private set; }
        // Optional name of the vanilla DMS SectionType this context governs (e.g.
        // "TraderRekt", "Exploration"). When the game's Dynamic Music System wants to
        // play that section (while CMM is on the Vanilla fallback), it must obey THIS
        // context's rules — chiefly its WarmUp dwell. Stored as the raw string; parsed
        // to a game SectionType in the reverse map (ContextMusicManager._sectionToContext)
        // so this data structure stays free of any game-assembly enum dependency. Null =
        // this context governs no vanilla section (the default).
        public string Link { get; private set; }

        public Contexts(string context = "None", string path = "", string fallback = "None",
            bool canberesumed = false, int timeuntilcontextstale = 120,
            bool loop = false, int cooldown = 5, int fadeInDuration = 3,
            bool bypassescooldown = false, bool caninterrupt = false,
            int priority = 0, bool playOnce = false, float volumeOverride = -1f,
            int warmup = 0, string warmGroup = null, bool includeSubfolders = false,
            string link = null)
        {
            Context = context;
            Path = path;
            Fallback = fallback;
            CanBeResumed = canberesumed;
            TimeUntilContextStale = timeuntilcontextstale;
            Loop = loop;
            Cooldown = cooldown;
            FadeInDuration = fadeInDuration;
            BypassesCooldown = bypassescooldown;
            CanInterrupt = caninterrupt;
            Priority = priority;
            PlayOnce = playOnce;
            VolumeOverride = volumeOverride;
            WarmUp = warmup;
            WarmGroup = warmGroup;
            IncludeSubfolders = includeSubfolders;
            Link = link;
        }
    }
}
