
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
        // False while this is a PARTIAL reading — the integrated loudness of the part of
        // the file decoded so far, published early so a long track can be normalized before
        // it has been read to the end (see MeasureLoudness's progressive overload). The
        // reading is real and gated the same way; it just has less of the file behind it,
        // so the caller treats it as provisional and lets the final one supersede it.
        public bool Final;
        // True when the decode reached EOF but produced NO gated block, so the file has no
        // defined integrated loudness (under 400 ms, or entirely below the -70 LUFS gate).
        // Ok is still true: this is a finished answer, not a failure. See LoudnessStore's
        // Measurement.NoIntegrated for why that distinction is worth carrying.
        public bool NoIntegrated;
        // Seconds of audio behind this reading. Only meaningful on a partial; it is what
        // makes "how much do I trust this?" answerable at the call site.
        public double SecondsMeasured;
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
                // ISO-BMFF video containers. FFmpeg's mov demuxer (inside CMM_Decoder) reads
                // all of them, QuickTime .mov included, and plays the AAC or ALAC audio track
                // while skipping the video without reading it.
                ".m4v", ".3gp", ".mov",
                // Matroska / WebM — demuxed + remuxed into an existing codec backend by
                // the native container reader (NativeContainerReader / cmm_container_*).
                ".mkv", ".webm",
            };

        // How much of a file's header-reported duration must actually decode before the
        // result is trusted as a whole-file measurement. Deliberately loose: it is separating
        // "a few frames of encoder padding" from "the drive vanished", and those differ by
        // three orders of magnitude, so precision here would only create false alarms.
        private const double TruncationTolerance = 0.90;

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
                ext == ".m4v" || ext == ".3gp" || ext == ".mov") return ReaderKind.Aac;
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
            => MeasureLoudness(filePath, 0.0, null);

        // Progressive overload. `chunkSeconds` > 0 decodes the file in slices of about that
        // much AUDIO and invokes `onPartial` after each one with the integrated loudness of
        // everything read so far (Final = false); the return value is the same whole-file
        // measurement the one-shot produces, bit-identical, with Final = true.
        //
        // This exists because integrated loudness is a whole-file quantity, so until the last
        // sample is read there is nothing to say — and a 37-minute podcast takes ~9 s to read.
        // For that whole window the track played at the unmeasured fallback (deliberately biased
        // 5 dB quiet), which on those files is about 12 dB from where they belong; and since
        // measurements no longer persist across launches, that happened on every rejoin. A slice
        // costs a fraction of a second, so the first usable answer arrives about a second in and
        // is refined from there. Measured across 27 real files at a 5-minute slice, the first
        // answer was within 0.82 dB of the final one, worst case.
        //
        // `onPartial` is called on THIS (background) thread. It must not touch main-thread state —
        // the caller's job is to hand the reading to the main thread, exactly as it does the final
        // one. chunkSeconds <= 0 reads the whole file in one slice, so `onPartial` never fires and
        // the behaviour is the plain one-shot.
        public static TrackLoudness MeasureLoudness(string filePath, double chunkSeconds,
                                                    Action<TrackLoudness> onPartial)
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

                // The progressive handle takes ownership of `dec` — including when begin()
                // itself fails, which is why there is no close on this path.
                IntPtr prog = NativeLoudness.cmm_lprog_begin(dec, kind, sampleRate, channels);
                if (prog == IntPtr.Zero)
                {
                    Log.Warning($"[CMM DIAG] Loudness BEGIN FAILED {Path.GetFileName(filePath)} " +
                                $"({ext}) kind={kind} ch={channels} rate={sampleRate}");
                    return fail;
                }

                // A slice of <= 0 is passed through as-is: native reads such a step to EOF,
                // making it the same single pass the one-shot does. Do NOT express that as a
                // huge number of seconds — seconds × sample rate overflows long long, and the
                // overflowed frame target clamps to one frame per slice.
                double slice = chunkSeconds > 0 ? chunkSeconds : 0.0;
                double lufs = 0, peak = 0, secs = 0;
                int valid = 0, eof = 0;
                try
                {
                    while (eof == 0)
                    {
                        valid = NativeLoudness.cmm_lprog_step(prog, slice, out lufs, out peak,
                                                              out secs, out eof);
                        // Publish everything before the last slice as provisional. The final
                        // reading is returned, not published, so the caller commits it once.
                        if (eof == 0 && valid != 0 && onPartial != null)
                        {
                            onPartial(new TrackLoudness
                            {
                                Ok              = true,
                                Hash            = hash,
                                Size            = size,
                                Lufs            = lufs,
                                Peak            = peak,
                                Final           = false,
                                SecondsMeasured = secs,
                            });
                        }
                    }
                }
                finally { NativeLoudness.cmm_lprog_free(prog); }

                analyzeMs = ElapsedMs(tAnalyze);

                // ── Did we actually read the file, or did the read just stop? ──────────
                // Every decoder in the read table reports EOF as "0 frames", and dr_flac /
                // dr_mp3 report an IO FAILURE the same way — so a drive going away mid-decode
                // is indistinguishable, at the read call, from reaching the end. Left alone
                // that produces a plausible measurement of the first fraction of the file,
                // committed as final and authoritative: a 37-minute podcast cut off at 40
                // seconds gets the intro's loudness, uncapped, for the rest of the session,
                // and HasCurrent then reports it as current so nothing ever re-measures it.
                // 13 of 16 stations here live on a removable drive, so this is a Tuesday.
                //
                // The header knows how long the file is, so compare. Encoder delay/padding
                // moves this by milliseconds; a truncation moves it by minutes, so a coarse
                // threshold separates them cleanly without a per-format table. Unknown length
                // (raw ADTS reports 0) means no check is possible — measure and accept, which
                // is what we did before this existed.
                double headerSeconds = (total > 0 && sampleRate > 0) ? (double)total / sampleRate : 0;
                if (headerSeconds > 0 && secs < headerSeconds * TruncationTolerance)
                {
                    Log.Warning($"[CMM] Loudness: {Path.GetFileName(filePath)} stopped decoding at " +
                                $"{secs:F1}s of {headerSeconds:F1}s — treating as a failed read, not a " +
                                "measurement (drive or file went away mid-decode).");
                    return fail;
                }

                if (valid == 0)
                {
                    // No gated block. Two very different reasons, and they must not share an
                    // outcome: reading NOTHING is a failure to retry, while reading the whole
                    // file and finding no gated block is a finished answer that will never
                    // change however many times it is re-decoded (EBU R128 integrates over
                    // gated 400 ms blocks, so anything shorter yields zero of them).
                    if (secs <= 0)
                    {
                        Log.Warning($"[CMM DIAG] Loudness read 0 frames {Path.GetFileName(filePath)} " +
                                    $"({ext}) kind={kind} ch={channels} rate={sampleRate} " +
                                    $"total={total} analyzeMs={analyzeMs:F1}");
                        return fail;
                    }

                    CMMConfig.D($"[CMM] Loudness: {Path.GetFileName(filePath)} has no gated block " +
                                $"({secs:F2}s decoded) — no defined integrated loudness; " +
                                "recorded as measured, no correction.");
                    return new TrackLoudness
                    {
                        Ok              = true,
                        Hash            = hash,
                        Size            = size,
                        Lufs            = 0,
                        Peak            = peak,
                        Final           = true,
                        NoIntegrated    = true,
                        SecondsMeasured = secs,
                    };
                }

                return new TrackLoudness
                {
                    Ok              = true,
                    Hash            = hash,
                    Size            = size,
                    Lufs            = lufs,
                    Peak            = peak,
                    Final           = true,
                    SecondsMeasured = secs,
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
