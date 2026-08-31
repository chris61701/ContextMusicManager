# Changelog

All notable changes to Context Music Manager are documented here.

## 1.0.5a — unreleased

Revisions since 1.0.5 shipped. ModInfo carries `1.0.5.1`, because the game runs
`Version.TryParse` over that field and warns in every user's log for anything non-numeric —
1.0.2a, 1.0.2b, 1.0.3a and the RC builds all tripped it. The letter is the name people read;
the fourth component is the number the game reads.

### Fixed

- **A track that will not decode now says so**. A file that is present and readable
  but that nothing can decode was recorded as unplayable and skipped from then on — correct
  behaviour, and completely silent. Every other instrument agreed with the silence: the file is
  on disk, so the coverage panel counted it; the playlist entry resolved, so the missing-entry
  report said nothing; and the station had other tracks, so the "no playable tracks" warning
  never fired. The track simply never played, and there was no way to find out. Found by probing
  a real 3,381-track library through the shipping decoder: 25 of them are Apple Lossless inside
  an `.m4a` container, which is not a supported format. Each such file now gets one warning
  naming it, capped at twelve before it summarises, and a drive that has merely gone away still
  says nothing — that distinction already existed and is what this is built on.
- **The crafting diagnostic worked everywhere except Alpha 20**, which is the one
  version it was written for. It read a recipe cache that A20 does not have, three lines below a
  comment explaining that A20 does not have it. The recipes themselves were always installed
  correctly; what was lost was the report that tells "the recipe is installed" apart from "the
  player can see it", plus a spurious retry for something that had not failed.

## 1.0.5 — released 2026-08-31

Shipped as **RC4**, built 2026-08-30. The candidates before it — RC1 (2026-08-23),
RC2 (2026-08-26), RC3 (2026-08-27) — were testing builds only; the *(RC3)* / *(RC4)* tags below
say which one an item first appeared in, and every one of them is in the release.

Everything below is measured against **1.0.4b**. Several large features arrive whole in this
release — world speakers, the personal radio, music packs, the overlay — and are described as
they now behave, rather than as a history of how they got there.

### Added

- **A coverage panel** (`Alt+Numpad8`, cycling off → everything → gaps only → off). One row per
  context: whether it has music of its own, how many tracks, and — when it has none — the
  fallback chain it walks, with the context that will actually be heard marked at the end of it.
  Radio stations are listed individually, since a station with no tracks is dead air rather than
  something a fallback covers. Everything is read live from disk, so editing `contexts.xml` or
  `context.lua`, dropping files into a folder or deleting a station all show up within a second
  without reopening the panel — and it works on the main menu, before a world is loaded.
- **A centre-screen announcement** when combat/tension music is muted or unmuted. That toggle
  changes what the game will play at all, so it no longer reports itself in the same corner box
  used for small confirmations. Wide text across the middle, held briefly and then faded out;
  both timings are settings (`BannerHoldSeconds`, `BannerFadeSeconds`).
- **7 Days to Die 3.2.0 b9 support.** The mod compiles and ships against 3.2 with no source
  changes — nothing in the game API it touches moved between 3.1 and 3.2.
- **The Context HUD now ships.** An on-screen readout of what is playing and *why* — press
  Alt+Numpad9 to cycle it hidden -> compact -> expanded. It lists the context you are
  hearing, the context tokens the decision script resolved (in red where a context has no
  music), the fallback chain actually walked to reach the playing track, the file and folder
  it came from, and how many tracks are staged and loudness-measured. Expanded adds whether
  the current track started already normalized and the next-track readiness of every other
  context. It answers "why is this playing?" without reading a log, and it is purely a
  readout — it never influences playback. Previously it existed only in development builds
  and was stripped from every release. Both keys are rebindable (`KeyContextHud`,
  `KeyContextHudReset`).

- **Music packs can ship as their own mods.** CMM now reads its music from your `MusicPath`
  folder AND the `Music` folder of every installed mod, treating all of them as one folder
  per context. A soundtrack author can publish a pack as an ordinary mod instead of asking
  people to copy files over CMM's install — so it survives updating CMM, and it works with
  MO2, Vortex and Wabbajack like any other mod. Packs mirror the same tree: a pack supplying
  POI_T3 ships `Music/poi/t3/`, and a folder matching no context is simply ignored. Several
  packs can feed the same context; identically named files from different packs are all kept
  (they are different tracks). Radio stations with the same folder name merge into one
  station holding both pools. Adding, removing or editing files in a pack is picked up within
  about two seconds, exactly like your own folder, and a pack whose folder disappears simply
  stops contributing.
  - `cmm roots` lists which folders are in use and which mod each came from;
    `cmm roots <context>` breaks down what each folder contributes to one context, which is
    the fastest way to answer "why isn't my pack playing".
  - `cmm scan` looks for music folders again — for a pack added while the game is running.
    It finds packs the game itself has not loaded, too: a music pack is data, so there is no
    reason to make you restart for one. (Any *code* such a mod carries still needs a restart.)
  - Add-on folders are always read flat. `subfolders="true"` applies only to the folder
    `MusicPath` points at, so a pack cannot accidentally pull in a nested context's music.
  - Playlist (`.m3u`) and absolute-path sources name one exact place and are never extended
    by packs.
  - Off with `<AddonMusicScanEnabled>false</AddonMusicScanEnabled>`, which restricts CMM to
    your own folder.

