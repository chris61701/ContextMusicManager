# The door: consultation outcome

Four agents were asked independently how to handle (1) sound behind you not occluding as you walk
through an animating door, and (2) sound beyond the door reaching you as it opens. Framings were
deliberately different: minimal-change-in-this-codebase, game-audio prior art, representation
theory, and red-team/test-design. Written 2026-08-22. Problem statement: `problem-door-swing.md`.

## They agreed, and it is one sentence

**Stop putting the LEAF in the field. Put the DOORWAY in the field.**

The acoustic object is a fixed plane on the traversal axis — fixed position, fixed orientation —
whose transmission varies with the swing. The swinging plate is an *input to that number*, never
geometry in its own right.

The prior-art agent found this unanimous across every shipped system it could verify. Wwise
portals, FMOD polygons, Hitman, GTA V and Microsoft's Project Acoustics all model the hole and
write one scalar into it. Nobody voxelises the leaf.

## The line that causes the bug

Two agents arrived at the same line independently, from opposite directions:

```csharp
// EmitterAcoustics.cs:702, in ModelEntityCover
if (b.lightOpacity >= 255 || IsSwingingLeaf(b, bv.type)) return FullCover;
```

Trace `FullCover` through `FieldOcclusion.CellNumbers`: every `cov` is 1, so `fn = 0.5`, so the
compose branch splits the door evenly and **both halves of all three axes** carry `sqrt(T)`. A shut
wood door is 20.2 dB total, so **10.1 dB in each of the six halves**. That is exactly the number
`test_field.c:642` and the `PublishEarCell` header record from the POI doorway.

`FullCover` is what makes a door a six-way isotropic lump. Not the halves, not the compose rule,
not Dijkstra, not the cell grid.

## The model already expresses sidedness — the door is the one thing not using it

This is the finding that makes the fix small, and it is the opposite of what the problem statement
assumed. `fn` **is** the sidedness parameter: `T^fn * T^(1-fn) = T` lets material sit wholly on one
side (`fn` = 0 or 1) while a straight pass-through still pays exactly `T` once. That is what a wall
is, and walls work.

And the geometry falls out, because **the hinge lies in the leaf's shut plane, which is the cell's
split plane**. For a swing of theta toward +X, every point of the leaf has x >= 0.5 for the entire
swing. So the measured AABB gives `mn.x = 0.5`, so `hitN` is false, so `cov[X-] = 0` and
`cov[X+] = cos theta`, so `fn = 0` — **all of the door's loss lands in the half on the side it
swung to**, at every angle.

| listener | source | charge |
|---|---|---|
| room A | room A | **0** |
| room A | room B | T |
| room B | room B | **0** |
| room B | room A | T |
| in the doorway, split at the body | its own side | **0** |
| in the doorway, split at the body | far side | T |

Chris's rule with **no sidedness test anywhere**. Both radios right at once. And the fade is the
coverage itself — `cos theta` gives 20.2 / 8.4 / 3.0 / 1.5 / 0 dB at 0/30/45/60/90 degrees, so a
door 5 degrees open is still acoustically shut. That is the geometric refutation of "assume open the
moment you press E", for free.

**No native change. No ABI bump. The flood, the relax charge, the shoulder rule and the seed are
all untouched.** The native model already had the vocabulary; the managed side was never using it.

## The 45-degree smear is not the problem it looked like

On the traversal axis — the only axis that decides anything — there is no smear, because the hinge
pins `mn.x` to 0.5 at every angle. The AABB gives the same answer as the true oriented plate. On
the axis parallel to the shut leaf the AABB does smear, but the quantity being smeared is ~3 dB and
the error is ~0.2 dB. No oriented-box rasteriser is needed.

## The openness curve is separately wrong, and fixing it shrinks the bug on its own

`CellLoss:497` is `shutLoss * (1f - open)` — **linear in openness**. Project Acoustics (and physics)
say loss goes as 10*log10 of the open *area* fraction: `T = alpha + (1-alpha) * T_shut`. A door 50%
open is **3 dB** down, not 10. The current curve is ~7 dB too muffled at mid-swing, in exactly the
direction of the complaint. `WorldEmitterDoorSwingFrac`, the split `GapFull`/`GapFullOpen`
thresholds and `CloseLeadSeconds` are all hand-built compensation for having the wrong curve.

Note the aperture model gives this for free — `cos theta` coverage through `Face()` is an area
fraction. Also from that paper, worth stealing: clamp maximum occlusion to ~30 dB, because real
doors leak and a door that reaches silence reads as a mix bug. (Probably exempt `IsSealDoor`.)

