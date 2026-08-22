# The FM dial: consultation outcome

Four agents, independently framed — critique the model as built, real receiver physics, the tuning
workflow, and the game/port constraints. Written 2026-08-22, after Chris said *"there's no way I
can fine tune this that sounds right."*

Companion to `plan-fm-dial.md` (the original design) and `plan-door-aperture.md` (same format).

## The headline: he is literally correct

The setting he is looking for **is not in the parameter space.** This was measured, not argued.

Take every slider to the extreme that helps — `noise` to max, soft mute defeated, `hcMin` maxed,
`deem` to 20 kHz, `noiseCurve` to minimum — and at the midpoint between two stations:

| | music | hiss | SNR |
|---|---|---|---|
| defaults | 0.0 dB | −53.6 dB | **+29.1 dB** |
| every slider pushed to help | −3.3 dB | −6.0 dB | **+2.7 dB** |

Even with four of the five downstream stages disabled, **the music is still louder than the static
at the point on the dial that is supposed to be pure static.** He is not failing to find the
setting. There isn't one. The complaint was a correct bug report.

## Three faults, each independently fatal

### 1. The music is de-emphasised. It never was supposed to be.

`index.html:750` puts a lowpass at 3183 Hz in the music path. Two things wrong with it.

**It is on the wrong signal.** Real FM is a *matched pair* — the transmitter pre-emphasises, the
receiver de-emphasises, and they cancel. Music comes out flat; the filter exists only to tame the
noise. The bench has no pre-emphasis, so every station eats the full curve, forever, at any signal
strength. A perfectly tuned station measures **−13.7 dB at 6 kHz and −23.0 dB at 10 kHz.**

**And it is the wrong slope.** A Web Audio biquad lowpass is 12 dB/oct; real de-emphasis is
6 dB/oct. At 12 kHz this is ~30 dB down where the real thing is ~11 dB down.

Consequences, all matching the complaint exactly:

