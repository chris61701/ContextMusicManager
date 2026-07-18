# Changelog

All notable changes to Context Music Manager are documented here.

## 1.0.5

### Added

- **Radio dial sounds.** The personal radio now sounds like a physical device: a click when
  it powers on, a lower clunk when it powers off, and a static "searching" hash while
  dialing between stations or joining one that's still tuning in — the static rolls for
  exactly as long as the join takes and tails off under the music's fade-in. And instead of
  sitting in silent dead air forever, the radio now powers itself off (with the clunk) when
  every station and track has been deleted; the tuned station is remembered, so restoring
  the files and dialing back on resumes as usual. All three sounds are synthesized inside
  the native mixer — no audio files ship, nothing to replace or mispackage — and ride the
  music volume exactly like the radio itself. `RadioSfxEnabled` / `RadioSfxVolume` in
  `settings.xml` (hot-reload), and every sound's character is yours to dial in live: the
  static's `RadioStaticLowHz` / `RadioStaticHighHz` (band body vs brightness),
  `RadioStaticCrackle` (0 = smooth hiss, 1 = full sputter) and `RadioStaticVolume`; the
  clicks' `RadioClickOn*` / `RadioClickOff*` (tone cap Hz, snap = sharp tick vs soft thud,
  length, volume). Edits are audible mid-static / on the next dial. Native ABI 38.
- **Ham radios sound like ham radios.** The `radioHam` block now has its own always-on
  "small communications speaker" voicing — narrowed band, a touch of soft-clip breakup, a
  whisper of static — while store shelf speakers keep playing clean. Under the hood every
  speaker point now carries a render class through to the native mixer, and each class is
  colored and distance-muffled as an independent signal (a station playing through a ham
  AND a shelf speaker at once renders both characters correctly; the room reverb follows
  whichever you're mostly hearing). Tunable via the new `WorldEmitterHamColorDrive` /
  `WorldEmitterHamColorStatic` / `WorldEmitterHamColorVoice` settings (hot-reload; all 0 =
  clean ham). Native ABI 34.
- **Personal radio.** Tune your own radio to any station with two keys (**Alt+PageUp** /
  **Alt+PageDown**, rebindable): the dial steps `Off → station → station → Off`, wrapping both
  ways, and shows the station in the game's own notification area above the toolbelt. Your
  radio plays the station's live world-clock broadcast (the same song, at the same point, as
  every world radio and every other player tuned to it), non-spatially, replacing the normal
  context music while it's on — dial back to Off and the situational soundtrack returns. The
  tuned station and on/off state persist across sessions. Works on EVERY supported game
  version (A20/A21/2.6/3.0) — only the in-world emitters remain 3.0-only.
- **Station folders can be removed live.** Deleting a station's folder mid-game no longer
  silently retunes every radio in the world: radios tuned to it go quiet ("off air" — static
  is planned), everything else keeps its station, and the moment the folder returns (~2 s
  watch) its broadcast resumes. The personal radio survives the same way.
- **Multi-station radio.** Every subfolder of the Radio source folder is now its own radio
  STATION (the folder name is the station name); loose tracks directly in the folder remain a
  single fallback station when no subfolders exist. Every POI's radios all tune to one station
  — picked stably per building, so the diner plays one thing and the store across the street
  another — and a radio you place yourself gets a station from its position (move it to
  retune). Stations broadcast on the WORLD CLOCK: each runs a deterministic rotation seeded by
  its name, so every session, every radio, and every player in the same world hears the same
  song at the same point, and walking back to a station rejoins its broadcast mid-song. Up to
  the 3 nearest stations are audible at once, each with its own position, occlusion, muffle
  and radio-coloration path (native render slots), sharing one room reverb the way real
  sources in one room do. The trader PA `loudspeaker` is no longer a music emitter (it keeps
  its announcements); ham radios and store speakers remain.
- **Ban / favourite / combat-music keybinds.** Four new live controls alongside the
  existing skip and volume keys (all behind the same Alt gate, rebindable in
  `settings.xml`; defaults sit on the Ins/Del/Home/End cluster, never on letters the game
  binds under free-look): **Alt+Delete** bans the playing track — it skips immediately and
  is never picked again (banning every track in a folder mutes that context, which then
  resolves through its fallback chain like an empty folder). **Alt+End** undoes the last
  ban — each press walks one step further back through this session's bans (25 deep); a
  banned track can also be unbanned by replaying it via Prev and pressing Alt+Delete
  again. **Alt+Insert** favourites it — favourites are
  picked `FavouriteWeight`× (default 3×) more often in the random orders. Both flags
  persist in `Cache/loudness.xml` next to the volume trim and follow a file through
  renames/moves via its content hash. **Alt+Home** toggles ALL combat music off/on for
  the session (the Combat tiers, their biome variants, Blood_Moon_Combat — any context
  whose name contains "combat"): while muted, fights keep whatever music was already
  playing, and the game's own Combat/Bloodmoon sections are held back exactly like a
  `DisabledVanillaSections` entry. Each keypress confirms via a notice on the Now Playing
  overlay (which pops up briefly for the notice even when toggled off).
