
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace CMM
{
    // Result of a one-pass loudness measurement (see AudioDecoder.MeasureLoudness).
    // Hash + Size are captured from the same byte read so the caller can store them
    // without re-touching the file. Ok is false when the file couldn't be read or
    // its encoding is unsupported, or it was too short/quiet to yield a reading.
    public struct TrackLoudness
    {
        public bool Ok;
        public string Hash;
        public long Size;
        public double Lufs;
        public double Peak;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AUDIO DECODER
    // ─────────────────────────────────────────────────────────────────────────

    public static class AudioDecoder
    {
        // ── Audio-thread heartbeat ────────────────────────────────────────────
        // Incremented (by sample count) every time Unity's PCMReaderCallback pulls
        // from one of our streaming clips. The audio thread only invokes these
        // callbacks while it is actively playing the clip, so a per-tick DELTA on
        // this counter is the ground truth for "is sound actually being produced",
        // independent of what AudioSource.volume/isPlaying claim. Read by the
        // DiagnosticLoop to distinguish a stalled engine (delta == 0 while
        // isPlaying) from a downstream mute (delta > 0 but inaudible). Volatile +
        // unguarded: an exact value isn't needed, only whether it's moving.
        public static long PcmHeartbeat;

        private static readonly HashSet<string> _naudioExts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mp3", ".wav", ".wave", ".aiff", ".aif", ".ogg", ".oga", ".opus", ".flac",
                ".m4a", ".mp4", ".aac",
                // ISO-BMFF video containers — the same box structure minimp4 already walks
                // for .mp4, so an AAC audio track inside one decodes via the AAC reader.
                // (.mov is QuickTime-flavoured ISO-BMFF — esds nested in a 'wave' atom that
                // minimp4 doesn't demux — so it's intentionally excluded for now.)
                ".m4v", ".3gp",
                // Matroska / WebM — demuxed + remuxed into an existing codec backend by
                // the native container reader (NativeContainerReader / cmm_container_*).
                ".mkv", ".webm",
            };

        public static bool IsSupported(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath);
            return _naudioExts.Contains(ext);
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

        // ── OGG codec detection ───────────────────────────────────────────────

        private static long ProfStart() => Stopwatch.GetTimestamp();

        private static void ProfEnd(long t0, string label, float warnMs = 2f)
        {
            float ms = (Stopwatch.GetTimestamp() - t0) * 1000f / Stopwatch.Frequency;
            if (ms > warnMs) CMMConfig.D($"[CMM PERF] {label}: {ms:F2}ms");
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
        // Returns an open WaveStream (native reader) for any supported format.
        // Buffers the file into memory when it fits (HotSwapMaxFileSizeBytes) and opens
        // from bytes; otherwise opens via the native file-open path. The caller owns the
        // returned stream and must Dispose() it when the associated AudioClip is destroyed.
        //
        // Called on a background thread by OpenReaderAsync.
        public static WaveStream OpenStreamingReader(string filePath)
        {
            if (!Thread.CurrentThread.IsBackground)
                CMMConfig.D($"[CMM PERF] OpenStreamingReader called on main thread for {Path.GetFileName(filePath)}!");

            // Decode straight from the file via the native readers (CMM_Decoder.dll): the
            // decoder streams from a native FILE* and keeps all working memory in native
            // (malloc) heap the GC never scans. The old path here did File.ReadAllBytes —
            // a multi-MB managed Large-Object-Heap buffer PER TRANSITION, whose Gen2
            // collection is stop-the-world on the ~2.6 GB in-game Mono heap and hitched the
            // game at every track change. Those bytes were only ever memcpy'd into the
            // decoder's OWN native copy (cmm_*_open_memory: malloc + memcpy) and then thrown
            // away, so opening natively removes the allocation with no change to what is
            // decoded. Native seeking is reliable across all formats (the historical Opus
            // resume-seek bug was in the retired managed decoder, not this path).
            //
            // Trade-off on slow removable media: playback now streams on demand from the
            // FILE* instead of a RAM copy, so the audio thread does the disk reads. At ~90
            // MB/s sequential vs ~0.02–0.1 MB/s playback the read head stays far ahead, and
            // the OS read-ahead/page cache covers it; only post-seek (resume/loop) pays one
            // cold seek.
            //
            // BUFFERED OPEN (default): read the whole file into a NATIVE buffer, hand it to
            // the decoder (which copies it into its own private buffer — see cmm_*_open_memory),
            // and CLOSE the file handle immediately. A streamed-from-disk reader holds the file
            // open for the WHOLE track, which on Windows pins not just the file but its PARENT
            // FOLDER (no share flag lets a folder be moved/renamed/deleted while a file inside
            // is open). Buffering up front means nothing — file or folder — is locked while the
            // track plays, so the user can freely reorganise the music folders mid-playback.
            // The buffer lives in NATIVE (AllocHGlobal) memory the GC never scans, read in via a
            // small reused Gen0 chunk, so there is NO managed Large-Object-Heap allocation — the
            // exact thing the comment above warns against (never File.ReadAllBytes here).
            //
            // Files larger than HotSwapMaxFileSizeBytes are NOT buffered (that would mean a
            // 100 MB+ native allocation held for the whole song): they fall back to streaming
            // from an open FILE* below. That one file stays open while it plays — its folder is
            // pinned for the song's duration — but it is the rare case, and the file itself is
            // still movable (FILE_SHARE_DELETE); only its parent folder is held.
            try
            {
                long size = new FileInfo(filePath).Length;
                if (size > 0 && size <= CMMConfig.HotSwapMaxFileSizeBytes)
                {
                    WaveStream buffered = OpenBufferedReader(filePath, size);
                    if (buffered != null) return buffered;
                    // Buffering failed (alloc/read/decode) — fall through to streaming.
                }
            }
            catch { /* stat failed — fall through to streaming from disk */ }

            return OpenNativeReader(filePath);
        }

        // Reads the whole file into a native (off-GC-heap) buffer and opens the matching
        // native reader from it, leaving NO file handle open (so neither the file nor its
        // folder is locked while the track plays). The file streams in through a small reused
        // 64 KB Gen0 chunk — no managed Large-Object-Heap allocation — and the decoder makes
        // its own private copy of the bytes (cmm_*_open_memory), so our buffer is freed the
        // instant the reader is constructed. Returns null on any failure so OpenStreamingReader
        // falls back to streaming-from-disk. Runs on the same background thread as the caller.
        private static WaveStream OpenBufferedReader(string filePath, long size)
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal(new IntPtr(size));
                byte[] chunk = new byte[64 * 1024];   // 64 KB < 85 KB LOH threshold → Gen0
                long off = 0;
                // Share read+write+delete so copying the file in never blocks another process
                // (or the user) from moving/deleting it even during this brief read.
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                                               FileOptions.SequentialScan))
                {
                    int n;
                    while (off < size &&
                           (n = fs.Read(chunk, 0, (int)Math.Min(chunk.Length, size - off))) > 0)
                    {
                        Marshal.Copy(chunk, 0, new IntPtr(buf.ToInt64() + off), n);
                        off += n;
                    }
                }
                if (off <= 0) return null;
                // The FileStream is closed now — no handle survives. Pull the first few bytes
                // back out of the native buffer for Opus-in-Ogg detection, then open from it.
                byte[] head = new byte[(int)Math.Min(off, 64)];
                Marshal.Copy(buf, head, 0, head.Length);
                string ext = Path.GetExtension(filePath).ToLower();
                return OpenNativeReaderFromPtr(buf, off, ext, head);
            }
            catch (Exception ex)
            {
                CMMConfig.D($"[CMM] Buffered open failed for {Path.GetFileName(filePath)}: {ex.Message}; streaming instead.");
                return null;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        // Opens a native reader for any supported format from a native memory buffer (pointer
        // + length). Mirrors OpenNativeReaderFromMemory but takes an unmanaged pointer so the
        // file bytes never touch the GC heap. `head` is the first few bytes, used only to tell
        // Opus-in-Ogg from Vorbis-in-Ogg. The decoder copies the buffer, so the caller owns and
        // frees `data`. Returns null for unknown extensions.
        private static WaveStream OpenNativeReaderFromPtr(IntPtr data, long length, string ext, byte[] head)
        {
            bool isOpus = ext == ".opus" ||
                ((ext == ".ogg" || ext == ".oga") && IsOpusOggBytes(head));
            switch (ClassifyReader(ext, isOpus))
            {
                case ReaderKind.Opus:      return new NativeOggOpusReader(data, length);
                case ReaderKind.Flac:      return new NativeFlacReader(data, length);
                case ReaderKind.Vorbis:    return new NativeVorbisReader(data, length);
                case ReaderKind.Mp3:       return new NativeMp3Reader(data, length);
                case ReaderKind.Wav:       return new NativeWavReader(data, length);
                case ReaderKind.Aac:       return new NativeAacReader(data, length);
                case ReaderKind.Container: return new NativeContainerReader(data, length);
                default:                   return WarnNoReader(ext);
            }
        }

        // Opens a native reader for any supported format directly from a file path
        // (large-file path: no full-file buffer). Returns null for unknown extensions.
        private static WaveStream OpenNativeReader(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath).ToLower();
            bool isOpus = ext == ".opus" ||
                ((ext == ".ogg" || ext == ".oga") && IsOpusOgg(filePath));
            switch (ClassifyReader(ext, isOpus))
            {
                case ReaderKind.Opus:      return new NativeOggOpusReader(filePath);
                case ReaderKind.Flac:      return new NativeFlacReader(filePath);
                case ReaderKind.Vorbis:    return new NativeVorbisReader(filePath);
                case ReaderKind.Mp3:       return new NativeMp3Reader(filePath);
                case ReaderKind.Wav:       return new NativeWavReader(filePath);
                case ReaderKind.Aac:       return new NativeAacReader(filePath);
                case ReaderKind.Container: return new NativeContainerReader(filePath);
                default:                   return WarnNoReader(ext);
            }
        }

        // Opens a native reader for any supported format from an in-memory buffer.
        // `raw` may be a REUSED buffer larger than the file (loudness sweep pooling);
        // only the first `length` bytes are decoded. Returns null for unknown extensions.
        private static WaveStream OpenNativeReaderFromMemory(byte[] raw, int length, string ext)
        {
            bool isOpus = ext == ".opus" ||
                ((ext == ".ogg" || ext == ".oga") && IsOpusOggBytes(raw));
            switch (ClassifyReader(ext, isOpus))
            {
                case ReaderKind.Opus:      return new NativeOggOpusReader(raw, length);
                case ReaderKind.Flac:      return new NativeFlacReader(raw, length);
                case ReaderKind.Vorbis:    return new NativeVorbisReader(raw, length);
                case ReaderKind.Mp3:       return new NativeMp3Reader(raw, length);
                case ReaderKind.Wav:       return new NativeWavReader(raw, length);
                case ReaderKind.Aac:       return new NativeAacReader(raw, length);
                case ReaderKind.Container: return new NativeContainerReader(raw, length);
                default:                   return WarnNoReader(ext);
            }
        }

        private enum ReaderKind { Unknown, Opus, Flac, Vorbis, Mp3, Wav, Aac, Container }

        // Single source of truth for the extension → native-reader-family mapping, shared by
        // the path / byte[] / IntPtr open methods above (which differ only in the buffer they
        // hand the chosen reader). `isOpus` is the caller's header sniff for the shared
        // .ogg/.oga case, which the extension alone can't disambiguate from Vorbis.
        private static ReaderKind ClassifyReader(string ext, bool isOpus)
        {
            if (isOpus)                          return ReaderKind.Opus;
            if (ext == ".flac")                  return ReaderKind.Flac;
            if (ext == ".ogg" || ext == ".oga")  return ReaderKind.Vorbis;
            if (ext == ".mp3")                   return ReaderKind.Mp3;
            if (ext == ".wav" || ext == ".wave" || ext == ".aiff" || ext == ".aif")
                                                 return ReaderKind.Wav;
            if (ext == ".m4a" || ext == ".mp4" || ext == ".aac" ||
                ext == ".m4v" || ext == ".3gp")  return ReaderKind.Aac;
            if (ext == ".mkv" || ext == ".webm") return ReaderKind.Container;
            return ReaderKind.Unknown;
        }

        private static WaveStream WarnNoReader(string ext)
        {
            Log.Warning($"[CMM] No native reader for extension '{ext}'");
            return null;
        }

        // ── Loudness measurement ──────────────────────────────────────────────
        // One full decode pass to integrated LUFS (EBU R128), plus the FNV-1a
        // content hash computed from the same byte read. No PCM is retained — the
        // R128Analyzer accumulates running statistics, so memory stays at a few KB
        // regardless of track length. Mirrors the byte-read + Opus/generic reader
        // branch in OpenStreamingReader / CreateStreamingClipFromReader.
        //
        // Called on a BACKGROUND thread (by the menu prewarm / look-ahead / block-before-play).
        // Pure measurement — touches no Unity main-thread state. The caller commits
        // the TrackLoudness into LoudnessStore on the main thread.
        // `scratch`, when supplied, provides a REUSED growable file buffer and sample buffer
        // so a measurement allocates nothing on the Large Object Heap. This is the fix for the
        // real cause of in-game stutter: the old per-file File.ReadAllBytes allocated a multi-MB
        // LOH array every track, and on the ~2.6 GB in-game Mono heap the resulting Gen2
        // collections were stop-the-world frame hitches. The scratch MUST be used
        // single-threaded; each caller that passes one serializes its own use. Callers without
        // a scratch (the rare block-before-play path) fall back to per-file allocation.
        public static TrackLoudness MeasureLoudness(string filePath, MeasureScratch scratch = null)
        {
            var fail = new TrackLoudness { Ok = false };

            // ── Step-0 instrumentation ────────────────────────────────────────
            // Per-file wall-clock split (read / hash / index / decode+analyze) plus
            // GC-collection deltas, so we can tell a CPU-bound (decode) sweep from an
            // allocation-bound (whole-file buffer → LOH → GC) one. All gated behind
            // CMMConfig.D (DebugLogging), so it's free in normal play. Stopwatch ticks
            // → ms via the helper below.
            long tStart = Stopwatch.GetTimestamp();
            int gc0 = GC.CollectionCount(0);
            int gc1 = GC.CollectionCount(1);
            int gc2 = GC.CollectionCount(2);
            float hashMs = 0, indexMs = 0, analyzeMs = 0;

            long size;
            string ext = Path.GetExtension(filePath).ToLower();
            try
            {
                size = new FileInfo(filePath).Length;
                if (size > CMMConfig.HotSwapMaxFileSizeBytes)
                {
                    Log.Warning($"[CMM] Loudness: {Path.GetFileName(filePath)} too large to buffer " +
                                "— skipping normalization (plays at unity gain).");
                    return fail;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] Loudness: could not stat {Path.GetFileName(filePath)}: {ex.Message}");
                return fail;
            }

            // Content hash via a STREAMING read (Gen0 64 KB buffer), NOT a whole-file
            // managed array. The old File.ReadAllBytes here allocated a multi-MB Large-
            // Object-Heap buffer per track; on the ~2.6 GB in-game Mono heap the resulting
            // stop-the-world Gen2 collection was the frame hitch felt at every transition to
            // an un-pre-measured track. HashFile yields the byte-identical hash without that
            // allocation, and the decode below reads the file natively — so a measurement now
            // allocates nothing on the LOH, on ANY caller path (block-before-play included).
            long tHash = Stopwatch.GetTimestamp();
            string hash = LoudnessStore.HashFile(filePath);
            hashMs = ElapsedMs(tHash);
            if (hash == null)
            {
                Log.Warning($"[CMM] Loudness: could not read {Path.GetFileName(filePath)} for hashing.");
                return fail;
            }

            WaveStream reader = null;
            try
            {
                // Decode straight from the file via the native readers (CMM_Decoder.dll):
                // native memory the GC never scans, and no managed whole-file buffer. The OS
                // page cache populated by the hash pass above usually serves this second read
                // warm, so it is mostly CPU (decode), not a second cold disk hit.
                long tIdx = Stopwatch.GetTimestamp();
                reader = OpenNativeReader(filePath);
                indexMs = ElapsedMs(tIdx);
                if (reader == null) return fail;

                long tAnalyze = Stopwatch.GetTimestamp();
                LoudnessResult result = AnalyzeToEnd(reader, filePath, scratch);
                analyzeMs = ElapsedMs(tAnalyze);
                if (!result.Valid) return fail;

                return new TrackLoudness
                {
                    Ok = true,
                    Hash = hash,
                    Size = size,
                    Lufs = result.IntegratedLufs,
                    Peak = result.SamplePeak,
                };
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] Loudness: measure failed for {Path.GetFileName(filePath)}: {ex.Message}");
                return fail;
            }
            finally
            {
                reader?.Dispose();

                if (CMMConfig.DebugLogging)
                {
                    float totalMs = ElapsedMs(tStart);
                    double mb = size / (1024.0 * 1024.0);
                    // Decode throughput in MB of source/sec — the headline number for
                    // ranking codecs (Opus/Concentus is expected to be the slowest).
                    double mbps = analyzeMs > 0.01f ? mb / (analyzeMs / 1000.0) : 0;
                    int dg0 = GC.CollectionCount(0) - gc0;
                    int dg1 = GC.CollectionCount(1) - gc1;
                    int dg2 = GC.CollectionCount(2) - gc2;
                    string kind = (ext == ".opus" || ((ext == ".ogg" || ext == ".oga") && IsOpusOgg(filePath)))
                        ? "opus" : (ext.Length > 1 ? ext.Substring(1) : ext);
                    CMMConfig.D(
                        $"[CMM PERF] Loudness {Path.GetFileName(filePath)}: {mb:F1}MB {kind} | " +
                        $"hash+read={hashMs:F0}ms index={indexMs:F0}ms " +
                        $"decode={analyzeMs:F0}ms total={totalMs:F0}ms " +
                        $"({mbps:F1}MB/s) gc(g0+{dg0},g1+{dg1},g2+{dg2})");
                }
            }
        }

        // Stopwatch-ticks → milliseconds since `t0`.
        private static float ElapsedMs(long t0)
            => (Stopwatch.GetTimestamp() - t0) * 1000f / Stopwatch.Frequency;

        // Reusable scratch for the loudness sweep: the decode buffer is reused across files
        // so a measurement allocates nothing on the heap per call. MUST be used single-
        // threaded; each caller that passes one serializes its own use. (The whole-file
        // FileBuf this used to carry is gone — MeasureLoudness now streams the hash and
        // decodes natively from the path, so no multi-MB Large-Object-Heap buffer is ever
        // allocated; see MeasureLoudness.)
        public sealed class MeasureScratch
        {
            public float[] SampleBuf;  // reused decode buffer
        }

        // Pulls the whole stream through the R128 analyzer via the native readers'
        // interleaved-float fast path (every reader is an IFloatPcmSource emitting IEEE
        // float). Reuses scratch.SampleBuf when provided so the decode buffer isn't
        // re-allocated per file.
        private static LoudnessResult AnalyzeToEnd(WaveStream reader, string filePath, MeasureScratch scratch)
        {
            float[] buf = scratch?.SampleBuf;
            if (buf == null)
            {
                buf = new float[16384];
                if (scratch != null) scratch.SampleBuf = buf;
            }

            var src = reader as IFloatPcmSource;
            if (src == null)
            {
                Log.Warning($"[CMM] Loudness: '{Path.GetFileName(filePath)}' reader is not a float source — skipping.");
                return new LoudnessResult { Valid = false };
            }

            var analyzer = new R128Analyzer(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
            while (true)
            {
                int n = src.ReadFloats(buf, 0, buf.Length);
                if (n <= 0) break;
                analyzer.Feed(buf, n);
            }
            return analyzer.Finish();
        }

        // ── Streaming AudioClip factory ───────────────────────────────────────
        // Creates an AudioClip whose PCMReaderCallback reads directly from the live
        // reader's interleaved-float fast path (every native reader is an
        // IFloatPcmSource emitting IEEE float). No full-decode allocation; memory cost
        // ≈ the reader's internal packet buffer (a few KB at most).
        //
        // IMPORTANT: `reader` must stay alive (not disposed) until after
        // Destroy(clip) has been called AND at least one frame has passed so
        // the audio thread is off the clip.  ContextCache.DisposeCurrentReader()
        // handles this correctly when called from DeferredDestroyPending().
        public static AudioClip CreateStreamingClipFromReader(
            WaveStream reader, string name)
        {
            if (reader == null) return null;

            var directSrc = reader as IFloatPcmSource;
            if (directSrc == null)
            {
                Log.Warning($"[CMM] '{name}' reader is not a float source — skipped.");
                return null;
            }

            int channels = reader.WaveFormat.Channels;
            int sampleRate = reader.WaveFormat.SampleRate;
            CMMConfig.D($"[CMM] Clip '{name}': IEEE float {sampleRate}Hz {channels}ch");

            // Length is in bytes of IEEE float (BlockAlign = channels*4), so convert
            // back to sample-frames for AudioClip. The reader already outputs IEEE
            // float and has its own ReadFloats fast path — no PCM conversion needed.
            int totalSamples = (int)(reader.Length / (channels * 4L));

            using (CMMPerf.Measure(CMMPerf.Phase.ClipCreate))
            return AudioClip.Create(name, totalSamples, channels, sampleRate, true,
                (float[] data) =>
                {
                    // Time the on-demand decode+disk read; this runs on the AUDIO thread.
                    long _t0 = Stopwatch.GetTimestamp();
                    int filled = directSrc.ReadFloats(data, 0, data.Length);
                    CMMPerf.PcmCallback(Stopwatch.GetTimestamp() - _t0);
                    if (filled < data.Length)
                        Array.Clear(data, filled, data.Length - filled);
                    PcmHeartbeat += filled;
                },
                (int newPosition) =>
                {
                    // Unity passes sample-frame position (not float index); the native
                    // reader's Position is in bytes of IEEE float.
                    try { reader.Position = (long)newPosition * channels * 4L; }
                    catch (Exception ex)
                    { UnityEngine.Debug.LogWarning($"[CMM] seek error at frame {newPosition}: {ex.Message}"); }
                }
            );
        }

        public static long ClampLong(long value, long min, long max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        // ── Full-decode path (menu music + PreloadCacheEnabled = true) ─────────
        public static DecodedAudio DecodeFileFromBytes(byte[] rawBytes, string filePath)
        {
            try
            {
                string ext = System.IO.Path.GetExtension(filePath).ToLower();

                // Every format decodes natively (CMM_Decoder.dll). The native readers are
                // IeeeFloat WaveStreams, so the shared DecodeWaveStream drain handles
                // them identically regardless of codec.
                using (WaveStream reader = OpenNativeReaderFromMemory(rawBytes, rawBytes.Length, ext))
                {
                    if (reader == null) return null;
                    return DecodeWaveStream(reader, filePath);
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] DecodeFileFromBytes failed for {Path.GetFileName(filePath)}: {ex.Message}");
                return null;
            }
        }

        private static DecodedAudio DecodeWaveStream(WaveStream reader, string filePath)
        {
            var src = reader as IFloatPcmSource;
            if (src == null)
            {
                Log.Warning($"[CMM] '{Path.GetFileName(filePath)}' reader is not a float source — skipped.");
                return null;
            }

            int channels = reader.WaveFormat.Channels;
            int frequency = reader.WaveFormat.SampleRate;

            const long MaxFloats = 48000L * 2 * 60 * 30;
            long estimatedFloats = (reader.Length > 0 && reader.WaveFormat.BlockAlign > 0)
                ? (reader.Length / reader.WaveFormat.BlockAlign) * channels : 0;
            float[] result = new float[Math.Min(Math.Max(estimatedFloats, 65536L), MaxFloats)];

            var buf = new float[65536];
            int offset = 0, read;

            while ((read = src.ReadFloats(buf, 0, buf.Length)) > 0)
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

        // Legacy clip factory — still used when PreloadCacheEnabled = true.
        public static AudioClip CreateStreamingClip(DecodedAudio audio, string name)
        {
            if (audio?.Samples == null || audio.Samples.Length == 0) return null;

            float[] samples = audio.Samples;
            int channels = audio.Channels;
            int frequency = audio.Frequency;
            int totalSamples = samples.Length / channels;
            int cursor = 0;

            using (CMMPerf.Measure(CMMPerf.Phase.ClipCreate))
            return AudioClip.Create(name, totalSamples, channels, frequency, true,
                (float[] data) =>
                {
                    long _t0 = Stopwatch.GetTimestamp();
                    int srcStart = cursor * channels;
                    int toCopy = Math.Min(data.Length, samples.Length - srcStart);
                    if (toCopy > 0) Array.Copy(samples, srcStart, data, 0, toCopy);
                    if (toCopy < data.Length) Array.Clear(data, toCopy, data.Length - toCopy);
                    if (toCopy > 0) PcmHeartbeat += toCopy;
                    cursor += data.Length / channels;
                    CMMPerf.PcmCallback(Stopwatch.GetTimestamp() - _t0);
                },
                (int newPosition) =>
                {
                    cursor = Mathf.Clamp(newPosition, 0, totalSamples - 1);
                }
            );
        }

        // Non-streaming clip: PCM lives in Unity's native audio buffer so the
        // audio thread never calls into managed code. Immune to GC pauses.
        public static AudioClip CreateNonStreamingClip(DecodedAudio audio, string name)
        {
            if (audio?.Samples == null || audio.Samples.Length == 0) return null;
            int totalSamples = audio.Samples.Length / audio.Channels;
            using (CMMPerf.Measure(CMMPerf.Phase.ClipCreate))
            {
                var clip = AudioClip.Create(name, totalSamples, audio.Channels, audio.Frequency, false);
                clip.SetData(audio.Samples, 0);   // full PCM upload — main-thread native cost
                return clip;
            }
        }

        // ── Quick validate ────────────────────────────────────────────────────
        public static bool QuickValidate(string filePath)
        {
            try
            {
                // Native file-open reads just the header (+ seek table / frame scan)
                // without a full decode. A bad header throws and is caught below →
                // invalid. OpenNativeReader routes every supported format.
                using (WaveStream reader = OpenNativeReader(filePath))
                {
                    if (reader == null) return false;
                    return reader.WaveFormat.SampleRate > 0
                        && reader.WaveFormat.Channels > 0
                        && reader.Length > 0
                        // Reject sub-half-second stubs (truncated/corrupt files whose
                        // header still parses), matching the Unity-clip validation path.
                        && reader.TotalTime.TotalSeconds > 0.5;
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
}
