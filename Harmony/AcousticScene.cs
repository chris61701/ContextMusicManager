using System;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────
    // AcousticScene — one native scene over a box of the world, and the speakers in it
    // ─────────────────────────────────────────────────────────────────────────────
    // Owns the native handles and nothing else: the geometry work, the routing and the
    // evaluation all live in cmm_acoustics.c. This is the layer that reads the game's blocks
    // and hands them over, and the layer that keeps the lifetime straight.
    //
    // WHAT THIS REPLACES, and why the shape is different: the shipped model floods outward
    // from the LISTENER and rebuilds that flood as the listener moves, so its cost grows with
    // movement and it cannot serve many speakers at once. Here the expensive half is bound to
    // GEOMETRY and rebuilt only when geometry changes, while each speaker keeps a cheap routed
    // solve of its own. That is what makes many speakers -- and later moving ones -- affordable.
    //
    // Disposal is explicit and idempotent. Native memory is invisible to the GC, so a dropped
    // handle is a leak the profiler will never show; every exit path goes through Dispose.
    // ─────────────────────────────────────────────────────────────────────────────
    internal sealed class AcousticScene : IDisposable
    {
        IntPtr _h;
        readonly int _x0, _y0, _z0, _sx, _sy, _sz;

        internal bool Valid => _h != IntPtr.Zero;
        internal int OriginX => _x0;
        internal int OriginY => _y0;
        internal int OriginZ => _z0;

        AcousticScene(IntPtr h, int x0, int y0, int z0, int sx, int sy, int sz)
        {
            _h = h; _x0 = x0; _y0 = y0; _z0 = z0; _sx = sx; _sy = sy; _sz = sz;
        }

        // Build a scene over a world box. Returns null if the native side is unavailable or the
        // box is unreasonable -- callers fall back to the shipped model rather than failing.
        //
        // Cost is voxel-sized (~310 ms for a 1.09M-voxel POI), so this belongs on the scheduler
        // thread. It is triggered by geometry changing, never by anything per-frame.
        internal static AcousticScene Build(World world, int x0, int y0, int z0, int sx, int sy, int sz)
        {
            if (!NativeAcoustics.Available) return null;
            if (sx <= 0 || sy <= 0 || sz <= 0) return null;
            long n = (long)sx * sy * sz;
            if (n > 64_000_000L) return null;

            IntPtr h = NativeAcoustics.cmm_ac_create(sx, sy, sz, x0, y0, z0);
            if (h == IntPtr.Zero) return null;

            var scene = new AcousticScene(h, x0, y0, z0, sx, sy, sz);
            if (!scene.Upload(world) || !scene.Rebuild())
            {
                scene.Dispose();
                return null;
            }
            return scene;
        }

        // Read the game's blocks into the native grid. CellLoss is live-engine state -- per
        // instance IsSeeThrough, resolved lightOpacity, shape bounds, density, water -- which is
        // why the model consumes it rather than parsing prefabs: parsing offline would model an
        // approximation of the game instead of the game.
        bool Upload(World world)
        {
            if (world == null || _h == IntPtr.Zero) return false;
            long n = (long)_sx * _sy * _sz;
            var loss = new float[n];
            var flags = new byte[n];
            try
            {
                for (int y = 0; y < _sy; y++)
                    for (int z = 0; z < _sz; z++)
                        for (int x = 0; x < _sx; x++)
                        {
                            int wx = _x0 + x, wy = _y0 + y, wz = _z0 + z;
                            long i = x + (long)_sx * (z + (long)_sz * y);
                            BlockValue bv = world.GetBlock(wx, wy, wz);
                            if (bv.isair) { flags[i] = (byte)NativeAcoustics.AIR; loss[i] = 0f; continue; }
                            loss[i] = EmitterAcoustics.DebugCellLoss(bv, world, wx, wy, wz);
                        }
            }
            catch (Exception e)
            {
                CMMConfig.D($"[CMM] acoustics upload failed: {e.Message}");
                return false;
            }
            return NativeAcoustics.cmm_ac_set_voxels(_h, loss, flags) != 0;
        }

        // Derive the fields and the graph. Separate from Upload so a geometry change can reuse
        // the same buffers later without reallocating the scene.
        bool Rebuild()
        {
            if (_h == IntPtr.Zero) return false;
            if (NativeAcoustics.cmm_ac_build_fields(_h) == 0) return false;
            if (NativeAcoustics.cmm_ac_build_segments(_h) == 0) return false;
            return true;
        }

        internal long Stat(int what) => _h == IntPtr.Zero ? -1 : NativeAcoustics.cmm_ac_stat(_h, what);

        internal long Rooms => Stat(7);
        internal long Nodes => Stat(9);
        internal long Edges => Stat(10);
        internal long ArenaBytes => Stat(4);

        internal float OpenAt(int wx, int wy, int wz) =>
            _h == IntPtr.Zero ? 0f : NativeAcoustics.cmm_ac_open_at(_h, wx, wy, wz);

        internal AcousticSource CreateSource(Vector3 pos)
        {
            if (_h == IntPtr.Zero) return null;
            IntPtr s = NativeAcoustics.cmm_ac_src_create(_h, pos.x, pos.y, pos.z);
            return s == IntPtr.Zero ? null : new AcousticSource(s);
        }

        internal IntPtr Handle => _h;

        public void Dispose()
        {
            if (_h == IntPtr.Zero) return;
            NativeAcoustics.cmm_ac_destroy(_h);
            _h = IntPtr.Zero;
        }
    }

    // One speaker's routed solve against a scene. Cheap to move, and Move allocates nothing --
    // the arena was sized once at creation from the scene's graph.
    internal sealed class AcousticSource : IDisposable
    {
        IntPtr _h;
        internal AcousticSource(IntPtr h) { _h = h; }
        internal bool Valid => _h != IntPtr.Zero;

        internal bool Move(Vector3 p) =>
            _h != IntPtr.Zero && NativeAcoustics.cmm_ac_src_move(_h, p.x, p.y, p.z) != 0;

        // Call after the scene it is bound to has been rebuilt; everything solved before that
        // describes geometry which no longer exists.
        internal bool Rebind() =>
            _h != IntPtr.Zero && NativeAcoustics.cmm_ac_src_rebind(_h) != 0;

        // Fill a caller-owned buffer with the arrivals at a listener. Nothing is allocated on
        // either side of the boundary, so this is safe to call per emitter per tick.
        internal int Evaluate(Vector3 ear, NativeAcoustics.Arrival[] buf) =>
            _h == IntPtr.Zero || buf == null ? 0
                : Math.Max(0, NativeAcoustics.cmm_ac_eval(_h, ear.x, ear.y, ear.z, buf, buf.Length));

        // The energy sum of every arrival, in dB relative to the source, for callers that want a
        // level rather than a set.
        internal double LevelDb(Vector3 ear) =>
            _h == IntPtr.Zero ? -120.0 : NativeAcoustics.cmm_ac_eval_db(_h, ear.x, ear.y, ear.z);

        public void Dispose()
        {
            if (_h == IntPtr.Zero) return;
            NativeAcoustics.cmm_ac_src_destroy(_h);
            _h = IntPtr.Zero;
        }
    }
}
