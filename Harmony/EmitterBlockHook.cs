#if GV_V3
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // BLOCK CHANGES, AT THE MOMENT THEY HAPPEN.
    //
    // WHY: nothing told the field a block had been placed or destroyed. The only patched methods
    // in the whole mod were the player lifecycle, the music conductor and TEFeatureDoor.SetOpen —
    // so a wall going up or coming down was picked up ONLY by the near-ring refresh, which runs
    // every NearRefreshSec = 0.4 s. That is the reported "adding or destroying blocks, occlusion
    // takes maybe 300 ms — I can't measure it exactly, I just know it's obvious": up to 400 ms,
    // averaging 200, and it is a poll rather than a response.
    //
    // A door already gets this treatment and lands in ~50 ms of read. A block is the same event
    // with a simpler answer, and it was the one the model could not see coming.
    //
    // GameManager.SetBlocksRPC is the choke point every change funnels through — player placement,
    // destruction, explosions, and the networked path on a client — on A20, A21, 2.6 and 3.1
    // alike (verified in all four decompiles).
    //
    // THE POSITION MOVED between versions, which is divergence number eleven. A20/A21/2.6 carry a
    // plain `public Vector3i pos` on BlockChangeInfo; 3.1 replaced it with `blockValueRef`, whose
    // BlockPosition holds it. Resolved once by reflection into a delegate rather than #if'd,
    // because one DLL ships to all four.
    //
    // THREADING: the postfix enqueues and nothing more. cmm_fld_dirty mutates the arena's dirty
    // flags, pending count and priority ring with no lock — the same reason the door hook stopped
    // calling it directly — so the drain happens on the main thread inside the field's own reader.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    [HarmonyPatch]
    public static class EmitterBlockHook
    {
        // Radius in blocks. A block is one cell; the neighbours matter because the half-voxel
        // split reads them, and cmm_fld_dirty grows the box by a step on each side anyway.
        private const int ChangeRadius = 1;
        // A cap, because an explosion or a falling structure can hand us hundreds at once and the
        // reader has a per-frame budget. Past this the near ring picks up the remainder, which is
        // the behaviour everything had before this hook existed.
        private const int MaxQueued = 512;

        private static readonly List<Vector3i> _pending = new List<Vector3i>();
        private static readonly object _lock = new object();
        private static volatile int _pendingCount;

        internal static bool HasPending => _pendingCount > 0;

        // Drained on the MAIN thread by the field reader. Returns the positions to dirty.
        internal static void Drain(List<Vector3i> into)
        {
            if (_pendingCount == 0 || into == null) return;
            lock (_lock)
            {
                into.AddRange(_pending);
                _pending.Clear();
                _pendingCount = 0;
            }
        }

        static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("GameManager"), "SetBlocksRPC");

        // A missing target must SKIP the patch, not abort PatchAll and take the mod with it.
        static bool Prepare() => TargetMethod() != null && PosOf != null;

        // ── The version-divergent position accessor, resolved once ──────────────────────────
        private static readonly Func<object, Vector3i> PosOf = BuildPosAccessor();

        private static Func<object, Vector3i> BuildPosAccessor()
        {
            try
            {
                Type t = AccessTools.TypeByName("BlockChangeInfo");
                if (t == null) return null;

                // A20 / A21 / 2.6
                FieldInfo direct = AccessTools.Field(t, "pos");
                if (direct != null && direct.FieldType == typeof(Vector3i))
                    return o => (Vector3i)direct.GetValue(o);

                // 3.1: blockValueRef.BlockPosition
                FieldInfo refField = AccessTools.Field(t, "blockValueRef");
                if (refField != null)
                {
                    FieldInfo bp = AccessTools.Field(refField.FieldType, "BlockPosition");
                    if (bp != null && bp.FieldType == typeof(Vector3i))
                        return o =>
                        {
                            object r = refField.GetValue(o);
                            return r == null ? Vector3i.zero : (Vector3i)bp.GetValue(r);
                        };
                    PropertyInfo bpp = AccessTools.Property(refField.FieldType, "BlockPosition");
                    if (bpp != null && bpp.PropertyType == typeof(Vector3i))
                        return o =>
                        {
                            object r = refField.GetValue(o);
                            return r == null ? Vector3i.zero : (Vector3i)bpp.GetValue(r, null);
                        };
                }
            }
            catch { /* fall through: no accessor ⇒ Prepare() skips the patch */ }
            return null;
        }

        static void Postfix(object _changes)
        {
            // Guarded: this runs inside the game's own block-change path on every placement and
            // every destruction. An exception escaping here would break building.
            try { PostfixBody(_changes); }
            catch (Exception e) { CmmGuard.Report("GameManager.SetBlocksRPC postfix", e); }
        }

        static void PostfixBody(object _changes)
        {
            if (!EmitterAudioLayer.HasActiveVoice) return;      // nothing is listening
            var list = _changes as System.Collections.IEnumerable;
            if (list == null) return;
            lock (_lock)
            {
                foreach (object ci in list)
                {
                    if (ci == null || _pending.Count >= MaxQueued) break;
                    _pending.Add(PosOf(ci));
                }
                _pendingCount = _pending.Count;
            }
        }

        // Radius for the caller, kept beside the queue it belongs to.
        internal static int Radius => ChangeRadius;
    }
}
#endif
