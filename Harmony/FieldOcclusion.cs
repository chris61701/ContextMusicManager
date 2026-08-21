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
        // Blocks per cell. The whole affordability of "rebuild it, don't patch it" rests on this:
        // 3 blocks is ~34k cells over a 128-block span where 1 block would be 2M. Measured, a
        // flood of that size is 7.3 ms. The cost is that an opening straddling a cell boundary
        // reads part-open — pinned by a test in test_field.c at about 2.7 dB.
        private const int Step = 3;
        private const int BelowBlocks = 24, AboveBlocks = 24;

        private static IntPtr _f = IntPtr.Zero;
        private static int _nx, _ny, _nz, _range;
        private static Vector3 _ear;              // captured on the main thread, read by the flood
        private static bool _earValid;

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
            return $"cells {tot} ({Step}-block, {_nx}x{_ny}x{_nz}), "
                 + (pend > 0 ? $"reading {100.0 * (tot - pend) / Math.Max(tot, 1):0}%, " : "read complete, ")
                 + $"reached {Reached}, flood {LastSolveUs / 1000.0:0.0} ms, {Discarded} discarded";
        }

        // ── Lifetime ────────────────────────────────────────────────────────────────────────────
        private static bool Ensure()
        {
            if (!NativeField.Available) return false;
            int range = Mathf.Clamp(Mathf.CeilToInt(CMMConfig.WorldEmitterRange) + 4, 24, 128);
            if (_f != IntPtr.Zero && range == _range) return true;
            Reset();
            _range = range;
            // Odd counts so the listener's cell sits at the centre of the window.
            _nx = _nz = (range + Step - 1) / Step * 2 + 1;
            _ny = (BelowBlocks + AboveBlocks) / Step + 1;
            _f = NativeField.cmm_fld_create(_nx, _ny, _nz, Step);
            if (_f == IntPtr.Zero) { Log.Warning("[CMM] field occlusion: create failed."); return false; }
            NativeField.cmm_fld_set_tuning(_f, CMMConfig.WorldEmitterFieldDetourDb, 60f);
            CMMConfig.D($"[CMM] field occlusion: {_nx}x{_ny}x{_nz} cells of {Step} blocks "
                      + $"(+/-{range} blocks), {(long)_nx * _ny * _nz} total");
            return true;
        }

        internal static void Reset()
        {
            if (_f != IntPtr.Zero) { NativeField.cmm_fld_destroy(_f); _f = IntPtr.Zero; }
            _earValid = false; Discarded = 0;
        }

        // ── Main thread: keep the window on the player and read the world into it ───────────────
        // Called per frame from the emitter layer's existing budgeted slot. The recentre is cheap
        // (it only marks what scrolled in as pending), so it can happen every frame and the window
        // never lags the player.
        internal static void FrameStep(EntityPlayerLocal player, World world, float budgetMs)
        {
            if (!CMMConfig.WorldEmitterFieldOcclusion || player == null || world == null) return;
            if (!Ensure()) return;

            _ear = EmitterAudioLayer.ListenerWorldPos(player);
            _earValid = true;
            NativeField.cmm_fld_recentre(_f, Mathf.FloorToInt(_ear.x),
                                             Mathf.FloorToInt(_ear.y), Mathf.FloorToInt(_ear.z));

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

        // One coarse cell, from the blocks underneath it. Reads Step^3 blocks — 27 at Step 3 — and
        // derives four numbers: how much gets through each of its three negative faces, and how
        // solid its interior is (which is what stops a route cutting through the middle of a wall
        // thicker than one cell).
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
            int bx = cx * Step, by = cy * Step, bz = cz * Step;
            int h = Step / 2;
            float sumX = 0f, maxX = 0f, sumY = 0f, maxY = 0f, sumZ = 0f, maxZ = 0f;
            float mSumX = 0f, mMaxX = 0f, mSumY = 0f, mMaxY = 0f, mSumZ = 0f, mMaxZ = 0f;
            int face = Step * Step;

            for (int dy = 0; dy < Step; dy++)
                for (int dz = 0; dz < Step; dz++)
                    for (int dx = 0; dx < Step; dx++)
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
                        // route passing through, which is what catches a wall sitting inside a cell
                        // rather than on its edge. Directional, because a floor blocks vertical
                        // travel through a cell and not horizontal — charging it for both made the
                        // open air outdoors read 3.2 dB down for no reason a player could see.
                        if (dx == h) { mSumX += t; if (t > mMaxX) mMaxX = t; }
                        if (dy == h) { mSumY += t; if (t > mMaxY) mMaxY = t; }
                        if (dz == h) { mSumZ += t; if (t > mMaxZ) mMaxZ = t; }
                    }

            o[0] = Face(sumX / face, maxX);   o[1] = Face(sumY / face, maxY);   o[2] = Face(sumZ / face, maxZ);
            o[3] = Face(mSumX / face, mMaxX); o[4] = Face(mSumY / face, mMaxY); o[5] = Face(mSumZ / face, mMaxZ);
        }

        // HOW MUCH GETS THROUGH A CELL FACE. The geometric mean of the area fraction and the best
        // single crossing — decided by measurement, not by argument (test_field.c):
        //
        //   MEAN   grid-dependent: the same doorway costs 4.2 dB more when it straddles a boundary
        //   MAX    a single 1-block hole reads as 3.0 dB, i.e. very nearly a doorway — a wall
        //          becomes a rumour, which is the failure mode that matters most here
        //   BLEND  straddle penalty 2.7 dB, pinhole 6.1 dB: passes both
        //
        // The argument for MAX is real — a route crosses a face at one point, so the best crossing
        // is the physical answer for a shortest-path model — but it is the argument that loses to
        // the pinhole measurement, and this project has paid enough for arguments that beat
        // measurements.
        private static float Face(float mean, float best)
        {
            float v = mean * best;
            return v <= 0f ? 0f : Mathf.Sqrt(v);
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
            if (!CMMConfig.WorldEmitterFieldOcclusion || _f == IntPtr.Zero || !_earValid) return;
            try
            {
                NativeField.cmm_fld_set_tuning(_f, CMMConfig.WorldEmitterFieldDetourDb, 60f);
                Vector3 e = _ear;
                if (NativeField.cmm_fld_solve(_f, e.x, e.y, e.z) < 0) Discarded++;
            }
            catch (Exception ex) { CMMConfig.D($"[CMM] field flood failed: {ex.Message}"); }
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

            float mn = CMMConfig.WorldEmitterMinDistance;
            float mx = CMMConfig.WorldEmitterRange;
            float fexp = CMMConfig.WorldEmitterFalloff;
            if (mx - mn <= 0.01f || fexp <= 0.01f) return false;

            for (int k = 0; k < ptCount; k++)
            {
                Vector3 sp = new Vector3(pts[k * 3], pts[k * 3 + 1], pts[k * 3 + 2]);
                float excessDb = NativeField.cmm_fld_sample_db(_f, sp.x, sp.y, sp.z);
                if (excessDb < 0f) excessDb = 0f;

                // Invert the mixer's own falloff so the published extra reproduces this attenuation
                // exactly, whatever the curve's shape: att = ((mx-dEff)/span)^fexp.
                double amp = Math.Pow(10.0, -excessDb / 20.0);
                if (amp > 1.0) amp = 1.0;
                if (amp < 1e-6) amp = 1e-6;
                float d = Vector3.Distance(ear, sp);
                float head = mx - d;
                float extra = head > 0f ? (float)(head * (1.0 - Math.Pow(amp, 1.0 / fexp))) : 0f;
                ptGain[k] = extra > 0f ? extra : 0f;

                if (k == 0 || d < _dbgDist)
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

            int steps = Mathf.Clamp(Mathf.CeilToInt(total / Step * 2f), 1, 96);
            var seen = new Vector3i(int.MinValue, int.MinValue, int.MinValue);
            for (int i = 0; i <= steps; i++)
            {
                Vector3 sp = Vector3.Lerp(ear, spk, i / (float)steps);
                var c = new Vector3i(Mathf.FloorToInt(sp.x / Step), Mathf.FloorToInt(sp.y / Step),
                                     Mathf.FloorToInt(sp.z / Step));
                if (c == seen) continue;
                seen = c;

                // Sample at the CELL CENTRE, not at the ray point: the ray point interpolates
                // across up to eight cells and would blur the very step being looked for.
                Vector3 mid = new Vector3((c.x + 0.5f) * Step, (c.y + 0.5f) * Step, (c.z + 0.5f) * Step);
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
