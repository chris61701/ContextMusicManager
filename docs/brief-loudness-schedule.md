# Brief: deterministic track queues, station clocks, and a session-scoped loudness cache

Working brief for design agents. Everything under "Verified" was checked against the code or
measured on 2026-08-28. Do not take it on trust, but do not re-derive it either — if your reading
disagrees, SAY SO. A contradiction is worth more than agreement.

## 1. What we are building

Five changes that only make sense together:

1. **Session-scoped loudness cache.** A measurement lives for the session. At next launch the file
   holds ONLY tracks the user altered (manual trim / ban / favourite). Rationale is the user's: no
   clutter left behind in `Cache/loudness.xml`.
2. **Contexts get a committed 3-deep queue.** Instead of guessing what plays next, draw the next 3
   from the rotation NOW and commit, so what to measure is known exactly.
3. **Every radio station keeps a live clock** — what is on air and how much remains — for all
   stations, not just the tuned one or ones with a speaker nearby. No decode, no playback.
4. **Every station keeps on-air + 2-3 measured**, across all stations.
5. **One serialized, priority-ordered background worker** doing duration probing AND loudness
   measurement in a single pass per file. The listing surfaces in the existing Alt+Num8 coverage
   panel.

## 2. Verified current state

**The loudness cache is evicted to disk every second.** `LoudnessStore.RetainOnly` prunes
`_byPath`/`_byHash` to a keep-set; `SnapshotForSave` then serializes whatever remains in memory as
the ENTIRE file. So eviction is a delete from `loudness.xml`. Live install right now: **33 tracks,
10.8 KB, against a 3,528-track radio library.** `IsPersistWorthy` exempts trimmed / banned /
favourited tracks only.

Keep-set (`ContextMusicManager.Loudness.cs:320-345`, rebuilt at 1 Hz): per context — playing,
last-played, staged, play-history ±1; plus everything in `_loudnessInFlight`; plus
`EmitterAudioLayer.AppendLoudnessKeep` (per station WITH A SPEAKER IN RANGE: on-air + N); plus
`PersonalRadio.AppendLoudnessKeep` (TUNED station only: current track + on-air + N).

**Stations already have a deterministic schedule.** `RadioStationCatalog.ResolveAt(st, B)` returns
the on-air file, index and offset for ANY station at ANY broadcast time, with no playback. It is a
cache-only dictionary walk WHEN track lengths are already cached; otherwise it returns false with
`WalkPending` and kicks `EnsureWindowWalk` (a ThreadPool job doing header opens via `ProbeLength`).
Lengths live in `_lenFrames` / `_lenRate` under `_lenLock`.

**Far stations are never resolved.** `EmitterAudioLayer.MaintainOnAir` skips any station with
`st.NearestDist == float.MaxValue`, so a station you are neither tuned to nor standing near has no
clock and no window walk at all.

**Contexts do NOT get a real 3-ahead.** `AudioPlayer.cs:742-757`: the exact next track is measured
deterministically, then `LoudnessMeasureAhead - 1` MORE are drawn **uniformly at random from the
whole folder snapshot** — not from the eligible set, and not the tracks that will actually play.
Those random picks are also absent from the keep-set, so they are evicted about a second later and
re-drawn next time. Measured, discarded, repeated indefinitely.

**Track rotation is a shuffled bag, not a permutation.** `TrackRotation`: a track is eligible until
it has had its allowance; when nothing is eligible the cycle refills. Public surface is only `Roll`,
`ScaledWeight`, `Allowance`. There is no accessor for the eligible set and no reservation concept.

**One OS thread per measurement.** `ContextMusicManager.Loudness.cs:603` creates `new Thread(...)`
per call; in-game these serialize through `_measureGate` (`:595`, `:607`). So a naive
`foreach (station) RequestPrewarm(...)` creates N threads all blocked on one lock.

**`HashFile` reads the whole file** with a per-byte managed FNV loop, before any decode.

**Catalog rebuild** (`RadioStationCatalog.EnsureCatalog`): on source-string change, else throttled
by a directory-signature stat plus a `ContentHash` fallback for sloppy-mtime filesystems (the music
drive is exFAT — directory mtimes do not bump when children change). Live emitter voices survive a
rebuild by station NAME; anything not migrated is released.

## 3. Measured ground truth (2026-08-28, this machine)

Full loudness measurement, by format:

- MP3 ~0.09 s per minute of audio (60 min file = 5.4 s)
- AAC ~0.20 s per minute (120 min file = 24.2 s); real 245 kbps podcasts ran ~0.30 s/min

Sampling six 60-second windows spread across the file, versus a full measure:

- **AAC/MP4** 120 min: 24.2 s to **1.3 s** (18x faster) — seeks are indexed
- **MP3** 60 min: 5.4 s to **3.2 s** (40% faster) — no seek table is ever bound, so
  `drmp3_seek_to_pcm_frame` brute-forces. It seeks FORWARD from the current position and windows
  advance monotonically, so the cost is about one full decode, not six.
- **MKV** 60 min: 5.4 s to **11.4 s** (2.1x SLOWER) — `cmm_container_seek` tears the demuxer down
  and re-decodes from offset 0 on every seek
- Accuracy on 9 real podcasts: within **±0.5 dB** of the full-file value

Sequential (non-spread) chunking converges slowly and is biased. Worst error vs final: 2.54 dB at
30 s, 2.19 dB at 60 s, 1.82 dB at 2 min, 1.15 dB at 5 min, 0.60 dB at 10 min.

