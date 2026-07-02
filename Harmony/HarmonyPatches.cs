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
            // NOTE: MoonSharp.Interpreter.dll MUST ship in the mod ROOT, not a subfolder.
            // The game discovers this IModApi by scanning every type in the assembly
            // (Mod.InitModCode → IsAssignableFrom over GetTypes()), which eagerly LOADS
            // each type — including CMM.ScriptHost, whose `_script` field is a MoonSharp
            // type. That load resolves MoonSharp.Interpreter BEFORE this method ever runs,
            // so an AppDomain.AssemblyResolve handler installed here would be too late. The
            // game auto-LoadFrom's every root *.dll before that scan, so root just works.
            var harmony = new Harmony("com.velasha.contextmusicmanager");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            ContextMusicManager.Init(__instance.Path);
        }
    }

    // Hosts CMM's coroutines AND, via Update, pumps the MainThreadDispatcher: once per
    // frame it drains any closures CmmScheduler (or another background thread) marshalled
    // back for main-thread execution. Cheap no-op when the queue is empty (the common case
    // when NativeScheduler is off). This is the only main-thread half of the off-frame-loop
    // control plane.
    public class CoRoutine : MonoBehaviour
    {
        void Update() => MainThreadDispatcher.Drain();
    }

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
                ContextMusicManager._menuMusicHold = false;   // back in-game; clear any stuck exit hold
                // Fade the menu music out GRACEFULLY (≈5s) as the world comes up — CONSUMER-side
                // (output gain) so it's audible through the deep load cushion — tailing into the
                // Spawn stinger, which reuses the same device and snaps the gain back to full.
                // EXPERIMENT: the fade was moved EARLIER, to WorldBuiltMenuFade (fires right after
                // createWorld, before the player spawns), so the 5s fade has room to finish before
                // the Spawn stinger instead of landing on top of it. Old trigger kept for reference:
                //ContextMusicManager.FadeMenuOutputForGameStart(ContextMusicManager.MenuTransitionFade);
                ContextMusicManager.OnEnterGame();
            }

            // Let vanilla DMS keep ticking; we mute it at the mixer (not by suppressing
            // Update — Conductor.Update runs server-side bloodmoon work, gated only by
            // IsServer, before any audio logic, so returning false killed that game state
            // on a single-player host). The DMS mute is now EDGE-TRIGGERED: asserted once
            // on world entry (OnEnterGame, which both this prefix and PlayerRespawn route
            // through) and ramped up/down by the vanilla fade coroutines for fallback.
            // dmsVol persists frame-to-frame (MixerController.Update only touches AllCbtVol),
            // so we no longer re-mute every frame here. Keeping the Conductor warm still lets
            // the "Vanilla" fallback fade in seamlessly mid-track rather than cold-starting.
            return true;
        }
    }

    // The game's MixerController re-applies dmsVol from the music-volume slider on
    // OnGamePrefChanged (slider moved) and OnSnapshotTransition — both routed through
    // SetDynamicMusicVolume — which clobbers CMM's edge-triggered mute. Re-clamp it back
    // to muted whenever the GAME raises it while CMM owns in-game music (not vanilla
    // fallback). CMM's own writes are flagged (_ownDmsWrite) and ignored here, so this
    // never fights the vanilla fade ramps (the fade-out runs with vanillaIsActive already
    // false) and never recurses. SetDynamicMusicVolume(float) exists identically in
    // A20/A21/3.0; resolved by name so the build needs no direct MixerController reference.
    [HarmonyPatch]
    public class Patch_DmsVolumeReclamp
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("DynamicMusic.MixerController"), "SetDynamicMusicVolume");

        static bool Prepare() => TargetMethod() != null;

        static void Postfix(float _vol)
        {
            if (ContextMusicManager._ownDmsWrite) return;          // our own set, not a game clobber
            if (_vol == 0f) return;                                // game already muted it — nothing to do
            if (!ContextMusicManager.inGame || ContextMusicManager.vanillaIsActive) return;
            if (MusicStates.DefaultMusicEnabled) return;
            ContextMusicManager.SetVanillaMusicVolume(0f, force: true);
        }
    }

    // Govern the game's DMS section selection so that, while CMM lets vanilla play (the
    // "Vanilla" fallback), the game's sections obey CMM's context rules — chiefly the
    // WarmUp dwell — instead of snapping straight to trader/home music. Conductor.Update
    // calls SectionSelector.Select() each frame to pick the section; rewriting its result
    // to the previously-playing section holds the current ambient until the mapped CMM
    // context is viable, and defers indefinitely to CMM's own music when it has any (see
    // GovernVanillaSection). Resolved by name with a Prepare() guard; SectionSelector.Select
    // exists on A20/A21/3.0. Runs client-side only — Conductor.Update early-outs (and never
    // reaches Select) on a dedicated server.
    [HarmonyPatch]
    public class Patch_GovernVanillaSection
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("DynamicMusic.SectionSelector"), "Select");

        static bool Prepare() => TargetMethod() != null;

        static void Postfix(ref MusicUtils.Enums.SectionType __result)
        {
            __result = ContextMusicManager.GovernVanillaSection(__result);
        }
    }

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
    //
    // A20-ONLY at runtime. FrequencyLimiter exists on all versions (so typeof compiles), but
    // only A20 throttles Combat AND takes IList<SectionType> (A21+ takes List<SectionType> and
    // already exempts Combat). Prepare() gates this to A20 — the sole version lacking
    // EntityAlive.IsSwimming() — so Harmony never binds the IList prefix/postfix against a newer
    // List<> Filter (a skipped patch class is never signature-checked).
    [HarmonyPatch(typeof(DynamicMusic.FrequencyLimiter), "Filter")]
    public class Patch_A20_FrequencyLimiter_ExemptCombat
    {
        static bool Prepare() => AccessTools.Method(typeof(EntityAlive), "IsSwimming") == null;

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
            // _menuMusicHold suppresses this auto-trigger during the game-exit fade, so the menu
            // music doesn't start until the game music has fully faded out (returns false either
            // way = vanilla menu music stays muted, leaving the exit fade audible over silence).
            if (!ContextMusicManager.inGame && !ContextMusicManager._menuMusicHold)
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
            // Safety: clear the loading flag on the way back to the menu in case StartGame
            // armed it but a spawn (which clears it) never happened — otherwise the menu
            // would stay deep-buffered. The idle menu wants the tight, responsive cushion.
            ContextMusicManager._gameIsLoading = false;
            ContextMusicManager._contextStack.Clear();
            ContextMusicManager._pendingContext = "Silence";
            ContextMusicManager.ResetVanillaGovernance();   // forget the governed vanilla section + warmup timer
            ScriptHost.Reset();   // clear the Lua script's cross-tick state on world-unload
            ContextResolver.ResetPoiClearState();   // forget per-world cleared-POI markers
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
            // Clear the deep cushion SYNCHRONOUSLY here, not via the 0.1s-polled VolumeSyncLoop:
            // the Spawn stinger is dispatched a few ms below, and if DeepBuffer were still true the
            // mixer would bank a deep cushion behind the stinger and then flush it on the falling
            // edge once the poll caught up — clipping the stinger's start ("Spawn plays offset").
            CmmMixer.DeepBuffer = false;
            CMMConfig.D("[CMM] Player respawned");
            ContextMusicManager._contextStack.Clear();

            // Set inGame = true here, before any music operations.
            // Without this, InGameMusicPatch.Prefix fires on the next Conductor.Update()
            // frame while Spawn's start is still in flight, calls FadeToSilence(), and
            // kills it — stranding Spawn as the current context with nothing audible.
            if (!ContextMusicManager.inGame)
            {
                ContextMusicManager.inGame = true;
                ContextMusicManager.OnEnterGame();
            }

            if (ContextMusicManager.playerIsDead)
            {
                // Death-respawn: rebuild the stack immediately so the post-stinger
                // re-resolve can return to the correct zone when Spawn ends.
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
                // Force Spawn through: after a death, currentContext is still the Death stinger
                // (priority 100, a lingering PlayOnce), so a normal SendContextUpdate("Spawn")
                // (Spawn priority 45) would be REJECTED as not higher-priority — that's why the
                // Spawn stinger only fired on initial entry (from low-priority Menu), not on a
                // death respawn. bypassPriority=0 drops the bar so the forced Spawn always takes.
                ContextMusicManager.SendContextUpdate("Spawn", 0);
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

    // Game pause/unpause (single-player ESC menu / inventory). Unity used to silence our
    // AudioSource on pause for free; the native mixer is on its own thread Unity can't pause,
    // so we duck its output gain ourselves (see OnGamePauseChanged).
    //
    // The pause flip lives in different methods across versions: 3.0 splits it into
    // Pause(bool) (queues a request) + the private updatePauseState() that actually applies
    // gamePaused; A20/A21's Pause(bool) applies it inline. Both leave gamePaused at its FINAL
    // value by the time the method returns, so a postfix reading IsPaused() is correct on
    // every version. Target updatePauseState where it exists (3.0) so we read the applied
    // state, else fall back to Pause(bool) (A20/A21). updatePauseState early-returns when no
    // pause was requested, so the per-frame postfix is a cheap no-op; OnGamePauseChanged
    // edge-guards anyway.
    [HarmonyPatch]
    public class Patch_GamePause
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(GameManager), "updatePauseState")
            ?? AccessTools.Method(typeof(GameManager), "Pause", new[] { typeof(bool) });

        static bool Prepare() => TargetMethod() != null;

        static void Postfix(GameManager __instance) =>
            ContextMusicManager.OnGamePauseChanged(__instance.IsPaused());
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
            // Guarded at the call site: this prefix fires for EVERY in-head sound the game
            // plays, and the interpolation would allocate before D() checked the flag.
            if (CMMConfig.DebugLogging)
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

    // World BUILT, player NOT yet spawned — the earliest post-freeze, pre-spawn seam, chosen
    // as the new trigger for the graceful menu fade so the ≈5s fade has room to COMPLETE before
    // the Spawn stinger, instead of landing on top of it (the old trigger fired at the first
    // Conductor.Update tick, which is at/after spawn — see InGameMusicPatch, now commented out).
    //
    // 3.0 fires ModEvents.CreateWorldDone at exactly this point, but that event does NOT exist on
    // A20/A21 (their ModEvents is a different API shape entirely — referencing it would crash
    // InitMod on the older alphas). Instead we patch NetPackageWorldInfo.PrepareWorldHashes(), a
    // public static called at the SAME seam on all three versions: GameManager.StartAsServer runs
    // it immediately after `yield return createWorld(...)` and before the local player is created
    // (A20 GameManager.cs:1310, A21:1343, 3.0:2023). Resolved by name via TargetMethod()+Prepare()
    // so a version missing it simply skips the patch instead of aborting PatchAll.
    //
    // SCOPE: this is the SERVER-side load path (single-player / listen host). A pure MP client
    // never calls PrepareWorldHashes, so it won't fade here — but that path is unchanged by this
    // experiment (its menu fade still needs the InGameMusicPatch Conductor trigger to be restored).
    [HarmonyPatch]
    public class WorldBuiltMenuFade
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("NetPackageWorldInfo"), "PrepareWorldHashes");

        static bool Prepare() => TargetMethod() != null;

        static void Postfix()
        {
            // inGame is still false here (player not spawned, Conductor hasn't ticked), and
            // PrepareWorldHashes runs once per world load, so this fades the menu exactly once.
            if (ContextMusicManager.inGame) return;
            // Hold off the vanilla-menu-music auto-retrigger (MenuMusicPatch) for the rest of the
            // load: without inGame set, that patch would otherwise re-request "Menu" and swell the
            // music back in over this fade. Cleared when inGame flips (InGameMusicPatch / Patch_GameEnded).
            ContextMusicManager._menuMusicHold = true;
            CMMConfig.D("[CMM] World built (PrepareWorldHashes) — fading menu music early for game start.");
            ContextMusicManager.FadeMenuOutputForGameStart(ContextMusicManager.MenuTransitionFade);
        }
    }

    // Menu → game START (single-player or client). This fires ~30s BEFORE World.LoadWorld
    // and, crucially, before the heavy asset load/unload freeze that drains the native
    // output ring. We flag _gameIsLoading here so the mixer banks its DEEP output cushion
    // ahead of that freeze — reacting at LoadWorld is too late (the ring already underran,
    // a brief stutter). Cleared in PlayerRespawn (in-game) and Patch_GameEnded (back to menu).
    //
    // GameManager.StartGame(bool) exists on 3.0+ but NOT on A20/A21. Resolving the
    // target via TargetMethod() + Prepare() (instead of an explicit attribute target)
    // lets Harmony skip this patch on the older alphas rather than throwing
    // "Undefined target method", which would abort PatchAll and fail the whole mod.
    // The early deep-buffer arm is a 3.0 native-mixer optimization, so skipping it on
    // A20/A21 is harmless (LoadChecker still flags loading on those versions).
    [HarmonyPatch]
    public class GameStartChecker
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(GameManager), "StartGame", new Type[] { typeof(bool) });

        static bool Prepare() =>
            AccessTools.Method(typeof(GameManager), "StartGame", new Type[] { typeof(bool) }) != null;

        static void Prefix()
        {
            CMMConfig.D("[CMM] Game is loading, from StartGame (early deep-buffer arm).");
            ContextMusicManager._gameIsLoading = true;
        }
    }
}
