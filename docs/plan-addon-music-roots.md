# Add-on music directories — implementation plan

**Goal.** At startup, look inside every loaded mod's folder. If it has a `Music` folder whose
subfolders match our contexts' sources, those tracks join the rotation. Multiple mods may
contribute; all of them are watched. Enabled by default; a hard `false` in `settings.xml`
restricts CMM to the single folder `MusicPath` points at.

**Why it matters.** A soundtrack author ships their music as its own MO2/Wabbajack mod instead
of overwriting CMM's install, and small packs can cover just a few contexts.

**Status.** Plan only — no code written. Reviewed by four research agents and two adversarial
reviewers; §7 lists what the review changed, because several of those items would otherwise
have shipped broken.

---

## 0. What the research found

The findings that actually shape the design. Everything here was read out of the code, not
assumed.

1. **`MusicDirectory` is a bare `public static string` read at 16 sites**, eight of which inline
   `Path.Combine(MusicDirectory, ctx.Source)` at a scan site. Only 2 of 8 scan sites route
   through the `MakeScanItem`/`EnumerateScanItem` pair.
2. **There is no invalidation on a root change today.** Editing `MusicPath` re-points
   `MusicDirectory` but nothing clears `directorySnapshot`, `_crawl`, or the watch timestamps —
   the repoint key in `ApplyConfigReload` is `(Source string, IncludeSubfolders)` and the
   *resolved directory* is not part of it (`ConfigWatch.cs:105`, `:136`). Pre-existing bug;
   fixed first, on its own.
3. **`LoudnessStore.ToRelKey` cannot express "which root"**, and its inverse `KeyToAbsPath`
   tells relative from absolute *by string shape*. Teaching it a second root would silently
   corrupt the ban/favourite mirrors. **Verified: the absolute fall-through already round-trips
   correctly** through save, load, `RebuildDerivedPathSets` and `RetainOnly` — so add-on tracks
   need no loudness change at all.
4. **`ModManager.GetLoadedMods()` is identical on A20/A21/2.6/3.0/3.1**, as are `Mod.Path` and
   `Mod.FolderName`. The mod list is fully populated before any mod's `InitMod` runs (two-phase
   `LoadMods`). **`Mod.Name` does not exist on A20** and needs the `StringAccessor` bridge.
5. **There are no `FileSystemWatcher`s anywhere.** Everything polls: a 2 s scheduler tick for
   contexts and config, an incremental crawler for recursive contexts, a separate 2 s check for
   the radio catalog.
6. **`RadioStationCatalog.ContentHash` does a full recursive tree stat on the Unity main
   thread**, up to every 2 s — the only watcher with no `ThreadPool` hop.
7. **Track pools are `string[] directorySnapshot`, swapped by reference, never mutated.** That
   gives a free, complete "the pool changed" test — `ReferenceEquals` — already used for the
   all-banned memo. Everything downstream of the scan works in absolute paths and does not care
   which root a track came from. **Verified** across `_invalidFiles`, `_validatedFiles`, play
   history, staging and the loudness derived sets.

---

## 1. The design

### 1.1 One immutable snapshot, published behind one volatile field

The naive `string[] All` + `int Generation` pair is unsound: a reader can pair `All` from
generation *G* with `Generation == G+1`. One object, one volatile field, read once per
operation into a local.

`Harmony/MusicRoots.cs`:

```csharp
internal sealed class RootSet
{
    public readonly string[] Roots;         // [0] = primary (MusicDirectory), immutable
    public readonly int      Generation;
    // Resolved ONCE per generation, off-thread. Makes ResolveDirs a pure lookup with no IO.
    public readonly Dictionary<string, string[]> DirsByContext;
    public readonly Dictionary<string, bool[]>   SloppyByContext;   // per-dir, per IsSloppyMtimeFilesystem
}

internal static class MusicRoots
{
    private static volatile RootSet _current;
    internal static RootSet Current => _current;
    internal static string[] ResolveDirs(RootSet rs, Contexts ctx);   // lookup, no IO
    internal static void Publish(RootSet candidate);                  // MAIN THREAD ONLY
}
```

**Rule for every reader: `RootSet rs = MusicRoots.Current;` into a local, then use `rs` for the
whole computation. Never read `Current` twice in one operation.**

`DirsByContext` being precomputed is not an optimisation, it is load-bearing three times over:

