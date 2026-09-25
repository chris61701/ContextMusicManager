Your own music, chosen by what you're doing. CMM watches your situation — fighting, driving, swimming, standing in a trader after dark, hiding in an infested POI — and plays from the matching folder. Drop files in, they play. Nothing to restart, no playlists to build, no menus.

**Music Packs by other creators that you can try out**

- [<span style="color: rgb(249, 115, 22);">Stormwrought Music Overhaul</span>](https://www.nexusmods.com/7daystodie/mods/11013) - by [<span style="color: rgb(249, 115, 22);">FinFNS</span>](https://www.nexusmods.com/profile/FinFNS)

**A music pack can be its own mod** - CMM reads music from your *MusicPath* folder **and** the *Music* folder of every installed mod, so a pack installs like any other mod, survives updating CMM, and works with MO2/Vortex/Wabbajack. The *cmm roots* console command shows which folders are in use and which mod each came from.

If you put your own music pack together with your own composition, or royalty-free and AI-generated content (that you are legally able to distribute), I'll link to it here

<span style="color: rgb(239, 68, 68);">**Alpha 20.7, Alpha 21.2 and 2.6 have not been thoroughly tested**</span>

---

**Requirements**

- EAC must be OFF
- **7 Days to Die** version 3.2.0 b10 - 2.6 b12 - A21.2 b37 - A20.7 b1 (one build runs on all of them)
- Windows. The native audio engine ships as a Windows library; Linux support is planned for 1.0.6

---

**Keybinds (defaults)**

- **Alt+Num7** - Now Playing overlay
- **Alt+Num8** - Coverage panel: which contexts have music, how many tracks, and what plays when they're empty. Radio stations are listed individually, so an empty station shows as dead air. It reads from disk live, so adding or moving music updates it within a second — even on the main menu
- **Alt+Num9** - Context status / debug HUD
- **Alt+Page Up / Page Down** - Switch radio stations (Off → station → station → Off)
- **Alt+Home** - Cycle combat music: combat **+** tension off → combat off → both on (tension is combat music that hasn't started yet)
- **Alt+Insert** - Favourite the current track — it plays more often, roughly once an hour in a 50+ track folder
- **Alt+Delete** - Ban the current track in the current context (never plays there again)
- **Alt+End** - Unban the last banned track
- **Alt+Left / Right** - Previous / next track in the current context
- **Alt+Up / Down** - Adjust the saved volume trim for the playing track

Both debug panels show their own keybinds, and follow yours if you rebind them.

**Getting your music in**

- **Drop files into the folder for the situation you want them in** — see the folder map below. You don't have to fill every folder; empty ones fall back automatically, so *ambient* and *combat* alone are a perfectly good start
- **No restart needed** — add, remove or replace files while the game runs and CMM picks them up within seconds, even the track currently playing
- **Radio stations are subfolders of Music/radio/** — the folder name is the station name. Alt+PgUp / Alt+PgDn dials through them, and any speaker block in the world plays them too
- **Your music can live anywhere** — set *MusicPath* in *Config/settings.xml* to any folder, even on another drive
- **Not sure whether CMM sees a folder?** Open the coverage panel with **Alt+Num8** — it lists every context, its track count, and what it's falling back to

**Folder Map**

```
Music/
├── ambient/                         Exploration default (lowest priority)
│   ├── _night/                      Night-only exploration tracks (generic)
│   ├── forest/                      Pine forest (day + everyday)
│   │   └── _night/                  Forest, night only
│   ├── burnt_forest/
│   │   └── _night/
│   ├── desert/
│   │   └── _night/
│   ├── snow/
│   │   └── _night/
│   └── wasteland/
│       └── _night/
│
├── radio/                           Stations for the personal radio AND world speakers
│   ├── <station name>/              One subfolder per station - the folder name IS the name
│   └── <station name>/
│
├── home/                            Near your bedroll / land claim
│   └── night/                       Home base, night only
│
├── poi/                             Inside a cleared / non-infested POI
│   ├── t0/  t1/  t2/  t3/  t4/  t5/   By POI difficulty tier
│   └── infested/                    Inside a POI with live sleepers
│       └── t0/ t1/ t2/ t3/ t4/ t5/  By POI difficulty tier
│
├── trader/                          Inside any open trader
│   ├── joel/  jen/  rekt/  bob/  hugh/   Per-trader themes
│
├── vehicle/                         Riding any vehicle
│   ├── gyrocopter/  bicycle/  minibike/  motorcycle/  4x4/
│
├── swimming/                        Deep enough to swim
│
├── tension/                         Enemies alert & nearby, not yet fighting
│
├── combat/                          Combat — Low intensity (the combat root IS the Low tier)
│   ├── _high/                       Combat — High intensity
│   ├── _boss/                       Combat — Boss intensity
│   ├── blood_moon/                  Combat during a Blood Moon night (no biome split)
│   ├── forest/                      Pine forest — biome combat (Low)
│   │   ├── _high/                   Forest — High
│   │   └── _boss/                   Forest — Boss
│   ├── burnt_forest/                Burnt forest — biome combat (Low)
│   │   ├── _high/
│   │   └── _boss/
│   ├── desert/                      Desert — biome combat (Low)
│   │   ├── _high/
│   │   └── _boss/
│   ├── snow/                        Snow — biome combat (Low)
│   │   ├── _high/
│   │   └── _boss/
│   └── wasteland/                   Wasteland — biome combat (Low)
│       ├── _high/
│       └── _boss/
│
├── events/
│   ├── death/                       Plays once on death
│   ├── low_health/                  Critically low HP during combat
│   ├── spawn/                       Plays once on spawn
│   └── poi_cleared/                 Sting when the last sleeper dies
│
└── menu/                            Main menu (loops)
```

---

**Highlights**

- **Live hot-swapping** — change music mid-game, even mid-track - Add, remove, or replace music **without restarting**. CMM picks up changes in seconds — even the track that's currently playing. No menus, no configs to edit. Just drop files in folders.
- **Music in the world** — radios and speaker blocks play positionally, occluded by walls and doors, with room reverb
- **A radio you carry** — the same broadcast wherever you are, on every supported game version
- **Music packs as mods** — install a soundtrack like any other mod
- **Squad-shared combat music** — your music reacts to your whole team's fight, client-side only
- **Every track plays before any track repeats** — a large soundtrack finally sounds large
- **Playlists** — point a context at a .m3u/.m3u8 file instead of a folder
- **Smart resume** — leave & return, the track picks up where it left off
- **Smooth crossfades** everywhere — up to four tracks overlapping, no hard cuts
- **Loudness-matched** — tracks from different albums and sources play at a consistent volume
- **Native streaming decoder** — a bundled native library handles every format with almost no garbage-collection pressure, so big libraries don't hurt FPS
- **Plays nice with vanilla** Dynamic Music, section by section (if you keep it enabled)
- **Hot-reload config** — tune volume, fades & priorities while playing

---

**Music in the world**

In-Game radio objects emit music from tracks stored in /Music/radio (or whichever path you define) as real positional, diegetic sound layered on top of your normal soundtrack — walk toward a speaker and it gets louder, pans across your ears, muffles behind walls, and picks up room reverb. A shared "station" keeps broadcasting even while you're out of range, so you rejoin mid-song when you come back.

- **Speaker character** — the sound is squeezed into a small driver in a small box, with the boxy resonance a cheap cone actually has. Ham radios are voiced harder still. (Considering a DJ system like Fallout 3/4 — maybe in 1.0.6)
- **Doors matter** — an open door is air and a closed one is solid, damaged doors leak sound, and sliding doors muffle gradually as they travel. Occlusion follows how much of the doorway is actually open, so a garage door and a closet door behave consistently, both leaves of a double door count, and tilted doors like the wood cellar hatch are measured properly. Standing in a doorway that has swung open sounds like standing in it, not behind it. (I'm not sure how this affects doors other players control)
- **Live tuning** — the whole spatial feel (range, falloff, occlusion, reverb) is hot-tunable live from radio.xml while the game runs
- **Volume** — world speakers follow the game's **Ambient** slider rather than Music, with their own *WorldEmitterVolume* trim (default **0.5**)

**A radio you carry**\
**Alt+PgUp / Alt+PgDn** dials through Off → station → station → Off. It works on every supported game version, including the alphas that have no speaker blocks at all. Turning it on holds your place in whatever context music was playing, and turning it off drops you back exactly where you left it, however long you listened.

---

**Squad-shared combat music (multiplayer)**

- **Purely client-side - no server mod needed**
- Your combat music reacts to your whole squad, not just the enemies on you personally. When a nearby teammate is under attack you drop into combat music too, even if their zombies are well outside your own detection range
- The music scales with the size of the fight: soloing a swarm and battling shoulder-to-shoulder with a full squad are different musical moments
- Works for players joining a server, not just the host — combat is detected even though the enemy AI runs server-side
- Fully configurable: how close a teammate must be to share their combat (default 30 units), and whether it counts only party members and friends or any nearby player
- Single-player is completely unaffected

---

**The audio engine**

Nearly the entire audio pipeline lives outside the game's managed (C#/Unity) environment, in a purpose-built native C library. The music doesn't compete with the game for the main thread and isn't interrupted by garbage-collection pauses — the biggest source of stutters.

- Decoding, mixing and output all run in the mod's own engine (CMM_Decoder.dll) on threads the mod owns, completely outside the frame loop. CMM doesn't use Unity's audio system at all
- **Crossfades don't get clobbered.** The mixer holds up to **four simultaneous voices**, so a new track can start while three older ones finish fading naturally. Ambient → tension → combat in two seconds sounds like one continuous handoff instead of an abrupt cut. If transitions pile up past the limit it drops only the *quietest* voice, already nearly silent at the tail of its fade — the incoming track is never the one sacrificed
- **Every format decoded natively** — ogg, opus, flac, mp3, wav, m4a/aac, and the audio inside mp4/m4v/mkv/webm containers
- **Per-track loudness normalization (EBU R128)** — measured once per track and cached, with a manual per-track trim on a keybind
- **Music survives loading freezes** — tracks keep playing, and even advance, while the game's main thread is frozen during world load
- **Device changes are handled** — unplugging a headset or switching the default output no longer strands the music
- **Gapless** — the next track is decoded in advance
- Background work — loudness scanning, pre-loading, folder and config watching — is fully off the game loop, and libraries of 100,000+ files scan without hitching
- The game's **Overall Volume** slider is applied by hand, since CMM's audio leaves through its own output device and the master slider never reached it

---

**Your config files are yours**\
settings.xml, radio.xml and contexts.xml are created on first run instead of shipping in the download, so an update never wipes what you set — it only appends what a new version actually added.

---

**The contexts (highest priority first)**\
\
**Death** (100) - plays once on death, then returns to the previous music. Interrupts everything

**POI Cleared** (91) - a one-shot sting when you kill the last sleeper in a POI\
\
**Low Health Panic** (90) - fires when you're badly hurt (default 30% - configurable) and in combat\
\
**Blood Moon Combat** (71) - combat during a Blood Moon night; the highest combat priority, overriding normal combat tiers

**Vehicles** (70 / base 69)

- **Gyrocopter, Bicycle, Minibike, Motorcycle, 4x4** each have their own music → fall back to the generic **Vehicle** track if their folder is empty

**Biome Combat** (62 / 61 / 60) - optional per-biome versions of each combat tier (**Forest, Burnt Forest, Desert, Snow, Wasteland**). When you fight in a biome that has its own combat music, it plays instead of the matching generic tier below; if that folder is empty it falls straight back to the generic tier. The biome is locked in when the fight starts — so crossing a border mid-fight won't swap the track — and re-locks if you leave combat and re-engage somewhere new (the intensity tier still resumes where it left off). Sits just above the generic combat tiers (and rides over vehicle/exploration music during a fight). Blood Moon combat stays generic.\
\
**Combat** - intensity is chosen by scoring the enemies targeting you. Three tiers, each falling back to the one below so missing folders degrade gracefully:

- **Combat Boss** (52) → **Combat High**
- **Combat High** (51) → **Combat Low**
- **Combat Low** (50) → vanilla combat music if the game is "panicked," otherwise back to the stack

The **combat/** root folder is itself the Low tier; the `_high` and `_boss` subfolders hold the escalations, and each biome mirrors this under **combat/&lt;biome&gt;/**.\
\
**Spawn** (45) - plays once when you spawn in\
\
**Radio** - the personal radio and the world speakers both broadcast from **Music/radio/**. While the personal radio is on it owns the music layer; world speakers play alongside your normal soundtrack.\
\
**Tension** (30) - plays when enemies are stalking you and close by; short stand-offs are ignored thanks to warmup (2 seconds), so a quick rush straight into a fight goes directly to combat\
\
**Traders** (26 / base 25)

- **Joel, Jen, Rekt, Bob, Hugh** each get their own theme → fall back to the generic **Trader** track → which falls back to vanilla trader music (when the game's Dynamic Music is on)

**Swimming** (23) - plays while you're deep enough to swim; short dips are ignored thanks to warmup (5 seconds)\
\
**Points of Interest** - two layers, each tiered **T0–T5** by POI difficulty:

- **Infested POIs** (22 → base 20) — plays while live sleepers remain. Each tier (T5…T1) falls back to **POI_Infested**, **POI_Infested** falls back to the stack (excludes T0 which always falls back to the stack)
- **Non-infested POIs** (18 → base 16) — the ordinary tier for whichever POI you're in. **As of 1.0.5 this stays on the stack even while the place is infested**, sitting underneath the infested tier — so if an infested folder is empty, the music can land on the matching ordinary tier instead of dropping out of the POI layer entirely. Each tier falls back to the generic **POI** track (excludes T0)

**Home base** (15) - near your bedroll or land claim, and it keeps its music even when your base is inside a POI

- **Home Night** plays night-exclusive tracks → falls back to **Home** (your default base music).

**Ambient / Biome** (12 → base 10) — your everyday exploration music, the lowest custom layer:

- Each biome (**Forest, Burnt Forest, Desert, Snow, Wasteland**) has a day track and an optional `_Night` variant.
- A biome's night track → falls back to the generic **Ambient Night** → which falls back to **Stack** (biome's day track if it exists).
- A biome's day track → falls back to **Ambient**.
- **Ambient** → falls back to vanilla music (when Dynamic Music is on).

**Menu** (0) - loops in the main menu; falls back to vanilla menu music.\
**Sentinels** (built-in, don't remove) — **Silence** and **Vanilla** are the two ultimate fallbacks: hand back to the game's own music, or play nothing.

---

**How the music is chosen**\
CMM keeps a **stack of everything currently true about your situation** (in combat, in a desert, at night, near a trader…) and plays the **highest-priority** one that has music. Equal priorities never interrupt each other, so whatever is already playing keeps going until something genuinely more important happens.\
\
If a context's folder is **empty**, it doesn't go silent — it follows that context's **fallback** to the next-best choice. A few shared rules:

- **Warmup** - some contexts must stay active for a few seconds before they take over, so brief moments (a quick dip in water, ducking into a doorway) don't interrupt your music
- **WarmGroup** - a shared tag for contexts that hand off to each other (like a day/night pair); members share one warmup timer so swapping between them stays seamless instead of briefly dropping to lower-priority music while the new one warms up. Each context still keeps its own Warmup — set grouped members equal for the smoothest result. Used by Home and Home_Night, and by the infested / non-infested POI tiers
- **Cooldown** - combat/event music waits a few seconds before it can re-trigger, preventing rapid flicker
- **Resume** - most contexts pick up where they left off when you re-enter them; short events restart fresh
- **Stale** - contexts are only eligible to resume from their saved state while inactive until they time out (go stale). It can be disabled per context so a context never expires while inactive
- **Once** - event stingers play to completion, then hand back to whatever was playing before
- **Fade length follows the context** - anything that comes and goes quickly (Tension, every Combat tier, POI_Infested and its tiers, the online-player contexts) fades in fast (1.2 s) so you actually hear it before it's gone. Music tied to where you are or what time it is keeps its slow fade

**Additional Attributes** that can be customized in contexts.xml:

- **Name** - context's ID
- **Source** - context's sub-folder, or a .m3u/.m3u8 playlist file. A folder containing playlists also picks up their tracks alongside the loose files, with no duplicates, and edits to a playlist take effect within seconds — including on USB, network and other non-standard drives
- **Subfolders** - if enabled, will play all music in the context folder's subfolder
- **Fallback** - what the context falls back to if it is missing files - another context, the Stack, Vanilla, or Silence
- **FadeInTime** - how long the context takes to fade in when activated
- **Priority** - priority while on the stack
- **Loop** - one track is chosen to play and repeats (not used by any context, but the option is there) until it goes stale
- **Bypass** - circumvents the Cooldown of the track that is currently playing

**Additional Settings** in settings.xml worth mentioning:

- **VolumeModifier** - how loud CMM tracks play relative to the game's music slider (0.0–1.0, default 0.5)
- **MusicPath** - music's root folder. Relative or absolute path (can store files in folders outside of the mod directory/drive)
- **AddonMusicScanEnabled** - whether music packs installed as separate mods are read
- **TrackNoRepeatMemory** - how much of a folder must play before a track can repeat (-1 = the whole folder)
- **FavouriteWeight** - how much more often a favourited track plays
- **DisabledVanillaSections** - turn off individual vanilla Dynamic Music sections (Exploration, Suspense, Combat, Bloodmoon, Trader)
- **EnemyDetectRange** - max distance (game units) an enemy is counted toward combat/tension state
- **HomeDetectRange** - distance (game units) from your bedroll or land claim that counts as "home"
- **PlayerHealthDangerThresholdPercent** - health fraction (0.0–1.0) below which combat escalates to Low_Health_Panic

**Additional Settings** in radio.xml (the radio and world-speaker half, split out so it doesn't bury the rest):

- **WorldEmitterEnabled / WorldEmitterVolume / WorldEmitterRange** - world speakers on/off, their own volume trim (they follow the **Ambient** slider, not Music), and how far they carry
- **WorldEmitterColorVoice / ColorDrive / ColorStatic** - how much like a small cheap speaker they sound, with a separate set for ham radios
- **WorldEmitterActiveByDefaultPercent** - how many of the radios that come with POIs are playing when you find them (default **10**%); the rest start switched off until you turn them on from the radial
- The rest of the acoustic model (occlusion, doors, reverb) and every knob of the station-change sound

---

**FAQ & Troubleshooting**\
\
**Q: Do I have to fill every folder?**\
No. Fill only the ones you care about — empty folders fall back automatically (and ultimately to the game's vanilla music). You can start with just *ambient* and *combat*.

**Q: How do I set up radio stations?**\
Make a subfolder inside *Music/radio/* for each station; the folder name is the station name. The personal radio (Alt+PgUp / Alt+PgDn) and any speaker block in the world both use them.

**Q: Do I need speakers in the world to use the radio?**\
No. The personal radio works on every supported version, including the alphas that have no speaker blocks at all.

**Q: Does the addon work on MacOS or Linux?**\
Not yet. The native audio engine currently ships as a Windows library, so Windows is the only officially supported platform. Linux support is planned for 1.0.6.

**Q: What audio formats are supported?**\
MP3, OGG (Vorbis), Opus, FLAC, WAV/AIFF, and AAC — including *.m4a* / *.mp4*, and even the audio track of an MP4/MKV/WebM video.

\
**Q: Can I really change music without restarting?**\
Yes. Add, remove, or replace files while the game runs — CMM detects the change within a few seconds, even for the track currently playing.

\
**Q: I added a file but nothing happened.**

- Make sure it's in the correct folder and a supported format.
- That situation may not be active yet (e.g. a *blood_moon* track only plays on horde night).
- A higher-priority situation may be overriding it (combat beats ambient, etc.).
- Corrupt files are auto-rejected — re-export the track if it won't play.
- If it's a music pack installed as its own mod, *cmm roots* will tell you whether CMM can see it.
- The coverage panel (Alt+Num8) shows exactly which contexts have music and which are falling back.

**Q: Multiple tracks in one folder - how are they picked?**\
Every track plays before any track repeats — each context deals from a shuffled bag and reshuffles once it runs out. Favourites get extra turns; banned tracks are skipped. **Next/Prev** walk your play history in that context, like a music player, rather than rolling a fresh random track. You can switch to Sequential or pure Random in *Config/settings.xml (only shuffled-bag has been thoroughly tested)*\
\
**Q: It's too loud / too quiet.**\
CMM follows your in-game Music volume slider, plus a ***VolumeModifier*** in *Config/settings.xml* (default 0.5). World speakers follow the **Ambient** slider instead, with their own *WorldEmitterVolume* trim (default 0.5). Raise or lower that — when you save the file, the updated settings load immediately if the game is running. Individual tracks are loudness-matched automatically, and **Alt+Up/Down** saves a per-track trim on top.\
\
**Q: Does this replace the vanilla music entirely?**\
Only where you provide tracks. Empty folders hand off to vanilla Dynamic Music, so the two coexist — and you can now disable individual vanilla sections rather than all of them.\
\
**Q: I left an area and came back - why did the music restart / not restart?**\
Tracks resume where you left off if you return quickly; after a context goes "stale" (configurable per situation), CMM picks a fresh track instead.\
\
**Q: Can I store my music outside the mod folder?**\
Yes — set *MusicPath* in *Config/settings.xml* to any folder (even on another drive). You can also install music as a separate mod entirely.\
\
**Q: Will updating CMM wipe my settings?**\
No. As of 1.0.5 the config files are created on first run rather than shipped in the download, so an update only appends settings that are genuinely new and leaves your choices alone.\
\
**Q: Does it work with the game's Dynamic Music setting?**\
Yes. Some folders fall back to vanilla only when Dynamic Music is enabled, so your choice in the options menu is respected.\
\
**Q: Was AI used to make this mod?**\
AI was used to help write this description page, and it amounts to \~80% of the mod's code.

---

<span style="color: rgb(234, 179, 8);">**V1.0.6 All hands on "Deck" (planned - October likely)**</span>

- <span style="color: rgb(245, 158, 11);">Linux support</span>
- <span style="color: rgb(245, 158, 11);">UI Support for controlling the radio (and perhaps other addon settings)</span>

---

**Third-Party Libraries**

This mod wouldn't be possible without the open-source libraries it's built on. Huge thanks to their authors:

- **MoonSharp** (BSD 3-Clause + MIT) — Marco Mastropaolo — the embedded Lua interpreter that runs the context.lua decision script. [moonsharp.org](http://moonsharp.org)  —  [github.com/moonsharp-devs/moonsharp](https://github.com/moonsharp-devs/moonsharp)

Audio is decoded and played by a small native library (CMM_Decoder.dll) bundled with the mod, which compiles in these components:

- **libopus / libopusfile** (BSD) — [Xiph.Org](http://Xiph.Org) Foundation — Opus decoding. [github.com/xiph/opusfile](https://github.com/xiph/opusfile)
- **libvorbis / libvorbisfile** (BSD) — [Xiph.Org](http://Xiph.Org) Foundation — OGG Vorbis decoding. [github.com/xiph/vorbis](https://github.com/xiph/vorbis)
- **libogg** (BSD) — [Xiph.Org](http://Xiph.Org) Foundation — Ogg container framing used by Opus and Vorbis. [github.com/xiph/ogg](https://github.com/xiph/ogg)
- **dr_flac / dr_mp3 / dr_wav** (public domain) — David Reid (mackron) — FLAC, MP3, and WAV/AIFF decoding. [github.com/mackron/dr_libs](https://github.com/mackron/dr_libs)
- **fdk-aac** (Fraunhofer FDK AAC license) — Fraunhofer IIS — AAC-LC / HE-AAC decoding. [github.com/mstorsjo/fdk-aac](https://github.com/mstorsjo/fdk-aac)
- **minimp4** (CC0) — Lieff — MP4 / M4A container demuxing. [github.com/lieff/minimp4](https://github.com/lieff/minimp4)
- **miniaudio** (public domain) — David Reid (mackron) — cross-platform audio playback. [github.com/mackron/miniaudio](https://github.com/mackron/miniaudio)

Full license texts are included in *THIRD_PARTY_NOTICES.txt* inside the mod download.
