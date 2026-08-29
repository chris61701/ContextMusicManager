using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// CmmSoak — a long play session, over shipped code, with the library changing
// underneath it. See CmmSoak.csproj for what is real here and what is not.
// ─────────────────────────────────────────────────────────────────────────────

internal static class Program
{
    // ── Invariants. A violation is a bug, not a measurement. ─────────────────
    sealed class Inv
    {
        internal readonly string Id, Text;
        internal int Checks, Violations;
        internal string FirstFailure;
        internal Inv(string id, string text) { Id = id; Text = text; }
    }
    static readonly List<Inv> _inv = new List<Inv>();
    static Inv Def(string id, string text) { var i = new Inv(id, text); _inv.Add(i); return i; }

    static void Hold(Inv i, bool ok, Func<string> detail)
    {
        i.Checks++;
        if (ok) return;
        i.Violations++;
        if (i.FirstFailure == null) i.FirstFailure = detail();
    }

    static readonly Inv I1 = Def("I1", "a track gain is finite and inside every configured cap");
    static readonly Inv I2 = Def("I2", "an unmeasured track never plays louder than unity");
    static readonly Inv I3 = Def("I3", "a resolved context is one the script was allowed to pick");
    static readonly Inv I4 = Def("I4", "a selected track exists on disk at the moment it is chosen");
    static readonly Inv I5 = Def("I5", "the rotation never repeats a track while others are unplayed");
    static readonly Inv I6 = Def("I6", "the station schedule is gapless and moves only forward");
    static readonly Inv I7 = Def("I7", "two independent evaluators agree on what is on air");
    static readonly Inv I8 = Def("I8", "an emitter slot is never owned by a station that is gone");
    static readonly Inv I9 = Def("I9", "only user data is ever written to the loudness cache");
    static readonly Inv I10 = Def("I10", "no track is measured more than once per content change");

    // ── Metrics. Reported, never gated: they describe this run, not the code. ──
    static int _ticks, _contextChanges, _trackPicks, _mutations, _stationResolves, _walkMisses;
    static int _measured, _measureFailed, _shortNoLoudness;
    static readonly Dictionary<string, int> _contextTally = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int> _measureCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int> _stampCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> _events = new List<string>();

    static int Main(string[] args)
    {
        double hours = 24;
        int seed = 1;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--hours" && i + 1 < args.Length) hours = double.Parse(args[++i]);
            else if (args[i] == "--seed" && i + 1 < args.Length) seed = int.Parse(args[++i]);
        }

