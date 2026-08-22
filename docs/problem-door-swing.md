# Problem statement — the door swing, and sound on your own side

Not a plan. This names the problem for someone else to solve. Written 2026-08-22, after the
shut-door case was fixed and confirmed working in game.

## The name

**Two different door models run side by side, and crossing a doorway means crossing between them.**

The door is a directional piece of geometry only while your head is inside its cell. Everywhere
else it is an omnidirectional lump. Walking through a door crosses that boundary twice, in about
two frames, while the leaf is also moving.

## The parts

### P1 — The measured leaf exists in exactly one cell

`MeasuredLeafBounds` (`Harmony/EmitterAcoustics.cs:826`), `RotationLeafBounds` (:872) and
`CoverSplitAtBounds` (:769) have exactly one caller: `FieldOcclusion.PublishEarCell`
(`Harmony/FieldOcclusion.cs:543-557`), plus the `cmm acoustics ray` diagnostic. Confirmed by grep.

Every other cell in the world — including the door cell you are one step away from — is priced by
`EmitterAcoustics.CellLoss`, which returns **one scalar for the whole voxel**: no geometry, no
sides, no idea where the leaf is. So the same door is a plane with two sides when you stand in it
and a uniform block of matter when you stand beside it. That is a step change in loss on a cell
boundary, and it is the dip.

### P2 — The two models disagree about what "open" means

- Ear cell: asks the animation where the leaf physically *is* (prefab colliders, current pose).
- Every other cell: asks `DoorAcoustic` for a 0..1 openness and returns `shutLoss * (1 - open)`
  (`EmitterAcoustics.cs:497`), remapped by `WorldEmitterDoorSwingFrac`.

Two different measures of the same swing, on two different curves, in adjacent cells, at the same
instant. Whether both should exist at all is part of the question.

### P3 — "Which side of the door am I on" is not expressible

The model has **box halves per cell** (−X/+X/−Y/+Y/−Z/+Z). Chris's rule is about sidedness
relative to the **doorway**, which is a plane spanning the whole opening — two cells tall for an
ordinary door, more for a garage door or drawbridge. Nothing in the model spans cells.

Worse where it matters most: a leaf at 45° is *diagonal*, so it smears across both halves of an
axis. The "wholly behind you or wholly ahead" property that makes a **shut** door work is at its
weakest precisely mid-swing — the only time this problem exists.

### P4 — Nothing in this window is currently trustworthy

Transit through a door cell at run speed is a few frames. Door animation drives a flood every
20 ms for 1.5 s. And the known [MED-HIGH] ear-cell override race (TODO.md, audit defect 1) is
worst *exactly* in that window — it is installed essentially the whole time, while the ear's own
cell is re-read every frame. **Any mid-swing measurement taken today is suspect.** That defect
probably has to go first, or conclusions drawn from a run-through log cannot be relied on.

### P5 — There is no source-side term, and the rule is a relation between two sides

Chris's rule — *"if you're on the same side of the door as the sound, the sound behind you is
released immediately, no matter the state of the door"* — is a statement about the **relation**
between where the listener is and where the source is.

The field is listener-anchored and scalar: one number per cell, read by every emitter. In
principle that delivers the rule for free — a route from the ear to a source on your own side
never crosses the leaf, so it never pays. It does not currently, because of P1 and P3: outside
your own cell there is no leaf to be on a side of.

## What a solution has to satisfy

1. **Continuous.** No step change in loss as you cross a cell boundary near a door.
2. **Your side is released immediately**, at any animation state, including fully shut.
3. **The far side fades in with the swing**, gradually — this is the part that has to feel real.
4. **It must not release the far side too.** An earlier proposal ("assume open the moment you press
   E") was rejected for exactly this: it releases both sides at once.
5. **Sources on both sides at once must both be right.** Two radios, one each side, is the test
   case Chris actually runs.
6. **Free when no door is near.** `_doors.Count > 0`-style gating; this is the hot loop.
7. **Must not regress the shut door**, which is confirmed working in game and was expensive.
8. **Symmetric on close as well as open.** Closing currently slams early on purpose.

## Do not re-derive

The door traps are already written down and cost four failed attempts:

- `door-leaf-geometry` memory — `GetBounds` is a unit cube for model entities *by construction*;
  the leaf IS measurable off prefab child colliders; a multiblock **child's rotation reads 31**
  (8 in A21) and your head is always in the child; the game **disables a shut door's colliders**
  when you stand in the doorway; `IsMovementBlocked` is a pathfinding predicate, not geometry;
  CMM's hook is a postfix on `TEFeatureDoor.SetOpen`, so `isOpen` flips at the **latch**, before
  any animation.
- `half-voxel-occlusion` memory — the six-half model, the compose rule, and why the ear's cell
  deliberately does not obey it.
- Measured swing ramp: `open` 0.000 @ t=0.054s, 0.295 @ 0.114s, 0.977 @ 0.212s of a 0.53 s clip.
- `WorldEmitterDoorSwingFrac` is 1.0. It did nothing at any value while the leaf was switched off
  during the swing; it has not been retested since.
