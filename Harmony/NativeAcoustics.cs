using System;
using System.Runtime.InteropServices;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────
    // NativeAcoustics — P/Invoke surface for the world-emitter acoustic model
    // ─────────────────────────────────────────────────────────────────────────────
    // The model lives in CMM_Decoder.dll (cmm_acoustics.c), ported stage by stage from
    // tools/CmmAcousticsJudge and verified against it: final level at a lattice of standable
    // positions agrees to 0.02 dB mean, 0.077 dB worst across three real captures. It is
    // native for one reason — the solve touches millions of voxels per rebuild and hundreds
    // of graph nodes per source, and Unity's non-generational GC cannot absorb that.
    //
    // TWO OBJECTS, AND THE SPLIT IS LOAD-BEARING:
    //
    //   Scene   geometry only. Voxels, openness, thickness, regions, portals, rooms,
    //           absorption. Knows nothing about any speaker. Rebuilt only when the WORLD
    //           changes -- a door swings, a wall breaks, a chunk loads. ~310 ms on the
    //           largest POI captured, so it belongs on CmmScheduler and never on the frame
    //           loop.
    //   Source  one speaker bound to a scene. The routed solve from its position.
    //           GRAPH-sized, not voxel-sized: 0.05-0.20 ms, and Move() ALLOCATES NOTHING.
    //
    // That second property is deliberate and was designed in before the remaining stages were
    // written, because vehicle-mounted speakers are a planned feature and the same engine is
    // intended for enemies later. N moving sources cost N graph solves per tick and zero
    // allocation; the scene they share is untouched. The SHIPPED model cannot be given this --
    // its flood is anchored to the LISTENER and rebuilt as the listener moves, so its cost
    // grows with movement rather than with geometry.
    //
    // Threading: a scene is single-writer. Build it on the scheduler thread while no source
    // solve is running against it; evaluate read-only from anywhere after.
    // ─────────────────────────────────────────────────────────────────────────────
    internal static class NativeAcoustics
    {
        const string Dll = "CMM_Decoder";

        // Voxel flags, matching the acsweep dump so the uploader passes them through unchanged.
        internal const uint AIR = 1u;

        // The ABI this build expects. Checked on first use so a stale DLL fails loudly with a
        // number rather than crashing somewhere inside the solve.
        internal const int ExpectedAbi = 3;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_ac_abi();

        // Live tuning (ABI 2). These are SCENE inputs -- material loss feeds the room coupling
        // and the calibration feeds the room fields, both computed at build time -- so a change
        // only takes full effect after a rebuild. A non-positive argument leaves that parameter
        // alone, so one knob can be turned without disturbing the others.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_ac_set_tuning(double dbMat, double matPow, double diffuseCal);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_ac_get_tuning(out double dbMat, out double matPow, out double diffuseCal);

        // The model's free-field level at a distance -- the REFERENCE eval_db is divided by, so
        // the bridge transports occlusion rather than an absolute level carrying the model's own
        // (nearly flat) distance law. Native-side so the two can never drift apart.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double cmm_ac_freefield_db(double dist);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_ac_create(int sx, int sy, int sz, int x0, int y0, int z0);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_ac_destroy(IntPtr ac);

        // loss/flags are sx*sy*sz, indexed x + sx*(z + sz*y) — the dump's own layout, kept so
        // nothing needs reindexing anywhere. Copied in, so the caller's buffers are reusable.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_ac_set_voxels(IntPtr ac, float[] loss, byte[] flags);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_ac_build_fields(IntPtr ac);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_ac_build_segments(IntPtr ac);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long cmm_ac_stat(IntPtr ac, int what);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern float cmm_ac_open_at(IntPtr ac, int wx, int wy, int wz);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_ac_src_create(IntPtr scene, float x, float y, float z);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_ac_src_destroy(IntPtr src);

        // Re-solves in place for a new position. Allocation-free: this is the call a moving
        // speaker makes every tick it moves.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_ac_src_move(IntPtr src, float x, float y, float z);

        // After a scene rebuild every source bound to it is stale; this re-solves, growing the
        // source's arena only if the graph actually got bigger.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_ac_src_rebind(IntPtr src);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_ac_eval(IntPtr src, float lx, float ly, float lz,
                                               [Out] Arrival[] outArr, int maxOut);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double cmm_ac_eval_db(IntPtr src, float lx, float ly, float lz);

        // One arrival: a distinct path from the speaker to the ear. Every field is load-bearing,
        // and discarding any one of them has produced an audible defect at some point:
        //   db       the obvious one
        //   px/py/pz where it radiates FROM -- the opening or the barrier patch, NOT the speaker.
        //            Putting transmitted sound at the speaker is the single most audible error
        //            this model can make: through a floor it reads as a point source sweeping
        //            underfoot where a whole ceiling should be radiating down.
        //   fc       a wall is a FILTER, not a fader, and this is the ear's primary occlusion cue
        //   len      path length; delay is len/343, and its absence is why caves never echoed
        //   weight   its share, so a path counted once is not counted twice
        //   kind     0 direct, 1 through a barrier, 2 through an opening, 3 the diffuse field
        [StructLayout(LayoutKind.Sequential)]
        internal struct Arrival
        {
            internal float db;
            internal float px, py, pz;
            internal float fc;
            internal float len;
            internal float weight;
            internal int kind;
        }

        internal const int KindDirect = 0, KindBarrier = 1, KindAperture = 2, KindDiffuse = 3;

        static int _abi = -1;
        static bool _abiBad;

        // Verified once, then cached. Returns false if the DLL is missing or mismatched, so
        // callers can fall back to the shipped model rather than throwing per frame.
        internal static bool Available
        {
            get
            {
                if (_abiBad) return false;
                if (_abi >= 0) return _abi == ExpectedAbi;
                try { _abi = cmm_ac_abi(); }
                catch (Exception e)
                {
                    _abiBad = true;
                    CMMConfig.D($"[CMM] acoustics native unavailable: {e.Message}");
                    return false;
                }
                if (_abi != ExpectedAbi)
                {
                    _abiBad = true;
                    CMMConfig.D($"[CMM] acoustics ABI mismatch: dll {_abi}, expected {ExpectedAbi}");
                    return false;
                }
                return true;
            }
        }
    }
}
