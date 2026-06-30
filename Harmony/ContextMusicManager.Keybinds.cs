using System.Collections;
using System.IO;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // PLAYBACK KEYBINDS
    // ─────────────────────────────────────────────────────────────────────────
    // Live controls for whatever music is currently audible (menu or in-game):
    //   • next / previous track   — skip within the playing context
    //   • per-track volume up/down — adjusts the current track's manual loudness trim
    // Keys + modifiers are configurable in Config/settings.xml (Ctrl+Alt+Arrows by
    // default). This is the MonoBehaviour's single Update(); the debug HUD's toggle
    // is folded in here because a component may declare Update() only once.

    public partial class ContextMusicManager
    {
        // Debounce: ignore further skip presses while a forced skip is mid-flight,
        // so key-mashing can't stack overlapping clip loads / transitions.
        private static bool _skipInProgress = false;

        // ── Volume popup state ─────────────────────────────────────────────────
        // Shown for VolumePopupHoldSeconds after the last volume key press, then it
        // fades out. Set by TrimCurrentTrack, drawn by DrawVolumePopup.
        private const float VolumePopupHoldSeconds = 4f;
        private static float  _volPopupUntil = -1f;
        private static string _volPopupTrack = null;
        private static float  _volPopupTrim  = -1f;    // <0 = not measured yet; else 0.1..1.0
        private const string SavedText = "Track Volume Saved";
        private int _popupSavedFontSize = 0;           // computed once to fit the box
        private Texture2D _popupBg, _popupBarBg, _popupBarFill;
        private GUIStyle  _popupTitleStyle, _popupValueStyle, _popupSavedStyle;

        private void Update()
        {
            // Per-frame stutter watchdog (no-op unless PerfTiming is on). Runs first so
            // it times the full frame-to-frame gap, including everything below it.
            CMMPerf.FrameTick(LiveMusicCoroutineCount);

            HandleMusicKeybinds();
#if CMM_DEBUG_HUD
            HandleDebugHudToggle();
#endif
        }

        private void OnGUI()
        {
            DrawVolumePopup();
#if CMM_DEBUG_HUD
            DrawDebugHud();
#endif
        }

        private void HandleMusicKeybinds()
        {
            if (!CMMConfig.KeybindsEnabled) return;

            // Cheap modifier gate first — short-circuits almost every frame.
            if (CMMConfig.KeybindRequireCtrl &&
                !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                return;
            if (CMMConfig.KeybindRequireAlt &&
                !(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
                return;

            if (Input.GetKeyDown(CMMConfig.KeyNextTrack))       SkipTrack(previous: false);
            else if (Input.GetKeyDown(CMMConfig.KeyPrevTrack))  SkipTrack(previous: true);
            else if (Input.GetKeyDown(CMMConfig.KeyVolumeUp))   TrimCurrentTrack(+CMMConfig.UserTrimStep);
            else if (Input.GetKeyDown(CMMConfig.KeyVolumeDown)) TrimCurrentTrack(-CMMConfig.UserTrimStep);
        }

        // The context backing the music actually playing. ActiveChannelContext names
        // whatever is on the active channel (what the listener hears); currentContext
        // is the resolver's logical pick, used as a fallback before audio settles.
        private static string AudibleContextName()
        {
            string c = ActiveChannelContext;
            if (!string.IsNullOrEmpty(c)) return c;
            return currentContext?.Context;
        }

        // ── Next / previous track ──────────────────────────────────────────────
        private void SkipTrack(bool previous)
        {
            if (_skipInProgress) return;

            string ctxName = AudibleContextName();
            if (ctxName == null) return;

            Contexts ctx = GetContext(ctxName);
            if (ctx == null || string.IsNullOrEmpty(ctx.Path)) return; // no music folder
            if (ctx.PlayOnce) return;                                  // stingers aren't skippable
            if (!ContextHasFiles(ctxName)) return;                     // borrowed vanilla / empty
            if (!cacheMap.TryGetValue(ctxName, out ContextCache cache)) return;

            // "Previous" plays the one-deep remembered track — but only if it still
            // exists on disk; otherwise fall through and pick a fresh track like Next.
            string forced = null;
            if (previous)
            {
                string prev = cache.previousPlayedFile;
                if (!string.IsNullOrEmpty(prev) && File.Exists(prev))
                    forced = prev;
                else
                    CMMConfig.D($"[CMM] Prev track: no valid previous for {ctxName}; picking a new one.");
            }

            CMM_CoRoutines.StartCoroutine(ForceSkipCoroutine(ctxName, forced));
        }

        // Tears down in-flight playback coroutines (mirroring TriggerMusic but keeping
        // the current context), loads the target clip, promotes it, and crossfades to
        // it as a same-context track switch.
        private IEnumerator ForceSkipCoroutine(string contextName, string forcedFile)
        {
            _skipInProgress = true;

            if (_nextCacheCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine);   _nextCacheCoroutine   = null; }
            if (_trackEndCoroutine    != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine);    _trackEndCoroutine    = null; }
            if (_crossFadeCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);   _crossFadeCoroutine   = null; }
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }

            // Native path: skip = a same-context next-track on the mixer (forcedFile for
            // prev). Bypasses the AudioClip preload machinery entirely.
            if (CMMConfig.NativeAudioOutput && ContextHasFiles(contextName))
            {
                int natId = ++_transitionId;
                _scheduleHandledTransition = false;
                Log.Out($"[CMM] {(forcedFile != null ? "Prev" : "Next")} track [{contextName}] (native)");
                StartNativeContext(contextName, true, natId, forcedFile);
                _skipInProgress = false;
                yield break;
            }

            ContextCache cache = cacheMap[contextName];

            // LoadNextClip honors the menu native / streaming split and updates
            // previousPlayedFile → lastPlayedFile. A null forcedFile makes it pick
            // (avoiding the current track under RandomNoRepeat).
            yield return StartCoroutine(LoadNextClip(contextName, forcedFile));
            if (cache.Next.Clip == null)
            {
                // Nothing loadable — the folder emptied out (or every remaining pick is
                // unreadable) while the user was skipping. We already stopped the driver
                // coroutines above, but the OLD clip is still playing on ActiveChannel.
                // Leaving it there is the "moved everything out but the previous song kept
                // playing" bug, so fade the stale track out and re-resolve to whatever is
                // now valid (another context / vanilla / silence). FadeToSilence is required
                // even though the resolver may already read Vanilla: SendContextUpdate
                // ("Vanilla") early-returns when vanilla is already active and would never
                // silence our channel.
                CMMConfig.D($"[CMM] Skip: no clip loaded for {contextName} — fading out and re-resolving.");
                if (cache.Current.Clip != null) StagePendingDisposal(cache.Current);
                cache.Current = default;
                FadeToSilence();

                _pendingContext = null;
                if (!inGame)
                    SendContextUpdate(currentContext?.Context ?? "Menu");
                else
                    SendContextUpdate(GetTopContext() ?? "Ambient");

                _skipInProgress = false;
                yield break;
            }

            // Promote Next → Current; stage the outgoing clip for deferred disposal.
            if (cache.Current.Clip != null) StagePendingDisposal(cache.Current);
            cache.Current = cache.Next;
            cache.Next = default;
            cache.resumePosition = 0f;
            cache.lastLeftTime = -1f;

            _scheduleHandledTransition = false;
            int newId = ++_transitionId;
            Log.Out($"[CMM] {(forcedFile != null ? "Prev" : "Next")} track [{contextName}]: " +
                    $"{Path.GetFileName(cache.lastPlayedFile)}");
            _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(PlayFromCache(contextName, true, newId));
            _skipInProgress = false;
        }

        // ── Per-track volume trim ──────────────────────────────────────────────
        private void TrimCurrentTrack(float delta)
        {
            string ctxName = AudibleContextName();
            if (ctxName == null) return;
            // playingFile, not lastPlayedFile: the latter has already advanced to the
            // preloaded next track during a transition, so trimming it would adjust the
            // wrong (not-yet-audible) track.
            if (!cacheMap.TryGetValue(ctxName, out ContextCache cache) ||
                string.IsNullOrEmpty(cache.playingFile))
                return;

            float trim = LoudnessStore.AdjustUserTrim(cache.playingFile, delta);

            // Arm the on-screen popup (held VolumePopupHoldSeconds past the last press).
            _volPopupTrack = Path.GetFileNameWithoutExtension(cache.playingFile);
            _volPopupTrim  = trim;   // <0 carries "not measured yet" through to the draw
            _volPopupUntil = Time.realtimeSinceStartup + VolumePopupHoldSeconds;

            if (trim < 0f)
            {
                // Unmeasured tracks have no Measurement to attach the trim to yet; the
                // background sweep measures every track within seconds of load.
                CMMConfig.D($"[CMM] Volume trim: {Path.GetFileName(cache.playingFile)} " +
                        "not measured yet — try again shortly.");
                return;
            }

            UpdateVolume();

            // UpdateVolume only touches the AudioSource channel — silent on the native path, where the
            // per-track gain was baked into the live voice at Play time. Push the new trim straight to
            // that voice so the change is audible immediately on the track you're listening to.
            if (CMMConfig.NativeAudioOutput
                && string.Equals(_nativeActiveContext, ctxName, System.StringComparison.OrdinalIgnoreCase))
            {
                long vid = System.Threading.Interlocked.Read(ref _nativeVoiceId);
                if (vid != 0)
                    CmmMixer.Instance?.SetVoiceTrackGain(vid, LoudnessStore.GetTrackGain(cache.playingFile));
            }

            string note = CMMConfig.LoudnessNormalizationEnabled
                ? ""
                : " (LoudnessNormalizationEnabled is false — trim is saved but won't affect volume)";
            Log.Out($"[CMM] Track volume {(delta >= 0f ? "up" : "down")} " +
                    $"[{Path.GetFileName(cache.playingFile)}]: trim → {trim * 100f:0}%{note}");
        }

        // ── Volume popup ───────────────────────────────────────────────────────
        // A small top-left overlay showing the current track and its volume as a
        // percentage (the trim: 100% = the track's normalized level and maximum, down
        // to 10%). Armed by TrimCurrentTrack and fading out once VolumePopupHoldSeconds
        // elapse with no further press. Works on the menu and in-game.
        private void DrawVolumePopup()
        {
            if (_volPopupTrack == null) return;
            float remaining = _volPopupUntil - Time.realtimeSinceStartup;
            if (remaining <= 0f) { _volPopupTrack = null; return; }

            EnsureVolumePopupStyles();

            // Fade over the final 0.6s of the hold window.
            float alpha = Mathf.Clamp01(remaining / 0.6f);
            Color prevColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);

            const float bw = 390f, bh = 82f;
            const float bx = 12f, by = 12f;

            GUI.DrawTexture(new Rect(bx, by, bw, bh), _popupBg, ScaleMode.StretchToFill);

            // During the fade-out, a "saved" confirmation fills the box (the trim is
            // persisted to Cache/loudness.xml by the debounced save loop). Only for a
            // real adjustment, not the "measuring" state.
            if (remaining < 2.0f && _volPopupTrim >= 0f)
            {
                EnsureSavedFontSize(bw - 28f);
                GUI.Label(new Rect(bx, by, bw, bh), SavedText, _popupSavedStyle);
            }
            else
            {
                DrawScrollingLabel(new Rect(bx + 14f, by + 8f, bw - 28f, 22f), _volPopupTrack, _popupTitleStyle);

                if (_volPopupTrim < 0f)
                {
                    GUI.Label(new Rect(bx + 14f, by + 40f, bw - 28f, 26f),
                            "measuring loudness…", _popupTitleStyle);
                }
                else
                {
                    // Bar = volume trim (0.1–1.0 → 10–100%); 100% is the track's normalized
                    // level and the maximum — the keys only attenuate from there.
                    float barX = bx + 16f, barY = by + 46f, barW = bw - 96f, barH = 12f;
                    GUI.DrawTexture(new Rect(barX, barY, barW, barH), _popupBarBg, ScaleMode.StretchToFill);
                    GUI.DrawTexture(new Rect(barX, barY, barW * Mathf.Clamp01(_volPopupTrim), barH),
                            _popupBarFill, ScaleMode.StretchToFill);
                    GUI.Label(new Rect(bx + bw - 74f, barY - 8f, 60f, 26f),
                            (_volPopupTrim * 100f).ToString("0") + "%", _popupValueStyle);
                }
            }

            GUI.color = prevColor;
        }

        // Draws text centred when it fits, or — when wider than the rect — clipped to
        // the rect and scrolled horizontally back and forth (ping-pong, with a short
        // dwell at each end) like a music player's now-playing display.
        private void DrawScrollingLabel(Rect rect, string text, GUIStyle style)
        {
            var content = new GUIContent(text);
            float textW = style.CalcSize(content).x;
            if (textW <= rect.width)
            {
                GUI.Label(rect, content, style);
                return;
            }

            float overflow = textW - rect.width;
            const float speed = 25f;   // px/sec
            const float dwell = 0.7f;  // seconds paused at each end
            float travel = overflow / speed;
            float period = (travel + dwell) * 2f;
            float t = Mathf.Repeat(Time.realtimeSinceStartup, period);

            float offset;
            if (t < dwell)                       offset = 0f;                                  // hold at start
            else if (t < dwell + travel)         offset = (t - dwell) / travel * overflow;     // scroll left
            else if (t < dwell + travel + dwell) offset = overflow;                            // hold at end
            else                                 offset = overflow - (t - 2f * dwell - travel) / travel * overflow; // back

            // Clip to the rect; the inner label is exactly text-width so the style's
            // alignment is moot and only the scroll offset positions it.
            GUI.BeginGroup(rect);
            GUI.Label(new Rect(-offset, 0f, textW, rect.height), content, style);
            GUI.EndGroup();
        }

        private void EnsureVolumePopupStyles()
        {
            if (_popupBg == null)
            {
                _popupBg      = MakeTex(new Color(0f, 0f, 0f, 0.80f));
                _popupBarBg   = MakeTex(new Color(1f, 1f, 1f, 0.16f));
                _popupBarFill = MakeTex(new Color(1f, 0.78f, 0.35f, 1f));   // amber
            }
            if (_popupTitleStyle == null)
            {
                _popupTitleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperCenter,
                    wordWrap = false,
                    clipping = TextClipping.Clip,
                };
                _popupTitleStyle.normal.textColor = Color.white;
            }
            if (_popupValueStyle == null)
            {
                _popupValueStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleRight,
                };
                _popupValueStyle.normal.textColor = new Color(1f, 0.85f, 0.4f);
            }
            if (_popupSavedStyle == null)
            {
                _popupSavedStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false,
                };
                _popupSavedStyle.normal.textColor = Color.white;
            }
        }

        // Sizes the "saved" confirmation once so it spans the box without overflowing.
        private void EnsureSavedFontSize(float maxWidth)
        {
            if (_popupSavedFontSize > 0) return;
            _popupSavedStyle.fontSize = 24;
            float w = _popupSavedStyle.CalcSize(new GUIContent(SavedText)).x;
            int size = w > 1f ? Mathf.FloorToInt(24f * maxWidth / w) : 18;
            _popupSavedFontSize = Mathf.Clamp(size, 12, 26);
            _popupSavedStyle.fontSize = _popupSavedFontSize;
        }

        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
