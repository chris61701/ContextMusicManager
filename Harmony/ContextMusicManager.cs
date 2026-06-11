// REQUIRED DLLs (drop into Mods/<YourMod>/Harmony/ alongside this assembly):
//   NAudio.dll            ≥ 2.3.0   (NuGet: NAudio)
//   NAudio.Core.dll       ≥ 2.3.0   (NuGet: NAudio.Core)
//   NAudio.WinMM.dll      ≥ 2.3.0   (NuGet: NAudio.WinMM)
//   NAudio.Vorbis.dll     ≥ 1.5.0   (NuGet: NAudio.Vorbis)   → Vorbis .ogg / .oga
//   NVorbis.dll           ≥ 0.10.4  (NuGet: NVorbis)         → pulled in by NAudio.Vorbis
//   NAudio.Flac.dll       ≥ 2.0.0   (NuGet: NAudio.Flac)     → .flac
//   Concentus.dll         ≥ 1.1.7   (NuGet: Concentus)       → Opus .opus / .ogg / .oga
//   Concentus.OggFile.dll ≥ 1.1.7   (NuGet: Concentus.OggFile)
//   System.Memory.dll              → keep, required by NVorbis
//
// Supported formats when UseNAudioDecoder = true:
//   .mp3  .wav  .wave  .aiff  .aif  .ogg (Vorbis or Opus)  .oga  .opus  .flac

#define NAUDIO

using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using System.Diagnostics;


#if NAUDIO
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Vorbis;
using NAudio.Flac;
using Concentus.Structs;
using Concentus.Oggfile;
#endif

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // Enums & small helpers
    // ─────────────────────────────────────────────────────────────────────────

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

    // ─────────────────────────────────────────────────────────────────────────
    // SECTION 1 — CONFIGURATION
    // ─────────────────────────────────────────────────────────────────────────

    public static class CMMConfig
    {
        /// <summary>
        /// When true NAudio decodes audio files — supports mp3, wav,
        /// aiff, ogg, flac.  Set to false to fall back to Unity's
        /// UnityWebRequest pipeline (mp3, ogg, wav only).
        /// Default: false to allow building without NAudio assemblies.
        /// </summary>
#if NAUDIO
        public static bool UseNAudioDecoder = true;
#else
        public static bool UseNAudioDecoder = false;
#endif

        /// <summary>
        /// When false, outgoing channel stops immediately instead of crossfading.
        /// </summary>
        public static bool CrossfadeEnabled = true;

        /// <summary>
        /// When true all context clips are decoded and cached on game entry.
        /// When false clips are decoded on demand and the float[] buffer is
        /// kept alive for the duration of playback then freed.
        /// </summary>
        public static bool PreloadCacheEnabled = false;

        // Unity fallback only — ignored when UseNAudioDecoder = true.
        public static bool UnityStreamingEnabled = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SECTION 2 — DATA STRUCTURES
    // ─────────────────────────────────────────────────────────────────────────

    public class ContextCache
    {
        // ── NAudio path ───────────────────────────────────────────────────────
        public DecodedAudio decodedAudio = null;  // current clip's PCM
        public DecodedAudio nextDecodedAudio = null;  // pre-decoded next clip
        public AudioClip cachedClip = null;  // AudioClip wrapping current PCM
        public AudioClip nextCachedClip = null;  // AudioClip wrapping next PCM

        // ── Shared ────────────────────────────────────────────────────────────
        public float resumePosition = 0f;
        public float lastLeftTime = -1f;
        public string lastPlayedFile = null;
        public int sequentialIndex = 0;
        public string[] directorySnapshot = null;
        public bool nextClipLoading = false;
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

        public Contexts(string context = "None", string path = "", string fallback = "None",
            bool canberesumed = false, int timeuntilcontextstale = 120,
            bool loop = false, int cooldown = 5, int warmup = 3,
            bool bypassescooldown = false, bool caninterrupt = false,
            int priority = 0, bool playOnce = false)
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
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SECTION 3 — NAudio DECODER
    // ─────────────────────────────────────────────────────────────────────────

#if NAUDIO
    public static class NAudioDecoder
    {
        private static readonly HashSet<string> _naudioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".wav", ".wave", ".aiff", ".aif", ".ogg", ".oga", ".opus", ".flac"
        };

        private static readonly HashSet<string> _unityExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".ogg", ".wav"
        };

        public static bool IsSupported(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath);
            return CMMConfig.UseNAudioDecoder
                ? _naudioExts.Contains(ext)
                : _unityExts.Contains(ext);
        }

        public static AudioType GetUnityAudioType(string filePath)
        {
            switch (System.IO.Path.GetExtension(filePath).ToLower())
            {
                case ".mp3": return AudioType.MPEG;
                case ".ogg": return AudioType.OGGVORBIS;
                case ".wav": return AudioType.WAV;
                default: return AudioType.UNKNOWN;
            }
        }

        // ── OGG codec detection ───────────────────────────────────────────────
        // Peeks at the first OGG page to check whether the codec is Opus or Vorbis.
        private static bool IsOpusOgg(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var header = new byte[64];
                    int read = fs.Read(header, 0, header.Length);
                    if (read < 36) return false;
                    for (int i = 0; i <= read - 8; i++)
                    {
                        if (header[i] == 'O' && header[i + 1] == 'p' &&
                            header[i + 2] == 'u' && header[i + 3] == 's' &&
                            header[i + 4] == 'H' && header[i + 5] == 'e' &&
                            header[i + 6] == 'a' && header[i + 7] == 'd')
                            return true;
                    }
                    return false;
                }
            }
            catch { return false; }
        }

        // ── Off-thread decode ─────────────────────────────────────────────────
        // Entry point used by DecodeAsync after the I/O phase has already read
        // the raw bytes. Wraps the buffer in a MemoryStream so NAudio never
        // touches the file system again, keeping all disk I/O on the I/O phase
        // and all CPU work on the decode phase.
        public static DecodedAudio DecodeFileFromBytes(byte[] rawBytes, string filePath)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                string ext = System.IO.Path.GetExtension(filePath).ToLower();

                if (ext == ".opus" || ((ext == ".ogg" || ext == ".oga") && IsOpusOggBytes(rawBytes)))
                    return DecodeOpusFromBytes(rawBytes, filePath);

                using (var ms = new MemoryStream(rawBytes, writable: false))
                using (WaveStream reader = OpenReaderFromStream(ms, ext, filePath))
                {
                    if (reader == null) return null;
                    sw.Stop();
                    Log.Out($"[CMM] DecodeFileFromBytes took {sw.Elapsed.TotalMilliseconds:F3} ms");
                    return DecodeWaveStream(reader, filePath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM NAudio] DecodeFileFromBytes failed for {Path.GetFileName(filePath)}: {ex.Message}");
                return null;
            }
        }

        // File-path entry point (used when bytes haven't been pre-loaded).
        public static DecodedAudio DecodeFile(string filePath)
        {
            try
            {
                string ext = System.IO.Path.GetExtension(filePath).ToLower();

                if (ext == ".opus" || ((ext == ".ogg" || ext == ".oga") && IsOpusOgg(filePath)))
                    return DecodeOpus(filePath);

                using (WaveStream reader = OpenReader(filePath))
                {
                    if (reader == null) return null;
                    return DecodeWaveStream(reader, filePath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM NAudio] Decode failed for {Path.GetFileName(filePath)}: {ex.Message}");
                return null;
            }
        }

        // ── Concentus Opus decoder (file-path entry point) ───────────────────
        // Reads the file into memory then delegates to DecodeOpusFromBytes so
        // all the channel-probe and decode logic lives in one place.
        private static DecodedAudio DecodeOpus(string filePath)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                byte[] rawBytes = File.ReadAllBytes(filePath);
                sw.Stop();
                Log.Out($"[CMM] DecodeOpus took {sw.Elapsed.TotalMilliseconds:F3} ms");
                return DecodeOpusFromBytes(rawBytes, filePath);
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM Concentus] Opus decode failed for {Path.GetFileName(filePath)}: {ex.Message}");
                return null;
            }
        }

        // Opens the appropriate NAudio WaveStream for non-Opus formats.
        private static WaveStream OpenReader(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath).ToLower();
            switch (ext)
            {
                case ".mp3": return new Mp3FileReader(filePath);
                case ".wav":
                case ".wave": return new WaveFileReader(filePath);
                case ".aiff":
                case ".aif": return new AiffFileReader(filePath);
                case ".ogg":
                case ".oga": return new VorbisWaveReader(filePath);
                case ".flac": return new FlacReader(filePath);
                default:
                    Log.Warning($"[CMM NAudio] No reader for extension '{ext}'");
                    return null;
            }
        }

        // Stream-based variant used by DecodeFileFromBytes so NAudio reads
        // from the pre-loaded MemoryStream rather than reopening the file.
        private static WaveStream OpenReaderFromStream(Stream stream, string ext, string filePath)
        {
            switch (ext)
            {
                case ".mp3": return new Mp3FileReader(stream);
                case ".wav":
                case ".wave": return new WaveFileReader(stream);
                // AiffFileReader and FlacReader don't expose a Stream ctor in all
                // versions; fall back to file path for those formats.
                case ".aiff":
                case ".aif": return new AiffFileReader(filePath);
                case ".ogg":
                case ".oga": return new VorbisWaveReader(stream);
                case ".flac": return new FlacReader(filePath);
                default:
                    Log.Warning($"[CMM NAudio] No stream reader for extension '{ext}'");
                    return null;
            }
        }

        // Shared decode logic for both file-path and pre-loaded-bytes paths.
        private static DecodedAudio DecodeWaveStream(WaveStream reader, string filePath)
        {
            ISampleProvider samples = reader.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                ? reader.ToSampleProvider()
                : new SampleChannel(reader, forceStereo: false);

            int channels = samples.WaveFormat.Channels;
            int frequency = samples.WaveFormat.SampleRate;

            const long MaxFloats = 48000L * 2 * 60 * 30;
            long estimatedFloats = (reader.Length > 0 && reader.WaveFormat.BlockAlign > 0)
                ? (reader.Length / reader.WaveFormat.BlockAlign) * channels
                : 0;
            float[] result = new float[Math.Min(Math.Max(estimatedFloats, 65536L), MaxFloats)];

            var buf = new float[65536];
            int offset = 0;
            int read;

            while ((read = samples.Read(buf, 0, buf.Length)) > 0)
            {
                if (offset + read > result.Length)
                {
                    long newSize = Math.Min((long)(result.Length * 1.25f) + read, MaxFloats);
                    Array.Resize(ref result, (int)newSize);
                }
                Array.Copy(buf, 0, result, offset, read);
                offset += read;
            }

            if (offset == 0 || channels == 0 || frequency == 0) return null;
            if (result.Length - offset > 65536) Array.Resize(ref result, offset);

            return new DecodedAudio { Samples = result, Channels = channels, Frequency = frequency };
        }

        // Byte-array variant of IsOpusOgg — no file I/O needed.
        private static bool IsOpusOggBytes(byte[] data)
        {
            if (data == null || data.Length < 36) return false;
            for (int i = 0; i <= Math.Min(data.Length - 8, 64); i++)
            {
                if (data[i] == 'O' && data[i + 1] == 'p' && data[i + 2] == 'u' && data[i + 3] == 's' &&
                    data[i + 4] == 'H' && data[i + 5] == 'e' && data[i + 6] == 'a' && data[i + 7] == 'd')
                    return true;
            }
            return false;
        }

        // Opus decode from a pre-loaded byte array.
        private static DecodedAudio DecodeOpusFromBytes(byte[] rawBytes, string filePath)
        {
            using (var ms = new MemoryStream(rawBytes, writable: false))
            {
                Stopwatch sw = Stopwatch.StartNew();
                // Probe channel count from stream header.
                int channels = 2;
                ms.Position = 0;
                var probeDecoder = new OpusDecoder(48000, 2);
                var probeReader = new OpusOggReadStream(probeDecoder, ms);
                //if (probeReader.HasNextPacket)
                //    channels = probeReader.channels > 0 ? probeReader.ChannelCount : 2;

                ms.Position = 0;
                var decoder = new OpusDecoder(48000, channels);
                long estimatedFloats = Math.Min((long)rawBytes.Length * 15L, 48000L * channels * 60 * 30);
                var resultBuffer = new float[Math.Max(estimatedFloats, 65536L)];
                int offset = 0;

                var oggReader = new OpusOggReadStream(decoder, ms);
                while (oggReader.HasNextPacket)
                {
                    short[] packet = oggReader.DecodeNextPacket();
                    if (packet == null) continue;

                    if (offset + packet.Length > resultBuffer.Length)
                    {
                        long newSize = Math.Min((long)(resultBuffer.Length * 1.25f) + packet.Length,
                                                48000L * channels * 60 * 30);
                        Array.Resize(ref resultBuffer, (int)newSize);
                    }

                    for (int i = 0; i < packet.Length; i++)
                        resultBuffer[offset + i] = packet[i] / 32768f;

                    offset += packet.Length;
                }

                if (offset == 0) return null;
                if (resultBuffer.Length - offset > 65536) Array.Resize(ref resultBuffer, offset);
                sw.Stop();
                Log.Out($"[CMM] DecodeOpusFromBytes took {sw.Elapsed.TotalMilliseconds:F3} ms");
                return new DecodedAudio { Samples = resultBuffer, Channels = channels, Frequency = 48000 };
            }
        }

        // ── AudioClip factory ─────────────────────────────────────────────────
        // FIX: Removed the two-Interlocked pattern. Unity's PCMReaderCallback
        // and PCMSetPositionCallback are serialised per-clip (never called
        // concurrently for the same clip), so a plain int cursor captured
        // in the closure is correct and eliminates the memory-barrier overhead
        // that was causing audio-thread pressure.
        public static AudioClip CreateStreamingClip(DecodedAudio audio, string name)
        {
            if (audio == null || audio.Samples == null || audio.Samples.Length == 0)
                return null;

            float[] samples = audio.Samples;
            int channels = audio.Channels;
            int frequency = audio.Frequency;
            int totalSamples = samples.Length / channels;
            int cursor = 0;

            Stopwatch sw = Stopwatch.StartNew();
            AudioClip clip = AudioClip.Create(
                name, totalSamples, channels, frequency, true,
                (float[] data) =>
                {
                    int srcStart = cursor * channels;
                    int toCopy = Math.Min(data.Length, samples.Length - srcStart);

                    if (toCopy > 0)
                        Array.Copy(samples, srcStart, data, 0, toCopy);
                    if (toCopy < data.Length)
                        Array.Clear(data, toCopy, data.Length - toCopy);

                    cursor += data.Length / channels;
                },
                (int newPosition) =>
                {
                    cursor = Mathf.Clamp(newPosition, 0, totalSamples - 1);
                }
            );
            sw.Stop();
            Log.Out($"[CMM] CreateStreamingClip took {sw.Elapsed.TotalMilliseconds:F3} ms");
            return clip;
        }

        // ── Quick validate ────────────────────────────────────────────────────
        // FIX: Validation now only peeks at the header / first few packets
        // rather than decoding 0.5s of audio. For most formats this means
        // opening the reader and confirming it has valid format metadata,
        // which is orders of magnitude cheaper and causes no decode stutter.
        public static bool QuickValidate(string filePath)
        {
            try
            {
                string ext = System.IO.Path.GetExtension(filePath).ToLower();

                if (ext == ".opus" || ((ext == ".ogg" || ext == ".oga") && IsOpusOgg(filePath)))
                {
                    // For Opus: confirm header decodes and at least one packet is readable.
                    var decoder = new OpusDecoder(48000, 2);
                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var oggReader = new OpusOggReadStream(decoder, fs);
                        if (!oggReader.HasNextPacket) return false;
                        short[] firstPacket = oggReader.DecodeNextPacket();
                        return firstPacket != null && firstPacket.Length > 0;
                    }
                }

                using (WaveStream reader = OpenReader(filePath))
                {
                    if (reader == null) return false;
                    // A valid file must have sensible format metadata and a
                    // non-trivial reported length. We don't read any audio data.
                    return reader.WaveFormat.SampleRate > 0
                        && reader.WaveFormat.Channels > 0
                        && reader.Length > 0;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[CMM] QuickValidate EXCEPTION for {Path.GetFileName(filePath)}: {ex.GetType().Name} – {ex.Message}");
                return false;
            }
        }
    }