- **Music through in-world speakers.** Radios, jukeboxes and speaker blocks in the world now
  play your Radio music where they physically stand, so a POI you are clearing has music
  coming out of the room rather than out of your head. Several stations sound at once, and
  every speaker broadcasting the same station is heard as one source in the right place.
  - **On 2.6 as well as 3.x.** The capability check required a composite tile-entity door —
    a type that only exists from 3.0 — as a belt-and-braces test, which is a check for DOORS
    standing in for a whole subsystem: every speaker on 2.6 was silent because that game
    models doors differently. Doors are recorded as a capability now rather than required, so
    2.6 gets stations, tuning, the acoustic field, reverb, the tuner menu and the craftable
    blocks. What it does not get is doors modulating what you hear as they swing — 2.6 has no
    swing state to read, so a shut door muffles the moment it shuts rather than across the
    animation. Alpha 20 and 21 have the speaker blocks but not the world APIs the layer needs,
    so the feature stays off there and the personal radio covers them instead.
  - **A real acoustic model, not a distance fade.** One acoustic field is flooded out from
    the listener several times a second and read by every speaker, so sound routes through
    doorways and around corners instead of through walls. Because the field is anchored to
    you rather than to each source, a speaker that moves costs nothing extra. A door opening
    or a wall breaking is simply in the next field, so there is no stale state to go wrong.
    Reverb around a corner is graded by how much energy survives the bounces — a stone cave
    carries it corner after corner, an absorptive wood room kills it quickly.
  - **Speakers sound like speakers.** `WorldEmitterColorVoice` (0.5 by default) squeezes the
    sound into a small driver in a small box: band-limited hard at both ends and given the
    boxy resonance a cheap cone actually has, rather than just being made quieter and duller.
    `ColorDrive` adds the crunch of one pushed too hard, and `ColorStatic` a bed of hiss.
    Ham radios carry their own set of the three, voiced harder (0.9), because a
    communications speaker is smaller and nastier than a shelf unit.
  - **They follow the Ambient slider**, together with Overall. A radio playing out of a block
    is an environmental sound, so turning the *music* down no longer silences the radios in a
    POI. `WorldEmitterVolume` is their own trim. Ambient defaults to 100% where Music defaults
    to 60%, so set it around `0.3` if you want them nearer the level music sits at.
  - Audible out to `WorldEmitterRange` (60 blocks). A speaker you place becomes audible within
    about a second, fading in over `WorldEmitterBirthFade`.
  - `cmm acoustics`, `cmm field` and `cmm emitters` show and tune the model while you listen.

- **Personal radio.** `Alt+PgUp` / `Alt+PgDn` step through Off → station → station → … → Off,
  wrapping both ways, so two keys give you on, off, next and previous. It plays the same
  world-clock broadcast the world speakers do — non-spatial, in your head — and replaces the
  normal context music while it is on. It works on every supported game version, including the
  alphas that have no speaker blocks at all, and your station is remembered between sessions.
  - **Subfolders of the Radio source are stations.** Each is its own pool, and everyone on a
    server hears the same song at the same moment: schedules are anchored to the shared world
    clock, so tuning in — or walking up to a speaker — joins a broadcast already in progress.
    Stations start instantly whatever the size of the pool, and adding, removing or renaming a
    station folder is picked up live.
  - **Changing station sounds like tuning a radio.** The station you are leaving sinks under
    rising static rather than being cut off, the dial passes through noise, and the new
    station arrives band-limited and distorted before opening out into full fidelity. The
    sound changes shape as it arrives, instead of merely fading up under a layer of hiss.
    Every knob lives in `radio.xml` and hot-reloads.
  - **The dial has recorded sounds**, in the mod's new `Sounds/` folder — `radio_dial.wav`
    for a station change and `radio_power.wav` for the power switch. Swap them for anything
    you like (mono or stereo, 8/16/24/32-bit PCM or float WAV). Every firing plays at a
    slightly different rate, so spinning the dial sounds like a mechanism rather than one
    recording retriggered eight times. Synthesized clicks remain as the fallback if a file is
    missing or unreadable: a sound file can quieten the radio, never break it. `cmm sfx` shows
    what loaded, and `cmm sfx test spin` fires eight detents in a row.
  - Death, spawn and POI-cleared stingers interrupt the radio rather than being swallowed by
    it, and it stays silent from death until you have respawned.

- **Now Playing overlay.** A small top-left readout of the track and context, toggled with
  `Alt+Keypad7` and remembered via `NowPlayingEnabled`. It hides during a single-player pause
  and while the F1 console is open — both cover the same corner — and returns exactly as it
  was.

