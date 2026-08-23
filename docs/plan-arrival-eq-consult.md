# The arriving station: what four agents said

2026-08-23. Consultation on the one complaint that has survived every round: *"When it fades out,
it's perfect. When it fades in, it doesn't sound right — the frequencies sound way too clean."*
The brief they answered is `brief-arrival-eq.md`. Lenses: RF/receiver engineering, audio DSP,
psychoacoustics, practical sound design.

## Two bugs in the bench, which invalidate two earlier listening rounds

Found by the DSP agent, by measuring rather than reading. Both are in `tunerender.c` (and were in
`sweepmp3.c`, which is byte-identical to it). **A checksum does not catch either** — the files
differ, they are just wrong.

1. **`musicEq=1` never moved the filter onto the music.** The music block writes `lp0`/`lp1`, and
   the sum block below reads and rewrites *the same two state variables* — and the sum block is not
   gated on `musicEq`. So that round ran the music through a pole and then ran the sum through the
   same pole's state again. **The round where the EQ "moved onto the music path" tested nothing of
   the kind.** Fix: give the music block its own `mlp0`/`mlp1` and gate the sum block on
   `musicEq < 0.5f`.

2. **The static's own top cap came back +3.6 dB hot.** `nzs = nz3 * 1.7f`. In V1 the noise already
   passes three poles — 2122, 2122, and the shared hi-cut parked at 2474 Hz across the search
   plateau. Enabling `nHiHz` reproduces those same three poles and then multiplies by 1.7. The
   correct makeup to reproduce the approved bed is **×1.00**. So *"the static has become way too
   harsh, what have you done?"* was, at least in large part, **a 4.6 dB level bug** — not the
   concept failing.

Neither bug is in the shipped `cmm_mix.c`. Both are in the bench, i.e. in the thing that generated
every verdict from the low-cut round onwards.

## The measurement that explains "it sounds like EQ"

Same filter, same 2.600-octave span (the floor is **2474 Hz**, not 1800 — `dull` 0.85 gives
`15000·(1800/15000)^0.85`), three different rates:

| | window | duration | rate | verdict |
|---|---|---|---|---|
| Shipped `cmm_mix.c` | `t=(34−snr)/26`, Δu 0.102 | — | **9.15 oct/s** | never complained about |
| Bench **departure** | Δu 0.632 over 1.2 s | 0.758 s | **3.43 oct/s** | *"perfect"* |
| Bench **arrival** | Δu 0.632 over 2.8 s | 1.770 s | **1.47 oct/s** | fails |

The bench's `stagger` branch widened the window from the shipped 0.102 to 0.632 — a **6.2×
slowdown** — and only the arrival is long enough for that to matter. So the audible filter sweep is
something the bench introduced, not something the design does. The threshold between "a receiver
did something" and "someone turned a knob" is bracketed, on this material, **between 1.47 and
3.43 oct/s**.

Also measured: from u≈0.55 to lock — the final ~1.25 s, exactly where the complaint lives — level,
drive and width have all finished. **The only thing still moving is that corner.** A stage moving
alongside others is attributed to the event; a stage moving alone is attributed to an agent.

No zipper risk anywhere in this family: dk/sample is 7e−6 on the current sweep, and a one-pole's
output is continuous under a step change in k regardless. The glide is not merely clean, it is
*pristine*, which is itself the tell — a perfect monotonic exponential is the signature of an
automated parameter.

## Where all four agree

**The programme's audio bandwidth does not change with signal strength. At all.** Every stage that
sets it is fixed by construction: TX deviation 30 Hz–15 kHz, the IF ceramic filter (140/180/230/280
kHz, soldered in), the discriminator, de-emphasis (an RC: 75 µs = 2122 Hz), the 15 kHz LPF ahead of
the MPX decoder. A station 3 dB over threshold and the same station 60 dB over threshold deliver
**identical** programme bandwidth.

Hi-cut is real but it is a *deliberate marketing artefact* added since ~1980, it is **disabled by
default** in AN332, and it is the smallest of the cues by some distance.

So the mental model behind the last four rounds — "band-limit the music while it is weak" — is not
what a receiver does, which is a large part of why every attempt to implement it read as EQ.

## What actually makes a weak station sound weak, ranked

