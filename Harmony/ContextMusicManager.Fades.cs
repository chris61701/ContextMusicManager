using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // FADES / CROSSFADE-TO-SILENCE / VANILLA HANDOFF (split from AudioPlayer)
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager
    {

        // ── Crossfade ─────────────────────────────────────────────────────────

        // fadeSeconds < 0 uses the normal in-game CrossFadeDuration; pass a longer value
        // (e.g. MenuTransitionFade) for a graceful, deliberate fade such as entering the game.
        public static void FadeToSilence(float fadeSeconds = -1f)
        {
            float fade = fadeSeconds >= 0f ? fadeSeconds : CrossFadeDuration;
            CMMConfig.D($"[CMM] Fading to silence ({fade:F1}s).");
            if (_trackEndCoroutine != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine); _trackEndCoroutine = null; }
            if (_crossFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine); _crossFadeCoroutine = null; }
            // The native mixer is the only output — fade it out (covers native→silence
            // and, via ActivateVanilla→FadeToSilence, native→vanilla).
            StopNativeIfActive(fade);
        }


        // Menu → game graceful fade (entering a world). The menu music plays through the native
        // mixer behind a DEEP load cushion, so a producer-side gain ramp would be buried — the
        // fade must run CONSUMER-side (output gain in the audio callback) to be heard. The mixer
        // voices also fade producer-side so they retire, but the device stays OPEN so the Spawn
        // stinger reuses it (its Play snaps the output gain back to full). Stops the menu control
        // coroutines first so nothing re-triggers mid-fade.
        internal static void FadeMenuOutputForGameStart(float seconds)
        {
            if (_trackEndCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine);   _trackEndCoroutine   = null; }
            if (_crossFadeCoroutine  != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);  _crossFadeCoroutine  = null; }

            var mx = CmmMixer.Instance;
            if (mx != null)
            {
                mx.SetOutputGain(0f, seconds);            // audible consumer-side fade
                mx.Stop(seconds, closeWhenIdle: false);   // retire the voices, keep the device for Spawn
            }
            // Clear the active-native marker so the Spawn context change doesn't try to reclaim
            // the faded menu voice; the worker's commit lock guards an in-flight start.
            lock (_natCommitLock) { _nativeActiveContext = null; Interlocked.Exchange(ref _nativeVoiceId, 0); }
            CMMConfig.D($"[CMM] native: menu output fading out ({seconds:F1}s) for game start.");
        }

        // ── Game-pause duck ───────────────────────────────────────────────────
        // When the game pauses (single-player ESC menu / inventory), Unity used to silence
        // our AudioSource for free. The native mixer runs on its own miniaudio thread that
        // Unity can't pause, so it would keep playing through the pause screen. Duck the
        // CONSUMER-side output gain to 0 on pause and restore it on unpause: that ramp is
        // applied in the audio callback in REAL time, so it works even though Time.timeScale
        // is frozen (a producer-side / coroutine fade would be stuck for the whole pause).
        // Edge-tracked so the per-frame pause-state postfix is a no-op outside transitions,
        // and so we never restore a gain we didn't lower. Pause is forced false in MP and on
        // the menu (GameManager.updatePauseState), so this only ever engages during SP play.
        private static volatile bool _pauseDucked;
        internal static void OnGamePauseChanged(bool paused)
        {
            // Only ever engage in-game. When not in a world (menu / mid-quit teardown) there's no
            // pause to duck for, and — critically — this stops a stray unpause fired during the
            // quit-to-menu cleanup from RESTORING the output gain and swelling the just-faded music
            // back in over the exit fade. The quit path continues the fade to silence itself.
            if (!inGame) return;
            if (paused == _pauseDucked) return;          // no edge — cheap per-frame guard
            _pauseDucked = paused;
            CmmMixer.Instance?.SetOutputGain(paused ? 0f : 1f, PauseFadeDuration);
            CMMConfig.D($"[CMM] native: pause {(paused ? "duck" : "restore")} ({PauseFadeDuration:F2}s)");
        }

        // ── Vanilla fade-out ──────────────────────────────────────────────────
        // Lerps the game's DMS mixer volume to 0 over the crossfade duration so
        // Vanilla audio fades out while CMM fades in, rather than cutting abruptly.
        public IEnumerator FadeInVanillaCoroutine(float duration)
        {
            float startVol = _currentVanillaVolume;
            float targetVol = GamePrefs.GetFloat(EnumGamePrefs.OptionsMusicVolumeLevel);
            CMMConfig.D($"[CMM] FadeInVanilla: {startVol:F2} → {targetVol:F2} over {duration:F1}s");
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                SetVanillaMusicVolume(Mathf.Lerp(startVol, targetVol, Mathf.Clamp01(elapsed / duration)));
                yield return null;
            }
            SetVanillaMusicVolume(targetVol);
            CMMConfig.D("[CMM] FadeInVanilla: complete.");
            _vanillaFadeInCoroutine = null;
        }

        public IEnumerator FadeOutVanillaCoroutine(float duration)
        {
            float startVol = _currentVanillaVolume;
            CMMConfig.D($"[CMM] FadeOutVanilla: start={startVol:F2} over {duration:F1}s | mixerMethod={_setDMVMethod != null} | mixerInst={_mixerInstance != null}");
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                SetVanillaMusicVolume(Mathf.Lerp(startVol, 0f, t));
                yield return null;
            }
            SetVanillaMusicVolume(0f);
            CMMConfig.D("[CMM] FadeOutVanilla: complete.");
        }

        // ── Menu vanilla fade-out ─────────────────────────────────────────────
        // On the menu screen there is no DMS mixer to ride, so we fade the game's
        // BackgroundMusicMono AudioSources directly. UpdateTrack() re-sets their
        // volume every frame; this coroutine yields one frame per step so its write
        // lands AFTER UpdateTrack each frame and therefore wins. Only once the fade
        // has reached zero do we flip the persistent `mute` flag — the steady-state
        // silence mechanism — so there is no audible cut at any point. Crucially we
        // do NOT fade OptionsMenuMusicVolumeLevel: CMM's own menu music scales off
        // that same pref, so fading it would zero the incoming track too.
        public IEnumerator FadeOutMenuVanillaCoroutine(float duration)
        {
            var sources = MenuMusicPatch.GetMenuSources();
            if (sources == null || sources.Length == 0) { _menuVanillaFadeCoroutine = null; yield break; }

            float[] startVols = sources.Select(s => s.volume).ToArray();
            CMMConfig.D($"[CMM] FadeOutMenuVanilla: {sources.Length} source(s) over {duration:F1}s");

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                for (int i = 0; i < sources.Length; i++)
                    if (sources[i] != null) sources[i].volume = Mathf.Lerp(startVols[i], 0f, t);
                yield return null;
            }

            // Fade complete: hand off to the persistent mute flag, then restore the
            // stored volume so a later ActivateVanilla unmute comes back at full.
            MenuMusicPatch.SetMenuMusicMuted(true);
            for (int i = 0; i < sources.Length; i++)
                if (sources[i] != null) sources[i].volume = startVols[i];
            CMMConfig.D("[CMM] FadeOutMenuVanilla: complete (muted).");
            _menuVanillaFadeCoroutine = null;
        }

        // Menu cold-start (A20): the mod can load many seconds into the main menu, long
        // after the game's BackgroundMusicMono.Start() already began playing the vanilla
        // title theme — so the Start-postfix mute never caught it and it's still audible
        // when CMM's own menu track is finally ready. Rather than hard-cutting it, fade it
        // out over the crossfade window as the custom track fades in. No-op on A21/3.0
        // (the Start postfix muted the bed before it was ever audible), whenever the bed
        // is already silent/muted, or when a menu fade is already running. Only invoked
        // from StartNativeContext when a real custom menu track is actually starting, so
        // we never silence vanilla unless there's a custom track to replace it.
        public static void FadeOutMenuVanillaIfAudible()
        {
            if (inGame) return;
            if (_menuVanillaFadeCoroutine != null) return;
            var sources = MenuMusicPatch.GetMenuSources();
            if (sources == null) return;
            bool audible = sources.Any(s => s != null && s.loop && !s.mute && s.volume > 0f);
            if (!audible) return;
            CMMConfig.D("[CMM] Menu cold-start: fading leftover vanilla title theme out as custom track takes over.");
            _menuVanillaFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeOutMenuVanillaCoroutine(CrossFadeDuration));
        }


    }
}
