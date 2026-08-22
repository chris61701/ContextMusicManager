# TODO / Roadmap

Backlog of planned and in-development features. Status tags: **(done)**, **(in progress)**,
**(conceptualizing)**, **(not started)**.

## 1.0.5 release blockers

Still on v1.0.5 — no release until these are worked out (list set 2026-07-11):

- [x] **Ham radios in mono, slightly distorted** — a ham radio shouldn't render a stereo image.
  Built 2026-07-13 (ABI 34, deployed all 4, in-game test pending): emitters were already
  mono-downmixed at the mixer, but the coloration was one global set defaulting to OFF — so
  hams played clean hi-fi. Now every speaker point carries a render class (per-point flags,
  bit0 = ham) and `radioHam` gets its own always-on coloration set
  (`WorldEmitterHamColor*`: drive 0.3 / static 0.04 / voice 0.65, hot-reloadable) rendered as
  an independent signal per station slot; shelf speakers keep the clean speaker set. The flag
  bits 1-2 are reserved for the stereo-pair channel split (next item).
- [ ] **Speakers in mono** — unless another speaker is nearby, in which case each plays one
  channel (a natural stereo pair). *(conceptualized 2026-07-13, design settled, not built:)*
  - Lone speakers are ALREADY mono (the mixer downmixes each station to a mono slot lane);
    only the pair split is new.
  - Signal: keep each slot's L and R through to the output callback (stereo slot ring;
    mono consumers take (L+R)/2 there — no third lane). ABI 35.
  - Flags: bits 1-2 of the per-point render flags shipped with ABI 34 (0 = mono, 1 = left,
    2 = right). Ham points never pair.
  - Pairing (managed, in GroupSnapshot): two speaker-class points of the same station within
    `WorldEmitterPairRadius` (~4 blocks) become a pair; L/R assigned by world-position order
    (lexicographic) so the assignment is stable across ticks, sessions and MP clients. 3+
    cluster: nearest two pair, the rest stay mono. Flags recompute every 4 Hz push, so a
    destroyed half reverts the survivor to mono with no state to clean.
  - Render: generalize today's per-class machinery to per-VARIANT (class × channel):
    speaker-M / speaker-L / speaker-R / ham-M, each with its own source pick, coloration +
    LPF state and gain bucket; cost only for variants actually present (most slots = 1).
    Carry reverb keeps feeding the dominant variant; no occlusion/reverb MODEL change, so
    no judge.js mirror needed.
- [ ] **Address performance issues.**
- [ ] **Convert anything to C that can be converted** — continue the managed→native migration.
- [x] **Buzz/static tuning sound effect when changing stations.** Built 2026-07-14 (ABI 35,
  deployed all 4, in-game test pending): band-passed noise synthesized in the native mixer (no
  asset files), pinged at the 4 Hz tick while a USER dial waits for its voice (async open/seek
  or the loudness gain-gate). Same-day fix (ABI 36, user heard double-fires): the static is a
  native KEEPALIVE — each ping extends ONE seamless hash instead of layering bursts, and
  `cmm_mix_stop_sfx` releases it (~90 ms) the moment the station voice lands. Spontaneous joins
  (world entry, EOF advance, stinger rejoin, unpause resync) stay clean. `RadioSfxEnabled` /
  `RadioSfxVolume` knobs, hot-reload.
- [x] **Click when the radio turns on.** Same build: synthesized power-on click (bright noise
  ping + low thump) fired on the Off→station dial edge.
- [x] **Click when the radio turns off.** Same build: duller, longer power-down clunk on the
  station→Off dial edge.
- [x] **Radio turns OFF (with the off click) when every station/track is deleted** — same
  build: `PersonalRadio.Tick` powers off (clunk + "Radio: off (no stations)") when the catalog
  is EMPTY; a single dead station with others present keeps the revivable "off air" hold. The
  tuned name persists, so dialing back on after restoring files resumes.

De-prioritized for this release: multiplayer testing, and cross-version (A20/A21/2.6)
verification — emitters are excluded there anyway.

## Audio engine — the remaining list (set 2026-08-21)

