# Door occlusion: in-game test script

Numbered, unambiguous, runnable in one sitting. Every step has a PASS and a FAIL signature so the
result is not a judgement call. Cheapest and most-likely-to-fail first. Written 2026-08-22 from the
consultation (`plan-door-aperture.md`).

**Step 4 is the one that matters right now.** It answers the single fact the whole aperture design
rests on, and it can be run before any code is written.

## Setup, once

- **S1.** A closed room, roughly 5x5, one wall with a **1-block-wide doorway**. A POI bathroom
  works. Ordinary 2-tall wood door in it (`oldWoodDoor`, or any 1x2x1).
- **S2. Radio A: INSIDE**, on the floor, 3 blocks straight back from the doorway, on its axis.
- **S3. Radio B: OUTSIDE**, same axis, **at least 12 blocks** from the door. Deliberate: `cmm field`
  only ever reports the **nearest** speaker, so keeping A nearest from both sides makes every number
  below refer to A. B is the by-ear check.
- **S4.** If A and B play the **same track**, pick B up and replace it one block sideways. Station is
  `hash(position) % stationCount`; a one-block move rehashes.
- **S5.** Mark three spots on the axis: **IN** (2 blocks inside), **DOOR** (in the door block),
  **OUT** (2 blocks outside).
- **S6. Needed for steps 4 and 12:** cut a 3-wide opening in another wall and fit a
  `woodenGarageDoor3x3White`. Its clip is ~6 s against a wood door's 0.53 s — it is the only door
  you can run a console command *during*.

## Steps

**1. Pin the model, turn diagnostics on.**
```
cmm acoustics model field
cmm quiet off
```
PASS: reply says pinned to FIELD, and within ~5 s `cmm field` shows `read complete`.
FAIL: never leaves `reading NN%` — stop, every number below is against half-read geometry.
`cmm quiet off` is not optional; the `ear cell:` line and `DoorTrace` are only written when logging
is on.

**2. Bisect.** At **OUT**, door shut. `cmm field occl off`, then `cmm field occl on`.
PASS: radio A clearly louder with occlusion off, muffled again with it on.
FAIL: no audible difference — the symptom is not occlusion and nothing below applies.

**3. The ear cell at rest.** At **DOOR**, door shut, facing into the room. `cmm acoustics ray`.
Read only the second line, `ear cell:`.
PASS: `... LEAF measured  split a,b,c`, and in the `published` line **one** horizontal half shows
~20 dB while its opposite shows ` . `.
FAIL-A: `LEAF rotation rot=NN` — the prefab was unreachable, you are testing the fallback plane.
FAIL-B: `no leaf`. FAIL-C: both halves of one axis carry a number — no sidedness at all.

**4. THE ONE THAT DECIDES THE DESIGN — the ear cell MID-SWING.** Stand in the **garage door's**
cell (S6). Hit it. While it is still moving, run `cmm acoustics ray` twice.
PASS: `LEAF measured` both times, and the `split` numbers **change** between the two reads.
FAIL: `LEAF rotation` mid-swing when step 3 said `measured` at rest. That is Unity refusing to
report bounds for colliders `AnimatorDoorState` disables for the whole animation — it means no
measured-leaf design can work during a swing, and the leaf must come from the animated child's
`Transform`/`Renderer.bounds` instead. Stop and re-scope.

**5. Turn-around invariance.** At **DOOR**, door shut. `cmm acoustics ray`, note which half carries
the loss. Turn 180 degrees **without moving your feet**. `cmm acoustics ray` again.
PASS: the same half carries the loss.
FAIL: the loaded half swaps — the camera is deciding sidedness, not the body.

**6. Shut door, your own side.** At **IN**. `cmm field`, read the `nearest:` line.
PASS: `wall` ~= 0.0, `excess` <= 3 dB.
FAIL: `wall` >= 10 dB with a shut door *behind* you and nothing between you and A.

**7. Shut door, far side.** At **OUT**. `cmm field`.
PASS: `wall 17-23` (one wood door is 20.2 dB at the shipped `wall 59.5 / pow 1.56`),
`excess ~= 19`.
FAIL-A: `wall` < 8, the door is not blocking. FAIL-B: `wall` > 45, it is charged twice.
(If it answers `no point`, the readout was consumed by the last call — wait a second, repeat.)

**8. The standing-in-the-doorway penalty.** At **DOOR**, door shut. `cmm field`.
PASS: within ~3 dB of step 6.
FAIL: >= 8 dB worse than step 6 — the "1.7 dB room becomes 12.6 dB just by standing there"
regression is back.

**9. The cell-boundary walk — the dip, located.** Door shut. From **IN**, step one block at a time
to **DOOR**, running `cmm field` at each block. Four readings.
PASS: monotone-ish, no single block adds more than ~5 dB.
FAIL: a step change >= 10 dB at one boundary. Note **which** boundary.

**10. The far side fades in.** At **OUT**. Open the door, listen to A.
PASS: A fades in over the swing.
FAIL-A: A arrives all at once at the latch, before the leaf moves.
FAIL-B: A arrives only when the animation stops.

**11. Your own side is untouched — the headline requirement.** At **IN**. Open and close the door
four or five times, listening to A only.
PASS: A does not change level at all, at any animation state.
FAIL: A dips or swells with the swing.

**12. Both sides at once.** Stand in the **garage door's** cell. Open it slowly, listen to A
(inside, near) and B (outside, far) together.
PASS: A stays steady and full while B fades in.
FAIL-A: both change together. FAIL-B: A drops when the door moves.

**13. Spam.** At **OUT**. Press E five times as fast as you can.
PASS: the level moves smoothly and is never louder than the door fully open.
FAIL: audible full-volume flashes on the *close* presses.

**14. Let it close on you.** Stand in the **DOOR** block, close the door, then stand completely
still for 5 seconds. `cmm acoustics ray` at the start and again at the end.
PASS: `LEAF measured` both times, the **same** half carries the loss, A holds steady.
FAIL-A: flips to `LEAF rotation` — colliders went off and stayed off.
FAIL-B: the loaded half changes on its own while you never moved — the game's `PushPlayers` is
walking your body across the split plane.

## Command surface, exactly as `CmmConsoleCmd.cs` parses it

The built-in help text is out of date in two places; this is what actually works.

`cmm field` · `cmm field occl on|off` · `cmm field detour|wall|pow|slew|aperture|swing <n>` ·
`cmm acoustics` (alias `ac`) · `cmm acoustics on|off|rebuild|here` · `cmm acoustics wall <n>` ·
`cmm acoustics verb <n>|off` · `cmm acoustics column` (alias `col`) · `cmm acoustics shape [x y z]` ·
`cmm acoustics ray [speaker]` · `cmm acoustics model auto|native|legacy|`**`field`** (the `field`
mode is accepted but undocumented in the help string) · `cmm acsweep <poi-name|here|list [filter]|status|cancel>` ·
`cmm quiet [on|off]` · `cmm sfx [test|jitter|samples|vol]` · `cmm scan|rescan|roots|rotation` ·
`cmm nativeplay|nativenext|nativestop`.
