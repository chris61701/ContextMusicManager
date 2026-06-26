# Changelog

All notable changes to Context Music Manager are documented here.

## 1.0.3

### Added

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

### Changed

- **POI infested→cleared handoff is now continuous.** All POI contexts (infested and
  non-infested) share `warmgroup="poi"`, so when the last sleeper dies the non-infested POI
  context inherits the group's warm-up start instead of restarting its own dwell. Ambient no
  longer leaks through the gap while the `POI_Cleared` stinger fades.

### Fixed

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