- [x] **A half block or a shut door in YOUR OWN cell does not occlude you** *(reported
  2026-08-21, built 2026-08-22, NOT YET HEARD IN GAME)*.
  - **What it actually was.** Bigger than the report. A cell's material lived on its three
    NEGATIVE faces, so the face between C and C+X belonged to C+X — which made entering the
    block in C from +X free, and leaving toward +X free, and a route that entered through one
    positive face and left through another paid nothing at all for the block it walked through.
    A plane wall survived it (a plane is always reached from its negative side, which is why
    every wall check in `test_field` passed); anything you can get AROUND did not. A speaker
    walled in on four sides with its corners open measured 0.1 dB against 58.6 dB with the
    corners filled — and the route was two straight moves, so the corner fix (`99edffb`) never
    even got a chance to fire.
  - **The fix, in two halves.** `cmm_field` ABI 4: cells carry six HALF-VOXELS instead of three
    faces, and a move charges the half it leaves through plus the half it enters through. The
    pair on an axis composes back to the cell's own material, so every answer for a full block
    is unchanged. Then `CoverOf` reads the block's own `GetBounds` and says WHICH half the
    material is in, which is what makes a half wall block crossing it and not standing beside
    it, and a sliver ceiling block upward while staying open downward.
  - The listener's own cell needed no special case in the end — it is still seeded free, but
    the flood's FIRST STEP out of it now crosses its own half.
  - **Four shape discounts came out** (`SolidLoss` ×0.5 and ×0.2, `CellLoss` ×0.1 trim and
    ×0.05 open-centre). All four were smuggling WHERE into HOW MUCH because there was nowhere
    else to put it; with real coverage they price the same geometry twice.
  - **Still open:** a shut door standing in your own cell gets HALF a leaf, not a whole one.
    Model entities have no honest bounds (`GetBounds` returns a default cube), so a door's
    material sits in both halves and leaving the cell crosses one of them. That is 0 dB → half
    a door, which should settle the report; the rest needs the leaf's facing from its rotation.
  - **Verify with** `cmm acoustics shape [x y z]` for the coverage numbers, then by ear.

- [ ] **The door SWING.** The leaf is now measured off its prefab colliders, so its position and
  axis are right and it moves with the animation. What is not settled is how the sound should
  behave DURING the swing. Chris's rule stands: the door blocks by which side you are on, never by
  what the animation is doing, and sound beyond it should fade in gradually as it opens while sound
  on your own side is released immediately.
  - The gradual part comes from `CellLoss`'s openness blend (`DoorAcoustic`), which with the shipped
    gap defaults is 0 until the leaf is 15% open and 1 at 70%. Measured swing: `open` 0.000 at
    t=0.054s, 0.295 at 0.114s, 0.977 at 0.212s of a 0.53 s clip. Closing slams early on purpose.
  - Now the leaf is measured, the openness ramp may be redundant for the cell you are standing in —
    the bounds already leave the voxel as it swings. Decide whether the two should both apply.
  - `WorldEmitterDoorSwingFrac` is 1.0 (no remap). It did nothing at any value while the leaf was
    switched off during the swing; retest it now that it is not.

