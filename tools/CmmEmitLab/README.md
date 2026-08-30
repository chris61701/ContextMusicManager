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

## STATUS — NOT YET CALIBRATED

It builds, boots the real mixer, opens a real output and drives `cmm_out_data_callback` for both
instruments. **The scene setup is not right yet** and its numbers should not be trusted until
these are fixed:

1. **The pocket radio pass renders nothing** (`−999 dBFS` composite). `cmm_mix_pump_capture`
   is returning silence — most likely the bus needs the receiver marked active and the voice
   marked radio before the first pump, or the capture path needs the device *not* open.
2. **The emitter's static peaks at the end of the window**, not during the leave. Either the
   receiver is not being armed down as the call sequence intends, or the meter is running past
   the sequence it means to measure.
3. **`t_onset` returns −1 for both statics**, so the −60 dBFS floor is wrong for this scene, or
   nothing is rendering into the first blocks at all.

Fix those three and the summary becomes meaningful. Until then treat this as a working rig with
an uncalibrated scene, not as a source of numbers.

Related: `tools/CmmRadioSim` and `tools/CmmRadioLab` do the same job for the pocket radio's bus
path only, and are the reason that instrument has been stable.
