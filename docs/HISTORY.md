# Context Music Manager — a development history

*A 7 Days to Die mod that replaced the game's music system, then replaced its own.*

403 commits. June 2 – August 23, 2026. Twelve weeks.

---

**About this document.** Everything in plain text is drawn from the repository: commit
subjects and bodies, `CHANGELOG.md`, `TODO.md`, `ModInfo.xml`, and the project's own design
notes. Quoted lines are verbatim commit subjects. Sections marked **▸ Why** are *my
analysis* — an outside reading of what the decision was solving for. They are inference,
not testimony, and the author may disagree with any of them.

---

## Contents

1. [The short version](#the-short-version)
2. [By the numbers](#by-the-numbers)
3. [Act I — A weekend mod](#act-i--a-weekend-mod)
4. [Act II — Becoming software](#act-ii--becoming-software)
5. [Act III — The engine goes native](#act-iii--the-engine-goes-native)
6. [Act IV — Music in the world](#act-iv--music-in-the-world)
7. [Act V — The acoustics rebuild](#act-v--the-acoustics-rebuild)
8. [Act VI — The library, and the things that take longest](#act-vi--the-library-and-the-things-that-take-longest)
9. [Six principles the codebase actually follows](#six-principles-the-codebase-actually-follows)
10. [The recurring failure modes](#the-recurring-failure-modes)
11. [Where it's going](#where-its-going)
12. [Interview questions](#interview-questions)
13. [Timeline](#timeline)

---

## The short version

Context Music Manager began as a weekend mod that played your own music instead of the
game's. It ended up shipping a purpose-built native audio engine, a calibrated acoustic
model for sound travelling through a voxel world, and an in-game radio with stations,
static, and a dial that clicks.

The through-line is a single adversary: **the game's frame loop**. Unity's audio system, the
garbage collector, and the main thread are all hostile to continuous music. Almost every
major architectural decision in the project is another step away from them — and the payoff
is not a feature you can screenshot. It's an absence. No stutter.

The second through-line is **measurement**. Eighteen purpose-built tools exist in this
repository for no reason other than to find out whether something is true. Several major
features exist only because a harness proved the previous version wrong, and at least twice
the project discovered it had spent weeks tuning something nobody had actually heard.

---

## By the numbers

| | |
|---|---|
| Commits | 403 |
| Span | 2026-06-02 → 2026-08-23 (12 weeks) |
| Tracked files | 636 |
| C# | ~39,600 lines / 96 files |
| Native C | ~14,300 lines / 36 files |
| Lua | 300 lines — the entire music-selection policy |
| Research harnesses (JS) | ~7,200 lines |
| Design docs in-repo | ~4,600 lines of Markdown |
| Purpose-built dev tools | 18 |
| Native audio ABI revisions | 50 |
| Acoustic field ABI revisions | 6 |
| Config schema revisions | 20 |
| Settings | 133, all described by one schema table |
| Game versions from one download | A20.7, A21.2, 2.6, 3.x |
| Formats decoded natively | ogg, opus, flac, mp3, wav, m4a/aac, + audio inside mp4/m4v/mkv/webm |

Busiest week: **174 commits** (week of Aug 17). Release candidate `v1.0.5-rc1` is 1.51 MB,
nine files.

---

## Act I — A weekend mod
### June 2 – June 17

The first commits are what you'd expect from someone scratching an itch: caching,
crossfading, and a toggle for each. But by **day four** the real problem has surfaced, and
it isn't playback:

> *"Fixes in logic to address contexts changing too quickly"* — June 5

**June 6** is the first big day. POI detection, sleeper detection, POI tiers 1–5, infested
and cleared states, blood-moon combat, and the contexts move from a list into a dictionary.
Buried in the same message:

> *"Hot Loading (removing/adding files while game running) went from an unintended glitch
> to intentional feature"*

**June 10** — NAudio goes in. It will be entirely removed eleven weeks later, and its
README is still sitting in the repo root today.

### ▸ Why: the glitch that became a contract

Promoting hot-reload from bug to feature looks like a throwaway line, and it isn't. It
committed the project to a promise — *you can reorganise your music while the game is
running* — that reached down into the architecture three weeks later.

If a track is playing from an open file handle, its folder is pinned. You cannot move it.
Honouring the promise is why **June 29** reads *"Buffer audio files into native memory on
open to avoid locking music folders"*: read the file into off-GC native memory, close the
handle immediately, and the folder is free. Files over a size cap still stream, which is the
honest compromise.

One casual user-facing promise, made in week one, forced a memory-management decision in
week four. This pattern repeats throughout the project.

---

## Act II — Becoming software
### June 20 – June 29

Ten days that turn a mod into an engineering project.

### One binary, four game versions (June 23, `1.0.1`)

7 Days to Die changed its API repeatedly across Alpha 20, Alpha 21, 2.6 and 3.x. The
project's own audit lists **ten distinct divergences**, and the dangerous thing about them is
that per-version compilation *hides* them:

- `TraderInfo.IsOpen` is a **property** on A20/A21/2.6 and a **field** on 3.0
- `Prefab.PrefabName`, same split, on a different boundary
- `World.GetPOIAtPosition()` takes **1, 2 or 3 parameters** depending on version — and C#
  bakes default arguments in at the call site, so a one-arg call breaks across arities
- `GetCurrentRainfallPercent()` on 3.0 is `GetCurrentRainfallValue()` on the alphas
- `EnumGamePrefs` **ordinals drift** — `OptionsDynamicMusicEnabled` is 170 on A20/A21 and
  175 on 3.0, and C# bakes enum members in as integer constants, so a 3.0-compiled binary
  silently reads *a different setting* on an older game

Each of these compiles cleanly N times and only fails when the binary built against one
version runs on another. The project found them by decompiling all four games and diffing
member *shapes* — not by reading source or counting `#if` blocks, neither of which can see
a field/property split.

### ▸ Why: the support burden, not the elegance

Shipping four downloads is easy. What's expensive is everything after: four sets of release
notes, four chances to grab the wrong file, and a bug queue where the first question is
always "which version did you install?" One file that loads everywhere converts a permanent
support tax into a one-time engineering cost.

The tell that this was the reasoning is **July 2**: *"Runtime-generate the cmm console
command (removes the last per-version fork)."* Nobody removes the *last* fork for
performance. You do it because a single remaining fork means you still have four build
outputs, and the whole point was to have one.

### The music logic leaves the DLL (June 26)

In a single day: `BuildSnapshot()` is extracted from `ResolveContext()`, a MoonSharp
interpreter is embedded, the resolver is ported to Lua, cut over, and the C# version is
deleted.

The entire policy — priorities, the weighted combat-threat model, day/night rules, biome
rules, fallback chains — now lives in `Config/context.lua`. It hot-reloads on save. It runs
sandboxed with no file or OS access. A pristine copy is embedded in the assembly and used
automatically if the on-disk file is missing or fails to compile, so a broken edit **cannot**
take the audio down. Delete the file and the default returns.

### ▸ Why: this is the part that is a matter of taste

Everything else in the mod has a right answer. Does the FLAC decode? Does the crossfade
click? Those are correct or they aren't.

*When should combat music start* has no right answer. It depends on how you play, your
difficulty, and your temperament. Any value the author picks will be wrong for a large
fraction of users — and each of those users would otherwise be a bug report that can only be
closed by shipping a new build.

Moving the policy into a hot-reloading script converts the most contentious surface in the
project from "file an issue and wait for a release" into "edit a file, alt-tab back." The
embedded-fallback detail is what makes it safe to hand players a live wire: the worst
outcome of a bad edit is that the default comes back.

Note also the *deletion* of the C# resolver. Keeping it as a fallback would have created two
implementations of one policy that could disagree — a failure mode the project runs into for
real, much later, in the acoustics work.

### Loudness normalization (June 26, `1.0.3`)

EBU R128 measures every track once and caches the result, so tracks from different albums
play at a consistent perceived level.

### ▸ Why: it's load-bearing for the core promise

The mod's pitch is "play your own library." A personal library spans decades of mastering
practice — a 1994 CD rip next to a 2019 loudness-war master is a 12 dB jump. Without
normalization, the headline feature actively produces a worse experience than the game's
own curated soundtrack. R128 isn't a nicety here; it's what makes "your music" viable.

### Biome combat music (June 26, `1.0.2a`)

Fight in the wasteland, get wasteland combat music. Two details show unusual care:

The biome **locks when the fight starts** and holds for the whole continuous fight, so
running across a border mid-battle doesn't swap the track. But if you break off and
re-engage somewhere new, it **re-locks to the new biome while keeping the intensity tier**
you'd escalated to. Leave a fight in the wasteland at Boss intensity, re-enter in the
desert: you get the desert track, still at Boss.

### ▸ Why: two clocks, and they run at different rates

Naively, "which music" is one question. It's two. *Where you are* changes at walking speed;
*how bad it is* changes over the arc of a fight and shouldn't reset because you stepped
across a line on a map. Treating them as one variable produces either music that flickers
with your footsteps or music that ignores where you are.

The same insight produces `warmgroup` the same week — day and night versions of a context
share a warm-up timer, so switching between Home and Home_Night doesn't restart the dwell
and leave a gap where the wrong track cuts in.

---

## Act III — The engine goes native
### June 28 – July 3

The largest technical arc in the project, and the one with the clearest payoff.

**The problem.** Music played through Unity competes with the game for the main thread and
is frozen by garbage collection. A GC pause is stop-the-world for *all* managed threads, so
there is no managed opt-out — you cannot fix this with better C#. On a horde night, when GC
pressure and chunk streaming peak simultaneously, the ring buffer underruns and you hear it.

**The answer was to leave, in stages.** Each removes one more thing that can be suspended:

| Stage | Date | What moved |
|---|---|---|
| 1 | Jun 28 | **Decoding** → `CMM_Decoder.dll`. NAudio removed entirely. |
| 2 | Jun 29 | **Buffering** → off-GC native memory; file handles close immediately. |
| 3 | Jun 30 | **Output** → miniaudio on the mod's own thread. The Unity `AudioSource` path is deleted the same day. |
| 4 | Jul 1 | **The mixer** → the per-sample producer loop, rewritten in C. |
| 5 | Jul 1 | **Loudness** → R128 analysis follows it native. |

The split the project settled on is worth stating exactly, because it's the load-bearing
design decision of the whole codebase:

> **C# stays the BRAIN** — which file, when, fades, resume, loudness bookkeeping,
> auto-advance, cooldowns. Infrequent, and GC-tolerant.
>
> **C becomes the HANDS** — decode → convert → gain → sum → write. Never touches the managed
> heap, never suspended.

### ▸ Why: the boundary is drawn at *frequency*, not at subject

The obvious way to split a codebase is by topic — "all audio in C, all game logic in C#."
That is not what happened. Decoders are still *opened* in C#, off-thread, once per track;
the file handle and format kind are then handed to `cmm_mix_add` and the mixer reads
natively from there.

The line is drawn at *how often the code runs*. Anything on the per-sample path is native.
Anything that happens once per track can stay managed, because a GC pause during a decision
is invisible — you have hundreds of milliseconds of buffered audio to absorb it. A GC pause
during the mix loop is a click.

That's a more disciplined boundary than "rewrite it in C," and it's why the C# side is still
39,000 lines. The rewrite was surgical.

**What it buys, concretely:**

- **Music survives loading freezes.** Because the engine owns its threads, tracks keep
  playing — *and keep advancing to the next track* — while the game's main thread is stalled
  loading a world.
- **Crossfades stop being clobbered.** The old system had exactly two channels. If a new
  context fired mid-crossfade it had to steal one and hard-cut. The new mixer holds **four
  voices**, so ambient → tension → combat in two seconds is one continuous handoff.
- **Eviction is inaudible.** Past four voices, the mixer drops the *quietest* — already
  nearly silent at the tail of its fade. The incoming track is never sacrificed.

### ▸ Why four voices, and why the quietest one

Four isn't an audio-engineering number. It's a *game* number: it's how many context changes
7 Days to Die can throw at you before the first fade finishes. The mixer is sized to the
game's rate of change, not to a mixing convention.

The eviction rule is the sharper decision. When you must drop something, the question is
which loss the player will notice. The tail of an old fade is near-silent — nobody hears it
go. The incoming track is the one the player is *waiting for*; cutting it is the loudest
possible failure. "Sacrifice the quietest, never the newest" reads as obvious once stated,
which is usually the sign of a rule someone learned the hard way.

**A bug worth telling:** *"Race-free native file reads: normalize FLACs of any size."*
Concurrent native file-opens were crossing each other's C-runtime buffers, so `drflac_open`
returned NULL — and FLAC tracks were then silently left un-normalized, playing too loud or
too quiet with no error logged anywhere. The fix was to decode off raw Win32 handles.

---

## Act IV — Music in the world
### July 4 – July 18

Until now the music was in your ears. Now it comes out of objects.

**World emitters (July 4).** Radios, jukeboxes and speaker blocks play music *where they
physically stand*, through a native spatializer and reverb. A POI you're clearing has music
coming from a corner of it.

**Doors (July 8).** Sound routes through doorways and around corners rather than through
walls. Opening, closing or damaging a door changes what gets through. Sliding doors muffle
gradually.

**The personal radio (July 10).** `Alt+PgUp` / `Alt+PgDn` steps through stations. Stations
are subfolders of the radio folder — the folder name *is* the station name. Everyone on a
server hears the same song at the same moment, and a station keeps broadcasting while you're
away, so tuning back in drops you into whatever is playing now, mid-track.

### ▸ Why "a station is a folder" is the best design decision in the project

There is no station manifest, no registration XML, no UI to define one. Make a folder, name
it, put music in it.

Compare the alternative: a `stations.xml` listing name, path, and ordering. It would work,
and it would be worse in every way that matters. It's a second source of truth that can
disagree with the disk. It breaks when someone renames a folder. It needs its own migration
when the schema changes. It needs documentation, and it needs a UI eventually.

Choosing the filesystem as the data model means the feature has no configuration surface at
all — and therefore no configuration bugs. The same instinct shows up again in August, when
add-on music packs turn out to need no registration file either: the convention is simply
`<mod>/Music`.

### The sync problem, and its answer

The world emitters and the personal radio drifted audibly apart. The measured cause is
lovely: **the game's `worldTime` advances at roughly 0.89× realtime**, while audio decoders
run at exactly 1.0×. Every voice outran the worldTime-based schedule by about **7 seconds
per minute**, and consumers re-synchronised at different moments.

The fix was to stop trusting game time: the broadcast clock is anchored to `worldTime` at
world entry, then runs at *realtime* rate, freezes during single-player pause, and carries
worldTime jumps. A second round handled 3.0 saving the world synchronously on the pause edge
— a ~2.5 second stall — by attributing clock intervals to their starting pause-state and
accumulating resync debt for sub-second pauses.

### ▸ Why this is harder than it looks

"Everyone hears the same song" sounds like a networking problem and is actually a *clock*
problem. There is no message you can send that fixes a schedule computed from a clock running
at the wrong rate; you'd just be re-syncing to the same drift every time. Finding that
worldTime runs at 0.89× requires instrumenting and *measuring*, not reasoning — and the
project built `[CMM SYNC]` audit logging specifically to do it.

**July 18** closes the act with the least glamorous and most important fix in it:
installing the mod on a dedicated server had been freezing joining players at the loading
screen.

---

## Act V — The acoustics rebuild
### August 13 – August 23

The most unusual stretch of the project. Roughly 250 commits in eleven days, almost all on
one question: **what does sound do inside a voxel world?**

The existing occlusion model worked, but nobody could say whether it was *right* — it was
uncalibrated. So it was rebuilt from a written specification, with reference scenarios, an
automated acceptance gate, and offline audio rendering so changes could be listened to
rather than argued about.

The commit log reads like a research notebook, and the honesty is the substance:

> *"synth + scenarios: controlled geometry finds three defects in the NEW model"*
> *"ref: the unifying principle — transmitted and diffracted sound RADIATES"*
> *"ref: write the model down — the document whose absence cost a day"*
> *"The gate was wrong: passivity replaces reach, and the calibration was in the wrong place"*
> *"The renderer was evaluating a DIFFERENT MODEL than the gates, and summing coherent copies"*
> *"Four routed-leg formulations measured; the orphan gate may itself be wrong"*

The model that emerged is genuinely sophisticated: portal graphs seeded from air pockets
around a speaker, Dijkstra routing between rooms, Fresnel-disc transmission treated as an
area integral, a diffuse field coupled through apertures, per-arrival delay producing cave
echoes, and bottom-up region segmentation that discovers rooms and corridors with no grid.
It was then ported to native C in five stages, each verified against the reference, finishing
at **0.02 dB agreement**.

And then it was replaced.

### August 20: "One field, centred on the listener, read by every source"

The trigger was blunt. The user A/B'd the models in-game and reported the new one
*"completely broken on native, works on legacy"* — and added a requirement that killed the
whole per-speaker approach: **it must handle moving sources, because zombies get sound
later.**

The replacement, in the project's own words:

> There is ONE pair of ears and there may be fifty noise-makers, some walking. So compute
> nothing per source: flood ONE field from the listener a few times a second; a source looks
> up where it stands. Attenuation is symmetric, so a listener-anchored field answers for a
> source exactly.

Measured: **0.11 µs per source lookup.** A 34,000-cell flood in 7.3 ms on a background
thread. Two tuning knobs total.

### ▸ Why this is the best decision in the project, and why it hurt

The topological model was *more correct*. It reached exact parity in native C. It modelled
diffraction and reverb coupling properly. And it lost.

It lost because a live game is not a scenario file. Sources move. Chunks stream in and out.
Doors swing while you're reading the geometry. Every one of those is a *cache invalidation*
problem for a per-speaker model, and the preceding week's bug list is almost entirely cache
staleness — including one measured case where native handed back stale geometry for **33
seconds straight**.

The listener-anchored field doesn't solve those bugs. It makes them impossible:

> **Nothing persists long enough to go stale** — a door is just in the next flood. No
> patching, no settling gate, no handover, no second model. The week's bug class cannot
> occur, rather than being fixed.

That distinction — *cannot occur* versus *is fixed* — is the most valuable sentence in the
repository. And the decision cost a month of correct work, which is exactly why most
projects don't make it.

There's a second, quieter design win in there. The field carries **excess** attenuation only
— what the building adds over a clear line — and leaves distance to the mixer. So clear line
of sight is 0 dB *by construction*, not by tuning. That's a whole category of "why does it
sound muffled standing right next to the speaker" bugs designed out rather than debugged.

### Doors became apertures (August 22)

A door's leaf cannot be read while it moves: the game blanks its colliders for the entire
swing. Measured — a door at 29.5% through its animation reports no collider at all while its
leaf still fully spans the cell.

So the door stopped being a leaf and became an **aperture**: the plane is measured once while
readable and remembered, and the openness ramp supplies how much of it is blocked. Fixed
geometry, moving number.

The prior attempt — synthesising a leaf position from the door's rotation — was worse than
nothing. Its guessed slab at 0.5 against a body at 0.480 put the door on the *wrong side of
the listener*: one frame after the latch, player standing still, a far-side speaker jumped
from 14.9 dB occluded to 0.0 dB.

> *"A door with no remembered plane now gets NO plane — the isotropic lump is blunt but
> symmetric, and a guessed plane is confidently wrong in the one direction you are
> listening."*

The project notes, correctly, that fixed-geometry-moving-number is what Wwise, FMOD and
Project Acoustics all do. It arrived there independently, by measurement.

### The defects, which are excellent on their own

- *"Furniture is not air: a model entity's collision flags and bounds both lie."*
- *"An unloaded chunk is not open sky: the scene was rebuilding into a different world."*
- *"A week on the wrong speaker: the nearest-point diagnostic reported the LAST station."*
- *"The listener was half a block off: 4 dB of occlusion standing next to a speaker."*
- *"A floor is not decorative trim: 90% of every partial floor plate was being discounted."*
- *"woodShapes:cube was charged HALF a wall: ask the variant, not the shape class."*
- *"A metal door is not a metal wall: cap what a shut door is made of."*
- *"A 3-wide cell cannot hold a 1-block wall: cells go to 2x1x2."*
- *"My earlier 'open air' tests floated 4 blocks up — the version of open air nobody stands in."*

That last one is the most instructive failure in the project: the test was passing because
the test was wrong, in a way that only a human standing on the ground in-game would ever
notice.

---

## Act VI — The library, and the things that take longest
### August 20 – August 23

### Every track plays before any track repeats (August 21)

The old `RandomNoRepeat` remembered exactly **one** track. Over a 200-track folder that lets
a track return on the very next pick, and the birthday effect makes an early repeat likely
within a session. A large soundtrack didn't *feel* large.

The replacement is a per-context bag. Three details are worth the detail:

**It commits at PLAY, not at pick.** Three of the six callers that pick a track are
speculative — the staged pin, the pre-staged advance, the missing-file replacement walk —
and their picks are routinely discarded. A bag consumed at pick time would drain without a
note being heard.

**`FavouriteWeight` now scales with folder size.** As a raw multiplier it meant "every 45
minutes" at 40 tracks and "every 19 hours" at 1000. Now measured constant at **5.7% of
airtime from 50 tracks to 5000**.

**Selection allocates nothing.** It walks the pool twice rather than building a candidate
array, which past ~10,600 tracks was a large-object-heap allocation on every pick.

### ▸ Why the FavouriteWeight fix is the most user-respecting change in the release

A setting must mean the same thing regardless of library size. If "favourite" means
noticeably-more-often at 40 tracks and never at 1000, then the setting is lying to the larger
half of the userbase — and worse, it's lying *silently*, in a way no user could diagnose.
Scaling it against a 50-track reference makes the promise size-independent.

The commit-at-play decision is the same instinct at a lower level. It's the difference
between a feature that works and a feature that works *and can be reasoned about*.

### Config files stopped being shipped (August 21)

Updating the mod had been overwriting every setting the player had chosen. Now the files are
generated on first run and migrated by schema — **133 settings, one table describing all of
them**, with `since:` for additions and `changedIn:`/`was:` for changed defaults.

### Music packs became ordinary mods (August 21)

Requested by a soundtrack author, who called it *"the single feature I'd request."*

Previously a soundtrack had to be copied *into* CMM's own music folder, which made CMM and
the soundtrack one blob on disk — exactly what a mod manager cannot cope with. Now every
installed mod's `Music` folder feeds the rotation. Under MO2 or Wabbajack, CMM and a
soundtrack stay separate mods with independent pages, versions and provenance; updating a
soundtrack is a normal mod update instead of a hand-merge.

Notably, the planned design had mods *declare* a music root via a small XML. The shipped
version has no registration file at all — the convention is simply `<mod>/Music`. Same
instinct as radio stations: make the filesystem the data model and there is nothing to
misconfigure.

### The radio dial

Worth reading as a sequence, because it's a study in knowing when to stop:

> *"The radio click was a pop, not a switch: rebuild it out of damped modes"*
> *"Clicks were tonal because I was hitting the resonators with an impulse"*
> *"Put the contact back: a deterministic strike on top of the noise"*
> *"Chatter and saturation for grit — and four measurement bugs found on the way"*
> *"Fit the clicks to two reference recordings, and lift the clamp that made it impossible"*
> *"Recorded clicks: a Sounds folder, and a different rate every firing"*

Synthesis got close and then stopped converging, so the final answer is **recorded samples**
— held in RAM, varied ±2000 Hz per firing so no two are identical, and sitting in a `Sounds/`
folder players can replace with their own.

### Tuning between stations

> *"The station was fading up out of the noise, which is the one thing a radio never does"*
> *"The arriving station is MANGLED, not faded"*
> *"Between stations is a ROAR, not a hiss: drop the low cut that was killing its body"*
> *"A receiver's noise floor is FIXED: stop scaling the static by the track's loudness gain"*

Getting there required building `CmmRadioSim` (renders a station change offline through the
*shipping* DLL) and `CmmRadioLab` (a UI that renders game against reference and A/Bs them).
Settled numbers: leave 700 ms, static floor 100 ms, arrive 1000 ms, knee 0.90, noise 0.15.
Press to tuned ≈ 1.8 s, with break-up starting **75 ms** after the key.

### ▸ Why build a whole offline simulator for a sound effect

Because in-game listening had already cost weeks — twice — and the project worked out why.

The first cause: `Config/radio.xml` held hand-edited values that **no migration could ever
reach**, because migration only rewrites a key still holding a recognised former default.
Every in-game listening round for weeks was spent judging a sound *nobody had approved*.

The second: *"A demo is not a keypress."* The tuning had been settled on rendered demos,
where nobody presses a button and waits — so the two-thirds of a second of dead air after
the keypress was invisible in every test that approved it.

The acceptance test is now that the offline simulator and the reference renderer agree — they
currently match to a mean 1.0 dB — with the explicit instruction to disable wander and
flutter before comparing, because wander is ±4 dB by design and swamps a 2 dB structural
difference.

---

## Six principles the codebase actually follows

Not stated as a manifesto anywhere. Inferred from what the code does, and in three cases
written down in the project's own review notes.

**1. Make bugs impossible, don't fix them.** The listener-anchored field's real argument is
that nothing lives long enough to go stale. The embedded fallback `context.lua` means a bad
edit *can't* kill audio. Excess-only attenuation means clear line of sight is 0 dB by
construction.

**2. One model, never two that can disagree.** The C# resolver was *deleted* at the Lua
cutover rather than kept as a fallback. The acoustics audit's recorded verdict: *"a fallback
must be 'freeze the last published value', never 'compute the same quantity a different
way'"* — after three uncalibrated models switching live were measured **14 dB apart** on the
same wall.

**3. No compensation constants.** From the same review: *"If it sounds wrong, find the wrong
thing; never add a coefficient."* Followed through in August, when four shape discounts were
deleted at once because *"all four were smuggling WHERE into HOW MUCH."*

**4. Nothing may vanish.** Every term has a value or fades to one. This came from a real
defect: the diffuse term switched off entirely at a threshold, producing 8–12 dB on/off from
a `floor()`.

**5. The filesystem is the data model.** Stations are folders. Music packs are folders. No
manifest can disagree with the disk if there is no manifest.

**6. A setting must mean the same thing everywhere.** FavouriteWeight scaling with library
size; occlusion in dB rather than virtual-distance units, so it doesn't change meaning when
range changes.

---

## The recurring failure modes

The project is unusually candid about these, and the same three keep recurring.

**A changed default reaches nobody who already has the file.** It bit three times in one day
across three different config files. `WorldEmitterDoorMaxLoss` was added with `since: 1`
against files already at schema 17, so it *has never existed in any upgraded install* — which
is almost certainly why that knob "was never heard" despite being tried repeatedly.

**A guard nobody reads is worse than no guard.** The config test printed the DoorMaxLoss bug
hours before it was found by hand, and it was waved off as stale-baseline noise — because 34
*other* failures were also noise. The project's own conclusion: *"Fix the baseline or the net
is decorative."*

**Silent fallbacks hide shipping bugs.** The release zip went out without the dial sounds for
want of one `CopyToOutputDirectory` — and failed silently *by design*, because a missing
sound falls back to the synthesised click. Debug installs had them (a different build target
force-copies), so every machine anyone tested on was fine and only the zip was wrong.

### ▸ Why these three are the same bug

All three are cases where **the system's default behaviour on failure is to look fine.** A
missing setting runs on a compiled default. A noisy test suite reads as "known noise." A
missing asset falls back to a synthesised one. Each is individually a reasonable robustness
decision, and collectively they form a system that cannot tell you when it's wrong.

The recorded lesson — *"open the artifact"*, i.e. list the actual shipped archive rather than
trusting the build — is the right generalisation.

---

## Where it's going

Drawn from `TODO.md`, the release-candidate handoff notes, and the shape of the work.

### Immediately before 1.0.5 final

Three shipped changes **nobody has listened to yet**: the emitter speaker voicing (0.5 shelf
/ 0.9 ham), the three-stop combat-music toggle, and the context-music loudness hold. The last
is flagged as the highest risk in the project, and the reason is exact: *"A fault here is
silence, the worst failure this mod has."*

Then a levels pass. Every world-emitter level knob was set by ear **while occlusion was
leaking** — a route could pass diagonally between two blocks meeting at a corner and pay
nothing. Measured at one speaker: 2.3 dB before the fix, 9.0 dB after, same spot. So those
numbers aren't preferences; they're compensations for a bug that no longer exists, and
they're all wrong in the same direction. One has already been corrected (`WorldEmitterVolume`
0.5 → 1.0). The rest need ears.

### 1.0.6, as currently scoped

- **Occlusion output shaping ported into the field** — described in the TODO as *"THE LAST
  THING the audio engine needs."* Two curves: a soft knee so detour and gaps can never fully
  silence a speaker (stand behind a crate, still hear it), and a hard gate on the *solid*
  term only, so enough material produces true silence (a sealed vault). Detour can never
  silence; material can.
- **Door fine-tuning.** The doors are now *right* — which side, and that they block at all —
  but not yet *good*. The known issue: a door's gap is measured along its largest dimension,
  which for an ordinary door is its **height**, so it reads acoustically fully open at 40% of
  its swing. A drawbridge is fully open after 10%.
- **Stereo pairs.** Lone speakers are already mono; what's unbuilt is two speakers of the
  same station close together each taking one channel, L/R assigned by lexicographic world
  position so it's stable across ticks, sessions and multiplayer clients.
- **Emitter squelch** — a distance- or signal-dependent breakup so a distant world radio
  degrades *like a radio* rather than merely getting quieter.
- **A continuous FM dial** — sweeping into a station rather than stepping, with partial
  signal and partial noise following how close you are.
- **A DJ system**, Fallout-style, listed as an idea.
- **Four field-audit defects**, already diagnosed and ranked but unfixed.

### ▸ Where I think it actually goes

**The stated 1.0.6 list is polish. The interesting signal is one line in a design note:**
*"it must handle MOVING sources, because zombies get sound later."*

That requirement is why the per-speaker acoustic model was abandoned. Nothing in the shipped
feature set needs it — speaker blocks don't walk. It was a constraint imposed on the
architecture for something that hasn't been built.

Combined with the ambition to *"convert anything to C that can be converted"*, and a
spatializer, reverb and occlusion field that are all already source-agnostic, the trajectory
is fairly clear: **CMM is becoming a general game-audio engine that currently happens to
play music.** The acoustic model does not care whether the thing making noise is a jukebox
or a zombie behind a door. A listener-anchored field costs the same for fifty sources as for
one.

The **diegetic authenticity pass** on the roadmap points the same way — *"emitters are
functional but far from satisfactory as in-world objects."* That's not a music goal. That's a
world-simulation goal.

If I'm right, the interesting question for the project isn't which features land in 1.0.6.
It's whether a mod can replace a game's entire audio presentation from the outside, across
four game versions, without the game's cooperation. The last twelve weeks say the hard parts
— threading, decoding, spatialisation, occlusion, calibration — are already solved.

**The risk is elsewhere, and the project has already named it.** Every one of the recurring
failure modes above is a *feedback* problem, not a capability problem: changed defaults that
reach nobody, guards drowned in noise, silent fallbacks. As the surface grows from "music" to
"all audio", the number of knobs that can be silently wrong grows with it. The single highest
-leverage item on the whole list is the unglamorous one — regenerate the schema baseline so
the config test stops crying wolf. Everything else is downstream of being able to trust your
own instruments.

---

## Interview questions

1. You threw away a month of acoustics work that had reached 0.02 dB parity in native C.
   What made the simpler listener-anchored field the right call?
2. There's a note that the field means a whole class of bug *cannot occur* rather than being
   fixed. Is that how you think about design generally?
3. Why leave Unity's audio system entirely instead of working around the stutters?
4. The C#/C boundary is drawn at how *often* code runs, not by subject. How did you arrive
   at that?
5. The music-selection policy is a 300-line Lua script players can edit. What made you take
   the most contentious part of the mod out of the compiled code — and then delete the C#
   version rather than keep it as a fallback?
6. What was harder: modelling how sound gets through a door, or making a radio click sound
   like a radio click?
7. You found that the game's world clock runs at 0.89× realtime. How do you even start
   looking for something like that?
8. Twice, weeks went into tuning something nobody had actually approved — once because a
   config file silently overrode the code. What changed in how you work after that?
9. One download runs on four game versions spanning years of API drift, including enum
   ordinals that moved. How?
10. Zombies with positional audio — is that where this is heading?

---

## Timeline

| Date | Milestone |
|---|---|
| Jun 2 | First commit |
| Jun 5 | *"Contexts changing too quickly"* — the core problem, on day four |
| Jun 6 | POI detection, blood moon; hot-reload promoted from bug to feature |
| Jun 10 | NAudio added *(removed 11 weeks later)* |
| Jun 23 | **1.0.1** — one binary for A20 / A21 / 2.6 / 3.0 |
| Jun 26 | **1.0.2a/b** — biome combat music; day/night warmup groups |
| Jun 26 | Context resolution ported to Lua and cut over in a single day |
| Jun 26 | **1.0.3** — EBU R128 loudness normalization |
| Jun 28 | NAudio replaced by native `CMM_Decoder.dll` |
| Jun 29 | Tracks buffered to native memory; folders no longer pinned |
| Jun 30 | **1.0.4** — native mixer becomes the sole audio output |
| Jul 1 | Mixer and loudness ported to C; music survives loading freezes |
| Jul 2 | Reflection bridges; the last per-version fork removed |
| Jul 3 | **1.0.5 line opens** — playlists, incremental crawling, per-section vanilla control |
| Jul 4 | Music plays from speakers and radios in the world |
| Jul 8 | Door acoustics |
| Jul 10 | Personal radio; stations as folders; server-synchronised broadcast |
| Jul 11 | Radio sync root cause measured — worldTime runs at 0.89× realtime |
| Jul 18 | Dedicated-server join freeze fixed |
| Aug 8 | Retarget to 3.1.0 b14; the game's Overall volume slider finally reaches CMM |
| Aug 13–19 | Acoustics rebuilt from spec; ported to native C at 0.02 dB parity |
| Aug 20 | Listener-anchored field replaces it and becomes the default |
| Aug 21 | Anti-repeat rotation; config files stop being overwritten; music packs as mods |
| Aug 22 | Doors become apertures rather than leaves |
| Aug 23 | **v1.0.5-rc1 tagged** — radio tuning settled against an offline reference |

---

## A note on version numbers

The public upload numbers and the mod's own version don't line up. Uploads 1–8 track version
bumps roughly 1:1; uploads 9–14 are all one line, informally "1.0.4b", because 1.0.5 spent
seven weeks in development. `ModInfo.xml` has read `1.0.5` since **July 3**, which means a
July development build and the August release candidate report the same version string.
Worth knowing before quoting a version number, or before trusting one in a bug report.

---

*Sources: the CMM git history (403 commits, 2026-06-02 → 2026-08-23), `CHANGELOG.md`,
`TODO.md`, `ModInfo.xml`, and the project's design notes. Every quoted line is a real commit
subject or a verbatim line from the project's own documentation. Sections marked ▸ Why are
the analysis of an outside reader, not the author's stated reasoning.*
