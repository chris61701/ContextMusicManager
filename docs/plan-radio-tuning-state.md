# Radio tuning: where it actually stands

2026-08-23, end of day. Read this before touching the tuning again. Supersedes every number in
earlier drafts of this file — the settings moved five times in one day and anything written down
mid-way is wrong.

## THE TOOL. Use it. Do not tune by ear in game.

`tools/CmmRadioLab` — a window: a slider per setting, **Render GAME** beside **Render REFERENCE**,
and A/B playback. It shells out to:

- `tools/CmmRadioSim` — renders a whole station change to a WAV by driving the **shipping**
  `CMM_Decoder.dll` headless (real mixer, real receiver, real voice lifecycle, real decoders),
  about four seconds a render.
- `tools/CmmTuneRender` — the offline model whose output was approved by ear, the reference.

**Those two agreeing is the acceptance test the radio never had.** As of today they agree to a mean
**1.0 dB** across the whole event with the randomness disabled on both sides (wander is ±4 dB by
design and swamps a 2 dB structural difference — turn it off before comparing anything).

Everything that went wrong this cycle went wrong because a question about how it *sounds* could
only be answered by loading a world. Six attempts shipped that way; four were wrong, two badly.

## What is deployed

**Everything, as of `56f5d98` (13:13).** Native ABI 49, schema 17, all four installs.

An earlier draft of this file said `8a28005` / `c97d527` / `95cd6af` were committed but never
deployed. That was wrong: the 12:28 build DID succeed and carried all three. What actually
happened is that the running 3.1 session had the *pre*-12:28 DLL loaded in memory, so it kept
reporting schema 16 while the file on disk was already schema 17 — and the 2.6 launch at 12:54
was the first process to load the new code. **A running game is not evidence of what is on
disk.** Check the DLL's timestamp and the config's schema stamp, not the session in front of you.

The loudness hold changes *when* music starts and a fault there is silence — the worst failure
this mod has. Still worth testing deliberately: enter a world with an unmeasured context, confirm
music arrives within ~3 s and at the right level.

## The two-player feedback loop (fixed 2026-08-23, `56f5d98`, unheard)

The radio and the context player fought once per fade for as long as the radio was on — heard as
"it keeps switching tracks". Both infer end-of-track from the same signal, *my voice is no longer
in the mixer*, and `cmm_mix_add` crossfades every other music voice out. Each one's start was the
thing that retired the other's voice, and each read that as EOF.

`PersonalRadio.SuppressesMusic` was checked in `StartContextCoroutine`, but the two auto-advance
paths and the loudness hold call `StartNativeContext` directly — three ways in, one guarded. The
gate now sits in `StartNativeContext`, which all three funnel through.

**The lesson is the shape, not the instance: a guard belongs at the choke point, not on the
path you happened to be looking at.** Ignition here was an 8 ms window — the radio dialled off
and straight back on.

## The settled numbers

Config lives in `Config/radio.xml` (radio) and `settings.xml` (loudness). All hot-reload.

| | value | |
|---|---|---|
| `RadioTuneLeaveMs` | **700** | station sinks under the rising static |
| `RadioTuneMinStaticMs` | **100** | static gap. A FLOOR — a slow file extends it |
| `RadioTuneArriveMs` | **1000** | station climbs back out |
| `RadioRecvKnee` | **0.90** | where the break-up happens. HIGH = sooner after the key |
| `RadioRecvKneeWidth` | 0.22 | how much of the ramp the event occupies. NARROW is right |
| `RadioRecvNoise` | **0.15** | static level. Set by ear in game |
| `RadioRecvClicks` / `ClickRate` | 0.18 / 180 | crackle. Above ~300/s it merges into a buzz |
| `RadioRecvWander` / `Flutter` / `FlutterHz` | 0.55 / 0.30 / 14 | the static must MOVE |
| `RadioRecvDeemphHz` | 2122 | 75 µs. The roar's colour |
| `RadioRecvFloorDb` / `MuteWidth` | 5 / 5 | how far the station sinks. SMALL IS RIGHT |
| `RadioRecvDrive` | 0.15 | |
| `RadioRecvMangle` | **1** | 0 = off, restores the plain fade-in |
| `RadioRecvLowCutHz` / `HighCutHz` | 400 / 2600 | the band an arriving station is squeezed into |
| `RadioRecvMakeupDb` | 9 | level put back. NOT optional |
| `RadioRecvNoiseHold` / `NoiseFloor` | 1.0 / 0.06 | |
| `RadioRecvWindowA` / `WindowB` | 0.28 / 0.912 | when the treble returns |
| `RadioRecvLockAt` / `LockMs` | 0.52 / 9 | stereo latches LAST |
| `RadioRecvHiFloorHz` / `Dullness` / `Mononess` | 1800 / 0.85 / 1 | |
| `LoudnessStartHoldMs` | 3000 | silent wait for a measurement, both radio and context |

