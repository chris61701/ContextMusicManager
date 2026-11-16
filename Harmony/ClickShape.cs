using System;
using System.Globalization;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // Click SHAPES — the settings.xml line for a band-noise click (CMM_Decoder v63), parsed into
    // the float layout cmm_mix_set_click_shape takes:
    //   [0..6] band levels dB   [7..13] band ms to -20 dB   [14] swell ms   [15] edge
    //   [16] release delay ms   [17] release level dB       [18] release tilt dB/band
    //   [19] impact count       [20..27] impact times ms    [28..35] impact levels dB
    //
    // Text form, sections separated by '|', in any order:
    //   levels -37.3 -28.9 -8.1 -7.8 -4 0 -2.6 | decay 75 47 10 10 7.3 5.8 3.2 | swell 2 | edge 2
    //   | hits 0:0 2.8:-10 5.9:-16 | release 177 -0.2 -2.4
    // A section that is missing or malformed keeps its default rather than failing the whole
    // line: a typo in one number should cost that number, not the click.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class ClickShape
    {
        internal const int Length = 36;

        // Parsed once per distinct string, per kind: a click fires many times a second while the
        // dial spins, and parsing (or allocating) on each one is pointless when the text has not
        // changed. The setting is hot-reloaded, so the cache is keyed by the string itself.
        private static readonly string[] _src = new string[3];
        private static readonly float[][] _parsed = new float[3][];

        internal static float[] Cached(int kind, string text)
        {
            if (kind < 0 || kind > 2) return null;
            if (!ReferenceEquals(text, _src[kind]) && text != _src[kind])
            {
                _parsed[kind] = Parse(text);
                _src[kind] = text;
            }
            return _parsed[kind];
        }

        // null for an empty line: that kind uses the older resonator knobs instead.
        internal static float[] Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var p = new float[Length];
            p[15] = 2f;                       // edge
            p[19] = 1f; p[28] = 0f;           // one impact, at 0 ms, 0 dB
            foreach (string section in text.Split('|'))
            {
                string[] w = section.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (w.Length < 2) continue;
                switch (w[0].ToLowerInvariant())
                {
                    case "levels":  ReadInto(w, p, 0, 7);  break;
                    case "decay":   ReadInto(w, p, 7, 7);  break;
                    case "swell":   ReadInto(w, p, 14, 1); break;
                    case "edge":    ReadInto(w, p, 15, 1); break;
                    case "release": ReadInto(w, p, 16, 3); break;
                    case "hits":
                    {
                        int n = 0;
                        for (int i = 1; i < w.Length && n < 8; i++)
                        {
                            int c = w[i].IndexOf(':');
                            if (c <= 0) continue;
                            if (!TryF(w[i].Substring(0, c), out float t) || !TryF(w[i].Substring(c + 1), out float db)) continue;
                            p[20 + n] = t; p[28 + n] = db; n++;
                        }
                        if (n > 0) p[19] = n;
                        break;
                    }
                }
            }
            return p;
        }

        private static void ReadInto(string[] w, float[] p, int at, int count)
        {
            for (int i = 0; i < count && i + 1 < w.Length; i++)
                if (TryF(w[i + 1], out float v)) p[at + i] = v;
        }

        private static bool TryF(string s, out float v)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
