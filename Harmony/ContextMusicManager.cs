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
using CMM;




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
        public static long HotSwapMaxFileSizeBytes = 100L * 1024 * 1024;
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
        // ── Streaming path (NAudio / SeekableOpusReader) ──────────────────────
        // The WaveStream is kept alive for the entire life of the AudioClip
        // because the PCMReaderCallback reads from it on the audio thread.
        // Disposing it before Destroy(clip) is called is a hard error.
        public WaveStream currentReader = null;
        public WaveStream nextReader = null;

        // ── Full-decode path (Opus only, or PreloadCacheEnabled = true) ───────
        public DecodedAudio decodedAudio = null;
        public DecodedAudio nextDecodedAudio = null;

        // ── Shared ────────────────────────────────────────────────────────────
        public AudioClip cachedClip = null;
        public AudioClip nextCachedClip = null;
        public float resumePosition = 0f;
        public float lastLeftTime = -1f;
        public string lastPlayedFile = null;
        public int sequentialIndex = 0;
        public string[] directorySnapshot = null;
        public bool nextClipLoading = false;

        // Safe disposal helper — call after Destroy(clip) (or after a deferred
        // frame) so the audio thread is guaranteed off the reader.
        public void DisposeCurrentReader()
        {
            if (currentReader != null) { try { currentReader.Dispose(); } catch { } currentReader = null; }
            if (decodedAudio != null) { decodedAudio.Samples = null; decodedAudio = null; }
        }

        public void DisposeNextReader()
        {
            if (nextReader != null) { try { nextReader.Dispose(); } catch { } nextReader = null; }
            if (nextDecodedAudio != null) { nextDecodedAudio.Samples = null; nextDecodedAudio = null; }
        }
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
    // ─────────────────────────────────────────────────────────────────────────
    // REPLACES: SECTION 3 — NAudio DECODER  (the entire #if NAUDIO block)
    // The #else stub at the bottom is unchanged — keep it as-is.
    // ─────────────────────────────────────────────────────────────────────────

