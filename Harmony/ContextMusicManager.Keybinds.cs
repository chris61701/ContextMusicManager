using System.Collections;
using System.Collections.Generic;
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
    //   • ban / favourite          — flag the current track (persisted with the trim
    //                                in Cache/loudness.xml): banned tracks are never
    //                                picked again, favourites play more often
    //   • undo ban                 — unban the most recently banned track (walks
    //                                back through up to 25 bans from this session)
    //   • combat music (toggle)    — mute/unmute ALL combat contexts for this
    //                                session; fights keep the music already playing
    //   • now playing (toggle)     — persistent top-left readout of the audible
    //                                track + context, independent of the debug HUD.
    //                                Line 2 also carries time remaining/total and
    //                                the trim %, and every keybind notice
    //                                (ban/unban, favourite, combat toggle) swaps
    //                                onto it briefly; with the readout off, the
    //                                box appears just for the notice.
    // Keys + modifiers are configurable in Config/settings.xml (Alt+Arrows for
    // skip/volume, Alt+Del/Ins/Home/End for ban/favourite/combat-toggle/undo-ban,
    // Alt+Num7 for now-playing by default — deliberately not letters, which the
    // game binds under free-look).
    // This is the MonoBehaviour's single Update(); the debug HUD's toggle is folded
    // in here because a component may declare Update() only once.

    public partial class ContextMusicManager
    {
        // Debounce: ignore further skip presses while a forced skip is mid-flight,
        // so key-mashing can't stack overlapping clip loads / transitions.
        private static bool _skipInProgress = false;

        // ── Overlay state (Now Playing + keybind notices, one box) ─────────────
        // A single top-left box serves as BOTH the Now Playing readout and the
        // keybind notification surface. Line 1 = the audible track's title (live,
        // scrolling) — or "Vanilla music" / "Nothing playing" when CMM has no voice.
        // Line 2 = "♪ context" with time-remaining/total and the track's volume trim
        // percentage to its right; a keypress notice ("Banned: …", "★ Favourite", …)
        // replaces the whole line for NoticeHoldSeconds. With the overlay toggled ON
        // (KeyNowPlaying) the box is persistent; toggled OFF, it appears just for
        // the notice and fades away with it. Drawn by DrawOverlayBox.
        private const float NoticeHoldSeconds = 4f;
        private static float _noticeUntil = -1f;
        private static GUIContent _noticeText;            // non-null → line-2 message swap
        private static GUIContent _noticeFallbackTitle;   // line 1 when nothing is audible
        private static bool NoticeActive => Time.realtimeSinceStartup < _noticeUntil;

        private Texture2D _popupBg;
        private GUIStyle  _popupTitleStyle, _popupValueStyle, _npSubtitleStyle;

        // Arm a notice. message != null swaps line 2 with it; message null just makes
        // the box visible for the hold window (volume keys — the trim percentage on
        // line 2 updates live, so there's nothing to say, only something to show).
        // fallbackTitle fills line 1 only when no track is audible at draw time (a
        // ban that emptied its folder, a combat toggle during the vanilla handoff).
        private static void ShowNotice(string message = null, string fallbackTitle = null)
        {
            _noticeText = message != null ? new GUIContent(message) : null;
            _noticeFallbackTitle = fallbackTitle != null ? new GUIContent(fallbackTitle) : null;
            _noticeUntil = Time.realtimeSinceStartup + NoticeHoldSeconds;
        }

        private void Update()
        {
            // Per-frame stutter watchdog (no-op unless PerfTiming is on). Runs first so
            // it times the full frame-to-frame gap, including everything below it.
            CMMPerf.FrameTick(LiveMusicCoroutineCount);

            HandleMusicKeybinds();
            // Personal radio (all versions): self-throttled; no-op while dialed Off.
            PersonalRadio.Tick();
#if GV_V3
            // World-emitter audio layer (experimental, 3.0 only): self-throttled; no-op unless
            // enabled + configured. Plays a diegetic station near detected in-world speakers.
            EmitterAudioLayer.Tick();
            // Acoustics sweep exporter (`cmm acsweep`): no-op unless a sweep is running.
            EmitterSweep.Tick();
#endif
#if CMM_DEBUG_HUD
            HandleDebugHudToggle();
#endif
        }

        private void OnGUI()
        {
            // The SP pause menu's options live in the same top-left corner as our overlays —
            // hide BOTH while the game is paused. Draw-time gate ONLY: NowPlaying/notice/HUD
            // state is untouched, so on unpause everything reappears exactly as it was.
            // (IsPaused() is forced false in MP and on the main menu — see Patch_GamePause —
            // so this never hides the overlay outside an SP pause.)
            if (inGame && (GameManager.Instance?.IsPaused() ?? false)) return;
            // The F1 console covers the same corner (and works in MP and on the menu, where
            // the pause gate above never fires) — same draw-time-only hide while it's open.
            if (ConsoleIsOpen()) return;
            DrawOverlayBox();
#if CMM_DEBUG_HUD
            DrawDebugHud();
#endif
        }

        // True while the F1 command console is open. The window is registered under its
        // type name on every supported version (A20/A21 set ID = typeof(...).Name, 3.0
        // passes the literal to its base ctor), so the string form is the one signal that
        // compiles and works everywhere. Dictionary lookup — cheap enough for OnGUI.
        private static bool ConsoleIsOpen()
        {
            var wm = LocalPlayerUI.primaryUI?.windowManager;
            return wm != null && wm.IsWindowOpen("GUIWindowConsole");
        }

        private void HandleMusicKeybinds()
        {
            if (!CMMConfig.KeybindsEnabled) return;

            // Typing/scrolling in the F1 console must not drive music keys: PgUp/PgDn
            // (console scroll) would dial the personal radio, and with the debug config's
            // bare bindings Home/End while editing a command would toggle combat music or
            // undo a ban. The overlays are hidden while it's open anyway (OnGUI gate).
            if (ConsoleIsOpen()) return;

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
            else if (Input.GetKeyDown(CMMConfig.KeyBanTrack))          ToggleBanCurrentTrack();
            else if (Input.GetKeyDown(CMMConfig.KeyUndoBan))           UndoLastBan();
            else if (Input.GetKeyDown(CMMConfig.KeyFavouriteTrack))    ToggleFavouriteCurrentTrack();
            else if (Input.GetKeyDown(CMMConfig.KeyToggleCombatMusic)) ToggleCombatMusic();
            else if (Input.GetKeyDown(CMMConfig.KeyNowPlaying))        ToggleNowPlaying();
            else if (Input.GetKeyDown(CMMConfig.KeyRadioNext))         PersonalRadio.Step(+1);
            else if (Input.GetKeyDown(CMMConfig.KeyRadioPrev))         PersonalRadio.Step(-1);
        }

        // The context backing the music actually playing. _nativeActiveContext names what
        // the mixer is voicing (what the listener hears); currentContext is the resolver's
        // logical pick, used as a fallback before audio settles.
        private static string AudibleContextName()
        {
            string c = _nativeActiveContext;
            if (!string.IsNullOrEmpty(c)) return c;
            return currentContext?.Context;
        }

        // ── Next / previous track ──────────────────────────────────────────────
        private void SkipTrack(bool previous)
        {
            if (_skipInProgress) return;
            // The radio broadcast is deterministic (world clock) — there is no "next track" to
            // force. This also keeps the skip path (which calls StartNativeContext DIRECTLY,
            // bypassing the StartContextCoroutine radio gate) from restarting context music.
            if (PersonalRadio.SuppressesMusic) return;

            string ctxName = AudibleContextName();
            if (ctxName == null) return;

            Contexts ctx = GetContext(ctxName);
            if (ctx == null || string.IsNullOrEmpty(ctx.Source)) return; // no music source
            if (ctx.PlayOnce) return;                                  // stingers aren't skippable
            if (!ContextHasFiles(ctxName)) return;                     // borrowed vanilla / empty
            if (!cacheMap.TryGetValue(ctxName, out ContextCache cache)) return;

            // "Previous" steps back through the context's play history (music-player
            // style; deleted files are pruned in the walk). At the very start of the
            // history there is nothing earlier — the press does nothing rather than
            // surprising with a random pick. "Next" is handled inside the same-context
            // switch: it replays forward through rewound history first, then picks fresh.
            string forced = null;
            if (previous)
            {
                forced = cache.HistoryBack();
                if (forced == null)
                {
                    CMMConfig.D($"[CMM] Prev track: at the start of {ctxName}'s history — nothing earlier.");
                    return;
                }
            }

            CMM_CoRoutines.StartCoroutine(ForceSkipCoroutine(ctxName, forced));
        }

        // Tears down in-flight playback coroutines (mirroring TriggerMusic but keeping
        // the current context), loads the target clip, promotes it, and crossfades to
        // it as a same-context track switch.
        private IEnumerator ForceSkipCoroutine(string contextName, string forcedFile)
        {
            _skipInProgress = true;

            if (_trackEndCoroutine    != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine);    _trackEndCoroutine    = null; }
            if (_crossFadeCoroutine   != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);   _crossFadeCoroutine   = null; }

            // Skip = a same-context next-track on the native mixer (forcedFile for prev).
            // A manual skip crossfades at ManualSkipFadeDuration — keypress-fast —
            // instead of the context/transition fade an automatic advance uses.
            if (ContextHasFiles(contextName))
            {
                int natId = ++_transitionId;
                Log.Out($"[CMM] {(forcedFile != null ? "Prev" : "Next")} track [{contextName}] (native)");
                StartNativeContext(contextName, true, natId, forcedFile, CMMConfig.ManualSkipFadeDuration);
            }
            else
            {
                // Files vanished while skipping — fade out and re-resolve to whatever's valid.
                CMMConfig.D($"[CMM] Skip: no files for {contextName} — fading out and re-resolving.");
                FadeToSilence();
                _pendingContext = null;
                if (!inGame) SendContextUpdate(currentContext?.Context ?? "Menu");
                else         SendContextUpdate(GetTopContext() ?? "Ambient");
            }
            _skipInProgress = false;
            yield break;
        }

        // ── Per-track volume trim ──────────────────────────────────────────────
        private void TrimCurrentTrack(float delta)
        {
            // Radio tracks follow the station's broadcast, not the context caches this reads;
            // per-track trim doesn't apply to them (bans/favourites likewise, below).
            if (PersonalRadio.SuppressesMusic) return;
            string ctxName = AudibleContextName();
            if (ctxName == null) return;
            // playingFile, not lastPlayedFile: the latter has already advanced to the
            // preloaded next track during a transition, so trimming it would adjust the
            // wrong (not-yet-audible) track.
            if (!cacheMap.TryGetValue(ctxName, out ContextCache cache) ||
                string.IsNullOrEmpty(cache.playingFile))
                return;

            float trim = LoudnessStore.AdjustUserTrim(cache.playingFile, delta);

            if (trim < 0f)
            {
                // Unmeasured tracks have no Measurement to attach the trim to yet; the
                // background sweep measures every track within seconds of load.
                ShowNotice("measuring loudness — try again shortly");
                CMMConfig.D($"[CMM] Volume trim: {Path.GetFileName(cache.playingFile)} " +
                        "not measured yet — try again shortly.");
                return;
            }
            // The trim percentage lives permanently on the overlay's line 2 — flag its
            // cache stale so the new value shows this frame, and flash the box up in
            // case the persistent readout is toggled off.
            _npRightDirty = true;
            ShowNotice();

            // The per-track gain was baked into the live voice at Play time. Push the new trim
            // straight to that voice so the change is audible immediately on the track you're
            // listening to. (VolumeSyncLoop also re-reads it on its next tick for master volume.)
            if (string.Equals(_nativeActiveContext, ctxName, System.StringComparison.OrdinalIgnoreCase))
            {
                long vid = System.Threading.Interlocked.Read(ref _nativeVoiceId);
                if (vid != 0)
                    CmmMixer.Instance?.SetVoiceTrackGain(
                            vid, LoudnessStore.GetTrackGain(cache.playingFile), TrackGainKeyRampSeconds);
            }

            string note = CMMConfig.LoudnessNormalizationEnabled
                ? ""
                : " (LoudnessNormalizationEnabled is false — trim is saved but won't affect volume)";
            Log.Out($"[CMM] Track volume {(delta >= 0f ? "up" : "down")} " +
                    $"[{Path.GetFileName(cache.playingFile)}]: trim → {trim * 100f:0}%{note}");
        }

        // ── Ban / favourite the current track ──────────────────────────────────
        // Both act on the track that's audibly playing (same target rule as the
        // volume trim) and persist via the loudness store, so they survive restarts
        // and follow a file that's later moved or renamed. Banning also skips ahead.
        // Like the trim, the flag needs the track's content hash, which exists once
        // it's measured — the sweep measures the playing track within seconds.

        // The last tracks banned by the key this session, most recent last — the
        // undo key (Alt+End by default) unbans them in reverse order. Session-only
        // (the bans themselves persist; this is just the undo order), capped like
        // the play history. A manual unban (Alt+Delete on the playing track) drops
        // its entry so undo never re-visits it.
        private const int BanUndoDepth = 25;
        private static readonly List<string> _banUndoStack = new List<string>();

        private void ToggleBanCurrentTrack()
        {
            if (_skipInProgress) return;   // mid-skip: playingFile is in flux
            if (PersonalRadio.SuppressesMusic) return;   // bans don't apply to radio stations
            string file = AudiblePlayingFile();
            if (file == null) return;

            string title = Path.GetFileNameWithoutExtension(file);
            bool? banned = LoudnessStore.ToggleBanned(file);
            if (banned == null)
            {
                ShowNotice("measuring loudness — try again shortly");
                return;
            }
            Log.Out($"[CMM] Track {(banned.Value ? "banned" : "unbanned")} [{Path.GetFileName(file)}] " +
                    $"({LoudnessStore.BannedCount} banned total)");
            // Keep the undo stack in step: a ban goes on top (deduped), a manual
            // unban leaves nothing behind for the undo key to re-visit.
            _banUndoStack.Remove(file);
            if (banned.Value)
            {
                _banUndoStack.Add(file);
                if (_banUndoStack.Count > BanUndoDepth) _banUndoStack.RemoveAt(0);

                // The skip below lands on the NEXT track, so the overlay's title line
                // will already show it — the notice names the track that was banned.
                ShowNotice("Banned: " + title, fallbackTitle: "Track banned");
                // Normally skip to the next track; but if that ban was the folder's
                // LAST unbanned track the context is now unplayable (SkipTrack would
                // bail on ContextHasFiles) — resolve to the fallback chain instead.
                string ctxName = AudibleContextName();
                if (ctxName != null && !ContextHasFiles(ctxName))
                    ReResolveNow();
                else
                    SkipTrack(previous: false);
            }
            else
            {
                ShowNotice("Ban removed");
            }
        }

        // Unban the most recently banned track (Alt+End by default), walking down
        // the undo stack. Entries whose ban was already lifted some other way are
        // skipped silently. Re-resolves afterwards in case the unban made an
        // all-banned context playable again (ReResolveNow only re-triggers when
        // resolution actually changed).
        private void UndoLastBan()
        {
            while (_banUndoStack.Count > 0)
            {
                string file = _banUndoStack[_banUndoStack.Count - 1];
                _banUndoStack.RemoveAt(_banUndoStack.Count - 1);
                if (!LoudnessStore.IsBannedPath(file)) continue;   // unbanned elsewhere
                if (LoudnessStore.ToggleBanned(file) != false) continue;   // couldn't unban

                Log.Out($"[CMM] Ban undone [{Path.GetFileName(file)}] " +
                        $"({LoudnessStore.BannedCount} banned total, {_banUndoStack.Count} more undoable)");
                ShowNotice("Ban removed: " + Path.GetFileNameWithoutExtension(file),
                           fallbackTitle: "Undo ban");
                ReResolveNow();
                return;
            }
            CMMConfig.D("[CMM] Undo ban: nothing to undo this session.");
        }

        private void ToggleFavouriteCurrentTrack()
        {
            if (PersonalRadio.SuppressesMusic) return;   // favourites don't apply to radio stations
            string file = AudiblePlayingFile();
            if (file == null) return;

            bool? fav = LoudnessStore.ToggleFavourite(file);
            if (fav == null)
            {
                ShowNotice("measuring loudness — try again shortly");
                return;
            }
            Log.Out($"[CMM] Track {(fav.Value ? "favourited" : "unfavourited")} [{Path.GetFileName(file)}] " +
                    $"({LoudnessStore.FavouriteCount} favourite(s) total)");
            ShowNotice(fav.Value ? "★ Favourite — plays more often" : "Favourite removed");
        }

        // The file audibly playing right now, or null when there's nothing to act on.
        // playingFile (not lastPlayedFile) for the same reason as the volume trim: the
        // latter has already advanced to the preloaded next track during a transition.
        private static string AudiblePlayingFile()
        {
            string ctxName = AudibleContextName();
            if (ctxName == null) return null;
            if (!cacheMap.TryGetValue(ctxName, out ContextCache cache) ||
                string.IsNullOrEmpty(cache.playingFile))
                return null;
            return cache.playingFile;
        }

        // ── Combat-music toggle ────────────────────────────────────────────────
        // One switch for ALL combat contexts (the tiers, the biome variants,
        // Blood_Moon_Combat — see RebuildCombatContextSet). While muted they resolve
        // through their fallback chains as if their folders were empty, and the
        // linked vanilla Combat/Bloodmoon sections are held back too — so a fight
        // keeps whatever ambient/POI music was already playing.
        //
        // THREE STOPS, not two: both off → combat off → both on. Tension is the pre-fight
        // stalking cue — combat music that has not started yet — so muting fights while it
        // still plays only moves the noise. The middle stop keeps tension for those who want
        // the mood without the drums.
        //
        // PERSISTED: the new state is written straight back to Config/settings.xml as
        // CombatMusicMuted and TensionMusicMuted, so it survives a restart. Someone who does not want combat music
        // does not want it TODAY — being made to press this again every launch was the whole
        // complaint. If the file cannot be written the toggle still applies, and the notice
        // says "this session only" rather than quietly lying about having saved it.
        private void ToggleCombatMusic()
        {
            CycleMusicMute();
            bool combat  = CombatMusicMuted;
            bool tension = TensionMusicMuted;
            CMMConfig.CombatMusicMuted  = combat;
            CMMConfig.TensionMusicMuted = tension;

            // `&` and NOT `&&`: both settings must be written whatever the first one returns.
            // Short-circuiting here would leave the file holding half a state — the combat line
            // saved and the tension line stale — which is worse than not saving at all, because
            // the next launch would load a combination the player never chose.
            bool saved = CMMConfigLoader.PersistSetting(ModPath, "CombatMusicMuted",
                                                        combat ? "true" : "false")
                       & CMMConfigLoader.PersistSetting(ModPath, "TensionMusicMuted",
                                                        tension ? "true" : "false");

            string notice = combat && tension
                ? "Combat + tension music off — fights keep the current music"
                : combat ? "Combat music off — tension music back on"
                         : "Combat and tension music back on";
            Log.Out($"[CMM] Combat music {(combat ? "OFF" : "ON")}, tension {(tension ? "OFF" : "ON")} " +
                    $"({CombatContextCount} combat, {TensionContextCount} tension context(s) covered)"
                    + (saved ? " — saved to settings.xml" : " — NOT saved (this session only)"));
            ShowNotice(notice + (saved ? "" : " (this session only)"),
                       fallbackTitle: "Combat music");
            ReResolveNow();
        }

        // Re-resolve immediately after a keybind changed what resolution would pick
        // (combat music toggled, last track of a folder banned), so the key
        // feels instant — in-game the scan loop would also converge within a tick via
        // its playability change detection, but the menu has no scan loop at all.
        // Priority-bypassed so a LOWER-priority context can take over from the one
        // that was just switched off. Every branch re-triggers ONLY when the change
        // altered what resolution picks — a re-send that lands back on the playing
        // context would needlessly restart its track.
        internal static void ReResolveNow()   // internal: PersonalRadio hands music back through this
        {
            if (!inGame)
            {
                // Always evaluate from "Menu" — the menu's entry context — NOT from
                // currentContext: after the menu fell back (e.g. every menu track
                // banned → Vanilla took over), currentContext IS the fallback, which
                // trivially resolves to itself and would mask a Menu that just became
                // playable again (an unban). Nothing on the menu resolves from any
                // other entry point, so "Menu" is the one question worth asking.
                if (ResolvePlayableContext("Menu") != currentContext?.Context)
                {
                    _pendingContext = null;
                    SendContextUpdate("Menu");
                }
                return;
            }
            string top = GetTopContext();
            if (top == null)
            {
                // Nothing playable and DMS is off — fade to silence (mirrors the scan loop).
                if (currentContext?.Context != "Silence")
                {
                    _pendingContext = null;
                    currentContext = GetContext("Silence");
                    ++_transitionId;
                    FadeToSilence();
                }
                return;
            }
            if (top != currentContext?.Context)
            {
                _pendingContext = null;
                SendContextUpdate(top, 0);
            }
        }

        // ── The overlay box (Now Playing + notices) ────────────────────────────
        // Toggled persistent by KeyNowPlaying (Alt+Numpad7 by default — the key that
        // used to flip the debug A/B dormant arm). Independent of the debug HUD,
        // which steps below the box whenever it's visible. The persistent readout
        // hides while CMM has nothing voiced (vanilla handoff / silence); notices
        // always show, transiently. NowPlayingEnabled in settings.xml picks the
        // state at launch; the key owns it from then on.
        private static bool? _nowPlayingOn;   // null until first toggle → config default
        private static bool NowPlayingOn => _nowPlayingOn ?? CMMConfig.NowPlayingEnabled;

        // Box metrics + the shift it imposes on elements stacked beneath it. With
        // the readout ON the box is always up (it shows the vanilla/silence state
        // rather than disappearing), so the HUD clearance keys off the same rule.
        private const float OverlayBoxH = 58f;
        internal const float OverlayClearanceHeight = 12f + OverlayBoxH + 8f;
        internal static bool OverlayOnScreen => NowPlayingOn || NoticeActive;

        // Draw caches, rebuilt only when the audible track/context changes so the
        // persistent overlay doesn't allocate strings every OnGUI pass.
        private static string _npFile, _npContext;
        private static GUIContent _npTitle, _npSubtitle;
        // Right-hand segment of line 2: "-remaining / total   trim%". Rebuilt when
        // the displayed second ticks over, the track changes, or a volume key fires
        // (_npRightDirty) — never per frame. Null when the voice has no known length.
        private static GUIContent _npRight;
        private static int  _npRightSecs = int.MinValue;
        private static bool _npRightDirty;
        // Fixed line-1/line-2 content for the no-voice states.
        private static readonly GUIContent VanillaTitle = new GUIContent("Vanilla music");
        private static readonly GUIContent NothingTitle = new GUIContent("Nothing playing");
        private static readonly GUIContent VanillaSub   = new GUIContent("♪  game soundtrack");
        private static readonly GUIContent SilenceSub   = new GUIContent("♪  silence");

        // The file audibly playing right now (same target rule as the keybinds), or
        // null when CMM has nothing voiced.
        private static string NowPlayingFile()
        {
            string ctxName = AudibleContextName();
            if (ctxName == null) return null;
            if (!cacheMap.TryGetValue(ctxName, out ContextCache cache) ||
                string.IsNullOrEmpty(cache.playingFile))
                return null;
            return cache.playingFile;
        }

        private void ToggleNowPlaying()
        {
            _nowPlayingOn = !NowPlayingOn;
            Log.Out($"[CMM] Now Playing overlay {(_nowPlayingOn.Value ? "shown" : "hidden")}.");
        }

        private void DrawOverlayBox()
        {
            bool notice = NoticeActive;
            bool persistent = NowPlayingOn;
            if (!persistent && !notice) return;

            EnsureOverlayStyles();

            // Line-1/line-2 caches follow the LIVE track; with no voice the box
            // stays up and states what's happening instead (vanilla / nothing).
            // While the personal radio owns the music layer, IT is the audible
            // source: its track titles the box and line 2 reads "Radio: <station>"
            // (all of it cached on state edges — no per-frame allocation).
            bool radio = PersonalRadio.SuppressesMusic;
            string file = radio ? PersonalRadio.PlayingTrack : NowPlayingFile();
            string ctxName = radio ? PersonalRadio.OverlayContextName : AudibleContextName();
            if (file != null && (!ReferenceEquals(file, _npFile) || ctxName != _npContext))
            {
                _npFile = file;
                _npContext = ctxName;
                _npTitle = new GUIContent(Path.GetFileNameWithoutExtension(file));
                _npSubtitle = new GUIContent("♪  " + ctxName);
                _npRightDirty = true;
            }
            GUIContent title, subtitle;
            if (file != null) { title = _npTitle; subtitle = _npSubtitle; }
            else if (radio) { title = PersonalRadio.OverlayIdleTitle; subtitle = PersonalRadio.OverlayIdleSub ?? SilenceSub; }
            else if (vanillaIsActive) { title = VanillaTitle; subtitle = VanillaSub; }
            else { title = NothingTitle; subtitle = SilenceSub; }
            // A notice raised when nothing was audible carries its own subject line.
            if (file == null && notice && _noticeFallbackTitle != null)
                title = _noticeFallbackTitle;

            // Without the persistent readout the box lives only as long as the
            // notice — fade it out over the notice's final 0.6 s.
            Color prevColor = GUI.color;
            if (!persistent)
                GUI.color = new Color(1f, 1f, 1f,
                        Mathf.Clamp01((_noticeUntil - Time.realtimeSinceStartup) / 0.6f));

            const float bw = 390f, bx = 12f, by = 12f;
            GUI.DrawTexture(new Rect(bx, by, bw, OverlayBoxH), _popupBg, ScaleMode.StretchToFill);
            DrawScrollingLabel(new Rect(bx + 14f, by + 7f, bw - 28f, 22f), title, _popupTitleStyle);

            if (notice && _noticeText != null)
            {
                // Keypress notice replaces the whole second line for the hold window.
                DrawScrollingLabel(new Rect(bx + 14f, by + 31f, bw - 28f, 20f),
                        _noticeText, _npSubtitleStyle);
            }
            else
            {
                if (file != null) RefreshOverlayRight(file);
                // Context on the left; "-remaining / total   trim%" on the right.
                float rightW = file != null && _npRight != null ? 170f : 0f;
                DrawScrollingLabel(new Rect(bx + 14f, by + 31f, bw - 28f - rightW, 20f),
                        subtitle, _npSubtitleStyle);
                if (rightW > 0f)
                    GUI.Label(new Rect(bx + bw - 14f - rightW, by + 31f, rightW, 20f),
                            _npRight, _popupValueStyle);
            }

            GUI.color = prevColor;
        }

        // Rebuild the right-hand segment of line 2 when its displayed second ticks
        // over (or a track change / volume key flagged it stale). One native state
        // query + one small string per second while the overlay is up — not per frame.
        private static void RefreshOverlayRight(string file)
        {
            // The radio's voice lives outside the context system's _nativeVoiceId.
            long vid = PersonalRadio.SuppressesMusic
                ? PersonalRadio.VoiceId
                : System.Threading.Interlocked.Read(ref _nativeVoiceId);
            if (vid == 0) { _npRight = null; _npRightSecs = int.MinValue; return; }
            // Declared and initialised up front rather than inline in the null-conditional call.
            // `Instance?.TryGetState(... out ...)` does not run when Instance is null, so the outs
            // stay unassigned while the following line reads them -- CS0165, a hard compile error
            // that has been sitting in this file since it was written.
            double pos = 0.0, len = 0.0; bool active = false;
            var mixer = CmmMixer.Instance;
            if (mixer == null || !mixer.TryGetState(vid, out pos, out len, out active)
                || !active || len <= 0.0 || double.IsInfinity(len))
            {
                _npRight = null;
                _npRightSecs = int.MinValue;
                return;
            }

            int remaining = (int)(len - pos + 0.5);
            if (remaining < 0) remaining = 0;
            if (!_npRightDirty && remaining == _npRightSecs) return;

            int total = (int)(len + 0.5);
            float trimPct = LoudnessStore.GetUserTrim(file) * 100f;
            _npRight = new GUIContent(
                    $"-{remaining / 60}:{remaining % 60:00} / {total / 60}:{total % 60:00}   {trimPct:0}%");
            _npRightSecs = remaining;
            _npRightDirty = false;
        }

        // Draws text centred when it fits, or — when wider than the rect — clipped to
        // the rect and scrolled horizontally back and forth (ping-pong, with a short
        // dwell at each end) like a music player's now-playing display.
        private void DrawScrollingLabel(Rect rect, string text, GUIStyle style)
            => DrawScrollingLabel(rect, new GUIContent(text), style);

        // GUIContent overload for persistent callers (the Now Playing overlay) that
        // cache their content and must not allocate per OnGUI pass.
        private void DrawScrollingLabel(Rect rect, GUIContent content, GUIStyle style)
        {
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

        private void EnsureOverlayStyles()
        {
            if (_popupBg == null)
                _popupBg = MakeTex(new Color(0f, 0f, 0f, 0.80f));
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
                // Right side of line 2: "-2:12 / 3:45   70%".
                _popupValueStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleRight,
                    wordWrap = false,
                    clipping = TextClipping.Clip,
                };
                _popupValueStyle.normal.textColor = new Color(1f, 0.85f, 0.4f);
            }
            if (_npSubtitleStyle == null)
            {
                // Left side of line 2: "♪ context" (and full-width notice messages).
                _npSubtitleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = false,
                    clipping = TextClipping.Clip,
                };
                _npSubtitleStyle.normal.textColor = new Color(1f, 0.85f, 0.4f);   // amber
            }
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
