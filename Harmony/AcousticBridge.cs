using System;
using System.Collections.Generic;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────
    // AcousticBridge — the new model, speaking the audio layer's existing language
    // ─────────────────────────────────────────────────────────────────────────────
    // EmitterAudioLayer publishes one number per speaker: PtGain, an EXTRA VIRTUAL DISTANCE in
    // blocks, which the native mixer then feeds through its own falloff curve. The new model
    // produces a level in dB instead, and that difference is the whole reason the old system
    // could not be tuned -- a fixed number of "extra blocks" is worth a different number of
    // decibels depending on where you stand, so every constant expressed in blocks meant
    // something different at every distance.
    //
    // Rather than rewrite the audio path to carry dB, this INVERTS the mixer's curve. The curve is
    //
    //     att = ((mx - dEff) / span)^fexp,   dEff = d + extra,   span = mx - mn
    //
    // so for a target amplitude A the extra that produces it exactly is
    //
    //     extra = mx - span * A^(1/fexp) - d
    //
    // Invert it correctly and the output gain equals the model's gain regardless of the curve's
    // shape: the curve stops being a model and becomes a lossless channel. That is what makes
    // this a drop-in -- no change to the mixer, no change to the point protocol, and the shipped
    // model stays one config flag away for an A/B.
    //
    // The one place the transport is lossy is SATURATION. The curve reaches silence at mx, so a
    // target quieter than the curve can express at that distance clamps. That is exactly the
    // failure mode the shipped model had -- its anti-silence ceiling landed precisely on the
    // silence threshold -- so it is reported rather than hidden.
    // ─────────────────────────────────────────────────────────────────────────────
    internal static class AcousticBridge
    {
        // How far around the listener a scene covers. Big enough to hold a POI and the ground
        // under it; small enough that a rebuild stays affordable. The largest real capture is
        // 112x87x112 and costs ~310 ms, which is why this runs off the frame loop.
        const int HalfX = 48, HalfZ = 48, Below = 12, Above = 28;

        // Rebuild once the listener has walked this far from the scene's centre. The scene is
        // source-independent, so it does NOT need rebuilding when a speaker moves -- only when
        // the listener leaves the box the geometry was captured over.
        const float RecentreDist = 24f;

        static AcousticScene _scene;
        static Vector3 _sceneCentre;
        static readonly Dictionary<Vector3i, AcousticSource> _sources = new Dictionary<Vector3i, AcousticSource>();
        static NativeAcoustics.Arrival[] _arrivals = new NativeAcoustics.Arrival[32];

        internal static int SaturatedPoints;   // how many points clamped last publish
        internal static long LastBuildMs;
        internal static string LastStatus = "not built";

        internal static bool Ready => _scene != null && _scene.Valid;

        // Drop everything. Native memory is invisible to the GC, so this must be reachable from
        // every teardown path -- world unload, mod disable, a failed rebuild.
        internal static void Reset()
        {
            foreach (var s in _sources.Values) s?.Dispose();
            _sources.Clear();
            _scene?.Dispose();
            _scene = null;
            LastStatus = "reset";
        }

        // Build or re-centre the scene around the listener. Returns false if the native side is
        // unavailable, in which case the caller keeps using the shipped model.
        internal static bool EnsureScene(World world, Vector3 ear)
        {
            if (!NativeAcoustics.Available || world == null) return false;
            if (_scene != null && _scene.Valid && (ear - _sceneCentre).sqrMagnitude < RecentreDist * RecentreDist)
                return true;

            // Re-centring invalidates every source: they were solved against geometry that this
            // scene no longer describes.
            foreach (var s in _sources.Values) s?.Dispose();
            _sources.Clear();
            _scene?.Dispose();
            _scene = null;

            int cx = Mathf.FloorToInt(ear.x), cy = Mathf.FloorToInt(ear.y), cz = Mathf.FloorToInt(ear.z);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _scene = AcousticScene.Build(world, cx - HalfX, cy - Below, cz - HalfZ,
                                         HalfX * 2 + 1, Below + Above + 1, HalfZ * 2 + 1);
            sw.Stop();
            LastBuildMs = sw.ElapsedMilliseconds;
            if (_scene == null) { LastStatus = "build failed"; return false; }
            _sceneCentre = ear;
            LastStatus = $"rooms {_scene.Rooms}, nodes {_scene.Nodes}, edges {_scene.Edges}, "
                       + $"{_scene.ArenaBytes / (1024 * 1024)} MB, {LastBuildMs} ms";
            return true;
        }

        static AcousticSource SourceAt(Vector3 p)
        {
            var key = new Vector3i(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
            if (_sources.TryGetValue(key, out var s) && s != null && s.Valid) return s;
            s = _scene?.CreateSource(p);
            if (s != null) _sources[key] = s;
            return s;
        }

        // Fill ptGain with the extra virtual distance that reproduces the model's level at each
        // speaker. Mirrors EmitterAcoustics.ComputeOcclusion's signature so the call site is a
        // one-line swap. Returns false if anything is missing, so the caller falls back.
        internal static bool ComputeOcclusion(Vector3 ear, float[] pts, int ptCount, float[] ptGain, World world)
        {
            if (!Ready || pts == null || ptGain == null || ptCount <= 0) return false;

            float mn = CMMConfig.WorldEmitterMinDistance;
            float mx = CMMConfig.WorldEmitterRange;
            float fexp = CMMConfig.WorldEmitterFalloff;
            float span = mx - mn;
            if (span <= 0.01f || fexp <= 0.01f) return false;

            SaturatedPoints = 0;
            for (int k = 0; k < ptCount; k++)
            {
                var sp = new Vector3(pts[k * 3], pts[k * 3 + 1], pts[k * 3 + 2]);
                var src = SourceAt(sp);
                if (src == null) { ptGain[k] = 0f; continue; }

                double db = src.LevelDb(ear);
                double amp = Math.Pow(10.0, db / 20.0);          // dB is a power ratio; att is amplitude
                if (amp > 1.0) amp = 1.0;
                if (amp < 1e-6) { amp = 1e-6; SaturatedPoints++; }

                float d = Vector3.Distance(ear, sp);
                float dEff = (float)(mx - span * Math.Pow(amp, 1.0 / fexp));
                float extra = dEff - d;
                // Never negative: the model can only take level away relative to a clear line at
                // this distance, and a negative extra would make an occluded speaker LOUDER than
                // an unoccluded one at the same range.
                ptGain[k] = extra > 0f ? extra : 0f;
            }
            return true;
        }

        // The listener has moved; speakers have not. Nothing to re-solve -- which is the point of
        // the scene/source split, and why this is a no-op rather than a rebuild.
        internal static void OnListenerMoved(Vector3 ear) { }

        // A speaker moved (vehicle-mounted, later enemies). Graph-sized and allocation-free.
        internal static void MoveSource(Vector3 from, Vector3 to)
        {
            var key = new Vector3i(Mathf.FloorToInt(from.x), Mathf.FloorToInt(from.y), Mathf.FloorToInt(from.z));
            if (!_sources.TryGetValue(key, out var s) || s == null) return;
            if (s.Move(to))
            {
                _sources.Remove(key);
                _sources[new Vector3i(Mathf.FloorToInt(to.x), Mathf.FloorToInt(to.y), Mathf.FloorToInt(to.z))] = s;
            }
        }

        // Geometry changed inside the scene box -- a door swung, a wall broke. The scene has to be
        // rebuilt and every source re-solved against it; dropping the scene forces that on the next
        // EnsureScene, which runs off the frame loop.
        internal static void InvalidateGeometry()
        {
            foreach (var s in _sources.Values) s?.Dispose();
            _sources.Clear();
            _scene?.Dispose();
            _scene = null;
            LastStatus = "invalidated";
        }
    }
}