        string root = Path.Combine(Path.GetTempPath(), "CmmSoak_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Console.WriteLine($"CmmSoak - {hours:F0} simulated hours, seed {seed}");
        Console.WriteLine("  " + root);

        int rc = 1;
        try
        {
            Directory.CreateDirectory(root);
            rc = Run(root, hours, seed);
        }
        catch (Exception ex)
        {
            Console.WriteLine("\nFATAL: the harness threw.\n" + ex);
            rc = 1;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        return rc;
    }

    static int Run(string root, double hours, int seed)
    {
        if (!SoakWorld.LoadContextDefinitions(out string ctxFile))
        {
            Console.WriteLine("FATAL: could not read contexts.xml, so the real context set is unknown.");
            return 1;
        }
        Console.WriteLine($"  contexts: {SoakWorld.ContextNames.Length} from {ctxFile}");

        var world = new SoakWorld(root, seed);
        world.Build(tracksPerContext: 6, stations: 3, tracksPerStation: 5);

        ContextMusicManager.MusicDirectory = world.MusicDir;
        ContextMusicManager.HasFilesHook = world.HasFiles;
        CMMConfig.WorldEmitterSource = world.RadioDir;
        CMMConfig.RadioAnchorSeconds = 900f;      // short windows: many anchors inside the run
        LoudnessStore.Load(root);

        ScriptHost.Load(root);
        if (!ScriptHost.Active)
        {
            Console.WriteLine("FATAL: the decision script did not load; nothing below would be real.");
            return 1;
        }
        Console.WriteLine("  decision script: " + (ScriptHost.UsingEmbeddedDefault ? "embedded default" : "on disk"));

        // Before anything moves: does the rotation keep its promise on a POOL THAT NEVER
        // CHANGES? If it does, any violation later in the run is about churn or about this
        // harness's bookkeeping, and saying which is the whole value of asking first.
        {
            var probe = new TrackRotation();
            var pool = new[] { "a", "b", "c", "d", "e", "f" };
            var pr = new Random(99);
            var seenClean = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool clean = true; string firstBad = null;
            for (int i = 0; i < 60; i++)
            {
                string pick = probe.Select(pool, CMMConfig.TrackNoRepeatMemory, null,
                                           _ => false, _ => false, CMMConfig.FavouriteWeight,
                                           () => pr.NextDouble());
                if (pick == null) break;
                // Select CHOOSES; Commit is what puts the play in the bag. Without it the
                // rotation stays permanently empty, every track remains eligible for ever, and
                // repeats look like a broken guarantee when they are pure chance. This harness
                // made exactly that mistake, and the isolated probe is what caught it.
                probe.Commit(pick, CMMConfig.TrackNoRepeatMemory);
                if (!seenClean.Add(pick))
                {
                    if (seenClean.Count < pool.Length && firstBad == null)
                    { clean = false; firstBad = $"repeated {pick} after only {seenClean.Count} of {pool.Length}"; }
                    seenClean.Clear(); seenClean.Add(pick);
                }
            }
            Console.WriteLine("  rotation on a stable pool: " + (clean ? "keeps its promise" : "BROKEN - " + firstBad));
        }

        var rng = new Random(seed);
        var rotations = new Dictionary<string, TrackRotation>(StringComparer.OrdinalIgnoreCase);
        var slotOwner = new RadioStation[3];
        var playedSince = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var poolIdOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string current = "Ambient";
        string playing = null;
        double playingUntil = 0;
        double tick = 0.5;                        // the shipped scan cadence
        long totalTicks = (long)(hours * 3600 / tick);
        var beat = new Beat(rng);
        int hourBucket = -1;
        var hourly = new List<string>();

        for (long t = 0; t < totalTicks; t++)
        {
            double simSeconds = t * tick;
            _ticks++;

            // ── 1. The gameplay loop, through the real decision script ───────
            GameState gs = beat.Advance(simSeconds);
            List<string> resolved;
            try { resolved = ScriptHost.Resolve(gs); }
            catch (Exception ex) { resolved = null; Note($"resolve threw: {ex.Message}"); }

            string want = (resolved != null && resolved.Count > 0) ? resolved[0] : current;
            Hold(I3, Array.IndexOf(SoakWorld.ContextNames, want) >= 0 || want == "Silence" || want == "Vanilla",
                 () => $"script returned an unknown context '{want}'");

            if (!string.Equals(want, current, StringComparison.OrdinalIgnoreCase))
            {
                current = want;
                _contextChanges++;
                _contextTally.TryGetValue(current, out int had);
                _contextTally[current] = had + 1;
                playing = null;                    // a context change starts a fresh track
            }

            // A track that never ends means one selection per context change, which barely
            // touches the rotation, the loudness store or eviction. Tracks run for their real
            // decoded length -- the harness knows it because it wrote the file.
            if (playing != null && simSeconds >= playingUntil) playing = null;

            // ── 2. Track selection, through the real rotation ────────────────
            if (playing == null)
            {
                IList<string> pool = world.Files(current);
                var live = new List<string>();
                for (int i = 0; i < pool.Count; i++) if (File.Exists(pool[i])) live.Add(pool[i]);

                if (live.Count > 0)
                {
                    if (!rotations.TryGetValue(current, out TrackRotation rot))
                        rotations[current] = rot = new TrackRotation();
                    // The SHIPPED settings, not literals. See the shim's note on
                    // TrackNoRepeatMemory: a wrong constant here changes which guarantee is
                    // even being made.
                    string pick = rot.Select(live.ToArray(), CMMConfig.TrackNoRepeatMemory, null,
                                             _ => false, _ => false, CMMConfig.FavouriteWeight,
                                             () => rng.NextDouble());
                    if (pick != null)
                    {
                        _trackPicks++;
                        playing = pick;
                        rot.Commit(pick, CMMConfig.TrackNoRepeatMemory);
                        playingUntil = simSeconds + SoakWorld.LengthOf(pick);
                        Hold(I4, File.Exists(pick), () => $"picked a file that is not there: {pick}");

                        // The fallback a track plays at BEFORE anything has measured it. Checked
                        // here, while it is still unmeasured, because a moment later it will not be.
                        if (!LoudnessStore.IsMeasured(pick))
                        {
                            float pre = LoudnessStore.GetTrackGain(pick);
                            Hold(I2, pre <= 1.0f + 1e-4f,
                                 () => $"unmeasured {Path.GetFileName(pick)} would play at {pre:F3}");
                        }

                        // Everything should play before anything repeats: track what this
                        // context has played since its last refill and complain if a repeat
                        // arrives while something is still unplayed.
                        // "Everything plays once before anything repeats" is a promise about a
                        // STABLE pool. A track added or removed mid-cycle legitimately reshapes
                        // it -- that is the churn TrackRotation is built to absorb -- so the
                        // bookkeeping resets whenever the pool does, and the invariant is only
                        // asserted across a stretch where the pool did not move. Asserting it
                        // through a mutation would be asserting a promise nothing makes.
                        // The rotation carries its own cycle across a pool change, by design --
                        // that churn is what CmmRotationTest covers in detail. So when the pool
                        // moves, BOTH sides restart together: a fresh rotation and a fresh record
                        // of what it has played. Clearing only my side left the two out of step,
                        // and a correct pick near the end of the rotation's cycle then looked like
                        // an early repeat. That is the harness measuring itself, not the code.
                        string poolId = string.Join("|", live);
                        if (!poolIdOf.TryGetValue(current, out string lastPool) || lastPool != poolId)
                        {
                            poolIdOf[current] = poolId;
                            playedSince[current] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            rotations[current] = rot = new TrackRotation();
                            pick = rot.Select(live.ToArray(), CMMConfig.TrackNoRepeatMemory, null,
                                              _ => false, _ => false, CMMConfig.FavouriteWeight,
                                              () => rng.NextDouble());
                            if (pick == null) { playing = null; goto afterSelect; }
                            playing = pick;
                            rot.Commit(pick, CMMConfig.TrackNoRepeatMemory);
                        }
                        HashSet<string> seen = playedSince[current];
                        if (!seen.Add(pick))
                        {
                            Hold(I5, seen.Count >= live.Count,
                                 () => $"{current}: repeated {Path.GetFileName(pick)} with "
                                     + $"{live.Count - seen.Count} of {live.Count} still unplayed");
                            seen.Clear(); seen.Add(pick);
                        }

                        MeasureAndCheck(pick);
                    }
                    afterSelect: ;
                }
            }

            // ── 3. The radio: stations, schedule, slots ──────────────────────
            if (t % 8 == 0)                        // 4 Hz, as the emitter layer runs
            {
                UnityEngine.Time.realtimeSinceStartup = (float)simSeconds;
                var before = new Dictionary<string, RadioStation>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in RadioStationCatalog.ByName) before[kv.Key] = kv.Value;

                try { RadioStationCatalog.EnsureCatalog(); }
                catch (Exception ex) { Note($"catalog threw: {ex.Message}"); }

                // A rebuild replaced the station objects: release what is gone, exactly as
                // the emitter layer does, and assert no slot is left pointing at a ghost.
                if (!ReferenceEquals(before, null))
                {
                    EmitterSlots.ReleaseAfterRebuild(before, RadioStationCatalog.Stations, slotOwner, _ => { });
                    for (int s = 0; s < slotOwner.Length; s++)
                    {
                        RadioStation own = slotOwner[s];
                        Hold(I8, own == null || RadioStationCatalog.Stations.Contains(own),
                             () => $"slot {s} owned by '{own.Name}', which is not a live station");
                    }
                }

                // Give any unowned slot to a station, so the table is genuinely exercised.
                foreach (RadioStation st in RadioStationCatalog.Stations)
                {
                    if (st.Slot >= 0) continue;
                    int slot = EmitterSlots.FreeSlot(slotOwner, false, -1);
                    if (slot < 0) break;
                    st.Slot = slot; slotOwner[slot] = st;
                }

                CheckSchedule(rng, simSeconds);
            }

            // ── 4. The library changes underneath all of it ──────────────────
            if (t % 240 == 0 && t > 0)             // every two simulated minutes
            {
                world.Mutate(_mutations, out string what);
                _mutations++;
                if (what != null && _events.Count < 40) Note($"{Fmt(simSeconds)}  {what}");
                // A file the mutation removed may be the one playing.
                if (playing != null && !File.Exists(playing)) playing = null;
            }

            // ── 5. Persistence policy, continuously ──────────────────────────
            if (t % 400 == 0)
            {
                LoudnessStore.SaveSnapshot snap = LoudnessStore.SnapshotForSave();
                if (snap != null)
                    Hold(I9, snap.Tracks.Count == 0,
                         () => $"{snap.Tracks.Count} plain measurement(s) queued for disk with no user data");
            }

            int h = (int)(simSeconds / 3600);
            if (h != hourBucket) { hourBucket = h; hourly.Add(SnapshotHour()); }
        }

        return Report(hourly);
    }