- **Ban, favourite and combat-music keybinds.**
  - `Alt+Delete` bans the playing track so it is never picked again; `Alt+End` undoes the last
    ban (25 deep, per session). Bans persist.
  - `Alt+Insert` marks a favourite, which is then played more often. `FavouriteWeight`
    (3 by default) is scaled to the size of the folder, so it means roughly the same thing —
    about once an hour of play — whether the folder holds fifty tracks or five thousand,
    instead of becoming meaningless in exactly the large libraries it matters most in.
  - `Alt+Home` cycles fight music through three stops: **combat and tension off**, then
    **combat off with tension back on**, then **both on**. Tension is combat music that has
    not started yet — the stalking cue the game plays when it thinks a fight is coming — so
    silencing fights while it keeps running only moves the noise, which is why one key covers
    both. While muted, those contexts resolve through their fallback chains as if their
    folders were empty, and the game's own linked sections are held back too, so a fight keeps
    whatever was already playing. The choice is saved and survives a restart.

- **Per-context play history for Next/Prev.** The skip keys walk back and forward through what
  you actually heard in that context, like a music player, instead of always rolling a fresh
  random track.

- **Playlist sources.** A context's `source` may name an `.m3u`/`.m3u8` file instead of a
  folder, so one playlist can draw tracks from anywhere on disk without moving or copying
  them. Relative and absolute entries both work, and the playlist is re-read when it changes.

- **Per-section switches for vanilla Dynamic Music.** `DisabledVanillaSections` turns off
  individual sections of the game's own music system (Exploration, Suspense, Combat,
  Bloodmoon, Trader…) rather than all-or-nothing, so you can keep the parts you like and let
  CMM cover the rest.

- **Richer game-state snapshot for `context.lua`.** The script sees considerably more of the
  world — POI tier and infestation, trader identity, vehicle, weather, biome, threat detail —
  so custom resolution policies can key off things that were previously not exposed. The
  documented API in `Config/context.api.md` covers the additions.
- **Every dead station sits on its own note** *(RC4)*. A station whose folder holds nothing
  playable no longer parks on a single 440 Hz test tone. It is a minor triad — 440 / 523.25 /
  659.25 Hz, each one a setting — and the new `WorldEmitterOffAirVarianceHz` moves the root by up
  to that many Hz either way, seeded from the station's NAME. So a dead channel keeps the same
  frequency every time you tune to it, on every client and across restarts: something you can
  learn rather than a pitch that moves under you. The other two notes transpose with the root, so
  whatever interval you set is the interval you hear at any pitch. `0` puts every off-air station
  back on the same note.

### Changed

- **The off-air tone is quieter by default** *(RC4)*. `WorldEmitterOffAirLevel` drops from 0.15 to
  0.04. It was set while an occlusion bug was quietly attenuating everything a speaker emitted,
  and once that was fixed the carrier sat far louder than a dead channel should — a tone is
  something you notice walking past a speaker, not something you sit and listen to. A value you
  chose yourself is kept.
- **The static from world speakers is half as loud** *(RC4)*. It was set against the hiss from
  the radio in your pocket, which reaches your ears through a different path entirely — a world
  speaker's noise arrives before distance, occlusion and the room are applied, so the same number
  is far hotter out of a shelf speaker across a room. `WorldEmitterStaticTrim` drops from 2.0 to
  1.0. The personal radio is untouched: it runs through its own receiver on its own setting.
- **World speakers default to half volume** *(RC3)*. They were set to full to compensate for an
  occlusion bug that has since been fixed twice over, so the compensation was being counted
  twice and they were too loud. If you had already chosen your own value it is kept
  (`WorldEmitterVolume`).

- **Debug keybinds moved** to make room for the coverage panel: `Alt+Numpad8` is now coverage,
  the context HUD moves to `Alt+Numpad9`, and its GC re-arm to `Alt+Numpad6`. Existing settings
  files are migrated. Both panels now read their keys from your configuration and show them in
  their own header, so a rebind is reflected on screen instead of the header naming stale keys.
- **`WorldEmitterFieldDetourDb` stays at 1.5** *(RC3 — RC2 shipped this as 3 and it was wrong)*.
  This is the cost of sound taking the long way around rather than going through a wall. At 3, a
  single right-angle corner cost 29 dB against a real single-edge diffraction loss of 10–15 dB,
  and sound stopped travelling far enough through a building. The reasoning that raised it
  compared a twenty-block detour against a single-edge loss, which are not the same measurement.
  Existing configs on any older default are migrated; a value you set yourself is left alone.
