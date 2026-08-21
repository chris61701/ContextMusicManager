// Gated to 3.0+ (GV_V3): it reads geometry through EmitterAcoustics and publishes to EmitterAudioLayer, both on the same gate.
#if GV_V3
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
        // SIZED FROM THE EMITTER RANGE, not from a guess. +/-48 then +/-72 were both guesses, and
        // both left speakers outside the scene -- at which point the bounds check below correctly
        // declines and the station falls back to the shipped model, so the new model simply never
        // runs for a distant speaker. Caught from the log: "ac nearest: no point" on every line
        // while pts=4, i.e. points were being published, by the OTHER model.
        //
        // The box has to cover what the audio layer will ask about, and that is the configured
        // range. At the default 100 that is 201 x 49 x 201 = 1.98M voxels: ~42 MB of native arena,
        // ~10 MB of managed read buffer, ~0.8 s to build on the scheduler and ~4 s to read at
        // 1.5 ms/frame -- during which the PREVIOUS scene keeps serving, so the cost is latency and
        // never a gap. Capped at 100 so a user who sets a huge range gets a large-but-bounded scene
        // rather than a gigabyte.
        static int HalfX => Mathf.Clamp(Mathf.CeilToInt(CMMConfig.WorldEmitterRange) + 4, 48, 100);
        static int HalfZ => HalfX;
        const int Below = 16, Above = 32;

        // Rebuild once the listener has walked this far from the scene's centre. The scene is
        // source-independent, so it does NOT need rebuilding when a speaker moves -- only when
        // the listener leaves the box the geometry was captured over.
        // Scaled to the box. At +/-48 a 24-block walk was a sensible third of the extent; at +/-100
        // it would rebuild a 1.98M-voxel scene every 24 blocks for no benefit, since the box still
        // covers everything the audio layer can ask about. A speaker that does fall outside between
        // rebuilds is handled by the bounds check, not by rebuilding pre-emptively.
        static float RecentreDist => HalfX * 0.45f;

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
        static bool _dbgReported = true;   // set by the 1 Hz report; next publish starts a fresh tally
        static double _dbgRaw, _dbgFree, _dbgOccl;
        static int _dbgN;
        static readonly double[] _dbgKind = new double[4];
        static double _dbgDomDb; static int _dbgDomKind;
        static float _dbgDomX, _dbgDomY, _dbgDomZ, _dbgDomLen;
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
            sb.Append(']');
            if (_dbgDomKind >= 0)
            {
                string[] kn = { "direct", "barrier", "opening", "diffuse" };
                sb.Append("  loudest=").Append(kn[_dbgDomKind < 4 ? _dbgDomKind : 0])
                  .Append(' ').Append(_dbgDomDb.ToString("0.0")).Append("dB from ")
                  .Append(_dbgDomX.ToString("0")).Append(',').Append(_dbgDomY.ToString("0"))
                  .Append(',').Append(_dbgDomZ.ToString("0"))
                  .Append(" pathlen ").Append(_dbgDomLen.ToString("0"));
            }
            _dbgNearest = 1e9f;   // consumed: start a fresh minimum for the next reporting interval
            _dbgReported = true;  // ...and a fresh clamp tally, over ALL stations, not just the last
            return sb.ToString();
        }
        internal static long LastBuildMs;
        internal static string LastStatus = "not built";
        static long _prevRooms = -1, _prevEdges = -1;   // previous build, for the reproducibility Δ

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
            // NEVER DISCARD AN IN-FLIGHT READ. This used to restart from scratch whenever the world
            // changed under it, which is correct in spirit and catastrophic in practice: the read is
            // ~2M voxels and takes about seven seconds, and every door swing bumps the generation.
            // In a house with doors the read therefore NEVER FINISHED again after the first one --
            // straight from the log:
            //
            //     reading world 17% .. 93% -> read complete -> scene live (7 ticks)
            //     reading world 5% -> 12% -> 19% -> 31% -> 5% -> 0% -> 8% -> 3% -> 5% -> 3% ...
            //
            // so the scene stayed frozen at the FIRST build for the rest of the session. Reported
            // as "it sounded like it was working initially, but after moving around it ceased
            // functioning as it did", which is exactly what a frozen scene sounds like.
            //
            // Let it finish instead. A read that spans a door swing has ONE voxel from the old
            // world in it -- a door -- and a scene that is one door stale is incomparably better
            // than a scene that is minutes stale. The generation is still tracked, so if it moved
            // while we were reading, the swap notices and immediately starts another pass: forward
            // progress is guaranteed because completing is no longer conditional on the world
            // holding still.

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
        static readonly List<Vector3i> _doorPositions = new List<Vector3i>();   // reused, never per-frame
        internal static void WatchDoors(bool animating)
        {
            if (animating)
            {
                _doorSeen = true;
                // Remember WHERE, while it is still animating: on the falling edge the door is gone
                // from the animating set and its position is no longer available. A door is one
                // block, and knowing which one turns a ~2M-voxel re-read into a 125-voxel patch.
                EmitterAcoustics.CollectAnimatingDoors(_doorPositions);
                if (_doorPositions.Count > 64) _doorPositions.RemoveRange(0, _doorPositions.Count - 64);
                return;
            }
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
                // Geometry moved while this was being read or built. INSTALL IT ANYWAY -- it is at
                // most a door out of date and it is the best answer available -- then leave _stale
                // set so the next FrameStep starts a fresh pass. Discarding here was the other half
                // of the never-finishes trap: the read completed and the result was thrown away.
                bool wasStale = gen != _geomGen;
                // Re-centring invalidates every source: they were solved against geometry this
                // scene no longer describes.
                foreach (var s in _sources.Values) s?.Dispose();
                _sources.Clear();
                _scene?.Dispose();
                _scene = built;
                _sceneCentre = want;
                // Cleared by the swap, not by the request -- and left SET if the world moved under
                // this pass, so another one starts immediately.
                _stale = wasStale;
                if (!wasStale) _geomLiveAt = Time.realtimeSinceStartup;   // this scene is current
                // REPRODUCIBILITY METER. The aperture count is the sharpest summary of whether two
                // builds saw the same world, and it is what caught the real defect: standing still,
                // the graph went 614 edges -> 428 edges between rebuilds and the reverberant field
                // at the ear moved 7 dB with it, because unresident chunks read as open sky. If Δ is
                // anything but small after a plain rebuild of unchanged geometry, the read is still
                // unstable and no amount of tuning downstream will hold the level still.
                long dRooms = _scene.Rooms - _prevRooms, dEdges = _scene.Edges - _prevEdges;
                string delta = _prevEdges < 0 ? "" : $"  Δrooms {dRooms:+#;-#;0} Δedges {dEdges:+#;-#;0}";
                _prevRooms = _scene.Rooms; _prevEdges = _scene.Edges;
                LastStatus = $"rooms {_scene.Rooms}, nodes {_scene.Nodes}, edges {_scene.Edges}, "
                           + $"{_scene.ArenaBytes / (1024 * 1024)} MB, {LastBuildMs} ms{delta}";
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

            // ACCUMULATE ACROSS STATIONS, RESET ON REPORT -- the same bug d17b746 fixed for
            // _dbgNearest, still live in the clamp counters sitting right beside it. Zeroed per
            // STATION, "ac gain=/sat=/pts=" described only the LAST station published, so a nearest
            // speaker clamped at unity read as gain=0 and the passivity meter said all-clear through
            // windows where it was firing. A diagnostic that reports the wrong subject is worse than
            // none: it is the thing that makes you rule out the actual cause.
            SaturatedPoints = _dbgReported ? 0 : SaturatedPoints;
            GainPoints      = _dbgReported ? 0 : GainPoints;
            PublishedPoints = _dbgReported ? ptCount : PublishedPoints + ptCount;
            _dbgReported = false;
            // _dbgNearest is deliberately NOT reset here. ComputeOcclusion runs once PER STATION,
            // so resetting per call made the log report whichever station happened to be processed
            // LAST rather than the nearest speaker -- and with several stations live, that was
            // reliably a distant one. Every reading in every log was 85-96 blocks, including sessions
            // where the author was standing beside a speaker reporting that it sounded wrong. A week
            // of diagnosis ran on the wrong speaker. It is reset when the line is PRINTED, so it
            // reports the nearest point seen across all stations since the last report.
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
                    // WHERE the loudest arrival radiates from, and which kind it is. The kind totals
                    // say "an opening is carrying it"; they cannot say WHICH opening, and that is the
                    // difference between "sound is coming round the doorway as it should" and "the
                    // graph found a hole in the floor". Guessing between those has cost several
                    // rounds already.
                    _dbgDomDb = -999.0; _dbgDomKind = -1;
                    for (int a = 0; a < _dbgN; a++)
                        if (_arrivals[a].db > _dbgDomDb)
                        {
                            _dbgDomDb = _arrivals[a].db;
                            _dbgDomKind = _arrivals[a].kind;
                            _dbgDomX = _arrivals[a].px; _dbgDomY = _arrivals[a].py; _dbgDomZ = _arrivals[a].pz;
                            _dbgDomLen = _arrivals[a].len;
                        }
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
            // ── A DOOR IS ONE BLOCK: PATCH IT, DO NOT RE-READ THE WORLD ──────────────────────
            // A full re-read is ~2M GetBlock calls and about seven seconds, so a door took seven
            // seconds to be heard -- and while any door kept swinging inside that window the read
            // never finished at all. If we know which blocks changed, the retained buffer can be
            // corrected in place and only the native build (~0.7 s, off the frame loop) repeats.
            // Falls back to a full re-read whenever the positions are unknown or the patch misses
            // the box, so a wall coming down still works.
            // DEDUPE FIRST. CollectAnimatingDoors appends once per FRAME for the whole swing, so a
            // 0.5 s swing at 60 fps queued the same block ~30 times and this loop then re-read the
            // same 125-voxel cube 30 times -- up to 8,000 unbudgeted GetBlock calls in the single
            // frame the door lands on. The 64-entry cap also meant one long swing could evict
            // ANOTHER door's position, so that door silently never got patched at all.
            // The field re-reads those cells and picks the change up on its next flood -- no
            // patch, no staleness window, nothing to get the ordering of.
            for (int i = 0; i < _doorPositions.Count; i++) FieldOcclusion.Invalidate(_doorPositions[i], 2);

            _doorPositions.Sort((a, b) => a.x != b.x ? a.x - b.x : a.y != b.y ? a.y - b.y : a.z - b.z);
            int uniq = 0;
            for (int i = 0; i < _doorPositions.Count; i++)
                if (i == 0 || _doorPositions[i] != _doorPositions[i - 1]) _doorPositions[uniq++] = _doorPositions[i];
            if (uniq < _doorPositions.Count) _doorPositions.RemoveRange(uniq, _doorPositions.Count - uniq);

            bool patched = false;
            if (_doorPositions.Count > 0 && _reusable != null)
            {
                World w = GameManager.Instance?.World;
                if (w != null)
                {
                    // TAKE THE GATE BEFORE TOUCHING THE BUFFER, NOT AFTER. This wrote _reusable's
                    // Loss/Flags first and only then checked _consuming, so a patch landing while the
                    // scheduler was inside FromVoxels (a 0.6-0.9 s window) mutated the very arrays
                    // cmm_ac_set_voxels was reading -- and then, because `patched` stayed false, threw
                    // the applied patch away and forced a full re-read on top.
                    bool mine = false;
                    lock (_gate) { if (!_consuming) { _consuming = true; mine = true; } }
                    if (mine)
                    {
                        bool all = true;
                        for (int i = 0; i < _doorPositions.Count; i++)
                            if (!_reusable.Patch(w, _doorPositions[i], 2)) all = false;
                        lock (_gate)
                        {
                            _consuming = false;
                            // Only if the box was CURRENT before this change. If it already carried
                            // an older generation, a full re-read is pending for a reason a
                            // 125-voxel patch cannot satisfy (a wall came down, a recentre) and
                            // declaring it current here would install that stale world permanently.
                            if (all && _boxGen == _geomGen)
                            {
                                // The buffer now describes the post-change world whether or not the
                                // read has finished, so the box's generation catches up. A COMPLETE
                                // box can be built from immediately; an in-flight one simply carries
                                // the correction to the build it is already headed for -- which is
                                // what stops a door during a read from latching another read.
                                _boxGen = _geomGen;
                                patched = true;
                                if (_reusable.Complete && _box == null) { _filled = _reusable; _stale = false; }
                            }
                        }
                    }
                }
            }
            _doorPositions.Clear();
            _geomChangeAt = Time.realtimeSinceStartup;
            if (patched)
            {
                LastStatus = _reusable != null && _reusable.Complete
                           ? "door patched, rebuilding" : "door patched into the read in flight";
                return;
            }

            lock (_gate)
            {
                _geomGen++;       // in-flight reads and filled boxes describe the pre-change world
                _filled = null;
                _stale = true;    // ... and this is what makes FrameStep start a fresh one
                LastStatus = "stale, rebuilding (still serving the previous scene)";
            }
        }
        static bool _stale;

        // When geometry last changed, and when a scene reflecting it last went live. The rise
        // limiter needs both: it exempts door-driven changes from its rate cap, but its own window
        // is measured from the door TOGGLE -- which expires about a second later, before this model
        // has even finished rebuilding. So the one change the exemption exists for was the one it
        // throttled. Reported as "with the door opening and closing, there is a huge delay in when
        // sound changes".
        static float _geomChangeAt = -999f, _geomLiveAt = -999f;

        // True while a geometry change is still working its way out to the audio: from the change
        // itself until shortly after a scene containing it went live.
        internal static bool GeometrySettling
        {
            get
            {
                float now = Time.realtimeSinceStartup;
                // Settling means: the world changed and this model has not yet published a scene
                // containing that change. The first version of this was true for a quarter second
                // after the change and then again briefly after delivery -- with a GAP in between,
                // which is precisely the interval that matters, because the rebuild takes ~0.6 s.
                //
                // Bounded so a rebuild that never lands cannot wedge the audio on the shipped model
                // forever: after ANY_REBUILD_GIVEUP the model is trusted again whether or not the
                // scene arrived. A stale scene is a worse answer than the other model, but an
                // indefinite handback is a worse bug than either.
                // ...AND THE ORDER OF THESE TWO TESTS WAS THE DOOR BUG.
                // The give-up ran FIRST, so a change that takes longer than GiveUp to deliver was
                // DECLARED delivered and this model published from pre-swing geometry. GiveUp was
                // 6 s and a full world re-read is 7.5-10 s, so any door swung while a read was in
                // flight hit it every single time. Straight from the log, four separate occasions,
                // the clearest one:
                //
                //     351.594  swing ends (door OPEN)
                //     352-357  why=warming   scene=reading world 4% .. 83%   "ac nearest: no point"
                //     358.474  why=native    +6.88 s == GiveUp
                //              extra=33.8  loudest=barrier      <- publishing the SHUT door
                //     359.544  new scene lands
                //     360.584  extra=1.5                        <- the truth, two seconds late
                //
                // and the mixer's adaptive glide renders that 32-block step in about 40 ms, so it
                // arrives as a click. "I open the door, it's clear, then it sounds like it is shut."
                //
                // Delivery now WINS. The give-up survives only as a wedge-breaker -- the original
                // reasoning ("an indefinite handback is a worse bug") is right, but it has to be
                // longer than the slowest legitimate delivery or it fires on the normal case, which
                // is what happened. A full read is 7.5-10 s, so 30 s is comfortably outside it while
                // still bounding a genuine wedge. Until then the shipped model covers, and the
                // shipped model renders a door swing correctly -- that is the whole reason this
                // handback exists.
                const float GiveUp = 30f, Tail = 0.35f;
                if (_geomLiveAt < _geomChangeAt)                    // change not delivered yet
                    return now - _geomChangeAt < GiveUp;
                return now - _geomLiveAt < Tail;                   // just delivered; let it settle
            }
        }

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
#endif
