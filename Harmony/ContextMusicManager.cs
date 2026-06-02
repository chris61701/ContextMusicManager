using Audio;
using CMM;
using DynamicMusic;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Tracing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Remoting.Contexts;
using System.Text;
using UAI;
using UniLinq;
using UnityEngine;
using UnityEngine.Assertions.Must;
using UnityEngine.Networking;
using static EntityDrone;

namespace CMM
{


    public static class ContextResolver
    {

        static readonly float enemyDetectRange = 20f;
        static readonly int dangerThreshold = 1;
        static readonly int criticalThreshold = 4;
        private static readonly float TraderDetectRange = 40f;
        static string combatState = "None";
        public static bool inCombat = false;
        public static bool inVehicle = false;

        public static void resetValues()
        {
            inCombat = false;
            inVehicle = false;
            combatState = "None";
        }
        public static string ResolveContext()
        {
            inCombat = false;
            inVehicle = false;
            int enemyAggroCount = 0;
            int enemyCount = 0;
            int enemiesNearby = 0;
            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();

            if (world == null || player == null) return "None";

            var entities = world.Entities.list;

            if (player.AttachedToEntity is EntityVehicle vehicle)
            {
                string vehicleName = vehicle.GetType().Name.ToLower();
                inVehicle = true;
                if (vehicleName.Contains("gyrocopter")) return "Vehicle_Gyrocopter";
                else if (vehicleName.Contains("bicycle")) return "Vehicle_Bicycle";
                else if (vehicleName.Contains("motorcycle") ||
                         vehicleName.Contains("minibike")) return "Vehicle_Motorcycle";
                else if (vehicleName.Contains("4x4") ||
                         vehicleName.Contains("truck")) return "Vehicle_4x4";
                else return "Vehicle";
            }

            foreach ( var entity in entities )
            {
                if (entity is EntityEnemy enemy)
                {
                    if (enemy.IsAlive())
                    {
                        if (Vector3.Distance(player.position, enemy.position) < enemyDetectRange)
                        {
                            //Log.Error("Enemy: " + enemy.entityName + " nearby");
                            enemyCount++;
                            enemiesNearby++;
                        }
                        if (enemy.GetAttackTarget() == player)
                        {
                            Log.Error("Enemy: " + enemy.entityName + " is aggroed on player");
                            inCombat = true;
                            enemyAggroCount++;
                        }
                    }
                }
            }

            Log.Error(enemyCount + " enemies nearby.");

            if (enemyAggroCount >= criticalThreshold)
            {
                combatState = "High";
                return "Combat_High";
            }

            if (enemyAggroCount >= dangerThreshold)
            {
                if (combatState != "High") combatState = "Low";
                return combatState == "High" ? "Combat_High" : "Combat_Low";
            }

            // If combat state was high at the beginning of the encounter, don't change it
            combatState = "None";

            foreach (var entity in world.Entities.list)
            {
                if (entity is EntityTrader trader)
                {
                    float dist = Vector3.Distance(player.position, trader.position);
                    if (dist > TraderDetectRange) continue;

                    string name = trader.EntityName?.ToLower() ?? "";
                    if (name.Contains("jen")) return "Trader_Jen";
                    if (name.Contains("rekt")) return "Trader_Rekt";
                    if (name.Contains("bob")) return "Trader_Bob";
                    if (name.Contains("hugh")) return "Trader_Hugh";
                    if (name.Contains("joel")) return "Trader_Joel";
                    return "Trader";
                }
            }

            if (enemiesNearby >= criticalThreshold) return "Tension_High";
            if (enemiesNearby >= dangerThreshold) return "Tension_Low";

            return "Ambient";
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
}
public class Contexts
{
    public string Context { get; private set; }
    public string Path { get; private set; }
    public string Fallback { get; private set; }
    //  Will the track resume if it switches back to this context?
    public bool CanBeResumed { get; private set; }
    // How long will the track be held in pause before the it is changed once it returns to this same context
    public int TimeUntilContextStale { get; private set; }
    public bool Loop { get; private set; }
    // Cooldown before the last context can switch to this context
    public int Cooldown { get; private set; }
    // Delay before next context should be played
    public int Delay { get; private set; }
    // Force the song to play the whole way through?
    public bool CanInterrupt { get; private set; }
    public int Priority{ get; private set; }
    public bool PlayOnce { get; private set; } = false;

