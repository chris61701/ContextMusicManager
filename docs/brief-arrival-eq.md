# Brief: the EQ of a station ARRIVING

You are being consulted on one narrow question. Read all of it before answering.

## The product

A 7 Days to Die music mod with an in-game FM radio. Pressing a key seeks to the next station.
The station change is rendered as a real receiver would sound it: the old station leaves, there is
a stretch of interstation noise while the new station's files load, then the new station arrives.
There is no dial and no frequency — only "leave", "search", "arrive". Timing is roughly
1.2 s out / 1.5 s search / 2.8 s in.

The whole thing is driven by ONE control signal per voice, `u` in 0..1: 1.0 = locked on,
0.0 = nothing there. On a station change it ramps 1 -> 0 -> 1. Everything else is derived from it.

## What is already settled by ear and MUST NOT be changed

These were each arrived at over multiple listening rounds with the actual user. Do not propose
undoing them; propose things that live alongside them.

1. **The fade OUT is perfect.** Verbatim: *"When it fades out, it's perfect."* Whatever you propose
   must be able to apply to the arrival WITHOUT altering the departure. Both directions currently
   share filter code and that has already caused one failure (below).
2. **The programme level barely moves.** `floorDb = 5`, i.e. the music is attenuated at most 5 dB
   at the deepest point. This is THE key finding of the whole effort. Pulling the station down
   26 dB *is* a fade whatever noise you layer over it, and doing so produced four consecutive
   rounds of *"it sounds like the music fades out and the static fades out, like they are two
   different things."* Real FM holds the programme at full level and lets the NOISE do all the
   moving. Do not propose gain-based solutions to the arrival.
3. **The static's character is settled.** wander 0.55, flutter 0.30 (band-limited noise, never
   oscillators), clicks 0.18 @ 180/s, de-emphasis 2122 Hz, two poles. The user has approved this
   sound. If your proposal changes how the STATIC sounds, that is a regression.
4. **Noise outlasts the tuning.** On the way in the noise has its own later window plus a residual
   floor that never reaches zero, because hitting absolute zero reads as "sound effect ended".
5. **Stereo is the last thing to come good**, latched on ramp position (not SNR — SNR saturates
   long before the station is merely audible).

## The complaint you are being asked to solve

On the ARRIVAL only, the music comes in sounding wrong. The user's own words, in order across
several rounds:

- *"Fade out is fine in all situations. Fade in, the static goes away too quickly before the
  tuning is complete. There's a period where it sounds like there is no static as it is fading in."*
  (this one was addressed — noise now holds later, item 4 above)
- *"It doesn't sound right. The frequencies sound way too clean on fade in."*
- *"Nothing is right. I'm talking about frequencies in terms of equalization. Is that what you
  attempted? When it fades out, it's perfect. When it fades in, it doesn't sound right."*
- *"You're targeting the frequency of the static. We want to target the frequency of the MUSIC
  coming in."*

So: as the station arrives, the music is too CLEAN, too full-bandwidth, too hi-fi, too soon. It
reads as a music file being faded up behind a noise bed, not as a broadcast being acquired.

## Three attempts that made it worse, and why

Do not re-propose these without knowing what went wrong.

1. **Band-limiting the SUM (music + noise together)** with a signal-dependent lowpass + highpass.
   Wrong because it shaped the static too — a weakening station's hiss has its own fixed colour
   and keeps it. Feedback: *"You're targeting the frequency of the static."*
2. **Moving that same filter onto the music path alone.** The correct instruction, but the shared
   filter had ALSO been the only thing darkening the noise, so removing it from the sum made the
   static bright and harsh. Feedback: *"The static has become way too harsh. What have you done?"*
   (Fix if you go this way: the noise needs its own fixed top cap.)
3. **A slow treble sweep on the way in** — a one-pole lowpass whose corner rises from ~1.8 kHz to
   15 kHz across the arrival. The way in and the way out shared one window, and because the
   arrival is 2.3x longer, the same curve stretched into an audible LOWPASS SWEEP. That is exactly
   what *"it sounds like EQ"* means: it read as a human turning a filter knob, not as a receiver.

