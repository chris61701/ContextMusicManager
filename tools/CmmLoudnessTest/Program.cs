using System;
using System.Collections.Generic;
using System.IO;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// CmmLoudnessTest — progressive measurement, and the policy around a reading that
// is used before it is finished.
//
// Integrated loudness is a whole-file quantity: until the last sample is read
// there is nothing to say. A 37-minute podcast takes about nine seconds to read,
// which is three times the start-hold, so those tracks always started on the
// unmeasured guess — about 12 dB from where they belong — and, now that plain
// measurements no longer persist across launches, they did it on every rejoin.
//
// Progressive measurement answers the same question incrementally. That buys a
// usable gain about a second in, and it costs a new kind of value: a reading that
// is real, gated correctly, and not yet right. Everything around such a reading is
// a guard, and none of those guards are observable by playing the game for the two
// seconds one exists — which is what this suite is for.
// ─────────────────────────────────────────────────────────────────────────────

internal static class Program
{
    static int _pass, _fail, _skip;

    static void Check(string name, bool ok, string detail = null)
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {name}"); }
        else    { _fail++; Console.WriteLine($"  FAIL  {name}" + (detail != null ? $"\n          {detail}" : "")); }
    }

    static void Near(string name, double actual, double expected, double tol)
        => Check(name, Math.Abs(actual - expected) <= tol,
                 $"expected {expected:F4} ± {tol:F4}, got {actual:F4}");

    static void Skip(string name, string why)
    {
        _skip++;
        Console.WriteLine($"  SKIP  {name}  ({why})");
    }

    static void Section(string title) => Console.WriteLine($"\n── {title} " + new string('─', Math.Max(0, 58 - title.Length)));

    // Gain → dB relative to unity, so expectations read in the units the policy is written in.
    static double Db(float gain) => 20.0 * Math.Log10(Math.Max(gain, 1e-6f));

    static int Main(string[] args)
    {
        Console.WriteLine("CmmLoudnessTest — progressive loudness + provisional-reading policy");

        string sandbox = Path.Combine(Path.GetTempPath(), "CmmLoudnessTest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(Path.Combine(sandbox, "Cache"));
        try
        {
            PolicyCases(sandbox);
            RealFileCases(CollectCorpus(args));
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }

        Console.WriteLine($"\n{_pass} passed, {_fail} failed, {_skip} skipped.");
        return _fail == 0 ? 0 : 1;
    }

    // ── Policy: how a provisional reading is allowed to behave ───────────────
    // These need no audio. Store() takes the measured values directly, which is
    // exactly how the real pipeline hands them over from the measurement thread.
    static void PolicyCases(string sandbox)
    {
        // A path OUTSIDE the music root, keyed whole — the same branch a radio
        // station on another drive takes, and 13 of 16 stations here are exactly that.
        string track = Path.Combine(sandbox, "track.flac");
        const string hash = "0123456789abcdef";
        const long size = 1234, mtime = 638000000000000000L;

        Section("Provisional gain policy");

        LoudnessStore.Load(sandbox);
        // -40 LUFS against a -18 target asks for +22 dB, past every cap, so both
        // ceilings are exercised by one absurdly quiet reading rather than two
        // fiddly ones that only just cross.
        LoudnessStore.Store(track, size, mtime, hash, -40.0, 0.1, provisional: true);
        Near("provisional boost is capped at ProvisionalMaxBoostDb",
             Db(LoudnessStore.GetTrackGain(track)), CMMConfig.LoudnessProvisionalMaxBoostDb, 0.01);

        LoudnessStore.Store(track, size, mtime, hash, -40.0, 0.1, provisional: false);
        Near("the same reading, final, gets the full MaxBoostDb",
             Db(LoudnessStore.GetTrackGain(track)), CMMConfig.LoudnessMaxBoostDb, 0.01);

        // The asymmetry is the point: a partial that reads too LOUD is the reliable
        // direction (R128's relative gate discards the quiet blocks), and cutting is
        // the safe direction anyway, so nothing caps it.
        LoudnessStore.Load(sandbox);
        LoudnessStore.Store(track, size, mtime, hash, 0.0, 1.0, provisional: true);
        Near("provisional CUT is not capped", Db(LoudnessStore.GetTrackGain(track)), -18.0, 0.01);

        LoudnessStore.Load(sandbox);
        CMMConfig.LoudnessNormalizationEnabled = false;
        LoudnessStore.Store(track, size, mtime, hash, -40.0, 0.1, provisional: true);
        Near("normalization off — a provisional changes nothing",
             LoudnessStore.GetTrackGain(track), 1.0, 1e-6);
        CMMConfig.LoudnessNormalizationEnabled = true;

        Section("Provisional freshness");

        LoudnessStore.Load(sandbox);
        LoudnessStore.Store(track, size, mtime, hash, -30.0, 0.2, provisional: true);
        // IsMeasured gates the start-hold and the prewarm: it must accept a partial,
        // or publishing early buys nothing. HasCurrent decides whether to MEASURE
        // again: it must reject one, or an abandoned partial (world exit, the file
        // going away) sits there for the session looking like a finished answer.
        Check("IsMeasured accepts a provisional", LoudnessStore.IsMeasured(track));
        Check("HasCurrent rejects a provisional", !LoudnessStore.HasCurrent(track, size, mtime));

        LoudnessStore.Store(track, size, mtime, hash, -30.0, 0.2, provisional: false);
        Check("HasCurrent accepts it once final", LoudnessStore.HasCurrent(track, size, mtime));

        Section("Provisional never walks a finished reading backwards");

        LoudnessStore.Load(sandbox);
        LoudnessStore.Store(track, size, mtime, hash, -20.0, 0.5, provisional: false);
        float finalGain = LoudnessStore.GetTrackGain(track);
        // The cache is content-keyed, so one measurement serves every path holding
        // that content — including a second path that is only part-way through its
        // own progressive measure.
        string twin = Path.Combine(sandbox, "same-audio-different-name.flac");
        LoudnessStore.Store(twin, size, mtime, hash, -35.0, 0.9, provisional: true);
        Near("a provisional does not overwrite a final reading",
             LoudnessStore.GetTrackGain(track), finalGain, 1e-6);
        Near("...and the second path resolves to that same final reading",
             LoudnessStore.GetTrackGain(twin), finalGain, 1e-6);
        Check("...while still binding the second path", LoudnessStore.HasCurrent(twin, size, mtime));

        LoudnessStore.Load(sandbox);
        LoudnessStore.Store(track, size, mtime, hash, -35.0, 0.9, provisional: true);
        LoudnessStore.Store(track, size, mtime, hash, -20.0, 0.5, provisional: false);
        Near("a final DOES overwrite a provisional", Db(LoudnessStore.GetTrackGain(track)), 2.0, 0.01);

        Section("Provisional never reaches disk");

        LoudnessStore.Load(sandbox);
        LoudnessStore.Store(track, size, mtime, hash, -30.0, 0.2, provisional: true);
        LoudnessStore.AdjustUserTrim(track, -0.1f);            // the user acts mid-measure
        var snap = LoudnessStore.SnapshotForSave();
        Check("a trimmed provisional is not written",
              snap != null && snap.Tracks.Count == 0,
              snap == null ? "no snapshot" : $"{snap.Tracks.Count} track lines");

        LoudnessStore.Store(track, size, mtime, hash, -30.0, 0.2, provisional: false);
        snap = LoudnessStore.SnapshotForSave();
        Check("once final, the trim IS written",
              snap != null && snap.Tracks.Count == 1,
              snap == null ? "no snapshot" : $"{snap.Tracks.Count} track lines");
        Near("...and the trim survived the provisional→final transition",
             LoudnessStore.GetUserTrim(track), 0.9, 1e-4);

        LoudnessStore.Load(sandbox);
        LoudnessStore.Store(track, size, mtime, hash, -30.0, 0.2, provisional: true);
        LoudnessStore.ToggleBanned(track);
        snap = LoudnessStore.SnapshotForSave();
        Check("a banned provisional is not written either",
              snap != null && snap.Tracks.Count == 0,
              snap == null ? "no snapshot" : $"{snap.Tracks.Count} track lines");
        Check("...but the ban is live immediately", LoudnessStore.IsBannedPath(track));

        Section("A partial that never finishes");

        LoudnessStore.Load(sandbox);
        LoudnessStore.Store(track, size, mtime, hash, -30.0, 0.2, provisional: true);
        LoudnessStore.ForgetProvisional(track);
        // The stuck state this prevents: HasCurrent rejects a provisional so nothing treats it
        // as done, IsMeasured accepts one so nothing asks again, and the track keeps a part-file
        // gain for the whole session with no path back.
        Check("a dropped partial is no longer measured", !LoudnessStore.IsMeasured(track));
        Near("...and the track falls back to the unmeasured guess",
             Db(LoudnessStore.GetTrackGain(track)), CMMConfig.LoudnessUnmeasuredDb, 0.01);

        LoudnessStore.Load(sandbox);
        LoudnessStore.Store(track, size, mtime, hash, -30.0, 0.2, provisional: false);
        LoudnessStore.ForgetProvisional(track);
        Check("a FINAL reading is never dropped", LoudnessStore.IsMeasured(track));

        LoudnessStore.Load(sandbox);
        LoudnessStore.Store(track, size, mtime, hash, -30.0, 0.2, provisional: true);
        LoudnessStore.AdjustUserTrim(track, -0.1f);
        LoudnessStore.ForgetProvisional(track);
        Near("user data on a partial survives the drop", LoudnessStore.GetUserTrim(track), 0.9, 1e-4);
        Check("...but the part-file reading itself is retired", !LoudnessStore.IsMeasured(track));
        var trimmedSnap = LoudnessStore.SnapshotForSave();
        Check("...and the retired entry now persists, carrying the trim",
              trimmedSnap != null && trimmedSnap.Tracks.Count == 1,
              trimmedSnap == null ? "no snapshot" : $"{trimmedSnap.Tracks.Count} track lines");

        // Content-keyed: a second path holding the same audio may be measuring happily, so the
        // hash entry has to outlive the dropped path.
        LoudnessStore.Load(sandbox);
        string other = Path.Combine(sandbox, "other-copy.flac");
        LoudnessStore.Store(track, size, mtime, hash, -30.0, 0.2, provisional: true);
        LoudnessStore.Store(other, size, mtime, hash, -30.0, 0.2, provisional: true);
        LoudnessStore.ForgetProvisional(track);
        Check("dropping one path leaves the other copy measured", LoudnessStore.IsMeasured(other));
        Check("...and the dropped one is gone", !LoudnessStore.IsMeasured(track));

        Section("Only user data reaches disk (regression: the path index)");

        // The Track filter was added when persistence became session-scoped; the Path filter
        // was not, and it tested the LIVE hash table -- so every measured track still wrote a
        // <Path> line pointing at a hash the file did not contain. loudness.xml went on being
        // the library index the policy existed to retire, and every one of those lines was an
        // orphan on load.
        LoudnessStore.Load(sandbox);
        for (int i = 0; i < 50; i++)
            LoudnessStore.Store(Path.Combine(sandbox, $"plain{i}.flac"), size, mtime,
                                $"hash{i:D4}", -20.0 - i * 0.1, 0.5);
        string kept = Path.Combine(sandbox, "trimmed.flac");
        LoudnessStore.Store(kept, size, mtime, "hashkeep", -20.0, 0.5);
        LoudnessStore.AdjustUserTrim(kept, -0.1f);

        var big = LoudnessStore.SnapshotForSave();
        Check("50 plain measurements write ONE track line (the trimmed one)",
              big != null && big.Tracks.Count == 1, big == null ? "null" : $"{big.Tracks.Count}");
        Check("...and ONE path line, not 51",
              big != null && big.Paths.Count == 1, big == null ? "null" : $"{big.Paths.Count}");
        bool everyPathResolves = true;
        if (big != null)
        {
            var written = new System.Collections.Generic.HashSet<string>();
            foreach (var t in big.Tracks) written.Add(t.Hash);
            foreach (var pl in big.Paths) if (!written.Contains(pl.Hash)) everyPathResolves = false;
        }
        Check("every path line resolves to a track line in the same file", everyPathResolves);

        Section("A file with no gated block is answered, not retried forever");

        // EBU R128 integrates over gated 400 ms blocks, so a 200 ms stinger yields zero of them
        // and has no defined integrated loudness. That is a FINISHED answer -- re-decoding will
        // never produce a different one -- but it used to read as a failed measurement, which
        // meant a 3 s start-hold before every death sting and a fresh decode on every attempt.
        LoudnessStore.Load(sandbox);
        string sting = Path.Combine(sandbox, "death-sting.wav");
        LoudnessStore.Store(sting, size, mtime, "hashsting", 0, 0.8, provisional: false,
                            noIntegrated: true);
        Check("a no-gated-block file counts as measured", LoudnessStore.IsMeasured(sting));
        Check("...and as current, so nothing re-measures it",
              LoudnessStore.HasCurrent(sting, size, mtime));
        Near("...and plays at no correction, not at the stored lufs of 0",
             LoudnessStore.GetTrackGain(sting), 1.0, 1e-4);
        Near("...but a user trim still applies to it",
             LoudnessStore.AdjustUserTrim(sting, -0.1f), 0.9, 1e-4);
        Near("...and the trim is what the gain becomes",
             LoudnessStore.GetTrackGain(sting), 0.9, 1e-4);

        // The flag has to survive the file, or lufs=0 is taken literally on reload and an
        // 18 dB cut is applied to a stinger.
        var stingSnap = LoudnessStore.SnapshotForSave();
        Check("the no-gated-block flag is captured for persistence",
              stingSnap != null && stingSnap.Tracks.Count == 1 && stingSnap.Tracks[0].NoIntegrated);
        if (stingSnap != null && LoudnessStore.WriteSnapshot(stingSnap))
        {
            LoudnessStore.Load(sandbox);
            Near("...and survives a save/load round trip (not an 18 dB cut)",
                 LoudnessStore.GetTrackGain(sting), 0.9, 1e-4);
        }
        else Skip("no-gated-block round trip", "snapshot write failed");

        Section("Settle ramp");

        // The ramp is what makes a correction sound like the level settling rather
        // than a step. Both bounds are load-bearing: too short is a click, too long
        // and the track sounds like it is deliberately getting louder.
        // 0.2 s per dB, so the ceiling only bites past ~12.5 dB. A track measuring 0 LUFS
        // against the -18 target, corrected from the -5 dB unmeasured guess, is 13 dB and
        // does reach it — the ceiling is not decorative.
        Near("a very large correction is capped at the ceiling",
             LoudnessStore.SettleRampSeconds(1f, 20f), LoudnessStore.RampMaxSeconds, 1e-4);
        Near("a 12 dB correction takes most of it",
             LoudnessStore.SettleRampSeconds(1f, 4f), 2.408, 0.01);
        Near("a tiny correction takes the floor, not zero",
             LoudnessStore.SettleRampSeconds(1f, 1.01f), LoudnessStore.RampMinSeconds, 1e-4);
        Near("a 6 dB correction scales in between",
             LoudnessStore.SettleRampSeconds(1f, 2f), 1.204, 0.01);
        Near("direction does not matter — down ramps like up",
             LoudnessStore.SettleRampSeconds(2f, 1f),
             LoudnessStore.SettleRampSeconds(1f, 2f), 1e-4);
        Check("a zero gain does not produce a NaN or infinite ramp",
              !float.IsNaN(LoudnessStore.SettleRampSeconds(0f, 1f))
              && !float.IsInfinity(LoudnessStore.SettleRampSeconds(0f, 1f)));
    }

    // ── Real files: the measurement itself ───────────────────────────────────
    static void RealFileCases(List<string> corpus)
    {
        Section("Progressive measurement over real files");

        if (corpus.Count == 0)
        {
            Skip("progressive measurement", "no corpus — pass a directory or set CMM_TEST_MUSIC");
            return;
        }
        Console.WriteLine($"  corpus: {corpus.Count} file(s)");

        int agreed = 0, measured = 0, sliced = 0, whole = 0;
        double worstFinalDelta = 0, worstFirstDelta = 0;
        bool orderingOk = true, flagsOk = true, boundsOk = true, firstBeatsFallbackOk = true;
        string firstFailure = null;

        foreach (string f in corpus)
        {
            // Reference: one slice, i.e. the whole file — the pre-progressive path.
            var partsWhole = new List<TrackLoudness>();
            TrackLoudness one = AudioDecoder.MeasureLoudness(f, 0.0, p => partsWhole.Add(p));
            if (!one.Ok) continue;
            measured++;
            if (partsWhole.Count == 0) whole++;
            else if (firstFailure == null) firstFailure = $"{Path.GetFileName(f)} published {partsWhole.Count} partial(s) with slicing off";

            var parts = new List<TrackLoudness>();
            TrackLoudness prog = AudioDecoder.MeasureLoudness(f, CMMConfig.LoudnessChunkSeconds, p => parts.Add(p));
            if (!prog.Ok) continue;

            // The claim the whole design rests on: slicing changes WHEN the answer is
            // available, never WHAT it is. Exact, not within a tolerance — a tolerance
            // here would hide a real divergence in the accumulating state.
            double d = Math.Abs(prog.Lufs - one.Lufs);
            if (d > worstFinalDelta) worstFinalDelta = d;
            if (prog.Lufs == one.Lufs) agreed++;
            else if (firstFailure == null) firstFailure = $"{Path.GetFileName(f)}: sliced {prog.Lufs:F4} vs whole {one.Lufs:F4}";

            if (!prog.Final) flagsOk = false;
            foreach (var p in parts) if (p.Final) flagsOk = false;

            if (parts.Count > 0)
            {
                sliced++;
                // Each partial has strictly more of the file behind it than the last,
                // and all of them less than the finished reading.
                double prev = 0;
                foreach (var p in parts)
                {
                    if (p.SecondsMeasured <= prev) orderingOk = false;
                    prev = p.SecondsMeasured;
                }
                if (prev >= prog.SecondsMeasured) orderingOk = false;
                // A partial must be a plausible loudness, not a sentinel or a NaN
                // leaking out of a half-filled analyzer.
                foreach (var p in parts)
                    if (double.IsNaN(p.Lufs) || p.Lufs > 0 || p.Lufs < -70) boundsOk = false;

                // And the reason it is published at all: the first partial must be a
                // better guess than the unmeasured fallback it replaces. That is the
                // claim in dB, on this file, rather than in the abstract.
                double firstErr    = Math.Abs(parts[0].Lufs - prog.Lufs);
                double fallbackErr = Math.Abs((CMMConfig.LoudnessTargetLufs - CMMConfig.LoudnessUnmeasuredDb) - prog.Lufs);
                if (firstErr > worstFirstDelta) worstFirstDelta = firstErr;
                if (firstErr > fallbackErr)
                {
                    firstBeatsFallbackOk = false;
                    if (firstFailure == null)
                        firstFailure = $"{Path.GetFileName(f)}: first slice off by {firstErr:F2} dB, " +
                                       $"fallback would have been off by {fallbackErr:F2} dB";
                }
            }
        }

        if (measured == 0) { Skip("progressive measurement", "nothing in the corpus decoded"); return; }

        Console.WriteLine($"  {measured} measured, {sliced} long enough to slice, {whole} single-slice");
        Check($"sliced result is identical to the whole-file result ({agreed}/{measured})",
              agreed == measured, firstFailure);
        Check("worst divergence is exactly zero", worstFinalDelta == 0.0, $"worst {worstFinalDelta:E3} dB");
        Check("slicing off publishes nothing at all", whole == measured, firstFailure);
        Check("partials are flagged partial, the final one final", flagsOk);

        TruncationCases(corpus);

        if (sliced == 0)
        {
            Skip("multi-slice behaviour", "no file in the corpus is longer than one slice");
            return;
        }
        Check("each partial has strictly more of the file behind it", orderingOk);
        Check("every partial is a plausible loudness", boundsOk);
        Check($"the first partial beats the unmeasured fallback on every long file " +
              $"(worst first-slice error {worstFirstDelta:F2} dB)", firstBeatsFallbackOk, firstFailure);
    }

    // ── A decode that stops early must not become a measurement ──────────────
    // Every decoder in the read table reports EOF as "0 frames", and dr_flac / dr_mp3 report
    // an IO FAILURE the same way -- so at the read call a drive going away is indistinguishable
    // from reaching the end of the file. Left alone that produces a plausible measurement of
    // the first fraction of the file, committed as final and authoritative, which nothing will
    // ever revisit because HasCurrent then reports it as current.
    //
    // A physically truncated file reproduces this exactly: the header still declares the full
    // duration, and the decoder stops early with the same "0 frames" the real fault produces.
    static void TruncationCases(List<string> corpus)
    {
        // FLAC and MP3 are the formats whose readers conflate the two, and between them they
        // are 82% of this library.
        string src = null;
        foreach (string f in corpus)
        {
            string e = Path.GetExtension(f).ToLowerInvariant();
            if (e == ".flac" || e == ".mp3") { src = f; break; }
        }
        if (src == null) { Skip("truncated decode is rejected", "no .flac/.mp3 in the corpus"); return; }

        string cut = Path.Combine(Path.GetTempPath(),
                                  "cmm_trunc_" + Guid.NewGuid().ToString("N").Substring(0, 8) +
                                  Path.GetExtension(src));
        try
        {
            byte[] all = File.ReadAllBytes(src);
            if (all.Length < 4096) { Skip("truncated decode is rejected", "source too small"); return; }
            // Half the file: far beyond any encoder-padding tolerance, and the shape a drive
            // disappearing mid-decode actually produces.
            File.WriteAllBytes(cut, Sub(all, all.Length / 2));

            TrackLoudness whole = AudioDecoder.MeasureLoudness(src, CMMConfig.LoudnessChunkSeconds, null);
            TrackLoudness half  = AudioDecoder.MeasureLoudness(cut, CMMConfig.LoudnessChunkSeconds, null);

            Check("a file that stops decoding half way is NOT accepted as measured",
                  !half.Ok,
                  half.Ok ? $"accepted: {half.Lufs:F2} LUFS over {half.SecondsMeasured:F1}s " +
                            $"(intact file: {whole.Lufs:F2} over {whole.SecondsMeasured:F1}s)"
                          : null);
            Check("...while the intact file still measures fine", whole.Ok);
        }
        catch (Exception ex) { Skip("truncated decode is rejected", ex.Message); }
        finally { try { File.Delete(cut); } catch { } }
    }

    static byte[] Sub(byte[] src, int len)
    {
        var d = new byte[len];
        Array.Copy(src, d, len);
        return d;
    }

    // ── Corpus ───────────────────────────────────────────────────────────────
    // Arguments first, then CMM_TEST_MUSIC. A directory contributes its supported
    // files; the long ones are what make the multi-slice cases run at all, so they
    // are taken first when a big folder has to be capped.
    static List<string> CollectCorpus(string[] args)
    {
        var roots = new List<string>(args);
        string env = Environment.GetEnvironmentVariable("CMM_TEST_MUSIC");
        if (roots.Count == 0 && !string.IsNullOrEmpty(env)) roots.Add(env);

        var files = new List<string>();
        foreach (string r in roots)
        {
            try
            {
                if (File.Exists(r)) { if (AudioDecoder.IsSupported(r)) files.Add(r); }
                else if (Directory.Exists(r))
                    foreach (string f in Directory.GetFiles(r, "*", SearchOption.AllDirectories))
                        if (AudioDecoder.IsSupported(f)) files.Add(f);
            }
            catch (Exception ex) { Console.WriteLine($"  (skipping {r}: {ex.Message})"); }
        }

        const int Cap = 40;
        if (files.Count > Cap)
        {
            files.Sort((a, b) => new FileInfo(b).Length.CompareTo(new FileInfo(a).Length));
            Console.WriteLine($"  (corpus capped at {Cap} of {files.Count} files, largest first)");
            files = files.GetRange(0, Cap);
        }
        return files;
    }
}