## ANSWERED IN GAME 2026-08-22 12:08 — the measurement dies mid-swing, and the fallback is on the WRONG SIDE

Run on `woodenGarageDoor3x3White`, player stationary throughout (ear identical to one decimal in
all three reads), speaker 6.8 m away in the **−Z** direction.

```
shut       t=0.010  LEAF measured             split 0.38,0.68,0.48
           published  -X 0.6  -Y 0.6  -Z 20.2   +X 0.6  +Y 0.6  +Z  .      excess 14.9 dB
nt=0.295   t=0.038  LEAF rotation rot=2 axis Z  split 0.38,0.68,0.48
           published  -X  .   -Y  .   -Z  .     +X  .   +Y  .   +Z 14.2    excess  0.0 dB
nt=0.767   AIR (open=1.000)                                                excess  0.0 dB
```

**1. Mid-swing measurement FAILS.** `LEAF measured` at rest, `LEAF rotation` at nt=0.295. The leaf
had risen 0.89 m of 3 m and was still fully spanning the cell, so "swung clear" is not the cause —
this is `AnimatorDoorState` blanking the colliders for `0 <= nt < 0.99`, exactly as predicted.
**The fallback design is the one to build.** Measure once while it is readable, remember the plane,
keep the ramp as the scalar.

**2. The shut measurement is excellent** — the full 20.2 dB on −Z, +Z free. Perfect sidedness, from
the geometry, with no side test. That is the thing worth preserving, and it confirms the aperture
model works when it has a plane to work with.

**3. `RotationLeafBounds` PUT THE DOOR ON THE WRONG SIDE.** Measured says −Z; the fallback's slab at
0.5 against a body at 0.480 says +Z. The real leaf sits at z < 0.48 in its voxel (prefab offset);
the assumed plane at 0.5 is on the other side of the player from the real one. **This is the dip,
and it is the whole of it**: the far-side speaker went from 14.9 dB occluded to *completely
released* the instant the measurement dropped — 15 dB of erroneous un-occlusion, one frame after
the latch, with the player standing still. The complaint arrives from the other direction too:
whichever side is released, the other is wrongly charged.

So the fallback is not a safety net, it is an active flip. Whatever replaces it must reuse the
**measured** plane, never a synthesised one.

**4. The ramp IS sampled at animation rate** — `DoorTrace` shows `open` moving every 30-60 ms
(0.257, 0.750, 1.000...). Agent 1's uncertainty #4 is closed; the gradual fade has a live input.

**5. `Span` confirmed live.** An ordinary door: `span=2m gap=0.81m thresh=0.70/0.15` reaches
`open=1.000` at **nt=0.406** — 40% through its animation, because span is its *height* not its 1 m
aperture. The garage door hits full at nt=0.767. Both are the 2x overstatement, measured.

## The fact this rested on, and how it was tested

`AnimatorDoorState.OnStateUpdate` (3.1 decompile, :119-129) calls `EnableColliders(false)` for the
span `collideOffPercent <= nt < collideOnPercent`, whose defaults are **0 and 0.99**. Every door,
every swing, obstruction or not: **the leaf's colliders are disabled for essentially the entire
animation.** The memory records this as the standing-in-the-doorway case; it is far broader.

So: does Unity keep `Collider.bounds` tracking an animated transform while `enabled == false`?
Measurement works at rest — confirmed in game — and mid-swing is unverified, which is the only time
this problem exists. The game's own `AnimatorDrawbridgeState.CheckForObstacles` does read `.bounds`
off a disabled collider, but the base `CheckForObstacles` re-enables before raycasting, so the game
does not fully trust it either.

**One console command answers it** (see the test script): stand in a garage door's cell — its clip
is ~6 s against a wood door's 0.53 s, so you can run a command during the swing — hit it, and run
`cmm acoustics ray` twice mid-animation. If both say `LEAF measured` and the `split` numbers change,
the design is live. If it falls back to `LEAF rotation`, the leaf has to come off the animated
child's `Transform`/`Renderer.bounds` instead, and the fallback design below applies.

## The one real disagreement — SETTLED by the test above

Mid-swing bounds do **not** work, so the elegant version (measured coverage *becomes* the openness,
`DoorAcoustic` retires) is off the table. Build the fallback:

**Measure the plane once per door instance, while the leaf is readable, remember it, and keep the
tuned `DoorAcoustic` ramp as the scalar.** Fixed geometry, moving number.