- [ ] **Field defects found by the 2026-08-22 audit and NOT fixed.** Ranked as the audit ranked them.
  These are real; the door work only fixed the three that shared its symptom.
  - **[MED-HIGH] The ear-cell override races the geometry reader.** `cmm_fld_solve` save/modifies/
    restores the shared `m6` slot for the ear's cell (`cmm_field.c` ~458 and ~782). Any
    `cmm_fld_set_cell` write landing inside a flood is overwritten by the restore *while staying
    marked clean*, so the read is thrown away and not redone until the next dirty. Worst exactly
    during a door: `DoorMovingOrJustMoved` floods every 20 ms tick for 1.5 s, so the override is
    installed essentially the whole window, and the ear's own cell is re-read every frame. Fix by
    not mutating `m6` — the leaving cell's pointer is hoisted once per settled cell, so a single
    `i == earIdx` test there covers it (nothing re-enters the seed cell profitably).
  - **[MED] `cmm_fld_set_ear_cell` publishes coordinates BEFORE the numbers**, and the guard tests
    only the coordinates. A reader can see the new cell with the old six values. Write `earOvr`
    first and `earHas` last.
  - **[MED] `_ear` is a `Vector3` read across threads with no barrier.** A torn read seeds from a
    position that never existed and silently drops the override for that flood — while `EarCellWhy`
    still prints it as published. Rare (<<1%), but it means the diagnostic line is not proof the
    override was applied. Neither is it when `have` is 0 for that cell.
  - **[MED] The seed's material charge omits the diagonal shoulder term**, though its comment claims
    it is "identical for a diagonal, which is what stops a seed slipping through the seam where two
    walls meet". A diagonal seed can start on the far side of a sealed corner for free — the same
    20 dB failure the shoulder rule exists to fix. Either add it or correct the comment.
  - **[LOW-MED] `free_space_db` mirrors the seeding arithmetic but not the seed SET** — it
    normalises over all eight cells and never checks `in_win`/`have`, where the flood skips unread
    seeds and normalises over the survivors. During warm-up and after every recentre this inflates
    `excess`.
  - **[LOW] The fallback seed can expand from uninitialised memory** — no `have` check before
    seeding the containing cell, and `m6` is malloc'd, never zeroed. One flood, on a fresh window or
    a teleport.
  - **[LOW] Cell coords used as block coords** in `PublishEarCell` — correct only because
    `StepXZ == StepY == 1`, and the body-fraction line has no guard of its own.

