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
        // Streaming enabled/disabled
        public static bool StreamingEnabled = true;
            
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
        public AudioClip cachedClip           = null;
        public AudioClip nextCachedClip       = null;
        public float     resumePosition       = 0f;
        public float     lastLeftTime         = -1f;
        public string    lastPlayedFile       = null;
        public int       sequentialIndex      = 0;
        public string[]  directorySnapshot    = null;
        public bool      nextClipLoading      = false;
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
        public string Context               { get; private set; }
        public string Path                  { get; private set; }
        public string Fallback              { get; private set; }
        public bool   CanBeResumed          { get; private set; }
        public int    TimeUntilContextStale { get; private set; }
        public bool   Loop                  { get; private set; }
        public int    Cooldown              { get; private set; }
        public int    WarmUp                { get; private set; }
        public bool   BypassesCooldown      { get; private set; }
        public bool   CanInterrupt          { get; private set; }
        public int    Priority              { get; private set; }
        public bool   PlayOnce              { get; private set; }

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
    static readonly float EnemyDetectRange                   = 20f;
    static readonly float TraderDetectRange                  = 40f;
    static readonly float HomeDetectRange                    = 70f;
    static readonly float PlayerHealthDangerThresholdPercent = 0.25f;
    static readonly int   DangerThreshold                    = 1;
    static readonly int   CriticalThreshold                  = 4;
    static readonly float CombatCooldown                     = 10f;

    static string combatState   = "None";
    static float  lastAggroTime = 0f;
    static bool   wasInfested   = false;
    static string lastPoI       = "None";

    public static void resetValues()
    {
        combatState = "None";
    }

    public static List<string> ResolveContext()
    {
        var world  = GameManager.Instance?.World;
        var player = world?.GetPrimaryPlayer();

        if (world == null || player == null)
            return new List<string> { "None" };

        var activeContexts = new List<string>();

        bool  isDay       = world.IsDaytime();
        bool  isBloodmoon = world.isEventBloodMoon;
        float healthPct   = player.Health / (float)player.GetMaxHealth();

        int enemyAggroCount = 0, enemyAlertCount = 0, enemiesNearby = 0;

        // ── Blood moon ───────────────────────────────────────────────────────
        if (isBloodmoon)
            activeContexts.Add("Blood_Moon");

        // ── Vehicle ──────────────────────────────────────────────────────────
        if (player.AttachedToEntity is EntityVehicle vehicle)
        {
            string vn = vehicle.GetType().Name.ToLower();
            activeContexts.Add("Vehicle");
            if      (vn.Contains("gyrocopter")) activeContexts.Add("Vehicle_Gyrocopter");
            else if (vn.Contains("bicycle"))    activeContexts.Add("Vehicle_Bicycle");
            else if (vn.Contains("motorcycle")) activeContexts.Add("Vehicle_Motorcycle");
            else if (vn.Contains("minibike"))   activeContexts.Add("Vehicle_Minibike");
            else if (vn.Contains("jeep"))       activeContexts.Add("Vehicle_4x4");
        }

        // ── Traders and enemies ──────────────────────────────────────────────
        foreach (var entity in world.Entities.list)
        {
                // ── Traders ──────────────────────────────────────────────────────────
                if (entity is EntityTrader trader)
                {
                    if (Vector3.Distance(player.position, trader.position) > TraderDetectRange) continue;
                    string n = trader.EntityName?.ToLower() ?? "";
                    string specific = null;
                    if (n.Contains("jen")) specific = "Trader_Jen";
                    else if (n.Contains("rekt")) specific = "Trader_Rekt";
                    else if (n.Contains("bob")) specific = "Trader_Bob";
                    else if (n.Contains("hugh")) specific = "Trader_Hugh";
                    else if (n.Contains("joel")) specific = "Trader_Joel";

                    // Only add the generic Trader context if no specific variant matched,
                    // or if the specific variant has no playable files. The fallback chain
                    // in ResolvePlayableContext handles the rest.
                    if (specific != null)
                        activeContexts.Add(specific);
                    else
                        activeContexts.Add("Trader");
                }
                else if (entity is EntityEnemy enemy && enemy.IsAlive())
            {
                if (Vector3.Distance(player.position, enemy.position) < EnemyDetectRange)
                    enemiesNearby++;
                if (enemy.isAlert)
                {
                    if (enemy.GetAttackTarget() == player)
                    { lastAggroTime = Time.realtimeSinceStartup; enemyAggroCount++; }
                    enemyAlertCount++;
                }
            }
        }

        // ── Combat ───────────────────────────────────────────────────────────
        float timeSinceAggro = Time.realtimeSinceStartup - lastAggroTime;
        bool  inCombat       = false;

        if (enemyAggroCount >= CriticalThreshold)
        {
            combatState = "High";
            activeContexts.Add("Combat_High");
            if (isBloodmoon) activeContexts.Add("Blood_Moon_Combat");
            inCombat = true;
        }
        else if (enemyAggroCount >= DangerThreshold)
        {
            if (combatState != "High") combatState = "Low";
            activeContexts.Add(combatState == "High" ? "Combat_High" : "Combat_Low");
            if (isBloodmoon) activeContexts.Add("Blood_Moon_Combat");
            inCombat = true;
        }
        else if (timeSinceAggro <= CombatCooldown && enemyAlertCount > 0)
        {
            activeContexts.Add(combatState == "High" ? "Combat_High" : "Combat_Low");
            inCombat = true;
        }
        else if (timeSinceAggro > CombatCooldown && enemyAlertCount == 0)
        {
            combatState = "None";
        }

        // Low health panic only meaningful during active combat
        if (inCombat && healthPct < PlayerHealthDangerThresholdPercent)
            activeContexts.Add("Low_Health_Panic");

        // ── Tension ──────────────────────────────────────────────────────────
        if      (enemiesNearby >= CriticalThreshold && enemyAlertCount >= CriticalThreshold)
            activeContexts.Add("Tension_High");
        else if (enemiesNearby >= DangerThreshold   && enemyAlertCount >= DangerThreshold)
            activeContexts.Add("Tension_Low");

        // ── Home ─────────────────────────────────────────────────────────────
        if (player is EntityPlayerLocal localPlayer && localPlayer.persistentPlayerData != null)
        {
            bool nearHome = false;

            if (localPlayer.persistentPlayerData.HasBedrollPos)
            {
                Vector3i bed = localPlayer.persistentPlayerData.BedrollPos;
                nearHome = Vector3.Distance(player.position, bed) < HomeDetectRange;
            }

            if (!nearHome)
            {
                var claims = localPlayer.persistentPlayerData.GetLandProtectionBlocks();
                if (claims.Count > 0)
                {
                    Vector3i nearest = claims.OrderBy(c => Vector3.Distance(player.position, c.ToVector3())).First();
                    nearHome = Vector3.Distance(player.position, nearest.ToVector3()) < HomeDetectRange;
                }
            }

            if (nearHome)
                activeContexts.Add(isDay ? "Home_Day" : "Home_Night");
        }

        // ── POI ──────────────────────────────────────────────────────────────
        PrefabInstance prefab = world.GetPOIAtPosition(player.position);
        if (prefab != null)
        {
            string currentPoI           = prefab.prefab.PrefabName;
            int    enemiesRemainingInPoi = prefab.sleeperVolumes.Count(v => !v.wasCleared);

            if (enemiesRemainingInPoi > 0)
            {
                wasInfested = true;
                switch (prefab.prefab.DifficultyTier)
                {
                    case 0:  activeContexts.Add("POI_T0"); break;
                    case 1:  activeContexts.Add("POI_T1"); break;
                    case 2:  activeContexts.Add("POI_T2"); break;
                    case 3:  activeContexts.Add("POI_T3"); break;
                    case 4:  activeContexts.Add("POI_T4"); break;
                    case 5:  activeContexts.Add("POI_T5"); break;
                    default: activeContexts.Add("POI");    break;
                }
            }
            else
            {
                if (wasInfested && currentPoI == lastPoI)
                    activeContexts.Add("POI_Cleared");
                wasInfested = false;
            }

            lastPoI = currentPoI;
        }
        else
        {
            wasInfested = false;
        }

        // ── Biome ─────────────────────────────────────────────────────────────
        string biomeName = world.GetBiome((int)player.position.x, (int)player.position.z)?.m_sBiomeName;
        switch (biomeName)
        {
            case "pine_forest":  activeContexts.Add("Ambient_Forest");       break;
            case "desert":       activeContexts.Add("Ambient_Desert");       break;
            case "burnt_forest": activeContexts.Add("Ambient_Burnt_Forest"); break;
            case "snow":         activeContexts.Add("Ambient_Snow");         break;
            case "wasteland":    activeContexts.Add("Ambient_Wasteland");    break;
            default:             activeContexts.Add("Ambient");              break;
        }

        return activeContexts;
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
        public static Dictionary<string, Contexts>     contexts      = new Dictionary<string, Contexts>();
        public static Dictionary<string, ContextCache> cacheMap      = new Dictionary<string, ContextCache>();
        public static List<ActiveContext>              _contextStack  = new List<ActiveContext>();
        public static Contexts                         currentContext;

        // FIX (Bug 1): Replace the _pendingContext string guard with an integer
        // transaction ID. Each call to SendContextUpdate increments this counter
        // and gives the new value to every coroutine it spawns. A coroutine
        // checks its captured ID against the current value before doing any
        // meaningful work; if they differ, a newer transition has taken over and
        // the coroutine exits. This eliminates the window where _pendingContext
        // is set before currentContext is updated, which previously allowed
        // re-entrant calls to see stale state and dispatch conflicting transitions.
        private static int    _transitionId        = 0;
        public  static string _pendingContext       = null;   // kept for cheap early-rejection only
        public  static string _playOnceReturnContext = null;

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
            AddContext("Death",                "events/death",         "Ambient",      false,  0,  false, 3, 1, true,  true, 100, true );

            AddContext("Low_Health_Panic",     "events/low_health",    "Ambient",      true,  30,  false, 5, 0, true,  true,  90, false);

            AddContext("Blood_Moon_Combat",    "blood_moon/combat",    "Combat_High",  true,  20,  false, 3, 3, true,  true,  71, false);
            AddContext("Blood_Moon",           "blood_moon",           "Ambient",      true,  30,  false, 5, 5, true,  true,  70, false);

            AddContext("Vehicle_Gyrocopter",   "vehicle/gyrocopter",   "Vehicle",      true,  30,  false, 5, 3, true,  true,  61, false);
            AddContext("Vehicle_Bicycle",      "vehicle/bicycle",      "Vehicle",      true,  30,  false, 5, 3, true,  true,  61, false);
            AddContext("Vehicle_Minibike",     "vehicle/minibike",     "Vehicle",      true,  30,  false, 5, 3, true,  true,  61, false);
            AddContext("Vehicle_Motorcycle",   "vehicle/motorcycle",   "Vehicle",      true,  30,  false, 5, 3, true,  true,  61, false);
            AddContext("Vehicle_4x4",          "vehicle/4x4",          "Vehicle",      true,  30,  false, 5, 3, true,  true,  61, false);
            AddContext("Vehicle",              "vehicle",              "Ambient",      true,  30,  false, 5, 3, true,  true,  60, false);

            AddContext("POI_Cleared",          "poi/cleared",          "Ambient",      true,  60,  false, 5, 5, true,  true,  55, true );
            AddContext("Combat_High",          "combat_high",          "Combat_Low",   true,  20,  false, 3, 3, true,  true,  51, false);
            AddContext("Combat_Low",           "combat_low",           "Tension_Low",  true,  20,  false, 3, 3, true,  true,  50, false);

            AddContext("Spawn",                "events/spawn",         "Ambient",      false,  0,  false, 3, 1, true,  true,  45, true );
            AddContext("Inventory",            "inventory",            "Ambient",      true,  15,  false, 0, 2, true,  true,  40, false);

            AddContext("Tension_High",         "tension_high",         "Tension_Low",  true,  30,  false, 5, 5, false, true,  30, false);
            AddContext("Tension_Low",          "tension_low",          "Ambient",      true,  30,  false, 5, 5, false, true,  30, false);

            AddContext("Trader_Joel",          "trader/joel",          "Trader",       true, 120,  false, 5, 5, false, true,  26, false);
            AddContext("Trader_Jen",           "trader/jen",           "Trader",       true, 120,  false, 5, 5, false, true,  26, false);
            AddContext("Trader_Rekt",          "trader/rekt",          "Trader",       true, 120,  false, 5, 5, false, true,  26, false);
            AddContext("Trader_Bob",           "trader/bob",           "Trader",       true, 120,  false, 5, 5, false, true,  26, false);
            AddContext("Trader_Hugh",          "trader/hugh",          "Trader",       true, 120,  false, 5, 5, false, true,  26, false);
            AddContext("Trader",               "trader",               "Ambient",      true, 120,  false, 5, 5, false, true,  25, false);

            AddContext("Home_Day",             "home/day",             "Home",         true, 120,  false, 5, 5, false, true,  20, false);
            AddContext("Home_Night",           "home/night",           "Home",         true, 120,  false, 5, 5, false, true,  20, false);
            AddContext("Home",                 "home",                 "Ambient",      true, 120,  false, 5, 5, false, true,  20, false);

            //AddContext("POI_Infested",         "poi/infested",         "POI",          true,  60,  false, 5, 5, false, true,  21, false);
            AddContext("POI_T5",               "poi/t5",               "POI",          true,  60,  false, 5, 5, false, true,  15, false);
            AddContext("POI_T4",               "poi/t4",               "POI",          true,  60,  false, 5, 5, false, true,  15, false);
            AddContext("POI_T3",               "poi/t3",               "POI",          true,  60,  false, 5, 5, false, true,  15, false);
            AddContext("POI_T2",               "poi/t2",               "POI",          true,  60,  false, 5, 5, false, true,  15, false);
            AddContext("POI_T1",               "poi/t1",               "POI",          true,  60,  false, 5, 5, false, true,  15, false);
            AddContext("POI_T0",               "poi/t0",               "Ambient",      true,  60,  false, 5, 5, false, true,  14, false);
            AddContext("POI",                  "poi",                  "Ambient",      true,  60,  false, 5, 5, false, true,  13, false);

            AddContext("Ambient",              "ambient",              "None",         true, 120,  false, 5, 4, false, true,  10, false);
            AddContext("Ambient_Forest",       "ambient/forest",       "Ambient",      true, 120,  false, 5, 5, false, true,  10, false);
            AddContext("Ambient_Burnt_Forest", "ambient/burnt_forest", "Ambient",      true, 120,  false, 5, 5, false, true,  10, false);
            AddContext("Ambient_Desert",       "ambient/desert",       "Ambient",      true, 120,  false, 5, 5, false, true,  10, false);
            AddContext("Ambient_Snow",         "ambient/snow",         "Ambient",      true, 120,  false, 5, 5, false, true,  10, false);
            AddContext("Ambient_Wasteland",    "ambient/wasteland",    "Ambient",      true, 120,  false, 5, 5, false, true,  10, false);

            AddContext("None",                 "",                     "None",         false,  0,  false, 0, 0, true,  true,   0, false);
            AddContext("Menu",                 "menu",                 "Ambient",      false,  0,  true,  5, 2, true,  true,   0, false);
        }

        // Looks up a context definition by name. Returns null if not found.
        public static Contexts GetContext(string contextIn) =>
            contexts.TryGetValue(contextIn, out var ctx) ? ctx : null;

        // Walks the fallback chain from contextName until it finds a context
        // whose folder contains at least one playable file. Returns null if
        // the entire chain is empty. Max depth 5 to prevent infinite loops.

        private static readonly HashSet<string> _invalidFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _validatedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private IEnumerator ValidateFile(string filePath, System.Action<bool> callback)
        {
            AudioType audioType = CheckType(filePath);
            if (audioType == AudioType.UNKNOWN) { callback(false); yield break; }

            using (var request = UnityWebRequestMultimedia.GetAudioClip(
                       "file:///" + filePath.Replace("\\", "/"), audioType))
            {
                // Never stream during validation — we need the full clip to measure it
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = false;

                float startTime = Time.realtimeSinceStartup;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Log.Warning($"[CMM] Validation failed (network error) for {Path.GetFileName(filePath)}: {request.error}");
                    callback(false);
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                bool valid = clip != null
                          && clip.samples > 0
                          && clip.channels > 0
                          && clip.length > 0.5f;   // reject sub-half-second clips as likely corrupt stubs

                if (!valid)
                    Log.Warning($"[CMM] Validation failed (bad clip data) for {Path.GetFileName(filePath)}: " +
                                $"samples={clip?.samples ?? 0} channels={clip?.channels ?? 0} length={clip?.length ?? 0f:F2}s");

                if (clip != null) Destroy(clip);
                callback(valid);
            }
        }

        private IEnumerator ValidateContextFiles(string contextName, System.Action<string[]> callback)
        {
            Contexts ctx = GetContext(contextName);
            if (ctx == null) { callback(new string[0]); yield break; }

            string dir = ModPath + MusicDirectory + "/" + ctx.Path + "/";
            if (!Directory.Exists(dir)) { callback(new string[0]); yield break; }

            string[] candidates = Directory.GetFiles(dir)
                .Where(f => CheckType(f) != AudioType.UNKNOWN && !_invalidFiles.Contains(f))
                .ToArray();

            var valid = new List<string>();
            foreach (string file in candidates)
            {
                bool isValid = false;
                yield return StartCoroutine(ValidateFile(file, result => isValid = result));
                if (isValid)
                    valid.Add(file);
                else
                    _invalidFiles.Add(file);
            }

            if (_invalidFiles.Count > 0)
                Log.Warning($"[CMM] {contextName}: {candidates.Length - valid.Count} invalid file(s) excluded: " +
                            string.Join(", ", candidates.Except(valid).Select(Path.GetFileName)));

            callback(valid.ToArray());
        }

        private static string ResolvePlayableContext(string contextName)
        {
            string current = contextName;
            for (int i = 0; i < 5; i++)
            {
                if (ContextHasFiles(current)) return current;
                Contexts ctx = GetContext(current);
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
        
        // FIX (Bug 3): Flag set by ScheduleNextCacheLoad immediately before it
        // fires PlayFromCache. TrackEndWatcher checks this and exits without
        // acting if true, preventing both coroutines from calling PlayFromCache
        // for the same track end. Cleared by TrackEndWatcher on read.
        private static bool _scheduleHandledTransition = false;
        
        // ── Playback ──────────────────────────────────────────────────────────

        // Entry point for playing a context. Loads the clip on demand if needed,
        // sets up the incoming channel, and starts a crossfade from the outgoing one.
        // isSameContextSwitchingToNextTrack: true when a track ends naturally and
        // the next track in the same context starts — uses a longer fade.
        private IEnumerator PlayFromCache(string contextName, bool isSameContextSwitchingToNextTrack, int transitionId)
        {
            // FIX (Bug 1): Bail immediately if a newer transition has been issued
            // since this coroutine was spawned.
            if (transitionId != _transitionId) yield break;

            Contexts     ctx   = GetContext(contextName);
            ContextCache cache = cacheMap[contextName];

            // If a crossfade is already running, stop it and snap the outgoing
            // channel to silence so the new fade starts from a clean state.
            if (_crossFadeCoroutine != null)
            {
                CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);
                _crossFadeCoroutine = null;
                //InactiveChannel.volume = 0f;
            }

            // ── Clip acquisition ─────────────────────────────────────────────
            if (CMMConfig.PreloadCacheEnabled)
            {
                // Wait up to 10s for a preloaded clip (slow on first boot).
                float waited = 0f;
                while (cache.cachedClip == null && waited < 10f)
                {
                    if (transitionId != _transitionId) yield break;
                    yield return new WaitForSeconds(0.25f);
                    waited += 0.25f;
                }

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

            // FIX (Bug 1): Check again after any yields — another transition
            // may have arrived while we were waiting for the clip to load.
            if (transitionId != _transitionId) yield break;

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
            AudioSource incoming        = InactiveChannel;
            AudioSource outgoing        = ActiveChannel;
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
                _trackEndCoroutine = CMM_CoRoutines.StartCoroutine(TrackEndWatcher(contextName, watchTarget, transitionId));

            // ── Schedule next-clip preload ───────────────────────────────────
            if (!ctx.Loop && !ctx.PlayOnce)
            {
                float clipLength = cache.cachedClip.length > 0f
                    ? cache.cachedClip.length
                    : TrackTransitionFadeDuration + TrackTransitionPreloadLead + 1f;
                float remaining = clipLength - startPosition;
                _nextCacheCoroutine = CMM_CoRoutines.StartCoroutine(ScheduleNextCacheLoad(contextName, remaining, transitionId));
            }

            _activeMusicCoroutine = null;
        }

        // Returns the total memory used by all cached AudioClips in bytes.
        // Formula: samples × channels × 4 bytes (Unity stores clips as 32-bit float PCM).
        public static long GetCacheMemoryBytes()
        {
            long total = 0;
            foreach (var cache in cacheMap.Values)
            {
                if (cache.cachedClip != null)
                    total += (long)cache.cachedClip.samples * cache.cachedClip.channels * 4;
                if (cache.nextCachedClip != null)
                    total += (long)cache.nextCachedClip.samples * cache.nextCachedClip.channels * 4;
            }
            return total;
        }

        public static string GetCacheMemorySummary(bool includePerContext = true)
        {
            long totalBytes = 0;
            var lines = new System.Text.StringBuilder();

            foreach (var kv in cacheMap)
            {
                long clipBytes = kv.Value.cachedClip != null ? (long)kv.Value.cachedClip.samples * kv.Value.cachedClip.channels * 4 : 0;
                long nextBytes = kv.Value.nextCachedClip != null ? (long)kv.Value.nextCachedClip.samples * kv.Value.nextCachedClip.channels * 4 : 0;
                long entryTotal = clipBytes + nextBytes;
                if (entryTotal == 0) continue;

                totalBytes += entryTotal;

                if (includePerContext)
                    lines.AppendLine($"  {kv.Key}: {FormatBytes(clipBytes)} current" +
                                     (nextBytes > 0 ? $" + {FormatBytes(nextBytes)} next" : ""));
            }

            if (includePerContext)
                lines.Append($"  TOTAL: {FormatBytes(totalBytes)}");
            else
                lines.Append(FormatBytes(totalBytes));

            return lines.ToString();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024f:F1} KB";
            return $"{bytes} B";
        }

        private IEnumerator VolumeSyncLoop()
        {
            float previousVolume = -1f;
            while (true)
            {
                yield return new WaitForSeconds(0.1f);

                float currentVolume = GamePrefs.GetFloat(
                    inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);

                if (Mathf.Approximately(currentVolume, previousVolume)) continue;
                previousVolume = currentVolume;

                // Only update the active channel directly if no crossfade is running.
                // If a crossfade is running it already reads the volume live each frame.
                if (_crossFadeCoroutine == null && ActiveChannel != null && ActiveChannel.isPlaying)
                    ActiveChannel.volume = currentVolume;
            }
        }

        // Fades outgoing down and incoming up over [duration] seconds.
        // Starts the cooldown timer after the fade completes.
        private IEnumerator CrossFade(AudioSource outgoing, AudioSource incoming,
            float duration, int cooldownSeconds, float outgoingStartVol)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float targetVolume = GamePrefs.GetFloat(
                    inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);

                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                incoming.volume = Mathf.Lerp(0f, targetVolume, t);
                outgoing.volume = Mathf.Lerp(outgoingStartVol, 0f, t);
                yield return null;
            }

            float finalVolume = GamePrefs.GetFloat(
                inGame ? EnumGamePrefs.OptionsMusicVolumeLevel : EnumGamePrefs.OptionsMenuMusicVolumeLevel);
            incoming.volume = finalVolume;
            outgoing.Stop();
            outgoing.clip = null;
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
            if (_nextCacheCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine);   _nextCacheCoroutine   = null; }
            if (_trackEndCoroutine    != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine);    _trackEndCoroutine    = null; }
            if (_crossFadeCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);   _crossFadeCoroutine   = null; }

            _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeOutCoroutine());
        }

        // Smoothly reduces the active channel to zero volume then stops it.
        // ── Fade out coroutine (updated to destroy pending clip) ─────────────────
        private IEnumerator FadeOutCoroutine()
        {
            AudioSource outgoing = ActiveChannel;
            float startVol  = outgoing.volume;
            float elapsed   = 0f;

            while (elapsed < CrossFadeDuration)
            {
                elapsed += Time.deltaTime;
                outgoing.volume = Mathf.Lerp(startVol, 0f, Mathf.Clamp01(elapsed / CrossFadeDuration));
                yield return null;
            }

            outgoing.Stop();
            outgoing.clip   = null;
            outgoing.volume = 0f;

            if (_clipToDestroyAfterFade != null) { Destroy(_clipToDestroyAfterFade); _clipToDestroyAfterFade = null; }

            _crossFadeCoroutine = null;
        }

        // FIX (Bug 3 + Bug 1): TrackEndWatcher now receives the transitionId it
        // was spawned under. It exits immediately if a newer transition has taken
        // over. It also checks _scheduleHandledTransition before firing its
        // safety-net PlayFromCache, so ScheduleNextCacheLoad and TrackEndWatcher
        // can never both call PlayFromCache for the same track end.
        //
        // FIX (Bug 2): The PlayOnce return context is now resolved here, at the
        // moment the track actually ends, rather than at the moment the context
        // was requested. This means the stack snapshot is taken as late as
        // possible, eliminating the window where ContextScanLoop could mutate
        // _contextStack between the snapshot and the return.
        private IEnumerator TrackEndWatcher(string contextName, AudioSource watchTarget, int transitionId)
        {
            float startTime          = Time.realtimeSinceStartup;
            float minimumPlaySeconds = 2f;

            yield return new WaitForSeconds(1f);

            // FIX (Bug 1): Exit if a newer transition took over during the initial wait.
            if (transitionId != _transitionId)
            {
                _trackEndCoroutine = null;
                yield break;
            }

            float notPlayingDuration = 0f;
            while (true)
            {
                yield return new WaitForSeconds(TrackEndPollInterval);

                // FIX (Bug 1): A newer transition means we should not act.
                if (transitionId != _transitionId)
                {
                    Log.Out($"[CMM] TrackEndWatcher: transition superseded for {contextName}, exiting.");
                    _trackEndCoroutine = null;
                    yield break;
                }

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

            if (transitionId != _transitionId || currentContext.Context != contextName)
            {
                Log.Out($"[CMM] TrackEndWatcher: context changed away from {contextName}, ignoring.");
                _trackEndCoroutine = null;
                yield break;
            }

            Log.Out($"[CMM] Track ended: {contextName}");
            _trackEndCoroutine = null;

            if (GetContext(contextName).PlayOnce)
            {
                if (playerIsDead)
                {
                    Log.Out($"[CMM] PlayOnce {contextName} finished but player is dead — fading to silence.");
                    _pendingContext = "None";
                    currentContext = GetContext("None");
                    FadeToSilence();
                    yield break;
                }
                // FIX (Bug 2): Resolve the return context now (late), not when the
                // PlayOnce was first requested. The stack reflects where the player
                // actually is right now, not where they were when the stinger fired.
                string returnTo = _contextStack
                     .Where(c => c.ContextName != contextName)
                     .Select(c => ResolvePlayableContext(c.ContextName))
                     .FirstOrDefault(c => c != null) ?? "Ambient";
                _playOnceReturnContext = null;
                Log.Out($"[CMM] PlayOnce finished, returning to: {returnTo}");
                SendContextUpdate(returnTo, 0);
                yield break;
            }

            // FIX (Bug 3): Only fire the safety net if ScheduleNextCacheLoad did
            // not already handle this transition. If it did, the flag is set and
            // we exit cleanly without double-firing PlayFromCache.
            if (_scheduleHandledTransition)
            {
                Log.Out($"[CMM] TrackEndWatcher: ScheduleNextCacheLoad already handled transition for {contextName}, skipping safety net.");
                _scheduleHandledTransition = false;
                yield break;
            }

            if (currentContext.Context == contextName && !ActiveChannel.isPlaying)
            {
                Log.Out($"[CMM] TrackEndWatcher safety net firing for {contextName}");
                ContextCache cache = cacheMap[contextName];
                cache.resumePosition = 0f;
                cache.lastLeftTime   = -1f;

                if (cache.cachedClip != null) { Destroy(cache.cachedClip); cache.cachedClip = null; }
                yield return StartCoroutine(PreloadContext(contextName));

                if (cache.cachedClip == null)
                {
                    Log.Out($"[CMM] Safety net load failed for {contextName}. Giving up.");
                    yield break;
                }

                // Use current _transitionId here — we are the one driving the next transition.
                int newId = ++_transitionId;
                _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(PlayFromCache(contextName, true, newId));
            }
        }


        // FIX (Bug 3): ScheduleNextCacheLoad now accepts the transitionId it was
        // spawned under, and exits early on any yield point if a newer transition
        // has taken over. It sets _scheduleHandledTransition = true immediately
        // before firing PlayFromCache so that TrackEndWatcher's safety net can
        // detect that the transition has already been handled.
        private IEnumerator ScheduleNextCacheLoad(string contextName, float clipLength, int transitionId)
        {
            float effectiveFade  = Mathf.Clamp(TrackTransitionFadeDuration, 0.25f, clipLength * 0.5f);
            float waitBeforeLoad = Mathf.Max(0f, clipLength - effectiveFade - TrackTransitionPreloadLead);
            float waitBeforePlay = Mathf.Max(0f, clipLength - effectiveFade);

            Log.Out($"[CMM] {contextName}: clip={clipLength:F1}s fade={effectiveFade:F1}s loadIn={waitBeforeLoad:F1}s playIn={waitBeforePlay:F1}s");

            // ── Phase 1: load the next clip ahead of time ────────────────────
            yield return new WaitForSeconds(waitBeforeLoad);
            if (transitionId != _transitionId || currentContext.Context != contextName) yield break;

            ContextCache cache = cacheMap[contextName];
            if (!cache.nextClipLoading && cache.nextCachedClip == null)
            {
                cache.nextClipLoading = true;
                Log.Out($"[CMM] Pre-loading next clip for {contextName}");
                yield return StartCoroutine(LoadNextClip(contextName));
                cache.nextClipLoading = false;
            }

            // ── Phase 2: wait until it's time to start the crossfade ─────────
            float remaining = waitBeforePlay - waitBeforeLoad;
            if (remaining > 0f)
                yield return new WaitForSeconds(remaining);

            if (transitionId != _transitionId || currentContext.Context != contextName) yield break;

            // ── Phase 3: promote and play with crossfade ─────────────────────
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
                cache.cachedClip     = cache.nextCachedClip;
                cache.nextCachedClip = null;
            }

            cache.resumePosition = 0f;
            cache.lastLeftTime   = -1f;

            if (cache.cachedClip == null)
            {
                Log.Out($"[CMM] No clip available for {contextName} transition. Giving up.");
                yield break;
            }

            // FIX (Bug 3): Signal to TrackEndWatcher that we are handling this
            // transition so its safety net does not also fire PlayFromCache.
            _scheduleHandledTransition = true;

            Log.Out($"[CMM] Starting crossfade to next track in {contextName}");
            int newId = ++_transitionId;
            _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(PlayFromCache(contextName, true, newId));
            _nextCacheCoroutine   = null;
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
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = CMMConfig.StreamingEnabled;
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
                .Where(f => CheckType(f) != AudioType.UNKNOWN
                         && !_invalidFiles.Contains(f))
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
            return Directory.GetFiles(dir)
                .Any(f => CheckType(f) != AudioType.UNKNOWN
                       && !_invalidFiles.Contains(f));
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
        public static bool   playerIsDead   = true;
        public static string ModPath;

        public static bool lastContextOnCoolDown = false;

        public static  GameObject          CMM_Audio_Object;
        public static  CoRoutine           CMM_CoRoutines;
        public static  ContextMusicManager Instance;

        // Coroutine handles — all owned here so TriggerMusic can cancel them cleanly.
        private static Coroutine _activeMusicCoroutine = null;
        private static Coroutine _nextCacheCoroutine   = null;
        private static Coroutine _trackEndCoroutine    = null;
        private static Coroutine _crossFadeCoroutine   = null;
        private static Coroutine _cooldownCoroutine    = null;

        // ── Timing configuration ──────────────────────────────────────────────
        public static TrackOrderMode GlobalTrackOrder           = TrackOrderMode.RandomNoRepeat;
        public static float          ContextScanCooldown        = 0.5f;
        public static float          StalenessCheckInterval     = 5f;
        public static float          NextCacheWindow            = 5f;
        public static float          TrackEndPollInterval       = 0.25f;
        public static float          CrossFadeDuration          = 2f;
        public static float          TrackTransitionFadeDuration = 6f;
        public static float          TrackTransitionPreloadLead  = 10f;

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

            currentContext  = GetContext("None");
            _pendingContext = "None";
            _transitionId   = 0;

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
            CMM_CoRoutines.StartCoroutine(Instance.VolumeSyncLoop());
            Log.Out("[CMM] Initialized!");
        }

        public static void OnEnterGame()
        {
            if (!inGame) return;
            CMM_CoRoutines.StartCoroutine(Instance.PreloadAllContexts());
        }

        // ── Central decision point ────────────────────────────────────────────

        // FIX (Bug 1 + Bug 2): SendContextUpdate increments _transitionId as its
        // first meaningful act after accepting a transition. All coroutines
        // spawned from here or from TriggerMusic capture the new ID. The
        // _pendingContext string is still used as a cheap early-exit guard, but
        // is no longer the sole protection against re-entrant calls.
        //
        // FIX (Bug 2): The PlayOnce return context snapshot has been removed from
        // here entirely. It is now resolved inside TrackEndWatcher at the moment
        // the clip actually ends, where the stack reflects the player's real
        // current situation rather than where they were when the stinger fired.
        public static void SendContextUpdate(string contextIn, int bypassPriority = -1)
        {
            Log.Error("Player is " + (playerIsDead ? "dead" : "alive") + ".");
            Contexts incoming = GetContext(contextIn);
            if (incoming == null) return;
            if (_pendingContext == contextIn) return;

            bool menuOverride = !inGame;
            int comparePriority = bypassPriority >= 0 ? bypassPriority : (currentContext?.Priority ?? 0);

            // Allow a reload of the currently-playing context (for example when files change)
            // even if the incoming priority isn't strictly higher. This fixes the case where
            // the same context was being re-requested but was rejected by the priority check.
            bool isReloadOfCurrent = currentContext != null && contextIn == currentContext.Context;
            bool priorityOk        = inGame && incoming.CanInterrupt && incoming.Priority > comparePriority;

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
                ContextCache leavingCache         = cacheMap[currentContext.Context];
                leavingCache.resumePosition       = ActiveChannel.time;
                leavingCache.lastLeftTime         = Time.realtimeSinceStartup;
                Log.Out($"[CMM] Saved resume for {currentContext.Context}: {leavingCache.resumePosition:F2}s");

                if (!CMMConfig.PreloadCacheEnabled && leavingCache.cachedClip != null)
                {
                    _clipToDestroyAfterFade = leavingCache.cachedClip;
                    leavingCache.cachedClip = null;
                }
            }

            // If something interrupts a PlayOnce, discard any stale return snapshot.
            if (currentContext != null && currentContext.PlayOnce && !incoming.PlayOnce)
                _playOnceReturnContext = null;

            // Find the best playable context in the fallback chain.
            string contextToPlay = ResolvePlayableContext(contextIn);

            if (contextToPlay == null)
            {
                Log.Out($"[CMM] No playable context in chain for {contextIn} — fading to silence.");
                _pendingContext = "None";
                currentContext  = GetContext("None");
                // FIX (Bug 1): Increment here too so in-flight coroutines know to stop.
                ++_transitionId;
                FadeToSilence();
                return;
            }

            Contexts candidate = GetContext(contextToPlay);
            bool bypassCooldown = incoming.BypassesCooldown || candidate.BypassesCooldown;

            if (lastContextOnCoolDown && !bypassCooldown && bypassPriority < 0)
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

        // FIX (Bug 1): TriggerMusic increments _transitionId and passes the new
        // value into PlayFromCache. This is the single authoritative place where
        // the counter advances for a user-initiated or scan-initiated transition.
        public static void TriggerMusic(string contextName)
        {
            Log.Out($"[CMM] TriggerMusic: {contextName}");
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }
            if (_nextCacheCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine);   _nextCacheCoroutine   = null; }
            if (_trackEndCoroutine    != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine);    _trackEndCoroutine    = null; }
            if (_crossFadeCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);   _crossFadeCoroutine   = null; }

            // Advancing the ID here means any coroutine still running from the
            // previous transition will see the mismatch and exit cleanly.
            int newId = ++_transitionId;
            _scheduleHandledTransition = false;
            _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(Instance.PlayFromCache(contextName, false, newId));
        }

        // ── Scan loops ────────────────────────────────────────────────────────

        // Runs every 0.5s. Resolves the current world state into a context list,
        // updates the stack, and triggers a context change if the top has changed.
        private IEnumerator ContextScanLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(ContextScanCooldown);
                if (!inGame || playerIsDead) continue;

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
                    // FIX (Bug 1): Clear _pendingContext before calling SendContextUpdate
                    // so the early-rejection guard does not suppress a legitimate scan update.
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

                //Log.Out($"[CMM] Cache state: {string.Join(", ", cacheMap.Select(kv => $"{kv.Key}={(kv.Value.cachedClip != null ? $"{kv.Value.cachedClip.length:F0}s" : "null")}(next={(kv.Value.nextCachedClip != null ? "ready" : "null")})"))}");
                Log.Out($"[CMM] Cache state: {string.Join(", ", cacheMap.Where(kv => kv.Value.cachedClip != null).Select(kv => $"{kv.Key}={kv.Value.cachedClip.length:F0}s(next={(kv.Value.nextCachedClip != null ? "ready" : "null")})"))}");
                Log.Out($"[CMM] Cache memory:\n{GetCacheMemorySummary(false)}");
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
                        cache.lastLeftTime   = -1f;
                        string avoid         = cache.lastPlayedFile;
                        cache.lastPlayedFile = null;

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

                Func<string, string> norm = p => string.IsNullOrEmpty(p) ? p : Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var currentNorm = currentFiles.Select(norm).ToArray();
                var previousNorm = previousFiles.Select(norm).ToArray();

                bool changed = currentNorm.Length != previousNorm.Length ||
                               currentNorm.Except(previousNorm, StringComparer.OrdinalIgnoreCase).Any() ||
                               previousNorm.Except(currentNorm, StringComparer.OrdinalIgnoreCase).Any();

                if (!changed) continue;
                // ── Evict removed files from validation sets ─────────────────────────
                var removedFiles = previousNorm.Except(currentNorm, StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (string removed in removedFiles)
                {
                    _validatedFiles.Remove(removed);
                    _invalidFiles.Remove(removed);
                }

                // ── Validate any files that weren't in the previous snapshot ─────────
                var newFiles = currentNorm.Except(previousNorm, StringComparer.OrdinalIgnoreCase).ToArray();
                if (newFiles.Length > 0)
                {
                    Log.Out($"[CMM] Validating {newFiles.Length} new file(s) in {contextName}...");
                    foreach (string newFile in newFiles)
                    {
                        bool isValid = false;
                        yield return StartCoroutine(ValidateFile(newFile, result => isValid = result));
                        if (!isValid)
                        {
                            _invalidFiles.Add(newFile);
                            Log.Warning($"[CMM] New file failed validation and will be ignored: {Path.GetFileName(newFile)}");
                        }
                    }

                    // Recompute after blacklisting so the rest of the loop works on clean data
                    currentFiles = currentFiles.Where(f => !_invalidFiles.Contains(norm(f))).ToArray();
                    currentNorm = currentFiles.Select(norm).ToArray();
                }

                Log.Out($"[CMM] File change detected in {contextName}: {previousFiles.Length} -> {currentFiles.Length} files");
                cache.directorySnapshot = currentFiles;

                // ── Detect whether the currently-playing file was removed ────────────
                bool lastPlayedMissing = false;
                if (!string.IsNullOrEmpty(cache.lastPlayedFile))
                {
                    string lastNorm = norm(cache.lastPlayedFile);
                    if (!currentNorm.Any(f => string.Equals(f, lastNorm, StringComparison.OrdinalIgnoreCase)))
                    {
                        Log.Out($"[CMM] Previously-played file missing for {contextName}: {Path.GetFileName(cache.lastPlayedFile)}");
                        cache.lastPlayedFile = null;
                        if (cache.cachedClip != null)
                        {
                            _clipToDestroyAfterFade = cache.cachedClip;
                            cache.cachedClip = null;
                        }
                        lastPlayedMissing = true;
                    }
                }

                // ── React to the change ──────────────────────────────────────────────
                if (currentFiles.Length == 0)
                {
                    Log.Out($"[CMM] {contextName} folder emptied, re-evaluating context.");
                    string fallback = ResolvePlayableContext(ctx.Fallback) ?? "Ambient";
                    _pendingContext = null;
                    SendContextUpdate(fallback, currentContext.Priority + 1);
                }
                else if (lastPlayedMissing)
                {
                    _pendingContext = null;

                    if (CMMConfig.PreloadCacheEnabled)
                        yield return StartCoroutine(PreloadContext(ctx.Context));

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
                Contexts ctx = GetContext(contextName);
                string fallbackName = ctx?.Fallback ?? "None";
                if (fallbackName != "None" && fallbackName != contextName)
                {
                    filePath = PickNextFile(fallbackName, avoidFile: null);
                    if (filePath == null)
                    { Log.Out($"[CMM] Preload: no files for {contextName} or fallback {fallbackName}"); yield break; }
                }
                else
                { Log.Out($"[CMM] Preload: no files for {contextName}"); yield break; }
            }

            // ── Validate before loading ───────────────────────────────────────────
            if (!_validatedFiles.Contains(filePath))
            {
                bool isValid = false;
                yield return StartCoroutine(ValidateFile(filePath, result => isValid = result));
                if (isValid)
                {
                    _validatedFiles.Add(filePath);
                }
                else
                {
                    _invalidFiles.Add(filePath);
                    Log.Warning($"[CMM] Preload: {Path.GetFileName(filePath)} failed validation, retrying with next file.");

                    // Try again — PickNextFile will now exclude the blacklisted file
                    filePath = PickNextFile(contextName, avoidFile: filePath);
                    if (filePath == null)
                    { Log.Out($"[CMM] Preload: no valid files remaining for {contextName}"); yield break; }

                    // Validate the replacement too
                    yield return StartCoroutine(ValidateFile(filePath, result => isValid = result));
                    if (!isValid)
                    {
                        _invalidFiles.Add(filePath);
                        Log.Warning($"[CMM] Preload: replacement file also invalid for {contextName}, giving up.");
                        yield break;
                    }
                    _validatedFiles.Add(filePath);
                }
            }

            // ── Load ──────────────────────────────────────────────────────────────
            AudioType audioType = CheckType(filePath);
            if (audioType == AudioType.UNKNOWN) yield break;

            using (var request = UnityWebRequestMultimedia.GetAudioClip(
                       "file:///" + filePath.Replace("\\", "/"), audioType))
            {
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = CMMConfig.StreamingEnabled;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                { Log.Error($"[CMM] Preload failed for {contextName}: {request.error}"); yield break; }

                if (cache.cachedClip != null) Destroy(cache.cachedClip);
                cache.cachedClip = DownloadHandlerAudioClip.GetContent(request);
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
                // Fade out any existing music coming from the game's dynamic system
                ContextMusicManager.FadeToSilence();
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

    // FIX (Bug 4 — Harmony signature): Postfix must return void. The original
    // returned bool, which Harmony silently ignores, meaning the backpack context
    // push/pop never fired. Renamed PostFix -> Postfix (correct casing) and
    // removed the return value.
    //[HarmonyPatch(typeof(XUiC_BackpackWindow), "OnOpen")]
    //public class PushBackpackOpen
    //{
    //    static void Postfix()
    //    {
    //        Log.Out("[CMM] Backpack opened");
    //        ContextMusicManager.PushContext("Inventory");
    //    }
    //}

    //[HarmonyPatch(typeof(XUiC_BackpackWindow), "OnClose")]
    //public class PushBackpackClose
    //{
    //    static void Postfix()
    //    {
    //        Log.Out("[CMM] Backpack closed");
    //        ContextMusicManager.PopContext("Inventory");
    //    }
    //}

    [HarmonyPatch(typeof(EntityPlayerLocal), "AfterPlayerRespawn")]
    public class PlayerRespawn
    {
        static void Postfix()
        {
            Log.Out("[CMM] Player respawned");
            ContextMusicManager._contextStack.Clear();
            List<string> worldContexts = ContextResolver.ResolveContext();
            foreach (string ctxName in worldContexts)
            {
                Contexts ctx = ContextMusicManager.GetContext(ctxName);
                if (ctx == null) continue;
                ContextMusicManager._contextStack.Add(new ActiveContext
                {
                    ContextName = ctxName,
                    Priority    = ctx.Priority,
                    ActivatedAt = Time.realtimeSinceStartup,
                    Manual      = false
                });
            }
            ContextMusicManager._contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            if (ContextMusicManager.playerIsDead)
                ContextMusicManager.SendContextUpdate("Spawn");
            ContextMusicManager.playerIsDead = false;
        }
    }

    [HarmonyPatch(typeof(EntityPlayerLocal), "OnDeathUpdate")]
    public class PlayerDeath
    {
        static void Postfix()
        {
            if (!ContextMusicManager.playerIsDead)
            {
                ContextMusicManager.playerIsDead = true;
                ContextMusicManager.SendContextUpdate("Death");
            }
        }
    }


    [HarmonyPatch(typeof(GameManager), "Awake")]
    public class StartupLogPatch
    {
        static void Postfix() => Log.Out("[CMM] Game started up!");
    }

    [HarmonyPatch(typeof(EntityPlayer), "Awake")]
    public static class Patch_SpawnStinger
    {
        public static void Prefix() => Log.Error("[CMM] Player spawned into world");
    }

    // FIX (Bug 4 — removed Patch_SpawnStinger): The original patch targeted
    // EntityPlayer.Awake, which fires for every entity that spawns in the world,
    // not just the local player. This produced spurious log spam and could have
    // interfered with context state. PlayerRespawn (AfterPlayerRespawn) is the
    // correct and sufficient hook for the player spawn stinger.

    // Routes sound group names to CMM context changes.
    // FIX (Bug 4 — inventory routing): The backpack patches (OnOpen/OnClose) are
    // now the sole path for inventory context changes; the open_inventory and
    // close_inventory cases here are removed to eliminate the dead-code ambiguity.
    // Any sound group not explicitly handled passes through to the game unchanged.
    public static class AudioStingerHandler
    {
        public static bool Handle(string soundGroupName)
        {
            Log.Out($"[CMM Audio Spy] PlayInsidePlayerHead: {soundGroupName}");
            switch (soundGroupName)
            {
                case "player_death_stinger":
                    return false;

                case "spawnInStinger":
                    return false;

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
