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
    public enum TrackOrderMode { Random, RandomNoRepeat, Sequential }

    // ─── Feature flags ────────────────────────────────────────────────────────────
    // All flags default to enabled (matching existing behaviour).
    // Safe to change at any time — the flags are read per-call, not cached.
    public static class CMMConfig
    {
        // Crossfade between contexts. When false, the outgoing channel stops
        // immediately and the incoming channel starts at full volume.
        public static bool CrossfadeEnabled = true;

        // Pre-load a clip for each context on game entry so the first play is
        // instant. When false, the clip is loaded on demand when the context
        // first activates (small stall possible on first play).
        public static bool PreloadCacheEnabled = false;
    }

    public class ContextCache
    {
        public AudioClip cachedClip = null;
        public AudioClip nextCachedClip = null;
        public float resumePosition = 0f;
        public float lastLeftTime = -1f;
        public string lastPlayedFile = null;
        public int sequentialIndex = 0;
        public string[] directorySnapshot = null;
        public bool nextClipLoading = false;
    }

    public class ActiveContext
    {
        public string ContextName;
        public int Priority;
        public float ActivatedAt;
        public bool Manual;
    }
    public class Contexts
    {
        public string Context { get; private set; }
        public string Path { get; private set; }
        public string Fallback { get; private set; }
        public bool CanBeResumed { get; private set; }
        public int TimeUntilContextStale { get; private set; }
        public bool Loop { get; private set; }
        public int Cooldown { get; private set; }
        public int WarmUp { get; private set; }
        public bool BypassesCooldown { get; private set; }
        public bool CanInterrupt { get; private set; }
        public int Priority { get; private set; }
        public bool PlayOnce { get; private set; }

        public Contexts(string context = "None", string path = "", string fallback = "None",
                        bool canberesumed = false, int timeuntilcontextstale = 120,
                        bool loop = false, int cooldown = 5, int warmup = 3, bool bypassescooldown = false,
                        bool caninterrupt = false, int priority = 0, bool playOnce = false)
        {
            Context = context;
            Path = path;
            Fallback = fallback;
            CanBeResumed = canberesumed;
            TimeUntilContextStale = timeuntilcontextstale;
            Loop = loop;
            Cooldown = cooldown;
            WarmUp = warmup;
            BypassesCooldown = bypassescooldown;
            CanInterrupt = caninterrupt;
            Priority = priority;
            PlayOnce = playOnce;
        }
    }

    public static class ContextResolver
    {
        static readonly float enemyDetectRange = 10f;
        static readonly int dangerThreshold = 1;
        static readonly int criticalThreshold = 3;
        static readonly float TraderDetectRange = 40f;
        static readonly float HomeDetectRange = 70f;
        static string combatState = "None";
        public static bool isDay = false;
        static int tickTracker = 0;
        static float combatCoolDown = 10f;
        static float lastAggroTime = 0f;

        public static void resetValues()
        {
            combatState = "None";
        }

        public static List<string> ResolveContext()
        {
            isDay = false;
            int enemyAggroCount = 0, enemyAlertCount = 0, enemyCount = 0, enemiesNearby = 0;
            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            tickTracker++;
            List<string> ActiveContexts = new List<string>();

            if (world == null || player == null)
            {
                ActiveContexts.Add("None");
                return ActiveContexts;
            }

            var entities = world.Entities.list;


            if (GameManager.Instance.World.IsDaytime()) isDay = true;

            if (GameManager.Instance.World.isEventBloodMoon) ActiveContexts.Add("Blood_Moon");

            if (player.AttachedToEntity is EntityVehicle vehicle)
            {
                string vn = vehicle.GetType().Name.ToLower();
                Log.Error($"Vehicle type name: {vn}"); // See actual value
                switch (vn)
                {
                    case string s when s.Contains("gyrocopter"):
                        ActiveContexts.Add("Vehicle_Gyrocopter");
                        Log.Error($"Vehicle type name: {vn} actually detected"); // See actual value
                        break;
                    case string s when s.Contains("bicycle"):
                        ActiveContexts.Add("Vehicle_Bicycle");
                        Log.Error($"Vehicle type name: {vn} actually detected"); // See actual value
                        break;
                    case string s when s.Contains("motorcycle"):
                        ActiveContexts.Add("Vehicle_Motorcycle");
                        Log.Error($"Vehicle type name: {vn} actually detected"); // See actual value
                        break;
                    case string s when s.Contains("minibike"):
                        ActiveContexts.Add("Vehicle_Minibike");
                        Log.Error($"Vehicle type name: {vn} actually detected"); // See actual value
                        break;
                    case string s when s.Contains("jeep"):
                        ActiveContexts.Add("Vehicle_4x4");
                        Log.Error($"Vehicle type name: {vn} actually detected"); // See actual value
                        break;
                    default:
                        ActiveContexts.Add("Vehicle");
                        Log.Error($"Vehicle type name: {vn} default fallback"); // See actual value
                        break;
                }
            }

            foreach (var entity in entities)
            {

                if (entity is EntityTrader trader)
                {
                    if (Vector3.Distance(player.position, trader.position) > TraderDetectRange) continue;
                    string n = trader.EntityName?.ToLower() ?? "";
                    if (n.Contains("jen")) ActiveContexts.Add("Trader_Jen");
                    else if (n.Contains("rekt")) ActiveContexts.Add("Trader_Rekt");
                    else if (n.Contains("bob")) ActiveContexts.Add("Trader_Bob");
                    else if (n.Contains("hugh")) ActiveContexts.Add("Trader_Hugh");
                    else if (n.Contains("joel")) ActiveContexts.Add("Trader_Joel");
                }
                else if (entity is EntityEnemy enemy)
                {
                    if (!enemy.IsAlive()) continue;
                    if (Vector3.Distance(player.position, enemy.position) < enemyDetectRange)
                    { enemyCount++; enemiesNearby++; }
                    if (enemy.isAlert)
                    {
                        if (enemy.GetAttackTarget() == player)
                        {
                            lastAggroTime = Time.realtimeSinceStartup;
                            enemyAggroCount++;
                        }
                        enemyAlertCount++;
                    }
                }
            }

            float combatCooldownState = Time.realtimeSinceStartup - lastAggroTime;

            if (enemyAggroCount >= criticalThreshold)
            {
                combatState = "High";
                ActiveContexts.Add("Combat_High");
            }
            else if (enemyAggroCount >= dangerThreshold)
            {
                if (combatState != "High") combatState = "Low";
                ActiveContexts.Add(combatState == "High" ? "Combat_High" : "Combat_Low");
            }
            else if (combatCooldownState <= combatCoolDown && enemyAlertCount > 0)
                ActiveContexts.Add(combatState == "High" ? "Combat_High" : "Combat_Low");

            if (enemiesNearby >= criticalThreshold || enemyAlertCount >= criticalThreshold)
            {
                ActiveContexts.Add("Tension_High");
            }
            else if (enemiesNearby >= dangerThreshold || enemyAlertCount >= dangerThreshold)
            {
                ActiveContexts.Add("Tension_Low");
            }

            var localPlayer = player as EntityPlayerLocal;

            if (localPlayer?.persistentPlayerData != null && localPlayer.persistentPlayerData.HasBedrollPos)
            {
                Vector3i bedrollPosMine = localPlayer.persistentPlayerData.BedrollPos;
                if (Vector3.Distance(player.position, bedrollPosMine) < HomeDetectRange)
                {
                    ActiveContexts.Add(isDay ? "Home_Day" : "Home_Night");
                }
            }

            if (localPlayer?.persistentPlayerData != null)
            {
                List<Vector3i> claims = localPlayer.persistentPlayerData.GetLandProtectionBlocks();
                if (claims.Count > 0)
                {
                    Vector3i nearest = claims
                        .OrderBy(c => Vector3.Distance(player.position, c.ToVector3()))
                        .First();
                    float dist = Vector3.Distance(player.position, nearest.ToVector3());
                    if (dist < HomeDetectRange)
                    {
                        ActiveContexts.Add(isDay ? "Home_Day" : "Home_Night");
                    }
                }
            }

                BiomeDefinition biome = world.GetBiome((int)player.position.x, (int)player.position.z);

            string biomeName = biome?.m_sBiomeName;
            //Log.Error($"[CMM] Current Boimeis: {biomeName}");
            switch (biomeName)
            {
                case "pine_forest":
                    ActiveContexts.Add("Ambient_Forest"); break;
                case "desert":
                    ActiveContexts.Add("Ambient_Desert"); break;
                case "burnt_forest":
                    ActiveContexts.Add("Ambient_Burnt_Forest"); break;
                case "snow":
                    ActiveContexts.Add("Ambient_Snow"); break;
                case "wasteland":
                    ActiveContexts.Add("Ambient_Wasteland"); break;
                default:
                    ActiveContexts.Add("Ambient"); break;
            }
            return ActiveContexts;
        }
    }

    public class Initialize : IModApi
    {
        public void InitMod(Mod __instance)
        {
            var harmony = new Harmony("com.velasha.contextmusicmanager");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            ContextMusicManager.Init(__instance.Path);
        }
    }

    public class CoRoutine : MonoBehaviour { }

    public class ContextMusicManager : MonoBehaviour
    {
        // ── Configuration ────────────────────────────────────────────────────────
        public static TrackOrderMode GlobalTrackOrder = TrackOrderMode.RandomNoRepeat;
        public static float ContextScanCooldown = 0.5f;
        public static float StalenessCheckInterval = 30f;
        public static float NextCacheWindow = 5f;
        public static float TrackEndPollInterval = 0.25f;
        public static float CrossFadeDuration = 2f;
        public static float TrackTransitionFadeDuration = 6f; // the fade between when playing the next track in the same context
        public static float TrackTransitionPreloadLead = 10f; // start loading this many seconds before end

        // ── Static state ─────────────────────────────────────────────────────────
        public static List<Contexts> contexts = new List<Contexts>();
        public static List<ActiveContext> _contextStack = new List<ActiveContext>();
        public static Dictionary<string, ContextCache> cacheMap = new Dictionary<string, ContextCache>();
        public static string MusicDirectory = "/Music";
        public static bool inGame = false;
        public static bool inInventory = false;
        public static Contexts currentContext;
        public static string ModPath;

        // FIX: concurrency sentinel — set immediately when a context change is
        // accepted so that a second rapid call sees it before currentContext updates.
        public static string _pendingContext = null;

        private static string _playOnceReturnContext = null;

        // Coroutine handles
        private static Coroutine _activeMusicCoroutine = null;
        private static Coroutine _nextCacheCoroutine = null;
        private static Coroutine _trackEndCoroutine = null;
        private static Coroutine _crossFadeCoroutine = null;
        private static Coroutine _cooldownCoroutine = null;
        private static AudioClip _clipToDestroyAfterFade = null; // Particularly when caching is disable to avoid hard cuts

        public static bool lastContextOnCoolDown = false;

        // Audio — dual channel for crossfade
        private static AudioSource CMM_Primary_Channel;
        private static AudioSource CMM_Secondary_Channel;
        private static GameObject CMM_Audio_Object;
        public static CoRoutine CMM_CoRoutines;
        public static ContextMusicManager Instance;

        // Track which AudioSource is the live one
        private static bool _primaryIsActive = true;
        private static AudioSource ActiveChannel => _primaryIsActive ? CMM_Primary_Channel : CMM_Secondary_Channel;
        private static AudioSource InactiveChannel => _primaryIsActive ? CMM_Secondary_Channel : CMM_Primary_Channel;

        // ── Context definitions ───────────────────────────────────────────────────
        public void InitContexts()
        {
            contexts.Add(new Contexts("Blood_Moon", "blood_moon", "Combat_High", true, 30, false, 5, 5, true, true, 99, false));
            contexts.Add(new Contexts("Vehicle", "vehicle", "Ambient", true, 30, false, 5, 3, true, true, 99, false));
            contexts.Add(new Contexts("Vehicle_Gyrocopter", "vehicle/gyrocopter", "Vehicle", true, 30, false, 5, 3, true, true, 99, false));
            contexts.Add(new Contexts("Vehicle_Bicycle", "vehicle/bicycle", "Vehicle", true, 30, false, 5, 3, true, true, 99, false));
            contexts.Add(new Contexts("Vehicle_Minibike", "vehicle/minibike", "Vehicle", true, 30, false, 5, 3, true, true, 99, false));
            contexts.Add(new Contexts("Vehicle_Motorcycle", "vehicle/motorcycle", "Vehicle", true, 30, false, 5, 3, true, true, 99, false));
            contexts.Add(new Contexts("Vehicle_4x4", "vehicle/4x4", "Vehicle", true, 30, false, 5, 3, true, true, 99, false));
            contexts.Add(new Contexts("Combat_High", "combat_high", "Combat_Low", true, 20, false, 3, 3, true, true, 97, false));
            contexts.Add(new Contexts("Combat_Low", "combat_low", "Tension_Low", true, 20, false, 3, 3, true, true, 95, false));
            contexts.Add(new Contexts("Death", "death", "Ambient", false, 0, false, 3, 1, true, true, 91, true));
            contexts.Add(new Contexts("Spawn", "stinger", "Ambient", false, 0, false, 3, 1, true, true, 90, true));
            contexts.Add(new Contexts("Inventory", "inventory", "Ambient", true, 15, false, 0, 2, true, true, 88, false));
            contexts.Add(new Contexts("Tension_High", "tension_high", "Tension_Low", true, 30, false, 5, 5, false, true, 87, false));
            contexts.Add(new Contexts("Tension_Low", "tension_low", "Ambient", true, 30, false, 5, 5, false, true, 85, false));
            contexts.Add(new Contexts("Trader_Joel", "trader/joel", "Trader", true, 120, false, 5, 5, false, true, 81, false));
            contexts.Add(new Contexts("Trader_Jen", "trader/jen", "Trader", true, 120, false, 5, 5, false, true, 81, false));
            contexts.Add(new Contexts("Trader_Rekt", "trader/rekt", "Trader", true, 120, false, 5, 5, false, true, 81, false));
            contexts.Add(new Contexts("Trader_Bob", "trader/bob", "Trader", true, 120, false, 5, 5, false, true, 81, false));
            contexts.Add(new Contexts("Trader_Hugh", "trader/hugh", "Trader", true, 120, false, 5, 5, false, true, 81, false));
            contexts.Add(new Contexts("Trader", "trader", "Ambient", true, 120, false, 5, 5, false, true, 81, false));
            contexts.Add(new Contexts("Home_Day", "home/day", "Home", true, 120, false, 5, 5, true, true, 51, false));
            contexts.Add(new Contexts("Home_Night", "home/night", "Home", true, 120, false, 5, 5, true, true, 51, false));
            contexts.Add(new Contexts("Home", "home", "Ambient", true, 120, false, 5, 5, true, true, 51, false));
            contexts.Add(new Contexts("Ambient", "ambient", "None", true, 120, false, 5, 4, true, true, 50, false));
            contexts.Add(new Contexts("Ambient_Forest", "ambient/forest", "Ambient", true, 120, false, 5, 5, true, true, 50, false));
            contexts.Add(new Contexts("Ambient_Burnt_Forest", "ambient/burnt_forest", "Ambient", true, 120, false, 5, 5, true, true, 50, false));
            contexts.Add(new Contexts("Ambient_Desert", "ambient/desert", "Ambient", true, 120, false, 5, 5, true, true, 50, false));
            contexts.Add(new Contexts("Ambient_Snow", "ambient/snow", "Ambient", true, 120, false, 5, 5, true, true, 50, false));
            contexts.Add(new Contexts("Ambient_Wasteland", "ambient/wasteland", "Ambient", true, 120, false, 5, 5, true, true, 50, false));
            contexts.Add(new Contexts("None", "", "None", false, 0, false, 0, 0, true, true, 0, false));
            contexts.Add(new Contexts("Menu", "menu", "Ambient", false, 0, true, 5, 2, true, true, 0, false));
        }

        // ── Initialisation ────────────────────────────────────────────────────────
        public static void Init(string modPath)
        {
            ModPath = modPath;
            CMM_Audio_Object = new GameObject("ContextMusicManager");
            UnityEngine.Object.DontDestroyOnLoad(CMM_Audio_Object);
            Instance = CMM_Audio_Object.AddComponent<ContextMusicManager>();
            Instance.InitContexts();

            foreach (var ctx in contexts)
                cacheMap[ctx.Context] = new ContextCache();

            currentContext = GetContext("None");
            _pendingContext = "None";

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
            Log.Out("[CMM] Initialized!");
        }

        private static string GetTopContext()
        {
            foreach (ActiveContext active in _contextStack)
            {
                if (ContextHasFiles(active.ContextName))
                    return active.ContextName;
            }
            return "Ambient";
        }

        public static void PushContext(string contextName)
        {
            // Check if context already in stack
            if (_contextStack.All(c => c.ContextName != contextName))
            {
                Contexts ctx = GetContext(contextName);
                if (ctx == null) return;

                // If there are no files for this context or its fallbacks, don't push it
                if (!ContextHasFiles(contextName)) return;

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
        public static void OnEnterGame()
        {
            if (!inGame) return;
            CMM_CoRoutines.StartCoroutine(Instance.PreloadAllContexts());
        }

        // ─────────────────────────────────────────────────────────────────────────
        // PRELOADING
        // ─────────────────────────────────────────────────────────────────────────

        private IEnumerator PreloadAllContexts()
        {
            if (!CMMConfig.PreloadCacheEnabled)
            {
                Log.Out("[CMM] Preload cache disabled — skipping.");
                yield break;
            }
            Log.Out("[CMM] Starting sequential preload for all contexts...");
            foreach (var ctx in contexts)
            {
                if (ctx.Context == "None") continue;
                yield return StartCoroutine(PreloadContext(ctx.Context));
            }
            Log.Out("[CMM] Preload complete.");
        }

        private IEnumerator PreloadContext(string contextName)
        {
            ContextCache cache = cacheMap[contextName];
            string filePath;
            if (cache.lastPlayedFile != null && File.Exists(cache.lastPlayedFile))
                filePath = cache.lastPlayedFile;
            else
            {
                cache.resumePosition = 0f;
                cache.lastPlayedFile = null;
                filePath = PickNextFile(contextName, avoidFile: null);
            }

            if (filePath == null)
            {
                Contexts ctx = GetContext(contextName);
                string fallbackName = ctx?.Fallback ?? "None";
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
            if (audioType == AudioType.UNKNOWN) yield break;

            using (var request = UnityWebRequestMultimedia.GetAudioClip(
                       "file:///" + filePath.Replace("\\", "/"), audioType))
            {
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Log.Error($"[CMM] Preload failed for {contextName}: {request.error}");
                    yield break;
                }
                if (cache.cachedClip != null) Destroy(cache.cachedClip);
                cache.cachedClip = DownloadHandlerAudioClip.GetContent(request);
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

                List<string> activeContexts = ContextResolver.ResolveContext();

                foreach (string ctxName in activeContexts)
                {
                    if (_contextStack.All(c => c.ContextName != ctxName))
                    {
                        Contexts ctx = GetContext(ctxName);
                        if (ctx == null) continue;

                        // Skip contexts with no music files — they're opted out
                        if (!ContextHasFiles(ctxName)) continue;

                        _contextStack.Add(new ActiveContext
                        {
                            ContextName = ctxName,
                            Priority = ctx.Priority,
                            ActivatedAt = Time.realtimeSinceStartup,
                            Manual = false
                        });
                    }
                }

                // REPLACE the old RemoveAll with this:
                _contextStack.RemoveAll(c => !c.Manual && !activeContexts.Contains(c.ContextName));

                _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));

                string topContext = GetTopContext();
                if (topContext != currentContext.Context)
                {
                    // If a PlayOnce is playing, only allow interruption from something
                    // with strictly higher priority
                    if (currentContext != null && currentContext.PlayOnce)
                    {
                        Contexts top = GetContext(topContext);
                        if (top == null || top.Priority <= currentContext.Priority)
                            continue;
                    }

                    SendContextUpdate(topContext, 0);
                }
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
                Log.Out($"[CMM] Cache state: {string.Join(", ", cacheMap.Select(kv => $"{kv.Key}={(kv.Value.cachedClip != null ? $"{kv.Value.cachedClip.length:F0}s" : "null")}(next={(kv.Value.nextCachedClip != null ? "ready" : "null")})"))}");

                // No preload cache to refresh when caching is disabled.
                if (!CMMConfig.PreloadCacheEnabled) continue;

                float now = Time.realtimeSinceStartup;
                foreach (var ctx in contexts)
                {
                    if (ctx.Context == currentContext.Context) continue;
                    if (!ctx.CanBeResumed || ctx.TimeUntilContextStale <= 0) continue;

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

            // FIX: check _pendingContext (not just currentContext.Context) so that
            // a second rapid call sees a change that is already in flight.
            if (_pendingContext == contextIn) return;

            bool menuOverride = !inGame;
            int comparePriority = bypassPriority >= 0 ? bypassPriority : (currentContext?.Priority ?? 0);
            bool priorityOk = inGame && incoming.CanInterrupt && incoming.Priority > comparePriority;

            if (!menuOverride && !priorityOk)
            {
                Log.Out($"[CMM] {contextIn} rejected — not higher priority than {currentContext?.Context}");
                return;
            }

            Log.Out($"[CMM] Context accepted: {currentContext?.Context} -> {contextIn}");

            // FIX: claim the pending slot immediately so concurrent calls are gated
            _pendingContext = contextIn;

            // Save resume data for the context we're leaving
            if (currentContext != null && currentContext.CanBeResumed && ActiveChannel.isPlaying)
            {
                ContextCache leavingCache = cacheMap[currentContext.Context];
                leavingCache.resumePosition = ActiveChannel.time;
                leavingCache.lastLeftTime = Time.realtimeSinceStartup;
                Log.Out($"[CMM] Saved resume for {currentContext.Context}: {leavingCache.resumePosition:F2}s");

                // With preload disabled the clip was loaded on demand and should not
                // persist in memory after we leave — only the position is kept.
                if (!CMMConfig.PreloadCacheEnabled && leavingCache.cachedClip != null)
                {
                    _clipToDestroyAfterFade = leavingCache.cachedClip;
                    leavingCache.cachedClip = null;
                }
            }

            // Handle PlayOnce return-to snapshot
            if (incoming.PlayOnce)
            {
                _playOnceReturnContext = GetTopContext();
                Log.Out($"[CMM] PlayOnce {contextIn} returns to: {_playOnceReturnContext}");
            }

            // If we're interrupting a PlayOnce, discard its return target so it
            // doesn't fire after the interrupting context ends
            if (currentContext != null && currentContext.PlayOnce && !incoming.PlayOnce)
            {
                _playOnceReturnContext = null;
            }

            // Walk fallback chain to find a playable context.
            // With preload on:  look for a loaded cachedClip.
            // With preload off: look for a context directory that has files on disk —
            //                   PlayFromCache will handle the actual load on demand.
            string contextToPlay = contextIn;
            for (int i = 0; i < 5; i++)
            {
                ContextCache cache = cacheMap[contextToPlay];
                bool isPlayable = CMMConfig.PreloadCacheEnabled
                    ? cache.cachedClip != null
                    : ContextHasFiles(contextToPlay);

                if (isPlayable)
                {
                    Contexts candidate = GetContext(contextToPlay);

                    if (lastContextOnCoolDown && !candidate.BypassesCooldown && bypassPriority < 0)
                    {
                        Log.Out($"[CMM] {candidate.Context} blocked — cooldown active");
                        _pendingContext = currentContext?.Context;

                        // Discard any on-demand loaded clip we're not going to play.
                        if (!CMMConfig.PreloadCacheEnabled)
                        {
                            ContextCache blocked = cacheMap[currentContext.Context];
                            if (blocked.cachedClip != null) { Destroy(blocked.cachedClip); blocked.cachedClip = null; }
                        }
                        return;
                    }

                    currentContext = candidate;

                    TriggerMusic(contextToPlay);
                    return;
                }

                Contexts fallbackCtx = GetContext(GetContext(contextToPlay)?.Fallback ?? "None");
                if (fallbackCtx == null || fallbackCtx.Context == "None" || fallbackCtx.Context == contextToPlay)
                {
                    Log.Out($"[CMM] No playable clip for {contextToPlay} or its fallbacks — silence.");
                    currentContext = GetContext(contextToPlay);
                    _pendingContext = contextToPlay;
                    return;
                }

                Log.Out($"[CMM] No clip for {contextToPlay}, falling back to {fallbackCtx.Context}");
                contextToPlay = fallbackCtx.Context;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // TRIGGER MUSIC
        // ─────────────────────────────────────────────────────────────────────────

        public static void TriggerMusic(string contextName)
        {
            Log.Out($"[CMM] TriggerMusic: {contextName}");

            // Stop all in-flight coroutines
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }
            if (_nextCacheCoroutine != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine); _nextCacheCoroutine = null; }
            if (_trackEndCoroutine != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine); _trackEndCoroutine = null; }
            if (_crossFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine); _crossFadeCoroutine = null; }
            // Note: cooldown coroutine is NOT stopped here — let it finish naturally.

            _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(Instance.PlayFromCache(contextName, false));
        }

        // ─────────────────────────────────────────────────────────────────────────
        // PLAY FROM CACHE
        // ─────────────────────────────────────────────────────────────────────────

        private IEnumerator PlayFromCache(string contextName, bool isSameContextSwitchingToNextTrack)
        {
            Contexts ctx = GetContext(contextName);
            ContextCache cache = cacheMap[contextName];

            // At the top of PlayFromCache, before the clip acquisition block:
            if (_crossFadeCoroutine != null)
            {
                CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);
                _crossFadeCoroutine = null;
                // Snap outgoing channel to silence so the new fade starts clean
                InactiveChannel.volume = 0f;
            }
            // ── Clip acquisition ─────────────────────────────────────────────────
            if (CMMConfig.PreloadCacheEnabled)
            {
                // Normal path: wait for preloaded clip (up to 10s on first boot).
                float waited = 0f;
                while (cache.cachedClip == null && waited < 10f)
                {
                    yield return new WaitForSeconds(0.25f);
                    waited += 0.25f;
                }

                if (cache.cachedClip == null)
                {
                    Log.Out($"[CMM] PlayFromCache: no clip for {contextName} after wait. Giving up.");
                    _activeMusicCoroutine = null;
                    yield break;
                }
            }
            else
            {
                // On-demand path: load right now, skipping the preload cache entirely.
                if (cache.cachedClip == null)
                {
                    Log.Out($"[CMM] On-demand load for {contextName}...");
                    yield return StartCoroutine(PreloadContext(contextName));
                }

                if (cache.cachedClip == null)
                {
                    Log.Out($"[CMM] On-demand load failed for {contextName}. Giving up.");
                    _activeMusicCoroutine = null;
                    yield break;
                }
            }

            // ── Resume position ──────────────────────────────────────────────────
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
                    Log.Out($"[CMM] Resume stale for {contextName}, starting fresh.");
                }
            }

            // ── Channel setup ────────────────────────────────────────────────────
            AudioSource incoming = InactiveChannel;
            AudioSource outgoing = ActiveChannel;
            float outgoingStartVol = outgoing.volume; // capture before any yield

            incoming.clip = cache.cachedClip;
            incoming.loop = ctx.Loop;
            incoming.time = Mathf.Clamp(startPosition, 0f, cache.cachedClip.length - 0.01f);
            incoming.volume = 0f;
            incoming.Play();

            _primaryIsActive = !_primaryIsActive;
            AudioSource watchTarget = incoming;

            // ── Crossfade or hard cut ────────────────────────────────────────────
            float targetVolume = GamePrefs.GetFloat(
                inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);

            if (CMMConfig.CrossfadeEnabled)
            {
                float fadeDuration;
                if (ctx.PlayOnce)
                {
                    fadeDuration = 0.25f;
                }
                else if (isSameContextSwitchingToNextTrack)
                {
                    // Clamp transition fade to half the clip so it doesn't consume the whole track
                    float clipLen = cache.cachedClip.length > 0f ? cache.cachedClip.length : CrossFadeDuration;
                    fadeDuration = Mathf.Clamp(TrackTransitionFadeDuration, 0.25f, clipLen * 0.5f);
                }
                else
                {
                    fadeDuration = GetContext(contextName).WarmUp;
                }

                _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(
                    CrossFade(outgoing, incoming, fadeDuration, currentContext.Cooldown, outgoingStartVol));
            }
            else
            {
                // Hard cut: stop outgoing immediately, bring incoming straight to volume.
                outgoing.Stop();
                // At the end of CrossFade, after outgoing.Stop() and before the cooldown:
                outgoing.clip = null;
                outgoing.volume = 0f;
                // Clean up any deferred clip from SendContextUpdate
                if (_clipToDestroyAfterFade != null)
                {
                    Destroy(_clipToDestroyAfterFade);
                    _clipToDestroyAfterFade = null;
                }
                incoming.volume = targetVolume;

                // Cooldown still applies — start it directly since CrossFade won't.
                int cooldown = currentContext.Cooldown;
                if (cooldown > 0)
                {
                    if (_cooldownCoroutine != null) CMM_CoRoutines.StopCoroutine(_cooldownCoroutine);
                    _cooldownCoroutine = CMM_CoRoutines.StartCoroutine(ContextCooldownTimer(cooldown));
                }
            }

            Log.Out($"[CMM] Playing: {contextName}");

            // ── Track-end watcher ────────────────────────────────────────────────
            if (!ctx.Loop)
            {
                _trackEndCoroutine = CMM_CoRoutines.StartCoroutine(
                    TrackEndWatcher(contextName, watchTarget));
            }

            // ── Schedule next-clip preload ───────────────────────────────────────
            // In PlayFromCache, pass remaining time rather than full clip length:
            if (!ctx.Loop && !ctx.PlayOnce)
            {
                float clipLength = cache.cachedClip.length > 0f
                    ? cache.cachedClip.length
                    : TrackTransitionFadeDuration + TrackTransitionPreloadLead + 1f;

                float remaining = clipLength - startPosition; // account for resume offset

                _nextCacheCoroutine = CMM_CoRoutines.StartCoroutine(
                    ScheduleNextCacheLoad(contextName, remaining));
            }

            _activeMusicCoroutine = null;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // CROSSFADE
        // FIX: cooldown starts AFTER the fade, not inside it.
        // ─────────────────────────────────────────────────────────────────────────

        private IEnumerator CrossFade(AudioSource outgoing, AudioSource incoming, float duration, int cooldownSeconds, float outgoingStartVol)
        {
            float targetVolume = GamePrefs.GetFloat(
                inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);

            float startOutVol = outgoingStartVol;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                incoming.volume = Mathf.Lerp(0f, targetVolume, t);
                outgoing.volume = Mathf.Lerp(startOutVol, 0f, t);
                yield return null;
            }

            incoming.volume = targetVolume;
            outgoing.Stop();
            outgoing.clip = null;
            outgoing.volume = 0f;

            // Add this:
            if (_clipToDestroyAfterFade != null) { Destroy(_clipToDestroyAfterFade); _clipToDestroyAfterFade = null; }

            // FIX: cooldown begins only after the fade is fully done
            if (cooldownSeconds > 0)
            {
                if (_cooldownCoroutine != null) CMM_CoRoutines.StopCoroutine(_cooldownCoroutine);
                _cooldownCoroutine = CMM_CoRoutines.StartCoroutine(ContextCooldownTimer(cooldownSeconds));
            }

            _crossFadeCoroutine = null;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // COOLDOWN TIMER
        // ─────────────────────────────────────────────────────────────────────────

        private IEnumerator ContextCooldownTimer(int seconds)
        {
            lastContextOnCoolDown = true;
            Log.Out($"[CMM] Cooldown started: {seconds}s");
            yield return new WaitForSeconds(seconds);
            lastContextOnCoolDown = false;
            Log.Out("[CMM] Cooldown expired.");
            _cooldownCoroutine = null;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // TRACK END WATCHER
        // FIX: takes a captured AudioSource instead of reading ActiveChannel.
        // ─────────────────────────────────────────────────────────────────────────

        private IEnumerator TrackEndWatcher(string contextName, AudioSource watchTarget)
        {
            float startTime = Time.realtimeSinceStartup;
            float minimumPlaySeconds = 2f;

            yield return new WaitForSeconds(1f);

            float notPlayingDuration = 0f;
            while (true)
            {
                yield return new WaitForSeconds(TrackEndPollInterval);

                if (watchTarget.isPlaying)
                {
                    notPlayingDuration = 0f;
                    continue;
                }

                if (Time.realtimeSinceStartup - startTime < minimumPlaySeconds)
                    continue;

                notPlayingDuration += TrackEndPollInterval;
                if (notPlayingDuration >= 0.5f)
                    break;
            }

            if (currentContext.Context != contextName)
            {
                Log.Out($"[CMM] TrackEndWatcher: context changed away from {contextName}, ignoring.");
                yield break;
            }

            Log.Out($"[CMM] Track ended: {contextName}");

            if (GetContext(contextName).PlayOnce)
            {
                string returnTo = _playOnceReturnContext ?? GetContext(contextName).Fallback;
                _playOnceReturnContext = null;
                Log.Out($"[CMM] PlayOnce finished, returning to: {returnTo}");
                SendContextUpdate(returnTo, 0);
            }
            else
            {
                ContextCache cache = cacheMap[contextName];

                // Clear resume position so the next track starts fresh
                cache.resumePosition = 0f;
                cache.lastLeftTime   = -1f;

                if (cache.nextCachedClip != null)
                {
                    // Pre-loaded clip is ready — promote it
                    Log.Out($"[CMM] Using pre-loaded next clip for {contextName}");
                    if (cache.cachedClip != null) Destroy(cache.cachedClip);
                    cache.cachedClip     = cache.nextCachedClip;
                    cache.nextCachedClip = null;
                }
                else
                {
                    // Not ready yet — load inline now (short clip or slow disk)
                    Log.Out($"[CMM] Next clip not pre-loaded for {contextName}, loading now");
                    if (cache.cachedClip != null) { Destroy(cache.cachedClip); cache.cachedClip = null; }
                    yield return StartCoroutine(PreloadContext(contextName));

                    if (cache.cachedClip == null)
                    {
                        Log.Out($"[CMM] Next-clip load failed for {contextName}. Giving up.");
                        yield break;
                    }
                }

                _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(PlayFromCache(contextName, true));
            }

            _trackEndCoroutine = null;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SCHEDULE NEXT CACHE LOAD
        // ─────────────────────────────────────────────────────────────────────────

        private IEnumerator ScheduleNextCacheLoad(string contextName, float clipLength)
        {
            // If the clip is shorter than the crossfade, we can't fade the full duration.
            // Clamp the fade to half the clip length minimum 0.25s, so something audible happens.
            float effectiveFade = Mathf.Clamp(TrackTransitionFadeDuration, 0.25f, clipLength * 0.5f);

            // Start loading the next clip [lead] seconds before the crossfade begins,
            // i.e. [clipLength - effectiveFade - lead] seconds from now.
            // If the clip is too short for a lead, start loading immediately.
            float waitBeforeLoad = Mathf.Max(0f, clipLength - effectiveFade - TrackTransitionPreloadLead);

            Log.Out($"[CMM] {contextName}: clip={clipLength:F1}s fade={effectiveFade:F1}s loadIn={waitBeforeLoad:F1}s");

            yield return new WaitForSeconds(waitBeforeLoad);
            if (currentContext.Context != contextName) yield break;

            ContextCache cache = cacheMap[contextName];
            if (cache.nextClipLoading || cache.nextCachedClip != null) yield break;

            cache.nextClipLoading = true;
            Log.Out($"[CMM] Pre-loading next clip for {contextName}");
            yield return StartCoroutine(LoadNextClip(contextName));
            cache.nextClipLoading = false;
            _nextCacheCoroutine = null;
        }

        private IEnumerator LoadNextClip(string contextName)
        {
            ContextCache cache = cacheMap[contextName];
            string avoid = GlobalTrackOrder == TrackOrderMode.RandomNoRepeat ? cache.lastPlayedFile : null;
            string filePath = PickNextFile(contextName, avoidFile: avoid);

            if (filePath == null) { Log.Out($"[CMM] LoadNextClip: no files for {contextName}"); yield break; }

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
                if (cache.nextCachedClip != null) Destroy(cache.nextCachedClip);
                cache.nextCachedClip = DownloadHandlerAudioClip.GetContent(request);
                cache.lastPlayedFile = filePath;
                Log.Out($"[CMM] Next clip ready for {contextName}: {Path.GetFileName(filePath)}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // FILE SELECTION
        // ─────────────────────────────────────────────────────────────────────────

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
            cacheMap[contextName].directorySnapshot = files;

            switch (GlobalTrackOrder)
            {
                case TrackOrderMode.Sequential:
                    {
                        ContextCache cache = cacheMap[contextName];
                        if (cache.sequentialIndex >= files.Length) cache.sequentialIndex = 0;
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
                default:
                    return files[UnityEngine.Random.Range(0, files.Length)];
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // UTILITIES
        // ─────────────────────────────────────────────────────────────────────────

        public static Contexts GetContext(string contextIn) =>
            contexts.Find(c => c.Context == contextIn);

        // Returns true if the context's directory contains at least one playable file.
        // Used by SendContextUpdate when preload is disabled so the fallback chain
        // doesn't skip every context just because cachedClip is null.
        private static bool ContextHasFiles(string contextName)
        {
            Contexts ctx = GetContext(contextName);
            if (ctx == null || string.IsNullOrEmpty(ctx.Path)) return false;
            string dir = ModPath + MusicDirectory + "/" + ctx.Path + "/";
            if (!Directory.Exists(dir)) return false;
            return Directory.GetFiles(dir).Any(f => CheckType(f) != AudioType.UNKNOWN);
        }

        public static AudioType CheckType(string inputFile)
        {
            switch (Path.GetExtension(inputFile).ToLower())
            {
                case ".mp3": return AudioType.MPEG;
                case ".ogg": return AudioType.OGGVORBIS;
                case ".wav": return AudioType.WAV;
                default: return AudioType.UNKNOWN;
            }
        }

        // UpdateVolume applies to whichever channel is currently live.
        public static void UpdateVolume()
        {
            if (ActiveChannel == null) return;
            float vol = GamePrefs.GetFloat(
                inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);
            ActiveChannel.volume = vol;
        }
    }

    // ─── Music states ─────────────────────────────────────────────────────────────
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
            if (!firstMessageLogged) { Log.Out("[CMM] Dynamic Music System ticks ignored!"); firstMessageLogged = true; }

            if (!ContextMusicManager.inGame)
            {
                Log.Out("[CMM] Entering game world");
                ContextMusicManager.inGame = true;
                ContextMusicManager.OnEnterGame();
                // Volume ramp handled by the crossfade in SendContextUpdate
                //ContextMusicManager.SendContextUpdate("Ambient");
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(BackgroundMusicMono), "Play")]
    public class MenuMusicPatch
    {
        static bool Prefix()
        {
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
            ContextMusicManager.inInventory = false;
            ContextMusicManager.inGame = false;
            ContextMusicManager._contextStack.Clear();
            ContextMusicManager._pendingContext = "None"; // add this
            ContextResolver.resetValues();
            ContextMusicManager.SendContextUpdate("Menu");
        }
    }


    [HarmonyPatch(typeof(GameManager), "Awake")]
    public class StartupLogPatch
    {
        static void Postfix() => Log.Error("[CMM] Game started up!");
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
                    ContextMusicManager._contextStack.Clear(); // ensure clean slate
                    // Seed the stack with the current world context so there's something
                    // to return to after the stinger finishes, and so the scan loop
                    // doesn't start from an empty stack
                    List<string> worldContexts = ContextResolver.ResolveContext();
                    foreach (string ctxName in worldContexts)
                    {
                        Contexts ctx = ContextMusicManager.GetContext(ctxName);
                        if (ctx == null) continue;
                        ContextMusicManager._contextStack.Add(new ActiveContext
                        {
                            ContextName = ctxName,
                            Priority = ctx.Priority,
                            ActivatedAt = Time.realtimeSinceStartup,
                            Manual = false
                        });
                    }
                    ContextMusicManager._contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                    // Now fire the stinger — it reads GetTopContext() as its return target,
                    // which will now be the correct world context
                    ContextMusicManager.SendContextUpdate("Spawn");
                    return false;
                case "open_inventory":
                    ContextMusicManager.inInventory = true;
                    ContextMusicManager.PushContext("Inventory");
                    return true;
                case "close_inventory":
                    ContextMusicManager.inInventory = false;
                    ContextMusicManager.PopContext("Inventory");
                    return true;
                default:
                    return true;
            }
        }
    }

    [HarmonyPatch   (typeof(Audio.Manager), "PlayInsidePlayerHead",
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