The measurement window is at rest, plus the first tick of a swing — the hook is a postfix on
`SetOpen`, so `open = 0.000` at t=0.054 s, and the observed blackout starts at nt=0. Prefer the
at-rest read: a door is shut and readable for the overwhelming majority of its life, and the
recorded plane survives the swing that follows. A door never yet seen shut falls back to the ramp
alone with **no** synthesised plane — given finding 3, no plane is strictly better than a guessed
one.

Accept a measurement only when the leaf is **thin** — smallest extent < ~0.25 block, other two
> ~0.5 — which also gives hatches the right (vertical) aperture axis by measurement rather than by
a name list, and rejects a prefab whose collider union swallowed the frame.

Accept a measurement only when it is **thin** — smallest extent < ~0.25 block, other two > ~0.5.
That one gate makes hatches come out right by measurement rather than by a name list (a hatch is
thin on Y, so its aperture axis is Y — deriving the axis from `(rotation & 1)` would put a hatch's
plane on a horizontal axis and leave a shut hatch not blocking a floor), handles the prefab offsets,
and rejects a prefab whose collider union includes the frame.

## Three things go first, and two of them may be the actual bug

**1. Camera picks the cell; body picks the split plane.** `PublishEarCell` takes `cx` from
`_ear` (camera, :508) but `sx` from the body (:535). When they are in different cells — *precisely
the frame you cross a threshold* — `sx` clamps to 0 or 1 and **the entire leaf is reported on one
side**. One to two frames, on entry and again on exit, twice per transit. This is the single most
likely mechanism behind the reported dip, it is a bug rather than a design question, and it will
masquerade as any sidedness bug added on top of it.

**2. The 8-cell trilinear seed drops seeds past the leaf.** `cmm_fld_solve` spreads the listener
over 8 cells; within ~1 block of a door some land *beyond* it, paying one move's material only —
and the seed separately omits the diagonal shoulder term (audit defect 4), so a diagonal seed past
a jamb corner is materially free. **The far side is already partially released today**, with no
sidedness feature in sight. A fix landed on top of this will look like it worked and will not have.

**3. The ear-cell override race** (audit defect 1). Already on the list. Nothing measured mid-swing
is admissible until it is gone, and after the aperture change it would be the only discontinuity
source left. The fix is ~6 lines and native: stop mutating `m6`; select `earOvr` where the leaving
cell's pointer is hoisted (`cmm_field.c:664`, and :573 for the seed), when `i == earIdx`. One
compare in the outer loop, none in the inner 26-loop.

## The calibration check that decides whether it ships

**Does the measured leaf cover the full voxel cross-section when shut?** `Face()` is a MEAN, so
coverage of 0.90 instead of 1.00 takes a shut wood door from **20.2 dB to 9.6 dB** — which reads as
"shut doors stopped working", the exact regression that was expensive to fix.

Fix by **normalising measured coverage against the plate's own area** (a pose-independent constant
of the prefab) rather than reporting raw cross-section. Then shut is 1.0 by construction and the
swing is `cos theta` of it — calibration preserved by arithmetic instead of by luck. Checkable from
`cmm acoustics ray`'s `ear cell:` line before writing anything.

## Cheapest falsification, before any game code

`test_field.c` already has `ovr_plane(onAxis, at, axis, neg, pos)`, which writes an asymmetric cell
directly. Add a door scene placing the loss **entirely in one half** — `ovr_plane(0, 32, 0, 1.0f,
T)` — with sources on both sides, and walk the listener from A through D into B. If sidedness
survives both cell boundaries and both sources read right at once, the representation claim is
proven with zero game risk.

## Traps the prior art flags

- **Double-counting.** Hitman explicitly excludes doors from its raycast obstruction pass because
  the portal already handles them. Keep leaf material in `CellLoss` *and* add an aperture term and
  doors get charged twice.
- **When the side test is ambiguous, RELEASE.** Project Acoustics chose a deliberately loose
  tolerance (epsilon ~= 3.4 m of path) because a false occlusion is worse than a missed one.
- **A faster flood is not the fix for pops.** Discretised refresh is itself documented as the cause
  of popping at the diffracting-to-line-of-sight boundary. Fix the function's continuity, not the
  sample rate. The 20 ms door tick is a discretiser sampling a discontinuous function.
