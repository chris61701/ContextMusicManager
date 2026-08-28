using System;
using System.Collections.Generic;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // RESUME POLICY — the two decisions behind "pick up where I left off", kept
    // deliberately free of UnityEngine so they can be TESTED.
    //
    // These rules used to live inline in ContextMusicManager.AudioPlayer.cs, which imports
    // UnityEngine and so cannot be linked into a test tool. That mattered: the personal radio
    // silently violated both of them for as long as the radio has existed, and nothing could
    // have caught it except playing the game and noticing. tools/CmmResumeTest now links THIS
    // FILE directly (the same trick CmmConfigTest uses on CMMSettingsSchema.cs) and drives the
    // real functions, so the edge cases are checked rather than argued about.
    //
    // Everything here is a pure function of its arguments — no clock, no filesystem, no static
    // state. The caller supplies "now" and a file-usable predicate.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class ResumePolicy
    {
        // What StartNativeContext should do about a saved position.
        internal struct Decision
        {
            public string ResumeFile;      // non-null ⇒ replay THIS exact file...
            public float  StartSeconds;    // ...from here
            public bool   ClearPosition;   // ⇒ caller zeroes the saved position (it is spent/stale)
        }

        // A saved resume position belongs to the SPECIFIC track that was playing — it is
        // meaningless for any other track. So a resume must replay that exact file; otherwise a
        // multi-track context's staged/random pick inherits the old track's position and a random
        // track starts mid-way (a real reported bug). Single-track contexts only ever had one file
        // to land on, which is why they looked fine and hid it.
        internal static Decision Decide(
            bool canBeResumed, float resumePosition, string lastPlayedFile, float lastLeftTime,
            float now, float staleWindow, bool switchingToNextTrack, bool forced,
            Func<string, bool> fileUsable)
        {
            var d = default(Decision);
            if (switchingToNextTrack || forced) return d;          // a deliberate move to another track
            if (!canBeResumed || resumePosition <= 0f) return d;
            if (string.IsNullOrEmpty(lastPlayedFile)) return d;
            if (fileUsable != null && !fileUsable(lastPlayedFile)) { d.ClearPosition = true; return d; }

            // lastLeftTime < 0 means "never left / still active" — treat as not stale.
            float elapsed = lastLeftTime < 0f ? 0f : now - lastLeftTime;
            if (elapsed >= staleWindow) { d.ClearPosition = true; return d; }

            d.ResumeFile   = lastPlayedFile;
            d.StartSeconds = resumePosition;
            return d;
        }

        // Should this context's staleness clock be HELD across an interval in which the music
        // layer was owned by something else (the personal radio)?
        //
        // TimeUntilContextStale asks "how long has this context been GONE". Through a broadcast the
        // answer, for a context still on the stack, is "it hasn't" — you are standing in the same
        // POI doing the same thing and the radio only borrowed the speaker.
        //
        // Both edges of the scope are load-bearing, and each was wrong in an earlier attempt:
        //   • NOT only the context playing at dial-off. Turn the radio on in a POI, have a zombie
        //     turn up mid-broadcast, dial off while combat is still live — the POI is still one you
        //     are standing in even though combat outranks it, so its clock should not have run.
        //   • NOT every cached context either. One you genuinely walked out of ten minutes ago is
        //     off the stack and really did go stale. The radio does not undo having left.
        internal static bool ShouldHold(bool onStack, float lastLeftTime, float resumePosition)
            => onStack && lastLeftTime > 0f && resumePosition > 0f;

        // Apply the hold to a set of contexts. `onStack` names the contexts currently held on the
        // stack; `clocks` maps context name → its lastLeftTime. Returns the names actually shifted.
        internal static List<string> Hold(
            float heldSeconds, ICollection<string> onStack, IDictionary<string, float> clocks,
            IDictionary<string, float> positions)
        {
            var moved = new List<string>();
            if (heldSeconds <= 0f || onStack == null || clocks == null) return moved;
            foreach (string name in onStack)
            {
                if (name == null) continue;
                if (!clocks.TryGetValue(name, out float left)) continue;
                float pos = 0f;
                positions?.TryGetValue(name, out pos);
                if (!ShouldHold(true, left, pos)) continue;
                clocks[name] = left + heldSeconds;
                moved.Add(name);
            }
            return moved;
        }
    }
}