- **A big library now sounds like a big library: every track plays before any track repeats.**
  `RandomNoRepeat` — the default track order — has always been weaker than its name suggests.
  It excluded exactly one track, the one just playing, so on a 200-track folder a song could
  come back within a handful of picks; measured over 400 runs, the old behaviour repeated
  something after an average of 18 tracks, and as soon as 3. Now each context deals from a
  shuffled bag: every eligible track gets a turn before any track gets a second one, and the
  folder reshuffles once it runs out. Hearing the same song twice in an evening is far more
  noticeable than any amount of variety is pleasing, which is the whole reason to have a
  large soundtrack in the first place.
  - Counted **per context** and **per session**. Each context works through its own folder, and
    the pass starts fresh each launch — the first track of a session is drawn from everything
    either way, so nothing is gained by remembering it across restarts.
  - **Adding music takes effect immediately** — new tracks are eligible the moment they are
    seen and get their turn in the pass already running. Removing music does not disturb it.
  - **Favourites still mean something.** A strict bag would quietly flatten the favourite
    keybind to one play per pass like everything else, so a favourite gets `FavouriteWeight`
    turns per pass instead of one. Banned tracks are skipped without stalling the pass.
  - **Resuming a context, or walking back through Prev/Next, costs nothing.** Only a genuinely
    new track spends a turn, so re-entering a context does not eat into the pass.
  - Tunable with `<TrackNoRepeatMemory>`: `-1` (default) is the whole folder; `0` restores the
    old one-track behaviour exactly; any other number remembers that many recent tracks.
    Applies to `RandomNoRepeat` only — `Random` and `Sequential` are unchanged.
  - `cmm rotation` shows how far each context is through its pass and how many tracks are
    still to come; `cmm rotation reset` starts them over.

- **The game's audio sliders now work on CMM's music.** CMM's audio leaves through its own
  output device rather than Unity's, which meant the **Overall Volume** slider — implemented by
  the game as a Unity-wide setting — never reached any of it. Turning the master to zero
  silenced the whole game *except* this mod. Overall is now applied to everything CMM emits:
  context music, menu music, the personal radio and the radio dial sounds. It multiplies with
  the music slider exactly as the game's own stages do, so Overall 70% with Music 20% lands at
  14% — matching vanilla to well under a tenth of a decibel. Slider changes still take effect
  within about a tenth of a second, and now ramp instead of stepping, so dragging a slider no
  longer clicks.

- **CMM writes its own config files instead of shipping them.** `settings.xml`, `radio.xml`
  and `contexts.xml` are no longer in the download; they are created on first run with the
  documented defaults, exactly as before. The point is what happens on the NEXT update:
  previously the archive overwrote them, so every setting you had chosen — and every context
  you had added or retuned — was silently reset. Now your files are yours. Updating writes in
  only the settings a new version has ADDED, appended at the end under a banner saying which
  version added them, and leaves everything else untouched.
  - When a DEFAULT changes between versions, the new value is applied only if you were still
    on the old default. If you had changed that setting, your value stands. Comparison is on
    the parsed value, so `0.50` and `0.5` are recognised as the same untouched setting.
  - A setting you delete stays deleted, and goes back to following CMM's built-in default.
  - Three settings that had always existed in code but were never written into the file are
    now documented there: `ReverseFadeReclaim`, `NativeScheduler` and `NativePreStageAdvance`.
  - Files are only ever written at startup, never while you have them open for editing.
  - Radio and world-speaker settings live in their own `Config/radio.xml` rather than swelling
    `settings.xml` to three times its length. Both are read as one chain and a setting found
    in `settings.xml` still wins, so nothing you have tuned is lost.

- **The Music folder tree is created by the mod, not by the installer.** The per-context
  folders (with a `.ContextName` placeholder naming each one) are now created at launch under
  whatever `MusicPath` points at, rather than being baked into the archive. They therefore
  appear where your music actually lives instead of only inside the mod folder, and contexts
  you add yourself get folders too. Strictly additive: it never writes into a folder that
  already exists, never deletes, and skips the job entirely if `MusicPath` points at a drive
  that is not currently attached.

- **An infested POI now carries its ordinary tier music underneath it.** `POI_T1`–`T5` used to
  be excluded outright while sleepers were alive, so the infested tier was the only POI music
  on the stack; if its folder was empty there was nothing POI-shaped left to fall back to. Both
  are now active together — the infested tier on top, the plain tier beneath it — so an empty
  `poi/infested/t3` drops to the generic infested bed and then, failing that, to `poi/t3`. The
  two also share the same warm-up dwell now, so the plain tier no longer becomes eligible
  first and gets audibly displaced a moment later by the infested one covering the same POI.

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

- **CMM's keys go inert while the F1 console is open.** Scrolling the console is
  PageUp/PageDown and numpad digits are ordinary typing, so a CMM binding firing underneath a
  command you were writing could change your music without you asking. Everything returns
  exactly as it was when the console closes.

### Fixed

