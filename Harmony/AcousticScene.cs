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

        // Build a scene from an ALREADY-READ box of voxels. Everything here is native work on
        // native memory -- no game API is touched -- which is what makes it safe on the scheduler
        // thread. Reading the world is the other half, and it is deliberately NOT here: see
        // VoxelBox.
        internal static AcousticScene FromVoxels(VoxelBox box)
        {
            if (!NativeAcoustics.Available || box == null || !box.Complete) return null;

            IntPtr h = NativeAcoustics.cmm_ac_create(box.Sx, box.Sy, box.Sz, box.X0, box.Y0, box.Z0);
            if (h == IntPtr.Zero) return null;

            var scene = new AcousticScene(h, box.X0, box.Y0, box.Z0, box.Sx, box.Sy, box.Sz);
            if (NativeAcoustics.cmm_ac_set_voxels(h, box.Loss, box.Flags) == 0
                || NativeAcoustics.cmm_ac_build_fields(h) == 0
                || NativeAcoustics.cmm_ac_build_segments(h) == 0)
            {
                scene.Dispose();
                return null;
            }
            return scene;
        }

        internal long Stat(int what) => _h == IntPtr.Zero ? -1 : NativeAcoustics.cmm_ac_stat(_h, what);

        internal long Rooms => Stat(7);
        internal long Nodes => Stat(9);
        internal long Edges => Stat(10);
        internal long ArenaBytes => Stat(4);

        internal float OpenAt(int wx, int wy, int wz) =>
            _h == IntPtr.Zero ? 0f : NativeAcoustics.cmm_ac_open_at(_h, wx, wy, wz);

        // Is this world position inside the box this scene was built over, with `margin` blocks to
        // spare? The margin matters: a source right on the face has no geometry on one side of it,
        // so its solve is as meaningless as one fully outside.
        internal bool Contains(Vector3 p, float margin)
        {
            return p.x >= _x0 + margin && p.x <= _x0 + _sx - 1 - margin
                && p.y >= _y0 + margin && p.y <= _y0 + _sy - 1 - margin
                && p.z >= _z0 + margin && p.z <= _z0 + _sz - 1 - margin;
        }

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

    // ─────────────────────────────────────────────────────────────────────────────
    // VoxelBox — the world read, on the main thread, a slice at a time
    // ─────────────────────────────────────────────────────────────────────────────
    // Reading blocks is the ONE part of a scene build that touches the game, and the game's
    // world is not safe to read from another thread: chunk access races a loader and gets you a
    // torn read or a hard crash, not an exception you can catch. So the read stays where every
    // other block read in this mod already is -- the main thread -- and is amortized across
    // frames under a millisecond budget, exactly like the shipped flood and the field cache.
    // Only the native half runs on the scheduler, where it touches nothing but its own arena.
    //
    // The buffers are allocated ONCE and reused for every rebuild. A 145x49x145 box is 4.1 MB of
    // float plus 1 MB of byte; re-allocating that per door swing on Unity's non-generational GC is
    // exactly the kind of churn this whole subsystem exists to avoid. The read is ~49 slabs at
    // 1.5 ms, so about two seconds of walking -- during which the PREVIOUS scene keeps serving, so
    // the only cost is latency, never a gap.
    internal sealed class VoxelBox
    {
        internal readonly int Sx, Sy, Sz;
        internal int X0, Y0, Z0;
        internal readonly float[] Loss;
        internal readonly byte[] Flags;
        internal bool Complete { get; private set; }

        // Cursor is (y, z) -- a ROW at a time, not a slab. A slab is Sx*Sz blocks: 9,409 at the old
        // +/-48 extent and 40,401 at +/-100, so checking the time budget only between slabs made the
        // budget meaningless and stalled the frame for as long as one slab took. Measured in game:
        // "STUTTER 3 frame(s) over 654 ms, worst 374.3 ms (10.4x baseline)" in the same frame the
        // read was running -- and that stall is very likely what stranded a PlayOnce stinger 382 ms
        // in, since a 374 ms frame is long enough for the scan loop to see a just-started voice as
        // dead. A row is Sx blocks (201 at the widest), which fits inside any sane budget.
        int _cursorY, _cursorZ;
        internal float ProgressPct =>
            (Sy == 0 || Sz == 0) ? 0f : 100f * (_cursorY * Sz + _cursorZ) / (Sy * Sz);

        internal VoxelBox(int sx, int sy, int sz)
        {
            Sx = sx; Sy = sy; Sz = sz;
            long n = (long)sx * sy * sz;
            Loss = new float[n];
            Flags = new byte[n];
        }

        // Point the box at a new origin and start over. Buffers are kept; only the cursor moves.
        internal void Restart(int x0, int y0, int z0)
        {
            X0 = x0; Y0 = y0; Z0 = z0;
            _cursorY = 0; _cursorZ = 0;
            Complete = false;
        }

        // Read up to budgetMs worth of slabs. Returns true once the whole box is filled. Whole
        // slabs only: the cost of one is bounded and predictable, and a partial slab would need a
        // second cursor for no benefit.
        //
        // CellLoss is live-engine state -- per-instance IsSeeThrough, resolved lightOpacity, shape
        // bounds, density, water -- which is why the model consumes it rather than parsing prefabs:
        // parsing offline would model an approximation of the game instead of the game.
        internal bool Step(World world, float budgetMs)
        {
            if (Complete) return true;
            if (world == null) return false;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            try
            {
                while (_cursorY < Sy)
                {
                    int y = _cursorY, z = _cursorZ;
                    for (int x = 0; x < Sx; x++)
                    {
                        int wx = X0 + x, wy = Y0 + y, wz = Z0 + z;
                        long i = x + (long)Sx * (z + (long)Sz * y);
                        BlockValue bv = world.GetBlock(wx, wy, wz);
                        if (bv.isair) { Flags[i] = (byte)NativeAcoustics.AIR; Loss[i] = 0f; continue; }
                        Flags[i] = 0;
                        Loss[i] = EmitterAcoustics.DebugCellLoss(bv, world, wx, wy, wz);
                    }
                    if (++_cursorZ >= Sz) { _cursorZ = 0; _cursorY++; }
                    if ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * toMs >= budgetMs) break;
                }
            }
            catch (Exception e)
            {
                CMMConfig.D($"[CMM] acoustics voxel read failed: {e.Message}");
                Complete = false;
                _cursorY = 0; _cursorZ = 0;
                return false;
            }
            if (_cursorY >= Sy) Complete = true;
            return Complete;
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
