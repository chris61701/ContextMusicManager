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
// streaming-open path used at a context transition) over a set of audio files,
// timing each and aggregating per format. This is the fast measure→optimize loop
// for the CPU-bound sweep work the in-game profiler points at — no Unity launch.
//
//   dotnet run --project tools/CmmBench -c Release -- <dir-or-files> [options]
//
// Options:
//   -n <count>   timed repeats per file (default 5; plus 1 warmup, discarded)
//   -v           verbose: enable the decoder's own per-file read/hash/decode split
//   --open       also benchmark OpenStreamingReader (transition open latency)
//   --no-scratch don't reuse the MeasureScratch buffer (measures per-file LOH cost)
// ─────────────────────────────────────────────────────────────────────────────

internal static class Program
{
    static readonly string[] Exts = { ".mp3", ".ogg", ".oga", ".opus", ".wav", ".wave", ".aiff", ".aif", ".flac", ".m4a", ".mp4", ".aac", ".m4v", ".3gp", ".mkv", ".webm" };

    static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }

        // Precise per-AppDomain allocation tracking (available on .NET Framework),
        // so a decode change's allocation delta is measurable, not just GC counts.
        AppDomain.MonitoringIsEnabled = true;

        int repeats = 5;
        bool verbose = false, benchOpen = false, useScratch = true, verifyFileOpen = false, cold = false;
        var inputs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-n": repeats = int.Parse(args[++i]); break;
                case "-v": verbose = true; break;
                case "--open": benchOpen = true; break;
                case "--no-scratch": useScratch = false; break;
                case "--verify-fileopen": verifyFileOpen = true; break;
                case "--cold": cold = true; break;
                case "-h": case "--help": Usage(); return 0;
                default: inputs.Add(args[i]); break;
            }
        }
        if (repeats < 1) repeats = 1;
        CMMConfig.DebugLogging = verbose;

        var files = CollectFiles(inputs);
        if (verifyFileOpen) return VerifyFileOpen(files);
        if (cold) return RunCold(files, repeats);
        if (files.Count == 0) { Console.WriteLine("No supported audio files found."); return 1; }

        Console.WriteLine($"CmmBench — {files.Count} file(s), {repeats} timed repeat(s) each, " +
                          $"scratch={(useScratch ? "reused" : "per-file")}, open={benchOpen}\n");

        // One shared scratch buffer mirrors the real serial sweep (no per-file LOH alloc).
        var scratch = useScratch ? new AudioDecoder.MeasureScratch() : null;
        var results = new List<Sample>();

        foreach (string f in files)
        {
            if (!AudioDecoder.IsSupported(f)) continue;
            long bytes = new FileInfo(f).Length;
            var s = new Sample { Path = f, Ext = Path.GetExtension(f).ToLowerInvariant(), Bytes = bytes };

            // Warmup (JIT + file cache), discarded for timing — but its result is the
            // correctness anchor (LUFS/peak), printed so a decode change can be verified.
            var warm = AudioDecoder.MeasureLoudness(f, useScratch ? new AudioDecoder.MeasureScratch() : null);
            if (!warm.Ok) { Console.WriteLine($"  SKIP (measure failed): {Path.GetFileName(f)}"); continue; }
            s.Lufs = warm.Lufs; s.Peak = warm.Peak;

            var measMs = new double[repeats];
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long allocBefore = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            for (int r = 0; r < repeats; r++)
            {
                long t = Stopwatch.GetTimestamp();
                var res = AudioDecoder.MeasureLoudness(f, scratch);
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
                    var rdr = AudioDecoder.OpenStreamingReader(f);
                    openMs[r] = Ms(t);
                    rdr?.Dispose();
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
                files.Add(p);
            else
                Console.WriteLine($"WARN: not found: {p}");
        }
        return files.Distinct().OrderBy(f => f).ToList();
    }

    // ── File-open verification ────────────────────────────────────────────────
    // Decodes each file through BOTH native paths — open_memory (buffered bytes) and
    // open_file (FILE* + _wfopen, the large-file path) — and asserts identical output
    // (frame count + sample peak). Proves the file-open callbacks/seek/close are
    // correct without needing an actual >100 MB file. (Routes by extension; assumes
    // .ogg = Vorbis, matching the test set — no opus-in-ogg here.)
    static int VerifyFileOpen(List<string> files)
    {
        int failures = 0;
        foreach (string f in files)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            byte[] raw = File.ReadAllBytes(f);
            WaveStream mem, fil;
            switch (ext)
            {
                case ".opus":       mem = new NativeOggOpusReader(raw, raw.Length); fil = new NativeOggOpusReader(f); break;
                case ".flac":       mem = new NativeFlacReader(raw, raw.Length);    fil = new NativeFlacReader(f);    break;
                case ".ogg":
                case ".oga":        mem = new NativeVorbisReader(raw, raw.Length);  fil = new NativeVorbisReader(f);  break;
                case ".mp3":        mem = new NativeMp3Reader(raw, raw.Length);     fil = new NativeMp3Reader(f);     break;
                case ".wav":
                case ".wave":
                case ".aiff":
                case ".aif":        mem = new NativeWavReader(raw, raw.Length);     fil = new NativeWavReader(f);     break;
                case ".m4a":
                case ".mp4":
                case ".aac":
                case ".m4v":
                case ".3gp":        mem = new NativeAacReader(raw, raw.Length);     fil = new NativeAacReader(f);     break;
                case ".mkv":
                case ".webm":       mem = new NativeContainerReader(raw, raw.Length); fil = new NativeContainerReader(f); break;
                default: Console.WriteLine($"  SKIP (not native): {Path.GetFileName(f)}"); continue;
            }

            long mf, ff; float mpk, fpk;
            using (mem) (mf, mpk) = Drain(mem);
            using (fil) (ff, fpk) = Drain(fil);

            bool ok = mf == ff && Math.Abs(mpk - fpk) < 1e-6f;
            if (!ok) failures++;
            Console.WriteLine($"  {Path.GetFileName(f),-30} {ext,-5} mem[frames={mf} peak={mpk:F5}]  " +
                              $"file[frames={ff} peak={fpk:F5}]  {(ok ? "PASS" : "FAIL")}");
        }
        Console.WriteLine($"\nverify-fileopen: {files.Count - failures}/{files.Count} matched.");
        return failures == 0 ? 0 : 1;
    }

    static (long frames, float peak) Drain(WaveStream ws)
    {
        var src = (IFloatPcmSource)ws;
        int ch = ws.WaveFormat.Channels;
        var buf = new float[ch * 8192];
        long frames = 0; float peak = 0f;
        int n;
        while ((n = src.ReadFloats(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++) { float a = Math.Abs(buf[i]); if (a > peak) peak = a; }
            frames += n / ch;
        }
        return (frames, peak);
    }

    // ── Cold-load benchmark ───────────────────────────────────────────────────
    // The default run warms the OS file cache (the warmup pass), so its timings are
    // decode-from-RAM. This mode instead reads every file CACHE-BYPASSED
    // (FILE_FLAG_NO_BUFFERING) so the bytes come off the physical device — the true
    // "load this track cold" cost on slow media (SD card / J:). Splits the cost into
    // the device READ (size / device MB/s) and the cache-independent DECODE (CPU), and
    // reports their sum — the full-decode preload path (Menu / PreloadCacheEnabled)
    // pays read+decode; streaming formats only read headers at open and page the rest
    // on the audio thread, so for them the read column is the upper bound, not the
    // transition cost.
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
                var decoded = AudioDecoder.DecodeFileFromBytes(bytes, f);
                decMs[r] = Ms(t);
                if (decoded == null) { Console.WriteLine($"  DECODE ERR {Path.GetFileName(f)}"); ok = false; break; }
            }
            if (!ok) continue;

            var row = new ColdRow
            {
                Ext = Path.GetExtension(f).ToLowerInvariant(),
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
            "  -n <count>     timed repeats per file (default 5; +1 warmup)\n" +
            "  -v             verbose per-file read/hash/index/decode split\n" +
            "  --open         also benchmark OpenStreamingReader (transition open latency)\n" +
            "  --no-scratch   don't reuse the MeasureScratch buffer (shows per-file LOH cost)\n" +
            "  --cold         cache-bypassed cold read + decode (true load-from-disk cost on slow media)\n");
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
