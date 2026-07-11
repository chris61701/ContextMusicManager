
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
    // AUDIO PLAYER
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager
    {
        // ── PickNextFile ──────────────────────────────────────────────────────
        // UNIFORM random selection over the whole eligible folder (RandomNoRepeat also
        // excludes avoidFile), except for the user's keybind flags: banned tracks are
        // excluded and favourites are weighted up (see PickRandomHonouringFlags).
        // There is deliberately NO bias toward already-measured tracks: under on-demand scanning only a tiny handful of a context's files are
        // ever measured (the menu prewarm measures one per context), so biasing toward
        // them collapsed every pick onto that one track — the "same track out of 3000"
        // bug. Normalization is handled independently of selection: StartNativeContext reads
        // the stored gain (non-blocking) and the look-ahead pre-measures the staged next track.
        // ── Native-output debug driver ────────────────────────────────────────
        // Drives CmmMixer from the `cmm native*` console commands for manual testing. Plays
        // one looped track per context; `next` crossfades to another pick; `stop` fades out.
        private static string _nativeDebugContext;
        private static string _nativeDebugLastFile;

        public static string NativeDebugPlay(string contextName)
        {
            if (!contexts.ContainsKey(contextName))
                return $"unknown context '{contextName}'.";
            if (!cacheMap.ContainsKey(contextName))
                return $"context '{contextName}' not ready yet (enter a game first).";
            _nativeDebugContext = contextName;
            return LoadAndPlayNative(contextName, avoid: null, fadeSeconds: 1.0f, loop: true)
                ? $"native: loading a track for '{contextName}'..."
                : $"native: no playable file for '{contextName}'.";
        }

        public static string NativeDebugNext()
        {
            if (_nativeDebugContext == null)
                return "nothing playing — use 'cmm nativeplay <context>' first.";
            return LoadAndPlayNative(_nativeDebugContext, avoid: _nativeDebugLastFile, fadeSeconds: 2.0f, loop: true)
                ? $"native: crossfading to another track in '{_nativeDebugContext}'..."
                : $"native: no other file for '{_nativeDebugContext}'.";
        }

        public static string NativeDebugStop()
        {
            CmmMixer.Instance?.Stop(1.0f);
            _nativeDebugContext = null;
            return "native: stopping.";
        }

        // Picks a file on the calling (main) thread — PickNextFile touches shared
        // per-context state — then opens the reader + installs the voice on a
        // background thread, since the open does disk I/O + decode init.
        private static bool LoadAndPlayNative(string contextName, string avoid, float fadeSeconds, bool loop)
        {
            string file = PickNextFile(contextName, avoid);
            if (file == null) return false;
            _nativeDebugLastFile = file;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    IntPtr dec = AudioDecoder.OpenNativeHandle(file, out int kind, out int srcCh, out int srcRate, out long total);
                    if (dec == IntPtr.Zero) { Log.Warning($"[CMM] native: failed to open {file}"); return; }
                    long id = CmmMixer.GetOrCreate().Play(dec, kind, srcRate, srcCh, total, fadeSeconds, loop);
                    Log.Out($"[CMM] native: {(id != 0 ? "playing" : "rejected")} {System.IO.Path.GetFileName(file)}");
                }
                catch (Exception ex) { Log.Error($"[CMM] native play error: {ex.Message}"); }
            });
            return true;
        }

        // ── Native playback controller (Phase 3c) ─────────────────────────────
        // Custom-file contexts play through CmmMixer. Context RESOLUTION (what should
        // play) is unchanged — only the output path differs. Vanilla/DMS-borrow +
        // Silence stay on the AudioSource path (decision A: hybrid), so the two outputs
        // never both produce the current context: StartContextCoroutine / FadeToSilence
        // stop the mixer whenever an AudioSource (or silent) context takes over, and the
        // mixer crossfades itself for native→native switches.
        //
        // _nativeActiveContext / _nativeVoiceId name the track currently on the mixer
        // (null/0 when the mixer isn't the active output).
        // _nativeActiveContext: volatile — set on the main thread, read by both the main
        // thread and the start worker (commit guard). _nativeVoiceId: written by the worker
        // once Play() succeeds, read by main-thread pollers; accessed via Interlocked since
        // long isn't volatile-able.
        private static volatile string _nativeActiveContext;
        private static long _nativeVoiceId;

        // ── Off-main-thread native start dispatcher ───────────────────────────────
        // The native START path (open reader → CmmMixer.Play) is dispatched to this
        // dedicated worker so a multi-second Unity MAIN-THREAD freeze (world load/unload,
        // quit-to-menu) can't stall the music start. The main thread captures ALL Unity
        // state synchronously at trigger time (file pick, resume, volume, loudness gain,
        // cache bookkeeping — see StartNativeContext) and hands the worker a self-contained
        // request; the worker then opens the reader and plays even while the main thread is
        // frozen. Everything the worker touches is thread-safe (AudioDecoder.OpenStreamingReader,
        // the locked CmmMixer.Play, volatile/Interlocked fields) — it never touches cache
        // fields or Unity APIs, so the main-thread coroutines (scan/watch/volume loops) stay
        // race-free.
        private struct NativeStartReq
        {
            public string Context;
            public string File;
            public int    TransitionId;
            public float  StartSeconds;
            public float  Fade;
            public bool   Loop;
            public float  TrackGain;
            public float  DuckOthersTo;   // >=0 → PlayOnce stinger ducks the bed; <0 → normal crossfade
            public bool   SnapTight;      // context change → flush a deep cushion; track switch → don't (avoid teleport)
        }
        // Voice the named context most recently played, kept so a quick flip back to it can
        // reverse-fade reclaim the still-fading voice instead of restarting its track. Touched
        // only on the main thread (StartNativeContext).
        private static readonly Dictionary<string, long> _recentNativeVoice =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<NativeStartReq> _natQueue = new Queue<NativeStartReq>();
        private static readonly object _natQueueLock = new object();
        private static readonly AutoResetEvent _natSignal = new AutoResetEvent(false);
        private static readonly object _natCommitLock = new object();
        private static Thread _natWorker;

        private static void EnsureNativeWorker()
        {
            if (_natWorker != null) return;
            _natWorker = new Thread(NativeStartWorker)
            {
                IsBackground = true,
                Name = "CMM-NativeStart",
                // BELOW-NORMAL, not AboveNormal: this worker's one job at a context switch is
                // to OPEN the decoder (AudioDecoder.OpenNativeHandle) before handing it to the
                // mixer, and a cold codec open is a CPU burst — worst for Opus, whose seekable
                // open decodes initial packets + probes the stream. At AboveNormal that burst
                // PREEMPTED Unity's (Normal-priority) render thread, so the first play of a
                // slow-opening track hitched the FRAME (the audio never underran — the native
                // producer thread stays AboveNormal and keeps the output ring full). Dropping
                // to BelowNormal lets the OS keep the game smooth and merely stretches a cold
                // open a little under load; the crossfade window hides the slightly later start.
                // Same rationale as the loudness worker (see StartMeasure). Do NOT raise this to
                // shave start latency — that reintroduces the frame stutter it exists to avoid.
                Priority = System.Threading.ThreadPriority.BelowNormal,
            };
            _natWorker.Start();
        }

        private static void NativeStartWorker()
        {
            while (true)
            {
                _natSignal.WaitOne();
                while (true)
                {
                    NativeStartReq req;
                    lock (_natQueueLock)
                    {
                        if (_natQueue.Count == 0) break;
                        req = _natQueue.Dequeue();
                    }

                    // Superseded before we paid the (buffering) open? Drop it. This is what
                    // dedups a skip storm: each skip bumps _transitionId, so all but the last
                    // queued request is discarded without re-buffering the file.
                    if (req.TransitionId != _transitionId) continue;

                    // Open a BARE native decoder handle (off the audio thread) to hand to the
                    // mixer — no managed WaveStream wrapper. The mixer takes ownership of the
                    // handle (closes it on retire, or on a Play/cmm_mix_add failure).
                    IntPtr dec = IntPtr.Zero;
                    int kind = -1, srcRate = 0, srcCh = 0;
                    long total = 0;
                    try { dec = AudioDecoder.OpenNativeHandle(req.File, out kind, out srcCh, out srcRate, out total); }
                    catch { }
                    if (dec == IntPtr.Zero)
                    {
                        // _invalidFiles is main-thread-only, so the worker only logs; genuinely
                        // corrupt files get blacklisted by the preload / file-watch paths.
                        Log.Warning($"[CMM] native: failed to open {Path.GetFileName(req.File)} (worker)");
                        continue;
                    }

                    lock (_natCommitLock)
                    {
                        // Re-check under the commit lock: a teardown during the open (a newer
                        // transition, or StopNativeIfActive nulling the context) means this start
                        // is stale — drop it instead of resurrecting dead audio. We opened the
                        // handle but won't hand it to the mixer, so close it ourselves.
                        if (req.TransitionId != _transitionId
                            || !string.Equals(_nativeActiveContext, req.Context, StringComparison.OrdinalIgnoreCase))
                        {
                            AudioDecoder.CloseNativeHandle(dec, kind);
                            continue;
                        }

                        long startFrame = 0;
                        if (req.StartSeconds > 0f && srcRate > 0)
                            startFrame = (long)(req.StartSeconds * srcRate);

                        long voiceId = CmmMixer.GetOrCreate().Play(dec, kind, srcRate, srcCh, total, req.Fade, req.Loop, startFrame, req.TrackGain, req.DuckOthersTo, req.SnapTight);
                        if (voiceId == 0)
                        {
                            // Play (cmm_mix_add) already closed the handle on rejection.
                            Log.Warning($"[CMM] native: Play rejected {Path.GetFileName(req.File)}");
                            continue;
                        }
                        Interlocked.Exchange(ref _nativeVoiceId, voiceId);
                        Log.Out($"[CMM] native: playing {Path.GetFileName(req.File)} for {req.Context} " +
                                $"(fade {req.Fade:F1}s, loop {req.Loop}, gain {req.TrackGain:F2}, start {req.StartSeconds:F1}s)");
                    }
                }
            }
        }

        // The single branch point for starting a context's playback. A custom-file context
        // plays through the native mixer; a context with no files (Vanilla / Silence / an
        // emptied custom folder) has nothing to play, so we just stop the mixer and let the
        // resolver / vanilla DMS handoff take over. Used by TriggerMusic, the skip path, and
        // the native auto-advance. The native path owns no coroutine — it's torn down by
        // _transitionId bumps + StopNativeIfActive.
        internal static void StartContextCoroutine(string contextName, bool isSameContextSwitchingToNextTrack, int newId)
        {
            // The personal radio owns the music layer while on (in-game): resolution keeps
            // tracking contexts, but none of them may START audio — the radio's voice is the
            // music. EXCEPTION: PlayOnce stingers (Death / Spawn / POI_Cleared) interrupt the
            // radio — their add crossfades (Death/Spawn) or ducks (POI_Cleared) the radio's
            // voice, and PersonalRadio holds its broadcast rejoin until the stinger finishes.
            // Dialing the radio off calls ReResolveNow, which lands back here ungated.
            if (PersonalRadio.SuppressesMusic)
            {
                Contexts gateCtx = GetContext(contextName);
                if (gateCtx == null || !gateCtx.PlayOnce)
                {
                    // Tear the context voice's bookkeeping down WITHOUT the bus-wide stop —
                    // the radio's own voice shares the bus and must survive. (This releases a
                    // just-finished stinger; it's a no-op when no context voice exists.)
                    ReleaseNativeVoiceForRadio(CrossFadeDuration);
                    return;
                }
            }
            if (ContextHasFiles(contextName))
                Instance.StartNativeContext(contextName, isSameContextSwitchingToNextTrack, newId, null);
            else
                StopNativeIfActive(CrossFadeDuration);          // a silent / vanilla context is taking over
        }

        // Clear the native context-voice bookkeeping and fade THAT VOICE only, per-voice. Used
        // by the radio-suppression gate: the bus-wide StopNativeIfActive would take the personal
        // radio's voice (same bus) down with it.
        private static void ReleaseNativeVoiceForRadio(float fade)
        {
            lock (_natCommitLock)
            {
                if (_nativeActiveContext != null)
                    CMMConfig.D($"[CMM] native: releasing context voice to the radio (was {_nativeActiveContext}).");
                _nativeActiveContext = null;
                long vid = Interlocked.Exchange(ref _nativeVoiceId, 0);
                if (vid != 0) CmmMixer.Instance?.SetVoiceTrackGain(vid, 0f, fade);
            }
        }

        // True while a PlayOnce stinger (Death / Spawn / POI_Cleared) is AUDIBLY playing on the
        // context voice. The personal radio holds its broadcast rejoin under it; VolumeSyncLoop
        // lets the stinger's own context volume govern the bus master for its duration.
        internal static bool PlayOnceStingerActive
        {
            get
            {
                Contexts c = currentContext;
                if (c == null || !c.PlayOnce) return false;
                long vid = Interlocked.Read(ref _nativeVoiceId);
                return vid != 0 && CmmMixer.Instance != null
                    && CmmMixer.Instance.TryGetState(vid, out _, out _, out _);
            }
        }

        // Fade the mixer out and forget it's active. No-op (so safe to call anywhere)
        // when the mixer isn't the current output. Callers stop _trackEndCoroutine
        // (the native auto-advance) themselves before this, mirroring the AudioSource teardown.
        internal static void StopNativeIfActive(float fade)
        {
            if (_nativeActiveContext == null) return;
            // Null the context under the commit lock so an in-flight start worker that has
            // already opened its reader sees the teardown and drops the request instead of
            // committing a voice after we've stopped.
            lock (_natCommitLock)
            {
                CMMConfig.D($"[CMM] native: stopping mixer (was {_nativeActiveContext}).");
                _nativeActiveContext = null;
                Interlocked.Exchange(ref _nativeVoiceId, 0);
            }
            CmmMixer.Instance?.Stop(fade);
        }

        // True when the native mixer is the current output and still producing sound (or a
        // start was just dispatched and the worker hasn't committed the voice yet). Lets
        // ContextScanLoop's currentAudioDead see native playback — without it, native output
        // (which owns no Unity coroutine) always looked "dead", so a PlayOnce stinger was
        // treated as strandable and got interrupted ~0.3s in.
        internal static bool NativeOutputBusy()
        {
            if (_nativeActiveContext == null) return false;
            long vid = Interlocked.Read(ref _nativeVoiceId);
            if (vid == 0) return true;   // dispatched; worker hasn't committed the voice yet
            return CmmMixer.Instance != null && CmmMixer.Instance.RemainingSeconds(vid) != -1;
        }


        // Resume capture for a native-played context being left. Reads the live playhead
        // from the mixer (the AudioSource sites read AudioSource.time, which is silent on
        // this path). Returns true when it handled the capture so the caller skips its
        // AudioSource branch.
        internal static bool CaptureNativeResume(Contexts leaving)
        {
            if (leaving == null || !leaving.CanBeResumed) return false;
            if (_nativeActiveContext == null
                || !string.Equals(_nativeActiveContext, leaving.Context, StringComparison.OrdinalIgnoreCase))
                return false;
            if (CmmMixer.Instance != null
                && CmmMixer.Instance.TryGetState(Interlocked.Read(ref _nativeVoiceId), out double pos, out _, out bool active) && active
                && cacheMap.TryGetValue(leaving.Context, out ContextCache lc))
            {
                lc.resumePosition = (float)pos;
                lc.lastLeftTime = Time.realtimeSinceStartup;
                CMMConfig.D($"[CMM] Saved native resume for {leaving.Context}: {pos:F2}s");
                return true;
            }
            return false;
        }

        // The context-start entry point. SYNCHRONOUS and MAIN-THREAD: it does every
        // Unity-touching step inline (file pick, loudness gain, resume calc, cache bookkeeping,
        // volume) and then hands the reader-open + CmmMixer.Play to the background start worker
        // (NativeStartWorker). Because all of this runs at trigger time — before any world
        // load/unload freeze — the music starts even while the main thread is subsequently
        // frozen. forcedFile (skip-to-specific) overrides the pick.
        //
        // Loudness change vs. the old coroutine: we no longer BLOCK the start on measurement.
        // Blocking re-introduced a main-thread stall — the exact thing this path removes. We
        // read the stored gain now (real for staged / menu-prewarmed tracks, which is the
        // common case; 1.0 if a straggler isn't measured yet) and fire a non-blocking measure
        // so it's correct next time.
        // fadeOverride ≥ 0 replaces the fade the context/transition settings would give
        // (the manual-skip keybinds pass CMMConfig.ManualSkipFadeDuration so a keypress
        // switches quickly regardless of the context's fadeintime); -1 = no override.
        internal void StartNativeContext(string contextName, bool isSameContextSwitchingToNextTrack,
                                         int transitionId, string forcedFile, float fadeOverride = -1f)
        {
            if (transitionId != _transitionId) return;
            Contexts ctx = GetContext(contextName);
            if (ctx == null || !cacheMap.TryGetValue(contextName, out ContextCache cache)) return;

            // Remember the voice of the context we're leaving so a quick flip back can reclaim
            // its still-fading track (and so a stinger's ducked bed can be reclaimed after).
            if (_nativeActiveContext != null
                && !string.Equals(_nativeActiveContext, contextName, StringComparison.OrdinalIgnoreCase))
            {
                long leavingVid = Interlocked.Read(ref _nativeVoiceId);
                if (leavingVid != 0) _recentNativeVoice[_nativeActiveContext] = leavingVid;
            }

            // ── C: reverse-fade reclaim ──────────────────────────────────────────
            // A genuine cross-context re-entry (not a same-context track flip, not a forced
            // skip, not a stinger) into a context whose voice is still audibly fading out:
            // ramp that voice back to full instead of restarting the track from the top. Also
            // reclaims the ducked bed when a stinger ends.
            if (CMMConfig.ReverseFadeReclaim
                && !isSameContextSwitchingToNextTrack
                && forcedFile == null
                && !ctx.PlayOnce
                && _recentNativeVoice.TryGetValue(contextName, out long reclaimId)
                && CmmMixer.Instance != null
                && CmmMixer.Instance.TryReclaim(reclaimId, ctx.FadeInDuration > 0f ? ctx.FadeInDuration : CrossFadeDuration))
            {
                _recentNativeVoice.Remove(contextName);
                _nativeActiveContext = contextName;
                Interlocked.Exchange(ref _nativeVoiceId, reclaimId);
                cache.lastLeftTime = -1f;            // active again; don't let staleness clear resume
                CmmMixer.MasterVolume = CMMConfig.GetVolume(inGame, ctx.VolumeOverride);
                CMMConfig.D($"[CMM] native: reverse-fade reclaim {contextName} (voice {reclaimId}).");

                // Re-arm auto-advance for the reclaimed voice (its prior loop was cancelled by
                // the transitionId bump). The reclaimed voice is still playing cache.lastPlayedFile.
                ArmNativeAutoAdvance(contextName, transitionId, !ctx.Loop && !ctx.PlayOnce, cache.lastPlayedFile);
                CMMPerf.EndTransition();
                return;
            }

            // ── Resume decision (fresh switch-in only) ───────────────────────────
            // A saved resume position belongs to the SPECIFIC track that was playing — it's
            // meaningless for any other track. So when we resume, we must replay THAT exact
            // file; otherwise the staged/random pick below would choose a different track in a
            // multi-track context and inherit the old track's position (random track starting
            // mid-way — the reported bug). Single-track contexts only ever had one file to land
            // on, which is why they looked fine.
            float startSeconds = 0f;
            string resumeFile = null;
            if (!isSameContextSwitchingToNextTrack && forcedFile == null
                && ctx.CanBeResumed && cache.resumePosition > 0f
                && !string.IsNullOrEmpty(cache.lastPlayedFile)
                && !_invalidFiles.Contains(cache.lastPlayedFile))
            {
                float elapsed = Time.realtimeSinceStartup - cache.lastLeftTime;
                if (elapsed < ctx.TimeUntilContextStale && File.Exists(cache.lastPlayedFile))
                {
                    resumeFile   = cache.lastPlayedFile;   // replay the exact track the position belongs to
                    startSeconds = cache.resumePosition;
                }
                else cache.resumePosition = 0f;            // stale (or file gone) → fresh pick from 0
            }

            // ── Select the file ──
            string file = forcedFile ?? resumeFile;
            if (file == null)
            {
                if (!isSameContextSwitchingToNextTrack
                    && cache.stagedFile != null && !_invalidFiles.Contains(cache.stagedFile))
                    file = cache.stagedFile;        // pre-measured pin for a fresh switch-in
                else if (isSameContextSwitchingToNextTrack)
                    file = PreferPreStagedNext(contextName, cache);   // prepared (measured) next, else fresh
                else
                    file = PickNextFile(contextName, null);
            }
            cache.stagedFile = null;                // consume the pin; maintenance re-pins later

            if (file == null)
            {
                CMMConfig.D($"[CMM] native: no file for {contextName} — fading to silence.");
                StopNativeIfActive(CrossFadeDuration);
                if (currentContext?.Context == contextName) { _pendingContext = null; currentContext = GetContext("Silence"); }
                return;
            }

            // ── Loudness gain (non-blocking; see method header) ──
            float trackGain = CMMConfig.LoudnessNormalizationEnabled ? LoudnessStore.GetTrackGain(file) : 1f;
            if (CMMConfig.LoudnessNormalizationEnabled && !LoudnessStore.IsMeasured(file))
                CMM_CoRoutines.StartCoroutine(EnsureLoudnessMeasured(file));

            bool loop = ctx.Loop;
            // PlayOnce stingers (Death / Spawn / POI_Cleared) honor their own fadeintime as the
            // window their track fades IN over — and, for POI_Cleared, the window the bed ducks
            // DOWN over (it plays the "area cleared" fanfare OVER the existing music instead of
            // replacing it; see the duck comment below). fadeintime=0 punches in instantly (native
            // snaps both the incoming voice and any ducked/outgoing bed on a 0 fade). A small value
            // like 0.05 avoids a hard-cut click while still feeling instant.
            bool poiCleared = contextName == "POI_Cleared";
            float fade;
            if (ctx.PlayOnce) fade = ctx.FadeInDuration;
            else if (isSameContextSwitchingToNextTrack) fade = Mathf.Max(0.25f, TrackTransitionFadeDuration);
            else fade = ctx.FadeInDuration > 0f ? ctx.FadeInDuration : CrossFadeDuration;
            if (fadeOverride >= 0f) fade = fadeOverride;   // manual skip: keypress-fast, not context-slow

            // ── Cache bookkeeping (main thread, before dispatch) ──
            cache.CommitToHistory(file);
            cache.playingFile        = file;
            cache.lastPlayedFile     = file;
#if CMM_DEBUG_HUD
            cache.playingFilePreMeasured = LoudnessStore.IsMeasured(file);   // HUD: pre-normalized vs on-the-fly
#endif
            cache.resumePosition     = 0f;
            cache.lastLeftTime       = -1f;
            _nativeActiveContext = contextName;
            Interlocked.Exchange(ref _nativeVoiceId, 0);   // the worker sets the real id once Play succeeds
            CmmMixer.MasterVolume = CMMConfig.GetVolume(inGame, ctx.VolumeOverride);

            // Menu cold-start (A20): the vanilla title theme can already be playing when the mod
            // loads (BackgroundMusicMono.Start() ran before our Start-postfix mute could catch it)
            // — without this the vanilla theme keeps playing under our custom menu track. Fade it
            // out here as our track takes over (no-op in-game, or when the bed is already
            // muted/silent). Native menu music only runs when the Menu context HAS files, so this
            // never silences vanilla without a custom track to replace it.
            if (!inGame) FadeOutMenuVanillaIfAudible();

            // A PlayOnce stinger (Spawn / Death / POI_Cleared) plays OVER the music underneath.
            // Death / Spawn take the bed fully OUT (duckTo -1 = full crossfade-out): Death needs the
            // SILENCE between the death stinger and the respawn Spawn stinger (bed gone + scan loop
            // dead-gated until respawn), Spawn a clean hit. POI_Cleared instead DUCKS the bed — it
            // keeps playing, quieter, under the "area cleared" fanfare and swells back over
            // STINGER_RESTORE_SEC (0.6s, native) once the fanfare retires, so exploration music
            // resumes naturally.
            //
            // TEMPORARY: duck level forced to 0 for now — the bed is silenced fully under the fanfare
            // (fades to 0 over `fade`, does NOT swell back, since a 0 target isn't marked ducked). The
            // intended AUDIBLE duck is 35% (StingerDuckLevel) of POI_Cleared's OWN volume — its
            // <volume> setting, or the global VolumeModifier when that's unset (-1), matching how
            // CMMConfig.GetVolume resolves an effective volume. Restore that to re-enable ducking:
            //   CmmMixer.StingerDuckLevel * (ctx.VolumeOverride >= 0f ? ctx.VolumeOverride : CMMConfig.VolumeModifier)
            float duckTo = poiCleared ? 0f /* TEMP: duck level 0 = bed silenced; restore to 35% of POI_Cleared volume (see note above) */ : -1f;

            // ── Hand the open + Play to the start worker (survives a main-thread freeze) ──
            EnsureNativeWorker();
            var req = new NativeStartReq
            {
                Context = contextName, File = file, TransitionId = transitionId,
                StartSeconds = startSeconds, Fade = fade, Loop = loop, TrackGain = trackGain,
                DuckOthersTo = duckTo,
                // Only a genuine context change may flush a deep cushion. A same-context Next/Prev
                // must NOT — flushing teleports the outgoing track being crossfaded (jump + swell) —
                // and neither may an AUDIBLE POI_Cleared duck (duckTo > 0): the bed keeps playing
                // under the fanfare, so a flush would skip it forward mid-duck. A duck to 0 (bed
                // silenced) or a full crossfade-out (-1) may snap for a prompt fanfare.
                SnapTight = !isSameContextSwitchingToNextTrack && duckTo <= 0f,
            };
            lock (_natQueueLock) _natQueue.Enqueue(req);
            _natSignal.Set();

            CMMConfig.D($"[CMM] native: dispatched {Path.GetFileName(file)} for {contextName} " +
                    $"(fade {fade:F1}s, loop {loop}, gain {trackGain:F2}, start {startSeconds:F1}s" +
                    $"{(duckTo >= 0f ? $", duck→{duckTo:F2}" : "")})");

            // ── Auto-advance (non-loop, non-stinger): crossfade to the next pick at end ──
            ArmNativeAutoAdvance(contextName, transitionId, !loop && !ctx.PlayOnce, file);

            CMMPerf.EndTransition();
        }

        // ── Auto-advance arming (frame-loop coroutine OR off-frame-loop scheduler poll) ──
        // Whether the current track should auto-advance at end. Read on the scheduler thread
        // (NativeAdvanceTick); written on the main thread (ArmNativeAutoAdvance). Loop and
        // PlayOnce contexts never advance, so it stays false for them.
        private static volatile bool _nativeAdvanceArmed;
        // Single-flight latch: set by the poll when it posts an advance, cleared by the main
        // thread once the advance is applied — stops a fast poll double-dispatching.
        private static volatile bool _advancePending;

        // Arm (or, for loop/PlayOnce, leave disarmed) auto-advance for the track just started
        // in `contextName` under `transitionId`, `currentFile` being the file it is playing.
        // NativeScheduler on → the singleton scheduler poll (NativeAdvanceTick) drives
        // advancement off the frame loop, and (NativePreStageAdvance on) the next track is
        // pre-staged so the advance needs no main-thread hop; off → a per-track coroutine
        // (NativeTrackLoop) on the frame loop, exactly as before. Centralizes both arm sites
        // (fresh dispatch + reverse-fade reclaim) and the post-advance reconcile re-arm.
        private void ArmNativeAutoAdvance(string contextName, int transitionId, bool shouldAdvance, string currentFile)
        {
            if (_trackEndCoroutine != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine); _trackEndCoroutine = null; }
            if (CMMConfig.NativeScheduler)
            {
                _advancePending = false;             // fresh track: no advance in flight
                _nativeAdvanceArmed = shouldAdvance;
                if (shouldAdvance) PreStageNextAdvance(contextName, transitionId, currentFile);
                else Interlocked.Exchange(ref _preStaged, null);
                return;
            }
            _nativeAdvanceArmed = false;             // coroutine path owns advancement
            if (shouldAdvance)
                _trackEndCoroutine = CMM_CoRoutines.StartCoroutine(NativeTrackLoop(contextName, transitionId));
        }

        // ── Pre-staged next-track advance (true advance-through-freeze) ──────────
        // An immutable, fully-formed advance to a context's NEXT track, computed on the main
        // thread when the CURRENT track starts. The scheduler thread can dispatch ps.Req
        // straight to the start worker at track-end with NO main-thread hop, so the next track
        // begins even while Unity's main thread is frozen; ReconcilePreStagedAdvance then fixes
        // up the main-thread-only cache bookkeeping (and stages the next-next track) once the
        // main thread runs again. Published/consumed via Interlocked (reference swap is atomic;
        // the object is immutable after construction) so no tearing and no lock on the read.
        private sealed class PreStagedAdvance
        {
            public NativeStartReq Req;
            public string Context;
            public int    TransitionId;
            public string NextFile;
        }
        private static PreStagedAdvance _preStaged;   // accessed only via Interlocked

        // MAIN THREAD: compute and publish the pre-staged advance for `contextName`'s next
        // track (the one after `currentFile`), under the SAME transition id the current track
        // plays — an auto-advance within a context is a continuation, not a context change, so
        // the worker's id guard still passes at dispatch time. Pre-measures the next track's
        // loudness (non-blocking) so its gain is ready; the gain baked into the request is the
        // value known now, which for a minutes-long track is virtually always the finished
        // measurement by the time it actually advances. Clears the pre-stage when the context
        // can't auto-advance or has no next file.
        private void PreStageNextAdvance(string contextName, int transitionId, string currentFile)
        {
            if (!CMMConfig.NativePreStageAdvance) { Interlocked.Exchange(ref _preStaged, null); return; }

            Contexts ctx = GetContext(contextName);
            if (ctx == null || ctx.Loop || ctx.PlayOnce
                || !cacheMap.TryGetValue(contextName, out ContextCache cache))
            {
                Interlocked.Exchange(ref _preStaged, null);
                return;
            }

            // A rewound history (the user pressed Prev) replays forward through what
            // they rewound before anything new is picked — so the pre-staged advance
            // must be that forward entry, or the auto-advance would desert the replay.
            // (Same eligibility guard as PreferPreStagedNext: a since-banned or
            // invalidated forward entry is not replayed.)
            string nextFile = cache.HistoryForward();
            if (nextFile == null || _invalidFiles.Contains(nextFile) || LoudnessStore.IsBannedPath(nextFile))
                nextFile = PickNextFile(contextName, avoidFile: currentFile);
            if (nextFile == null) { Interlocked.Exchange(ref _preStaged, null); return; }

            float gain = CMMConfig.LoudnessNormalizationEnabled ? LoudnessStore.GetTrackGain(nextFile) : 1f;
            if (CMMConfig.LoudnessNormalizationEnabled && !LoudnessStore.IsMeasured(nextFile))
                CMM_CoRoutines.StartCoroutine(EnsureLoudnessMeasured(nextFile));

            // N-ahead: also pre-measure a few MORE of this context's tracks so a chain of short tracks can't
            // outrun the measurement (the exact next is measured above; these top it up to LoudnessMeasureAhead).
            // Sampled from the snapshot directly — side-effect-free, so it never disturbs PickNextFile's
            // no-repeat history. RequestPrewarm no-ops on already-measured / in-flight files.
            int extra = CMMConfig.LoudnessMeasureAhead - 1;
            if (extra > 0 && CMMConfig.LoudnessNormalizationEnabled
                && cache.directorySnapshot != null && cache.directorySnapshot.Length > 1)
            {
                string[] snap = cache.directorySnapshot;
                for (int k = 0; k < extra; k++)
                    RequestPrewarm(snap[UnityEngine.Random.Range(0, snap.Length)]);
            }

            var ps = new PreStagedAdvance
            {
                Context = contextName,
                TransitionId = transitionId,
                NextFile = nextFile,
                Req = new NativeStartReq
                {
                    Context = contextName, File = nextFile, TransitionId = transitionId,
                    StartSeconds = 0f, Fade = Mathf.Max(0.25f, TrackTransitionFadeDuration),
                    Loop = false, TrackGain = gain, DuckOthersTo = -1f, SnapTight = false,
                },
            };
            Interlocked.Exchange(ref _preStaged, ps);
        }

        // MAIN THREAD (posted from the scheduler after a pre-staged dispatch): commit the cache
        // bookkeeping for the track the worker just started, but only if nothing superseded it
        // — the worker applies the same transition-id + active-context guard to the AUDIO, so
        // this keeps the cache consistent with what actually plays. Then re-arm advance and
        // pre-stage the next-next track. Always releases the single-flight latch.
        private static void ReconcilePreStagedAdvance(PreStagedAdvance ps)
        {
            if (ps.TransitionId == _transitionId
                && string.Equals(_nativeActiveContext, ps.Context, StringComparison.OrdinalIgnoreCase)
                && cacheMap.TryGetValue(ps.Context, out ContextCache cache))
            {
                cache.CommitToHistory(ps.NextFile);
                cache.playingFile        = ps.NextFile;
                cache.lastPlayedFile     = ps.NextFile;
#if CMM_DEBUG_HUD
                // Pre-staged next tracks are picked + measured while the previous track plays,
                // so by advance time they're almost always measured (HUD: pre-normalized).
                cache.playingFilePreMeasured = LoudnessStore.IsMeasured(ps.NextFile);
#endif
                cache.resumePosition     = 0f;
                cache.lastLeftTime       = -1f;
                cache.stagedFile         = null;
                CmmMixer.MasterVolume = CMMConfig.GetVolume(inGame, GetContext(ps.Context)?.VolumeOverride ?? -1f);
                // Apply the loudness gain the track was measured to. The gain baked into ps.Req
                // was read at PRE-stage time, when this (freshly-picked) next track was almost
                // never measured yet — so a measurement that completed WHILE the previous track
                // was still playing (its SettleTrackGainIntoLiveVoice ran as a no-op because this
                // file wasn't playing yet) never reached the voice, leaving a natural transition
                // un-normalized. Now that this track IS the playing one, re-read the current gain
                // and settle it into the voice once the worker commits it.
                if (CMMConfig.LoudnessNormalizationEnabled)
                    CMM_CoRoutines.StartCoroutine(
                        Instance.SettlePreStagedGain(ps.NextFile, ps.TransitionId));
                // Re-arm + pre-stage the next-next track (also clears _advancePending).
                Instance.ArmNativeAutoAdvance(ps.Context, ps.TransitionId, true, ps.NextFile);
            }
            else
            {
                _advancePending = false;   // superseded by a real context change — just release
            }
        }

        // Settle the loudness gain into the voice a pre-staged advance just started. The worker
        // opens the reader + calls Play on its own thread, so the voice id isn't committed the
        // instant reconcile runs; wait for it (bounded), then push the CURRENT stored gain via
        // SettleTrackGainIntoLiveVoice (which reads the gain fresh, so a measurement that landed
        // between pre-stage and advance is now applied). Bails if a real context switch supersedes
        // (transition id bumped) or the worker never commits (open failed). A same-context
        // auto-advance keeps the same transition id, so the guard only trips on a genuine change.
        private IEnumerator SettlePreStagedGain(string file, int transitionId)
        {
            float waited = 0f;
            while (Interlocked.Read(ref _nativeVoiceId) == 0)
            {
                if (transitionId != _transitionId || waited >= 5f) yield break;
                yield return null;
                waited += Time.unscaledDeltaTime;
            }
            if (transitionId != _transitionId) yield break;
            SettleTrackGainIntoLiveVoice(file);
        }

        // Off-frame-loop auto-advance: the scheduler twin of NativeTrackLoop. Runs on
        // CmmScheduler's CRITICAL lane (a dedicated thread) every TrackEndPollInterval when
        // NativeScheduler is on, so track-end advancement survives a main-thread freeze (a
        // world load/unload that stalls Unity's coroutines) instead of stalling with it —
        // the housekeeping ticks' blocking Sends park only their own lane, never this poll.
        // LANE CONTRACT: this body must never call MainThreadDispatcher.Send; a blocking
        // wait on a frozen main thread here would re-create the very stall the lane split
        // exists to remove. Everything it reads is thread-safe — the transition counter and
        // active-context marker (volatile), the voice id (Interlocked), and the mixer's own
        // locked TryGetState — so the POLL runs here; only the ADVANCE (++_transitionId +
        // StartNativeContext, which mutate main-thread-only state) hops back to the main
        // thread via MainThreadDispatcher.Post.
        //
        // Single-flight via _advancePending: once an advance is posted the poll stops until the
        // main thread applies it, so a fast poll can't double-dispatch. The posted advance
        // re-validates the transition id + active context on the main thread, so a real context
        // switch that lands first simply turns it into a no-op. Frozen while paused (matches the
        // old coroutine, whose WaitForSeconds stopped at timeScale 0) so the playlist doesn't
        // burn through an AFK pause. Stop-to-silence paths null _nativeActiveContext, which
        // gates this off without needing to touch every stop site.
        internal static void NativeAdvanceTick()
        {
            if (_functionallyDormant) return;   // A/B dormant arm: no track auto-advance
            if (!_nativeAdvanceArmed || _advancePending || _pauseDucked) return;

            string ctx = _nativeActiveContext;
            if (ctx == null) return;
            long vid = Interlocked.Read(ref _nativeVoiceId);
            if (vid == 0) return;                        // worker hasn't committed the voice yet

            var mx = CmmMixer.Instance;
            if (mx == null) return;

            int tid = _transitionId;                     // capture for the main-thread re-validation
            bool advance;
            if (!mx.TryGetState(vid, out double posSec, out double lenSec, out bool active) || !active)
                advance = true;                          // voice ended → advance now
            else if (lenSec > 0)
            {
                // Cap the pre-roll lead to HALF the track length so a track shorter than the
                // lead window isn't advanced the instant it starts (see NativeTrackLoop).
                double lead = Math.Max(TrackTransitionFadeDuration, 0.5);
                double effLead = Math.Min(lead, lenSec * 0.5);
                advance = lenSec - posSec <= effLead;
            }
            else advance = false;                        // unknown length → wait for EOF (active=false)

            if (!advance) return;

            // ── Pre-staged path: dispatch straight to the start worker, no main-thread hop ──
            // If a valid pre-stage exists for this exact context + transition, hand its request
            // to the worker NOW (so the next track begins even while the main thread is frozen)
            // and reconcile the cache bookkeeping back on the main thread whenever it next runs.
            PreStagedAdvance ps = Interlocked.CompareExchange(ref _preStaged, null, null);   // atomic read
            if (CMMConfig.NativePreStageAdvance && ps != null
                && ps.TransitionId == tid
                && string.Equals(ps.Context, ctx, StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref _preStaged, null, ps) == ps)   // consume iff unchanged
            {
                _advancePending = true;
                // Reset the tracked voice to "dispatched, not yet committed" (as StartNativeContext
                // does) BEFORE enqueueing — otherwise, if the reconcile clears _advancePending
                // before the worker commits the new voice, the poll would still see the OLD ended
                // voice and advance a second time. The worker sets the new id once Play succeeds.
                Interlocked.Exchange(ref _nativeVoiceId, 0);
                lock (_natQueueLock) _natQueue.Enqueue(ps.Req);
                _natSignal.Set();
                MainThreadDispatcher.Post(() => ReconcilePreStagedAdvance(ps));
                return;
            }

            // ── Fallback: marshal the advance to the main thread (no valid pre-stage) ──
            _advancePending = true;
            MainThreadDispatcher.Post(() =>
            {
                // Main thread: advance only if nothing changed since the poll captured state.
                if (tid == _transitionId
                    && string.Equals(_nativeActiveContext, ctx, StringComparison.OrdinalIgnoreCase))
                {
                    int newId = ++_transitionId;
                    Instance.StartNativeContext(ctx, true, newId, null);
                }
                _advancePending = false;
            });
        }

        // Polls the active voice and, at the transition lead window (or once it has
        // ended), crossfades in the context's next track — the frame-loop fallback for
        // NativeAdvanceTick. Reads _nativeVoiceId (set by the start
        // worker) each tick rather than a captured id, so it works even though the voice
        // doesn't exist yet at dispatch. Cancelled by a _transitionId bump (any context
        // switch) or the mixer moving off this context.
        internal IEnumerator NativeTrackLoop(string contextName, int transitionId)
        {
            float lead = Mathf.Max(TrackTransitionFadeDuration, 0.5f);
            while (true)
            {
                if (transitionId != _transitionId || _nativeActiveContext != contextName) yield break;
                long vid = Interlocked.Read(ref _nativeVoiceId);
                if (vid != 0)                                                // worker has committed the voice
                {
                    if (CmmMixer.Instance == null) break;
                    if (!CmmMixer.Instance.TryGetState(vid, out double posSec, out double lenSec, out bool active) || !active)
                        break;                                              // voice gone (ended) → advance now
                    // Cap the pre-roll lead to HALF the track length: a track SHORTER than the lead
                    // window (e.g. a few-second .wav) otherwise satisfies remain<=lead from the very
                    // first frame and is advanced the instant it starts — the "press Next → it flashes
                    // then jumps to another track" double-dispatch. Unknown length (lenSec<=0, e.g. raw
                    // ADTS) keeps the full lead and relies on the active=false EOF check above.
                    if (lenSec > 0)
                    {
                        double effLead = Math.Min(lead, lenSec * 0.5);
                        if (lenSec - posSec <= effLead) break;              // near end → advance
                    }
                }
                yield return new WaitForSeconds(TrackEndPollInterval);
            }
            if (transitionId != _transitionId || _nativeActiveContext != contextName) yield break;
            int newId = ++_transitionId;
            StartNativeContext(contextName, true, newId, null);
        }

        // Same-context track switch (manual Next, or an auto-advance that fell back to
        // StartNativeContext instead of the direct pre-staged dispatch): reuse the ONE next
        // track the playing context already prepared and began measuring (rule 2 — the playing
        // context keeps a normalized track ready to go) rather than re-rolling a fresh unmeasured
        // pick that discards that preparation. Returns _preStaged's next file when it's for this
        // context and still valid; otherwise a fresh PickNextFile. TransitionId is deliberately
        // NOT matched here — that id guards the freeze-survival DISPATCH, not file validity; a
        // manual skip bumps it, yet the prepared file is still the right track to play (and the
        // caller re-reads its loudness gain + re-arms a fresh pre-stage afterward).
        private static string PreferPreStagedNext(string contextName, ContextCache cache)
        {
            // A rewound history replays forward before anything fresh (usually the
            // pre-stage below already holds this exact file — see PreStageNextAdvance).
            string fwd = cache.HistoryForward();
            if (fwd != null && !_invalidFiles.Contains(fwd) && !LoudnessStore.IsBannedPath(fwd))
                return fwd;

            PreStagedAdvance ps = Interlocked.CompareExchange(ref _preStaged, null, null);   // atomic read
            if (ps != null
                && string.Equals(ps.Context, contextName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(ps.NextFile)
                && !_invalidFiles.Contains(ps.NextFile)
                && !LoudnessStore.IsBannedPath(ps.NextFile)   // staged before the ban keypress
                && !string.Equals(ps.NextFile, cache.lastPlayedFile, StringComparison.OrdinalIgnoreCase)
                && File.Exists(ps.NextFile))
                return ps.NextFile;
            return PickNextFile(contextName, cache.lastPlayedFile);
        }

        private static string PickNextFile(string contextName, string avoidFile)
        {
            Contexts ctx = GetContext(contextName);
            if (ctx == null) return null;

            // Contexts with no folder (path="" — Vanilla, Silence) have no custom
            // music. Without this guard, Path.Combine(MusicDirectory, "") resolves to
            // the MUSIC ROOT, so any loose files a user left in the root get enumerated
            // and played — most visibly when Menu falls back to Vanilla and ends up
            // playing a stray root track instead of the menu's own folder. The presence
            // scans and FileWatchLoop already skip empty-path contexts; this is the last
            // enumeration site (the fallback pick) that didn't.
            if (string.IsNullOrEmpty(ctx.Source)) return null;

            ContextCache cache = cacheMap[contextName];

            string[] files = cache.directorySnapshot;
            if (files == null)
            {
                if (ctx.IsPlaylist)
                {
                    // Playlist source: gather the listed tracks (flat pool; the subfolders
                    // flag and the recursive crawler don't apply). Cheap single-file read.
                    files = PlaylistSource.ReadTracks(PlaylistSource.ResolveFullPath(ctx.Source));
                    cache.directorySnapshot = files;
                }
                else
                {
                    // Recursive contexts are populated exclusively by RecursiveCrawlLoop off the
                    // main thread. A null snapshot means the crawler's first sweep hasn't published
                    // yet — never enumerate the tree here: a subfolder context can span hundreds of
                    // thousands of files, and a synchronous walk on the frame thread is a multi-
                    // second freeze (the exact stall the crawler exists to avoid). Read as "not
                    // ready" so the caller falls back to a lower-priority context; the crawler's
                    // incremental publish will make this context playable within a second or two.
                    if (ctx.IncludeSubfolders) return null;

                    string dir = Path.Combine(MusicDirectory, ctx.Source);
                    if (!Directory.Exists(dir)) return null;
                    files = EnumerateContextFiles(ctx, dir, out string[] pf);
                    cache.directorySnapshot = files;
                    cache.playlistFiles = pf;   // flat folder's in-folder .m3u list (for the watch)
                }
            }
            else if (_invalidFiles.Count > 0)
            {
                files = files.Where(f => !_invalidFiles.Contains(f)).ToArray();
            }

            if (files.Length == 0) return null;

            switch (GlobalTrackOrder)
            {
                case TrackOrderMode.Sequential:
                    {
                        // Sequential is an explicit ordering — don't reorder it by
                        // measured-ness (that would skip tracks). Sequential users accept
                        // the occasional first-encounter unity play.
                        if (cache.sequentialIndex >= files.Length) cache.sequentialIndex = 0;
                        string pick = files[cache.sequentialIndex];
                        cache.sequentialIndex = (cache.sequentialIndex + 1) % files.Length;
                        // Banned tracks are skipped in place (the ordering is otherwise
                        // preserved; favourites don't apply to an explicit order). If the
                        // bounded walk finds nothing unbanned the whole folder is banned —
                        // null, so the context resolves through its fallback chain (the
                        // resolver normally catches this first via ContextHasFiles).
                        if (LoudnessStore.BannedCount > 0)
                        {
                            for (int n = 1; n < files.Length && LoudnessStore.IsBannedPath(pick); n++)
                            {
                                pick = files[cache.sequentialIndex];
                                cache.sequentialIndex = (cache.sequentialIndex + 1) % files.Length;
                            }
                            if (LoudnessStore.IsBannedPath(pick)) return null;
                        }
                        return pick;
                    }
                case TrackOrderMode.RandomNoRepeat:
                    {
                        if (files.Length == 1)
                            return LoudnessStore.IsBannedPath(files[0]) ? null : files[0];
                        string[] candidates = avoidFile != null
                            ? files.Where(f => f != avoidFile).ToArray()
                            : files;
                        if (candidates.Length == 0) candidates = files;
                        string pick = PickRandomHonouringFlags(candidates);
                        // Everything except the avoided (current) track is banned: retry
                        // with the full pool so the sole unbanned track — the current one —
                        // replays rather than a banned pick or silence.
                        if (pick == null && candidates != files)
                            pick = PickRandomHonouringFlags(files);
                        return pick;
                    }
                default:
                    {
                        return PickRandomHonouringFlags(files);
                    }
            }
        }

        // Random pick honouring the keybind-set track flags: banned tracks are excluded
        // and each favourite counts FavouriteWeight× in the roll. Returns null when
        // every candidate is banned — the caller (and the resolver, via the ban-aware
        // ContextHasFiles) treats that like an empty folder and falls back. When no
        // flags exist anywhere (the common case) this is the same single uniform roll
        // as before; with flags it's one O(n) pass of zero-alloc HashSet checks.
        private static string PickRandomHonouringFlags(string[] candidates)
        {
            bool anyBan = LoudnessStore.BannedCount > 0;
            bool anyFav = LoudnessStore.FavouriteCount > 0;
            if (!anyBan && !anyFav)
                return candidates[UnityEngine.Random.Range(0, candidates.Length)];

            float favWeight = CMMConfig.FavouriteWeight < 1f ? 1f : CMMConfig.FavouriteWeight;
            int eligible = 0, favs = 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (anyBan && LoudnessStore.IsBannedPath(candidates[i])) continue;
                eligible++;
                if (anyFav && LoudnessStore.IsFavouritePath(candidates[i])) favs++;
            }
            if (eligible == 0) return null;   // all banned → resolve like an empty folder

            double roll = UnityEngine.Random.Range(0f, 1f)
                          * ((eligible - favs) + favs * (double)favWeight);
            string last = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (anyBan && LoudnessStore.IsBannedPath(candidates[i])) continue;
                last = candidates[i];
                roll -= (anyFav && LoudnessStore.IsFavouritePath(candidates[i])) ? favWeight : 1.0;
                if (roll < 0.0) return candidates[i];
            }
            return last;   // fp edge (roll landed exactly on the total)
        }

        // ── Memory summary ────────────────────────────────────────────────────
        // CMM no longer holds Unity AudioClips/PCM in the managed cache — the native mixer
        // decodes and buffers audio in its own (native) memory, off the managed heap. So
        // there is no managed clip-cache footprint to report here.
        public static long GetCacheMemoryBytes() => 0;

        public static string GetCacheMemorySummary(bool includePerContext = true)
            => "native mixer (buffers held in native memory)";
    }
}
