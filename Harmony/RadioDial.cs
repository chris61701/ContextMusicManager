using System;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // THE DIAL, AND WHAT COMES AFTER A TRACK ENDS
    // ─────────────────────────────────────────────────────────────────────────
    // Two rules from the personal radio that are pure functions of their inputs, pulled
    // out for the reason ResumePolicy and EmitterSlots were: PersonalRadio needs Unity —
    // a player entity, a mixer, the frame clock — so nothing about it could be tested
    // except by playing the game, and both of these are fiddlier than they look.
    //
    // THE DIAL is not "next in a list". A station you are tuned to can go off air while
    // you stay tuned to it: the name survives, the station does not, and the dial still
    // has to move sensibly from a position that is no longer in the list. It also wraps
    // through OFF rather than round the ends, so there are n+1 positions over n stations.
    //
    // WHAT COMES AFTER A TRACK ENDS is the pause-drift guard. The schedule is a paper
    // clock: it says what SHOULD be on air, which after a track ends early — the decoder
    // reached EOF before the paper said so, because the world clock froze during a pause
    // while the audio kept running — is still the track that just finished. Starting it
    // again would loop it for as long as the drift lasts.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class RadioDial
    {
        internal const int Off = -1;

        // Where one dial press lands. `names` is the station list IN CATALOG ORDER (which is
        // sorted by name); `tuned` is the name currently tuned, which may no longer be in the
        // list; `on` is whether the radio is on at all. Returns an index into `names`, or Off.
        //
        // From OFF, forward lands on the first station and back on the last, so a single press
        // in either direction always gets you playing something.
        internal static int Step(string[] names, string tuned, bool on, int dir)
        {
            if (names == null || names.Length == 0) return Off;
            int n = names.Length;
            if (!on) return dir > 0 ? 0 : n - 1;

            // Where are we? Either the tuned name is in the list, or it is not and we take its
            // INSERTION POINT — the first station sorting after it. From there, forward lands
            // on that station and back on the one before, which is what "carry on from where
            // the missing station would have been" means. `exact` compensates for the step over
            // that phantom slot: a real station has to be stepped off, an absent one does not.
            int idx = 0; bool exact = false;
            for (int i = 0; i < n; i++)
            {
                int cmp = string.Compare(names[i], tuned, StringComparison.OrdinalIgnoreCase);
                if (cmp == 0) { idx = i; exact = true; break; }
                if (cmp < 0) idx = i + 1;
            }

            int target = dir > 0 ? (exact ? idx + 1 : idx) : idx - 1;
            if (target >= n || target < Off) target = Off;   // off the end either way ⇒ wrap through Off
            return target;
        }

        // The track just ended. `file`/`idx`/`offset` are what the schedule says is on air NOW;
        // `ended` is what actually finished. If they are the same track, the paper clock has not
        // caught up — take the next one from its start rather than replaying what just ended.
        //
        // Returns true when it had to override, which is only interesting for logging.
        internal static bool SkipEndedTrack(string[] order, string ended,
                                            ref string file, ref int idx, ref double offset)
        {
            if (order == null || order.Length == 0 || ended == null || file == null) return false;
            if (!string.Equals(file, ended, StringComparison.OrdinalIgnoreCase)) return false;

            idx = (idx + 1) % order.Length;
            file = order[idx];
            offset = 0;      // a track reached this way starts at its beginning, not mid-song
            return true;
        }
    }
}