- `BuildFileWatchDescriptors` runs inside `FileWatchTick`'s **blocking main-thread `Send`**,
  which is documented as doing no IO so it does not forfeit freeze-survival
  (`FileWatch.cs:570-572`). A `Directory.Exists` per root per context there would be ~300 stats
  per tick on the main thread at 20 roots. As a lookup it is free.
- A per-context subfolder appearing or vanishing (`<mod>\Music\Combat`, not the whole `Music`
  folder) becomes a **generation change**. Nothing else can see that — see §1.7.
- The existence probes land on the 10 s off-thread lane, where the work belongs anyway.

### 1.2 Resolution rules

| Source kind | Resolves to |
|---|---|
| Empty (`Vanilla`, `Silence`) | nothing — every existing empty-source guard still returns first |
| `.m3u` / `.m3u8` | exactly one path, `PlaylistSource.ResolveFullPath`, **unchanged**. A playlist is an explicit file list; add-ons do not extend it. |
| Absolute folder (`D:\Whatever`) | exactly one path, **unchanged**. Note this works *by accident of `Path.Combine`* — `Path.Combine(anyRoot, "D:\\Jazz")` returns `D:\Jazz`, so a per-root loop collapses to one entry on its own. Load-bearing; do not "clean up" to string concatenation. |
| Relative folder (the normal case) | `roots[0] + source` **always** (even if missing — this preserves the empty-folder → fallback behaviour byte for byte), plus `roots[n] + source` for each add-on root where that subfolder exists |

### 1.3 The safety property this whole plan rests on

> **When `Roots.Length == 1`, every new code path must produce byte-identical results to
> today.**

That is what makes a 30-site refactor of the scan layer safe to ship, and it is directly
testable — see Phase 0, which is more work than it looks (§2).

### 1.4 Discovery

Between `LoadContexts` (`ContextMusicManager.cs:118`) and `cacheMap` population (`:122`) —
**not merely "at Init"**. `RebuildContextsWithFiles()` at `:129` is the synchronous first scan
and it ends with `_contextsWithFilesDirty = false; _presenceDirty.Clear();` with nothing to
re-queue it. Discovery after that point means a context whose tracks live only in an add-on
reads as empty on frame one and stays empty — `Menu` in particular starts on the vanilla bed.

`LoudnessStore.Load` at `:119` does read `MusicDirectory`, but only to rebuild the ban/favourite
mirrors, and add-on keys are absolute — so discovery may safely sit after it. Written down here
so nobody "fixes" the order later.

```csharp
foreach (Mod m in ModManager.GetLoadedMods())      // identical across all 5 game versions
{
    string music = Path.Combine(m.Path, "Music");   // Mod.Path is mixed-separator — normalize
    if (!Directory.Exists(music)) continue;
    string full = Path.GetFullPath(music);
    if (SamePath(full, primary)) continue;          // user pointed MusicPath at a mod's Music
    if (SamePath(full, ourOwnMusic)) continue;      // CMM's own folder IS the primary
    roots.Add(full);
}
```

Dedicated-server containment is **free**: `HarmonyPatches.cs:25` returns before
`ContextMusicManager.Init` is ever called at `:39`. `CmmGuard` is a *patch-body* exception
guard and does not apply here — a plain `try/catch → CmmGuard.Report("MusicRoots.Discover", e)`
is the right shape.

`Mod.Name` is used **for logging only**, through a `StringAccessor<Mod>("Name")` bridge with a
`FolderName` fallback. Log both: real folder names are mangled by mod managers
(`AAD-GridlessZombies 11024 1.3 HbltJ5tJB` declares `<Name value="PathSmoothing" />`).

### 1.5 Root-set changes at runtime

The *mod list* is fixed once the game is running — mods cannot be added mid-session. But a
`Music` folder, or a context subfolder inside one, can appear or vanish. So: enumerate mods
**once** at init on the main thread (the only game-API call), then on a 10 s housekeeping-lane
tick, off-thread, re-probe the cached path strings **and** each `<root>\<context source>`, build
a candidate `RootSet`, and diff it against `Current`.

### 1.6 Invalidation: reuse the repoint recovery, do not invent a nuke

The first draft of this plan had an `OnRootsChanged()` that nulled every `directorySnapshot`,
cleared `_crawl`, and cleared the watch dicts. **That was wrong three ways** and the review
caught all three: it is a cross-thread data race, it opens a real silence window, and it leaves
pinned add-on paths live so the disable leaks tracks. Replaced with the path
`ApplyConfigReload` already uses for a repointed context (`ConfigWatch.cs:126-237`), which was
written for exactly this problem.

