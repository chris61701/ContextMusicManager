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
                                       "distance-tone", "distance-music" };
            var runs  = new Action[] { Turn_On, Retune_To_Live_Station, Retune_To_Cold_Station,
                                       Rapid_Switching, Off_Mid_Tune, Broken_Station,
                                       Distance_Tone, Distance_Music };

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
              "broken-station", "distance-tone", "distance-music" };

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

        // -- the tape, for a human --------------------------------------------------------------
        static void DumpCsv(string root, string name)
        {
            string p = Path.Combine(root, name + ".csv");
            using (var w = new StreamWriter(p))
            {
                w.Write("t,earDb,peak");
                for (int s = 0; s < CmmMixer.MaxEmitterSlots; s++)
                    w.Write(",sig" + s + ",lane" + s + "Db,mir" + s + ",tone" + s);
                w.WriteLine(",note");
                foreach (var x in _sim.Tape)
                {
                    w.Write(x.T.ToString("0.000") + "," + Sim.Db(x.Rms).ToString("0.0") + "," + x.Peak.ToString("0.000"));
                    for (int s = 0; s < CmmMixer.MaxEmitterSlots; s++)
                        w.Write("," + x.Sig[s].ToString("0.00") + "," + Sim.Db(x.LaneRms[s]).ToString("0.0")
                                + "," + x.Mir[s].ToString("0.00") + "," + x.Tone[s].ToString("0.00"));
                    w.WriteLine("," + (x.Note ?? ""));
                }
            }
            Console.WriteLine("   csv   " + p);
        }
    }
}
