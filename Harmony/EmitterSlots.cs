using System;
using System.Collections.Generic;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // EMITTER SLOT OWNERSHIP
    // ─────────────────────────────────────────────────────────────────────────
    // There are CmmMixer.MaxEmitterSlots native render slots and any number of stations, so
    // which station owns which slot is a small piece of bookkeeping with an outsized failure
    // mode: get it wrong and a slot is held by a station that no longer exists, FreeSlot
    // returns -1 for ever, and no speaker in the world plays again for the rest of the
    // session -- silently, because a station that cannot get a slot simply returns.
    //
    // That happened. The release path skipped any station with no live voice, on the
    // reasoning that there was nothing to release; but a station holds a slot from the
    // moment it is chosen, and starting the voice can fail or be deferred (the window walk
    // has not landed, the open is still running off-thread). Three of those and the emitter
    // layer is dead.
    //
    // The rule lives HERE, in a file with no UnityEngine and no game types, for the reason
    // ResumePolicy does: the code it came from cannot be linked into a test, and a timing
    // test for an interleaving would pass by luck. This is a plain function over the state,
    // so tools/CmmScheduleTest can drive every case directly, including the ones that need
    // a specific order of events to reach in a running game.
    // ─────────────────────────────────────────────────────────────────────────
    internal static class EmitterSlots
    {
        // Release what a catalog rebuild leaves behind, and reconcile the slot table.
        //
        //   old        name → station, as the catalog was BEFORE the rebuild
        //   live       the stations that exist now (migrated ones already carry their slot)
        //   slotOwner  the slot table, mutated in place
        //   stopVoice  called for each voice that must be retired; the id is never 0
        //
        // Returns how many stale owners the backstop had to reclaim -- non-zero means
        // something released a station without releasing its slot, which is a bug worth
        // seeing in the log rather than absorbing.
        internal static int ReleaseAfterRebuild(
            Dictionary<string, RadioStation> old,
            List<RadioStation> live,
            RadioStation[] slotOwner,
            Action<long> stopVoice)
        {
            if (slotOwner == null) return 0;

            if (old != null)
            {
                foreach (var kv in old)
                {
                    RadioStation prev = kv.Value;
                    if (prev == null) continue;

                    // The SLOT goes back whether or not a voice was ever started on it. This is
                    // the line the leak was hiding behind: it used to sit after a
                    // "no voice, nothing to do" early-out.
                    if (prev.Slot >= 0 && prev.Slot < slotOwner.Length && slotOwner[prev.Slot] == prev)
                    {
                        slotOwner[prev.Slot] = null;
                        prev.Slot = -1;
                    }
                    if (prev.VoiceId == 0) continue;
                    stopVoice?.Invoke(prev.VoiceId);
                }
            }

            // Backstop: a slot owned by anything that is not a live station. The loop above
            // covers what the rebuild knows about; this covers every other way an owner can go
            // stale. Three comparisons, against a failure that is total and silent.
            int reclaimed = 0;
            for (int s = 0; s < slotOwner.Length; s++)
            {
                RadioStation owner = slotOwner[s];
                if (owner == null) continue;
                if (live != null && live.Contains(owner)) continue;
                slotOwner[s] = null;
                reclaimed++;
            }
            return reclaimed;
        }

        // The first slot nobody owns, or -1. `pendingSlot` is a slot an asynchronous start has
        // chosen but cannot own yet -- ownership is only real once the voice exists, and handing
        // the same slot to a second station would have both rendering into it.
        // borrowSlot: a slot held by no station but not free either — a single speaker is being
        // swept on it (see EmitterAudioLayer's sweep borrow). It has no owner by design, so
        // without naming it here the next station to start would be handed the slot out from
        // under the hiss.
        internal static int FreeSlot(RadioStation[] slotOwner, bool startPending, int pendingSlot,
                                     int borrowSlot = -1)
        {
            if (slotOwner == null) return -1;
            for (int s = 0; s < slotOwner.Length; s++)
            {
                if (slotOwner[s] != null) continue;
                if (startPending && s == pendingSlot) continue;
                if (s == borrowSlot) continue;
                return s;
            }
            return -1;
        }
    }
}