**Thread split** — the probe and the apply are different threads and must be different
functions:

- **Scheduler thread (10 s probe):** IO only. Build the candidate `RootSet`, diff, and if it
  differs `MainThreadDispatcher.Post(() => MusicRoots.Publish(candidate))`. Nothing else.
- **Main thread (`MusicRoots.Publish`):** volatile-swap `_current`, then recover. The
  `settingsChanged` disable path enters here too, so there is exactly **one** apply path.
- **Scheduler thread (`FileWatchTick`, top):** compares `Current.Generation` against its own
  `_fwGeneration` and clears **its own** dicts on its own thread. No cross-thread `.Clear()`
  anywhere.

An extra rescan if the two halves land in either order is harmless — the apply is idempotent.

**`MusicRoots.Publish` (main thread), per affected context only:**

1. Diff `candidate.DirsByContext` against `Current.DirsByContext`; take only contexts whose dir
   set actually changed. A pack shipping only `Combat` must not disturb `Menu`.
2. Purge every pinned path that no longer sits under one of that context's new dirs:
   `lastPlayedFile`, `stagedFile`, `resumePosition`, `lastLeftTime`, `playHistory`,
   `playlistFiles`, and the static `_preStaged` (`Interlocked.Exchange(ref _preStaged, null)`).
   `_preStaged` is not in `cacheMap` and `PreferPreStagedNext` validates only context /
   `_invalidFiles` / banned / `File.Exists` — a pinned add-on track passes all four.
3. Enumerate off-thread via `MakeScanItem`/`EnumerateScanItem`, then **swap**
   `directorySnapshot` to the new array. **Never leave it null** — see §1.6.1.
4. Playback recovery, the `ConfigWatch.cs:189-236` template: active context affected →
   `StopNativeIfActive(0.25f)` + `TriggerMusic`; parked on `Vanilla`/`Silence` and a context
   came back → reclaim.
5. `_previousPlayableContexts.Clear()`; `_contextsWithFilesDirty = true`.
6. **Crawler: do nothing.** `CrawlState.Dirs` is keyed by *absolute* directory and `FinishSweep`
   already prunes any key not in `Seen` (`Crawler.cs:407-416`), so a root add needs no clearing
   and a root removal self-heals on the next sweep. If faster recovery is wanted, use the
   existing thread-safe request channel `lock (_rescanLock) { _rescanAll = true; }`
   (`Crawler.cs:477`). **Never touch `_crawl` directly** — it is main-thread-only
   (`Crawler.cs:68`) and a `Clear` against `BeginSweep`'s insert is a dictionary corruption.
7. Radio: `ForceRebuild()` (**does not exist — must be added**) resets
   `_catalogSrc`/`_catalogSig`/`_catalogHash`/`_nextCatalogCheck` and lets the next
   `EnsureCatalog` do the work on the main thread. Do not call `RebuildCatalog` off-thread — it
   migrates live native emitter voices via `EmitterAudioLayer.OnCatalogRebuilt`.

#### 1.6.1 Why "swap, never null" — the silence window, traced

Nulling `directorySnapshot` on a **recursive** active context:

1. `_contextsWithFiles` is not cleared and `AllTracksBanned` returns `false` for a null
   snapshot (`Presence.cs:212`), so `ContextHasFiles` still returns **true** — the resolver
   keeps selecting the context.
2. `_contextsWithFilesDirty` does not help: `PresenceScanLoop` **skips recursive contexts**
   (`Presence.cs:364`).
3. Track ends → `PickNextFile` → `AudioPlayer.cs:911` `if (ctx.IncludeSubfolders) return null`
   → `AudioPlayer.cs:459-465` *"no file — fading to silence"* → `currentContext = Silence`.
4. Recovery waits on the crawler: `CrawlPassPause` 5 s, round-robined, and if `_crawl` was
   cleared the whole tree is re-listed from scratch.

So the active context reports files, cannot produce one, and drops to silence — seconds at
best, minutes on a large recursive library. Swapping a complete array instead of nulling
eliminates the window entirely, and costs nothing because pools are already reference-swapped.

Also note: nulling never *stops* audio — the playing voice is buffered in native memory
(`FileWatch.cs:479-483`). All the damage lands at the next pick.

### 1.7 The watch signature — a composite fold, **not** a max

The first draft said "max of `ContextWriteSignatureUtc` over the dirs". That is wrong.
`ContextWriteSignatureUtc` returns `DateTime.MinValue` for a missing dir (`SourceScan.cs:97`),
which is the **identity element** of a max fold — so a dir disappearing is absorbed silently.
Today, with one dir, that same sentinel is *informative* and forces a rescan. A max fold
destroys the property.