- **Now Playing overlay.** **Alt+Num7** (rebindable: `KeyNowPlaying`; `NowPlayingEnabled`
  sets the launch state) toggles a persistent top-left readout: the playing track's title
  (long names scroll like a music player's display) over a status line with the context,
  the time remaining / total track time, and the track's volume trim percentage — which
  updates live as the volume keys are pressed. While vanilla music or silence plays the
  box stays up and says so ("Vanilla music" / "Nothing playing") instead of vanishing.
  The same box is the notification surface for every keybind: ban/favourite/combat
  notices swap onto the status line for a few seconds, then it reverts — and with the
  readout toggled off, the box appears just for the notice and fades away. The debug HUD
  steps below the box whenever it's visible, so nothing in the top-left corner ever
  overlaps. (Num7's old debug role — the functionally-dormant GC A/B switch — is retired.)
- **Per-context play history for Next/Prev (music-player back/forward).** Each context now
  remembers the last 25 tracks that actually played (session-only). **Prev** steps back
  through them one at a time (deleted files are skipped; at the very start of the history
  the press does nothing — it no longer falls back to a random pick), and **Next** — or an
  automatic track advance — replays forward through anything you rewound before picking
  fresh. Manual skips (next/prev/ban) also crossfade at the new `ManualSkipFadeDuration`
  (default 0.25 s) instead of the context's fade times, so a keypress switches immediately;
  automatic advances keep using `TrackTransitionFadeDuration`.
- **World-emitter "Radio" — music through in-world speakers.** Custom music can now play
  diegetically out of speaker blocks placed in the world, layered additively over the
  normal soundtrack and heard positionally: per-speaker panning and distance attenuation,
  occlusion that routes around walls (a material-weighted shortest-path cost field plus a
  direct-ray fallback), and distance-scaled reverb, all rendered natively (CMM_Decoder
  ABI 25) and spatialized in the output callback so head-turns are heard in ~10 ms. A
  station clock keeps the track continuous as you move in and out of range, and the source
  hot-swaps on disk edits like any other context. Configured through the new inert `Radio`
  context (source/crossfade/volume) plus `WorldEmitter*` knobs in `settings.xml` (range,
  min-distance, falloff, wall-distance, reverb, stereo width), all hot-reloaded.
- **Playlist (`.m3u`/`.m3u8`) sources for contexts.** A context's `path` attribute is
  renamed to `source` (`path` kept as a deprecated alias) and a source can now be a folder
  **or** a playlist file. An `.m3u` source is a flat, file-only pool: relative entries
  resolve against the playlist's folder, absolute paths as-is; blank/`#` lines and
  missing/invalid entries are skipped. A folder source also auto-unions its top-level
  `.m3u` files with its loose music (deduped by path); recursive (`subfolders=true`)
  folders ignore `.m3u`. In-place playlist edits hot-reload, including on filesystems
  (exFAT/FAT/network) whose folder mtime doesn't move on add/remove.
- **Per-section disable switches for vanilla Dynamic Music.** A `<DisabledVanillaSections>`
  block in `settings.xml` (one hot-reloaded `true`/`false` child per section — Combat,
  Trader, Bloodmoon, etc.) lets you switch off individual vanilla sections while DMS is
  otherwise enabled. A disabled section defers to its linked context's rules rather than
  going silent — e.g. with Combat disabled, a POI theme keeps playing through a fight
  instead of being torn down for vanilla combat music — and only falls to the vanilla
  Exploration ambient (or silence) when nothing else is on the stack. An empty list leaves
  default behavior byte-identical.
- **Richer game-state snapshot for `context.lua`.** The version-agnostic `gameState` passed
  to the Lua resolver gains weather (rainfall, snowfall, wind speed, cloud thickness,
  temperature), time of day (minute-resolved hour + day), vitals (stamina/food/water %),
  stealth (`isCrouching`), active `buffs[]` (with a new `cmm.hasBuff(gameState, sub)`
  helper), and both a live `fallDistance` and a one-shot `fallImpact` that fires once at
  touchdown with the peak height of the fall (so landings are detectable by the poll).
  All fields are change-gated in the marshal, so steady-state resolve allocations stay ~0.

### Changed

- **Radio stations start instantly at any library size.** Station broadcasts no longer scan
  every track's length up front (a large playlist station could sit silent for a minute after
  a fresh launch). Schedules now re-anchor once per hour of world time (`RadioAnchorSeconds`)
  and a join reads only the handful of file headers between the anchor and "now" — in the
  background, independent of pool size, so tuning in or approaching a radio plays within about
  a second even on a first launch. Songs still play whole and back-to-back on the shared world
  clock; at the hourly re-anchor a playing song finishes first, then the station rejoins the
  live broadcast (nothing is ever cut mid-song). Also covers live edits: adding or removing
  tracks re-resolves instantly instead of re-scanning.
- **Massive contexts now crawl incrementally.** A `subfolders=true` context pointed at a
  huge tree fills its snapshot over time instead of all at once: a growing partial snapshot
  is published during the sweep so the context becomes playable long before the full walk
  finishes, recursive contexts are round-robined one file-budgeted turn at a time (so one
  giant folder can't starve another context's first snapshot), and a single very large
  folder is paged via a resumable cursor. The main frame thread never enumerates a
  recursive context, closing a window that could briefly freeze it.
- **Lower resolve-tick GC.** The per-tick snapshot marshal now caches each scalar field and
  re-sends only the ones that changed instead of boxing/allocating ~20 values every scan
  tick (steady roaming tick: ~20 allocations → ~1).
- **Overlays hide while the game is paused.** The Now Playing box and the debug HUD sit in
  the same top-left corner as the pause menu's options; both now hide during a single-player
  pause and come back exactly as they were on unpause.
- **Overlays hide — and CMM's keys go inert — while the F1 console is open.** The console
  covers the same corner (including on the main menu and in multiplayer, where the pause
  hide can't apply). Every CMM key is also disabled while it's open: scrolling the console
  is PageUp/PageDown — the radio dial keys — and numpad digits typed into a command could
  silently cycle the debug HUD. Everything returns exactly as it was when the console closes.
- **World-emitter occlusion rebuilt on per-speaker acoustic fields.** Occlusion used to be
  re-computed around the listener several times a second inside a hard node budget, which
  produced abrupt loudness jumps while walking (adjacent-step cliffs of 12 dB+, an audible
  "ring" at the computation boundary, and one-block-thick floors/roofs leaking sound
  upward). Each speaker now gets one cached, budget-free acoustic field that the moving
  listener simply samples — validated offline against exported game-world data, cutting
  adjacent loudness cliffs by 83–97% across test POIs. Door swings keep their tuned
  fast path; fields quietly rebuild when a door settles or the world changes. New
  hot-reloaded knobs: `WorldEmitterFieldCache` (on/off, off = the old behavior),
  `WorldEmitterFieldRadius`, `WorldEmitterFieldRefresh`.
- **Around-corner reverb now carries by bounce energy.** The reverb heard around corners
  was graded by raw route distance, which read as "muffled" one corner in. It's now graded
  by how much energy survives the bounces (route length ÷ the room's mean free path, at
  the room's measured reflectivity) — in a reflective stone cave the reverb follows you
  around corner after corner until it genuinely runs out of energy; in an absorptive wood
  room it still dies quickly. New hot-reloaded `WorldEmitterReverbBounce` (default on).

### Fixed

- **World radios could be silent after quitting to the menu and continuing.** The menu↔game
  handoff parks the audio output's gain at zero and relied on the next music start (normally
  the spawn sting) to restore it — with the personal radio off and no spawn track, the world
  emitters restarted into a muted output and stayed inaudible until any music event (e.g.
  dialing the personal radio). The gain is now restored at every spawn.
- **Pausing no longer knocks the radios off the broadcast clock.** The world clock freezes in
  the single-player pause menu while radio audio keeps playing (muted), so every pause pushed
  live stations ahead of the shared broadcast — world radios and the personal radio audibly
  drifted apart over a session. On unpause they now rejoin the broadcast, which also means a
  pause no longer skips music: the song resumes where you stopped hearing it.
- **World radios and the personal radio no longer drift apart at all (broadcast clock
  rebuilt).** Beyond the pause bug above, measurement showed the game's world time itself
  advances noticeably slower than real time during play (~10% on the test machine), so every
  radio slowly outran the world-time broadcast schedule and re-joined it at different moments
  (a dial, a track ending, an unpause) — ending up seconds apart, occasionally even on
  different songs. The broadcast clock now runs at true real time, anchored to world time at
  world entry — so relogs and other players still share the same broadcast — and two pause
  subtleties are handled with it: the game saves the world on the pause edge (a multi-second
  stall that used to be double-counted against the clock), and several quick sub-second
  pauses now add up toward the same broadcast rejoin that one long pause gets. Radios now
  hold within a fraction of a second of each other indefinitely.
- **A hint of the main-menu theme could be heard while paused.** The game deliberately fades
  the title theme in over the single-player pause screen, and while CMM's vanilla-music
  fallback was active it slipped through the pause silence. The menu music now stays muted
  through every pause, and quitting to the menu hands menu music back through its proper
  fade-in (it could previously skip that handoff entirely after an in-game vanilla fallback).
- **A track ending during a pause unmuted everything (CMM_Decoder ABI 33).** The pause
  silence is a device-wide gain duck, and starting any new audio revived that gain (a
  mechanism menu transitions rely on) — so when the playing track hit its end mid-pause and
  auto-advanced, the new track and every nearby world radio became audible through the
  pause screen. A voice started during the pause now stays silent under it and is simply
  in progress when you unpause.
- **Fully-silenced spawn stingers bled in right at their end.** With `VolumeModifier` at 0
  and the radio resuming after a spawn, the stinger's last seconds became audible during its
  release fade: the shared volume governance flips from the stinger's volume (which honours
  `VolumeModifier`) to the radio's (which deliberately doesn't) while the released voice is
  still fading. The released voice's level is now matched across that flip — silent stays
  silent, and normal configurations sound unchanged.
