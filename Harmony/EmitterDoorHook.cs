// Gated to 3.0 (GV_V3): TEFeatureDoor (composite tile-entity door) + the world-emitter layer are 3.0-only.
#if GV_V3
using HarmonyLib;

namespace CMM
{
    // Make the radio's occlusion react the moment a door opens/closes instead of ~1 s late.
    //
    // WHY: a door's logical open/closed state (TEFeatureDoor.isOpen) flips INSTANTLY inside SetOpen — before
    // the swing animation plays — and our occlusion flood reads that state via Block.IsSeeThrough. But the
    // flood only re-runs on the emitter's 4 Hz decision tick, so we noticed the change up to 250 ms late, and
    // with the ~1 s native gain glide stacked on top the audible change landed around when the swing FINISHED
    // ("it happens at the end of the animation"). This postfix pokes the emitter to re-flood on the next frame,
    // so the ~1 s glide starts WITH the swing and the sound opens/closes along with the door.
    //
    // It also registers the door as MID-SWING (EmitterAcoustics.RegisterAnimatingDoor) so the flood samples
    // its Animator openness and the occlusion follows the physical swing (Tier 2) — without this, closing a
    // multi-second garage door would obstruct audio the instant isOpen flips, before it's finished shutting.
    //
    // SetOpen is the single chokepoint every door toggle passes through — player activation, wire/trigger, and
    // the auto-close timer all route here — so one hook covers them all. NOTE (multiplayer): a REMOTE player's
    // door reaches a client via TEFeatureDoor.Read(), which bypasses SetOpen, so remote door toggles on a
    // client aren't registered and fall back to the binary at-rest path (still correct, just not swing-tracked).
    // Doors YOU operate run SetOpen locally and get the full treatment.
    //
    // Read-only, exception-guarded: it never touches the door's state or return, and a throw here must never
    // break the game's door logic.
    [HarmonyPatch(typeof(TEFeatureDoor), nameof(TEFeatureDoor.SetOpen))]
    internal static class Patch_DoorAcousticChange
    {
        static void Postfix(TEFeatureDoor __instance, bool _open)
        {
            try
            {
                if (!EmitterAudioLayer.HasActiveVoice) return;
                // Flag this door as mid-swing (with its direction) so the flood samples its Animator openness
                // (Tier 2) and the occlusion tracks the physical swing, then poke the emitter to refresh now.
                EmitterAcoustics.RegisterAnimatingDoor(__instance.blockValue.type, __instance.ToWorldPos(), _open);
                EmitterAudioLayer.NotifyAcousticChange();
            }
            catch { /* a door must never break because of us */ }
        }
    }
}
#endif