Concrete failure: delete `<addon>\Music\Combat`. `Generation` does not bump if it only watches
`<mod>\Music`. The fold is unchanged. `contentChanged` is 0 on NTFS. `previouslyEmpty`,
`recentlyTouched`, `neverScanned` all false ⇒ `FileWatch.cs:745` `continue`s ⇒ the deleted
pack's tracks stay in the snapshot **forever**.

```csharp
internal struct WatchSig { public long Fold; public DateTime Newest; public bool Faulted; }

internal static WatchSig ContextWatchSignature(string[] dirs, bool[] sloppy,
                                               bool includeSub, string[] folderPlaylistFiles)
{
    ulong h = 14695981039346656037UL;            // FNV-1a, same constants as FileSetContentHash
    DateTime newest = DateTime.MinValue;
    for (int i = 0; i < dirs.Length; i++)
    {
        MixString(ref h, dirs[i]);               // MEMBERSHIP + ORDER go into the fold
        bool exists = Directory.Exists(dirs[i]);
        // -1 is a sentinel no real Ticks value can collide with: present->missing is a fold
        // change even when another dir holds a later mtime.
        long ticks = exists ? Directory.GetLastWriteTimeUtc(dirs[i]).Ticks : -1L;
        MixI64(ref h, ticks);
        if (exists && ticks > newest.Ticks) newest = new DateTime(ticks, DateTimeKind.Utc);
        if (includeSub && exists) { /* fold subdir mtimes as today, per dir */ }
        if (sloppy[i] && exists)                 // PER-DIR branch, not per-context
        {
            try { MixI64(ref h, FileSetContentHash(dirs[i], includeSub)); }
            catch { return new WatchSig { Faulted = true }; }
        }
    }
    foreach (string pf in folderPlaylistFiles ?? _noPlaylistFiles) { /* fold mtime, as today */ }
    return new WatchSig { Fold = (long)h, Newest = newest };
}
```

Properties this has and a max fold does not:

- **Membership- and order-sensitive.** A root or context subfolder added, removed or reordered
  changes the fold even if no mtime moved.
- **Missing ≠ absorbed.** `-1L` is distinguishable from any `Ticks`.
- **Per-dir sloppy branch.** `IsSloppyMtimeFilesystem` is cached *per drive root*
  (`SourceScan.cs:216-241`), so with a primary on NTFS and an add-on on exFAT the branch
  genuinely differs per dir. Gating on `dirs[0]` means add-on changes are never detected;
  gating on `any(sloppy)` pays a full listing on every NTFS dir. Precompute `sloppy[]` into
  `RootSet.SloppyByContext` — it is a locked dict hit, not something to call N× per tick.
- **Fault semantics preserved.** Today a throw during the sloppy listing leaves
  `contentHash = 0` = "no change" so transient IO does not flap (`FileWatch.cs:731`). A partial
  hash inside a single fold would differ every tick and cause a rescan storm on a flaky or
  removable volume. `Faulted` ⇒ `continue` **without updating stored state**.

**State shape.** `_fwLastWriteTimes` + `_fwLastContentHash` (both keyed by context name)
collapse to `_fwLastSig` (`long`) + `_fwLastNewest` (`DateTime`, only for `recentlyTouched`).
One entry per context stays correct — the *reaction* is per-context, `ApplyFileWatchScan`
publishes one merged snapshot, and per-dir keys would need eviction and would make
`neverScanned` ambiguous.

**Two adjacent bugs to fix while in here:**

- `recentlyTouched` is `(UtcNow - t) < 6s`. A **future** mtime (clock skew on removable/network
  volumes, `robocopy /DCOPY:T`, a restored backup) gives a negative `TimeSpan`, which is `< 6s`,
  so it is permanently "recently touched". Today that costs one context a rescan every 2 s;
  folded over N dirs it forces a full N-dir re-enumeration every 2 s forever, with an exFAT
  root in the set. Clamp: `d >= TimeSpan.Zero && d < 6s`.
- `FileWatchLoop` (the `NativeScheduler=false` fallback) keeps its watch state in **coroutine
  locals** (`FileWatch.cs:21`, `:24`), unreachable from any invalidation. The two loops are
  mutually exclusive, so hoist them into the same statics and let `FileWatchInit()` own them.

### 1.8 Generation-stamping off-thread commits