- **Tuning a speaker to a dead station gave static, a pause, then more static** *(RC4)*. Two
  things were raising the off-air carrier: the tone's own upkeep, which works out every tick who
  is listening to a station that cannot play, and a second, older trigger that fired the moment a
  speaker with a dead pin was seen. While a speaker is mid-tune it belongs to neither, so the
  second trigger claimed a lane and started hissing, the first found nobody to serve and tore it
  straight back down, and the pair repeated every tick for the length of the sweep — 42 of each in
  one dial turn on the bench, ten a second in a game log. The tone now has one way to be raised.
  Two related delays went with it: the speaker rejoins the group as soon as its sweep starts
  finishing rather than after it has completely finished, and a sweep towards a station already
  known not to open no longer waits out its full four-second deadline first. A dial turn onto a
  broken station is now one unbroken hiss that settles onto the tone, about two and a half seconds
  from the press.
- **Removing a station's music left it playing anyway** *(RC4)*. Repairing a broken station by
  dropping a playlist into it works, and taking that playlist away again did not: the song carried
  on to its end. Nothing could see it. The file was still on disk, so the vanished-file check did
  not fire; the station still existed, because its folder was still there; and the only trace was
  a `paper UNRESOLVED` note beside the voice in the log every few seconds. A playing track is now
  checked against the station it belongs to after any change on disk, and stopped if the station
  no longer carries it — after which the speaker gets the off-air tone, like any other station
  that cannot play.
- **World speakers went quiet in the middle of a station change** *(RC4)*. Changing station on a
  speaker is meant to be a crossover: the static covers the change while the new station rises
  under it. It only ever had one side. The speaker was held out of every station until the
  handover had completely finished, so the old sound faded away over a full second with nothing
  fading in beneath it, and the new one appeared at full a moment later. Measured on the bench as
  a 180 ms hole with a level step at each end, while the station's own audio sat there at a
  steady level the whole time. Both sides now cross over the same span, and because both carry
  the same decode the sound simply moves across rather than fading out and back in.
- **Ticking and popping when you turned your head near a speaker** *(RC4)*. A speaker's position
  in the stereo field depends on which way you are facing, and that was recalculated once per
  audio block and applied as a single value across it — so turning quickly moved it a whole
  block's worth in one sample, over and over, which is heard as ticking. It now slides across the
  block instead. Nothing is delayed by this: the turn still lands in the same block, the change is
  just spread across it rather than sitting on its edge.
- **Static formed over the off-air tone and stayed there** *(RC4)*. The receiver keeps a faint bed
  of noise even when locked on, because falling to absolute silence sounds like a sound effect
  ending rather than like a receiver. That is right for a station and wrong for a speaker sitting
  on the off-air tone, which has nothing else on it. The hiss now clears as the tone comes up, so
  the sequence finishes the way a real set does: hiss while it hunts, tone rising underneath, then
  the tone alone.
- **Long tracks started at the wrong volume, and stayed there** *(RC4)*. A track's loudness can
  only be worked out by reading the whole file, so a 37-minute podcast took about nine seconds
  before the mod knew anything about it — three times longer than it is willing to wait — and it
  played at a guess for the whole of that. On the podcast stations that guess is around 12 dB
  quiet. Long files are now measured in five-minute pieces: a usable level arrives about a second
  in and is refined as the rest is read, easing rather than stepping. Anything shorter than five
  minutes is unaffected. Measured across 27 real files, the first answer was within 0.82 dB of the
  final one, worst case.
- **Death and spawn stingers were delayed by up to three seconds** *(RC4)*. Very short audio
  cannot be loudness-measured at all — the standard needs 400 ms of sound to work with — so the
  mod waited for a measurement that could never arrive, every single time. You died, then three
  seconds of nothing, then the sting. Stingers now never wait, and a file too short to measure is
  recorded as such once instead of being re-read on every play.
- **Replacing a music file kept the old file's volume** *(RC4)*. Swap a track for a different
  version under the same filename — a re-encode, or a real track over a placeholder — and the new
  one played at whatever the old one needed. It cleared on restart, unless you had adjusted that
  track's volume yourself, in which case it was permanent.
- **`Cache/loudness.xml` grew far larger than intended** *(RC4)*. It was meant to hold only tracks
  you had adjusted, banned or favourited, and was instead accumulating an entry for every track
  played.
- **World speakers could go silent for the rest of the session** *(RC4)*. Each time the station
  list rebuilt, a speaker that had claimed one of the three audio slots without having started
  playing yet never gave it back. Three of those and no speaker in the world played again, with
  nothing in the log to say why.
- **Starting a world speaker froze the game briefly** *(RC4)*. Opening the file and skipping to
  the right point in the broadcast ran on the main thread — measured at 403 ms for a station
  joining ten minutes into a podcast, and the worst stalls in a session were all speaker starts.
  It now happens in the background. Measured after: the same work costs under 9 ms.
- **Re-encoding a radio track put the station out of step** *(RC4)*. Track lengths were remembered
  by filename and never rechecked, so a track replaced with a different-length version left the
  station starting songs part way through — and, on a server, players hearing different songs on
  the same station. Lengths are now rechecked, and the station corrects itself within the hour.
