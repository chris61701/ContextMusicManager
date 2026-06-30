using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MusicUtils.Enums;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // CONTEXT STACK
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager : MonoBehaviour
    {
        public static Dictionary<string, Contexts> contexts = new Dictionary<string, Contexts>();
        public static Dictionary<string, ContextCache> cacheMap = new Dictionary<string, ContextCache>();
        public static List<ActiveContext> _contextStack = new List<ActiveContext>();
        public static Contexts currentContext;

        // Cancellation token for in-flight transitions. Bumped on EVERY context change
        // (TriggerMusic, skip, auto-advance, fade-to-silence). Volatile because the native
        // start worker thread reads it to drop superseded requests (see StartNativeContext).
        private static volatile int _transitionId = 0;
        public static string _pendingContext = null;

        // When true, Conductor.Update() runs freely and BackgroundMusicMono is unblocked.
        public static bool vanillaIsActive = false;
        // Tracks what the game's Conductor wants to play even while it's blocked.
        public static string LastKnownGameSection = "None";

        public void InitContexts()
        {
            // caninterrupt is always true — hardcoded below. Defaults:
            // fallback="Stack", resume=true, stale=120, loop=false, cool=5, fadeintime=5, bypass=false,
            // once=false, warmup=0.  fadeintime = fade-in seconds; warmup = dwell seconds before viable.
            void Add(string name, string path, string fallback = "Stack",
                     bool resume = true, int stale = 120, bool loop = false,
                     int cool = 5, int fadeintime = 5, bool bypass = false,
                     int priority = 0, bool once = false, float volume = -1f,
                     int warmup = 0, string warmgroup = null, bool subfolders = false)
                => contexts[name] = new Contexts(name, path, fallback, resume, stale,
                                                 loop, cool, fadeintime, bypass, true, priority, once, volume,
                                                 warmup, warmgroup, subfolders);

            Add("Death",               "events/death",          resume:false, stale:0,  cool:3, fadeintime:1, bypass:true, priority:100, once:true);
            Add("Low_Health_Panic",    "events/low_health",     stale:30,     cool:5,   fadeintime:2, bypass:true, priority:90);
            Add("Blood_Moon_Combat",   "combat/blood_moon",     "Combat_Boss", stale:20, cool:3, fadeintime:3, bypass:true, priority:71);
            Add("Vehicle_Gyrocopter",  "vehicle/gyrocopter",    "Vehicle",    stale:30, cool:5, fadeintime:3, bypass:true, priority:70, warmup:1);
            Add("Vehicle_Bicycle",     "vehicle/bicycle",       "Vehicle",    stale:30, cool:5, fadeintime:3, bypass:true, priority:70, warmup:1);
            Add("Vehicle_Minibike",    "vehicle/minibike",      "Vehicle",    stale:30, cool:5, fadeintime:3, bypass:true, priority:70, warmup:1);
            Add("Vehicle_Motorcycle",  "vehicle/motorcycle",    "Vehicle",    stale:30, cool:5, fadeintime:3, bypass:true, priority:70, warmup:1);
            Add("Vehicle_4x4",         "vehicle/4x4",           "Vehicle",    stale:30, cool:5, fadeintime:3, bypass:true, priority:70, warmup:1);
            Add("Vehicle",             "vehicle",               stale:30,     cool:5,   fadeintime:3, bypass:true, priority:69, warmup:1);
            Add("POI_Cleared",         "events/poi_cleared",    stale:60,                        bypass:true, priority:91, once:true, volume:1.0f);
            // Three combat tiers. The combat/ root folder IS the base (Low) tier; _high and
            // _boss subfolders hold the escalations (underscore-prefixed so the top-level
            // directory scan never confuses them with the per-biome subfolders). Low bridges
            // to vanilla via VanillaIfPanicked. Mirrors contexts.xml.
            Add("Combat_Boss",         "combat/_boss",          "Combat_High",       stale:20, cool:3, fadeintime:3, bypass:true, priority:52);
            Add("Combat_High",         "combat/_high",          "Combat_Low",        stale:20, cool:3, fadeintime:3, bypass:true, priority:51);
            Add("Combat_Low",          "combat",                "VanillaIfPanicked", stale:20, cool:3, fadeintime:3, bypass:true, priority:50);
            // Biome-specific combat tiers (mirror contexts.xml). Each biome's combat/<biome>
            // folder mirrors the generic layout (root = Low, _high / _boss = escalations).
            // Each variant outranks its generic counterpart so biome combat music wins when
            // its folder has tracks, else falls back to the matching generic tier. The resolver
            // only stacks the variant for the one active tier, so the 60–62 band never inverts.
            Add("Combat_Boss_Burnt_Forest", "combat/burnt_forest/_boss", "Combat_Boss", stale:20, cool:3, fadeintime:3, bypass:true, priority:62);
            Add("Combat_High_Burnt_Forest", "combat/burnt_forest/_high", "Combat_High", stale:20, cool:3, fadeintime:3, bypass:true, priority:61);
            Add("Combat_Low_Burnt_Forest",  "combat/burnt_forest",       "Combat_Low",  stale:20, cool:3, fadeintime:3, bypass:true, priority:60);
            Add("Combat_Boss_Desert",       "combat/desert/_boss",       "Combat_Boss", stale:20, cool:3, fadeintime:3, bypass:true, priority:62);
            Add("Combat_High_Desert",       "combat/desert/_high",       "Combat_High", stale:20, cool:3, fadeintime:3, bypass:true, priority:61);
            Add("Combat_Low_Desert",        "combat/desert",             "Combat_Low",  stale:20, cool:3, fadeintime:3, bypass:true, priority:60);
            Add("Combat_Boss_Forest",       "combat/forest/_boss",       "Combat_Boss", stale:20, cool:3, fadeintime:3, bypass:true, priority:62);
            Add("Combat_High_Forest",       "combat/forest/_high",       "Combat_High", stale:20, cool:3, fadeintime:3, bypass:true, priority:61);
            Add("Combat_Low_Forest",        "combat/forest",             "Combat_Low",  stale:20, cool:3, fadeintime:3, bypass:true, priority:60);
            Add("Combat_Boss_Snow",         "combat/snow/_boss",         "Combat_Boss", stale:20, cool:3, fadeintime:3, bypass:true, priority:62);
            Add("Combat_High_Snow",         "combat/snow/_high",         "Combat_High", stale:20, cool:3, fadeintime:3, bypass:true, priority:61);
            Add("Combat_Low_Snow",          "combat/snow",               "Combat_Low",  stale:20, cool:3, fadeintime:3, bypass:true, priority:60);
            Add("Combat_Boss_Wasteland",    "combat/wasteland/_boss",    "Combat_Boss", stale:20, cool:3, fadeintime:3, bypass:true, priority:62);
            Add("Combat_High_Wasteland",    "combat/wasteland/_high",    "Combat_High", stale:20, cool:3, fadeintime:3, bypass:true, priority:61);
            Add("Combat_Low_Wasteland",     "combat/wasteland",          "Combat_Low",  stale:20, cool:3, fadeintime:3, bypass:true, priority:60);
            Add("Spawn",               "events/spawn",          resume:false, stale:0,  cool:3, fadeintime:1, bypass:true, priority:45, once:true);
            Add("Tension",             "tension",               stale:30,                                      priority:30, warmup:2);
            Add("Trader_Joel",         "trader/joel",           "Trader",                                      priority:26, warmup:3);
            Add("Trader_Jen",          "trader/jen",            "Trader",                                      priority:26, warmup:3);
            Add("Trader_Rekt",         "trader/rekt",           "Trader",                                      priority:26, warmup:3);
            Add("Trader_Bob",          "trader/bob",            "Trader",                                      priority:26, warmup:3);
            Add("Trader_Hugh",         "trader/hugh",           "Trader",                                      priority:26, warmup:3);
            Add("Trader",              "trader",                "VanillaIfDMS",                                priority:25, warmup:3);
            Add("Home_Night",          "home/night",            "Home",                                        priority:15, warmup:5, warmgroup:"home");
            Add("Home",                "home",                                                                 priority:15, warmup:5, warmgroup:"home");
            Add("Swimming",            "swimming",                                                            priority:23, warmup:5);
            Add("POI_Infested_T5",     "poi/infested/t5",       "POI_Infested", stale:60,                      priority:22, warmup:10, warmgroup:"poi");
            Add("POI_Infested_T4",     "poi/infested/t4",       "POI_Infested", stale:60,                      priority:22, warmup:10, warmgroup:"poi");
            Add("POI_Infested_T3",     "poi/infested/t3",       "POI_Infested", stale:60,                      priority:22, warmup:10, warmgroup:"poi");
            Add("POI_Infested_T2",     "poi/infested/t2",       "POI_Infested", stale:60,                      priority:22, warmup:10, warmgroup:"poi");
            Add("POI_Infested_T1",     "poi/infested/t1",       "POI_Infested", stale:60,                      priority:22, warmup:10, warmgroup:"poi");
            Add("POI_Infested_T0",     "poi/infested/t0",       stale:60,                                      priority:21, warmup:10, warmgroup:"poi");
            Add("POI_Infested",        "poi/infested",          stale:60,                                      priority:20, warmup:10, warmgroup:"poi");
            Add("POI_T5",              "poi/t5",                "POI",        stale:60,                        priority:18, warmup:5, warmgroup:"poi");
            Add("POI_T4",              "poi/t4",                "POI",        stale:60,                        priority:18, warmup:5, warmgroup:"poi");
            Add("POI_T3",              "poi/t3",                "POI",        stale:60,                        priority:18, warmup:5, warmgroup:"poi");
            Add("POI_T2",              "poi/t2",                "POI",        stale:60,                        priority:18, warmup:5, warmgroup:"poi");
            Add("POI_T1",              "poi/t1",                "POI",        stale:60,                        priority:18, warmup:5, warmgroup:"poi");
            Add("POI_T0",              "poi/t0",                stale:60,                                      priority:17, warmup:5, warmgroup:"poi");
            Add("POI",                 "poi",                   stale:60,                                      priority:16, warmup:5, warmgroup:"poi");
            Add("Ambient_Forest_Night",      "ambient/forest/_night",       "Ambient_Night",             priority:12);
            Add("Ambient_Forest",            "ambient/forest",              "Ambient",                   priority:11);
            Add("Ambient_Burnt_Forest_Night","ambient/burnt_forest/_night", "Ambient_Night",             priority:12);
            Add("Ambient_Burnt_Forest",      "ambient/burnt_forest",        "Ambient",                   priority:11);
            Add("Ambient_Desert_Night",      "ambient/desert/_night",       "Ambient_Night",             priority:12);
            Add("Ambient_Desert",            "ambient/desert",              "Ambient",                   priority:11);
            Add("Ambient_Snow_Night",        "ambient/snow/_night",         "Ambient_Night",             priority:12);
            Add("Ambient_Snow",              "ambient/snow",                "Ambient",                   priority:11);
            Add("Ambient_Wasteland_Night",   "ambient/wasteland/_night",    "Ambient_Night",             priority:12);
            Add("Ambient_Wasteland",         "ambient/wasteland",           "Ambient",                   priority:11);
            Add("Ambient_Night",             "ambient/_night",              "Stack",                     priority:11);
            Add("Ambient",                   "ambient",                    "VanillaIfDMS", fadeintime:4,            priority:10);
            // Sentinels — no path, no files
            Add("Silence",  "", "Silence",  resume:false, stale:0, cool:0, fadeintime:0, bypass:true, priority:0);
            Add("Vanilla",  "", "Vanilla",  resume:false, stale:0, cool:0, fadeintime:0, bypass:true, priority:-1);
            Add("Menu",     "menu", "Vanilla", resume:false, stale:0, cool:5, fadeintime:2, bypass:true, priority:0);
        }

        public static Contexts GetContext(string contextIn) =>
            contextIn != null && contexts.TryGetValue(contextIn, out var ctx) ? ctx : null;

        // Diagnostics for the WarmUp dwell: throttle the per-frame "still warming"
        // spam to ~one line/second per context, and remember which contexts are
        // warming so we can log a single "now viable" line when each one clears.
        private static readonly Dictionary<string, float> _warmLogThrottle =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _warmingNow =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // True once a context has been on the stack long enough to satisfy its WarmUp
        // dwell. WarmUp <= 0 (the default) is always satisfied immediately. A context
        // that left and re-entered gets a fresh FirstSeenAt, so the dwell restarts.
        private static bool DwellSatisfied(ActiveContext active)
        {
            Contexts ctx = GetContext(active.ContextName);
            int warm = ctx?.WarmUp ?? 0;
            if (warm <= 0) return true;

            float now = Time.realtimeSinceStartup;
            float remaining = warm - (now - active.FirstSeenAt);
            if (remaining > 0f)
            {
                // Held back by dwell — this is the usual reason a context that's
                // clearly "active" still isn't playing. Log it (throttled) so it's
                // visible instead of a silent skip.
                if (!_warmLogThrottle.TryGetValue(active.ContextName, out float last) || now - last >= 1f)
                {
                    _warmLogThrottle[active.ContextName] = now;
                    CMMConfig.D($"[CMM] {active.ContextName} warming up — {remaining:F1}s left before it can trigger (WarmUp={warm}s)");
                }
                _warmingNow.Add(active.ContextName);
                return false;
            }

            if (_warmingNow.Remove(active.ContextName))
            {
                _warmLogThrottle.Remove(active.ContextName);
                CMMConfig.D($"[CMM] {active.ContextName} warmup complete — now viable to trigger");
            }
            return true;
        }

        // FirstSeenAt for a context about to join the stack. Normally this is just
        // "now", which (re)starts its WarmUp dwell. But if the context belongs to a
        // WarmGroup and a sibling in that group is already on the stack, inherit the
        // group's OLDEST FirstSeenAt so the dwell carries across the handoff instead
        // of restarting — e.g. a day/night swap (Home -> Home_Night) stays viable
        // continuously rather than dropping out for the full WarmUp and letting a
        // lower context leak through. The outgoing sibling is still present here
        // because stack inserts run before the absence-based eviction each scan.
        internal static float InheritedFirstSeen(string contextName, float now)
        {
            string group = GetContext(contextName)?.WarmGroup;
            if (string.IsNullOrEmpty(group)) return now;
            float oldest = now;
            foreach (ActiveContext ac in _contextStack)
            {
                if (ac.ContextName == contextName) continue;
                if (string.Equals(GetContext(ac.ContextName)?.WarmGroup, group,
                                   StringComparison.OrdinalIgnoreCase))
                    oldest = Mathf.Min(oldest, ac.FirstSeenAt);
            }
            return oldest;
        }

        private static readonly HashSet<string> _invalidFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validation cache: keys are "fullpath|size|mtimeTicks" of files that passed
        // QuickValidate this session. Checked in FileWatchLoop before re-decoding a "new"
        // file, so moving a folder's files out and back (same size+mtime) doesn't
        // re-validate every one. Kept across removals on purpose; a changed file gets a
        // new key and is re-checked. In-memory only (a fresh launch re-validates, though
        // already-measured files short-circuit via LoudnessStore.IsMeasured).
        private static readonly HashSet<string> _validatedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Tracks all contexts currently open on the ResolvePlayableContext call stack.
        // Prevents Combat_High → Combat_Low → (stack walk) → Combat_High mutual recursion.
        private static readonly HashSet<string> _resolvingContexts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Walks the fallback chain to find the nearest playable context.
        // Returns "Vanilla" when the chain terminates at a Vanilla fallback.
        // Returns null when the chain terminates at Silence (explicit silence).
        private static string ResolvePlayableContext(string contextName)
        {
            // Re-entry guard: if this context is already open somewhere above us on the
            // call stack, returning null here breaks the cycle rather than overflowing.
            if (_resolvingContexts.Contains(contextName)) return null;
            _resolvingContexts.Add(contextName);
            try
            {
                return ResolvePlayableContextCore(contextName);
            }
            finally
            {
                _resolvingContexts.Remove(contextName);
            }
        }

        private static string ResolvePlayableContextCore(string contextName)
        {
            string current = contextName;
            for (int i = 0; i < 5; i++)
            {
                if (ContextHasFiles(current)) return current;
                Contexts ctx = GetContext(current);
                string fallback = ctx?.Fallback ?? "Silence";
                if (fallback == "Vanilla") return "Vanilla";
                if (fallback == "Stack")
                {
                    foreach (ActiveContext active in _contextStack)
                    {
                        if (!DwellSatisfied(active)) continue;   // still warming up — not yet viable
                        string resolved = ResolvePlayableContext(active.ContextName);
                        if (resolved != null) return resolved;
                    }
                    return null;
                }
                // Resolves to Vanilla only when the game's threat detection is Panicked;
                // otherwise falls through to Stack so no music change occurs.
                if (fallback == "VanillaIfPanicked")
                {
                    bool dmsEnabled = GamePrefs.GetBool(EnumGamePrefs.OptionsDynamicMusicEnabled);
                    var localPlayer = GameManager.Instance?.World?.GetPrimaryPlayer();
                    if (dmsEnabled && localPlayer?.ThreatLevel?.Category == ThreatLevelType.Panicked)
                        return "Vanilla";
                    foreach (ActiveContext active in _contextStack)
                    {
                        if (!DwellSatisfied(active)) continue;   // still warming up — not yet viable
                        string resolved = ResolvePlayableContext(active.ContextName);
                        if (resolved != null) return resolved;
                    }
                    return null;
                }
                // Resolves to Vanilla only when DMS is enabled; otherwise falls through
                // to the stack so ambient/home/etc. keeps playing instead of vanilla.
                if (fallback == "VanillaIfDMS")
                {
                    if (GamePrefs.GetBool(EnumGamePrefs.OptionsDynamicMusicEnabled))
                    {
                        // If the originating context (e.g. Trader_Rekt) maps to a vanilla
                        // SingleClip section we can play ourselves, resolve to that context
                        // — PlayFromCache plays the borrowed game clip on our own channel
                        // instead of handing off to the Conductor. contextName is the seed
                        // of this resolve, so a per-trader context resolves to its own clip.
                        if (TryGetReadyVanillaClip(contextName, out _))
                            return contextName;
                        return "Vanilla";
                    }
                    foreach (ActiveContext active in _contextStack)
                    {
                        if (!DwellSatisfied(active)) continue;   // still warming up — not yet viable
                        string resolved = ResolvePlayableContext(active.ContextName);
                        if (resolved != null) return resolved;
                    }
                    return null;
                }
                if (fallback == "Silence" || fallback == current) return null;
                current = fallback;
            }
            return null;
        }

        private static string GetTopContext()
        {
            foreach (ActiveContext active in _contextStack)
            {
                if (!DwellSatisfied(active)) continue;   // still warming up — not yet viable
                string playable = ResolvePlayableContext(active.ContextName);
                if (playable != null) return playable;
            }
            // Null signals "nothing playable and DMS is off" — caller should fade to
            // silence rather than activating vanilla and releasing the Conductor.
            return GamePrefs.GetBool(EnumGamePrefs.OptionsDynamicMusicEnabled) ? "Vanilla" : null;
        }

        // ── Vanilla handoff ───────────────────────────────────────────────────

        public static void ActivateVanilla()
        {
            _pendingContext = "Vanilla";
            vanillaIsActive = true;
            currentContext = GetContext("Vanilla");
            FadeToSilence();
            if (inGame)
            {
                SetVanillaMusicVolume(0f);
                if (_vanillaFadeInCoroutine != null) { CMM_CoRoutines.StopCoroutine(_vanillaFadeInCoroutine); _vanillaFadeInCoroutine = null; }
                _vanillaFadeInCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeInVanillaCoroutine(CrossFadeDuration));
            }
            else
            {
                // A fade-out to vanilla-muted may still be in flight (files were added
                // then removed quickly). Cancel it so it can't re-mute after we unmute.
                if (_menuVanillaFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_menuVanillaFadeCoroutine); _menuVanillaFadeCoroutine = null; }
                MenuMusicPatch.SetMenuMusicMuted(false);
            }
            CMMConfig.D($"[CMM] Vanilla activated ({(inGame ? "in-game" : "menu")})");
        }

        public static void DeactivateVanilla()
        {
            vanillaIsActive = false;
            _pendingContext = null;
            if (inGame)
            {
                if (_vanillaFadeInCoroutine != null) { CMM_CoRoutines.StopCoroutine(_vanillaFadeInCoroutine); _vanillaFadeInCoroutine = null; }
                CMM_CoRoutines.StartCoroutine(Instance.FadeOutVanillaCoroutine(CrossFadeDuration));
            }
            else
            {
                // Fade the vanilla menu music out over the crossfade window instead of
                // an instant mute, so it doesn't abruptly cut when CMM's menu music
                // (which fades in over the same window) takes over after files appear.
                if (_menuVanillaFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_menuVanillaFadeCoroutine); _menuVanillaFadeCoroutine = null; }
                _menuVanillaFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeOutMenuVanillaCoroutine(CrossFadeDuration));
            }
            CMMConfig.D("[CMM] Reclaiming from Vanilla.");
        }

        // Cached reflection handles for MixerController — resolved once on first use.
        private static MethodInfo _setDMVMethod;
        private static object _mixerInstance;
        private static volatile bool _mixerResolved;
        private static float _currentVanillaVolume = 0f;

        public static void SetVanillaMusicVolume(float vol)
        {
            _currentVanillaVolume = vol;
            if (!_mixerResolved)
            {
                _mixerResolved = true;
                var t = typeof(DynamicMusic.Conductor).Assembly.GetType("DynamicMusic.MixerController");
                _mixerInstance = t?.GetField("instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.GetValue(null);
                _setDMVMethod = t?.GetMethod("SetDynamicMusicVolume",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (_setDMVMethod == null)
                    Log.Warning("[CMM] MixerController.SetDynamicMusicVolume not found — Vanilla volume control unavailable.");
                else
                    CMMConfig.D($"[CMM] MixerController resolved — instance={_mixerInstance != null} method={_setDMVMethod.Name}");
            }
            if (_setDMVMethod != null && _mixerInstance != null)
                _setDMVMethod.Invoke(_mixerInstance, new object[] { vol });
        }

        // ── Cache cleanup ─────────────────────────────────────────────────────
        // Releases all AudioClips and WaveStreams so Unity and NAudio can reclaim
        // the memory before the menu screen loads. Immediate (hard-cut) form — the
        // game-exit path uses FadeOutAndCleanup() to fade first; this remains for the
        // no-runner fallback and any caller that genuinely wants an instant teardown.
        public static void CleanupAllCaches()
        {
            StopMusicCoroutines();
            StopAndDetachChannels();
            DisposeAllCaches();
        }

        // ── Game-exit graceful teardown ───────────────────────────────────────
        // Fades the playing channel(s) to silence over the crossfade window, THEN
        // releases the caches and brings up the menu music — so returning to the menu
        // fades out instead of hard-cutting (CMM_Audio_Object is DontDestroyOnLoad, so
        // the fade coroutine survives the world unload). Falls back to the immediate
        // teardown when no coroutine runner is available.
        public static void FadeOutAndCleanup()
        {
            // Stop the in-flight transition coroutines now (not inside the fade): the
            // fade below is itself assigned to _crossFadeCoroutine, both so a second
            // exit can cancel it and so VolumeSyncLoop stays suppressed while it runs.
            StopMusicCoroutines();

            if (Instance == null || CMM_CoRoutines == null)
            {
                StopAndDetachChannels();
                DisposeAllCaches();
                SendContextUpdate("Menu");
                return;
            }

            // Hold off the menu-screen auto-trigger (MenuMusicPatch) so the menu music can't
            // fade in until the game music has fully faded out (cleared at the end of the coroutine).
            _menuMusicHold = true;
            _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeOutAndCleanupCoroutine());
        }

        // Game-exit fade is intentionally longer than the in-game CrossFadeDuration —
        // a slower, more deliberate fade as the world tears down and the menu loads.
        private const float ExitFadeDuration = MenuTransitionFade;

        private System.Collections.IEnumerator FadeOutAndCleanupCoroutine()
        {
            AudioSource a = ActiveChannel;
            AudioSource b = InactiveChannel;
            float aStart = (a != null && a.isPlaying) ? a.volume : 0f;
            float bStart = (b != null && b.isPlaying) ? b.volume : 0f;

            // The game music usually plays through the NATIVE mixer, not the AudioSource channels.
            // Fade it CONSUMER-side (output gain in the audio callback) — the producer-side ramp
            // can't be relied on here because the world-unload starves the Pump mid-fade, cutting
            // the music short. The consumer (OS audio thread) keeps running through the freeze, so
            // the fade is heard over the full ExitFadeDuration. The voices are hard-stopped only
            // AFTER the fade completes (below), so the device isn't torn down mid-fade.
            bool nativeWasPlaying = NativeOutputBusy();
            CmmMixer.Instance?.SetOutputGain(0f, ExitFadeDuration);

            if (aStart > 0.0001f || bStart > 0.0001f || nativeWasPlaying)
            {
                float elapsed = 0f;
                while (elapsed < ExitFadeDuration)
                {
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / ExitFadeDuration);
                    if (a != null && a.isPlaying) a.volume = Mathf.Lerp(aStart, 0f, t);
                    if (b != null && b.isPlaying) b.volume = Mathf.Lerp(bStart, 0f, t);
                    yield return null;
                }
            }

            // Faded out — now hard-stop the (already silent) native voices and do the teardown the
            // hard-cut path does up front. The menu music below opens a FRESH device (output gain
            // back to full), so it fades in cleanly after the silence rather than over the game.
            StopNativeIfActive(0f);
            // StopNativeIfActive only ARMS a per-voice snap-to-0 (Stop) and waits on a Pump tick to
            // retire the voices. The menu Play below now wins that race (files buffer into native
            // memory, so its worker reaches Play almost immediately) and, finding the voices still
            // live at full per-voice gain — the consumer fade above never touched them — re-fades
            // them once it restores the output gain, bleeding the just-faded game track back in.
            // Discard the voices synchronously here so the menu track starts over true silence.
            CmmMixer.Instance?.StopAllNow();
            StopAndDetachChannels();
            DisposeAllCaches();
            _crossFadeCoroutine = null;

            // Game music has FULLY faded and caches are released — only NOW release the hold and
            // bring up the menu music, so it fades in after silence rather than over the game music.
            _menuMusicHold = false;
            SendContextUpdate("Menu");
        }

        // Stop any in-flight music coroutines so none re-triggers a read (or a fresh
        // play) on a reader we're about to dispose.
        private static void StopMusicCoroutines()
        {
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }
            if (_nextCacheCoroutine  != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine);  _nextCacheCoroutine  = null; }
            if (_trackEndCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine);   _trackEndCoroutine   = null; }
            if (_crossFadeCoroutine  != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);  _crossFadeCoroutine  = null; }
        }

        // Stop and detach both channels BEFORE disposing readers. Unity's audio thread
        // keeps invoking a streaming clip's PCMReaderCallback until the AudioSource is
        // stopped; disposing the backing WaveStream while a clip is still playing throws
        // an NRE on every buffer fill (the console spam seen on a return to the menu).
        private static void StopAndDetachChannels()
        {
            foreach (var src in new[] { CMM_Primary_Channel, CMM_Secondary_Channel })
            {
                if (src == null) continue;
                src.Stop();
                src.clip = null;
                src.volume = 0f;
            }
        }

        // Stage every cached clip/reader for deferred disposal (one frame later) so the
        // audio thread is fully off the clips before their readers are disposed — the
        // same safety guarantee the normal transition path uses — and reset per-context
        // state.
        private static void DisposeAllCaches()
        {
            foreach (var cache in cacheMap.Values)
            {
                StagePendingDisposal(cache.Current);
                StagePendingDisposal(cache.Next);
                cache.Current = default;
                cache.Next = default;
                cache.directorySnapshot = null;
                cache.lastPlayedFile = null;
                cache.resumePosition = 0f;
                cache.lastLeftTime = -1f;
            }
            if (Instance != null && CMM_CoRoutines != null)
                CMM_CoRoutines.StartCoroutine(Instance.DeferredDestroyPending());

            _invalidFiles.Clear();
            _validatedFiles.Clear();
            _contextsWithFilesDirty = true;

            // Drop borrowed vanilla-clip references — the DMS rebuilds its content on
            // world reload, so a cached AudioClip from this world must not survive.
            ClearVanillaClipState();
        }

        // ── Manual push/pop ───────────────────────────────────────────────────

        public static void PushContext(string contextName)
        {
            if (_contextStack.All(c => c.ContextName != contextName))
            {
                Contexts ctx = GetContext(contextName);
                if (ctx == null) return;
                if (ResolvePlayableContext(contextName) == null) return;
                float nowPush = Time.realtimeSinceStartup;
                _contextStack.Add(new ActiveContext
                {
                    ContextName = contextName,
                    Priority = ctx.Priority,
                    ActivatedAt = nowPush,
                    FirstSeenAt = InheritedFirstSeen(contextName, nowPush),
                    Manual = true
                });
                _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
            SendContextUpdate(GetTopContext(), 0);
        }

        public static void PopContext(string contextName)
        {
            _contextStack.RemoveAll(c => c.ContextName == contextName);
            _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            SendContextUpdate(GetTopContext(), 0);
        }
    }
}
