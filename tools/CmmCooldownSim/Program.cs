using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CMM
{
    internal static class Program
    {
        static int _fail, _pass;
        static string _scenario;
        static bool _trace;
        static World _w;

        static void Expect(bool cond, string what)
        {
            if (cond) { _pass++; Console.WriteLine("    ok    " + what); }
            else
            {
                _fail++;
                Console.WriteLine("    FAIL  " + what);
                if (_w != null && !_trace)
                {
                    Console.WriteLine("          ── log ──");
                    foreach (var e in _w.Log.Where(e => e.Kind != "eof" || true).Take(80)) Console.WriteLine("          " + e);
                }
            }
        }

        // An audible start of `ctx` (fresh, resumed, crossfaded or reclaimed) at/after t.
        static Ev StartOf(World w, double t, string ctx) =>
            w.Log.FirstOrDefault(e => e.T >= t - 1e-9 && e.Ctx == ctx
                && (e.Kind == "start" || e.Kind == "start(resume)" || e.Kind == "crossfade" || e.Kind == "reclaim"));
        static bool NoStartOf(World w, double from, double to, string ctx) =>
            !w.Log.Any(e => e.T >= from && e.T < to && e.Ctx == ctx
                && (e.Kind == "start" || e.Kind == "start(resume)" || e.Kind == "crossfade" || e.Kind == "reclaim"));
        static Ev Eof(World w, double t, string ctx) => w.Log.FirstOrDefault(e => e.T >= t && e.Kind == "eof" && e.Ctx == ctx);
        static bool Near(double v, double want, double tol) => Math.Abs(v - want) <= tol;
        static string F(double v) => v.ToString("F2");

        static void Clean(World w)
        {
            Expect(w.GateViolations == 0, $"no audio ever started for a resting context ({w.GateViolations})");
            Expect(w.Strands == 0, $"no unintended silences ({w.Strands})");
            Expect(w.DoubleVoices == 0, $"never two live music voices at once ({w.DoubleVoices})");
        }

        static readonly List<(string id, string title, Action body)> Scenarios = new List<(string, string, Action)>();
        static void S(string id, string title, Action body) => Scenarios.Add((id, title, body));

        static World NewWorld(int seed = 1)
        {
            _w?.Dispose();
            _w = new World(seed) { Trace = _trace };
            return _w;
        }

        static void Ambient3(World w, string ctx = "Ambient", double len = 60)
        {
            for (int i = 1; i <= 3; i++) w.Lib.Add(w.Src(ctx), ctx.ToLower() + i, len);
        }

        static int Main(string[] args)
        {
            string only = null;
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--only" || args[i] == "only") && i + 1 < args.Length) only = args[++i];
                if ((args[i] == "--trace" || args[i] == "trace") && i + 1 < args.Length) { only = args[++i]; _trace = true; }
            }

            Define();
            foreach (var (id, title, body) in Scenarios)
            {
                if (only != null && !string.Equals(only, id, StringComparison.OrdinalIgnoreCase)) continue;
                _scenario = id;
                Console.WriteLine($"── {id}  {title}");
                try { body(); }
                catch (Exception ex) { _fail++; Console.WriteLine("    FAIL  threw: " + ex); }
            }
            if (only == null || only.Equals("FUZZ", StringComparison.OrdinalIgnoreCase)) Fuzz();
            _w?.Dispose();

            Console.WriteLine();
            Console.WriteLine(_fail == 0 ? $"ALL {_pass} CHECKS PASSED" : $"{_fail} CHECK(S) FAILED, {_pass} passed");
            return _fail == 0 ? 0 : 1;
        }

        static void Define()
        {
            // ── The basics ─────────────────────────────────────────────────────
            S("S1", "a track ends → 180 s of silence → the next fades in at fadeintime", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(1);
                Expect(w.PlayingCtx == "Ambient", "Ambient is playing");
                w.Run(65);
                var eof = Eof(w, 0, "Ambient");
                var rest = w.FirstSince(0, "rest", "Ambient");
                Expect(eof != null && rest != null, "the track played out and a rest began");
                Expect(rest != null && w.Log.All(e => e.Kind != "crossfade"), "no crossfade into the next track");
                w.Run(170);
                Expect(NoStartOf(w, 60, w.Now, "Ambient"), $"nothing starts during the rest (t={F(w.Now)})");
                Expect(!w.Since(61, "converge").Any(), "the convergence net never re-asserts during the rest");
                w.Run(10);
                var next = StartOf(w, 61, "Ambient");
                Expect(next != null, "the next track starts");
                if (next != null && eof != null)
                {
                    double gap = next.T - eof.T;
                    Expect(gap >= 180.2 - 0.05 && gap <= 180.2 + 0.4, $"silence after the audible end = {F(gap)} s (want 180.2 = 180 + 0.2 cushion)");
                    Expect(next.Note == "fade 5.00", $"it fades in at the context's fadeintime ({next.Note})");
                    Expect(next.File != eof.File, "a different track");
                }
                Clean(w);
            });

            S("S2", "cooldown 0 = the old behaviour: crossfade at the lead, no silence", () =>
            {
                var w = NewWorld(); Ambient3(w); w.SetCooldown("Ambient", 0); w.Active.Add("Ambient");
                w.Run(70);
                var xf = w.FirstSince(0, "crossfade", "Ambient");
                Expect(xf != null && Near(xf.T, 55, 0.4), $"crossfade at ~55 s (60 - 5 s lead): {(xf != null ? F(xf.T) : "none")}");
                Expect(w.FirstSince(0, "rest") == null, "no rest");
                Clean(w);
            });

            S("S3", "a context's own number wins: cooldown=\"45\"", () =>
            {
                var w = NewWorld(); Ambient3(w); w.SetCooldown("Ambient", 45); w.Active.Add("Ambient");
                w.Run(120);
                var eof = Eof(w, 0, "Ambient"); var next = StartOf(w, 61, "Ambient");
                Expect(next != null && eof != null && Near(next.T - eof.T, 45.2, 0.4), $"45 s rest ({(next != null && eof != null ? F(next.T - eof.T) : "-")})");
                Clean(w);
            });

            S("S4", "hot-reload re-times a rest in progress (default 180 → 60, then → 0)", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(100);
                w.DefaultTrackCooldown = 60f;                   // settings.xml edited mid-rest
                w.Run(30);
                var eof = Eof(w, 0, "Ambient"); var next = StartOf(w, 61, "Ambient");
                Expect(next != null && Near(next.T - eof.T, 60.2, 0.4), $"rest re-timed to 60 s from the END, not from the edit ({(next != null ? F(next.T - eof.T) : "none")})");
                w.Run(80);                                      // second track ended ~180, resting
                double t = w.Now;
                Expect(w.Tc.IsResting("Ambient"), "resting again after the second track");
                w.DefaultTrackCooldown = 0f;
                w.Run(1);
                Expect(StartOf(w, t, "Ambient") != null, "default → 0 ends the rest at once");
                Clean(w);
            });

            S("S5", "cooldown turned ON mid-track (0 → 120): no crossfade, a 120 s rest", () =>
            {
                var w = NewWorld(); Ambient3(w); w.SetCooldown("Ambient", 0); w.Active.Add("Ambient");
                w.Run(20);
                w.SetCooldown("Ambient", 120);
                w.Run(70);
                Expect(w.FirstSince(0, "crossfade") == null, "the pre-staged crossfade did not fire");
                Expect(w.FirstSince(0, "rest", "Ambient") != null, "a rest began at the end");
                w.Run(130);
                var eof = Eof(w, 0, "Ambient"); var next = StartOf(w, 61, "Ambient");
                Expect(next != null && Near(next.T - eof.T, 120.2, 0.4), $"120 s ({(next != null ? F(next.T - eof.T) : "none")})");
                Clean(w);
            });

            S("S22", "TrackCooldownsEnabled=false: every context crossfades; flipping it mid-rest plays at once; back on rests again", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(100);
                Expect(w.Tc.IsResting("Ambient") && w.Playing == null, "on (default): resting after the first track");
                w.TrackCooldownsEnabled = false; double t = w.Now; w.Run(1);
                Expect(StartOf(w, t, "Ambient") != null, "switched off mid-rest → plays at once");
                w.Run(65);
                Expect(w.FirstSince(t, "crossfade", "Ambient") != null && w.FirstSince(t, "rest") == null, "off: the next track crossfades, no rest");
                w.TrackCooldownsEnabled = true; t = w.Now; w.Run(70);
                Expect(w.FirstSince(t, "rest", "Ambient") != null, "back on: the next end rests again");

                var h = NewWorld(); Ambient3(h, "Home"); Ambient3(h, "Ambient", 300); h.TrackCooldownsEnabled = false;
                h.Active.Add("Ambient"); h.Run(30); h.Active.Add("Home"); h.Run(2);
                Expect(h.PlayingCtx == "Home", "off: walking home plays Home (nothing rests, so nothing yields)");
                h.Run(70);
                Expect(h.FirstSince(0, "rest") == null && h.PlayingCtx == "Home", "off: Home's tracks run into each other");
                Clean(w); Clean(h);
            });

            // ── Interruptions ──────────────────────────────────────────────────
            S("S6", "combat during the rest: plays at once; back to ambient = wait out the REST of it", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Lib.Add("combat", "c1", 400); w.Active.Add("Ambient");
                w.Run(100);
                w.Active.Add("Combat");
                w.Run(2);
                Expect(w.PlayingCtx == "Combat", "combat plays immediately");
                w.Run(58);
                w.Active.Remove("Combat");
                w.Run(5);
                Expect(w.FirstSince(160, "held", "Ambient") != null, "ambient comes back resting (held silent)");
                Expect(w.Playing == null, "and combat's voice was faded out — nothing audible");
                w.Run(70);
                Expect(NoStartOf(w, 160, w.Now, "Ambient"), "ambient stays silent until its rest ends");
                w.Run(10);
                var eof = Eof(w, 0, "Ambient"); var next = StartOf(w, 160, "Ambient");
                Expect(next != null && Near(next.T - eof.T, 180.2, 0.5), $"ambient resumes 180 s after ITS track ended ({(next != null ? F(next.T - eof.T) : "none")})");
                Clean(w);
            });

            S("S7", "combat outlasts the rest: ambient plays as soon as combat ends", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Lib.Add("combat", "c1", 600); w.Active.Add("Ambient");
                w.Run(100); w.Active.Add("Combat"); w.Run(400); w.Active.Remove("Combat");
                double t = w.Now; w.Run(4);
                var next = StartOf(w, t, "Ambient");
                Expect(next != null && next.T - t < 2.5, $"ambient back {(next != null ? F(next.T - t) : "-")} s after combat ended (eviction 1.5 s)");
                Expect(next != null && next.Kind == "start", "a fresh track, not a resume of the finished one");
                Clean(w);
            });

            S("S8", "interrupted MID-TRACK: no rest; the track resumes and the rest follows ITS end", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Lib.Add("combat", "c1", 400); w.Active.Add("Ambient");
                w.Run(30); w.Active.Add("Combat"); w.Run(10); w.Active.Remove("Combat"); w.Run(5);
                Expect(w.FirstSince(0, "rest") == null, "leaving mid-track did not start a rest");
                var back = StartOf(w, 40, "Ambient");
                Expect(back != null && (back.Kind == "start(resume)" || back.Kind == "reclaim"), $"ambient resumed ({back?.Kind})");
                w.Run(60);
                Expect(w.FirstSince(40, "rest", "Ambient") != null, "the rest began when the resumed track finished");
                Clean(w);
            });

            S("S9", "Next during the rest plays now; the next end rests again", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(100); w.SkipTrack(false); w.Run(0.2);
                var s = StartOf(w, 100, "Ambient");
                Expect(s != null && s.T - 100 < 0.15 && s.Note == "fade 0.25", $"Next starts a track at once at the skip fade ({s?.Note})");
                Expect(!w.Tc.IsResting("Ambient"), "the rest is over");
                w.Run(65);
                Expect(w.FirstSince(101, "rest", "Ambient") != null, "the skipped-to track's end rests again");
                Clean(w);
            });

            S("S10", "Prev during the rest replays the previous track", () =>
            {
                var w = NewWorld(); Ambient3(w); w.SetCooldown("Ambient", 10); w.Active.Add("Ambient");
                w.Run(1); var first = StartOf(w, 0, "Ambient");
                w.Run(135);                                   // 2nd track 70.2..130.2, now resting
                Expect(w.Tc.IsResting("Ambient"), "resting after the second track");
                double t = w.Now; w.SkipTrack(true); w.Run(0.2);
                var s = StartOf(w, t, "Ambient");
                Expect(s != null && s.File == first.File, "Prev plays the track before the one that just ended");
                Clean(w);
            });

            S("S11", "the clock stops while paused", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(100); w.SetPaused(true); w.Run(100); w.SetPaused(false);
                w.Run(135);
                Expect(NoStartOf(w, 61, w.Now, "Ambient"), $"no start at the unpaused deadline (t={F(w.Now)})");
                w.Run(10);
                var eof = Eof(w, 0, "Ambient"); var next = StartOf(w, 61, "Ambient");
                Expect(next != null && Near(next.T - eof.T, 280.2, 0.5), $"rest = 180 s of unpaused time ({(next != null ? F(next.T - eof.T) : "none")} wall)");
                Clean(w);
            });

            S("S12", "a track that ends UNDER the pause is a natural end; the rest starts on resume", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(50); w.SetPaused(true); w.Run(40); w.SetPaused(false); w.Run(2);
                var end = w.FirstSince(0, "end", "Ambient");
                Expect(end != null && end.Note == "natural", $"classified natural ({end?.Note})");
                Expect(NoStartOf(w, 61, w.Now, "Ambient"), "nothing starts on unpause");
                w.Run(300);
                var next = StartOf(w, 61, "Ambient");
                Expect(next != null && Near(next.T, 270.2, 0.6), $"next at ~270 = 60 s played + 30 s paused + 180 ({(next != null ? F(next.T) : "none")})");
                Clean(w);
            });

            S("S13", "a voice that dies early (decode error, device reset) is NOT an end", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(20); w.KillCurrentVoice(); w.Run(1);
                var end = w.FirstSince(0, "end", "Ambient");
                Expect(end != null && end.Note == "EARLY", "classified as an early stop");
                Expect(w.FirstSince(0, "rest") == null, "no rest");
                Expect(StartOf(w, 20.01, "Ambient") != null, "the next track started straight away");
                Clean(w);
            });

            S("S14", "a track of UNKNOWN length (raw ADTS) rests on EOF", () =>
            {
                var w = NewWorld(); w.Lib.Add("ambient", "u1", 45, knownLen: false); w.Lib.Add("ambient", "u2", 45, knownLen: false);
                w.Active.Add("Ambient"); w.Run(50);
                var end = w.FirstSince(0, "end", "Ambient");
                Expect(end != null && end.Note == "natural" && w.FirstSince(0, "rest") != null, "natural end, rest began");
                Clean(w);
            });

            // ── Per-context, and cooldownreset ─────────────────────────────────
            S("S15", "night falls mid-rest: the night context plays at once; day comes back resting", () =>
            {
                var w = NewWorld(); Ambient3(w, "Ambient_Forest"); Ambient3(w, "Ambient_Forest_Night", 500);
                w.Active.Add("Ambient_Forest");
                w.Run(100);
                w.Active.Add("Ambient_Forest_Night"); w.Run(2);
                Expect(w.PlayingCtx == "Ambient_Forest_Night", "night plays immediately (per-context rest)");
                w.Run(98); w.Active.Remove("Ambient_Forest_Night"); w.Run(5);
                Expect(w.FirstSince(200, "held", "Ambient_Forest") != null && w.Playing == null, "day returns still resting, night faded out");
                w.Run(160);
                var next = StartOf(w, 200, "Ambient_Forest");
                Expect(next != null && Near(next.T, 240.2, 0.6), $"day plays at its own deadline ({(next != null ? F(next.T) : "none")})");
                Clean(w);
            });

            S("S16", "cooldownreset: POI resets on leaving; Home ships false and keeps its rest; true = plays at once", () =>
            {
                var w = NewWorld(); Ambient3(w, "POI"); Ambient3(w); w.PoiId = "A";
                w.Active.Add("POI"); w.Active.Add("Ambient");
                w.Run(100);
                Expect(w.Tc.IsResting("POI"), "POI resting");
                w.Active.Remove("POI"); w.PoiId = "None"; w.Run(30);
                Expect(!w.Tc.IsResting("POI"), "walking out cleared the POI's rest");
                Expect(w.PlayingCtx == "Ambient", "ambient plays outside");
                w.Active.Add("POI"); w.PoiId = "A"; double t = w.Now; w.Run(1);
                Expect(StartOf(w, t, "POI") != null, "re-entering plays POI music at once");

                var d = NewWorld(); Ambient3(d, "Home"); Ambient3(d); d.Active.Add("Home"); d.Active.Add("Ambient");
                d.Run(100); d.Active.Remove("Home"); d.Run(30);
                Expect(d.Tc.IsResting("Home") && !d.Since(0, "leave-reset").Any(), "Home (ships cooldownreset=false) keeps its rest after leaving");
                Clean(d);

                var h = NewWorld(); Ambient3(h, "Home"); Ambient3(h); h.SetCooldown("Home", -1, true);
                h.Active.Add("Home"); h.Active.Add("Ambient");
                h.Run(100); h.Active.Remove("Home"); h.Run(30);
                Expect(!h.Tc.IsResting("Home"), "Home with cooldownreset=true cleared its rest on leaving");
                h.Active.Add("Home"); t = h.Now; h.Run(1);
                Expect(StartOf(h, t, "Home") != null, "…and coming home plays at once");
                Clean(h);
            });

            S("S17", "straight from one POI into the next (same context): the rest clears", () =>
            {
                var w = NewWorld(); Ambient3(w, "POI"); w.PoiId = "A"; w.Active.Add("POI");
                w.Run(100);
                w.PoiId = "B"; double t = w.Now; w.Run(1);
                Expect(w.FirstSince(t, "poi-reset", "POI") != null, "the POI change cleared the rest");
                Expect(StartOf(w, t, "POI") != null, "and the new POI plays at once");
                Clean(w);
            });

            S("S18", "a tier playing its fallback (POI_T3 → POI): leaving clears POI's rest", () =>
            {
                var w = NewWorld(); Ambient3(w, "POI"); Ambient3(w); w.PoiId = "A";
                w.Active.Add("POI_T3"); w.Active.Add("Ambient");
                w.Run(2);
                Expect(w.PlayingCtx == "POI", "POI_T3 (empty) plays POI's music");
                w.Run(98);
                Expect(w.Tc.IsResting("POI"), "POI resting");
                w.Active.Remove("POI_T3");                    // PoiId kept: this is the EVICTION path alone
                w.Run(3);
                Expect(w.Since(100, "leave-reset", "POI").Any(), "eviction of POI_T3 cleared POI");
            });

            S("S19", "a POI with no music of its own does NOT reset the ambient playing underneath", () =>
            {
                var w = NewWorld(); Ambient3(w); w.PoiId = "A";
                w.Active.Add("POI_T3"); w.Active.Add("Ambient");
                w.Run(100);
                Expect(w.Tc.IsResting("Ambient"), "ambient resting (the POI resolves to it via Stack)");
                w.Active.Remove("POI_T3"); w.PoiId = "None"; w.Run(20);
                Expect(w.Tc.IsResting("Ambient") && NoStartOf(w, 100, w.Now, "Ambient"), "ambient keeps resting after walking out");
                Clean(w);
            });

            S("S21", "a biome ambient ships reset=false: leave and come back = still resting; reset=true plays at once", () =>
            {
                var s = NewWorld(); Ambient3(s, "Ambient_Forest"); Ambient3(s);
                s.Active.Add("Ambient_Forest"); s.Active.Add("Ambient");
                s.Run(100); s.Active.Remove("Ambient_Forest"); s.Run(5);
                Expect(s.Tc.IsResting("Ambient_Forest") && s.PlayingCtx == "Ambient", "out of the forest: its rest kept, plain Ambient plays");
                s.Run(10); s.Active.Add("Ambient_Forest"); double t0 = s.Now; s.Run(3);
                Expect(s.FirstSince(t0, "held", "Ambient_Forest") != null && s.Playing == null, "back in: forest holds its silence (no yield on ambient)");
                Clean(s);

                var w = NewWorld(); Ambient3(w, "Ambient_Forest"); Ambient3(w); w.SetCooldown("Ambient_Forest", -1, true);
                w.Active.Add("Ambient_Forest"); w.Active.Add("Ambient");
                w.Run(100);
                Expect(w.Tc.IsResting("Ambient_Forest"), "forest resting");
                w.Active.Remove("Ambient_Forest"); w.Run(5);
                Expect(!w.Tc.IsResting("Ambient_Forest"), "walking out of the forest cleared its rest");
                Expect(w.PlayingCtx == "Ambient", "plain Ambient plays (it never ended a track, so no rest)");
                w.Run(10); w.Active.Add("Ambient_Forest"); double t = w.Now; w.Run(1);
                Expect(StartOf(w, t, "Ambient_Forest") != null, "back in the forest → plays at once");
                Clean(w);
            });

            S("S20", "combat INSIDE the POI is not leaving: the rest survives", () =>
            {
                var w = NewWorld(); Ambient3(w, "POI"); w.Lib.Add("combat", "c1", 400); w.PoiId = "A"; w.Active.Add("POI");
                w.Run(100); w.Active.Add("Combat"); w.Run(60); w.Active.Remove("Combat"); w.Run(5);
                Expect(!w.Since(0, "leave-reset").Any() && !w.Since(0, "poi-reset").Any(), "no reset");
                Expect(w.FirstSince(160, "held", "POI") != null, "POI comes back resting");
                w.Run(200);
                var next = StartOf(w, 160, "POI");
                Expect(next != null && Near(next.T, 240.2, 0.6), $"POI plays at its own deadline ({(next != null ? F(next.T) : "none")})");
                Clean(w);
            });

            // ── cooldownyield (Home) ───────────────────────────────────────────
            S("Y1", "Home resting, walk in while ambient plays: ambient carries on; Home crossfades in when its rest ends", () =>
            {
                var w = NewWorld(); Ambient3(w, "Home"); Ambient3(w, "Ambient", 300);
                w.Active.Add("Home"); w.Active.Add("Ambient");
                w.Run(62);
                Expect(w.Tc.IsResting("Home") && w.Playing == null, "Home's track ended at home → silence, nothing fills it");
                w.Active.Remove("Home"); w.Run(5);
                Expect(w.PlayingCtx == "Ambient" && w.Tc.IsResting("Home"), "outside: ambient plays, Home keeps its rest");
                w.Active.Add("Home"); double t = w.Now; w.Run(30);
                Expect(w.PlayingCtx == "Ambient" && !w.Since(t, "held", "Home").Any(), "back home: ambient carries on, Home does not silence it");
                Expect(NoStartOf(w, t, w.Now, "Ambient"), "the same ambient track — not restarted");
                w.Run(150);
                var eof = Eof(w, 0, "Home"); var h = StartOf(w, t, "Home");
                Expect(h != null && eof != null && Near(h.T - eof.T, 180.2, 0.5), $"Home comes in the moment its rest ends ({(h != null && eof != null ? F(h.T - eof.T) : "none")} s after its track)");
                w.Run(5);
                Expect(w.PlayingCtx == "Home", "Home owns the layer, ambient faded out under it");
                Clean(w);
            });

            S("Y2", "the carried-on ambient track ENDS during Home's rest → silence; ambient is not started again", () =>
            {
                var w = NewWorld(); Ambient3(w, "Home"); Ambient3(w, "Ambient", 80);
                w.Active.Add("Home"); w.Active.Add("Ambient");
                w.Run(62); w.Active.Remove("Home"); w.Run(10); w.Active.Add("Home"); w.Run(10);
                Expect(w.PlayingCtx == "Ambient", "ambient carries on at home");
                w.Run(80);
                Expect(w.Tc.IsResting("Ambient") && w.FirstSince(140, "held", "Home") != null && w.Playing == null,
                       "ambient's track ended → it rests, Home holds the layer silent");
                w.Run(100);
                var eof = Eof(w, 0, "Home"); var h = StartOf(w, 140, "Home");
                Expect(h != null && Near(h.T - eof.T, 180.2, 0.5), $"Home plays at its own deadline ({(h != null ? F(h.T - eof.T) : "none")})");
                Expect(NoStartOf(w, 140, w.Now, "Ambient"), "ambient never restarted at home");
                Clean(w);
            });

            S("Y3", "a resting Home_Night yields to the NIGHT ambient, never to day Home", () =>
            {
                var w = NewWorld(); Ambient3(w, "Home_Night"); Ambient3(w, "Home"); Ambient3(w, "Ambient_Forest_Night", 300);
                w.Active.Add("Home_Night"); w.Active.Add("Ambient_Forest_Night");
                w.Run(62); w.Active.Remove("Home_Night"); w.Run(5);
                Expect(w.PlayingCtx == "Ambient_Forest_Night", "outside: night ambient");
                w.Active.Add("Home_Night"); double t = w.Now; w.Run(20);
                Expect(w.PlayingCtx == "Ambient_Forest_Night" && NoStartOf(w, t, w.Now, "Home"), "back home at night: night ambient carries on, day Home never plays");
                Clean(w);
            });

            S("Y4", "Home_Night with an EMPTY folder plays Home's music; resting Home yields under it too", () =>
            {
                var w = NewWorld(); w.Lib.Folder("home/night"); Ambient3(w, "Home"); Ambient3(w, "Ambient_Forest_Night", 300);
                w.Active.Add("Home_Night"); w.Active.Add("Ambient_Forest_Night");
                w.Run(2);
                Expect(w.PlayingCtx == "Home", "empty Home_Night → Home's music");
                w.Run(60); w.Active.Remove("Home_Night"); w.Run(5);
                w.Active.Add("Home_Night"); double t = w.Now; w.Run(20);
                Expect(w.Tc.IsResting("Home") && w.PlayingCtx == "Ambient_Forest_Night" && NoStartOf(w, t, w.Now, "Home"),
                       "back home: the night ambient carries on while Home rests");
                Clean(w);
            });

            S("Y5", "combat at home cuts into the carried ambient: afterwards the ambient RESUMES; Home still comes in on time", () =>
            {
                var w = NewWorld(); Ambient3(w, "Home"); Ambient3(w, "Ambient", 300); w.Lib.Add("combat", "c1", 400);
                w.Active.Add("Home"); w.Active.Add("Ambient");
                w.Run(62); w.Active.Remove("Home"); w.Run(5); w.Active.Add("Home"); w.Run(5);
                Expect(w.PlayingCtx == "Ambient", "ambient carrying on at home");
                var amb = w.Playing?.File;
                w.Active.Add("Combat"); w.Run(20);
                Expect(w.PlayingCtx == "Combat", "combat plays");
                w.Active.Remove("Combat"); double t = w.Now; w.Run(5);
                var back = StartOf(w, t, "Ambient");
                Expect(back != null && (back.Kind == "start(resume)" || back.Kind == "reclaim") && back.File == amb,
                       $"after the fight the same ambient track picks up where it left off ({back?.Kind})");
                Expect(!w.Since(t, "held", "Home").Any(), "Home does not hold silence over it");
                w.Run(160);
                var eof = Eof(w, 0, "Home"); var h = StartOf(w, t, "Home");
                Expect(h != null && Near(h.T - eof.T, 180.2, 0.5), $"Home still comes in at its deadline ({(h != null ? F(h.T - eof.T) : "none")})");
                Clean(w);
            });

            S("Y7", "a fight longer than ambient's stale window: no resume left, Home holds silence (nothing new starts)", () =>
            {
                var w = NewWorld(); Ambient3(w, "Home"); Ambient3(w, "Ambient", 300); w.Lib.Add("combat", "c1", 400);
                w.Active.Add("Home"); w.Active.Add("Ambient");
                w.Run(62); w.Active.Remove("Home"); w.Run(5); w.Active.Add("Home"); w.Run(5);
                w.Active.Add("Combat"); w.Run(140); w.Active.Remove("Combat"); double t = w.Now; w.Run(5);
                Expect(w.FirstSince(t, "held", "Home") != null && NoStartOf(w, t, w.Now, "Ambient"),
                       "ambient's resume went stale (140 s > stale 120) → Home's silence, ambient not restarted");
                Clean(w);
            });

            S("Y8", "Home interrupted the ambient BEFORE its own rest: Home's track ends → silence, the parked ambient stays parked", () =>
            {
                var w = NewWorld(); Ambient3(w, "Home"); Ambient3(w, "Ambient", 300);
                w.Active.Add("Ambient"); w.Run(30);
                w.Active.Add("Home"); w.Run(2);
                Expect(w.PlayingCtx == "Home", "walking home (not resting) → Home takes over from ambient mid-track");
                w.Run(70);
                Expect(w.Tc.IsResting("Home") && w.Playing == null, "Home's track ended → silence");
                double t = w.Now; w.Run(60);
                Expect(NoStartOf(w, t - 5, w.Now, "Ambient"), "the ambient Home interrupted is not resumed into Home's rest");
                Clean(w);
            });

            S("Y6", "cooldownyield=false: walking in mid-ambient during Home's rest fades the ambient out", () =>
            {
                var w = NewWorld(); Ambient3(w, "Home"); Ambient3(w, "Ambient", 300); w.SetCooldown("Home", -1, false, false);
                w.Active.Add("Home"); w.Active.Add("Ambient");
                w.Run(62); w.Active.Remove("Home"); w.Run(5); w.Active.Add("Home"); double t = w.Now; w.Run(5);
                Expect(w.FirstSince(t, "held", "Home") != null && w.Playing == null, "Home holds silence over the ambient");
                Clean(w);
            });

            // ── Files removed and added ────────────────────────────────────────
            S("F1", "the track that just ended is deleted during the rest", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(100);
                var ended = w.FirstSince(0, "end", "Ambient").File;
                w.Lib.Remove(ended); w.Run(3);
                Expect(w.FirstSince(100, "fw-hotswap", "Ambient") != null, "the file watcher's hot-swap fired");
                Expect(NoStartOf(w, 100, w.Now, "Ambient"), "…and the gate kept it silent");
                w.Run(260);
                var next = StartOf(w, 103, "Ambient");
                Expect(next != null && Near(next.T, 240.2, 0.6) && File.Exists(next.File), $"rest ran its full length, then an existing track ({(next != null ? F(next.T) : "none")})");
                Clean(w);
            });

            S("F2", "the PRE-STAGED next track is deleted during the rest; another is added", () =>
            {
                var w = NewWorld(); var a = w.Lib.Add("ambient", "a1", 60); var b = w.Lib.Add("ambient", "a2", 60);
                w.Active.Add("Ambient"); w.Run(100);
                var played = w.FirstSince(0, "end", "Ambient").File;
                var staged = played == a ? b : a;             // with two tracks the pre-stage is the other one
                w.Lib.Remove(staged); var c = w.Lib.Add("ambient", "a3", 60);
                w.Run(270);
                var next = StartOf(w, 100, "Ambient");
                Expect(next != null && next.File == c, $"rest end picked the new track ({(next != null ? Path.GetFileName(next.File) : "none")})");
                Expect(!w.Log.Any(e => e.Kind == "open-FAIL"), "no failed open of the deleted file");
                Clean(w);
            });

            S("F3", "the resting context's folder is EMPTIED, then refilled before the rest ends", () =>
            {
                var w = NewWorld(); Ambient3(w, "Ambient_Forest"); Ambient3(w, "Ambient", 500);
                w.Active.Add("Ambient_Forest"); w.Active.Add("Ambient");
                w.Run(100);
                Expect(w.Tc.IsResting("Ambient_Forest"), "forest resting");
                w.Lib.Empty("ambient/forest"); w.Run(3);
                Expect(w.PlayingCtx == "Ambient", "emptied → its fallback (Ambient, not resting) plays");
                w.Lib.Add("ambient/forest", "f9", 60); w.Run(4);
                Expect(w.FirstSince(103, "held", "Ambient_Forest") != null, "refilled → forest is back, still resting, held silent");
                Expect(w.Playing == null, "ambient faded out under it (per-context rest: forest owns the layer)");
                w.Run(260);
                var next = StartOf(w, 103, "Ambient_Forest");
                Expect(next != null && Near(next.T, 240.2, 0.6), $"forest plays at its original deadline ({(next != null ? F(next.T) : "none")})");
                Clean(w);
            });

            S("F4", "an EMPTY folder gains music while its fallback rests → it plays at once", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Lib.Folder("ambient/forest");
                w.Active.Add("Ambient_Forest"); w.Active.Add("Ambient");
                w.Run(100);
                Expect(w.Tc.IsResting("Ambient") && w.Playing == null, "ambient (the fallback) resting");
                w.Lib.Add("ambient/forest", "f1", 60); double t = w.Now; w.Run(3);
                var s = StartOf(w, t, "Ambient_Forest");
                Expect(s != null && s.T - t < 2.2, $"forest plays as soon as the watcher sees it ({(s != null ? F(s.T - t) : "-")} s)");
                Expect(w.Tc.IsResting("Ambient"), "ambient's own rest is untouched");
                Clean(w);
            });

            S("F5", "an empty folder gains music while its fallback is MID-TRACK → cuts in (unchanged)", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Lib.Folder("ambient/forest");
                w.Active.Add("Ambient_Forest"); w.Active.Add("Ambient");
                w.Run(30); w.Lib.Add("ambient/forest", "f1", 60); w.Run(3);
                Expect(w.PlayingCtx == "Ambient_Forest", "the more specific context takes over (as before this feature)");
                Expect(w.FirstSince(0, "rest") == null, "no rest — ambient's track did not end");
                Clean(w);
            });

            S("F6", "a folder emptied MID-TRACK: falls back, and no rest is recorded", () =>
            {
                var w = NewWorld(); Ambient3(w, "Ambient_Forest"); Ambient3(w, "Ambient", 500);
                w.Active.Add("Ambient_Forest"); w.Active.Add("Ambient");
                w.Run(30); w.Lib.Empty("ambient/forest"); w.Run(3);
                Expect(w.PlayingCtx == "Ambient", "fallback plays");
                Expect(!w.Tc.IsResting("Ambient_Forest"), "the forest is not resting — its track never ended");
                w.Lib.Add("ambient/forest", "f9", 60); w.Run(3);
                Expect(w.PlayingCtx == "Ambient_Forest", "refilled → plays at once");
                Clean(w);
            });

            S("F7", "ALL music removed during the rest, restored after it would have ended", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(100); w.Lib.Empty("ambient"); w.Run(300);
                Expect(w.Playing == null && w.currentContext.Context == "Silence", "silence (nothing to play)");
                Ambient3(w); double t = w.Now; w.Run(3);
                var s = StartOf(w, t, "Ambient");
                Expect(s != null && s.T - t < 2.2, "restored after the deadline → plays at once");
                Clean(w);
            });

            S("F8", "files deleted and put back (same paths) during the rest: the rest persists", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(100); w.Lib.Empty("ambient"); w.Run(10); Ambient3(w); w.Run(5);
                Expect(w.Tc.IsResting("Ambient") && NoStartOf(w, 100, w.Now, "Ambient"), "back, still resting");
                w.Run(250);
                var s = StartOf(w, 100, "Ambient");
                Expect(s != null && Near(s.T, 240.2, 0.6), $"plays at the original deadline ({(s != null ? F(s.T) : "none")})");
                Clean(w);
            });

            S("F9", "a single-track context: rest, then the same track again", () =>
            {
                var w = NewWorld(); var a = w.Lib.Add("ambient", "only", 60); w.Active.Add("Ambient");
                w.Run(365);
                var s = StartOf(w, 61, "Ambient");
                Expect(s != null && s.File == a && Near(s.T, 240.2, 0.6), "the one track again after the rest");
                Clean(w);
            });

            S("F10", "a track added during the rest is eligible when it ends", () =>
            {
                var w = NewWorld(); var a = w.Lib.Add("ambient", "a1", 60); w.Active.Add("Ambient");
                w.Run(100); var b = w.Lib.Add("ambient", "a2", 60); w.Run(265);
                var s = StartOf(w, 61, "Ambient");
                Expect(s != null && s.File == b, $"the new track plays (the old one is avoided) ({(s != null ? Path.GetFileName(s.File) : "none")})");
                Clean(w);
            });

            // ── Everything else that touches the music layer ───────────────────
            S("O1", "death clears every rest; the respawn plays at once", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(100); w.Die(); w.Run(10); w.Respawn(); double t = w.Now; w.Run(1);
                Expect(!w.Tc.IsResting("Ambient") && StartOf(w, t, "Ambient") != null, "plays on respawn");
                Clean(w);
            });

            S("O2", "personal radio during the rest", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(100); w.SetRadio(true); w.Run(100); w.SetRadio(false); w.Run(5);
                Expect(NoStartOf(w, 100, w.Now, "Ambient"), "radio off before the deadline → still resting");
                w.Run(160);
                var s = StartOf(w, 200, "Ambient");
                Expect(s != null && Near(s.T, 240.2, 0.6), $"plays at the deadline ({(s != null ? F(s.T) : "none")})");

                var v = NewWorld(); Ambient3(v); v.Active.Add("Ambient");
                v.Run(100); v.SetRadio(true); v.Run(300); v.SetRadio(false); double t = v.Now; v.Run(1);
                Expect(StartOf(v, t, "Ambient") != null, "radio off after the deadline → plays at once");
                Clean(v);
            });

            S("O3", "a POI_Cleared stinger during the POI's rest does not restart the POI", () =>
            {
                var w = NewWorld(); Ambient3(w, "POI"); w.Lib.Add("events/poi_cleared", "fanfare", 5); w.PoiId = "A"; w.Active.Add("POI");
                w.Run(100); w.Active.Add("POI_Cleared"); w.Run(1); w.Active.Remove("POI_Cleared");
                Expect(w.PlayingCtx == "POI_Cleared", "the stinger plays");
                w.Run(10);
                Expect(NoStartOf(w, 100, w.Now, "POI") && w.FirstSince(100, "held", "POI") != null, "after it, POI is held silent");
                w.Run(255);
                var s = StartOf(w, 100, "POI");
                Expect(s != null && Near(s.T, 240.2, 0.6), $"POI plays at its deadline ({(s != null ? F(s.T) : "none")})");
                Clean(w);
            });

            S("O4", "a looping context never rests, whatever its cooldown says", () =>
            {
                var w = NewWorld(); w.Lib.Add("looping", "l1", 30); w.Active.Add("Looping"); w.Run(200);
                Expect(w.FirstSince(0, "rest") == null && w.PlayingCtx == "Looping", "still playing, no rest");
                Clean(w);
            });

            S("O6", "radio on and off over a context with NO cooldown: the music comes back", () =>
            {
                var w = NewWorld(); w.Lib.Add("combat", "c1", 400); w.Active.Add("Combat");
                w.Run(20); w.SetRadio(true); w.Run(10); w.SetRadio(false); double t = w.Now; w.Run(5);
                Expect(StartOf(w, t, "Combat") != null, "combat resumes after the radio");
                Clean(w);
            });

            S("O5", "the source is repointed by a contexts.xml reload during the rest", () =>
            {
                var w = NewWorld(); Ambient3(w); w.Active.Add("Ambient");
                w.Run(100); w.Repoint("Ambient"); w.Run(1);
                Expect(!w.Tc.IsResting("Ambient") && StartOf(w, 100, "Ambient") != null, "rest cleared, plays");
                Clean(w);
            });
        }

        // ── Fuzz: hours of random play against the invariants ─────────────────
        static void Fuzz()
        {
            Console.WriteLine("── FUZZ  random play, 6 h simulated per seed, invariants checked every 50 ms");
            int seeds = 12, gate = 0, strand = 0, dbl = 0, threw = 0, starts = 0, rests = 0, held = 0;
            for (int seed = 1; seed <= seeds; seed++)
            {
                var w = NewWorld(seed);
                var rng = new Random(seed * 7919);
                string[] folders = { "ambient", "ambient/forest", "ambient/forest/_night", "ambient/_night", "poi", "poi/t3", "home", "home/night", "combat", "events/poi_cleared" };
                int n = 0;
                foreach (var f in folders) for (int i = 0; i < 3; i++) w.Lib.Add(f, $"{f.Replace('/', '_')}{n++}", 20 + rng.Next(200), rng.Next(10) != 0);
                w.Active.Add("Ambient"); w.Active.Add("Ambient_Forest");
                string[] togglable = { "Combat", "POI", "POI_T3", "Home", "Home_Night", "Ambient_Forest_Night", "Ambient_Night", "POI_Cleared" };
                float[] cds = { 0f, -1f, 15f, 60f, 300f };
                try
                {
                    while (w.Now < 6 * 3600)
                    {
                        w.Run(2 + rng.NextDouble() * 40);
                        switch (rng.Next(16))
                        {
                            case 0: case 1: case 2:
                                { var c = togglable[rng.Next(togglable.Length)]; if (!w.Active.Remove(c)) w.Active.Add(c); break; }
                            case 3: w.PoiId = rng.Next(3) == 0 ? "None" : "P" + rng.Next(4); break;
                            case 4:
                                { var f = folders[rng.Next(folders.Length)]; var l = w.Lib.List(f); if (l.Length > 0) w.Lib.Remove(l[rng.Next(l.Length)]); break; }
                            case 5:
                                { var f = folders[rng.Next(folders.Length)]; w.Lib.Add(f, "x" + n++, 15 + rng.Next(200), rng.Next(8) != 0); break; }
                            case 6: if (rng.Next(4) == 0) w.Lib.Empty(folders[rng.Next(folders.Length)]); break;
                            case 7: w.SkipTrack(rng.Next(3) == 0); break;
                            case 8: w.SetPaused(!w._pauseDucked); break;
                            case 9: w.SetRadio(!w.RadioOn); break;
                            case 10: if (rng.Next(5) == 0) { w.Die(); w.Run(5); w.Respawn(); } break;
                            case 11: if (rng.Next(3) == 0 && w._nativeVoiceId != 0) w.KillCurrentVoice(); break;
                            case 12: { var c = new[] { "Ambient", "Ambient_Forest", "POI", "Home" }[rng.Next(4)]; w.SetCooldown(c, cds[rng.Next(cds.Length)], rng.Next(2) == 0, rng.Next(2) == 0); break; }
                            case 13: w.DefaultTrackCooldown = new[] { 0f, 30f, 120f, 300f }[rng.Next(4)]; break;
                            case 14: if (rng.Next(4) == 0) w.Repoint(new[] { "Ambient", "POI", "Home" }[rng.Next(3)]); break;
                            case 15: if (rng.Next(6) == 0) w.TrackCooldownsEnabled = !w.TrackCooldownsEnabled; break;
                            default: break;
                        }
                    }
                    if (w._pauseDucked) w.SetPaused(false);
                }
                catch (Exception ex) { threw++; Console.WriteLine($"    seed {seed} threw: {ex}"); }
                gate += w.GateViolations; strand += w.Strands; dbl += w.DoubleVoices;
                starts += w.Log.Count(e => e.Kind.StartsWith("start") || e.Kind == "crossfade");
                rests += w.Log.Count(e => e.Kind == "rest"); held += w.Log.Count(e => e.Kind == "held");
                if (w.GateViolations + w.Strands + w.DoubleVoices > 0)
                {
                    Console.WriteLine($"    seed {seed}: gate {w.GateViolations}, strands {w.Strands}, double {w.DoubleVoices}");
                    foreach (var e in w.Log.Where(e => e.Kind == "STRAND" || e.Kind == "GATE-BYPASS").Take(5))
                    {
                        Console.WriteLine("      " + e);
                        foreach (var x in w.Log.Where(x => x.T >= e.T - 8 && x.T <= e.T + 0.5 && x.Kind != "eof")) Console.WriteLine("        " + x);
                    }
                }
            }
            Console.WriteLine($"    {seeds} seeds · {starts} starts · {rests} rests · {held} gate holds");
            Expect(threw == 0, "no exceptions");
            Expect(gate == 0, $"never started audio for a resting context ({gate})");
            Expect(strand == 0, $"no unintended silences ({strand})");
            Expect(dbl == 0, $"never two live voices ({dbl})");
        }
    }
}