- **A briefly disconnected drive could take a station off the air permanently** *(RC4)*, and could
  leave a music context looking empty for the rest of the session. A drive that is not there is
  now told apart from a track that is genuinely gone, so both come back on their own when the
  drive does.
- **Music could stop after a track ended** *(RC4)*, until you walked into a different context.
  If the next track had been deleted, moved or was otherwise unreadable, the mod gave up instead
  of picking another. It now moves on, and remembers not to try that file again.
- **A track that could not be read was retried once a second, forever** *(RC4)* — a full re-read
  of the file each time. It now backs off.
- **Playlists quietly losing tracks are now visible** *(RC4)*. If entries in an `.m3u` point at
  files that have moved, those tracks were dropped in silence unless *every* entry failed. The
  coverage panel (`Alt+Numpad8`) now shows a station short of its playlist as, for example,
  `9   of 12 — 3 missing`, counts them in its summary, and lists them in gaps-only mode.
- **A part-read file could set a wrong volume permanently** *(RC4)*. If a drive dropped out
  mid-scan, the fraction that had been read was recorded as though it were the whole track.
- **FLAC files with an ID3 tag would not play at all** *(RC3)*. A FLAC is supposed to begin with
  a `fLaC` marker, but plenty of tagging programs write an ID3 tag in front of one anyway. Those
  files play everywhere else and were silently skipped here — never played, never loudness-
  measured, with nothing to tell you why. In one 9,839-file library 146 of them were affected,
  and because tagging tools work an album at a time they arrive in whole albums. The tag is now
  stepped over.
- **Speakers stopped being detected, sometimes for the rest of the session** *(RC3)*. Placing more
  speakers did not help and only restarting the game fixed it. The mod looked up the game's
  internal ids for speaker blocks once per session, but the game reassigns those ids every time a
  world loads — so on the second world of a session they could point at the wrong block, or at
  nothing, and every speaker in the world went silent at once. Ids are now re-read per world.
- **Leaving a world kept its acoustics in memory** *(RC3)*. The per-speaker sound maps were never
  released when you returned to the menu — up to ~43 MB held indefinitely — and a speaker at the
  same coordinates in a different world could be handed the old world's map.
- **The radio no longer loses your place** *(RC3)*. Switching the radio on took over the music
  layer without noting where the track had got to, so switching it off restarted the context on a
  different track from the beginning. It now resumes exactly where you left it, however long you
  listened — a broadcast no longer counts as time away from the context you are standing in.
- **Music that came and went was cut off before it had finished arriving** *(RC3)*. Contexts
  driven by things that vanish in seconds — enemies, nearby players, POI state — had 3–5 second
  fade-ins, but a context is dropped 1.5 seconds after its trigger disappears, so a flicker
  cancelled the fade about a third of the way up and you never really heard it. Those contexts
  now fade in inside that window; music tied to where you are or what time it is keeps its slow
  fade.
- **A misleading "none decodable" warning** *(RC3)*. A station that was simply still preparing
  reported that its files could not be decoded and told you to check their format. It now says
  that only when the whole station really is unplayable.

- **The radio kept playing after you switched it off** *(RC3)*. Changing station deliberately does
  not fade the station you are leaving — it plays on, buried under the tuning static, until the
  next station's crossfade retires it. But the release forgot the voice, so if you dialled fast
  enough that no station ever landed and then rolled onto Off, nothing was left to retire it —
  and switching off disarms the receiver, which removes the static that was hiding it. The
  station was revealed at close to full level and played its whole track with the radio showing
  Off. A released voice is now remembered and retired on every "the radio is done" path.
- **A station could be added after the radio had already been switched off** *(RC3)*. The guard
  before the audio hand-off checked whether the dial had moved but not whether the radio was
  still on, leaving a window where a station that was still opening landed anyway.
- **Two station loads could run at once and one of them was lost** *(RC3)*. The hand-off between
  the loading thread and the game is a single slot, and a ten-second safety net allows a second
  load to start when the first one is slow — a large FLAC on a network drive is enough. The
  second overwrote the first, so the first station's audio was never accounted for and played on
  unattended. It is now silenced rather than dropped.
- **Radio music was quietly distorted** *(RC3)*. The receiver's drive stage never fully switched
  off: at full signal it still ran the audio through a soft clipper rather than passing it
  through, so every fully-tuned station carried 2–7% distortion depending on how hot the track
  was. It happened before the volume control, so turning the volume down did not remove it.
- **Tracks whose loudness had not been measured yet played at full level** *(RC3)*. "Unmeasured"
  was treated as "already at the right level", which for a modern master is 8–12 dB too loud.
  They now start at a conservative level and settle to the measured one the moment it lands
  (`LoudnessUnmeasuredDb`, 0 restores the old behaviour).

