using System;
using System.Collections.Generic;
using CMM;

// ─────────────────────────────────────────────────────────────────────────────
// CmmResumeTest — the edge cases behind "pick up where I left off", driven against the
// SHIPPED rules in Harmony/ResumePolicy.cs (linked, not copied — see the csproj).
//
// Two rules are under test:
//   Decide()     — should a start replay the saved track at the saved position?
//   Hold()       — whose staleness clock stops while the personal radio owns the speaker?
//
// The radio broke BOTH for as long as it has existed and nothing could see it: the rules
// lived inside a UnityEngine-importing file, so the only instrument was playing the game.
// ─────────────────────────────────────────────────────────────────────────────
internal static class Program
{
    static int _pass, _fail;

    // A context's stale window, seconds — the real values from contexts.xml.
    const float Ambient = 120f, Tension = 30f, Poi = 120f;

    static void Main()
    {
        Console.WriteLine("CmmResumeTest — resume + staleness edge cases (shipped ResumePolicy)\n");

        Console.WriteLine("── Decide: when a saved position is honoured ─────────────────────────");
        // (label, expectResume, expectClear, the call)
        Case("plain re-entry inside the window",              true,  false, D(120f, 30f));
        Case("re-entry exactly at the window (>= is stale)",  false, true,  D(120f, 120f));
        Case("re-entry past the window",                      false, true,  D(120f, 121f));
        Case("Tension's shorter 30 s window, inside",         true,  false, D(Tension, 29f));
        Case("Tension's shorter 30 s window, past",           false, true,  D(Tension, 31f));
        Case("context is not resumable",                      false, false, D(Ambient, 5f, canResume: false));
        Case("nothing was ever saved (position 0)",           false, false, D(Ambient, 5f, pos: 0f));
        Case("negative position is not a position",           false, false, D(Ambient, 5f, pos: -1f));
        Case("no file recorded for the position",             false, false, D(Ambient, 5f, file: null));
        Case("empty file recorded",                           false, false, D(Ambient, 5f, file: ""));
        Case("file has since gone / is unplayable",           false, true,  D(Ambient, 5f, usable: false));
        Case("switching to the NEXT track, not returning",    false, false, D(Ambient, 5f, switching: true));
        Case("a forced skip-to-file overrides resume",        false, false, D(Ambient, 5f, forced: true));
        Case("still active (lastLeft < 0) is never stale",    true,  false, DRaw(Ambient, lastLeft: -1f, now: 9999f));
        Case("never-left beats even a huge clock",            true,  false, DRaw(Tension, lastLeft: -1f, now: 1e6f));

        Console.WriteLine("\n── Hold: whose clock stops while the radio has the speaker ───────────");
        HoldCase("the context you were in, still on the stack",  true,  onStack: true,  left: 100f, pos: 42f);
        HoldCase("a context you walked out of (off the stack)",  false, onStack: false, left: 100f, pos: 42f);
        HoldCase("on the stack but nothing saved to protect",    false, onStack: true,  left: 100f, pos: 0f);
        HoldCase("on the stack and still ACTIVE (left < 0)",     false, onStack: true,  left: -1f,  pos: 42f);

        Console.WriteLine("\n── The reported bug, end to end ─────────────────────────────────────");
        RadioScenario("30 s of radio, same POI",            broadcast: 30f,  onStack: true,  expectResume: true);
        RadioScenario("5 min of radio, same POI",           broadcast: 300f, onStack: true,  expectResume: true);
        RadioScenario("30 min of radio, same POI",          broadcast: 1800f, onStack: true, expectResume: true);
        RadioScenario("5 min of radio, walked out of POI",  broadcast: 300f, onStack: false, expectResume: false);
        RadioScenario("20 s of radio, walked out of POI",   broadcast: 20f,  onStack: false, expectResume: true);

        Console.WriteLine($"\n{_pass} passed, {_fail} failed.");
        Environment.Exit(_fail == 0 ? 0 : 1);
    }

    // ── Decide harness ───────────────────────────────────────────────────────
    static ResumePolicy.Decision D(float stale, float elapsed, bool canResume = true, float pos = 42.5f,
                                   string file = "track.mp3", bool usable = true,
                                   bool switching = false, bool forced = false)
        => ResumePolicy.Decide(canResume, pos, file, lastLeftTime: 1000f, now: 1000f + elapsed,
                               staleWindow: stale, switchingToNextTrack: switching, forced: forced,
                               fileUsable: _ => usable);

    static ResumePolicy.Decision DRaw(float stale, float lastLeft, float now)
        => ResumePolicy.Decide(true, 42.5f, "track.mp3", lastLeft, now, stale, false, false, _ => true);

    static void Case(string label, bool expectResume, bool expectClear, ResumePolicy.Decision d)
    {
        bool resumed = d.ResumeFile != null;
        bool ok = resumed == expectResume && d.ClearPosition == expectClear;
        // A resume must carry the position with it — a resume to 0 is just a fresh start.
        if (ok && resumed && d.StartSeconds <= 0f) ok = false;
        Report(ok, label, $"resume={resumed} clear={d.ClearPosition} at={d.StartSeconds:F1}s",
               $"resume={expectResume} clear={expectClear}");
    }

    static void HoldCase(string label, bool expect, bool onStack, float left, float pos)
        => Report(ResumePolicy.ShouldHold(onStack, left, pos) == expect, label,
                  ResumePolicy.ShouldHold(onStack, left, pos).ToString(), expect.ToString());

    // ── The full radio round trip: capture → broadcast → hold → decide ───────
    static void RadioScenario(string label, float broadcast, bool onStack, bool expectResume)
    {
        const string Ctx = "POI_Infested_T2";
        float takeover = 500f;                       // when the radio grabbed the speaker

        // 1) Tune(): the playhead is captured and the clock stamped at the takeover.
        var clocks    = new Dictionary<string, float> { [Ctx] = takeover };
        var positions = new Dictionary<string, float> { [Ctx] = 67.25f };

        // 2) TurnOff(): clocks of contexts still on the stack are pushed by the broadcast length.
        float dialOff = takeover + broadcast;
        var stack = onStack ? new List<string> { Ctx } : new List<string>();
        ResumePolicy.Hold(broadcast, stack, clocks, positions);

        // 3) The restart asks whether that saved position still stands.
        var d = ResumePolicy.Decide(true, positions[Ctx], "track.mp3", clocks[Ctx], dialOff,
                                    Poi, false, false, _ => true);
        bool resumed = d.ResumeFile != null;
        Report(resumed == expectResume, label,
               resumed ? $"resumed at {d.StartSeconds:F2}s" : "fresh track",
               expectResume ? "resumed" : "fresh track");
    }

    static void Report(bool ok, string label, string got, string want)
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {label,-52} {got}"); }
        else    { _fail++; Console.WriteLine($"  FAIL  {label,-52} got {got}; wanted {want}"); }
    }
}
