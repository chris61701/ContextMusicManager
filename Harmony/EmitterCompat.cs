// Gated to 3.0 (GV_V3) like the rest of the world-emitter layer — but note this gate is about
// which BUILD contains the code, not which GAME runs it. The everyday Debug fan-out and the
// Release package both ship ONE DLL compiled against 3.x (GV_V3 defined) to every supported
// game version, so all the GV_V3 code below is PRESENT and loadable on A20/A21/2.6. This class
// is the runtime side of the gate: it detects the game we actually woke up inside and decides
// what the emitter feature can do there.
#if GV_V3
using System;
using System.Collections.Generic;
using HarmonyLib;

namespace CMM
{
    internal static class EmitterCompat
    {
        // True on a 2.x or 3.x game that exposes everything the emitter layer needs. Every
        // world-emitter entry point that can execute on an older game must check this FIRST:
        //   - EmitterAudioLayer.Tick() (the frame driver)
        //   - Patch_DoorAcousticChange.Prepare() (so the TEFeatureDoor patch is skipped, not applied)
        //   - the DebugHud "Emitters:" row (drives EmitterRegistry.Poll directly)
        // Everything deeper is safe by lazy JIT: methods that touch version-shaped game APIs are
        // never invoked when this is false, so their member tokens are never resolved.
        internal static readonly bool RuntimeSupported = Probe();

        // True when the game has composite tile-entity doors (3.0+). NOT a requirement: without
        // them the emitter layer runs and doors simply do not modulate occlusion mid-swing, which
        // is a smaller feature inside a working one. It used to be part of the gate, which is why
        // 2.6 had no world speakers at all -- a door type standing in for a whole subsystem.
        internal static bool HasCompositeDoors { get; private set; }

        // ── Enumerating the world's prefabs ──────────────────────────────────────────────────
        // The one real API divergence between 2.6 and 3.x in everything the emitter layer touches,
        // and it is a member-SHAPE split, which is the kind a per-version compile cannot see:
        //
        //   3.x   void GetWorldPrefabs(List<PrefabInstance> dst)      fills the caller's list
        //   2.6   List<PrefabInstance> GetDynamicPrefabs()            returns the live list
        //
        // Both are "every prefab placed in the world" -- 3.2's AddWorldPrefab writes worldPrefabs
        // and allPrefabs together, and 2.6's AddPrefab writes allPrefabs. Bound once by name, so
        // neither token is emitted into the call site.
        private static Action<object, List<PrefabInstance>> _fillPrefabs;

        // Fills `dst` with the world's prefabs. False when this game exposes neither shape, which
        // is what RuntimeSupported is really testing: a version number is a proxy, this is the
        // capability itself.
        internal static bool GetWorldPrefabs(object decorator, List<PrefabInstance> dst)
        {
            if (_fillPrefabs == null || decorator == null || dst == null) return false;
            try { _fillPrefabs(decorator, dst); return true; }
            catch { return false; }   // a list mutating under us: skip this poll, retry in 250 ms
        }

        static bool BindPrefabEnumerator()
        {
            Type deco = AccessTools.TypeByName("DynamicPrefabDecorator");
            if (deco == null) return false;

            var fill = AccessTools.Method(deco, "GetWorldPrefabs", new[] { typeof(List<PrefabInstance>) });
            if (fill != null)
            {
                _fillPrefabs = (d, dst) => fill.Invoke(d, new object[] { dst });
                return true;
            }

            var ret = AccessTools.Method(deco, "GetDynamicPrefabs", Type.EmptyTypes);
            if (ret != null)
            {
                // 2.6 hands back the LIVE list with no lock of its own, so copying it can race a
                // prefab being added. AddRange would throw there; the caller treats a throw as
                // "nothing this poll" rather than as an error, and the next poll is 250 ms away.
                _fillPrefabs = (d, dst) =>
                {
                    var live = ret.Invoke(d, null) as List<PrefabInstance>;
                    if (live == null) return;
                    for (int i = 0; i < live.Count; i++)
                    {
                        PrefabInstance pi = live[i];
                        if (pi != null) dst.Add(pi);
                    }
                };
                return true;
            }
            return false;
        }

        static bool Probe()
        {
            try
            {
                // Constants.cVersionInformation and VersionInformation.{ReleaseType,Major} have the
                // same shape (public readonly fields) from A20 through 3.2, but bind by NAME anyway:
                // this probe is the one piece of emitter code guaranteed to RUN on every version, so
                // it must not carry a hard member token that could itself fail to resolve.
                object vi = AccessTools.Field(AccessTools.TypeByName("Constants"), "cVersionInformation")?.GetValue(null);
                if (vi == null) return false;
                var t = vi.GetType();
                object rel = AccessTools.Field(t, "ReleaseType")?.GetValue(vi);
                object major = AccessTools.Field(t, "Major")?.GetValue(vi);

                // A20/A21 report "Alpha 20/21"; 2.x/3.x report "V 2/3". The version is a floor, not
                // the test: what the feature actually needs is a way to enumerate world prefabs,
                // so that is what decides. Doors are recorded as a capability rather than required.
                if (rel == null || rel.ToString() != "V") return false;
                if (!(major is int m) || m < 2) return false;

                HasCompositeDoors = AccessTools.TypeByName("TEFeatureDoor") != null;
                return BindPrefabEnumerator();
            }
            catch
            {
                // Unknown game shape → treat as unsupported: the radio stays off, the rest of
                // the mod is untouched.
                return false;
            }
        }
    }
}
#endif