    // ── Loudness, through the real store and the real decoder ────────────────
    static void MeasureAndCheck(string file)
    {
        long size = 0, mtime = 0;
        try { var fi = new FileInfo(file); size = fi.Length; mtime = fi.LastWriteTimeUtc.Ticks; }
        catch { return; }

        if (!LoudnessStore.HasCurrent(file, size, mtime))
        {
            TrackLoudness m = AudioDecoder.MeasureLoudness(file, CMMConfig.LoudnessChunkSeconds, null);
            if (m.Ok)
            {
                LoudnessStore.Store(file, m.Size, mtime, m.Hash, m.Lufs, m.Peak, false, m.NoIntegrated);
                _measured++;
                if (m.NoIntegrated) _shortNoLoudness++;
                _measureCount.TryGetValue(file, out int had);
                _measureCount[file] = had + 1;
                // Re-measuring the same content is the shape of the bug that once decoded one
                // track 295 times. The key carries the stamp, so a genuine content change is a
                // new key and is allowed to measure again.
                string stamped = file + "|" + size + "|" + mtime;
                _stampCount.TryGetValue(stamped, out int seenTimes);
                _stampCount[stamped] = seenTimes + 1;
                Hold(I10, seenTimes == 0,
                     () => $"{Path.GetFileName(file)} measured {seenTimes + 1}x at the same size+mtime");
            }
            else _measureFailed++;
        }

        float g = LoudnessStore.GetTrackGain(file);
        Hold(I1, !float.IsNaN(g) && !float.IsInfinity(g) && g >= 0f && g <= 8f,
             () => $"{Path.GetFileName(file)} resolved to gain {g}");
        if (!LoudnessStore.IsMeasured(file))
            Hold(I2, g <= 1.0f + 1e-4f,
                 () => $"unmeasured {Path.GetFileName(file)} would play at {g:F3}");
    }