#else
    // Fallback stub — compiled when NAudio assemblies are absent.
    public static class NAudioDecoder
    {
        private static readonly HashSet<string> _naudioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".wav", ".wave", ".aiff", ".aif", ".ogg", ".oga", ".opus", ".flac" };

        private static readonly HashSet<string> _unityExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".ogg", ".wav" };

        public static bool IsSupported(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath);
            return CMMConfig.UseNAudioDecoder ? _naudioExts.Contains(ext) : _unityExts.Contains(ext);
        }

        public static AudioType GetUnityAudioType(string filePath)
        {
            switch (System.IO.Path.GetExtension(filePath).ToLower())
            {
                case ".mp3": return AudioType.MPEG;
                case ".ogg": return AudioType.OGGVORBIS;
                case ".wav": return AudioType.WAV;
                default:     return AudioType.UNKNOWN;
            }
        }

        public static DecodedAudio DecodeFile(string filePath)
        {
            Log.Warning($"[CMM] NAudio not available. DecodeFile unsupported for {Path.GetFileName(filePath)}");
            return null;
        }

        public static AudioClip CreateStreamingClip(DecodedAudio audio, string name)
        {
            Log.Warning("[CMM] NAudio not available. CreateStreamingClip unsupported.");
            return null;
        }

        public static bool QuickValidate(string filePath)
        {
            Log.Warning($"[CMM] NAudio not available. QuickValidate returning false for {Path.GetFileName(filePath)}");
            return false;
        }
    }
