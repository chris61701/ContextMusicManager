using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

namespace CMM
{
    // ─── Track ordering mode (global) ───────────────────────────────────────────
    public enum TrackOrderMode { Random, RandomNoRepeat, Sequential }

    // ─── Per-context runtime state ───────────────────────────────────────────────
    public class ContextCache
    {
        public AudioClip   cachedClip        = null;   // ready to play instantly
        public AudioClip   nextCachedClip    = null;   // preloaded ~NextCacheWindow s before end
        public float       resumePosition    = 0f;     // seconds
        public float       lastLeftTime      = -1f;    // Time.realtimeSinceStartup when we left
        public string      lastPlayedFile    = null;   // for RandomNoRepeat
        public int         sequentialIndex   = 0;      // for Sequential
        public string[]    directorySnapshot = null;   // file list from last scan
        public bool        nextClipLoading   = false;  // guard against double-loads
    }

    // ─── Context definition (unchanged fields, added TrackOrder stub) ─────────────
    public class Contexts
    {
        public string Context              { get; private set; }
        public string Path                 { get; private set; }
        public string Fallback             { get; private set; }
        public bool   CanBeResumed         { get; private set; }
        public int    TimeUntilContextStale{ get; private set; }
        public bool   Loop                 { get; private set; }
        public int    Cooldown             { get; private set; }
        public int    WarmUp               { get; private set; }
        public bool   BypassesCooldown     { get; private set; }
        public bool   CanInterrupt         { get; private set; }
        public int    Priority             { get; private set; }
        public bool   PlayOnce             { get; private set; }

        public Contexts(string context = "None", string path = "", string fallback = "None",
                        bool canberesumed = false, int timeuntilcontextstale = 120,
                        bool loop = false, int cooldown = 5, int warmup = 3, bool bypassescooldown = false,
                        bool caninterrupt = false, int priority = 0, bool playOnce = false)
        {
            Context               = context;
            Path                  = path;
            Fallback              = fallback;
            CanBeResumed          = canberesumed;
            TimeUntilContextStale = timeuntilcontextstale;
            Loop                  = loop;
            Cooldown              = cooldown;
            WarmUp                = warmup;
            BypassesCooldown      = bypassescooldown;
            CanInterrupt          = caninterrupt;
            Priority              = priority;
            PlayOnce              = playOnce;
        }
    }

    // ─── Context resolver (unchanged) ────────────────────────────────────────────
    public static class ContextResolver
    {
        static readonly float enemyDetectRange  = 20f;
        static readonly int   dangerThreshold   = 1;
        static readonly int   criticalThreshold = 4;
        static readonly float TraderDetectRange = 40f;
        static string combatState = "None";
        public static bool inCombat   = false;
        public static bool wasInCombat= false;
        public static bool inVehicle  = false;
        public static bool wasInVehicle=false;
        public static bool inTension  = false;
        public static bool wasTension = false;
        public static bool atTrader = false;
        static int tickTracker = 0;
        static float combatCoolDown = 10f;
        static float lastAggroTime = 0f;
        public static void resetValues()
        {
            inCombat = inVehicle = inTension = false;
            combatState = "None";
        }