- **Vanilla fallback could hold the wrong music for minutes near a trader.** While CMM's
  own music plays, the mod tracks (muted) what the game's music system proposes, and holds
  vanilla sections back so they can't bleed under CMM's tracks. After handing off TO vanilla,
  that same hold kept applying — pinning the game to whatever section happened to be current
  when the handoff happened, sometimes for minutes (observed at Trader Rekt). While vanilla
  is the active layer the game's live section choice now plays; user-disabled sections stay
  suppressed.
- **Hitches while the radio plays and when switching stations.** Three causes: with the radio
  owning the music layer the context engine re-asserted its "music should be playing" recovery
  ~9× a second for the whole radio session; every station switch opened and seeked minutes into
  a file on the main thread (rapid dialing compounded it); and dialing onto a station whose
  join track wasn't loudness-measured yet could leave the previous station playing until its
  song ended. Radio starts now open/seek on a background worker and the suppression is
  understood by the recovery watchdog.
- **Death/spawn/POI-clear stings now interrupt the radio.** One-shot stingers crossfade (or
  duck, for POI-clear) the personal radio instead of being swallowed by it, and the radio stays
  silent from death until respawn — and never starts before you've spawned into the world.
- **Approaching a radio no longer flips its station.** Overlapping POI prefabs are discovered
  progressively on approach, which could reassign a radio's station seconds after it first
  became audible; a radio's station is now pinned to its physical position for the session.
