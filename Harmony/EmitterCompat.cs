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

        // ── Fetching a loaded chunk ──────────────────────────────────────────────────────────
        // The second divergence, and a nastier kind than the prefab one: the NAME is the same, the
        // SIGNATURE is the same, the return type is the same. What moved is the DECLARING TYPE.
        //
        //   3.x   abstract WorldBase.GetChunkSync(int, int), overridden on World
        //   2.6            World.GetChunkSync(int, int), and no WorldBase declaration at all
        //
        // Because 3.x's is virtual, a call compiled there targets the slot on WorldBase -- so the
        // shipped DLL asks 2.6 for WorldBase.GetChunkSync and gets MissingMethodException, 36 of
        // them in the first minute. Nothing about the call site looks version-specific, and a
        // survey that checks a member EXISTS finds it on both: it is only wrong about WHERE.
        //
        // Bound against the world's own runtime type, which finds the method wherever it is
        // declared. Closed over the instance and cached, because this is called once per chunk in
        // the proximity sweep -- roughly 120 times per pass -- and reflection per call would be a
        // real cost on the frame thread. Rebound whenever the world object changes, i.e. per load.
        private static object _chunkWorld;
        private static Func<int, int, IChunk> _getChunkSync;

        internal static IChunk GetChunkSync(object world, int chunkX, int chunkZ)
        {
            if (world == null) return null;
            if (!ReferenceEquals(world, _chunkWorld))
            {
                _chunkWorld = world;
                _getChunkSync = null;
                try
                {
                    var m = AccessTools.Method(world.GetType(), "GetChunkSync",
                                               new[] { typeof(int), typeof(int) });
                    if (m != null)
                        _getChunkSync = (Func<int, int, IChunk>)Delegate.CreateDelegate(
                            typeof(Func<int, int, IChunk>), world, m);
                }
                catch { _getChunkSync = null; }

                if (_getChunkSync == null)
                    Log.Warning("[CMM] World emitters: this game has no GetChunkSync(int,int) — "
                              + "speaker discovery cannot run.");
            }
            if (_getChunkSync == null) return null;
            try { return _getChunkSync(chunkX, chunkZ); }
            catch { return null; }
        }

        // ── Terrain density ──────────────────────────────────────────────────────────────────
        // The third divergence, and a third KIND: an arity change. 3.x dropped the leading cluster
        // index that 2.6 requires.
        //
        //   3.x   sbyte GetDensity(int x, int y, int z)
        //   2.6   sbyte GetDensity(int clrIdx, int x, int y, int z)
        //
        // My first survey compared member NAMES, which missed the prefab split's shape change. The
        // second compared DECLARING TYPES, which found GetChunkSync and passed this one, because
        // the name is on the same types in both. Only comparing full SIGNATURES finds it. Three
        // rounds, three lessons, and the honest summary is that a version survey has to compare
        // the whole signature or it is not a survey.
        //
        // Cluster 0 is the main world, as every 2.6 caller in the game's own code uses it.
        private static object _densityWorld;
        private static Func<int, int, int, sbyte> _getDensity;

        internal static sbyte GetDensity(object world, int x, int y, int z)
        {
            // Solid, not air, when the answer cannot be had. This feeds the terrain-fullness
            // scale in the occlusion cost: -128 reads as a completely full voxel, so a failure
            // makes rock behave like rock. Returning 0 would make it read as very nearly empty
            // and leak sound straight through the ground.
            const sbyte Solid = -128;
            if (world == null) return Solid;

            if (!ReferenceEquals(world, _densityWorld))
            {
                _densityWorld = world;
                _getDensity = null;
                try
                {
                    Type t = world.GetType();
                    var three = AccessTools.Method(t, "GetDensity",
                                                   new[] { typeof(int), typeof(int), typeof(int) });
                    if (three != null)
                    {
                        _getDensity = (Func<int, int, int, sbyte>)Delegate.CreateDelegate(
                            typeof(Func<int, int, int, sbyte>), world, three);
                    }
                    else
                    {
                        var four = AccessTools.Method(t, "GetDensity",
                            new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
                        if (four != null)
                        {
                            var f = (Func<int, int, int, int, sbyte>)Delegate.CreateDelegate(
                                typeof(Func<int, int, int, int, sbyte>), world, four);
                            _getDensity = (px, py, pz) => f(0, px, py, pz);
                        }
                    }
                }
                catch { _getDensity = null; }

                if (_getDensity == null)
                    Log.Warning("[CMM] World emitters: this game has no GetDensity the mod can call — "
                              + "terrain will be treated as fully solid for occlusion.");
            }

            if (_getDensity == null) return Solid;
            try { return _getDensity(x, y, z); }
            catch { return Solid; }
        }

        // ── Can sound pass through this block? ───────────────────────────────────────────────
        // The fourth divergence, and the same arity change as GetDensity, on a different type:
        //
        //   3.x   bool Block.IsSeeThrough(WorldBase w, Vector3i pos, BlockValue bv)
        //   2.6   bool Block.IsSeeThrough(WorldBase w, int clrIdx, Vector3i pos, BlockValue bv)
        //
        // Virtual, so it is bound per BLOCK type rather than per world: a window and a door are
        // different Block subclasses with their own overrides. Cached by the declaring type, which
        // is a handful of entries for a whole world and stable for the process.
        private static readonly Dictionary<Type, Func<object, object, Vector3i, BlockValue, bool>> _seeThrough =
            new Dictionary<Type, Func<object, object, Vector3i, BlockValue, bool>>();
        private static bool _seeThroughWarned;

        internal static bool IsSeeThrough(Block b, object world, Vector3i pos, BlockValue bv)
        {
            // Solid, not transparent, when the answer cannot be had: a wrong "sound passes here"
            // opens a hole in the occlusion, while a wrong "it does not" merely over-muffles.
            if (b == null) return false;
            Type t = b.GetType();
            Func<object, object, Vector3i, BlockValue, bool> fn;
            if (!_seeThrough.TryGetValue(t, out fn))
            {
                fn = BindSeeThrough(t);
                _seeThrough[t] = fn;
                if (fn == null && !_seeThroughWarned)
                {
                    _seeThroughWarned = true;
                    Log.Warning("[CMM] World emitters: this game has no IsSeeThrough the mod can call — "
                              + "windows and open doorways will occlude like solid walls.");
                }
            }
            if (fn == null) return false;
            try { return fn(b, world, pos, bv); }
            catch { return false; }
        }

        static Func<object, object, Vector3i, BlockValue, bool> BindSeeThrough(Type t)
        {
            try
            {
                Type wb = AccessTools.TypeByName("WorldBase");
                if (wb == null) return null;

                var three = AccessTools.Method(t, "IsSeeThrough",
                                               new[] { wb, typeof(Vector3i), typeof(BlockValue) });
                if (three != null)
                    return (blk, w, p, v) => (bool)three.Invoke(blk, new object[] { w, p, v });

                var four = AccessTools.Method(t, "IsSeeThrough",
                                              new[] { wb, typeof(int), typeof(Vector3i), typeof(BlockValue) });
                if (four != null)
                    return (blk, w, p, v) => (bool)four.Invoke(blk, new object[] { w, 0, p, v });
            }
            catch { }
            return null;
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
