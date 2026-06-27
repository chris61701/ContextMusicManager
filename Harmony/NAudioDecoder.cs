
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEngine;
using NAudio.Wave;
using NAudio.Vorbis;
using Concentus;
using Concentus.Oggfile;
using CSCore.Codecs.FLAC;

namespace CMM
{
    // Result of a one-pass loudness measurement (see NAudioDecoder.MeasureLoudness).
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
    // NAudio DECODER
    // ─────────────────────────────────────────────────────────────────────────

    public static class NAudioDecoder
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
            private IOpusDecoder _decoder;
            private long _samplePos;   // current position in frames

            // One-packet decode buffer.  Filled by DecodeNextPacketLocked(),
            // drained by Read().  Never allocated larger than one Opus frame
            // (120ms × channels × 48kHz = 5760 samples × channels).
            private float[] _pcmBuf = Array.Empty<float>();
            private int _pcmBufStart = 0;
            private int _pcmBufEnd = 0;   // exclusive
            private float[] _readBuf = new float[8192]; // covers typical Unity audio buffer sizes
            private byte[] _headerBytes; // Ogg header pages (BOS + comment), cached for PrependStream mid-file seeks



            public SeekableOpusReader(string filePath, OggPageIndex index)
            {
                _filePath = filePath;
                _index = index;
                _format = WaveFormat.CreateIeeeFloatWaveFormat(48000, index.Channels);
                // Open the FileStream once here and keep it for the lifetime of the reader.
                _fileStream = new FileStream(filePath, FileMode.Open,
                                             FileAccess.Read, FileShare.Read,
                                             bufferSize: 65536);
                _decoder = OpusCodecFactory.CreateDecoder(48000, index.Channels);
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
            // Armed by SeekBeforePlay, disarmed by ClearInitialSeekIntercept once the
            // resume's explicit time-set has committed. While armed, SeekToSampleLocked
            // ignores Unity's redundant post-Play PCMSetPositionCallback(s) so they can't
            // re-open the stream mid-file and strand it at premature EOS (see the comment
            // there). Survives the PCMReaderCallback-fires-first race because the reader is
            // never moved off the SeekBeforePlay position while armed.
            private bool _interceptNextSeek = false;
            // One-shot flag: log end-of-stream once per decoder session (reset on reopen)
            // so the audio thread doesn't spam the log on every buffer fill after EOS.
            private volatile bool _eosLogged = false;

            // Disarm the post-Play seek interceptor once the resume seek has been committed
            // (the explicit AudioSource.time set already positioned the reader). Without this
            // the interceptor stays armed and a later loop restart's SetPosition(0) would be
            // wrongly swallowed instead of restarting at 0.
            public void ClearInitialSeekIntercept()
            {
                lock (_lock) _interceptNextSeek = false;
            }

            // Pre-seek to targetFrame from byte 0 via decode-and-discard.
            // Call BEFORE AudioSource.Play() on the main or a background thread.
            // This bypasses PrependStream entirely — correct for any seek distance
            // regardless of Ogg sequence-number continuity.  Also arms the post-Play
            // seek interceptor so Unity's redundant position callbacks are ignored.
            public void SeekBeforePlay(long targetFrame)
            {
                lock (_lock)
                {
                    OpenStreamAtByte(0);
                    if (targetFrame > 0)
                        DiscardSamplesLocked(targetFrame * _format.Channels);
                    _interceptNextSeek = true;
                }
            }

            // ── Seeking ───────────────────────────────────────────────────────
            private void SeekToSampleLocked(long targetFrame)
            {
                // Intercept the PCMSetPositionCallback(s) Unity fires right after Play().
                // SeekBeforePlay has ALREADY positioned the reader at _pendingInitialFrame
                // via the reliable from-byte-0 decode-discard path, so every position
                // callback that arrives while the interceptor is armed is redundant.
                // Honoring one would re-run the seek through the mid-file OpenStreamAtByte
                // PrependStream path, whose OpusOggReadStream yields a prematurely-EOS
                // stream (ReadFloats then returns 0 forever → permanent silence with
                // isPlaying=true). So IGNORE armed callbacks entirely and leave the reader
                // exactly where it is. Stays armed across MULTIPLE callbacks until the
                // resume's explicit time-set commits (ClearInitialSeekIntercept) — A20's
                // stale AudioSource.time fires an extra callback at the PREVIOUS resume frame
                // here (e.g. 8.0s when resuming to 17.2s), which is what made the silent
                // strand reproducible there.
                //
                // We do NOT touch the reader: by the time the time-set callback arrives the
                // audio thread has usually read a few DSP buffers, so _samplePos has drifted
                // a few ms PAST the pre-seek frame — that is correct forward playback, not an
                // error. Re-seeking back (the old "defensive" reopen) decoded the whole
                // prefix from byte 0 synchronously ON THE MAIN THREAD (the time-set fires the
                // callback inline), which both stuttered the resume and replayed already-
                // delivered samples. Doing nothing is both correct and free.
                if (_interceptNextSeek)
                {
                    _seekCount++;
                    CMMConfig.D($"[CMM] OpusSeek #{_seekCount}: intercepted callback " +
                                $"frame={targetFrame} → held at reader pos {_samplePos} ({_samplePos / 48000f:F2}s)");
                    return;
                }

                _seekCount++;

                if (_index.Count == 0)
                {
                    OpenStreamAtByte(0);
                    _samplePos = 0;
                    return;
                }

                int pageIdx        = _index.FindPageForSample(targetFrame);
                // Go back 4 pages for ~80ms of Opus pre-roll. RFC 7845 §7 requires
                // 80ms of decoder history after a seek; 2 pages was only ~40ms for
                // typical ffmpeg-encoded files (1 frame/page × 20ms/frame).
                int prerollIdx     = Math.Max(0, pageIdx - 4);
                long pageByteOffset = _index.ByteOffsets[prerollIdx];

                const float fastForwardWindowSeconds = 2f;
                bool canFastForward =
                    _samplePos <= targetFrame &&
                    (targetFrame - _samplePos) < (long)(48000 * fastForwardWindowSeconds);

                CMMConfig.D($"[CMM] OpusSeek #{_seekCount}: frame={targetFrame} ({targetFrame / 48000f:F2}s) samplePos={_samplePos} fastFwd={canFastForward}");
                if (!canFastForward)
                    OpenStreamAtByte(pageByteOffset);

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
                var t0 = ProfStart();
                _pcmBufStart = 0;
                _pcmBufEnd = 0;
                _eosLogged = false;

                // Cache the Ogg header pages (BOS + comment) on first open.
                // These are ~200 bytes and are needed by PrependStream so that
                // OpusOggReadStream always sees a valid sequential stream starting
                // with the mandatory header pages, regardless of where in the file
                // we seek to.  Without this, the container reader never registers
                // the logical stream serial number and delivers no packets.
                if (_headerBytes == null && _index.Count > 0)
                {
                    long headerEnd = _index.ByteOffsets[0];
                    _fileStream.Position = 0;
                    _headerBytes = new byte[headerEnd];
                    int total = 0, r;
                    while (total < (int)headerEnd &&
                           (r = _fileStream.Read(_headerBytes, total, (int)headerEnd - total)) > 0)
                        total += r;
                }

                _decoder?.Dispose();
                _decoder = OpusCodecFactory.CreateDecoder(48000, _format.Channels);

                // A seek at or before the first audio page (ByteOffsets[0]) is just a
                // read from the top: the header pages occupy [0, ByteOffsets[0]) and the
                // first audio page is the natural continuation of the top-down stream.
                // Routing it through the PrependStream mid-file path instead produces an
                // OpusOggReadStream that yields no packets (immediate EOS) — which silently
                // killed playback whenever Unity's PCMSetPositionCallback(0) fired after the
                // audio thread had already advanced _samplePos past 0 during clip priming.
                bool atOrBeforeFirstPage = _index.Count > 0 && byteOffset <= _index.ByteOffsets[0];
                if (byteOffset == 0 || _headerBytes == null || atOrBeforeFirstPage)
                {
                    // Reading from the top — standard path, no PrependStream needed.
                    _fileStream.Position = 0;
                    _oggStream = new OpusOggReadStream(_decoder, _fileStream);
                    _samplePos = 0;
                }
                else
                {
                    // Mid-file seek: present header bytes followed by audio data
                    // from byteOffset as one sequential stream.  OpusOggReadStream
                    // reads the header pages from _headerBytes, then reads audio
                    // pages from _fileStream — no post-construction repositioning,
                    // so any internal read-ahead buffer stays correctly aligned.
                    _fileStream.Position = byteOffset;
                    _oggStream = new OpusOggReadStream(_decoder,
                        new PrependStream(_headerBytes, _fileStream));
                    _samplePos = 0;
                    for (int i = _index.Count - 1; i >= 0; i--)
                    {
                        if (_index.ByteOffsets[i] <= byteOffset)
                        {
                            // GranulePositions[i] is the last sample of page i.
                            // GranulePositions[i-1] is the last sample of the previous
                            // page, which equals the index of the first sample of page i.
                            // Using this avoids negative discard when pages contain more
                            // than 1920 samples (e.g. 4+ frames per page from ffmpeg).
                            _samplePos = (i > 0) ? _index.GranulePositions[i - 1] : 0L;
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
                _decoder = OpusCodecFactory.CreateDecoder(48000, index.Channels);
                OpenStreamAtByte(0);
            }

            // Decodes the next Ogg/Opus packet into _pcmBuf.
            // Returns false at end of stream.
            private bool FillPacketBufferLocked()
            {
                long t0 = Stopwatch.GetTimestamp();
                if (_oggStream == null || !_oggStream.HasNextPacket)
                {
                    if (!_eosLogged)
                    {
                        _eosLogged = true;
                        CMMConfig.D($"[CMM] Opus: stream end at samplePos={_samplePos} ({_samplePos / 48000f:F2}s)");
                    }
                    return false;
                }

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
                if (ms > 1f) CMMConfig.D($"[CMM PERF] OpusDecodePacket: {ms:F2}ms");
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
                        _decoder?.Dispose();
                        _decoder = null;
                    }
                }
                base.Dispose(disposing);
            }

            // Presents a fixed byte[] prefix followed by reads from a tail Stream,
            // as if they were a single contiguous sequential stream.  Used to feed
            // OpusOggReadStream the cached Ogg header pages before audio data from
            // a mid-file seek position, without ever repositioning _fileStream after
            // the OpusOggReadStream constructor runs.
            private sealed class PrependStream : Stream
            {
                private readonly byte[] _prefix;
                private readonly Stream _tail;
                private int _prefixPos;

                public PrependStream(byte[] prefix, Stream tail)
                {
                    _prefix = prefix;
                    _tail = tail;
                }

                public override bool CanRead  => true;
                public override bool CanSeek  => false;
                public override bool CanWrite => false;
                public override long Length   => throw new NotSupportedException();
                public override long Position
                {
                    get => _prefixPos < _prefix.Length ? _prefixPos : _prefix.Length + _tail.Position;
                    set => throw new NotSupportedException();
                }
                public override void Flush() { }
                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value)                 => throw new NotSupportedException();
                public override void Write(byte[] buf, int off, int cnt)   => throw new NotSupportedException();

                public override int Read(byte[] buffer, int offset, int count)
                {
                    if (_prefixPos < _prefix.Length)
                    {
                        int available = _prefix.Length - _prefixPos;
                        int toCopy = Math.Min(available, count);
                        Array.Copy(_prefix, _prefixPos, buffer, offset, toCopy);
                        _prefixPos += toCopy;
                        return toCopy;
                    }
                    return _tail.Read(buffer, offset, count);
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // CSCORE FLAC READER
        // Adapts CSCore.Codecs.FLAC.FlacFile (net35, no Span<T>) to the NAudio
        // WaveStream contract so it slots into CreateStreamingClipFromReader
        // identically to the other format readers.  Pre-scans the file/stream
        // on construction (synchronous, always called on a background thread)
        // to build CSCore's internal seek index, enabling accurate Position.set.
        // ═════════════════════════════════════════════════════════════════════
        private sealed class CsCoreFlacReader : WaveStream
        {
            private readonly CSCore.Codecs.FLAC.FlacFile _flac;
            private readonly WaveFormat _waveFormat;

            public CsCoreFlacReader(string filePath)
            {
                _flac = new FlacFile(filePath);
                _waveFormat = BuildFormat(_flac.WaveFormat);
            }

            public CsCoreFlacReader(Stream stream)
            {
                _flac = new FlacFile(stream, FlacPreScanMode.Sync);
                _waveFormat = BuildFormat(_flac.WaveFormat);
            }

            private static WaveFormat BuildFormat(CSCore.WaveFormat f)
                => new WaveFormat(f.SampleRate, f.BitsPerSample, f.Channels);

            public override WaveFormat WaveFormat => _waveFormat;
            public override long Length         => _flac.Length;
            public override long Position
            {
                get => _flac.Position;
                set => _flac.Position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
                => _flac.Read(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                if (disposing) _flac.Dispose();
                base.Dispose(disposing);
            }
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
                CMMConfig.D($"[CMM PERF] OpenStreamingReader called on main thread for {Path.GetFileName(filePath)}!");

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
                        return new CsCoreFlacReader(ms);
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
                case ".flac": return new CsCoreFlacReader(filePath);
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
                case ".flac": return new CsCoreFlacReader(stream);
                default:
                    Log.Warning($"[CMM NAudio] No stream reader for extension '{ext}'");
                    return null;
            }
        }

        // ── Loudness measurement ──────────────────────────────────────────────
        // One full decode pass to integrated LUFS (EBU R128), plus the FNV-1a
        // content hash computed from the same byte read. No PCM is retained — the
        // R128Analyzer accumulates running statistics, so memory stays at a few KB
        // regardless of track length. Mirrors the byte-read + Opus/generic reader
        // branch in OpenStreamingReader / CreateStreamingClipFromReader.
        //
        // Called on a BACKGROUND thread (by the loudness sweep / block-before-play).
        // Pure measurement — touches no Unity main-thread state. The caller commits
        // the TrackLoudness into LoudnessStore on the main thread.
        public static TrackLoudness MeasureLoudness(string filePath)
        {
            var fail = new TrackLoudness { Ok = false };

            byte[] raw;
            long size;
            try
            {
                size = new FileInfo(filePath).Length;
                if (size > CMMConfig.HotSwapMaxFileSizeBytes)
                {
                    Log.Warning($"[CMM] Loudness: {Path.GetFileName(filePath)} too large to buffer " +
                                "— skipping normalization (plays at unity gain).");
                    return fail;
                }
                raw = File.ReadAllBytes(filePath);
            }
            catch (Exception ex)
            {
                Log.Warning($"[CMM] Loudness: could not read {Path.GetFileName(filePath)}: {ex.Message}");
                return fail;
            }

            string hash = LoudnessStore.HashBytes(raw, raw.Length);
            string ext = Path.GetExtension(filePath).ToLower();
            bool isOpus = ext == ".opus" ||
                ((ext == ".ogg" || ext == ".oga") && IsOpusOggBytes(raw));

            WaveStream reader = null;
            try
            {
                if (isOpus)
                {
                    OggPageIndex index = OggPageIndex.BuildFromBytes(raw, filePath);
                    reader = new SeekableOpusReader(raw, index, filePath);
                }
                else
                {
                    reader = OpenReaderFromMemory(raw, ext, filePath);
                }
                if (reader == null) return fail;

                LoudnessResult result = AnalyzeToEnd(reader, filePath);
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
            }
        }

        // Pulls the whole stream through the R128 analyzer. Uses SeekableOpusReader's
        // ReadFloats fast path for Opus (it already outputs IEEE float), and the same
        // PCM→float conversion as the clip factory for everything else.
        private static LoudnessResult AnalyzeToEnd(WaveStream reader, string filePath)
        {
            var buf = new float[16384];

            if (reader is SeekableOpusReader opus)
            {
                var analyzer = new R128Analyzer(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
                int n;
                while ((n = opus.ReadFloats(buf, 0, buf.Length)) > 0)
                    analyzer.Feed(buf, n);
                return analyzer.Finish();
            }

            var enc = reader.WaveFormat.Encoding;
            int bps = reader.WaveFormat.BitsPerSample;
            ISampleProvider src;
            if (enc == WaveFormatEncoding.IeeeFloat)
                src = reader.ToSampleProvider();
            else if (enc == WaveFormatEncoding.Pcm && (bps == 8 || bps == 16 || bps == 24 || bps == 32))
                src = new PcmToFloatProvider(reader);
            else
            {
                Log.Warning($"[CMM] Loudness: '{Path.GetFileName(filePath)}' unsupported encoding " +
                            $"'{enc}' {bps}bit — skipping normalization.");
                return new LoudnessResult { Valid = false };
            }

            var analyzer2 = new R128Analyzer(src.WaveFormat.SampleRate, src.WaveFormat.Channels);
            int read;
            while ((read = src.Read(buf, 0, buf.Length)) > 0)
                analyzer2.Feed(buf, read);
            return analyzer2.Finish();
        }

        // ── PCM → float converter ─────────────────────────────────────────────
        // Replaces NAudio's SampleChannel/ConvertWaveProviderIntoSampleProvider,
        // which triggers a hard JIT crash in Mono 2021.3.19 (Unity A21) for both
        // the ACM fallback path (unsupported encodings) and the Pcm16BitToSampleProvider
        // path (valid 16-bit PCM). Neither crash is catchable. This class is pure
        // managed code with no NAudio converter chain dependency.
        private sealed class PcmToFloatProvider : ISampleProvider
        {
            private readonly IWaveProvider _source;
            private readonly int _bytesPerSample;
            private byte[] _buf = new byte[4096];

            public WaveFormat WaveFormat { get; }

            public PcmToFloatProvider(IWaveProvider source)
            {
                _source = source;
                _bytesPerSample = source.WaveFormat.BitsPerSample / 8;
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                    source.WaveFormat.SampleRate, source.WaveFormat.Channels);
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int bytesNeeded = count * _bytesPerSample;
                if (_buf.Length < bytesNeeded) _buf = new byte[bytesNeeded];
                int bytesRead = _source.Read(_buf, 0, bytesNeeded);
                int samples = bytesRead / _bytesPerSample;
                switch (_bytesPerSample)
                {
                    case 2:
                        for (int i = 0; i < samples; i++)
                            buffer[offset + i] = (short)(_buf[i * 2] | (_buf[i * 2 + 1] << 8)) / 32768f;
                        break;
                    case 3:
                        for (int i = 0; i < samples; i++)
                        {
                            int s = _buf[i*3] | (_buf[i*3+1] << 8) | (_buf[i*3+2] << 16);
                            if (s > 0x7FFFFF) s -= 0x1000000;
                            buffer[offset + i] = s / 8388608f;
                        }
                        break;
                    case 4:
                        for (int i = 0; i < samples; i++)
                            buffer[offset + i] = (_buf[i*4] | (_buf[i*4+1] << 8) | (_buf[i*4+2] << 16) | (_buf[i*4+3] << 24)) / 2147483648f;
                        break;
                    default: // 8-bit unsigned
                        for (int i = 0; i < samples; i++)
                            buffer[offset + i] = (_buf[i] - 128) / 128f;
                        break;
                }
                if (samples < count) Array.Clear(buffer, offset + samples, count - samples);
                return samples;
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

            var enc = reader.WaveFormat.Encoding;
            int bps = reader.WaveFormat.BitsPerSample;
            CMMConfig.D($"[CMM NAudio] Clip '{name}': {enc} {bps}bit {reader.WaveFormat.SampleRate}Hz {reader.WaveFormat.Channels}ch");

            // SampleChannel internally uses Windows ACM P/Invoke for non-PCM formats, and its
            // Pcm16BitToSampleProvider path triggers a hard JIT crash in Mono 2021.3.19 (A21).
            // Both crash variants bypass .NET exception handling and cannot be caught. Use our
            // own PCM→float converter for all PCM depths to avoid the NAudio code path entirely.
            ISampleProvider src;
            if (enc == WaveFormatEncoding.IeeeFloat)
                src = reader.ToSampleProvider();
            else if (enc == WaveFormatEncoding.Pcm && (bps == 8 || bps == 16 || bps == 24 || bps == 32))
                src = new PcmToFloatProvider(reader);
            else
            {
                Log.Warning($"[CMM NAudio] '{name}' has unsupported encoding '{enc}' {bps}bit — " +
                            $"skipped to prevent crash. Re-encode as PCM WAV, OGG, FLAC, or MP3.");
                return null;
            }

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

                return AudioClip.Create(name, totalSamples, channels, sampleRate, true,
                    (float[] data) =>
                    {
                        var t0 = ProfStart();
                        int filled = directSrc.ReadFloats(data, 0, data.Length);
                        if (filled < data.Length)
                            Array.Clear(data, filled, data.Length - filled);
                        PcmHeartbeat += filled;
                        ProfEnd(t0, "PCMRead_Opus", 1f);
                    },
                    (int newPosition) =>
                    {
                        // Unity passes sample-frame position (not float index).
                        // Position property on SeekableOpusReader expects bytes.
                        try { reader.Position = (long)newPosition * channels * 4L; }
                        catch (Exception ex)
                        { UnityEngine.Debug.LogWarning($"[CMM] Opus seek error at frame {newPosition}: {ex.Message}"); }
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
                        try
                        {
                            int read = src.Read(data, 0, data.Length);
                            if (read < data.Length)
                                Array.Clear(data, read, data.Length - read);
                            PcmHeartbeat += read;
                        }
                        catch
                        {
                            // A late audio-thread callback can fire after the backing
                            // reader has been disposed on teardown. Output silence
                            // rather than throwing an NRE on every buffer fill.
                            Array.Clear(data, 0, data.Length);
                        }
                    }
                },
                (int newPosition) =>
                {
                    lock (streamLock)
                    {
                        try
                        {
                            if (reader.WaveFormat.BlockAlign > 0)
                                reader.Position = ClampLong(
                                    (long)newPosition * reader.WaveFormat.BlockAlign,
                                    0L, reader.Length - reader.WaveFormat.BlockAlign);
                        }
                        catch { /* reader disposed mid-seek during teardown */ }
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

        private static DecodedAudio DecodeWaveStream(WaveStream reader, string filePath)
        {
            var enc = reader.WaveFormat.Encoding;
            int bps = reader.WaveFormat.BitsPerSample;
            ISampleProvider samples;
            if (enc == WaveFormatEncoding.IeeeFloat)
                samples = reader.ToSampleProvider();
            else if (enc == WaveFormatEncoding.Pcm && (bps == 8 || bps == 16 || bps == 24 || bps == 32))
                samples = new PcmToFloatProvider(reader);
            else
            {
                Log.Warning($"[CMM NAudio] '{Path.GetFileName(filePath)}' has unsupported encoding '{enc}' {bps}bit — " +
                            $"skipped to prevent crash. Re-encode as PCM WAV, OGG, FLAC, or MP3.");
                return null;
            }

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
            using (var decoder = OpusCodecFactory.CreateDecoder(48000, channels))
            {
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
                    if (toCopy > 0) PcmHeartbeat += toCopy;
                    cursor += data.Length / channels;
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
            var clip = AudioClip.Create(name, totalSamples, audio.Channels, audio.Frequency, false);
            clip.SetData(audio.Samples, 0);
            return clip;
        }

        // ── Quick validate ────────────────────────────────────────────────────
        public static bool QuickValidate(string filePath)
        {
            try
            {
                string ext = System.IO.Path.GetExtension(filePath).ToLower();

                if (ext == ".opus" || ((ext == ".ogg" || ext == ".oga") && IsOpusOgg(filePath)))
                {
                    using (var decoder = OpusCodecFactory.CreateDecoder(48000, 2))
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
