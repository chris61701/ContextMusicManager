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
                // THE FIELD IS TOLD BY THE DRAIN, NOT FROM HERE. This postfix can run off the main
                // thread -- the line above says so, and it is why RegisterAnimatingDoor only
                // enqueues -- but cmm_fld_dirty mutates the arena's dirty flags, pending count,
                // priority ring and cursor with NO lock, concurrently with cmm_fld_next_pending
                // and cmm_fld_set_cell on the main thread. Ring corruption there looks exactly
                // like "the door sometimes doesn't land", which is a report we have.
                //
                // Nothing is lost by deferring: the only consumer of a dirty flag is the world
                // READER, which is main-thread and cannot run between this postfix and the drain.
                // Marking early buys no earlier read.
            }
            catch { /* a door must never break because of us */ }
        }
    }

    // -- The same job on 2.6, through the door model 2.6 actually has ----------------------------
    //
    // 2.6 has no TEFeatureDoor, so Patch_DoorAcousticChange above never applies there and every
    // door reported its swing as an instant flip -- "the timing is completely off". The first read
    // of that was that 2.6 exposes no swing at all and the honest answer was a binary aperture.
    // That was wrong. 2.6 animates its doors through the SAME rig: AnimatorDoorState is present
    // verbatim in both games with the same Open/Close/IsOpen hashes, and BlockDoor.updateAnimState
    // drives it off the BlockEntityData exactly as 3.x does. Every member EmitterAcoustics'
    // AdvanceDoor touches to sample that -- WorldBase.ChunkCache (a public ChunkCluster field on
    // both), ChunkCluster.GetBlockEntity(Vector3i), BlockEntityData.transform/bHasTransform --
    // exists unchanged on 2.6. Nothing in the Tier 2 chain names TEFeatureDoor.
    //
    // So the sampling was never the missing piece. The CHOKEPOINT was: something had to say "this
    // door just started moving, in this direction". On 2.6 that is OnBlockValueChanged, which is
    // where the game itself kicks the animator and which hands us both block values, so the meta
    // open bit (meta & 1) gives the edge AND the direction in one place.
    //
    // BETTER than the 3.x hook in one respect: Chunk.SetBlock calls this on the general set path,
    // so it fires for REPLICATED changes too. The MP caveat on Patch_DoorAcousticChange -- a remote
    // player's door bypasses SetOpen and falls back to binary at-rest -- does not apply here. On
    // 2.6 a door somebody else opens is swing-tracked like your own.
    //
    // COVERAGE: BlockDoorSecure overrides this but calls base, so the one patch covers secure doors
    // (which is most POI doors). BlockPoweredDoor derives from BlockPowered and BlockTrapDoor from
    // Block; neither routes through here and neither is handled yet.
    //
    // CROSS-VERSION SHAPE -- the exact mirror of the constraint above it, and the one that matters:
    // BlockDoor.cs is ABSENT from the 3.2 decompile just as TEFeatureDoor.cs is absent from 2.6,
    // and the shipped DLL is compiled ONCE against 3.2 and copied to every install (see the
    // DebugFanout comment in the .csproj). So this code must live in a binary that COMPILES where
    // BlockDoor does not exist. Three places a token would leak, all avoided:
    //   1. a typed class attribute      -> bare [HarmonyPatch] + TargetMethod() resolving by name
    //   2. a typed __instance           -> not taken at all
    //   3. the parameter types          -> these are FINE. WorldBase, Chunk, Vector3i and BlockValue
    //      all exist on 3.2; only the DECLARING type is 2.6-only. So the parameters stay typed and
    //      Harmony binds them by name, which the TEFeatureDoor hook could not do.
    // Prepare() is false on 3.x, so the patch is never applied and Postfix never JITs -- nothing in
    // this body is ever asked to resolve there. Verify with tools/CmmApiDiff before shipping: it
    // walks the DLL's own member references, so "did a BlockDoor token get emitted" stops being a
    // matter of my opinion.
    [HarmonyPatch]
    internal static class Patch_DoorAcousticChange26
    {
        // DeclaredMethod, NOT Method: Block declares OnBlockValueChanged too, and a name-only
        // lookup that fell through to the BASE would patch EVERY block change in the world --
        // every block placed, damaged or destroyed, on the frame thread, registering non-doors as
        // animating doors. Restricting the lookup to BlockDoor's own override means the patch
        // either lands on the door method or does not land at all.
        static bool Prepare()
            => EmitterCompat.RuntimeSupported
            && !EmitterCompat.HasCompositeDoors            // 3.x is served by the hook above
            && AccessTools.DeclaredMethod(AccessTools.TypeByName("BlockDoor"), "OnBlockValueChanged") != null;

        static MethodBase TargetMethod()
            => AccessTools.DeclaredMethod(AccessTools.TypeByName("BlockDoor"), "OnBlockValueChanged");

        // The open bit. BlockDoor.IsDoorOpen(byte) is exactly this and nothing more, but calling it
        // would put a BlockDoor token in the call site for no gain.
        private static bool IsOpen(byte meta) => (meta & 1) != 0;

        static void Postfix(Vector3i _blockPos, BlockValue _oldBlockValue, BlockValue _newBlockValue)
        {
            try
            {
                if (!EmitterAudioLayer.HasActiveVoice) return;
                // Doors are multiblock (1,2,1). The game routes child activations to the parent
                // before it gets here, so a non-child position IS the parent position, which is
                // what RegisterAnimatingDoor wants.
                if (_newBlockValue.ischild) return;

                bool open = IsOpen(_newBlockValue.meta);
                if (open == IsOpen(_oldBlockValue.meta)) return;   // meta changed, but not the door

                CMMConfig.D($"[CMM] Door {(open ? "opening" : "closing")} at {_blockPos} (2.6 path)");
                EmitterAcoustics.RegisterAnimatingDoor(_newBlockValue.type, _blockPos, open);
                EmitterAudioLayer.NotifyAcousticChange();
                // As above: the field is told by the DRAIN, not from here. This can run off the
                // main thread and cmm_fld_dirty is unlocked.
            }
            catch { /* a door must never break because of us */ }
        }
    }
}
#endif
