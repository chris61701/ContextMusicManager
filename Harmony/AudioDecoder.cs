
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

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
        private static readonly HashSet<string> _supportedExts =
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
            return _supportedExts.Contains(ext);
        }

        // Returns true if this file must be fully decoded before playback
        // (i.e. it is an Opus stream AND the caller has not yet built a page
        // index for it — which OpenStreamingReader handles automatically).
        // Exposed so callers can log the code path taken.
        public static bool IsOpusFile(string filePath)
        {
            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            return ext == ".opus"
                || ((ext == ".ogg" || ext == ".oga") && IsOpusOgg(filePath));
        }

        // ── OGG codec detection ───────────────────────────────────────────────

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

        // Opens a BARE native decoder handle (no managed WaveStream wrapper) for the
        // native decoder handoff used by the loudness analyzer (and, later, the mixer):
        // returns the raw cmm_decoder* plus its format `kind` (0..6 — the cmm_loudness /
        // cmm_mix mapping, which is ClassifyReader − 1) and PCM format. The caller passes
        // (handle, kind) to a native consumer that TAKES OWNERSHIP and closes it
        // (cmm_loudness_analyze / cmm_mix_add). Returns IntPtr.Zero (kind = -1) for an
        // unknown extension or a decode-open failure. Same header-sniff as OpenNativeReader.
        // DIAG: native open error from the most recent OpenNativeHandle call on THIS
        // thread (measures run on background threads). -1 = file open (share/IO) failed,
        // -2 = decode-open failed, -3 = alloc, 0 = ok. Thread-static so concurrent
        // prescan lanes don't clobber each other's code.
        [ThreadStatic] internal static int LastNativeOpenErr;

        internal static IntPtr OpenNativeHandle(string filePath, out int kind,
            out int channels, out int sampleRate, out long totalFrames)
        {
            kind = -1; channels = 0; sampleRate = 0; totalFrames = 0;
            string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            bool isOpus = ext == ".opus" ||
                ((ext == ".ogg" || ext == ".oga") && IsOpusOgg(filePath));

            IntPtr h; int err = 0;
            switch (ClassifyReader(ext, isOpus))
            {
                case ReaderKind.Opus:      kind = 0; h = NativeOpus.cmm_opus_open_file(filePath, out channels, out sampleRate, out totalFrames, out err); break;
                case ReaderKind.Flac:      kind = 1; h = NativeFlac.cmm_flac_open_file(filePath, out channels, out sampleRate, out totalFrames, out err); break;
                case ReaderKind.Vorbis:    kind = 2; h = NativeVorbis.cmm_vorbis_open_file(filePath, out channels, out sampleRate, out totalFrames, out err); break;
                case ReaderKind.Mp3:       kind = 3; h = NativeMp3.cmm_mp3_open_file(filePath, out channels, out sampleRate, out totalFrames, out err); break;
                case ReaderKind.Wav:       kind = 4; h = NativeWav.cmm_wav_open_file(filePath, out channels, out sampleRate, out totalFrames, out err); break;
                case ReaderKind.Aac:       kind = 5; h = NativeAac.cmm_aac_open_file(filePath, out channels, out sampleRate, out totalFrames, out err); break;
                case ReaderKind.Container: kind = 6; h = NativeContainer.cmm_container_open_file(filePath, out channels, out sampleRate, out totalFrames, out err); break;
                default: WarnNoReader(ext); LastNativeOpenErr = 0; return IntPtr.Zero;
            }
            LastNativeOpenErr = err;   // DIAG: -1 fopen/share fail, -2 decode-open fail, -3 alloc
            if (h == IntPtr.Zero) { kind = -1; return IntPtr.Zero; }
            return h;
        }

        // Close a bare native handle from OpenNativeHandle that was NOT handed to a native
        // consumer (e.g. a start superseded between open and cmm_mix_add). cmm_mix_add /
        // cmm_loudness_analyze take ownership and close the handle themselves, so this is ONLY
        // for the open-but-dropped path. `kind` is the 0..6 value OpenNativeHandle returned.
        internal static void CloseNativeHandle(IntPtr dec, int kind)
        {
            if (dec == IntPtr.Zero) return;
            switch (kind)
            {
                case 0: NativeOpus.cmm_opus_close(dec); break;
                case 1: NativeFlac.cmm_flac_close(dec); break;
                case 2: NativeVorbis.cmm_vorbis_close(dec); break;
                case 3: NativeMp3.cmm_mp3_close(dec); break;
                case 4: NativeWav.cmm_wav_close(dec); break;
                case 5: NativeAac.cmm_aac_close(dec); break;
                case 6: NativeContainer.cmm_container_close(dec); break;
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

        private static void WarnNoReader(string ext)
        {
            Log.Warning($"[CMM] No native reader for extension '{ext}'");
        }

        // ── Loudness measurement ──────────────────────────────────────────────
        // One full decode pass to integrated LUFS (EBU R128), plus the FNV-1a content hash
        // computed from a separate streaming byte read. No PCM is retained and NOTHING lands on
        // the GC heap: OpenNativeHandle opens a native decoder and cmm_loudness_analyze decodes
        // + measures it entirely in native (malloc) memory, so a measurement can't trigger a
        // stop-the-world collection that hitches the game (the point of the native port).
        //
        // Called on a BACKGROUND thread (by the menu prewarm / look-ahead / block-before-play).
        // Pure measurement — touches no Unity main-thread state. The caller commits the
        // TrackLoudness into LoudnessStore on the main thread.
        public static TrackLoudness MeasureLoudness(string filePath)
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
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            try
            {
                // No size cap: the native decoder streams files larger than its buffered-open
                // limit (CMM_BUFFER_MAX in CMM_Decoder.c; Windows: over a raw Win32 handle,
                // race-free and with a few KB of state), so even the 100 MB+ ambient FLACs
                // measure safely instead of playing at unity gain. Small files still buffer
                // natively; it's a RAM/speed tradeoff inside the decoder, not a
                // normalization boundary.
                size = new FileInfo(filePath).Length;
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

            try
            {
                // Decode + measure entirely in native code (CMM_Decoder.dll): open a bare
                // decoder handle here, then cmm_loudness_analyze reads it to EOF and runs the
                // K-weighting + gating in native (malloc) memory — nothing on the GC heap, so
                // the sweep can't trigger a stop-the-world collection that hitches the game.
                // It TAKES OWNERSHIP of the handle (closes it), so there's no managed reader to
                // dispose. The OS page cache from the hash pass above usually serves this second
                // read warm, so it is mostly CPU (decode), not a second cold disk hit.
                long tIdx = Stopwatch.GetTimestamp();
                int kind, channels, sampleRate;
                long total;
                IntPtr dec = OpenNativeHandle(filePath, out kind, out channels, out sampleRate, out total);
                indexMs = ElapsedMs(tIdx);
                if (dec == IntPtr.Zero)
                {
                    // DIAG: distinguish an OPEN failure (share/IO = -1, decode-open = -2)
                    // from an analyze failure below. This is the FLAC-only in-game symptom
                    // we're hunting — a measure that logs decode=0ms / no LUFS.
                    Log.Warning($"[CMM DIAG] Loudness OPEN FAILED {Path.GetFileName(filePath)} " +
                                $"({ext}) kind={kind} err={LastNativeOpenErr} " +
                                "(-1=file-open/share, -2=decode-open, -3=alloc)");
                    return fail;
                }

                long tAnalyze = Stopwatch.GetTimestamp();
                double lufs, peak;
                int valid = NativeLoudness.cmm_loudness_analyze(dec, kind, sampleRate, channels, out lufs, out peak);
                analyzeMs = ElapsedMs(tAnalyze);
                if (valid == 0)
                {
                    // DIAG: open SUCCEEDED but analyze produced no gated result — either the
                    // decoder read 0 frames or the track gated out (too short/quiet). ch/rate/
                    // total let us tell a bad open (0/0/0) from a genuine gating result.
                    Log.Warning($"[CMM DIAG] Loudness ANALYZE=0 {Path.GetFileName(filePath)} " +
                                $"({ext}) kind={kind} ch={channels} rate={sampleRate} " +
                                $"total={total} analyzeMs={analyzeMs:F1}");
                    return fail;
                }

                return new TrackLoudness
                {
                    Ok = true,
                    Hash = hash,
                    Size = size,
                    Lufs = lufs,
                    Peak = peak,
                };
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] Loudness: measure failed for {Path.GetFileName(filePath)}: {ex.Message}");
                return fail;
            }
            finally
            {
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

        // ── Quick validate ────────────────────────────────────────────────────
        public static bool QuickValidate(string filePath)
        {
            try
            {
                // Native file-open reads just the header (+ seek table / frame scan)
                // without a full decode; a failed open → invalid. Same acceptance as
                // the retired WaveStream path: positive rate/channels, a known length,
                // and more than half a second of audio (rejects truncated/corrupt
                // stubs whose header still parses; unknown length — e.g. raw ADTS —
                // reads as totalFrames <= 0 and is rejected, exactly as Length == 0 was).
                IntPtr dec = OpenNativeHandle(filePath, out int kind,
                    out int channels, out int sampleRate, out long totalFrames);
                if (dec == IntPtr.Zero) return false;
                CloseNativeHandle(dec, kind);
                return sampleRate > 0
                    && channels > 0
                    && totalFrames > 0
                    && (double)totalFrames / sampleRate > 0.5;
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
