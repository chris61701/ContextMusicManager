# CmmEmitSim — the emitter tuning state machine, offline, at speed

```
dotnet run --project tools/CmmEmitSim -c Release
dotnet run --project tools/CmmEmitSim -c Release -- --only retune-cold --echo --csv
```

Exit code 0 = every invariant held. A run is a few seconds.

## Why it exists

Twelve harnesses in `tools/` compile real `Harmony/*.cs`. **None of them compiled
`EmitterAudioLayer.cs`** — the file every tuning bug of the last week lived in.

`CmmEmitLab` mirrors that logic in C. `CmmTuneWeb` mirrors it in JavaScript. **A mirror cannot
have a missing call.** It only has what was written into it — so the two defects of 2026-08-30
(a mirror gain that was never restored, a swap that committed to a slot number captured before
the station had one) were structurally invisible to every tool we had, while both were plainly
visible in a game log.

This one models nothing.

## What is real

`EmitterAudioLayer` in its entirety — every tune, borrow, re-aim, carrier, slot steal and fade —
plus `CmmMixer`, `RadioStationCatalog` and its anchored schedule, `cmm_mix.c`, `cmm_out.c`, the
receiver DSP, the spatialiser and the decoder. The scenarios press a dial; everything between
presses is shipped code deciding what to do.

## What is not

The block registry, the tuner's radial menu, and the acoustics flood + field cache. All three
need a voxel world, none is under test, and each has its own coverage (`CmmAcousticsJudge`, the
`acsweep` captures). Each is replaced by a fake in `Fakes.cs` whose whole surface is a handful of
fields the harness sets directly.

**The rule that keeps the fakes honest:** a fake may only ever be a SOURCE of input to the state
machine, never a model of its behaviour. The moment a fake starts deciding something, it is a
mirror again — and mirrors are why this harness had to be written.

## How it runs faster than real time

One step is one game frame: set the clock, tick the state machine, then collect exactly the audio
that tick produced. **The audio is the clock** — the sim advances by the frames the native
producer actually rendered, so the managed state machine and the DSP cannot drift apart the way
they would if the harness kept its own time.

The producer thread fills eagerly (it only sleeps when the ring is full), so draining hard makes
it render flat out, bounded by decode speed rather than by the wall clock.

`CmmClock` is the one shipped file the harness replaces.

## What it measures

Every assertion is one of the user's reports, stated as a number:

| assertion | the report it is |
|---|---|
| `no hole between press and arrival` | "the static stopped playing before the music started" |
| `no level step while audible` | "snapping as if it is stitching two different volume clips together" |
| `static arrives promptly` | "sometimes the static isn't coming in at all" |
| `the station being left plays on under the noise` | step 2 — buried, never stopped |
| `every arrival had a programme under it` | the 2026-08-30 defect, as an invariant |
| `switching off mid-tune goes quiet and stays quiet` | the hard cut of step 15 |
| `a station that will not open sits on the carrier` | step 8 |

| `the carrier is raised once for the whole dial turn` | "static, a little silence, then more static before the tone" |
| `the song stops when its pool goes away` | the playlist is removed and the song plays on |

`--csv` writes the full tape per scenario: ear dBFS per frame, plus `sig` / lane dBFS / mirror
gain / tone envelope for all six lanes, with the presses annotated.

## One process per scenario

`EmitterAudioLayer` is a static singleton with twelve-second grace timers, a pending-start latch,
and a native mixer `cmm_mix_start_ex` will not re-enter cleanly. Resetting all of that from
outside would mean modelling it, which is the mistake this harness exists to avoid — so the
parent run spawns a child per scenario and adds up the verdicts. `--only <name>` is the child.
`CmmEmitLab` settled on the same answer for the same reason.

It matters: `off-mid-tune` passed alone and failed after `rapid-switch`, purely because the
previous scenario's audio was still draining. A result that depends on what ran before it is not
a result.

## Two known flakes

`retune-cold` and `rapid-switch` each fail roughly one run in four, and NEITHER is a regression --
both flake identically on `main` (measured: `rapid-switch` 3/12 before a change, 1/10 after). Do
not read one red run as a break; re-run it.

`rapid-switch` is worth a look one day rather than a widened budget: the hole is 367-400 ms and it
lands at t=2.75 s **every time**, with a decision log byte-identical to the passing runs. Same
presses, same timestamps, different audio -- so it is below the state machine, in the producer or
the capture.

## What is NOT deterministic

The clock is driven, but a station start is a decoder open plus a seek **on a ThreadPool thread**,
so how many ticks pass before the first sound is not fixed — measured across runs at 83, 133 and
167 ms for the same scenario. So the assertions split the two: `StaticBy` owns the onset delay
and carries a budget wide enough for it, while `NoHole` measures from *when the sound actually
starts*, which is not variable because by then nothing is waiting on a thread.

Scenarios wait for a station to be audible rather than pressing on a stopwatch, for the same
reason.

## The library

Written by the harness at KNOWN levels (`Library.cs`, −20 dBFS sines, a different pitch per
station so a capture is readable by eye). Loudness normalisation is switched OFF, so a level
assertion is arithmetic rather than an opinion about a track. "Broken FM" is a real station
folder holding a file that will not decode — that is not an edge case, it is step 8.

## One thing this cost, worth remembering

The headless capture never rendered emitter lanes at all. `cmm_out_drain_into` moved the ring
pointers and threw the slot rings away; the entire emitter mix — spatialisation, reverb, the
receiver — lived only inside the device callback. So every offline measurement of a world
speaker had been silence, and `cmm_out_drain_into`'s own comment promised "the mixer's real
output as samples". That is true now: the callback body is `cmm_out_render`, and both the device
and the harness call it.
