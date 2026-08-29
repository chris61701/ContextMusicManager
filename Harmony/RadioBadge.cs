using System;
using System.Text;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // A STATION NAME, IN FOUR CHARACTERS.
    //
    // The radial's segment label is a 48x48 box (the vanilla radial_entry template) which holds
    // about four characters. Ten segments all wearing the same icon told you nothing, so the
    // abbreviation IS the label — which makes how it is derived a real question rather than a
    // detail. These rules were read off a worked list of seventeen real station names; every one
    // of them is a case in CmmScheduleTest, and the list is the specification.
    //
    //   1. Pure-number words are dropped.        CTOP 50            -> CTOP
    //   2. One word: its first four letters.     CTOP               -> CTOP
    //   3. Two words, SHORT last word (<=3):     Cathedral FM       -> CaFM
    //      keep the last word whole, the first   Lit FM             -> LiFM
    //      word fills whatever is left.          Wasteland Pop      -> WPop
    //                                            KPOP Top           -> KTop
    //   4. Two words, LONG last word:            Concrete Jungle    -> CJ
    //      plain initials.                       Enclave Radio      -> ER
    //   5. Three or more words: initials of      Rust Belt Rock     -> RBR
    //      the first four.                       Light Scripe Shadow FM -> LSSF
    //
    // FM / AM / XM are named explicitly as band suffixes: they behave like any other short last
    // word, but they are ALWAYS uppercased and always qualify however short the budget gets, so a
    // band never comes out half-cased or clipped. Note that rule 5 outranks this — with four words
    // there is no room for a band to survive whole, which is why LSSF ends in a bare F.
    //
    // There WAS a sixth rule here, capping a word's contribution at all-but-two of its letters,
    // invented to explain why the worked list had Cathedral FM as CaFM but Lit FM as LFM. It was
    // explaining a typo: Lit FM is LiFM. The cap is gone and rule 3 is uniform again — which is
    // the outcome to expect when a rule exists only to justify one entry in a list.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    internal static class RadioBadge
    {
        internal const int MaxChars = 4;

        // Band suffixes get whole-word treatment however tight the budget, and are uppercased.
        private static readonly string[] Bands = { "FM", "AM", "XM" };

        // A last word this short or shorter survives whole, and the first word takes the rest.
        private const int ShortWord = 3;

        internal static string For(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string[] words = Split(name);
            if (words.Length == 0) return "";

            if (words.Length == 1) return Cap(words[0], MaxChars);

            if (words.Length == 2)
            {
                string last = words[1];
                bool band = IsBand(last);
                if (band || last.Length <= ShortWord)
                {
                    string tail = band ? last.ToUpperInvariant() : last;
                    // The first word spends whatever the tail leaves. At least one letter, or
                    // there is no initial at all.
                    int take = MaxChars - tail.Length;
                    if (take < 1) take = 1;
                    return Cap(words[0], take) + tail;
                }
                // A long second word cannot be kept, so neither word is: plain initials.
                return Initials(words, 2);
            }

            return Initials(words, MaxChars);
        }

        // First letters of up to `max` words, each uppercased.
        private static string Initials(string[] words, int max)
        {
            var sb = new StringBuilder(max);
            for (int i = 0; i < words.Length && sb.Length < max; i++)
                if (words[i].Length > 0) sb.Append(char.ToUpperInvariant(words[i][0]));
            return sb.ToString();
        }

        // The first `n` characters, with the first uppercased and the rest left as the author
        // wrote them: "Cathedral" -> "Ca", and an acronym like "CTOP" stays "CTOP" rather than
        // being flattened to "Ctop".
        private static string Cap(string word, int n)
        {
            if (word.Length == 0) return "";
            if (n > word.Length) n = word.Length;
            if (n <= 0) n = 1;
            var sb = new StringBuilder(n);
            sb.Append(char.ToUpperInvariant(word[0]));
            for (int i = 1; i < n; i++) sb.Append(word[i]);
            return sb.ToString();
        }

        private static bool IsBand(string w)
        {
            for (int i = 0; i < Bands.Length; i++)
                if (string.Equals(w, Bands[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Words, with pure-number tokens dropped -- "CTOP 50" is one word for our purposes, and a
        // channel number is never what tells two stations apart at a glance. Separators are space,
        // underscore and hyphen, because a station is a FOLDER NAME and those all read as spaces.
        private static string[] Split(string name)
        {
            string[] raw = name.Split(new[] { ' ', '_', '-', '\t' },
                                      StringSplitOptions.RemoveEmptyEntries);
            int keep = 0;
            for (int i = 0; i < raw.Length; i++) if (!IsNumber(raw[i])) keep++;
            // Every word was a number ("50"): fall back to the raw words rather than returning
            // nothing, so such a station still gets a label.
            if (keep == 0) return raw;
            var outp = new string[keep];
            int k = 0;
            for (int i = 0; i < raw.Length; i++) if (!IsNumber(raw[i])) outp[k++] = raw[i];
            return outp;
        }

        private static bool IsNumber(string w)
        {
            if (w.Length == 0) return false;
            for (int i = 0; i < w.Length; i++) if (!char.IsDigit(w[i])) return false;
            return true;
        }
    }
}
