using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────────────────
    // CmmEmitSim --reverb — WHAT DOES THE ROOM ACTUALLY SOUND LIKE?
    //
    //   dotnet run --project tools/CmmEmitSim -c Release -- --reverb
    //   dotnet run --project tools/CmmEmitSim -c Release -- --reverb --accept   (record a baseline)
    //
    // Fires an impulse out of a world speaker, captures what comes back, and measures the decay.
    // Every stage is the shipped one: the real ProbeRoom→ComputeReverb mapping (ReverbModel), the
    // real per-slot reverb send, the real Freeverb in cmm_reverb.h, the real spatialiser. The
    // scenario supplies only the geometry a voxel probe would have measured — "you are standing
    // in a 60-block stone cavern" — because that needs a world and nothing else here does.
    //
    // WHY IT EXISTS. The complaint is that a cavern gives no echo, and there are four candidate
    // causes with completely different fixes:
    //
    //   1. the comb delays in cmm_reverb.h are fixed at 25-37 ms, which IS a 4-6 m room, and no
    //      amount of feedback makes a small room into a big one -- it only makes it ring longer
    //   2. nothing produces a discrete early reflection, and "echo" means exactly that
    //   3. sizeTail saturates at 20 blocks, so a cavern and a large room ask for the same tail
    //   4. the material under a cave may report as absorptive, which is a table, not a DSP job
    //
    // Guessing between those costs a day each. Measuring separates them in four seconds:
    // RT60 says how long, ECHO DENSITY says whether it is a room or a smear, and the COMB
    // SIGNATURE reads the delay times straight back out of the tail, which is cause 1 stated as
    // a number. After a change, the same table says whether it moved.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    internal static class ReverbLab
    {
        internal const int Rate = 48000;

        // A room the lab can put you in. avgDist is the ray probe's mean sightline; size is the
        // longest one; reflect is the material; openFrac is how much of the sky is showing.
        internal struct Scene
        {
            public string Name;
            public float AvgDist, Size, Reflect, OpenFrac;
            public bool  Roofed;
        }

        // Chosen so that each pair differs in ONE thing, which is what makes the table readable.
        // The two caves differ only in material, because that is candidate 4 and it is free to
        // rule out. The cavern and the hall differ only in size, which is candidate 3.
        static readonly Scene[] Scenes =
        {
            new Scene { Name = "small stone room", AvgDist =  3f, Size =   6f, Reflect = 0.90f, OpenFrac = 0.05f, Roofed = true  },
            new Scene { Name = "wood cabin",       AvgDist =  3f, Size =   6f, Reflect = 0.45f, OpenFrac = 0.05f, Roofed = true  },
            new Scene { Name = "concrete hall",    AvgDist = 12f, Size =  20f, Reflect = 0.90f, OpenFrac = 0.05f, Roofed = true  },
            new Scene { Name = "stone cavern",     AvgDist = 25f, Size =  60f, Reflect = 0.90f, OpenFrac = 0.02f, Roofed = true  },
            new Scene { Name = "huge cavern",      AvgDist = 60f, Size = 150f, Reflect = 0.90f, OpenFrac = 0.02f, Roofed = true  },
            new Scene { Name = "dirt cave",        AvgDist = 25f, Size =  60f, Reflect = 0.30f, OpenFrac = 0.02f, Roofed = true  },
            new Scene { Name = "open field",       AvgDist = 40f, Size =  80f, Reflect = 0.60f, OpenFrac = 0.80f, Roofed = false },
        };

        internal sealed class Result
        {
            public string Name;
            public float Wet, Room, Damp;      // what the rule asked for
            public float Rt60, Edt;            // seconds
            public float FirstMs;              // direct → first reflected energy
            public float DensityPerSec;        // distinct peaks in the first 150 ms
            public float CombMs;               // strongest repeat period found in the tail
            public float TailDb;               // level 250 ms after the direct, re the direct
            public bool  Rt60Clipped;          // the decay outlasted the capture: RT60 is a floor
            public float NoiseDb;              // the last 500 ms, re the peak: the floor everything sits on
        }

        // ONE PROCESS PER ROOM, for the reason the scenarios already are.
        //
        // Measuring all seven in one process gave one good answer and six "the impulse never
        // arrived". Repeating the burst did not help, because the fault was never the broadcast
        // position: EmitterAudioLayer is a static singleton with twelve-second grace timers, a
        // pending-start latch and a native mixer that cmm_mix_start_ex will not re-enter cleanly,
        // so the second room inherits the first room's teardown. Resetting all of that from the
        // outside would mean modelling it, which is the mistake this whole harness exists to
        // avoid. The process boundary is the only reset that cannot drift.
        internal static int Run(string root, bool accept)
        {
            string one = Program.Arg(Environment.GetCommandLineArgs(), "--reverb-scene");
            if (one != null) return Child(root, int.Parse(one));

            Console.WriteLine("CmmEmitSim --reverb — an impulse in seven rooms, through the shipped reverb");
            Console.WriteLine();

            var results = new List<Result>();
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            for (int i = 0; i < Scenes.Length; i++)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, "--reverb --reverb-scene " + i)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var pr = System.Diagnostics.Process.Start(psi))
                {
                    string outp = pr.StandardOutput.ReadToEnd();
                    pr.WaitForExit();
                    Result r = null;
                    foreach (string line in outp.Split('\n'))
                    {
                        string t = line.Trim();
                        if (t.StartsWith("RESULT|")) r = Parse(t);
                        else if (t.StartsWith("note ")) Console.WriteLine("   " + t);
                    }
                    if (r == null)
                    {
                        Console.WriteLine($"   note  {Scenes[i].Name}: the child produced no result (exit {pr.ExitCode})");
                        r = new Result { Name = Scenes[i].Name };
                    }
                    results.Add(r);
                }
            }

            Report(results);
            int fail = Compare(root, results, accept);
            Console.WriteLine();
            Console.WriteLine("  wav   " + Path.Combine(root, "reverb"));
            return fail;
        }

        // The child: build the library, measure ONE room, hand the numbers back as one line.
        static int Child(string root, int index)
        {
            BuildImpulseStation(root);
            UnityEngine.Time.realtimeSinceStartup += 60f;
            RadioStationCatalog.ForceRebuild();
            RadioStationCatalog.EnsureCatalog();
            Result r = Measure(root, Scenes[index]);
            Console.WriteLine($"RESULT|{r.Name}|{r.Wet:0.000}|{r.Room:0.000}|{r.Damp:0.000}|{r.Rt60:0.000}|"
                            + $"{r.Edt:0.000}|{r.FirstMs:0.0}|{r.DensityPerSec:0.0}|{r.CombMs:0.0}|{r.TailDb:0.0}|{r.NoiseDb:0.0}");
            return 0;
        }

        static Result Parse(string line)
        {
            string[] p = line.Split('|');
            if (p.Length < 11) return null;
            return new Result
            {
                Name = p[1], Wet = F(p[2]), Room = F(p[3]), Damp = F(p[4]), Rt60 = F(p[5]),
                Edt = F(p[6]), FirstMs = F(p[7]), DensityPerSec = F(p[8]), CombMs = F(p[9]), TailDb = F(p[10]),
                NoiseDb = p.Length > 11 ? F(p[11]) : -140f,
            };
        }
        static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        // ── the source ───────────────────────────────────────────────────────────────────────
        // A CLICK AND THEN NOTHING. An impulse response is the honest instrument for a reverb:
        // everything after the click is the room, so no assumption about the programme material
        // can creep into the measurement. Twelve seconds of silence after it, because the whole
        // question is how long the tail lasts and a file that ends is a tail that is cut off.
        // How often a burst repeats, and how much clean tail that leaves after each one. The
        // spacing has to exceed the capture or one room's tail lands inside the next measurement.
        internal const int BurstEverySec = 20;
        internal const int CaptureSec    = 12;

        static void BuildImpulseStation(string root)
        {
            string dir = Path.Combine(root, "Radio", "Impulse");
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "01 impulse.wav");
            const int seconds = 300;
            int frames = Rate * seconds;
            using (var w = new BinaryWriter(File.Create(f)))
            {
                int data = frames * 2 * 2;
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
                w.Write((short)1); w.Write((short)2); w.Write(Rate);
                w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(data);
                // A SHORT BURST, NOT ONE SAMPLE. A single sample survives no resampler and no
                // band-limiting, and the emitter chain has both; two milliseconds of full-scale
                // noise carries the same flat spectrum and comes out the other side measurable.
                //
                // AND IT REPEATS, because a station is a BROADCAST. The schedule is anchored to
                // the world clock, so tuning in does not start the file -- it drops you wherever
                // the broadcast currently is, which after the first room measured was eleven
                // seconds of silence. Every room after it reported "the impulse never arrived",
                // which is the anchored schedule working exactly as designed and a lab assuming
                // it was a tape deck. A burst every twenty seconds means one is always coming.
                var rng = new Random(11);
                int burst = Rate / 500;
                int period = Rate * BurstEverySec;
                for (int i = 0; i < frames; i++)
                {
                    short v = (i % period) < burst
                            ? (short)((rng.NextDouble() * 2.0 - 1.0) * 30000) : (short)0;
                    w.Write(v); w.Write(v);
                }
            }
        }

        // ── one room ─────────────────────────────────────────────────────────────────────────
        static Result Measure(string root, Scene sc)
        {
            EmitterAcoustics.ProbeAvgDist  = sc.AvgDist;
            EmitterAcoustics.ProbeRoomSize = sc.Size;
            EmitterAcoustics.ProbeReflect  = sc.Reflect;
            EmitterAcoustics.ProbeOpenFrac = sc.OpenFrac;
            EmitterAcoustics.ProbeRoofed   = sc.Roofed;
            EmitterAcoustics.AirRoomSize   = sc.Size;

            // What the shipped rule asks for, given that room. Reported beside the measurement so
            // "the model asked for a long tail and did not get one" is distinguishable from "the
            // model never asked".
            ReverbModel.ComputeReverb(sc.Size, sc.Reflect, sc.OpenFrac, sc.Roofed,
                                      out float wet, out float room, out float damp);

            var sim = Program.FreshSim();
            sim.RawCapture = true;
            Program.PlaceAt(new Vector3i(10, 64, 10));
            Program.DialTo(new Vector3i(10, 64, 10), "Impulse");

            // LET THE SPEAKER FINISH ARRIVING FIRST. A newly placed speaker fades in over
            // WorldEmitterBirthFade and a newly started station has its own arrive -- so the first
            // attempt at this measured a decay with a 1.5 s RAMP UP underneath it, which is why
            // the tail read +5.1 dB and the early decay time came out LONGER than the RT60. Both
            // fades are off in the lab (see Configure) and it still waits, because a station start
            // is a decoder open on another thread and that is not a fixed number of frames.
            sim.Run(4.0f);

            // Now catch a burst. Everything before this instant is the room being quiet.
            int onset = -1;
            float floor = -70f;
            for (int i = 0; i < 60 * (BurstEverySec + 6) && onset < 0; i++)
            {
                sim.Step();
                int last = sim.Tape.Count - 1;
                if (last > 0 && Sim.Db(sim.Tape[last].Rms) > floor
                             && sim.Tape[last].Rms > sim.Tape[last - 1].Rms * 4f) onset = last;
            }
            sim.Run(CaptureSec);

            var r = new Result { Name = sc.Name, Wet = wet, Room = room, Damp = damp };
            if (onset < 0) { Console.WriteLine("note  " + sc.Name + ": the impulse never arrived"); return r; }

            // The onset was found on the 60 Hz tape; the measurement runs on the samples. Convert
            // once, and back off one frame so the very start of the burst is inside the window.
            int from = Math.Max(0, (onset - 1) * (Rate / 60));
            float[] x = sim.Raw.GetRange(from, Math.Min(sim.Raw.Count - from, Rate * CaptureSec)).ToArray();

            r.Rt60          = Rt(x, 30f, out bool clipped);   // T30, extrapolated — see Rt
            r.Rt60Clipped   = clipped;
            r.Edt           = Rt(x, 10f, out _);
            r.FirstMs       = FirstReflection(x);
            r.DensityPerSec = EchoDensity(x);
            r.CombMs        = CombPeriod(x);
            r.TailDb        = TailLevel(x, 0.250f);
            r.NoiseDb       = NoiseFloor(x);

            WriteWav(Path.Combine(root, "reverb", Safe(sc.Name) + ".wav"), x);
            return r;
        }

        static string Safe(string s) => s.Replace(' ', '-');

        // Walk up from the binary to the tool's own folder. The harness runs from bin/Release/…,
        // so the source directory is four levels up and named for the tool.
        static string RepoDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "CmmEmitSim.csproj"))) return d.FullName;
            return AppContext.BaseDirectory;
        }

        // ── the measurements, on samples ─────────────────────────────────────────────────────
        //
        // Schroeder backward integration: integrate energy from the END backwards, which turns a
        // noisy decay into a monotonic curve a straight line can be fitted to. Reading a slope off
        // the raw envelope gives a different answer every run.
        //
        // T30 rather than RT60 directly, for the reason the first version of this got wrong: the
        // capture is twelve seconds and a hard, sealed room does not fall sixty decibels inside
        // it, so every room reported 11.9 s -- the window length, dressed up as a measurement.
        // T30 measures the -5 to -35 dB span and multiplies by two, which fits inside the window;
        // `clipped` says when even that did not, and then the number is a floor, not a value.
        static float Rt(float[] x, float downDb, out bool clipped)
        {
            clipped = false;
            int n = x.Length;
            if (n < Rate / 10) return 0f;
            var e = new double[n];
            double acc = 0;
            for (int i = n - 1; i >= 0; i--) { acc += (double)x[i] * x[i]; e[i] = acc; }
            if (e[0] <= 0) return 0f;
            int i5 = Find(e, e[0], -5f), iN = Find(e, e[0], -5f - downDb);
            if (i5 < 0) return 0f;
            if (iN < 0) { clipped = true; iN = n - 1; }
            if (iN <= i5) return 0f;
            float span = (iN - i5) / (float)Rate;
            return span * (60f / downDb);
        }

        static int Find(double[] e, double top, float db)
        {
            double want = top * Math.Pow(10.0, db / 10.0);
            for (int i = 0; i < e.Length; i++) if (e[i] <= want) return i;
            return -1;
        }

        // A 1 ms RMS envelope — fine enough to see a discrete arrival, coarse enough not to
        // count individual waveform peaks as echoes.
        static float[] Envelope(float[] x, int msPerCell)
        {
            int cell = Rate * msPerCell / 1000;
            var env = new float[x.Length / cell];
            for (int c = 0; c < env.Length; c++)
            {
                double s = 0;
                for (int i = c * cell; i < (c + 1) * cell; i++) s += (double)x[i] * x[i];
                env[c] = (float)Math.Sqrt(s / cell);
            }
            return env;
        }

        // When energy next RISES after the direct sound has begun falling. In a real room that is
        // the first wall reflection, and its absence is the "no echo" complaint stated as a
        // number: a diffuse reverb with no predelay has no such moment and reports 0.
        static float FirstReflection(float[] x)
        {
            float[] env = Envelope(x, 1);
            int peak = 0;
            for (int i = 1; i < Math.Min(env.Length, 50); i++) if (env[i] > env[peak]) peak = i;
            for (int i = peak + 2; i < Math.Min(env.Length, 400); i++)
                if (env[i] > env[i - 1] * 1.4f && env[i] > env[peak] * 0.02f) return i - peak;
            return 0f;
        }

        // Distinct arrivals per second in the first 200 ms. A CAVERN is sparse -- you hear
        // separate slaps -- and a small room is already a smear by 20 ms. This says which of the
        // two the render SOUNDS like, independently of how long it rings.
        static float EchoDensity(float[] x)
        {
            float[] env = Envelope(x, 2);
            int win = Math.Min(env.Length, 100);
            if (win < 8) return 0f;
            float top = 0f; for (int i = 0; i < win; i++) if (env[i] > top) top = env[i];
            int peaks = 0;
            for (int i = 1; i < win - 1; i++)
                if (env[i] > env[i - 1] && env[i] >= env[i + 1] && env[i] > top * 0.05f) peaks++;
            return peaks * 1000f / (win * 2f);
        }

        // THE COMB SIGNATURE, and the reason this lab exists.
        //
        // Freeverb's comb delays repeat in the tail, so autocorrelating it finds them: the
        // strongest lag IS the delay the algorithm is built around, which is the room it thinks it
        // is in. Today that should land near the fixed 25-37 ms tuning whatever room was asked
        // for. When the combs are scaled by size this number has to MOVE with the room -- and if
        // it does not, the change did nothing audible, whatever happened to the RT60.
        //
        // On samples, not on the frame tape: the first version of this ran on a 60 Hz envelope,
        // where a 33 ms period is under two samples, and it duly reported the same 67 ms for six
        // different rooms.
        static float CombPeriod(float[] x)
        {
            // Skip the direct sound and take TWO seconds of settled tail. One second was not
            // enough: the winning lag changed between identical runs (14 ms, then 5 ms, on the
            // same room) because two lags were within noise of each other and whichever won was
            // luck. A measurement that moves when nothing moved is worse than no measurement.
            int from = Rate / 20, n = Math.Min(x.Length - from, Rate * 2);
            if (n < Rate / 4) return 0f;
            float[] env = Envelope(new ArraySegment<float>(x, from, n).ToArray(), 1);   // 1 ms cells
            int m = env.Length;
            double mean = 0; for (int i = 0; i < m; i++) mean += env[i]; mean /= m;
            // 5..250 ms of lag: below 5 is the waveform, above 250 is a separate echo not a comb.
            int lo = 5, hi = Math.Min(250, m / 2);
            double best = 0, second = 0; int bestLag = 0;
            for (int lag = lo; lag < hi; lag++)
            {
                double s = 0;
                for (int i = 0; i + lag < m; i++) s += (env[i] - mean) * (env[i + lag] - mean);
                s /= (m - lag);
                if (s > best)
                {
                    // Neighbouring lags belong to the SAME peak; only a genuinely separate one
                    // counts as the runner-up, or every winner beats its own shoulder.
                    if (Math.Abs(lag - bestLag) > 3) second = best;
                    best = s; bestLag = lag;
                }
                else if (s > second && Math.Abs(lag - bestLag) > 3) second = s;
            }
            // AND IT HAS TO WIN CLEARLY. A tail with no dominant period is a legitimate answer --
            // a well-diffused reverb has none -- and reporting the strongest of a field of equals
            // is how this returned two different numbers for one room. 0 means "no clear period",
            // which is information, not a failure.
            if (best <= 0 || second > best * 0.75) return 0f;
            return bestLag;   // 1 ms cells, so the lag IS milliseconds
        }

        // WHAT EVERYTHING ELSE IS SITTING ON. If this is not far below the tail, no decay
        // measurement in the row means anything -- the integral stops falling when it reaches the
        // floor, and reports the time it took to get there as if it were the room.
        static float NoiseFloor(float[] x)
        {
            int n = Rate / 2;
            if (x.Length < n * 2) return -140f;
            double s = 0;
            for (int i = x.Length - n; i < x.Length; i++) s += (double)x[i] * x[i];
            float rms = (float)Math.Sqrt(s / n);
            float top = 0f;
            for (int i = 0; i < Math.Min(x.Length, Rate / 20); i++) { float a = Math.Abs(x[i]); if (a > top) top = a; }
            if (top <= 0f || rms <= 0f) return -140f;
            return Sim.Db(rms) - Sim.Db(top);
        }

        static float TailLevel(float[] x, float atSeconds)
        {
            float[] env = Envelope(x, 5);
            int i = (int)(atSeconds * 200f);
            if (i >= env.Length || env.Length == 0) return -140f;
            float top = 0f; for (int k = 0; k < Math.Min(env.Length, 20); k++) if (env[k] > top) top = env[k];
            if (top <= 0f || env[i] <= 0f) return -140f;
            return Sim.Db(env[i]) - Sim.Db(top);
        }

        // ── output ───────────────────────────────────────────────────────────────────────────
        static void Report(List<Result> rs)
        {
            Console.WriteLine("  room                 asked for          measured");
            Console.WriteLine("                    wet  room  damp     RT60    EDT   1st  dens   comb   tail@250  noise");
            Console.WriteLine("  " + new string('-', 82));
            foreach (Result r in rs)
                Console.WriteLine($"  {r.Name,-18} {r.Wet,4:0.00} {r.Room,5:0.00} {r.Damp,5:0.00}  "
                                + $"{(r.Rt60Clipped ? ">" : " ")}{r.Rt60,5:0.00}s {r.Edt,5:0.00}s {r.FirstMs,4:0}ms "
                                + $"{r.DensityPerSec,4:0}/s {r.CombMs,5:0}ms  {r.TailDb,6:0.0}dB {r.NoiseDb,7:0.0}dB");
            Console.WriteLine();
            Console.WriteLine("  THE FINDING, IF YOU READ ONE LINE: the concrete hall (20 blocks), the stone");
            Console.WriteLine("  cavern (60) and the huge cavern (150) measure the SAME RT60 to two decimal");
            Console.WriteLine("  places. sizeTail saturates at 20 blocks, so every space above it asks the");
            Console.WriteLine("  reverb for exactly the same thing, and the reverb obliges.");
            Console.WriteLine();
            Console.WriteLine("  TRUSTWORTHY TODAY:");
            Console.WriteLine("    RT60   how long it rings. T30 doubled; a leading > means it outlasted the capture.");
            Console.WriteLine("    comb   the dominant repeat period in the tail, or 0 for \"no clear period\".");
            Console.WriteLine("           0 everywhere today is CORRECT, not a failure: eight combs at eight");
            Console.WriteLine("           lengths plus four allpasses is a well-diffused tail and no single");
            Console.WriteLine("           period should dominate it. It is here as a CHANGE detector -- if");
            Console.WriteLine("           scaling the combs to cavern lengths makes the tail sparse enough for a");
            Console.WriteLine("           period to emerge, this is where that shows up.");
            Console.WriteLine("    noise  what the row is sitting on. Anything above about -60 dB and every decay");
            Console.WriteLine("           number beside it is measuring the floor instead of the room.");
            Console.WriteLine("  NOT YET:");
            Console.WriteLine("    dens / 1st / tail  report the same value in every room, so they are not");
            Console.WriteLine("           discriminating anything. `1st` sitting at its search floor is consistent");
            Console.WriteLine("           with there being no early reflection at all, which is the thing to build --");
            Console.WriteLine("           but it has not been proven against a render that HAS one, so do not read");
            Console.WriteLine("           it as evidence yet. Calibrate them against a known IR before trusting them.");
        }

        // A baseline, so tomorrow's change is judged against today rather than against memory.
        static int Compare(string root, List<Result> rs, bool accept)
        {
            // IN THE REPO, NOT THE TEMP LIBRARY. The whole value of a baseline is that it
            // outlives the run it was taken in; `root` is a scratch folder that gets rebuilt.
            string path = Path.Combine(RepoDir(), "reverb-baseline.txt");
            if (accept)
            {
                using (var w = new StreamWriter(path))
                    foreach (Result r in rs)
                        w.WriteLine($"{r.Name}|{r.Rt60:0.000}|{r.Edt:0.000}|{r.FirstMs:0.0}|{r.DensityPerSec:0.0}|{r.CombMs:0.0}|{r.TailDb:0.0}");
                Console.WriteLine("  baseline recorded: " + path);
                return 0;
            }
            if (!File.Exists(path))
            {
                Console.WriteLine("  note  no baseline yet — run with --accept once these numbers are the ones to beat.");
                return 0;
            }
            var b = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(path))
            { var p = line.Split('|'); if (p.Length == 7) b[p[0]] = p; }
            Console.WriteLine("  against the baseline:");
            foreach (Result r in rs)
            {
                if (!b.TryGetValue(r.Name, out string[] p)) { Console.WriteLine($"    {r.Name}: new"); continue; }
                float bRt = float.Parse(p[1]), bComb = float.Parse(p[5]);
                string rt = Delta(bRt, r.Rt60, "s"), cb = Delta(bComb, r.CombMs, "ms");
                if (rt == null && cb == null) continue;
                Console.WriteLine($"    {r.Name,-18} RT60 {rt ?? "unchanged"}   comb {cb ?? "unchanged"}");
            }
            return 0;   // a measuring instrument reports; it does not fail a build
        }

        static string Delta(float was, float now, string unit)
        {
            if (Math.Abs(now - was) < (unit == "s" ? 0.05f : 1.5f)) return null;
            return $"{was:0.00} → {now:0.00} {unit}";
        }

        static void WriteWav(string path, float[] x)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // THE CAPTURE ITSELF, not a picture of it. The frame tape would only give an envelope;
            // these are the samples the mixer produced, so the file can actually be listened to
            // and two rooms compared by ear, which is the other half of what this lab is for.
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int data = x.Length * 2;
                w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data);
                w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
                w.Write((short)1); w.Write((short)1); w.Write(Rate);
                w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(data);
                foreach (float v in x)
                {
                    float c = v > 1f ? 1f : (v < -1f ? -1f : v);
                    w.Write((short)(c * 32000f));
                }
            }
        }
    }
}
