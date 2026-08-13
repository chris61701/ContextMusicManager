# World-emitter acoustics — reference specification

**This file is the definition of "correct" for world-emitter occlusion.**

It exists because the previous system had no such definition. Every change was validated by
"did the latest complaint go away", which is unfalsifiable — and the result was 41 tuning dials,
a soft-knee ceiling that algebraically cancelled to exactly the silence threshold, and a
hard-stop gate that fired zero times in 380 measurements. Nobody knew, because nothing was
measured against a stated intent.

Every future change to the acoustic model is checked against the scenarios below. A change that
improves one scenario and silently breaks another is a regression, not progress.

---

## Ground truth (author's intent, 2026-08-13)

| # | Scenario | Should sound like |
|---|---|---|
| 1 | Radio in the next room, interior door **shut** | Clearly quieter but obviously still music, and duller. Roughly half as loud. |
| 2 | Same, door **open** | Nearly as loud as line-of-sight — and it must come **from the doorway**, not through the wall. |
| 3 | Outside the house, all doors shut | Faint, bass-heavy, no detail. **Depends on the wall material.** |
| 4 | Vault door shut | Nothing. **But depends on what is adjacent to it.** |
| 5 | 60 blocks down a cave | Faint, reverberant, and clearly coming from **the tunnel behind you**. |
| 6 | Garage door opening | Sound floods in **as the gap widens** — gradually, not at the end of the animation. |

---

## Two principles these imply

The parenthetical qualifiers on #3, #4 and #6 are not caveats. They are the specification.

### A. Flanking dominates. Sealing is a property of the assembly, never of one block.

> *"Vault door shut → nothing — depends what is adjacent to it."*

A vault door set in a **wooden wall** seals nothing: sound goes *around* it through the wall.
The same door in a **concrete bunker** seals completely. The partition's performance is limited
by the weakest path around it — this is flanking transmission, and it is why a
single-shortest-path model cannot express #4.

**Consequences:**

- The model must **sum all arrivals**, not pick the cheapest. Under a `min` model the airtight
  door sets the answer and #4 is wrong in the wooden-wall case. Under a sum, the wooden wall
  dominates and the door's rating stops mattering — which is the stated intent.
- `IsSealDoor`, `AirtightLoss` and the `WorldEmitterDoorSealTypes` config list must be
  **deleted**. They declare "sealed" as an attribute of a door type. It is not. A vault door is
  simply thick metal with zero aperture; whether anything is sealed is an emergent property of
  the surrounding geometry.
- #3's material dependence is the same principle at wall scale: the dB table does this work, and
  it must be denominated in dB so material differences read as consistent, ordered steps rather
  than the current 25 dB cliff between wood and stone.

### B. Aperture is continuous, and it is the thing that opens — not the material loss.

> *"Garage door opening → should be gradual."*

A door opening is an **aperture growing**, not a wall getting thinner. Those are physically
different and audibly different: sound through a gap is bright, direct and localised; sound
through a leaf is dull and diffuse.

**Consequences:**

- Door openness (already computed in **metres of gap**, see `DoorAcoustic`) drives the portal
  **aperture area**, not a blend of the cell's solid loss.
- Gradual is then automatic — aperture area grows continuously with the leaf angle.
- A small gap must read as a **localised source at the gap**, not as a quieter version of the
  room beyond (author, 2026-08-13). Tighter gap ⇒ more sharply localised.
- The same mechanism covers damage states: damage opens an aperture. A 1% hole takes a 60 dB
  vault door to ~20 dB while barely touching an 8 dB wood door — the correct and well-known
  behaviour, and it makes #4's "first damage state breaches the seal" fall out for free instead
  of needing a special case.

---

## Measured baseline — the OLD system

From `output_log_client__2026-08-13__04-37-25.txt`, ~10 min, one POI walk + door test.
This is what we are replacing. Kept so improvement can be demonstrated, not asserted.

| Metric | Old system |
|---|---|
| Cost of a shut interior wood door | **−4.4 dB** (spec #1 wants ≈ −6, and *duller*) |
| Range of "wall cost" across one session | **0 dB … −115 dB** for comparable geometry |
| Near speakers (<40 blk) driven to total silence | **7%** — all by the soft knee, not the gate |
| Hard-stop gate activations | **0 of 380** |
| Flood node budget exhausted | **380 of 380 ticks** — never once completed |
| Measurements on the boundary-guess branch | **39%** |
| Jitter, guess branch vs measured branch | **3.2 vs 1.1 blocks** mean, spikes to 90 |
| Reproducible failure | 22→26 blocks of walking: **−5.5 dB → −26 dB → silence** |
| Tone cue | Pinned at maximum dullness beyond 25 blocks — no gradation anywhere |

Scenario coverage of the old system: **#1 too loud and too dull. #4 wrong whenever flanking
matters. #5 silent instead of faint. #6 two-step, not gradual.**

---

## Contents

- `doors/` — per-frame `DoorTrace` + `Occl` traces of the OLD system's door behaviour, per door
  type and direction. The door feel is tuned; these are how we prove it wasn't lost.
- `walks/` — listener-path traces, POI and cave.
- `renders/` — offline WAV renders, old model vs new, same geometry and same path.
- `dumps/` — game-truth voxel data (`cmm acsweep`) for the geometry the traces came from.

## How a change is verified

1. Re-render every scenario offline (old model and new) and listen. This is the primary test —
   scenarios #1–#6 are perceptual and no metric substitutes for them.
2. Diff door traces against `doors/`. Level trajectory within a couple of dB per door type.
   Outside that is a bug, not a taste question.
3. Check the baseline table above has moved in the intended direction and that nothing regressed.