#endif

    // ─────────────────────────────────────────────────────────────────────────
    // SECTION 4 — CONTEXT RESOLVER  (unchanged)
    // ─────────────────────────────────────────────────────────────────────────

    public static class ContextResolver
    {
        static readonly float EnemyDetectRange = 20f;
        static readonly float TraderDetectRange = 40f;
        static readonly float HomeDetectRange = 70f;
        static readonly float PlayerHealthDangerThresholdPercent = 0.25f;
        static readonly int DangerThreshold = 1;
        static readonly int CriticalThreshold = 4;
        static readonly float CombatCooldown = 10f;

        static string combatState = "None";
        static float lastAggroTime = 0f;
        static bool wasInfested = false;
        static string lastPoI = "None";

        public static void resetValues() { combatState = "None"; }

        public static List<string> ResolveContext()
        {
            //Stopwatch sw = Stopwatch.StartNew();
            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            if (world == null || player == null)
                return new List<string> { "None" };

            var activeContexts = new List<string>();
            bool isDay = world.IsDaytime();
            bool isBloodmoon = world.isEventBloodMoon;
            float healthPct = player.Health / (float)player.GetMaxHealth();

            int enemyAggroCount = 0, enemyAlertCount = 0, enemiesNearby = 0;

            if (isBloodmoon) activeContexts.Add("Blood_Moon");

            if (player.AttachedToEntity is EntityVehicle vehicle)
            {
                string vn = vehicle.GetType().Name.ToLower();
                activeContexts.Add("Vehicle");
                if (vn.Contains("gyrocopter")) activeContexts.Add("Vehicle_Gyrocopter");
                else if (vn.Contains("bicycle")) activeContexts.Add("Vehicle_Bicycle");
                else if (vn.Contains("motorcycle")) activeContexts.Add("Vehicle_Motorcycle");
                else if (vn.Contains("minibike")) activeContexts.Add("Vehicle_Minibike");
                else if (vn.Contains("jeep")) activeContexts.Add("Vehicle_4x4");
            }

            foreach (var entity in world.Entities.list)
            {
                if (entity is EntityTrader trader)
                {
                    if (Vector3.Distance(player.position, trader.position) > TraderDetectRange) continue;
                    string n = trader.EntityName?.ToLower() ?? "";
                    string specific = null;
                    if (n.Contains("jen")) specific = "Trader_Jen";
                    else if (n.Contains("rekt")) specific = "Trader_Rekt";
                    else if (n.Contains("bob")) specific = "Trader_Bob";
                    else if (n.Contains("hugh")) specific = "Trader_Hugh";
                    else if (n.Contains("joel")) specific = "Trader_Joel";
                    activeContexts.Add(specific ?? "Trader");
                }
                else if (entity is EntityEnemy enemy && enemy.IsAlive())
                {
                    if (Vector3.Distance(player.position, enemy.position) < EnemyDetectRange) enemiesNearby++;
                    if (enemy.isAlert)
                    {
                        if (enemy.GetAttackTarget() == player) { lastAggroTime = Time.realtimeSinceStartup; enemyAggroCount++; }
                        enemyAlertCount++;
                    }
                }
            }

            float timeSinceAggro = Time.realtimeSinceStartup - lastAggroTime;
            bool inCombat = false;

            if (enemyAggroCount >= CriticalThreshold)
            {
                combatState = "High";
                activeContexts.Add("Combat_High");
                if (isBloodmoon) activeContexts.Add("Blood_Moon_Combat");
                inCombat = true;
            }
            else if (enemyAggroCount >= DangerThreshold)
            {
                if (combatState != "High") combatState = "Low";
                activeContexts.Add(combatState == "High" ? "Combat_High" : "Combat_Low");
                if (isBloodmoon) activeContexts.Add("Blood_Moon_Combat");
                inCombat = true;
            }
            else if (timeSinceAggro <= CombatCooldown && enemyAlertCount > 0)
            {
                activeContexts.Add(combatState == "High" ? "Combat_High" : "Combat_Low");
                inCombat = true;
            }
            else if (timeSinceAggro > CombatCooldown && enemyAlertCount == 0)
            {
                combatState = "None";
            }

            if (inCombat && healthPct < PlayerHealthDangerThresholdPercent) activeContexts.Add("Low_Health_Panic");

            if (enemiesNearby >= CriticalThreshold && enemyAlertCount >= CriticalThreshold) activeContexts.Add("Tension_High");
            else if (enemiesNearby >= DangerThreshold && enemyAlertCount >= DangerThreshold) activeContexts.Add("Tension_Low");

            if (player is EntityPlayerLocal localPlayer && localPlayer.persistentPlayerData != null)
            {
                bool nearHome = false;
                if (localPlayer.persistentPlayerData.HasBedrollPos)
                {
                    Vector3i bed = localPlayer.persistentPlayerData.BedrollPos;
                    nearHome = Vector3.Distance(player.position, bed) < HomeDetectRange;
                }
                if (!nearHome)
                {
                    var claims = localPlayer.persistentPlayerData.GetLandProtectionBlocks();
                    if (claims.Count > 0)
                    {
                        Vector3i nearest = claims.OrderBy(c => Vector3.Distance(player.position, c.ToVector3())).First();
                        nearHome = Vector3.Distance(player.position, nearest.ToVector3()) < HomeDetectRange;
                    }
                }
                if (nearHome) activeContexts.Add(isDay ? "Home_Day" : "Home_Night");
            }

            PrefabInstance prefab = world.GetPOIAtPosition(player.position);
            if (prefab != null)
            {
                string currentPoI = prefab.prefab.PrefabName;
                int enemiesRemainingInPoi = prefab.sleeperVolumes.Count(v => !v.wasCleared);
                if (enemiesRemainingInPoi > 0)
                {
                    wasInfested = true;
                    switch (prefab.prefab.DifficultyTier)
                    {
                        case 0: activeContexts.Add("POI_T0"); break;
                        case 1: activeContexts.Add("POI_T1"); break;
                        case 2: activeContexts.Add("POI_T2"); break;
                        case 3: activeContexts.Add("POI_T3"); break;
                        case 4: activeContexts.Add("POI_T4"); break;
                        case 5: activeContexts.Add("POI_T5"); break;
                        default: activeContexts.Add("POI"); break;
                    }
                }
                else
                {
                    if (wasInfested && currentPoI == lastPoI) activeContexts.Add("POI_Cleared");
                    wasInfested = false;
                }
                lastPoI = currentPoI;
            }
            else
            {
                wasInfested = false;
            }

            string biomeName = world.GetBiome((int)player.position.x, (int)player.position.z)?.m_sBiomeName;
            switch (biomeName)
            {
                case "pine_forest": activeContexts.Add("Ambient_Forest"); break;
                case "desert": activeContexts.Add("Ambient_Desert"); break;
                case "burnt_forest": activeContexts.Add("Ambient_Burnt_Forest"); break;
                case "snow": activeContexts.Add("Ambient_Snow"); break;
                case "wasteland": activeContexts.Add("Ambient_Wasteland"); break;
                default: activeContexts.Add("Ambient"); break;
            }
            //sw.Stop();
            //Log.Out($"[CMM] ResolvedContext took {sw.Elapsed.TotalMilliseconds:F3} ms");

            return activeContexts;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MOD ENTRY POINT
    // ─────────────────────────────────────────────────────────────────────────

    public class Initialize : IModApi
    {
        public void InitMod(Mod __instance)
        {
            var harmony = new Harmony("com.velasha.contextmusicmanager");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            ContextMusicManager.Init(__instance.Path);
        }
    }

    public class CoRoutine : MonoBehaviour { }

    // ─────────────────────────────────────────────────────────────────────────
    // SECTION 5 — CONTEXT STACK
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager : MonoBehaviour
    {
        public static Dictionary<string, Contexts> contexts = new Dictionary<string, Contexts>();
        public static Dictionary<string, ContextCache> cacheMap = new Dictionary<string, ContextCache>();
        public static List<ActiveContext> _contextStack = new List<ActiveContext>();
        public static Contexts currentContext;

        private static int _transitionId = 0;
        public static string _pendingContext = null;
        public static string _playOnceReturnContext = null;

        private static void AddContext(string name, string path, string fallback,
            bool canBeResumed, int staleTime, bool loop, int cooldown, int warmUp,
            bool bypassesCooldown, bool canInterrupt, int priority, bool playOnce)
        {
            contexts[name] = new Contexts(name, path, fallback, canBeResumed, staleTime,
                loop, cooldown, warmUp, bypassesCooldown, canInterrupt, priority, playOnce);
        }

        public void InitContexts()
        {
            AddContext("Death", "events/death", "Ambient", false, 0, false, 3, 1, true, true, 100, true);
            AddContext("Low_Health_Panic", "events/low_health", "Ambient", true, 30, false, 5, 0, true, true, 90, false);
            AddContext("Blood_Moon_Combat", "blood_moon/combat", "Combat_High", true, 20, false, 3, 3, true, true, 71, false);
            AddContext("Blood_Moon", "blood_moon", "Ambient", true, 30, false, 5, 5, true, true, 70, false);
            AddContext("Vehicle_Gyrocopter", "vehicle/gyrocopter", "Vehicle", true, 30, false, 5, 3, true, true, 61, false);
            AddContext("Vehicle_Bicycle", "vehicle/bicycle", "Vehicle", true, 30, false, 5, 3, true, true, 61, false);
            AddContext("Vehicle_Minibike", "vehicle/minibike", "Vehicle", true, 30, false, 5, 3, true, true, 61, false);
            AddContext("Vehicle_Motorcycle", "vehicle/motorcycle", "Vehicle", true, 30, false, 5, 3, true, true, 61, false);
            AddContext("Vehicle_4x4", "vehicle/4x4", "Vehicle", true, 30, false, 5, 3, true, true, 61, false);
            AddContext("Vehicle", "vehicle", "Ambient", true, 30, false, 5, 3, true, true, 60, false);
            AddContext("POI_Cleared", "poi/cleared", "Ambient", true, 60, false, 5, 5, true, true, 55, true);
            AddContext("Combat_High", "combat_high", "Combat_Low", true, 20, false, 3, 3, true, true, 51, false);
            AddContext("Combat_Low", "combat_low", "Tension_Low", true, 20, false, 3, 3, true, true, 50, false);
            AddContext("Spawn", "events/spawn", "Ambient", false, 0, false, 3, 1, true, true, 45, true);
            AddContext("Inventory", "inventory", "Ambient", true, 15, false, 0, 2, true, true, 40, false);
            AddContext("Tension_High", "tension_high", "Tension_Low", true, 30, false, 5, 5, false, true, 30, false);
            AddContext("Tension_Low", "tension_low", "Ambient", true, 30, false, 5, 5, false, true, 30, false);
            AddContext("Trader_Joel", "trader/joel", "Trader", true, 120, false, 5, 5, false, true, 26, false);
            AddContext("Trader_Jen", "trader/jen", "Trader", true, 120, false, 5, 5, false, true, 26, false);
            AddContext("Trader_Rekt", "trader/rekt", "Trader", true, 120, false, 5, 5, false, true, 26, false);
            AddContext("Trader_Bob", "trader/bob", "Trader", true, 120, false, 5, 5, false, true, 26, false);
            AddContext("Trader_Hugh", "trader/hugh", "Trader", true, 120, false, 5, 5, false, true, 26, false);
            AddContext("Trader", "trader", "Ambient", true, 120, false, 5, 5, false, true, 25, false);
            AddContext("Home_Day", "home/day", "Home", true, 120, false, 5, 5, false, true, 20, false);
            AddContext("Home_Night", "home/night", "Home", true, 120, false, 5, 5, false, true, 20, false);
            AddContext("Home", "home", "Ambient", true, 120, false, 5, 5, false, true, 20, false);
            AddContext("POI_T5", "poi/t5", "POI", true, 60, false, 5, 5, false, true, 15, false);
            AddContext("POI_T4", "poi/t4", "POI", true, 60, false, 5, 5, false, true, 15, false);
            AddContext("POI_T3", "poi/t3", "POI", true, 60, false, 5, 5, false, true, 15, false);
            AddContext("POI_T2", "poi/t2", "POI", true, 60, false, 5, 5, false, true, 15, false);
            AddContext("POI_T1", "poi/t1", "POI", true, 60, false, 5, 5, false, true, 15, false);
            AddContext("POI_T0", "poi/t0", "Ambient", true, 60, false, 5, 5, false, true, 14, false);
            AddContext("POI", "poi", "Ambient", true, 60, false, 5, 5, false, true, 13, false);
            AddContext("Ambient", "ambient", "None", true, 120, false, 5, 4, false, true, 10, false);
            AddContext("Ambient_Forest", "ambient/forest", "Ambient", true, 120, false, 5, 5, false, true, 10, false);
            AddContext("Ambient_Burnt_Forest", "ambient/burnt_forest", "Ambient", true, 120, false, 5, 5, false, true, 10, false);
            AddContext("Ambient_Desert", "ambient/desert", "Ambient", true, 120, false, 5, 5, false, true, 10, false);
            AddContext("Ambient_Snow", "ambient/snow", "Ambient", true, 120, false, 5, 5, false, true, 10, false);
            AddContext("Ambient_Wasteland", "ambient/wasteland", "Ambient", true, 120, false, 5, 5, false, true, 10, false);
            AddContext("None", "", "None", false, 0, false, 0, 0, true, true, 0, false);
            AddContext("Menu", "menu", "Ambient", false, 0, true, 5, 2, true, true, 0, false);
        }

        public static Contexts GetContext(string contextIn) =>
            contexts.TryGetValue(contextIn, out var ctx) ? ctx : null;

        private static readonly HashSet<string> _invalidFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _validatedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Walks the fallback chain to find the nearest playable context.
        private static string ResolvePlayableContext(string contextName)
        {
            string current = contextName;
            for (int i = 0; i < 5; i++)
            {
                if (ContextHasFiles(current)) return current;
                Contexts ctx = GetContext(current);
                string fallback = ctx?.Fallback ?? "None";
                if (fallback == "None" || fallback == current) return null;
                current = fallback;
            }
            return null;
        }

        private static string GetTopContext()
        {
            foreach (ActiveContext active in _contextStack)
            {
                string playable = ResolvePlayableContext(active.ContextName);
                if (playable != null) return playable;
            }
            return "Ambient";
        }

        public static void PushContext(string contextName)
        {
            if (_contextStack.All(c => c.ContextName != contextName))
            {
                Contexts ctx = GetContext(contextName);
                if (ctx == null) return;
                if (ResolvePlayableContext(contextName) == null) return;
                _contextStack.Add(new ActiveContext
                {
                    ContextName = contextName,
                    Priority = ctx.Priority,
                    ActivatedAt = Time.realtimeSinceStartup,
                    Manual = true
                });
                _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
            SendContextUpdate(GetTopContext(), 0);
        }

        public static void PopContext(string contextName)
        {
            _contextStack.RemoveAll(c => c.ContextName == contextName);
            _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            SendContextUpdate(GetTopContext(), 0);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SECTION 6 — AUDIO PLAYER
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager
    {
        private static AudioSource CMM_Primary_Channel;
        private static AudioSource CMM_Secondary_Channel;
        private static bool _primaryIsActive = true;
        private static AudioSource ActiveChannel => _primaryIsActive ? CMM_Primary_Channel : CMM_Secondary_Channel;
        private static AudioSource InactiveChannel => _primaryIsActive ? CMM_Secondary_Channel : CMM_Primary_Channel;

        private static AudioClip _clipToDestroyAfterFade = null;
        private static DecodedAudio _decodedToReleaseAfterFade = null;
        private static bool _scheduleHandledTransition = false;

        // ── NAudio async decode helper ────────────────────────────────────────
        // FIX: Split into two phases so disk I/O and CPU decode are each on a
        // background thread but the main thread yields a frame between them.
        // This keeps the main thread responsive throughout and avoids a single
        // large ThreadPool item that could block the pool for 500-800ms on slow
        // spinning disks or large files.
        private IEnumerator DecodeAsync(string filePath, Action<DecodedAudio> onComplete)
        {

            // Phase 1: read raw bytes off the main thread (disk I/O).
            byte[] rawBytes = null;
            bool ioFinished = false;
            Stopwatch sw = Stopwatch.StartNew();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { rawBytes = File.ReadAllBytes(filePath); }
                catch { /* rawBytes stays null */ }
                ioFinished = true;
                sw.Stop();
                Log.Out($"[CMM] ReadAllBytes took {sw.Elapsed.TotalMilliseconds:F3} ms");
            });
            while (!ioFinished) yield return null;

            if (rawBytes == null)
            {
                Log.Warning($"[CMM] DecodeAsync: could not read file {Path.GetFileName(filePath)}");
                onComplete(null);
                yield break;
            }

            // Phase 2: decode PCM off the main thread (pure CPU).
            DecodedAudio result = null;
            bool decodeFinished = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { result = NAudioDecoder.DecodeFileFromBytes(rawBytes, filePath); }
                catch { /* result stays null */ }
                decodeFinished = true;
            });
            while (!decodeFinished) yield return null;

            onComplete(result);
        }

        // ── Validation ────────────────────────────────────────────────────────
        private IEnumerator ValidateFile(string filePath, Action<bool> callback)
        {
            if (!NAudioDecoder.IsSupported(filePath)) { callback(false); yield break; }

            if (CMMConfig.UseNAudioDecoder)
            {
                Stopwatch sw = Stopwatch.StartNew();
                bool valid = false;
                bool finished = false;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    valid = NAudioDecoder.QuickValidate(filePath);
                    finished = true;
                });
                while (!finished) yield return null;
                if (!valid)
                    Log.Warning($"[CMM] Validation failed (NAudio) for {filePath}");
                sw.Stop();
                Log.Out($"[CMM] QuickValidateWithCache took {sw.Elapsed.TotalMilliseconds:F3} ms");
                callback(valid);
            }
            else
            {
                AudioType audioType = NAudioDecoder.GetUnityAudioType(filePath);
                if (audioType == AudioType.UNKNOWN) { callback(false); yield break; }

                using (var req = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(
                           "file:///" + filePath.Replace("\\", "/"), audioType))
                {
                    ((UnityEngine.Networking.DownloadHandlerAudioClip)req.downloadHandler).streamAudio = false;
                    yield return req.SendWebRequest();
                    if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    {
                        Log.Warning($"[CMM] Validation failed (Unity) for {Path.GetFileName(filePath)}: {req.error}");
                        callback(false);
                        yield break;
                    }
                    AudioClip clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(req);
                    bool valid = clip != null && clip.samples > 0 && clip.channels > 0 && clip.length > 0.5f;
                    if (clip != null) Destroy(clip);
                    callback(valid);
                }
            }
        }

        // ── PlayFromCache ─────────────────────────────────────────────────────

        private IEnumerator PlayFromCache(string contextName, bool isSameContextSwitchingToNextTrack, int transitionId)
        {
            if (transitionId != _transitionId) yield break;

            Contexts ctx = GetContext(contextName);
            ContextCache cache = cacheMap[contextName];

            if (_crossFadeCoroutine != null)
            {
                CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);
                _crossFadeCoroutine = null;
            }

            // ── Ensure clip is available ─────────────────────────────────────
            if (CMMConfig.PreloadCacheEnabled)
            {
                float waited = 0f;
                while (cache.cachedClip == null && waited < 10f)
                {
                    if (transitionId != _transitionId) yield break;
                    yield return new WaitForSeconds(0.25f);
                    waited += 0.25f;
                }
                if (cache.cachedClip == null)
                {
                    Log.Out($"[CMM] PlayFromCache: no clip for {contextName} after wait.");
                    _activeMusicCoroutine = null;
                    yield break;
                }
            }
            else
            {
                if (cache.cachedClip == null)
                {
                    Log.Out($"[CMM] On-demand load for {contextName}...");
                    yield return StartCoroutine(PreloadContext(contextName));
                }
                if (cache.cachedClip == null)
                {
                    Log.Out($"[CMM] On-demand load failed for {contextName}.");
                    _activeMusicCoroutine = null;
                    yield break;
                }
            }

            if (transitionId != _transitionId) yield break;

            // ── Resume position ──────────────────────────────────────────────
            float startPosition = 0f;
            if (ctx.CanBeResumed && cache.resumePosition > 0f)
            {
                float elapsed = Time.realtimeSinceStartup - cache.lastLeftTime;
                if (elapsed < ctx.TimeUntilContextStale)
                {
                    startPosition = cache.resumePosition;
                    Log.Out($"[CMM] Resuming {contextName} at {startPosition:F2}s");
                }
                else
                {
                    cache.resumePosition = 0f;
                    Log.Out($"[CMM] Resume stale for {contextName}, starting fresh.");
                }
            }

            // ── Channel setup ────────────────────────────────────────────────
            AudioSource incoming = InactiveChannel;
            AudioSource outgoing = ActiveChannel;
            float outgoingStartVol = outgoing.volume;

            // FIX: Guard against degenerate clip lengths before clamping.
            float clipLen = cache.cachedClip.length;
            float safeStart = clipLen > 0.02f
                ? Mathf.Clamp(startPosition, 0f, clipLen - 0.01f)
                : 0f;

            incoming.clip = cache.cachedClip;
            incoming.loop = ctx.Loop;
            incoming.time = safeStart;
            incoming.volume = 0f;
            incoming.Play();

            _primaryIsActive = !_primaryIsActive;
            AudioSource watchTarget = incoming;

            // ── Crossfade ────────────────────────────────────────────────────
            float targetVolume = GamePrefs.GetFloat(
                inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);

            if (CMMConfig.CrossfadeEnabled)
            {
                float fadeDuration;
                if (ctx.PlayOnce)
                    fadeDuration = 0.25f;
                else if (isSameContextSwitchingToNextTrack)
                {
                    float cLen = clipLen > 0f ? clipLen : CrossFadeDuration;
                    fadeDuration = Mathf.Clamp(TrackTransitionFadeDuration, 0.25f, cLen * 0.5f);
                }
                else
                    fadeDuration = ctx.WarmUp;

                _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(
                    CrossFade(outgoing, incoming, fadeDuration, currentContext.Cooldown, outgoingStartVol));
            }
            else
            {
                outgoing.Stop();
                outgoing.clip = null;
                outgoing.volume = 0f;
                // FIX: Defer destroy by one frame so the audio thread has fully
                // moved off the outgoing clip before we release it.
                CMM_CoRoutines.StartCoroutine(DeferredDestroyPending());
                incoming.volume = targetVolume;
                int cooldown = currentContext.Cooldown;
                if (cooldown > 0)
                {
                    if (_cooldownCoroutine != null) CMM_CoRoutines.StopCoroutine(_cooldownCoroutine);
                    _cooldownCoroutine = CMM_CoRoutines.StartCoroutine(ContextCooldownTimer(contextName, cooldown));
                }
            }

            Log.Out($"[CMM] Playing: {contextName}");

            if (!ctx.Loop)
                _trackEndCoroutine = CMM_CoRoutines.StartCoroutine(TrackEndWatcher(contextName, watchTarget, transitionId));

            if (!ctx.Loop && !ctx.PlayOnce)
            {
                float remaining = (clipLen > 0f ? clipLen : TrackTransitionFadeDuration + TrackTransitionPreloadLead + 1f)
                                  - safeStart;
                _nextCacheCoroutine = CMM_CoRoutines.StartCoroutine(
                    ScheduleNextCacheLoad(contextName, remaining, transitionId));
            }

            _activeMusicCoroutine = null;
        }

        // FIX: Waits one frame after Stop() before releasing clip/buffer so the
        // audio thread cannot be mid-fill on the old clip when we free it.
        private IEnumerator DeferredDestroyPending()
        {
            yield return new WaitForEndOfFrame();
            DestroyPendingClipAndAudio();
        }

        // Destroys pending clip and, in NAudio mode, releases the PCM buffer.
        private static void DestroyPendingClipAndAudio()
        {
            if (_clipToDestroyAfterFade != null)
            {
                Destroy(_clipToDestroyAfterFade);
                _clipToDestroyAfterFade = null;
            }
            if (!CMMConfig.PreloadCacheEnabled && _decodedToReleaseAfterFade != null)
            {
                _decodedToReleaseAfterFade.Samples = null;
                _decodedToReleaseAfterFade = null;
            }
        }

        // ── Volume ────────────────────────────────────────────────────────────
        private IEnumerator VolumeSyncLoop()
        {
            float previousVolume = -1f;
            while (true)
            {
                yield return new WaitForSeconds(0.1f);
                float currentVolume = GamePrefs.GetFloat(
                    inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);
                if (Mathf.Approximately(currentVolume, previousVolume)) continue;
                previousVolume = currentVolume;
                if (_crossFadeCoroutine == null && ActiveChannel != null && ActiveChannel.isPlaying)
                    ActiveChannel.volume = currentVolume;
            }
        }

        public static void UpdateVolume()
        {
            if (ActiveChannel == null) return;
            ActiveChannel.volume = GamePrefs.GetFloat(
                inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);
        }

        // ── Crossfade ─────────────────────────────────────────────────────────
        private IEnumerator CrossFade(AudioSource outgoing, AudioSource incoming,
            float duration, int cooldownSeconds, float outgoingStartVol)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float targetVolume = GamePrefs.GetFloat(
                    inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                incoming.volume = Mathf.Lerp(0f, targetVolume, t);
                outgoing.volume = Mathf.Lerp(outgoingStartVol, 0f, t);
                yield return null;
            }

            float finalVolume = GamePrefs.GetFloat(
                inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);
            incoming.volume = finalVolume;
            outgoing.Stop();
            outgoing.clip = null;
            outgoing.volume = 0f;

            // FIX: Defer destroy past the end of the crossfade coroutine so the
            // audio thread has fully moved off the outgoing clip. One frame is
            // sufficient; the crossfade already takes several seconds.
            yield return new WaitForEndOfFrame();
            DestroyPendingClipAndAudio();

            if (cooldownSeconds > 0)
            {
                if (_cooldownCoroutine != null) CMM_CoRoutines.StopCoroutine(_cooldownCoroutine);
                // FIX: pass the context name so the cooldown is associated with
                // the leaving context rather than blocking everything globally.
                _cooldownCoroutine = CMM_CoRoutines.StartCoroutine(
                    ContextCooldownTimer(currentContext?.Context ?? "None", cooldownSeconds));
            }
            _crossFadeCoroutine = null;
        }

        public static void FadeToSilence()
        {
            Log.Out("[CMM] Fading to silence.");
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }
            if (_nextCacheCoroutine != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine); _nextCacheCoroutine = null; }
            if (_trackEndCoroutine != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine); _trackEndCoroutine = null; }
            if (_crossFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine); _crossFadeCoroutine = null; }
            _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeOutCoroutine());
        }

        private IEnumerator FadeOutCoroutine()
        {
            AudioSource outgoing = ActiveChannel;
            float startVol = outgoing.volume;
            float elapsed = 0f;
            while (elapsed < CrossFadeDuration)
            {
                elapsed += Time.deltaTime;
                outgoing.volume = Mathf.Lerp(startVol, 0f, Mathf.Clamp01(elapsed / CrossFadeDuration));
                yield return null;
            }
            outgoing.Stop();
            outgoing.clip = null;
            outgoing.volume = 0f;
            yield return new WaitForEndOfFrame();
            DestroyPendingClipAndAudio();
            _crossFadeCoroutine = null;
        }

        // ── Track-end watcher ─────────────────────────────────────────────────
        private IEnumerator TrackEndWatcher(string contextName, AudioSource watchTarget, int transitionId)
        {
            // FIX: Record startTime after the initial sleep so the minimum-play
            // guard is measured from when we actually start watching, not from
            // when the coroutine was spawned.
            yield return new WaitForSeconds(1f);
            float startTime = Time.realtimeSinceStartup;
            float minimumPlaySeconds = 2f;

            if (transitionId != _transitionId) { _trackEndCoroutine = null; yield break; }

            float notPlayingDuration = 0f;
            while (true)
            {
                yield return new WaitForSeconds(TrackEndPollInterval);
                if (transitionId != _transitionId)
                {
                    Log.Out($"[CMM] TrackEndWatcher: transition superseded for {contextName}, exiting.");
                    _trackEndCoroutine = null;
                    yield break;
                }
                if (currentContext.Context != contextName)
                {
                    Log.Out($"[CMM] TrackEndWatcher: context changed mid-watch for {contextName}, bailing.");
                    _trackEndCoroutine = null;
                    yield break;
                }
                if (watchTarget.isPlaying) { notPlayingDuration = 0f; continue; }
                if (Time.realtimeSinceStartup - startTime < minimumPlaySeconds) continue;
                notPlayingDuration += TrackEndPollInterval;
                if (notPlayingDuration >= 0.5f) break;
            }

            if (transitionId != _transitionId || currentContext.Context != contextName)
            {
                Log.Out($"[CMM] TrackEndWatcher: context changed away from {contextName}, ignoring.");
                _trackEndCoroutine = null;
                yield break;
            }

            Log.Out($"[CMM] Track ended: {contextName}");
            _trackEndCoroutine = null;

            if (GetContext(contextName).PlayOnce)
            {
                if (playerIsDead)
                {
                    Log.Out($"[CMM] PlayOnce {contextName} finished but player is dead — fading to silence.");
                    _pendingContext = "None";
                    currentContext = GetContext("None");
                    FadeToSilence();
                    yield break;
                }
                string returnTo = _contextStack
                    .Where(c => c.ContextName != contextName)
                    .Select(c => ResolvePlayableContext(c.ContextName))
                    .FirstOrDefault(c => c != null) ?? "Ambient";
                _playOnceReturnContext = null;
                Log.Out($"[CMM] PlayOnce finished, returning to: {returnTo}");
                SendContextUpdate(returnTo, 0);
                yield break;
            }

            if (_scheduleHandledTransition)
            {
                Log.Out($"[CMM] TrackEndWatcher: ScheduleNextCacheLoad already handled transition for {contextName}, skipping safety net.");
                _scheduleHandledTransition = false;
                yield break;
            }

            if (currentContext.Context == contextName && !ActiveChannel.isPlaying)
            {
                Log.Out($"[CMM] TrackEndWatcher safety net firing for {contextName}");
                ContextCache cache = cacheMap[contextName];
                cache.resumePosition = 0f;
                cache.lastLeftTime = -1f;

                if (cache.cachedClip != null) { Destroy(cache.cachedClip); cache.cachedClip = null; }

                if (!CMMConfig.PreloadCacheEnabled && cache.decodedAudio != null)
                {
                    cache.decodedAudio.Samples = null;
                    cache.decodedAudio = null;
                }

                yield return StartCoroutine(PreloadContext(contextName));
                if (cache.cachedClip == null)
                {
                    Log.Out($"[CMM] Safety net load failed for {contextName}. Giving up.");
                    yield break;
                }

                int newId = ++_transitionId;
                _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(PlayFromCache(contextName, true, newId));
            }
        }

        // ── ScheduleNextCacheLoad ─────────────────────────────────────────────
        // FIX: Pick the next file once here (for the size estimate) and pass
        // it directly to LoadNextClip so the random/sequential selector does not
        // advance twice and land on different files.
        private IEnumerator ScheduleNextCacheLoad(string contextName, float clipLength, int transitionId)
        {
            ContextCache cache = cacheMap[contextName];

            // Peek the next file to get an accurate file-size-based decode estimate.
            string avoidForPick = GlobalTrackOrder == TrackOrderMode.RandomNoRepeat ? cache.lastPlayedFile : null;
            string prePickedFile = PickNextFile(contextName, avoidFile: avoidForPick);

            float estimatedDecodeSeconds = 0f;
            if (prePickedFile != null)
            {
                try
                {
                    long fileBytes = new FileInfo(prePickedFile).Length;
                    // ~1ms per MB is a conservative estimate; 3x safety margin.
                    estimatedDecodeSeconds = fileBytes / (1024f * 1024f) * 0.001f;
                }
                catch { }
            }

            float effectiveFade = Mathf.Clamp(TrackTransitionFadeDuration, 0.25f, clipLength * 0.5f);
            float safeLeadTime = TrackTransitionPreloadLead + estimatedDecodeSeconds * 3f;
            float waitBeforeLoad = Mathf.Max(0f, clipLength - effectiveFade - safeLeadTime);
            float waitBeforePlay = Mathf.Max(0f, clipLength - effectiveFade);

            Log.Out($"[CMM] {contextName}: clip={clipLength:F1}s fade={effectiveFade:F1}s decodeEst={estimatedDecodeSeconds * 1000f:F0}ms loadIn={waitBeforeLoad:F1}s playIn={waitBeforePlay:F1}s");

            yield return new WaitForSeconds(waitBeforeLoad);
            if (transitionId != _transitionId || currentContext.Context != contextName) yield break;

            if (!cache.nextClipLoading && cache.nextCachedClip == null)
            {
                cache.nextClipLoading = true;
                Log.Out($"[CMM] Pre-loading next clip for {contextName}");
                yield return StartCoroutine(LoadNextClip(contextName, prePickedFile));
                cache.nextClipLoading = false;
            }

            float remaining = waitBeforePlay - waitBeforeLoad;
            if (remaining > 0f)
                yield return new WaitForSeconds(remaining);

            if (transitionId != _transitionId || currentContext.Context != contextName) yield break;

            if (cache.nextCachedClip == null)
            {
                Log.Out($"[CMM] Next clip not ready in time for {contextName}, loading inline.");
                if (cache.cachedClip != null)
                {
                    _clipToDestroyAfterFade = cache.cachedClip;
                    cache.cachedClip = null;
                    if (!CMMConfig.PreloadCacheEnabled) _decodedToReleaseAfterFade = cache.decodedAudio;
                    cache.decodedAudio = null;
                }
                yield return StartCoroutine(PreloadContext(contextName));
            }
            else
            {
                if (cache.cachedClip != null)
                {
                    _clipToDestroyAfterFade = cache.cachedClip;
                    if (!CMMConfig.PreloadCacheEnabled) _decodedToReleaseAfterFade = cache.decodedAudio;
                }
                cache.cachedClip = cache.nextCachedClip;
                cache.decodedAudio = cache.nextDecodedAudio;
                cache.nextCachedClip = null;
                cache.nextDecodedAudio = null;
            }

            cache.resumePosition = 0f;
            cache.lastLeftTime = -1f;

            if (cache.cachedClip == null)
            {
                Log.Out($"[CMM] No clip available for {contextName} transition. Giving up.");
                yield break;
            }

            _scheduleHandledTransition = true;
            Log.Out($"[CMM] Starting crossfade to next track in {contextName}");
            int newId = ++_transitionId;
            _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(PlayFromCache(contextName, true, newId));
            _nextCacheCoroutine = null;
        }

        // ── LoadNextClip ──────────────────────────────────────────────────────
        // FIX: Accept a pre-picked file path from ScheduleNextCacheLoad so the
        // track selector doesn't advance twice.
        private IEnumerator LoadNextClip(string contextName, string prePickedFile = null)
        {
            ContextCache cache = cacheMap[contextName];
            string avoid = GlobalTrackOrder == TrackOrderMode.RandomNoRepeat ? cache.lastPlayedFile : null;
            string filePath = prePickedFile ?? PickNextFile(contextName, avoidFile: avoid);

            if (filePath == null) { Log.Out($"[CMM] LoadNextClip: no files for {contextName}"); yield break; }

            if (CMMConfig.UseNAudioDecoder)
            {
                DecodedAudio decoded = null;
                yield return StartCoroutine(DecodeAsync(filePath, d => decoded = d));

                if (decoded == null)
                {
                    Log.Error($"[CMM] LoadNextClip NAudio decode failed for {contextName}: {Path.GetFileName(filePath)}");
                    yield break;
                }
                AudioClip clip = NAudioDecoder.CreateStreamingClip(decoded, Path.GetFileNameWithoutExtension(filePath));
                if (clip == null) yield break;

                if (cache.nextCachedClip != null) Destroy(cache.nextCachedClip);
                if (!CMMConfig.PreloadCacheEnabled && cache.nextDecodedAudio != null)
                    cache.nextDecodedAudio.Samples = null;

                cache.nextDecodedAudio = decoded;
                cache.nextCachedClip = clip;
                cache.lastPlayedFile = filePath;
                Log.Out($"[CMM] Next clip ready for {contextName}: {Path.GetFileName(filePath)}");
            }
            else
            {
                AudioType audioType = NAudioDecoder.GetUnityAudioType(filePath);
                if (audioType == AudioType.UNKNOWN) yield break;

                using (var request = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(
                           "file:///" + filePath.Replace("\\", "/"), audioType))
                {
                    ((UnityEngine.Networking.DownloadHandlerAudioClip)request.downloadHandler).streamAudio = CMMConfig.UnityStreamingEnabled;
                    yield return request.SendWebRequest();
                    if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    { Log.Error($"[CMM] LoadNextClip Unity failed for {contextName}: {request.error}"); yield break; }

                    if (cache.nextCachedClip != null) Destroy(cache.nextCachedClip);
                    cache.nextCachedClip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(request);
                    cache.lastPlayedFile = filePath;
                    Log.Out($"[CMM] Next clip ready (Unity) for {contextName}: {Path.GetFileName(filePath)}");
                }
            }
        }

        // ── PickNextFile ──────────────────────────────────────────────────────
        private static string PickNextFile(string contextName, string avoidFile)
        {
            Contexts ctx = GetContext(contextName);
            if (ctx == null) return null;

            ContextCache cache = cacheMap[contextName];

            string[] files = cache.directorySnapshot;
            if (files == null)
            {
                string dir = ModPath + MusicDirectory + "/" + ctx.Path + "/";
                if (!Directory.Exists(dir)) return null;
                files = Directory.GetFiles(dir)
                    .Where(f => NAudioDecoder.IsSupported(f) && !_invalidFiles.Contains(f))
                    .ToArray();
                cache.directorySnapshot = files;
            }
            else
            {
                files = files.Where(f => !_invalidFiles.Contains(f)).ToArray();
            }

            if (files.Length == 0) return null;

            switch (GlobalTrackOrder)
            {
                case TrackOrderMode.Sequential:
                    {
                        if (cache.sequentialIndex >= files.Length) cache.sequentialIndex = 0;
                        string pick = files[cache.sequentialIndex];
                        cache.sequentialIndex = (cache.sequentialIndex + 1) % files.Length;
                        return pick;
                    }
                case TrackOrderMode.RandomNoRepeat:
                    {
                        if (files.Length == 1) return files[0];
                        string[] candidates = avoidFile != null
                            ? files.Where(f => f != avoidFile).ToArray()
                            : files;
                        if (candidates.Length == 0) candidates = files;
                        return candidates[UnityEngine.Random.Range(0, candidates.Length)];
                    }
                default:
                    return files[UnityEngine.Random.Range(0, files.Length)];
            }
        }

        // ── File presence check ───────────────────────────────────────────────
        // FIX: Instead of invalidating the entire _contextsWithFiles set on any
        // file-change event (which forces a full re-scan of all context directories
        // on the next ContextHasFiles call), only mark the specific changed context
        // as dirty. For mods with many contexts this avoids dozens of Directory.GetFiles
        // calls triggered by a single file change in one folder.
        private static bool ContextHasFiles(string contextName)
        {
            if (_contextsWithFilesDirty)
                RebuildContextsWithFiles();
            return _contextsWithFiles.Contains(contextName);
        }

        // Marked internal so FileWatchLoop can call it for a single context.
        private static void InvalidateContextFileCache(string contextName = null)
        {
            if (contextName == null)
            {
                _contextsWithFilesDirty = true;
            }
            else
            {
                // Remove only the affected context so other entries stay valid.
                _contextsWithFiles.Remove(contextName);

                Contexts ctx = GetContext(contextName);
                if (ctx != null && !string.IsNullOrEmpty(ctx.Path))
                {
                    string d = ModPath + MusicDirectory + "/" + ctx.Path + "/";
                    if (Directory.Exists(d) &&
                        Directory.GetFiles(d).Any(f => NAudioDecoder.IsSupported(f) && !_invalidFiles.Contains(f)))
                        _contextsWithFiles.Add(contextName);
                }
            }
        }

        private static void RebuildContextsWithFiles()
        {
            _contextsWithFiles.Clear();
            foreach (var ctx in contexts.Values)
            {
                if (string.IsNullOrEmpty(ctx.Path)) continue;
                string d = ModPath + MusicDirectory + "/" + ctx.Path + "/";
                if (!Directory.Exists(d)) continue;
                if (Directory.GetFiles(d).Any(f => NAudioDecoder.IsSupported(f) && !_invalidFiles.Contains(f)))
                    _contextsWithFiles.Add(ctx.Context);
            }
            _contextsWithFilesDirty = false;
        }

        // ── Memory summary ────────────────────────────────────────────────────
        public static long GetCacheMemoryBytes()
        {
            long total = 0;
            foreach (var cache in cacheMap.Values)
            {
                if (cache.decodedAudio?.Samples != null)
                    total += (long)cache.decodedAudio.Samples.Length * 4;
                if (cache.nextDecodedAudio?.Samples != null)
                    total += (long)cache.nextDecodedAudio.Samples.Length * 4;
                if (cache.cachedClip != null && cache.decodedAudio == null)
                    total += (long)cache.cachedClip.samples * cache.cachedClip.channels * 4;
                if (cache.nextCachedClip != null && cache.nextDecodedAudio == null)
                    total += (long)cache.nextCachedClip.samples * cache.nextCachedClip.channels * 4;
            }
            return total;
        }

        public static string GetCacheMemorySummary(bool includePerContext = true)
        {
            long totalBytes = 0;
            var lines = new System.Text.StringBuilder();
            foreach (var kv in cacheMap)
            {
                long cur = kv.Value.decodedAudio?.Samples != null ? (long)kv.Value.decodedAudio.Samples.Length * 4 : 0;
                long next = kv.Value.nextDecodedAudio?.Samples != null ? (long)kv.Value.nextDecodedAudio.Samples.Length * 4 : 0;
                if (cur == 0 && kv.Value.cachedClip != null) cur = (long)kv.Value.cachedClip.samples * kv.Value.cachedClip.channels * 4;
                if (next == 0 && kv.Value.nextCachedClip != null) next = (long)kv.Value.nextCachedClip.samples * kv.Value.nextCachedClip.channels * 4;
                if (cur + next == 0) continue;
                totalBytes += cur + next;
                if (includePerContext)
                    lines.AppendLine($"  {kv.Key}: {FormatBytes(cur)} current" +
                                     (next > 0 ? $" + {FormatBytes(next)} next" : ""));
            }
            if (includePerContext) lines.Append($"  TOTAL: {FormatBytes(totalBytes)}");
            else lines.Append(FormatBytes(totalBytes));
            return lines.ToString();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024f:F1} KB";
            return $"{bytes} B";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SECTION 7 — COORDINATOR
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager
    {
        public static string MusicDirectory = "/Music";
        public static bool inGame = false;
        public static bool inInventory = false;
        public static bool playerIsDead = true;
        public static string ModPath;

        // FIX: Per-context cooldown map replaces the flat lastContextOnCoolDown
        // bool. This prevents a cooldown on context A from blocking a valid
        // transition to context B at higher priority.
        private static readonly HashSet<string> _contextsOnCooldown =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static GameObject CMM_Audio_Object;
        public static CoRoutine CMM_CoRoutines;
        public static ContextMusicManager Instance;

        private static Coroutine _activeMusicCoroutine = null;
        private static Coroutine _nextCacheCoroutine = null;
        private static Coroutine _trackEndCoroutine = null;
        private static Coroutine _crossFadeCoroutine = null;
        private static Coroutine _cooldownCoroutine = null;

        public static TrackOrderMode GlobalTrackOrder = TrackOrderMode.RandomNoRepeat;
        public static float ContextScanCooldown = 0.5f;
        public static float StalenessCheckInterval = 5f;
        public static float NextCacheWindow = 5f;
        public static float TrackEndPollInterval = 0.25f;
        public static float CrossFadeDuration = 2f;
        public static float TrackTransitionFadeDuration = 6f;
        public static float TrackTransitionPreloadLead = 10f;

        private static readonly HashSet<string> _contextsWithFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _contextsWithFilesDirty = true;

        // ── Init ──────────────────────────────────────────────────────────────
        public static void Init(string modPath)
        {
            ModPath = modPath;
            CMM_Audio_Object = new GameObject("ContextMusicManager");
            UnityEngine.Object.DontDestroyOnLoad(CMM_Audio_Object);
            Instance = CMM_Audio_Object.AddComponent<ContextMusicManager>();
            Instance.InitContexts();

            foreach (var ctx in contexts.Values)
                cacheMap[ctx.Context] = new ContextCache();

            currentContext = GetContext("None");
            _pendingContext = "None";
            _transitionId = 0;

            CMM_Primary_Channel = CMM_Audio_Object.AddComponent<AudioSource>();
            CMM_Primary_Channel.volume = 0f;
            CMM_Primary_Channel.loop = false;

            CMM_Secondary_Channel = CMM_Audio_Object.AddComponent<AudioSource>();
            CMM_Secondary_Channel.volume = 0f;
            CMM_Secondary_Channel.loop = false;

            CMM_CoRoutines = CMM_Audio_Object.AddComponent<CoRoutine>();
            CMM_CoRoutines.StartCoroutine(Instance.PreloadContext("Menu"));
            CMM_CoRoutines.StartCoroutine(Instance.ContextScanLoop());
            CMM_CoRoutines.StartCoroutine(Instance.StalenessCheckLoop());
            CMM_CoRoutines.StartCoroutine(Instance.FileWatchLoop());
            CMM_CoRoutines.StartCoroutine(Instance.VolumeSyncLoop());

            Log.Out($"[CMM] Initialized! Decoder: {(CMMConfig.UseNAudioDecoder ? "NAudio" : "Unity")} | Preload: {CMMConfig.PreloadCacheEnabled}");
        }

        public static void OnEnterGame()
        {
            if (!inGame) return;
            CMM_CoRoutines.StartCoroutine(Instance.PreloadAllContexts());
        }

        // ── SendContextUpdate ─────────────────────────────────────────────────
        public static void SendContextUpdate(string contextIn, int bypassPriority = -1)
        {
            Contexts incoming = GetContext(contextIn);
            if (incoming == null) return;
            if (_pendingContext == contextIn) return;

            bool menuOverride = !inGame;
            int comparePriority = bypassPriority >= 0 ? bypassPriority : (currentContext?.Priority ?? 0);
            bool isReloadOfCurrent = currentContext != null && contextIn == currentContext.Context;
            bool priorityOk = inGame && incoming.CanInterrupt && incoming.Priority > comparePriority;

            if (!menuOverride && !priorityOk && !isReloadOfCurrent)
            {
                Log.Out($"[CMM] {contextIn} rejected — not higher priority than {currentContext?.Context}");
                return;
            }

            Log.Out($"[CMM] Context accepted: {currentContext?.Context} -> {contextIn}");
            _pendingContext = contextIn;

            if (currentContext != null && currentContext.CanBeResumed && ActiveChannel.isPlaying)
            {
                ContextCache leavingCache = cacheMap[currentContext.Context];
                leavingCache.resumePosition = ActiveChannel.time;
                leavingCache.lastLeftTime = Time.realtimeSinceStartup;
                Log.Out($"[CMM] Saved resume for {currentContext.Context}: {leavingCache.resumePosition:F2}s");

                if (!CMMConfig.PreloadCacheEnabled && leavingCache.cachedClip != null)
                {
                    _clipToDestroyAfterFade = leavingCache.cachedClip;
                    _decodedToReleaseAfterFade = leavingCache.decodedAudio;
                    leavingCache.cachedClip = null;
                    leavingCache.decodedAudio = null;
                }
            }

            if (currentContext != null && currentContext.PlayOnce && !incoming.PlayOnce)
                _playOnceReturnContext = null;

            string contextToPlay = ResolvePlayableContext(contextIn);
            if (contextToPlay == null)
            {
                Log.Out($"[CMM] No playable context in chain for {contextIn} — fading to silence.");
                _pendingContext = "None";
                currentContext = GetContext("None");
                ++_transitionId;
                FadeToSilence();
                return;
            }

            Contexts candidate = GetContext(contextToPlay);
            bool bypassCooldown = incoming.BypassesCooldown || candidate.BypassesCooldown;

            // FIX: Check per-context cooldown rather than a global flag.
            // A cooldown on the leaving context no longer blocks unrelated contexts.
            bool onCooldown = !bypassCooldown
                              && bypassPriority < 0
                              && _contextsOnCooldown.Contains(contextToPlay);

            if (onCooldown)
            {
                Log.Out($"[CMM] {candidate.Context} blocked — cooldown active");
                _pendingContext = currentContext?.Context ?? "None";
                if (!CMMConfig.PreloadCacheEnabled)
                {
                    ContextCache blocked = cacheMap[currentContext.Context];
                    if (blocked.cachedClip != null) { Destroy(blocked.cachedClip); blocked.cachedClip = null; }
                    if (blocked.decodedAudio != null) { blocked.decodedAudio.Samples = null; blocked.decodedAudio = null; }
                }
                return;
            }

            currentContext = candidate;
            TriggerMusic(contextToPlay);
        }

        public static void TriggerMusic(string contextName)
        {
            Log.Out($"[CMM] TriggerMusic: {contextName}");
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }
            if (_nextCacheCoroutine != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine); _nextCacheCoroutine = null; }
            if (_trackEndCoroutine != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine); _trackEndCoroutine = null; }
            if (_crossFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine); _crossFadeCoroutine = null; }

            int newId = ++_transitionId;
            _scheduleHandledTransition = false;
            _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(Instance.PlayFromCache(contextName, false, newId));
        }

        // ── Scan loops ────────────────────────────────────────────────────────
        // FIX: Track the previous context set so we only touch the stack and
        // call SendContextUpdate when something actually changed.
        private HashSet<string> _previousActiveContexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private IEnumerator ContextScanLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(ContextScanCooldown);
                if (!inGame || playerIsDead) continue;

                List<string> activeContexts = ContextResolver.ResolveContext();

                // Early-out: if the resolved set hasn't changed, skip all stack work.
                bool changed = activeContexts.Count != _previousActiveContexts.Count
                               || activeContexts.Any(c => !_previousActiveContexts.Contains(c));

                if (!changed) continue;

                _previousActiveContexts.Clear();
                foreach (string c in activeContexts) _previousActiveContexts.Add(c);

                foreach (string ctxName in activeContexts)
                {
                    if (_contextStack.All(c => c.ContextName != ctxName))
                    {
                        Contexts ctx = GetContext(ctxName);
                        if (ctx == null) continue;
                        if (ResolvePlayableContext(ctxName) == null) continue;
                        _contextStack.Add(new ActiveContext
                        {
                            ContextName = ctxName,
                            Priority = ctx.Priority,
                            ActivatedAt = Time.realtimeSinceStartup,
                            Manual = false
                        });
                    }
                }

                _contextStack.RemoveAll(c => !c.Manual && !activeContexts.Contains(c.ContextName));
                _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));

                string topContext = GetTopContext();
                if (topContext != currentContext.Context)
                {
                    if (currentContext != null && currentContext.PlayOnce)
                    {
                        Contexts top = GetContext(topContext);
                        if (top == null || top.Priority <= currentContext.Priority) continue;
                    }
                    _pendingContext = null;
                    SendContextUpdate(topContext, 0);
                }
            }
        }

        // FIX: Log cache state only when something has actually changed,
        // not unconditionally every 5 seconds.
        private string _lastCacheLogState = "";

        private IEnumerator StalenessCheckLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(StalenessCheckInterval);
                if (!inGame) continue;

                // Build state string cheaply; only emit log lines if it changed.
                string cacheState = string.Join(", ", cacheMap
                    .Where(kv => kv.Value.cachedClip != null)
                    .Select(kv => $"{kv.Key}={kv.Value.cachedClip.length:F0}s(next={(kv.Value.nextCachedClip != null ? "ready" : "null")})"));

                if (cacheState != _lastCacheLogState)
                {
                    Log.Out($"[CMM] Cache state: {cacheState}");
                    Log.Out($"[CMM] Cache memory: {GetCacheMemorySummary(false)}");
                    _lastCacheLogState = cacheState;
                }

                float now = Time.realtimeSinceStartup;
                foreach (var ctx in contexts.Values)
                {
                    if (ctx.Context == currentContext.Context) continue;
                    if (!ctx.CanBeResumed || ctx.TimeUntilContextStale <= 0) continue;

                    ContextCache cache = cacheMap[ctx.Context];
                    if (cache.lastLeftTime < 0) continue;

                    float elapsed = now - cache.lastLeftTime;
                    if (elapsed >= ctx.TimeUntilContextStale)
                    {
                        Log.Out($"[CMM] Context {ctx.Context} is stale ({elapsed:F0}s). Clearing for fresh pick.");
                        cache.resumePosition = 0f;
                        cache.lastLeftTime = -1f;
                        string avoid = cache.lastPlayedFile;
                        cache.lastPlayedFile = null;

                        if (CMMConfig.PreloadCacheEnabled)
                            yield return StartCoroutine(PreloadContext(ctx.Context, avoidFile: avoid));
                    }
                }
            }
        }

        private IEnumerator FileWatchLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(2f);
                if (!inGame) continue;
                if (currentContext == null || currentContext.Context == "None") continue;

                string contextName = currentContext.Context;
                ContextCache cache = cacheMap[contextName];
                Contexts ctx = GetContext(contextName);
                if (ctx == null) continue;

                string dir = ModPath + MusicDirectory + "/" + ctx.Path + "/";

                string[] currentFiles = null;
                bool scanDone = false;
                string[] previousFiles = cache.directorySnapshot ?? new string[0];

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    currentFiles = Directory.Exists(dir)
                        ? Directory.GetFiles(dir).Where(f => NAudioDecoder.IsSupported(f)).ToArray()
                        : new string[0];
                    scanDone = true;
                });
                while (!scanDone) yield return null;

                Func<string, string> norm = p => string.IsNullOrEmpty(p) ? p
                    : Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                var currentNorm = currentFiles.Select(norm).ToArray();
                var previousNorm = previousFiles.Select(norm).ToArray();

                bool changed = currentNorm.Length != previousNorm.Length ||
                               currentNorm.Except(previousNorm, StringComparer.OrdinalIgnoreCase).Any() ||
                               previousNorm.Except(currentNorm, StringComparer.OrdinalIgnoreCase).Any();
                if (!changed) continue;

                var removedFiles = previousNorm.Except(currentNorm, StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (string removed in removedFiles)
                {
                    _validatedFiles.Remove(removed);
                    _invalidFiles.Remove(removed);
                }

                var newFiles = currentNorm.Except(previousNorm, StringComparer.OrdinalIgnoreCase).ToArray();
                if (newFiles.Length > 0)
                {
                    Log.Out($"[CMM] Validating {newFiles.Length} new file(s) in {contextName}...");
                    foreach (string newFile in newFiles)
                    {
                        bool isValid = false;
                        yield return StartCoroutine(ValidateFile(newFile, result => isValid = result));
                        if (!isValid)
                        {
                            _invalidFiles.Add(newFile);
                            Log.Warning($"[CMM] New file failed validation and will be ignored: {Path.GetFileName(newFile)}");
                        }
                    }
                    currentFiles = currentFiles.Where(f => !_invalidFiles.Contains(norm(f))).ToArray();
                    currentNorm = currentFiles.Select(norm).ToArray();
                }

                Log.Out($"[CMM] File change detected in {contextName}: {previousFiles.Length} -> {currentFiles.Length} files");
                cache.directorySnapshot = currentFiles;

                // FIX: Invalidate only this context's entry, not the whole cache.
                InvalidateContextFileCache(contextName);

                bool lastPlayedMissing = false;
                if (!string.IsNullOrEmpty(cache.lastPlayedFile))
                {
                    string lastNorm = norm(cache.lastPlayedFile);
                    if (!currentNorm.Any(f => string.Equals(f, lastNorm, StringComparison.OrdinalIgnoreCase)))
                    {
                        Log.Out($"[CMM] Previously-played file missing for {contextName}: {Path.GetFileName(cache.lastPlayedFile)}");
                        cache.lastPlayedFile = null;
                        if (cache.cachedClip != null)
                        {
                            _clipToDestroyAfterFade = cache.cachedClip;
                            _decodedToReleaseAfterFade = cache.decodedAudio;
                            cache.cachedClip = null;
                            cache.decodedAudio = null;
                        }
                        lastPlayedMissing = true;
                    }
                }

                if (currentFiles.Length == 0)
                {
                    Log.Out($"[CMM] {contextName} folder emptied, re-evaluating context.");
                    string fallback = ResolvePlayableContext(ctx.Fallback) ?? "Ambient";
                    _pendingContext = null;
                    SendContextUpdate(fallback, currentContext.Priority + 1);
                }
                else if (lastPlayedMissing)
                {
                    _pendingContext = null;
                    if (CMMConfig.PreloadCacheEnabled)
                        yield return StartCoroutine(PreloadContext(ctx.Context));
                    SendContextUpdate(ctx.Context, currentContext.Priority + 1);
                }
                else
                {
                    string top = GetTopContext();
                    if (top != currentContext.Context)
                    {
                        _pendingContext = null;
                        SendContextUpdate(top, 0);
                    }
                }
            }
        }

        // ── Preload ───────────────────────────────────────────────────────────
        private IEnumerator PreloadAllContexts()
        {
            if (!CMMConfig.PreloadCacheEnabled) { Log.Out("[CMM] Preload cache disabled — skipping."); yield break; }
            Log.Out("[CMM] Starting sequential preload for all contexts...");
            foreach (var ctx in contexts.Values)
            {
                if (ctx.Context == "None") continue;
                yield return StartCoroutine(PreloadContext(ctx.Context));
            }
            Log.Out("[CMM] Preload complete.");
        }

        private IEnumerator PreloadContext(string contextName, string avoidFile = null)
        {
            ContextCache cache = cacheMap[contextName];
            string filePath;

            if (cache.lastPlayedFile != null && File.Exists(cache.lastPlayedFile)
                && cache.lastPlayedFile != avoidFile
                && !_invalidFiles.Contains(cache.lastPlayedFile))
                filePath = cache.lastPlayedFile;
            else
            {
                cache.resumePosition = 0f;
                cache.lastPlayedFile = null;
                filePath = PickNextFile(contextName, avoidFile: avoidFile);
            }

            if (filePath == null)
            {
                Contexts ctx = GetContext(contextName);
                string fallback = ctx?.Fallback ?? "None";
                if (fallback != "None" && fallback != contextName)
                    filePath = PickNextFile(fallback, avoidFile: null);
                if (filePath == null)
                { Log.Out($"[CMM] Preload: no files for {contextName}"); yield break; }
            }

            // Validate
            if (!_validatedFiles.Contains(filePath))
            {
                bool isValid = false;
                yield return StartCoroutine(ValidateFile(filePath, result => isValid = result));
                if (isValid)
                {
                    _validatedFiles.Add(filePath);
                }
                else
                {
                    _invalidFiles.Add(filePath);
                    Log.Warning($"[CMM] Preload: {Path.GetFileName(filePath)} failed validation, retrying.");
                    // FIX: Pass the original invalid file as avoidFile so we don't
                    // immediately re-pick it, then validate exactly once more.
                    // If the replacement is also invalid we give up rather than
                    // looping silently.
                    string replacement = PickNextFile(contextName, avoidFile: filePath);
                    if (replacement == null) { Log.Out($"[CMM] Preload: no valid files for {contextName}"); yield break; }
                    yield return StartCoroutine(ValidateFile(replacement, result => isValid = result));
                    if (!isValid)
                    {
                        _invalidFiles.Add(replacement);
                        Log.Warning($"[CMM] Preload: replacement also invalid for {contextName}. Giving up.");
                        yield break;
                    }
                    _validatedFiles.Add(replacement);
                    filePath = replacement;
                }
            }

            // ── NAudio path ───────────────────────────────────────────────────
            if (CMMConfig.UseNAudioDecoder)
            {
                DecodedAudio decoded = null;
                yield return StartCoroutine(DecodeAsync(filePath, d => decoded = d));

                if (decoded == null)
                {
                    Log.Error($"[CMM] Preload NAudio decode failed for {contextName}: {Path.GetFileName(filePath)}");
                    _invalidFiles.Add(filePath);
                    yield break;
                }

                AudioClip clip = NAudioDecoder.CreateStreamingClip(decoded, Path.GetFileNameWithoutExtension(filePath));
                if (clip == null)
                {
                    Log.Error($"[CMM] Preload: CreateStreamingClip returned null for {contextName}");
                    yield break;
                }

                if (cache.cachedClip != null) Destroy(cache.cachedClip);
                if (!CMMConfig.PreloadCacheEnabled && cache.decodedAudio != null)
                    cache.decodedAudio.Samples = null;

                cache.cachedClip = clip;
                cache.decodedAudio = decoded;
                cache.lastPlayedFile = filePath;
                Log.Out($"[CMM] Preloaded (NAudio): {contextName} — {Path.GetFileName(filePath)} ({decoded.LengthSeconds:F1}s)");
            }
            else
            {
                // ── Unity fallback path ───────────────────────────────────────
                AudioType audioType = NAudioDecoder.GetUnityAudioType(filePath);
                if (audioType == AudioType.UNKNOWN) yield break;

                using (var request = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(
                           "file:///" + filePath.Replace("\\", "/"), audioType))
                {
                    ((UnityEngine.Networking.DownloadHandlerAudioClip)request.downloadHandler).streamAudio = CMMConfig.UnityStreamingEnabled;
                    yield return request.SendWebRequest();
                    if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    { Log.Error($"[CMM] Preload Unity failed for {contextName}: {request.error}"); yield break; }

                    if (cache.cachedClip != null) Destroy(cache.cachedClip);
                    cache.cachedClip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(request);
                    cache.lastPlayedFile = filePath;
                    Log.Out($"[CMM] Preloaded (Unity): {contextName}");
                }
            }
        }

        // FIX: ContextCooldownTimer is now per-context. The cooldown is
        // recorded against the specific context name that triggered it, so
        // unrelated contexts at higher priority are never blocked.
        private IEnumerator ContextCooldownTimer(string contextName, int seconds)
        {
            _contextsOnCooldown.Add(contextName);
            Log.Out($"[CMM] Cooldown started for {contextName}: {seconds}s");
            yield return new WaitForSeconds(seconds);
            _contextsOnCooldown.Remove(contextName);
            Log.Out($"[CMM] Cooldown expired for {contextName}.");
            _cooldownCoroutine = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SECTION 8 — MUSIC STATES
    // ─────────────────────────────────────────────────────────────────────────

    public static class MusicStates
    {
        public static bool DefaultMusicEnabled = false;
        public static void ToggleDefaultMusic()
        {
            DefaultMusicEnabled = !DefaultMusicEnabled;
            Log.Error($"[CMM] Default music enabled: {DefaultMusicEnabled}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SECTION 9 — HARMONY PATCHES
    // ─────────────────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(DynamicMusic.Conductor), "Update")]
    public class InGameMusicPatch
    {
        static bool firstMessageLogged = false;
        static bool Prefix()
        {
            if (MusicStates.DefaultMusicEnabled) return true;
            if (!firstMessageLogged) { Log.Out("[CMM] Dynamic Music System ticks ignored!"); firstMessageLogged = true; }

            if (!ContextMusicManager.inGame)
            {
                Log.Out("[CMM] Entering game world");
                ContextMusicManager.inGame = true;
                ContextMusicManager.FadeToSilence();
                ContextMusicManager.OnEnterGame();
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(BackgroundMusicMono), "Play")]
    public class MenuMusicPatch
    {
        static bool Prefix()
        {
            if (!ContextMusicManager.inGame)
                ContextMusicManager.SendContextUpdate("Menu");
            return false;
        }
    }

    [HarmonyPatch(typeof(GameManager), "SaveAndCleanupWorld")]
    public class Patch_GameEnded
    {
        static void Prefix()
        {
            ContextMusicManager.inInventory = false;
            ContextMusicManager.inGame = false;
            ContextMusicManager._contextStack.Clear();
            ContextMusicManager._pendingContext = "None";
            ContextResolver.resetValues();
            ContextMusicManager.SendContextUpdate("Menu");
        }
    }

    [HarmonyPatch(typeof(EntityPlayerLocal), "AfterPlayerRespawn")]
    public class PlayerRespawn
    {
        static void Postfix()
        {
            Log.Out("[CMM] Player respawned");
            ContextMusicManager._contextStack.Clear();
            List<string> worldContexts = ContextResolver.ResolveContext();
            foreach (string ctxName in worldContexts)
            {
                Contexts ctx = ContextMusicManager.GetContext(ctxName);
                if (ctx == null) continue;
                ContextMusicManager._contextStack.Add(new ActiveContext
                {
                    ContextName = ctxName,
                    Priority = ctx.Priority,
                    ActivatedAt = Time.realtimeSinceStartup,
                    Manual = false
                });
            }
            ContextMusicManager._contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            if (ContextMusicManager.playerIsDead)
                ContextMusicManager.SendContextUpdate("Spawn");
            ContextMusicManager.playerIsDead = false;
        }
    }

    [HarmonyPatch(typeof(EntityPlayerLocal), "OnDeathUpdate")]
    public class PlayerDeath
    {
        static void Postfix()
        {
            if (!ContextMusicManager.playerIsDead)
            {
                ContextMusicManager.playerIsDead = true;
                ContextMusicManager.SendContextUpdate("Death");
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), "Awake")]
    public class StartupLogPatch
    {
        static void Postfix() => Log.Out("[CMM] Game started up!");
    }

    [HarmonyPatch(typeof(EntityPlayer), "Awake")]
    public static class Patch_SpawnStinger
    {
        public static void Prefix() => Log.Error("[CMM] Player spawned into world");
    }

    public static class AudioStingerHandler
    {
        public static bool Handle(string soundGroupName)
        {
            Log.Out($"[CMM Audio Spy] PlayInsidePlayerHead: {soundGroupName}");
            switch (soundGroupName)
            {
                case "player_death_stinger": return false;
                case "spawnInStinger": return false;
                default: return true;
            }
        }
    }

    [HarmonyPatch(typeof(Audio.Manager), "PlayInsidePlayerHead",
        new Type[] { typeof(string), typeof(int), typeof(float), typeof(bool), typeof(bool) })]
    public class Patch_LogPlayInsidePlayerHead
    {
        static bool Prefix(string soundGroupName) => AudioStingerHandler.Handle(soundGroupName);
    }

    [HarmonyPatch(typeof(Audio.Manager), "PlayInsidePlayerHead",
        new Type[] { typeof(string), typeof(int) })]
    public class Patch_LogPlayInsidePlayerHead2
    {
        static bool Prefix(string soundGroupNameBegin) => AudioStingerHandler.Handle(soundGroupNameBegin);
    }
}