    public bool hasValidFiiles = false;

    public Contexts(string context = "None", string path = "", string fallback = "None", bool canberesumed = false, int timeuntilcontextstale = 120, bool loop = false, int cooldown = 5, int delay = 3, bool caninterrupt = false, int priority = 0, bool playOnce = false)
    {
        Context = context;
        Path = path;
        Fallback = fallback;
        CanBeResumed = canberesumed;
        TimeUntilContextStale = timeuntilcontextstale;
        Loop = loop;
        Cooldown = cooldown;
        Delay = delay;
        CanInterrupt = caninterrupt;
        Priority = priority;
        PlayOnce = playOnce;
    }
}

class MusicFile
{
    public string FilePath { get; set; }
    public AudioType AudioType { get; set; }
}

namespace CMM
{
    public class CoRoutine : MonoBehaviour { }
    public class ContextMusicManager : MonoBehaviour
    {
        public static List<Contexts> contexts = new List<Contexts>();

        public static string MusicDirectory = "/Music";

        public static bool inGame = false;
        public static bool musicIsPlaying = false;
        public static Contexts currentContext;

        public static AudioClip clip;
        public static string lastClipPlayed;

        public void InitContexts()
        {
            contexts.Add(new Contexts("Vehicle",            "vehicle",            "Ambient",    true,  30,  false, 5, 1, true, 100, false));
            contexts.Add(new Contexts("Vehicle_Gyrocopter", "vehicle/gyrocopter", "Vehicle",    true,  30,  false, 5, 1, true, 100, false));
            contexts.Add(new Contexts("Vehicle_Bicycle",    "vehicle/bicycle",    "Vehicle",    true,  30,  false, 5, 1, true, 100, false));
            contexts.Add(new Contexts("Vehicle_Motorcycle", "vehicle/motorcyle",  "Vehicle",    true,  30,  false, 5, 1, true, 100, false));
            contexts.Add(new Contexts("Vehicle_4x4",        "vehicle/4x4",        "Vehicle",    true,  30,  false, 5, 1, true, 100, false));
            contexts.Add(new Contexts("Combat_High",        "combat_high",        "Combat_Low", false, 15,  false, 3, 1, true, 97,  false));
            contexts.Add(new Contexts("Combat_Low",         "combat_low",         "Tension_Low",false, 15,  false, 3, 1, true, 95,  false));
            contexts.Add(new Contexts("Spawn",              "stinger",            "Ambient",    false, 0,   false, 3, 0, true, 90,  true ));
            contexts.Add(new Contexts("Tension_High",       "tension_high",       "Tension_Low",false, 60,  false, 5, 1, true, 87,  false));
            contexts.Add(new Contexts("Tension_Low",        "tension_low",        "Ambient",    false, 60,  false, 5, 1, true, 85,  false));
            contexts.Add(new Contexts("Death",              "death",              "Ambient",    false, 0,   false, 3, 0, true, 75,  true ));
            contexts.Add(new Contexts("Ambient",            "ambient",            "None",       true,  120, false, 5, 0, true, 50,  false));
            contexts.Add(new Contexts("Trader_Joel",        "trader/joel",        "Trader",     true,  120, false, 5, 0, true, 81,  false));
            contexts.Add(new Contexts("Trader_Jen",         "trader/jen",         "Trader",     true,  120, false, 5, 0, true, 81,  false));
            contexts.Add(new Contexts("Trader_Rekt",        "trader/rekt",        "Trader",     true,  120, false, 5, 0, true, 81,  false));
            contexts.Add(new Contexts("Trader_Bob",         "trader/bob",         "Trader",     true,  120, false, 5, 0, true, 81,  false));
            contexts.Add(new Contexts("Trader_Hugh",        "trader/hugh",        "Trader",     true,  120, false, 5, 0, true, 81,  false));
            contexts.Add(new Contexts("Trader",             "trader",             "Ambient",    true,  120, false, 5, 0, true, 50,  false));
            contexts.Add(new Contexts("None",               "",                   "None",       false, 0,   false, 0, 0, true, 0,   false));
            contexts.Add(new Contexts("Menu",               "menu",               "Ambient",    false, 0,   true,  0, 3, true, 0,   false));
        }