#if NAUDIO
    public static class NAudioDecoder
    {
        private static readonly HashSet<string> _naudioExts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".wav", ".wave", ".aiff", ".aif", ".ogg", ".oga", ".opus", ".flac" };

        private static readonly HashSet<string> _unityExts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".ogg", ".wav" };

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

        // Returns true if this file must be fully decoded before playback
        // (i.e. it is an Opus stream AND the caller has not yet built a page
        // index for it — which OpenStreamingReader handles automatically).
        // Exposed so callers can log the code path taken.
        public static bool IsOpusFile(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath).ToLower();
            return ext == ".opus"
                || ((ext == ".ogg" || ext == ".oga") && IsOpusOgg(filePath));
        }

        // ═════════════════════════════════════════════════════════════════════
        // OGG PAGE INDEX
        // Scans an Ogg bitstream reading only page headers (27 bytes each plus
        // the segment table) — no audio data is decoded.  For each page it
        // stores the granule position (= last sample frame in the page) and
        // the byte offset of the start of that page.
        //
        // This is all that is needed for seeking: given a target sample frame,
        // binary-search the table for the largest granule ≤ target, seek the
        // FileStream to that page's byte offset, and resume decoding.  The
        // worst-case decode-to-discard distance is one page (typically 20 ms).
        // ═════════════════════════════════════════════════════════════════════
        public class OggPageIndex
        {
            // Parallel arrays — kept as value arrays (not List<struct>) to
            // minimise GC pressure; the index is built once and then read-only.
            public readonly long[] GranulePositions;   // sample frame of last sample in page
            public readonly long[] ByteOffsets;        // byte offset of page start in file
            public readonly int Count;
            public readonly int Channels;           // from OpusHead packet
            public readonly long TotalSamples;       // granule of last page

            private OggPageIndex(long[] granules, long[] offsets, int count,
                                  int channels, long totalSamples)
            {
                GranulePositions = granules;
                ByteOffsets = offsets;
                Count = count;
                Channels = channels;
                TotalSamples = totalSamples;
            }

            // ── Build ─────────────────────────────────────────────────────────
            // Called on a background thread.  Reads the file twice in the worst
            // case: once to count pages (to pre-allocate arrays), once to fill.
            // In practice a single forward pass with a growing list is fine for
            // files up to ~600MB (a 30-min track).
            public static OggPageIndex Build(string filePath)
            {
                using (var fs = new FileStream(filePath, FileMode.Open,
                                               FileAccess.Read, FileShare.Read,
                                               bufferSize: 65536))
                    return BuildFromStream(fs, filePath);
            }

            // Finds the index of the last entry with GranulePosition ≤ target.
            // Returns 0 if target is before the first indexed page (so we seek
            // to the beginning of the audio data).
            public int FindPageForSample(long targetSample)
            {
                if (Count == 0) return 0;
                int lo = 0, hi = Count - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) >> 1;
                    if (GranulePositions[mid] <= targetSample)
                        lo = mid;
                    else
                        hi = mid - 1;
                }
                return lo;
            }

            public static OggPageIndex BuildFromBytes(byte[] data, string debugName = "")
            {
                // Reuses the exact same page-scanning logic as Build(filePath).
                // We wrap the byte[] in a MemoryStream — MemoryStream.Read is just
                // an Array.Copy internally, so this is as fast as a cached read.
                using (var ms = new MemoryStream(data, writable: false))
                {
                    // (The implementation is identical to Build(); replace the
                    //  `new FileStream(...)` line with `ms` and remove the `using`
                    //  wrapper for the outer stream — everything else is unchanged.)
                    return BuildFromStream(ms, debugName);
                }
            }

            private static OggPageIndex BuildFromStream(Stream fs, string debugName)
            {
                const int HeaderSize = 27;
                var granules = new List<long>(512);
                var offsets = new List<long>(512);
                int channels = 2;
                long fileLen = fs.Length;
                var hdr = new byte[HeaderSize + 255];

                while (fs.Position < fileLen - HeaderSize)
                {
                    long pageStart = fs.Position;
                    int got = ReadFully(fs, hdr, 0, HeaderSize);
                    if (got < HeaderSize) break;

                    if (hdr[0] != 'O' || hdr[1] != 'g' || hdr[2] != 'g' || hdr[3] != 'S')
                    { fs.Position = pageStart + 1; continue; }

                    long granule = ReadInt64LE(hdr, 6);
                    int nSegs = hdr[26];

                    got = ReadFully(fs, hdr, 0, nSegs);
                    if (got < nSegs) break;

                    int bodySize = 0;
                    for (int i = 0; i < nSegs; i++) bodySize += hdr[i];

                    if (granule > 0) { granules.Add(granule); offsets.Add(pageStart); }

                    if (channels == 2 && granules.Count == 0 && bodySize >= 10)
                    {
                        long bodyStart = fs.Position;
                        var magic = new byte[10];
                        if (ReadFully(fs, magic, 0, 10) == 10 &&
                            magic[0] == 'O' && magic[1] == 'p' && magic[2] == 'u' && magic[3] == 's' &&
                            magic[4] == 'H' && magic[5] == 'e' && magic[6] == 'a' && magic[7] == 'd')
                            channels = magic[9];
                        fs.Position = bodyStart + bodySize;
                    }
                    else
                    {
                        fs.Position += bodySize;
                    }
                }

                long total = granules.Count > 0 ? granules[granules.Count - 1] : 0;
                return new OggPageIndex(granules.ToArray(), offsets.ToArray(),
                                        granules.Count, channels, total);
            }

            private static int ReadFully(Stream s, byte[] buf, int offset, int count)
            {
                int total = 0;
                while (total < count)
                {
                    int r = s.Read(buf, offset + total, count - total);
                    if (r == 0) break;
                    total += r;
                }
                return total;
            }

            private static long ReadInt64LE(byte[] b, int o) =>
                (long)b[o] | ((long)b[o + 1] << 8) | ((long)b[o + 2] << 16) |
                ((long)b[o + 3] << 24) | ((long)b[o + 4] << 32) | ((long)b[o + 5] << 40) |
                ((long)b[o + 6] << 48) | ((long)b[o + 7] << 56);
        }

        // ═════════════════════════════════════════════════════════════════════
        // SEEKABLE OPUS READER
        // Implements WaveStream so it slots directly into CreateStreamingClipFromReader.
        //
        // Seeking mechanics:
        //   • Forward seek within the current page: just decode-and-discard.
        //   • Any other seek: look up the nearest prior page in OggPageIndex,
        //     reposition the FileStream to that page's byte offset, create a
        //     fresh OpusOggReadStream from that position, then discard packets
        //     until we reach the target granule.
        //
        // The PCMReaderCallback fills `data` from a small float[] decode buffer
        // (_pcmBuf) that holds exactly one decoded Opus packet at a time.  No
        // large allocation ever lives longer than one packet.
        //
        // Thread safety: the lock (_lock) guards all mutable state because
        // Unity's audio thread calls Read() while the main thread may call
        // Position.set (via PCMSetPositionCallback).
        // ═════════════════════════════════════════════════════════════════════
        public class SeekableOpusReader : WaveStream
        {
            private readonly string _filePath;
            private readonly OggPageIndex _index;
            private readonly WaveFormat _format;

            // Live decode state — all guarded by _lock
            private readonly object _lock = new object();
            private Stream _fileStream;
            private OpusOggReadStream _oggStream;
            private OpusDecoder _decoder;
            private long _samplePos;   // current position in frames

            // One-packet decode buffer.  Filled by DecodeNextPacketLocked(),
            // drained by Read().  Never allocated larger than one Opus frame
            // (120ms × channels × 48kHz = 5760 samples × channels).
            private float[] _pcmBuf = Array.Empty<float>();
            private int _pcmBufStart = 0;
            private int _pcmBufEnd = 0;   // exclusive
            private float[] _readBuf = new float[8192]; // covers typical Unity audio buffer sizes



            public SeekableOpusReader(string filePath, OggPageIndex index)
            {
                _filePath = filePath;
                _index = index;
                _format = WaveFormat.CreateIeeeFloatWaveFormat(48000, index.Channels);
                // Open the FileStream once here and keep it for the lifetime of the reader.
                _fileStream = new FileStream(filePath, FileMode.Open,
                                             FileAccess.Read, FileShare.Read,
                                             bufferSize: 65536);
                _decoder = new OpusDecoder(48000, index.Channels);
                OpenStreamAtByte(0);
            }

            // ── WaveStream overrides ──────────────────────────────────────────
            public override WaveFormat WaveFormat => _format;

            // Length in bytes (IEEE float = 4 bytes per sample per channel).
            public override long Length =>
                _index.TotalSamples * _format.Channels * 4L;

            public override long Position
            {
                get
                {
                    lock (_lock) return _samplePos * _format.Channels * 4L;
                }
                set
                {
                    lock (_lock) SeekToSampleLocked(value / (_format.Channels * 4L));
                }
            }

            // Read is called by Unity's audio thread inside the PCMReaderCallback.
            // `buffer` is a float[] cast to byte[] by NAudio's SampleChannel.
            // We implement it as a byte-level read and let SampleChannel handle
            // the conversion — but since we declare IEEE float format, NAudio
            // will use us directly as an ISampleProvider.
            public override int Read(byte[] buffer, int offset, int count)
            {
                int floatCount = count / 4;
                if (_readBuf.Length < floatCount)
                    _readBuf = new float[floatCount]; // only grows, never shrinks
                int filled = ReadFloats(_readBuf, 0, floatCount);
                Buffer.BlockCopy(_readBuf, 0, buffer, offset, filled * 4);
                return filled * 4;
            }

            // Called directly by CreateStreamingClipFromReader's PCMReaderCallback
            // which already works in float[].  Avoids the byte[] round-trip.
            public int ReadFloats(float[] data, int offset, int count)
            {
                lock (_lock)
                {
                    int written = 0;
                    while (written < count)
                    {
                        // Drain the packet buffer first
                        int available = _pcmBufEnd - _pcmBufStart;
                        if (available > 0)
                        {
                            int copy = Math.Min(available, count - written);
                            Array.Copy(_pcmBuf, _pcmBufStart, data, offset + written, copy);
                            _pcmBufStart += copy;
                            written += copy;
                            _samplePos += copy / _format.Channels;
                            continue;
                        }

                        // Buffer empty — decode the next packet
                        if (!FillPacketBufferLocked()) break; // end of stream
                    }
                    return written;
                }
            }

            private int _seekCount = 0;
            // ── Seeking ───────────────────────────────────────────────────────
            private void SeekToSampleLocked(long targetFrame)
            {
                _seekCount++;

                if (_index.Count == 0)
                {
                    OpenStreamAtByte(0);
                    _samplePos = 0;
                    return;
                }

                int pageIdx        = _index.FindPageForSample(targetFrame);
                long pageByteOffset = _index.ByteOffsets[pageIdx];

                const float fastForwardWindowSeconds = 2f;
                bool canFastForward =
                    _samplePos <= targetFrame &&
                    (targetFrame - _samplePos) < (long)(48000 * fastForwardWindowSeconds);

                if (!canFastForward)
                {
                    // Log outside hot path is not possible here since we hold _lock,
                    // but reopening is rare (only on context resume / loop), so a
                    // brief log is acceptable at that frequency.
                    // DO NOT log on every seek — only on reopen.
                    UnityEngine.Debug.Log($"[CMM] OpusReopen seek #{_seekCount} → frame {targetFrame}");
                    OpenStreamAtByte(pageByteOffset);
                }

                long discard = targetFrame - _samplePos;
                if (discard > 0)
                    DiscardSamplesLocked(discard * _format.Channels);
            }

            private void DiscardSamplesLocked(long floatCount)
            {
                // Drain existing buffer first
                long fromBuf = Math.Min(_pcmBufEnd - _pcmBufStart, floatCount);
                _pcmBufStart += (int)fromBuf;
                _samplePos += fromBuf / _format.Channels;
                floatCount -= fromBuf;

                // Decode-and-discard remaining packets
                while (floatCount > 0)
                {
                    if (!FillPacketBufferLocked()) break;
                    long take = Math.Min(_pcmBufEnd - _pcmBufStart, floatCount);
                    _pcmBufStart += (int)take;
                    _samplePos += take / _format.Channels;
                    floatCount -= take;
                }
            }

            // ── Internal helpers ──────────────────────────────────────────────
            private void OpenStreamAtByte(long byteOffset)
            {
                var t0 = ProfStart(); // ← add this
                _fileStream.Position = byteOffset;
                _decoder = new OpusDecoder(48000, _format.Channels);
                _oggStream = new OpusOggReadStream(_decoder, _fileStream);
                _pcmBufStart = 0;
                _pcmBufEnd = 0;

                // Estimate sample position from granule of the nearest prior page
                if (byteOffset == 0)
                {
                    _samplePos = 0;
                }
                else
                {
                    // Walk backwards to find matching byte offset
                    for (int i = _index.Count - 1; i >= 0; i--)
                    {
                        if (_index.ByteOffsets[i] <= byteOffset)
                        {
                            // Granule is the LAST sample of the page, so actual
                            // current position after opening here is approximately
                            // that granule minus one page's worth.  We correct by
                            // the discard loop in SeekToSampleLocked.
                            _samplePos = Math.Max(0, _index.GranulePositions[i]
                                                  - 48000 / 25); // ~40ms safety margin
                            break;
                        }
                    }
                }
                ProfEnd(t0, $"OpenStreamAtByte({byteOffset})", 2f);
            }

            public SeekableOpusReader(byte[] rawData, OggPageIndex index, string debugName = "")
            {
                _filePath = debugName;
                _index = index;
                _format = WaveFormat.CreateIeeeFloatWaveFormat(48000, index.Channels);

                // Wrap the byte[] in a MemoryStream.  MemoryStream does NOT hold an OS
                // handle, so the original file can be overwritten/deleted immediately.
                // The MemoryStream is stored in _fileStream (typed as Stream, not FileStream)
                // — change the field type from FileStream to Stream.
                _fileStream = new MemoryStream(rawData, writable: false);
                _decoder = new OpusDecoder(48000, index.Channels);
                OpenStreamAtByte(0);
            }

            // Decodes the next Ogg/Opus packet into _pcmBuf.
            // Returns false at end of stream.
            private bool FillPacketBufferLocked()
            {
                long t0 = Stopwatch.GetTimestamp();
                if (_oggStream == null || !_oggStream.HasNextPacket)
                    return false;

                short[] packet = _oggStream.DecodeNextPacket();
                if (packet == null || packet.Length == 0) return false;

                // Grow the float buffer only when necessary (rare — Opus packet
                // sizes are fixed per frame duration).
                if (_pcmBuf.Length < packet.Length)
                    _pcmBuf = new float[packet.Length];

                for (int i = 0; i < packet.Length; i++)
                    _pcmBuf[i] = packet[i] / 32768f;

                _pcmBufStart = 0;
                _pcmBufEnd = packet.Length;
                float ms = (Stopwatch.GetTimestamp() - t0) * 1000f / Stopwatch.Frequency;
                if (ms > 1f) Log.Warning($"[CMM PERF] OpusDecodePacket: {ms:F2}ms");
                return true;
            }

            // ── IDisposable ───────────────────────────────────────────────────
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    lock (_lock)
                    {
                        _oggStream = null;
                        _fileStream?.Dispose();
                        _fileStream = null;
                    }
                }
                base.Dispose(disposing);
            }
        }

        // ── OGG codec detection ───────────────────────────────────────────────

        private static long _lastProfileTime;

        private static long ProfStart() => Stopwatch.GetTimestamp();

        private static void ProfEnd(long t0, string label, float warnMs = 2f)
        {
            float ms = (Stopwatch.GetTimestamp() - t0) * 1000f / Stopwatch.Frequency;
            if (ms > warnMs) Log.Warning($"[CMM PERF] {label}: {ms:F2}ms");
        }
        public static bool IsOpusOgg(string filePath)
        {
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open,
                                               FileAccess.Read, FileShare.Read))
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

        private static bool IsOpusOggBytes(byte[] data)
        {
            if (data == null || data.Length < 36) return false;
            for (int i = 0; i <= Math.Min(data.Length - 8, 64); i++)
            {
                if (data[i] == 'O' && data[i + 1] == 'p' &&
                    data[i + 2] == 'u' && data[i + 3] == 's' &&
                    data[i + 4] == 'H' && data[i + 5] == 'e' &&
                    data[i + 6] == 'a' && data[i + 7] == 'd')
                    return true;
            }
            return false;
        }

        // ── Streaming reader factory ──────────────────────────────────────────
        // Returns an open WaveStream for any supported format.
        // For Opus, builds the OggPageIndex first then wraps it in
        // SeekableOpusReader.  For everything else, returns the NAudio reader
        // directly.  The caller owns the returned stream and must Dispose() it
        // when the associated AudioClip is destroyed.
        //
        // Called on a background thread by OpenReaderAsync.
        public static WaveStream OpenStreamingReader(string filePath)
        {
            if (!Thread.CurrentThread.IsBackground)
                Log.Warning($"[CMM PERF] OpenStreamingReader called on main thread for {Path.GetFileName(filePath)}!");

            byte[] raw = null;
            try
            {
                long size = new FileInfo(filePath).Length;
                if (size <= CMMConfig.HotSwapMaxFileSizeBytes)
                    raw = File.ReadAllBytes(filePath);
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] OpenStreamingReader: could not buffer {Path.GetFileName(filePath)}: {ex.Message}");
            }

            string ext = Path.GetExtension(filePath).ToLower();

            // ← REPLACES the inline IsOpusOgg call in the if-condition below
            bool isOpus = ext == ".opus" ||
                ((ext == ".ogg" || ext == ".oga") &&
                 (raw != null ? IsOpusOggBytes(raw) : IsOpusOgg(filePath)));

            if (isOpus)
            {
                if (raw != null)
                {
                    OggPageIndex index = OggPageIndex.BuildFromBytes(raw, filePath);
                    return new SeekableOpusReader(raw, index, filePath);
                }
                else
                {
                    Log.Warning($"[CMM] {Path.GetFileName(filePath)} too large to buffer ...");
                    OggPageIndex index = OggPageIndex.Build(filePath);
                    return new SeekableOpusReader(filePath, index);
                }
            }

            if (raw != null)
                return OpenReaderFromMemory(raw, ext, filePath);
            else
                return OpenReader(filePath);
        }
        private static WaveStream OpenReaderFromMemory(byte[] raw, string ext, string filePath)
        {
            var ms = new MemoryStream(raw, writable: false);
            try
            {
                switch (ext)
                {
                    case ".mp3": return new Mp3FileReader(ms);
                    case ".wav":
                    case ".wave": return new WaveFileReader(ms);
                    case ".aiff":
                    case ".aif":
                        // AiffFileReader has no Stream constructor in all versions;
                        // fall back to file path (rare format in practice).
                        ms.Dispose();
                        return new AiffFileReader(filePath);
                    case ".ogg":
                    case ".oga": return new VorbisWaveReader(ms);
                    case ".flac":
                        // FlacReader may not have a Stream ctor — check your version.
                        // If it does: return new FlacReader(ms);
                        ms.Dispose();
                        return new FlacReader(filePath);
                    default:
                        ms.Dispose();
                        Log.Warning($"[CMM NAudio] No in-memory reader for extension '{ext}'");
                        return null;
                }
            }
            catch
            {
                ms.Dispose();
                throw;
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

        // Stream-based variant — used by the legacy DecodeFileFromBytes path.
        private static WaveStream OpenReaderFromStream(Stream stream, string ext, string filePath)
        {
            switch (ext)
            {
                case ".mp3": return new Mp3FileReader(stream);
                case ".wav":
                case ".wave": return new WaveFileReader(stream);
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

        // ── Streaming AudioClip factory ───────────────────────────────────────
        // Creates an AudioClip whose PCMReaderCallback reads directly from the
        // live WaveStream.  No full-decode allocation; memory cost ≈ one Opus
        // packet buffer or NAudio's internal read buffer (a few KB at most).
        //
        // IMPORTANT: `reader` must stay alive (not disposed) until after
        // Destroy(clip) has been called AND at least one frame has passed so
        // the audio thread is off the clip.  ContextCache.DisposeCurrentReader()
        // handles this correctly when called from DeferredDestroyPending().
        public static AudioClip CreateStreamingClipFromReader(
            WaveStream reader, string name)
        {
            if (reader == null) return null;

            // Normalise to float samples regardless of source encoding.
            ISampleProvider src = reader.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                ? reader.ToSampleProvider()
                : new SampleChannel(reader, forceStereo: false);

            int channels = src.WaveFormat.Channels;
            int sampleRate = src.WaveFormat.SampleRate;
            int totalSamples = reader.Length > 0 && reader.WaveFormat.BlockAlign > 0
                ? (int)(reader.Length / reader.WaveFormat.BlockAlign)
                : 0;

            // For SeekableOpusReader the length is in bytes of IEEE float;
            // convert back to frames for AudioClip.
            if (reader is SeekableOpusReader opusReader)
            {
                totalSamples = (int)(reader.Length / (channels * 4L));
                // Bypass SampleChannel for Opus — SeekableOpusReader already
                // outputs IEEE float and has its own ReadFloats fast path.
                var directSrc = (SeekableOpusReader)reader;
                var readLock = new object(); // shared with PCMSetPositionCallback

                return AudioClip.Create(name, totalSamples, channels, sampleRate, true,
                    (float[] data) =>
                    {
                        var t0 = ProfStart();
                        directSrc.ReadFloats(data, 0, data.Length);
                        ProfEnd(t0, "PCMRead_Opus", 1f);
                    },
                    (int newPosition) =>
                    {
                        // Unity passes sample-frame position (not float index).
                        // Position property on SeekableOpusReader expects bytes.
                        reader.Position = (long)newPosition * channels * 4L;
                    }
                );
            }

            // Non-Opus: generic WaveStream path with a lock.
            // NAudio's readers are not thread-safe; the audio thread reads while
            // the main thread may seek (on resume or loop).
            var streamLock = new object();

            return AudioClip.Create(name, totalSamples, channels, sampleRate, true,
                (float[] data) =>
                {
                    lock (streamLock)
                    {
                        int read = src.Read(data, 0, data.Length);
                        if (read < data.Length)
                            Array.Clear(data, read, data.Length - read);
                    }
                },
                (int newPosition) =>
                {
                    lock (streamLock)
                    {
                        if (reader.WaveFormat.BlockAlign > 0)
                            reader.Position = ClampLong(
                                (long)newPosition * reader.WaveFormat.BlockAlign,
                                0L, reader.Length - reader.WaveFormat.BlockAlign);
                    }
                }
            );
        }

        public static long ClampLong(long value, long min, long max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ── Legacy full-decode path (kept for PreloadCacheEnabled = true) ─────
        public static DecodedAudio DecodeFileFromBytes(byte[] rawBytes, string filePath)
        {
            try
            {
                string ext = System.IO.Path.GetExtension(filePath).ToLower();
                if (ext == ".opus" || ((ext == ".ogg" || ext == ".oga") && IsOpusOggBytes(rawBytes)))
                    return DecodeOpusFromBytes(rawBytes, filePath);

                using (var ms = new MemoryStream(rawBytes, writable: false))
                using (WaveStream reader = OpenReaderFromStream(ms, ext, filePath))
                {
                    if (reader == null) return null;
                    return DecodeWaveStream(reader, filePath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM NAudio] DecodeFileFromBytes failed for {Path.GetFileName(filePath)}: {ex.Message}");
                return null;
            }
        }

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

        private static DecodedAudio DecodeOpus(string filePath)
        {
            try { return DecodeOpusFromBytes(File.ReadAllBytes(filePath), filePath); }
            catch (Exception ex)
            {
                Log.Warning($"[CMM Concentus] Opus decode failed for {Path.GetFileName(filePath)}: {ex.Message}");
                return null;
            }
        }

        private static DecodedAudio DecodeWaveStream(WaveStream reader, string filePath)
        {
            ISampleProvider samples = reader.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                ? reader.ToSampleProvider()
                : new SampleChannel(reader, forceStereo: false);

            int channels = samples.WaveFormat.Channels;
            int frequency = samples.WaveFormat.SampleRate;

            const long MaxFloats = 48000L * 2 * 60 * 30;
            long estimatedFloats = (reader.Length > 0 && reader.WaveFormat.BlockAlign > 0)
                ? (reader.Length / reader.WaveFormat.BlockAlign) * channels : 0;
            float[] result = new float[Math.Min(Math.Max(estimatedFloats, 65536L), MaxFloats)];

            var buf = new float[65536];
            int offset = 0, read;

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

        private static DecodedAudio DecodeOpusFromBytes(byte[] rawBytes, string filePath)
        {
            // Use the page index to get the authoritative channel count from
            // the OpusHead packet rather than the broken probe-decoder approach.
            var index = OggPageIndex.BuildFromBytes(rawBytes, filePath);
            int channels = index.Channels > 0 ? index.Channels : 2;

            using (var ms = new MemoryStream(rawBytes, writable: false))
            {
                var decoder  = new OpusDecoder(48000, channels);
                long estF    = Math.Min((long)rawBytes.Length * 15L, 48000L * channels * 60 * 30);
                var result   = new float[Math.Max(estF, 65536L)];
                int offset   = 0;

                var oggReader = new OpusOggReadStream(decoder, ms);
                while (oggReader.HasNextPacket)
                {
                    short[] packet = oggReader.DecodeNextPacket();
                    if (packet == null) continue;
                    if (offset + packet.Length > result.Length)
                    {
                        long newSize = Math.Min((long)(result.Length * 1.25f) + packet.Length,
                                                48000L * channels * 60 * 30);
                        Array.Resize(ref result, (int)newSize);
                    }
                    for (int i = 0; i < packet.Length; i++)
                        result[offset + i] = packet[i] / 32768f;
                    offset += packet.Length;
                }

                if (offset == 0) return null;
                if (result.Length - offset > 65536) Array.Resize(ref result, offset);
                return new DecodedAudio { Samples = result, Channels = channels, Frequency = 48000 };
            }
        }

        // Legacy clip factory — still used when PreloadCacheEnabled = true.
        public static AudioClip CreateStreamingClip(DecodedAudio audio, string name)
        {
            if (audio?.Samples == null || audio.Samples.Length == 0) return null;

            float[] samples = audio.Samples;
            int channels = audio.Channels;
            int frequency = audio.Frequency;
            int totalSamples = samples.Length / channels;
            int cursor = 0;

            return AudioClip.Create(name, totalSamples, channels, frequency, true,
                (float[] data) =>
                {
                    int srcStart = cursor * channels;
                    int toCopy = Math.Min(data.Length, samples.Length - srcStart);
                    if (toCopy > 0) Array.Copy(samples, srcStart, data, 0, toCopy);
                    if (toCopy < data.Length) Array.Clear(data, toCopy, data.Length - toCopy);
                    cursor += data.Length / channels;
                },
                (int newPosition) =>
                {
                    cursor = Mathf.Clamp(newPosition, 0, totalSamples - 1);
                }
            );
        }

        // ── Quick validate ────────────────────────────────────────────────────
        public static bool QuickValidate(string filePath)
        {
            try
            {
                string ext = System.IO.Path.GetExtension(filePath).ToLower();

                if (ext == ".opus" || ((ext == ".ogg" || ext == ".oga") && IsOpusOgg(filePath)))
                {
                    var decoder = new OpusDecoder(48000, 2);
                    using (var fs = new FileStream(filePath, FileMode.Open,
                                                   FileAccess.Read, FileShare.Read))
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
                    return reader.WaveFormat.SampleRate > 0
                        && reader.WaveFormat.Channels > 0
                        && reader.Length > 0;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[CMM] QuickValidate EXCEPTION for {Path.GetFileName(filePath)}: " +
                          $"{ex.GetType().Name} – {ex.Message}");
                return false;
            }
        }
    }
#endif
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
            AddContext("Home_Day", "home/day", "Home", true, 120, false, 5, 5, false, true, 15, false);
            AddContext("Home_Night", "home/night", "Home", true, 120, false, 5, 5, false, true, 15, false);
            AddContext("Home", "home", "Ambient", true, 120, false, 5, 5, false, true, 15, false);
            AddContext("POI_T5", "poi/t5", "POI", true, 60, false, 5, 5, false, true, 22, false);
            AddContext("POI_T4", "poi/t4", "POI", true, 60, false, 5, 5, false, true, 22, false);
            AddContext("POI_T3", "poi/t3", "POI", true, 60, false, 5, 5, false, true, 22, false);
            AddContext("POI_T2", "poi/t2", "POI", true, 60, false, 5, 5, false, true, 22, false);
            AddContext("POI_T1", "poi/t1", "POI", true, 60, false, 5, 5, false, true, 22, false);
            AddContext("POI_T0", "poi/t0", "Ambient", true, 60, false, 5, 5, false, true, 21, false);
            AddContext("POI", "poi", "Ambient", true, 60, false, 5, 5, false, true, 20, false);
            AddContext("Ambient_Forest", "ambient/forest", "Ambient", true, 120, false, 5, 5, false, true, 11, false);
            AddContext("Ambient_Burnt_Forest", "ambient/burnt_forest", "Ambient", true, 120, false, 5, 5, false, true, 11, false);
            AddContext("Ambient_Desert", "ambient/desert", "Ambient", true, 120, false, 5, 5, false, true, 11, false);
            AddContext("Ambient_Snow", "ambient/snow", "Ambient", true, 120, false, 5, 5, false, true, 11, false);
            AddContext("Ambient_Wasteland", "ambient/wasteland", "Ambient", true, 120, false, 5, 5, false, true, 11, false);
            AddContext("Ambient", "ambient", "None", true, 120, false, 5, 4, false, true, 10, false);
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
        private static WaveStream _readerToDisposeAfterFade = null;   // NEW
        private static DecodedAudio _decodedToReleaseAfterFade = null;   // kept for PreloadCache path
        private static bool _scheduleHandledTransition = false;
        private static PropertyInfo _isLoadingProp = null;
        private static bool _isLoadingPropResolved = false;
        public static bool _gameIsLoading = false;

        private static bool IsGameLoading()
        {
            if (!_isLoadingPropResolved)
            {
                _isLoadingPropResolved = true;
                try
                {
                    var t = Type.GetType("LoadingScreen, Assembly-CSharp");
                    _isLoadingProp = t?.GetProperty("IsLoading",
                        BindingFlags.Public | BindingFlags.Static);
                    Log.Warning("[CMM] - Game is loading");
                }
                catch { }
            }
            if (_isLoadingProp == null) return false;
            try { return (bool)_isLoadingProp.GetValue(null); }
            catch { return false; }
        }

        // ── NAudio async decode helper ────────────────────────────────────────
        // FIX: Split into two phases so disk I/O and CPU decode are each on a
        // background thread but the main thread yields a frame between them.
        // This keeps the main thread responsive throughout and avoids a single
        // large ThreadPool item that could block the pool for 500-800ms on slow
        // spinning disks or large files.
        private IEnumerator OpenReaderAsync(string filePath, Action<WaveStream> onComplete)
        {
            // Wait until the engine is not mid-load so we don't compete for I/O.
            float loadWait = 0f;
            //while (IsGameLoading() && loadWait < 8f)
            while (_gameIsLoading && loadWait < 8f)
            {
                yield return new WaitForSeconds(0.25f);
                loadWait += 0.25f;
            }
            if (loadWait > 0f)
                Log.Out($"[CMM] Deferred open by {loadWait:F1}s (game was loading)");

            WaveStream reader = null;
            bool finished = false;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { reader = NAudioDecoder.OpenStreamingReader(filePath); }
                catch { }
                finished = true;
            });

            while (!finished) yield return null;

            if (reader == null)
            {
                _invalidFiles.Add(filePath);
                Log.Error($"[CMM] Preload: failed to open {Path.GetFileName(filePath)}, marking invalid");
            }

            onComplete(reader);
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

            float clipLen = cache.cachedClip.length;
            float safeStart = clipLen > 0.02f
                ? Mathf.Clamp(startPosition, 0f, clipLen - 0.01f)
                : 0f;

            if (safeStart > 0f && cache.currentReader != null)
            {
                long targetFrame = (long)(safeStart * cache.currentReader.WaveFormat.SampleRate);
                long targetByte = targetFrame * cache.currentReader.WaveFormat.Channels * 4L;
                try { cache.currentReader.Position = targetByte; }
                catch (Exception ex)
                {
                    Log.Warning($"[CMM] Pre-seek failed for {contextName}: {ex.Message}");
                }
                Log.Out($"[CMM] Pre-seek to frame {targetFrame} ({safeStart:F2}s) for {contextName}");
            }

            incoming.clip = cache.cachedClip;
            incoming.loop = ctx.Loop;
            incoming.volume = 0f;
            incoming.Play();

            if (safeStart > 0f)
            {
                incoming.time = safeStart;
                Log.Out($"[CMM] Seek to {safeStart:F2}s for {contextName} " +
                        $"(clip={clipLen:F1}s, streaming={cache.currentReader != null})");
            }

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
            // Streaming path: dispose the WaveStream after the clip is gone.
            if (_readerToDisposeAfterFade != null)
            {
                try { _readerToDisposeAfterFade.Dispose(); } catch { }
                _readerToDisposeAfterFade = null;
            }
            // Legacy full-decode path (PreloadCacheEnabled = true).
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

                if (cache.cachedClip != null)
                {
                    Destroy(cache.cachedClip);
                    cache.cachedClip = null;
                    // One frame so the audio thread moves off before Dispose.
                    yield return new WaitForEndOfFrame();
                    cache.DisposeCurrentReader();
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

            string avoidForPick = GlobalTrackOrder == TrackOrderMode.RandomNoRepeat
                                   ? cache.lastPlayedFile : null;
            string prePickedFile = PickNextFile(contextName, avoidFile: avoidForPick);

            // File-size heuristic for decode-lead estimate.
            // For the streaming path this is almost always negligible (just a
            // file-open), so the estimate acts as a safety margin.
            float estimatedOpenSeconds = 0f;
            if (prePickedFile != null)
            {
                try
                {
                    long fileBytes = new FileInfo(prePickedFile).Length;
                    estimatedOpenSeconds = fileBytes / (1024f * 1024f) * 0.0002f; // ~0.2ms/MB
                }
                catch { }
            }

            float effectiveFade = Mathf.Clamp(TrackTransitionFadeDuration, 0.25f, clipLength * 0.5f);
            float safeLeadTime = TrackTransitionPreloadLead + estimatedOpenSeconds * 3f;
            float waitBeforeLoad = Mathf.Max(0f, clipLength - effectiveFade - safeLeadTime);
            float waitBeforePlay = Mathf.Max(0f, clipLength - effectiveFade);

            Log.Out($"[CMM] {contextName}: clip={clipLength:F1}s fade={effectiveFade:F1}s " +
                    $"openEst={estimatedOpenSeconds * 1000f:F1}ms loadIn={waitBeforeLoad:F1}s playIn={waitBeforePlay:F1}s");

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
                    _readerToDisposeAfterFade = cache.currentReader;   // streaming
                    _decodedToReleaseAfterFade = cache.decodedAudio;   // legacy
                    cache.cachedClip = null;
                    cache.currentReader = null;
                    cache.decodedAudio = null;
                }
                yield return StartCoroutine(PreloadContext(contextName));
            }
            else
            {
                // Promote next → current; stage current for deferred disposal.
                if (cache.cachedClip != null)
                {
                    _clipToDestroyAfterFade = cache.cachedClip;
                    _readerToDisposeAfterFade = cache.currentReader;
                    _decodedToReleaseAfterFade = cache.decodedAudio;
                }
                cache.cachedClip = cache.nextCachedClip;
                cache.currentReader = cache.nextReader;
                cache.decodedAudio = cache.nextDecodedAudio;
                cache.nextCachedClip = null;
                cache.nextReader = null;
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
            string avoid = GlobalTrackOrder == TrackOrderMode.RandomNoRepeat
                              ? cache.lastPlayedFile : null;
            string filePath = prePickedFile ?? PickNextFile(contextName, avoidFile: avoid);

            if (filePath == null)
            { Log.Out($"[CMM] LoadNextClip: no files for {contextName}"); yield break; }

            if (CMMConfig.UseNAudioDecoder)
            {
                WaveStream reader = null;
                yield return StartCoroutine(OpenReaderAsync(filePath, r => reader = r));

                if (reader == null)
                {
                    Log.Error($"[CMM] LoadNextClip: failed to open {Path.GetFileName(filePath)}");
                    yield break;
                }

                AudioClip clip = NAudioDecoder.CreateStreamingClipFromReader(
                    reader, Path.GetFileNameWithoutExtension(filePath));

                if (clip == null) { reader.Dispose(); yield break; }

                // Dispose previous next-slot
                if (cache.nextCachedClip != null) Destroy(cache.nextCachedClip);
                cache.DisposeNextReader();

                cache.nextCachedClip = clip;
                cache.nextReader = reader;
                cache.lastPlayedFile = filePath;
                Log.Out($"[CMM] Next clip ready (streaming) for {contextName}: {Path.GetFileName(filePath)}");
            }
            else
            {
                // Unity fallback path — unchanged
                AudioType audioType = NAudioDecoder.GetUnityAudioType(filePath);
                if (audioType == AudioType.UNKNOWN) yield break;

                using (var request = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(
                           "file:///" + filePath.Replace("\\", "/"), audioType))
                {
                    ((UnityEngine.Networking.DownloadHandlerAudioClip)request.downloadHandler)
                        .streamAudio = CMMConfig.UnityStreamingEnabled;
                    yield return request.SendWebRequest();
                    if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    { Log.Error($"[CMM] LoadNextClip Unity failed: {request.error}"); yield break; }

                    if (cache.nextCachedClip != null) Destroy(cache.nextCachedClip);
                    cache.nextCachedClip = UnityEngine.Networking.DownloadHandlerAudioClip
                                              .GetContent(request);
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
                // Full-decode path only (PreloadCacheEnabled = true or Opus fallback)
                if (cache.decodedAudio?.Samples != null) total += (long)cache.decodedAudio.Samples.Length * 4;
                if (cache.nextDecodedAudio?.Samples != null) total += (long)cache.nextDecodedAudio.Samples.Length * 4;
                // Unity fallback clips
                if (cache.cachedClip != null && cache.currentReader == null && cache.decodedAudio == null)
                    total += (long)cache.cachedClip.samples * cache.cachedClip.channels * 4;
                if (cache.nextCachedClip != null && cache.nextReader == null && cache.nextDecodedAudio == null)
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
                ContextCache c = kv.Value;

                bool curStreaming = c.cachedClip != null && c.currentReader != null;
                bool nextStreaming = c.nextCachedClip != null && c.nextReader != null;

                long cur = c.decodedAudio?.Samples != null ? (long)c.decodedAudio.Samples.Length * 4 : 0;
                long next = c.nextDecodedAudio?.Samples != null ? (long)c.nextDecodedAudio.Samples.Length * 4 : 0;
                if (cur == 0 && c.cachedClip != null && !curStreaming)
                    cur = (long)c.cachedClip.samples * c.cachedClip.channels * 4;
                if (next == 0 && c.nextCachedClip != null && !nextStreaming)
                    next = (long)c.nextCachedClip.samples * c.nextCachedClip.channels * 4;

                bool anyLoaded = c.cachedClip != null || c.nextCachedClip != null;
                if (!anyLoaded) continue;

                totalBytes += cur + next;
                if (includePerContext)
                {
                    string curLabel = curStreaming ? "streaming" : FormatBytes(cur);
                    string nextLabel = nextStreaming ? "streaming" : (next > 0 ? FormatBytes(next) : null);
                    lines.AppendLine($"  {kv.Key}: {curLabel} current" +
                                     (nextLabel != null ? $" + {nextLabel} next" : ""));
                }
            }

            string footer = $"  PCM total: {FormatBytes(totalBytes)}" +
                            (totalBytes == 0 ? " (all contexts streaming)" : "");
            if (includePerContext) lines.Append(footer);
            else lines.Append(totalBytes == 0 ? "streaming" : FormatBytes(totalBytes));
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
            // Get the context from the list of contexts
            Contexts incoming = GetContext(contextIn);
            // if no context, return
            if (incoming == null) return;
            // if the context waiting to be swapped to is already waiting to be transitioned to, there's no need to reprocess this
            if (_pendingContext == contextIn) return;

            // if not ingame
            bool menuOverride = !inGame;
            int comparePriority = bypassPriority >= 0 ? bypassPriority : (currentContext?.Priority ?? 0);
            // if currentContext is not null and the contetxIn is the currentContext
            // if a current context was null and fallen back on, but is now valid and the contextIN, then swap to it
            bool isReloadOfCurrent = currentContext != null && contextIn == currentContext.Context;
            bool priorityOk = inGame && incoming.CanInterrupt && incoming.Priority > comparePriority;

            // if not in game, if not higher priority, if currentContext is re-validated
            if (!menuOverride && !priorityOk && !isReloadOfCurrent)
            {
                Log.Out($"[CMM] {contextIn} rejected — not higher priority than {currentContext?.Context}");
                return;
            }

            Log.Out($"[CMM] Context accepted: {currentContext?.Context} -> {contextIn}");
            // having gotten this far, the next context to queue is contextIn
            _pendingContext = contextIn;

            // if currentContext is not null, can be resumed, and music is currently playing
            if (currentContext != null && currentContext.CanBeResumed && ActiveChannel.isPlaying)
            {
                // save resume positions for the track we are leaving, if it can be resumed
                ContextCache leavingCache = cacheMap[currentContext.Context];
                leavingCache.resumePosition = ActiveChannel.time;
                leavingCache.lastLeftTime = Time.realtimeSinceStartup;
                Log.Out($"[CMM] Saved resume for {currentContext.Context}: {leavingCache.resumePosition:F2}s");

                // the condition for PreloadCache is likely obsolete
                // if there is a clip in the cacheMap, set it to be destroyed after change?
                if (!CMMConfig.PreloadCacheEnabled && leavingCache.cachedClip != null)
                {
                    // after fade is complete, this clip will be discarded
                    _clipToDestroyAfterFade = leavingCache.cachedClip;
                    _readerToDisposeAfterFade = leavingCache.currentReader;
                    _decodedToReleaseAfterFade = leavingCache.decodedAudio;
                    // leaving cache is cleared for next entry
                    leavingCache.cachedClip = null;
                    leavingCache.currentReader = null;
                    leavingCache.decodedAudio = null;
                }
            }

            // if currentContext is not null and plays once, and the new context doesn't play once
            // don't alter the return context
            if (currentContext != null && currentContext.PlayOnce && !incoming.PlayOnce)
                _playOnceReturnContext = null;

            // find out if the context is valid - will resolve fallbacks and what is playable
            string contextToPlay = ResolvePlayableContext(contextIn);
            // if context/fallsback are all invalid, fade to silence
            if (contextToPlay == null)
            {
                Log.Out($"[CMM] No playable context in chain for {contextIn} — fading to silence.");
                _pendingContext = "None";
                currentContext = GetContext("None");
                // increment transition ID
                ++_transitionId;
                FadeToSilence();
                return;
            }

            Contexts candidate = GetContext(contextToPlay);
            // if incoming or candidate bypass cooldown
            bool bypassCooldown = incoming.BypassesCooldown || candidate.BypassesCooldown;
            
            // is the contextToPlay on cooldown and can it be bypassed by the candidate context
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
                    if (blocked.cachedClip != null)
                    {
                        Destroy(blocked.cachedClip);
                        blocked.cachedClip = null;
                        blocked.DisposeCurrentReader();
                    }
                    if (blocked.decodedAudio != null)
                    {
                        blocked.decodedAudio.Samples = null;
                        blocked.decodedAudio = null;
                    }
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
        private HashSet<string> _previousPlayableContexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private IEnumerator ContextScanLoop()
        {
            while (true)
            {
                Log.Out($"Current Context is: {currentContext.Context}");
                Log.Out($"Player is dead? {playerIsDead}");
                yield return new WaitForSeconds(ContextScanCooldown);
                if (!inGame || playerIsDead) continue;

                List<string> activeContexts = ContextResolver.ResolveContext();

                // Compute which of the active contexts are currently playable.
                // We need to re-evaluate the stack if EITHER the active set OR
                // the playable set has changed — a context can become playable
                // without its name entering or leaving the active set.
                var currentlyPlayable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string c in activeContexts)
                {
                    string p = ResolvePlayableContext(c);
                    if (p != null) currentlyPlayable.Add(c); // track by active name, not resolved
                }

                bool namesChanged = activeContexts.Count != _previousActiveContexts.Count
                                    || activeContexts.Any(c => !_previousActiveContexts.Contains(c));
                bool playabilityChanged = currentlyPlayable.Count != _previousPlayableContexts.Count
                                          || currentlyPlayable.Any(c => !_previousPlayableContexts.Contains(c));

                if (!namesChanged && !playabilityChanged) continue;

                _previousActiveContexts.Clear();
                foreach (string c in activeContexts) _previousActiveContexts.Add(c);
                _previousPlayableContexts.Clear();
                foreach (string c in currentlyPlayable) _previousPlayableContexts.Add(c);

                // Rest of the stack rebuild continues as before...
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
                    // Guard: never interrupt a PlayOnce context with something
                    // of equal or lower priority. The null-clear is intentionally
                    // AFTER this check so the guard is re-evaluated every tick.
                    if (currentContext != null && currentContext.PlayOnce)
                    {
                        Contexts top = GetContext(topContext);
                        if (top == null || top.Priority <= currentContext.Priority)
                            continue; // do NOT clear _pendingContext here
                    }
                    _pendingContext = null; // only clear when we're actually transitioning
                    SendContextUpdate(topContext, 0);
                }

// Also add the same guard at the top of FileWatchLoop's "else" branch
// (the one that calls SendContextUpdate(top, 0) when files change but the
// playing track is still present):
                else
                {
                    string top = GetTopContext();
                    if (top != currentContext.Context)
                    {
                        // Same PlayOnce guard as ContextScanLoop.
                        if (currentContext != null && currentContext.PlayOnce)
                        {
                            Contexts topCtx = GetContext(top);
                            if (topCtx == null || topCtx.Priority <= currentContext.Priority)
                                continue; // foreach body — skip this contextName iteration
                        }
                        _pendingContext = null;
                        SendContextUpdate(top, 0);
                    }
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
                long gcBefore = GC.GetTotalMemory(false);
                yield return new WaitForSeconds(StalenessCheckInterval);
                long gcAfter = GC.GetTotalMemory(false);
                long collected = gcBefore - gcAfter;
                if (collected > 1024 * 1024)
                    Log.Warning($"[CMM PERF] GC collected {collected / 1024f:F0} KB between staleness checks");

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
            var lastWriteTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                yield return new WaitForSeconds(2f);
                if (!inGame) continue;
                if (currentContext == null || currentContext.Context == "None") continue;

                // Build the set of contexts to watch: the current one PLUS every
                // context currently on the stack (they may have files added/removed
                // while overridden by a higher-priority context).
                var contextsToWatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { currentContext.Context };
                foreach (var ac in _contextStack)
                    contextsToWatch.Add(ac.ContextName);

                foreach (string contextName in contextsToWatch)
                {
                    ContextCache cache = cacheMap.TryGetValue(contextName, out var c) ? c : null;
                    if (cache == null) continue;
                    Contexts ctx = GetContext(contextName);
                    if (ctx == null || string.IsNullOrEmpty(ctx.Path)) continue;

                    string dir = ModPath + MusicDirectory + "/" + ctx.Path + "/";

                    // ── FAST PATH: single stat ──────────────────────────────────────
                    DateTime currentWriteTime = DateTime.MinValue;
                    bool statDone = false;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            currentWriteTime = Directory.Exists(dir)
                                  ? Directory.GetLastWriteTimeUtc(dir)
                                  : DateTime.MinValue;
                        }
                        catch { }
                        statDone = true;
                    });
                    while (!statDone) yield return null;

                    lastWriteTimes.TryGetValue(contextName, out DateTime knownTime);

                    // Special case: if we've never scanned this context and the folder
                    // exists, treat it as changed so we populate the snapshot.
                    bool neverScanned = !lastWriteTimes.ContainsKey(contextName);

                    if (!neverScanned && currentWriteTime == knownTime) continue;
                    lastWriteTimes[contextName] = currentWriteTime;

                    // ── SLOW PATH: enumerate files ──────────────────────────────────
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

                    bool changed = neverScanned ||
                                   currentNorm.Length != previousNorm.Length ||
                                   currentNorm.Except(previousNorm, StringComparer.OrdinalIgnoreCase).Any() ||
                                   previousNorm.Except(currentNorm, StringComparer.OrdinalIgnoreCase).Any();
                    if (!changed) continue;

                    // Update the snapshot and invalidate the file-presence cache so
                    // ResolvePlayableContext sees the new state immediately.
                    cache.directorySnapshot = currentFiles;
                    InvalidateContextFileCache(contextName);

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
                                Log.Warning($"[CMM] New file failed validation: {Path.GetFileName(newFile)}");
                            }
                        }
                        currentFiles = currentFiles.Where(f => !_invalidFiles.Contains(norm(f))).ToArray();
                        currentNorm = currentFiles.Select(norm).ToArray();
                        cache.directorySnapshot = currentFiles;
                        // Re-invalidate after filtering out bad files.
                        InvalidateContextFileCache(contextName);
                    }

                    Log.Out($"[CMM] File change in {contextName}: {previousFiles.Length} -> {currentFiles.Length} files");

                    _previousPlayableContexts.Clear();
                    // ── Only react immediately if this is the active context ─────────
                    // For non-current contexts, the snapshot + cache invalidation above
                    // is enough: ContextScanLoop will pick up the change on its next
                    // tick and call SendContextUpdate if the top context has changed.
                    if (contextName != currentContext.Context) continue;

                    bool lastPlayedMissing = false;
                    if (!string.IsNullOrEmpty(cache.lastPlayedFile))
                    {
                        string lastNorm = norm(cache.lastPlayedFile);
                        if (!currentNorm.Any(f => string.Equals(f, lastNorm, StringComparison.OrdinalIgnoreCase)))
                        {
                            Log.Out($"[CMM] Currently-playing file removed: {Path.GetFileName(cache.lastPlayedFile)}");
                            cache.lastPlayedFile = null;
                            if (cache.cachedClip != null)
                            {
                                _clipToDestroyAfterFade = cache.cachedClip;
                                _readerToDisposeAfterFade = cache.currentReader;
                                _decodedToReleaseAfterFade = cache.decodedAudio;
                                cache.cachedClip = null;
                                cache.currentReader = null;
                                cache.decodedAudio = null;
                            }
                            lastPlayedMissing = true;
                        }
                    }

                    if (currentFiles.Length == 0)
                    {
                        Log.Out($"[CMM] {contextName} folder emptied, re-evaluating.");
                        string fallback = ResolvePlayableContext(ctx.Fallback) ?? "Ambient";
                        _pendingContext = null;
                        SendContextUpdate(fallback, currentContext.Priority + 1);
                    }
                    else if (lastPlayedMissing)
                    {
                        // FIX 1 applied here too — see above.
                        _pendingContext = null;
                        yield return StartCoroutine(PreloadContext(ctx.Context));

                        if (cacheMap[ctx.Context].cachedClip != null)
                        {
                            Log.Out($"[CMM] Replacement clip ready for {ctx.Context}, triggering.");
                            currentContext = GetContext(ctx.Context);
                            TriggerMusic(ctx.Context);
                        }
                        else
                        {
                            string fallback = ResolvePlayableContext(ctx.Fallback) ?? "Ambient";
                            SendContextUpdate(fallback, currentContext.Priority + 1);
                        }
                    }
                    else
                    {
                        // Files changed but the playing track is still present.
                        // Re-evaluate the top context in case a higher-priority one
                        // is now unblocked.
                        string top = GetTopContext();
                        if (top != currentContext.Context)
                        {
                            _pendingContext = null;
                            SendContextUpdate(top, 0);
                        }
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
                Contexts ctx2 = GetContext(contextName);
                string fallback = ctx2?.Fallback ?? "None";
                if (fallback != "None" && fallback != contextName)
                    filePath = PickNextFile(fallback, avoidFile: null);
                if (filePath == null)
                { Log.Out($"[CMM] Preload: no files for {contextName}"); yield break; }
            }

            // ── Validate ──────────────────────────────────────────────────────
            //if (!_validatedFiles.Contains(filePath))
            //{
            //    bool isValid = false;
            //    yield return StartCoroutine(ValidateFile(filePath, r => isValid = r));
            //    if (isValid)
            //    {
            //        _validatedFiles.Add(filePath);
            //    }
            //    else
            //    {
            //        _invalidFiles.Add(filePath);
            //        Log.Warning($"[CMM] Preload: {Path.GetFileName(filePath)} failed validation, retrying.");
            //        string replacement = PickNextFile(contextName, avoidFile: filePath);
            //        if (replacement == null)
            //        { Log.Out($"[CMM] Preload: no valid files for {contextName}"); yield break; }
            //        yield return StartCoroutine(ValidateFile(replacement, r => isValid = r));
            //        if (!isValid)
            //        {
            //            _invalidFiles.Add(replacement);
            //            Log.Warning($"[CMM] Preload: replacement also invalid for {contextName}. Giving up.");
            //            yield break;
            //        }
            //        _validatedFiles.Add(replacement);
            //        filePath = replacement;
            //    }
            //}

            if (_invalidFiles.Contains(filePath))
            {
                filePath = PickNextFile(contextName, avoidFile: filePath);
                if (filePath == null)
                { Log.Out($"[CMM] Preload: no valid files for {contextName}"); yield break; }
            }

            // ── NAudio streaming path ─────────────────────────────────────────
            if (CMMConfig.UseNAudioDecoder)
            {
                if (CMMConfig.PreloadCacheEnabled)
                {
                    // ── Full-decode path (PreloadCacheEnabled = true) ─────────
                    // Keep the existing full-decode behaviour so the preload
                    // cache works as before.
                    DecodedAudio decoded = null;
                    byte[] rawBytes = null;
                    bool ioOk = false;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { rawBytes = File.ReadAllBytes(filePath); ioOk = true; }
                        catch { }
                        ioOk = true; // set even on failure so coroutine doesn't hang
                    });
                    while (!ioOk) yield return null;

                    if (rawBytes != null)
                    {
                        bool decodeOk = false;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try { decoded = NAudioDecoder.DecodeFileFromBytes(rawBytes, filePath); }
                            catch { }
                            decodeOk = true;
                        });
                        while (!decodeOk) yield return null;
                    }

                    if (decoded == null)
                    {
                        Log.Error($"[CMM] Preload (full decode) failed for {contextName}: {Path.GetFileName(filePath)}");
                        _invalidFiles.Add(filePath);
                        yield break;
                    }

                    AudioClip fullClip = NAudioDecoder.CreateStreamingClip(
                        decoded, Path.GetFileNameWithoutExtension(filePath));
                    if (fullClip == null) yield break;

                    if (cache.cachedClip != null) Destroy(cache.cachedClip);
                    cache.DisposeCurrentReader(); // no-op on full-decode path
                    if (cache.decodedAudio != null) cache.decodedAudio.Samples = null;

                    cache.cachedClip = fullClip;
                    cache.decodedAudio = decoded;
                    cache.lastPlayedFile = filePath;
                    Log.Out($"[CMM] Preloaded (full decode): {contextName} — " +
                            $"{Path.GetFileName(filePath)} ({decoded.LengthSeconds:F1}s)");
                }
                else
                {
                    // ── Streaming path (default) ──────────────────────────────
                    WaveStream reader = null;
                    yield return StartCoroutine(OpenReaderAsync(filePath, r => reader = r));

                    if (reader == null)
                    {
                        Log.Error($"[CMM] Preload (stream) failed to open {contextName}: {Path.GetFileName(filePath)}");
                        _invalidFiles.Add(filePath);
                        yield break;
                    }

                    AudioClip clip = NAudioDecoder.CreateStreamingClipFromReader(
                        reader, Path.GetFileNameWithoutExtension(filePath));

                    if (clip == null)
                    {
                        reader.Dispose();
                        Log.Error($"[CMM] Preload: CreateStreamingClipFromReader null for {contextName}");
                        yield break;
                    }

                    // Stage old clip/reader for deferred disposal
                    if (cache.cachedClip != null)
                    {
                        Destroy(cache.cachedClip);
                        // Dispose after one frame via the cache helper — called
                        // by DeferredDestroyPending on the next audio tick.
                        // Here we dispose immediately since we're not mid-play.
                        cache.DisposeCurrentReader();
                    }

                    cache.cachedClip = clip;
                    cache.currentReader = reader;
                    cache.lastPlayedFile = filePath;

                    float lenSec = reader.Length > 0 && reader.WaveFormat.BlockAlign > 0
                        ? (float)(reader.Length / reader.WaveFormat.BlockAlign) / reader.WaveFormat.SampleRate
                        : 0f;
                    Log.Out($"[CMM] Preloaded (streaming): {contextName} — " +
                            $"{Path.GetFileName(filePath)}" +
                            (lenSec > 0 ? $" ({lenSec:F1}s)" : "") +
                            (NAudioDecoder.IsOpusFile(filePath) ? " [Opus/seekable]" : ""));
                }
            }
            else
            {
                // ── Unity fallback path ───────────────────────────────────────
                AudioType audioType = NAudioDecoder.GetUnityAudioType(filePath);
                if (audioType == AudioType.UNKNOWN) yield break;

                using (var request = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(
                           "file:///" + filePath.Replace("\\", "/"), audioType))
                {
                    ((UnityEngine.Networking.DownloadHandlerAudioClip)request.downloadHandler)
                        .streamAudio = CMMConfig.UnityStreamingEnabled;
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
            //_cooldownCoroutine = null;
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

    // This happens when the world is almost finished loading the world is loading
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

    // this is called constantly while on the menu screen
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

    // this happens when leaving a gameworld and going back to the menu screen
    [HarmonyPatch(typeof(GameManager), "SaveAndCleanupWorld")]
    public class Patch_GameEnded
    {
        static void Prefix()
        {
            ContextMusicManager.inInventory = false;
            ContextMusicManager.inGame = false;
            ContextMusicManager.playerIsDead = true;
            ContextMusicManager._contextStack.Clear();
            ContextMusicManager._pendingContext = "None";
            ContextResolver.resetValues();
            ContextMusicManager.SendContextUpdate("Menu");
        }
    }

    // This happens when the player spawn (both initially, and after death)
    [HarmonyPatch(typeof(EntityPlayerLocal), "AfterPlayerRespawn")]
    public class PlayerRespawn
    {
        static void Postfix()
        {
            ContextMusicManager._gameIsLoading = false;
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

    // This patch exist for debuff purposes
    [HarmonyPatch(typeof(GameManager), "Awake")]
    public class StartupLogPatch
    {
        static void Postfix() => Log.Out("[CMM] Game started up!");
    }

    // This patch exist for debuff purposes
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

    // This keeps a log of sounds that have played so we can catch them later with patches
    [HarmonyPatch(typeof(Audio.Manager), "PlayInsidePlayerHead",
        new Type[] { typeof(string), typeof(int), typeof(float), typeof(bool), typeof(bool) })]
    public class Patch_LogPlayInsidePlayerHead
    {
        static bool Prefix(string soundGroupName) => AudioStingerHandler.Handle(soundGroupName);
    }

    // this does the same thing as the previous function but exists for the overload version of the function
    [HarmonyPatch(typeof(Audio.Manager), "PlayInsidePlayerHead",
        new Type[] { typeof(string), typeof(int) })]
    public class Patch_LogPlayInsidePlayerHead2
    {
        static bool Prefix(string soundGroupNameBegin) => AudioStingerHandler.Handle(soundGroupNameBegin);
    }

    // this exists to keep track of when the world starts loading, however, it has not been useful thus far
[   HarmonyPatch(typeof(World), "LoadWorld")]
    public class LoadChecker
    {

        static bool Prefix()
        {
            Log.Warning("[CMM] Game is loading, from LoadChecker.");
            ContextMusicManager._gameIsLoading = true;
            return true;
        }
    }
}