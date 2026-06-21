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

        private static int _transitionId = 0;
        public static string _pendingContext = null;
        public static string _playOnceReturnContext = null;

        // When true, Conductor.Update() runs freely and BackgroundMusicMono is unblocked.
        public static bool vanillaIsActive = false;
        // Tracks what the game's Conductor wants to play even while it's blocked.
        public static string LastKnownGameSection = "None";

        public void InitContexts()
        {
            // caninterrupt is always true — hardcoded below. Defaults:
            // fallback="Stack", resume=true, stale=120, loop=false, cool=5, warm=5, bypass=false, once=false
            void Add(string name, string path, string fallback = "Stack",
                     bool resume = true, int stale = 120, bool loop = false,
                     int cool = 5, int warm = 5, bool bypass = false,
                     int priority = 0, bool once = false, float volume = -1f)
                => contexts[name] = new Contexts(name, path, fallback, resume, stale,
                                                 loop, cool, warm, bypass, true, priority, once, volume);

            Add("Death",               "events/death",          resume:false, stale:0,  cool:3, warm:1, bypass:true, priority:100, once:true);
            Add("Low_Health_Panic",    "events/low_health",     stale:30,     cool:5,   warm:2, bypass:true, priority:90);
            Add("Blood_Moon_Combat",   "combat_blood_moon",     stale:20,     cool:3,   warm:3, bypass:true, priority:71);
            Add("Blood_Moon",          "blood_moon",            stale:30,                        bypass:true, priority:70);
            Add("Vehicle_Gyrocopter",  "vehicle/gyrocopter",    "Vehicle",    stale:30, cool:5, warm:3, bypass:true, priority:61);
            Add("Vehicle_Bicycle",     "vehicle/bicycle",       "Vehicle",    stale:30, cool:5, warm:3, bypass:true, priority:61);
            Add("Vehicle_Minibike",    "vehicle/minibike",      "Vehicle",    stale:30, cool:5, warm:3, bypass:true, priority:61);
            Add("Vehicle_Motorcycle",  "vehicle/motorcycle",    "Vehicle",    stale:30, cool:5, warm:3, bypass:true, priority:61);
            Add("Vehicle_4x4",         "vehicle/4x4",           "Vehicle",    stale:30, cool:5, warm:3, bypass:true, priority:61);
            Add("Vehicle",             "vehicle",               stale:30,     cool:5,   warm:3, bypass:true, priority:60);
            Add("POI_Cleared",         "events/poi_cleared",    stale:60,                        bypass:true, priority:55, once:true, volume:1.0f);
            Add("Combat_High",         "combat_high",           "Combat_Low", stale:20, cool:3, warm:3, bypass:true, priority:51);
            Add("Combat_Low",          "combat_low",            "VanillaIfPanicked", stale:20, cool:3, warm:3, bypass:true, priority:50);
            Add("Spawn",               "events/spawn",          resume:false, stale:0,  cool:3, warm:1, bypass:true, priority:45, once:true);
            Add("Inventory",           "inventory",             stale:15,     cool:0,   warm:2, bypass:true, priority:40);
            Add("Tension_High",        "tension_high",          "Tension_Low", stale:30,                       priority:30);
            Add("Tension_Low",         "tension_low",           stale:30,                                      priority:30);
            Add("Trader_Joel",         "trader/joel",           "Trader",                                      priority:26);
            Add("Trader_Jen",          "trader/jen",            "Trader",                                      priority:26);
            Add("Trader_Rekt",         "trader/rekt",           "Trader",                                      priority:26);
            Add("Trader_Bob",          "trader/bob",            "Trader",                                      priority:26);
            Add("Trader_Hugh",         "trader/hugh",           "Trader",                                      priority:26);
            Add("Trader",              "trader",                "VanillaIfDMS",                                priority:25);
            Add("Home_Night",          "home/night",            "Home",                                        priority:15);
            Add("Home",                "home",                                                                 priority:15);
            Add("POI_Infested_T5",     "poi/infested/t5",       "POI_Infested", stale:60,                      priority:22);
            Add("POI_Infested_T4",     "poi/infested/t4",       "POI_Infested", stale:60,                      priority:22);
            Add("POI_Infested_T3",     "poi/infested/t3",       "POI_Infested", stale:60,                      priority:22);
            Add("POI_Infested_T2",     "poi/infested/t2",       "POI_Infested", stale:60,                      priority:22);
            Add("POI_Infested_T1",     "poi/infested/t1",       "POI_Infested", stale:60,                      priority:22);
            Add("POI_Infested_T0",     "poi/infested/t0",       stale:60,                                      priority:21);
            Add("POI_Infested",        "poi/infested",          stale:60,                                      priority:20);
            Add("POI_T5",              "poi/t5",                "POI",        stale:60,                        priority:18);
            Add("POI_T4",              "poi/t4",                "POI",        stale:60,                        priority:18);
            Add("POI_T3",              "poi/t3",                "POI",        stale:60,                        priority:18);
            Add("POI_T2",              "poi/t2",                "POI",        stale:60,                        priority:18);
            Add("POI_T1",              "poi/t1",                "POI",        stale:60,                        priority:18);
            Add("POI_T0",              "poi/t0",                stale:60,                                      priority:17);
            Add("POI",                 "poi",                   stale:60,                                      priority:16);
            Add("Ambient_Forest_Night",      "ambient/biome/forest/night",       "Ambient_Forest",            priority:13);
            Add("Ambient_Forest",            "ambient/biome/forest",             "Ambient",                   priority:12);
            Add("Ambient_Burnt_Forest_Night","ambient/biome/burnt_forest/night", "Ambient_Burnt_Forest",      priority:13);
            Add("Ambient_Burnt_Forest",      "ambient/biome/burnt_forest",       "Ambient",                   priority:12);
            Add("Ambient_Desert_Night",      "ambient/biome/desert/night",       "Ambient_Desert",            priority:13);
            Add("Ambient_Desert",            "ambient/biome/desert",             "Ambient",                   priority:12);
            Add("Ambient_Snow_Night",        "ambient/biome/snow/night",         "Ambient_Snow",              priority:13);
            Add("Ambient_Snow",              "ambient/biome/snow",               "Ambient",                   priority:12);
            Add("Ambient_Wasteland_Night",   "ambient/biome/wasteland/night",    "Ambient_Wasteland",         priority:13);
            Add("Ambient_Wasteland",         "ambient/biome/wasteland",          "Ambient",                   priority:12);
            Add("Ambient_Night",             "ambient/night",              "Ambient",                         priority:11);
            Add("Ambient",                   "ambient",                    "VanillaIfDMS", warm:4,            priority:10);
            // Sentinels — no path, no files
            Add("Silence",  "", "Silence",  resume:false, stale:0, cool:0, warm:0, bypass:true, priority:0);
            Add("Vanilla",  "", "Vanilla",  resume:false, stale:0, cool:0, warm:0, bypass:true, priority:-1);
            Add("Menu",     "menu", "Vanilla", resume:false, stale:0, loop:true, cool:5, warm:2, bypass:true, priority:0);
        }

        public static Contexts GetContext(string contextIn) =>
            contexts.TryGetValue(contextIn, out var ctx) ? ctx : null;

        private static readonly HashSet<string> _invalidFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        // Called on game exit to release all AudioClips and WaveStreams so Unity
        // and NAudio can reclaim the memory before the menu screen loads.
        public static void CleanupAllCaches()
        {
            // Stop any in-flight music coroutines first so none of them re-triggers
            // a read (or a fresh play) on a reader we're about to dispose.
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }
            if (_nextCacheCoroutine  != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine);  _nextCacheCoroutine  = null; }
            if (_trackEndCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine);   _trackEndCoroutine   = null; }
            if (_crossFadeCoroutine  != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);  _crossFadeCoroutine  = null; }

            // Stop and detach both channels BEFORE disposing readers. Unity's audio
            // thread keeps invoking a streaming clip's PCMReaderCallback until the
            // AudioSource is stopped; disposing the backing WaveStream while a clip
            // is still playing throws an NRE on every buffer fill (the console spam
            // seen on a return to the menu).
            foreach (var src in new[] { CMM_Primary_Channel, CMM_Secondary_Channel })
            {
                if (src == null) continue;
                src.Stop();
                src.clip = null;
                src.volume = 0f;
            }

            // Stage every cached clip/reader for deferred disposal (one frame later)
            // so the audio thread is fully off the clips before their readers are
            // disposed — same safety guarantee the normal transition path uses.
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
                _contextStack.Add(new ActiveContext
                {
                    ContextName = contextName,
                    Priority = ctx.Priority,
                    ActivatedAt = Time.realtimeSinceStartup,
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
