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

- [x] **The door SWING — the aperture model.** Done 2026-08-22. A door's leaf cannot be read while
  it moves (`AnimatorDoorState` blanks its colliders for `0 <= nt < 0.99`, every door every swing —
  measured: `LEAF measured` at rest, `LEAF rotation` at nt=0.295 with the leaf still fully spanning
  the cell). So the door stopped being a leaf and became an APERTURE: its plane is measured once
  while readable and remembered, and the openness ramp supplies how much of it is blocked. Fixed
  geometry, moving number — what Wwise, FMOD and Project Acoustics all do.
  - `RotationLeafBounds` is DELETED, and that was the bug. Its synthesised slab at 0.5, against a
    body at 0.480, put the door on the OTHER SIDE of the listener: one frame after the latch, player
    standing still, a far-side speaker went 14.9 dB occluded -> 0.0 dB. Both halves of the complaint,
    one mechanism. A door with no remembered plane now gets NO plane — the isotropic lump is blunt
    but symmetric, and a guessed plane is confidently wrong in the one direction you are listening.
  - Three capture points, all main-thread: the reader passing a shut door (once per door, ever),
    the first tick of an opening swing (before the clip starts, so the colliders are still on), and
    standing in one at rest. The middle one is what covers a door somebody else opened.
  - Accepted only when the leaf is THIN (min extent < 0.25, mid > 0.5). That gate also gives a hatch
    its horizontal aperture by measurement, and rejects a prefab whose collider union ate the frame.
  - STILL OPEN: `Span` is the largest multiblock dimension, so an ordinary door's gap is its HEIGHT.
    Measured live: a 1x2x1 door reads acoustically fully open at nt=0.406, the garage door at 0.767.
    Fixing it rescales every door's ramp, so it belongs with the re-tuning pass, not before it.
- [ ] **DOOR FINE-TUNING — deferred 2026-08-22, after the aperture work was confirmed working.**
  The doors are RIGHT now (which side, and that they block at all). What is left is how they FEEL,
  and all of it wants ears rather than a debugger. Do it in one sitting, with the levels pass.
  - **`Span` is the largest multiblock dimension, so every door's gap is overstated.** For an
    ordinary 1x2x1 door that dimension is its HEIGHT, not the 1 m opening, so it reads acoustically
    fully open at **nt=0.406** — 40% into a 0.53 s animation. Measured live 2026-08-22, alongside
    the garage door at nt=0.767 and `doorWoodLargeGate` at ~14%. A drawbridge (5,6,7) uses its
    7-block DECK and is fully open after **10%**. The honest span is the dimension the aperture
    actually opens along, which the remembered leaf now knows — thin axis aside, a swinging door
    opens across its width and a roll-up across its height. Fixing it rescales every door's ramp,
    which is why it belongs here and not before.
  - **A shut door contributes almost nothing; the DETOUR does the work.** Measured with a shut iron
    door between listener and speaker: `wall 1.0, detour 31.6` — 1 dB of the 32 came from the door,
    the rest from the flood routing ~36 blocks around the building. Defensible as diffraction, but
    it means `WorldEmitterFieldDetourDb` is currently the door knob, and turning the door knob does
    almost nothing. Decide which one should be carrying it before tuning either.
  - **`WorldEmitterDoorMaxLoss` (0.60 = 26.8 dB) was chosen by arithmetic, not by ear.** It is the
    ceiling that stops a metal door reading as a metal wall. Sanity-check a shut steel door against
    a shut wood door (20.2 dB) and a concrete wall (52 dB) and move it. `cmm field doormax <n>`.
  - **Re-toggle resets openness to the endpoint.** `RefreshDoorAnim` overwrites with
    `Opening ? 0f : 1f`, so closing a door that is 60% open sets it to fully OPEN for a tick.
    Spamming the key gives full-volume flashes on every close press. Cheap fix, audible symptom.
  - **A door anywhere unlocks the global slew for 1.5 s**, for every speaker including the one at
    your elbow. Should be per-door-per-speaker, or at least range-gated.
  - **`WorldEmitterDoorSwingFrac` is 1.0 and has never done anything measurable.** It did nothing
    while the leaf was switched off during the swing; that is no longer true. Either it earns its
    place in this pass or it goes.
  - Everything else door-shaped that is NOT about feel — powered doors never hooked, MP remote
    doors bypassing the hook, chainlink gates reading as vault walls, `manholeHatch` — is written
    up in `docs/plan-door-aperture.md` under "separate real bugs". Those are correctness, not
    tuning; do not fold them into this sitting.
