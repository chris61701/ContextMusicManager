
using System;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // R128 ANALYZER SELF-TEST
    // ─────────────────────────────────────────────────────────────────────────
    // Synthesizes EBU Tech 3341 style reference tones and checks R128Analyzer
    // against their known integrated-loudness values. No files, no game state —
    // pure math, so it can run at mod load to confirm the analyzer before any
    // real track depends on it.
    //
    // Reference values:
    //   • A 1 kHz sine at −23 dBFS in STEREO reads −23.0 LUFS. The K-weighting
    //     filter's ~+0.69 dB lift near 1 kHz is designed to cancel the −0.691
    //     block offset, so a 1 kHz tone reads back its own dBFS level; the two
    //     summed channels land it exactly at −23.
    //   • The same tone in MONO reads −26.0 LUFS: R128 SUMS channels (it does not
    //     average), so dropping from two channels to one costs 10·log10(2) ≈ 3 dB.
    //   • A full-scale (0 dBFS) stereo tone reads 0.0 LUFS.
    //   • Pure silence yields no gated block → Valid == false.
    //
    // Invoke ContextMusicManager.Init with CMM_LOUDNESS_SELFTEST defined to run it.
    // ─────────────────────────────────────────────────────────────────────────
    public static class R128SelfTest
    {
        private const int Fs = 48000;
        private const double Tolerance = 0.2;          // LUFS
        private const double TestSeconds = 4.0;        // enough for many gated blocks

        public static bool Run()
        {
            Log.Out("[CMM] ── R128 self-test ──────────────────────────────");
            bool ok = true;

            ok &= CheckTone("stereo 1kHz @ -23dBFS", channels: 2, dbfs: -23.0, expectLufs: -23.0);
            ok &= CheckTone("mono   1kHz @ -23dBFS", channels: 1, dbfs: -23.0, expectLufs: -26.0);
            ok &= CheckTone("stereo 1kHz @   0dBFS", channels: 2, dbfs:   0.0, expectLufs:   0.0);
            ok &= CheckSilence();
            ok &= CheckPeak();

            Log.Out($"[CMM] R128 self-test: {(ok ? "PASS" : "FAIL")}");
            Log.Out("[CMM] ────────────────────────────────────────────────");
            return ok;
        }

        // Feeds a 1 kHz sine of the given amplitude/channel count and compares the
        // integrated loudness against the expected LUFS within tolerance.
        private static bool CheckTone(string label, int channels, double dbfs, double expectLufs)
        {
            var analyzer = new R128Analyzer(Fs, channels);
            FeedSine(analyzer, channels, dbfs, TestSeconds);
            LoudnessResult r = analyzer.Finish();

            if (!r.Valid)
            {
                Log.Error($"[CMM]   {label}: FAIL (Valid=false, expected {expectLufs:F1} LUFS)");
                return false;
            }

            double err = Math.Abs(r.IntegratedLufs - expectLufs);
            bool pass = err <= Tolerance;
            string line = $"[CMM]   {label}: {r.IntegratedLufs,7:F2} LUFS " +
                          $"(expect {expectLufs:F1}, err {err:F3}) {(pass ? "ok" : "FAIL")}";
            if (pass) Log.Out(line); else Log.Error(line);
            return pass;
        }

        private static bool CheckSilence()
        {
            var analyzer = new R128Analyzer(Fs, 2);
            var buf = new float[Fs * 2]; // 1 s of stereo zeros
            analyzer.Feed(buf, buf.Length);
            LoudnessResult r = analyzer.Finish();

            bool pass = !r.Valid;
            string line = $"[CMM]   silence: Valid={r.Valid} (expect false) {(pass ? "ok" : "FAIL")}";
            if (pass) Log.Out(line); else Log.Error(line);
            return pass;
        }

        private static bool CheckPeak()
        {
            // A −23 dBFS sine peaks at 10^(-23/20) ≈ 0.0708.
            const double dbfs = -23.0;
            double expected = Math.Pow(10.0, dbfs / 20.0);

            var analyzer = new R128Analyzer(Fs, 2);
            FeedSine(analyzer, 2, dbfs, 1.0);
            LoudnessResult r = analyzer.Finish();

            bool pass = Math.Abs(r.SamplePeak - expected) <= 0.001;
            string line = $"[CMM]   peak: {r.SamplePeak:F4} (expect {expected:F4}) {(pass ? "ok" : "FAIL")}";
            if (pass) Log.Out(line); else Log.Error(line);
            return pass;
        }

        // Generates a continuous-phase 1 kHz sine and feeds it in varied,
        // channel-aligned chunks so filter state must carry across Feed() calls.
        private static void FeedSine(R128Analyzer analyzer, int channels, double dbfs, double seconds)
        {
            double amp = Math.Pow(10.0, dbfs / 20.0);
            double w = 2.0 * Math.PI * 1000.0 / Fs;
            int totalFrames = (int)(Fs * seconds);

            int chunkFrames = 4096;
            var chunk = new float[chunkFrames * channels];

            int n = 0;
            while (n < totalFrames)
            {
                int framesThisChunk = Math.Min(chunkFrames, totalFrames - n);
                int idx = 0;
                for (int f = 0; f < framesThisChunk; f++)
                {
                    float s = (float)(amp * Math.Sin(w * (n + f)));
                    for (int c = 0; c < channels; c++) chunk[idx++] = s;
                }
                analyzer.Feed(chunk, framesThisChunk * channels);
                n += framesThisChunk;
            }
        }
    }
}
