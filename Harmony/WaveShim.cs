using System;
using System.IO;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // WAVE SHIM — in-repo replacements for the two NAudio types CMM still needed
    // ─────────────────────────────────────────────────────────────────────────
    // Once every format decodes natively (CMM_Decoder.dll) the ONLY thing NAudio was
    // still providing was the `WaveStream` base class and the `WaveFormat` value
    // object — eight DLLs of dependency for two small types. These tiny shims drop
    // that whole stack (see [[native-audio-decode]]).
    //
    // Surface kept deliberately minimal: exactly the members CMM's player + decoder
    // use (WaveFormat / Length / Position / Read / TotalTime / Dispose, and the
    // WaveFormat fields SampleRate / Channels / BlockAlign). All audio is interleaved
    // 32-bit IEEE float (the native readers' output), so there is no PCM-encoding or
    // sample-format machinery here — that died with NAudio.

    // Format descriptor for a stream. Only the IEEE-float factory is exposed because
    // that is the only format the native readers ever produce.
    public sealed class WaveFormat
    {
        public int SampleRate            { get; }
        public int Channels              { get; }
        public int BitsPerSample         { get; }
        public int BlockAlign            { get; }   // bytes per sample-frame (Channels * BitsPerSample/8)
        public int AverageBytesPerSecond { get; }   // BlockAlign * SampleRate (drives TotalTime/CurrentTime)

        private WaveFormat(int sampleRate, int channels, int bitsPerSample)
        {
            SampleRate            = sampleRate;
            Channels              = channels;
            BitsPerSample         = bitsPerSample;
            BlockAlign            = channels * (bitsPerSample / 8);
            AverageBytesPerSecond = sampleRate * BlockAlign;
        }

        // Interleaved 32-bit IEEE float — the one format the native decoders emit.
        public static WaveFormat CreateIeeeFloatWaveFormat(int sampleRate, int channels)
            => new WaveFormat(sampleRate, channels, 32);
    }

    // Abstract seekable PCM stream. Extends System.IO.Stream — like NAudio's
    // WaveStream did — so concrete readers override Read/Position/Length/Dispose(bool)
    // unchanged and any incidental Stream consumer still works. Position/Length are in
    // BYTES of the WaveFormat (BlockAlign-aligned), matching the prior NAudio contract.
    public abstract class WaveStream : Stream
    {
        public abstract WaveFormat WaveFormat { get; }

        // Read-only, seekable, non-writable — same as NAudio's WaveStream.
        public override bool CanRead  => true;
        public override bool CanSeek  => true;
        public override bool CanWrite => false;

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Current: Position += offset;        break;
                case SeekOrigin.End:     Position = Length + offset; break;
                default:                 Position = offset;          break;
            }
            return Position;
        }

        // Convenience time accessors (QuickValidate uses TotalTime). Computed from the
        // byte Length/Position over AverageBytesPerSecond, exactly as NAudio did.
        public TimeSpan TotalTime => WaveFormat.AverageBytesPerSecond > 0
            ? TimeSpan.FromSeconds((double)Length / WaveFormat.AverageBytesPerSecond)
            : TimeSpan.Zero;

        public TimeSpan CurrentTime => WaveFormat.AverageBytesPerSecond > 0
            ? TimeSpan.FromSeconds((double)Position / WaveFormat.AverageBytesPerSecond)
            : TimeSpan.Zero;
    }
}