- **Occlusion changes arrived late, and by a different amount on every door.** The smoothing at
  the very bottom of the audio chain used a coefficient applied per audio buffer rather than per
  second, so its real time constant was ~330 ms on a 10 ms audio device and ~660 ms on a 20 ms
  one — nobody had chosen either number, they fell out of the buffer size. It was also
  *adaptive*: it sped up only for large jumps, so it ran three to seven times faster on a closing
  door than an opening one. The result was a lag that varied with the door, with how fast it
  swung, and with where you were standing, which no fixed compensation could ever cancel. It is
  now one time constant in seconds (`WorldEmitterOcclusionSlewMs`, default 50), the same for
  every door and every listening position, and the door model's lead is set to the measured
  pipeline delay rather than a guess at it.
- **Door timing was rebuilt around what a doorway actually opens.** A door's sound now follows
  the measured open area of its doorway in square metres rather than a fraction of its animation,
  so a garage door and a closet door behave consistently without a per-door table. The aperture is
  measured once at the shut pose, and a shut door reads as sealed by construction rather than by a
  tolerance.
- **The muffle arrived after the level.** Tone and level are now slewed on the same time constant,
  so an occlusion change lands as one event instead of two.
- **Installing the mod on a dedicated server no longer freezes joining players.** CMM is
  client-side only, but server admins sometimes install it server-side anyway — and the full
  client stack (patches, coroutines, audio init) used to start up on the headless server,
  with one hook sitting directly on the server's world-load path where any hiccup stalled
  world readiness and hung every joining client at the loading screen. The mod now detects a
  dedicated server at init and stays completely inert there (no patches, no threads, no
  audio), logging one clear line telling the admin it's safe to leave installed. Three more
  layers make the CLIENT side of a multiplayer join bulletproof regardless of the server:
  - Every Harmony patch body is now exception-contained: a CMM failure inside a vanilla
    lifecycle method (spawn, world load, save/teardown) can no longer abort that method — the
    game always completes the spawn, CMM logs the suppressed error (rate-limited) and
    recovers on its next scan tick.
  - The world polls that run at spawn now tolerate the partial game state a multiplayer
    client sees mid-join: POI and trader lookups survive the game's own unguarded
    chunk-provider dereference on unstreamed chunks, the sleepers-cleared quest hook retries
    instead of throwing, and a resolver failure yields "no contexts" instead of killing the
    10 Hz scan loop for the session.
  - The native mixer no longer opens or closes the audio device while holding its global
    lock: the lazy device open used to run under the lock on the first spawn-sting start, so
    a wedged Windows audio endpoint froze the game's main thread at exactly the spawn moment.
    Device open/close now lives solely on the mixer's own producer thread, outside the lock;
    a stalled audio driver just delays music, never the game.

- **Music no longer starts at the wrong volume and slides into place a second later.** A track
  whose loudness had not been measured yet started uncorrected and had its real level eased in
  later, which on a quiet file needing the full boost is an audible climb a second into the
  song. Playback now waits, silently, for the measurement — up to `LoudnessStartHoldMs`
  (3 s), after which it starts anyway rather than leaving you in silence. With
  `LoudnessNormalizationEnabled` off there is nothing to wait for and the hold is skipped.

- **Loudness measurements are kept between sessions again.** They had stopped persisting, so
  the track resuming at world entry re-measured every launch. The cache holds the
  recently-played working set, so the file stays small; manual volume trims were never
  dropped and still aren't.

- **Live edits went unnoticed on exFAT, FAT, SD cards and many network shares.** Those
  filesystems don't update a folder's timestamp when files inside it change, so CMM's change
  detection never fired: adding, removing or renaming tracks went unnoticed until a restart.
  All three watchers now use a listing-based signature on those filesystems, so live edits
  hot-load there just like on NTFS. NTFS keeps the cheaper timestamp fast path.

- **Changed settings now actually reach an existing config file.** The migration that updates
  a setting you never touched only recognised **one** previous default, so changing the same
  default twice left anyone who had skipped the version in between stranded on a value the
  rule no longer recognised — permanently, since it read as a deliberate override. Every
  former default a setting has ever had is now recognised.

- **Config files open correctly in text editors.** `settings.xml`, `radio.xml` and
  `contexts.xml` are UTF-8 and always were, but they were written without a byte-order mark,
  so Windows editors guessed Windows-1252 and every accented character, arrow and rule in the
  comments appeared as mojibake — a correct file that read as a corrupted one. The mark is now
  written, and an existing file missing it is rewritten once to gain it.

- **A hint of the main-menu theme could be heard while paused.** The game deliberately fades
  the title theme in over the single-player pause screen, and while CMM's vanilla-music
  fallback was active it slipped through the pause silence. The menu music now stays muted
  through every pause, and quitting to the menu hands menu music back through its proper
  fade-in (it could previously skip that handoff entirely after an in-game vanilla fallback).

- **A track ending during a pause unmuted everything.** The pause silence is a device-wide
  gain duck, and starting any new audio revived that gain (a mechanism menu transitions rely
  on) — so when the playing track hit its end mid-pause and auto-advanced, the new track
  became audible through the pause screen. A voice started during the pause now stays silent
  under it and is simply in progress when you unpause.

