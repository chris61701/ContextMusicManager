# TODO / Roadmap

Backlog of planned and in-development features. Status tags: **(done)**, **(in progress)**,
**(conceptualizing)**, **(not started)**.

## Radio / world-emitter

- [ ] **In-game item/mod to play radio stations** — a placeable/equippable radio the player
  can use to tune stations. *(conceptualizing; will be disabled in multiplayer)*
- [ ] **Multiple radio stations from `/Music/radio` subfolders** — each subfolder under
  `/Music/radio` defines a separate station, and the folder's name is the station name.
- [x] **Radio distortion / coloration DSP** — "through a speaker" bandpass + soft-clip +
  low static (audio stage 3c). Built (ABI 26, `WorldEmitterColor*` knobs); in-game test pending.
- [ ] **DJ system** — Fallout 3/4-style station DJ/announcer. *(idea, maybe 1.0.6)*
- [ ] **Radio sync** — world emitters and the personal radio still drift audibly apart
  (should stay within ~1 s). The unpause re-glue (2026-07-10) helps but does not fully
  fix it — instrument the actual voice-vs-paper offsets to find the remaining source.
  *(in progress)*
- [ ] **Diegetic authenticity pass** — emitters are functional but far from satisfactory
  as in-world objects; polish the spatial/acoustic presentation until a placed radio
  reads as a real thing in the world. *(not started)*

## UI / overlays

- [x] **Now Playing overlay** — on-screen current-track display. Built: Alt+Num7
  toggles a persistent top-left track+context readout; in-game test pending.
- [x] **DebugHud** — done, just hasn't shipped with a release version yet.
- [ ] **Hide DebugHud while the console is open** — the F1 console overlaps it; gate the
  HUD (and Now Playing?) the same way the SP-pause hide works. *(not started)*

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