- **Placing a radio inside a building took up to ~8 s to become audible.** Radios placed inside
  a POI's bounds waited for that POI's periodic rescan (an 0–8 s lottery, ~5 s buildings). The
  proximity sweep now registers a just-placed radio immediately with its proper station —
  audible in about a second, with the new `WorldEmitterBirthFade` controlling its fade-in
  (`-1` inherits the station start fade; the fade also covers a fresh station voice born from
  the placement).
- **Radio tracks no longer start loud and then settle.** Two causes: the personal radio's
  upcoming tracks were never loudness-measured ahead of time (the world-emitter prewarm only
  covers stations with a nearby speaker), so every track change started at full volume until
  its measurement landed; and loudness measurements weren't saved between sessions, so even
  the song that resumes at world entry re-measured (and started hot) every launch. The tuned
  station's on-air and next tracks now prewarm continuously, measurements persist in
  Cache/loudness.xml again (the file stays small — it only ever holds the recently-played
  working set; manual volume trims are still never dropped), and a station switch onto a
  never-measured track briefly holds in "tuning" until its measurement lands (bounded at
  a few seconds) instead of starting loud — the broadcast keeps running, so you simply
  join the song a moment later. On filesystems that don't
  update a folder's timestamp when files inside it change (exFAT USB drives, SD cards, FAT,
  many network shares), the mod's change detection never fired: adding, removing or renaming
  tracks — or whole radio station folders — went unnoticed until a restart. All three watchers
  (flat context folders, recursive contexts, and the radio station catalog) now use a
  listing-based signature on those filesystems, so live edits hot-load there just like on NTFS.
  NTFS users keep the cheaper timestamp fast path.
- **Wrong game option read on A20/A21 (cross-version enum drift).** `EnumGamePrefs` member
  ordinals differ between game versions, and the single cross-version DLL bakes the compile-time
  values in — so on A20/A21 the mod's "is the vanilla Dynamic Music System enabled" checks read a
  different, unrelated option, which could skew the vanilla-handoff decisions there. Affected
  option lookups are now resolved by NAME at runtime against the running game (the new
  world-clock day-length read uses the same path).
- **Mod could fail to load entirely on A20/A21 (unified DLL).** The world-emitter door hook
  referenced a 3.0-only game type in its Harmony attribute; on games without that type the
  patch scan threw and aborted every patch in the mod. The hook now resolves its target by
  name at runtime and skips itself cleanly on pre-3.0 versions, and the whole emitter layer
  runtime-detects the game version instead of trusting the build flags.
- **World-emitter radio transitions cleaned up (CMM_Decoder ABI 30).** Four related fixes to
  how the station starts, stops, and hands over between speakers:
  - *Ghost audio when replacing a destroyed radio.* Destroying a radio and placing another
    within the ~2 s reverb-tail window re-voiced the old track's leftover reverb energy at
    full wet gain under the new voice's fade-in. A fresh station start now resets the native
    acoustic state (reverb tails, tone/pan/mix slews) before the new voice exists. A track
    advance deliberately keeps the tail — the room hasn't changed.
  - *Burst of un-muffled audio at distance on track change / station start.* A new native
    voice was seeded unoccluded until the first occlusion flood landed (~0.1–0.5 s), so an
    indoor speaker 50 m away played "in the open" for a beat. A track advance now carries
    the last landed occlusion set straight onto the new voice; a fresh start seeds muted and
    lets the first flood glide the true value in under the start fade.
  - *Hard cut at ~64 m from POI speakers.* The registry forgot POI speakers past a flat 64 m
    while the audible range is 100 m (reverb 150 m), so walking away hard-cut the station
    mid-audibility and walking in popped it into existence. The keep radius now derives from
    the configured range × reverb reach.
  - *Placed radios pop in at full volume when the station is already playing.* A speaker that
    appears while the voice is live now fades in over the Radio context's `fadeintime`
    (a decaying virtual-distance ramp on its published occlusion gain) instead of popping.
    Stale per-slot smoothing in the native spatializer — which glided a re-activated point
    from whatever the previous occupant left behind — now initializes from the true target.
  - *Decorative trim / plates / poles / frames blocked sound like half a wall.* The occlusion
    model's "artist marked it as passing light" discount (thin or holed partial shapes,
    `LightOpacity 0` in shapes.xml — 526 of 2263 shapes, including all plate/trim pieces)
    never fired: it read `shape.LightOpacity`, which the game never populates for shapes.xml
    shapes (the value lands on the block's own `lightOpacity` field instead). A wood
    `plateHalf` used as wall trim was charged half a wood wall per cell. Now reads the
    block's resolved value: trim, poles, frames and holed pieces drop to a fifth of a wall;
    solid halves/wedges/ramps and doors are unchanged.
  - *Hard cut walking out of a building.* Crossing a doorway/threshold could flip the routed
    occlusion from near-clear to fully-blocked in one 4 Hz tick, and the big-jump glide
    (tuned for door slams) muted the radio in ~50 ms — a hard cut, leaving only the reverb
    tail. Movement-driven occlusion **rises** are now rate-limited (new hot-reloaded
    `WorldEmitterOcclusionFadeRate`, virtual blocks/s, default 50 ⇒ ~a one-second fade-out;
    0 restores the old instant behavior). Door events bypass the cap (their tuned fast
    response is unchanged) and occlusion drops are never capped — sound arriving still
    tracks the event.
- **Decoder stalls on large video files.** Two container-reader bugs made the in-game music
  scanner hang on certain large `.mp4`/`.mkv` files. An mp4's oversized `moov` index (e.g.
  36 MB on a 10 h recording) exceeded the cache cap and was parsed via millions of tiny
  reads on a multi-GB file (2 GB file: >25 s → 0.7 s). And mkv/webm eagerly demuxed the
  whole audio track into RAM at open; it now streams on demand (20 GB mkv: ~130 s → 0.2 s,
  peak working set for an 808 MB decode 17 MB instead of O(size)). Decoded output verified
  bit-for-bit identical (LUFS matches ffmpeg).