        public static string ResolveContext()
        {
            inCombat = inVehicle = inTension = false;
            string traderName    = "Trader";
            bool   traderNearby  = false;
            int    enemyAggroCount = 0, enemyAlertCount = 0, enemyCount = 0, enemiesNearby = 0;
            var world  = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            tickTracker++;

            if (world == null || player == null) return "None";
            var entities = world.Entities.list;

            if (player.AttachedToEntity is EntityVehicle vehicle)
            {
                string vn = vehicle.GetType().Name.ToLower();
                inVehicle = true;
                if (vn.Contains("gyrocopter")) return "Vehicle_Gyrocopter";
                if (vn.Contains("bicycle"))    return "Vehicle_Bicycle";
                if (vn.Contains("motorcycle") || vn.Contains("minibike")) return "Vehicle_Motorcycle";
                if (vn.Contains("4x4") || vn.Contains("truck"))           return "Vehicle_4x4";
                return "Vehicle";
            }

            foreach (var entity in entities)
            {
                if (entity is EntityTrader trader)
                {
                    if (Vector3.Distance(player.position, trader.position) > TraderDetectRange) continue;
                    traderNearby = true;
                    atTrader = true;
                    string n = trader.EntityName?.ToLower() ?? "";
                    if (n.Contains("jen"))  traderName = "Trader_Jen";
                    if (n.Contains("rekt")) traderName = "Trader_Rekt";
                    if (n.Contains("bob"))  traderName = "Trader_Bob";
                    if (n.Contains("hugh")) traderName = "Trader_Hugh";
                    if (n.Contains("joel")) traderName = "Trader_Joel";
                }
                else if (entity is EntityEnemy enemy)
                {
                    if (!enemy.IsAlive()) continue;
                    if (Vector3.Distance(player.position, enemy.position) < enemyDetectRange)
                    { enemyCount++; enemiesNearby++; }
                    if (enemy.isAlert)
                    {
                        if (tickTracker % 10 == 1)
                            Log.Error($"Enemy: {enemy.entityName} is awere of the player");
                        if (enemy.GetAttackTarget() == player)
                        {
                            if (tickTracker % 10 == 1) 
                                Log.Error($"Enemy: {enemy.entityName} is aggroed on player");
                            lastAggroTime = Time.realtimeSinceStartup;
                            enemyAggroCount++;
                            inCombat = true;
                        }
                        enemyAlertCount++;
                    }
                }
            }

            atTrader = false;

            float CombatCooldownState = Time.realtimeSinceStartup - lastAggroTime;

            if (enemyAggroCount >= criticalThreshold)
            {   combatState = "High";
                return "Combat_High";
            }
            if (enemyAggroCount >= dangerThreshold)
            {
                if (combatState != "High")
                {
                    combatState = "Low";
                }
                return combatState == "High" ? "Combat_High" : "Combat_Low";
            }

            if (CombatCooldownState <= combatCoolDown && enemyAlertCount > 0)
            {
                // Still within cooldown window even with no active aggro
                return combatState == "High" ? "Combat_High" : "Combat_Low";
            }

            combatState = "None";
            if (traderNearby) return traderName;
            if (enemiesNearby >= criticalThreshold || enemyAlertCount >= criticalThreshold)  return "Tension_High";
            if (enemiesNearby >= dangerThreshold || enemyAlertCount >= dangerThreshold)    return "Tension_Low";
            return "Ambient";
        }
    }

    // ─── Mod entry point ─────────────────────────────────────────────────────────
    public class Initialize : IModApi
    {
        public void InitMod(Mod __instance)
        {
            var harmony = new Harmony("com.velasha.contextmusicmanager");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            ContextMusicManager.Init(__instance.Path);
        }
    }

    // ─── Coroutine host ──────────────────────────────────────────────────────────
    public class CoRoutine : MonoBehaviour { }

    // ─── Main manager ────────────────────────────────────────────────────────────
    public class ContextMusicManager : MonoBehaviour
    {
        // ── Configuration ────────────────────────────────────────────────────────
        public static TrackOrderMode GlobalTrackOrder      = TrackOrderMode.RandomNoRepeat;
        public static float          ContextScanCooldown   = 0.5f;   // seconds
        public static float          StalenessCheckInterval= 30f;    // seconds
        public static float          NextCacheWindow       = 5f;     // seconds before track end
        public static float          TrackEndPollInterval  = 0.25f;  // seconds

        // ── Static state ─────────────────────────────────────────────────────────
        public static List<Contexts>                    contexts      = new List<Contexts>();
        public static Dictionary<string, ContextCache> cacheMap      = new Dictionary<string, ContextCache>();
        public static string  MusicDirectory = "/Music";
        public static bool    inGame         = false;
        public static bool    inInventory    = false;
        public static Contexts currentContext;
        public static string  ModPath;

        // PlayOnce: remember what context was active before a PlayOnce fired
        private static string _playOnceReturnContext = null;

        // Coroutine handles
        private static Coroutine _activeMusicCoroutine  = null;
        private static Coroutine _nextCacheCoroutine    = null;
        private static Coroutine _trackEndCoroutine     = null;

        // Audio
        private static AudioSource  CMM_Primary_Channel;
        private static GameObject   CMM_Audio_Object;
        public  static CoRoutine    CMM_CoRoutines;
        public  static ContextMusicManager Instance;

