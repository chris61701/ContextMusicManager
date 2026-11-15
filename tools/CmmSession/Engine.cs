using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // Engine — standalone port of the CMM selection layer for the session bench.
    //
    // The real selection lives in the Unity-coupled ContextMusicManager partial
    // (MonoBehaviour + coroutines + GamePrefs + GameManager), so it can't be
    // linked the way the decoder is. Instead this class:
    //   • is named ContextMusicManager (CMM namespace) so the LINKED ScriptHost.cs
    //     can call ContextMusicManager.ContextHasFiles unchanged, and
    //   • COPIES VERBATIM the pure selection helpers + InitContexts + the per-tick
    //     ContextScanLoop body from the shipped source (cited per-block below),
    //     replacing the three Unity touch-points with sim stand-ins:
    //         Time.realtimeSinceStartup                     → SimClock.Now
    //         GamePrefs.GetBool(OptionsDynamicMusicEnabled) → DmsEnabled
    //         localPlayer.ThreatLevel == Panicked           → _snapshot.IsPanicked
    //         TryGetReadyVanillaClip(...)                   → false (no DMS borrow)
    //
    // Keep in sync if the originals change. The folder-enumeration / claiming
    // helpers are the same ones tools/CmmScanTest copies from
    // ContextMusicManager.AudioPlayer.cs (~1604-1717).
    // ─────────────────────────────────────────────────────────────────────────
    public static class ContextMusicManager
    {
        // DMS toggle stand-in for GamePrefs.GetBool(OptionsDynamicMusicEnabled).
        public static bool DmsEnabled = true;

        // Mirrors the shipped ContextMusicManager.CrossFadeDuration default. A transition
        // arriving sooner than the previous one's fade window interrupts an in-flight
        // crossfade (StopCoroutine(_crossFadeCoroutine)) — the "jarring" case.
        public const float CrossFadeDuration = 2f;

        public static Dictionary<string, Contexts> contexts =
            new Dictionary<string, Contexts>(StringComparer.OrdinalIgnoreCase);
        public static List<ActiveContext> _contextStack = new List<ActiveContext>();
        public static Contexts currentContext;
        public static bool vanillaIsActive = false;

        // Snapshot of the tick currently being resolved — the VanillaIfPanicked branch
        // reads it instead of the live game's ThreatLevel.
        private static GameState _snapshot;

        // ── Context table (copied from ContextMusicManager.ContextStack.cs InitContexts) ──
        public static void InitContexts()
        {
            void Add(string name, string path, string fallback = "Stack",
                     bool resume = true, int stale = 120, bool loop = false,
                     int cool = 5, float fadeintime = 5f, bool bypass = false,
                     int priority = 0, bool once = false, float volume = -1f,
                     int warmup = 0, string warmgroup = null, bool subfolders = false)
                => contexts[name] = new Contexts(name, path, fallback, resume, stale,
                                                 loop, cool, fadeintime, bypass, priority, once, volume,
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
            Add("Combat_Boss",         "combat/_boss",          "Combat_High",       stale:20, cool:3, fadeintime:3, bypass:true, priority:52);
            Add("Combat_High",         "combat/_high",          "Combat_Low",        stale:20, cool:3, fadeintime:3, bypass:true, priority:51);
            Add("Combat_Low",          "combat",                "VanillaIfPanicked", stale:20, cool:3, fadeintime:3, bypass:true, priority:50);
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
            Add("Online_Stranger",     "testing/online/stranger", stale:30,                                    priority:29, warmup:3);
            Add("Online_Friend",       "testing/online/friendly", stale:30,                                    priority:14, warmup:3);
            Add("Trader_Joel",         "trader/joel",           "Trader",                                      priority:26, warmup:3);
            Add("Trader_Jen",          "trader/jen",            "Trader",                                      priority:26, warmup:3);
            Add("Trader_Rekt",         "trader/rekt",           "Trader",                                      priority:26, warmup:3);
            Add("Trader_Bob",          "trader/bob",            "Trader",                                      priority:26, warmup:3);
            Add("Trader_Hugh",         "trader/hugh",           "Trader",                                      priority:26, warmup:3);
            Add("Trader",              "trader",                "VanillaIfDMS",                                priority:25, warmup:3);
            Add("Home_Night",          "home/night",            "Home",                                        priority:19, warmup:5, warmgroup:"home");
            Add("Home",                "home",                                                                 priority:19, warmup:5, warmgroup:"home");
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
            // Inert carrier for the world-emitter/personal radio (source/fades/volume);
            // never activated by the resolver — mirrored so the table count matches.
            Add("Radio",                     "Radio",                      "Stack",                     priority:10, volume:0.5f);
            Add("Silence",  "", "Silence",  resume:false, stale:0, cool:0, fadeintime:0, bypass:true, priority:0);
            Add("Vanilla",  "", "Vanilla",  resume:false, stale:0, cool:0, fadeintime:0, bypass:true, priority:-1);
            Add("Menu",     "menu", "Vanilla", resume:false, stale:0, cool:5, fadeintime:2, bypass:true, priority:0);
        }

        public static Contexts GetContext(string contextIn) =>
            contextIn != null && contexts.TryGetValue(contextIn, out var ctx) ? ctx : null;

        // ════════════════════════════════════════════════════════════════════
        // File index — built once over the real (read-only) music library, using
        // the SAME enumeration + claiming logic the mod ships (copied below).
        // ContextHasFiles becomes a pure dictionary read, exactly as it is in-game
        // (a snapshot of _contextsWithFiles), and PickFile drives the per-transition
        // decode against real tracks.
        // ════════════════════════════════════════════════════════════════════
        private static string MusicDirectory;
        private static readonly Dictionary<string, string[]> _index =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _rotor =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static int IndexedContexts => _index.Count(kv => kv.Value.Length > 0);
        public static int IndexedFiles => _index.Sum(kv => kv.Value.Length);

        // Builds context → file[] over the library. Subfolders are enabled for any
        // context whose folder strictly contains another context's folder (so the
        // claiming filter engages exactly as a real recursive config would).
        public static void BuildFileIndex(string musicDir)
        {
            MusicDirectory = musicDir;
            _index.Clear();
            if (string.IsNullOrEmpty(musicDir) || !Directory.Exists(musicDir)) return;

            // Mirror a recursive config: turn on subfolders for parents-of-contexts.
            foreach (var c in contexts.Values.Where(c => !string.IsNullOrEmpty(c.Source) && HasDescendantContext(c)).ToList())
                contexts[c.Context] = new Contexts(context: c.Context, source: c.Source, fallback: c.Fallback,
                    canberesumed: c.CanBeResumed, timeuntilcontextstale: c.TimeUntilContextStale, loop: c.Loop,
                    reentry: c.Reentry, fadeInDuration: c.FadeInDuration, bypassesreentry: c.BypassesReentry,
                    priority: c.Priority, playOnce: c.PlayOnce,
                    volumeOverride: c.VolumeOverride, warmup: c.WarmUp, warmGroup: c.WarmGroup,
                    includeSubfolders: true);

            foreach (var ctx in contexts.Values)
            {
                if (string.IsNullOrEmpty(ctx.Source)) continue;
                string dir = Path.Combine(MusicDirectory, ctx.Source);
                _index[ctx.Context] = Directory.Exists(dir) ? EnumerateContextFiles(ctx, dir) : Array.Empty<string>();
            }
        }

        // Pure dictionary read — mirrors the in-game ContextHasFiles (a read of the
        // _contextsWithFiles snapshot). Referenced by the LINKED ScriptHost.cmm.hasFiles.
        public static bool ContextHasFiles(string contextName) =>
            contextName != null && _index.TryGetValue(contextName, out var f) && f.Length > 0;

        // RandomNoRepeat-ish rotation over a context's pool — the file a transition decodes.
        public static string PickFile(string contextName)
        {
            if (contextName == null || !_index.TryGetValue(contextName, out var files) || files.Length == 0) return null;
            int i = _rotor.TryGetValue(contextName, out int last) ? (last + 1) % files.Length : 0;
            _rotor[contextName] = i;
            return files[i];
        }

        // Read-only view of the file index + a non-advancing peek at the file the NEXT PickFile
        // would return for a context. Used by the harness to model the loudness policy: the menu
        // prewarm (one track per context) iterates the index; the look-ahead pre-measures the
        // staged next track (PeekNextFile) exactly as ScheduleNextCacheLoad does in-game.
        public static IReadOnlyDictionary<string, string[]> Index => _index;
        public static string PeekNextFile(string contextName)
        {
            if (contextName == null || !_index.TryGetValue(contextName, out var files) || files.Length == 0) return null;
            int i = _rotor.TryGetValue(contextName, out int last) ? (last + 1) % files.Length : 0;
            return files[i];
        }

        // ════════════════════════════════════════════════════════════════════
        // Selection — copied from ContextMusicManager.ContextStack.cs with the
        // Unity touch-points swapped (see header). Logic is otherwise verbatim.
        // ════════════════════════════════════════════════════════════════════
        private static readonly Dictionary<string, float> _warmLogThrottle =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _warmingNow =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool DwellSatisfied(ActiveContext active)
        {
            Contexts ctx = GetContext(active.ContextName);
            int warm = ctx?.WarmUp ?? 0;
            if (warm <= 0) return true;

            float now = SimClock.Now;                                  // was Time.realtimeSinceStartup
            float remaining = warm - (now - active.FirstSeenAt);
            if (remaining > 0f)
            {
                if (!_warmLogThrottle.TryGetValue(active.ContextName, out float last) || now - last >= 1f)
                {
                    _warmLogThrottle[active.ContextName] = now;
                    CMMConfig.D($"[CMM] {active.ContextName} warming up — {remaining:F1}s left (WarmUp={warm}s)");
                }
                _warmingNow.Add(active.ContextName);
                return false;
            }
            if (_warmingNow.Remove(active.ContextName))
                _warmLogThrottle.Remove(active.ContextName);
            return true;
        }

        internal static float InheritedFirstSeen(string contextName, float now)
        {
            string group = GetContext(contextName)?.WarmGroup;
            if (string.IsNullOrEmpty(group)) return now;
            float oldest = now;
            foreach (ActiveContext ac in _contextStack)
            {
                if (ac.ContextName == contextName) continue;
                if (string.Equals(GetContext(ac.ContextName)?.WarmGroup, group, StringComparison.OrdinalIgnoreCase))
                    oldest = UnityEngine.Mathf.Min(oldest, ac.FirstSeenAt);
            }
            return oldest;
        }

        private static readonly HashSet<string> _resolvingContexts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string ResolvePlayableContext(string contextName)
        {
            if (_resolvingContexts.Contains(contextName)) return null;
            _resolvingContexts.Add(contextName);
            try { return ResolvePlayableContextCore(contextName); }
            finally { _resolvingContexts.Remove(contextName); }
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
                        if (!DwellSatisfied(active)) continue;
                        string resolved = ResolvePlayableContext(active.ContextName);
                        if (resolved != null) return resolved;
                    }
                    return null;
                }
                if (fallback == "VanillaIfPanicked")
                {
                    // was: DMS on && localPlayer.ThreatLevel.Category == Panicked
                    if (DmsEnabled && _snapshot != null && _snapshot.IsPanicked)
                        return "Vanilla";
                    foreach (ActiveContext active in _contextStack)
                    {
                        if (!DwellSatisfied(active)) continue;
                        string resolved = ResolvePlayableContext(active.ContextName);
                        if (resolved != null) return resolved;
                    }
                    return null;
                }
                if (fallback == "VanillaIfDMS")
                {
                    if (DmsEnabled)
                    {
                        // was: if (TryGetReadyVanillaClip(contextName, out _)) return contextName;
                        // No borrowed DMS clips standalone → always hand off to Vanilla.
                        return "Vanilla";
                    }
                    foreach (ActiveContext active in _contextStack)
                    {
                        if (!DwellSatisfied(active)) continue;
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
                if (!DwellSatisfied(active)) continue;
                string playable = ResolvePlayableContext(active.ContextName);
                if (playable != null) return playable;
            }
            return DmsEnabled ? "Vanilla" : null;
        }

        // ════════════════════════════════════════════════════════════════════
        // Per-tick driver — ports the synchronous body of ContextScanLoop
        // (ContextMusicManager.cs ~296-451): resolve → change-detect gate → stack
        // add/refresh/evict/sort → GetTopContext → transition decision. The audio-
        // strand recovery (currentAudioDead / coroutine + channel probes) is dropped:
        // there is no audio thread here, so a committed transition is treated as
        // "now playing". Returns whether a real transition fired and the resolved top.
        // ════════════════════════════════════════════════════════════════════
        private static readonly HashSet<string> _previousActiveContexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _previousPlayableContexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _hadWarmingContexts = false;

        public struct TickResult { public bool Transitioned; public string Top; }

        // A PlayOnce stinger (Death/Spawn/POI_Cleared) is a single short track. In-game the
        // PlayOnce guard only protects it WHILE AUDIBLE — once its track ends (currentAudioDead)
        // the guard is skipped so the next context takes over. With no audio engine here, model
        // that audible window as a fixed stinger length; past it the guard is skipped, exactly
        // as the shipped "stranded silent PlayOnce" path does. Without this a PlayOnce would
        // latch as currentContext forever and block every later transition.
        private const float PlayOnceStingerSeconds = 20f;
        private static float _playOnceUntil = -1f;

        public static TickResult Tick(List<string> activeContexts, GameState snapshot)
        {
            _snapshot = snapshot;
            var result = new TickResult { Transitioned = false, Top = currentContext?.Context };

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

            bool currentIsStuck = currentContext == null || currentContext.Context == "Silence";
            bool hasPendingEvictions = _contextStack.Any(c => !c.Manual && !activeContexts.Contains(c.ContextName));
            bool hasWarmingContexts = _contextStack.Any(c => !DwellSatisfied(c));
            bool warmingJustCleared = _hadWarmingContexts && !hasWarmingContexts;
            _hadWarmingContexts = hasWarmingContexts;

            if (!namesChanged && !playabilityChanged && !currentIsStuck && !vanillaIsActive
                && !hasPendingEvictions && !hasWarmingContexts && !warmingJustCleared)
                return result;

            _previousActiveContexts.Clear();
            foreach (string c in activeContexts) _previousActiveContexts.Add(c);
            _previousPlayableContexts.Clear();
            foreach (var kv in currentlyPlayable) _previousPlayableContexts[kv.Key] = kv.Value;

            foreach (string ctxName in activeContexts)
            {
                if (_contextStack.All(c => c.ContextName != ctxName))
                {
                    Contexts ctx = GetContext(ctxName);
                    if (ctx == null) continue;
                    if (ResolvePlayableContext(ctxName) == null) continue;
                    float seenAt = SimClock.Now;
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

            float now = SimClock.Now;
            foreach (ActiveContext ac in _contextStack)
                if (!ac.Manual && activeContexts.Contains(ac.ContextName)) ac.ActivatedAt = now;
            _contextStack.RemoveAll(c => !c.Manual && !activeContexts.Contains(c.ContextName) && (now - c.ActivatedAt) >= 1.5f);
            _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            string topContext = GetTopContext();

            if (topContext == null)
            {
                if (currentContext?.Context != "Silence")
                {
                    currentContext = GetContext("Silence");
                    vanillaIsActive = false;
                    result.Transitioned = true;
                }
            }
            else if (currentContext == null || topContext != currentContext.Context)
            {
                // Guard an AUDIBLE PlayOnce only (within its stinger window); a stinger that
                // has finished is "dead" and may be replaced — see PlayOnceStingerSeconds.
                if (currentContext != null && currentContext.PlayOnce && SimClock.Now < _playOnceUntil)
                {
                    Contexts top = GetContext(topContext);
                    if (top == null || top.Priority <= currentContext.Priority)
                    {
                        result.Top = currentContext.Context;
                        return result; // do not interrupt an audible PlayOnce with ≤ priority
                    }
                }
                currentContext = GetContext(topContext) ?? GetContext("Vanilla");
                vanillaIsActive = topContext == "Vanilla";
                _playOnceUntil = currentContext != null && currentContext.PlayOnce ? SimClock.Now + PlayOnceStingerSeconds : -1f;
                result.Transitioned = true;
            }

            result.Top = topContext ?? "Silence";
            return result;
        }

        // World-unload reset (mirrors the in-game teardown of selection state).
        public static void ResetSession()
        {
            _contextStack.Clear();
            _previousActiveContexts.Clear();
            _previousPlayableContexts.Clear();
            _warmingNow.Clear();
            _warmLogThrottle.Clear();
            _hadWarmingContexts = false;
            currentContext = null;
            vanillaIsActive = false;
            _playOnceUntil = -1f;
        }

        // ════════════════════════════════════════════════════════════════════
        // Folder enumeration + subtree claiming — copied VERBATIM from
        // ContextMusicManager.AudioPlayer.cs (~1604-1717), same as tools/CmmScanTest.
        // ════════════════════════════════════════════════════════════════════
        private static readonly string[] _noClaimPrefixes = new string[0];
        private static readonly HashSet<string> _invalidFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static SearchOption SearchOptionFor(Contexts ctx) =>
            ctx != null && ctx.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        private static string[] GetClaimedSubtreePrefixes(string contextName)
        {
            Contexts self = GetContext(contextName);
            if (self == null || string.IsNullOrEmpty(self.Source) || !self.IncludeSubfolders) return _noClaimPrefixes;
            string selfDir = NormalizedContextDir(self.Source);
            if (selfDir == null) return _noClaimPrefixes;
            string selfPrefix = selfDir + Path.DirectorySeparatorChar;

            List<string> prefixes = null;
            foreach (Contexts other in contexts.Values)
            {
                if (other == self || string.IsNullOrEmpty(other.Source)) continue;
                if (string.Equals(other.Context, self.Context, StringComparison.OrdinalIgnoreCase)) continue;
                string otherDir = NormalizedContextDir(other.Source);
                if (otherDir == null) continue;
                if (otherDir.Length > selfDir.Length && otherDir.StartsWith(selfPrefix, StringComparison.OrdinalIgnoreCase))
                    (prefixes ?? (prefixes = new List<string>())).Add(otherDir + Path.DirectorySeparatorChar);
            }
            return prefixes == null ? _noClaimPrefixes : prefixes.ToArray();
        }

        private static bool IsClaimedByDeeperContext(string file, string[] claimedPrefixes)
        {
            if (claimedPrefixes.Length == 0) return false;
            string full;
            try { full = Path.GetFullPath(file); } catch { return false; }
            foreach (string p in claimedPrefixes)
                if (full.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string NormalizedContextDir(string ctxPath)
        {
            try
            {
                return Path.GetFullPath(Path.Combine(MusicDirectory, ctxPath))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return null; }
        }

        private static string[] EnumerateContextFiles(Contexts ctx, string dir)
        {
            string[] claimed = GetClaimedSubtreePrefixes(ctx.Context);
            return Directory.GetFiles(dir, "*", SearchOptionFor(ctx))
                .Where(f => AudioDecoder.IsSupported(f) && !_invalidFiles.Contains(f) && !IsClaimedByDeeperContext(f, claimed))
                .ToArray();
        }

        private static bool HasDescendantContext(Contexts self)
        {
            string selfDir = NormalizedContextDir(self.Source);
            if (selfDir == null) return false;
            string selfPrefix = selfDir + Path.DirectorySeparatorChar;
            foreach (var other in contexts.Values)
            {
                if (other == self || string.IsNullOrEmpty(other.Source)) continue;
                string od = NormalizedContextDir(other.Source);
                if (od != null && od.Length > selfDir.Length && od.StartsWith(selfPrefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