    // ── The broadcast schedule ───────────────────────────────────────────────
    static double _lastB = -1;
    static readonly Dictionary<string, string> _lastOnAir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, double> _lastOffset = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> _lastOrder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, long> _lastWindow = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    static void CheckSchedule(Random rng, double B)
    {
        foreach (RadioStation st in RadioStationCatalog.Stations)
        {
            if (!st.Ready) continue;
            if (!RadioStationCatalog.ResolveAt(st, B, out string f, out int idx, out double off))
            { _walkMisses++; continue; }
            _stationResolves++;

            // Within one track, on an UNCHANGED pool, the offset advances with the clock and
            // never runs back. The pool qualifier is not a let-off: the schedule is anchored,
            // so adding or removing a track shifts everything after it and a listener really
            // does land somewhere else. That is the design. What must never happen is the
            // offset moving backwards while nothing changed.
            // The window matters as much as the pool. At an anchor boundary the chain
            // restarts, so the offset drops to zero -- correctly, and for every client at the
            // same instant. Comparing across a boundary would flag the feature working.
            string order = string.Join("|", st.Order);
            long window = RadioStationCatalog.WindowIndex(B);
            bool stablePool = _lastOrder.TryGetValue(st.Name, out string prevOrder) && prevOrder == order
                              && _lastWindow.TryGetValue(st.Name, out long prevWindow) && prevWindow == window;
            _lastWindow[st.Name] = window;
            if (stablePool && _lastOnAir.TryGetValue(st.Name, out string prevFile)
                && prevFile == f && _lastB >= 0 && B > _lastB
                && _lastOffset.TryGetValue(st.Name, out double prevOff))
            {
                Hold(I6, off >= prevOff - 1e-6,
                     () => $"{st.Name}: offset went backwards, {prevOff:F2} -> {off:F2}, pool unchanged");
            }
            _lastOrder[st.Name] = order;
            _lastOnAir[st.Name] = f; _lastOffset[st.Name] = off;

            // A second evaluation at the same second must agree. Same question, and the
            // answer is what every client independently computes.
            if (RadioStationCatalog.ResolveAt(st, B, out string f2, out _, out double off2))
                Hold(I7, f2 == f && Math.Abs(off2 - off) < 1e-6,
                     () => $"{st.Name}: two resolves at B={B:F1} disagreed");
        }
        _lastB = B;
    }

