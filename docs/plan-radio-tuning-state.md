# Radio tuning: where it actually stands

2026-08-23. Read this before touching the tuning again. The spec is `plan-radio-tuning.md`; the
research is `plan-fm-dial-consult.md`. This is the *state*.

## What is deployed

`8b80aeb` — ABI 46, schema 9. That is the **J1** settings, and it is NOT the latest thing Chris has
heard. Everything after J1 was settled in the offline renderer and has not been ported.

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

## Open, and where it stopped

Chris was sent **V1** and **V2** (`scratchpad/v20/`, and the scratchpad is temporary):

- **V1** = N3 reproduced exactly, with every later change switched off.
- **V2** = V1 with *only* the stereo lock moved later (0.85 of the ramp) and its clunk removed.

The first question is whether **V1 actually sounds like N3 did**. If it does not, the revert is
wrong and everything downstream of it is guesswork.

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
