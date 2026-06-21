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
        static bool Prefix(DynamicMusic.Conductor __instance)
        {
            CachedInstance = __instance;
            ContextMusicManager.LastKnownGameSection = __instance.CurrentSectionType.ToString();

            if (MusicStates.DefaultMusicEnabled || ContextMusicManager.vanillaIsActive) return true;
            if (!firstMessageLogged) { CMMConfig.D("[CMM] Dynamic Music System ticks ignored!"); firstMessageLogged = true; }

            if (!ContextMusicManager.inGame)
            {
                CMMConfig.D("[CMM] Entering game world");
                ContextMusicManager.inGame = true;
                ContextMusicManager.FadeToSilence();
                ContextMusicManager.OnEnterGame();
            }
            return false;
        }
    }

    // this is called constantly while on the menu screen
    [HarmonyPatch(typeof(BackgroundMusicMono), "Play")]
    public class MenuMusicPatch
    {
        public static BackgroundMusicMono CachedInstance;

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
            ContextMusicManager.CleanupAllCaches();
            ContextResolver.resetValues();
            ContextMusicManager.SendContextUpdate("Menu");
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
                    FirstSeenAt = Time.realtimeSinceStartup,
                    Manual = false
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
        public static void Prefix() => Log.Error("[CMM] Player spawned into world");
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
    [HarmonyPatch(typeof(Audio.Manager), "PlayInsidePlayerHead",
        new Type[] { typeof(string), typeof(int), typeof(float), typeof(bool), typeof(bool) })]
    public class Patch_LogPlayInsidePlayerHead
    {
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
            Log.Warning("[CMM] Game is loading, from LoadChecker.");
            ContextMusicManager._gameIsLoading = true;
            return true;
        }
    }
}
