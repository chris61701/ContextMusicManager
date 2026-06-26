
using System.Collections.Generic;
using UnityEngine;
using NAudio.Wave;

namespace CMM
{
    public enum TrackOrderMode { Random, RandomNoRepeat, Sequential }

    // One score band in the weighted combat-threat model. When the summed threat
    // score reaches MinScore this band becomes eligible; the highest eligible band
    // wins and its Context is emitted as the active combat context.
    public struct TierBand
    {
        public string Context;
        public float MinScore;
    }

    // Weighted combat-threat model loaded from <CombatThreat> in settings.xml.
    //   score = Σ over in-range, player-targeting enemies of (typeWeight × stateMultiplier)
    // The score selects one of the ordered Tiers, whose Context is emitted as the
    // active combat context (replacing the flat enemy-count thresholds). Type and
    // state matching is longest-substring-wins against the entity class name
    // (e.g. "zombieBoeFeral" → no type key matches so DefaultWeight, state "feral").
    // Unconfigured (Enabled false or no Tiers) ⇒ ResolveContext uses the legacy counts.
    public class CombatThreatConfig
    {
        public bool Enabled;
        public float DefaultWeight = 1f;
        public readonly List<KeyValuePair<string, float>> Weights = new List<KeyValuePair<string, float>>();
        public readonly List<KeyValuePair<string, float>> States  = new List<KeyValuePair<string, float>>();
        public readonly List<TierBand> Tiers = new List<TierBand>(); // kept ascending by MinScore

        public bool IsConfigured => Enabled && Tiers.Count > 0;

        // Type weight × state multiplier for one enemy, by its config class name.
        public float ScoreEnemy(string className)
        {
            if (string.IsNullOrEmpty(className)) return DefaultWeight;
            string cn = className.ToLowerInvariant();

            float weight = DefaultWeight; int bestW = -1;
            foreach (var kv in Weights)
                if (kv.Key.Length > bestW && cn.Contains(kv.Key)) { weight = kv.Value; bestW = kv.Key.Length; }

            float mult = 1f; int bestS = -1;
            foreach (var kv in States)
                if (kv.Key.Length > bestS && cn.Contains(kv.Key)) { mult = kv.Value; bestS = kv.Key.Length; }

            return weight * mult;
        }

        // Index of the highest tier whose MinScore is satisfied, or -1 when the
        // score is below the first (lowest) band — i.e. not in combat.
        public int TierForScore(float score)
        {
            int idx = -1;
            for (int i = 0; i < Tiers.Count; i++)
            {
                if (score >= Tiers[i].MinScore) idx = i;
                else break;
            }
            return idx;
        }
    }

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
        public WaveStream Reader;
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
            if (Reader  != null) { try { Reader.Dispose(); } catch { } Reader  = null; }
            if (Decoded != null) { Decoded.Samples = null;               Decoded = null; }
        }
    }

    public class ContextCache
    {
        public TrackSlot Current;
        public TrackSlot Next;
        public float resumePosition;
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

        public Contexts(string context = "None", string path = "", string fallback = "None",
            bool canberesumed = false, int timeuntilcontextstale = 120,
            bool loop = false, int cooldown = 5, int fadeInDuration = 3,
            bool bypassescooldown = false, bool caninterrupt = false,
            int priority = 0, bool playOnce = false, float volumeOverride = -1f,
            int warmup = 0, string warmGroup = null)
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
        }
    }
}