- **Fully-silenced spawn stingers bled in right at their end.** With `VolumeModifier` at 0 the
  stinger's last seconds became audible during its release fade, because the shared volume
  governance flips owners while the released voice is still fading. The released voice's level
  is now matched across that flip — silent stays silent, and normal configurations sound
  unchanged.

- **Vanilla fallback could hold the wrong music for minutes near a trader.** While CMM's
  own music plays, the mod tracks (muted) what the game's music system proposes, and holds
  vanilla sections back so they can't bleed under CMM's tracks. After handing off TO vanilla,
  that same hold kept applying — pinning the game to whatever section happened to be current
  when the handoff happened, sometimes for minutes (observed at Trader Rekt). While vanilla
  is the active layer the game's live section choice now plays; user-disabled sections stay
  suppressed.

- **Disabled trader dropped combat music to Exploration.** In a trader's area the game
  proposes the trader section even mid-combat; with the trader disabled, the blind fallback
  played exploration music during a fight. It now reproduces the section the game would pick
  from threat alone (Combat/Bloodmoon when panicked, Suspense when spooked), matching CMM's
  own priority model where combat outranks trader.

- **Recursive menu music never left the vanilla bed on a huge tree.** The menu reclaim only
  ran after a full sweep (minutes away on hundreds of thousands of files); it now also runs
  on the crawler's incremental partial publish, so custom menu music takes over within
  seconds. Handing the menu back to vanilla now fades the title theme back in over the
  crossfade instead of snapping it to full volume.

- **Decoder stalls on large video files.** Two container-reader bugs made the in-game music
  scanner hang on certain large `.mp4`/`.mkv` files. An mp4's oversized `moov` index (e.g.
  36 MB on a 10 h recording) exceeded the cache cap and was parsed via millions of tiny
  reads on a multi-GB file (2 GB file: >25 s → 0.7 s). And mkv/webm eagerly demuxed the
  whole audio track into RAM at open; it now streams on demand (20 GB mkv: ~130 s → 0.2 s,
  peak working set for an 808 MB decode 17 MB instead of O(size)). Decoded output verified
  bit-for-bit identical (LUFS matches ffmpeg).

- **Wrong/missing context music on Turkish and Azerbaijani locales.** Vehicle-type,
  trader-name, and file-extension matching used culture-sensitive `ToLower()`, so the
  Turkish-i rule made strings containing "i" (e.g. `EntityMinibike`) stop matching the ASCII
  literals. Now uses `ToLowerInvariant`.

- **Wrong game option read on A20/A21 (cross-version enum drift).** `EnumGamePrefs` member
  ordinals differ between game versions, and the single cross-version DLL bakes the
  compile-time values in — so on A20/A21 the mod's "is the vanilla Dynamic Music System
  enabled" checks read a different, unrelated option, which could skew the vanilla-handoff
  decisions there. Affected option lookups are now resolved by NAME at runtime against the
  running game.

- **Mod could fail to load entirely on A20/A21 (unified DLL).** A world-emitter hook
  referenced a 3.0-only game type in its Harmony attribute; on games without that type the
  patch scan threw and aborted every patch in the mod. Hooks now resolve their targets by
  name at runtime and skip themselves cleanly on older versions, and the whole emitter layer
  runtime-detects the game version instead of trusting the build flags.

### Build

- **Two new offline test suites, and a soak** *(RC4)*. The radio's schedule and the emitter's slot
  bookkeeping are now covered by `CmmScheduleTest` (68 cases) against the shipped catalog, and a
  new `CmmSoak` drives a 24-hour play session — trader, quest, POI, combat, blood moons — through
  the real decision script, rotation, schedule and loudness code while files are added, deleted,
  renamed, moved and re-encoded underneath it. Ten invariants, over 400,000 checks per run.
  The schedule suite found that one of this release's own fixes was incomplete on the day it was
  written, which is the reason it exists.
- **Now built against 3.1.0 (b14) instead of 3.0.** The supported set is unchanged in spirit —
  one DLL still runs on Alpha 20.7, Alpha 21.2, 2.6 and current 3.x — but 3.0 is no longer a
  build target (Steam upgrades that install in place, so there is no 3.0 left to compile
  against). 3.1 broke none of the game APIs this mod touches: all four versions compile clean
  with no source changes. The release zip is named `... for A20-A21-2.6-3.1.zip` accordingly.
- **VS Code "Package (Release zip)" task** now invokes the single-zip `Package` MSBuild
  target (was `PackageAll`).
- **Development tooling** (not shipped): offline benches that render a station change through
  the shipping decoder headless, a UI for dialling the radio by ear against a reference render,
  a console `cmm acsweep` that exports game-truth acoustics dumps, and an offline judge that
  replays the shipped acoustic model against them. A settings-schema guard checks that every
  new or changed setting can actually reach an existing config file.

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