- [ ] **Port the occlusion output shaping into the field.** THE LAST THING the audio engine
  needs. The per-door gap tuning (`WorldEmitterDoorGapOverrides`, hand-tuned per door type
  2026-07-08) was calibrated through `EmitterAcoustics.ShapeOcclusion`, and the field replaced
  that stage with a straight line. The gap model still produces the same `Openness`; what
  changed is everything downstream of `CellLoss`. Two bends are missing:
  - **soft knee (tanh)** toward a ceiling, so detour/gaps can never fully silence a speaker —
    there is always an audible floor (stand behind a crate, still hear it).
  - **hard-stop gate on the SOLID part only**, smoothstepped in from 55%, so enough material
    drives true silence (a sealed vault). Detour can never do this; material can.
  - Together they give a long shallow middle and a decisive slam at the end, and that shape is
    what the per-door numbers were placing. Linear vs tanh diverge most in the MIDDLE, which is
    exactly where the doors now feel wrong while the endpoints feel right.
  - **Do it in dB, not in virtual-distance units.** ShapeOcclusion works in virtual blocks and
    scales walls by `range/30`; the field's whole contract is that occlusion is a dB value
    reproduced exactly and independent of range (which was the fix for "high Range almost
    ignores occlusion"). Same two curves, applied where the field already works.
  - The field already computes the two terms separately — that is the `(wall X, detour Y)` split
    in the diagnostic — so the inputs are there. Ship behind a flag so the current linear
    behaviour is one setting away.
  - Note `ShapeOcclusion` is already `internal` "so EmitterFieldCache shapes its field-sampled
    occlusion through the SAME curves" — the precedent exists.

- [ ] **Tuning INTO a frequency, like an FM dial.** Dialling a station should sweep in rather
  than cut in: the signal rises out of the noise as you approach it and falls back as you pass,
  the way a real analogue dial behaves. Today `Alt+PgUp/PgDn` steps discretely and the static
  burst is a fixed "searching" sound between stations. Wants a notion of being BETWEEN two
  frequencies — partial signal, partial noise, and the mix following how close you are.
  Relates to the static work below; probably the same feature.

- [ ] **Static on emitters does not sound right.** Needs "some type of cut off" for world
  radios — a band limit / squelch so a distant or weak world radio degrades like a radio rather
  than just getting quieter. Currently the ham coloration is always-on per render class
  (`WorldEmitterHamColor*`) and there is no distance- or signal-dependent breakup.

- [ ] **Stereo pairs.** Lone speakers are ALREADY mono — the mixer downmixes each station into a
  mono slot lane (`g_emitterMono`), so a single speaker is not playing a stereo image. What is
  not built is the PAIR split: two speakers of the same station close together each taking one
  channel. Design was settled 2026-07-13 (render flags bits 1-2, `WorldEmitterPairRadius`,
  L/R by lexicographic world position so it is stable across ticks and MP clients). See the
  1.0.5 blocker entry above — this is the same item.

## Radio / world-emitter

- [ ] **In-game item/mod to play radio stations** — a placeable/equippable radio the player
  can use to tune stations. *(conceptualizing; will be disabled in multiplayer)*
- [ ] **Multiple radio stations from `/Music/radio` subfolders** — each subfolder under
  `/Music/radio` defines a separate station, and the folder's name is the station name.
- [x] **Radio distortion / coloration DSP** — "through a speaker" bandpass + soft-clip +
  low static (audio stage 3c). Built (ABI 26, `WorldEmitterColor*` knobs); in-game test pending.
- [ ] **DJ system** — Fallout 3/4-style station DJ/announcer. *(idea, maybe 1.0.6)*
- [x] **Radio sync** — world emitters and the personal radio drifted audibly apart.
  Instrumented (`[CMM SYNC]` audit lines, DebugLogging builds), root cause measured:
  the game's `worldTime` advances at only ~0.89× realtime while decoders run 1.0×, so
  every voice outran the worldTime paper schedule at ~7 s/min and consumers re-glued at
  different moments. Fixed by making the broadcast clock local: worldTime-anchored at
  world entry, realtime-rate in play, frozen during SP pause, worldTime jumps carried.
  Round 2: 3.0 saves the world synchronously on the pause edge (~2.5 s stall) — clock
  intervals are now attributed to their START pause-state so the stall counts, and
  sub-second pauses accumulate resync debt. Play-tested 2026-07-11: sync confirmed good.
- [ ] **Diegetic authenticity pass** — emitters are functional but far from satisfactory
  as in-world objects; polish the spatial/acoustic presentation until a placed radio
  reads as a real thing in the world. *(not started)*

## UI / overlays

- [x] **Now Playing overlay** — on-screen current-track display. Built: Alt+Num7
  toggles a persistent top-left track+context readout; in-game test pending.
- [x] **DebugHud** — done, just hasn't shipped with a release version yet.
- [x] **Hide DebugHud while the console is open** — the F1 console overlaps it; gate the
  HUD (and Now Playing?) the same way the SP-pause hide works. Built: draw-time gate on
  `GUIWindowConsole` being open hides DebugHud + Now Playing/notices (all versions, MP and
  menu included); music keybinds are also suppressed while the console is open so console
  scrolling (PgUp/PgDn = radio dial) and line editing can't drive them. Verified 2026-07-11.

## Contexts

- [ ] **Option: disable stale for contexts** so a context never expires while inactive
  (always resumes its track no matter how long you've been away). Today this requires a
  large per-context `stale` value in `contexts.xml`; add a first-class toggle.

## Music library

- [x] **Scan registered / add-on music directories at startup** *(requested 2026-08-20 by a
  soundtrack author; called "the single feature I'd request")*. **Built and confirmed in-game
  2026-08-21.** No registration file in the end — the convention IS `<mod>/Music`, mirroring the
  same tree, so a pack is just an ordinary mod. `cmm roots` / `cmm roots <ctx>` / `cmm scan`.
  `subfolders="true"` applies to the primary only; playlist and absolute sources are never
  extended; same-named stations merge. Everything under "has to get right" below is covered,
  and the folder-membership fold in `ContextWatchSignature` is the part that mattered. Original
  problem statement kept for the record:
  Today a soundtrack has to be
  copied INTO CMM's own music folder, so every soundtrack update means manually overwriting
  files inside CMM. That makes CMM and the soundtrack one blob on disk, which is exactly what
  a mod manager cannot cope with.
  - **Why it matters beyond convenience:** under MO2/Wabbajack, CMM and a soundtrack would stay
    separate mods with independent Nexus pages, versions, downloads and file provenance. A
    soundtrack update becomes a normal mod update instead of a hand-merge, and uninstalling
    either one leaves the other intact.
  - **Shape:** other mods declare a music root (a small XML in their own Mods folder, or a
    convention like `Mods/<anything>/CMMMusic/`), and CMM scans every registered root at
    startup, unioning them into the same context tree it builds from `MusicPath` today.
  - **Things it has to get right:** two roots supplying the same context folder must MERGE, not
    win; the loudness store is hash-keyed so measurements survive a track moving between roots;
    the file watcher must cover every root (and J: is exFAT, so each needs the sloppy-mtime
    listing fallback); and a root that disappears must degrade to "those tracks are gone", not
    to an error.

- [x] **Track cooldown / anti-repeat over the whole pool** *(requested 2026-08-20, same source)*.
  **Built 2026-08-21**, `Harmony/TrackRotation.cs`, offline harness `tools/CmmRotationTest`
  (231 checks incl. a real-filesystem section), **in-game test still pending**. The bag, as
  asked: `TrackNoRepeatMemory` defaults to -1 = the whole folder, 0 = the old behaviour
  exactly, N = a recency window. Every "has to get right" below is covered — bans skip
  without stalling a pass, added music is eligible at once, and the pass is per context and
  session-only (deliberately not persisted). `cmm rotation`.
  - **`FavouriteWeight` was reworked in the same pass.** A strict bag would have flattened a
    favourite to one play per pass like everything else, so it gets `FavouriteWeight` TURNS
    per pass — and the weight is now SCALED to folder size (50-track reference, never scales
    down), because as a raw multiplier it meant "every 45 min" at 40 tracks and "every 19 h"
    at 1000. Measured constant at 5.7% of airtime from 50 tracks to 5000. Both the draw
    weight and the allowance scale; scaling the allowance alone does nothing.
  - Selection allocates NOTHING — `Select` walks the pool twice rather than building a
    candidate array, which past ~10,600 tracks was a large-object-heap allocation per pick.
  - **Radio does not use any of this** — see the code note in `RadioStationCatalog`: each
    station is a fixed name-seeded permutation, so it already has the guarantee, and bans
    and favourites never reach it.
  - **The load-bearing decision: commit at PLAY, not at pick.** Three of the six
    `PickNextFile` callers are speculative (the staged pin, the pre-staged advance, the
    missing-file replacement walk) and their picks are routinely discarded — a bag consumed
    at pick time would drain without a note being heard. The commit hangs off
    `ContextCache.CommitToHistory`'s existing replay detection, which is also what makes
    "a resume must not spend a slot" true for free.
  - The WHOLE random path (bag, avoid, seam, weighted roll) moved into TrackRotation so the
    harness links it. `PickRandomHonouringFlags` is gone; `CmmScaleBench`'s hand-ported copy
    of the selection is gone with it.
  - Cost, measured (`CmmScaleBench`): per pick, 3000-track folder 0.40 ms → 1.54 ms, same
    allocation. Once per track change, and repeat picks inside one change hit a cache.
  - Original problem statement kept for the record:
  With a large library, hearing the same track twice in a short window is far more noticeable
  than any amount of variety is pleasing. The ask: every eligible song plays once before
  anything reshuffles, so a big soundtrack FEELS as big as it is.
  - **What exists today is weaker than the name suggests:** `RandomNoRepeat` (the default) only
    excludes the track that just played -- one track of memory. Over a 200-track folder that
    still lets a track return on the very next pick with probability ~1/199 each time, and the
    birthday effect makes an early repeat likely within a listening session.
  - **Shape:** a per-context bag (shuffled permutation) that refills only when exhausted, or a
    recency window of the last N picks sized as a fraction of the pool. The bag is the stronger
    guarantee and matches the request literally.
  - **Things it has to get right:** interaction with `stale`/resume (a resumed track must not
    consume a fresh bag slot), with ban/favourite flags (favourites want to appear MORE often,
    which a strict bag forbids -- probably weighted refill rather than duplicate entries), with
    a folder whose contents change mid-session, and with persistence (does the bag survive a
    relog? Probably not, but the answer should be deliberate).

## Keybinds / shortcuts

- [x] **Shortcut keys** — built (Alt+Delete ban / Alt+Insert favourite / Alt+Home
  combat-music toggle, rebindable); in-game test pending:
  - Ban tracks you don't like (skips + never picked again; persists via loudness store)
  - Favourite tracks you want to hear more often (`FavouriteWeight`× random weighting)
  - Toggle all combat music off/on on the fly — session-only; linked vanilla
    Combat/Bloodmoon sections suppressed too