        public static ContextMusicManager Instance;
        private static AudioSource CMM_Primary_Channel;
        private static GameObject CMM_Audio_Object;
        public static CoRoutine CoRoutine;
        public static string ModPath;

        public static float ContextScanCooldown = 0.5f;
        public static void Init(string modPath)
        {
            CMM_Audio_Object = new GameObject("ContextMusicManager");
            UnityEngine.Object.DontDestroyOnLoad(CMM_Audio_Object);
            Instance = CMM_Audio_Object.AddComponent<ContextMusicManager>();
            Instance.InitContexts();
            currentContext = GetContext("None");
            CMM_Primary_Channel = CMM_Audio_Object.AddComponent<AudioSource>();
            CoRoutine = CMM_Audio_Object.AddComponent<CoRoutine>();
            CMM_Primary_Channel.volume = GamePrefs.GetFloat(EnumGamePrefs.OptionsMenuMusicVolumeLevel);
            CMM_Primary_Channel.loop = false;
            ModPath = modPath;

            // Start Scanning for Context Changes
            CoRoutine.StartCoroutine(Instance.ContextScanLoop());
            Log.Error("[Context Music Manager] Initialized!");
        }
        private static readonly HashSet<string> ValidExtensions = new HashSet<string>
        {
            ".mp3", ".ogg", ".wav"
        };

        private IEnumerator ContextScanLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(ContextScanCooldown);
                if (!inGame) continue;

                string resolved = ContextResolver.ResolveContext();
                Contexts incoming = GetContext(resolved);
                if (resolved == currentContext.Context) continue;
                Log.Out("[Context Music Manager] inCombat is: " + ContextResolver.inCombat);
                if (resolved != currentContext.Context)
                {
                    bool isEscalation = ContextResolver.inCombat && incoming.Priority > currentContext.Priority;
                    bool isCombatEnding = !ContextResolver.inCombat && currentContext.Context.ToLower().Contains("combat");
                    bool isVehicleEnding = !ContextResolver.inVehicle && currentContext.Context.ToLower().Contains("vehicle");

                    if (isEscalation)
                    {
                        SendContextUpdate(resolved);
                    }
                    else if (isCombatEnding || isVehicleEnding )
                    {
                        SendContextUpdate(resolved, 0);
                    }
                    else
                    {
                        SendContextUpdate(resolved);
                    }
                }
            }
        }

        private bool HasValidFiles(string directory)
        {
            if (!Directory.Exists(directory)) return false;

            return Directory.GetFiles(directory)
                .Any(f => ValidExtensions.Contains(
                    System.IO.Path.GetExtension(f).ToLower()));
        }
        public static float GetVolume()
        {
            return CMM_Primary_Channel.volume;
        }

        private bool CheckAllContextsDirectories()
        {
            foreach (var context in contexts)
            {
                string dir = ModPath + MusicDirectory + "/" + context.Path + "/";
                if (!HasValidFiles(dir))
                {
                    Log.Out("[Context Music Manager] No valid music files found for context: " + context.Context + " in directory: " + dir);
                    return false;
                }
                context.hasValidFiiles = true;
            }
            return true;
        }
        public static Contexts GetContext(string contextIn)
        {
            Contexts ctx = contexts.Find(c => c.Context == contextIn);
            if (ctx == null)
            {
                Log.Out("[Context Music Manager] Context not found: " + contextIn);
                return null;
            }

            //Log.Out("Context Music Manager] Attempting to get context:  " + ctx.Context);
            return ctx;
        }

