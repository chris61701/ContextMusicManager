using System;
using System.IO;

namespace CMM
{
    // ── THE LIBRARY ──────────────────────────────────────────────────────────────────────────
    //
    // Written by the harness, at KNOWN levels, so a level assertion is arithmetic rather than an
    // opinion about a track. Loudness normalisation is switched off for the same reason: it has
    // its own harness, and leaving it on would make every level here depend on a measurement.
    //
    // "Broken FM" is a real station folder holding a file that will not decode. That is not an
    // edge case: it is step 8 of the spec, and the off-air carrier exists for it.
    internal static class Library
    {
        internal const float TrackDbfs = -20f;

        internal static string Build(string root)
        {
            string radio = Path.Combine(root, "Radio");
            if (Directory.Exists(radio)) Directory.Delete(radio, true);
            Directory.CreateDirectory(radio);

            // Different pitches per station so a capture can be read by eye in a spectrogram, and
            // different track counts so the schedules do not march in step.
            Station(radio, "Alpha FM",   new[] { 220f, 277f, 330f });
            Station(radio, "Beta FM",    new[] { 440f, 554f });
            Station(radio, "Gamma FM",   new[] { 660f, 831f, 988f, 1174f });
            Broken (radio, "Broken FM");
            return radio;
        }

        private static void Station(string radio, string name, float[] hz)
        {
            string dir = Path.Combine(radio, name);
            Directory.CreateDirectory(dir);
            for (int i = 0; i < hz.Length; i++)
                Tone(Path.Combine(dir, $"{i + 1:00} {hz[i]:0}Hz.wav"), hz[i], 45f);
        }

        // A folder with a file in it that is not audio. Ready() is true for this station — that
        // is the point, and it is why "the station will not open" has to be detected by the open
        // FAILING rather than by asking whether the station looks playable.
        private static void Broken(string radio, string name)
        {
            string dir = Path.Combine(radio, name);
            Directory.CreateDirectory(dir);
            var junk = new byte[8192];
            new Random(7).NextBytes(junk);
            // A valid RIFF header over nonsense, so the extension check passes and the decode does not.
            using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "01 dead air.wav"))))
            { w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(junk.Length); w.Write(junk); }
        }

        private static void Tone(string path, float hz, float seconds)
        {
            int rate = 48000, ch = 2;
            int frames = (int)(rate * seconds);
            float amp = (float)Math.Pow(10.0, TrackDbfs / 20.0) * 1.41421356f;   // RMS -> peak for a sine
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int data = frames * ch * 2;
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
                w.Write((short)1); w.Write((short)ch); w.Write(rate);
                w.Write(rate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(data);
                double step = 2.0 * Math.PI * hz / rate;
                for (int i = 0; i < frames; i++)
                {
                    short v = (short)(Math.Sin(step * i) * amp * 32767.0);
                    w.Write(v); w.Write(v);
                }
            }
        }
    }
}