- **Recursive menu music never left the vanilla bed on a huge tree.** The menu reclaim only
  ran after a full sweep (minutes away on hundreds of thousands of files); it now also runs
  on the crawler's incremental partial publish, so custom menu music takes over within
  seconds. Handing the menu back to vanilla now fades the title theme back in over the
  crossfade instead of snapping it to full volume.
- **Wrong/missing context music on Turkish & Azerbaijani locales.** Vehicle-type, trader-name,
  and file-extension matching used culture-sensitive `ToLower()`, so the Turkish-i rule made
  strings containing "i" (e.g. `EntityMinibike`) stop matching the ASCII literals. Now uses
  `ToLowerInvariant`.
- **Disabled trader dropped combat music to Exploration.** In a trader's area the game
  proposes the trader section even mid-combat; with the trader disabled, the blind fallback
  played exploration music during a fight. It now reproduces the section the game would pick
  from threat alone (Combat/Bloodmoon when panicked, Suspense when spooked), matching CMM's
  own priority model where combat outranks trader.

### Build

- **VS Code "Package (Release zip)" task** now invokes the single-zip `Package` MSBuild
  target (was `PackageAll`).
- **Acoustics validation tooling** (development only): an in-game `cmm acsweep` console
  command exports a POI's voxel grid, occlusion floods and acoustic fields as game-truth
  dumps, and `tools/CmmAcousticsJudge/judge3d.js` replays the shipped model against them
  bit-for-bit offline (parity checks, defect detectors, variant comparison, browser slice
  viewers). This loop found and validated the per-speaker field rework above. Debug builds
  also log `[CMM SYNC]` broadcast-drift audit lines (the data behind the clock rebuild).

## 1.0.4

### Added

- **Context resolution is now an editable Lua script.** The decision logic that maps the
  game state to a music context lives in `Config/context.lua` instead of being baked into
  the DLL, so the whole policy — priorities, the weighted combat-threat model, day/night and
  biome rules — is open for modders to edit, hot-reloaded the moment the file is saved. The
  script runs in a hard sandbox (no file/OS access) via an embedded MoonSharp interpreter and
  sees a version-agnostic `gameState` snapshot plus your `settings.xml` tunables through the
  documented `resolve(gameState, cfg)` API (`Config/context.api.md`). A pristine copy of the
  default script is embedded in the assembly and used automatically if the on-disk
  `context.lua` is missing or fails to compile, so a broken edit can never take down audio —
  delete the file to restore the default. The shipped `context.lua` reproduces the previous
  built-in behaviour token-for-token.
- **Playback and per-track volume keybinds.** Skip to the next/previous track and nudge the
  current track's volume up/down with `Ctrl+Alt+Arrows` (rebindable in `settings.xml`), with
  a transient on-screen popup showing the track and its level. The manual per-track trim
  (`userTrim`) layers on top of loudness normalization and is saved to `Cache/loudness.xml`,
  so a track you've turned down stays down across sessions.