        public static void SendContextUpdate(string contextIn, int bypassPriority = -1)
        {
            Contexts incoming = GetContext(contextIn);
            if (incoming == null) { Log.Out("[Context Music Manager] Context " + contextIn + " not found "); return; }
            if (currentContext.Context == contextIn) return;

            bool menuOverride = !inGame;
            // Optional ability to bypass priority checks for certain context changes
            int comparePriority = bypassPriority >= 0 ? bypassPriority : currentContext.Priority;
            bool priorityOverride = inGame && incoming.CanInterrupt
                                           && incoming.Priority > comparePriority;

            if (inGame)
            {
                Log.Out("[Context Music Manager] Context registers as being inGame");
            }

            if (incoming.Priority > currentContext.Priority)
            {
                Log.Out($"[Context Music Manager] {contextIn} has higher priority over {currentContext.Context}");
            }
            else
            {
                Log.Out("[Context Music Manager] " + contextIn + " does not have higher priority over " + currentContext.Context);
            }

            if (incoming.CanInterrupt)
            {
                Log.Out($"[Context Music Manager] {contextIn} can interrupt");
            }
            else
            {
                Log.Out($"[Context Music Manager] {contextIn} cannot interrupt");
            }

            if (!menuOverride && !priorityOverride) return;

            Log.Out("[Context Music Manager] Context changed: " + currentContext.Context + " -> " + contextIn);
            CMM_Primary_Channel.Stop();
            currentContext = incoming;
            TriggerMusic(currentContext.Context);
        }

        public static void ForceContextUpdate(string contextIn)
        {
            Contexts incoming = GetContext(contextIn);
            if (incoming == null) { Log.Out("[CMM] Context not found: " + contextIn); return; }

            Log.Out("[CMM] Context downgrade: " + currentContext.Context + " -> " + contextIn);
            CMM_Primary_Channel.Stop();
            currentContext = incoming;
            TriggerMusic(currentContext.Context);
        }
        void SetVolume(float volume) => CMM_Primary_Channel.volume = Mathf.Clamp01(volume);

        public AudioType CheckType(string inputFile)
        {
            string extension = System.IO.Path.GetExtension(inputFile).ToLower();
            switch (extension)
            {
                case ".mp3":
                    return AudioType.MPEG;
                case ".ogg":
                    return AudioType.OGGVORBIS;
                case ".wav":
                    return AudioType.WAV;
                default:
                    return AudioType.UNKNOWN;
            }
        }
        public string ScanForFiles(string contextIn)
        {
            Contexts ctx = GetContext(contextIn);
            if (ctx == null)
            {
                Log.Out("[Context Music Manager] Context " + contextIn + " not found ");
                return null;
            }

            string dir = ModPath + MusicDirectory + "/" + ctx.Path + "/";

            if (!System.IO.Directory.Exists(dir))
            {
                Log.Out("[Context Music Manager] Directory not found " + dir);
                ContextFallback(ctx.Fallback);
                return null;
            }

            string[] files = System.IO.Directory.GetFiles(dir);
            if (files.Length == 0) { Log.Out("[Context Music Manager] No files in " + dir); return null; }
            return files[UnityEngine.Random.Range(0, files.Length)];
        }

        private void ContextFallback(string fallback)
        {
            currentContext = contexts.Find(c => c.Context == fallback);
            if (fallback == "None") currentContext = GetContext("None");
            SendContextUpdate(fallback);
        }

