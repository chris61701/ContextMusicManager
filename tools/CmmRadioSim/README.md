# CmmRadioSim — render a station change offline, through the REAL mixer

A dial press, rendered to a WAV in about four seconds, instead of starting the game and loading a
world for several minutes and judging by ear.

## Why it exists

Six consecutive attempts at the station change were shipped by ear. Four were wrong, two badly.
And the failures were never in the audio maths — they were in the seams:

- `Config/radio.xml` silently overrode three settled values, so **every in-game listening round was
  spent on numbers nobody approved**, and no schema bump could ever reach them.
- `FadeVoiceOut` did two jobs; removing the call for one silently took the other, and the dial
  stopped working entirely.
- A route was gated on a state flag, so audio was discarded when the flag was clear.
- Loading the next station was the same operation that killed the current one.

**An offline model that reimplemented the DSP would have reproduced exactly none of those.** So
this drives the shipping `CMM_Decoder.dll`: the real mixer, the real receiver stage, the real voice
lifecycle, the real decoders, headless on a null device, drained through `cmm_mix_pump_capture`.
What comes out is what the game plays.

## Run

```
CmmRadioSim <A.mp3> <B.mp3> <out.wav>
            [--config <radio.xml>] [--startA sec] [--startB sec] [--gainA g] [--gainB g]
```

`--config` reads a real `Config/radio.xml` and prints every value that differs from the shipped
default. **Point it at a live install before trusting any listening judgement** — that one line of
output is the check that was missing for the whole effort.

## The acceptance test

Render the same material through both this and `tools/CmmTuneRender` (the approved reference) and
compare. To compare *structure*, turn the randomness off on both sides — wander is ±4 dB by design
and will swamp a 2 dB structural difference:

```
# reference
tunerender srcA.mp3 20 bench.wav 0.55 0.0 180 2122 15  0.0 0.0 14 0  -5 0.15 5 \
           srcB.mp3 30  1.2 1.5 2.8  0  1.0 0.9 1.0 0.06  0.52 0.0 9 1800 0.28 0.912 \
           0 0 0 0  0 0  1.0 9 400 2600
# the game, with the same material under the press (the reference holds 4.275 s before it)
CmmRadioSim srcA.mp3 srcB.mp3 game.wav --config flat.xml --startA 21.246 --startB 36.2
```

As of 2026-08-23 those agree to a **mean 1.0 dB** across the whole event.

## What it does NOT run

`PersonalRadio.cs` — that needs Unity. `Timeline` replays the sequence of native calls a dial press
produces, with the same timings, read off that file. **Keep them in step.** If the state machine
changes and this does not, it becomes a faithful simulator of something that no longer exists.

Two things the game has that this does not: the tick is quantised to 4 Hz (up to 250 ms of jitter
on the swap and the arrival), and the output cushion can be far deeper. Neither changes the sound,
but both change the timing you would measure against a log.

## The UI

`tools/CmmRadioLab` is a window over this: a slider per setting, **Render GAME** and **Render
REFERENCE** side by side, and A/B playback. It shells out to this tool and to `CmmTuneRender`
rather than linking either, because the native mixer is a process-wide singleton with its own
producer thread — a wedged render should not take the window with it.

Two things it does that are worth having:

- **Load radio.xml prints every value that disagrees with the shipped default**, and the disagreeing
  knobs turn red and stay red. That is the check whose absence sent the whole tuning effort to
  numbers nobody approved.
- **Save edits radio.xml in place**, keeping its comments, its schema stamp and every key the UI
  does not own, with a `.bak` alongside. So a sound dialled in here ships as-is.
