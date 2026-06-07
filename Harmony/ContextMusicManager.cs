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

    // ═════════════════════════════════════════════════════════════════════════
    // SECTION 1 — CONFIGURATION
    // Feature flags read per-call, never cached. Safe to change at runtime.
    // ═════════════════════════════════════════════════════════════════════════

    public static class CMMConfig
    {
        // When false, outgoing channel stops immediately instead of crossfading.
        public static bool CrossfadeEnabled = true;

        // When false, clips load on demand instead of being preloaded on game entry.
        public static bool PreloadCacheEnabled = false;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SECTION 2 — DATA STRUCTURES
    // Plain data containers. No logic lives here.
    // ═════════════════════════════════════════════════════════════════════════

    // Holds per-context audio state: cached clips, resume position, file list.
    public class ContextCache
    {
        public AudioClip cachedClip       = null;
        public AudioClip nextCachedClip   = null;
        public float     resumePosition   = 0f;
        public float     lastLeftTime     = -1f;
        public string    lastPlayedFile   = null;
        public int       sequentialIndex  = 0;
        public string[]  directorySnapshot = null;
        public bool      nextClipLoading  = false;
    }

    // A context that is currently active in the stack, with runtime metadata.
    public class ActiveContext
    {
        public string ContextName;
        public int    Priority;
        public float  ActivatedAt;
        public bool   Manual;
    }

    // Immutable definition of a music context loaded at startup.
    public class Contexts
    {
        public string Context              { get; private set; }
        public string Path                 { get; private set; }
        public string Fallback             { get; private set; }
        public bool   CanBeResumed         { get; private set; }
        public int    TimeUntilContextStale { get; private set; }
        public bool   Loop                 { get; private set; }
        public int    Cooldown             { get; private set; }
        public int    WarmUp               { get; private set; }
        public bool   BypassesCooldown     { get; private set; }
        public bool   CanInterrupt         { get; private set; }
        public int    Priority             { get; private set; }
        public bool   PlayOnce             { get; private set; }

        public Contexts(string context = "None", string path = "", string fallback = "None",
                        bool canberesumed = false, int timeuntilcontextstale = 120,
                        bool loop = false, int cooldown = 5, int warmup = 3,
                        bool bypassescooldown = false, bool caninterrupt = false,
                        int priority = 0, bool playOnce = false)
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

    // ═════════════════════════════════════════════════════════════════════════
    // SECTION 3 — CONTEXT RESOLVER
    // Reads game world state each tick and returns the list of contexts that
    // are currently active (combat, biome, POI, vehicle, etc).
    // Called by ContextScanLoop every 0.5s.
    // ═════════════════════════════════════════════════════════════════════════

    public static class ContextResolver
    {
        static readonly float enemyDetectRange  = 10f;
        static readonly int   dangerThreshold   = 1;
        static readonly int   criticalThreshold = 3;
        static readonly float TraderDetectRange = 40f;
        static readonly float HomeDetectRange   = 70f;

        static string combatState   = "None";
        static float  combatCoolDown = 10f;
        static float  lastAggroTime  = 0f;
        static bool   wasInfested   = false;
        static string lastPoI       = "None";
        static int    tickTracker   = 0;

        public static bool isDay = false;

        public static void resetValues()
        {
            combatState = "None";
        }

        public static List<string> ResolveContext()
        {
            isDay = false;
            int enemyAggroCount = 0, enemyAlertCount = 0, enemyCount = 0, enemiesNearby = 0;
            var world  = GameManager.Instance?.World;
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

            // ── Blood moon ───────────────────────────────────────────────────
            if (GameManager.Instance.World.isEventBloodMoon)
                ActiveContexts.Add("Blood_Moon");

            // ── Vehicle ──────────────────────────────────────────────────────
            if (player.AttachedToEntity is EntityVehicle vehicle)
            {
                string vn = vehicle.GetType().Name.ToLower();
                ActiveContexts.Add("Vehicle");
                if      (vn.Contains("gyrocopter"))  ActiveContexts.Add("Vehicle_Gyrocopter");
                else if (vn.Contains("bicycle"))     ActiveContexts.Add("Vehicle_Bicycle");
                else if (vn.Contains("motorcycle"))  ActiveContexts.Add("Vehicle_Motorcycle");
                else if (vn.Contains("minibike"))    ActiveContexts.Add("Vehicle_Minibike");
                else if (vn.Contains("jeep"))        ActiveContexts.Add("Vehicle_4x4");
            }

            // ── Traders and enemies ──────────────────────────────────────────
            foreach (var entity in entities)
            {
                if (entity is EntityTrader trader)
                {
                    if (Vector3.Distance(player.position, trader.position) > TraderDetectRange) continue;
                    string n = trader.EntityName?.ToLower() ?? "";
                    if      (n.Contains("jen"))  ActiveContexts.Add("Trader_Jen");
                    else if (n.Contains("rekt")) ActiveContexts.Add("Trader_Rekt");
                    else if (n.Contains("bob"))  ActiveContexts.Add("Trader_Bob");
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
                        { lastAggroTime = Time.realtimeSinceStartup; enemyAggroCount++; }
                        enemyAlertCount++;
                    }
                }
            }

            // ── Combat state ─────────────────────────────────────────────────
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
            else if (combatCooldownState > combatCoolDown && enemyAlertCount == 0)
                combatState = "None";

            // ── Tension ──────────────────────────────────────────────────────
            if      (enemiesNearby >= criticalThreshold || enemyAlertCount >= criticalThreshold)
                ActiveContexts.Add("Tension_High");
            else if (enemiesNearby >= dangerThreshold  || enemyAlertCount >= dangerThreshold)
                ActiveContexts.Add("Tension_Low");

            // ── Home (bedroll or land claim) ──────────────────────────────────
            var localPlayer = player as EntityPlayerLocal;
            if (localPlayer?.persistentPlayerData != null)
            {
                if (localPlayer.persistentPlayerData.HasBedrollPos)
                {
                    Vector3i bedPos = localPlayer.persistentPlayerData.BedrollPos;
                    if (Vector3.Distance(player.position, bedPos) < HomeDetectRange)
                        ActiveContexts.Add(isDay ? "Home_Day" : "Home_Night");
                }

                List<Vector3i> claims = localPlayer.persistentPlayerData.GetLandProtectionBlocks();
                if (claims.Count > 0)
                {
                    Vector3i nearest = claims.OrderBy(c => Vector3.Distance(player.position, c.ToVector3())).First();
                    if (Vector3.Distance(player.position, nearest.ToVector3()) < HomeDetectRange)
                        ActiveContexts.Add(isDay ? "Home_Day" : "Home_Night");
                }
            }

            // ── POI ───────────────────────────────────────────────────────────
            PrefabInstance prefab = GameManager.Instance.World.GetPOIAtPosition(player.position);
            if (prefab != null)
            {
                string currentPoI          = prefab.prefab.PrefabName;
                int    enemiesRemainingInPoi = 0;

                foreach (SleeperVolume volume in prefab.sleeperVolumes)
                    if (!volume.wasCleared) enemiesRemainingInPoi++;

                if (enemiesRemainingInPoi != 0)
                {
                    ActiveContexts.Add("POI_Infested");
                    wasInfested = true;
                }
                else
                {
                    if (wasInfested && currentPoI == lastPoI)
                        ActiveContexts.Add("POI_Cleared");
                    wasInfested = false;
                }

                lastPoI = currentPoI;

                switch (prefab.prefab.DifficultyTier)
                {
                    case 1: ActiveContexts.Add("POI_T1"); break;
                    case 2: ActiveContexts.Add("POI_T2"); break;
                    case 3: ActiveContexts.Add("POI_T3"); break;
                    case 4: ActiveContexts.Add("POI_T4"); break;
                    case 5: ActiveContexts.Add("POI_T5"); break;
                    default: ActiveContexts.Add("POI");   break;
                }
            }
            else
            {
                wasInfested = false;
            }

            // ── Biome ─────────────────────────────────────────────────────────
            BiomeDefinition biome    = world.GetBiome((int)player.position.x, (int)player.position.z);
            string          biomeName = biome?.m_sBiomeName;
            switch (biomeName)
            {
                case "pine_forest":   ActiveContexts.Add("Ambient_Forest");       break;
                case "desert":        ActiveContexts.Add("Ambient_Desert");       break;
                case "burnt_forest":  ActiveContexts.Add("Ambient_Burnt_Forest"); break;
                case "snow":          ActiveContexts.Add("Ambient_Snow");         break;
                case "wasteland":     ActiveContexts.Add("Ambient_Wasteland");    break;
                default:              ActiveContexts.Add("Ambient");              break;
            }

            return ActiveContexts;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // MOD ENTRY POINT
    // ═════════════════════════════════════════════════════════════════════════

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

    // ═════════════════════════════════════════════════════════════════════════
    // SECTION 4 — CONTEXT STACK
    // Maintains the ordered list of currently active contexts.
    // Answers the question: "what should be playing right now?"
    // ═════════════════════════════════════════════════════════════════════════

    public partial class ContextMusicManager : MonoBehaviour
    {
        // ── Context stack state ───────────────────────────────────────────────
        public static Dictionary<string, Contexts>    contexts     = new Dictionary<string, Contexts>();
        public static Dictionary<string, ContextCache> cacheMap    = new Dictionary<string, ContextCache>();
        public static List<ActiveContext>             _contextStack = new List<ActiveContext>();
        public static Contexts                        currentContext;

        // Sentinel set immediately when a context change is accepted, so rapid
        // re-entrant calls see it before currentContext is updated.
        public static string _pendingContext       = null;
        public static string _playOnceReturnContext = null;

        // ── Context definitions ───────────────────────────────────────────────
        // Helper so InitContexts stays readable — wraps the dictionary assignment.
        private static void AddContext(string name, string path, string fallback,
            bool canBeResumed, int staleTime, bool loop, int cooldown, int warmUp,
            bool bypassesCooldown, bool canInterrupt, int priority, bool playOnce)
        {
            contexts[name] = new Contexts(name, path, fallback, canBeResumed, staleTime,
                loop, cooldown, warmUp, bypassesCooldown, canInterrupt, priority, playOnce);
        }

        // Registers all known contexts. Add new contexts here.
        public void InitContexts()
        {
            AddContext("Low_Health_Panic",     "events/low_health",    "Ambient",      true,  30,  false, 5, 5, true,  true, 100, false);

            AddContext("Blood_Combat",         "blood_moon/combat",    "Combat_High",  true,  30,  false, 5, 5, true,  true,  95, false);
            AddContext("Blood_Moon",           "blood_moon",           "Combat_High",  true,  30,  false, 5, 5, true,  true,  90, false);

            AddContext("Death",                "death",                "Ambient",      false,  0,  false, 3, 1, true,  true,  71, true );

            AddContext("Vehicle_Gyrocopter",   "vehicle/gyrocopter",   "Vehicle",      true,  30,  false, 5, 3, true,  true,  61, false);
            AddContext("Vehicle_Bicycle",      "vehicle/bicycle",      "Vehicle",      true,  30,  false, 5, 3, true,  true,  61, false);
            AddContext("Vehicle_Minibike",     "vehicle/minibike",     "Vehicle",      true,  30,  false, 5, 3, true,  true,  61, false);
            AddContext("Vehicle_Motorcycle",   "vehicle/motorcycle",   "Vehicle",      true,  30,  false, 5, 3, true,  true,  61, false);
            AddContext("Vehicle_4x4",          "vehicle/4x4",          "Vehicle",      true,  30,  false, 5, 3, true,  true,  61, false);
            AddContext("Vehicle",              "vehicle",              "Ambient",      true,  30,  false, 5, 3, true,  true,  60, false);

            AddContext("POI_Cleared",          "poi/cleared",          "Ambient",      true,  60,  false, 5, 5, true,  true,  55, true );
            AddContext("Combat_High",          "combat_high",          "Combat_Low",   true,  20,  false, 3, 3, true,  true,  51, false);
            AddContext("Combat_Low",           "combat_low",           "Tension_Low",  true,  20,  false, 3, 3, true,  true,  50, false);

            AddContext("Spawn",                "stinger",              "Ambient",      false,  0,  false, 3, 1, true,  true,  45, true );
            AddContext("Inventory",            "inventory",            "Ambient",      true,  15,  false, 0, 2, true,  true,  40, false);

            AddContext("Tension_High",         "tension_high",         "Tension_Low",  true,  30,  false, 5, 5, false, true,  30, false);
            AddContext("Tension_Low",          "tension_low",          "Ambient",      true,  30,  false, 5, 5, false, true,  30, false);

            AddContext("Trader_Joel",          "trader/joel",          "Trader",       true, 120,  false, 5, 5, false, true,  26, false);
            AddContext("Trader_Jen",           "trader/jen",           "Trader",       true, 120,  false, 5, 5, false, true,  26, false);
            AddContext("Trader_Rekt",          "trader/rekt",          "Trader",       true, 120,  false, 5, 5, false, true,  26, false);
            AddContext("Trader_Bob",           "trader/bob",           "Trader",       true, 120,  false, 5, 5, false, true,  26, false);
            AddContext("Trader_Hugh",          "trader/hugh",          "Trader",       true, 120,  false, 5, 5, false, true,  26, false);
            AddContext("Trader",               "trader",               "Ambient",      true, 120,  false, 5, 5, false, true,  25, false);

            AddContext("Home_Day",             "home/day",             "Home",         true, 120,  false, 5, 5, true,  true,  20, false);
            AddContext("Home_Night",           "home/night",           "Home",         true, 120,  false, 5, 5, true,  true,  20, false);
            AddContext("Home",                 "home",                 "Ambient",      true, 120,  false, 5, 5, true,  true,  20, false);

            AddContext("POI_Infested",         "poi/infested",         "Tension_High", true,  60,  false, 5, 5, true,  true,  16, false);
            AddContext("POI_T1",               "poi/t1",               "POI",          true,  60,  false, 5, 5, true,  true,  15, false);
            AddContext("POI_T2",               "poi/t2",               "POI",          true,  60,  false, 5, 5, true,  true,  15, false);
            AddContext("POI_T3",               "poi/t3",               "POI",          true,  60,  false, 5, 5, true,  true,  15, false);
            AddContext("POI_T4",               "poi/t4",               "POI",          true,  60,  false, 5, 5, true,  true,  15, false);
            AddContext("POI_T5",               "poi/t5",               "POI",          true,  60,  false, 5, 5, true,  true,  15, false);
            AddContext("POI",                  "poi",                  "Ambient",      true,  60,  false, 5, 5, true,  true,  15, false);

            AddContext("Ambient",              "ambient",              "None",         true, 120,  false, 5, 4, true,  true,  10, false);
            AddContext("Ambient_Forest",       "ambient/forest",       "Ambient",      true, 120,  false, 5, 5, true,  true,  10, false);
            AddContext("Ambient_Burnt_Forest", "ambient/burnt_forest", "Ambient",      true, 120,  false, 5, 5, true,  true,  10, false);
            AddContext("Ambient_Desert",       "ambient/desert",       "Ambient",      true, 120,  false, 5, 5, true,  true,  10, false);
            AddContext("Ambient_Snow",         "ambient/snow",         "Ambient",      true, 120,  false, 5, 5, true,  true,  10, false);
            AddContext("Ambient_Wasteland",    "ambient/wasteland",    "Ambient",      true, 120,  false, 5, 5, true,  true,  10, false);

            AddContext("None",                 "",                     "None",         false,  0,  false, 0, 0, true,  true,   0, false);
            AddContext("Menu",                 "menu",                 "Ambient",      false,  0,  true,  5, 2, true,  true,   0, false);
        }

        // Looks up a context definition by name. Returns null if not found.
        public static Contexts GetContext(string contextIn) =>
            contexts.TryGetValue(contextIn, out var ctx) ? ctx : null;

        // Walks the fallback chain from contextName until it finds a context
        // whose folder contains at least one playable file. Returns null if
        // the entire chain is empty. Max depth 5 to prevent infinite loops.
        private static string ResolvePlayableContext(string contextName)
        {
            string current = contextName;
            for (int i = 0; i < 5; i++)
            {
                if (ContextHasFiles(current)) return current;
                Contexts ctx  = GetContext(current);
                string fallback = ctx?.Fallback ?? "None";
                if (fallback == "None" || fallback == current) return null;
                current = fallback;
            }
            return null;
        }

        // Returns the highest-priority context on the stack that has playable
        // files (directly or via fallback). Falls back to "Ambient" if empty.
        private static string GetTopContext()
        {
            foreach (ActiveContext active in _contextStack)
            {
                string playable = ResolvePlayableContext(active.ContextName);
                if (playable != null) return playable;
            }
            return "Ambient";
        }

        // Manually adds a context to the stack (e.g. Inventory open).
        // No-ops if already present. Triggers a context update after pushing.
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
                    Priority    = ctx.Priority,
                    ActivatedAt = Time.realtimeSinceStartup,
                    Manual      = true
                });
                _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
            SendContextUpdate(GetTopContext(), 0);
        }

        // Manually removes a context from the stack (e.g. Inventory closed).
        // Triggers a context update after popping.
        public static void PopContext(string contextName)
        {
            _contextStack.RemoveAll(c => c.ContextName == contextName);
            _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            SendContextUpdate(GetTopContext(), 0);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SECTION 5 — AUDIO PLAYER
    // Owns the two AudioSource channels and all playback logic.
    // Answers the question: "how do I play this context?"
    // ═════════════════════════════════════════════════════════════════════════

    public partial class ContextMusicManager
    {
        // ── Audio state ───────────────────────────────────────────────────────
        private static AudioSource CMM_Primary_Channel;
        private static AudioSource CMM_Secondary_Channel;

        // Tracks which channel is currently the live (audible) one.
        private static bool        _primaryIsActive = true;
        private static AudioSource ActiveChannel   => _primaryIsActive ? CMM_Primary_Channel : CMM_Secondary_Channel;
        private static AudioSource InactiveChannel => _primaryIsActive ? CMM_Secondary_Channel : CMM_Primary_Channel;

        // Clip queued for destruction after the crossfade finishes.
        // Used when preload is disabled to avoid destroying a clip mid-fade.
        private static AudioClip _clipToDestroyAfterFade = null;

        // ── Playback ──────────────────────────────────────────────────────────

        // Entry point for playing a context. Loads the clip on demand if needed,
        // sets up the incoming channel, and starts a crossfade from the outgoing one.
        // isSameContextSwitchingToNextTrack: true when a track ends naturally and
        // the next track in the same context starts — uses a longer fade.
        private IEnumerator PlayFromCache(string contextName, bool isSameContextSwitchingToNextTrack)
        {
            Contexts    ctx   = GetContext(contextName);
            ContextCache cache = cacheMap[contextName];

            // If a crossfade is already running, stop it and snap the outgoing
            // channel to silence so the new fade starts from a clean state.
            if (_crossFadeCoroutine != null)
            {
                CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);
                _crossFadeCoroutine    = null;
                InactiveChannel.volume = 0f;
            }

            // ── Clip acquisition ─────────────────────────────────────────────
            if (CMMConfig.PreloadCacheEnabled)
            {
                // Wait up to 10s for a preloaded clip (slow on first boot).
                float waited = 0f;
                while (cache.cachedClip == null && waited < 10f)
                { yield return new WaitForSeconds(0.25f); waited += 0.25f; }

                if (cache.cachedClip == null)
                {
                    Log.Out($"[CMM] PlayFromCache: no clip for {contextName} after wait.");
                    _activeMusicCoroutine = null;
                    yield break;
                }
            }
            else
            {
                // On-demand: load now if not already cached.
                if (cache.cachedClip == null)
                {
                    Log.Out($"[CMM] On-demand load for {contextName}...");
                    yield return StartCoroutine(PreloadContext(contextName));
                }
                if (cache.cachedClip == null)
                {
                    Log.Out($"[CMM] On-demand load failed for {contextName}.");
                    _activeMusicCoroutine = null;
                    yield break;
                }
            }

            // ── Resume position ──────────────────────────────────────────────
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

            // ── Channel setup ────────────────────────────────────────────────
            AudioSource incoming       = InactiveChannel;
            AudioSource outgoing       = ActiveChannel;
            float       outgoingStartVol = outgoing.volume;

            incoming.clip   = cache.cachedClip;
            incoming.loop   = ctx.Loop;
            incoming.time   = Mathf.Clamp(startPosition, 0f, cache.cachedClip.length - 0.01f);
            incoming.volume = 0f;
            incoming.Play();

            _primaryIsActive = !_primaryIsActive;
            AudioSource watchTarget = incoming;

            // ── Crossfade or hard cut ────────────────────────────────────────
            float targetVolume = GamePrefs.GetFloat(
                inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);

            if (CMMConfig.CrossfadeEnabled)
            {
                float fadeDuration;
                if (ctx.PlayOnce)
                    fadeDuration = 0.25f;
                else if (isSameContextSwitchingToNextTrack)
                {
                    float clipLen = cache.cachedClip.length > 0f ? cache.cachedClip.length : CrossFadeDuration;
                    fadeDuration = Mathf.Clamp(TrackTransitionFadeDuration, 0.25f, clipLen * 0.5f);
                }
                else
                    fadeDuration = ctx.WarmUp;

                _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(
                    CrossFade(outgoing, incoming, fadeDuration, currentContext.Cooldown, outgoingStartVol));
            }
            else
            {
                outgoing.Stop();
                outgoing.clip   = null;
                outgoing.volume = 0f;
                if (_clipToDestroyAfterFade != null) { Destroy(_clipToDestroyAfterFade); _clipToDestroyAfterFade = null; }
                incoming.volume = targetVolume;

                int cooldown = currentContext.Cooldown;
                if (cooldown > 0)
                {
                    if (_cooldownCoroutine != null) CMM_CoRoutines.StopCoroutine(_cooldownCoroutine);
                    _cooldownCoroutine = CMM_CoRoutines.StartCoroutine(ContextCooldownTimer(cooldown));
                }
            }

            Log.Out($"[CMM] Playing: {contextName}");

            // ── Track-end watcher ────────────────────────────────────────────
            if (!ctx.Loop)
                _trackEndCoroutine = CMM_CoRoutines.StartCoroutine(TrackEndWatcher(contextName, watchTarget));

            // ── Schedule next-clip preload ───────────────────────────────────
            if (!ctx.Loop && !ctx.PlayOnce)
            {
                float clipLength = cache.cachedClip.length > 0f
                    ? cache.cachedClip.length
                    : TrackTransitionFadeDuration + TrackTransitionPreloadLead + 1f;
                float remaining = clipLength - startPosition;
                _nextCacheCoroutine = CMM_CoRoutines.StartCoroutine(ScheduleNextCacheLoad(contextName, remaining));
            }

            _activeMusicCoroutine = null;
        }

        // Fades outgoing down and incoming up over [duration] seconds.
        // Starts the cooldown timer after the fade completes.
        private IEnumerator CrossFade(AudioSource outgoing, AudioSource incoming,
            float duration, int cooldownSeconds, float outgoingStartVol)
        {
            float targetVolume = GamePrefs.GetFloat(
                inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                incoming.volume = Mathf.Lerp(0f,               targetVolume, t);
                outgoing.volume = Mathf.Lerp(outgoingStartVol, 0f,           t);
                yield return null;
            }

            incoming.volume = targetVolume;
            outgoing.Stop();
            outgoing.clip   = null;
            outgoing.volume = 0f;

            if (_clipToDestroyAfterFade != null) { Destroy(_clipToDestroyAfterFade); _clipToDestroyAfterFade = null; }

            if (cooldownSeconds > 0)
            {
                if (_cooldownCoroutine != null) CMM_CoRoutines.StopCoroutine(_cooldownCoroutine);
                _cooldownCoroutine = CMM_CoRoutines.StartCoroutine(ContextCooldownTimer(cooldownSeconds));
            }

            _crossFadeCoroutine = null;
        }

        // Fades the active channel to silence with no incoming track.
        // Called when the context resolves to nothing playable.
        public static void FadeToSilence()
        {
            Log.Out("[CMM] Fading to silence.");
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }
            if (_nextCacheCoroutine  != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine);   _nextCacheCoroutine   = null; }
            if (_trackEndCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine);    _trackEndCoroutine    = null; }
            if (_crossFadeCoroutine  != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);   _crossFadeCoroutine   = null; }

            _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeOutCoroutine());
        }

        // Smoothly reduces the active channel to zero volume then stops it.
        // ── Fade out coroutine (updated to destroy pending clip) ─────────────────
        private IEnumerator FadeOutCoroutine()
        {
            AudioSource outgoing = ActiveChannel;
            float startVol = outgoing.volume;
            float elapsed = 0f;

            while (elapsed < CrossFadeDuration)
            {
                elapsed += Time.deltaTime;
                outgoing.volume = Mathf.Lerp(startVol, 0f, Mathf.Clamp01(elapsed / CrossFadeDuration));
                yield return null;
            }

            outgoing.Stop();
            outgoing.clip = null;
            outgoing.volume = 0f;

            if (_clipToDestroyAfterFade != null) { Destroy(_clipToDestroyAfterFade); _clipToDestroyAfterFade = null; }

            _crossFadeCoroutine = null;
        }

        // Polls the watch target AudioSource. When it stops playing, triggers
        // the next track (same context) or returns from a PlayOnce context.
        private IEnumerator TrackEndWatcher(string contextName, AudioSource watchTarget)
        {
            float startTime          = Time.realtimeSinceStartup;
            float minimumPlaySeconds = 2f;

            yield return new WaitForSeconds(1f);

            float notPlayingDuration = 0f;
            while (true)
            {
                yield return new WaitForSeconds(TrackEndPollInterval);

                if (currentContext.Context != contextName)
                {
                    Log.Out($"[CMM] TrackEndWatcher: context changed mid-watch for {contextName}, bailing.");
                    _trackEndCoroutine = null;
                    yield break;
                }

                if (watchTarget.isPlaying) { notPlayingDuration = 0f; continue; }
                if (Time.realtimeSinceStartup - startTime < minimumPlaySeconds) continue;

                notPlayingDuration += TrackEndPollInterval;
                if (notPlayingDuration >= 0.5f) break;
            }

            if (currentContext.Context != contextName)
            {
                Log.Out($"[CMM] TrackEndWatcher: context changed away from {contextName}, ignoring.");
                _trackEndCoroutine = null;
                yield break;
            }

            Log.Out($"[CMM] Track ended: {contextName}");
            _trackEndCoroutine = null;

            if (GetContext(contextName).PlayOnce)
            {
                string returnTo = _playOnceReturnContext ?? GetContext(contextName).Fallback;
                _playOnceReturnContext = null;
                Log.Out($"[CMM] PlayOnce finished, returning to: {returnTo}");
                SendContextUpdate(returnTo, 0);
                yield break;
            }

            // Same context, next track — only if ScheduleNextCacheLoad didn't already handle it
            if (currentContext.Context == contextName && !ActiveChannel.isPlaying)
            {
                Log.Out($"[CMM] TrackEndWatcher safety net firing for {contextName}");
                ContextCache cache = cacheMap[contextName];
                cache.resumePosition = 0f;
                cache.lastLeftTime = -1f;

                if (cache.cachedClip != null) { Destroy(cache.cachedClip); cache.cachedClip = null; }
                yield return StartCoroutine(PreloadContext(contextName));

                if (cache.cachedClip == null)
                {
                    Log.Out($"[CMM] Safety net load failed for {contextName}. Giving up.");
                    yield break;
                }

                _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(PlayFromCache(contextName, true));
            }
        }

        // Waits until [lead] seconds before the end of the current clip, then
        // loads the next clip into nextCachedClip so TrackEndWatcher can promote
        // it instantly when the track finishes.
        // Waits until [TrackTransitionFadeDuration] seconds before the end of the
        // current clip, then starts the next track playing on the inactive channel
        // and crossfades into it — while the current track is still audible.
        // ── Schedule next cache load (avoid destroying the currently playing clip) ─
        private IEnumerator ScheduleNextCacheLoad(string contextName, float clipLength)
        {
            float effectiveFade = Mathf.Clamp(TrackTransitionFadeDuration, 0.25f, clipLength * 0.5f);
            float waitBeforeLoad = Mathf.Max(0f, clipLength - effectiveFade - TrackTransitionPreloadLead);
            float waitBeforePlay = Mathf.Max(0f, clipLength - effectiveFade);

            Log.Out($"[CMM] {contextName}: clip={clipLength:F1}s fade={effectiveFade:F1}s loadIn={waitBeforeLoad:F1}s playIn={waitBeforePlay:F1}s");

            // ── Phase 1: load the next clip ahead of time ────────────────────────
            yield return new WaitForSeconds(waitBeforeLoad);
            if (currentContext.Context != contextName) yield break;

            ContextCache cache = cacheMap[contextName];
            if (!cache.nextClipLoading && cache.nextCachedClip == null)
            {
                cache.nextClipLoading = true;
                Log.Out($"[CMM] Pre-loading next clip for {contextName}");
                yield return StartCoroutine(LoadNextClip(contextName));
                cache.nextClipLoading = false;
            }

            // ── Phase 2: wait until it's time to start the crossfade ────────────
            float remaining = waitBeforePlay - waitBeforeLoad;
            if (remaining > 0f)
                yield return new WaitForSeconds(remaining);

            if (currentContext.Context != contextName) yield break;

            // ── Phase 3: promote and play with crossfade ─────────────────────────
            if (cache.nextCachedClip == null)
            {
                // Load didn't finish in time — load inline now as fallback
                Log.Out($"[CMM] Next clip not ready in time for {contextName}, loading inline.");
                // Don't destroy the currently-playing clip — mark it to be destroyed after the fade
                if (cache.cachedClip != null)
                {
                    _clipToDestroyAfterFade = cache.cachedClip;
                    cache.cachedClip = null;
                }
                yield return StartCoroutine(PreloadContext(contextName));
            }
            else
            {
                // Mark the currently-playing clip for destruction after the fade instead of destroying it immediately
                if (cache.cachedClip != null)
                {
                    _clipToDestroyAfterFade = cache.cachedClip;
                }
                cache.cachedClip = cache.nextCachedClip;
                cache.nextCachedClip = null;
            }

            cache.resumePosition = 0f;
            cache.lastLeftTime = -1f;

            if (cache.cachedClip == null)
            {
                Log.Out($"[CMM] No clip available for {contextName} transition. Giving up.");
                yield break;
            }

            Log.Out($"[CMM] Starting crossfade to next track in {contextName}");
            _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(PlayFromCache(contextName, true));
            _nextCacheCoroutine = null;
        }

        // Picks the next file for a context and loads it into nextCachedClip.
        private IEnumerator LoadNextClip(string contextName)
        {
            ContextCache cache    = cacheMap[contextName];
            string       avoid    = GlobalTrackOrder == TrackOrderMode.RandomNoRepeat ? cache.lastPlayedFile : null;
            string       filePath = PickNextFile(contextName, avoidFile: avoid);

            if (filePath == null) { Log.Out($"[CMM] LoadNextClip: no files for {contextName}"); yield break; }

            AudioType audioType = CheckType(filePath);
            if (audioType == AudioType.UNKNOWN) yield break;

            using (var request = UnityWebRequestMultimedia.GetAudioClip(
                       "file:///" + filePath.Replace("\\", "/"), audioType))
            {
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                { Log.Error($"[CMM] LoadNextClip failed for {contextName}: {request.error}"); yield break; }

                if (cache.nextCachedClip != null) Destroy(cache.nextCachedClip);
                cache.nextCachedClip = DownloadHandlerAudioClip.GetContent(request);
                cache.lastPlayedFile = filePath;
                Log.Out($"[CMM] Next clip ready for {contextName}: {Path.GetFileName(filePath)}");
            }
        }

        // Selects the next file from the context's folder according to
        // GlobalTrackOrder (Random, RandomNoRepeat, Sequential).
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

        // Applies the current music volume setting to the active channel.
        // Called when the player changes volume in settings.
        public static void UpdateVolume()
        {
            if (ActiveChannel == null) return;
            ActiveChannel.volume = GamePrefs.GetFloat(
                inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);
        }

        // Returns true if the context folder contains at least one supported audio file.
        private static bool ContextHasFiles(string contextName)
        {
            Contexts ctx = GetContext(contextName);
            if (ctx == null || string.IsNullOrEmpty(ctx.Path)) return false;
            string dir = ModPath + MusicDirectory + "/" + ctx.Path + "/";
            if (!Directory.Exists(dir)) return false;
            return Directory.GetFiles(dir).Any(f => CheckType(f) != AudioType.UNKNOWN);
        }

        // Returns the AudioType for a file based on its extension.
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
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SECTION 6 — COORDINATOR
    // Owns the coroutine handles, scan loops, and the central
    // SendContextUpdate decision. Calls into the Stack and AudioPlayer.
    // ═════════════════════════════════════════════════════════════════════════

    public partial class ContextMusicManager
    {
        // ── Coordinator state ─────────────────────────────────────────────────
        public static string MusicDirectory = "/Music";
        public static bool   inGame         = false;
        public static bool   inInventory    = false;
        public static bool   playerIsDead = false;
        public static string ModPath;

        public static bool lastContextOnCoolDown = false;

        public static  GameObject     CMM_Audio_Object;
        public static  CoRoutine      CMM_CoRoutines;
        public static  ContextMusicManager Instance;

        // Coroutine handles — all owned here so TriggerMusic can cancel them cleanly.
        private static Coroutine _activeMusicCoroutine = null;
        private static Coroutine _nextCacheCoroutine   = null;
        private static Coroutine _trackEndCoroutine    = null;
        private static Coroutine _crossFadeCoroutine   = null;
        private static Coroutine _cooldownCoroutine    = null;

        // ── Timing configuration ──────────────────────────────────────────────
        public static TrackOrderMode GlobalTrackOrder = TrackOrderMode.RandomNoRepeat;
        public static float ContextScanCooldown        = 0.5f;
        public static float StalenessCheckInterval     = 5f;
        public static float NextCacheWindow            = 5f;
        public static float TrackEndPollInterval       = 0.25f;
        public static float CrossFadeDuration          = 2f;
        public static float TrackTransitionFadeDuration = 6f;
        public static float TrackTransitionPreloadLead  = 10f;

        // ── Initialisation ────────────────────────────────────────────────────

        // Called once by the mod entry point. Sets up audio sources, registers
        // all contexts, and starts the background coroutines.
        public static void Init(string modPath)
        {
            ModPath          = modPath;
            CMM_Audio_Object = new GameObject("ContextMusicManager");
            UnityEngine.Object.DontDestroyOnLoad(CMM_Audio_Object);
            Instance = CMM_Audio_Object.AddComponent<ContextMusicManager>();
            Instance.InitContexts();

            foreach (var ctx in contexts.Values)
                cacheMap[ctx.Context] = new ContextCache();

            currentContext   = GetContext("None");
            _pendingContext  = "None";

            CMM_Primary_Channel         = CMM_Audio_Object.AddComponent<AudioSource>();
            CMM_Primary_Channel.volume  = 0f;
            CMM_Primary_Channel.loop    = false;

            CMM_Secondary_Channel        = CMM_Audio_Object.AddComponent<AudioSource>();
            CMM_Secondary_Channel.volume = 0f;
            CMM_Secondary_Channel.loop   = false;

            CMM_CoRoutines = CMM_Audio_Object.AddComponent<CoRoutine>();
            CMM_CoRoutines.StartCoroutine(Instance.PreloadContext("Menu"));
            CMM_CoRoutines.StartCoroutine(Instance.ContextScanLoop());
            CMM_CoRoutines.StartCoroutine(Instance.StalenessCheckLoop());
            CMM_CoRoutines.StartCoroutine(Instance.FileWatchLoop());
            Log.Out("[CMM] Initialized!");
        }

        public static void OnEnterGame()
        {
            if (!inGame) return;
            CMM_CoRoutines.StartCoroutine(Instance.PreloadAllContexts());
        }

        // ── Central decision point ────────────────────────────────────────────

        // Called whenever something thinks the context should change.
        // Validates priority, saves resume data, resolves the fallback chain,
        // checks cooldown, then hands off to TriggerMusic.
        public static void SendContextUpdate(string contextIn, int bypassPriority = -1)
        {
            Contexts incoming = GetContext(contextIn);
            if (incoming == null) return;
            if (_pendingContext == contextIn) return;

            bool menuOverride = !inGame;
            int comparePriority = bypassPriority >= 0 ? bypassPriority : (currentContext?.Priority ?? 0);

            // Allow a reload of the currently-playing context (for example when files change)
            // even if the incoming priority isn't strictly higher. This fixes the case where
            // the same context was being re-requested but was rejected by the priority check.
            bool isReloadOfCurrent = currentContext != null && contextIn == currentContext.Context;

            bool priorityOk = inGame && incoming.CanInterrupt && incoming.Priority > comparePriority;

            if (!menuOverride && !priorityOk && !isReloadOfCurrent)
            {
                Log.Out($"[CMM] {contextIn} rejected — not higher priority than {currentContext?.Context}");
                return;
            }

            Log.Out($"[CMM] Context accepted: {currentContext?.Context} -> {contextIn}");
            _pendingContext = contextIn;

            // Save resume position for the context we're leaving.
            if (currentContext != null && currentContext.CanBeResumed && ActiveChannel.isPlaying)
            {
                ContextCache leavingCache = cacheMap[currentContext.Context];
                leavingCache.resumePosition = ActiveChannel.time;
                leavingCache.lastLeftTime = Time.realtimeSinceStartup;
                Log.Out($"[CMM] Saved resume for {currentContext.Context}: {leavingCache.resumePosition:F2}s");

                if (!CMMConfig.PreloadCacheEnabled && leavingCache.cachedClip != null)
                {
                    _clipToDestroyAfterFade = leavingCache.cachedClip;
                    leavingCache.cachedClip = null;
                }
            }

            // Snapshot the return context before a PlayOnce takes over.
            if (incoming.PlayOnce)
            {
                _playOnceReturnContext = _contextStack
                    .Where(c => c.ContextName != contextIn)
                    .Select(c => ResolvePlayableContext(c.ContextName))
                    .FirstOrDefault(c => c != null) ?? "Ambient";
                Log.Out($"[CMM] PlayOnce {contextIn} returns to: {_playOnceReturnContext}");
            }

            // If something interrupts a PlayOnce, discard its return snapshot.
            if (currentContext != null && currentContext.PlayOnce && !incoming.PlayOnce)
                _playOnceReturnContext = null;

            // Find the best playable context in the fallback chain.
            string contextToPlay = ResolvePlayableContext(contextIn);

            if (contextToPlay == null)
            {
                Log.Out($"[CMM] No playable context in chain for {contextIn} — fading to silence.");
                _pendingContext = "None";
                currentContext = GetContext("None");
                FadeToSilence();
                return;
            }

            Contexts candidate = GetContext(contextToPlay);

            if (lastContextOnCoolDown && !candidate.BypassesCooldown && bypassPriority < 0)
            {
                Log.Out($"[CMM] {candidate.Context} blocked — cooldown active");
                _pendingContext = currentContext?.Context ?? "None";
                if (!CMMConfig.PreloadCacheEnabled)
                {
                    ContextCache blocked = cacheMap[currentContext.Context];
                    if (blocked.cachedClip != null) { Destroy(blocked.cachedClip); blocked.cachedClip = null; }
                }
                return;
            }

            currentContext = candidate;
            TriggerMusic(contextToPlay);
        }

        // Cancels all in-flight playback coroutines and starts a fresh PlayFromCache.
        // The cooldown coroutine is intentionally left running.
        public static void TriggerMusic(string contextName)
        {
            Log.Out($"[CMM] TriggerMusic: {contextName}");
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }
            if (_nextCacheCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine);   _nextCacheCoroutine   = null; }
            if (_trackEndCoroutine    != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine);    _trackEndCoroutine    = null; }
            if (_crossFadeCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);   _crossFadeCoroutine   = null; }
            _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(Instance.PlayFromCache(contextName, false));
        }

        // ── Scan loops ────────────────────────────────────────────────────────

        // Runs every 0.5s. Resolves the current world state into a context list,
        // updates the stack, and triggers a context change if the top has changed.
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
                        if (ResolvePlayableContext(ctxName) == null) continue;

                        _contextStack.Add(new ActiveContext
                        {
                            ContextName = ctxName,
                            Priority    = ctx.Priority,
                            ActivatedAt = Time.realtimeSinceStartup,
                            Manual      = false
                        });
                    }
                }

                _contextStack.RemoveAll(c => !c.Manual && !activeContexts.Contains(c.ContextName));
                _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));

                string topContext = GetTopContext();
                if (topContext != currentContext.Context)
                {
                    if (currentContext != null && currentContext.PlayOnce)
                    {
                        Contexts top = GetContext(topContext);
                        if (top == null || top.Priority <= currentContext.Priority) continue;
                    }
                    _pendingContext = null;
                    SendContextUpdate(topContext, 0);
                }
            }
        }

        // Runs every 30s. Logs cache state and refreshes stale preloaded clips.
        private IEnumerator StalenessCheckLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(StalenessCheckInterval);
                if (!inGame) continue;

                Log.Out($"[CMM] Cache state: {string.Join(", ", cacheMap.Select(kv => $"{kv.Key}={(kv.Value.cachedClip != null ? $"{kv.Value.cachedClip.length:F0}s" : "null")}(next={(kv.Value.nextCachedClip != null ? "ready" : "null")})"))}");

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
                        Log.Out($"[CMM] Context {ctx.Context} is stale ({elapsed:F0}s). Clearing for fresh pick.");
                        cache.resumePosition = 0f;
                        cache.lastLeftTime = -1f;
                        string avoid = cache.lastPlayedFile;
                        cache.lastPlayedFile = null; // clear so PreloadContext doesn't reload same file

                        // Only re-cache now if preload is enabled — otherwise the fresh
                        // pick happens on-demand when the context next activates.
                        if (CMMConfig.PreloadCacheEnabled)
                            yield return StartCoroutine(PreloadContext(ctx.Context, avoidFile: avoid));
                    }
                }
            }
        }

        // Runs every 2s. Detects files added or removed from the current context's
        // folder and triggers a context re-evaluation if something changed.
        private IEnumerator FileWatchLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(2f);
                if (!inGame) continue;
                if (currentContext == null || currentContext.Context == "None") continue;

                string contextName = currentContext.Context;
                ContextCache cache = cacheMap[contextName];
                Contexts ctx = GetContext(contextName);
                if (ctx == null) continue;

                string dir = ModPath + MusicDirectory + "/" + ctx.Path + "/";
                string[] currentFiles = Directory.Exists(dir)
                    ? Directory.GetFiles(dir).Where(f => CheckType(f) != AudioType.UNKNOWN).ToArray()
                    : new string[0];
                string[] previousFiles = cache.directorySnapshot ?? new string[0];

                // Normalize to full paths for reliable comparisons
                Func<string, string> norm = p => string.IsNullOrEmpty(p) ? p : Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var currentNorm = currentFiles.Select(norm).ToArray();
                var previousNorm = previousFiles.Select(norm).ToArray();

                bool changed = currentNorm.Length != previousNorm.Length ||
                               currentNorm.Except(previousNorm, StringComparer.OrdinalIgnoreCase).Any() ||
                               previousNorm.Except(currentNorm, StringComparer.OrdinalIgnoreCase).Any();

                if (!changed) continue;

                Log.Out($"[CMM] File change detected in {contextName}: {previousFiles.Length} -> {currentFiles.Length} files");
                cache.directorySnapshot = currentFiles;

                // If the last-played file was removed from disk, clear it so PreloadContext won't resurrect it later.
                bool lastPlayedMissing = false;
                if (!string.IsNullOrEmpty(cache.lastPlayedFile))
                {
                    string lastNorm = norm(cache.lastPlayedFile);
                    if (!currentNorm.Any(f => string.Equals(f, lastNorm, StringComparison.OrdinalIgnoreCase)))
                    {
                        Log.Out($"[CMM] Previously-played file missing for {contextName}: {Path.GetFileName(cache.lastPlayedFile)}");
                        cache.lastPlayedFile = null;
                        // If cachedClip is the clip that came from the removed file, don't destroy it immediately;
                        // mark it to be destroyed after the next fade so the audio system can finish crossfades.
                        if (cache.cachedClip != null)
                        {
                            _clipToDestroyAfterFade = cache.cachedClip;
                            cache.cachedClip = null;
                        }
                        lastPlayedMissing = true;
                    }
                }

                if (currentFiles.Length == 0)
                {
                    Log.Out($"[CMM] {contextName} folder emptied, re-evaluating context.");
                    string fallback = ResolvePlayableContext(ctx.Fallback) ?? "Ambient";
                    _pendingContext = null;
                    // force the fallback even if cooldown would otherwise block
                    SendContextUpdate(fallback, currentContext.Priority + 1);
                }
                else
                {
                    // If the currently playing file was removed, request a reload of the same context
                    // (this will pick a new file from the folder or fall back as needed).
                    if (lastPlayedMissing)
                    {
                        _pendingContext = null;
                        SendContextUpdate(ctx.Context, currentContext.Priority + 1);
                    }
                    else
                    {
                        string top = GetTopContext();
                        if (top != currentContext.Context)
                        {
                            _pendingContext = null;
                            SendContextUpdate(top, 0);
                        }
                    }
                }
            }
        }

        // ── Preloading ────────────────────────────────────────────────────────

        // Preloads all contexts sequentially on game entry (only when PreloadCacheEnabled).
        private IEnumerator PreloadAllContexts()
        {
            if (!CMMConfig.PreloadCacheEnabled) { Log.Out("[CMM] Preload cache disabled — skipping."); yield break; }
            Log.Out("[CMM] Starting sequential preload for all contexts...");
            foreach (var ctx in contexts.Values)
            {
                if (ctx.Context == "None") continue;
                yield return StartCoroutine(PreloadContext(ctx.Context));
            }
            Log.Out("[CMM] Preload complete.");
        }

        // Loads a single clip for a context into cachedClip.
        // Falls back one level if the context's own folder is empty.
        private IEnumerator PreloadContext(string contextName, string avoidFile = null)
        {
            ContextCache cache = cacheMap[contextName];
            string filePath;

            // Only resume the last file if we're not being asked to avoid it
            if (cache.lastPlayedFile != null && File.Exists(cache.lastPlayedFile)
                && cache.lastPlayedFile != avoidFile)
                filePath = cache.lastPlayedFile;
            else
            {
                cache.resumePosition = 0f;
                cache.lastPlayedFile = null;
                filePath = PickNextFile(contextName, avoidFile: avoidFile);
            }

            if (filePath == null)
            {
                Contexts ctx        = GetContext(contextName);
                string   fallbackName = ctx?.Fallback ?? "None";
                if (fallbackName != "None" && fallbackName != contextName)
                {
                    filePath = PickNextFile(fallbackName, avoidFile: null);
                    if (filePath == null)
                    { Log.Out($"[CMM] Preload: no files for {contextName} or fallback {fallbackName}"); yield break; }
                }
                else
                { Log.Out($"[CMM] Preload: no files for {contextName}"); yield break; }
            }

            AudioType audioType = CheckType(filePath);
            if (audioType == AudioType.UNKNOWN) yield break;

            using (var request = UnityWebRequestMultimedia.GetAudioClip(
                       "file:///" + filePath.Replace("\\", "/"), audioType))
            {
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = true;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                { Log.Error($"[CMM] Preload failed for {contextName}: {request.error}"); yield break; }

                if (cache.cachedClip != null) Destroy(cache.cachedClip);
                cache.cachedClip     = DownloadHandlerAudioClip.GetContent(request);
                cache.lastPlayedFile = filePath;
                Log.Out($"[CMM] Preloaded: {contextName}");
            }
        }

        // Prevents context switches during the cooldown window after a transition.
        private IEnumerator ContextCooldownTimer(int seconds)
        {
            lastContextOnCoolDown = true;
            Log.Out($"[CMM] Cooldown started: {seconds}s");
            yield return new WaitForSeconds(seconds);
            lastContextOnCoolDown = false;
            Log.Out("[CMM] Cooldown expired.");
            _cooldownCoroutine = null;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SECTION 7 — MUSIC STATES
    // ═════════════════════════════════════════════════════════════════════════

    public static class MusicStates
    {
        public static bool DefaultMusicEnabled = false;
        public static void ToggleDefaultMusic()
        {
            DefaultMusicEnabled = !DefaultMusicEnabled;
            Log.Error($"[CMM] Default music enabled: {DefaultMusicEnabled}");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SECTION 8 — HARMONY PATCHES
    // Intercepts game events and translates them into CMM context changes.
    // ═════════════════════════════════════════════════════════════════════════

    // Suppresses the game's own dynamic music system and detects game entry.
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
            }
            return false;
        }
    }

    // Intercepts the main menu music and routes it through CMM.
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

    // Cleans up CMM state when the player exits to the main menu.
    [HarmonyPatch(typeof(GameManager), "SaveAndCleanupWorld")]
    public class Patch_GameEnded
    {
        static void Prefix()
        {
            ContextMusicManager.inInventory = false;
            ContextMusicManager.inGame      = false;
            ContextMusicManager._contextStack.Clear();
            ContextMusicManager._pendingContext = "None";
            ContextResolver.resetValues();
            ContextMusicManager.SendContextUpdate("Menu");
        }
    }

    [HarmonyPatch(typeof(EntityPlayerLocal), "AfterPlayerRespawn")]
    public class PlayerRespawn
    {
        static void Postfix()
        {
            Log.Error("This actually worked");
            ContextMusicManager._contextStack.Clear();
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
            ContextMusicManager.SendContextUpdate("Spawn");
            ContextMusicManager.playerIsDead = false;
        }
    }

    [HarmonyPatch(typeof(EntityPlayerLocal), "OnDeathUpdate")]
    public class PlayerDeath
    {
        static void Postfix()
        {
            if(!ContextMusicManager.playerIsDead)
            {
                ContextMusicManager.SendContextUpdate("Death");
            }
            ContextMusicManager.playerIsDead = true;
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

    // Routes sound group names from the game's audio system to CMM context changes.
    // Handles death, spawn, and inventory open/close stingers.
    public static class AudioStingerHandler
    {
        public static bool Handle(string soundGroupName)
        {
            Log.Error($"[CMM Audio Spy] PlayInsidePlayerHead: {soundGroupName}");
            switch (soundGroupName)
            {
                case "player_death_stinger":
                    return false;

                case "spawnInStinger":
                    return false;

                case "open_inventory":
                    //ContextMusicManager.inInventory = true;
                    //ContextMusicManager.PushContext("Inventory");
                    return true;

                case "close_inventory":
                    //ContextMusicManager.inInventory = false;
                    //ContextMusicManager.PopContext("Inventory");
                    return true;

                default:
                    return true;
            }
        }
    }

    [HarmonyPatch(typeof(XUiC_BackpackWindow), "OnOpen")]
    public class PushBackpackOpen
    {
        static bool PostFix()
        {
            Log.Error("Backpack is open");
            ContextMusicManager.PushContext("Inventory");
            return true;
        }
    }

    [HarmonyPatch(typeof(XUiC_BackpackWindow), "OnClose")]
    public class PushBackpackClose
    {
        static bool PostFix()
        {
            Log.Error("Backpack is closed");
            ContextMusicManager.PopContext("Inventory");
            return true;
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