        // ── Context definitions ───────────────────────────────────────────────────
        public void InitContexts()
        {
            contexts.Add(new Contexts("Vehicle",            "vehicle",            "Ambient",     true,  30,  false, 5, 1, true,  true,  100, false));
            contexts.Add(new Contexts("Vehicle_Gyrocopter", "vehicle/gyrocopter", "Vehicle",     true,  30,  false, 5, 1, true,  true,  100, false));
            contexts.Add(new Contexts("Vehicle_Bicycle",    "vehicle/bicycle",    "Vehicle",     true,  30,  false, 5, 1, true,  true,  100, false));
            contexts.Add(new Contexts("Vehicle_Motorcycle", "vehicle/motorcycle", "Vehicle",     true,  30,  false, 5, 1, true,  true,  100, false));
            contexts.Add(new Contexts("Vehicle_4x4",        "vehicle/4x4",        "Vehicle",     true,  30,  false, 5, 1, true,  true,  100, false));
            contexts.Add(new Contexts("Combat_High",        "combat_high",        "Combat_Low",  true,  20,  false, 3, 1, true,  true,  97,  false));
            contexts.Add(new Contexts("Combat_Low",         "combat_low",         "Tension_Low", true,  20,  false, 3, 1, true,  true,  95,  false));
            contexts.Add(new Contexts("Death",              "death",              "Ambient",     false, 0,   false, 3, 0, true,  true,  91,  true ));
            contexts.Add(new Contexts("Spawn",              "stinger",            "Ambient",     false, 0,   false, 3, 0, true,  true,  90,  true ));
            contexts.Add(new Contexts("Inventory",          "inventory",          "Ambient",     true,  15,  false, 3, 0, true,  true,  88,  true ));
            contexts.Add(new Contexts("Tension_High",       "tension_high",       "Tension_Low", true,  30,  false, 5, 5, false, true,  87,  false));
            contexts.Add(new Contexts("Tension_Low",        "tension_low",        "Ambient",     true,  30,  false, 5, 5, false, true,  85,  false));
            contexts.Add(new Contexts("Trader_Joel",        "trader/joel",        "Trader",      true,  120, false, 5, 3, false, true,  81,  false));
            contexts.Add(new Contexts("Trader_Jen",         "trader/jen",         "Trader",      true,  120, false, 5, 3, false, true,  81,  false));
            contexts.Add(new Contexts("Trader_Rekt",        "trader/rekt",        "Trader",      true,  120, false, 5, 3, false, true,  81,  false));
            contexts.Add(new Contexts("Trader_Bob",         "trader/bob",         "Trader",      true,  120, false, 5, 3, false, true,  81,  false));
            contexts.Add(new Contexts("Trader_Hugh",        "trader/hugh",        "Trader",      true,  120, false, 5, 3, false, true,  81,  false));
            contexts.Add(new Contexts("Trader",             "trader",             "Ambient",     true,  120, false, 5, 3, false, true,  50,  false));
            contexts.Add(new Contexts("Ambient",            "ambient",            "None",        true,  120, false, 5, 2, true,  true,  50,  false));
            contexts.Add(new Contexts("None",               "",                   "None",        false, 0,   false, 0, 0, true,  true,  0,   false));
            contexts.Add(new Contexts("Menu",               "menu",               "Ambient",     false, 0,   true,  5, 3, true,  true,  0,   false));
        }

        // ── Initialisation ────────────────────────────────────────────────────────
        public static void Init(string modPath)
        {
            ModPath         = modPath;
            CMM_Audio_Object = new GameObject("ContextMusicManager");
            UnityEngine.Object.DontDestroyOnLoad(CMM_Audio_Object);
            Instance = CMM_Audio_Object.AddComponent<ContextMusicManager>();
            Instance.InitContexts();

            // Create a cache entry for every context
            foreach (var ctx in contexts)
                cacheMap[ctx.Context] = new ContextCache();

            currentContext          = GetContext("None");
            CMM_Primary_Channel     = CMM_Audio_Object.AddComponent<AudioSource>();
            CMM_CoRoutines          = CMM_Audio_Object.AddComponent<CoRoutine>();
            CMM_Primary_Channel.volume = 0f;
            CMM_Primary_Channel.loop   = false;
            CMM_CoRoutines.StartCoroutine(Instance.PreloadContext("Menu"));

            CMM_CoRoutines.StartCoroutine(Instance.ContextScanLoop());
            CMM_CoRoutines.StartCoroutine(Instance.StalenessCheckLoop());
            Log.Out("[CMM] Initialized!");
        }

