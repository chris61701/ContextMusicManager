# CmmEmitLab

Renders **one retune** through the real native audio code — for a world speaker and for the
pocket radio — and prints when each one's static and music actually arrive, and at what level.

```
bash tools/CmmEmitLab/build.sh
tools/CmmEmitLab/emitlab.exe          # summary
tools/CmmEmitLab/emitlab.exe --csv    # + a 10 ms timeline
```

## Why

The emitter tuning was debugged for fourteen hours entirely by ear, one report at a time, and
the loop never converged. Several fixes caused the next symptom. Three separate causes turned
out to be numbers nobody had measured:

- the off-air carrier was **11 dB louder** than the hiss it replaced, while being reported as
  inaudible;
- the hiss swung **12 dB** depending on whether the player had touched the pocket radio, because
  only `PersonalRadio` pushed the shared receiver parameters;
- the arrive fade put **72% of its span below −19 dB**, because it decayed virtual *distance*
  linearly through a 2.5-power falloff.

Every one took minutes with a probe and hours by ear.

`PersonalRadio` is the specification — it sounds right. So both instruments render through the
same metering and print side by side. *"The emitter is 900 ms late and 12 dB quiet"* is a fact
you can act on; *"it feels wrong"* is not.

## What is real

**Real:** `cmm_mix.c` and `cmm_out.c` verbatim (`#include`d, so file-statics like `g_emRx`,
`g_emTone*` and `g_out` are observable), the actual producer thread, the actual receiver DSP,
the actual spatialiser and rings. The call order mirrors `EmitterAudioLayer`'s.

**Not real:** the decoders (one synthetic noise source stands in under `KIND_WAV`), and the
managed-side point fades, which live in C# and are **mirrored** in `joinExtra()`. If you change
`ApplyBirthFades`/`FadeExtra` in `EmitterAudioLayer.cs`, change `joinExtra` too — otherwise this
harness will quietly lie to you.

## How it separates static from music

Three passes, identical timing:

| pass | what | gives |
|---|---|---|
| `silent` | programme muted | the **static** envelope |
| `clear` | receiver never armed | the **music** envelope |
| `both` | the real thing | what the player hears |

The report is read off the first two. The third is there to catch the case where they do not sum
to what actually comes out.

## Status — calibrated, and it works

Three faults were in the harness, not the mod:

1. **The ring was metres deep.** `tightFrames = RATE/5`, `maxFrames = RATE*2` — so the producer
   ran up to two seconds ahead and the callback drained audio generated long before the call
   that shaped it. Ring depth is *latency between a control call and its audio*. One block now.
2. **Both drains were fighting.** `cmm_mix_pump_capture` and the device callback are alternative
   drains of the same bus, and a device was open for the emitter — so the radio pass pumped an
   already-drained bus and got silence. The music bus feeds `cmm_out` too, so one path measures
   both and they are directly comparable.
3. **Passes contaminated each other.** `cmm_mix_start_ex` is not re-entrant, `stop_all_now`
   triggers an idle teardown that reallocates the output (leaving the stand-in device pointing at
   freed memory), and the receiver, tone envelopes and ring depth all carry across. Running the
   same numbers twice gave a **650 ms swing** in the static onset. The parent now re-executes
   itself once per pass; each child runs one sequence in a fresh address space.

### First measurement

```
WORLD SPEAKER
  static  onset   460 ms  peak  -20.9 dBFS  clears 2360 ms
  music   onset   690 ms  peak  -41.6 dBFS  within3dB   never
  static +20.7 dB over the music it covers

POCKET RADIO
  static  onset    50 ms  peak  -18.2 dBFS  clears 2130 ms
  music   onset     0 ms  peak  -33.9 dBFS  within3dB 2350 ms
  static +15.7 dB over the music it covers

DIFFERENCE   static onset +410 ms   static/music +5.0 dB
```

Two open questions it hands over, both worth chasing:

- **The emitter's static arrives 410 ms later** than the radio's, from an identical 700 ms signal
  ramp with identical receiver parameters. Same rate, same knee — so it is level or chain, not
  timing.
- **The emitter's music never settles** within the 6 s window (`within3dB never`), while the
  radio's settles at 2350 ms.

## Known real bug this surfaced

`ApplyBirthFades` expresses the mandatory music hold as `gains[i] += range` — added virtual
distance on the **point**. But the point carries *both* the station's music and the receiver's
static, on the same lane. **So the music hold silences the static for its whole duration**, which
is the reported "static, then nothing, then music". Not yet fixed.

Related: `tools/CmmRadioSim` and `tools/CmmRadioLab` do the same job for the pocket radio's bus
path only, and are the reason that instrument has been stable.
