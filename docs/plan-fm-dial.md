# Tuning INTO a frequency — the FM dial

*Design, 2026-08-21. Item 2 of the four audio-engine items in TODO.md.*

## What is wrong today

`Alt+PgUp` / `Alt+PgDn` step Off → station → station → Off. Each step is a **cut**: the
old voice stops, a fixed "searching" static burst covers the gap, the new voice starts.
There is no notion of being *between* two frequencies, so the radio behaves like a
playlist selector wearing a radio's clothes.

The ask: *dialling a station should sweep in rather than cut in — the signal rises out of
the noise as you approach and falls back as you pass.*

## What a real receiver does

Researched rather than guessed, because three of these are counter-intuitive and getting
any of them wrong makes the result sound like a volume fade rather than a tuning.

1. **The hiss is bright, and that is physics.** An FM demodulator produces noise whose
   power rises with the *square* of frequency — the "noise triangle". This is the entire
   reason FM broadcasting needs pre-emphasis. It is why between-station static hisses
   rather than rumbles.
   → Generate **violet noise** (difference successive white samples = exactly +6 dB/octave)
   rather than filtering white noise into an approximation.

2. **Noise is added before de-emphasis, not after.** The receiver chain is
   demodulator → de-emphasis → audio, and the noise is created *in* the demodulator. So
   the same filter that dulls a weak station also dulls its hiss.
   → Mix noise into the signal **upstream** of the tone filters. Get this backwards and a
   weak station sounds like bright hiss over clean music; done right it sounds like
   muffled music under filtered hiss, which is what a radio actually sounds like.

3. **Capture ratio is 3–4 dB, so there is no crossfade.** Once one signal is a few dB
   stronger it suppresses the other *completely* at the limiter. Sweeping between two
   stations gives you one, then noise, then the other.
   → Not a blend. A suppression with a hard-ish knee. And within about a decibel the two
   fight, and real sets **flutter** between them — worth reproducing, it is the
   characteristic sound of the exact midpoint between two transmitters.

4. **Stereo goes before level.** A stereo signal needs roughly **20 dB** more signal than
   mono for the same noise floor, so decoders blend toward mono as signal falls and are
   fully mono well before the station becomes quiet.
   → Blend to mono early. A station should be *mono and dull* while still perfectly
   audible.

5. **Soft mute is the last stage.** Receivers attenuate output on weak signal so a dead
   spot is quiet rather than full-volume hiss.

## The model

```
station audio ─┐
               ├─▶ capture ─▶ [ + violet noise ] ─▶ de-emphasis ─▶ high-cut ─▶ soft mute ─▶ out
      noise ───┘
```

Per station *i* at frequency *fᵢ*, dial at *f*:

```
    xᵢ   = |f − fᵢ| / bandwidth
    rawᵢ = exp(−ln2 · xᵢ^(2·skirt))        generalised Gaussian; skirt = IF filter order
```

Capture, against the strongest station:

```
    downᵢ  = 20·log10(best / rawᵢ)
    shareᵢ = clamp(1 − downᵢ / captureDb, 0, 1)²
```

Signal quality `S = best` then drives everything downstream:

| stage | mapping |
|---|---|
| hiss | `(1 − S)^onset · level` |
| stereo width | `(S − monoBelow) / (stereoAbove − monoBelow)`, clamped |
| high-cut | `hcMin + (hcMax − hcMin)·S^0.6` |
| soft mute | full above `muteBelow`, falling to `floorDb` at zero |

Flutter fires when the top two are within `flutterDb` of each other.

## The bench

`tools/CmmDialLab/index.html` — a standalone page, no build step, no network. Load your own
tracks as stations, drag the dial, and every parameter above is a live slider. It exports
the settled values as the `radio.xml` block.

**This exists because the numbers are a listening judgement.** The occlusion field has two
knobs of pure taste in it, and every attempt to settle those from logs cost days and landed
on the wrong value; they were settled in minutes once they could be heard back to back. The
dial has a dozen such numbers. Settling them in a browser with real music, seconds apart, is
the cheapest correct way to get them — and none of it needs a game launch, a build, or a
deploy.

Do not port anything until the numbers feel right in the lab.

## Then, in the game

- **Native.** The mix runs in `cmm_mix.c` alongside the existing station render slots — the
  managed side has no business touching per-buffer audio. The static generator already
  there (`cmm_mix_set_static_tone`) becomes the noise source, extended to violet.
- **The dial becomes continuous.** `PersonalRadio` currently holds a station *name*; it
  gains a float dial position, with the tuned name derived from whatever the receiver has
  captured. Persisting the dial position rather than the name follows automatically.
- **Keys.** Tap nudges, hold sweeps, and the existing next/prev becomes a *seek* that
  glides across the intervening noise rather than cutting. That keeps the current muscle
  memory while making the transition audible.
- **World emitters get it too**, but the dial is per-listener, not per-emitter — a speaker
  in the world is tuned by whoever placed it, not by the player walking past.
- **Item 3 (emitter squelch) falls out of this for free.** "Static on emitters needs a
  cut-off" is the same soft-mute stage, driven by distance/occlusion instead of dial
  detuning. Build the chain once and drive its `S` from either source.

## Open questions for the bench session

- Is a continuous dial right at all, or should stations stay on detented steps with only
  the *transition* swept? The lab supports both — set the nudge large and the glide long.
- How wide should the band feel? `spacing` decides whether neighbours bleed into each
  other. Wide spacing = clean stations with dead air between; narrow = a crowded band.
- Should the dial rest between stations at all, or spring to the nearest?
