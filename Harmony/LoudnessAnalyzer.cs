// Compiled ONLY under CMM_LOUDNESS_SELFTEST. Production loudness measurement runs
// natively (CMM_Decoder.dll cmm_loudness_* — a faithful port of this class); this
// managed analyzer is kept as the readable reference implementation and is
// exercised by R128SelfTest against EBU Tech 3341 reference tones.
#if CMM_LOUDNESS_SELFTEST
using System;
using System.Collections.Generic;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // LOUDNESS ANALYZER  (EBU R128 / ITU-R BS.1770 integrated loudness)
    // ─────────────────────────────────────────────────────────────────────────
    // Measures the perceived loudness of a track in LUFS so a per-track gain can
    // be derived (target − measured, in dB → linear). The analyzer is a pure,
    // streaming measurement object: Feed() it interleaved float PCM in arbitrary
    // chunks, then call Finish() once at end-of-stream. It never retains the PCM,
    // so a full-track scan costs only a few KB of working state regardless of
    // track length.
    //
    // Pipeline (per BS.1770-4):
    //   1. K-weighting   — two cascaded biquads per channel (a high-shelf "head"
    //                      model + a ~38 Hz high-pass), approximating ear
    //                      frequency sensitivity before any energy is summed.
    //   2. 400 ms blocks — mean square per channel, channel-weighted sum, with
    //                      75% overlap (100 ms hop) realised as a sliding window
    //                      over 100 ms sub-blocks.
    //   3. Gating        — drop blocks below the −70 LUFS absolute gate, then
    //                      below (mean − 10 LU) relative gate; the integrated
    //                      loudness is the mean energy of the survivors.
    //
    // Sample-rate aware: the biquad coefficients are derived from the analog
    // prototype for the actual sample rate (BS.1770 specifies them at 48 kHz, but
    // WAV/FLAC/MP3 arrive at their native rate). The Opus path is always 48 kHz.
    //
    // Channel weighting: music here is mono or stereo, so every channel is weighted
    // 1.0 (the R128 surround weights only differ for ≥5 channels / LFE, which we
    // never decode). Extend BuildChannelWeights if true multichannel ever ships.
    // ─────────────────────────────────────────────────────────────────────────

    public struct LoudnessResult
    {
        // Integrated loudness in LUFS. Only meaningful when Valid is true.
        public double IntegratedLufs;
        // Highest absolute sample value seen (pre-filter), 0..1+. A crest-factor /
        // boost-cap heuristic — NOT a true-peak measurement (no oversampling).
        public double SamplePeak;
        // False when the track was too short / too quiet to yield a single gated
        // block (e.g. < 400 ms, or pure silence). Callers should treat that as
        // "no normalization" (gain 1.0) rather than trusting IntegratedLufs.
        public bool Valid;
    }

    public sealed class R128Analyzer
    {
        // ── A single biquad (direct form I) with its own delay state ───────────
        private struct Biquad
        {
            // Feed-forward (numerator) and feedback (denominator) coefficients,
            // already normalised so a0 == 1.
            public double B0, B1, B2, A1, A2;
            // Delay registers (per channel — one Biquad instance per channel/stage).
            public double X1, X2, Y1, Y2;

            public double Process(double x)
            {
                double y = B0 * x + B1 * X1 + B2 * X2 - A1 * Y1 - A2 * Y2;
                X2 = X1; X1 = x;
                Y2 = Y1; Y1 = y;
                return y;
            }
        }

        private readonly int _channels;
        private readonly int _samplesPerSubBlock;   // 100 ms worth of frames
        private readonly double[] _channelWeights;

        // Two cascaded K-weighting biquads per channel. [ch*2 + 0] = stage 1
        // (high-shelf), [ch*2 + 1] = stage 2 (high-pass).
        private readonly Biquad[] _filters;

        // Sum of squared K-weighted samples in the CURRENT 100 ms sub-block,
        // per channel, plus how many frames we've accumulated into it.
        private readonly double[] _subSumSq;
        private int _subFrames;

        // Sliding history of the last few completed sub-blocks' per-channel
        // squared sums. A 400 ms block = the most recent 4 sub-blocks; advancing
        // one sub-block at a time gives the 75% overlap for free. Implemented as a
        // pre-allocated ring buffer (not a Queue<double[]>) so closing a sub-block —
        // which happens 10×/second of audio — allocates nothing. Steady-state churn
        // here forced Mono's stop-the-world GC to run during the background sweep,
        // hitching the game's main thread.
        private const int SubBlocksPerBlock = 4;     // 4 × 100 ms = 400 ms
        private readonly double[][] _ring;           // [SubBlocksPerBlock][_channels]
        private int _ringPos;                        // next slot to overwrite
        private int _ringFilled;                     // slots written so far (caps at 4)

        // One weighted mean-square value (z) per completed 400 ms block, retained
        // for the two-stage gating done in Finish(). Linear, not dB.
        private readonly List<double> _blockEnergies = new List<double>(1024);

        private double _samplePeak;

        // BS.1770 block-loudness offset:  L = -0.691 + 10·log10(z)
        private const double AbsoluteGateLufs = -70.0;
        private const double RelativeGateLu = -10.0;
        private const double LoudnessOffset = -0.691;

        public R128Analyzer(int sampleRate, int channels)
        {
            if (sampleRate <= 0) sampleRate = 48000;
            if (channels <= 0) channels = 1;

            _channels = channels;
            _samplesPerSubBlock = Math.Max(1, (int)Math.Round(sampleRate * 0.1)); // 100 ms
            _channelWeights = BuildChannelWeights(channels);

            _filters = new Biquad[channels * 2];
            for (int ch = 0; ch < channels; ch++)
            {
                _filters[ch * 2 + 0] = MakeHighShelf(sampleRate);
                _filters[ch * 2 + 1] = MakeHighPass(sampleRate);
            }

            _subSumSq = new double[channels];

            _ring = new double[SubBlocksPerBlock][];
            for (int i = 0; i < SubBlocksPerBlock; i++)
                _ring[i] = new double[channels];
        }

        // ── Feed interleaved float PCM ─────────────────────────────────────────
        // `count` is the number of valid floats in `data` (frames × channels).
        // Partial trailing frames (count not divisible by channels) are ignored.
        public void Feed(float[] data, int count)
        {
            if (data == null || count <= 0) return;
            int frames = count / _channels;
            int idx = 0;

            for (int f = 0; f < frames; f++)
            {
                for (int ch = 0; ch < _channels; ch++)
                {
                    double x = data[idx++];

                    double a = x < 0 ? -x : x;
                    if (a > _samplePeak) _samplePeak = a;

                    // K-weight: high-shelf then high-pass, channel-local state.
                    double w = _filters[ch * 2 + 0].Process(x);
                    w = _filters[ch * 2 + 1].Process(w);

                    _subSumSq[ch] += w * w;
                }

                if (++_subFrames >= _samplesPerSubBlock)
                    CloseSubBlock();
            }
        }

        // ── Finalise ───────────────────────────────────────────────────────────
        public LoudnessResult Finish()
        {
            var result = new LoudnessResult { SamplePeak = _samplePeak, Valid = false };
            if (_blockEnergies.Count == 0) return result;

            // Stage 1 — absolute gate at −70 LUFS.
            double absThresholdZ = ZForLoudness(AbsoluteGateLufs);
            double sum = 0; int n = 0;
            for (int i = 0; i < _blockEnergies.Count; i++)
            {
                double z = _blockEnergies[i];
                if (z > absThresholdZ) { sum += z; n++; }
            }
            if (n == 0) return result;

            // Stage 2 — relative gate at (mean − 10 LU) of the absolute-gated set.
            double meanZ = sum / n;
            double relThresholdLufs = LoudnessOffset + 10.0 * Math.Log10(meanZ) + RelativeGateLu;
            double relThresholdZ = ZForLoudness(relThresholdLufs);

            sum = 0; n = 0;
            for (int i = 0; i < _blockEnergies.Count; i++)
            {
                double z = _blockEnergies[i];
                if (z > absThresholdZ && z > relThresholdZ) { sum += z; n++; }
            }
            if (n == 0) return result;

            result.IntegratedLufs = LoudnessOffset + 10.0 * Math.Log10(sum / n);
            result.Valid = true;
            return result;
        }

        // ── Sub-block / block bookkeeping ──────────────────────────────────────
        private void CloseSubBlock()
        {
            // Copy this sub-block's per-channel energy into the next ring slot
            // (overwriting the oldest once the ring is full), then reset the
            // accumulator. No allocation — the ring slots are pre-sized in the ctor.
            double[] slot = _ring[_ringPos];
            Array.Copy(_subSumSq, slot, _channels);
            _ringPos = (_ringPos + 1) % SubBlocksPerBlock;
            if (_ringFilled < SubBlocksPerBlock) _ringFilled++;

            Array.Clear(_subSumSq, 0, _channels);
            _subFrames = 0;

            if (_ringFilled < SubBlocksPerBlock) return;

            // Weighted mean square over the last 400 ms (all four ring slots; their
            // order doesn't matter to the sum):
            //   z = Σ_ch G_ch · (Σ_subblocks sumSq_ch) / framesInBlock
            int framesInBlock = SubBlocksPerBlock * _samplesPerSubBlock;
            double z = 0;
            for (int ch = 0; ch < _channels; ch++)
            {
                double perChannel = 0;
                for (int s = 0; s < SubBlocksPerBlock; s++)
                    perChannel += _ring[s][ch];
                z += _channelWeights[ch] * (perChannel / framesInBlock);
            }

            _blockEnergies.Add(z);
        }

        // Inverse of  L = -0.691 + 10·log10(z)  →  z = 10^((L + 0.691) / 10)
        private static double ZForLoudness(double lufs)
            => Math.Pow(10.0, (lufs - LoudnessOffset) / 10.0);

        private static double[] BuildChannelWeights(int channels)
        {
            // Mono/stereo: all 1.0. (R128 surround weights — e.g. 1.41 for the
            // rear channels, 0 for LFE — only matter for ≥5-channel content, which
            // this mod never decodes.)
            var w = new double[channels];
            for (int i = 0; i < channels; i++) w[i] = 1.0;
            return w;
        }

        // ── K-weighting biquad coefficients (sample-rate aware) ────────────────
        // Derived from the BS.1770 analog prototype via the bilinear transform,
        // matching the reference libebur128 derivation. Valid for any sample rate;
        // at 48 kHz they reduce to the canonical tabulated coefficients.

        // Stage 1: high-frequency "head/shoulder" shelving boost (~+4 dB).
        private static Biquad MakeHighShelf(int sampleRate)
        {
            const double f0 = 1681.974450955533;
            const double G = 3.999843853973347;
            const double Q = 0.7071752369554196;

            double K = Math.Tan(Math.PI * f0 / sampleRate);
            double Vh = Math.Pow(10.0, G / 20.0);
            double Vb = Math.Pow(Vh, 0.4996667741545416);
            double a0 = 1.0 + K / Q + K * K;

            return new Biquad
            {
                B0 = (Vh + Vb * K / Q + K * K) / a0,
                B1 = 2.0 * (K * K - Vh) / a0,
                B2 = (Vh - Vb * K / Q + K * K) / a0,
                A1 = 2.0 * (K * K - 1.0) / a0,
                A2 = (1.0 - K / Q + K * K) / a0,
            };
        }

        // Stage 2: ~38 Hz high-pass (the RLB low-frequency roll-off).
        private static Biquad MakeHighPass(int sampleRate)
        {
            const double f0 = 38.13547087602444;
            const double Q = 0.5003270373238773;

            double K = Math.Tan(Math.PI * f0 / sampleRate);
            double a0 = 1.0 + K / Q + K * K;

            return new Biquad
            {
                B0 = 1.0,
                B1 = -2.0,
                B2 = 1.0,
                A1 = 2.0 * (K * K - 1.0) / a0,
                A2 = (1.0 - K / Q + K * K) / a0,
            };
        }
    }
}
#endif
