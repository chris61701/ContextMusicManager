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

        // The scene is built on the SCHEDULER thread and read on the decision thread, so the swap
        // and every read are serialised. Reads are microseconds (a graph solve per speaker), so
        // holding the gate across a publish costs nothing; the 310 ms BUILD happens outside it,
        // against a scene nobody can see yet, and only the swap is inside.
        static readonly object _gate = new object();
        static AcousticScene _scene;
        static Vector3 _sceneCentre;
        static Vector3 _wantCentre;
        static bool _wantValid;
        // Bumped every time the world's blocks change under us. A build carries the generation it
        // started from, so a build that raced a door swing can be recognised and dropped.
        static long _geomGen;
        static readonly Dictionary<Vector3i, AcousticSource> _sources = new Dictionary<Vector3i, AcousticSource>();
        // Reused, never per-call: an allocation here would land on the decision tick every publish.
        static readonly NativeAcoustics.Arrival[] _arrivals = new NativeAcoustics.Arrival[32];

        internal static int SaturatedPoints;   // how many points clamped last publish
        internal static long LastBuildMs;
        internal static string LastStatus = "not built";

        internal static bool Ready { get { lock (_gate) return _scene != null && _scene.Valid; } }

        // Drop everything. Native memory is invisible to the GC, so this must be reachable from
        // every teardown path -- world unload, mod disable, a failed rebuild.
        internal static void Reset()
        {
            lock (_gate)
            {
                foreach (var s in _sources.Values) s?.Dispose();
                _sources.Clear();
                _scene?.Dispose();
                _scene = null;
                LastStatus = "reset";
            }
        }

        // Called from the decision tick: cheap, just records where the listener is. The actual
        // build is the scheduler's job -- a 310 ms voxel walk has no business on the frame loop,
        // and a door swing must never be able to stutter the game.
        internal static void NoteListener(Vector3 ear)
        {
            _wantCentre = ear;
            _wantValid = true;
        }

        // Per-frame door edge watcher. Called with the live "is any door mid-swing" flag; the scene
        // is dropped on the FALLING edge, once the swing has settled and the world's blocks are
        // final. Watching the edge rather than the flag means one rebuild per swing instead of one
        // per frame of it, and the shipped model covers the ~0.5 s until the rebuild lands.
        static bool _doorSeen;
        internal static void WatchDoors(bool animating)
        {
            if (animating) { _doorSeen = true; return; }
            if (!_doorSeen) return;
            _doorSeen = false;
            InvalidateGeometry();
        }

        // Registered with CmmScheduler. Builds the new scene OUTSIDE the gate, then swaps under it,
        // so readers never touch a half-built scene and never block for the build.
        internal static void SchedulerTick()
        {
            if (!CMMConfig.WorldEmitterNativeAcoustics || !_wantValid) return;
            if (!NativeAcoustics.Available) return;

            Vector3 want = _wantCentre;
            bool need;
            long gen;
            lock (_gate)
            {
                gen = _geomGen;
                need = _scene == null || !_scene.Valid
                    || (want - _sceneCentre).sqrMagnitude >= RecentreDist * RecentreDist;
            }
            if (!need) return;

            World world = GameManager.Instance?.World;
            if (world == null) return;

            int cx = Mathf.FloorToInt(want.x), cy = Mathf.FloorToInt(want.y), cz = Mathf.FloorToInt(want.z);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            AcousticScene built = null;
            try
            {
                built = AcousticScene.Build(world, cx - HalfX, cy - Below, cz - HalfZ,
                                            HalfX * 2 + 1, Below + Above + 1, HalfZ * 2 + 1);
            }
            catch (Exception e)
            {
                CMMConfig.D($"[CMM] acoustics scene build threw: {e.Message}");
            }
            sw.Stop();
            LastBuildMs = sw.ElapsedMilliseconds;

            lock (_gate)
            {
                if (built == null) { LastStatus = $"build failed ({LastBuildMs} ms)"; return; }
                // Geometry changed WHILE this was building (a door settled mid-walk), so the scene
                // just built already describes a world that no longer exists. Throw it away rather
                // than install it: a stale scene is silent and permanent, where a discarded one
                // costs 310 ms and is rebuilt on the very next tick.
                if (gen != _geomGen)
                {
                    built.Dispose();
                    LastStatus = $"discarded stale build ({LastBuildMs} ms)";
                    return;
                }
                // Re-centring invalidates every source: they were solved against geometry this
                // scene no longer describes.
                foreach (var s in _sources.Values) s?.Dispose();
                _sources.Clear();
                _scene?.Dispose();
                _scene = built;
                _sceneCentre = want;
                LastStatus = $"rooms {_scene.Rooms}, nodes {_scene.Nodes}, edges {_scene.Edges}, "
                           + $"{_scene.ArenaBytes / (1024 * 1024)} MB, {LastBuildMs} ms";
            }
        }

        // Readiness only -- this NEVER builds. It used to, inline on the decision tick, which put a
        // 310 ms voxel walk on the frame path; the build now belongs to SchedulerTick. Returns false
        // until a scene exists, and the caller keeps using the shipped model until then.
        internal static bool EnsureScene(World world, Vector3 ear)
        {
            NoteListener(ear);
            lock (_gate) return _scene != null && _scene.Valid;
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
            if (pts == null || ptGain == null || ptCount <= 0) return false;
            lock (_gate)
            {
            if (_scene == null || !_scene.Valid) return false;

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
        }

        // Human-readable breakdown at one position: the total, and every arrival that makes it up.
        // Reported per KIND because that is what makes a wrong sound diagnosable by ear -- "the
        // barrier component is 20 dB too loud" is actionable where "it sounds off" is not.
        internal static string DescribeAt(Vector3 ear)
        {
            lock (_gate)
            {
                if (_scene == null || !_scene.Valid) return "no scene";
                if (_sources.Count == 0) return "scene ready, no speakers solved yet";
                var sb = new System.Text.StringBuilder();
                sb.Append("scene: ").Append(LastStatus);
                foreach (var kv in _sources)
                {
                    var src = kv.Value;
                    if (src == null || !src.Valid) continue;
                    int n = src.Evaluate(ear, _arrivals);
                    double tot = src.LevelDb(ear);
                    sb.Append("\n  speaker ").Append(kv.Key).Append("  total ").Append(tot.ToString("0.0")).Append(" dB");
                    double[] byKind = new double[4];
                    int[] cnt = new int[4];
                    for (int i = 0; i < n; i++)
                    {
                        int k = _arrivals[i].kind;
                        if (k < 0 || k > 3) continue;
                        byKind[k] += _arrivals[i].weight * Math.Pow(10.0, _arrivals[i].db / 10.0);
                        cnt[k]++;
                    }
                    string[] nm = { "direct", "barrier", "aperture", "diffuse" };
                    for (int k = 0; k < 4; k++)
                        if (cnt[k] > 0)
                            sb.Append("\n    ").Append(nm[k]).Append(" x").Append(cnt[k]).Append("  ")
                              .Append((10.0 * Math.Log10(byKind[k])).ToString("0.0")).Append(" dB");
                }
                return sb.ToString();
            }
        }

        // The listener has moved; speakers have not. Nothing to re-solve -- which is the point of
        // the scene/source split, and why this is a no-op rather than a rebuild.
        internal static void OnListenerMoved(Vector3 ear) { }

        // A speaker moved (vehicle-mounted, later enemies). Graph-sized and allocation-free.
        internal static void MoveSource(Vector3 from, Vector3 to)
        {
            lock (_gate)
            {
            var key = new Vector3i(Mathf.FloorToInt(from.x), Mathf.FloorToInt(from.y), Mathf.FloorToInt(from.z));
            if (!_sources.TryGetValue(key, out var s) || s == null) return;
            if (s.Move(to))
            {
                _sources.Remove(key);
                _sources[new Vector3i(Mathf.FloorToInt(to.x), Mathf.FloorToInt(to.y), Mathf.FloorToInt(to.z))] = s;
            }
            }
        }

        // Geometry changed inside the scene box -- a door swung, a wall broke. The scene has to be
        // rebuilt and every source re-solved against it; dropping the scene forces that on the next
        // EnsureScene, which runs off the frame loop.
        internal static void InvalidateGeometry()
        {
            lock (_gate)
            {
                foreach (var s in _sources.Values) s?.Dispose();
                _sources.Clear();
                _scene?.Dispose();
                _scene = null;
                _geomGen++;
                LastStatus = "invalidated";
            }
        }
    }
}
