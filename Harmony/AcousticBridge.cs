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
        // MUST COVER WHAT IT IS ASKED ABOUT. This was +/-48 blocks around the listener while the
        // emitter range is 100, so any speaker past 48 blocks sat OUTSIDE the scene entirely -- and
        // nothing checked. The native solve happily returns a bare free-field arrival for a source
        // it has no geometry for, which reads as ZERO OCCLUSION, so up to half the audible speakers
        // were unoccludable purely on distance regardless of what stood between. Caught from the
        // in-game log, every line identical:
        //
        //     ac nearest: d=87.8 model=-38.9 free=-38.9 OCCL=0.0 extra=0.0 [dir -38.9]
        //
        // One lone arrival, model exactly equal to free field. That is the shape of a scene that
        // does not contain the speaker, and it is what "the occlusion is all wonky" was.
        //
        // +/-72 rather than the full 100: the cost is voxel-cubed and a full-range box is 1.66M
        // voxels (~39 MB, ~650 ms). 72 covers the great majority of audible speakers at 1.03M
        // (~22 MB), and anything still outside now falls back to the shipped model instead of being
        // published as unoccluded -- see the bounds check in ComputeOcclusion.
        const int HalfX = 72, HalfZ = 72, Below = 16, Above = 32;

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

        internal static int SaturatedPoints;   // clamped at the QUIET end last publish
        internal static int GainPoints;        // clamped at UNITY -- the model reported gain
        internal static int PublishedPoints;   // denominator: gain/pts is the share switched off

        // Nearest-point diagnostics, refreshed every publish and read by the 1 Hz log line.
        static float _dbgNearest = 1e9f;
        static double _dbgRaw, _dbgFree, _dbgOccl;
        static int _dbgN;
        static readonly double[] _dbgKind = new double[4];
        static float _dbgExtra;

        internal static string DescribeNearest()
        {
            if (_dbgNearest > 1e8f) return "no point";
            string[] nm = { "dir", "bar", "ap", "diff" };
            var sb = new System.Text.StringBuilder();
            sb.Append("d=").Append(_dbgNearest.ToString("0.0"))
              .Append(" model=").Append(_dbgRaw.ToString("0.0"))
              .Append(" free=").Append(_dbgFree.ToString("0.0"))
              .Append(" OCCL=").Append(_dbgOccl.ToString("0.0"))
              .Append(" extra=").Append(_dbgExtra.ToString("0.0"))
              .Append(" [");
            for (int k = 0; k < 4; k++)
                if (_dbgKind[k] > 0)
                    sb.Append(nm[k]).Append(' ')
                      .Append((10.0 * Math.Log10(_dbgKind[k])).ToString("0.0")).Append(' ');
            return sb.Append(']').ToString();
        }
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
                // Bump the generation as well: an in-flight read or a filled box describes a world
                // this reset just said we are no longer in (unload, mod disable, model switched off).
                _geomGen++;
                _filled = null;
                _wantValid = false;
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

        // ── The build, split across two threads ──────────────────────────────────────────────
        // MAIN thread fills a VoxelBox from the world, a slab per frame under a budget. SCHEDULER
        // thread turns a filled box into a scene. The split is not an optimisation: reading the
        // game's blocks off-thread races the chunk loader, and that failure is a crash rather than
        // an exception, so the read cannot leave the main thread. Everything after it touches only
        // our own memory and has no business ON the main thread.
        const float ReadBudgetMs = 1.5f;
        static VoxelBox _box;              // main thread owns this while filling
        static VoxelBox _filled;           // handed to the scheduler under _gate
        static bool _consuming;            // scheduler is reading the shared buffers right now
        static Vector3 _boxCentre;         // where the in-flight read is centred
        static long _boxGen;               // the geometry generation the read started from

        // Per frame, main thread. Decides when a read is needed and advances it.
        internal static void FrameStep(World world)
        {
            if (!CMMConfig.WorldEmitterNativeAcoustics || !NativeAcoustics.Available) return;
            if (world == null || !_wantValid) return;

            if (_box == null)
            {
                bool need;
                lock (_gate)
                    need = _filled == null && !_consuming
                        && (_stale || _scene == null || !_scene.Valid
                            || (_wantCentre - _sceneCentre).sqrMagnitude >= RecentreDist * RecentreDist);
                if (!need) return;

                int sx = HalfX * 2 + 1, sy = Below + Above + 1, sz = HalfZ * 2 + 1;
                if (_reusable == null || _reusable.Sx != sx || _reusable.Sy != sy || _reusable.Sz != sz)
                    _reusable = new VoxelBox(sx, sy, sz);
                _box = _reusable;
                _boxCentre = _wantCentre;
                lock (_gate) _boxGen = _geomGen;
                _box.Restart(Mathf.FloorToInt(_boxCentre.x) - HalfX,
                             Mathf.FloorToInt(_boxCentre.y) - Below,
                             Mathf.FloorToInt(_boxCentre.z) - HalfZ);
                LastStatus = "reading world 0%";
                return;
            }

            // Geometry changed under an in-flight read: what has been read already describes a
            // world that no longer exists, so start over rather than build from a mixture.
            lock (_gate)
                if (_boxGen != _geomGen) { _box = null; return; }

            if (!_box.Step(world, ReadBudgetMs))
            {
                LastStatus = $"reading world {_box.ProgressPct:0}%";
                return;
            }
            lock (_gate) { _filled = _box; }
            _box = null;
            LastStatus = "read complete, building";
        }
        static VoxelBox _reusable;

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
            if (!CMMConfig.WorldEmitterNativeAcoustics) return;
            if (!NativeAcoustics.Available) return;

            VoxelBox box;
            long gen;
            Vector3 want;
            lock (_gate)
            {
                box = _filled;
                _filled = null;
                gen = _boxGen;
                want = _boxCentre;
                if (box != null) _consuming = true;   // the buffers are REUSED: no new read until done
            }
            if (box == null) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            AcousticScene built = null;
            try
            {
                built = AcousticScene.FromVoxels(box);
            }
            catch (Exception e)
            {
                CMMConfig.D($"[CMM] acoustics scene build threw: {e.Message}");
            }
            sw.Stop();
            LastBuildMs = sw.ElapsedMilliseconds;

            lock (_gate)
            {
                _consuming = false;
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
                _stale = false;          // the swap is what clears it, never the request
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
            GainPoints = 0;
            PublishedPoints = ptCount;
            _dbgNearest = 1e9f;
            for (int k = 0; k < ptCount; k++)
            {
                var sp = new Vector3(pts[k * 3], pts[k * 3 + 1], pts[k * 3 + 2]);
                // A speaker outside the scene has no geometry to be occluded BY, and the native
                // solve does not refuse it -- it returns free field, which publishes as "nothing in
                // the way". Declining the whole station instead hands it to the shipped model, which
                // does attenuate at range. Never publish an answer about geometry we do not hold.
                if (!_scene.Contains(sp, 2f)) return false;
                var src = SourceAt(sp);
                if (src == null) { ptGain[k] = 0f; continue; }

                // ── TRANSPORT OCCLUSION, NOT AN ABSOLUTE LEVEL ───────────────────────────────
                // This took the model's absolute level and solved for the distance that produces
                // it, which quietly handed the model's DISTANCE LAW to the audio layer as well.
                // The model's law is SLOPE = 0.35 dB/block -- about 2 dB over 8 blocks, nearly
                // flat -- so the game's own tuned falloff was replaced by one that barely falls.
                // Heard in game as "the speaker sounds far away no matter how close I get to it",
                // and it is also why the wall knob did nothing: a level pinned near the top of the
                // range by a flat law inverts to extra = 0 whatever the material does.
                //
                // Divide by the model's own free field at the same distance and the distance law
                // cancels on both sides. What is left is the only thing the model genuinely knows
                // -- how much the GEOMETRY took away relative to a clear line at that range -- and
                // the game's curve goes back to doing distance, which it was always better at.
                //
                //   want:  curve(d + extra) = curve(d) * A
                //   so:    extra = (mx - d) * (1 - A^(1/fexp))
                //
                // which is >= 0 for any A <= 1 and cannot saturate: full occlusion lands exactly
                // on the curve's silence point instead of overshooting it.
                float d = Vector3.Distance(ear, sp);
                double db = src.LevelDb(ear) - NativeAcoustics.cmm_ac_freefield_db(d);
                double amp = Math.Pow(10.0, db / 20.0);          // dB is a power ratio; att is amplitude
                // Both clamps counted. The loud side is the one that mattered and was invisible:
                // a model above unity has no attenuation that reproduces it, so it published FULLY
                // UNOCCLUDED and no tuning could reach the point.
                if (amp > 1.0) { amp = 1.0; GainPoints++; }
                if (amp < 1e-6) { amp = 1e-6; SaturatedPoints++; }

                // WHAT IS ACTUALLY PUBLISHED, for the NEAREST point, once a second. Every diagnosis
                // in this subsystem that turned out to be wrong was wrong because the offline scene
                // was not the player's scene: an empty test room around a speaker cannot show what a
                // shelf, a wall behind it and a ceiling do to the transmission disc. This is the one
                // number that was never looked at in game, and it is cheap.
                if (d < _dbgNearest)
                {
                    _dbgNearest = d;
                    _dbgRaw = src.LevelDb(ear);
                    _dbgFree = NativeAcoustics.cmm_ac_freefield_db(d);
                    _dbgOccl = db;
                    _dbgN = src.Evaluate(ear, _arrivals);
                    _dbgKind[0] = _dbgKind[1] = _dbgKind[2] = _dbgKind[3] = 0.0;
                    for (int a = 0; a < _dbgN; a++)
                    {
                        int kk = _arrivals[a].kind;
                        if (kk >= 0 && kk < 4)
                            _dbgKind[kk] += _arrivals[a].weight * Math.Pow(10.0, _arrivals[a].db / 10.0);
                    }
                }

                float head = mx - d;                              // room left on the curve
                float extra = head > 0f ? (float)(head * (1.0 - Math.Pow(amp, 1.0 / fexp))) : 0f;
                ptGain[k] = extra > 0f ? extra : 0f;
                if (d <= _dbgNearest) _dbgExtra = ptGain[k];
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
        // rebuilt and every source re-solved against it.
        //
        // STALE BUT STILL SERVING, rather than dropped. Dropping the scene means the native path
        // has nothing to answer with, so it hands back to the SHIPPED model for the second or so
        // the rebuild takes -- and that handback is audible. It made every A/B dishonest: changing
        // a tuning value produced a clear change in the sound that was just the other model
        // passing through, identical whatever number was typed, which is exactly what the author
        // reported ("does the exact same thing it did the last two launches"). Confirmed in the
        // log: 'why=warming' for one tick after each command, then 'why=native' again.
        //
        // A one-second-old scene is a far better answer than a different model. The rebuild is
        // started, the old scene keeps serving until the new one is ready, and the swap is silent.
        internal static void InvalidateGeometry()
        {
            lock (_gate)
            {
                _geomGen++;       // in-flight reads and filled boxes describe the pre-change world
                _filled = null;
                _stale = true;    // ... and this is what makes FrameStep start a fresh one
                LastStatus = "stale, rebuilding (still serving the previous scene)";
            }
        }
        static bool _stale;

        // Drop it outright. Only for teardown, where there IS no correct answer to keep serving.
        internal static void DropScene()
        {
            lock (_gate)
            {
                foreach (var s in _sources.Values) s?.Dispose();
                _sources.Clear();
                _scene?.Dispose();
                _scene = null;
                _geomGen++;
                _filled = null;
                _stale = false;
                LastStatus = "dropped";
            }
        }
    }
}