Roots can change between a scheduler thread computing a signature and the main thread applying
the scan. Today the result is committed anyway: `ApplyFileWatchScan` **re-derives `dir` itself**
(`FileWatch.cs:258`) and unconditionally assigns `cache.directorySnapshot = currentFiles`
(`:378`, `:432`) — clobbering the invalidation with a pool built from the old root set, after
which per §1.7 the fold may never move again to correct it.

Two fixes, both needed:

1. **Carry the dirs, don't re-derive them.** Put `Dirs[]` and the `RootSet` reference on
   `FileWatchDescriptor` and pass them into `ApplyFileWatchScan`, so the enumeration and the
   signature agree by construction.
2. **Stamp every off-thread → snapshot commit**: `if (!ReferenceEquals(rs, MusicRoots.Current))
   return;` — drop the stale result, the next tick redoes it. Sites:

```
FileWatch.cs:378, :432        AudioPlayer.cs:900, :916   (PickNextFile lazy fill)
Presence.cs:389               ConfigWatch.cs:175
Crawler.cs:379, :439          ContextMusicManager.cs:780 (hot-swap fresh rescan)
```

### 1.9 The crawler

`CrawlState.Dirs` keyed by absolute dir means two roots coexist without collision, and
`SameFileList` is fine. Four things are not:

- **`SweepState.SloppyFs` is one bool per sweep, from one root** (`Crawler.cs:248`). Primary
  NTFS + add-on on exFAT ⇒ `SloppyFs = false` ⇒ the per-directory mtime skip is applied to the
  exFAT subtree, where dir mtimes never move ⇒ **add-on changes invisible for the life of the
  process.** That is test #15's exact configuration. Carry a per-root `bool[]` and tag stack
  entries with their root index — don't call `IsSloppyMtimeFilesystem` per directory in the
  worker's inner loop (it allocates and takes a lock).
- **`BeginSweep` clears the context when its dir is missing** (`Crawler.cs:207-224`). With
  §1.2's "primary always, even if missing", a context that exists *only* in an add-on root gets
  wiped and `_crawl` purged on **every** sweep, so its tracks appear in the partial publish and
  are wiped at the next `BeginSweep` — flapping playback. The gate must become "**no** root
  exists".
- **`Stack` is LIFO** (`Crawler.cs:154`, pushed at `:249`). Pushing `roots[0]` then `roots[1]`
  walks the add-on root entirely first, so the throttled partial publish (`:368-388`) publishes
  an add-on-only pool for as long as that takes — and `TryReclaimMenuOnVanillaBed` fires on it.
  Push in reverse.
- **`FinishSweep` reassembles by `state.Dirs.Values`** (`:419-421`) — Dictionary insertion
  order. Two interleaved roots make snapshot ordering non-deterministic across sweeps, and
  `TrackOrderMode.Sequential` indexes into that array. Latent today, routine under multi-root.
  Sort the assembled snapshot.

Also: in-flight sweeps hold `SweepState.State` as a **direct reference** to the `CrawlState`
object, so they survive any dictionary-level invalidation and `FinishSweep` publishes the old
root set's file list as authoritative (`Crawler.cs:434`). Stamp `SweepState` with its
generation and bail at the commit and publish points.

### 1.10 Claim prefixes

