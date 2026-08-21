// Gated to 3.0+ (GV_V3): reads the world through EmitterAcoustics.CellLoss and publishes through
// EmitterAudioLayer, both on the same gate.
#if GV_V3
using System;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────
    // FieldOcclusion — the game side of the one listener-anchored field
    // ─────────────────────────────────────────────────────────────────────────────
    // There is exactly one pair of ears and there may be many things making noise, some of them
    // moving. So nothing here is computed per source: a single field is flooded outward from the
    // listener a few times a second, and a speaker — or later a zombie, or anything else — finds
    // out how muffled it is by looking up where it happens to be standing. Attenuation is
    // symmetric, so a field built from the listener answers for a source exactly as one built from
    // the source would have.
    //
    // WHAT THIS FIXES, stated plainly, because the previous model failed on each of them:
    //
    //   Nothing persists long enough to go stale. The field is rebuilt whole, so a door that opens
    //   is simply in the next one. There is no patch, no "has the rebuild landed", no handover at
    //   the wrong moment, and no second model to fall back to — the bug class that cost a week
    //   cannot occur here rather than being fixed here.
    //
    //   Nothing is global. Every cell's value comes from a route to it. A chunk loading eighty
    //   blocks away cannot change what you hear, which the room-coupling solve could and did.
    //
    //   Moving sources are free. The hundredth costs one lookup.
    //
    // THREADING. Reading blocks is the one part that must be on the main thread (chunk access off
    // it is a crash, not an exception), so the geometry fill is amortised across frames there. The
    // flood is pure arithmetic over the native arena and runs on the scheduler. They overlap
    // safely: the flood builds into its own buffer and publishes only if the window did not move
    // under it, and a cell being re-read mid-flood just means that cell carries geometry from a
    // moment earlier, which is a value the flood would have been entitled to read anyway.
    internal static class FieldOcclusion
    {
        // Blocks per cell — WIDE AND SHORT, not cubic. The affordability of "rebuild it, don't
        // patch it" rests on being coarse; the correctness rests on not being coarse VERTICALLY.
        //
        // Buildings are not isotropic. A wall spans a cell's full height, so a 3-block cell
        // represents it exactly. A FLOOR is one block thick with a storey of air above it, and a
        // 3-block-tall cell cannot represent that at all: a source standing on a floor landed in a
        // cell whose Y middle plane WAS the floor, so reaching it from above was charged as going
        // through the floor. Measured in FLAT OPEN AIR with nothing in the way, varying only where
        // the ground's top block fell relative to the cell grid:
        //
        //     ground top y=20  ->   0.00 dB        y=22  ->  29.69 dB
        //     ground top y=21  ->   1.36 dB        y=23  ->   0.00 dB
        //
        // Three ground heights in every eight were 29 dB down outdoors, which is "the music is all
        // quiet and the occlusion is still broken" exactly. The offline probe missed it for weeks
        // by testing a single ground height that happened to align.
        //
        // Short cells cost NOTHING in geometry reads — those track the block volume, which is
        // unchanged at ~930k for a full window — and multiply only the flood, 7 ms to ~21 ms, on
        // the scheduler thread at 3.3 Hz. test_field.c now sweeps every alignment.
        // TWO blocks wide, not three. A 3-wide cell cannot represent a 1-block wall around a small
        // room -- the same failure the 3-block-TALL cell had against a 1-block floor, on the other
        // axes. Measured on a bathroom-sized box with one door, listener inside, speaker nine
        // blocks outside:
        //
        //     cell size    door open    door shut    difference
        //      3x1x3          9.5 dB      11.6 dB       2.1 dB   <- shutting the door did nothing
        //      2x1x2          9.3 dB      56.4 dB      47.1 dB
        //      1x1x1          0.0 dB      56.8 dB      56.8 dB
        //
        // Reported as "I'm in a bathroom, and closing and opening the door does nothing".
        //
        // 1x1x1 is the right answer and costs 819 ms a flood, so it is not an answer. 2x1x2 over
        // the audible range costs 36.9 ms against the 36.6 ms three-wide cells cost over a taller
        // window -- the same money, paid for out of vertical extent (see BelowBlocks/AboveBlocks).
        // Geometry reads do not change at all: they track the number of BLOCKS in the window, not
        // the number of cells.
        // ONE BLOCK. Not two, not three.
        //
        // Two blocks could not seal a one-block wall, which is the same failure as the original
        // three-block-tall cell against a one-block floor -- now for the third time, on the last
        // axes left. Measured on a POI bathroom with the game's own face rule, a SHUT door read
        // identical to bricking the doorway up with concrete, to the decimal, at every one of the
        // four ways the room can land on the cell grid. Swapping the wood door for a steel vault
        // door changed nothing. The door's mass was not an input to the answer.
        //
        // A cell straddling a room's outside corner holds one wall block and three air blocks, and
        // every route left through such a corner in one diagonal hop. No material knob can reach
        // that: sweeping the aperture exponent 1..8 moved the OPEN-door path 3.3 -> 24.3 dB while
        // shut saturated at 4.6.
        //
        // The cost is a smaller window, and it comes out even. Geometry reads track BLOCKS, not
        // cells, so they get ~4x cheaper (560k -> 140k) and the full re-read sweep drops from ~11 s
        // to ~3 s. The flood over +/-32 blocks costs 30.8 ms against the 24.9 ms two-wide cells
        // cost over +/-64. Same money, exact geometry.
        private const int StepXZ = 1;
        private const int StepY  = 1;
        // What the finer horizontal grid is paid for with; was 24/24. Safe because a speaker
        // outside the window now reads CLEAR rather than sealed (see sample_field in cmm_field.c),
        // so the edge is a fade to "no opinion" rather than a cut-off.
        private const int BelowBlocks = 16, AboveBlocks = 16;

        private static IntPtr _f = IntPtr.Zero;
        private static int _nx, _ny, _nz, _range;
        private static Vector3 _ear;              // captured on the main thread, read by the flood
        private static bool _earValid;
        // How often the world is re-read even when nothing said it changed. See the rolling
        // re-read in FrameStep for why this exists at all.
        // 0.4 s, down from 1.0. Geometry reads got about four times cheaper when cells went to
        // one block (they track BLOCKS, not cells), so the ring that catches a door the hook
        // missed can run more than twice as often for less than half the budget it used to take.
        // Worst-case latency for a door is now this plus one flood interval.
        private const float NearRefreshSec = 0.4f, FarRefreshSec = 10f;
        private const int   NearRefreshBlocks = 12;
        private static float _nearRefreshAt, _farRefreshAt;
        internal static int NearRefreshes, FarRefreshes;

        private static Vector3i _centre;          // block position the window was last centred on
        private static bool _centreValid;
        internal static int Recentres;            // how often the window actually had to move

        internal static long CellsPending => _f == IntPtr.Zero ? -1 : NativeField.cmm_fld_stat(_f, 1);
        internal static long CellsTotal   => _f == IntPtr.Zero ? -1 : NativeField.cmm_fld_stat(_f, 0);
        internal static long LastSolveUs  => _f == IntPtr.Zero ? -1 : NativeField.cmm_fld_stat(_f, 3);
        internal static long Reached      => _f == IntPtr.Zero ? -1 : NativeField.cmm_fld_stat(_f, 2);
        internal static int  Discarded;           // floods thrown away because the window moved
        internal static bool Ready => _f != IntPtr.Zero && Reached > 0;

        internal static string Status()
        {
            if (_f == IntPtr.Zero) return "not built";
            long pend = CellsPending, tot = CellsTotal;
            return $"cells {tot} ({StepXZ}x{StepY}x{StepXZ}-block, {_nx}x{_ny}x{_nz}), "
                 + (pend > 0 ? $"reading {100.0 * (tot - pend) / Math.Max(tot, 1):0}%, " : "read complete, ")
                 + $"reached {Reached}, flood {LastSolveUs / 1000.0:0.0} ms, "
                 + $"{Discarded} discarded, {Recentres} recentres, "
                 + $"{NearRefreshes}+{FarRefreshes} rereads";
        }

        // ── Lifetime ────────────────────────────────────────────────────────────────────────────
        private static bool Ensure()
        {
            if (!NativeField.Available) return false;
            // The field's opinion reaches FieldRangeCap blocks even when the mixer carries sound
            // further: at one block per cell the window is what costs, and occlusion matters most
            // in the room you are standing in. Beyond it a speaker reads CLEAR rather than sealed
            // (sample_field returns "no opinion" outside the window), so the edge is a graceful
            // absence of occlusion, not a cut-off.
            const int FieldRangeCap = 32;
            int range = Mathf.Clamp(Mathf.CeilToInt(CMMConfig.WorldEmitterRange) + 4, 24, FieldRangeCap);
            if (_f != IntPtr.Zero && range == _range) return true;
            Reset();
            _range = range;
            // Odd counts so the listener's cell sits at the centre of the window.
            _nx = _nz = (range + StepXZ - 1) / StepXZ * 2 + 1;
            _ny = (BelowBlocks + AboveBlocks) / StepY + 1;
            _f = NativeField.cmm_fld_create(_nx, _ny, _nz, StepXZ, StepY, StepXZ);
            if (_f == IntPtr.Zero) { Log.Warning("[CMM] field occlusion: create failed."); return false; }
            NativeField.cmm_fld_set_tuning(_f, CMMConfig.WorldEmitterFieldDetourDb, 60f);
            CMMConfig.D($"[CMM] field occlusion: {_nx}x{_ny}x{_nz} cells of "
                      + $"{StepXZ}x{StepY}x{StepXZ} blocks (+/-{range} blocks), "
                      + $"{(long)_nx * _ny * _nz} total");
            return true;
        }

        // 1 while the scheduler thread is inside cmm_fld_solve. Reset() runs on the MAIN thread
        // (Ensure() calls it whenever WorldEmitterRange changes, and so does `cmm acoustics model`)
        // and frees all sixteen native arrays — straight into a flood that may be 20-50 ms from
        // finishing, writing its queue and buffers into freed heap. Never seen as a dip; seen as a
        // crash, eventually, on someone else's machine.
        private static int _flooding;

        internal static void Reset()
        {
            IntPtr f = System.Threading.Volatile.Read(ref _f);
            System.Threading.Volatile.Write(ref _f, IntPtr.Zero);   // no NEW flood can start
            if (f != IntPtr.Zero)
            {
                // Let one already in flight finish. It re-checks _f after taking the flag, so a
                // flood that started before the store above will bail on its own; this only waits
                // for one that got past that point. Bounded so a wedged scheduler cannot hang the
                // game — leaking the arena is the better failure.
                for (int i = 0; i < 400 && System.Threading.Volatile.Read(ref _flooding) != 0; i++)
                    System.Threading.Thread.Sleep(1);
                if (System.Threading.Volatile.Read(ref _flooding) == 0)
                    NativeField.cmm_fld_destroy(f);
                else
                    Log.Warning("[CMM] field: flood still running at reset — arena leaked rather than freed under it.");
            }
            _earValid = false; Discarded = 0; _centreValid = false; Recentres = 0;
            _nearRefreshAt = _farRefreshAt = 0f; NearRefreshes = FarRefreshes = 0;
            _slew.Clear();
        }

        // ── Main thread: keep the window on the player and read the world into it ───────────────
        // Called per frame from the emitter layer's existing budgeted slot. The recentre is cheap
        // (it only marks what scrolled in as pending), so it can happen every frame and the window
        // never lags the player.
        internal static void FrameStep(EntityPlayerLocal player, World world, float budgetMs)
        {
            if (!EmitterAudioLayer.FieldActive || player == null || world == null) return;
            if (!Ensure()) return;

            _ear = EmitterAudioLayer.ListenerWorldPos(player);
            _earValid = true;

            // RECENTRE ON A DRIFT, NOT ON EVERY STEP. A recentre changes what every index means,
            // so a flood in progress when one lands is thrown away — and with one-block-tall cells
            // ANY single block of vertical movement used to trigger one. Walking up a slope, or a
            // staircase, or simply jumping, re-centred the window over and over; the log showed
            // floods being discarded and the audio dropping to another model each time.
            //
            // The window is bigger than the audible range on purpose, so the listener is allowed
            // to wander a few blocks off centre before it has to move. Slack is smaller in Y
            // because the window is shallower there (+/-24 blocks against +/-64).
            const int SlackXZ = 6, SlackYb = 4;
            int ebx = Mathf.FloorToInt(_ear.x), eby = Mathf.FloorToInt(_ear.y), ebz = Mathf.FloorToInt(_ear.z);
            if (!_centreValid
                || Math.Abs(ebx - _centre.x) > SlackXZ
                || Math.Abs(eby - _centre.y) > SlackYb
                || Math.Abs(ebz - _centre.z) > SlackXZ)
            {
                NativeField.cmm_fld_recentre(_f, ebx, eby, ebz);
                _centre = new Vector3i(ebx, eby, ebz);
                _centreValid = true;
                Recentres++;
            }

            // ── ROLLING RE-READ ────────────────────────────────────────────────────────────
            // The field only learns the world changed when something TELLS it: a door swing on the
            // falling edge, or the window scrolling as the player walks. Stand still and open a
            // door whose animation the hook does not catch and those cells keep the reading they
            // had when you walked in -- for as long as you stand there. Reported exactly so:
            // opening and closing a bathroom door changed nothing at all.
            //
            // That is one hook standing between the model and every geometry change in the game:
            // block damage, another mod's blocks, a peer's edits in multiplayer, a chunk quietly
            // swapping under us. The explicit door invalidation stays because it is instant; this
            // is the net under it, and it depends on nothing.
            //
            // Two rings, because they want different rates. The near ring is small enough to
            // re-read in about a tenth of a second, so a door beside you lands within one period.
            // The far pass costs a full window read and only has to catch things you are not
            // standing next to. Both ride the same per-frame budget as every other read; marking
            // cells dirty is cheap and they keep answering with what they last knew until the
            // reader gets to them.
            float nowRr = Time.realtimeSinceStartup;
            if (nowRr - _nearRefreshAt >= NearRefreshSec)
            {
                _nearRefreshAt = nowRr;
                Invalidate(new Vector3i(Mathf.FloorToInt(_ear.x), Mathf.FloorToInt(_ear.y),
                                        Mathf.FloorToInt(_ear.z)), NearRefreshBlocks);
                NearRefreshes++;
            }
            // ONLY START A FULL SWEEP ONCE THE LAST ONE FINISHED. A full window is ~560k block
            // reads and takes about eleven seconds at the per-frame budget, so a ten-second timer
            // re-dirtied everything before the reader had got round it: the field spent the entire
            // session in "reading N%" and never once completed. Harmless to correctness -- a dirty
            // cell keeps answering with what it last knew -- but it is a treadmill, and it made the
            // status line useless for telling a warm-up from a stall.
            if (nowRr - _farRefreshAt >= FarRefreshSec && CellsPending <= 0)
            {
                _farRefreshAt = nowRr;
                Invalidate(new Vector3i(Mathf.FloorToInt(_ear.x), Mathf.FloorToInt(_ear.y),
                                        Mathf.FloorToInt(_ear.z)), _range);
                FarRefreshes++;
            }

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            try
            {
                while (NativeField.cmm_fld_next_pending(_f, out int cx, out int cy, out int cz) != 0)
                {
                    FillCell(world, cx, cy, cz);
                    if ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * toMs >= budgetMs) break;
                }
            }
            catch (Exception e) { CMMConfig.D($"[CMM] field read failed: {e.Message}"); }
        }

        // One coarse cell, from the blocks underneath it. Reads StepXZ*StepY*StepXZ blocks — 9 at
        // 3x1x3 — and derives six numbers: how much gets through each of its three negative faces,
        // and what stands across its interior on each axis (which is what stops a route cutting
        // through the middle of a wall thicker than one cell).
        private static readonly float[] _cellScratch = new float[6];
        private static void FillCell(World world, int cx, int cy, int cz)
        {
            CellNumbers(world, cx, cy, cz, _cellScratch);
            NativeField.cmm_fld_set_cell(_f, cx, cy, cz,
                _cellScratch[0], _cellScratch[1], _cellScratch[2],
                _cellScratch[3], _cellScratch[4], _cellScratch[5]);
        }

        // The six numbers themselves, split out so the ray dump below reports exactly what the
        // reader stores rather than a second copy of the arithmetic that could drift from it.
        private static void CellNumbers(World world, int cx, int cy, int cz, float[] o)
        {
            int bx = cx * StepXZ, by = cy * StepY, bz = cz * StepXZ;
            int hx = StepXZ / 2, hy = StepY / 2, hz = StepXZ / 2;
            float sumX = 0f, maxX = 0f, sumY = 0f, maxY = 0f, sumZ = 0f, maxZ = 0f;
            float mSumX = 0f, mMaxX = 0f, mSumY = 0f, mMaxY = 0f, mSumZ = 0f, mMaxZ = 0f;
            // Each plane has its OWN sub-block count now that the cell is not a cube: the X and Z
            // faces are StepY x StepXZ, the Y face is StepXZ x StepXZ. Dividing all six by one
            // number was correct only while the cell was cubic.
            float nFaceXZ = StepY * StepXZ;      // the -X and -Z faces, and their middle planes
            float nFaceY  = StepXZ * StepXZ;     // the -Y face, and its middle plane

            for (int dy = 0; dy < StepY; dy++)
                for (int dz = 0; dz < StepXZ; dz++)
                    for (int dx = 0; dx < StepXZ; dx++)
                    {
                        int x = bx + dx, y = by + dy, z = bz + dz;
                        float t = Transmission(world, x, y, z);
                        // The cell's own boundary layer stands for the face it shares with the
                        // neighbour below it on that axis. Using only this cell's blocks halves
                        // the reads: whichever cell contains the wall is the one that reports it.
                        if (dx == 0) { sumX += t; if (t > maxX) maxX = t; }
                        if (dy == 0) { sumY += t; if (t > maxY) maxY = t; }
                        if (dz == 0) { sumZ += t; if (t > maxZ) maxZ = t; }
                        // ...and the MIDDLE plane perpendicular to each axis: what stands across a
                        // route passing through, which catches a wall sitting inside a cell rather
                        // than on its edge. Directional, because a floor blocks vertical travel
                        // through a cell and not horizontal.
                        if (dx == hx) { mSumX += t; if (t > mMaxX) mMaxX = t; }
                        if (dy == hy) { mSumY += t; if (t > mMaxY) mMaxY = t; }
                        if (dz == hz) { mSumZ += t; if (t > mMaxZ) mMaxZ = t; }
                    }

            o[0] = Face(sumX / nFaceXZ, maxX);
            o[1] = Face(sumY / nFaceY,  maxY);
            o[2] = Face(sumZ / nFaceXZ, maxZ);
            // A MIDDLE PLANE ONLY EXISTS IF THE CELL IS AT LEAST 2 BLOCKS DEEP ON THAT AXIS. At
            // depth 1 the cell IS its own face — there is nothing between the two faces to catch —
            // and charging one anyway would double the cost of every move along that axis. With
            // StepY = 1 that is the Y axis, so vertical travel is charged by the faces alone.
            o[3] = StepXZ >= 2 ? Face(mSumX / nFaceXZ, mMaxX) : 1f;
            o[4] = StepY  >= 2 ? Face(mSumY / nFaceY,  mMaxY) : 1f;
            o[5] = StepXZ >= 2 ? Face(mSumZ / nFaceXZ, mMaxZ) : 1f;
        }

        // HOW MUCH GETS THROUGH A CELL FACE: the AREA FRACTION. Decided by measurement, not by
        // argument (test_field.c), and re-decided when the cell stopped being a cube:
        //
        //                doorway   1-block hole   gap    straddle penalty
        //     MEAN         1.41        2.91      +1.50        none
        //     MAX          0.30        0.30       0.00        none   <- a wall becomes a rumour
        //     BLEND        0.91        1.66      +0.75        none
        //
        // This was BLEND while cells were cubic, because MEAN then cost 4.2 dB on a doorway that
        // straddled a cell boundary. That penalty came from a TALL cell splitting an opening
        // vertically, and a one-block-tall cell cannot do it — so the only argument against MEAN
        // went away with the shape, and MEAN now wins outright on the case that decides this.
        //
        // What the shape change COST, recorded because a fix's price is easier to forget than its
        // benefit: a face is 1x3 sub-blocks instead of 3x3, so a single-block hole is a third of
        // it rather than a ninth, and a pinhole can no longer be separated from a doorway as
        // sharply. The ordering holds and a sealed wall is still 41 dB. `best` stays in the
        // signature because this rule is chosen by measurement and has now changed twice.
        private static float Face(float mean, float best)
        {
            if (mean <= 0f) return 0f;
            if (mean >= 1f) return 1f;      // fully open stays free at any exponent
            float p = CMMConfig.WorldEmitterFieldAperturePow;
            return p == 1f ? mean : Mathf.Pow(mean, p);
        }

        // One block's transmission as a linear energy fraction. CellLoss is the shared block reader
        // — per-instance shapes, door state, damage, water, terrain density — so this asks the
        // game's own answer for what a block IS, and only decides what it is WORTH.
        //
        // Worth is fitted to the shipped model rather than borrowed from the native one. Borrowing
        // was the obvious move and it was wrong: that curve is calibrated to a spec band about
        // DOORS (4-9 dB), so a wood wall came out at 6 dB against the 20 dB the model people
        // actually like. Two models agreeing on a constant is worth nothing if the constant is
        // measuring different things.
        private static float Transmission(World world, int x, int y, int z)
        {
            BlockValue bv = world.GetBlock(x, y, z);
            float loss = EmitterAcoustics.DebugCellLoss(bv, world, x, y, z);
            if (loss <= 0.0001f) return 1f;
            float db = (float)(CMMConfig.WorldEmitterFieldWallDb
                             * Math.Pow(loss, CMMConfig.WorldEmitterFieldWallPow));
            if (db <= 0f) return 1f;
            if (db > 120f) db = 120f;
            return Mathf.Pow(10f, -db / 10f);
        }

        // ── Scheduler thread: the flood ─────────────────────────────────────────────────────────
        // Pure arithmetic over the native arena; touches no game API, which is what makes it safe
        // off the main thread. A flood that races a recentre is discarded by the native side and
        // simply repeats on the next tick.
        internal static void SchedulerTick()
        {
            if (!EmitterAudioLayer.FieldActive || !_earValid) return;
            IntPtr f = System.Threading.Volatile.Read(ref _f);
            if (f == IntPtr.Zero) return;
            System.Threading.Interlocked.Exchange(ref _flooding, 1);
            try
            {
                // Re-read AFTER taking the flag: Reset() clears _f first and only then waits on
                // the flag, so this is the check that makes the pair safe rather than merely
                // narrow. Without it the pointer could be freed between the read above and here.
                if (System.Threading.Volatile.Read(ref _f) != f) return;
                NativeField.cmm_fld_set_tuning(f, CMMConfig.WorldEmitterFieldDetourDb, 60f);
                Vector3 e = _ear;
                if (NativeField.cmm_fld_solve(f, e.x, e.y, e.z) < 0) Discarded++;
            }
            catch (Exception ex) { CMMConfig.D($"[CMM] field flood failed: {ex.Message}"); }
            finally { System.Threading.Interlocked.Exchange(ref _flooding, 0); }
        }

        // Every cell needs reading again: a knob that changes what a BLOCK is worth invalidates
        // all of the stored geometry, not a region of it. Costs one window refill, amortised at
        // the usual per-frame budget, so the field stays usable (and conservative) while it runs.
        internal static void RereadAll()
        {
            if (_f == IntPtr.Zero || !_earValid) return;
            try
            {
                NativeField.cmm_fld_dirty(_f, Mathf.FloorToInt(_ear.x), Mathf.FloorToInt(_ear.y),
                                          Mathf.FloorToInt(_ear.z), _range * 2 + 64);
            }
            catch { }
        }

        // A door swung, a wall came down, a chunk arrived: mark those cells for re-reading. Cheap —
        // it queues work, it never touches the field. The next flood picks up whatever has been
        // re-read by then, which is why there is no "settling" state to get wrong.
        internal static void Invalidate(Vector3i pos, int radiusBlocks)
        {
            if (_f == IntPtr.Zero) return;
            try { NativeField.cmm_fld_dirty(_f, pos.x, pos.y, pos.z, radiusBlocks); }
            catch { /* a failed invalidation costs freshness, never correctness */ }
        }

        // ── Publishing ──────────────────────────────────────────────────────────────────────────
        // Fill ptGain with the extra virtual distance that reproduces the field's occlusion at each
        // speaker point. The field carries EXCESS attenuation only — what the building adds over a
        // clear line — so this transports occlusion and nothing else; distance stays the mixer's
        // job, which already has both positions. That separation is what the previous bridge got
        // wrong, and it is why its numbers could never be tuned.
        internal static bool ComputeOcclusion(Vector3 ear, float[] pts, int ptCount, float[] ptGain)
        {
            if (_f == IntPtr.Zero || pts == null || ptGain == null || ptCount <= 0) return false;
            if (Reached <= 0) return false;                      // no flood has landed yet

            SweepSlew(Time.realtimeSinceStartup);
            float mn = CMMConfig.WorldEmitterMinDistance;
            float mx = CMMConfig.WorldEmitterRange;
            float fexp = CMMConfig.WorldEmitterFalloff;
            if (mx - mn <= 0.01f || fexp <= 0.01f) return false;

            for (int k = 0; k < ptCount; k++)
            {
                Vector3 sp = new Vector3(pts[k * 3], pts[k * 3 + 1], pts[k * 3 + 2]);
                float excessDb = NativeField.cmm_fld_sample_db(_f, sp.x, sp.y, sp.z);
                if (excessDb < 0f) excessDb = 0f;

                // SLEW IT, IN dB, BEFORE THE INVERSION.
                //
                // Each flood is an independent answer, and walking past a doorway flips the route
                // from "straight through the opening" to "the long way round" between one flood
                // and the next. Both answers are correct; the STEP between them is not something
                // any real room does. Measured while walking: 2.7 -> 10.7 dB in a quarter second.
                //
                // This has to happen here, in dB, and not in the blocks-per-second limiter
                // downstream: extra distance and loudness are not proportional, so a limit
                // expressed in blocks is loose next to a speaker and tight far from one. That
                // limiter was set to 50 blk/s -- 12.5 blocks per publish at 4 Hz -- which is to
                // say it was passing the very jumps it was there to catch.
                {
                    var key = new Vector3i(Mathf.FloorToInt(sp.x), Mathf.FloorToInt(sp.y),
                                           Mathf.FloorToInt(sp.z));
                    // A DOOR IS NOT NOISE. The limiter exists for the model changing its mind
                    // between floods; a door is the world changing, the player did it on purpose
                    // and is listening for the result, so it goes through at full rate.
                    float slew = EmitterAudioLayer.DoorMovingOrJustMoved
                               ? 0f : CMMConfig.WorldEmitterFieldSlewDb;
                    float now = Time.realtimeSinceStartup;
                    if (slew > 0f && _slew.TryGetValue(key, out Slewed s) && now - s.At < 2f)
                    {
                        // A gap longer than a flood interval is a speaker that went out of range
                        // and came back; clamp the step so it does not teleport on return.
                        float step = slew * Mathf.Clamp(now - s.At, 0.02f, 1f);
                        if (excessDb > s.Db + step) excessDb = s.Db + step;
                        else if (excessDb < s.Db - step) excessDb = s.Db - step;
                    }
                    _slew[key] = new Slewed { Db = excessDb, At = now };
                }

                // Invert the mixer's own falloff so the published extra reproduces this attenuation
                // exactly, whatever the curve's shape: att = ((mx-dEff)/span)^fexp.
                double amp = Math.Pow(10.0, -excessDb / 20.0);
                if (amp > 1.0) amp = 1.0;
                if (amp < 1e-6) amp = 1e-6;
                float d = Vector3.Distance(ear, sp);
                float head = mx - d;
                float extra = head > 0f ? (float)(head * (1.0 - Math.Pow(amp, 1.0 / fexp))) : 0f;
                ptGain[k] = extra > 0f ? extra : 0f;

                // NOT `k == 0 || d < _dbgDist`. ComputeOcclusion is called once PER STATION, so
                // forcing an update on each station's first point meant the last station processed
                // always won and the line described an arbitrary speaker rather than the nearest.
                // With three stations playing it reported one 39 blocks away while the listener
                // stood beside two others — and since `ray` follows this point, the route dump was
                // aimed at the wrong speaker too. Resetting on report is what makes a plain
                // minimum correct here.
                if (d < _dbgDist)
                {
                    _dbgDist = d; _dbgExcess = excessDb; _dbgExtra = ptGain[k];
                    // Split it. A speaker quiet because of walls and one quiet because the route
                    // wandered look identical in the total and want opposite fixes.
                    _dbgMat = NativeField.cmm_fld_sample_mat_db(_f, sp.x, sp.y, sp.z);
                    _dbgPt = sp; _dbgPtValid = true;   // so `ray` can aim at THIS one
                }
            }
            return true;
        }

        // Per-point slew state. Keyed by the speaker point's block, the same key the downstream
        // limiter uses, so a speaker that is destroyed and rebuilt starts clean rather than
        // inheriting a stale level.
        private struct Slewed { public float Db; public float At; }
        private static readonly System.Collections.Generic.Dictionary<Vector3i, Slewed> _slew =
            new System.Collections.Generic.Dictionary<Vector3i, Slewed>();
        private static readonly System.Collections.Generic.List<Vector3i> _slewDrop =
            new System.Collections.Generic.List<Vector3i>();
        private static float _slewSweptAt;

        // Drop points nothing has published for a while. Runs at most every 10 s and reuses its
        // scratch list, because this is on the 4 Hz publish path and the resolve path's allocation
        // budget is already the thing being watched.
        private static void SweepSlew(float now)
        {
            if (now - _slewSweptAt < 10f) return;
            _slewSweptAt = now;
            _slewDrop.Clear();
            foreach (var kv in _slew) if (now - kv.Value.At > 10f) _slewDrop.Add(kv.Key);
            for (int i = 0; i < _slewDrop.Count; i++) _slew.Remove(_slewDrop[i]);
        }

        private static float _dbgDist = 1e9f, _dbgExcess, _dbgExtra, _dbgMat;

        // WHERE the last reported nearest point was. Kept separately from _dbgDist because that
        // one resets on report; this must survive between the 1 Hz line and someone typing a
        // command after reading it. Without it `ray` aimed at the nearest SPEAKER, which in a POI
        // is the one three blocks away that already sounds right — so the first ray dumped three
        // cells of the working case while the 39-block speaker being complained about went
        // unexamined. The diagnostic and the command have to be talking about the same object.
        private static Vector3 _dbgPt;
        private static bool _dbgPtValid;
        internal static bool TryGetReportedPoint(out Vector3 p)
        {
            p = _dbgPt;
            return _dbgPtValid;
        }

        // Nearest published point, reset ON REPORT rather than per station — the exact bug that
        // made the previous model's diagnostic describe a speaker ninety blocks away for a week.
        internal static string DescribeNearest()
        {
            if (_dbgDist > 1e8f) return "no point";
            string s = $"d={_dbgDist:0.0} excess={_dbgExcess:0.0}dB (wall {_dbgMat:0.0}, detour {Math.Max(0f, _dbgExcess - _dbgMat):0.0})"
                     + $" extra={_dbgExtra:0.0}blk  {_dbgExcess / Math.Max(_dbgDist, 0.1f):0.00}dB/blk";
            _dbgDist = 1e9f;
            return s;
        }

        // ── cmm acoustics ray ───────────────────────────────────────────────────────────────────
        // Walk the STRAIGHT line from the ear to a speaker and print, cell by cell, what the field
        // charges there and what geometry it read to justify it.
        //
        // This exists because the total is unfalsifiable on its own. "Nine decibels of excess with
        // a clear view" is consistent with a wall the reader invented, with a route that went the
        // long way round, and with the model being right and the speaker not being where I think
        // it is. Those want three different fixes, and every previous round of this was me picking
        // one by argument. The columns separate them:
        //
        //   ROUTED climbing faster than IDEAL between two cells ⇒ the route is not this line.
        //   wall appearing at a cell whose six numbers are all 0.0 dB ⇒ charged somewhere else.
        //   a non-zero face in open air ⇒ CellLoss called something solid that a player sees through.
        internal static string DescribeRay(World world, Vector3 ear, Vector3 spk)
        {
            if (_f == IntPtr.Zero) return "no field";
            if (Reached <= 0) return "field has not flooded yet";

            float per = CMMConfig.WorldEmitterFieldDetourDb;
            var o = new float[6];
            var sb = new System.Text.StringBuilder();
            float total = Vector3.Distance(ear, spk);
            sb.Append("ray  ear ").Append(ear.ToCultureInvariantString())
              .Append(" -> speaker ").Append(spk.ToCultureInvariantString())
              .Append("   d=").Append(total.ToString("0.0"))
              .Append("   detour ").Append(per.ToString("0.00")).Append(" dB/blk")
              .Append("\n   dist  routed   ideal   excess    wall  |  -X   -Y   -Z   midX midY midZ  (dB loss)");

            int steps = Mathf.Clamp(Mathf.CeilToInt(total / StepY * 2f), 1, 160);
            var seen = new Vector3i(int.MinValue, int.MinValue, int.MinValue);
            for (int i = 0; i <= steps; i++)
            {
                Vector3 sp = Vector3.Lerp(ear, spk, i / (float)steps);
                var c = new Vector3i(Mathf.FloorToInt(sp.x / StepXZ), Mathf.FloorToInt(sp.y / StepY),
                                     Mathf.FloorToInt(sp.z / StepXZ));
                if (c == seen) continue;
                seen = c;

                // Sample at the CELL CENTRE, not at the ray point: the ray point interpolates
                // across up to eight cells and would blur the very step being looked for.
                Vector3 mid = new Vector3((c.x + 0.5f) * StepXZ, (c.y + 0.5f) * StepY,
                                          (c.z + 0.5f) * StepXZ);
                float d = Vector3.Distance(ear, mid);
                float ex = NativeField.cmm_fld_sample_db(_f, mid.x, mid.y, mid.z);
                float mat = NativeField.cmm_fld_sample_mat_db(_f, mid.x, mid.y, mid.z);
                CellNumbers(world, c.x, c.y, c.z, o);

                sb.Append("\n  ").Append(d.ToString("0.0").PadLeft(5))
                  .Append((ex + d * per).ToString("0.0").PadLeft(8))
                  .Append((d * per).ToString("0.0").PadLeft(8))
                  .Append(ex.ToString("0.0").PadLeft(9))
                  .Append(mat.ToString("0.0").PadLeft(8)).Append("  | ");
                for (int k = 0; k < 6; k++)
                {
                    float db = o[k] <= 1e-12f ? 99f : -10f * Mathf.Log10(o[k]);
                    sb.Append((db < 0.05f ? "  . " : db.ToString("0.0").PadLeft(4)));
                    if (k == 2) sb.Append(' ');
                }
            }
            return sb.ToString();
        }
    }
}
#endif
