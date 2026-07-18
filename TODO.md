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

## Keybinds / shortcuts

- [x] **Shortcut keys** — built (Alt+Delete ban / Alt+Insert favourite / Alt+Home
  combat-music toggle, rebindable); in-game test pending:
  - Ban tracks you don't like (skips + never picked again; persists via loudness store)
  - Favourite tracks you want to hear more often (`FavouriteWeight`× random weighting)
  - Toggle all combat music off/on on the fly — session-only; linked vanilla
    Combat/Bloodmoon sections suppressed too
