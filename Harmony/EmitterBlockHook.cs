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
                    Vector3i bp = PosOf(ci);
                    _pending.Add(bp);
                    Arm(bp);
                }
                _pendingCount = _pending.Count;
            }
        }

        // Radius for the caller, kept beside the queue it belongs to.
        internal static int Radius => ChangeRadius;

        // ── THE BLOCK STOPWATCH ─────────────────────────────────────────────────────────────
        // The door stopwatch stops at the field publish, which is not where anybody hears it.
        // This one runs to the number the MIXER receives: place or destroy a block enclosing an
        // emitter, and it reports how long until the gain that block changed actually moved.
        //
        // Three stamps, so a slow answer says WHICH stage was slow:
        //   change -> field : the geometry re-read plus the flood that carried it
        //   field  -> sound : the emitter tick picking it up, and the rise limiter letting it out
        //
        // That last stage is the one no existing counter covered, and it is the one with a rate
        // cap in it (WorldEmitterOcclusionFadeRate), so it can legitimately dominate.
        private static float _watchAt = -999f, _watchFieldAt = -999f, _watchTickAt = -999f;
        private static Vector3i _watchPos;

        private static void Arm(Vector3i pos)
        {
            // Only one in flight: a wall goes up as many changes and the first is the one to time.
            if (_watchAt > 0f && CmmClock.Now - _watchAt < 5f) return;
            _watchAt = CmmClock.Now; _watchFieldAt = -999f; _watchTickAt = -999f; _watchPos = pos;
        }

        // A BLOCK IS AS URGENT AS A DOOR. The flood is gated to one every FloodSec (0.30 s)
        // unless a door is mid-swing, and a block change set no such flag -- so it waited out the
        // cadence. Measured: the field stage of a block change ran ~230 ms against a door's ~115,
        // and a uniform 0-300 ms wait plus a ~65 ms flood predicts exactly that. The player did
        // the same thing in both cases, deliberately, and is listening for the result.
        private const float UrgentWindow = 1.0f;
        internal static bool RecentChange => _watchAt > 0f && CmmClock.Now - _watchAt < UrgentWindow;

        // Called when a flood completes; the first one after a change is the one carrying it.
        internal static void NoteFieldPublished()
        {
            if (_watchAt <= 0f || _watchFieldAt >= _watchAt) return;
            _watchFieldAt = CmmClock.Now;
            // AND DO NOT LET IT SIT FOR THE NEXT TICK. The emitter layer publishes at
            // WorldEmitterTickHz -- 100 ms at the default 10 -- and the answer is ready NOW.
            // Measured: once the flood stopped waiting out its own cadence, the publish stage
            // became the whole cost at ~130 ms median, which is that tick almost exactly. The door
            // path has always collapsed it here, on the flood that carries the door; a block
            // change is the same event and was not asking.
            EmitterAudioLayer.RequestImmediatePublish();
        }

        // The emitter tick that will carry it has STARTED. Splits the old publish stage into the
        // wait for the tick and the work inside it, because collapsing the tick did not move the
        // number and one of those two has to be where it went.
        internal static void NoteTickEntered()
        {
            if (_watchAt > 0f && _watchFieldAt >= _watchAt && _watchTickAt < _watchFieldAt)
                _watchTickAt = CmmClock.Now;
        }

        // Called from the emitter publish with the change in the value the mixer is handed.
        internal static void NoteGainMoved(float delta)
        {
            if (_watchAt <= 0f || _watchFieldAt < _watchAt) return;   // field has not answered yet
            float now = CmmClock.Now;
            float total = (now - _watchAt) * 1000f;
            if (total > 5000f) { _watchAt = -999f; return; }          // stale: nothing moved
            float tickWait = _watchTickAt >= _watchFieldAt ? (_watchTickAt - _watchFieldAt) * 1000f : -1f;
            float inTick   = _watchTickAt >= _watchFieldAt ? (now - _watchTickAt) * 1000f : -1f;
            CMMConfig.D($"[CMM] block -> sound: {total:0} ms "
                      + $"(field {(_watchFieldAt - _watchAt) * 1000f:0} "
                      + $"+ tickwait {tickWait:0} + intick {inTick:0}, "
                      + $"gain moved {delta:0.00} at {_watchPos})");
            _watchAt = -999f;
        }
    }
}
#endif