        public IEnumerator ChangeMusic(string contextIn)
        {
            Log.Out("[Context Music Manager] ChangeMusic(" + contextIn + ")");
            string filePath = ScanForFiles(contextIn);
            if (filePath == null) yield break;
            AudioType audioType = CheckType(filePath);
            if (audioType == AudioType.UNKNOWN) yield break;

            // Inline the web request here instead of calling GetMusic()
            using (var request = UnityWebRequestMultimedia.GetAudioClip("file:///" + filePath.Replace("\\", "/"), audioType))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Log.Error("[Context Music Manager] Failed to load: " + request.error);
                    yield break;
                }
                clip = DownloadHandlerAudioClip.GetContent(request);
                currentContext = GetContext(contextIn);
                Log.Error("[Context Music Manager] Attempting to play next track " + currentContext.Context);
                PlayMusic(clip, currentContext);
            }
        }

        public IEnumerator WaitForMusicEnd(string contextIn)
        {
            yield return new WaitForSeconds(0.1f);

            while (CMM_Primary_Channel.isPlaying)
            {
                yield return new WaitForSeconds(0.5f);
            }
            Log.Out("[Context Music Manager] Music has finished playing.");
            if (!currentContext.PlayOnce)
            {
                Log.Out("[Context Music Manager] Context not changed, finding another random file: " + currentContext.Context);
                TriggerMusic(currentContext.Context);
            }
            else
            {
                Log.Out("[Context Music Manager] Context is set to play once, switching to fallback context: " + currentContext.Fallback);
                currentContext = GetContext(currentContext.Fallback);
                //SendContextUpdate(currentContext.Context);
            }
        }

        public static void TriggerMusic(string contextName)
        {
            Log.Out("[Context Music Manager] TriggerMusic: " + contextName);
            CoRoutine.StartCoroutine(Instance.ChangeMusic(contextName));
        }

        public void PlayMusic(AudioClip clip, Contexts contextIn)
        {
            Log.Out("[Context Music Manager] PlayMusic: " + contextIn.Context);
            CMM_Primary_Channel.loop = contextIn.Loop;
            CMM_Primary_Channel.clip = clip;
            CMM_Primary_Channel.Play();
            CoRoutine.StartCoroutine(WaitForMusicEnd(contextIn.Context));
        }

        private static float lastVolumeCheck;
        private static Coroutine volumeFadeCoroutine;

        private static IEnumerator FadeVolumeOnChange(AudioSource source, float targetVolume, float duration)
        {
            if (source == null)
                yield break;

            float startVolume = source.volume;

            if (Mathf.Approximately(startVolume, targetVolume))
                yield break;

            float elapsed = 0f;

            Log.Out($"[CMM] Fade start: {startVolume} → {targetVolume}");

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;

                source.volume = Mathf.Lerp(startVolume, targetVolume, t);

                yield return null;
            }

            source.volume = targetVolume;

            Log.Out($"[CMM] Fade complete: {targetVolume}");
        }

        internal static void CheckMusicVolume()
        {
            float targetVolume = GamePrefs.GetFloat(
                inGame
                    ? EnumGamePrefs.OptionsMusicVolumeLevel
                    : EnumGamePrefs.OptionsMenuMusicVolumeLevel);

            if (Mathf.Approximately(lastVolumeCheck, targetVolume))
                return;

            lastVolumeCheck = targetVolume;

            if (CMM_Primary_Channel == null)
                return;

            // If nothing is playing, just set instantly
            if (!CMM_Primary_Channel.isPlaying)
            {
                CMM_Primary_Channel.volume = targetVolume;
                return;
            }

            // Stop previous fade (IMPORTANT)
            if (volumeFadeCoroutine != null)
            {
                CoRoutine.StopCoroutine(volumeFadeCoroutine);
                volumeFadeCoroutine = null;
            }

            volumeFadeCoroutine = CoRoutine.StartCoroutine(
                FadeVolumeOnChange(CMM_Primary_Channel, targetVolume, 1.0f)
            );
        }
    }
        public static class MusicStates
    {
        public static bool DefaultMusicEnabled = false;
        public static void ToggleDefaultMusic()
        {
            DefaultMusicEnabled = !DefaultMusicEnabled;
            Debug.LogError($"[Context Music Manager] Default music enabled: {DefaultMusicEnabled}");
        }
    }

    [HarmonyPatch(typeof(DynamicMusic.Conductor), "Update")]
    public class InGameMusicPatch
    {
        static bool firstMessageLogged = false;
        static bool Prefix()
        {
            if (MusicStates.DefaultMusicEnabled == true)
                return true;

            //if (GameManager.IsDedicatedServer)
            //    return true;

            //if (SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            //    return true;
            //ContextMusicManager.CoRoutine.StartCoroutine(ContextMusicManager.PlayMusic(ContextMusicManager.ModPath + "/Music/stinger/ExitTheVault.mp3"));
            if (firstMessageLogged == false)
            {
                Log.Out("[Context Music Manager] Dynamic Music System ticks ignored!");
                firstMessageLogged = true;
            }
            if (!ContextMusicManager.inGame)
            {
                ContextMusicManager.inGame = true;
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
            if (!ContextMusicManager.inGame)
            {
                //Log.Out("[Context Music Manager] - Menu Music Patch triggered, sending Menu context update");
                ContextMusicManager.SendContextUpdate("Menu");
            }
            
            //Log.Out("[Context Music Manager] inGame = " + ContextMusicManager.inGame);
            //Log.Out("[Context Music Manager] current Context : " + ContextMusicManager.currentContext.Context);
            //Log.Out($"[CMM] Current runtime volume: {ContextMusicManager.GetVolume()}");
            ContextMusicManager.CheckMusicVolume();
            //Log.Error("[Context Music Manager] BackgroundMuiscMono running");
            //Debug.LogError("[MyMod] This ran!");
            return false;
        }
    }

    [HarmonyPatch(typeof(GameManager), "SaveAndCleanupWorld")]
    public class Patch_GameEnded
    {
        static void Prefix()
        {
            Debug.LogError("[Context Music Manager] Logging out");
            ContextMusicManager.SendContextUpdate("Menu");
            ContextMusicManager.inGame = false;
            ContextResolver.resetValues();
            // fires when the world is torn down
        }
    }

    [HarmonyPatch(typeof(GameManager), "Awake")]
    public class StartupLogPatch
    {
        static void Postfix()
        {
            Debug.LogError("[Context Music Manager] Game has started up!");
        }
    }

    //[HarmonyPatch(typeof(XUiC_BackpackWindow), "Open")]
    //public static class Patch_Backpack_OpenSound
    //{
    //    public static void Prefix()
    //    {
    //        Log.Out("[MusicSuppress] Blocked backpack open sound (XUiC_BackpackWindow.Open)");
    //    }
    //}

    //[HarmonyPatch(typeof(XUiC_BackpackWindow), "OnClose")]
    //public static class Patch_Backpack_CloseSound
    //{
    //    public static void Prefix()
    //    {
    //        Log.Out("[MusicSuppress] Blocked backpack close sound (XUiC_BackpackWindow.Open)");
    //    }
    //}


    [HarmonyPatch(typeof(EntityPlayer), "Awake")]
    public static class Patch_SpawnStinger
    {
        public static void Prefix()
        {
            Debug.LogError("[Context Music Manager] Player spawned into world)");
        }
    }

    [HarmonyPatch(typeof(Audio.Manager), "PlayInsidePlayerHead", new Type[] { typeof(string), typeof(int), typeof(float), typeof(bool), typeof(bool) })]
    public class Patch_LogPlayInsidePlayerHead
    {
        static bool Prefix(string soundGroupName)
        {
            Debug.LogError("[Context Music Manager Audio Spy] PlayInsidePlayerHead: " + soundGroupName);
            if (soundGroupName == "player_death_stinger")
            {
                ContextMusicManager.SendContextUpdate("Death");
                return false;
            }
            if (soundGroupName == "spawnInStinger")
            {
                ContextMusicManager.SendContextUpdate("Spawn");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Audio.Manager), "PlayInsidePlayerHead", new Type[] { typeof(string), typeof(int) })]
    public class Patch_LogPlayInsidePlayerHead2
    {
        static bool Prefix(string soundGroupNameBegin)
        {
            Debug.LogError("[Context Music Manager Audio Spy ] PlayInsidePlayerHead2: " + soundGroupNameBegin);
            if (soundGroupNameBegin == "player_death_stinger")
            {
                ContextMusicManager.SendContextUpdate("Death");
                return false;
            }
            if (soundGroupNameBegin == "spawnInStinger")
            {
                ContextMusicManager.SendContextUpdate("Spawn");
                return false;
            }
            return true;
        }
    }
}


