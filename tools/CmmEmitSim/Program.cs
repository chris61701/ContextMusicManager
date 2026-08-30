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
                                       "rapid-switch", "off-mid-tune", "broken-station" };
            var runs  = new Action[] { Turn_On, Retune_To_Live_Station, Retune_To_Cold_Station,
                                       Rapid_Switching, Off_Mid_Tune, Broken_Station };

            for (int i = 0; i < names.Length; i++)
            {
                if (only != null && names[i] != only) continue;
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

            EmitterTuner.Pins.Clear();
            EmitterTuner.LastTuneAt = -999f; EmitterTuner.SweepHeldAt = -999f;
            EmitterRegistry.Snapshot.Clear();
            _sim = new Sim();
            GameManager.Instance.World.Player.position = new Vector3(10.5f, 64.5f, 12.5f);
            GameManager.Instance.World.Player.cameraTransform.position = new Vector3(10.5f, 65.2f, 12.5f);
            _sim.Run(0.5f);   // settle to silence before the scenario starts
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
        static void NoHole(string sc, float from, float to, float maxMs)
        {
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

        // A borrow that leaves a live station must carry that station under its noise. If the
        // mirror gain is zero for the whole leave, the speaker went silent the instant it was
        // retuned, which no radio does.
        static void LeavesWithProgramme(string sc, float press)
        {
            float best = -1f;
            int i0 = _sim.IndexAt(press), i1 = _sim.IndexAt(press + 0.7f);
            for (int i = i0; i <= i1 && i < _sim.Tape.Count; i++)
                for (int s = CmmMixer.StationSlots; s < CmmMixer.MaxEmitterSlots; s++)
                    if (_sim.Tape[i].Mir[s] > best) best = _sim.Tape[i].Mir[s];
            Check(sc, best > 0.2f, "the station being left plays on under the noise",
                  best < 0f ? "no mirror on any tuning lane" : "peak mirror gain " + best.ToString("0.00"));
        }

        // -- scenarios -------------------------------------------------------------------------
        //
        // Each is a sequence of dial presses at scripted times. Everything between them is the
        // shipped state machine deciding what to do.

        static void Turn_On()
        {
            const string sc = "turn-on";
            Place(A);
            EmitterTuner.Off(A); _sim.Run(1.0f);

            float press = _sim.Now;
            _sim.Note("press: off -> Alpha FM");
            EmitterTuner.Press(A, "Alpha FM");
            _sim.Run(4.0f);

            StaticBy(sc, press, 200f);
            NoHole(sc, press + 0.05f, press + 3.0f, 120f);
            NoStep(sc, press, press + 3.5f, 14f);
            ArrivesOntoSomething(sc);
            Console.WriteLine("   level at 3 s: " + _sim.MeanDb(press + 2.8f, press + 3.2f).ToString("0.0") + " dBFS");
        }

        static void Retune_To_Live_Station()
        {
            const string sc = "retune-shared";
            Place(A); Place(B);
            EmitterTuner.Press(A, "Alpha FM");
            EmitterTuner.Press(B, "Beta FM");
            _sim.Run(6.0f);                       // both stations up and settled

            float press = _sim.Now;
            _sim.Note("press: A, Alpha FM -> Beta FM (already playing for B)");
            EmitterTuner.Press(A, "Beta FM");
            _sim.Run(4.0f);

            StaticBy(sc, press, 200f);
            LeavesWithProgramme(sc, press);
            NoHole(sc, press, press + 3.0f, 60f);
            NoStep(sc, press, press + 3.5f, 12f);
            ArrivesOntoSomething(sc);
        }

        static void Retune_To_Cold_Station()
        {
            const string sc = "retune-cold";
            Place(A);
            EmitterTuner.Press(A, "Alpha FM");
            _sim.Run(6.0f);

            float press = _sim.Now;
            _sim.Note("press: Alpha FM -> Gamma FM (nothing is playing it)");
            EmitterTuner.Press(A, "Gamma FM");
            _sim.Run(5.0f);

            StaticBy(sc, press, 200f);
            LeavesWithProgramme(sc, press);
            NoHole(sc, press, press + 4.0f, 60f);
            NoStep(sc, press, press + 4.5f, 12f);
            ArrivesOntoSomething(sc);
        }

        static void Rapid_Switching()
        {
            const string sc = "rapid-switch";
            Place(A);
            EmitterTuner.Press(A, "Alpha FM");
            _sim.Run(6.0f);

            float first = _sim.Now;
            string[] dial = { "Beta FM", "Gamma FM", "Alpha FM", "Beta FM", "Gamma FM" };
            for (int i = 0; i < dial.Length; i++)
            {
                _sim.Note("press -> " + dial[i]);
                EmitterTuner.Press(A, dial[i]);
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
            EmitterTuner.Press(A, "Alpha FM");
            _sim.Run(6.0f);

            _sim.Note("press -> Gamma FM");
            EmitterTuner.Press(A, "Gamma FM");
            _sim.Run(0.5f);                       // mid sweep
            float off = _sim.Now;
            _sim.Note("press -> off, mid-tune");
            EmitterTuner.Off(A);
            _sim.Run(3.0f);

            float quiet = _sim.MeanDb(off + 1.0f, off + 2.5f);
            Check(sc, quiet <= Floor, "switching off mid-tune goes quiet and stays quiet",
                  quiet.ToString("0.0") + " dBFS after the cut");
        }

        static void Broken_Station()
        {
            const string sc = "broken-station";
            Place(A);
            EmitterTuner.Press(A, "Alpha FM");
            _sim.Run(6.0f);

            float press = _sim.Now;
            _sim.Note("press -> Broken FM (nothing in it will decode)");
            EmitterTuner.Press(A, "Broken FM");
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