- Everything sounds muffled and no slider fixes it. `hcMin`/`hcMax` — the two sliders explicitly
  labelled for "weak station goes dull", the behaviour the whole design is about — sit *downstream*
  of it and are the **weakest controls in the set** (4.9 and 6.3 dB of authority across their full
  range, against `deem`'s 17.4).
- **The readout lies.** `rHicut` proudly shows 15.0k at full signal while the actual −3 dB point is
  3.2 kHz. Tuning by ear against a display that does not describe the sound is impossible in
  principle.
- The hiss is ruined too: violet noise (+6 dB/oct) through −12 dB/oct nets −6 dB/oct — a *rumble*,
  which is precisely what the design doc says must not happen.

**Fix: de-emphasise the noise only.** One filter on the noise path, none on the music. Identical
physical result, and one fewer per-sample filter in the C port.

### 2. The station's level never falls, then falls off a cliff

`share` is normalised by the strongest station, so the one you are listening to is **always exactly
1.000** — `best === raw[winner]`, so `downDb` is 0 by construction. Swept across a whole band, one
station holds full volume across a 3.1 MHz rectangle and then goes `1.000 → 0.000` in one 10 kHz
step.

**The model reproduces the original bug — a discrete cut — at the level of station gain, and hides
it behind tone-shaping that varies smoothly.**

**Fix: stop normalising.** Drive each station's gain from its own response curve; delete
`best`/`second`/`share`/`downDb`. That single deletion fixes the step, fixes the single-station
case, removes three dead parameters, makes capture **emergent** (at the midpoint both stations are
weak *on their own terms*), and gives adjacent-channel bleed for free.

### 3. Capture and flutter are unreachable — about one third of one pixel

At the shipped defaults the second station is ~95 dB down everywhere except a sliver at the exact
midpoint. Measured:

- capture zone: **24 kHz = 1.0% of the gap = 0.15 of one key press**
- flutter zone: **5 kHz = 0.21% of the gap**, crossed in **1 millisecond** at the default sweep rate
- the band draws 18 kHz per pixel, so the flutter window is **one third of one pixel**
- `Flutter window` maxes at 3 dB — the range is ~10× too small to ever be audible

Three sliders sit under a heading explaining FM capture ratio, and none of them can ever be heard.

**Fix:** express the flutter window in MHz, not dB (~0.08 MHz ≈ 5 px ≈ 30 ms — a brief warble as
you cross, which is the real thing), and make the two-station test route ~1.9 MHz apart so the
midpoint sits at S ≈ 0.3, above the mute knee, where capture is audible at all.

## The physics we had wrong

From PA3FWM's technote on FM receiver noise (verified) — **FM produces two completely different
noise spectra, and switching between them IS the tuning sound:**

| | demod noise | after de-emphasis | sounds like |
|---|---|---|---|
| **no carrier** (between stations) | flat | flat to 2.1 kHz, then −6 dB/oct | dark, heavy **roar / rush** |
| **carrier present** (weak station) | triangular, +6 dB/oct | flat 2.1 → 15 kHz | bright, thin **hiss / sizzle** |

So the off-station sound is **darker** than the on-station hiss. That is the opposite of most
people's intuition, and it is a free, highly specific realism win.

CMM's shipped static is neither: `RadioStaticLowHz 350` / `RadioStaticHighHz 2000`
(`CMMConfig.cs:167`) is a midrange "shhh" with no low-mid body and no top. It cannot be the roar
(needs flat energy from ~50 Hz) or the hiss (needs a hard wall at 15 kHz). **That single mismatch
is likely most of why the existing tuning static sounds synthetic.**

De-emphasis corners, verified: **2122 Hz** (75 µs, US) / **3183 Hz** (50 µs, EU).

### The other landmarks, ranked for the illusion

0. **Broadcast processing on the station audio** — Optimod-style: dense multiband compression, hard
   clipper, **15 kHz brickwall**. Constant, not a tuning event. If station audio is clean
   full-bandwidth music, no amount of noise will sell it. *Cymbals extending past 15 kHz read
   instantly as "not radio".*
1. **The quieting knee, and the fact that the audio never fades.** ~20 dB of S/N gained across
   ~4.6 dB of RF — roughly a 4:1 slope — then it flattens. Critically: **the program sits at full
   level the whole time; only the noise moves.** Crossfading music up out of the noise is the #1
   fake tell. FM signals do not fade out, they crash into the noise.
2. **The click / sputter zone.** Below threshold the noise phasor exceeds the carrier and produces
   a sudden phase step — an audible click. Rate rises from ~1/sec to hundreds/sec across ~3–5 dB,
   merging into a rattle. Each click is a bipolar impulse a few hundred µs long.
3. **Stereo collapse — and the hiss stepping UP when the pilot locks.** Stereo needs **~20 dB** more
   signal than mono (the L−R subcarrier sits at 23–53 kHz where triangular noise is far worse).
   Real receivers click on the switch; there are patents specifically about suppressing it. Marginal
   signals chatter between mono and stereo.
4. **Capture.** 1.0 dB excellent, 1.5 dB typical. Abrupt whole-program swapping, **plus the loser
   bleeding through the winner's quiet moments** — program-dependent ducking, not a crossfade.
5. **Multipath.** Swishy, phasey, gritty — and distinguishable from noise because it is *correlated
   with the program*: it grits up on loud bright passages and vanishes in gaps. Cheap fake: a
   sub-ms comb (0.5–50 µs) with the delay rotating at 0–20 Hz, plus a mono-sum bypass so it "goes
   away in mono" like the real thing.
6. **Muting and the arrival thump.**

**Correction to a premise we were carrying:** the swept heterodyne whistle is an **AM/shortwave**
signature, not FM. On FM the adjacent carrier is 200 kHz away — far above audio — and there is no
BFO. Putting swept whistles on an FM sweep is a giveaway to anyone who has used a real tuner. What
FM *does* have is fixed-frequency **birdies** from internal oscillators, which sit at fixed dial
spots and do not sweep.

### The architecture worth stealing

**effetune's FM Radio Simulator** (MIT, github.com/Frieve-A/effetune) does not model any of the
phenomena above. It adds complex white noise into the **I/Q baseband before the discriminator**
and then just demodulates. The flat-vs-triangular spectra, the clicks, the threshold knee and
capture **all fall out of the physics for free.**

That is the real answer to "it sounds synthetic". Minimum complex rate ~200 kHz (±75 kHz deviation
plus the 19/38/57 kHz MPX); 256–384 kHz is comfortable — only 8× audio rate for a single voice,
very affordable in the native mixer. It does *not* model capture (linear phase discriminator), so
that would still be added.

Its parameter set, worth stealing wholesale: Emphasis (50/75 µs), Processing (0..+18 dB), **Signal
(0..70 dBµV)**, **Tuning (±200 kHz)**, IF Band (80..240 kHz), Multipath, Path Delay, Fading, Stereo
(Auto/Stereo/Mono).

### Reference recordings — real receivers, CC-licensed

Freesound has thrift-shop analogue radios being swept, which is exactly the target:

- kwahmah_02 — "fm tuning 1.wav", freesound.org/people/kwahmah_02/sounds/316811/ (CC-BY, old Sony,
  full 87–108 sweep) — and the whole AM/FM/SW pack, /packs/17740/
- jordanielmills /sounds/167191/ · skymary /sounds/372220/ · MrAuralization /sounds/269702/
- YleArkisto /s/350265/ — 1967 receiver, Nagra, analogue tape. Period-correct.

**Caveat:** YouTube bandscans are mostly recorded on TEF6686/SDR front-ends, which *step* 100–200
kHz and have soft mute and continuous blend. They do not contain the between-station sweep. Use
them for station density; use the Freesound thrift-radio recordings for the sweep itself.

## Why he cannot converge, beyond the bugs

He is judging a **movement** using **static sliders**, so every comparison is against a memory of a
sweep from a minute ago. Human timbre memory is a few seconds. Adjusting also takes his hand off
the dial — the band holds keyboard focus, and touching a slider steals it.

Plus a set of workflow faults found by inspection:

- **`tilt` is mislabelled.** It is called "Brightness" and it is a **19.3 dB volume knob** — violet
  noise keeps energy above the high-cut and white does not, so crossfading them past a 2.7 kHz
  lowpass changes loudness enormously. The archetypal untunable pair.
- **`bw` and `spacing` are the same parameter** — only the ratio is audible (`bw 0.85/spacing 2.4`
  and `bw 1.70/spacing 4.8` are identical to six decimals). And `spacing` lives in a different card
  and silently calls `respace()`, so moving it invalidates the tuning of `bw`, `step`, `sweep` and
  `glide` at once.
- **MHz is a fake unit here.** The band is arbitrary and stations are placed procedurally. The only
  meaningful unit of dial distance is *fraction of the gap to the next station*. Five parameters
  are quoted in a unit with no meaning.
- **Presets are path-dependent** — `cinematic` carries `glide`/`sweep`, the others don't, so
  clicking cinematic then cheap keeps cinematic's glide.
- **Every preset click silently respaces the stations**, destroying any frequency typed by hand —
  including the close pair set up to hear flutter.
- **"Realistic receiver" is byte-identical to the defaults**, so clicking it appears to do nothing.
  One click teaches him presets are useless.
- **`Clip length` is a silent no-op** after files are loaded.
- **No persistence at all** — no localStorage anywhere. Every reload loses his stations *and* all 21
  values.
- **The transition is 3.1 key presses wide.** 49% of the gap is indistinguishable from dead centre.
  Even with the SNR fixed, he is still hearing cuts, just smaller ones.

## The bench may be the wrong tool

This repo already contains the argument, written by this project about this project, at the top of
`CMM_Decoder/tune_clicks.c`:

> *"It calls cmm_mix_render_click in the SHIPPED DLL. Not a port, not a model of it, not a
> JavaScript twin: the exact code the game runs. Every time this project has built a second
> implementation of 'the same' thing — an offline judge, a reference model, a spectrum analyser —
> the two have drifted and the copy has been the one telling the lies. So there is no copy."*

Of the 21 parameters, **13 transfer, 5 do not, 3 do not apply.** The five that do not are the ones
that decide whether it sounds like a radio:

- **`noise`** — an absolute gain in the bench against a `random()*0.5` buffer at master 0.7 with no
  clip guard; in game it sits against R128-normalised material under a slewed master with a hard
  clip. A ratio between two things whose absolute levels differ in both worlds.
- **`tilt`** — crossfades two buffers of *different RMS*.
- **`deem`, `hcMin`, `hcMax`** — the bench uses `BiquadFilterNode` (12 dB/oct, Q 0.707). **There is
  not one biquad in this codebase.** Every filter in `cmm_out.c`/`cmm_mix.c` is a one-pole. Settle
  "1400" against a biquad, implement a one-pole, and the weak station arrives audibly brighter than
  what was signed off.

Plus an invisible sixth category: **five `setTargetAtTime` time constants** (0.012 on shares, 0.05
on width, 0.03 on high-cut and mute, 0.05 on de-emphasis). Those *are* the sound of a sweep — they
are what stops it zippering — and **none is in the export.**

## Recommendation

### Do the emitter squelch FIRST. It ships in a day and it settles the dial's hardest numbers.

This is the sharpest finding of the four. The emitter path **already has everything**:

- `cmm_emitter_gains` (`cmm_out.c:359-392`) already computes `bright[c]` — *"1 = near & clear, →0 =
  far or wall-occluded"*. **That is the signal-quality scalar `S`.**
- The coloration at `cmm_out.c:761-801` already adds `colNoiseAmp` **before** the voicing band-pass,
  with the comment *"Static bed first, so the hiss shares the voicing + drive"* — which is the dial
  design's own principle #1, independently arrived at.

Only two things are missing, and they are exactly the TODO's complaint:

1. `colStatic` is a **constant from config**, not a function of `bright`. Same hiss at 5 m as at
   55 m. A radio barely reaching you should hiss *more*.
2. **There is no squelch.** `att` fades smoothly to zero at `emMaxDist`. "Some type of cut off" is
   the soft-mute stage, and it is simply absent.

~40 lines in `cmm_out.c` plus two config floats. No structural change. The knobs already hot-reload
at 4 Hz through `SetEmitterColor`, and `cmm field <knob> <n>` is an established idiom.

**And it makes the shared model audible in game, against real material, through the real gain
chain** — which is the cheapest possible way to settle the five numbers that do not transfer. The
dial then inherits settled values instead of guessed ones.

### Then: one model, two call sites

`cmm_radio_receive(state, params, S, in) → out`, called from `cmm_out.c` per emitter slot with
`S = bright[c]`, and from `cmm_mix.c` on a radio sub-bus with `S` from the dial. Two things must
**not** be shared: slew rates (emitter `S` is slow and smooth; dial `S` moves at 5 stations/s), and
capture/flutter, which are dial-only — two world radios at different distances do not fight at a
limiter, they genuinely sum.

### Then: `tune_dial.c`, on the `tune_clicks.c` pattern

A native harness that sweeps a real file through the real shipped code at the real 48 kHz bus rate,
arrow keys on the parameters, sound in ~50 ms. A day of work, and it gets the bench's fast-iteration
property *without a second implementation*. Then `cmm dial <knob> <n>` live in game for the last
mile.

### The bench keeps a job — but a smaller one

Fix the three faults, then use it for **shape** and for the two open UX questions in
`plan-fm-dial.md` (does the dial rest between stations or spring to the nearest; continuous vs
detented). Those are feel judgements that do not depend on filter topology.

And if it is kept, the one change worth making above all others:

> **The tool drives the sweep on a loop over a fixed route, and one key swaps the whole parameter
> set A↔B mid-loop without restarting the movement.**

Same point in the same movement, same music, one variable. That turns search into hill-climbing.
Not the macros — with the loop and all 21 sliders he *would* converge; with macros and no loop he
would not.

Second-best value-per-line: **three lamps under the band — DULL FIRST · MONO FIRST · QUIET LAST.**
The logic already exists in `selftest.js:123-135`; lift it live. A dark lamp means the model
*structurally cannot* sound right, and he sees it without having to hear it.

### The six controls that should replace the 21

| control | unit | default | what it does |
|---|---|---|---|
| Lock width | fraction of the gap | 0.28 | how much of the way to the next station still counts as tuned |
| Edge sharpness | 0–1 | 0.35 | lazy analogue approach vs. snap |
| Signal drop | dB | −14 | how far the music sinks when detuned — **the missing one** |
| Static level | dB | +2 | how loud the hiss gets. **Must be able to exceed the music** |
| Muffling | 0–1 | 0.65 | how dull a weak station goes; drives stereo collapse from the same curve |
| Nudge per press | fraction of the gap | 0.10 | ~10 presses across a gap instead of 3 |

Delete outright: `tilt`, `flutRate`, `deem`. Merge `hcMin`+`hcMax`+`order`, `noise`+`noiseCurve`,
`bw`+`spacing`+`cap`, `muteKnee`+`muteFloor`+`flut`. Lock `stFull`/`stNone` at the physical ~20 dB
relationship. Move `vol`, `clip`, `step`, `sweep`, `glide` out of the tuning stack entirely.

**One disagreement between agents, resolved:** the model critic wants a "Signal drop" so the music
can sink below the noise; the physics research says real FM keeps the program at full level and
only the noise moves. Both goals are met by letting the **noise rise** rather than the music fall —
so keep the music roughly flat through the useful range, then let it crash (FM does not fade, it
crashes), and give Static level enough headroom (+12 dB) to invert the SNR on its own.

## The game side, if and when the dial is built

**The dial belongs to the personal radio only.** A dial is a property of a receiver.
`EmitterAudioLayer._posPins` encodes the opposite model deliberately — whoever placed the speaker
chose the station and it must not retune. Emitters inherit the **receiver stage**, never the dial.

**Frequency assignment** — do NOT use `hash % n`; that moves every station when one is added. Use
the existing `RadioStation.Seed` (FNV-1a over the lowercased name, `RadioStationCatalog.cs:604`),
already documented as *"adding a different station doesn't reshuffle this one"*:

```
band 88.0–108.0, 0.2 MHz channels → 100 slots   (real FM spacing; also makes the readout look right)
slot0(name) = Seed(name) % 100
collision   → probe slot0 + k*37 mod 100        (37 coprime to 100)
```

Built by walking the catalog in name order (`RebuildCatalog` already sorts). Pure, deterministic,
**no persistence** — a persisted map has to be reconciled against a changing folder set, which is a
bug farm. Author override: a leading frequency token in the folder name (`98.5 Country`), one
`float.TryParse` in `AddStation`, declared frequencies claim first. Handle >100 stations explicitly
(drop to 0.1 spacing, or log the unreachable ones) rather than failing silently.

Free bonus: because the broadcast clock already syncs everyone, a personal radio tuned to a nearby
speaker's frequency plays it **in sync**. That falls out of the assignment, not the dial.

**Structural work, in order** — this is the part that takes weeks:

1. Held-key sweep. `HandleMusicKeybinds` is a `GetKeyDown` else-if chain with no concept of hold.
2. **Off stops being a dial position.** Needs a separate power key. *User-visible muscle-memory
   change — needs Chris's decision before any code.*
3. `_dialHz` replaces `_tunedName` as the source of truth; `_tunedName` becomes a derived readback.
4. **The music bus must carry more than one radio voice.** `cmm_mix_add` (`cmm_mix.c:1046`) drives
   *every* non-emitter voice to zero — two stations cannot sound at once **by construction**. Needs
   an `isRadio` class exempt from that loop, mirroring `isEmitter`.
5. A radio sub-bus, so the receiver stage applies once per receiver rather than per station.
6. Dial evaluation **in the producer, per block** — pushed from the 4 Hz tick it would step in
   250 ms chunks and zipper.
7. The decoder set: `_startPending`/`_doneReady`/`_startGen` all assume exactly one start in flight.
8. The loudness gain-gate holds a start up to 6 s waiting for a measurement. You cannot hold a
   sweep.
9. Retire the tuning static keepalive — the dial's own noise replaces it.
10. Rate-limit detent clicks to channel-grid crossings, not nudges.

**Cost in the mixer: the DSP is free; the decoders are the budget.** The receiver stage is ~25
flops/frame against the ~400 the emitter layer already spends — about a tenth of one reverb tick.
But continuous tuning needs ≥2 decoders running, and one extra Opus/Vorbis decode is **20–60× the
entire dial DSP.** Warm exactly two neighbours; that is the design constraint. And keep the
`powf` per block — per sample it instantly becomes the most expensive thing in the mixer.