    // ── Reporting ────────────────────────────────────────────────────────────
    static void Note(string s) { if (_events.Count < 40) _events.Add(s); }
    static string Fmt(double s) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss");

    static string SnapshotHour()
        => $"{_contextChanges,6} {_trackPicks,6} {_measured,6} {_stationResolves,7} {_mutations,5}";

    static int Report(List<string> hourly)
    {
        int violated = 0;
        foreach (Inv i in _inv) if (i.Violations > 0) violated++;

        Console.WriteLine();
        Console.WriteLine(violated == 0
            ? "VERDICT: PASS - every invariant held"
            : $"VERDICT: FAIL - {violated} of {_inv.Count} invariants violated");

        if (violated > 0)
        {
            Console.WriteLine("\n-- Violations " + new string('-', 50));
            foreach (Inv i in _inv)
            {
                if (i.Violations == 0) continue;
                Console.WriteLine($" FAIL  {i.Id}  {i.Text}");
                Console.WriteLine($"       {i.Violations} of {i.Checks} checks");
                Console.WriteLine($"       first: {i.FirstFailure}");
            }
        }

        Console.WriteLine("\n-- Invariants " + new string('-', 50));
        foreach (Inv i in _inv)
            Console.WriteLine($" {(i.Violations == 0 ? "ok  " : "FAIL")}  {i.Id,-4} {i.Text,-58} {i.Checks,9:N0} checks");

        // The shape over time matters more than the total: a rate that DECAYS is the
        // signature of every accumulation bug this project has had, and an average hides it.
        Console.WriteLine("\n-- Per hour (cumulative) " + new string('-', 39));
        Console.WriteLine("  hour  ctxchg  picks  measd  resolve  muts");
        for (int h = 0; h < hourly.Count; h++)
            Console.WriteLine($"  {h,4}  {hourly[h]}");

        int reMeasured = 0, worst = 0; string worstFile = null;
        foreach (var kv in _measureCount)
        {
            if (kv.Value <= 1) continue;
            reMeasured++;
            if (kv.Value > worst) { worst = kv.Value; worstFile = Path.GetFileName(kv.Key); }
        }

        Console.WriteLine("\n-- Metrics (no pass/fail) " + new string('-', 38));
        Console.WriteLine($"  ticks                {_ticks,10:N0}");
        Console.WriteLine($"  context changes      {_contextChanges,10:N0}");
        Console.WriteLine($"  track selections     {_trackPicks,10:N0}");
        Console.WriteLine($"  library mutations    {_mutations,10:N0}");
        Console.WriteLine($"  schedule resolves    {_stationResolves,10:N0}   ({_walkMisses:N0} awaiting a walk)");
        Console.WriteLine($"  tracks measured      {_measured,10:N0}   ({_measureFailed} failed, {_shortNoLoudness} too short to have a loudness)");
        Console.WriteLine($"  files measured twice {reMeasured,10:N0}" + (worstFile != null ? $"   worst: {worstFile} x{worst}" : ""));
        Console.WriteLine($"  loudness cache       {LoudnessStore.MeasuredFileCount,10:N0} entries held");

        if (_contextTally.Count > 0)
        {
            Console.WriteLine("\n  contexts entered:");
            foreach (var kv in _contextTally) Console.WriteLine($"    {kv.Key,-18} {kv.Value,6:N0}");
        }

        if (_events.Count > 0)
        {
            Console.WriteLine("\n-- Library events (first " + _events.Count + ") " + new string('-', 30));
            foreach (string e in _events) Console.WriteLine("  " + e);
        }

        Console.WriteLine("\n-- Scope " + new string('-', 55));
        Console.WriteLine("  REAL: context resolution (context.lua), track rotation, station");
        Console.WriteLine("        discovery and ordering, the anchored schedule and its length");
        Console.WriteLine("        cache, playlist reading, loudness measurement and the gain");
        Console.WriteLine("        policy, eviction, emitter slot ownership.");
        Console.WriteLine("  NOT:  the audio player's coroutines, the personal radio's state");
        Console.WriteLine("        machine, emitter spatialisation. All three need Unity, and a");
        Console.WriteLine("        model of them here would only test the model.");
        return violated == 0 ? 0 : 1;
    }
}
