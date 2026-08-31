using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CMM
{
    internal static class Program
    {
        static int _fail;
        static readonly List<string> _failures = new List<string>();
        static Sim _sim;

        internal static int Main(string[] args)
        {
            bool echo = Array.IndexOf(args, "--echo") >= 0;
            bool csv  = Array.IndexOf(args, "--csv")  >= 0;
            string only = Arg(args, "--only");

            // ONE PROCESS PER SCENARIO. EmitterAudioLayer is a static singleton with twelve-second
            // grace timers, a pending-start latch and a native mixer that cmm_mix_start_ex will not
            // re-enter cleanly -- so a scenario inherited whatever the last one left behind, and
            // off-mid-tune passed alone while failing after rapid-switch. A result that depends on
            // what ran before it is not a result.
            //
            // Resetting all of that from outside would mean modelling it, which is the exact
            // mistake this harness exists to avoid. The process boundary is the only reset that
            // cannot drift, and it is what CmmEmitLab settled on for the same reason.
            if (only == null) return RunEach(args);

            string root = Path.Combine(Path.GetTempPath(), "CmmEmitSim");
            Directory.CreateDirectory(root);
            Console.WriteLine("CmmEmitSim - the real EmitterAudioLayer, offline");
            Console.WriteLine("  library : " + root);
            Library.Build(root);

            Configure(root);
            SimLog.Echo = echo;

            CmmMixer.Headless = true;
            var mixer = CmmMixer.GetOrCreate();
            if (mixer == null) { Console.WriteLine("  FATAL: native mixer would not start"); return 2; }

            // Let the catalog find the stations before any scenario presses a dial.
            UnityEngine.Time.realtimeSinceStartup += 60f;
            RadioStationCatalog.EnsureCatalog();
            Console.WriteLine("  stations: " + RadioStationCatalog.Stations.Count
                              + " (" + string.Join(", ", Names().ToArray()) + ")");
            Console.WriteLine();

            var names = new string[] { "turn-on", "retune-shared", "retune-cold",
                                       "rapid-switch", "off-mid-tune", "broken-station",
                                       "distance-tone", "distance-music", "dead-group",
                                       "dead-tune", "pool-vanishes",
                                       "warp-speed", "dial-storm", "speaker-farm" };
            var runs  = new Action[] { Turn_On, Retune_To_Live_Station, Retune_To_Cold_Station,
                                       Rapid_Switching, Off_Mid_Tune, Broken_Station,
                                       Distance_Tone, Distance_Music, Dead_Group,
                                       Dead_Tune, Pool_Vanishes,
                                       Warp_Speed, Dial_Storm, Speaker_Farm };

            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] != only) continue;
                Console.WriteLine("-- " + names[i] + " " + new string('-', Math.Max(0, 52 - names[i].Length)));
                int before = _fail;
                Reset();
                try { runs[i](); }
                catch (Exception e) { Fail(names[i], "threw: " + e.Message); }
                if (csv) DumpCsv(root, names[i]);
                if (_fail == before) Console.WriteLine();
            }

            Console.WriteLine(new string('=', 62));
            if (_fail == 0) Console.WriteLine("every invariant held.");
            else
            {
                Console.WriteLine(_fail + " FAILURE(S):");
                foreach (string f in _failures) Console.WriteLine("  " + f);
            }
            try { CmmMixer.Instance?.StopAllNow(); } catch { }
            System.Threading.Thread.Sleep(150);
            return _fail == 0 ? 0 : 1;
        }

        static readonly string[] Scenarios =
            { "turn-on", "retune-shared", "retune-cold", "rapid-switch", "off-mid-tune",
              "broken-station", "distance-tone", "distance-music", "dead-group",
              "dead-tune", "pool-vanishes", "warp-speed", "dial-storm", "speaker-farm" };

        // The parent: run each scenario in its own process, relay its output, add up the verdicts.
        static int RunEach(string[] args)
        {
            var pass = new List<string>();
            var fail = new List<string>();
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string extra = "";
            foreach (string a in args) if (a != "--only") extra += " " + a;

            foreach (string name in Scenarios)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, "--only " + name + extra)
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var pr = System.Diagnostics.Process.Start(psi))
                {
                    string outp = pr.StandardOutput.ReadToEnd();
                    pr.WaitForExit();
                    char[] nl = { (char)10, (char)13 };
                    foreach (string line in outp.Split(nl, StringSplitOptions.RemoveEmptyEntries))
                        if (line.StartsWith("   ") || line.StartsWith("-- ") || line.Contains("stations:"))
                            Console.WriteLine(line.TrimEnd());
                    // A CHILD THAT DIES SAYS SO. Counting the exit code alone let a scenario that
                    // crashed, or never started the mixer, register as a plain "failed" with no
                    // FAIL line anywhere above it -- a silent failure in the one tool whose job is
                    // to make failures loud.
                    if (pr.ExitCode != 0 && !outp.Contains("FAIL"))
                    {
                        Console.WriteLine("   DIED  exit " + pr.ExitCode + ", no failure reported:");
                        string err = pr.StandardError.ReadToEnd();
                        string tailSrc = err.Length > 0 ? err : outp;
                        string[] tl = tailSrc.Split(nl, StringSplitOptions.RemoveEmptyEntries);
                        for (int k = Math.Max(0, tl.Length - 6); k < tl.Length; k++)
                            Console.WriteLine("         " + tl[k].TrimEnd());
                    }
                    (pr.ExitCode == 0 ? pass : fail).Add(name);
                }
            }

            Console.WriteLine(new string('=', 62));
            Console.WriteLine(pass.Count + "/" + Scenarios.Length + " scenario(s) held every invariant.");
            if (fail.Count > 0) Console.WriteLine("failed: " + string.Join(", ", fail.ToArray()));
            return fail.Count == 0 ? 0 : 1;
        }

        static string Arg(string[] a, string k)
        { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

        static List<string> Names()
        {
            var l = new List<string>();
            foreach (var s in RadioStationCatalog.Stations) l.Add(s.Name);
            return l;
        }

        // -- setup -----------------------------------------------------------------------------
        static void Configure(string root)
        {
            ContextMusicManager.MusicDirectory = root;
            // The station parent is resolved through MusicRoots, exactly as the game does; the
            // primary root is normally set when MusicPath resolves at startup.
            MusicRoots.SetPrimary(root);
            CMMConfig.WorldEmitterEnabled = true;
            CMMConfig.WorldEmitterSource  = "Radio";
            CMMConfig.WorldEmitterOcclusion = false;          // clear line: the levels here are the tuning's
            CMMConfig.LoudnessNormalizationEnabled = false;   // known levels; loudness has its own harness
            // PINNED, not inherited. The shipped default is deliberately quiet (0.04), and every
            // level here -- the distance walk especially -- is calibrated against a carrier the
            // ear can measure. Taking the shipped value would silently retune the assertions
            // whenever it is tuned, which is the harness measuring the config rather than the code.
            CMMConfig.WorldEmitterOffAirLevel = 0.15f;
            CMMConfig.DebugLogging = true;
        }

        static readonly Vector3i A = new Vector3i(10, 64, 10);
        static readonly Vector3i B = new Vector3i(14, 64, 10);

        static void Reset()
        {
            // Through the shipped path, not a back door: disabling the feature makes Tick call
            // StopAll itself, which is the same teardown a player gets from the settings screen.
            CMMConfig.WorldEmitterEnabled = false;
            CmmClock.Set(_sim != null ? _sim.Now + 0.02f : 0.02f);
            EmitterAudioLayer.Tick();
            CMMConfig.WorldEmitterEnabled = true;

            // Back to automatic through the real command, so nothing of the last scenario's
            // pins, before-off memory or sweep hold survives into the next.
            var snap = EmitterRegistry.Snapshot;
            for (int i = 0; i < snap.Count; i++) EmitterTuner.Apply(snap[i], EmitterTuner.CmdAuto);
            EmitterTuner.LastTuneAt = -999f; EmitterTuner.SweepHeldAt = -999f;
            EmitterRegistry.Snapshot.Clear();
            _sim = new Sim();
            GameManager.Instance.World.Player.position = new Vector3(10.5f, 64.5f, 12.5f);
            GameManager.Instance.World.Player.cameraTransform.position = new Vector3(10.5f, 65.2f, 12.5f);

            // SILENT BEFORE THE SCENARIO STARTS, AND PROVED SO. A fixed settle was not enough:
            // off-mid-tune passed alone and failed after rapid-switch, purely because the previous
            // scenario's audio was still draining. A result that depends on what ran before it is
            // not a result. So this runs until the ear is actually quiet -- and says so if it
            // never gets there, because that would be a leak worth knowing about rather than a
            // reason to carry on measuring.
            _sim.Run(0.25f);                       // a tape to look at before asking about it
            for (int i = 0; i < 300; i++)
            {
                if (Sim.Db(_sim.Tape[_sim.Tape.Count - 1].Rms) <= Floor) break;
                _sim.Step();
            }
            _sim.Run(0.20f);
            float residue = _sim.MeanDb(_sim.Now - 0.2f, _sim.Now);
            if (residue > Floor)
                Console.WriteLine("   note  did not reach silence before this scenario: "
                                  + residue.ToString("0.0") + " dBFS");
        }

        // A PRESS IS THE REAL ONE. EmitterTuner.Apply is what the radial menu invokes, so a
        // scenario turning a dial takes the same path a player does -- the pin write, the silent
        // pin, the before-off memory, the sweep hold and the retune stamp all included.
        static void Dial(Vector3i pos, string cmd)
        {
            var snap = EmitterRegistry.Snapshot;
            for (int i = 0; i < snap.Count; i++)
                if (snap[i].Pos.Equals(pos)) { EmitterTuner.Apply(snap[i], cmd); return; }
            throw new Exception("no speaker at " + pos);
        }

        // WAIT FOR THE SPEAKER TO BE PLAYING, rather than assuming it after N seconds. A cold
        // start has to find the station, open a file and seek into the broadcast, and how long
        // that takes is not a constant -- so a scenario that pressed on a stopwatch was really
        // measuring "was it up yet", and said 4.3 seconds of missing static when it was not.
        static bool WaitAudible(float maxSeconds = 8f)
        {
            int cap = (int)(maxSeconds * 60f);
            for (int i = 0; i < cap; i++)
            {
                _sim.Step();
                if (Sim.Db(_sim.Tape[_sim.Tape.Count - 1].Rms) > Floor) { _sim.Run(1.0f); return true; }
            }
            return false;
        }

        static void Place(Vector3i pos)
        {
            EmitterRegistry.Snapshot.Add(new EmitterRegistry.Emitter
            { Pos = pos, Name = "cntStoreElectronicsBookShelfSpeaker", Dist = 2.5f, PoiId = 1 });
        }

        // -- the assertions --------------------------------------------------------------------
        const float Floor = -55f;    // below this the ear has nothing

        // HOW MANY TIMES DID IT DECIDE THIS? A state machine that flaps says so in its own log,
        // and the flap is invisible in the audio when the two lanes overlap -- which is exactly
        // how ten carrier raises in one second went unheard as anything but "a little silence".
        static int Said(string needle, float from)
        {
            int n = 0;
            foreach (string line in SimLog.Lines)
            {
                if (line.IndexOf(needle, StringComparison.Ordinal) < 0) continue;
                float t;
                if (float.TryParse(line.Substring(0, 7).Trim(), out t) && t >= from) n++;
            }
            return n;
        }

        static void Fail(string sc, string why)
        { _fail++; _failures.Add(sc + ": " + why); Console.WriteLine("   FAIL  " + why); }

        static void Check(string sc, bool ok, string what, string detail)
        {
            if (ok) Console.WriteLine("   pass  " + what + "  (" + detail + ")");
            else Fail(sc, what + " - " + detail);
        }

        // No silence between the press and the arrival. This is "CTOP static stopped playing
        // before the music started playing", stated as a number.
        // FROM WHEN THE SOUND STARTS, not from the press.
        //
        // These two were double-counting. A station open is a decoder open plus a seek on a
        // ThreadPool thread, so how many ticks pass before the first sound is genuinely variable
        // -- measured across runs at 83, 133 and 167 ms for the same scenario, with a driven
        // clock. That delay is StaticBy's business and it has its own budget.
        //
        // What this measures is the reported bug: a hole in the middle, AFTER the speaker has
        // started making a noise and BEFORE the station arrives. That is not variable, because
        // once the static is running nothing is waiting on a thread any more.
        static void NoHole(string sc, float press, float to, float maxMs)
        {
            float from = _sim.FirstAbove(press, Floor);
            if (float.IsNaN(from)) { Fail(sc, "nothing was ever audible after the press"); return; }
            from += 0.02f;
            float at;
            float hole = _sim.LongestHole(from, to, Floor, out at) * 1000f;
            Check(sc, hole <= maxMs, "no hole between press and arrival",
                  hole <= 0.5f ? "none" : hole.ToString("0") + " ms at t=" + at.ToString("0.00")
                                 + "s, budget " + maxMs.ToString("0") + " ms");
        }

        // No level discontinuity while the speaker is audible. This is "snapping/popping as if it
        // is stitching two different volume clips together".
        static void NoStep(string sc, float from, float to, float maxDb)
        {
            float at;
            float step = _sim.BiggestStepDb(from, to, Floor, out at);
            Check(sc, step <= maxDb, "no level step while audible",
                  "worst " + step.ToString("0.0") + " dB at t=" + at.ToString("0.00")
                  + "s, budget " + maxDb.ToString("0.0") + " dB");
        }

        static void StaticBy(string sc, float press, float ms)
        {
            float on = _sim.FirstAbove(press, Floor);
            float dt = (on - press) * 1000f;
            Check(sc, !float.IsNaN(on) && dt <= ms, "static arrives promptly",
                  float.IsNaN(on) ? "never audible"
                                  : dt.ToString("0") + " ms, budget " + ms.ToString("0") + " ms");
        }

        // Whenever a tuning lane's receiver is ARRIVING (signal climbing out of the noise) it must
        // have a programme under it. A lane arriving with no mirror connected is the static fading
        // off an empty lane - the defect of 2026-08-30, stated as an invariant.
        static void ArrivesOntoSomething(string sc)
        {
            for (int i = 1; i < _sim.Tape.Count; i++)
            {
                var p = _sim.Tape[i - 1]; var c = _sim.Tape[i];
                for (int s = CmmMixer.StationSlots; s < CmmMixer.MaxEmitterSlots; s++)
                {
                    // The ARM is not an arrival. A lane going from "not armed" (-1) straight to a
                    // signal is a receiver being switched on at the level it should hold, which is
                    // what a borrow does when it opens on the station it is leaving; the arrival is
                    // the climb OUT of the noise afterwards, and only that needs a programme.
                    if (p.Sig[s] < 0f) continue;
                    bool climbing = c.Sig[s] > 0.15f && c.Sig[s] < 0.98f && c.Sig[s] > p.Sig[s];
                    if (!climbing) continue;
                    if (c.Tone[s] >= 0f) continue;              // a carrier IS the programme (step 8)
                    if (c.Mir[s] < 0f)
                    {
                        Fail(sc, "lane " + s + " arriving at t=" + c.T.ToString("0.00")
                                 + "s with NO mirror connected (sig " + p.Sig[s].ToString("0.00")
                                 + "->" + c.Sig[s].ToString("0.00") + ") - the static is lifting off silence");
                        return;
                    }
                }
            }
            Console.WriteLine("   pass  every arrival had a programme under it");
        }

        // Step 2: the station being left keeps playing, buried by the receiver -- never stopped,
        // never faded. Stated as LEVEL, not as mechanism.
        //
        // This used to look for mirror gain on a tuning lane, which is only how the BORROW does
        // it. The tune machine does the same job on the station's own lane and carries no mirror
        // at all, so a retune taking the better path failed an assertion for taking it. What both
        // owe the ear is identical and that is what is measured: through the leave the speaker
        // stays within a few dB of where it was, because a programme is still under the noise.
        static void LeavesWithProgramme(string sc, float press)
        {
            float before = _sim.MeanDb(press - 0.30f, press - 0.02f);
            float during = _sim.MeanDb(press + 0.05f, press + LeaveRamp);
            float drop   = before - during;
            Check(sc, drop < 10f, "the station being left plays on under the noise",
                  "was " + before.ToString("0.0") + " dBFS, " + during.ToString("0.0")
                  + " through the leave (" + drop.ToString("+0.0;-0.0") + " dB)");
        }
        const float LeaveRamp = 0.70f;

        // -- scenarios -------------------------------------------------------------------------
        //
        // Each is a sequence of dial presses at scripted times. Everything between them is the
        // shipped state machine deciding what to do.

        static void Turn_On()
        {
            const string sc = "turn-on";
            Place(A);
            Dial(A, EmitterTuner.CmdOff); _sim.Run(1.0f);

            float press = _sim.Now;
            _sim.Note("press: off -> Alpha FM");
            Dial(A, "Alpha FM");
            _sim.Run(4.0f);

            StaticBy(sc, press, 350f);
            // ONE EMIT TICK, AND IT IS THE FLOOR. Turning a silenced speaker on has nothing
            // playing to carry it, so the first sound it can make waits for the layer's next tick
            // (~110 ms) plus the ring. Measured at 133 ms and structural: shortening it means
            // arming the noise from the keypress rather than from the tick.
            //
            // Left as it is because the dial CLICK already fires on the press, so the control is
            // acknowledged instantly and the gap is underneath it. The budget records the real
            // number rather than pretending it is zero -- if it grows, that is a regression.
            NoHole(sc, press, press + 3.0f, 120f);
            // FROM AFTER THE ONSET. Turning a silenced speaker on has no station to slide out of,
            // so the noise deliberately arrives at its level in 40 ms -- which is 16 dB between two
            // frames at 60 Hz. That is the designed fade-in, not a discontinuity, and measuring it
            // as one would make the control's own responsiveness read as a fault.
            NoStep(sc, press + 0.2f, press + 3.5f, 14f);
            ArrivesOntoSomething(sc);
            Console.WriteLine("   level at 3 s: " + _sim.MeanDb(press + 2.8f, press + 3.2f).ToString("0.0") + " dBFS");
        }

        static void Retune_To_Live_Station()
        {
            const string sc = "retune-shared";
            Place(A); Place(B);
            Dial(A, "Alpha FM");
            Dial(B, "Beta FM");
            if (!WaitAudible()) Fail(sc, "neither station became audible");
            _sim.Run(2.0f);                       // both stations up and settled

            float press = _sim.Now;
            _sim.Note("press: A, Alpha FM -> Beta FM (already playing for B)");
            Dial(A, "Beta FM");
            _sim.Run(4.0f);

            StaticBy(sc, press, 350f);
            LeavesWithProgramme(sc, press);
            NoHole(sc, press, press + 3.0f, 60f);
            NoStep(sc, press, press + 3.5f, 12f);
            ArrivesOntoSomething(sc);
        }

        static void Retune_To_Cold_Station()
        {
            const string sc = "retune-cold";
            Place(A);
            Dial(A, "Alpha FM");
            if (!WaitAudible()) Fail(sc, "Alpha FM never became audible");

            float press = _sim.Now;
            _sim.Note("press: Alpha FM -> Gamma FM (nothing is playing it)");
            Dial(A, "Gamma FM");
            _sim.Run(5.0f);

            StaticBy(sc, press, 350f);
            LeavesWithProgramme(sc, press);
            NoHole(sc, press, press + 4.0f, 60f);
            NoStep(sc, press, press + 4.5f, 12f);
            ArrivesOntoSomething(sc);
        }

        static void Rapid_Switching()
        {
            const string sc = "rapid-switch";
            Place(A);
            Dial(A, "Alpha FM");
            if (!WaitAudible()) Fail(sc, "Alpha FM never became audible");

            float first = _sim.Now;
            string[] dial = { "Beta FM", "Gamma FM", "Alpha FM", "Beta FM", "Gamma FM" };
            for (int i = 0; i < dial.Length; i++)
            {
                _sim.Note("press -> " + dial[i]);
                Dial(A, dial[i]);
                _sim.Run(0.45f);                  // faster than a sweep: the stress case
            }
            _sim.Run(5.0f);

            NoHole(sc, first, _sim.Now - 0.5f, 150f);
            NoStep(sc, first, _sim.Now - 0.5f, 14f);
            ArrivesOntoSomething(sc);
        }

        static void Off_Mid_Tune()
        {
            const string sc = "off-mid-tune";
            Place(A);
            Dial(A, "Alpha FM");
            if (!WaitAudible()) Fail(sc, "Alpha FM never became audible");

            _sim.Note("press -> Gamma FM");
            Dial(A, "Gamma FM");
            _sim.Run(0.5f);                       // mid sweep
            float off = _sim.Now;
            _sim.Note("press -> off, mid-tune");
            Dial(A, EmitterTuner.CmdOff);
            _sim.Run(3.0f);

            float quiet = _sim.MeanDb(off + 1.0f, off + 2.5f);
            Check(sc, quiet <= Floor, "switching off mid-tune goes quiet and stays quiet",
                  quiet.ToString("0.0") + " dBFS after the cut");
        }

        static void Broken_Station()
        {
            const string sc = "broken-station";
            Place(A);
            Dial(A, "Alpha FM");
            if (!WaitAudible()) Fail(sc, "Alpha FM never became audible");

            float press = _sim.Now;
            _sim.Note("press -> Broken FM (nothing in it will decode)");
            Dial(A, "Broken FM");
            _sim.Run(12.0f);

            float carrier = -1f;
            for (int i = _sim.IndexAt(press); i < _sim.Tape.Count; i++)
                for (int s = 0; s < CmmMixer.MaxEmitterSlots; s++)
                    if (_sim.Tape[i].Tone[s] > carrier) carrier = _sim.Tape[i].Tone[s];
            Check(sc, carrier > 0.5f, "a station that will not open sits on the carrier",
                  carrier < 0f ? "no tone on any lane" : "peak tone envelope " + carrier.ToString("0.00"));

            float late = _sim.MeanDb(press + 9f, press + 11f);
            Check(sc, late > Floor, "the carrier is still audible once settled", late.ToString("0.0") + " dBFS");

            // TURNING IT DOWN WHILE IT PLAYS. radio.xml hot-reloads, but the carrier's level was
            // sampled once when it armed and then held in the native tone, so editing it while a
            // speaker sat on the carrier did nothing until you dialled away and back.
            float before = _sim.MeanDb(_sim.Now - 1.0f, _sim.Now);
            _sim.Note("OffAirLevel 0.15 -> 0.02 while the carrier is sounding");
            CMMConfig.WorldEmitterOffAirLevel = 0.02f;
            _sim.Run(3.0f);
            float after = _sim.MeanDb(_sim.Now - 1.0f, _sim.Now);
            CMMConfig.WorldEmitterOffAirLevel = 0.15f;
            Check(sc, before - after > 8f, "the carrier level follows a live config edit",
                  before.ToString("0.0") + " -> " + after.ToString("0.0") + " dBFS ("
                  + (before - after).ToString("0.0") + " dB, want > 8)");
        }

        // -- does it get quieter as you walk away? ----------------------------------------------
        //
        // Reported from the game: an off-air TONE plays at full volume anywhere inside
        // WorldEmitterRange, while occlusion clearly works on it. Distance and occlusion are the
        // same computation in the spatialiser (occlusion is added VIRTUAL distance), so one
        // working and the other not is a contradiction worth measuring rather than reasoning
        // about. `distance-music` is the control: the identical walk with a station playing.
        static void MoveListener(float z)
        {
            var w = GameManager.Instance.World;
            w.Player.position = new Vector3(10.5f, 64.5f, z);
            w.Player.cameraTransform.position = new Vector3(10.5f, 65.2f, z);
        }

        static float Walk(string sc, string station, bool wantTone)
        {
            Place(A);                                  // speaker at (10, 64, 10)
            MoveListener(12.5f);                       // ~2.5 blocks away
            Dial(A, station);
            if (!WaitAudible(14f)) { Fail(sc, station + " never became audible"); return 0f; }
            _sim.Run(wantTone ? 8f : 2f);              // the carrier takes its hunt first

            if (wantTone)
            {
                float tone = -1f;
                for (int i = _sim.Tape.Count - 60; i < _sim.Tape.Count; i++)
                    for (int k = 0; k < CmmMixer.MaxEmitterSlots; k++)
                        if (i >= 0 && _sim.Tape[i].Tone[k] > tone) tone = _sim.Tape[i].Tone[k];
                if (tone < 0.5f) { Fail(sc, "the carrier never came up (tone " + tone.ToString("0.00") + ")"); return 0f; }
            }

            float near = _sim.MeanDb(_sim.Now - 1.5f, _sim.Now);
            _sim.Note("walk to 40 blocks");
            MoveListener(50.5f);                       // ~40 blocks, still inside Range (60)
            _sim.Run(4f);                              // let the position glide settle
            float far = _sim.MeanDb(_sim.Now - 1.5f, _sim.Now);

            Console.WriteLine("   near " + near.ToString("0.0") + " dBFS, far "
                              + far.ToString("0.0") + " dBFS, drop "
                              + (near - far).ToString("0.0") + " dB");
            return near - far;
        }

        static void Distance_Tone()
        {
            const string sc = "distance-tone";
            float drop = Walk(sc, "Broken FM", true);
            Check(sc, drop > 12f, "the off-air tone gets quieter with distance",
                  drop.ToString("0.0") + " dB over 2.5 -> 40 blocks, want > 12");
        }

        static void Distance_Music()
        {
            const string sc = "distance-music";
            float drop = Walk(sc, "Alpha FM", false);
            Check(sc, drop > 12f, "a station gets quieter with distance",
                  drop.ToString("0.0") + " dB over 2.5 -> 40 blocks, want > 12");
        }

        // -- SEVERAL SPEAKERS ON ONE DEAD STATION ------------------------------------------------
        //
        // Not an edge case: a base has several speakers and sooner or later some of them are on a
        // station that will not open. The carrier was written for exactly ONE speaker and every
        // assumption from that leaked out somewhere different -- only one sounded, retuning one
        // sent both to static, breaking one sent the rest to static, and the pitch moved when the
        // lane changed hands. Four separate reports, one missing scenario.
        //
        // Everything here is about what the OTHER speaker hears while something happens to the
        // first one, which is the property none of those fixes had a test for.
        static float ToneOn()
        {
            float t = -1f;
            var last = _sim.Tape[_sim.Tape.Count - 1];
            for (int s = 0; s < CmmMixer.MaxEmitterSlots; s++) if (last.Tone[s] > t) t = last.Tone[s];
            return t;
        }

        // Points the CONSUMER holds on the carrier's lane: how many speakers it is really serving.
        static int CarrierPoints()
        {
            var last = _sim.Tape[_sim.Tape.Count - 1];
            for (int s = 0; s < CmmMixer.MaxEmitterSlots; s++)
                if (last.Tone[s] >= 0f) return (int)CmmMixer.EmitterLaneStat(s, 4);
            return 0;
        }

        static void Dead_Group()
        {
            const string sc = "dead-group";
            Place(A); Place(B);
            Dial(A, "Broken FM");
            Dial(B, "Broken FM");
            _sim.Run(10f);                                  // hunt, give up, settle on the carrier

            Check(sc, ToneOn() > 0.5f, "the carrier comes up for a dead station",
                  "tone " + ToneOn().ToString("0.00"));
            Check(sc, CarrierPoints() >= 2, "it serves BOTH speakers, not one",
                  CarrierPoints() + " point(s) on the lane");

            // RETUNE ONE. The other is not involved and must not hear it.
            _sim.Note("retune A away; B stays on Broken FM");
            float before = _sim.MeanDb(_sim.Now - 1.0f, _sim.Now);
            Dial(A, "Alpha FM");
            _sim.Run(4f);
            float at;
            float hole = _sim.LongestHole(_sim.Now - 3.5f, _sim.Now, Floor, out at) * 1000f;
            Check(sc, ToneOn() > 0.5f, "the tone survives retuning the other speaker",
                  "tone " + ToneOn().ToString("0.00"));
            Check(sc, hole <= 250f, "and does not go through a hole doing it",
                  hole <= 0.5f ? "none" : hole.ToString("0") + " ms");

            // DESTROY THE ONE THAT IS LEFT ON IT... by taking the OTHER away first, so whichever
            // holds the lane, the survivor is the one being asked about.
            // TUNE IT BACK ON. A speaker JOINING the station must not disturb the one already
            // hearing it -- the give-up used to raise the carrier again for its own position and
            // drop the first speaker to static.
            _sim.Note("tune A back onto Broken FM while B is on it");
            Dial(A, "Broken FM");
            _sim.Run(6f);
            float joinHole = _sim.LongestHole(_sim.Now - 5.5f, _sim.Now, Floor, out at) * 1000f;
            Check(sc, ToneOn() > 0.5f, "the tone survives a speaker joining the station",
                  "tone " + ToneOn().ToString("0.00"));
            Check(sc, joinHole <= 400f, "and the one already on it is not dropped to hunt again",
                  joinHole <= 0.5f ? "none" : joinHole.ToString("0") + " ms of silence");
            Check(sc, CarrierPoints() >= 2, "both are served again", CarrierPoints() + " point(s)");

            // DESTROY THE ONE HOLDING THE LANE. The other keeps the tone, promptly.
            _sim.Note("destroy A");
            for (int i = 0; i < EmitterRegistry.Snapshot.Count; i++)
                if (EmitterRegistry.Snapshot[i].Pos.Equals(A)) { EmitterRegistry.Snapshot.RemoveAt(i); break; }
            _sim.Run(4f);
            Check(sc, ToneOn() > 0.5f, "the tone survives destroying the other speaker",
                  "tone " + ToneOn().ToString("0.00"));
            Check(sc, CarrierPoints() >= 1, "and is still serving the one that is left",
                  CarrierPoints() + " point(s)");

            // THE STATION COMES BACK. Nothing signals this -- a folder gains a playable file and
            // the catalog notices -- so the carrier has to end because the set empties, not
            // because anything told it to.
            _sim.Note("Broken FM gains a playable track");
            Library.Repair(Path.Combine(Path.GetTempPath(), "CmmEmitSim"));
            UnityEngine.Time.realtimeSinceStartup += 60f;
            RadioStationCatalog.ForceRebuild();
            RadioStationCatalog.EnsureCatalog();
            _sim.Run(12f);
            Check(sc, ToneOn() < 0f, "the tone stops once the station can play",
                  ToneOn() < 0f ? "carrier down" : "tone still " + ToneOn().ToString("0.00"));
        }

        // -- DIALLING ONTO A STATION THAT WILL NOT PLAY ------------------------------------------
        //
        // "There's static, a little silence, then more static before the tone."
        //
        // Two noises with a hole between them, and they are two different lanes: the sweep
        // borrow's, which covers the dial turn, and the carrier's, which starts its own hunt from
        // scratch once the borrow has finished. Between them the borrow's arrive lifts the hiss
        // off a lane with nothing under it -- there is nothing to arrive ONTO, because the station
        // will not open -- and the speaker is in no group at all until the borrow lets go.
        //
        // Stated as the ear hears it: from the press to the settled carrier there is ONE noise.
        static void Dead_Tune()
        {
            const string sc = "dead-tune";
            // TWO SPEAKERS, so the one being tuned is NOT alone on its station's lane and the
            // dial turn takes the SWEEP BORROW rather than the tune machine. That is the path the
            // game log shows, and the only one the defect lives on: the borrow holds the speaker
            // out of every group for the whole of its arrive, so the carrier could see nothing to
            // serve while a second entry point kept raising it anyway.
            Place(A); Place(B);
            Dial(A, "Alpha FM");
            Dial(B, "Alpha FM");
            if (!WaitAudible()) Fail(sc, "Alpha FM never became audible");
            _sim.Run(1.5f);

            float press = _sim.Now;
            _sim.Note("press: A, Alpha FM -> Broken FM (nothing in it will decode)");
            Dial(A, "Broken FM");
            _sim.Run(14f);

            // The carrier is the destination, so it has to actually get there.
            float tone = ToneOn();
            Check(sc, tone > 0.5f, "the dial turn ends on the carrier",
                  tone < 0f ? "no tone on any lane" : "tone " + tone.ToString("0.00"));

            // THE REPORT, AS A COUNT. Raised once, ended never -- anything more is the carrier
            // being torn down and rebuilt under the player, which is what the second static was.
            int raises = Said("Dead air: slot", press);
            int ends   = Said("Dead air ending", press);
            Check(sc, raises == 1, "the carrier is raised once for the whole dial turn",
                  raises + " raise(s)");
            Check(sc, ends == 0, "and never torn down while a speaker is still on it",
                  ends + " teardown(s)");

            // ...and it does not sit on its noise waiting for a voice that is not coming. The
            // give-up is four seconds; a station already known not to open is known at the swap.
            float settled = float.NaN;
            for (int i = _sim.IndexAt(press); i < _sim.Tape.Count; i++)
            {
                float t = -1f;
                for (int s = 0; s < CmmMixer.MaxEmitterSlots; s++)
                    if (_sim.Tape[i].Tone[s] > t) t = _sim.Tape[i].Tone[s];
                if (t > 0.95f) { settled = _sim.Tape[i].T - press; break; }
            }
            Check(sc, !float.IsNaN(settled) && settled < 4f, "and gets there without waiting out the give-up",
                  float.IsNaN(settled) ? "never settled" : settled.ToString("0.00") + " s after the press, want < 4");

            LeavesWithProgramme(sc, press);
            NoStep(sc, press + 0.2f, _sim.Now - 0.5f, 14f);
        }

        // -- THE POOL GOES AWAY WHILE IT IS PLAYING ----------------------------------------------
        //
        // "If the station is fixed with a playlist, and then the playlist is removed, it just
        // keeps on playing the song."
        //
        // Repairing is the easy direction: the dead set empties and the carrier ends because
        // nothing is in it. This is the other one, and it is not symmetric -- a voice that is
        // playing perfectly well has to be STOPPED, and the file it is playing still exists, so
        // the airing-file-vanished interrupt cannot see it either. The only fact is that the
        // station is no longer in the catalog.
        static void Pool_Vanishes()
        {
            const string sc = "pool-vanishes";
            string root = Path.Combine(Path.GetTempPath(), "CmmEmitSim");
            _sim.Note("Broken FM gains a playable track");
            Library.Repair(root);
            UnityEngine.Time.realtimeSinceStartup += 60f;
            RadioStationCatalog.ForceRebuild();
            RadioStationCatalog.EnsureCatalog();

            Place(A);
            Dial(A, "Broken FM");
            if (!WaitAudible(14f)) { Fail(sc, "the repaired station never played"); return; }
            _sim.Run(3f);
            float playing = _sim.MeanDb(_sim.Now - 1.0f, _sim.Now);
            Check(sc, playing > Floor + 15f, "the repaired station is playing",
                  playing.ToString("0.0") + " dBFS");

            _sim.Note("the playlist is removed while it plays");
            Library.Unrepair(root);
            UnityEngine.Time.realtimeSinceStartup += 60f;
            RadioStationCatalog.ForceRebuild();
            RadioStationCatalog.EnsureCatalog();
            _sim.Run(4f);

            // The station SURVIVES -- its folder still holds the file that will not decode, so it
            // is still in the catalog with a pool of one. That is the case the game is in, and it
            // is why nothing existing could see the defect: the release path only fires for a
            // station that disappeared entirely.
            RadioStationCatalog.ByName.TryGetValue("Broken FM", out RadioStation bf);
            Check(sc, bf != null && bf.Order != null && bf.Order.Length == 1,
                  "the station survives with a pool that no longer holds the track",
                  bf == null ? "gone from the catalog" : bf.Order.Length + " track(s)");

            // THE REPORT, and it has to be asked of the VOICE rather than of the ear: the carrier
            // that replaces the song sits at almost exactly the song's level here, so a level
            // measurement cannot tell "it stopped" from "it never stopped".
            Check(sc, bf != null && bf.VoiceId == 0, "the song stops when its pool goes away",
                  bf == null ? "no station" : bf.VoiceId == 0 ? "voice released"
                              : "still playing " + Path.GetFileName(bf.VoiceTrack ?? "?"));

            // ...and what a speaker pinned to a station that cannot play is owed.
            _sim.Run(8f);
            Check(sc, ToneOn() > 0.5f, "and the carrier takes over",
                  ToneOn() < 0f ? "no tone" : "tone " + ToneOn().ToString("0.00"));
        }

        // -- STRESS ------------------------------------------------------------------------------
        //
        // The eleven above ask whether it works. These three ask what happens when it is used
        // harder than it was designed for, and they report the MAIN-THREAD COST of every tick
        // alongside the audio, because a state machine can be perfectly correct and still be a
        // stutter.

        static void Perf(string sc, int from, float meanBudget, float p99Budget, float maxBudget)
        {
            float mean = _sim.TickMean(from), p50 = _sim.TickPct(0.50, from);
            float p99 = _sim.TickPct(0.99, from), max = _sim.TickPct(1.0, from);
            Console.WriteLine("   perf  tick mean " + mean.ToString("0.000") + " ms, p50 "
                              + p50.ToString("0.000") + ", p99 " + p99.ToString("0.000")
                              + ", worst " + max.ToString("0.000") + " ms over "
                              + (_sim.TickMs.Count - from) + " ticks");
            Check(sc, mean <= meanBudget, "the average tick stays cheap",
                  mean.ToString("0.000") + " ms, budget " + meanBudget.ToString("0.0"));
            Check(sc, p99 <= p99Budget, "and so does the 99th percentile",
                  p99.ToString("0.000") + " ms, budget " + p99Budget.ToString("0.0"));
            // A frame is 16.7 ms at 60 fps, so a single tick over that IS a dropped frame.
            Check(sc, max <= maxBudget, "no single tick drops a frame",
                  max.ToString("0.000") + " ms, budget " + maxBudget.ToString("0.0"));
        }

        // -- COMING INTO RANGE AT WARP SPEED -----------------------------------------------------
        //
        // A gyrocopter crosses a speaker's whole range in a fraction of a second, and everything
        // the emitter layer does is deliberately rate-limited: the acoustic field is flooded from
        // the listener a few times a second, occlusion RISES are rate-limited, the spatialiser
        // slews its virtual distance, and a station starts when its nearest speaker comes inside
        // range. Move faster than all of those and the question is whether the result is a fast
        // fade or a click.
        //
        // Teleports rather than a smooth ride, because that is the harder case and it is what a
        // laggy client and a fast-travel actually do to the listener position.
        static void Warp_Speed()
        {
            const string sc = "warp-speed";
            Place(A);
            MoveListener(12.5f);
            Dial(A, "Alpha FM");
            if (!WaitAudible(14f)) { Fail(sc, "Alpha FM never became audible"); return; }
            _sim.Run(2f);
            int perfFrom = _sim.TickMs.Count;

            // Each pass crosses the speaker from well outside range to point blank and out the
            // far side, in fewer steps each time -- 120 blocks in two frames at the end, which is
            // 3,600 blocks per second.
            int[] steps = { 24, 12, 6, 3, 1 };
            float worst = 0f; string worstAt = "none";
            foreach (int n in steps)
            {
                _sim.Note("pass in " + n + " step(s) each way (" + (120 / n) + " blocks per frame)");
                int mark = _sim.Tape.Count;
                for (int i = 0; i <= n; i++) { MoveListener(70.5f - 120f * i / n); _sim.Step(); }
                for (int i = 0; i <= n; i++) { MoveListener(-49.5f + 120f * i / n); _sim.Step(); }
                _sim.Run(1.0f);
                float at;
                float step = _sim.BiggestStepDb(_sim.Tape[mark].T, _sim.Now, Floor, out at);
                if (step > worst) { worst = step; worstAt = n + " step(s), t=" + at.ToString("0.00") + "s"; }
            }

            // A CROSSING IS A FADE, NOT A CUT. The budget is generous, because the speaker really
            // does go from point blank to inaudible -- but a click is a discontinuity between two
            // frames that are BOTH audible, and that is what this measures.
            Check(sc, worst <= 20f, "crossing a speaker at any speed is a fade, not a click",
                  "worst " + worst.ToString("0.0") + " dB between frames (" + worstAt + "), budget 20");

            // AND IT STILL PLAYS AFTERWARDS. The real risk of thrashing the range boundary is a
            // station that stops and never comes back, or a lane leaked on every crossing.
            MoveListener(12.5f);
            _sim.Run(3f);
            float back = _sim.MeanDb(_sim.Now - 1.5f, _sim.Now);
            Check(sc, back > Floor + 15f, "and the station is still there when you stop",
                  back.ToString("0.0") + " dBFS");
            Perf(sc, perfFrom, 2.0f, 6.0f, 16.7f);
        }

        // -- THE DIAL STORM ----------------------------------------------------------------------
        //
        // rapid-switch presses five times at 0.45 s. This presses forty times, as little as one
        // frame apart, walks through Off and Automatic, and includes the dead station -- so the
        // tune machine, the sweep borrow, the carrier and the slot allocator are all being asked
        // to change their minds faster than any of their own timers.
        //
        // The question is not whether it sounds good; nobody dials like this. It is whether it
        // comes out the other side CORRECT: on a station, audible, with no lane left hissing and
        // no runaway cost.
        static void Dial_Storm()
        {
            const string sc = "dial-storm";
            Place(A); Place(B);
            Dial(A, "Alpha FM"); Dial(B, "Gamma FM");
            if (!WaitAudible(14f)) { Fail(sc, "nothing became audible"); return; }
            _sim.Run(2f);
            int perfFrom = _sim.TickMs.Count;

            string[] wheel = { "Alpha FM", "Beta FM", "Gamma FM", "Broken FM",
                               EmitterTuner.CmdOff, "Beta FM", "Alpha FM", EmitterTuner.CmdAuto };
            _sim.Note("40 presses, one every 1-8 frames, across both speakers");
            for (int i = 0; i < 40; i++)
            {
                Dial(i % 3 == 0 ? B : A, wheel[i % wheel.Length]);
                _sim.Run((1 + (i % 8)) / 60f);
            }
            _sim.Note("the dial settles on Beta FM");
            Dial(A, "Beta FM"); Dial(B, "Beta FM");
            _sim.Run(12f);

            // NOTHING LEFT SEARCHING. Every lane a press borrowed must have been handed back.
            int hissing = 0, stations = 0;
            var last = _sim.Tape[_sim.Tape.Count - 1];
            for (int s = 0; s < CmmMixer.MaxEmitterSlots; s++)
            {
                if (last.Sig[s] >= 0f && last.Sig[s] < 0.98f) hissing++;
                if (s < CmmMixer.StationSlots && last.Pts[s] > 0f) stations++;
            }
            Check(sc, hissing == 0, "no lane is left hissing once the dial stops",
                  hissing + " lane(s) with the receiver still searching");
            Check(sc, stations >= 1, "the speakers end up on a station",
                  stations + " station lane(s) carrying points");

            float settled = _sim.MeanDb(_sim.Now - 2f, _sim.Now);
            Check(sc, settled > Floor + 10f, "and it is audible", settled.ToString("0.0") + " dBFS");
            float at;
            float hole = _sim.LongestHole(_sim.Now - 8f, _sim.Now, Floor, out at) * 1000f;
            Check(sc, hole <= 250f, "with no silence after it settles",
                  hole <= 0.5f ? "none" : hole.ToString("0") + " ms at t=" + at.ToString("0.00") + "s");
            Perf(sc, perfFrom, 2.0f, 6.0f, 16.7f);
        }

        // -- A BASE FULL OF SPEAKERS -------------------------------------------------------------
        //
        // Forty speakers is a horde base with a radio in every room. The point sets are capped
        // (MaxPoints per station, MaxDeadPts for the carrier), the occlusion pass runs per point,
        // and only three lanes can hold a station -- so this asks whether the caps degrade or
        // break, and what forty speakers cost on the frame thread.
        static void Speaker_Farm()
        {
            const string sc = "speaker-farm";
            string[] st = { "Alpha FM", "Beta FM", "Gamma FM", "Broken FM" };
            for (int i = 0; i < 40; i++)
            {
                var p = new Vector3i(4 + (i % 8) * 3, 64 + (i / 16), 4 + ((i / 8) % 5) * 3);
                Place(p);
                Dial(p, st[i % st.Length]);
            }
            MoveListener(12.5f);
            if (!WaitAudible(20f)) { Fail(sc, "forty speakers and nothing played"); return; }
            _sim.Run(6f);
            int perfFrom = _sim.TickMs.Count;
            _sim.Run(8f);

            var last = _sim.Tape[_sim.Tape.Count - 1];
            int pts = 0;
            for (int s = 0; s < CmmMixer.MaxEmitterSlots; s++) if (last.Pts[s] > 0) pts += (int)last.Pts[s];
            Console.WriteLine("   lanes " + pts + " point(s) rendering across all lanes");
            Check(sc, pts >= 3, "the lanes carry the speakers they can", pts + " point(s)");

            float lvl = _sim.MeanDb(_sim.Now - 3f, _sim.Now);
            Check(sc, lvl > Floor + 10f, "forty speakers are audible", lvl.ToString("0.0") + " dBFS");
            float at;
            float step = _sim.BiggestStepDb(_sim.Now - 7f, _sim.Now, Floor, out at);
            Check(sc, step <= 14f, "and steady once settled", step.ToString("0.0") + " dB worst step");
            // The headline number: what a horde base pays on every single frame.
            Perf(sc, perfFrom, 4.0f, 10.0f, 16.7f);
        }

        // -- the tape, for a human --------------------------------------------------------------
        static void DumpCsv(string root, string name)
        {
            string p = Path.Combine(root, name + ".csv");
            using (var w = new StreamWriter(p))
            {
                w.Write("t,earDb,peak");
                for (int s = 0; s < CmmMixer.MaxEmitterSlots; s++)
                    w.Write(",sig" + s + ",lane" + s + "Db,mir" + s + ",tone" + s
                            + ",pts" + s + ",app" + s);
                w.WriteLine(",note");
                foreach (var x in _sim.Tape)
                {
                    w.Write(x.T.ToString("0.000") + "," + Sim.Db(x.Rms).ToString("0.0") + "," + x.Peak.ToString("0.000"));
                    for (int s = 0; s < CmmMixer.MaxEmitterSlots; s++)
                        w.Write("," + x.Sig[s].ToString("0.00") + "," + Sim.Db(x.LaneRms[s]).ToString("0.0")
                                + "," + x.Mir[s].ToString("0.00") + "," + x.Tone[s].ToString("0.00")
                                + "," + x.Pts[s].ToString("0") + "," + x.App[s].ToString("0.000"));
                    w.WriteLine("," + (x.Note ?? ""));
                }
            }
            Console.WriteLine("   csv   " + p);
        }
    }
}