`GetClaimedSubtreePrefixes` enforces "deepest context owns the file". Under multi-root a
recursive parent walking `addon\Music\ambient` would swallow `addon\Music\ambient\poi`, because
the claim prefix computed from root 0 is `primary\Music\ambient\poi\` and does not match — and
the child, resolving under the same add-on root, lists the file too. One file in two pools,
double-counted in the loudness keep-set. Fix: for each root, resolve both contexts under
*that* root and union the prefixes.

**Compute claim prefixes from `Path.Combine(root, source)` for every root unconditionally — do
not reuse `ResolveDirs`' existence filter.** A child folder created between claim computation
and the scan would be unclaimed and get swallowed. A prefix that matches nothing is free.

Cost note not in the risk table before: `IsClaimedByDeeperContext` does `Path.GetFullPath(file)`
plus an O(prefixes) loop **per file**, in the crawler's inner loop. N roots × M child contexts
is a real multiplier at 20 roots.

### 1.11 What deliberately does not change

- **Loudness cache keys.** Add-on tracks fall through to `ToRelKey`'s absolute branch. Verified
  end to end: the key persists verbatim as `rel="c:/…"`, reloads verbatim, `KeyToAbsPath`
  detects it by `key[1] == ':'`, and `_bannedAbs`/`_favouriteAbs` are `OrdinalIgnoreCase` so it
  matches the real-cased path. Nothing assumes a key is relative. **Do not touch `ToRelKey`
  beyond the boundary fix below.**
  - Fix the boundary bug: `StartsWith(fullRoot)` has no separator check, so with a root of
    `…\Music`, `…\Music_Extra\a.mp3` produces the key `_extra/a.mp3` and round-trips to a path
    that does not exist. `DebugHud.DisplayPath` has the same bug. Two lines each. (Reachability
    is lower than it looks — the natural layout is `Mods\<mod>\Music`, a sibling — but fix it.)
  - **Write down the duplicate-file behaviour:** `ToggleFlag` mirrors a flag to *every* path
    bound to the same content hash, so banning a track present in both the primary and an
    add-on bans both copies. Almost certainly what you want; say so before it is filed as a bug.
- **All twelve `string.IsNullOrEmpty(ctx.Source)` guards stay.** They are the only thing
  stopping an empty source from enumerating a whole root — and with N roots it would be N whole
  roots. Do not tidy them into one.
- **Play/selection/history/staging.** Verified root-agnostic.

---

## 2. Phasing

Each phase is shippable and independently testable. Phases 0–2 contain all of the risk and none
of the feature.

### Phase 0 — the safety net. **Bigger than it looks; do not skip.**

The plan's whole gate is "the offline harness output is byte-identical". That gate is currently
worthless, because **the harnesses do not run the shipped code and have already drifted**:

- `tools/CmmScanTest` links exactly one file (`DataStructures.cs`). Its `Program.cs:255` is
  headed *"VERBATIM COPY"* and carries its own `GetClaimedSubtreePrefixes`,
  `IsClaimedByDeeperContext`, `NormalizedContextDir`, `EnumerateContextFiles`, and its own
  supported-extension set.
- The copy has drifted: the shipped `GetClaimedSubtreePrefixes` guards on `IsPlaylist`
  (`Presence.cs:113-115`, `:121`); the harness copy does not. The harness's
  `EnumerateContextFiles` skips the flat-folder `.m3u` union that
  `SourceScan.EnumerateFolderPoolRaw` does (`SourceScan.cs:49-55`).
- `tools/CmmScaleBench` carries a second copy; `tools/CmmSession/Engine.cs:503` a third.
  `tools/CmmPlaylistTest` is the only one that links `SourceScan.cs`.

This is the same failure that cost a night on the acoustics work — a harness grading a model
that does not ship. So Phase 0 is:

- Delete the hand-ported copies; link `SourceScan.cs` (and later `MusicRoots.cs`) into
  ScanTest, ScaleBench and Session.
- Add a multi-root mode and a `--dump` printing, per context, the resolved dirs and the full
  **sorted** track list.
- Capture a baseline dump against the real library on `J:` at current `HEAD`.
- Assert: one root ⇒ resolution returns one dir.

### Phase 1 — plumbing, still one root  ← *the risky refactor, isolated*

- Add `MusicRoots` / `RootSet` with `Roots = { MusicDirectory }`.
- Add `SourceScan.EnumerateFolderPoolMulti(dirs, recursive, claimed, out playlistFiles)` —
  union, dedup by `FullKey`, concatenate playlist lists.
- Add `SourceScan.ContextWatchSignature` (§1.7); collapse the two watch dicts into
  `_fwLastSig`/`_fwLastNewest`; hoist `FileWatchLoop`'s coroutine locals to statics; clamp
  `recentlyTouched`.
- Route **all** resolution sites through `MusicRoots.ResolveDirs`:
  `Presence.cs:64`, `:155`, `:307` · `AudioPlayer.cs:913` · `ContextMusicManager.cs:761` ·
  `FileWatch.cs:103`, `:258`, `:662` · `Crawler.cs:213` ·
  **`RadioStationCatalog.cs:414`, `:446`, `:565`** ← all three, see §2 note below.
- `PresenceScanItem.Dir` → `Dirs[]`; `FileWatchDescriptor.Dir` → `Dirs[]` + `RootSet` (§1.8).
- Split `NormalizedContextDir`'s two jobs — crawler walk root vs claim prefixes (§1.10).
- Crawler: per-root sloppiness, "no root exists" gate, reverse push order, sorted assembly.
- **Gate:** the Phase 0 dump is byte-identical.

> **The radio catalog needs all three sites.** `EnsureCatalog` rebuilds only when
> `srcChanged || sig != _catalogSig || hash != _catalogHash`. Multi-rooting `RebuildCatalog`
> (`:446`) alone leaves the *change detector* watching the primary only, so dropping a station
> folder into an add-on is never noticed — forever, until something else touches the primary.
> Worse, `Stations.Count > 0` in the guard means an empty catalog re-enumerates every tick, so
> an add-on-**only** radio setup works while a mixed one silently does not: the worst possible
> shape for diagnosis.

### Phase 2 — invalidation

- `RootSet` publication discipline, `MusicRoots.Publish` (§1.6), generation stamping (§1.8).
- Wire it to a `MusicPath` change in `ApplyConfigReload`. **Own commit** — pre-existing bug,
  testable on its own by editing `MusicPath` in a running game, and it doubles as the mechanism
  the whole feature needs.

### Phase 3 — discovery and the setting

- `MusicRoots.Discover()` at init (§1.4); the 10 s off-thread re-probe.
- `CMMConfig.AddonMusicScanEnabled = true`; `ReadBool` in `LoadSettings`; element + comment in
  `Config/settings.xml`. Flipping it at runtime routes through `MusicRoots.Publish` — the same
  single apply path (§1.6), which is what makes the disable deterministic instead of racing
  `PresenceScanLoop`.
- **No keybind for this flag.** A keybind calling `PersistSetting` would overwrite the user's
  hard-disable on the first keypress; `CombatMusicMuted` only survives that pattern because it
  is default-*off*.
- `cmm roots`: active roots, which mod each came from, per-context resolved dirs with track
  counts. Without it, "my pack isn't playing" is undiagnosable.
- Log one line at startup naming the roots in use — see §5 Q1.

### Phase 4 — radio and the main-thread stat

- Stations **merge by name** across roots: gather `name → pooled tracks`, then `AddStation`
  once per name. Today's `ByName` assignment is last-wins, so an add-on `Country` folder would
  *replace* the primary's. Order stays deterministic — the shuffle is seeded from the station
  name, not the pool.
- **Move `ContentHash` off the main thread.** Already a per-frame hazard on a large exFAT
  library at one root. Prerequisite, not a nice-to-have.
- Add `ForceRebuild()`.

### Phase 5 — live tests

---

## 3. Live test matrix

Real files, real game, moved while running. Test on **`J:`** — the exFAT drive — not `C:`,
because the sloppy-mtime fallback is the path that actually runs here, and a mixed
NTFS-primary / exFAT-add-on setup exercises both branches simultaneously for the first time.

Throwaway mod: `<Mods>\ZZ_CMM_TestPack\ModInfo.xml` + `Music\<context>\*.ogg`.

| # | Action | Expected |
|---|---|---|
| 1 | Baseline, no add-on mods | identical to today; `cmm roots` shows 1 root |
| 2 | Add-on with one matching context folder | tracks join that context's rotation only |
| 3 | Two add-ons, same context | both pools present, both playable |
| 4 | Add-on folder name matches no context | ignored silently, no log spam |
| 5 | Drop a file into an add-on folder while that context plays | picked up within ~2 s, no interruption |
| 6 | Delete the **currently playing** add-on track | hot-swap to a survivor, no silence, no stall |
| 7 | Delete an add-on's whole `Music` folder mid-session | roots re-probe, pools rebuild, playback survives |
| 7b | Delete just `<addon>\Music\<oneContext>` | **the §1.7 case a max fold misses** — must still be detected |
| 8 | Recreate either | comes back within ~10 s |
| 9 | `AddonMusicScanEnabled=false` while an add-on track plays | deterministic hot-swap, and **no add-on track plays after it** (the `_preStaged` / history leak) |
| 10 | Add-on contributes to a **recursive** context | crawler walks both roots; no flapping (§1.9 `BeginSweep`); partial publish is not add-on-only |
| 11 | Add-on contributes a radio station folder | detected at all (§2 note); merges by name; dial order stable; tuned station survives |
| 12 | Duplicate track in primary and add-on | plays as two entries; measured once via content hash; banning one bans both |
| 13 | Ban a track that lives in an add-on | persists across restart |
| 14 | Edit `MusicPath` while running | full rebuild (Phase 2), no stale snapshot |
| 15 | Add-on `Music` on exFAT, primary on NTFS | both watched; sloppy path per-dir, not per-context or per-sweep |
| 16 | 20+ add-on roots | startup cost, per-tick cost, **and empty-context cost** measured, not guessed |

---

## 4. Ranked risks

| Risk | Why | Mitigation |
|---|---|---|
| Signature fold loses information | A max fold absorbs a vanished dir; the snapshot keeps dead tracks forever | §1.7 composite fold with the `-1` sentinel; test 7b |
| Cross-thread invalidation | `_crawl`, `cacheMap` and `_fwLast*` have three different owners; a `Clear` against an insert corrupts a Dictionary and the lane runner swallows the exception into a log line | §1.6 thread split; scheduler clears only its own dicts |
| Silence window on invalidation | Recursive active context reports files, cannot produce one, drops to `Silence` for seconds-to-minutes | §1.6.1 swap, never null |
| The resolution refactor misses a site | Six sites inline the combine; a miss reads as "presence says files exist but `PickNextFile` returns null", or a watch on a directory nobody scans | Phase 1 isolated behind the byte-identical gate; grep-assert zero remaining `Path.Combine(MusicDirectory` outside `MusicRoots` |
| Harness drift | Precedent: the acoustics harness graded a model that did not ship for hours. Three harnesses currently carry stale hand-ported copies | Phase 0 deletes them and links the real code — prerequisite work |
| Radio change-detector left single-root | Add-on stations never detected; mixed setups fail while add-on-only ones work | All three sites in Phase 1 |
| Crawler sloppiness from one root | exFAT add-on becomes invisible for the process lifetime | Per-root flag (§1.9) |
| Loudness key ambiguity | Teaching `ToRelKey` two roots corrupts the ban mirrors | Do not teach it — absolute fall-through already round-trips |
| Empty-source guard × N roots | Would enumerate every root | Keep all twelve; harness assertion |
| Cost multipliers | `previouslyEmpty` forces a full scan every 2 s while a context is empty — × N dirs; `IsClaimedByDeeperContext` is per-file × N roots | §1.2's "add-on dirs only if they exist" bounds the common case; measure in test 16 |

---

## 5. Open questions

1. ~~**The hard-disable is destroyed by mod updates.**~~ **RESOLVED 2026-08-21** — future
   versions ship no `Config/*.xml` at all; CMM generates them at first run and backfills keys
   added since, with version-stamped migration for changed defaults. A hard `false` now
   survives updates. See `plan-config-generation.md`. Still log the root list at startup
   (Phase 3) — that was worth doing on its own.
2. **Should add-on roots extend an *absolute* context source?** Plan says no. Cheap to reverse.
3. **Should add-on roots extend a *playlist* source?** Plan says no.
4. **Precedence when both roots hold a same-named file** — plan plays both. The alternative
   (add-on shadows primary by relative path) is what a *replacement* pack wants, and is a
   different feature.

---

## 6. Two pre-existing bugs this work fixes for free

Worth landing as their own commits regardless of whether the feature ships:

- **No invalidation on a `MusicPath` change** (§0.2). Phase 2.
- **`recentlyTouched` never expires on a future mtime** (§1.7). Phase 1.

And two more found in review, unrelated to roots: `FileWatchLoop`'s watch state is unreachable
from any invalidation (§1.7), and `FinishSweep`'s snapshot ordering is non-deterministic, which
`TrackOrderMode.Sequential` indexes into (§1.9).

---

## 7. What the adversarial review changed

Recorded so the reasoning is not lost. Six items in the first draft would have shipped broken:

1. **"Max of mtimes" signature fold** — silently absorbs a vanished directory. Replaced with a
   membership-sensitive composite fold.
2. **`OnRootsChanged()` as one function** — races `_crawl` (main-thread-only) from the
   scheduler thread and `_fwLast*` (scheduler-only) from the main thread. Split by ownership.
3. **"Null every `directorySnapshot`"** — opens a real silence window on recursive contexts.
   Swap complete arrays instead.
4. **The disable path** — left `stagedFile`, `_preStaged` and history pinned, so disabling
   add-ons let one more add-on track play, and lost the race to `PresenceScanLoop` so no
   hot-swap ever happened. Routed through the repoint recovery.
5. **Radio: one site instead of three** — the change detector would have stayed single-root.
6. **Phase 0's gate** — the harnesses do not link the shipped code and have already drifted, so
   the gate would have passed a broken refactor.

Plus: crawler per-root sloppiness, `BeginSweep`'s missing-root gate, LIFO push order, claim
prefixes needing unconditional resolution, and the startup-order constraint being "before
`RebuildContextsWithFiles`", not merely "after `LoadContexts`".