Not in config: `SwapFade` = 0.25 s in `PersonalRadio.cs`, the handover crossfade.

Press → fully tuned is **~1.8 s**; the station starts breaking up **75 ms** after the key.

## The things that cost the most, and why

**The game had never once run the approved numbers.** `radio.xml` carried hand-edited schema-8
values (`Noise 1.6`, `Clicks 0.7`, `ClickRate 900`) that no migration could reach, because the
migration only rewrites a key still holding a *former default*. Every in-game listening round for
weeks was spent on a sound nobody had approved. **`CmmRadioLab`'s Load button now prints every value
that differs from the shipped default, in red.** That is the check whose absence caused this.

**"What is this actually modelling?"** The dial felt dead for two thirds of a second. Two commits
were spent making the wait *shorter* before anyone asked what the wait was *for*. Answer: the knee
models a station fading out on its own — driving out of range — where a leading stretch of "nothing
yet" is correct. Nobody drives out of range; they press a button. The plateau was modelling a wait
that does not exist.

**A demo is not a keypress.** The whole transition was settled on rendered demos, and in a demo
nobody presses the key, so half a second of dead plateau at the head of the leave is invisible. It
only exists as a defect when you are the one pressing the button. Applies to anything tuned this way.

**Normalised programme, fixed noise floor.** The noise was scaled by each track's loudness gain, on
the reasoning that the programme carries it so the noise must follow. True of a *raw* programme,
false of a normalised one — the music comes out at the same level either way, so scaling the noise
moved only the noise. A quiet track needing +12 dB got +12 dB of static. Level is a constant now.

**One function, two jobs.** `FadeVoiceOut` silenced the voice *and* released `_voiceId`. Removing
the call for the first silently took the second, and the dial stopped working entirely, because the
dispatch for the next station lives in the `_voiceId == 0` branch. Third time in that file that
moving a statement took an invisible control-flow side effect with it.

**Gating a route on a state flag loses audio.** Gating an *effect* on one is fine. The v48 refactor
returned early from the bus stage when the receiver was unarmed, discarding the radio's programme.

## Three predicted bugs that were NOT real

Tested in game rather than taken on trust. All three rested on `Tick`'s `player == null` return
firing when the game is paused. **The pause menu keeps a player.** The tick keeps running, the
station lands, the timers clear.

- the radio dialling itself after a menu trip — does not happen
- static over the menu music — does not happen
- a stinger mid-tune destroying the transition — does not happen; death during a change keeps the
  static through the death screen and then resolves the station correctly

Four agents read the same file and all reasoned from that return firing, because it is what the code
looks like it does, and none of them could run the game. **A code-path argument is a hypothesis.**

`POI_Cleared` silencing the radio rather than ducking it was tested and is **preferred** — the
`TEMP` marker is now a recorded decision, with the ducking recipe kept in a comment.

## Still open

- **Emitter levels** — tuned against an occlusion bug that has since been fixed, so every world
  speaker compensates for something that no longer happens. Needs ears. Biggest remaining item.
- **Dead settings still documented as live** — `RadioTuneGainPow` (its only consumer was removed at
  ABI 48) and the `RadioStatic*` group (the separate hash generator is gone). Both still ship with
  help text describing behaviour they no longer have.
- **~200 ms of clean full-volume music** can appear inside the static: `cmm_mix_add` runs on the
  worker, `SetVoiceRadio` on the main thread a frame later, and in between the voice routes straight
  to the bus unprocessed.
- **The static clips on loud tracks.** The reference reserves headroom explicitly (a 0.55 output
  scale); the game has no such guarantee and relies on loudness normalisation happening to provide
  it. Anything normalised above ~0.575 clips the hiss.
- `rising` is a whole-block value, so a ramp completing mid-block steps the residual noise ~35 dB.
- Doors, the four field-audit defects, emitter squelch + stereo pairs.
