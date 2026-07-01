
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

        private static Coroutine _nextCacheCoroutine = null;
        private static Coroutine _trackEndCoroutine = null;
        private static Coroutine _crossFadeCoroutine = null;
        private static Coroutine _vanillaFadeInCoroutine = null;
        private static Coroutine _menuVanillaFadeCoroutine = null;

        // Live count of CMM's music coroutines, for the per-frame stutter watchdog
        // (CMMPerf.FrameTick). A rising number during combat churn means crossfades/
        // watchers are piling up faster than they finish — a sustained-stutter suspect.
        internal static int LiveMusicCoroutineCount
        {
            get
            {
                int n = 0;
                if (_nextCacheCoroutine      != null) n++;
                if (_trackEndCoroutine       != null) n++;
                if (_crossFadeCoroutine      != null) n++;
                if (_vanillaFadeInCoroutine  != null) n++;
                if (_menuVanillaFadeCoroutine != null) n++;
                return n;
            }
        }

        public static TrackOrderMode GlobalTrackOrder = TrackOrderMode.RandomNoRepeat;
        public static float ContextScanCooldown = 0.5f;
        public static float StalenessCheckInterval = 5f;
        public static float TrackEndPollInterval = 0.25f;
        public static float CrossFadeDuration = 2f;
        public static float TrackTransitionFadeDuration = 6f;
        public static float TrackTransitionPreloadLead = 10f;

        // Graceful, deliberate fade for the menu↔game boundary (entering a world, exiting
        // back to the menu) — intentionally longer than the in-game CrossFadeDuration.
        public const float MenuTransitionFade = 5f;
        // Graceful fade when the game pauses (entering the ESC menu / inventory in single-player):
        // the music fades out over this window rather than cutting, and fades back in over it on
        // resume. The quit-to-menu teardown reuses the same fade — quitting mid-fade just continues
        // it to silence (see OnGamePauseChanged and FadeOutAndCleanupCoroutine).
        public const float PauseFadeDuration = 3f;
        // Post-spawn / post-teleport deep-cushion grace. The world-load deep buffer (_gameIsLoading)
        // is cleared the instant the player spawns, but heavy chunk STREAMING continues for a while
        // after the loading screen closes — starving the native mixer's producer (the managed Pump)
        // and underrunning the tight in-game ring (an audible pause). AfterPlayerRespawn (which fires
        // on initial spawn, death-respawn, AND teleport) arms this deadline; VolumeSyncLoop holds the
        // extra cushion until it passes, then it eases back to tight by natural drain (no flush → no
        // teleport). Covers the diagnosed post-load streaming; unpredictable heavy events elsewhere
        // (fast travel, horde spawns) still fall back to the mixer's reactive growth.
        public const float PostSpawnDeepBufferSeconds = 30f;
        internal static float _deepBufferUntil;

        // While true, the menu-screen BackgroundMusicMono auto-trigger does NOT start CMM's
        // menu music. Held during the game-exit fade so the menu music can't fade in until
        // the game music has fully faded out (set in FadeOutAndCleanup, cleared once silent).
        internal static bool _menuMusicHold;

        private static readonly HashSet<string> _contextsWithFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _contextsWithFilesDirty = true;
        // Contexts whose presence needs a background (re)scan because no in-memory
        // snapshot was available when they were invalidated. Drained by PresenceScanLoop.
        private static readonly HashSet<string> _presenceDirty =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static float PresenceScanInterval = 0.5f;

        // ── Loudness menu-prewarm / save pacing ────────────────────────────────
        public static float LoudnessSweepStartDelay = 8f;   // let startup settle first
        public static float LoudnessSweepInterval = 0.25f;  // menu: tiny yield between prewarm passes
        public static float LoudnessSweepInGameInterval = 1f;  // in-game: idle tick (prewarm only runs on the menu)
        public static float LoudnessSaveDebounce = 5f;      // flush the cache if dirty

        // ── Init ──────────────────────────────────────────────────────────────
        public static void Init(string modPath)
        {
            ModPath = modPath;
            MusicDirectory = Path.Combine(modPath, "Music");

            // Preload the native decode shim by full path BEFORE any decode runs — the
            // Mods subfolder isn't on the OS native search path, so the later
            // DllImport("CMM_Decoder") only resolves once the module is already in-process.
            // Required for ALL formats (every codec decodes natively, no managed fallback).
            NativeOpus.Preload(modPath);
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
            ScriptHost.Load(modPath);   // external Lua decision script (Config/context.lua)

            foreach (var ctx in contexts.Values)
                cacheMap[ctx.Context] = new ContextCache();

            // One-time synchronous presence scan at mod load (before menu music
            // starts) so ContextHasFiles answers correctly from frame one. All
            // subsequent updates run off the main thread via PresenceScanLoop.
            RebuildContextsWithFiles();

            currentContext = GetContext("Silence");
            _pendingContext = "Silence";
            _transitionId = 0;

            CMM_CoRoutines = CMM_Audio_Object.AddComponent<CoRoutine>();
            CMM_CoRoutines.StartCoroutine(Instance.ContextScanLoop());
            CMM_CoRoutines.StartCoroutine(Instance.StalenessCheckLoop());
            CMM_CoRoutines.StartCoroutine(Instance.PresenceScanLoop());
            CMM_CoRoutines.StartCoroutine(Instance.RecursiveCrawlLoop());
            CMM_CoRoutines.StartCoroutine(Instance.VolumeSyncLoop());
            CMM_CoRoutines.StartCoroutine(Instance.DiagnosticLoop());
            CMM_CoRoutines.StartCoroutine(Instance.LoudnessSweepLoop());

            // Off-frame-loop control thread (experimental): pace + run the debounced loudness
            // cache save on CmmScheduler instead of a coroutine, so the slow XML/file IO lands
            // off Unity's main thread and keeps ticking through a main-thread freeze. The store
            // snapshot still hops back to the main thread (MainThreadDispatcher.Send). When the
            // flag is off, the original coroutine runs instead. CaptureMainThread tags THIS
            // thread (Init runs on the main thread) so the dispatcher can detect self-calls.
            MainThreadDispatcher.CaptureMainThread();
            if (CMMConfig.NativeScheduler)
            {
                CmmScheduler.Every(LoudnessSaveDebounce, LoudnessSaveTick, "loudness-save");
                // Track-end auto-advance, off the frame loop: the poll reads only thread-safe
                // mixer/transition state and marshals the actual advance back to the main
                // thread, so advancement survives a main-thread freeze. Replaces the per-track
                // NativeTrackLoop coroutine (still used when the flag is off).
                CmmScheduler.Every(TrackEndPollInterval, NativeAdvanceTick, "native-advance");
                // Config/settings/contexts/script hot-reload watch, off the frame loop: the
                // stamp probe runs on the scheduler thread and only marshals an actual change
                // back to the main thread (see ConfigWatchTick). Replaces ConfigWatchLoop.
                ConfigWatchInit();
                CmmScheduler.Every(2.0, ConfigWatchTick, "config-watch");
                // Music-folder watch, off the frame loop: the directory-signature probe +
                // active-track ping run on the scheduler thread and only an actual change
                // marshals its reaction (hot-swap / re-resolve) back to the main thread, so
                // a file edit is picked up even mid-freeze. Replaces FileWatchLoop.
                FileWatchInit();
                CmmScheduler.Every(2.0, FileWatchTick, "file-watch");
                CmmScheduler.Start();
            }
            else
            {
                CMM_CoRoutines.StartCoroutine(Instance.LoudnessSaveLoop());
                CMM_CoRoutines.StartCoroutine(Instance.ConfigWatchLoop());
                CMM_CoRoutines.StartCoroutine(Instance.FileWatchLoop());
            }

            // Follow OS audio-device changes (headset plugged/unplugged, default endpoint
            // switched). Unity re-targets its own audio automatically; the native mixer owns a
            // device pinned at open time, so it must reopen on the new endpoint or it strands
            // on the old one. Registered once.
            if (!_audioConfigHooked)
            {
                AudioSettings.OnAudioConfigurationChanged += OnAudioConfigChanged;
                _audioConfigHooked = true;
            }

            CMMConfig.D($"[CMM] Initialized! Decoder: CMM_Decoder (native) | Preload: {CMMConfig.PreloadCacheEnabled}");
        }

        private static bool _audioConfigHooked = false;

        // OS audio device / config changed. Ask the native mixer to reopen its output device
        // on the new default endpoint (no-op when nothing is playing — the next Play opens
        // fresh on the new endpoint anyway). deviceWasChanged is true only for an actual device
        // swap, but Unity also fires this for sample-rate/speaker changes, which equally
        // invalidate our open device, so we reinit on any firing.
        private static void OnAudioConfigChanged(bool deviceWasChanged)
        {
            CMMConfig.D($"[CMM] Audio configuration changed (deviceWasChanged={deviceWasChanged}).");
            CmmMixer.Instance?.RequestDeviceReinit();
        }

        public static void OnEnterGame()
        {
            if (!inGame) return;
            // Edge-triggered DMS mute: CMM takes over in-game music on world entry, so mute
            // the game's Dynamic Music System ONCE here (dmsVol persists frame-to-frame)
            // instead of re-asserting it every Conductor.Update frame. Both in-game entry
            // paths — the Conductor.Update prefix and PlayerRespawn — funnel through
            // OnEnterGame, so this single point covers them. Vanilla fallback re-raises
            // dmsVol via the vanilla fade coroutines; don't fight that while it's active.
            //
            // FORCE the write (bypass the idempotence guard): MixerController is a persistent
            // static singleton whose Master AudioMixer keeps dmsVol across worlds. On a
            // leave→rejoin the game's DMS preload (MixerController.Init / OnSnapshotTransition)
            // re-raises dmsVol to the slider level DURING LOAD, while inGame is still false — so
            // Patch_DmsVolumeReclamp (gated on inGame) skips it. Our cached _currentVanillaVolume
            // is still 0 from the previous session's mute, so a non-forced SetVanillaMusicVolume(0f)
            // no-ops and the mute is never re-applied — leaving vanilla DMS (e.g. the trader theme
            // when you spawn straight into a trader) audible under CMM's own music. force:true
            // re-asserts the mute on every entry; the reclamp still covers later in-game raises.
            if (!vanillaIsActive) SetVanillaMusicVolume(0f, force: true);
        }

        // ── SendContextUpdate ─────────────────────────────────────────────────
        public static void SendContextUpdate(string contextIn, int bypassPriority = -1)
        {
            if (contextIn == "Vanilla")
            {
                if (vanillaIsActive || _pendingContext == "Vanilla") return;
                // Save the native mixer playhead so a quick return resumes mid-track.
                if (currentContext != null) CaptureNativeResume(currentContext);
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

            // Save the native mixer playhead so a quick return resumes mid-track.
            if (currentContext != null) CaptureNativeResume(currentContext);

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
                return;
            }

            if (vanillaIsActive)
                DeactivateVanilla();

            _cooldownTarget = currentContext;   // the context being left (for its cooldown)
            ArmContextCooldown(_cooldownTarget);
            currentContext = candidate;
            TriggerMusic(contextToPlay);
        }

        // Put the context we're LEAVING on cooldown (its own Cooldown value) so it can't
        // immediately re-trigger — anti-flicker (e.g. combat receding then instantly re-firing).
        // The old AudioSource path armed this on crossfade COMPLETION; the native mixer fades on
        // its own thread with no control-layer callback, so we arm at transition time instead —
        // equivalent for the membership-window gate in SendContextUpdate. Fire-and-forget per
        // context: each timer adds itself and removes itself after Cooldown seconds, so multiple
        // contexts can cool down concurrently and none is ever orphaned in the set (the old
        // single-_cooldownCoroutine handle leaked a context whenever a second transition cancelled
        // its timer before the Remove ran, permanently blocking that context).
        private static void ArmContextCooldown(Contexts leaving)
        {
            if (leaving == null || leaving.Cooldown <= 0 || CMM_CoRoutines == null || Instance == null) return;
            CMM_CoRoutines.StartCoroutine(Instance.ContextCooldownTimer(leaving.Context, leaving.Cooldown));
        }

        public static void TriggerMusic(string contextName)
        {
            CMMConfig.D($"[CMM] TriggerMusic: {contextName}");
            if (_nextCacheCoroutine != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine); _nextCacheCoroutine = null; }
            if (_trackEndCoroutine != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine); _trackEndCoroutine = null; }
            if (_crossFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine); _crossFadeCoroutine = null; }

            int newId = ++_transitionId;
            CMMPerf.BeginTransition(contextName);
            // Route the context to the native mixer (custom-file contexts) or stop it
            // (Vanilla / Silence / empty folder).
            StartContextCoroutine(contextName, false, newId);
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

                // GC instrumentation: bracket the per-tick resolve + change-detection work
                // below (no yields in this window, so the main-thread heap delta is CMM's
                // own allocation). Reported and reset by StalenessCheckLoop.
                long scanAllocBefore = GC.GetTotalMemory(false);

                List<string> activeContexts = ContextResolver.ResolveContext();

                // Time the synchronous change-detection gate (no yields here, so this is
                // the steady-state per-tick cost that runs whether or not we transition).
                var _gatePerf = CMMPerf.Measure(CMMPerf.Phase.ScanGate);

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
                // Convergence safety net. currentContext names a real audio context but
                // nothing is audible and NO transition machinery is in flight — a prior
                // transition was interrupted or its load failed and stranded us in silence
                // (a burst of interrupting contexts is the usual cause). The name-difference
                // check below would miss this whenever the stranded context still equals the
                // top context, so detect it here and force a re-assert. All four coroutines
                // must be null: if any is live, a normal transition/track-flip is already
                // recovering and re-triggering would race it.
                bool currentAudioDead = currentContext != null
                    && currentContext.Context != "Silence"
                    && currentContext.Context != "Vanilla"
                    && !vanillaIsActive
                    && _crossFadeCoroutine == null
                    && _trackEndCoroutine == null
                    && !_advancePending          // an off-frame-loop advance is in flight (NativeScheduler)
                    && _nextCacheCoroutine == null
                    // The native mixer is the output; it's "busy" while a voice is live (or a
                    // start was just dispatched). A PlayOnce stinger / looping track starts no
                    // NativeTrackLoop, so this is the only signal that it's still playing.
                    && !NativeOutputBusy();
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

                // Keep scanning while a PlayOnce stinger (Spawn / Death / POI_Cleared) is still
                // AUDIBLY playing, even if the active set hasn't changed. The stinger is current
                // but below combat/panic priority; without this the early-out swallows the standing
                // mismatch and a higher-priority danger context can't cut in until the stinger ends
                // (e.g. respawning INTO existing combat — combat was already active, so namesChanged
                // is false). The PlayOnce guard at the transition below still blocks anything of
                // EQUAL-or-LOWER priority, so the stinger is only interrupted by genuine higher-pri
                // events — exactly "let combat cut in to warn you're in danger".
                bool currentIsLivePlayOnce = currentContext != null && currentContext.PlayOnce && !currentAudioDead;

                // Record this tick's allocation (the resolve + gate work above runs every
                // tick whether or not we end up transitioning). Drop negative samples — a GC
                // fired inside the window, so the delta is meaningless there.
                long scanAllocDelta = GC.GetTotalMemory(false) - scanAllocBefore;
                if (scanAllocDelta > 0) _scanAllocBytes += scanAllocDelta;
                _scanAllocTicks++;

                _gatePerf.Dispose();
                if (!namesChanged && !playabilityChanged && !currentIsStuck && !currentAudioDead && !vanillaIsActive && !hasPendingEvictions && !hasWarmingContexts && !warmingJustCleared && !currentIsLivePlayOnce) continue;

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
                    // ...unless a PlayOnce is still audibly playing: never interrupt it with
                    // something of equal or lower priority. A STRANDED (silent) PlayOnce has
                    // nothing left to protect, so the guard is skipped when audio is dead.
                    if (currentContext != null && currentContext.PlayOnce && !currentAudioDead)
                    {
                        Contexts top = GetContext(topContext);
                        if (top == null || top.Priority <= currentContext.Priority)
                            continue; // do NOT clear _pendingContext here
                    }
                    _pendingContext = null; // only clear when we're actually transitioning
                    SendContextUpdate(topContext, 0);
                }
                else if (currentAudioDead)
                {
                    // topContext == currentContext.Context, but it is silent and idle with no
                    // transition in flight — re-assert it so the priority music actually
                    // starts. SendContextUpdate treats this as a reload-of-current (it bypasses
                    // the priority gate), so the silent strand is recovered.
                    CMMConfig.D($"[CMM] Convergence: {currentContext.Context} stranded silent — re-asserting {topContext}.");
                    _pendingContext = null;
                    SendContextUpdate(topContext, 0);
                }
            }
        }

        // GC instrumentation (debug). Per-tick allocation of ContextScanLoop's resolve +
        // change-detection section — the dominant steady-state allocator (~90% of CMM's
        // churn) — accumulated between StalenessCheckLoop reports so CMM's GC contribution
        // can be estimated live in-game. These are main-thread GetTotalMemory deltas, i.e.
        // an estimate: a GC firing mid-window understates that sample (negatives dropped),
        // and a concurrent background-thread allocation can slightly inflate it.
        private static long _scanAllocBytes = 0;
        private static int  _scanAllocTicks = 0;

        // FIX: Log cache state only when something has actually changed,
        // not unconditionally every 5 seconds.
        private string _lastCacheLogState = "";

        private IEnumerator StalenessCheckLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(StalenessCheckInterval);

                // GC contribution estimate. _scanAllocBytes holds the per-tick allocation of
                // ContextScanLoop's resolve+gate section (the dominant steady-state source)
                // accumulated since the last report; convert to a rate so it can be weighed
                // against the game's own churn (typically hundreds of KB–MB/s), then reset.
                if (_scanAllocTicks > 0)
                {
                    float scanKB = _scanAllocBytes / 1024f;
                    float bytesPerTick = (float)_scanAllocBytes / _scanAllocTicks;
                    float kbPerSec = scanKB / StalenessCheckInterval;
                    CMMConfig.D($"[CMM PERF] Scan churn: {scanKB:F1} KB / {_scanAllocTicks} ticks " +
                                $"= {bytesPerTick:F0} B/tick, ~{kbPerSec:F1} KB/s " +
                                $"(process heap {GC.GetTotalMemory(false) / 1048576f:F1} MB)");
                    _scanAllocBytes = 0;
                    _scanAllocTicks = 0;
                }

                // Per-phase timing rollup (poll/resolve/gate/transition), when PerfTiming is on.
                CMMPerf.DumpAndReset();

                if (!inGame) continue;

                // Build state string cheaply; only emit log lines if it changed. The native
                // mixer holds no managed clip cache, so report each context's current pick.
                string cacheState = string.Join(", ", cacheMap
                    .Where(kv => kv.Value.playingFile != null || kv.Value.stagedFile != null)
                    .Select(kv => $"{kv.Key}=play:{(kv.Value.playingFile != null ? Path.GetFileName(kv.Value.playingFile) : "-")}" +
                                  $"/staged:{(kv.Value.stagedFile != null ? Path.GetFileName(kv.Value.stagedFile) : "-")}"));

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
                        cache.lastPlayedFile = null;
                        // Clear the staged pin too so the next switch-in re-picks fresh rather
                        // than replaying the stale track. StartNativeContext picks on demand.
                        cache.stagedFile = null;
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

            // How recently (seconds) a folder must have been written for us to distrust an
            // unchanged directory write-time and force a reconciling enumeration anyway.
            // Covers a couple of 2s poll intervals plus the 2s FAT/exFAT stamp resolution,
            // so a swap that collides with the previous stamp value is still caught.
            const float SignatureFreshnessWindow = 6f;

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
            // One main-thread hop to snapshot the watch set + whether anything is blacklisted.
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

                // Hot-reload the Lua decision script on its own write-time change.
                // Cheap stamp check on the main thread; only re-parses when it changed.
                ScriptHost.CheckReload();
                if (settingsChanged) settingsTime = newSettingsTime;
                if (contextsChanged) contextsTime = newContextsTime;
                if (settingsChanged || contextsChanged)
                    yield return StartCoroutine(ApplyConfigReload(settingsChanged, contextsChanged));
            }
        }

        // Shared config-reload body (settings + contexts), MAIN-THREAD only: it reloads
        // settings, swaps the contexts dict and re-triggers playback — all touching the
        // lock-free contexts/cacheMap/_contextStack/currentContext. Started by the
        // ConfigWatchLoop coroutine (the NativeScheduler-off fallback) or, off the frame
        // loop, by ConfigWatchTick → MainThreadDispatcher.Post when the scheduler-thread
        // stamp probe sees a change. The contexts-changed branch keeps its own off-thread
        // folder enumeration (yield-driven), so this stays a coroutine.
        private IEnumerator ApplyConfigReload(bool settingsChanged, bool contextsChanged)
        {
            if (settingsChanged)
            {
                CMMConfig.D("[CMM] Config/settings.xml changed — reloading.");
                CMMConfigLoader.LoadSettings(ModPath);
            }

            if (contextsChanged)
            {
                CMMConfig.D("[CMM] Config/contexts.xml changed — reloading.");

                // Snapshot each context's source (folder + recursion) BEFORE the
                // reload swaps the dict, so we can tell which contexts had their
                // Path repointed. A surviving context whose folder moved keeps the
                // same name, so none of the add/remove handling below fires for it.
                var oldSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in contexts)
                    oldSources[kv.Key] = $"{kv.Value?.Path}|{kv.Value?.IncludeSubfolders}";

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

                // A context whose folder (or recursion flag) was repointed still
                // carries a snapshot + lastPlayedFile + (if it's the one playing) a
                // live clip from its OLD directory. Forget that stale state so the new
                // folder is used from here on, and collect the repointed contexts so we
                // can refresh their presence and recover playback below.
                string activeName = currentContext?.Context;
                var repointed = new List<PresenceScanItem>();
                foreach (var ctx in contexts.Values)
                {
                    oldSources.TryGetValue(ctx.Context, out string oldSrc);
                    string newSrc = $"{ctx.Path}|{ctx.IncludeSubfolders}";
                    if (string.Equals(oldSrc, newSrc, StringComparison.OrdinalIgnoreCase)) continue;
                    if (cacheMap.TryGetValue(ctx.Context, out var movedCache))
                    {
                        movedCache.directorySnapshot = null;   // force fresh enumeration of the new folder
                        movedCache.lastPlayedFile = null;      // old folder's track no longer belongs here
                        movedCache.resumePosition = 0f;
                        movedCache.lastLeftTime = -1f;
                    }
                    if (!string.IsNullOrEmpty(ctx.Path))
                        repointed.Add(new PresenceScanItem
                        {
                            Name    = ctx.Context,
                            Dir     = Path.Combine(MusicDirectory, ctx.Path),
                            Option  = SearchOptionFor(ctx),
                            Claimed = GetClaimedSubtreePrefixes(ctx.Context),
                        });
                }

                if (repointed.Count > 0)
                {
                    // Refresh presence + snapshot for the repointed contexts NOW
                    // (enumerate the NEW folders off-thread, apply on the main thread)
                    // rather than leaving it to PresenceScanLoop. That loop runs far more
                    // often than FileWatchLoop, so it would refill the snapshot from the
                    // new folder before either the recovery decision here or FileWatchLoop
                    // could act on the change — stranding the swap on the next end-of-track
                    // pick. Doing it synchronously makes the decision below deterministic.
                    var results = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                    bool scanDone = false;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        foreach (var item in repointed)
                        {
                            try
                            {
                                results[item.Name] = Directory.Exists(item.Dir)
                                    ? Directory.GetFiles(item.Dir, "*", item.Option)
                                        .Where(f => AudioDecoder.IsSupported(f)
                                                    && !IsClaimedByDeeperContext(f, item.Claimed))
                                        .ToArray()
                                    : new string[0];
                            }
                            catch { results[item.Name] = new string[0]; }
                        }
                        scanDone = true;
                    });
                    while (!scanDone) yield return null;

                    foreach (var kv in results)
                    {
                        if (cacheMap.TryGetValue(kv.Key, out var c)) c.directorySnapshot = kv.Value;
                        bool has = kv.Value.Any(f => !_invalidFiles.Contains(f));
                        if (has) _contextsWithFiles.Add(kv.Key);
                        else _contextsWithFiles.Remove(kv.Key);
                    }

                    // ── Recover playback deterministically ───────────────────────
                    // Neither ContextScanLoop nor FileWatchLoop reliably reacts here:
                    // the playing context keeps the same name (so no add/remove fires),
                    // and once a missing folder has dropped us to Vanilla, currentContext
                    // is "Vanilla" — so the FileWatchLoop active-context reaction never
                    // matches it either. Handle both cases inline.
                    bool activeRepointed = activeName != null && results.ContainsKey(activeName)
                                           && cacheMap.ContainsKey(activeName);

                    if (activeRepointed)
                    {
                        // The context that's playing was repointed to a new folder: stop the
                        // orphaned buffered voice and re-trigger so the native mixer picks a
                        // track from the new folder (mirrors FileWatchLoop's lastPlayedMissing).
                        var activeCache = cacheMap[activeName];
                        CMMConfig.D($"[CMM] Active context {activeName} repointed — hot-swapping to new folder.");
                        activeCache.lastPlayedFile = null;
                        activeCache.stagedFile = null;   // drop any pin from the old folder
                        _pendingContext = null;

                        if (ContextHasFiles(activeName))
                        {
                            StopNativeIfActive(0.25f);
                            currentContext = GetContext(activeName);
                            TriggerMusic(activeName);
                        }
                        else if (inGame)
                            SendContextUpdate(GetTopContext() ?? "Ambient", 0);  // new folder empty — fall back
                        else
                            SendContextUpdate(activeName);                        // menu — re-resolve its chain
                    }
                    else if (currentContext == null
                             || currentContext.Context == "Vanilla"
                             || currentContext.Context == "Silence")
                    {
                        // We're on a fallback (no custom music) because the active context
                        // had no files; a repointed context has now come back with tracks.
                        // Re-assert the top context so it reclaims playback from vanilla.
                        if (inGame)
                        {
                            string top = GetTopContext();
                            if (top != null && (currentContext == null || top != currentContext.Context))
                            {
                                CMMConfig.D($"[CMM] Repointed folder repopulated — reclaiming with {top}.");
                                _pendingContext = null;
                                SendContextUpdate(top, 0);
                            }
                        }
                        else if (results.ContainsKey("Menu"))
                        {
                            CMMConfig.D("[CMM] Menu folder repointed — re-triggering menu music.");
                            SendContextUpdate("Menu");
                        }
                    }
                }

                // Re-evaluate in case priorities or fallbacks changed.
                _contextsWithFilesDirty = true;
                _previousPlayableContexts.Clear();
            }
        }

        // ── Off-frame-loop config watch (NativeScheduler) ─────────────────────
        // The cheap stamp probe runs on the scheduler thread; only an ACTUAL change marshals
        // the reload back to the main thread. So a settings/contexts/script edit is picked up
        // even while Unity's main thread is frozen (world load/unload), and there is no per-
        // frame busy-poll. Mirrors ConfigWatchLoop, which remains the flag-off fallback.
        private static string   _cfgSettingsPath, _cfgContextsPath;
        private static DateTime _cfgSettingsTime, _cfgContextsTime;

        internal static void ConfigWatchInit()
        {
            _cfgSettingsPath = CMMConfigLoader.SettingsFile(ModPath);
            _cfgContextsPath = CMMConfigLoader.ContextsFile(ModPath);
            // Snapshot timestamps right after init so the first poll doesn't re-fire.
            _cfgSettingsTime = File.Exists(_cfgSettingsPath) ? File.GetLastWriteTimeUtc(_cfgSettingsPath) : DateTime.MinValue;
            _cfgContextsTime = File.Exists(_cfgContextsPath) ? File.GetLastWriteTimeUtc(_cfgContextsPath) : DateTime.MinValue;
        }

        internal static void ConfigWatchTick()
        {
            bool settingsChanged = false, contextsChanged = false;
            DateTime ns = _cfgSettingsTime, nc = _cfgContextsTime;
            try
            {
                if (File.Exists(_cfgSettingsPath))
                {
                    var t = File.GetLastWriteTimeUtc(_cfgSettingsPath);
                    if (t != _cfgSettingsTime) { settingsChanged = true; ns = t; }
                }
                if (File.Exists(_cfgContextsPath))
                {
                    var t = File.GetLastWriteTimeUtc(_cfgContextsPath);
                    if (t != _cfgContextsTime) { contextsChanged = true; nc = t; }
                }
            }
            catch { /* transient IO during an external save — retry next tick */ }
            _cfgSettingsTime = ns; _cfgContextsTime = nc;

            bool scriptChanged = ScriptHost.ScriptChangedOnDisk();
            if (!settingsChanged && !contextsChanged && !scriptChanged) return;   // nothing changed — no main hop

            MainThreadDispatcher.Post(() =>
            {
                if (scriptChanged) ScriptHost.CheckReload();
                if (settingsChanged || contextsChanged)
                    CMM_CoRoutines.StartCoroutine(Instance.ApplyConfigReload(settingsChanged, contextsChanged));
            });
        }

        // Picks a snapshot entry that still exists on disk, pruning any that have also been
        // moved out (each miss is one cheap stat — never a folder walk). Returns null when the
        // snapshot holds no surviving file. Shared by the hot-swap fast path and its post-rescan
        // retry so the pick-and-verify loop lives in one place.
        private string PickExistingReplacement(string contextName, string goneFile)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                string pick = PickNextFile(contextName, avoidFile: goneFile);
                if (pick == null) return null;                       // nothing left
                if (File.Exists(pick)) return pick;
                PruneMissingFromSnapshot(contextName, pick);         // also gone — drop and retry
            }
            return null;
        }

        // ── Hot-swap when the playing file is moved/deleted out from under us ───
        // Driven by FileWatchLoop's cheap File.Exists ping, NOT a folder walk, so it
        // stays O(1) in folder size. Trims the gone file from the in-memory snapshot,
        // picks a replacement that STILL exists on disk (skipping any that have also
        // been moved out — "check the next one exists too"), and plays it; falls back
        // when nothing playable remains.
        private IEnumerator HotSwapFromMissingPlayingFile(string contextName, string goneFile)
        {
            ContextCache cache = cacheMap[contextName];
            CMMConfig.D($"[CMM] Playing file gone (ping): {Path.GetFileName(goneFile)} in {contextName}");

            // Drop the gone file from snapshot + presence. Keep its _validatedFiles
            // entry (keyed by path|size|mtime) so moving it back hits the cache.
            PruneMissingFromSnapshot(contextName, goneFile);
            _invalidFiles.Remove(goneFile);

            // Forget the gone track; the native voice still playing it is stopped below
            // once we've decided whether a replacement exists.
            cache.lastPlayedFile = null;

            // Pick a replacement that still exists, pruning any that have also been
            // moved out. Each miss is one cheap stat — never a folder walk.
            string replacement = PickExistingReplacement(contextName, goneFile);

            // The snapshot yielded no surviving replacement — but it may be STALE. A swap
            // that removes the playing file AND adds a new one in the same poll window
            // leaves the new file undiscovered, because this O(1) fast path never walks the
            // folder. Before surrendering to the vanilla bed, do ONE fresh enumeration to
            // pick up such adds, so an add+remove swaps STRAIGHT to the new track instead of
            // flickering through vanilla for a poll cycle (the "vanilla plays while custom
            // files exist" symptom). Only reached once the snapshot is empty of survivors,
            // so the folder is small here and the walk is cheap; large folders always find a
            // replacement above and never get here. Off the main thread to be safe.
            if (replacement == null)
            {
                Contexts ictx = GetContext(contextName);
                if (ictx != null && !string.IsNullOrEmpty(ictx.Path))
                {
                    string dir = Path.Combine(MusicDirectory, ictx.Path);
                    string[] fresh = null;
                    bool scanDone = false;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { if (Directory.Exists(dir)) fresh = EnumerateContextFiles(ictx, dir); }
                        catch { }
                        finally { scanDone = true; }
                    });
                    while (!scanDone) yield return null;

                    if (fresh != null && fresh.Length > 0)
                    {
                        cache.directorySnapshot = fresh;
                        InvalidateContextFileCache(contextName);
                        CMMConfig.D($"[CMM] Hot-swap: fresh scan found {fresh.Length} file(s) in {contextName} after removal.");
                        replacement = PickExistingReplacement(contextName, goneFile);
                    }
                }
            }

            if (replacement != null)
            {
                _pendingContext = null;
                // Pin the verified replacement so StartNativeContext plays exactly it (a
                // fresh switch-in consumes stagedFile), then re-trigger on the native mixer.
                cache.stagedFile = replacement;
                CMMConfig.D($"[CMM] Hot-swap (ping) to {Path.GetFileName(replacement)} in {contextName}.");
                StopNativeIfActive(0.25f);
                currentContext = GetContext(contextName);
                TriggerMusic(contextName);
                yield break;
            }

            // Nothing playable left — re-evaluate / fall back (mirrors the slow path's
            // folder-emptied handling).
            CMMConfig.D($"[CMM] No playable file left in {contextName} after removal — re-evaluating.");
            _pendingContext = null;
            if (!inGame)
                SendContextUpdate(currentContext.Context);
            else
            {
                string fallback = GetTopContext() ?? "Ambient";
                SendContextUpdate(fallback, 0);
            }
        }

        // Per-context cooldown: the leaving context is recorded against its own name (so
        // unrelated higher-priority contexts are never blocked) and clears itself after its
        // Cooldown seconds. Fire-and-forget (one instance per arming, see ArmContextCooldown),
        // so it always reaches the Remove and never orphans a name in the set.
        private IEnumerator ContextCooldownTimer(string contextName, int seconds)
        {
            _contextsOnCooldown.Add(contextName);
            CMMConfig.D($"[CMM] Cooldown started for {contextName}: {seconds}s");
            yield return new WaitForSeconds(seconds);
            _contextsOnCooldown.Remove(contextName);
            CMMConfig.D($"[CMM] Cooldown expired for {contextName}.");
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
                        $" | nativeCtx={_nativeActiveContext ?? "null"}" +
                        $" | nativeBusy={NativeOutputBusy()}" +
                        $" | stack=[{stack}]");

                // ── Output-audibility self-check ──────────────────────────────
                // The native mixer drives the OS audio device directly (miniaudio), so the
                // managed PcmHeartbeat stays flat — audibility is read from the mixer's own
                // voice state plus the listener (still the final volume/pause gate in-game).
                var listener = FindObjectOfType<AudioListener>();
                CMMConfig.D($"[CMM AUDIBLE] nativeBusy={NativeOutputBusy()}" +
                        $" | masterVol={CmmMixer.MasterVolume:F2}" +
                        $" | listenerVol={AudioListener.volume:F2}" +
                        $" | listenerPaused={AudioListener.pause}" +
                        $" | listener={(listener != null ? listener.name : "NONE")}");
            }
        }
    }
}
