
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

using static CMM.SourceScan;

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
                if (_trackEndCoroutine       != null) n++;
                if (_crossFadeCoroutine      != null) n++;
                if (_vanillaFadeInCoroutine  != null) n++;
                if (_menuVanillaFadeCoroutine != null) n++;
                return n;
            }
        }

        public static TrackOrderMode GlobalTrackOrder = TrackOrderMode.RandomNoRepeat;
        public static float ContextScanCooldown = 0.1f;
        public static float StalenessCheckInterval = 5f;
        public static float TrackEndPollInterval = 0.25f;
        public static float CrossFadeDuration = 2f;
        public static float TrackTransitionFadeDuration = 6f;

        // Graceful, deliberate fade for the menu↔game boundary (entering a world, exiting
        // back to the menu) — intentionally longer than the in-game CrossFadeDuration.
        public const float MenuTransitionFade = 3f;
        // Graceful fade when the game pauses (entering the ESC menu / inventory in single-player):
        // the music fades out over this window rather than cutting, and fades back in over it on
        // resume. The quit-to-menu teardown reuses the same fade — quitting mid-fade just continues
        // it to silence (see OnGamePauseChanged and FadeOutAndCleanupCoroutine).
        public const float PauseFadeDuration = 3f;
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
            PersonalRadio.Load(modPath);   // persisted dial state (Cache/radio.xml)
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
                // NativeTrackLoop coroutine (still used when the flag is off). On the CRITICAL
                // lane (its own thread): the housekeeping ticks below block on the main thread
                // via Send, and during a freeze that would park a shared thread — and the
                // advance with it — exactly when advance-through-freeze is supposed to work.
                CmmScheduler.Every(TrackEndPollInterval, NativeAdvanceTick, "native-advance",
                    lane: CmmScheduler.Lane.Critical);
                // Config/settings/contexts/script hot-reload watch, off the frame loop: the
                // stamp probe runs on the scheduler thread and only marshals an actual change
                // back to the main thread (see ConfigWatchTick). Replaces ConfigWatchLoop.
                ConfigWatchInit();
                CmmScheduler.Every(2.0, ConfigWatchTick, "config-watch");
                // The acoustic scene is a ~310 ms voxel walk. It belongs here and never on the
                // frame loop: a door swinging must not be able to stutter the game. The decision
                // tick only records where the listener is; this notices and rebuilds.
                CmmScheduler.Every(0.5, AcousticBridge.SchedulerTick, "acoustics-scene");
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

            CMMConfig.D("[CMM] Initialized! Decoder: CMM_Decoder (native)");
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
            bool priorityOk = inGame && incoming.Priority > comparePriority;

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
        // Scratch map rebuilt every scan tick (cleared, never reallocated) — this loop
        // runs forever at ContextScanCooldown, so per-tick allocations here are the
        // mod's dominant steady-state GC churn (see _scanAllocBytes).
        private readonly Dictionary<string, string> _currentlyPlayable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private IEnumerator ContextScanLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(ContextScanCooldown);
                if (!inGame || playerIsDead) continue;
                if (_functionallyDormant) continue;   // A/B: dormant arm skips resolve+gate (the dominant allocator)

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
                _currentlyPlayable.Clear();
                foreach (string c in activeContexts)
                {
                    string p = ResolvePlayableContext(c);
                    if (p != null) _currentlyPlayable[c] = p;
                }

                // Plain loops instead of LINQ Any(): these gates run every tick and the
                // lambdas were allocating a closure + enumerator each time.
                bool namesChanged = activeContexts.Count != _previousActiveContexts.Count;
                if (!namesChanged)
                {
                    for (int i = 0; i < activeContexts.Count; i++)
                    {
                        if (!_previousActiveContexts.Contains(activeContexts[i])) { namesChanged = true; break; }
                    }
                }
                bool playabilityChanged = _currentlyPlayable.Count != _previousPlayableContexts.Count;
                if (!playabilityChanged)
                {
                    foreach (var kv in _currentlyPlayable)
                    {
                        if (!_previousPlayableContexts.TryGetValue(kv.Key, out string prev)
                            || !string.Equals(prev, kv.Value, StringComparison.OrdinalIgnoreCase))
                        { playabilityChanged = true; break; }
                    }
                }

                // Also re-evaluate if currentContext drifted to Silence/null from an external
                // event (PlayOnce ended, load failed, folder emptied) without the active set changing.
                bool currentIsStuck = currentContext == null || currentContext.Context == "Silence";
                // Convergence safety net. currentContext names a real audio context but
                // nothing is audible and NO transition machinery is in flight — a prior
                // transition was interrupted or its load failed and stranded us in silence
                // (a burst of interrupting contexts is the usual cause). The name-difference
                // check below would miss this whenever the stranded context still equals the
                // top context, so detect it here and force a re-assert. Every transition
                // signal must be idle: if any is live, a normal transition/track-flip is
                // already recovering and re-triggering would race it.
                bool currentAudioDead = currentContext != null
                    && currentContext.Context != "Silence"
                    && currentContext.Context != "Vanilla"
                    && !vanillaIsActive
                    // The personal radio owns the music layer: the context being silent is
                    // INTENTIONAL, not a strand. Without this the convergence branch re-asserts
                    // the context every scan tick, the radio gate swallows it, and the loop
                    // storms TriggerMusic ~10×/s for as long as the radio plays (the
                    // Ambient_Desert→Ambient_Desert TX-INTERRUPT storm, log 16-23-19).
                    && !PersonalRadio.SuppressesMusic
                    && _crossFadeCoroutine == null
                    && _trackEndCoroutine == null
                    && !_advancePending          // an off-frame-loop advance is in flight (NativeScheduler)
                    // The native mixer is the output; it's "busy" while a voice is live (or a
                    // start was just dispatched). A PlayOnce stinger / looping track starts no
                    // NativeTrackLoop, so this is the only signal that it's still playing.
                    && !NativeOutputBusy();
                // Also keep running if any stack entry is absent from activeContexts but hasn't
                // hit the 1.5s eviction threshold yet — without this the early-continue fires
                // on the second scan and the RemoveAll eviction check never runs again.
                // Also: keep scanning while any context is still warming up — its WarmUp dwell
                // can elapse without the active set changing, and GetTopContext must re-run to
                // promote it once it becomes viable. One pass over the stack covers both.
                bool hasPendingEvictions = false;
                bool hasWarmingContexts = false;
                for (int i = 0; i < _contextStack.Count; i++)
                {
                    ActiveContext ac = _contextStack[i];
                    if (!hasPendingEvictions && !ac.Manual && !activeContexts.Contains(ac.ContextName))
                        hasPendingEvictions = true;
                    if (!hasWarmingContexts && !DwellSatisfied(ac))
                        hasWarmingContexts = true;
                    if (hasPendingEvictions && hasWarmingContexts) break;
                }
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
                foreach (var kv in _currentlyPlayable) _previousPlayableContexts[kv.Key] = kv.Value;

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
        // Last computed per-tick scan allocation (bytes), surfaced on the debug HUD's GC line.
        private static float _lastScanBytesPerTick;

        // A/B diagnostic kill-switch (toggled by Numpad 7 while the debug HUD is up). When
        // set, CMM's steady-state managed loops become no-ops — the context scan/resolve/gate,
        // presence rescan, loudness sweep, volume sync, and the config/file/advance watch
        // ticks all early-out — so the mod sits "functionally dormant" while still loaded.
        // This gives the whole-heap GC gauge a clean second arm (enabled vs dormant) in the
        // SAME session, with the HUD's own per-frame allocation common to both arms. Native
        // playback already running is left alone (it's GC-immune); nothing is torn down, so
        // clearing the flag resumes normal work on the next tick. Read from the CmmScheduler
        // thread as well as the main thread, hence volatile.
        internal static volatile bool _functionallyDormant;

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
                    _lastScanBytesPerTick = (float)_scanAllocBytes / _scanAllocTicks;
                    if (CMMConfig.DebugLogging) // interpolation below allocates; the HUD gauge above is kept fed either way
                    {
                        float scanKB = _scanAllocBytes / 1024f;
                        CMMConfig.D($"[CMM PERF] Scan churn: {scanKB:F1} KB / {_scanAllocTicks} ticks " +
                                    $"= {_lastScanBytesPerTick:F0} B/tick, ~{scanKB / StalenessCheckInterval:F1} KB/s " +
                                    $"(process heap {GC.GetTotalMemory(false) / 1048576f:F1} MB)");
                    }
                    _scanAllocBytes = 0;
                    _scanAllocTicks = 0;
                }

                // Per-phase timing rollup (poll/resolve/gate/transition), when PerfTiming is on.
                CMMPerf.DumpAndReset();

                if (!inGame) continue;

                // Build state string; only emit log lines if it changed. The native mixer
                // holds no managed clip cache, so report each context's current pick. The
                // string work only exists to be logged, so it's skipped when logging is off.
                if (CMMConfig.DebugLogging)
                {
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
                if (ictx != null && !string.IsNullOrEmpty(ictx.Source))
                {
                    bool isPlaylist = ictx.IsPlaylist;
                    string dir = isPlaylist ? null : Path.Combine(MusicDirectory, ictx.Source);
                    string playlistFile = isPlaylist ? PlaylistSource.ResolveFullPath(ictx.Source) : null;
                    string[] fresh = null;
                    string[] freshPlaylistFiles = _noPlaylistFiles;
                    bool scanDone = false;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            if (isPlaylist) fresh = PlaylistSource.ReadTracks(playlistFile);
                            else if (Directory.Exists(dir)) fresh = EnumerateContextFiles(ictx, dir, out freshPlaylistFiles);
                        }
                        catch { }
                        finally { scanDone = true; }
                    });
                    while (!scanDone) yield return null;

                    if (fresh != null && fresh.Length > 0)
                    {
                        cache.directorySnapshot = fresh;
                        cache.playlistFiles = freshPlaylistFiles;
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

        // Cached scene AudioListener for the diagnostic line below. FindObjectOfType walks
        // the whole scene, so it's only re-run when the cached one is gone (Unity's
        // destroyed-object == null covers world unload/reload).
        private AudioListener _diagListener;

        private IEnumerator DiagnosticLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(3f);
                if (!inGame && !vanillaIsActive) continue;
                // Everything below exists solely to produce debug log lines — skip the
                // string building (and the scene walk) entirely when logging is off.
                if (!CMMConfig.DebugLogging) continue;

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
                if (_diagListener == null) _diagListener = FindObjectOfType<AudioListener>();
                AudioListener listener = _diagListener;
                // All four playback sliders, not just music: CMM's output bypasses Unity, so it
                // applies Overall itself (music AND emitters) and Ambient (emitters only) — when a
                // level looks wrong, this line says which slider is responsible. listenerVol is the
                // game's Overall applied Unity-side, i.e. what VANILLA audio is riding; ours is the
                // `overall` figure. The two should track each other.
                CMMConfig.D($"[CMM AUDIBLE] nativeBusy={NativeOutputBusy()}" +
                        $" | masterVol={CmmMixer.MasterVolume:F2}" +
                        $" | overall={CMMConfig.OverallVolume:F2}" +
                        $" | ambient={CMMConfig.AmbientVolume:F2}" +
                        $" | music={gameMusicVol:F2}" +
                        $" | menuMusic={GamePrefs.GetFloat(EnumGamePrefs.OptionsMenuMusicVolumeLevel):F2}" +
                        $" | emitterTrim={CMMConfig.WorldEmitterVolume:F2}" +
                        $" | listenerVol={AudioListener.volume:F2}" +
                        $" | listenerPaused={AudioListener.pause}" +
                        $" | listener={(listener != null ? listener.name : "NONE")}");
            }
        }
    }
}
