using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // FILE WATCH (folder change detection + rescan; split from coordinator)
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager
    {
        private IEnumerator FileWatchLoop()
        {
            var lastWriteTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            // Re-validate blacklisted files no more than this often (seconds). The
            // directory mtime does NOT change when a file finishes writing its contents,
            // so a file that failed validation while still being copied in (locked /
            // incomplete) would never be re-examined by the mtime fast path. This forces
            // a periodic slow-path sweep while anything is blacklisted, so such files
            // recover instead of being excluded forever.
            const float BlacklistRetryInterval = 30f;
            float lastBlacklistRetry = -BlacklistRetryInterval;

            // How recently (seconds) a folder must have been written for us to distrust an
            // unchanged directory write-time and force a reconciling enumeration anyway.
            // Covers a couple of 2s poll intervals plus the 2s FAT/exFAT stamp resolution,
            // so a swap that collides with the previous stamp value is still caught.
            const float SignatureFreshnessWindow = 6f;

            while (true)
            {
                yield return new WaitForSeconds(2f);
                if (_functionallyDormant) continue;   // A/B dormant arm
                if (!inGame && currentContext?.Context != "Menu" && currentContext?.Context != "Ambient"
                    && currentContext?.Context != "Silence" && currentContext?.Context != "Vanilla") continue;
                if (currentContext == null) continue;

                // Build the set of contexts to watch: the current one PLUS every
                // context currently on the stack (they may have files added/removed
                // while overridden by a higher-priority context).
                var contextsToWatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { currentContext.Context };
                foreach (var ac in _contextStack)
                    contextsToWatch.Add(ac.ContextName);
                // Also watch the fallback chain of every already-watched context.
                // POI_Tx is in activeContexts but "POI" never is, so "POI" never
                // lands on _contextStack. Without this expansion, restoring files to
                // a fallback-only context (e.g. POI when playing via POI_T1 -> POI
                // -> Ambient) is never detected.
                foreach (string seed in contextsToWatch.ToList())
                {
                    string cur = seed;
                    for (int fi = 0; fi < 5; fi++)
                    {
                        Contexts fctx = GetContext(cur);
                        string fb = fctx?.Fallback ?? "None";
                        if (fb == "None" || fb == "Stack" || fb == cur) break;
                        contextsToWatch.Add(fb);
                        cur = fb;
                    }
                }
                // Stack is empty on the menu screen — always watch Menu explicitly
                // so file additions after a fallback to Ambient can re-trigger it.
                if (!inGame) contextsToWatch.Add("Menu");
                // Always watch PlayOnce event contexts (Spawn, Death) — they are never
                // on the stack during normal gameplay but need file-change detection so
                // that files added back are recognized on the next death/spawn event.
                foreach (var ctx in contexts.Values)
                {
                    if (ctx.PlayOnce && !string.IsNullOrEmpty(ctx.Path))
                        contextsToWatch.Add(ctx.Context);
                }

                // Once per tick, decide whether this sweep should also re-validate
                // blacklisted files. Only ever true while something is blacklisted, so a
                // clean library never pays for the extra enumeration.
                bool blacklistRetryDue = _invalidFiles.Count > 0 &&
                    (Time.realtimeSinceStartup - lastBlacklistRetry) >= BlacklistRetryInterval;
                if (blacklistRetryDue) lastBlacklistRetry = Time.realtimeSinceStartup;

                foreach (string contextName in contextsToWatch)
                {
                    ContextCache cache = cacheMap.TryGetValue(contextName, out var c) ? c : null;
                    if (cache == null) continue;
                    Contexts ctx = GetContext(contextName);
                    if (ctx == null || string.IsNullOrEmpty(ctx.Path)) continue;

                    string dir = Path.Combine(MusicDirectory, ctx.Path);
                    // Reach + ownership for this context, resolved here on the main
                    // thread (claim prefixes read the shared `contexts` dict); the
                    // background stat/scan closures below only use the captured copies.
                    bool includeSub = ctx.IncludeSubfolders;
                    string[] claimedPrefixes = GetClaimedSubtreePrefixes(contextName);

                    bool isActiveContext =
                        string.Equals(contextName, currentContext.Context, StringComparison.OrdinalIgnoreCase);
                    // For the ACTIVE context we can't trust the directory write-time:
                    // exFAT/FAT (e.g. an SD card), some network shares, and junctions do
                    // NOT reliably bump a folder's mtime when a file is moved/deleted out
                    // of it, so the stat shortcut would miss the removal of the playing
                    // track. Instead of enumerating the whole folder every tick (O(n) —
                    // catastrophic at tens of thousands of files), we ping the ONE file
                    // that's playing. That single stat is O(1) regardless of folder size;
                    // a full enumeration only happens once the ping shows it gone.
                    string playingFile = isActiveContext ? cache.lastPlayedFile : null;

                    // ── RECURSIVE: owned by RecursiveCrawlLoop ──────────────────────
                    // Subfolder contexts can span thousands of folders; walking the whole
                    // tree for a change signature every 2s (the fast path below) is O(folders)
                    // and does not scale. The crawler does that work incrementally off-tick.
                    // Here we keep ONLY the O(1) ping of the active context's playing file so
                    // a deleted current track still hot-swaps instantly; all other adds/removes
                    // are reconciled by the crawler.
                    if (includeSub)
                    {
                        if (isActiveContext && !string.IsNullOrEmpty(playingFile))
                        {
                            bool goneR = false, pingDoneR = false;
                            string pf = playingFile;
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try { goneR = !File.Exists(pf); } catch { }
                                pingDoneR = true;
                            });
                            while (!pingDoneR) yield return null;
                            if (goneR)
                                yield return StartCoroutine(HotSwapFromMissingPlayingFile(contextName, pf));
                        }
                        continue;
                    }

                    // ── FAST PATH: single stat (+ one ping for the active context) ──────
                    DateTime currentWriteTime = DateTime.MinValue;
                    bool playingFileGone = false;
                    bool statDone = false;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            currentWriteTime = ContextWriteSignatureUtc(dir, includeSub);
                            if (!string.IsNullOrEmpty(playingFile))
                                playingFileGone = !File.Exists(playingFile);
                        }
                        catch { }
                        statDone = true;
                    });
                    while (!statDone) yield return null;

                    lastWriteTimes.TryGetValue(contextName, out DateTime knownTime);

                    // Special case: if we've never scanned this context and the folder
                    // exists, treat it as changed so we populate the snapshot.
                    bool neverScanned = !lastWriteTimes.ContainsKey(contextName);
                    // Never trust the write-time shortcut for empty directories: Windows
                    // can stamp the empty dir and a re-filled dir with the same timestamp
                    // when the add happens within the same filesystem precision window.
                    bool previouslyEmpty = !neverScanned && (cache.directorySnapshot?.Length ?? 0) == 0;
                    // Enumerate the active context only when its playing file has actually
                    // vanished. Otherwise the cheap mtime/ping gate below lets us skip the
                    // folder walk entirely.
                    bool activeNeedsScan = isActiveContext && playingFileGone;

                    // A coarse directory write-time (FAT/exFAT = 2s resolution — the SD-card
                    // case this loop already worries about elsewhere; some network shares are
                    // worse) can stamp a JUST-changed folder with the SAME value it had a moment
                    // ago, so `currentWriteTime == knownTime` is NOT proof of "no change" while
                    // the user is actively swapping files in/out: an add or remove that lands in
                    // the same resolution tick is hidden. (previouslyEmpty only patches the
                    // empty→filled corner.) If the signature timestamp is within a few poll
                    // intervals of now, the folder was touched recently enough that a same-valued
                    // stamp may be masking a change — force the enumeration to reconcile. Cheap:
                    // no extra IO (we already have currentWriteTime), and it self-limits to the
                    // brief window after an edit, settling back to the stamp shortcut once the
                    // folder goes quiet for SignatureFreshnessWindow seconds.
                    bool recentlyTouched = currentWriteTime != DateTime.MinValue
                        && (DateTime.UtcNow - currentWriteTime) < TimeSpan.FromSeconds(SignatureFreshnessWindow);

                    if (!activeNeedsScan && !neverScanned && !previouslyEmpty && !recentlyTouched
                        && currentWriteTime == knownTime && !blacklistRetryDue) continue;
                    lastWriteTimes[contextName] = currentWriteTime;

                    // ── FAST REACTION: the ping found the playing file gone ──────────
                    // We already know exactly what changed, so react off the in-memory
                    // snapshot (trim the gone file, pick a still-present replacement,
                    // play it) instead of walking the whole folder. This keeps the
                    // hot-swap O(1) in folder size — the disk enumeration below would be
                    // the expensive part at tens of thousands of files. Other adds/removes
                    // are reconciled lazily (on a later mtime change, or when a stale pick
                    // fails to open).
                    if (isActiveContext && playingFileGone)
                    {
                        yield return StartCoroutine(HotSwapFromMissingPlayingFile(contextName, playingFile));
                        continue;
                    }

                    // ── SLOW PATH: enumerate, diff, validate, react ─────────────────
                    // Body lives in ApplyFileWatchScan so the scheduler-driven path
                    // (FileWatchTick) can Post the same reaction onto the main thread.
                    yield return StartCoroutine(ApplyFileWatchScan(contextName, neverScanned));
                }
            }
        }

        // ── File-watch slow-path reaction (MAIN THREAD) ─────────────────────
        // Enumerates one non-recursive context's folder, diffs it against the cached
        // snapshot, validates genuinely-new files and reacts (hot-swap / fall back /
        // re-resolve) — all touching the lock-free contexts/cacheMap/currentContext, so
        // it must run on the main thread. Driven by FileWatchLoop (the NativeScheduler-off
        // fallback) or, off the frame loop, by FileWatchTick → MainThreadDispatcher.Post
        // once the scheduler-thread stat probe decides this context needs a full scan.
        // `neverScanned` carries the probe's first-scan verdict (the snapshot must be
        // populated even when the folder write-time looks unchanged).
        private IEnumerator ApplyFileWatchScan(string contextName, bool neverScanned)
        {
            ContextCache cache = cacheMap.TryGetValue(contextName, out var c) ? c : null;
            if (cache == null) yield break;
            Contexts ctx = GetContext(contextName);
            if (ctx == null || string.IsNullOrEmpty(ctx.Path)) yield break;
            if (currentContext == null) yield break;

            string dir = Path.Combine(MusicDirectory, ctx.Path);
            bool includeSub = ctx.IncludeSubfolders;
            string[] claimedPrefixes = GetClaimedSubtreePrefixes(contextName);

            // ── SLOW PATH: enumerate files ──────────────────────────────────
            string[] currentFiles = null;
            bool scanDone = false;
            string[] previousFiles = cache.directorySnapshot ?? new string[0];

            Func<string, string> norm = p => string.IsNullOrEmpty(p) ? p
                : Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Enumerate via DirectoryInfo so each file's size+mtime comes back WITH
            // the listing (Windows fills these from the directory scan — no extra
            // stat per file). Build a per-file validation key (path|size|mtime) for
            // the cache check below: a library moved out and back keeps the same
            // size+mtime, so it's accepted without re-decoding every file.
            var keyByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        var fis = new DirectoryInfo(dir)
                            .GetFiles("*", includeSub ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                            .Where(fi => AudioDecoder.IsSupported(fi.FullName)
                                         && !IsClaimedByDeeperContext(fi.FullName, claimedPrefixes)).ToArray();
                        currentFiles = fis.Select(fi => fi.FullName).ToArray();
                        foreach (var fi in fis)
                        {
                            string nk = norm(fi.FullName);
                            keyByPath[nk] = $"{nk}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
                        }
                    }
                    else currentFiles = new string[0];
                }
                // A throw mid-enumeration (an unreadable subdir or an over-MAX_PATH
                // path under a recursive subfolders="true" context, removed media, or
                // the folder vanishing after the Exists check) must not leave
                // currentFiles null with scanDone never set: the `while (!scanDone)`
                // driver below would then spin forever and kill FileWatchLoop for the
                // rest of the session. Fall back to the previous snapshot so this tick
                // reads as "no change" and we retry on the next sweep.
                catch { currentFiles = previousFiles; }
                finally { scanDone = true; }
            });
            while (!scanDone) yield return null;

            var currentNorm = currentFiles.Select(norm).ToArray();
            var previousNorm = previousFiles.Select(norm).ToArray();

            bool filesActuallyChanged =
                currentNorm.Length != previousNorm.Length ||
                currentNorm.Except(previousNorm, StringComparer.OrdinalIgnoreCase).Any() ||
                previousNorm.Except(currentNorm, StringComparer.OrdinalIgnoreCase).Any();

            bool changed = neverScanned || filesActuallyChanged;
            if (!changed) yield break;

            // Update the snapshot and invalidate the file-presence cache so
            // ResolvePlayableContext sees the new state immediately.
            cache.directorySnapshot = currentFiles;
            InvalidateContextFileCache(contextName);

            var removedFiles = previousNorm.Except(currentNorm, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string removed in removedFiles)
            {
                // Keep the _validatedFiles entry: it's keyed by path|size|mtime, so
                // a file moved out and back hits the cache instead of re-validating.
                // A genuinely changed file gets a new key and is re-checked anyway.
                _invalidFiles.Remove(removed);
            }

            var newFiles = currentNorm.Except(previousNorm, StringComparer.OrdinalIgnoreCase).ToArray();
            if (newFiles.Length > 0)
            {
                // Skip files already known good — loudness-measured (fully decoded
                // once) or validated earlier this session at the same size+mtime —
                // so a library moved out and back isn't re-decoded file by file.
                // Only genuinely new/changed files pay the QuickValidate cost.
                var toValidate = new List<string>();
                foreach (string nf in newFiles)
                {
                    bool cached = LoudnessStore.IsMeasured(nf)
                        || (keyByPath.TryGetValue(nf, out string k) && _validatedFiles.Contains(k));
                    if (cached) _invalidFiles.Remove(nf);
                    else toValidate.Add(nf);
                }

                if (toValidate.Count > 0)
                    CMMConfig.D($"[CMM] Validating {toValidate.Count} new file(s) in {contextName}" +
                                $" ({newFiles.Length - toValidate.Count} cached)...");

                foreach (string newFile in toValidate)
                {
                    bool isValid = false;
                    yield return StartCoroutine(ValidateFile(newFile, result => isValid = result));
                    if (!isValid)
                    {
                        _invalidFiles.Add(newFile);
                        Log.Warning($"[CMM] New file failed validation: {Path.GetFileName(newFile)}");
                    }
                    else
                    {
                        // Re-validation passed — clear any stale blacklist entry from
                        // an earlier failed attempt (e.g. the file was still being
                        // copied in / locked the first time we saw it). Without this a
                        // transiently-bad file stays excluded even after it's fine.
                        _invalidFiles.Remove(newFile);
                        // Remember it so a later move-out/move-back skips re-decoding.
                        if (keyByPath.TryGetValue(newFile, out string vk)) _validatedFiles.Add(vk);
                    }
                }
                currentFiles = currentFiles.Where(f => !_invalidFiles.Contains(norm(f))).ToArray();
                currentNorm = currentFiles.Select(norm).ToArray();
                cache.directorySnapshot = currentFiles;
                // Re-invalidate after filtering out bad files.
                InvalidateContextFileCache(contextName);
            }

            // First scan populates lastWriteTimes but shouldn't react if
            // the snapshot already matched (e.g. preload already ran).
            if (!filesActuallyChanged) yield break;

            CMMConfig.D($"[CMM] File change in {contextName}: {previousFiles.Length} -> {currentFiles.Length} files");

            _previousPlayableContexts.Clear();
            // ── Only react immediately if this is the active context ─────────
            // For non-current contexts, the snapshot + cache invalidation above
            // is enough: ContextScanLoop will pick up the change on its next
            // tick and call SendContextUpdate if the top context has changed.
            // Exception: on the menu screen ContextScanLoop is skipped (!inGame),
            // so we must re-trigger Menu here when its files are restored.
            if (contextName != currentContext.Context)
            {
                if (!inGame && contextName == "Menu" && currentFiles.Length > 0)
                {
                    // If _pendingContext was set to "Menu" by the silent-guard above
                    // (to block BackgroundMusicMono.Play() re-triggers), clear it now
                    // so files being added back can actually start the music.
                    if (_pendingContext == contextName) _pendingContext = null;
                    SendContextUpdate("Menu");
                }
                yield break;
            }

            bool lastPlayedMissing = false;
            if (!string.IsNullOrEmpty(cache.lastPlayedFile))
            {
                string lastNorm = norm(cache.lastPlayedFile);
                if (!currentNorm.Any(f => string.Equals(f, lastNorm, StringComparison.OrdinalIgnoreCase)))
                {
                    CMMConfig.D($"[CMM] Currently-playing file removed: {Path.GetFileName(cache.lastPlayedFile)}");
                    cache.lastPlayedFile = null;
                    lastPlayedMissing = true;
                }
            }

            if (currentFiles.Length == 0)
            {
                CMMConfig.D($"[CMM] {contextName} folder emptied, re-evaluating.");
                _pendingContext = null;
                // Native: the active context's voice is buffered in native memory, so
                // deleting its files does NOT stop it, and the re-resolve below isn't
                // guaranteed to tear it down (cooldown / priority early-returns, the
                // DMS-borrow path). Stop it explicitly so a hot-swap (remove → re-add)
                // can't leave the old buffered track overlapping the re-added one.
                StopNativeIfActive(CrossFadeDuration);
                if (currentContext.PlayOnce)
                {
                    // PlayOnce events (Spawn/Death): don't wait for the fallback clip
                    // to load before stopping — fade out immediately and let
                    // ContextScanLoop re-evaluate the stack on its next tick.
                    CMMConfig.D($"[CMM] PlayOnce {contextName} removed during playback — fading out immediately.");
                    ++_transitionId;
                    _pendingContext = null;
                    currentContext = GetContext("Silence");
                    FadeToSilence();
                }
                else if (!inGame)
                {
                    // On the menu screen the context stack is empty, so GetTopContext()
                    // can't surface the current context's fallback. Re-trigger the
                    // current context itself so its OWN fallback chain resolves
                    // (Menu -> Vanilla), handing the menu back to the game's music.
                    SendContextUpdate(currentContext.Context);
                }
                else
                {
                    string fallback = GetTopContext() ?? "Ambient";
                    SendContextUpdate(fallback, 0);
                }
            }
            else if (lastPlayedMissing)
            {
                // Hot-swap: the custom file that was playing got removed but the
                // context still has surviving tracks. Stop the orphaned buffered
                // voice and re-trigger so the native mixer picks a fresh one.
                _pendingContext = null;
                if (ContextHasFiles(ctx.Context))
                {
                    CMMConfig.D($"[CMM] Surviving tracks in {ctx.Context}, hot-swapping (native).");
                    StopNativeIfActive(0.25f);
                    currentContext = GetContext(ctx.Context);
                    TriggerMusic(ctx.Context);
                }
                else
                {
                    string fallback = GetTopContext() ?? "Ambient";
                    SendContextUpdate(fallback, 0);
                }
            }
            else
            {
                // Files changed but the playing track is still present.
                // Re-evaluate the top context in case a higher-priority one
                // is now unblocked.
                // On the menu screen the context stack is always empty, so
                // GetTopContext() always returns "Ambient" — which has no
                // files and causes FadeToSilence(), killing the menu preload.
                if (!inGame) yield break;
                string top = GetTopContext();
                // null = nothing playable + DMS off. Don't forward null into
                // GetContext/SendContextUpdate (Dictionary rejects a null key);
                // let ContextScanLoop fade to silence on its next tick.
                if (top == null) yield break;
                if (top != currentContext.Context)
                {
                    // Don't let the initial directory scan (neverScanned) or
                    // a file-change event interrupt a PlayOnce context (Spawn/Death)
                    // with something of lower or equal priority.
                    if (currentContext != null && currentContext.PlayOnce)
                    {
                        Contexts topCtx = GetContext(top);
                        if (topCtx == null || topCtx.Priority <= currentContext.Priority)
                            yield break;
                    }
                    _pendingContext = null;
                    SendContextUpdate(top, 0);
                }
            }
        }

        // ── Off-frame-loop file watch (NativeScheduler) ──────────────────────
        // Same idea as the config watch: the cheap detection (folder write-time signature
        // + the active track's File.Exists ping) runs on the scheduler thread, and only a
        // context that ACTUALLY changed marshals its reaction back to the main thread. So
        // file edits are still picked up while Unity's main thread is frozen (world load/
        // unload) and the per-frame `while(!done) yield` busy-poll is gone. Mirrors
        // FileWatchLoop, which stays the NativeScheduler-off fallback.
        //
        // The watch SET is built on the main thread each tick (it reads the lock-free
        // resolver state — current/stack/fallback chains/PlayOnce/Menu) via a single
        // blocking Send; that closure does no IO, so it does not forfeit freeze-survival
        // for the stat/scan work that follows it off-thread.
        private sealed class FileWatchDescriptor
        {
            public string ContextName;
            public string Dir;
            public bool   IncludeSub;
            public string PlayingFile;      // active context only (else null) — the O(1) ping target
            public bool   IsActiveContext;
            public int    SnapshotLength;   // for the empty→filled "previouslyEmpty" gate
        }

        private const double FwBlacklistRetryInterval = 30.0;   // see FileWatchLoop's BlacklistRetryInterval
        private const double FwSignatureFreshnessWindow = 6.0;  // see FileWatchLoop's SignatureFreshnessWindow

        // Scheduler-owned: only ever touched on the scheduler thread, never the main thread,
        // so they need no lock and stay independent of the coroutine fallback's own copies.
        private static readonly Dictionary<string, DateTime> _fwLastWriteTimes =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Diagnostics.Stopwatch _fwClock = System.Diagnostics.Stopwatch.StartNew();
        private static double _fwLastBlacklistRetry;

        internal static void FileWatchInit()
        {
            _fwLastWriteTimes.Clear();
            _fwLastBlacklistRetry = -FwBlacklistRetryInterval;   // allow the first retry sweep immediately
        }

        // MAIN THREAD ONLY (run inside the tick's Send). Reproduces FileWatchLoop's
        // top-of-tick guards + watch-set construction, capturing per-context only the
        // handful of values the off-thread stat/scan needs. Empty list = watch nothing.
        private static List<FileWatchDescriptor> BuildFileWatchDescriptors()
        {
            var result = new List<FileWatchDescriptor>();
            if (currentContext == null) return result;
            if (!inGame && currentContext.Context != "Menu" && currentContext.Context != "Ambient"
                && currentContext.Context != "Silence" && currentContext.Context != "Vanilla") return result;

            var contextsToWatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { currentContext.Context };
            foreach (var ac in _contextStack)
                contextsToWatch.Add(ac.ContextName);
            // Expand each watched context's fallback chain (e.g. POI_T1 -> POI -> Ambient) so
            // files restored to a fallback-only context that never lands on the stack are seen.
            foreach (string seed in contextsToWatch.ToList())
            {
                string cur = seed;
                for (int fi = 0; fi < 5; fi++)
                {
                    Contexts fctx = GetContext(cur);
                    string fb = fctx?.Fallback ?? "None";
                    if (fb == "None" || fb == "Stack" || fb == cur) break;
                    contextsToWatch.Add(fb);
                    cur = fb;
                }
            }
            // Stack is empty on the menu screen — always watch Menu so additions re-trigger it.
            if (!inGame) contextsToWatch.Add("Menu");
            // PlayOnce events (Spawn/Death) are never on the stack but still need change
            // detection so files added back are recognized on the next death/spawn.
            foreach (var ctx in contexts.Values)
                if (ctx.PlayOnce && !string.IsNullOrEmpty(ctx.Path))
                    contextsToWatch.Add(ctx.Context);

            foreach (string contextName in contextsToWatch)
            {
                ContextCache cache = cacheMap.TryGetValue(contextName, out var c) ? c : null;
                if (cache == null) continue;
                Contexts ctx = GetContext(contextName);
                if (ctx == null || string.IsNullOrEmpty(ctx.Path)) continue;

                bool isActive = string.Equals(contextName, currentContext.Context, StringComparison.OrdinalIgnoreCase);
                result.Add(new FileWatchDescriptor
                {
                    ContextName     = contextName,
                    Dir             = Path.Combine(MusicDirectory, ctx.Path),
                    IncludeSub      = ctx.IncludeSubfolders,
                    // Active context only: we can't trust the folder mtime for the playing
                    // track (exFAT/network/junction), so we ping that one file (O(1)) instead.
                    PlayingFile     = isActive ? cache.lastPlayedFile : null,
                    IsActiveContext = isActive,
                    SnapshotLength  = cache.directorySnapshot?.Length ?? 0,
                });
            }
            return result;
        }

        internal static void FileWatchTick()
        {
            if (_functionallyDormant) return;   // A/B dormant arm
            // One main-thread hop to snapshot the watch set + whether anything is blacklisted.
            // Blocking Send: during a main-thread freeze this parks the HOUSEKEEPING lane
            // until the main thread pumps again — which is why this tick must never share a
            // thread with native-advance (see CmmScheduler's lane contract).
            List<FileWatchDescriptor> descriptors = null;
            bool anyBlacklisted = false;
            MainThreadDispatcher.Send(() =>
            {
                descriptors = BuildFileWatchDescriptors();
                anyBlacklisted = _invalidFiles.Count > 0;
            });
            if (descriptors == null || descriptors.Count == 0) return;

            // Re-validate blacklisted files only periodically, and only while something is
            // blacklisted — paced off the scheduler's own clock (Time.realtimeSinceStartup
            // isn't readable off the main thread). Forces a slow scan so a file that failed
            // while still being copied in eventually recovers (the dir mtime doesn't bump
            // when the file finishes writing its contents).
            double now = _fwClock.Elapsed.TotalSeconds;
            bool blacklistRetryDue = anyBlacklisted && (now - _fwLastBlacklistRetry) >= FwBlacklistRetryInterval;
            if (blacklistRetryDue) _fwLastBlacklistRetry = now;

            foreach (var d in descriptors)
            {
                string ctxName = d.ContextName, pf = d.PlayingFile;   // capture for the closures below
                try
                {
                    // RECURSIVE: reconciliation is RecursiveCrawlLoop's job; here only the O(1)
                    // ping of the active context's playing file, so a deleted current track
                    // still hot-swaps instantly.
                    if (d.IncludeSub)
                    {
                        if (d.IsActiveContext && !string.IsNullOrEmpty(pf) && PingGone(pf))
                            MainThreadDispatcher.Post(() =>
                                CMM_CoRoutines.StartCoroutine(Instance.HotSwapFromMissingPlayingFile(ctxName, pf)));
                        continue;
                    }

                    // FAST PATH: one directory write-time signature (+ one ping for the active ctx).
                    DateTime currentWriteTime = ContextWriteSignatureUtc(d.Dir, false);
                    bool playingFileGone = !string.IsNullOrEmpty(pf) && PingGone(pf);

                    bool neverScanned = !_fwLastWriteTimes.TryGetValue(ctxName, out DateTime knownTime);
                    // Never trust the write-time shortcut for an empty dir (a refill can land on
                    // the same filesystem-precision stamp) or a folder touched within the last
                    // few poll intervals (a same-valued FAT/exFAT stamp may be masking a change).
                    bool previouslyEmpty = !neverScanned && d.SnapshotLength == 0;
                    bool activeNeedsScan = d.IsActiveContext && playingFileGone;
                    bool recentlyTouched = currentWriteTime != DateTime.MinValue
                        && (DateTime.UtcNow - currentWriteTime) < TimeSpan.FromSeconds(FwSignatureFreshnessWindow);

                    if (!activeNeedsScan && !neverScanned && !previouslyEmpty && !recentlyTouched
                        && currentWriteTime == knownTime && !blacklistRetryDue) continue;   // no change — no main hop
                    _fwLastWriteTimes[ctxName] = currentWriteTime;

                    if (d.IsActiveContext && playingFileGone)
                    {
                        // Playing file gone: react off the in-memory snapshot (O(1) in folder size).
                        MainThreadDispatcher.Post(() =>
                            CMM_CoRoutines.StartCoroutine(Instance.HotSwapFromMissingPlayingFile(ctxName, pf)));
                        continue;
                    }

                    // Folder changed (or first scan / forced retry sweep): enumerate + reconcile on main.
                    bool ns = neverScanned;
                    MainThreadDispatcher.Post(() =>
                        CMM_CoRoutines.StartCoroutine(Instance.ApplyFileWatchScan(ctxName, ns)));
                }
                catch (Exception ex) { Log.Error($"[CMM] FileWatchTick '{ctxName}' failed: {ex.Message}"); }
            }
        }

        // File.Exists, swallowing the transient IO error an external move/delete can throw
        // mid-stat (treated as "still present" — same as the coroutine fast path's empty catch).
        private static bool PingGone(string path)
        {
            try { return !File.Exists(path); } catch { return false; }
        }

    }
}