        // ── Called once when we enter the game world ──────────────────────────────
        public static void OnEnterGame()
        {
            if (!inGame) return;
            CMM_CoRoutines.StartCoroutine(Instance.PreloadAllContexts());
        }

        // ─────────────────────────────────────────────────────────────────────────
        // PRELOADING
        // ─────────────────────────────────────────────────────────────────────────

        // Sequentially preload one cachedClip per context
        private IEnumerator PreloadAllContexts()
        {
            Log.Out("[CMM] Starting sequential preload for all contexts...");
            foreach (var ctx in contexts)
            {
                if (ctx.Context == "None") continue;
                yield return StartCoroutine(PreloadContext(ctx.Context));
            }
            Log.Out("[CMM] Preload complete.");
        }

        // Scan directory, pick a file, load it into cacheMap[contextName].cachedClip
        // If directory is empty, tries the fallback chain (but only one level deep for
        // preload — if fallback is also empty the clip stays null).
        private IEnumerator PreloadContext(string contextName)
        {
            ContextCache cache = cacheMap[contextName];
            string filePath = PickNextFile(contextName, avoidFile: null);

            if (filePath == null)
            {
                // Try one fallback level
                Contexts ctx         = GetContext(contextName);
                string   fallbackName = ctx?.Fallback ?? "None";
                if (fallbackName != "None" && fallbackName != contextName)
                {
                    filePath = PickNextFile(fallbackName, avoidFile: null);
                    if (filePath == null)
                    {
                        Log.Out($"[CMM] Preload: no files for {contextName} or fallback {fallbackName}");
                        yield break;
                    }
                }
                else
                {
                    Log.Out($"[CMM] Preload: no files for {contextName}");
                    yield break;
                }
            }

            AudioType audioType = CheckType(filePath);
            if (audioType == AudioType.UNKNOWN) { yield break; }

            using (var request = UnityWebRequestMultimedia.GetAudioClip(
                       "file:///" + filePath.Replace("\\", "/"), audioType))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Log.Error($"[CMM] Preload failed for {contextName}: {request.error}");
                    yield break;
                }
                if (cache.cachedClip != null)
                    Destroy(cache.cachedClip);

                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
                cache.cachedClip     = DownloadHandlerAudioClip.GetContent(request);
                cache.lastPlayedFile = filePath;
                Log.Out($"[CMM] Preloaded: {contextName}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // CONTEXT SCAN LOOP
        // ─────────────────────────────────────────────────────────────────────────

        private IEnumerator ContextScanLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(ContextScanCooldown);
                if (!inGame) continue;

                string   resolved = ContextResolver.ResolveContext();
                Contexts incoming = GetContext(resolved);
                if (incoming == null || resolved == currentContext.Context) continue;

                bool isCombatEnding  =    !ContextResolver.inCombat   && currentContext.Context.ToLower().Contains("combat");
                bool isVehicleEnding =    !ContextResolver.inVehicle   && currentContext.Context.ToLower().Contains("vehicle");
                bool isTensionEnding =    !ContextResolver.inTension   && currentContext.Context.ToLower().Contains("tension");
                bool isInventoryClosing = !inInventory && currentContext.Context.ToLower().Contains("inventory");
                bool isLeavingTrader =    !ContextResolver.atTrader && currentContext.Context.ToLower().Contains("trader");

                int bypassPriority = (isCombatEnding || isVehicleEnding || isTensionEnding || isInventoryClosing || isLeavingTrader) ? 0 : -1;
                SendContextUpdate(resolved, bypassPriority);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // STALENESS CHECK LOOP
        // ─────────────────────────────────────────────────────────────────────────

        private IEnumerator StalenessCheckLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(StalenessCheckInterval);
                if (!inGame) continue;

                float now = Time.realtimeSinceStartup;
                foreach (var ctx in contexts)
                {
                    if (ctx.Context == currentContext.Context) continue;
                    if (!ctx.CanBeResumed) continue;
                    if (ctx.TimeUntilContextStale <= 0) continue;

                    ContextCache cache = cacheMap[ctx.Context];
                    if (cache.lastLeftTime < 0) continue;

                    float elapsed = now - cache.lastLeftTime;
                    if (elapsed >= ctx.TimeUntilContextStale)
                    {
                        Log.Out($"[CMM] Context {ctx.Context} is stale ({elapsed:F0}s). Re-caching.");
                        cache.resumePosition = 0f;
                        yield return StartCoroutine(PreloadContext(ctx.Context));
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SEND CONTEXT UPDATE
        // ─────────────────────────────────────────────────────────────────────────

        public static void SendContextUpdate(string contextIn, int bypassPriority = -1)
        {
            Contexts incoming = GetContext(contextIn);
            if (incoming == null) return;
            if (currentContext != null && currentContext.Context == contextIn) return;

            bool menuOverride    = !inGame;
            int  comparePriority = bypassPriority >= 0 ? bypassPriority : (currentContext?.Priority ?? 0);
            bool priorityOverride = inGame && incoming.CanInterrupt && incoming.Priority > comparePriority;

            if (!menuOverride && !priorityOverride)
            {
                Log.Out($"[CMM] {contextIn} does not have higher priority over {currentContext?.Context}");
                Log.Out($"[CMM] {contextIn} can{(incoming.CanInterrupt ? "" : "not")} interrupt");
                return;
            }

            Log.Out($"[CMM] Context changed: {currentContext?.Context} -> {contextIn}");

            // Save resume data for the context we're leaving
            if (currentContext != null && currentContext.CanBeResumed && CMM_Primary_Channel.isPlaying)
            {
                ContextCache leavingCache = cacheMap[currentContext.Context];
                leavingCache.resumePosition = CMM_Primary_Channel.time;
                leavingCache.lastLeftTime   = Time.realtimeSinceStartup;
                Log.Out($"[CMM] Saved resume position for {currentContext.Context}: {leavingCache.resumePosition:F2}s");
            }

            // Handle PlayOnce: snapshot what context we should return to
            if (incoming.PlayOnce)
            {
                string resolved = ContextResolver.ResolveContext();
                Contexts resolvedCtx = GetContext(resolved);
                // Walk past any other PlayOnce
                while (resolvedCtx != null && resolvedCtx.PlayOnce && resolved != incoming.Context)
                {
                    if (resolvedCtx.Priority > incoming.Priority)
                    {
                        // The resolved PlayOnce has higher priority — let it take over instead
                        incoming = resolvedCtx;
                        contextIn = resolved;
                    }
                    else
                    {
                        resolved    = resolvedCtx.Fallback;
                        resolvedCtx = GetContext(resolved);
                    }
                }
                _playOnceReturnContext = (resolvedCtx != null && !resolvedCtx.PlayOnce) ? resolved : incoming.Fallback;
                Log.Out($"[CMM] PlayOnce {contextIn} will return to: {_playOnceReturnContext}");
            }

            // Walk fallback chain to find a context with actual files
            string contextToPlay = contextIn;
            for (int i = 0; i < 5; i++)
            {
                ContextCache cache = cacheMap[contextToPlay];
                if (cache.cachedClip != null)
                {
                    currentContext = GetContext(contextToPlay);
                    TriggerMusic(contextToPlay);
                    return;
                }
                // No cached clip — try fallback
                Contexts fallbackCtx = GetContext(GetContext(contextToPlay)?.Fallback ?? "None");
                if (fallbackCtx == null || fallbackCtx.Context == "None" || fallbackCtx.Context == contextToPlay)
                {
                    Log.Out($"[CMM] No cached clip for {contextToPlay} or its fallbacks. Silence.");
                    currentContext = GetContext(contextToPlay);
                    return;
                }
                Log.Out($"[CMM] No cached clip for {contextToPlay}, falling back to {fallbackCtx.Context}");
                contextToPlay = fallbackCtx.Context;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // TRIGGER MUSIC  (single entry point for all playback)
        // ─────────────────────────────────────────────────────────────────────────

        public static void TriggerMusic(string contextName)
        {
            Log.Out($"[CMM] TriggerMusic: {contextName}");

            // Stop any in-flight load
            if (_activeMusicCoroutine != null)
            {
                CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine);
                _activeMusicCoroutine = null;
            }
            if (_nextCacheCoroutine != null)
            {
                CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine);
                _nextCacheCoroutine = null;
            }
            if (_trackEndCoroutine != null)
            {
                CMM_CoRoutines.StopCoroutine(_trackEndCoroutine);
                _trackEndCoroutine = null;
            }

            _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(Instance.PlayFromCache(contextName));
        }

        // ─────────────────────────────────────────────────────────────────────────
        // PLAY FROM CACHE
        // ─────────────────────────────────────────────────────────────────────────

        private IEnumerator PlayFromCache(string contextName)
        {
            Contexts     ctx   = GetContext(contextName);
            ContextCache cache = cacheMap[contextName];

            // If cachedClip is somehow null at this point, wait briefly for preload
            float waited = 0f;
            while (cache.cachedClip == null && waited < 10f)
            {
                yield return new WaitForSeconds(0.25f);
                waited += 0.25f;
            }

            if (cache.cachedClip == null)
            {
                Log.Out($"[CMM] PlayFromCache: still no clip for {contextName} after waiting. Giving up.");
                yield break;
            }

            // Check resume
            float startPosition = 0f;
            if (ctx.CanBeResumed && cache.resumePosition > 0f)
            {
                float elapsed = Time.realtimeSinceStartup - cache.lastLeftTime;
                if (elapsed < ctx.TimeUntilContextStale)
                {
                    startPosition = cache.resumePosition;
                    Log.Out($"[CMM] Resuming {contextName} at {startPosition:F2}s");
                }
                else
                {
                    cache.resumePosition = 0f;
                    Log.Out($"[CMM] Resume position for {contextName} was stale, starting fresh.");
                }
            }

            UpdateVolume();
            CMM_Primary_Channel.Stop();
            CMM_Primary_Channel.loop  = ctx.Loop;
            CMM_Primary_Channel.clip  = cache.cachedClip;
            CMM_Primary_Channel.time  = Mathf.Clamp(startPosition, 0f, cache.cachedClip.length - 0.1f);
            CMM_Primary_Channel.Play();
            Log.Out($"[CMM] PlayMusic: {contextName}");

            // Swap nextCachedClip into cachedClip slot for next time (unless PlayOnce)
            if (!ctx.PlayOnce && cache.nextCachedClip != null)
            {
                Destroy(cache.cachedClip);
                cache.cachedClip     = cache.nextCachedClip;
                cache.nextCachedClip = null;
            }

            // Start watching for track end
            _trackEndCoroutine = CMM_CoRoutines.StartCoroutine(
                TrackEndWatcher(contextName, cache.cachedClip.length, startPosition));

            // Start preloading the next clip (not for PlayOnce)
            if (!ctx.PlayOnce)
            {
                float remaining = cache.cachedClip.length - startPosition;
                _nextCacheCoroutine = CMM_CoRoutines.StartCoroutine(
                    ScheduleNextCacheLoad(contextName, remaining));
            }

            _activeMusicCoroutine = null;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // TRACK END WATCHER
        // ─────────────────────────────────────────────────────────────────────────

        private IEnumerator TrackEndWatcher(string contextName, float clipLength, float startPosition)
        {
            // Poll until the track stops naturally (not looping)
            Contexts ctx = GetContext(contextName);
            if (ctx.Loop) yield break; // looping tracks never "end"

            while (CMM_Primary_Channel.isPlaying)
                yield return new WaitForSeconds(TrackEndPollInterval);

            // Confirm it's the same context still
            if (currentContext.Context != contextName) yield break;

            Log.Out($"[CMM] Track ended naturally: {contextName}");

            if (ctx.PlayOnce)
            {
                // Return to snapshot context
                string returnTo = _playOnceReturnContext ?? ctx.Fallback;
                _playOnceReturnContext = null;
                Log.Out($"[CMM] PlayOnce finished, returning to: {returnTo}");
                SendContextUpdate(returnTo, 0);
            }
            else
            {
                // Play next cached clip for this context
                ContextCache cache = cacheMap[contextName];

                if (cache.nextCachedClip == null)
                {
                    Log.Out($"[CMM] Next clip not ready for {contextName}, waiting...");
                    while (cache.nextCachedClip == null && currentContext.Context == contextName)
                        yield return new WaitForSeconds(TrackEndPollInterval);
                    if (currentContext.Context != contextName) yield break;
                }

                Log.Out($"[CMM] Advancing to next cached clip for {contextName}");
                Destroy(cache.cachedClip);
                cache.cachedClip     = cache.nextCachedClip;
                cache.nextCachedClip = null;

                _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(PlayFromCache(contextName));
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SCHEDULE NEXT CACHE LOAD
        // ─────────────────────────────────────────────────────────────────────────

        private IEnumerator ScheduleNextCacheLoad(string contextName, float trackRemaining)
        {
            float waitTime = Mathf.Max(0f, trackRemaining - NextCacheWindow);
            yield return new WaitForSeconds(waitTime);

            if (currentContext.Context != contextName) yield break;

            ContextCache cache = cacheMap[contextName];
            if (cache.nextClipLoading || cache.nextCachedClip != null) yield break;

            cache.nextClipLoading = true;
            yield return StartCoroutine(LoadNextClip(contextName));
            cache.nextClipLoading = false;
        }

        private IEnumerator LoadNextClip(string contextName)
        {
            ContextCache cache   = cacheMap[contextName];
            string       avoid   = GlobalTrackOrder == TrackOrderMode.RandomNoRepeat
                                    ? cache.lastPlayedFile : null;
            string       filePath = PickNextFile(contextName, avoidFile: avoid);

            if (filePath == null)
            {
                Log.Out($"[CMM] LoadNextClip: no files for {contextName}");
                yield break;
            }

            AudioType audioType = CheckType(filePath);
            if (audioType == AudioType.UNKNOWN) yield break;

            using (var request = UnityWebRequestMultimedia.GetAudioClip(
                       "file:///" + filePath.Replace("\\", "/"), audioType))
            {
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Log.Error($"[CMM] LoadNextClip failed for {contextName}: {request.error}");
                    yield break;
                }
                if (cache.nextCachedClip != null)
                    Destroy(cache.nextCachedClip);
                cache.nextCachedClip = DownloadHandlerAudioClip.GetContent(request);
                cache.lastPlayedFile = filePath;
                Log.Out($"[CMM] Next clip ready for {contextName}: {Path.GetFileName(filePath)}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // FILE SELECTION
        // ─────────────────────────────────────────────────────────────────────────

        // Scan directory fresh each time (picks up new files from modders),
        // then apply the global TrackOrderMode.
        private static string PickNextFile(string contextName, string avoidFile)
        {
            Contexts ctx = GetContext(contextName);
            if (ctx == null) return null;

            string dir = ModPath + MusicDirectory + "/" + ctx.Path + "/";
            if (!Directory.Exists(dir)) return null;

            string[] files = Directory.GetFiles(dir)
                .Where(f => CheckType(f) != AudioType.UNKNOWN)
                .ToArray();

            if (files.Length == 0) return null;

            // Update snapshot
            cacheMap[contextName].directorySnapshot = files;

            switch (GlobalTrackOrder)
            {
                case TrackOrderMode.Sequential:
                {
                    ContextCache cache = cacheMap[contextName];
                    // Reset index if files changed
                    if (cache.sequentialIndex >= files.Length)
                        cache.sequentialIndex = 0;
                    string pick = files[cache.sequentialIndex];
                    cache.sequentialIndex = (cache.sequentialIndex + 1) % files.Length;
                    return pick;
                }
                case TrackOrderMode.RandomNoRepeat:
                {
                    if (files.Length == 1) return files[0];
                    string[] candidates = avoidFile != null
                        ? files.Where(f => f != avoidFile).ToArray()
                        : files;
                    if (candidates.Length == 0) candidates = files;
                    return candidates[UnityEngine.Random.Range(0, candidates.Length)];
                }
                default: // Random
                    return files[UnityEngine.Random.Range(0, files.Length)];
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // UTILITIES
        // ─────────────────────────────────────────────────────────────────────────

        public static Contexts GetContext(string contextIn)
        {
            return contexts.Find(c => c.Context == contextIn);
        }

        public static AudioType CheckType(string inputFile)
        {
            switch (Path.GetExtension(inputFile).ToLower())
            {
                case ".mp3": return AudioType.MPEG;
                case ".ogg": return AudioType.OGGVORBIS;
                case ".wav": return AudioType.WAV;
                default:     return AudioType.UNKNOWN;
            }
        }

        public static void UpdateVolume()
        {
            if (CMM_Primary_Channel == null) return;
            CMM_Primary_Channel.volume = inGame
                ? GamePrefs.GetFloat(EnumGamePrefs.OptionsMusicVolumeLevel)
                : GamePrefs.GetFloat(EnumGamePrefs.OptionsMenuMusicVolumeLevel);
        }

        private static Coroutine _volumeFadeCoroutine = null;
        public static void FadeToVolume(float targetVolume, float duration = 1.5f)
        {
            if (_volumeFadeCoroutine != null)
                CMM_CoRoutines.StopCoroutine(_volumeFadeCoroutine);
            _volumeFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.VolumeFade(targetVolume, duration));
        }

        private IEnumerator VolumeFade(float targetVolume, float duration)
        {
            float startVolume = CMM_Primary_Channel.volume;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                CMM_Primary_Channel.volume = Mathf.Lerp(startVolume, targetVolume, elapsed / duration);
                yield return null;
            }
            CMM_Primary_Channel.volume = targetVolume;
        }
    }

    // ─── Music states (default game music toggle) ─────────────────────────────────
    public static class MusicStates
    {
        public static bool DefaultMusicEnabled = false;
        public static void ToggleDefaultMusic()
        {
            DefaultMusicEnabled = !DefaultMusicEnabled;
            Log.Error($"[CMM] Default music enabled: {DefaultMusicEnabled}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // HARMONY PATCHES
    // ─────────────────────────────────────────────────────────────────────────────

    [HarmonyPatch(typeof(DynamicMusic.Conductor), "Update")]
    public class InGameMusicPatch
    {
        static bool firstMessageLogged = false;
        static bool Prefix()
        {
            if (MusicStates.DefaultMusicEnabled) return true;
            if (!firstMessageLogged)
            {
                Log.Out("[CMM] Dynamic Music System ticks ignored!");
                firstMessageLogged = true;
            }
            if (!ContextMusicManager.inGame)
            {
                Log.Out("[CMM] Context registers as being inGame");
                ContextMusicManager.inGame = true;
                float gameVolume = GamePrefs.GetFloat(EnumGamePrefs.OptionsMusicVolumeLevel);
                ContextMusicManager.FadeToVolume(gameVolume, 1.5f);
                ContextMusicManager.OnEnterGame();
                ContextMusicManager.SendContextUpdate("Ambient");
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(BackgroundMusicMono), "Play")]
    public class MenuMusicPatch
    {
        static bool Prefix()
        {
            ContextMusicManager.UpdateVolume();
            if (!ContextMusicManager.inGame)
                ContextMusicManager.SendContextUpdate("Menu");
            return false;
        }
    }

    [HarmonyPatch(typeof(GameManager), "SaveAndCleanupWorld")]
    public class Patch_GameEnded
    {
        static void Prefix()
        {
            Log.Error("[CMM] Logging out");
            ContextMusicManager.SendContextUpdate("Menu");
            ContextMusicManager.inInventory = false;
            ContextMusicManager.inGame = false;
            ContextResolver.resetValues();
        }
    }

    [HarmonyPatch(typeof(GameManager), "Awake")]
    public class StartupLogPatch
    {
        static void Postfix() => Log.Error("[CMM] Game has started up!");
    }

    [HarmonyPatch(typeof(EntityPlayer), "Awake")]
    public static class Patch_SpawnStinger
    {
        public static void Prefix() => Log.Error("[CMM] Player spawned into world");
    }

    public static class AudioStingerHandler
    {
        public static bool Handle(string soundGroupName)
        {
            Log.Error($"[CMM Audio Spy] PlayInsidePlayerHead: {soundGroupName}");
            switch (soundGroupName)
            {
                case "player_death_stinger":
                    ContextMusicManager.SendContextUpdate("Death");
                    return false;
                case "spawnInStinger":
                    ContextMusicManager.SendContextUpdate("Spawn");
                    return false;
                case "open_inventory":
                    ContextMusicManager.inInventory = true;
                    ContextMusicManager.SendContextUpdate("Inventory");
                    return true;
                case "close_inventory":
                    ContextMusicManager.inInventory = false;
                    string resolved = ContextResolver.ResolveContext();
                    ContextMusicManager.SendContextUpdate(resolved, 0);
                    return true;
                default:
                    return true;
            }
        }
    }

    [HarmonyPatch(typeof(Audio.Manager), "PlayInsidePlayerHead",
        new Type[] { typeof(string), typeof(int), typeof(float), typeof(bool), typeof(bool) })]
    public class Patch_LogPlayInsidePlayerHead
    {
        static bool Prefix(string soundGroupName) => AudioStingerHandler.Handle(soundGroupName);
    }

    [HarmonyPatch(typeof(Audio.Manager), "PlayInsidePlayerHead",
        new Type[] { typeof(string), typeof(int) })]
    public class Patch_LogPlayInsidePlayerHead2
    {
        static bool Prefix(string soundGroupNameBegin) => AudioStingerHandler.Handle(soundGroupNameBegin);
    }
}