1. **Noise that is spectrally co-located with the programme.** Nothing else is close.
2. **Mono collapse** — the L−R subchannel sits at 23–53 kHz where the f² noise is enormous;
   ~20–23 dB S/N penalty (matches the 20.0–22.9 dB measured across eight tuners already cited in
   the code).
3. **Distortion** — incomplete limiting, AFC error, channel-filter group delay.
4. **Broadcast dynamics** — every station is Optimod'd; dense and flat.
5. **Hi-cut.** Last. Smallest. The one four rounds were spent on.

## The missing component: carrier-present hiss is BRIGHT

Three of the four arrived at this independently, and it is the physical explanation for the exact
words *"too clean"*.

FM demodulator noise is **triangular** — power ∝ f², i.e. +6 dB/oct. De-emphasis cancels that
+6 dB/oct **exactly**. So:

- **No carrier** → noise is flat → after de-emphasis, a dark **roar**. *This is what the model has,
  and it is the approved between-stations bed.*
- **Carrier present** → noise is triangular → after de-emphasis, **flat in density from 2.1 kHz to
  the 15 kHz wall**: a bright, thin **hiss**. *The model does not have this at all.*

Measured power distribution of the current dark bed:

| | < 2 kHz | 2–6 kHz | 6–15 kHz |
|---|---|---|---|
| dark bed (2 poles @ 2122) | **79.1 %** | 18.8 % | **1.8 %** |
| bright/triangular hiss | 3.4 % | 23.6 % | **45.0 %** |

The bed is −18.6 dB at 6 kHz and −31.3 dB at 15 kHz. **It has no energy where "clean" lives.** You
cannot veil a bright thing with a dark veil. Per-ERB at u = 0.8 the noise is 25–30 dB below the
music above 4 kHz — there is effectively no masker at all across the last second.

Consequence: in a real set, when the hi-cut releases the sound gets brighter **and hissier in the
same instant**. Here it brightens onto silence. That is what converts the percept from "a receiver
acquired" to "a filter opened".

It also retrospectively vindicates failure 1: *"you're targeting the frequency of the static"* was
aimed at a behaviour that is physically correct — a carrier appearing really does turn the roar into
a sizzle — but it was implemented as a shared filter, 6× too slow, with a 4.6 dB level bug.

## Why leaving works and arriving doesn't, with identical processing

Four independent reasons, all pointing the same way, and none of them is "the numbers are wrong":

1. **Streaming build-up.** Segregation takes 1–4 s to form. On the departure the music is an
   established object and the noise arrives *into* it, so it is heard as happening *to* it. On the
   arrival the **noise** is the established stream (1.5 s of search plus a 1.2 s departure) and the
   music is the newcomer — it enters as a second *figure*, not as ground being uncovered.
2. **Onsets form streams; offsets don't.** The fuse-or-split decision is made at onset. A clean,
   full-bandwidth, full-width onset against a noise bed *will* be segregated. That is not taste, it
   is what the mechanism does — and it is precisely what *"two different things"* means.
3. **Masking is asymmetric in time.** Forward masking 100–200 ms; backward masking 5–20 ms. The
   departure is continuously covered; the arrival is not. **Holding the noise later (item 4 of the
   approved list) already extracted everything temporal masking has to give.** What is left has to
   be *simultaneous* masking — i.e. frequency. Which is the argument for the bright hiss.
4. **2.8 s vs 1.2 s is 2.33×.** Every gesture derived from `u` runs at 43 % of the departure's rate.

Mixer's one-line version: **you don't fade a thing in under noise — you cut it in and hide the cut.
Departures may be gestures. Arrivals must be events.**

## The ranked plan

**1 — Narrow the arrival's treble window to departure-rate parity.** `tInA 0.28 → 0.60`,
`tInB 0.912 → 0.87`. Zero code, two numbers, both arrival-only. 1.47 → **3.43 oct/s**, the exact
rate already approved by ear on the other direction, and it lands the motion inside the noise's
steepest 13.8 dB of decay instead of running naked for its second half. Endpoints unchanged, so the
static is untouched and the departure branch is not even reachable. Follow-ups if needed:
`0.60/0.80` (4.6 oct/s), `0.62/0.72` (9.4 oct/s = the shipped rate).