- [ ] **Field defects found by the 2026-08-22 audit and NOT fixed.** Ranked as the audit ranked them.
  These are real; the door work only fixed the three that shared its symptom.
  - **[FIXED 2026-08-22] The ear-cell override races the geometry reader.** `cmm_fld_solve` save/modifies/
    restores the shared `m6` slot for the ear's cell (`cmm_field.c` ~458 and ~782). Any
    `cmm_fld_set_cell` write landing inside a flood is overwritten by the restore *while staying
    marked clean*, so the read is thrown away and not redone until the next dirty. Worst exactly
    during a door: `DoorMovingOrJustMoved` floods every 20 ms tick for 1.5 s, so the override is
    installed essentially the whole window, and the ear's own cell is re-read every frame. Fix by
    not mutating `m6` — the leaving cell's pointer is hoisted once per settled cell, so a single
    `i == earIdx` test there covers it (nothing re-enters the seed cell profitably).
  - **[FIXED 2026-08-22] `cmm_fld_set_ear_cell` publishes coordinates BEFORE the numbers**, and the guard tests
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
- [ ] **Sweep the dial past stations and hear them** — 1.0.5a. *(scoped 2026-08-27, not
  started.)* Today one station is open at a time, so a press costs an open + seek (1.7-3 s
  measured) and a fast spin lands nothing at all: you hear the station you left, buried under
  the hiss, until something finally arrives. Realistic tuning means the neighbours are already
  running so crossing one is instant and already in step with the broadcast clock.
  - NOT all stations at once. Decode is not the constraint — real-time factors off this
    machine's own logs are ~650x for flac, ~200x opus, ~100x m4a, i.e. 0.15-1% of a core per
    stream — but three other things are:
    - `MAX_VOICES_HARD` is 12, budgeted as 4 music + emit stations x 2. Fifteen stations do
      not fit; three or four do.
    - `cmm_mix_add` sets every OTHER music voice's target to 0. A second station cannot
      coexist with the first today — the add is what kills it. This is the real blocker and
      it is a rule, not a limit.
    - The wait is the OPEN, not the decode, and it is per-station-change.
  - So: keep the station either side of the dial position open and decoding at zero gain, and
    warm the new neighbours in the background once the dial settles. Sweep slowly and you hear
    each station cross; sweep fast and you outrun the warming and get static — which is what a
    real radio does, so the failure mode is the correct one.
  - The hard part already ships: world speakers run up to three concurrent stations in render
    slots that coexist and sum (`emitSlot`, `CMM_MAX_EMIT_VOICES`). The personal radio is on
    the main music bus instead, where the retire-others rule applies. Give radio voices the
    same slot treatment and raise the voice cap.
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
- [ ] **Speakers near the spawn point can stay silent for a whole session** — *(reported
  2026-08-27, not diagnosed.)* Loaded in at (460, 38, -453); some speakers close by never
  played. Reloading the world fixed it completely.
  - From the log (`10-15-08`, after the 13:31 spawn): only TWO stations were ever registered,
    Enclave Radio and Continue FM. No third station appeared in the six minutes that followed,
    and the listener stayed within ~30 blocks of spawn throughout. So the speakers were never
    DETECTED — this is EmitterRegistry, not voicing.
  - Ruled out: slot exhaustion (slot 2 sat empty, `s2=0/0`); the 8-point cap (3 and 2 points in
    play); selection; a station with no tracks (Chris confirms all valid); the chunks-not-loaded
    race (`IsChunkAreaLoaded` guards the scan); POI distance (`DistanceToBox` uses the real box,
    not the corner); oversized POIs (only two `rwg_tile_*` road tiles were skipped, neither of
    which holds shelf speakers).
  - Left: the blocks were not one of the three `TargetNames`, or that POI was absent from
    `GetWorldPrefabs` at the moment we looked and nothing brought it back. The 8 s
    `RescanInterval` only rescans POIs already in the near set, so a POI that never entered it
    is never reconsidered.
  - NOTE the target set is matched BY NAME (`radioHam`,
    `cntStoreShelfElectronicsSingleBottomSpeaker`, `cntStoreElectronicsBookShelfSpeaker`) and we
    have just moved to 3.2. A renamed or added variant fails silently, with no log line and no
    error — the same shape as the `IChunk.GetBlock` removal in 3.1. Worth checking the 3.2 block
    table against that list regardless of this bug.
  - Next occurrence: stand next to a silent one and run `cmm ac ray speaker`. "no speaker in
    range" means it is not in the target set. A `cmm speakers` dump of the registry snapshot +
    which nearby POIs were scanned/cached/skipped would settle it in one line and does not exist
    yet.
- [ ] **A station out of range keeps its render voice** — *(found 2026-08-27, not fixed.)*
  After the 13:31 spawn, Enclave Radio held slot 0 with its nearest speaker at 64.2 m against
  a `WorldEmitterRange` of 60. Inaudible, and occupying one of three slots. Harmless that day
  because slot 2 was free; with three stations nearby it costs a real one.
- [x] **Block -> sound latency: measured, and it is fine.** *(closed 2026-08-27 by ear.)*
  A block change reaches the ear in ~2 s (19-24 emitter ticks), and Chris's verdict placing a
  block in a doorway against closing a door in the same doorway was "seems about the same" --
  which is the result that matters, since the door path is the one tuned by ear.
  - The rate limiter is NOT the pacer: only 2-3 of those 20-odd ticks are clamped. The rest is
    the field's own answer settling as it reads more of the scene in. Worth remembering before
    anyone blames `WorldEmitterOcclusionFadeRate` again.
  - The old instrument could not have told us this. It fired on "the gain moved >= 1.0 in one
    tick", so it reported a smooth two-second delivery and a one-step delivery identically, and
    its 5 s cutoff silently discarded everything slower. Both fixed (784df3a) before this test.
  - No `door -> sound` lines were produced during the test, so there is no instrumented door
    reference -- only the ear's. If this is ever reopened, get one.
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
