using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// CmmBench — standalone decode / EBU-R128 micro-benchmark.
//
// Drives the mod's REAL AudioDecoder.MeasureLoudness (and, with --open, the
// bare-handle open path used at a context transition: AudioDecoder.OpenNativeHandle
// → cmm_mix_add) over a set of audio files, timing each and aggregating per format.
// This is the fast measure→optimize loop for the CPU-bound sweep work the in-game
// profiler points at — no Unity launch.
//
//   dotnet run --project tools/CmmBench -c Release -- <dir-or-files> [options]
//
// Options:
//   -n <count>   timed repeats per file (default 5; plus 1 warmup, discarded)
//   -v           verbose: enable the decoder's own per-file read/hash/decode split
//   --open       also benchmark OpenNativeHandle (transition open latency)
// ─────────────────────────────────────────────────────────────────────────────

internal static class Program
{
    static readonly string[] Exts = { ".mp3", ".ogg", ".oga", ".opus", ".wav", ".wave", ".aiff", ".aif", ".flac", ".m4a", ".mp4", ".aac", ".m4v", ".3gp", ".mov", ".mkv", ".webm" };

    static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }

        // Precise per-AppDomain allocation tracking (available on .NET Framework),
        // so a decode change's allocation delta is measurable, not just GC counts.
        AppDomain.MonitoringIsEnabled = true;

        int repeats = 5;
        bool verbose = false, benchOpen = false, verifyFileOpen = false, cold = false, probe = false, seektest = false, probeAll = false;
        var inputs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-n": repeats = int.Parse(args[++i]); break;
                case "-v": verbose = true; break;
                case "--open": benchOpen = true; break;
                case "--verify-fileopen": verifyFileOpen = true; break;
                case "--cold": cold = true; break;
                case "--probe": probe = true; break;
                case "--probeall": probeAll = true; break;
                case "--seektest": seektest = true; break;
                case "-h": case "--help": Usage(); return 0;
                default: inputs.Add(args[i]); break;
            }
        }
        if (repeats < 1) repeats = 1;
        CMMConfig.DebugLogging = verbose;

        // ── Single-file probe (process-isolated hang detection) ──────────────────
        // Opens ONE file through the game's native open_file path (same reader the
        // in-game loudness sweep uses), reads a bounded chunk of PCM to confirm a
        // decodable audio track, and exits with a distinct code. Meant to be driven
        // one-process-per-file with an external timeout: a true native demux hang
        // never returns, so the parent kills THIS process and records it — a hang
        // can't be caught in-process (Thread.Abort won't interrupt a stuck P/Invoke).
        //   exit 0 = OK   2 = OPEN-FAIL   3 = ZERO-FRAMES/no-audio   4 = bad-args
        if (probe)
        {
            if (inputs.Count != 1) { Console.WriteLine("PROBE bad-args (need exactly one file)"); return 4; }
            return Probe(inputs[0]);
        }

        var files = CollectFiles(inputs);
        if (probeAll) return ProbeAll(files);
        if (seektest) return SeekTest(files);
        if (verifyFileOpen) return VerifyFileOpen(files);
        if (cold) return RunCold(files, repeats);
        if (files.Count == 0) { Console.WriteLine("No supported audio files found."); return 1; }

        Console.WriteLine($"CmmBench — {files.Count} file(s), {repeats} timed repeat(s) each, open={benchOpen}\n");

        var results = new List<Sample>();

        foreach (string f in files)
        {
            if (!AudioDecoder.IsSupported(f)) continue;
            long bytes = new FileInfo(f).Length;
            var s = new Sample { Path = f, Ext = Path.GetExtension(f).ToLowerInvariant(), Bytes = bytes };

            // Warmup (JIT + file cache), discarded for timing — but its result is the
            // correctness anchor (LUFS/peak), printed so a decode change can be verified.
            var warm = AudioDecoder.MeasureLoudness(f);
            if (!warm.Ok) { Console.WriteLine($"  SKIP (measure failed): {Path.GetFileName(f)}"); continue; }
            s.Lufs = warm.Lufs; s.Peak = warm.Peak;

            var measMs = new double[repeats];
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long allocBefore = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            for (int r = 0; r < repeats; r++)
            {
                long t = Stopwatch.GetTimestamp();
                var res = AudioDecoder.MeasureLoudness(f);
                measMs[r] = Ms(t);
                if (!res.Ok) { measMs[r] = double.NaN; }
            }
            s.AllocMB = (AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocBefore)
                        / 1024.0 / 1024.0 / repeats;
            s.MeasureMs = measMs.Where(x => !double.IsNaN(x)).DefaultIfEmpty(0).ToArray();
            s.GcGen0 = GC.CollectionCount(0) - g0;
            s.GcGen1 = GC.CollectionCount(1) - g1;
            s.GcGen2 = GC.CollectionCount(2) - g2;

            if (benchOpen)
            {
                var openMs = new double[repeats];
                for (int r = 0; r < repeats; r++)
                {
                    long t = Stopwatch.GetTimestamp();
                    IntPtr dec = AudioDecoder.OpenNativeHandle(f, out int kind, out _, out _, out _);
                    openMs[r] = Ms(t);
                    AudioDecoder.CloseNativeHandle(dec, kind);
                }
                s.OpenMs = openMs;
            }

            results.Add(s);
            PrintFile(s, benchOpen);
        }

        PrintAggregate(results, benchOpen);
        return 0;
    }

    // ── Reporting ───────────────────────────────────────────────────────────
    static void PrintFile(Sample s, bool benchOpen)
    {
        double avg = s.MeasureMs.Average(), min = s.MeasureMs.Min(), max = s.MeasureMs.Max();
        double mbps = s.Bytes / 1024.0 / 1024.0 / (avg / 1000.0);
        string open = benchOpen ? $"  open avg {s.OpenMs.Average():F2}ms" : "";
        Console.WriteLine($"  {Path.GetFileName(s.Path),-44} {s.Ext,-5} " +
                          $"{s.Bytes / 1024.0 / 1024.0,6:F1}MB  " +
                          $"measure avg {avg,7:F2}ms (min {min:F2} max {max:F2})  " +
                          $"{mbps,6:F1} MB/s  alloc {s.AllocMB,6:F1}MB ({s.AllocMB / (s.Bytes / 1024.0 / 1024.0),5:F1}x)  " +
                          $"gc[{s.GcGen0}/{s.GcGen1}/{s.GcGen2}]  {s.Lufs,6:F1} LUFS{open}");
    }

    static void PrintAggregate(List<Sample> results, bool benchOpen)
    {
        if (results.Count == 0) return;
        Console.WriteLine("\n── By format ──────────────────────────────────────────────────────────");
        foreach (var g in results.GroupBy(r => r.Ext).OrderBy(g => g.Key))
        {
            var all = g.SelectMany(r => r.MeasureMs).ToArray();
            double mb = g.Sum(r => r.Bytes) / 1024.0 / 1024.0;
            double totalMs = g.Sum(r => r.MeasureMs.Average());
            string open = benchOpen
                ? $"  open avg {g.SelectMany(r => r.OpenMs).Average():F2}ms"
                : "";
            Console.WriteLine($"  {g.Key,-6} n={g.Count(),-3} " +
                              $"measure avg {all.Average(),7:F2}ms  max {all.Max(),7:F2}ms  " +
                              $"{mb / (totalMs / 1000.0),6:F1} MB/s{open}");
        }

        var flat = results.SelectMany(r => r.MeasureMs).OrderBy(x => x).ToArray();
        Console.WriteLine($"\n  TOTAL files={results.Count}  " +
                          $"measure p50 {Pct(flat, 50):F2}ms  p95 {Pct(flat, 95):F2}ms  " +
                          $"max {flat.Last():F2}ms  " +
                          $"gc total [{results.Sum(r => r.GcGen0)}/{results.Sum(r => r.GcGen1)}/{results.Sum(r => r.GcGen2)}]");
    }

    static double Pct(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        int idx = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Max(0, Math.Min(sorted.Length - 1, idx))];
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    static double Ms(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;

    static List<string> CollectFiles(List<string> inputs)
    {
        var files = new List<string>();
        foreach (string p in inputs)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)
                    .Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant())));
            else if (File.Exists(p))
            {
                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext == ".m3u" || ext == ".m3u8")
                {
                    // Expand playlist entries (relative paths resolve against the playlist's folder),
                    // matching how a station's PlaylistSource sees them.
                    string baseDir = Path.GetDirectoryName(Path.GetFullPath(p));
                    foreach (string raw in File.ReadAllLines(p))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        string full = Path.IsPathRooted(line) ? line : Path.Combine(baseDir, line);
                        if (File.Exists(full) && Exts.Contains(Path.GetExtension(full).ToLowerInvariant()))
                            files.Add(full);
                    }
                }
                else files.Add(p);
            }
            else
                Console.WriteLine($"WARN: not found: {p}");
        }
        return files.Distinct().OrderBy(f => f).ToList();
    }

    // ── Bare-handle codec dispatch (bench-only: verify / cold paths) ──────────
    // Routes by extension to the same cmm_* exports the mod's OpenNativeHandle
    // uses. (.ogg is assumed Vorbis, matching the test set — no opus-in-ogg here.)
    delegate IntPtr OpenMemFn(byte[] data, long len, out int ch, out int sr, out long total, out int err);
    delegate IntPtr OpenFileFn(string path, out int ch, out int sr, out long total, out int err);
    delegate int    ReadFn(IntPtr d, float[] buf, int maxFrames);
    delegate void   CloseFn(IntPtr d);

    sealed class Codec
    {
        public OpenMemFn OpenMem; public OpenFileFn OpenFile; public ReadFn Read; public CloseFn Close;
    }

    static Codec CodecFor(string ext)
    {
        switch (ext)
        {
            case ".opus":
                return new Codec { OpenMem = NativeOpus.cmm_opus_open_memory, OpenFile = NativeOpus.cmm_opus_open_file,
                                   Read = NativeOpus.cmm_opus_read_float, Close = NativeOpus.cmm_opus_close };
            case ".flac":
                return new Codec { OpenMem = NativeFlac.cmm_flac_open_memory, OpenFile = NativeFlac.cmm_flac_open_file,
                                   Read = NativeFlac.cmm_flac_read_float, Close = NativeFlac.cmm_flac_close };
            case ".ogg":
            case ".oga":
                return new Codec { OpenMem = NativeVorbis.cmm_vorbis_open_memory, OpenFile = NativeVorbis.cmm_vorbis_open_file,
                                   Read = NativeVorbis.cmm_vorbis_read_float, Close = NativeVorbis.cmm_vorbis_close };
            case ".mp3":
                return new Codec { OpenMem = NativeMp3.cmm_mp3_open_memory, OpenFile = NativeMp3.cmm_mp3_open_file,
                                   Read = NativeMp3.cmm_mp3_read_float, Close = NativeMp3.cmm_mp3_close };
            case ".wav":
            case ".wave":
            case ".aiff":
            case ".aif":
                return new Codec { OpenMem = NativeWav.cmm_wav_open_memory, OpenFile = NativeWav.cmm_wav_open_file,
                                   Read = NativeWav.cmm_wav_read_float, Close = NativeWav.cmm_wav_close };
            case ".m4a":
            case ".mp4":
            case ".aac":
            case ".m4v":
            case ".3gp":
            case ".mov":
                return new Codec { OpenMem = NativeAac.cmm_aac_open_memory, OpenFile = NativeAac.cmm_aac_open_file,
                                   Read = NativeAac.cmm_aac_read_float, Close = NativeAac.cmm_aac_close };
            case ".mkv":
            case ".webm":
                return new Codec { OpenMem = NativeContainer.cmm_container_open_memory, OpenFile = NativeContainer.cmm_container_open_file,
                                   Read = NativeContainer.cmm_container_read_float, Close = NativeContainer.cmm_container_close };
            default: return null;
        }
    }

    // Read a handle to EOF, tracking frame count + sample peak, then close it.
    static (long frames, float peak) DrainAndClose(IntPtr dec, Codec c, int channels)
    {
        var buf = new float[Math.Max(1, channels) * 8192];
        long frames = 0; float peak = 0f;
        int n;
        while ((n = c.Read(dec, buf, 8192)) > 0)
        {
            int floats = n * channels;
            for (int i = 0; i < floats; i++) { float a = Math.Abs(buf[i]); if (a > peak) peak = a; }
            frames += n;
        }
        c.Close(dec);
        return (frames, peak);
    }

    // ── Batch length-probe throughput ─────────────────────────────────────────
    // Times the EXACT per-track cost of RadioStationCatalog.ProbeLength — one
    // AudioDecoder.OpenNativeHandle (header parse yields total frames) + close per
    // file, no PCM read — over the whole input set, back to back with no budget
    // throttle. Answers "how long does it take to make a station of N tracks
    // schedule-Ready?" Run it twice: pass 1 ≈ cold-ish OS cache, pass 2 = warm.
    static int ProbeAll(List<string> files)
    {
        if (files.Count == 0) { Console.WriteLine("No supported audio files found."); return 1; }
        Console.WriteLine($"CmmBench --probeall — {files.Count} file(s)\n");

        var perExtMs = new Dictionary<string, List<double>>();
        int ok = 0, fail = 0;
        double worstMs = 0; string worstFile = null;
        long swAll = Stopwatch.GetTimestamp();

        foreach (string f in files)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            long t = Stopwatch.GetTimestamp();
            IntPtr dec = AudioDecoder.OpenNativeHandle(f, out int kind, out _, out int rate, out long total);
            if (dec != IntPtr.Zero) AudioDecoder.CloseNativeHandle(dec, kind);
            double ms = Ms(t);

            bool good = dec != IntPtr.Zero && total > 0 && rate > 0;
            if (good) ok++; else { fail++; Console.WriteLine($"  FAIL {Path.GetFileName(f)} ({ext})"); }
            if (!perExtMs.TryGetValue(ext, out var list)) perExtMs[ext] = list = new List<double>();
            list.Add(ms);
            if (ms > worstMs) { worstMs = ms; worstFile = f; }
        }

        double totalS = Ms(swAll) / 1000.0;
        Console.WriteLine("\n── Probe throughput by format ─────────────────────────────────────────");
        foreach (var kv in perExtMs.OrderBy(k => k.Key))
        {
            var sorted = kv.Value.OrderBy(x => x).ToArray();
            Console.WriteLine($"  {kv.Key,-6} n={sorted.Length,-4} avg {sorted.Average(),7:F2}ms  " +
                              $"p50 {Pct(sorted, 50),7:F2}ms  p95 {Pct(sorted, 95),7:F2}ms  max {sorted.Last(),8:F2}ms");
        }
        Console.WriteLine($"\n  TOTAL {files.Count} probes in {totalS:F2}s  =  {files.Count / totalS:F1} probes/s  " +
                          $"(ok {ok}, fail {fail})");
        if (worstFile != null)
            Console.WriteLine($"  slowest: {worstMs:F1}ms  {worstFile}");
        return fail == 0 ? 0 : 2;
    }

    // ── Single-file probe ─────────────────────────────────────────────────────
    // Opens via the same native open_file path the in-game sweep uses (routed by
    // extension through CodecFor), reads up to ~10 s of PCM to prove a decodable
    // audio track exists, then closes. Prints one machine-parseable line and returns
    // a distinct exit code. If the native call hangs, this never returns and the
    // parent process (with a timeout) records it as HANG.
    static int Probe(string f)
    {
        string ext = Path.GetExtension(f).ToLowerInvariant();
        Codec c = CodecFor(ext);
        if (c == null) { Console.WriteLine($"PROBE not-native ext={ext}"); return 2; }

        int err = 0, ch = 0, sr = 0;
        long total = 0;
        IntPtr dec = c.OpenFile(f, out ch, out sr, out total, out err);
        if (dec == IntPtr.Zero)
        {
            Console.WriteLine($"PROBE open-fail ext={ext} err={err}");
            return 2;
        }

        // Read a bounded chunk (~10 s) — enough to exercise the demux/decode loop
        // without draining multi-GB video files. A hang inside here also never returns.
        int channels = Math.Max(1, ch);
        int rate = sr > 0 ? sr : 48000;
        long frameCap = (long)rate * 10;
        var buf = new float[channels * 8192];
        long frames = 0; float peak = 0f; int n;
        while (frames < frameCap && (n = c.Read(dec, buf, 8192)) > 0)
        {
            int floats = n * channels;
            for (int i = 0; i < floats; i++) { float a = Math.Abs(buf[i]); if (a > peak) peak = a; }
            frames += n;
        }
        c.Close(dec);

        if (frames == 0)
        {
            Console.WriteLine($"PROBE zero-frames ext={ext} ch={ch} sr={sr}");
            return 3;
        }
        Console.WriteLine($"PROBE ok ext={ext} ch={ch} sr={sr} frames={frames} peak={peak:F4}");
        return 0;
    }

    // ── Container loop/seek verification ──────────────────────────────────────
    // Drives cmm_container_seek(0) — the exact call the native mixer makes to LOOP a
    // track — and asserts the second decode pass is byte-identical to the first
    // (frame count + sample peak). Also seeks to the midpoint and checks the tail
    // matches the first pass's tail. Proves the streaming demuxer rebuilds from the
    // start correctly (no Cues index), so a looped mkv/webm bed doesn't drift or die.
    static int SeekTest(List<string> files)
    {
        int failures = 0;
        foreach (string f in files)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext != ".mkv" && ext != ".webm") { Console.WriteLine($"  SKIP (not container): {Path.GetFileName(f)}"); continue; }

            IntPtr dec = NativeContainer.cmm_container_open_file(f, out int ch, out _, out _, out int err);
            if (dec == IntPtr.Zero) { failures++; Console.WriteLine($"  {Path.GetFileName(f),-30} OPEN FAIL err={err}  FAIL"); continue; }

            (long f1, float p1) = Drain(dec, ch);
            int s0 = NativeContainer.cmm_container_seek(dec, 0);
            (long f2, float p2) = Drain(dec, ch);
            // Mid-seek: rebuild + discard to the midpoint, then drain the tail.
            long mid = f1 / 2;
            int sM = NativeContainer.cmm_container_seek(dec, mid);
            (long f3, float _) = Drain(dec, ch);
            NativeContainer.cmm_container_close(dec);

            bool loopOk = (s0 == 0) && f1 == f2 && Math.Abs(p1 - p2) < 1e-6f;
            bool midOk  = (sM == 0) && Math.Abs((f1 - mid) - f3) <= 1152;   // ~1 packet slack
            bool ok = loopOk && midOk;
            if (!ok) failures++;
            Console.WriteLine($"  {Path.GetFileName(f),-28} {ext,-5} pass1[frames={f1} peak={p1:F5}] " +
                              $"loop[frames={f2} peak={p2:F5}] mid@{mid}[tail={f3}]  {(ok ? "PASS" : "FAIL")}");
        }
        Console.WriteLine($"\nseektest: {files.Count - failures}/{files.Count} passed.");
        return failures == 0 ? 0 : 1;
    }

    static (long frames, float peak) Drain(IntPtr dec, int channels)
    {
        int ch = Math.Max(1, channels);
        var buf = new float[ch * 8192];
        long frames = 0; float peak = 0f; int n;
        while ((n = NativeContainer.cmm_container_read_float(dec, buf, 8192)) > 0)
        {
            int floats = n * ch;
            for (int i = 0; i < floats; i++) { float a = Math.Abs(buf[i]); if (a > peak) peak = a; }
            frames += n;
        }
        return (frames, peak);
    }

    // ── File-open verification ────────────────────────────────────────────────
    // Decodes each file through BOTH native paths — open_memory (buffered bytes) and
    // open_file (the streaming / large-file path) — and asserts identical output
    // (frame count + sample peak). Proves the file-open callbacks/seek/close are
    // correct without needing an actual >100 MB file.
    static int VerifyFileOpen(List<string> files)
    {
        int failures = 0;
        foreach (string f in files)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            Codec c = CodecFor(ext);
            if (c == null) { Console.WriteLine($"  SKIP (not native): {Path.GetFileName(f)}"); continue; }

            byte[] raw = File.ReadAllBytes(f);
            IntPtr mem = c.OpenMem(raw, raw.Length, out int mch, out _, out _, out int merr);
            IntPtr fil = c.OpenFile(f, out int fch, out _, out _, out int ferr);
            if (mem == IntPtr.Zero || fil == IntPtr.Zero)
            {
                if (mem != IntPtr.Zero) c.Close(mem);
                if (fil != IntPtr.Zero) c.Close(fil);
                failures++;
                Console.WriteLine($"  {Path.GetFileName(f),-30} {ext,-5} OPEN FAIL (mem err={merr}, file err={ferr})  FAIL");
                continue;
            }

            (long mf, float mpk) = DrainAndClose(mem, c, mch);
            (long ff, float fpk) = DrainAndClose(fil, c, fch);

            bool ok = mf == ff && Math.Abs(mpk - fpk) < 1e-6f;
            if (!ok) failures++;
            Console.WriteLine($"  {Path.GetFileName(f),-30} {ext,-5} mem[frames={mf} peak={mpk:F5}]  " +
                              $"file[frames={ff} peak={fpk:F5}]  {(ok ? "PASS" : "FAIL")}");
        }
        Console.WriteLine($"\nverify-fileopen: {files.Count - failures}/{files.Count} matched.");
        return failures == 0 ? 0 : 1;
    }

    // ── Cold-load benchmark ───────────────────────────────────────────────────
    // The default run warms the OS file cache (the warmup pass), so its timings are
    // decode-from-RAM. This mode instead reads every file CACHE-BYPASSED
    // (FILE_FLAG_NO_BUFFERING) so the bytes come off the physical device — the true
    // "load this track cold" cost on slow media (SD card / J:). Splits the cost into
    // the device READ (size / device MB/s) and the cache-independent DECODE (a full
    // open_memory + drain-to-EOF, CPU only), and reports their sum.
    static int RunCold(List<string> files, int repeats)
    {
        var supported = files.Where(AudioDecoder.IsSupported).ToList();
        if (supported.Count == 0) { Console.WriteLine("No supported audio files found."); return 1; }

        Console.WriteLine($"CmmBench --cold — {supported.Count} file(s), cache BYPASSED (FILE_FLAG_NO_BUFFERING), " +
                          $"{repeats} repeat(s) each\n");
        Console.WriteLine($"  {"file",-44} {"ext",-5} {"MB",6}  {"read",8} {"MB/s",7}  {"decode",9}  {"total",9}");

        var rows = new List<ColdRow>();
        foreach (string f in supported)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            Codec c = CodecFor(ext);
            if (c == null) continue;

            double mb = new FileInfo(f).Length / 1024.0 / 1024.0;
            var readMs = new double[repeats];
            var decMs = new double[repeats];
            bool ok = true;
            for (int r = 0; r < repeats; r++)
            {
                byte[] bytes;
                try { (bytes, readMs[r]) = CmmBench.ColdRead.Read(f); }
                catch (Exception ex) { Console.WriteLine($"  COLD-READ ERR {Path.GetFileName(f)}: {ex.Message}"); ok = false; break; }

                long t = Stopwatch.GetTimestamp();
                IntPtr dec = c.OpenMem(bytes, bytes.Length, out int ch, out _, out _, out _);
                if (dec == IntPtr.Zero) { Console.WriteLine($"  DECODE ERR {Path.GetFileName(f)}"); ok = false; break; }
                (long frames, _) = DrainAndClose(dec, c, ch);
                decMs[r] = Ms(t);
                if (frames == 0) { Console.WriteLine($"  DECODE ERR (0 frames) {Path.GetFileName(f)}"); ok = false; break; }
            }
            if (!ok) continue;

            var row = new ColdRow
            {
                Ext = ext,
                MB = mb,
                ReadMs = readMs.Average(),
                DecodeMs = decMs.Average(),
            };
            rows.Add(row);
            Console.WriteLine($"  {Path.GetFileName(f),-44} {row.Ext,-5} {mb,6:F1}  " +
                              $"{row.ReadMs,7:F1}ms {mb / (row.ReadMs / 1000.0),6:F1}  " +
                              $"{row.DecodeMs,8:F1}ms  {row.ReadMs + row.DecodeMs,8:F1}ms");
        }

        if (rows.Count == 0) { Console.WriteLine("\n(no files measured)"); return 1; }

        Console.WriteLine("\n── Cold load by format ────────────────────────────────────────────────");
        foreach (var g in rows.GroupBy(r => r.Ext).OrderBy(g => g.Key))
        {
            double mb = g.Sum(r => r.MB), readMs = g.Sum(r => r.ReadMs), decMs = g.Sum(r => r.DecodeMs);
            Console.WriteLine($"  {g.Key,-6} n={g.Count(),-3} " +
                              $"read {mb / (readMs / 1000.0),6:F1} MB/s  " +
                              $"avg read {readMs / g.Count(),7:F1}ms  decode {decMs / g.Count(),7:F1}ms  " +
                              $"total {(readMs + decMs) / g.Count(),7:F1}ms");
        }

        double allMb = rows.Sum(r => r.MB), allRead = rows.Sum(r => r.ReadMs);
        var totals = rows.Select(r => r.ReadMs + r.DecodeMs).OrderBy(x => x).ToArray();
        Console.WriteLine($"\n  DEVICE cold read: {allMb / (allRead / 1000.0):F1} MB/s over {allMb:F0} MB" +
                          $"   ·   cold-load total p50 {Pct(totals, 50):F1}ms  p95 {Pct(totals, 95):F1}ms  max {totals.Last():F1}ms");
        return 0;
    }

    sealed class ColdRow { public string Ext; public double MB, ReadMs, DecodeMs; }

    static void Usage()
    {
        Console.WriteLine(
            "CmmBench — standalone decode / EBU-R128 micro-benchmark\n\n" +
            "  dotnet run --project tools/CmmBench -c Release -- <dir-or-files> [options]\n\n" +
            "Options:\n" +
            "  -n <count>         timed repeats per file (default 5; +1 warmup)\n" +
            "  -v                 verbose per-file read/hash/index/decode split\n" +
            "  --open             also benchmark OpenNativeHandle (transition open latency)\n" +
            "  --verify-fileopen  decode via open_memory AND open_file, assert identical output\n" +
            "  --cold             cache-bypassed cold read + full decode (true load-from-disk cost)\n");
    }

    sealed class Sample
    {
        public string Path, Ext;
        public long Bytes;
        public double[] MeasureMs = Array.Empty<double>();
        public double[] OpenMs = Array.Empty<double>();
        public int GcGen0, GcGen1, GcGen2;
        public double Lufs, Peak, AllocMB;
    }
}