- **Hysteresis on a binary side flag is a trap** — nobody in the prior art uses it. Wwise blends
  (both rooms at once inside a portal's depth), Hitman interpolates by listener angle, Project
  Acoustics widens the tolerance. A latched flag gets stuck wrong when the player backs out the way
  they came, which is a test that will get run.
- **Zero-thickness portals are the documented cause of harsh transitions.** Wwise gives portals real
  depth for exactly this. If the plane-crossing step turns out audible, an acoustic depth band
  around the plane is the prior-art answer — and it kills the body-vs-camera coin flip too.
- **A post-flood per-source correction cannot work.** For a same-side source the door is not in the
  field's answer to begin with: the winning route never crosses the leaf, so there is nothing to
  subtract. Where the door *is* in the number, the route went through it — which means the source is
  not on your side.

## Separate real bugs found on the way, none of them the swing

Recorded so they are not rediscovered. None block the aperture work.

- **Powered doors (`Class="PoweredDoor"`) are never hooked at all** — no `TEFeatureDoor`, so
  `Patch_DoorAcousticChange` never fires. Powered garage doors, `vaultHatch01_Powered`,
  `metalReinforcedWoodDrawBridgePowered`: the biggest and slowest doors in the game, and they snap.
- **MP remote doors and trader auto-close bypass the hook.** `TEFeatureDoor.Read` and `OnBlockReset`
  flip `isOpen` and call `HandleDoorAnimation` without going through `SetOpen`; auto-close is
  `IsServer`-only. Any door you did not personally touch releases the far side instantly and
  completely.
- **See-through doors are acoustically vault walls.** 618 of the 622 `Place=Door` blocks carry
  `LightOpacity=1`, so chainlink, jail and bar gates take the "stops light means matter" branch:
  `MaterialLoss` 1.0 gives **59.5 dB shut**, identical to concrete. You can see through it.
- **`Span` is the largest multiblock dimension.** An ordinary 1x2x1 door's gap is its *height*, so
  every door is 2x overstated; a drawbridge (5,6,7) uses its 7-block deck and reaches acoustically
  full after **10%** of its animation. Same for `doorWoodLargeGate` (14%).
- **Re-toggle resets openness to the endpoint.** `RefreshDoorAnim` overwrites with
  `Opening ? 0f : 1f`, so closing a door that is 60% open sets openness to **1.0** for a tick.
  Spamming E gives full-volume flashes on every close press.
- **A door anywhere unlocks the global slew for 1.5 s** for every speaker, including the one at your
  elbow.
- **`manholeHatch` reads as solid metal once open** — `Place="TowardsPlacerInverted"` with no second
  `Place`, so `IsSwingingLeaf` is false and it falls to `SolidLoss`. There is already a gap override
  entry for it, which makes it look handled.
- **`IsSwingingLeaf` catches corn and floor traps.** `plantedCorn1/2` and `plantedGraceCorn1/2`
  resolve `Place=Door` via `cropsGrowingMaster`; `b is BlockTrapDoor` catches `looseBoardsTrapBlock`,
  `looseConcreteTrap`, `barbedWireSpindle`, `trapGlass`. Latent today. Live the moment anything keys
  sidedness off `IsSwingingLeaf`.
- **A multiblock child's rotation is worse than recorded.** `parenty + 32` truncated to 5 bits:
  2-tall door top = 31, **3-tall garage top row = 30, middle = 31, drawbridge rows 27-31**.
  `(rotation & 1)` on a child is the parity of the vertical offset.
- **The `(rotation & 1)` axis rule only holds in the Auto branch** of `BlockPlacementDoor`. POI
  prefab data and manual rotation go through `BlockPlacementTowardsPlacerInverted`.

## Declare out of scope, explicitly

So nobody spends a day discovering them.

- **All horizontal leaves** — hatches, cellar doors, manholes, shutters. You are never inside their
  cell, so the ear-cell mechanism cannot apply. They keep the gap ramp and binary at-rest behaviour.
- **Drawbridges.** 5x6x7, one union AABB, no cell-local model reaches it. Give it a gap override so
  it ramps instead of snapping at 10%.
- **Garage/roll-up beyond the ear's own cell.** The plane spans up to 28 cells.
- **Two independent doors sharing one doorway cell**, and the 1-block airlock. The cell answers for
  the leaf it contains.
- **A speaker inside a door voxel** — impossible, the multiblock occupies it. A non-case.
- **Secret/POI doors** — no such block class exists in 3.1. POI "secret" doors are ordinary
  `Place=Door` blocks with a wall texture. Question closed.
