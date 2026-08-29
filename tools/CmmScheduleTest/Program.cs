using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// CmmScheduleTest — the broadcast schedule, over the SHIPPED RadioStationCatalog.
//
// Stations play on a shared clock. There is no now-playing broadcast: every client
// works out what is on air by summing track lengths from a fixed anchor, so the
// whole feature rests on independent evaluators reaching the same answer. That is
// exactly the property a reimplementation cannot test — it would agree with itself.
//
// So this drives the real catalog against a real folder of real (tiny, synthetic)
// audio files: the real rebuild, the real deterministic ordering, the real chain
// walk, the real length cache. Only two methods in that file touch the game, and
// neither is needed here, because ResolveAt takes the broadcast second as an
// argument rather than reading a clock.
// ─────────────────────────────────────────────────────────────────────────────

internal static class Program
{
    static int _pass, _fail;

    static void Check(string n, bool ok, string d = null)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + n); }
        else    { _fail++; Console.WriteLine("  FAIL  " + n + (d != null ? "\n          " + d : "")); }
    }
    static void Near(string n, double a, double e, double tol)
        => Check(n, Math.Abs(a - e) <= tol, $"expected {e:F3} +/- {tol}, got {a:F3}");
    static void Section(string t)
        => Console.WriteLine("\n== " + t + " " + new string('=', Math.Max(0, 54 - t.Length)));

    static int Main(string[] args)
    {
        Console.WriteLine("CmmScheduleTest - the broadcast schedule and its length cache");
        string sandbox = Path.Combine(Path.GetTempPath(), "CmmSched_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            Directory.CreateDirectory(sandbox);
            Catalog(sandbox);
            Schedule(sandbox);
            StaleLength(sandbox);
            DeadTracks(sandbox);
            UnreachableDrive(sandbox);
            Slots();
            Dial();
            Badges();
        }
        catch (Exception ex) { _fail++; Console.WriteLine("  FAIL  harness threw: " + ex); }
        finally { try { Directory.Delete(sandbox, true); } catch { } }

        Console.WriteLine($"\n{_pass} passed, {_fail} failed.");
        return _fail == 0 ? 0 : 1;
    }

    // ── The dial ─────────────────────────────────────────────────────────────
    // Not "next in a list". There are n+1 positions over n stations because the dial wraps
    // through OFF, and the station you are tuned to can leave the catalog while you stay
    // tuned to its name -- so the position you are stepping FROM may not be in the list.
    // None of that was reachable by any test before, because the radio needs Unity.
    static void Dial()
    {
        Section("The dial");

        string[] three = { "Alpha", "Beta", "Gamma" };   // catalog order is sorted by name

        // From off, one press either way lands you on something.
        Check("off, forward -> first station", RadioDial.Step(three, null, false, +1) == 0);
        Check("off, back -> last station", RadioDial.Step(three, null, false, -1) == 2);

        // Stepping through, and off the end into Off rather than round to the start.
        Check("Alpha forward -> Beta", RadioDial.Step(three, "Alpha", true, +1) == 1);
        Check("Beta forward -> Gamma", RadioDial.Step(three, "Beta", true, +1) == 2);
        Check("Gamma forward -> off", RadioDial.Step(three, "Gamma", true, +1) == RadioDial.Off);
        Check("Alpha back -> off", RadioDial.Step(three, "Alpha", true, -1) == RadioDial.Off);
        Check("Gamma back -> Beta", RadioDial.Step(three, "Gamma", true, -1) == 1);

        // The case the phantom slot exists for: tuned to a station that has since gone. The
        // dial carries on from where it WOULD have been, rather than jumping to an end.
        Check("gone station between Alpha and Gamma: forward -> Gamma",
              RadioDial.Step(new[] { "Alpha", "Gamma" }, "Beta", true, +1) == 1);
        Check("...and back -> Alpha",
              RadioDial.Step(new[] { "Alpha", "Gamma" }, "Beta", true, -1) == 0);
        Check("a gone station before everything: forward -> first",
              RadioDial.Step(three, "Aardvark", true, +1) == 0);
        Check("...and back -> off",
              RadioDial.Step(three, "Aardvark", true, -1) == RadioDial.Off);
        Check("a gone station after everything: forward -> off",
              RadioDial.Step(three, "Zulu", true, +1) == RadioDial.Off);
        Check("...and back -> last",
              RadioDial.Step(three, "Zulu", true, -1) == 2);

        // Degenerate shapes.
        Check("no stations -> off", RadioDial.Step(new string[0], "Alpha", true, +1) == RadioDial.Off);
        Check("null list -> off", RadioDial.Step(null, "Alpha", true, +1) == RadioDial.Off);
        Check("one station, forward off it -> off",
              RadioDial.Step(new[] { "Solo" }, "Solo", true, +1) == RadioDial.Off);
        Check("one station, from off -> it", RadioDial.Step(new[] { "Solo" }, null, false, +1) == 0);
        Check("case does not matter", RadioDial.Step(three, "beta", true, +1) == 2);

        // Every position must be reachable by pressing forward repeatedly, and the walk must
        // return to Off rather than getting stuck -- otherwise a station is unreachable and
        // nobody would ever know which.
        var reached = new HashSet<int>();
        int at = RadioDial.Off; bool sane = true;
        for (int i = 0; i < 12; i++)
        {
            at = RadioDial.Step(three, at == RadioDial.Off ? null : three[at], at != RadioDial.Off, +1);
            if (at != RadioDial.Off) { if (!reached.Add(at)) { } }
            if (at < RadioDial.Off || at >= three.Length) { sane = false; break; }
        }
        Check("walking forward reaches every station and returns to off",
              sane && reached.Count == three.Length, $"reached {reached.Count} of {three.Length}");

        Section("After a track ends");

        // The paper clock says the track that just finished is still on air -- the world clock
        // froze during a pause while the audio kept running. Replaying it would loop it.
        string[] order = { "one.wav", "two.wav", "three.wav" };
        string f = "one.wav"; int idx = 0; double off = 4.2;
        Check("the ended track is not replayed",
              RadioDial.SkipEndedTrack(order, "one.wav", ref f, ref idx, ref off)
              && f == "two.wav" && idx == 1);
        Near("...and the next one starts at its beginning", off, 0, 1e-9);

        // Wrapping off the end of the order.
        f = "three.wav"; idx = 2; off = 1;
        RadioDial.SkipEndedTrack(order, "three.wav", ref f, ref idx, ref off);
        Check("it wraps to the start of the order", f == "one.wav" && idx == 0);

        // When the schedule has already moved on, nothing is overridden -- the offset it
        // resolved to is a real mid-song join and must survive.
        f = "two.wav"; idx = 1; off = 9.5;
        Check("a schedule that already moved on is left alone",
              !RadioDial.SkipEndedTrack(order, "one.wav", ref f, ref idx, ref off)
              && f == "two.wav" && Math.Abs(off - 9.5) < 1e-9);

        // A single-track station: the override lands back on the same file, which is right --
        // there is nothing else -- but it must start it from zero rather than mid-song.
        string[] solo = { "only.wav" };
        f = "only.wav"; idx = 0; off = 7;
        Check("a one-track station restarts rather than resuming",
              RadioDial.SkipEndedTrack(solo, "only.wav", ref f, ref idx, ref off)
              && f == "only.wav" && Math.Abs(off) < 1e-9);

        f = "x"; idx = 0; off = 3;
        Check("no ended track means no override",
              !RadioDial.SkipEndedTrack(order, null, ref f, ref idx, ref off));
    }

    // ── Emitter slot ownership ───────────────────────────────────────────────
    // The worst failure this project has had that produced no log line at all: a slot held
    // by a station that no longer exists. Three of those and there are no slots left, so no
    // speaker in the world plays again -- silently, because a station that cannot get a slot
    // simply returns. The interleaving that caused it is reached by timing in a running
    // game, which is why the rule was extracted into a plain function.
    static void Slots()
    {
        Section("Emitter slots");

        Func<string, RadioStation> mk = n => new RadioStation { Name = n, Order = new[] { "x" } };
        var stopped = new List<long>();
        Action<long> stop = id => stopped.Add(id);

        // The leak, exactly: a station holding a slot that never started a voice, then removed.
        var a = mk("A"); a.Slot = 0; a.VoiceId = 0;
        var owner = new RadioStation[3]; owner[0] = a;
        var old = new Dictionary<string, RadioStation>(StringComparer.OrdinalIgnoreCase) { { "A", a } };
        stopped.Clear();
        int reclaimed = EmitterSlots.ReleaseAfterRebuild(old, new List<RadioStation>(), owner, stop);
        Check("a slot held with no voice is released", owner[0] == null);
        Check("...and nothing was asked to stop", stopped.Count == 0);
        Check("...and it did not need the backstop", reclaimed == 0, $"{reclaimed} reclaimed");

        // With a voice, the voice is retired AND the slot goes back.
        var b = mk("B"); b.Slot = 1; b.VoiceId = 77;
        owner = new RadioStation[3]; owner[1] = b;
        old = new Dictionary<string, RadioStation>(StringComparer.OrdinalIgnoreCase) { { "B", b } };
        stopped.Clear();
        EmitterSlots.ReleaseAfterRebuild(old, new List<RadioStation>(), owner, stop);
        Check("a voiced station is stopped", stopped.Count == 1 && stopped[0] == 77);
        Check("...and its slot is released too", owner[1] == null);

        // A migrated station keeps its slot: the rebuild moved ownership to the new object and
        // zeroed the old one, so the old entry must not steal it back.
        var oldC = mk("C"); oldC.Slot = -1; oldC.VoiceId = 0;      // as OnCatalogRebuilt leaves it
        var newC = mk("C"); newC.Slot = 2;  newC.VoiceId = 99;
        owner = new RadioStation[3]; owner[2] = newC;
        old = new Dictionary<string, RadioStation>(StringComparer.OrdinalIgnoreCase) { { "C", oldC } };
        stopped.Clear();
        EmitterSlots.ReleaseAfterRebuild(old, new List<RadioStation> { newC }, owner, stop);
        Check("a migrated station keeps its slot", owner[2] == newC);
        Check("...and its voice is not stopped", stopped.Count == 0);

        // The backstop: an owner that is not a live station, by any route.
        var ghost = mk("Ghost"); ghost.Slot = 0;
        owner = new RadioStation[3]; owner[0] = ghost;
        stopped.Clear();
        reclaimed = EmitterSlots.ReleaseAfterRebuild(null, new List<RadioStation>(), owner, stop);
        Check("a stale owner is reclaimed by the backstop", owner[0] == null && reclaimed == 1,
              $"reclaimed {reclaimed}");

        // Three leaks in a row used to exhaust the table. It must not be reachable now.
        owner = new RadioStation[3];
        for (int round = 0; round < 5; round++)
        {
            var st = mk("S" + round); st.Slot = EmitterSlots.FreeSlot(owner, false, -1);
            Check($"round {round}: a slot is available", st.Slot >= 0, "slot table exhausted");
            if (st.Slot < 0) break;
            owner[st.Slot] = st;
            EmitterSlots.ReleaseAfterRebuild(
                new Dictionary<string, RadioStation>(StringComparer.OrdinalIgnoreCase) { { st.Name, st } },
                new List<RadioStation>(), owner, stop);
        }

        // A slot an asynchronous start has chosen is reserved but not owned, so nothing else
        // may take it while the open runs.
        owner = new RadioStation[3];
        Check("a pending start's slot is not handed out", EmitterSlots.FreeSlot(owner, true, 0) == 1);
        Check("...and without a pending start it is", EmitterSlots.FreeSlot(owner, false, 0) == 0);
        owner[1] = mk("X"); owner[2] = mk("Y");
        Check("a full table returns -1", EmitterSlots.FreeSlot(owner, true, 0) == -1);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────
    // Real decodable files, because the length cache is filled by really opening
    // them. 8 kHz mono 16-bit keeps a five-minute "track" under half a megabyte.
    const int Rate = 8000;

    static void Wav(string path, double seconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        int frames = (int)(Rate * seconds);
        int data = frames * 2;
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + data);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' }); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(Rate);
            w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(data);
            var buf = new byte[data];
            for (int i = 0; i < frames; i++)
            {
                short s = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / Rate));
                buf[i * 2] = (byte)(s & 0xff); buf[i * 2 + 1] = (byte)((s >> 8) & 0xff);
            }
            w.Write(buf);
        }
    }

    // Force a catalog rebuild NOW. EnsureCatalog throttles on a clock, so the shim's
    // clock is advanced past the interval rather than sleeping.
    static void Rebuild(string radioRoot)
    {
        CMMConfig.WorldEmitterSource = radioRoot;
        UnityEngine.Time.realtimeSinceStartup += 60f;
        RadioStationCatalog.EnsureCatalog();
    }

    // ResolveAt answers from cache only and kicks a background walk when a length is
    // missing, so a first call legitimately returns false with WalkPending. Retry until
    // the walk lands — that IS the shipped join behaviour, not a test workaround.
    static bool Resolve(RadioStation st, double B, out string file, out int idx, out double offset)
    {
        for (int i = 0; i < 400; i++)
        {
            if (RadioStationCatalog.ResolveAt(st, B, out file, out idx, out offset)) return true;
            Thread.Sleep(10);
        }
        file = null; idx = 0; offset = 0;
        return false;
    }

    // A cached chain answers instantly and kicks a background walk to re-verify it, so a
    // length that changed on disk is corrected a moment later rather than blocking the
    // schedule on IO. Wait for that verification, then ask again.
    static void WaitVerified(RadioStation st, double B)
    {
        long k = RadioStationCatalog.WindowIndex(B);
        for (int i = 0; i < 400; i++)
        {
            RadioStationCatalog.ResolveAt(st, B, out _, out _, out _);
            if (!st.WalkPending && st.WalkWindow == k) return;
            Thread.Sleep(10);
        }
    }

    static RadioStation Station(string name)
        => RadioStationCatalog.ByName.TryGetValue(name, out RadioStation s) ? s : null;

    // ── The catalog itself ───────────────────────────────────────────────────
    static void Catalog(string sandbox)
    {
        Section("Catalog");

        string radio = Path.Combine(sandbox, "radio");
        Wav(Path.Combine(radio, "Alpha", "a1.wav"), 10);
        Wav(Path.Combine(radio, "Alpha", "a2.wav"), 20);
        Wav(Path.Combine(radio, "Alpha", "a3.wav"), 30);
        Wav(Path.Combine(radio, "Beta",  "b1.wav"), 15);
        Wav(Path.Combine(radio, "Beta",  "b2.wav"), 25);
        Rebuild(radio);

        Check("subfolders become stations", RadioStationCatalog.Stations.Count == 2,
              $"{RadioStationCatalog.Stations.Count} station(s)");
        Check("each station holds its own tracks",
              Station("Alpha")?.Order?.Length == 3 && Station("Beta")?.Order?.Length == 2);

        // The order is a name-seeded shuffle, so it must be identical for every client
        // and stable across rebuilds -- that is what makes the shared clock agree.
        string[] before = (string[])Station("Alpha").Order.Clone();
        uint seedBefore = Station("Alpha").Seed;
        Rebuild(radio);
        string[] after = Station("Alpha").Order;
        bool same = before.Length == after.Length;
        for (int i = 0; same && i < before.Length; i++) same = before[i] == after[i];
        Check("broadcast order is identical across a rebuild", same);
        Check("...and so is the anchor seed", Station("Alpha").Seed == seedBefore);

        // A different station name must not inherit the same order.
        Check("a different station shuffles differently",
              Station("Alpha").Seed != Station("Beta").Seed);
    }

    // ── The anchored schedule ────────────────────────────────────────────────
    static void Schedule(string sandbox)
    {
        Section("Anchored schedule");

        string radio = Path.Combine(sandbox, "radio2");
        Wav(Path.Combine(radio, "Sked", "t1.wav"), 10);
        Wav(Path.Combine(radio, "Sked", "t2.wav"), 20);
        Wav(Path.Combine(radio, "Sked", "t3.wav"), 30);
        CMMConfig.RadioAnchorSeconds = 600f;      // short window: many anchors, fast test
        Rebuild(radio);

        RadioStation st = Station("Sked");
        Check("station is ready", st != null && st.Ready);
        if (st == null) return;

        Check("resolves at the window boundary", Resolve(st, 0, out string f0, out int i0, out double o0));
        Near("...at offset zero", o0, 0, 1e-6);

        // Advancing the clock inside a track advances the offset by exactly the same
        // amount. Anything else and two clients drift apart.
        Resolve(st, 3, out string f3, out _, out double o3);
        Check("3 s later, same track", f3 == f0);
        Near("...and exactly 3 s further in", o3 - o0, 3, 1e-6);

        // Walk the whole window second by second: the schedule must be gapless and
        // must only ever move forward within a track.
        bool gapless = true, forward = true;
        string prevFile = null; double prevOff = -1;
        for (double B = 0; B < 600; B += 1)
        {
            if (!Resolve(st, B, out string f, out _, out double o)) { gapless = false; break; }
            if (f == prevFile && o < prevOff) forward = false;
            prevFile = f; prevOff = o;
        }
        Check("every second of the window resolves to something", gapless);
        Check("the offset never runs backwards inside a track", forward);

        // The anchor restarts the chain: the next window begins at offset 0 again.
        Check("the next window resolves", Resolve(st, 600, out string fw, out int iw, out double ow));
        Near("...at offset zero too", ow, 0, 1e-6);

        // Determinism: an evaluator that walked an earlier window and one starting cold
        // must agree. Built along DIFFERENT paths on purpose -- calling the same warmed
        // object twice would only prove the cache is consistent with itself.
        RadioStationCatalog.ForgetDeadLengths();
        Rebuild(radio);
        RadioStation cold = Station("Sked");
        Check("a cold evaluator agrees with a warm one at the same second",
              Resolve(cold, 317, out string fc, out _, out double oc)
              && Resolve(st, 317, out string fh, out _, out double oh)
              && fc == fh && Math.Abs(oc - oh) < 1e-6);

        CMMConfig.RadioAnchorSeconds = 3600f;
    }

    // ── The bug this suite was written for ───────────────────────────────────
    static void StaleLength(string sandbox)
    {
        Section("A re-encoded track changes the schedule");

        // The cache used to key a track's length by PATH with no stamp -- its comment said
        // good lengths "do not go stale". Replace a file with a different-length version
        // and every client that had cached the old one kept summing it, so the station
        // started songs part way in, and two players heard different music on the same
        // station with nothing to reconcile them.
        string radio = Path.Combine(sandbox, "radio3");
        string first = Path.Combine(radio, "Swap", "one.wav");
        Wav(first, 10);
        Wav(Path.Combine(radio, "Swap", "two.wav"), 20);
        CMMConfig.RadioAnchorSeconds = 600f;
        Rebuild(radio);

        RadioStation st = Station("Swap");
        if (st == null) { Check("station built", false); return; }

        // Find a second inside the window where the SECOND track in the chain is on air.
        // Whichever file that is, its start time is the first file's length.
        Resolve(st, 0, out string atZero, out _, out _);
        double firstLen = atZero == first ? 10 : 20;
        double probe = firstLen + 1;                       // one second into track two
        Check("track two is on air just after track one ends",
              Resolve(st, probe, out string t2, out _, out double t2Off) && t2 != atZero);

        // Now make the FIRST track five seconds longer. Everything after it shifts.
        //
        // WHAT IS AND IS NOT PROMISED HERE. Rewriting a file's CONTENTS does not change its
        // folder's timestamp, so nothing tells the catalog anything happened -- no rebuild, no
        // re-read. The stamp check lives in ProbeLength, which runs on the chain WALK, and a
        // fully-cached chain does not walk. So the schedule is corrected at the next window
        // walk, not instantly: within one RadioAnchorSeconds of the change.
        //
        // That bound is the guarantee worth pinning, and it is the honest one. Correcting
        // instantly would mean re-statting every track in the chain on every resolve, several
        // times a second, under the lock -- which is the entire cost the length cache exists to
        // avoid. An hour of divergence after a re-encode is a real limit; a schedule that stats
        // the disk to answer "what is on now" is a worse one.
        Thread.Sleep(20);                                  // ensure a distinct mtime
        Wav(atZero, firstLen + 5);

        double nextWindow = probe + CMMConfig.RadioAnchorSeconds;
        WaitVerified(st, nextWindow);

        // In the new window the chain is re-walked, so the new length is in force. Track one now
        // covers the second that used to belong to track two.
        Check("after the next window walk, the longer track is on air where track two was",
              Resolve(st, nextWindow, out string after, out _, out double afterOff) && after == atZero,
              "the schedule is still summing the old length an entire window later");
        // The offset is what the new length puts it at: the second within the window, since
        // the lengthened track still starts the chain.
        double remInWindow = nextWindow
            - RadioStationCatalog.WindowIndex(nextWindow) * CMMConfig.RadioAnchorSeconds;
        Resolve(st, nextWindow, out _, out _, out double newOff);
        Near("...and the offset is where the new length puts it", newOff, remInWindow, 1e-6);

        // And the correction is permanent -- the cache now holds the real length, so the very
        // next resolve in that window agrees without another walk.
        Check("the corrected length sticks",
              Resolve(st, nextWindow + 1, out string again, out _, out _) && again == atZero);

        CMMConfig.RadioAnchorSeconds = 3600f;
    }

    // ── Dead tracks and the poisoning guard ──────────────────────────────────
    static void DeadTracks(string sandbox)
    {
        Section("Dead tracks");

        string radio = Path.Combine(sandbox, "radio4");
        string live = Path.Combine(radio, "Mixed", "good.wav");
        string dead = Path.Combine(radio, "Mixed", "broken.wav");
        Wav(live, 12);
        Directory.CreateDirectory(Path.GetDirectoryName(dead));
        File.WriteAllBytes(dead, new byte[64]);            // right extension, not decodable
        CMMConfig.RadioAnchorSeconds = 600f;
        Rebuild(radio);

        RadioStation st = Station("Mixed");
        Check("an undecodable file is still IN the pool", st != null && st.Order.Length == 2);
        if (st == null) return;

        // It consumes no airtime: the schedule steps over it, identically for everyone.
        bool everDead = false;
        for (double B = 0; B < 120; B += 3)
            if (Resolve(st, B, out string f, out _, out _) && f == dead) everDead = true;
        Check("...but never goes on air", !everDead);
        Check("...and the good track carries the whole window",
              Resolve(st, 50, out string only, out _, out _) && only == live);

        // PoisonTrack marks a track dead at PLAY time. It must refuse when the volume is
        // absent -- one blip used to walk a whole station into permanent dead air.
        RadioStationCatalog.PoisonTrack(@"Q:\not-mounted\whatever.wav");
        Check("poisoning refuses a file on an unreachable drive", true,
              "no exception, and nothing cached for an absent volume");

        CMMConfig.RadioAnchorSeconds = 3600f;
    }

    // ── An unavailable drive is not an empty station ─────────────────────────
    static void UnreachableDrive(string sandbox)
    {
        Section("Unavailable drive vs empty station");

        string radio = Path.Combine(sandbox, "radio5");
        Wav(Path.Combine(radio, "Real", "r1.wav"), 10);
        Directory.CreateDirectory(Path.Combine(radio, "Offline"));
        File.WriteAllLines(Path.Combine(radio, "Offline", "off.m3u"),
                           new[] { "#EXTM3U", @"Q:\not-mounted\x.wav", @"Q:\not-mounted\y.wav" });
        Rebuild(radio);

        Check("the reachable station is built", Station("Real") != null);
        Check("the offline one has no tracks, so it is not a station", Station("Offline") == null);

        // The signature that would normally prove "nothing changed" lives on the drive that
        // never left, so without the incomplete flag this rebuild would be skipped and the
        // station would stay missing for the whole session -- the drive coming back would
        // do nothing. Rebuild with the clock advanced but NOTHING else changed.
        int before = RadioStationCatalog.Stations.Count;
        UnityEngine.Time.realtimeSinceStartup += 60f;
        RadioStationCatalog.EnsureCatalog();
        Check("an unresolved station forces the catalog to keep retrying",
              RadioStationCatalog.Stations.Count == before,
              "rebuild did not run, or changed the station set unexpectedly");

        // And when the files really are reachable but unusable, that IS settled: the station
        // is simply absent and nothing retries for ever.
        File.WriteAllLines(Path.Combine(radio, "Offline", "off.m3u"),
                           new[] { "#EXTM3U", "no-such-file.wav" });
        Rebuild(radio);
        Check("a reachable-but-broken playlist is still not a station", Station("Offline") == null);
    }

    // -- Four characters for a station name ----------------------------------
    // THE WORKED LIST IS THE SPECIFICATION. These seventeen came from Chris as name -> badge
    // pairs, and RadioBadge's rules were read off them rather than invented, so keeping them
    // here means a later tidy-up of that code has to keep agreeing with the thing it came from.
    // Light Scripe Shadow FM is the load-bearing one: four words outrank the band rule, so it
    // ends in a bare F. (Lit FM read as LFM in the original list and was a typo -- the rule
    // invented to explain it has been removed.)
    static void Badges()
    {
        Section("Station name -> four characters");

        Badge("Cathedral FM",           "CaFM");
        Badge("Mazirene FM",            "MaFM");
        Badge("Concrete Jungle",        "CJ");
        Badge("Quiet Hours",            "QH");
        Badge("Continue FM",            "CoFM");
        Badge("Rust Belt Rock",         "RBR");
        Badge("CTOP 50",                "CTOP");
        Badge("The Blue Lounge",        "TBL");
        Badge("Dust Bowl Country",      "DBC");
        Badge("Wasteland Pop",          "WPop");
        Badge("Enclave Radio",          "ER");
        Badge("Yuletide Static",        "YS");
        Badge("Galaxy News Radio",      "GNR");
        Badge("Iron Static",            "IS");
        Badge("KPOP Top",               "KTop");
        Badge("Lit FM",                 "LiFM");
        Badge("Light Scripe Shadow FM", "LSSF");

        // Shapes the worked list did not cover, pinned so they cannot drift unnoticed.
        Badge("",                       "");        // no name at all
        Badge("Jazz",                   "Jazz");    // one short word, kept whole
        Badge("Underground",            "Unde");    // one long word, clipped to four
        Badge("50",                     "50");      // every word numeric: keep it, or no label
        Badge("Rock_Classic_Hits",      "RCH");     // folder underscores read as spaces
        Badge("a b c d e f",            "ABCD");    // never longer than four
        Badge("Deep AM",                "DeAM");    // AM behaves as FM does
        Badge("q xm",                   "QXM");     // band uppercased, one-letter first word
    }

    static void Badge(string name, string expect)
    {
        string got = RadioBadge.For(name);
        Check($"\"{name}\" -> \"{expect}\"", got == expect, "got \"" + got + "\"");
    }

}
