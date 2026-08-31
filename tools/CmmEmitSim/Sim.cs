using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CMM
{
    // ── THE RIG ──────────────────────────────────────────────────────────────────────────────
    //
    // One step is one game frame: set the clock, tick the REAL state machine, then collect
    // exactly the audio that tick produced. The audio is the clock — the sim advances by the
    // frames the native producer actually rendered, so the managed state machine and the DSP can
    // never drift apart the way they would if the harness kept its own time.
    //
    // It runs FASTER than real time because the producer thread fills eagerly (it only sleeps
    // when the ring is full), so draining hard makes it render flat out, bounded by decode speed
    // rather than by the wall clock. A two-second tune-in costs a fraction of a second.
    internal sealed class Sim
    {
        [DllImport("CMM_Decoder", CallingConvention = CallingConvention.Cdecl)]
        private static extern int cmm_mix_pump_capture(float[] dst, int frames);

        internal const int Rate = 48000;
        internal const int StepFrames = Rate / 60;      // one game frame

        private readonly float[] _buf     = new float[StepFrames * 2];
        private readonly float[] _scratch = new float[StepFrames * 2];
        private float _t;

        internal float Now => _t;

        // ── what the ear gets, and why ───────────────────────────────────────────────────────
        internal struct Sample
        {
            public float T;            // sim seconds
            public float Rms;          // bus RMS this step, LINEAR
            public float Peak;         // bus peak this step, linear
            public float[] Sig;        // per lane: receiver signal, -1 = not armed
            public float[] LaneRms;    // per lane: dry lane RMS after its receiver
            public float[] Mir;        // per lane: mirror gain, -1 = no mirror connected
            public float[] Tone;       // per lane: carrier envelope, -1 = silent
            // CONSUMER TRUTH, not what the layer meant to publish. A speaker can be in NO point
            // set for a tick -- held between two machines, or dropped by one before the other
            // picks it up -- and every lane-level reading stays perfectly healthy while the ear
            // hears nothing. That is a whole class of report ("a little silence") and the tape
            // could not see it.
            public float[] Pts;        // per lane: points the native mixer is rendering
            public float[] App;        // per lane: applied dry gain for the nearest point
            public string Note;        // scenario annotation, if any
        }

        internal readonly List<Sample> Tape = new List<Sample>();
        // Main-thread cost of every tick in this run, in milliseconds, in order.
        internal readonly List<float> TickMs = new List<float>();

        // RAW SAMPLES, off by default.
        //
        // The tape holds one RMS per FRAME, which is a 60 Hz view -- fine for "is it audible",
        // "did it step" and "was there a hole", which is everything the scenarios ask. It cannot
        // answer anything about the shape of a decay: Freeverb's comb delays are 25-37 ms and a
        // 16.7 ms sampling interval cannot resolve them, so the reverb lab measured the same
        // 67 ms repeat in six different rooms and it was an artefact of the tape, not the reverb.
        //
        // With this on, Step also keeps the mono sum of the capture at the real 48 kHz, and the
        // lab's autocorrelation and Schroeder integral run on that instead.
        internal bool RawCapture;
        internal readonly List<float> Raw = new List<float>();

        // Percentile over the ticks recorded so far. p=1 is the worst single tick.
        internal float TickPct(double p, int from = 0)
        {
            if (TickMs.Count <= from) return 0f;
            var v = TickMs.GetRange(from, TickMs.Count - from);
            v.Sort();
            int i = (int)Math.Round(p * (v.Count - 1));
            return v[i < 0 ? 0 : i >= v.Count ? v.Count - 1 : i];
        }
        internal float TickMean(int from = 0)
        {
            if (TickMs.Count <= from) return 0f;
            double s = 0; for (int i = from; i < TickMs.Count; i++) s += TickMs[i];
            return (float)(s / (TickMs.Count - from));
        }
        private string _pendingNote;

        internal static float Db(float lin) => lin > 1e-9f ? 20f * (float)Math.Log10(lin) : -140f;

        internal void Note(string s) { _pendingNote = s; SimLog.Line("--- " + s); }

        // Advance by `seconds`, one game frame at a time.
        internal void Run(float seconds)
        {
            int steps = (int)Math.Round(seconds * 60f);
            for (int i = 0; i < steps; i++) Step();
        }

        internal void Step()
        {
            _t += 1f / 60f;
            CmmClock.Set(_t);

            // THE COST OF THE TICK, on the thread the game would be running it on. This is the
            // number a stutter is made of: EmitterAudioLayer.Tick() is called from Unity's frame
            // loop, so whatever it spends here the frame waits for. Recorded per step rather than
            // averaged, because a mean hides the one 40 ms tick that is the whole complaint.
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EmitterAudioLayer.Tick();
            float tickMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0)
                                   * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            TickMs.Add(tickMs);

            // Collect one frame's worth, waiting on the producer rather than reading a hole it
            // has not filled yet. ONE buffer, one call shape: the earlier version allocated a
            // second array for the remainder, which meant two different destination sizes for the
            // same native write and no way to tell which one a corruption came from.
            int want = StepFrames, got = 0, spins = 0;
            Array.Clear(_buf, 0, _buf.Length);
            while (got < want)
            {
                int n = cmm_mix_pump_capture(_scratch, want - got);
                if (n <= 0)
                {
                    if (++spins > 20000) break;         // producer wedged; the report will show it
                    System.Threading.Thread.Sleep(0);
                    continue;
                }
                Array.Copy(_scratch, 0, _buf, got * 2, n * 2);
                got += n;
            }

            double sum = 0; float peak = 0f;
            for (int i = 0; i < got * 2; i++)
            {
                float v = _buf[i];
                sum += (double)v * v;
                float a = v < 0 ? -v : v;
                if (a > peak) peak = a;
            }
            if (RawCapture)
                for (int i = 0; i < got; i++) Raw.Add(0.5f * (_buf[i * 2] + _buf[i * 2 + 1]));

            int slots = CmmMixer.MaxEmitterSlots;
            var s = new Sample
            {
                T = _t,
                Rms = got > 0 ? (float)Math.Sqrt(sum / (got * 2)) : 0f,
                Peak = peak,
                Sig = new float[slots], LaneRms = new float[slots],
                Mir = new float[slots], Tone = new float[slots],
                Pts = new float[slots], App = new float[slots],
                Note = _pendingNote
            };
            _pendingNote = null;
            for (int k = 0; k < slots; k++)
            {
                s.Sig[k]     = CmmMixer.EmitterLaneStat(k, 0);
                s.LaneRms[k] = CmmMixer.EmitterLaneStat(k, 1);
                s.Mir[k]     = CmmMixer.EmitterLaneStat(k, 2);
                s.Tone[k]    = CmmMixer.EmitterLaneStat(k, 3);
                s.Pts[k]     = CmmMixer.EmitterLaneStat(k, 4);
                s.App[k]     = CmmMixer.EmitterLaneStat(k, 5);
            }
            Tape.Add(s);
        }

        // ── reading the tape ────────────────────────────────────────────────────────────────
        internal int IndexAt(float t)
        {
            for (int i = 0; i < Tape.Count; i++) if (Tape[i].T >= t) return i;
            return Tape.Count - 1;
        }

        // First moment at or after `from` where the ear is above `db`.
        internal float FirstAbove(float from, float db)
        {
            for (int i = IndexAt(from); i < Tape.Count; i++)
                if (Db(Tape[i].Rms) > db) return Tape[i].T;
            return float.NaN;
        }

        // The longest run, inside [from,to], where the ear sits at or below `db`. This is the
        // "hole" measure: the report "the static stopped playing before the music started" is a
        // hole, and so is "the music cut out immediately".
        internal float LongestHole(float from, float to, float db, out float holeAt)
        {
            float best = 0f, runStart = -1f; holeAt = float.NaN;
            for (int i = IndexAt(from); i < Tape.Count && Tape[i].T <= to; i++)
            {
                bool quiet = Db(Tape[i].Rms) <= db;
                if (quiet) { if (runStart < 0f) runStart = Tape[i].T; }
                else if (runStart >= 0f)
                {
                    float run = Tape[i].T - runStart;
                    if (run > best) { best = run; holeAt = runStart; }
                    runStart = -1f;
                }
            }
            if (runStart >= 0f)
            {
                float run = to - runStart;
                if (run > best) { best = run; holeAt = runStart; }
            }
            return best;
        }

        // The biggest frame-to-frame level change inside [from,to] while the ear is audible.
        // "Snapping as if it is stitching two different volume clips together" is this number.
        internal float BiggestStepDb(float from, float to, float floorDb, out float stepAt)
        {
            float worst = 0f; stepAt = float.NaN;
            int i0 = IndexAt(from);
            for (int i = i0 + 1; i < Tape.Count && Tape[i].T <= to; i++)
            {
                float a = Db(Tape[i - 1].Rms), b = Db(Tape[i].Rms);
                // BOTH ends must be audible. A sound STARTING is a step from digital silence by
                // construction, so counting it would flag every onset in every scenario; a
                // dropout is the same coin's other face and LongestHole is what measures that.
                // What is left is the thing actually being asked about: a level that jumps while
                // you are already listening to it.
                if (a <= floorDb || b <= floorDb) continue;
                float d = Math.Abs(b - a);
                if (d > worst) { worst = d; stepAt = Tape[i].T; }
            }
            return worst;
        }

        internal float MeanDb(float from, float to)
        {
            double sum = 0; int n = 0;
            for (int i = IndexAt(from); i < Tape.Count && Tape[i].T <= to; i++) { sum += Tape[i].Rms; n++; }
            return n > 0 ? Db((float)(sum / n)) : -140f;
        }
    }
}