The general lesson so far: **a slow monotonic corner-frequency sweep on the arriving programme is
audible AS a sweep and is not what a receiver sounds like.** Something else is needed.

## The actual signal chain, as it exists today

Per sample, per voice. `u` is the 0..1 control. This is the approved "N3/V1" configuration.

```c
/* 1. THE KNEE -- bend the linear ramp into plateau -> event -> plateau */
q   = 1/(1+exp(-(u - knee) * (4/kneeW)));      /* knee 0.52, kneeW very small (sharp) */
snr = -4 + 64*q;                                /* an SNR-like dB scale */

/* 2. PROGRAMME LEVEL -- soft mute only, max 5 dB */
mute  = (5/muteW) * (14 - snr);  clamp [0, 5];  /* muteW 5 */
progG = 10^(-mute/20);

/* 3. NOISE LEVEL -- derived from snr so the two can never disagree */
nLvl = 10^(-snr/20);
if (rising) {                                   /* the arrival holds noise later */
    nf = 1 - (u-0.22)/(0.55+0.42*nHold - 0.22); clamp; smoothstep;
    held = nFloor + (1-nFloor)*nf;              /* nHold 1.0, nFloor 0.06 */
    nLvl = max(nLvl, held);
}
nAmp = nLvl * trim * wander * flutter;          /* trim 0.55 */

/* 4. NOISE COLOUR -- two one-pole lowpasses at 2122 Hz (de-emphasis), noise only */
nz = lp2(white) * nAmp * 2.0;
nz += poisson_clicks();                         /* additive, bipolar, rate 10x per dB */

/* 5. PROGRAMME -- soft mute, then saturation that eases off as the station locks */
pl = musicL * progG;   pr = musicR * progG;
d  = 1 + drive*7*(1-q);                         /* drive 0.15 */
pl = tanh(pl*d)/d;                              /* unity small-signal gain */

/* 6. THE SUM */
l = pl + nz;   r = pr + nz*0.85;                /* noise is worse in the side channel */

/* 7. SHARED HI-CUT -- one-pole, 15 kHz down to hiFloor(1800 Hz), on the SUM.
      THIS IS THE STAGE UNDER DISCUSSION. On the arrival its window is
      u in [tInA=0.28, tInB=0.912]; on the departure [0.28, 0.98]. */
tt = 1 - (u - a)/(b - a);  clamp;  tt *= dull;  /* dull 0.85 */
fc = 15000 * (hiFloor/15000)^tt;
l  = onepole_lp(l, fc);   r = onepole_lp(r, fc);

/* 8. STEREO -- latched on u > lockU (0.52), snapped over lockMs (9 ms), mid/side width */
w = latched_width();
m = (l+r)/2;  s = (l-r)/2*w;   l = m+s;  r = m-s;
```

## What the answer needs to contain

1. **What actually happens to the PROGRAMME's spectrum in a real FM receiver as it acquires a
   station** — over what timescale, driven by what, and in what order relative to the noise
   dropping and the stereo locking. Real chip behaviour and real numbers where you have them
   (Si4700/Si474x AN332, RDA5807, TEA5767, classic discrete sets — whatever you actually know).
   Be specific about whether the effect is on bandwidth, on level, on distortion, or on something
   else entirely, and how big it is in dB and Hz.
2. **Whether "the programme is bandwidth-limited while weak" is even the right model.** It may not
   be. If the real reason a weak station sounds different is something other than EQ, say so and
   say what it is. The user is asking for EQ, but the user is describing a SOUND, and the job is
   to make the sound right.
3. **A concrete implementable proposal** in terms of the chain above: where the stage goes, what
   the filter is, what drives it, what the numbers are. It must be applicable to the arrival
   without changing the departure.
4. **Why your proposal will not read as a filter sweep**, given failure 3 above. This is the
   crux. If your answer is a corner frequency that rises over 2.8 seconds, explain what makes
   yours different from the one that already failed.
5. **What you would NOT do**, and what you think the single highest-value change is if only one
   change can be made. There is a hard process rule: ONE change per listening round, reverting to
   the last approved version each time. So rank your proposals.

Be concrete and be brief. Numbers over prose. If you are unsure of a real-world figure, say that
you are unsure rather than inventing one — a wrong number costs a whole listening round.
