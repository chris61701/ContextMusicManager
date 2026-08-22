// Gated to 3.0+ (GV_V3): it fills its cells from EmitterAcoustics.CellLoss, on the same gate.
#if GV_V3
using System;
using System.Runtime.InteropServices;

namespace CMM
{
    // P/Invoke surface for cmm_field — ONE occlusion field, centred on the listener, read by every
    // source. See CMM_Decoder/cmm_field.h for the design; the short version is that there is only
    // ever one pair of ears and there may be many things making noise, some of them moving, so
    // nothing is computed per source.
    internal static class NativeField
    {
        private const string Dll = "CMM_Decoder";

        // Checked on first use so a stale DLL fails with a number rather than inside the flood.
        internal const int ExpectedAbi = 4;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_fld_abi();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_fld_create(int nx, int ny, int nz,
                                                    int stepX, int stepY, int stepZ);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_fld_destroy(IntPtr f);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_fld_recentre(IntPtr f, int bx, int by, int bz);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_fld_next_pending(IntPtr f, out int cx, out int cy, out int cz);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        // Six HALF-VOXELS: the material in the cell's -X/-Y/-Z half and in its +X/+Y/+Z half,
        // as linear transmissions. See cmm_field.h — the pair on an axis must compose back to the
        // cell's own material or every wall in the world doubles.
        internal static extern void cmm_fld_set_cell(IntPtr f, int cx, int cy, int cz,
                                                     float tnx, float tny, float tnz,
                                                     float tpx, float tpy, float tpz);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_fld_dirty(IntPtr f, int bx, int by, int bz, int radiusBlocks);

        // Returns cells reached, or -1 if a recentre landed mid-flood and the result was discarded.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_fld_solve(IntPtr f, float lx, float ly, float lz);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern float cmm_fld_sample_db(IntPtr f, float x, float y, float z);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern float cmm_fld_sample_db_blended(IntPtr f, float x, float y, float z, float mix);

        // The MATERIAL half of the excess: walls, as opposed to the route going the long way.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern float cmm_fld_sample_mat_db(IntPtr f, float x, float y, float z);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_fld_set_tuning(IntPtr f, float dbPerBlock, float maxDb);

        // 0 cells, 1 pending, 2 reached last flood, 3 last flood microseconds.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long cmm_fld_stat(IntPtr f, int what);

        private static int _available = -1;
        internal static bool Available
        {
            get
            {
                if (_available >= 0) return _available == 1;
                try
                {
                    int abi = cmm_fld_abi();
                    _available = abi == ExpectedAbi ? 1 : 0;
                    if (_available == 0)
                        Log.Warning($"[CMM] cmm_field ABI {abi}, expected {ExpectedAbi} — field occlusion off.");
                }
                catch (Exception e)
                {
                    _available = 0;
                    CMMConfig.D($"[CMM] cmm_field unavailable: {e.Message}");
                }
                return _available == 1;
            }
        }
    }
}
#endif
