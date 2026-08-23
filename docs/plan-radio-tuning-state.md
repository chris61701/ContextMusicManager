# Radio tuning: where it actually stands

2026-08-23. Read this before touching the tuning again. The spec is `plan-radio-tuning.md`; the
research is `plan-fm-dial-consult.md`. This is the *state*.

## What is deployed

**SETTLED AND SHIPPED, 2026-08-23.** ABI 47, schema 10, built and deployed to all four game
installs. Everything below is now the compiled default — the bench and the game agree for the
first time since J1.

In-game listening test still pending. What to check, in order: that a station change at real game
volume still reads as tuning (the bench is 15 s of headphones, not a firefight); that the
mangle-then-repair is not too aggressive on bass-heavy tracks; and that nothing pops at the lock
instant (there is a theoretical −29 dB one-sample step there where the held noise floor stops
applying — it measured below the music and never surfaced in the bench, but game speakers at
volume are a different test).

## The one that mattered

*"It still sounds like audio at its full fidelity slowly fading in with things layered on top of
it."* Four rounds were spent adding things — bright hiss, treble windows, noise holds — on top of a
programme that measurement showed was **essentially untouched**: −3 dB at 3.75 kHz, full bass, full
dynamics, full stereo. The answer was never additive. The music itself has to be wrecked and then
repaired: band-limited to 400 Hz – 2.6 kHz with two poles at each end, driven, squashed 8:1, made
up +9 dB, and released as the station locks. Verdict on that, first try, at full depth: *"Full is
fine."*

Three details that make it work rather than sound like an EQ:

- **The low cut comes BEFORE the saturator.** With bass still in it a soft clipper makes mush;
  with the bass gone the same drive becomes midrange grit. This is also why `drive` at 0.15 had
  been inaudible for the entire project — it was working on a full-range signal.
- **Two poles at each end, not one.** A one-pole is a 6 dB/oct tilt, i.e. a tone control. Two is
  an edge, i.e. a bandwidth. Every previous EQ round used one pole and was correctly heard as EQ.
- **The makeup gain is not optional.** Band-limiting costs real level, and a station that gets
  quieter as it arrives is the fade that four rounds were spent killing. Measured flat to within
  half a dB of the locked level.

## Settled by ear, and NOT to be relitigated

| | verdict |
|---|---|
| **The fade OUT** | *"perfect in all situations"*. Do not touch it. Every later experiment left it alone. |
| **`floorDb` = 5 dB** (J1) | THE key finding. The station must barely drop; the NOISE does all the moving. Pulling the station down 26 dB IS a fade whatever is layered over it, and that was the cause of *"it sounds like the music fades out and the static fades out, like they are two different things"* for four rounds. |
| **Static character** (C1) | wander 0.55, flutter 0.30, clicks 0.18 @ 180/s, de-emphasis 2122 Hz. |
| **Flutter must be band-limited noise, not oscillators** | two fixed sines gave it a *rate* — *"the frequency at which things flutter seems static and not random"*. |
| **Noise holds into the arrival + a residual floor** (N3) | tied to signal alone the hiss cleared out while the station was still opening, leaving clean music fading in with nothing over it. |
| **~5.5 s total** | 1.2 s out / 1.5 s search / 2.8 s in. 14 s was far too long; 3 s was not tried in game. |

## Rejected, with the reason

- **14.4 s total.** Came from "double is fine" applied to a *listening demo* and then shipped as the
  game default without anyone checking it as a gameplay timing.
- **Stereo arriving immediately.** It was latched on signal strength, which saturates near maximum
  by the time the station is merely audible — so it could never fire late whatever threshold it was
  given. Now keyed to ramp position. Stereo should be the LAST thing to come good.
- **The decoder clunk at the stereo latch.** Real ones do it; not wanted here.
- **Drive at ×36.** A fuzzbox. And its level compensation was unrelated to the drive, so mid-
  transition it put 7.3× in and 0.69× out — the station came back **14 dB hot**, measured as a
  +9.5 dB overshoot. That was *"the volume ramps way up before settling"*. Now `tanh(x·d)/d`, ×7.
- **Noise at 2.5× full scale.** Clipping, and harsh.

## Rejected in the final rounds

- **Speeding up the treble sweep.** The bench's arrival ran its 2.6 octaves at 1.47 oct/s against
  the approved departure's 3.43 and the shipped code's 9.15, which looked like a smoking gun. A
  four-step ladder said otherwise: *"1-slowest-current is fine."* The rate was never the problem.
- **Carrier-present bright hiss.** Physically real (demod noise is triangular, de-emphasis cancels
  the +6 dB/oct exactly, so a carrier turns the dark roar into a bright sizzle) and the missing
  masker above 6 kHz, where the dark bed holds only 1.8% of its power. Three of four agents made it
  their top pick. Verdict: *"It sounds completely wrong. That's not the right direction."* Kept in
  the bench behind `hfLvl` (arg 36, default 0) in case it earns a place later.
- **The decoder clunk.** A one-sample 0.34 DC step into the mid channel at the stereo latch,
  measured at −14.6 dBFS — the largest discontinuity anywhere in the transition, larger than any
  musical transient in either track. Real decoders do it. Not wanted, and never ported.

## Things that were tried and made it worse

Recorded so they are not tried again without knowing why:

- **Band-limiting the SUM** (music + static together). It shaped the static too. Chris: *"You're
  targeting the frequency of the static. We want to target the frequency of the music coming in."*
- **Moving that filter onto the music path alone.** Correct instruction, but the shared filter had
  also been the thing darkening the static, so removing it made the static harsh — *"The static has
  become way too harsh. What have you done?"*
- **A slow treble sweep on the way in.** The way in and the way out shared one window, and because
  the arrival is more than twice as long, the same curve stretched into an audible **lowpass
  sweep** — which is what *"it sounds like EQ"* was.
- **Stacking all of the above.** By the U round the chain was four unapproved changes deep and the
  only possible verdict was *"worse than ever"*.

## The two process rules

**Checksum every render set before sending it.** Three times a set went out where the varied
parameter never reached the audio — a silently failed build, an `argc` guard never satisfied, and a
direction flag consumed before it was set. Byte-identical files, and the listening rounds spent on
them were worse than wasted because the feedback was misleading.

**Change one thing.** Revert to the last approved version, change ONE parameter, get a verdict.
The alternative is what happened here.

## The tool

`tools/CmmTuneRender/` — moved out of the scratch folder so it survives. It renders a full station
change over the user's own mp3s using the same maths as the receiver, in about ten seconds. See its
README for the argument order and the last approved settings.

Test material used throughout: `J:\7D2D Custom Music\radio\Prism Radio\星降る夜に (1).mp3` at 20 s
leaving, `Mazirene FM\Butterfly Kisses - 2023-05-29.mp3` at 30 s arriving. Two *different* tracks
matters — with the same song both sides you cannot tell a handover from a dropout.
