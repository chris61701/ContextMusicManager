# CMM Handoff — Remaining Audio/Perf Issues

Self-contained handoff for a fresh agent with no prior session context.

**Repo:** `ContextMusicManager` (7 Days to Die music mod). Branch `cleanup/identical-behavior`.

**Just completed (context, not part of this handoff):** resolver alloc squeeze +
scan cadence lowered `0.5s → 0.1s` (commit `0c445a0`). Compile-verified against
`GameVersion="3.0.0 b259"`, **not** runtime-verified.

**Build/verify recipe** (compile-check without deploying):

```
MSBuild ContextMusicManager.csproj /t:Build /p:CMMDeployAll=false /p:OutputPath=temp_build /p:GameVersion="3.1.0 b14"
```

The shipped binary is compiled **once** against the current release (3.1.0 b14) and runs
on A20/A21/2.6/3.1 via reflection bridges — so the 3.x compile is the gate. MSBuild is at
`C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`.
Clean up the `temp_build`/`temp_buildConfig` leftovers afterward.

---

## Issue 1 — Stutter on first play of slow-decoding (Opus) tracks  ★ primary

**Symptom (user-reported):** audible hitch when a track first loads, worst on Opus.
Likely an **audio-domain glitch (music stutters/underruns), not a framerate drop** —
decode already runs off the main thread.

**Already ruled out / established:**
- Decode is off-main-thread: `NativeStartWorker` (dedicated thread, AboveNormal) opens
  the decoder → `CmmMixer.Play` → native producer thread decodes. See
  `Harmony/ContextMusicManager.AudioPlayer.cs:1019` and `Harmony/CmmMixer.cs`.
- File selection at trigger time is in-memory (cached `directorySnapshot`), not disk I/O.
- Loudness measurement is non-blocking (`BelowNormal` worker); an un-measured track just
  plays at gain 1.0 and settles later — **not** the stutter.

**Hypothesis to test first:** the staged track's **decoder is opened cold at first play**
— the file is pre-picked and loudness pre-measured, but the *decoder is never
pre-opened/pre-rolled*. So the native producer decodes Opus from zero and can't fill the
output ring before the voice goes audible → **underrun**.

**How to confirm:** `cmm_mix_get_output_stats` returns an `underruns` field
(`Harmony/CmmMixer.cs:82-84`). Surface it (DebugHud or log) and watch it right after a
fresh Opus track starts. If it climbs → confirmed.

**Fix directions (once confirmed):**
- Pre-open + pre-roll the staged decoder ahead of switch-in (mirror how file pick +
  loudness are already staged in `EnsureStagedPins`/`StageNextFile`,
  `Harmony/ContextMusicManager.AudioPlayer.cs:249`), **or**
- Hold a deeper output cushion for the first ~few hundred ms of any new voice / for slow
  codecs — today `DeepBuffer` is only held during world-load
  (`Harmony/ContextMusicManager.AudioPlayer.cs:161`, `Harmony/CmmMixer.cs:135`).
- Native ring/priming logic lives in `CMM_Decoder/cmm_mix.c` and `cmm_out.c` — check the
  initial fill threshold before a voice is marked audible.

---

## Issue 2 — Verify ahead-of-time normalization actually covers first play

**Status:** the mechanism exists — menu prescan measures one track/context
(`MenuPrescanDrain`, `Harmony/ContextMusicManager.AudioPlayer.cs:334`); in-game
`LoudnessMaintenanceTick` → `EnsureStagedPins` keeps each inactive context's staged pin
measured. **But not proven to close every gap.**

**Gaps to check:**
- The staged pin's measure is fired async (`BelowNormal`) and isn't guaranteed complete
  before a fast switch-in → track can start un-measured (volume settle, audible on large
  corrections).
- Multi-track advance uses `PreferPreStagedNext`
  (`Harmony/ContextMusicManager.AudioPlayer.cs:~1240`) — confirm the *next* track is
  actually pre-measured, not just the first switch-in pin.
- Random picks that diverge from the pinned track.

**Deliverable:** instrument whether tracks reach `Play` with
`LoudnessStore.IsMeasured(file) == false`, count occurrences per context, and decide if
the staging window needs widening (measure N-ahead, or measure synchronously during the
deep-buffer window).

---

## Issue 3 — Resolver at 20 ms (deferred stretch goal)

**Context:** cadence is now 100 ms (a safe no-op win). 20 ms (50 Hz) was explicitly
deferred. The blocker is **not** C# allocation (that's now squeezed) — it's **MoonSharp**:
`_script.Call` in `Harmony/ScriptHost.cs:250` allocates internally every invocation, plus
~20 `DynValue` allocs/tick in `MarshalSnapshot` (`Harmony/ScriptHost.cs:353`). At 50 Hz on
Unity's main thread that becomes the dominant GC source — a stutter risk in itself.

**Two viable paths (needs a decision):**
- **(a) Off-thread split:** main thread does the Unity poll (`BuildSnapshot` — must stay
  main-thread, it reads `world.Entities`/`player.position`); a worker runs the MoonSharp
  resolve + marshalling off-frame; tokens applied back on main thread. Requires
  double-buffering the `GameState` (currently a single reused `_gameState`). Makes 20 ms
  frame-safe regardless of MoonSharp churn.
- **(b) Dirty-field marshalling:** only push changed snapshot fields into the Lua table
  (most are stable tick-to-tick) to cut `DynValue` churn — reduces but doesn't remove the
  `_script.Call` floor.

**Prerequisite:** measure first. CMMPerf already splits `ScanPoll` / `ScanMarshal` /
`ScanLuaCall` / `ScanGate` per phase (enable `PerfTiming`, stress with Ctrl+Alt+P). Get
real per-tick µs + alloc numbers before committing to (a) or (b).

---

**Suggested order:** Issue 1 (confirm underrun → pre-roll fix) is highest user-visible
value and most likely the actual reported problem. Issue 2 supports it. Issue 3 only if
faster context reaction is still wanted after 100 ms is validated in-game.