**2 — Add carrier-present HF hiss, arrival only.** `white − lp(2122)` is a one-pole highpass at the
de-emphasis corner for free, reusing existing state; two poles at 15 kHz give the MPX wall. Level
via a hump `4·o·(1−o)` across the treble window so it is zero at both ends and non-monotonic.
Measured ladder: 0.12 → +2.5 dB of 6–15 kHz masking; **0.25 → +8.9 dB masking for +1.2 dB broadband**;
0.45 → +14.0 dB for +3.1 dB. Start 0.25. Put it in the **side** channel (real high-blend exists
*because* side hiss is 20 dB worse) — then the stereo latch finally acquires an audible consequence
and the image opening, the treble opening and the hiss appearing become **one event**.

**3 — Event structure instead of one monotonic knee.** Real acquisition is non-monotonic: partial
capture (honky, distorted, mono) → falls back → captures harder → brief overshoot → pilot locks.
Concretely: 2–3 dropouts of 20–60 ms across the last 40 %; one false capture of 250–400 ms at
~0.75 then ~200 ms back to noise; a 20–60 ms noise burst placed *on* the lock instant to mask the
seam; a squelch tail of 30–80 ms. Highest ceiling on the list, biggest change.

**4 — Programme-driven noise.** `nAmp *= 0.75 + 0.5·env`, env = 10 ms/150 ms follower on the
programme. High deviation really does degrade effective C/N, so loud passages on a weak station are
genuinely noisier. Gives the noise the music's onsets → common fate. Touches the programme not at
all.

**5 — Give `drive` its own late window.** Measured, `d` is 1.064 by u = 0.60 and 1.011 by u = 0.70 —
gone before the treble window even starts. A non-EQ way to make the top read as wrong.

**6 — Event-shaped treble instead of any ramp.** Fast attack / slow release (AN332's own 3 ms /
3.3 s, a 230:1 asymmetry) over a deliberately jittery SNR estimate, with multipath-style dips every
~450 ms shrinking toward lock. A corner that jitters can never read as a hand on a knob. This is
where stage 7 should end up eventually; not while it is still the stage that has lost four rounds.

## What NOT to do

- **No low-cut on the programme.** Sub-2 kHz is the *only* band where the music and the noise
  currently overlap; stripping it removes the last spectral evidence that they are one thing. There
  is also no mechanism in any FM receiver that removes bass with signal strength — de-emphasis is a
  fixed RC. (The sound-design lens argued the opposite, on small-speaker grounds, and was outvoted
  3–1 on the masking argument. Recorded because it is a real craft technique that is right for
  *speech* and for a permanently-radio-ised source — just not for an arrival under a dark masker.)
- **No monotonic corner ramp on the arrival at any curve**, unless it is fast enough to be an event.
- **Never below ~2 kHz.** AN332's most aggressive setting is 2 kHz. The 2474 Hz floor is already at
  the limit — keep it as the *bottom*, not the resting state.
- **Don't touch** `floorDb`, the knee, the static's parameters, `nHold`/`nFloor`, the de-emphasis
  corner, or the departure branch.
- **Bit-crush / sample-rate reduction: no.** Reads as *digital*, the opposite of an FM set.
- **Ring mod against a pilot residue: no.** 19 kHz products fold at 48 kHz, and FM has *fixed*
  birdies, not swept whistles.
- **Don't stack 1 and 2.** They are independent — one is the music's filter, the other is the noise.

## Corrections to the brief, for next time

- Knee is **0.45** and `kneeW` **0.22** in `cmm_mix.c`. 0.52 is `lockU`. The brief said 0.52/sharp.
- The hi-cut floor in practice is **2474 Hz**, not `hiFloor` 1800 — `dull` 0.85 is an exponent.
- The stereo latch (`lockU`/`lockMs`/hysteresis) exists **only in the bench**. Deployed `8b80aeb`
  opens width on `w = (snr−14)/13`, saturating at the knee. Expected — the latch was settled after
  that commit — but it means the deployed build arrives in full stereo, and that must be ported
  along with everything else.

## One strategic question, outside this round

The arrival's *destination* is a 15 kHz-flat, full-width, unlimited music file. The identity
"radio" is carried entirely by the transition, which then has to contradict itself the moment it
finishes. A permanent ~13–14 kHz ceiling plus a gentle limiter on the locked station would make the
endpoint coherent with the journey and reduce how hard the arrival has to work. Worth deciding
after the arrival is settled.
