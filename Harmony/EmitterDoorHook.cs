// Gated to 3.0 (GV_V3): TEFeatureDoor (composite tile-entity door) + the world-emitter layer are 3.0-only.
#if GV_V3
using System.Reflection;
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
    // CROSS-VERSION SHAPE: the shipped DLL is compiled once with GV_V3 and runs on A20/A21/2.6 too, where
    // TEFeatureDoor does not exist (composite tile entities are 1.0+). Nothing PatchAll scans — the class
    // attribute, the method signatures — may carry a TEFeatureDoor token, or attribute/parameter resolution
    // throws TypeLoadException there and aborts EVERY patch in the mod. So: bare [HarmonyPatch] + the target
    // resolved by NAME in TargetMethod, Prepare() skipping the patch cleanly off 3.0, and __instance typed as
    // object. The cast inside Postfix is safe because method BODIES only resolve their tokens at JIT time,
    // and Postfix is only ever invoked where the patch was applied (i.e. on 3.0, where the type exists).
    //
    // Read-only, exception-guarded: it never touches the door's state or return, and a throw here must never
    // break the game's door logic.
    [HarmonyPatch]
    internal static class Patch_DoorAcousticChange
    {
        // Both checks by name: RuntimeSupported covers the game-version gate, the Method probe covers a
        // future 3.x renaming SetOpen (a null TargetMethod would abort PatchAll; skipping is graceful).
        static bool Prepare() => EmitterCompat.RuntimeSupported
            && AccessTools.Method(AccessTools.TypeByName("TEFeatureDoor"), "SetOpen") != null;

        static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("TEFeatureDoor"), "SetOpen");

        static void Postfix(object __instance, bool _open)
        {
            try
            {
                if (!EmitterAudioLayer.HasActiveVoice) return;
                var door = (TEFeatureDoor)__instance;
                // Flag this door as mid-swing (with its direction) so the flood samples its Animator openness
                // (Tier 2) and the occlusion tracks the physical swing, then poke the emitter to refresh now.
                EmitterAcoustics.RegisterAnimatingDoor(door.blockValue.type, door.ToWorldPos(), _open);
                EmitterAudioLayer.NotifyAcousticChange();
            }
            catch { /* a door must never break because of us */ }
        }
    }
}
#endif