- **Loudness normalization (EBU R128).** Each track is measured once for its integrated
  loudness (LUFS) and played back at a per-track gain, so quiet and loud tracks sit at a
  consistent perceived level instead of jumping in volume between tracks. Measurements are
  cached in `Cache/loudness.xml` keyed by file content (FNV-1a hash), so renaming or moving
  a track keeps its value. The track you need next is measured up front; a throttled
  background sweep pre-measures the rest one file per tick. Measurement runs on a background
  thread and retains no PCM, so memory stays flat regardless of track length. Tunable in
  `settings.xml` via `LoudnessNormalizationEnabled`, `LoudnessTargetLufs` (default -18 LUFS),
  and `LoudnessMaxBoostDb` (default 12 dB; attenuation is uncapped — the boost cap exists
  because `AudioSource.volume` can't exceed 1.0).

- **Multiplayer squad-shared combat.** An enemy fighting a nearby player now puts you into
  combat too, so a squad shares one combat state even when the zombies are on your teammate.
  Two `settings.xml` knobs tune it, independently of `PlayerDetectRange` (which governs the
  ally/stranger proximity counts): `CombatShareRange` (how close that player must be for their
  fight to count as yours) and `CombatShareRequireAlly` (when `true`, only party members /
  friends share combat; set `false` to share with any nearby player). No effect in single-player.

### Changed

- **Music now plays through Context Music Manager's own audio engine.** Custom tracks are
  decoded, mixed, and sent to your audio device entirely on threads the mod owns, instead of
  going through the game's Unity `AudioSource`/`AudioClip` system. Because playback no longer
  shares Unity's audio and main threads, the music keeps playing smoothly even when the game
  saturates the CPU — world generation, loading screens, or another program briefly hogging
  the machine. The output buffer adapts on its own: it deepens while a world is loading, so
  nothing can interrupt the music, and stays tight and responsive on the menu and in normal
  play. Crossfades, mid-track resume, next/previous-track skipping, loudness normalization,
  and the per-track volume trim all carry over unchanged, and menu↔game transitions fade in
  and out gracefully over ~5 seconds. The engine uses the bundled cross-platform miniaudio
  backend and shares the game's entry in the Windows volume mixer, so the system music slider
  still controls it.
- **Exit to the main menu now fades the music out** over ~5 seconds before releasing audio
  caches, instead of cutting it off abruptly.
- **Moved-while-playing tracks are detected reliably on any filesystem.** The file watcher
  now pings the single playing file directly (an O(1) existence check) rather than trusting
  the music folder's modification time, which exFAT/FAT/SD cards, some network shares, and
  junctions don't reliably update when a file is moved out. When the playing file disappears
  it is trimmed from the in-memory snapshot and hot-swapped for another track that still
  exists, keeping detection O(1) so it scales to very large libraries on slow media.
- **POI infested→cleared handoff is now continuous.** All POI contexts (infested and
  non-infested) share `warmgroup="poi"`, so when the last sleeper dies the non-infested POI
  context inherits the group's warm-up start instead of restarting its own dwell. Ambient no
  longer leaks through the gap while the `POI_Cleared` stinger fades.

### Fixed

- **Music could get stranded in silence after a burst of interrupting contexts.** If a
  transition was cut off (or its load failed) while no further context change followed, the
  named context could be left "playing" with nothing audible and no transition in flight. A
  convergence check now detects this exact state and re-asserts the top context so the music
  actually starts.
- **Click when a new context interrupted an in-progress crossfade.** Repurposing a channel
  that was still playing an audible leftover from a half-finished crossfade caused a click as
  it was zeroed. The residual is now ramped to silence over a brief window first, so the
  incoming track always fades in cleanly.
- **`POI_Cleared` stinger could be buried under lingering combat.** Clearing a POI means you
  were just fighting, so combat contexts (generic and biome) are still on the stack during
  the brief eviction window. `POI_Cleared` priority moved from 55 to 91 so the one-shot
  stinger wins over all combat tiers, `Blood_Moon_Combat`, and `Low_Health_Panic` — only
  `Death` supersedes it.
- **Removed-file replay loop.** When a track failed to open because the file was genuinely
  gone, the resolver kept re-picking that same missing file every scan tick. Missing files
  are now pruned from the presence snapshot on the failed open, so the resolver moves on
  instead of retrying the dead path.

## 1.0.2b

### Changed

- **Music folder placeholders are now named after their context.** The drop-in tree's
  empty-folder placeholders (previously all named `.keep`) are now named for the context
  that feeds each folder — e.g. `Music/combat/.Combat_Low`,
  `Music/ambient/wasteland/_night/.Ambient_Wasteland_Night` — so a glance at a folder
  tells you which context's music goes there. They're still inert hidden files the audio
  scanner ignores, and you can delete one once you add your own tracks.

### Fixed

- **Day/night music flicker at the home/biome transition.** When a day↔night context
  swap happened (e.g. `Home` → `Home_Night`), the incoming context restarted its full
  warm-up dwell from zero, leaving a gap where neither variant was eligible and a
  lower-priority context (typically biome `Ambient`) would play for a few seconds before
  the night theme took over. Sibling contexts can now share the dwell so the swap stays
  viable continuously and no longer leaks the wrong track.

### Added

- **`warmgroup` context attribute.** Contexts that hand off to each other (like a
  day/night pair) can be tagged with a shared `warmgroup`. When one joins while a group
  sibling is still active, it inherits the group's oldest warm-up start instead of
  restarting its own — so the handoff doesn't drop out for the full warm-up. Each context
  still uses its own `warmup` value; keep group members' warmups equal for a predictable
  dwell. `Home` and `Home_Night` ship tagged `warmgroup="home"`.

## 1.0.2a

### Added

- **Biome-specific combat music.** Each combat tier now layers a biome variant on the
  stack alongside its generic tier whenever combat fires in a known biome — e.g.
  `Combat_High` + `Combat_High_Forest`. The variant outranks its generic counterpart
  (priority band 60–62, below `Blood_Moon_Combat`) so biome combat music wins when its
  folder has tracks, and falls back to the generic tier — the existing behavior — when
  the folder is empty. Only the one active tier ever gets a variant on the stack, so the
  band never inverts across tiers. `Blood_Moon_Combat` stays generic (no biome split).

- **Combat music tracks the biome you're fighting in.** The biome is latched when a fight
  engages and held for the whole continuous encounter, so crossing into another biome
  mid-fight does NOT swap the track. Unlike the combat *tier* (held/resumed through the
  cooldown), the biome deliberately re-locks each time combat actually re-engages after the
  music has stopped: leave a fight in the wasteland, re-enter in the desert, and the track
  switches to the desert variant while the tier still resumes at its held High/Boss level.

### Changed

- **Combat folder layout reorganized into three tiers.** The `Music/combat/` root folder
  is now the base (Low) tier directly, with `_high` and `_boss` subfolders for the
  escalations (replacing the old `combat/low`, `combat/high`, `combat/boss` folders). Each
  biome mirrors this under `combat/<biome>/` — e.g. `combat/forest` (Low),
  `combat/forest/_high`, `combat/forest/_boss`. The `_`-prefixed tier subfolders are never
  confused with the biome subfolders (folder scanning is top-level only), the same way
  `ambient/<biome>/_night` works. **Migration:** move existing `combat/low/*` tracks up to
  `combat/`, and `combat/high` → `combat/_high`, `combat/boss` → `combat/_boss`.

- **Combat threat tier thresholds accept fractional scores.** `minScore` is now documented
  as a float, and the default `Combat_Low` threshold dropped from `1` to `0.5` so a single
  low-weight enemy (e.g. a crawler at 0.6) still opens combat music.

- **Tension no longer flickers before combat.** Added a 2-second warm-up to the `Tension`
  context so a quick escalation from "enemy spotted" straight into a fight goes directly to
  combat music instead of briefly playing a tension sting first.

## 1.0.2

### Fixed

- **Panicked combat bridge could slam to the top tier.** When no tier is literally
  named `Combat_High`, the vanilla "Panicked" bridge now falls back to the
  second-from-top tier instead of the last one, so the loose Panicked signal can no
  longer fire the heaviest fight music (e.g. `Combat_Boss`) on its own.
- **`GetContext(null)` guard.** A null context key now short-circuits to `null`
  instead of being passed into the dictionary lookup.

### Changed

- **Lower per-tick allocations in `ResolveContext`.** The per-tick entity scan now
  reuses a single shared scratch `List<Entity>` (Clear + AddRange) instead of
  allocating a fresh snapshot list every tick. The "near home" land-claim check
  early-exits on the first in-range claim using a squared-distance compare instead of
  `OrderBy(...).First()` with a per-claim `sqrt`, and the POI sleeper-volume tallies
  are counted in one pass instead of four LINQ `Count()` enumerations.

### Removed

- **Dead Unity decoder / streaming fallback.** CMM originally relied on Unity's
  `UnityWebRequestMultimedia` decoder before the NAudio engine replaced it. The Unity
  path had been permanently disabled by a file-scoped `#define NAUDIO` on every audio
  source file, so every Unity `else` branch was unreachable. Removed all of it: the
  `UseNAudioDecoder` / `UnityStreamingEnabled` flags, the `GetUnityAudioType` helper and
  `_unityExts` set, the three Unity fallback branches (validate / load / preload), the
  duplicate `#else` `NAudioDecoder` class, and the entire `#define`/`#if NAUDIO`
  scaffolding (the live NAudio code is now unconditional). No behavior change — the
  Unity path could never run.

### Build

- **"Package All (Release zips)" VS Code task** that invokes the `PackageAll` MSBuild
  target to produce every version's release zip in one run.

## 1.0.1

First multi-version release. CMM now builds, deploys, and packages against
Alpha 20.7, Alpha 21.2, 2.6, and 3.0 from a single project, and a large batch of
audio-engine and context-detection fixes land alongside it.

### Added

- **Multi-version support (A20 / A21 / 2.6 / 3.0).** A single MSBuild `GameVersion`
  property now derives everything version-specific: game-assembly paths, the deploy
  Mods folder, the release zip name, and a `GV_*` compile symbol used to `#if`-guard
  per-version APIs.
  - `DeployAll` / `PackageAll` targets, plus a Debug fan-out so a plain Debug build
    deploys to every installed version's Mods folder at once.
  - `ValidateGameVersion` fails fast on an unknown/missing game install instead of
    emitting a wall of "type not found" errors.
- **A20 legacy ModInfo support.** A20 expects the nested `<xml><ModInfo>…</ModInfo></xml>`
  format; `ModInfo.A20.xml` is deployed in its place for the A20 target, with the
  version kept in sync from the authoritative `ModInfo.xml` at build time.
- **A20 `netstandard.dll` shim.** A20's Unity 2020.3 Mono ships no `netstandard.dll`,
  so the netstandard2.0 NAudio assemblies failed to load and Harmony never patched.
  The build now copies the shim into the A20 mod folder.
- **`Swimming` context.** Plays while the player is in deep enough water to swim, with
  an 8s dwell so brief dips don't interrupt the current track.
- **Generic `Combat` fallback tier.** The combat chain now degrades
  `Blood_Moon_Combat → Combat_Boss → Combat_High → Combat_Low → Combat → vanilla`,
  so missing custom folders fall through gracefully to vanilla combat music.
- **`.keep` placeholders** in every scaffolded Music context folder so mod managers
  (e.g. Vortex) don't strip the empty drop-in tree on install.
- **New `settings.xml` tuning knobs:** `EnemyDetectRange`, `HomeDetectRange`,
  `PlayerHealthDangerThresholdPercent`, `CombatExitDelay`, `CombatCooldown`
  (previously hard-coded constants).

### Fixed

- **A20 combat music rarely played / went silent.** A20's `FrequencyLimiter` rations
  *all* sections — including Combat — by the "Dynamic Music daily time" setting, so on
  a reduced daily-time setting vanilla combat music only started a handful of times per
  session. Backported A21's rule (exempt Combat from the limiter) via a Harmony
  patch, so when CMM falls back to vanilla, combat music now fires reliably.
- **FLAC crash on A21.** Replaced `BunLabs.NAudio.Flac` (uses `Span<T>`, hard-crashes
  the Mono 2021.3.19 JIT on A21) with `CSCore.Codecs.FLAC`, wrapped in a new
  `CsCoreFlacReader`. FLAC now works across all supported versions.
- **PCM playback crash on A21.** NAudio's `SampleChannel` / `Pcm16BitToSampleProvider`
  triggers an uncatchable Mono JIT crash on A21. Replaced with a self-contained
  `PcmToFloatProvider` covering 8/16/24/32-bit PCM. Unsupported encodings are now
  skipped with a warning instead of crashing the game.
- **Opus/streaming resume restarted at the top (A20).** Resume position is now derived
  from the backing reader's consumed-frame count rather than `AudioSource.time`, which
  A20's Unity reports as ~0 for streaming clips. The playhead is also pinned after
  `Play()` to defeat a stale offset carried over from an interrupted crossfade.
- **Opus seek to the first page caused silent EOS.** A seek at/before the first audio
  page now reads from the top instead of routing through the mid-file PrependStream
  path (which produced an immediately-ended stream and killed playback).
- **Vanilla blood-moon state lost on a single-player host.** The in-game music patch
  now mutes vanilla Dynamic Music *at the mixer* instead of suppressing
  `Conductor.Update`, preserving the server-side blood-moon work that ran there — and
  letting the "Vanilla" fallback fade in seamlessly mid-track.
- **Menu music bleed / cold-start.** Mute the vanilla title theme at
  `BackgroundMusicMono.Start` (the earliest point), and fade out any leftover vanilla
  title theme when a custom menu track takes over after a late A20 load.
- **Valid tracks getting permanently blacklisted.** A file that fails to open is only
  blacklisted if it actually exists on disk (corrupt/unsupported). A file still being
  copied in (locked/incomplete) is treated as transient, and the file watcher now
  re-validates blacklisted files periodically so they recover once fully written.
- **Spurious custom combat triggers.** The vanilla "Panicked" bridge — which uses a
  wider 50u/any-player box than CMM's in-range scan — is now used only when CMM ships
  no combat music of its own, so it can't fire custom combat tracks when nothing is
  actually nearby.

### Changed

- **Combat is now scored by the weighted threat model** with `Combat_Boss` requiring a
  higher score (minScore 12 → 18) so Boss is reserved for genuinely heavy fights.
- **Merged `Tension_High` / `Tension_Low` into a single `Tension` context.**
- **Removed the standalone `Blood_Moon` and `Inventory` contexts.** Blood-moon combat
  is handled by `Blood_Moon_Combat`.
- **Music folder layout reorganized** for a flatter, more intuitive tree:
  `combat_*` → `combat/*`, `tension_*` → `tension`, `ambient/biome/<x>` →
  `ambient/<x>`, biome night folders → `ambient/<x>/_night`, `ambient/night` →
  `ambient/_night`.
- **Night ambient fallback** now reaches a biome's *own* day theme before the generic
  Ambient: the night context falls back through the stack, where the resolver also
  places the biome's day context at night.
- **Cross-version detection** for blood moon (`player.IsBloodMoon` on A20/A21 vs
  `World.isEventBloodMoon` on 2.6/3.0), trader resolution (entity scan pre-3.0 vs
  `TraderArea.GetTrader()` on 3.0), and POI name (falls back to the prefab instance
  name where `PrefabName` is empty on A21).
- **Debug logging is off by default** (`DebugLogging=false`), and most verbose logging
  is routed through the debug-gated logger. Warnings/errors still always emit.
- Debug builds deploy straight into the selected game's Mods folder; Release stages to
  a clean per-version folder and packages a `dist\` zip (never touching the live game
  or music directories).

### Removed

- **`Config/Localization.txt`** — leftover template junk (crosshair/weapon keys) from
  another mod that 7DTD would auto-load.
- **`app.config`** — binding redirects from a mod DLL's `.config` are ignored by the
  game's host process, so it served no purpose; `AutoGenerateBindingRedirects` is now off.
