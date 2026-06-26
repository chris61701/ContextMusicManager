
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using NAudio.Wave;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // COORDINATOR
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager
    {
        public static string MusicDirectory;  // absolute path; set in Init, overridable via settings.xml MusicPath
        public static bool inGame = false;
        public static bool playerIsDead = true;
        public static string ModPath;

        // FIX: Per-context cooldown map replaces the flat lastContextOnCoolDown
        // bool. This prevents a cooldown on context A from blocking a valid
        // transition to context B at higher priority.
        private static readonly HashSet<string> _contextsOnCooldown =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The context we are leaving in the current transition. Captured before
        // currentContext is reassigned to the incoming candidate, so CrossFade can
        // put the LEAVING context on cooldown (its own cool= value) rather than the
        // one that just started. Null when no prior context was playing.
        private static Contexts _cooldownTarget;

        public static GameObject CMM_Audio_Object;
        public static CoRoutine CMM_CoRoutines;
        public static ContextMusicManager Instance;

        private static Coroutine _activeMusicCoroutine = null;
        private static Coroutine _nextCacheCoroutine = null;
        private static Coroutine _trackEndCoroutine = null;
        private static Coroutine _crossFadeCoroutine = null;
        private static Coroutine _cooldownCoroutine = null;
        private static Coroutine _vanillaFadeInCoroutine = null;
        private static Coroutine _menuVanillaFadeCoroutine = null;

        public static TrackOrderMode GlobalTrackOrder = TrackOrderMode.RandomNoRepeat;
        public static float ContextScanCooldown = 0.5f;
        public static float StalenessCheckInterval = 5f;
        public static float TrackEndPollInterval = 0.25f;
        public static float CrossFadeDuration = 2f;
        public static float TrackTransitionFadeDuration = 6f;
        public static float TrackTransitionPreloadLead = 10f;

        private static readonly HashSet<string> _contextsWithFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _contextsWithFilesDirty = true;
        // Contexts whose presence needs a background (re)scan because no in-memory
        // snapshot was available when they were invalidated. Drained by PresenceScanLoop.
        private static readonly HashSet<string> _presenceDirty =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static float PresenceScanInterval = 0.5f;

        // ── Loudness sweep / save pacing ───────────────────────────────────────
        public static float LoudnessSweepStartDelay = 8f;   // let startup settle first
        public static float LoudnessSweepInterval = 1f;     // one file per tick, throttled
        public static float LoudnessSaveDebounce = 5f;      // flush the cache if dirty

        // ── Init ──────────────────────────────────────────────────────────────
        public static void Init(string modPath)
        {
            ModPath = modPath;
            MusicDirectory = Path.Combine(modPath, "Music");
#if CMM_LOUDNESS_SELFTEST
            R128SelfTest.Run();
#endif
            CMM_Audio_Object = new GameObject("ContextMusicManager");
            UnityEngine.Object.DontDestroyOnLoad(CMM_Audio_Object);
            Instance = CMM_Audio_Object.AddComponent<ContextMusicManager>();
            Instance.InitContexts();

            // Override built-in defaults with external config files if present.
            CMMConfigLoader.LoadSettings(modPath);
            CMMConfigLoader.LoadContexts(modPath);
            LoudnessStore.Load(modPath);

            foreach (var ctx in contexts.Values)
                cacheMap[ctx.Context] = new ContextCache();

            // One-time synchronous presence scan at mod load (before menu music
            // starts) so ContextHasFiles answers correctly from frame one. All
            // subsequent updates run off the main thread via PresenceScanLoop.
            RebuildContextsWithFiles();

            currentContext = GetContext("Silence");
            _pendingContext = "Silence";
            _transitionId = 0;

            CMM_Primary_Channel = CMM_Audio_Object.AddComponent<AudioSource>();
            CMM_Primary_Channel.volume = 0f;
            CMM_Primary_Channel.loop = false;

            CMM_Secondary_Channel = CMM_Audio_Object.AddComponent<AudioSource>();
            CMM_Secondary_Channel.volume = 0f;
            CMM_Secondary_Channel.loop = false;

            CMM_CoRoutines = CMM_Audio_Object.AddComponent<CoRoutine>();
            CMM_CoRoutines.StartCoroutine(Instance.PreloadContext("Menu"));
            CMM_CoRoutines.StartCoroutine(Instance.ContextScanLoop());
            CMM_CoRoutines.StartCoroutine(Instance.StalenessCheckLoop());
            CMM_CoRoutines.StartCoroutine(Instance.FileWatchLoop());
            CMM_CoRoutines.StartCoroutine(Instance.PresenceScanLoop());
            CMM_CoRoutines.StartCoroutine(Instance.ConfigWatchLoop());
            CMM_CoRoutines.StartCoroutine(Instance.VolumeSyncLoop());
            CMM_CoRoutines.StartCoroutine(Instance.DiagnosticLoop());
            CMM_CoRoutines.StartCoroutine(Instance.LoudnessSweepLoop());
            CMM_CoRoutines.StartCoroutine(Instance.LoudnessSaveLoop());

            CMMConfig.D($"[CMM] Initialized! Decoder: NAudio | Preload: {CMMConfig.PreloadCacheEnabled}");
        }

        public static void OnEnterGame()
        {
            if (!inGame) return;
            CMM_CoRoutines.StartCoroutine(Instance.PreloadAllContexts());
        }

        // ── SendContextUpdate ─────────────────────────────────────────────────
        public static void SendContextUpdate(string contextIn, int bypassPriority = -1)
        {
            if (contextIn == "Vanilla")
            {
                if (vanillaIsActive || _pendingContext == "Vanilla") return;
                if (currentContext != null && currentContext.CanBeResumed && ActiveChannel.isPlaying)
                {
                    ContextCache leavingCache = cacheMap[currentContext.Context];
                    leavingCache.resumePosition = GetPlaybackPosition(ActiveChannel, leavingCache);
                    leavingCache.lastLeftTime = Time.realtimeSinceStartup;
                    CMMConfig.D($"[CMM] Saved resume for {currentContext.Context}: {leavingCache.resumePosition:F2}s (→ Vanilla)");
                    // In streaming mode, release the clip so stale detection can pick a
                    // fresh track on re-entry (mirrors the normal context-switch path).
                    if (!CMMConfig.PreloadCacheEnabled && leavingCache.Current.Clip != null)
                    {
                        StagePendingDisposal(leavingCache.Current);
                        leavingCache.Current = default;
                    }
                }
                ActivateVanilla();
                return;
            }

            Contexts incoming = GetContext(contextIn);
            if (incoming == null) return;
            if (_pendingContext == contextIn) return;

            bool menuOverride = !inGame;
            int comparePriority = bypassPriority >= 0 ? bypassPriority : (currentContext?.Priority ?? 0);
            // allows re-triggering a context that was a null-fallback but is now valid
            bool isReloadOfCurrent = currentContext != null && contextIn == currentContext.Context;
            bool priorityOk = inGame && incoming.CanInterrupt && incoming.Priority > comparePriority;

            if (!menuOverride && !priorityOk && !isReloadOfCurrent)
            {
                CMMConfig.D($"[CMM] {contextIn} rejected — not higher priority than {currentContext?.Context}");
                return;
            }

            CMMConfig.D($"[CMM] Context accepted: {currentContext?.Context} -> {contextIn}");
            _pendingContext = contextIn;

            if (currentContext != null && currentContext.CanBeResumed && ActiveChannel.isPlaying)
            {
                ContextCache leavingCache = cacheMap[currentContext.Context];
                leavingCache.resumePosition = GetPlaybackPosition(ActiveChannel, leavingCache);
                leavingCache.lastLeftTime = Time.realtimeSinceStartup;
                CMMConfig.D($"[CMM] Saved resume for {currentContext.Context}: {leavingCache.resumePosition:F2}s");

                if (!CMMConfig.PreloadCacheEnabled && leavingCache.Current.Clip != null)
                {
                    StagePendingDisposal(leavingCache.Current);
                    leavingCache.Current = default;
                }
            }

            // PlayOnce contexts with no custom files: go silent rather than
            // resolving through the Stack fallback. This keeps the behavior
            // consistent whether files exist or not — vanilla stingers play
            // cleanly over silence, and ambient resumes after the player spawns.
            if (incoming.PlayOnce && !ContextHasFiles(contextIn))
            {
                CMMConfig.D($"[CMM] {contextIn} has no files — fading to silence for vanilla stinger.");
                _pendingContext = "Silence";
                currentContext = GetContext("Silence");
                ++_transitionId;
                FadeToSilence();
                return;
            }

            string contextToPlay = ResolvePlayableContext(contextIn);
            if (contextToPlay == null)
            {
                CMMConfig.D($"[CMM] No playable context in chain for {contextIn} — fading to silence.");
                // On the menu screen, BackgroundMusicMono.Play() fires every frame.
                // Guard against re-triggering FadeToSilence when already silent.
                if (!inGame && currentContext?.Context == "Silence")
                    return;
                _pendingContext = "Silence";
                currentContext = GetContext("Silence");
                ++_transitionId;
                FadeToSilence();
                return;
            }
            if (contextToPlay == "Vanilla")
            {
                if (!vanillaIsActive)
                    ActivateVanilla();
                else
                    _pendingContext = "Vanilla";
                return;
            }

            Contexts candidate = GetContext(contextToPlay);
            bool bypassCooldown = incoming.BypassesCooldown || candidate.BypassesCooldown;
            bool onCooldown = !bypassCooldown
                              && bypassPriority < 0
                              && _contextsOnCooldown.Contains(contextToPlay);

            if (onCooldown)
            {
                CMMConfig.D($"[CMM] {candidate.Context} blocked — cooldown active");
                _pendingContext = currentContext?.Context ?? "Silence";
                if (!CMMConfig.PreloadCacheEnabled
                    && currentContext != null
                    && cacheMap.TryGetValue(currentContext.Context, out var blocked))
                {
                    if (blocked.Current.Clip != null)
                    {
                        if (!blocked.Current.Borrowed) Destroy(blocked.Current.Clip);
                        blocked.Current.Release();
                    }
                }
                return;
            }

            if (vanillaIsActive)
                DeactivateVanilla();

            _cooldownTarget = currentContext;   // the context being left (for its cooldown)
            currentContext = candidate;
            TriggerMusic(contextToPlay);
        }

        public static void TriggerMusic(string contextName)
        {
            CMMConfig.D($"[CMM] TriggerMusic: {contextName}");
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }
            if (_nextCacheCoroutine != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine); _nextCacheCoroutine = null; }
            if (_trackEndCoroutine != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine); _trackEndCoroutine = null; }
            if (_crossFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine); _crossFadeCoroutine = null; }

            int newId = ++_transitionId;
            _scheduleHandledTransition = false;
            _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(Instance.PlayFromCache(contextName, false, newId));
        }

        // ── Scan loops ────────────────────────────────────────────────────────
        // FIX: Track the previous context set so we only touch the stack and
        // call SendContextUpdate when something actually changed.
        private HashSet<string> _previousActiveContexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Maps active context name → what it currently resolves to. Tracking the resolved
        // value (not just null/non-null) means a DMS toggle that changes Combat_High from
        // resolving to "Home_Night" → "Vanilla" (or back) is detected as a change even
        // though the context itself never left the active set.
        private Dictionary<string, string> _previousPlayableContexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Whether any stack entry was still warming up on the previous scan. A context
        // finishing its WarmUp dwell is a state change that none of the other flags can
        // see (the active set and resolved set are identical before and after), so we
        // force one extra evaluation on the warming→clear edge to actually promote it.
        private bool _hadWarmingContexts = false;
        private IEnumerator ContextScanLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(ContextScanCooldown);
                if (!inGame || playerIsDead) continue;

                List<string> activeContexts = ContextResolver.ResolveContext();

                // Compute which of the active contexts are currently playable AND what
                // they resolve to. We need to re-evaluate the stack when the active set,
                // the playable set, OR the resolution of any context changes — a DMS
                // toggle can change Combat_High from resolving to "Home_Night" to "Vanilla"
                // without any context entering or leaving the active set.
                var currentlyPlayable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string c in activeContexts)
                {
                    string p = ResolvePlayableContext(c);
                    if (p != null) currentlyPlayable[c] = p;
                }

                bool namesChanged = activeContexts.Count != _previousActiveContexts.Count
                                    || activeContexts.Any(c => !_previousActiveContexts.Contains(c));
                bool playabilityChanged = currentlyPlayable.Count != _previousPlayableContexts.Count
                                          || currentlyPlayable.Any(kv =>
                                              !_previousPlayableContexts.TryGetValue(kv.Key, out string prev)
                                              || !string.Equals(prev, kv.Value, StringComparison.OrdinalIgnoreCase));

                // Also re-evaluate if currentContext drifted to Silence/null from an external
                // event (PlayOnce ended, load failed, folder emptied) without the active set changing.
                bool currentIsStuck = currentContext == null || currentContext.Context == "Silence";
                // Also keep running if any stack entry is absent from activeContexts but hasn't
                // hit the 1.5s eviction threshold yet — without this the early-continue fires
                // on the second scan and the RemoveAll eviction check never runs again.
                bool hasPendingEvictions = _contextStack.Any(c => !c.Manual && !activeContexts.Contains(c.ContextName));
                // Keep scanning while any context is still warming up: its WarmUp dwell can
                // elapse without the active set changing, and GetTopContext must re-run to
                // promote it once it becomes viable.
                bool hasWarmingContexts = _contextStack.Any(c => !DwellSatisfied(c));
                // The tick a context finishes warming, hasWarmingContexts flips to false
                // and every other flag is already false — so without catching this edge the
                // early-out below swallows the promotion and the context never starts.
                bool warmingJustCleared = _hadWarmingContexts && !hasWarmingContexts;
                _hadWarmingContexts = hasWarmingContexts;
                if (!namesChanged && !playabilityChanged && !currentIsStuck && !vanillaIsActive && !hasPendingEvictions && !hasWarmingContexts && !warmingJustCleared) continue;

                _previousActiveContexts.Clear();
                foreach (string c in activeContexts) _previousActiveContexts.Add(c);
                _previousPlayableContexts.Clear();
                foreach (var kv in currentlyPlayable) _previousPlayableContexts[kv.Key] = kv.Value;

                // Rest of the stack rebuild continues as before...
                foreach (string ctxName in activeContexts)
                {
                    if (_contextStack.All(c => c.ContextName != ctxName))
                    {
                        Contexts ctx = GetContext(ctxName);
                        if (ctx == null) continue;
                        if (ResolvePlayableContext(ctxName) == null) continue;
                        float seenAt = Time.realtimeSinceStartup;
                        _contextStack.Add(new ActiveContext
                        {
                            ContextName = ctxName,
                            Priority = ctx.Priority,
                            ActivatedAt = seenAt,
                            FirstSeenAt = InheritedFirstSeen(ctxName, seenAt),
                            Manual = false
                        });
                    }
                }

                // Refresh "last seen" time for contexts still active, then only
                // evict ones absent for ≥1.5s (3 scan cycles). This prevents a
                // single-scan detection miss from yanking a context off the stack
                // while keeping genuine exits (combat over, left vehicle) responsive.
                float now = Time.realtimeSinceStartup;
                foreach (ActiveContext ac in _contextStack)
                {
                    if (!ac.Manual && activeContexts.Contains(ac.ContextName))
                        ac.ActivatedAt = now;
                }
                _contextStack.RemoveAll(c => !c.Manual
                    && !activeContexts.Contains(c.ContextName)
                    && (now - c.ActivatedAt) >= 1.5f);
                _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));

                string topContext = GetTopContext(); // null = nothing playable + DMS disabled

                if (topContext == null)
                {
                    // Nothing is playable and DMS is off — fade to silence rather than
                    // activating vanilla and releasing the Conductor unnecessarily.
                    if (currentContext?.Context != "Silence")
                    {
                        _pendingContext = null;
                        currentContext = GetContext("Silence");
                        ++_transitionId;
                        FadeToSilence();
                    }
                }
                else if (currentContext == null || topContext != currentContext.Context)
                {
                    // Guard: never interrupt a PlayOnce context with something
                    // of equal or lower priority. The null-clear is intentionally
                    // AFTER this check so the guard is re-evaluated every tick.
                    if (currentContext != null && currentContext.PlayOnce)
                    {
                        Contexts top = GetContext(topContext);
                        if (top == null || top.Priority <= currentContext.Priority)
                            continue; // do NOT clear _pendingContext here
                    }
                    _pendingContext = null; // only clear when we're actually transitioning
                    SendContextUpdate(topContext, 0);
                }
            }
        }

        // FIX: Log cache state only when something has actually changed,
        // not unconditionally every 5 seconds.
        private string _lastCacheLogState = "";

        private IEnumerator StalenessCheckLoop()
        {
            while (true)
            {
                long gcBefore = GC.GetTotalMemory(false);
                yield return new WaitForSeconds(StalenessCheckInterval);
                long gcAfter = GC.GetTotalMemory(false);
                long collected = gcBefore - gcAfter;
                if (collected > 1024 * 1024)
                    CMMConfig.D($"[CMM PERF] GC collected {collected / 1024f:F0} KB between staleness checks");

                if (!inGame) continue;

                // Build state string cheaply; only emit log lines if it changed.
                string cacheState = string.Join(", ", cacheMap
                    .Where(kv => kv.Value.Current.Clip != null)
                    .Select(kv => $"{kv.Key}={kv.Value.Current.Clip.length:F0}s(next={(kv.Value.Next.Clip != null ? "ready" : "null")})"));

                if (cacheState != _lastCacheLogState)
                {
                    CMMConfig.D($"[CMM] Cache state: {cacheState}");
                    CMMConfig.D($"[CMM] Cache memory: {GetCacheMemorySummary(false)}");
                    _lastCacheLogState = cacheState;
                }

                float now = Time.realtimeSinceStartup;
                foreach (var ctx in contexts.Values)
                {
                    if (ctx.Context == currentContext.Context) continue;
                    if (!ctx.CanBeResumed || ctx.TimeUntilContextStale <= 0) continue;

                    ContextCache cache = cacheMap[ctx.Context];
                    if (cache.lastLeftTime < 0) continue;

                    float elapsed = now - cache.lastLeftTime;
                    if (elapsed >= ctx.TimeUntilContextStale)
                    {
                        CMMConfig.D($"[CMM] Context {ctx.Context} is stale ({elapsed:F0}s). Clearing for fresh pick.");
                        cache.resumePosition = 0f;
                        cache.lastLeftTime = -1f;
                        string avoid = cache.lastPlayedFile;
                        cache.lastPlayedFile = null;

                        if (CMMConfig.PreloadCacheEnabled)
                            yield return StartCoroutine(PreloadContext(ctx.Context, avoidFile: avoid));
                    }
                }
            }
        }

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

            while (true)
            {
                yield return new WaitForSeconds(2f);
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

                    // ── FAST PATH: single stat ──────────────────────────────────────
                    DateTime currentWriteTime = DateTime.MinValue;
                    bool statDone = false;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            currentWriteTime = Directory.Exists(dir)
                                  ? Directory.GetLastWriteTimeUtc(dir)
                                  : DateTime.MinValue;
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

                    if (!neverScanned && !previouslyEmpty && currentWriteTime == knownTime && !blacklistRetryDue) continue;
                    lastWriteTimes[contextName] = currentWriteTime;

                    // ── SLOW PATH: enumerate files ──────────────────────────────────
                    string[] currentFiles = null;
                    bool scanDone = false;
                    string[] previousFiles = cache.directorySnapshot ?? new string[0];

                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        currentFiles = Directory.Exists(dir)
                            ? Directory.GetFiles(dir).Where(f => NAudioDecoder.IsSupported(f)).ToArray()
                            : new string[0];
                        scanDone = true;
                    });
                    while (!scanDone) yield return null;

                    Func<string, string> norm = p => string.IsNullOrEmpty(p) ? p
                        : Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    var currentNorm = currentFiles.Select(norm).ToArray();
                    var previousNorm = previousFiles.Select(norm).ToArray();

                    bool filesActuallyChanged =
                        currentNorm.Length != previousNorm.Length ||
                        currentNorm.Except(previousNorm, StringComparer.OrdinalIgnoreCase).Any() ||
                        previousNorm.Except(currentNorm, StringComparer.OrdinalIgnoreCase).Any();

                    bool changed = neverScanned || filesActuallyChanged;
                    if (!changed) continue;

                    // Update the snapshot and invalidate the file-presence cache so
                    // ResolvePlayableContext sees the new state immediately.
                    cache.directorySnapshot = currentFiles;
                    InvalidateContextFileCache(contextName);

                    var removedFiles = previousNorm.Except(currentNorm, StringComparer.OrdinalIgnoreCase).ToArray();
                    foreach (string removed in removedFiles)
                    {
                        _validatedFiles.Remove(removed);
                        _invalidFiles.Remove(removed);
                    }

                    var newFiles = currentNorm.Except(previousNorm, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (newFiles.Length > 0)
                    {
                        CMMConfig.D($"[CMM] Validating {newFiles.Length} new file(s) in {contextName}...");
                        foreach (string newFile in newFiles)
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
                    if (!filesActuallyChanged) continue;

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
                        continue;
                    }

                    bool lastPlayedMissing = false;
                    if (!string.IsNullOrEmpty(cache.lastPlayedFile))
                    {
                        string lastNorm = norm(cache.lastPlayedFile);
                        if (!currentNorm.Any(f => string.Equals(f, lastNorm, StringComparison.OrdinalIgnoreCase)))
                        {
                            CMMConfig.D($"[CMM] Currently-playing file removed: {Path.GetFileName(cache.lastPlayedFile)}");
                            cache.lastPlayedFile = null;
                            if (cache.Current.Clip != null)
                            {
                                StagePendingDisposal(cache.Current);
                                cache.Current = default;
                            }
                            lastPlayedMissing = true;
                        }
                    }

                    if (currentFiles.Length == 0)
                    {
                        CMMConfig.D($"[CMM] {contextName} folder emptied, re-evaluating.");
                        _pendingContext = null;
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
                    else if (lastPlayedMissing || cache.Current.Borrowed)
                    {
                        // Hot-swap reload. Two cases land here:
                        //  • lastPlayedMissing — the custom file that was playing got removed.
                        //  • cache.Current.Borrowed — we're looping the borrowed vanilla
                        //    fallback clip and custom files have just been added back to the
                        //    active context's folder. Re-populating must swap straight back
                        //    to the custom track instead of riding the vanilla bed.
                        // FIX 1 applied here too — see above.
                        _pendingContext = null;
                        yield return StartCoroutine(PreloadContext(ctx.Context));

                        // Only trigger when PreloadContext actually loaded a custom clip;
                        // if it couldn't (no valid files), Current is still the borrowed
                        // clip, so fall through to the normal fallback path instead.
                        if (cacheMap[ctx.Context].Current.Clip != null && !cacheMap[ctx.Context].Current.Borrowed)
                        {
                            CMMConfig.D($"[CMM] Custom track ready for {ctx.Context}, hot-swapping.");
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
                        if (!inGame) continue;
                        string top = GetTopContext();
                        // null = nothing playable + DMS off. Don't forward null into
                        // GetContext/SendContextUpdate (Dictionary rejects a null key);
                        // let ContextScanLoop fade to silence on its next tick.
                        if (top == null) continue;
                        if (top != currentContext.Context)
                        {
                            // Don't let the initial directory scan (neverScanned) or
                            // a file-change event interrupt a PlayOnce context (Spawn/Death)
                            // with something of lower or equal priority.
                            if (currentContext != null && currentContext.PlayOnce)
                            {
                                Contexts topCtx = GetContext(top);
                                if (topCtx == null || topCtx.Priority <= currentContext.Priority)
                                    continue;
                            }
                            _pendingContext = null;
                            SendContextUpdate(top, 0);
                        }
                    }
                }
            }
        }

        // ── Config hot-reload ─────────────────────────────────────────────────
        private IEnumerator ConfigWatchLoop()
        {
            string settingsPath = CMMConfigLoader.SettingsFile(ModPath);
            string contextsPath = CMMConfigLoader.ContextsFile(ModPath);

            // Snapshot timestamps right after init so the first poll doesn't re-fire.
            DateTime settingsTime = File.Exists(settingsPath) ? File.GetLastWriteTimeUtc(settingsPath) : DateTime.MinValue;
            DateTime contextsTime = File.Exists(contextsPath) ? File.GetLastWriteTimeUtc(contextsPath) : DateTime.MinValue;

            while (true)
            {
                yield return new WaitForSeconds(2f);

                bool settingsChanged = false, contextsChanged = false;
                DateTime newSettingsTime = settingsTime, newContextsTime = contextsTime;
                bool done = false;

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (File.Exists(settingsPath))
                        {
                            var t = File.GetLastWriteTimeUtc(settingsPath);
                            if (t != settingsTime) { settingsChanged = true; newSettingsTime = t; }
                        }
                        if (File.Exists(contextsPath))
                        {
                            var t = File.GetLastWriteTimeUtc(contextsPath);
                            if (t != contextsTime) { contextsChanged = true; newContextsTime = t; }
                        }
                    }
                    catch { }
                    done = true;
                });
                while (!done) yield return null;

                if (settingsChanged)
                {
                    settingsTime = newSettingsTime;
                    CMMConfig.D("[CMM] Config/settings.xml changed — reloading.");
                    CMMConfigLoader.LoadSettings(ModPath);
                }

                if (contextsChanged)
                {
                    contextsTime = newContextsTime;
                    CMMConfig.D("[CMM] Config/contexts.xml changed — reloading.");
                    CMMConfigLoader.LoadContexts(ModPath);

                    // Ensure new contexts have cache entries.
                    foreach (var ctx in contexts.Values)
                        if (!cacheMap.ContainsKey(ctx.Context))
                            cacheMap[ctx.Context] = new ContextCache();

                    // Drop stack entries for contexts that no longer exist.
                    _contextStack.RemoveAll(c => !contexts.ContainsKey(c.ContextName));

                    // If the playing context was removed, fade to silence.
                    if (currentContext != null && !contexts.ContainsKey(currentContext.Context))
                    {
                        _pendingContext = null;
                        currentContext = GetContext("Silence");
                        ++_transitionId;
                        FadeToSilence();
                    }

                    // Re-evaluate in case priorities or fallbacks changed.
                    _contextsWithFilesDirty = true;
                    _previousPlayableContexts.Clear();
                }
            }
        }

        // ── Preload ───────────────────────────────────────────────────────────
        private IEnumerator PreloadAllContexts()
        {
            if (!CMMConfig.PreloadCacheEnabled) { CMMConfig.D("[CMM] Preload cache disabled — skipping."); yield break; }
            CMMConfig.D("[CMM] Starting sequential preload for all contexts...");
            foreach (var ctx in contexts.Values)
            {
                if (string.IsNullOrEmpty(ctx.Path)) continue;
                yield return StartCoroutine(PreloadContext(ctx.Context));
            }
            CMMConfig.D("[CMM] Preload complete.");
        }

        private IEnumerator PreloadContext(string contextName, string avoidFile = null)
        {
            ContextCache cache = cacheMap[contextName];
            if (cache.isPreloading) { yield break; }
            cache.isPreloading = true;
            yield return StartCoroutine(PreloadContextCore(contextName, avoidFile));
            cache.isPreloading = false;
        }

        private IEnumerator PreloadContextCore(string contextName, string avoidFile = null)
        {
            ContextCache cache = cacheMap[contextName];
            string filePath;

            if (cache.lastPlayedFile != null && File.Exists(cache.lastPlayedFile)
                && cache.lastPlayedFile != avoidFile
                && !_invalidFiles.Contains(cache.lastPlayedFile))
                filePath = cache.lastPlayedFile;
            else
            {
                cache.resumePosition = 0f;
                cache.lastPlayedFile = null;
                filePath = PickNextFile(contextName, avoidFile: avoidFile);
            }

            if (filePath == null)
            {
                Contexts ctx2 = GetContext(contextName);
                string fallback = ctx2?.Fallback ?? "None";
                if (fallback != "None" && fallback != contextName)
                    filePath = PickNextFile(fallback, avoidFile: null);
                if (filePath == null)
                { CMMConfig.D($"[CMM] Preload: no files for {contextName}"); yield break; }
            }

            if (_invalidFiles.Contains(filePath))
            {
                filePath = PickNextFile(contextName, avoidFile: filePath);
                if (filePath == null)
                { CMMConfig.D($"[CMM] Preload: no valid files for {contextName}"); yield break; }
            }

            // ── NAudio streaming path ─────────────────────────────────────────
            {
                // Menu always uses full-decode so the PCMReaderCallback is a plain
                // Array.Copy — immune to CPU pressure during the loading screen.
                if (CMMConfig.PreloadCacheEnabled || contextName == "Menu")
                {
                    // ── Full-decode path (PreloadCacheEnabled = true) ─────────
                    // Keep the existing full-decode behaviour so the preload
                    // cache works as before.
                    DecodedAudio decoded = null;
                    byte[] rawBytes = null;
                    bool ioOk = false;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { rawBytes = File.ReadAllBytes(filePath); ioOk = true; }
                        catch { }
                        ioOk = true; // set even on failure so coroutine doesn't hang
                    });
                    while (!ioOk) yield return null;

                    if (rawBytes != null)
                    {
                        bool decodeOk = false;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try { decoded = NAudioDecoder.DecodeFileFromBytes(rawBytes, filePath); }
                            catch { }
                            decodeOk = true;
                        });
                        while (!decodeOk) yield return null;
                    }

                    if (decoded == null)
                    {
                        MarkInvalidIfCorrupt(filePath, $"Preload (full decode) failed for {contextName}");
                        if (!File.Exists(filePath))
                            PruneMissingFromSnapshot(contextName, filePath);
                        else
                            InvalidateContextFileCache(contextName);
                        yield break;
                    }

                    // Menu uses a non-streaming clip so the audio thread reads from
                    // Unity's native buffer — completely immune to GC pauses.
                    // All other full-decode contexts keep the managed streaming path.
                    bool isMenu = contextName == "Menu";
                    AudioClip fullClip = isMenu
                        ? NAudioDecoder.CreateNonStreamingClip(decoded, Path.GetFileNameWithoutExtension(filePath))
                        : NAudioDecoder.CreateStreamingClip(decoded, Path.GetFileNameWithoutExtension(filePath));
                    if (fullClip == null) yield break;

                    if (cache.Current.Clip != null && !cache.Current.Borrowed) Destroy(cache.Current.Clip);
                    cache.Current.Release();

                    cache.Current.Clip = fullClip;
                    // Menu: native buffer owns the samples now; drop the managed copy.
                    if (isMenu) { decoded.Samples = null; decoded = null; }
                    cache.Current.Decoded = isMenu ? null : decoded;
                    cache.lastPlayedFile = filePath;
                    CMMConfig.D($"[CMM] Preloaded (full decode{(isMenu ? "/native" : "")}): {contextName} — " +
                            $"{Path.GetFileName(filePath)}");
                }
                else
                {
                    // ── Streaming path (default) ──────────────────────────────
                    WaveStream reader = null;
                    yield return StartCoroutine(OpenReaderAsync(filePath, r => reader = r));

                    if (reader == null)
                    {
                        // OpenReaderAsync already decided whether to blacklist (corrupt)
                        // or skip (transient/missing). If the file is genuinely gone, drop
                        // it from the snapshot now so the resolver stops re-picking this
                        // same missing file every scan tick (removed-file replay loop);
                        // otherwise just refresh presence from the existing snapshot.
                        if (!File.Exists(filePath))
                            PruneMissingFromSnapshot(contextName, filePath);
                        else
                            InvalidateContextFileCache(contextName);
                        yield break;
                    }

                    AudioClip clip = NAudioDecoder.CreateStreamingClipFromReader(
                        reader, Path.GetFileNameWithoutExtension(filePath));

                    if (clip == null)
                    {
                        reader.Dispose();
                        Log.Error($"[CMM] Preload: CreateStreamingClipFromReader null for {contextName}");
                        yield break;
                    }

                    if (cache.Current.Clip != null)
                    {
                        if (!cache.Current.Borrowed) Destroy(cache.Current.Clip);
                        cache.Current.Release();
                    }

                    cache.Current.Clip   = clip;
                    cache.Current.Reader = reader;
                    cache.lastPlayedFile = filePath;

                    float lenSec = reader.Length > 0 && reader.WaveFormat.BlockAlign > 0
                        ? (float)(reader.Length / reader.WaveFormat.BlockAlign) / reader.WaveFormat.SampleRate
                        : 0f;
                    CMMConfig.D($"[CMM] Preloaded (streaming): {contextName} — " +
                            $"{Path.GetFileName(filePath)}" +
                            (lenSec > 0 ? $" ({lenSec:F1}s)" : "") +
                            (NAudioDecoder.IsOpusFile(filePath) ? " [Opus/seekable]" : ""));
                }
            }
        }

        // FIX: ContextCooldownTimer is now per-context. The cooldown is
        // recorded against the specific context name that triggered it, so
        // unrelated contexts at higher priority are never blocked.
        private IEnumerator ContextCooldownTimer(string contextName, int seconds)
        {
            _contextsOnCooldown.Add(contextName);
            CMMConfig.D($"[CMM] Cooldown started for {contextName}: {seconds}s");
            yield return new WaitForSeconds(seconds);
            _contextsOnCooldown.Remove(contextName);
            CMMConfig.D($"[CMM] Cooldown expired for {contextName}.");
            //_cooldownCoroutine = null;
        }

        private IEnumerator DiagnosticLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(3f);
                if (!inGame && !vanillaIsActive) continue;

                float gameMusicVol = GamePrefs.GetFloat(EnumGamePrefs.OptionsMusicVolumeLevel);
                string stack = _contextStack.Count > 0
                    ? string.Join(" > ", _contextStack.Select(c => c.ContextName))
                    : "(empty)";

                CMMConfig.D($"[CMM DIAG] vanilla={vanillaIsActive}" +
                        $" | current={currentContext?.Context ?? "null"}" +
                        $" | pending={_pendingContext ?? "null"}" +
                        $" | gameSection={LastKnownGameSection}" +
                        $" | gameMusicVol={gameMusicVol:F2}" +
                        $" | mixerMethod={_setDMVMethod != null}" +
                        $" | mixerInst={_mixerInstance != null}" +
                        $" | primaryVol={CMM_Primary_Channel?.volume:F2}" +
                        $" | secondaryVol={CMM_Secondary_Channel?.volume:F2}" +
                        $" | stack=[{stack}]");
            }
        }
    }
}