**Actual library:** 3,528 radio tracks — 42% FLAC, 40% MP3, 17% M4A, no MKV/WebM. 13 of 16 stations
are `.m3u` files pointing at `H:\`. So the container penalty is a guard to write rather than a
problem we have; and 40% of the library gains little from sampling but is cheap to measure anyway.

`ComputeGain` uses integrated LUFS only. `Peak` is stored and never used for gain.

## 4. The hard part: mutation

The five changes are easy in a still world. Everything interesting is what happens when the world
moves under them. This is what the agents are for.

Sources of change, all of which already happen at runtime:

- A file is added to or removed from a context folder (file watcher, ~2 s)
- A music pack mod is added; its folders merge into existing contexts
- A track is added to or removed from an `.m3u` while the game runs
- A station folder appears or disappears; a station is renamed
- The catalog is rebuilt (station objects are REPLACED; migration is by name)
- A file is edited in place — same name, new content (size/mtime guard)
- The music drive disconnects (13 of 16 stations point at `H:\`)
- The user bans or favourites a track, or sets a manual trim
- The user skips, or retunes, mid-track
- Chunk / world load, and world exit

For each, the design must say what happens to: the **station schedule**, the **committed context
queue**, the **measurement work queue**, the **loudness cache**, and anything **currently playing**.
A track that is on air, queued, mid-measurement and in the keep-set at the same moment, and is then
deleted, is the case that decides the design.

Tensions worth naming explicitly:

- A committed queue is a promise. A ban must break that promise immediately; a file appearing
  should not (it can join the next draw).
- Reserving 3 tracks from the bag without counting them as played needs a state the rotation does
  not currently have. Getting it wrong quietly breaks "everything plays once before anything
  repeats" — the guarantee the rotation exists for.
- Station schedules are ANCHORED to a broadcast clock so every client agrees. Changing a station's
  pool changes what is on air at a given time. What does a listener mid-track hear?
- The loudness cache is content-hash keyed and shared across paths. Session-scoping changes when
  entries die, but must not change that identity.

## 5. What the agents are asked

Three briefs, deliberately overlapping at the seams:

**A. Queues and schedule.** The committed context queue and the all-stations clock. Reservation
semantics against the bag; the full mutation matrix above; what is recomputed versus stored; what a
listener hears when the thing they are listening to is removed.

**B. Work scheduling and cache lifetime.** The single serialized worker: priority tiers, what "low
priority" means in measurable units, pause conditions, the failure memo. Session-scoped cache: what
persists, what an entry's identity is, how eviction interacts with in-flight work, and what the
first minutes of a session cost with nothing cached.

**C. Adversary.** Attack the design. Where does the mutation matrix have a hole; which failure is
silent; what is over-built; what would you cut. Explicitly invited to answer "do less".

## 6. Traps already paid for — do not rediscover

- A changed default reaches nobody who already has a config file; only `since:` / `changedIn:`
  schema entries migrate. New settings need a schema entry.
- `Config/contexts.xml` is written once and then owned by the user; code defaults never reach an
  existing install.
- The music drive is exFAT: directory mtimes do not bump on child changes.
- Concurrent native file opens once crossed msvcrt FILE* buffers and silently failed on FLAC.
- An earlier prewarm/eviction interaction re-measured one track 295 times.
- Instruments that grow with the session forge the leak they are measuring.
- A warning that fires on a transient state teaches users to ignore warnings.

## 7. Edge cases the design must survive

Answer each of these explicitly. "It probably works" is not an answer; say what happens.

**Extremely short files.** Stingers are seconds long; some are well under one second. EBU R128
integrates over gated 400 ms blocks, so a file shorter than 400 ms produces ZERO blocks and has no
defined integrated loudness. What gain does such a file get, and does the answer differ between a
100 ms stinger and a 3-second one? Note `Death`, `Spawn` and `POI_Cleared` are PlayOnce stingers
that already bypass most of the pipeline.

**Extremely long files.** Hours. A committed 3-deep queue whose first entry lasts two hours means
entries 2 and 3 are measured now and not needed until much later — is that waste, or insurance?
Does a station whose whole pool is two-hour files behave sensibly when the schedule window is one
hour (`RadioAnchorSeconds = 3600`)?

**Very short files in a station pool.** The inverse: a station of 20-second clips advances its
schedule constantly. Does the clock resolve correctly, does the work queue keep up, and does the
"on-air + 2-3 ahead" set churn faster than it can be measured?

**Rapid context switching.** Measured: 39% of transitions interrupt a fade; worst burst was 3
transitions inside one 2-second window. With committed queues, does a context that is entered and
left repeatedly re-draw each time (wasting the commitment) or keep its queue across visits? What
happens to the work queue when the priority order changes several times per second?

**A file removed while it is everything at once.** On air, in a committed queue, mid-measurement,
in the keep-set, and playing — then deleted. Say exactly what happens to each.

**A file added.** Does it join the current cycle or the next? Does it invalidate a committed queue?
If a music pack adds 500 tracks to a context mid-session, what happens?

**A station gains or loses tracks.** The schedule is anchored to a broadcast clock, so the pool
determines what is on air at a given time. Removing one track shifts everything after it. What does
a listener currently mid-track hear? What about a listener on a DIFFERENT station in the same pool
(stations share the length cache)?

**A station is renamed.** Live voices migrate by NAME across a catalog rebuild. What happens to its
clock, its queue and its measurements?

**The drive disconnects.** 13 of 16 stations point at `H:\`. Every file open fails. What does the
work queue do, what does the schedule do, and what does the user see? What happens when it comes
back?

**The same file in several places.** A track can be in multiple stations and contexts at once. The
loudness cache is content-hash keyed, so one measurement serves all of them — confirm nothing in
the new design assumes one owner per file.

**Duplicate content, different paths.** Same audio, two filenames. Hash-keyed cache handles it;
does the work queue avoid measuring it twice?

**Session start with nothing cached.** 3,528 tracks. What is measured in the first 30 seconds, the
first 5 minutes, and what does the player experience while that happens?
