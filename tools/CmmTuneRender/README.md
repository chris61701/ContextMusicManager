# CmmTuneRender — the offline tuning bench

Renders a whole station change (leave → search → arrive) over **real mp3s**, through the same maths
as the receiver stage in `CMM_Decoder/cmm_mix.c`. A round trip is about ten seconds, against a
close-build-restart cycle in game. That is the only reason twenty rounds of A/B were affordable.

Written 2026-08-23 while settling the radio tuning by ear. Lives in the repo rather than a scratch
folder because the scratch folder is temporary and this took a day to arrive at.

## Build

```
gcc -O2 -I../../CMM_Decoder/third_party/dr_libs -o tunerender tunerender.c -lm
```

## Run

```
tunerender <inA.mp3> <startA> <out.wav> \
           <noiseTrim> <clickAmt> <clickMax> <deemHz> <secs> \
           <wander> <flutter> <flutHz> <burst> \
           <floorDb> <drive> <muteW> \
           [inB.mp3] [startB] [leaveS] [searchS] [arriveS] [kneeW] \
           [lockAmt] [stagger] [noiseHold] [noiseFloor] [lockU] \
           [lockClick] [lockMs] [hiFloor] [trebleInA] [trebleInB] \
           [musicLowCut] [musicSquash] [staticTopCap] [musicEqOn]
```

Positional and long, deliberately — it is driven from a shell loop, not by hand.

The **settled settings** — "F", approved 2026-08-23 and now the shipped defaults (ABI 47,
schema 10):

```
tunerender srcA.mp3 20 out.wav  0.55 0.18 180 2122 15  0.55 0.30 14 0  -5 0.15 5 \
           srcB.mp3 30  1.2 1.5 2.8  0  1.0 0.9 1.0 0.06  0.52 0.0 9 1800 0.28 0.912 \
           0 0 0 0  0 0  1.0 9 400 2600
```

The three that carry the sound, in order of how much they matter:

| arg | value | what it is |
|---|---|---|
| `radioAmt` (38) | **1.0** | how hard the arriving station is mangled. This is the whole thing. |
| `floorDb` (13) | **−5** | how far the station sinks. Small is right; see the state doc. |
| `lockClick` (27) | **0.0** | the decoder clunk. Authentic, measured as the largest discontinuity in the transition, and not wanted. |

Note the two source files must be **different tracks**. With the same song both sides you cannot
tell a handover from a dropout, and a round gets spent on it.

## Two rules, both learned the hard way

**1. Checksum the renders before handing them over.** Three separate times a set went out where
the parameter being varied never reached the audio — a build that silently failed, a `argc` guard
that was never satisfied, a direction flag consumed before it was set — and the files were
byte-identical. Every listening round after that was wasted, and worse, the feedback was
misleading. Always:

```
md5sum *.wav      # or the python one-liner in the shell history
```

If they match, do not send them.

**2. Never stack changes.** Go back to the last thing that was approved, change ONE thing, and get
a verdict before the next. This got broken repeatedly and the result was a chain of four unapproved
changes deep, at which point "worse than ever" is the only possible feedback and there is no way to
tell which layer did it.
