using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // MOD ENTRY POINT
    // ─────────────────────────────────────────────────────────────────────────

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

    // ─────────────────────────────────────────────────────────────────────────
    // MUSIC STATES
    // ─────────────────────────────────────────────────────────────────────────

    public static class MusicStates
    {
        public static bool DefaultMusicEnabled = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HARMONY PATCHES
    // ─────────────────────────────────────────────────────────────────────────

    // This happens when the world is almost finished loading the world is loading
    [HarmonyPatch(typeof(DynamicMusic.Conductor), "Update")]
    public class InGameMusicPatch
    {
        public static DynamicMusic.Conductor CachedInstance;
        static bool firstMessageLogged = false;
        // Cache the last section enum so we only pay the (allocating, reflection-backed)
        // enum ToString() when it actually changes — this runs every frame on the
        // Conductor's hot Update path, and the string is only read by the 3s diag log.
        static MusicUtils.Enums.SectionType _lastSectionType;
        static bool _sectionTypeInit;
        static bool Prefix(DynamicMusic.Conductor __instance)
        {
            CachedInstance = __instance;
            var section = __instance.CurrentSectionType;
            if (!_sectionTypeInit || section != _lastSectionType)
            {
                _sectionTypeInit = true;
                _lastSectionType = section;
                ContextMusicManager.LastKnownGameSection = section.ToString();
            }

            if (MusicStates.DefaultMusicEnabled || ContextMusicManager.vanillaIsActive) return true;
            if (!firstMessageLogged) { CMMConfig.D("[CMM] Dynamic Music System ticks muted!"); firstMessageLogged = true; }

            if (!ContextMusicManager.inGame)
            {
                CMMConfig.D("[CMM] Entering game world");
                ContextMusicManager.inGame = true;
                ContextMusicManager.FadeToSilence();
                ContextMusicManager.OnEnterGame();
            }

            // Let vanilla DMS keep ticking; just mute it at the mixer instead of
            // suppressing Update. Conductor.Update runs server-side bloodmoon work
            // (eligibility + NetPackage sends, gated only by IsServer) before any audio
            // logic, so returning false killed that game state on a single-player host.
            // Muting dmsVol persists frame-to-frame (MixerController.Update only touches
            // AllCbtVol), and keeping vanilla warm lets the "Vanilla" fallback fade in
            // seamlessly mid-track rather than cold-starting. The vanillaIsActive guard
            // above keeps this per-frame re-mute from fighting FadeInVanillaCoroutine
            // when vanilla is the active fallback.
            ContextMusicManager.SetVanillaMusicVolume(0f);
            return true;
        }
    }

#if GV_A20
    // ─────────────────────────────────────────────────────────────────────────
    // A20 COMBAT-MUSIC THROTTLE FIX (backport of the A21 FrequencyLimiter rule)
    // ─────────────────────────────────────────────────────────────────────────
    //
    // A20's DynamicMusic.FrequencyLimiter rations ALL sections (including Combat)
    // by the "Dynamic Music daily time" setting: during its cooldown or on a failed
    // random roll it does FilterAllBut(None), stripping Combat down to None so the
    // Conductor never fades a combat cue in. With a reduced daily-time setting that
    // cooldown can run many minutes, so vanilla combat music only starts a handful
    // of times per session and otherwise goes silent mid-fight. (A21+ fixed this by
    // exempting Combat — and None — from the limiter; ambient stays rationed, combat
    // always fires.)
    //
    // This backports A21's exact rule onto A20: record whether Combat was requested
    // before the limiter ran, and restore it afterward if the limiter stripped it.
    // Combat only reaches the limiter when PlayerTracker already selected it (threat
    // == Panicked), so restoring it can never spuriously force combat — it only undoes
    // the throttle on a real fight. Non-combat sections stay subject to the daily-time
    // setting, as intended.
    [HarmonyPatch(typeof(DynamicMusic.FrequencyLimiter), "Filter")]
    public class Patch_A20_FrequencyLimiter_ExemptCombat
    {
        static void Prefix(IList<MusicUtils.Enums.SectionType> _sectionTypes, out bool __state)
        {
            __state = _sectionTypes != null && _sectionTypes.Contains(MusicUtils.Enums.SectionType.Combat);
        }

        static void Postfix(bool __state, IList<MusicUtils.Enums.SectionType> __result)
        {
            if (__state && __result != null && !__result.Contains(MusicUtils.Enums.SectionType.Combat))
                __result.Add(MusicUtils.Enums.SectionType.Combat);
        }
    }
#endif

    // On 3.0+, patches Play(); on A20/A21 where Play() doesn't exist, falls back to Update().
    [HarmonyPatch]
    public class MenuMusicPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(BackgroundMusicMono), "Play")
            ?? AccessTools.Method(typeof(BackgroundMusicMono), "Update");
        public static BackgroundMusicMono CachedInstance;

        // BackgroundMusicMono.Play(MusicTrack) exists in 1.0+/2.x/3.x but NOT in the older
        // alphas (A20/A21), whose BackgroundMusicMono has only Start()/Update(). Returning
        // false makes Harmony skip this patch there so PatchAll doesn't abort on a missing
        // target; menu-music reclaim is simply unavailable on those versions (vanilla menu
        // music plays). The cached-instance helpers below stay null-safe when unpatched.
        // static bool Prepare() => AccessTools.Method(typeof(BackgroundMusicMono), "Play") != null;

        // Mute/unmute the vanilla menu music when CMM reclaims or yields the menu.
        // Uses mute rather than volume because UpdateTrack() overwrites volume every frame.
        //
        // Only the looping background-music sources are muted. BackgroundMusicMono's
        // GameObject also hosts a non-looping AudioSource that the game plays menu UI
        // sound effects through (via PlayOneShot, so its .clip is null at rest); muting
        // it silenced all menu SFX. Music loops, the SFX source does not — so loop
        // cleanly separates them. (Gating on isPlaying was unreliable: a UI one-shot can
        // be mid-play at the instant we mute, which made the SFX loss intermittent.)
        public static void SetMenuMusicMuted(bool mute)
        {
            if (CachedInstance == null) return;
            foreach (var src in CachedInstance.GetComponents<AudioSource>())
            {
                if (!src.loop) continue;
                src.mute = mute;
            }
        }

        // The BackgroundMusicMono AudioSources, so CMM can fade their volume directly
        // when handing the menu off from vanilla to its own music. Returns null when
        // the instance hasn't been cached yet (menu screen never reached this session).
        public static AudioSource[] GetMenuSources()
        {
            return CachedInstance != null ? CachedInstance.GetComponents<AudioSource>() : null;
        }

        static bool Prefix(BackgroundMusicMono __instance)
        {
            CachedInstance = __instance;
            if (ContextMusicManager.vanillaIsActive) return true;
            if (!ContextMusicManager.inGame)
                ContextMusicManager.SendContextUpdate("Menu");
            return false;
        }
    }

    // A20/A21: BackgroundMusicMono has no Play(MusicTrack) method, so MenuMusicPatch
    // falls back to patching Update(). But Start() calls audioSource.Play() directly
    // at a non-zero initial volume (0.1f A20, 0.01f A21) — the source is already
    // playing before Update() can suppress it, producing the faint audible bleed.
    // Patching Start() as a Postfix lets us mute at the earliest possible point.
    // Safe on 3.0/2.6: if Play() never ran in Start() the mute is a no-op, and
    // Prepare() skips the patch entirely if Start() doesn't exist.
    [HarmonyPatch]
    public class Patch_BackgroundMusicMono_Start
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(BackgroundMusicMono), "Start");

        static bool Prepare() =>
            AccessTools.Method(typeof(BackgroundMusicMono), "Start") != null;

        static void Postfix(BackgroundMusicMono __instance)
        {
            if (ContextMusicManager.vanillaIsActive) return;
            MenuMusicPatch.CachedInstance = __instance;
            MenuMusicPatch.SetMenuMusicMuted(true);
        }
    }

    // this happens when leaving a gameworld and going back to the menu screen
    [HarmonyPatch(typeof(GameManager), "SaveAndCleanupWorld")]
    public class Patch_GameEnded
    {
        static void Prefix()
        {
            ContextMusicManager.inGame = false;
            ContextMusicManager.playerIsDead = true;
            ContextMusicManager._contextStack.Clear();
            ContextMusicManager._pendingContext = "Silence";
            ScriptHost.Reset();   // clear the Lua script's cross-tick state on world-unload
            // Fade the game music out, THEN release caches and bring up the menu music
            // (FadeOutAndCleanup ends with SendContextUpdate("Menu")), instead of the
            // old hard-cut CleanupAllCaches() + immediate Menu switch.
            ContextMusicManager.FadeOutAndCleanup();
        }
    }

    // This happens when the player spawn (both initially, and after death)
    [HarmonyPatch(typeof(EntityPlayerLocal), "AfterPlayerRespawn")]
    public class PlayerRespawn
    {
        static void Postfix()
        {
            ContextMusicManager._gameIsLoading = false;
            CMMConfig.D("[CMM] Player respawned");
            ContextMusicManager._contextStack.Clear();

            // Set inGame = true here, before any music operations.
            // Without this, InGameMusicPatch.Prefix fires on the next Conductor.Update()
            // frame while Spawn's PlayFromCache coroutine is still loading, calls
            // FadeToSilence(), and kills it. The PreloadContext sub-coroutine survives
            // (child coroutines outlive their stopped parent), so the clip loads but
            // PlayFromCache never completes — Spawn becomes current at vol=0 forever.
            if (!ContextMusicManager.inGame)
            {
                ContextMusicManager.inGame = true;
                ContextMusicManager.OnEnterGame();
            }

            if (ContextMusicManager.playerIsDead)
            {
                // Death-respawn: rebuild the stack immediately so Spawn's TrackEndWatcher
                // can return to the correct zone when the stinger ends.
                List<string> worldContexts = ContextResolver.ResolveContext();
                foreach (string ctxName in worldContexts)
                {
                    Contexts ctx = ContextMusicManager.GetContext(ctxName);
                    if (ctx == null) continue;
                    float seenAt = Time.realtimeSinceStartup;
                    ContextMusicManager._contextStack.Add(new ActiveContext
                    {
                        ContextName = ctxName,
                        Priority = ctx.Priority,
                        ActivatedAt = seenAt,
                        FirstSeenAt = ContextMusicManager.InheritedFirstSeen(ctxName, seenAt),
                        Manual = false
                    });
                }
                ContextMusicManager._contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                ContextMusicManager.SendContextUpdate("Spawn");
                ContextMusicManager.playerIsDead = false;
            }
            else
            {
                // Teleport: leave the stack empty. Rebuilding it here populates stale
                // pre-teleport zone contexts (e.g. Ambient_Forest_Night) that then sit
                // on the stack alongside the correct destination contexts (same priority 12)
                // for up to 1.5s — the eviction grace window. When GetTopContext() happens
                // to pick the stale entry it matches currentContext and no transition fires,
                // so the wrong zone plays until eviction clears it. Leaving the stack empty
                // lets the scan loop read the actual destination position on its next tick
                // and populate the stack cleanly with no stale competition.
                ContextMusicManager.playerIsDead = false;
            }
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

    // This patch exist for debuff purposes
    [HarmonyPatch(typeof(GameManager), "Awake")]
    public class StartupLogPatch
    {
        static void Postfix() => CMMConfig.D("[CMM] Game started up!");
    }

    // This patch exist for debuff purposes
    [HarmonyPatch(typeof(EntityPlayer), "Awake")]
    public static class Patch_SpawnStinger
    {
        public static void Prefix() => CMMConfig.D("[CMM] Player spawned into world");
    }

    public static class AudioStingerHandler
    {
        public static bool Handle(string soundGroupName)
        {
            CMMConfig.D($"[CMM Audio Spy] PlayInsidePlayerHead: {soundGroupName}");
            switch (soundGroupName)
            {
                case "player_death_stinger": return !ContextMusicManager.ContextHasFiles("Death");
                case "spawnInStinger": return !ContextMusicManager.ContextHasFiles("Spawn");
                default: return true;
            }
        }
    }

    // This keeps a log of sounds that have played so we can catch them later with patches
    // 3.0 exposes PlayInsidePlayerHead(string,int,float,bool,bool); the older alphas
    // (A20/A21) have the 4-arg (string,int,float,bool). Resolve whichever exists at runtime
    // so this patch applies on every version instead of aborting PatchAll on a null target.
    [HarmonyPatch]
    public class Patch_LogPlayInsidePlayerHead
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(Audio.Manager), "PlayInsidePlayerHead",
                new[] { typeof(string), typeof(int), typeof(float), typeof(bool), typeof(bool) })
            ?? AccessTools.Method(typeof(Audio.Manager), "PlayInsidePlayerHead",
                new[] { typeof(string), typeof(int), typeof(float), typeof(bool) });
        static bool Prefix(string soundGroupName) => AudioStingerHandler.Handle(soundGroupName);
    }

    // this does the same thing as the previous function but exists for the overload version of the function
    [HarmonyPatch(typeof(Audio.Manager), "PlayInsidePlayerHead",
        new Type[] { typeof(string), typeof(int) })]
    public class Patch_LogPlayInsidePlayerHead2
    {
        static bool Prefix(string soundGroupNameBegin) => AudioStingerHandler.Handle(soundGroupNameBegin);
    }

    // this exists to keep track of when the world starts loading, however, it has not been useful thus far
[   HarmonyPatch(typeof(World), "LoadWorld")]
    public class LoadChecker
    {

        static bool Prefix()
        {
            CMMConfig.D("[CMM] Game is loading, from LoadChecker.");
            ContextMusicManager._gameIsLoading = true;
            return true;
        }
    }
}
