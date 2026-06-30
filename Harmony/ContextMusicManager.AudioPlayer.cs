
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
        private static AudioSource CMM_Primary_Channel;
        private static AudioSource CMM_Secondary_Channel;
        private static bool _primaryIsActive = true;
        private static AudioSource ActiveChannel => _primaryIsActive ? CMM_Primary_Channel : CMM_Secondary_Channel;
        private static AudioSource InactiveChannel => _primaryIsActive ? CMM_Secondary_Channel : CMM_Primary_Channel;

        // The resolved context name each physical channel last played. Tracked so a
        // re-entry into a context that is still audibly fading out on the inactive
        // channel can reverse its fade (ramp it back up) instead of reloading and
        // fading in from zero. See the reverse-fade reclaim path in PlayFromCache.
        private static string _primaryChannelContext;
        private static string _secondaryChannelContext;
        private static string ActiveChannelContext
        {
            get => _primaryIsActive ? _primaryChannelContext : _secondaryChannelContext;
            set { if (_primaryIsActive) _primaryChannelContext = value; else _secondaryChannelContext = value; }
        }
        private static string InactiveChannelContext
        {
            get => _primaryIsActive ? _secondaryChannelContext : _primaryChannelContext;
            set { if (_primaryIsActive) _secondaryChannelContext = value; else _primaryChannelContext = value; }
        }

        // Slots awaiting disposal. A FIFO queue rather than a single field so that
        // two transitions staging disposals back-to-back (e.g. rapid combat flicker)
        // can't overwrite — and thereby leak — an earlier slot's AudioClip and its
        // live streaming FileStream handle. Drained by DestroyPendingClipAndAudio().
        private static readonly Queue<TrackSlot> _pendingDisposals = new Queue<TrackSlot>();

        // Menu-prewarm loudness state. _loudnessQueue holds files awaiting a
        // measurement; _loudnessProcessed marks files resolved this session (measured,
        // confirmed current, or skipped) so we don't re-stat/re-measure them. Both are
        // main-thread only, like LoudnessStore. Plain FIFO: it carries only the bounded
        // menu prewarm set (BuildMenuPrewarmQueue); on-demand next-track measurement
        // goes straight through EnsureLoudnessMeasured, not this queue.
        private static readonly LinkedList<string> _loudnessQueue = new LinkedList<string>();
        private static string LoudnessDequeue()
        {
            string v = _loudnessQueue.First.Value;
            _loudnessQueue.RemoveFirst();
            return v;
        }
        private static void LoudnessEnqueue(string p) => _loudnessQueue.AddLast(p);
        private static readonly HashSet<string> _loudnessProcessed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Paths whose measurement thread is running right now, keyed so the sweep and the
        // block-before-play path can't double-decode the same file (observed in the perf
        // logs: one track measured twice 0.5s apart). A second request for an in-flight
        // path joins the existing MeasureHandle instead of spawning a new thread.
        private static readonly Dictionary<string, MeasureHandle> _loudnessInFlight =
            new Dictionary<string, MeasureHandle>(StringComparer.OrdinalIgnoreCase);
        // One-shot: on the menu most context snapshots aren't built yet (ContextScanLoop
        // is in-game only), so the pre-warm sweep has nothing to queue. The first time the
        // queue comes up empty we ask PresenceScanLoop to enumerate every context folder
        // off-thread; once the snapshots exist the next Refill can pre-warm them.
        private static bool _loudnessSnapshotsRequested;

        // ── Menu prewarm prescan ──────────────────────────────────────────────
        // On the menu, measure a BOUNDED prewarm set — one track per context, plus a
        // second for the context currently playing — on a pool of worker threads, then
        // stop. The point is that every context has a normalized track ready for its
        // first play without scanning the whole library on the menu; the rest is left
        // to the on-demand look-ahead (EnsureLoudnessMeasured / ScheduleNextCacheLoad).
        // All orchestration here (queue, store, bookkeeping) stays on the main-thread
        // coroutine — only the decode runs on the workers, each with its OWN scratch.
        // Set once the bounded menu prewarm goal is met (one track per context plus
        // a second for the playing context); stops the menu scanner.
        private static bool _menuPrescanComplete;
        private static AudioDecoder.MeasureScratch[] _prescanScratch;

        // One concurrent measurement slot for the parallel prescan.
        private struct PrescanSlot
        {
            public string Path;
            public long Mtime;
            public MeasureHandle Handle;
        }

        private static void StagePendingDisposal(TrackSlot slot)
        {
            // Borrowed game-owned clips are never ours to destroy/dispose — just drop them.
            if (slot.Borrowed) return;
            if (slot.Clip == null && slot.Reader == null && slot.Decoded == null) return;
            _pendingDisposals.Enqueue(slot);
        }

        // Pulls a previously-staged slot back out of the disposal queue, matched by
        // its AudioClip. Used by the reverse-fade reclaim path: when we left a context
        // its slot was staged for disposal, but if it is still playing on the inactive
        // channel and we re-enter it, we must reclaim that slot so it is not freed
        // mid-fade and so future leaves manage it normally. Returns false (slot left
        // untouched) when the clip is not in the queue.
        private static bool TryReclaimSlotFromDisposal(AudioClip clip, out TrackSlot reclaimed)
        {
            reclaimed = default;
            if (clip == null || _pendingDisposals.Count == 0) return false;
            int count = _pendingDisposals.Count;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                TrackSlot slot = _pendingDisposals.Dequeue();
                if (!found && slot.Clip == clip) { reclaimed = slot; found = true; }
                else _pendingDisposals.Enqueue(slot);
            }
            return found;
        }

        private static bool _scheduleHandledTransition = false;
        public static bool _gameIsLoading = false;

        // Reused-channel residual quick-fade. When a new context interrupts an
        // in-flight crossfade, the channel we repurpose for the incoming priority
        // track may still be playing an audible leftover (the half-arrived previous
        // track, frozen at whatever volume it reached when its crossfade coroutine
        // was stopped). Instantly zeroing it clicks. If that residual is above the
        // threshold we ramp it to silence over this (very short) window first so the
        // incoming music always fades in cleanly; below it the instant cut is inaudible.
        private const float ResidualFadeDuration  = 0.12f;
        private const float ResidualFadeThreshold = 0.1f;

        // ── Playback position (version-independent) ───────────────────────────
        // AudioSource.time is unreliable for streaming AudioClip.Create clips on
        // older Unity: A20 (Unity 2020.3) reports ~0 for PCM-callback clips, so the
        // resume capture stored 0 and every "resume" restarted at the top. A21
        // (2021.3) and 3.0 report it correctly. Derive the position from the backing
        // NAudio reader's consumed-frame count instead — version-independent, and
        // only a hair (one DSP/read buffer, tens of ms) ahead of the audible point.
        // Falls back to AudioSource.time when there is no streaming reader (borrowed
        // vanilla clips, or the preload full-PCM path where .time is reliable anyway).
        private static float GetPlaybackPosition(AudioSource channel, ContextCache cache)
        {
            if (channel == null) return 0f;
            WaveStream reader = cache?.Current.Reader;
            if (reader != null)
            {
                try
                {
                    int blockAlign = reader.WaveFormat.BlockAlign;
                    int sampleRate = reader.WaveFormat.SampleRate;
                    if (blockAlign > 0 && sampleRate > 0)
                        return (float)(reader.Position / (double)blockAlign / sampleRate);
                }
                catch { /* reader disposed or mid-seek; fall back to AudioSource.time */ }
            }
            return channel.time;
        }

        // ── NAudio async decode helper ────────────────────────────────────────
        // FIX: Split into two phases so disk I/O and CPU decode are each on a
        // background thread but the main thread yields a frame between them.
        // This keeps the main thread responsive throughout and avoids a single
        // large ThreadPool item that could block the pool for 500-800ms on slow
        // spinning disks or large files.
        private IEnumerator OpenReaderAsync(string filePath, Action<WaveStream> onComplete)
        {
            // Wait until the engine is not mid-load so we don't compete for I/O.
            float loadWait = 0f;
            while (_gameIsLoading && loadWait < 8f)
            {
                yield return new WaitForSeconds(0.25f);
                loadWait += 0.25f;
            }
            if (loadWait > 0f)
                CMMConfig.D($"[CMM] Deferred open by {loadWait:F1}s (game was loading)");

            WaveStream reader = null;
            bool finished = false;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { reader = AudioDecoder.OpenStreamingReader(filePath); }
                catch { }
                finished = true;
            });

            while (!finished) yield return null;

            if (reader == null)
                MarkInvalidIfCorrupt(filePath, "Preload: failed to open");

            onComplete(reader);
        }

        // ── Validation ────────────────────────────────────────────────────────
        private IEnumerator ValidateFile(string filePath, Action<bool> callback)
        {
            if (!AudioDecoder.IsSupported(filePath)) { callback(false); yield break; }

            {
                Stopwatch sw = Stopwatch.StartNew();
                bool valid = false;
                bool finished = false;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    valid = AudioDecoder.QuickValidate(filePath);
                    finished = true;
                });
                while (!finished) yield return null;
                if (!valid)
                    Log.Warning($"[CMM] Validation failed (NAudio) for {filePath}");
                sw.Stop();
                CMMConfig.D($"[CMM] QuickValidateWithCache took {sw.Elapsed.TotalMilliseconds:F3} ms");
                callback(valid);
            }
        }

        // ── PlayFromCache ─────────────────────────────────────────────────────

        private IEnumerator PlayFromCache(string contextName, bool isSameContextSwitchingToNextTrack, int transitionId)
        {
            if (transitionId != _transitionId) yield break;

            Contexts ctx = GetContext(contextName);
            ContextCache cache = cacheMap[contextName];

            if (_crossFadeCoroutine != null)
            {
                CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine);
                _crossFadeCoroutine = null;
            }

            // ── Reverse-fade reclaim ─────────────────────────────────────────
            // If this context is still audibly fading out on the inactive channel
            // (a crossfade we just interrupted), don't reload it and fade in from
            // zero — reverse the fade. Ramp that still-playing channel back up from
            // its current volume while the channel we were fading IN now fades out.
            if (CMMConfig.ReverseFadeReclaim
                && CMMConfig.CrossfadeEnabled
                && !isSameContextSwitchingToNextTrack
                && !ctx.PlayOnce
                && InactiveChannel != null
                && InactiveChannel.isPlaying
                && InactiveChannel.clip != null
                && InactiveChannel.volume > 0.0001f
                && string.Equals(InactiveChannelContext, contextName, StringComparison.OrdinalIgnoreCase)
                // Only a genuine cross-context re-entry: the active channel must be a
                // DIFFERENT context. If both channels carry this context we're mid
                // same-context track flip — reclaiming would leak the new track.
                && !string.Equals(ActiveChannelContext, contextName, StringComparison.OrdinalIgnoreCase))
            {
                // Recover the live slot. In streaming mode it was staged for disposal
                // when we left; pull it back so it isn't freed mid-fade. In preload
                // mode it still lives in cache.Current. If we can't recover it (e.g. a
                // borrowed vanilla clip), fall through to the normal reload path.
                bool slotOk = cache.Current.Clip == InactiveChannel.clip;
                if (!slotOk && TryReclaimSlotFromDisposal(InactiveChannel.clip, out TrackSlot live))
                {
                    cache.Current = live;
                    slotOk = true;
                }

                if (slotOk)
                {
                    AudioSource keep = InactiveChannel;   // still playing contextName
                    AudioSource drop = ActiveChannel;     // the track we were fading in
                    string leavingCtx = ActiveChannelContext;

                    cache.lastLeftTime = -1f;             // active again; don't let staleness clear it

                    _primaryIsActive = !_primaryIsActive; // keep becomes the active channel
                    ActiveChannelContext = contextName;
                    InactiveChannelContext = leavingCtx;

                    float reclaimFade = ctx.FadeInDuration > 0f ? ctx.FadeInDuration : CrossFadeDuration;
                    CMMConfig.D($"[CMM] Reverse-fade reclaim: {contextName} ramping " +
                            $"{keep.volume:F2}→target over {reclaimFade:F1}s (dropping {leavingCtx ?? "None"})");

                    _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(
                        CrossFade(drop, keep, reclaimFade, _cooldownTarget?.Cooldown ?? 0, drop.volume, keep.volume, contextName));

                    float reclaimLen = keep.clip.length;
                    float reclaimPos = GetPlaybackPosition(keep, cache);
                    if (!ctx.Loop)
                    {
                        _trackEndCoroutine = CMM_CoRoutines.StartCoroutine(
                            TrackEndWatcher(contextName, keep, transitionId));
                        float remaining = (reclaimLen > 0f
                                ? reclaimLen
                                : TrackTransitionFadeDuration + TrackTransitionPreloadLead + 1f) - reclaimPos;
                        _nextCacheCoroutine = CMM_CoRoutines.StartCoroutine(
                            ScheduleNextCacheLoad(contextName, remaining, transitionId));
                    }

                    CMMPerf.EndTransition();
                    _activeMusicCoroutine = null;
                    yield break;
                }
            }

            // ── Game-clip (vanilla section) path ─────────────────────────────
            // A trader context with no custom files resolves to itself but is backed
            // by the game's own DMS section clip, played on our channel while the
            // Conductor stays frozen. Borrow the clip — we never decode or destroy it.
            AudioClip gameClip = null;
            bool useGameClip = !ContextHasFiles(contextName)
                               && GamePrefs.GetBool(EnumGamePrefs.OptionsDynamicMusicEnabled)
                               && TryGetReadyVanillaClip(contextName, out gameClip);

            // ── Ensure clip is available ─────────────────────────────────────
            if (useGameClip)
            {
                if (cache.Current.Clip != gameClip)
                {
                    if (cache.Current.Clip != null && !cache.Current.Borrowed)
                        StagePendingDisposal(cache.Current);
                    cache.Current = new TrackSlot { Clip = gameClip, Borrowed = true };
                }
                CMMConfig.D($"[CMM] Playing vanilla section clip for {contextName} (borrowed, looped).");
            }
            else if (CMMConfig.PreloadCacheEnabled)
            {
                // Clip may be null if files were removed and re-added (evicted from cache).
                // Kick off a preload on-demand rather than spinning until timeout.
                if (cache.Current.Clip == null && !cache.isPreloading)
                {
                    CMMConfig.D($"[CMM] PlayFromCache: clip missing for {contextName}, triggering on-demand preload.");
                    CMM_CoRoutines.StartCoroutine(PreloadContext(contextName));
                }
                float waited = 0f;
                while (cache.Current.Clip == null && waited < 10f)
                {
                    if (transitionId != _transitionId) yield break;
                    yield return new WaitForSeconds(0.25f);
                    waited += 0.25f;
                }
                if (cache.Current.Clip == null)
                {
                    CMMConfig.D($"[CMM] PlayFromCache: no clip for {contextName} after wait.");
                    _activeMusicCoroutine = null;
                    yield break;
                }
            }
            else
            {
                if (cache.Current.Clip == null)
                {
                    CMMConfig.D($"[CMM] On-demand load for {contextName}...");
                    if (cache.isPreloading)
                    {
                        // Proactive preload already in progress; wait for it instead of racing
                        float onDemandWait = 0f;
                        while (cache.isPreloading && cache.Current.Clip == null && onDemandWait < 10f)
                        {
                            if (transitionId != _transitionId) yield break;
                            yield return new WaitForSeconds(0.1f);
                            onDemandWait += 0.1f;
                        }
                    }
                    else
                    {
                        yield return StartCoroutine(PreloadContext(contextName));
                    }
                }
                if (cache.Current.Clip == null)
                {
                    CMMConfig.D($"[CMM] On-demand load failed for {contextName}.");
                    _activeMusicCoroutine = null;
                    // Don't leave a PlayOnce context as currentContext with no audio — it
                    // blocks ContextScanLoop's priority guard forever since nothing beats it.
                    if (currentContext?.Context == contextName)
                    {
                        _pendingContext = null;
                        currentContext = GetContext("Silence");
                    }
                    yield break;
                }
            }

            if (transitionId != _transitionId) yield break;

            // ── Loudness normalization ───────────────────────────────────────
            // Normal tracks: if the sweep hasn't reached this one yet, measure it now
            // (block) so even the first play is normalized. Stingers/events (PlayOnce)
            // must fire instantly — they NEVER block; instead we kick off a background
            // measure so subsequent fires are normalized (this first fire uses whatever
            // gain is already cached, unity on first encounter). Skipped for borrowed
            // vanilla clips (no file).
            if (!useGameClip && !cache.Current.Borrowed)
            {
                if (ctx.PlayOnce)
                {
                    CMM_CoRoutines.StartCoroutine(EnsureLoudnessMeasured(cache.lastPlayedFile));
                }
                else
                {
                    yield return StartCoroutine(EnsureLoudnessMeasured(cache.lastPlayedFile));
                    if (transitionId != _transitionId) yield break;
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
                    CMMConfig.D($"[CMM] Resuming {contextName} at {startPosition:F2}s");
                }
                else
                {
                    cache.resumePosition = 0f;
                    CMMConfig.D($"[CMM] Resume stale for {contextName}, starting fresh.");
                }
            }

            // ── Channel setup ────────────────────────────────────────────────
            // Normally the newcomer reuses the idle InactiveChannel and the playing
            // ActiveChannel becomes the outgoing fade. But a faster context switch can
            // INTERRUPT an in-flight crossfade — TriggerMusic stops the coroutine before
            // its closing Stop() runs — leaving BOTH channels audible: ActiveChannel
            // rising into the half-arrived context, InactiveChannel still falling from the
            // one before it. Blindly reusing InactiveChannel then hard-cuts whatever is
            // louder there (often the original track the listener is anchored on, while
            // the transient gets the graceful fade — backwards). Instead, repurpose the
            // QUIETER of the two channels for the newcomer so the DOMINANT track is the one
            // that crossfades out gracefully; the quiet leftover we evict is barely
            // audible. In the normal case InactiveChannel is idle (not playing, volume 0),
            // so this reduces to the old behaviour exactly.
            var _setupPerf = CMMPerf.Measure(CMMPerf.Phase.TxChannelSetup);
            string activeCtxName = ActiveChannelContext;
            string inactiveCtxName = InactiveChannelContext;
            AudioSource incoming, outgoing;
            string previousActiveContext;
            if (InactiveChannel.isPlaying && InactiveChannel.volume > ActiveChannel.volume)
            {
                // Interrupted crossfade and the Inactive leftover is the louder track:
                // fade IT out and repurpose the quieter Active channel for the newcomer.
                incoming = ActiveChannel;
                outgoing = InactiveChannel;
                previousActiveContext = inactiveCtxName;
                CMMConfig.D($"[CMM] Interrupted crossfade: fading out louder '{inactiveCtxName ?? "None"}' " +
                        $"({outgoing.volume:F2}), evicting quieter '{activeCtxName ?? "None"}' ({incoming.volume:F2}) for {contextName}");
            }
            else
            {
                incoming = InactiveChannel;
                outgoing = ActiveChannel;
                previousActiveContext = activeCtxName;
            }
            float outgoingStartVol = outgoing.volume;
            _setupPerf.Dispose();

            float clipLen = cache.Current.Clip.length;
            float safeStart = clipLen > 0.02f
                ? Mathf.Clamp(startPosition, 0f, clipLen - 0.01f)
                : 0f;

            // Vanilla trader songs are one-shot in the DMS; loop them here so the
            // borrowed clip plays as a continuous bed. This also suppresses the
            // track-end and next-clip scheduling below (there is no "next" file).
            bool loopThis = ctx.Loop || useGameClip;

            // Quick-silence any audible residual still on the channel we're reusing so
            // the incoming priority track fades in over silence instead of replacing a
            // click. This only fires after an interrupted crossfade left the reused
            // channel playing at a real volume; in the normal idle case the channel is
            // not playing (volume already 0), so this is skipped and behaviour is
            // unchanged. The new clip is not assigned until below, so this ramps the
            // leftover track itself. unscaledDeltaTime so it still progresses if paused.
            if (incoming.isPlaying && incoming.volume > ResidualFadeThreshold)
            {
                float residualStart = incoming.volume;
                float residualElapsed = 0f;
                while (residualElapsed < ResidualFadeDuration)
                {
                    if (transitionId != _transitionId) yield break;
                    residualElapsed += Time.unscaledDeltaTime;
                    incoming.volume = Mathf.Lerp(residualStart, 0f,
                        Mathf.Clamp01(residualElapsed / ResidualFadeDuration));
                    yield return null;
                }
            }

            incoming.loop = loopThis;
            incoming.volume = 0f;

            if (safeStart > 0f)
            {
                CMMConfig.D($"[CMM] Seek to {safeStart:F2}s for {contextName} " +
                        $"(clip={clipLen:F1}s, streaming={cache.Current.Reader != null})");

                if (cache.Current.Reader is IFloatPcmSource opusRdr)
                {
                    // Unity does NOT call PCMSetPositionCallback(0) on Play() — it
                    // just starts invoking PCMReaderCallback from wherever the reader
                    // is.  We must pre-seek the reader to the resume frame BEFORE
                    // Play() is called so the very first DSP buffer fill is correct.
                    // Seek by opening from byte 0 + decode-discard (no PrependStream,
                    // no Ogg sequence-number assumptions).  Done on a background thread
                    // to avoid blocking the main thread for long tracks.
                    // Use the reader's OWN sample rate: Opus is always 48k, but native
                    // FLAC (also IFloatPcmSource) preserves its source rate (e.g. 44.1k).
                    int rdrRate = cache.Current.Reader.WaveFormat.SampleRate;
                    long resumeFrame = (long)(safeStart * rdrRate);
                    bool[] seekDone = { false };
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try   { opusRdr.SeekBeforePlay(resumeFrame); }
                        catch (Exception ex)
                        { UnityEngine.Debug.LogWarning($"[CMM] Opus pre-seek failed: {ex.Message}"); }
                        seekDone[0] = true;
                    });
                    float seekWait = 0f;
                    while (!seekDone[0] && seekWait < 30f)
                    {
                        if (transitionId != _transitionId) yield break;
                        yield return null;
                        seekWait += Time.unscaledDeltaTime;
                    }
                    if (!seekDone[0])
                        Log.Warning($"[CMM] Opus pre-seek timed out for {contextName}");

                    // playbackOffset stays 0: the incoming.time = safeStart below
                    // makes Unity's AudioSource.time track the true absolute position,
                    // so the leave-context code needs no offset correction.
                }
            }

            // The clip assign + Play() + time-set below run synchronously on the main
            // thread. The time-set fires the streaming clip's SetPosition callback, but on
            // a resume the reader pre-seeked (SeekBeforePlay) and armed its one-shot
            // intercept, so that callback is swallowed instead of repeating the decode-scan
            // here — what used to be a 70–192ms main-thread hitch for AAC resumes. Timed as
            // one span (no yields inside).
            var _commitPerf = CMMPerf.Measure(CMMPerf.Phase.TxClipCommit);
            incoming.clip = cache.Current.Clip;
            CMMConfig.D($"[CMM] Clip assigned: samples={incoming.clip.samples} ch={incoming.clip.channels} len={incoming.clip.length:F2}s");

            incoming.Play();
            CMMConfig.D($"[CMM] After Play(): isPlaying={incoming.isPlaying} time={incoming.time:F3}s vol={incoming.volume:F2}");

            // Pin the playhead after Play() — even for a fresh start (safeStart == 0).
            // The AudioSource can carry a stale `time` from a previously-interrupted
            // crossfade: a channel that never received Stop() (Stop only runs at the
            // END of CrossFade/FadeToSilence) keeps its playhead live, and assigning a
            // new clip does not reliably zero it on every Unity version. On A21 this
            // made a first-play track begin mid-song at the leaving track's offset.
            // Writing time also flushes Unity's primed DSP buffer and updates
            // AudioSource.time to `safeStart`; the reader itself stays where SeekBeforePlay
            // put it (the redundant reader re-seek is intercepted). incoming.volume is still
            // 0 at this point (the crossfade ramps it up), so any one-frame correction burst
            // is silent.
            incoming.time = safeStart;
            // On a resume the reader was pre-seeked to safeStart and the post-Play seek
            // interceptor armed; disarm it now the explicit time-set has committed the
            // position, so a later loop restart's SetPosition(0) isn't swallowed.
            if (safeStart > 0f && cache.Current.Reader is IFloatPcmSource flushedRdr)
                flushedRdr.ClearInitialSeekIntercept();
            CMMConfig.D($"[CMM] Pinned time: requested={safeStart:F3}s actual={incoming.time:F3}s isPlaying={incoming.isPlaying}");
            _commitPerf.Dispose();

            // incoming becomes the active channel. Derive the flag from the concrete
            // channel identity (not a blind toggle): after the quieter-channel selection
            // above, incoming may be EITHER physical channel, so a toggle could point
            // ActiveChannel at the wrong source.
            _primaryIsActive = (incoming == CMM_Primary_Channel);
            // outgoing (the leaving context) becomes the inactive channel and may still
            // fade for a few seconds — record both so a re-entry can reverse that fade
            // instead of restarting.
            ActiveChannelContext = contextName;
            InactiveChannelContext = previousActiveContext;
            AudioSource watchTarget = incoming;

            // On the menu, a real custom track is now starting — fade out any vanilla
            // title theme still audible underneath (A20 cold-start, where the mod loaded
            // too late for the Start-postfix mute). Skipped for borrowed vanilla beds.
            if (!inGame && !cache.Current.Borrowed)
                FadeOutMenuVanillaIfAudible();

            // The incoming clip is now committed to the active channel, so THIS is the
            // file actually audible. Volume keys off playingFile, not lastPlayedFile:
            // lastPlayedFile advances to the next track the moment its preload runs
            // (≈10s before this one ends, or immediately on a skip), and applying that
            // upcoming track's normalization gain to the still-playing track would
            // swell/duck it before the crossfade. Borrowed vanilla beds have no file.
            cache.playingFile = (useGameClip || cache.Current.Borrowed) ? null : cache.lastPlayedFile;

            // ── Crossfade ────────────────────────────────────────────────────
            float targetVolume = GetContextVolume(contextName);

            if (CMMConfig.CrossfadeEnabled)
            {
                float fadeDuration;
                // -1 = outgoing shares the incoming fade duration (a true crossfade).
                float outFadeDuration = -1f;
                if (ctx.PlayOnce)
                {
                    fadeDuration = 0.25f;                      // stinger enters quickly
                    outFadeDuration = CrossFadeDuration;       // previous track leaves gracefully
                }
                else if (isSameContextSwitchingToNextTrack)
                {
                    float cLen = clipLen > 0f ? clipLen : CrossFadeDuration;
                    fadeDuration = Mathf.Clamp(TrackTransitionFadeDuration, 0.25f, cLen * 0.5f);
                }
                else
                    fadeDuration = ctx.FadeInDuration;

                _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(
                    CrossFade(outgoing, incoming, fadeDuration, _cooldownTarget?.Cooldown ?? 0, outgoingStartVol, 0f, contextName, outFadeDuration));
            }
            else
            {
                outgoing.Stop();
                outgoing.clip = null;
                outgoing.volume = 0f;
                // FIX: Defer destroy by one frame so the audio thread has fully
                // moved off the outgoing clip before we release it.
                CMM_CoRoutines.StartCoroutine(DeferredDestroyPending());
                incoming.volume = targetVolume;
                int cooldown = currentContext.Cooldown;
                if (cooldown > 0)
                {
                    if (_cooldownCoroutine != null) CMM_CoRoutines.StopCoroutine(_cooldownCoroutine);
                    _cooldownCoroutine = CMM_CoRoutines.StartCoroutine(ContextCooldownTimer(contextName, cooldown));
                }
            }

            CMMConfig.D($"[CMM] Playing: {contextName}");

            if (!loopThis)
                _trackEndCoroutine = CMM_CoRoutines.StartCoroutine(TrackEndWatcher(contextName, watchTarget, transitionId));

            if (!loopThis && !ctx.PlayOnce)
            {
                float remaining = (clipLen > 0f ? clipLen : TrackTransitionFadeDuration + TrackTransitionPreloadLead + 1f)
                                  - safeStart;
                _nextCacheCoroutine = CMM_CoRoutines.StartCoroutine(
                    ScheduleNextCacheLoad(contextName, remaining, transitionId));
                // (Next-track loudness is measured inside ScheduleNextCacheLoad, against the
                // exact file it pre-picks — see there.)
            }

            CMMPerf.EndTransition();
            _activeMusicCoroutine = null;
        }

        // FIX: Waits one frame after Stop() before releasing clip/buffer so the
        // audio thread cannot be mid-fill on the old clip when we free it.
        private IEnumerator DeferredDestroyPending()
        {
            yield return new WaitForEndOfFrame();
            DestroyPendingClipAndAudio();
        }

        private static void DestroyPendingClipAndAudio()
        {
            if (_pendingDisposals.Count == 0) return;
            // Destroy() frees the clip's native audio buffer on the main thread — for a
            // non-streaming (full-PCM) clip that can be a multi-MB free and a real hitch.
            // Timed so a frame stutter caused by clip teardown is attributable.
            using (CMMPerf.Measure(CMMPerf.Phase.ClipDestroy))
            {
                while (_pendingDisposals.Count > 0)
                {
                    TrackSlot slot = _pendingDisposals.Dequeue();
                    if (slot.Clip != null) Destroy(slot.Clip);
                    slot.Release();
                }
            }
        }

        // ── Volume ────────────────────────────────────────────────────────────
        // Per-context target volume = global/override volume × the active track's
        // loudness-normalization gain. The gain is keyed to the file currently loaded
        // for the context (cache.lastPlayedFile); unmeasured tracks and borrowed
        // vanilla clips resolve to a neutral 1.0, so this matches GetVolume exactly
        // until a measurement exists.
        public static float GetContextVolume(string contextName)
        {
            Contexts ctx = contextName != null ? GetContext(contextName) : null;
            float vol = CMMConfig.GetVolume(inGame, ctx?.VolumeOverride ?? -1f);
            // playingFile (the audible track), NOT lastPlayedFile (which has already
            // advanced to the preloaded next track during the transition lead window).
            if (contextName != null
                && cacheMap.TryGetValue(contextName, out ContextCache cache)
                && cache.playingFile != null)
                vol *= LoudnessStore.GetTrackGain(cache.playingFile);
            return vol;
        }

        private IEnumerator VolumeSyncLoop()
        {
            float previousVolume = -1f;
            while (true)
            {
                yield return new WaitForSeconds(0.1f);
                // Keep the native mixer's master volume in lock-step with the game's
                // music slider. Sampled here on the main thread (GamePrefs is not safe
                // to read from the mixer thread) and stored in a volatile the mixer reads.
                // Uses the active native context's volume override, mirroring GetContextVolume.
                if (CMMConfig.NativeAudioOutput)
                {
                    Contexts nctx = _nativeActiveContext != null ? GetContext(_nativeActiveContext) : null;
                    CmmMixer.MasterVolume = CMMConfig.GetVolume(inGame, nctx?.VolumeOverride ?? -1f);
                    // Hold a DEEP output cushion ONLY while actually loading a world (_gameIsLoading)
                    // — the window of heavy CPU contention (world-gen) where output latency doesn't
                    // matter — so the music can't hitch no matter how starved the producer gets.
                    // NOT on the idle main menu: the menu isn't CPU-starved, and a deep cushion there
                    // makes decode run seconds ahead of playback, so a manual next-track flush would
                    // teleport the leaving track forward (and without a flush the new track lags behind
                    // the queue). Tight on the menu keeps track changes prompt; reactive growth in the
                    // mixer still covers any surprise starvation (other apps) on menu OR in gameplay.
                    CmmMixer.DeepBuffer = _gameIsLoading;
                }
                // Key off ActiveChannelContext, NOT currentContext: currentContext is
                // reassigned to the incoming context before PlayFromCache swaps the
                // audio channels, so for a short window ActiveChannel still plays the
                // OUTGOING track. Using currentContext's loudness gain there would ramp
                // the still-playing old track to the incoming track's normalized level.
                // ActiveChannelContext always names whatever is actually on ActiveChannel.
                float currentVolume = GetContextVolume(ActiveChannelContext);
                if (Mathf.Approximately(currentVolume, previousVolume)) continue;
                previousVolume = currentVolume;
                if (_crossFadeCoroutine == null && ActiveChannel != null && ActiveChannel.isPlaying)
                    ActiveChannel.volume = currentVolume;
            }
        }

        public static void UpdateVolume()
        {
            if (ActiveChannel == null) return;
            // ActiveChannelContext, not currentContext — see VolumeSyncLoop: the two can
            // briefly disagree across a context switch, and ActiveChannel must be scaled
            // by the gain of the track it is actually playing.
            ActiveChannel.volume = GetContextVolume(ActiveChannelContext);
        }

        // ── Loudness: measure-on-demand (block-before-play) ───────────────────
        // Ensures `filePath` has a current LUFS measurement, measuring it now (on a
        // background thread, awaited) if not. Called from PlayFromCache before a track
        // starts so the first play is normalized. No-op when normalization is off, the
        // file is gone, or it's already current.
        //
        // When a measurement IS needed the caller awaits it, so the track's start is
        // delayed by one (low-priority) decode pass. That delay is intentional — see
        // StartMeasure: we favor a smooth frame rate over an instant start, so an
        // unscanned track waits to begin rather than stuttering the game as it scans.
        private IEnumerator EnsureLoudnessMeasured(string filePath, bool useScratch = false)
        {
            if (!CMMConfig.LoudnessNormalizationEnabled || string.IsNullOrEmpty(filePath)) yield break;

            long size = 0, mtime = 0;
            bool ok = true;
            try
            {
                var fi = new FileInfo(filePath);
                if (!fi.Exists) ok = false;
                else { size = fi.Length; mtime = fi.LastWriteTimeUtc.Ticks; }
            }
            catch { ok = false; }
            if (!ok) yield break;
            if (LoudnessStore.HasCurrent(filePath, size, mtime)) yield break;

            // Look-ahead prefetch (useScratch) borrows the serial _lookAheadScratch so the
            // common per-transition measurement allocates nothing on the LOH; it's lent out
            // only while free, so two concurrent look-aheads never share it. The block-before-
            // play and stinger paths pass useScratch=false and allocate per file (rare).
            // Coroutines are single-threaded, so this check-and-set needs no lock.
            AudioDecoder.MeasureScratch scratch = null;
            bool heldScratch = false;
            if (useScratch && !_lookAheadScratchBusy)
            {
                _lookAheadScratchBusy = true;
                heldScratch = true;
                scratch = _lookAheadScratch;
            }

            MeasureHandle h = StartMeasure(filePath, scratch);
            while (!h.Done) yield return null;
            ClearInFlight(filePath, h);
            if (heldScratch) _lookAheadScratchBusy = false;
            TrackLoudness res = h.Result;

            _loudnessProcessed.Add(filePath);
            if (res.Ok)
            {
                LoudnessStore.Store(filePath, res.Size, mtime, res.Hash, res.Lufs, res.Peak);
                CMMConfig.D($"[CMM] Loudness (block) {Path.GetFileName(filePath)}: {res.Lufs:F1} LUFS");
            }
        }

        // ── Track staging (the unit that ties selection + prewarm + normalization) ─
        // Each context keeps ONE pinned "staged" track (cache.stagedFile): the exact file
        // it will play the next time it starts fresh, and the only file we pre-measure for
        // it. Pinning the selection is what makes "a normalized track is ready when we
        // switch to that context" literally true — without it the prewarm measured one
        // random track while PickNextFile chose a different one, so switch-in still stalled.

        // Pure selection half: ensure cache.stagedFile holds a valid pin, picking a fresh
        // track ONLY when the slot is empty or its file went invalid. Idempotent — a context
        // with a good pin is never re-picked, so repeated maintenance passes (and a brief
        // leave/return) keep the same prepared track. No measurement side effect, so the menu
        // prescan (which measures the pins itself) can call it without double-queuing. Static
        // so BuildMenuPrewarmQueue can use it. Returns the pinned path, or null.
        private static string PinStagedFile(string contextName)
        {
            if (!cacheMap.TryGetValue(contextName, out ContextCache cache)) return null;
            string pin = cache.stagedFile;
            if (pin != null && !_invalidFiles.Contains(pin)) return pin;
            // Steer the fresh pick away from the track this context last played so a pin
            // chosen right after a leave doesn't immediately repeat it (RandomNoRepeat).
            pin = PickNextFile(contextName, avoidFile: cache.lastPlayedFile);
            cache.stagedFile = pin;
            return pin;
        }

        // Selection + measurement: pin the context's next-fresh track and kick a background
        // loudness measure for it (unless it's already measured). The measure runs at
        // BelowNormal priority, so this never hitches the frame. Used by the in-game
        // maintenance pass; the menu prescan measures pins through its own worker pool.
        private void StageNextFile(string contextName)
        {
            string pin = PinStagedFile(contextName);
            if (pin != null && CMMConfig.LoudnessNormalizationEnabled && !LoudnessStore.IsMeasured(pin))
                CMM_CoRoutines.StartCoroutine(EnsureLoudnessMeasured(pin, useScratch: true));
        }

        // Rule 1 maintenance: every context that is neither the active one nor parked on a
        // live resume should have exactly one pinned, measured staged track ready for a
        // stall-free switch-in. Idempotent (only picks/measures an empty or unmeasured pin),
        // so it's cheap to run every tick. Resume-parked resumable contexts are skipped —
        // returning to them resumes the kept track, so no new track is staged (rule 3); a
        // context whose resume later goes stale gets pinned on the next pass.
        private void EnsureStagedPins()
        {
            string active = ActiveChannelContext;
            foreach (var kv in cacheMap)
            {
                string name = kv.Key;
                if (string.Equals(name, active, StringComparison.OrdinalIgnoreCase)) continue;
                Contexts ctx = GetContext(name);
                if (ctx == null || string.IsNullOrEmpty(ctx.Path)) continue;   // no folder → nothing to pin
                ContextCache cache = kv.Value;
                if (ctx.CanBeResumed && cache.resumePosition > 0f && cache.lastPlayedFile != null) continue;
                StageNextFile(name);
            }
        }

        // In-game loudness maintenance, run once per sweep tick: (1) keep every inactive
        // context's staged pin fresh + measured (rule 1 / re-stage-on-leave), then (2) bound
        // the in-memory store to the live working set — each context's playing/last/previous/
        // staged files plus any in-flight measurement — reclaiming everything else. Together
        // these keep the store at ~O(contexts) instead of growing one entry per track heard.
        private static readonly List<string> _loudnessKeepScratch = new List<string>(64);
        private void LoudnessMaintenanceTick()
        {
            if (!CMMConfig.LoudnessNormalizationEnabled) return;

            EnsureStagedPins();

            _loudnessKeepScratch.Clear();
            foreach (var cache in cacheMap.Values)
            {
                if (cache.playingFile != null)      _loudnessKeepScratch.Add(cache.playingFile);
                if (cache.lastPlayedFile != null)   _loudnessKeepScratch.Add(cache.lastPlayedFile);
                if (cache.previousPlayedFile != null) _loudnessKeepScratch.Add(cache.previousPlayedFile);
                if (cache.stagedFile != null)       _loudnessKeepScratch.Add(cache.stagedFile);
            }
            // Protect tracks mid-measure (their path isn't in a cache field yet) so a sweep
            // can't evict a measurement the instant before its track is loaded.
            foreach (var p in _loudnessInFlight.Keys) _loudnessKeepScratch.Add(p);

            LoudnessStore.RetainOnly(_loudnessKeepScratch);
        }

        // ── Loudness: menu prewarm driver ────────────────────────────────────
        // Drives the bounded menu prewarm: while on the menu (not in-game, not loading,
        // not crossfading) it runs MenuPrescanDrain until the per-context prewarm goal is
        // met, then idles. Everything else is measured on demand by EnsureLoudnessMeasured
        // / the look-ahead, so there is no whole-library background sweep.
        private IEnumerator LoudnessSweepLoop()
        {
            yield return new WaitForSeconds(LoudnessSweepStartDelay);

            while (true)
            {
                // Menu prewarm: while on the menu, measure a BOUNDED set — one track per
                // context + a second for the playing one — on a worker pool, then stop
                // (_menuPrescanComplete). This guarantees every context has a ready,
                // normalized track the first time it's switched to. The rest of each
                // context is filled in on demand by the look-ahead (EnsureLoudnessMeasured
                // / ScheduleNextCacheLoad), so nothing is measured beyond what's about to
                // be heard.
                if (CMMConfig.LoudnessNormalizationEnabled
                    && !_menuPrescanComplete
                    && !inGame && !_gameIsLoading && _crossFadeCoroutine == null)
                {
                    yield return StartCoroutine(MenuPrescanDrain());
                }
                else if (inGame)
                {
                    // In-game: no whole-library work — just keep every inactive context's
                    // staged pin fresh + measured (rule 1 / re-stage-on-leave) and bound the
                    // in-memory store to the live working set.
                    LoudnessMaintenanceTick();
                }

                yield return new WaitForSeconds(inGame ? LoudnessSweepInGameInterval : LoudnessSweepInterval);
            }
        }

        // ── Parallel menu prescan ─────────────────────────────────────────────
        // Measures the bounded menu prewarm set up-front while on the menu, keeping up to
        // `width` decodes in flight on a worker pool. All queue/store bookkeeping stays on
        // this (main-thread) coroutine — no locking needed — while only MeasureLoudness runs
        // on the workers, each with its OWN scratch. Runs until the bounded set is measured
        // or a game starts loading, then lets in-flight slots finish (so no work is lost).
        private IEnumerator MenuPrescanDrain()
        {
            int width = PrescanThreadCount();
            EnsurePrescanScratch(width);
            var slots = new PrescanSlot[width];

            // Make sure every context folder has been enumerated so there's a work set
            // to consider.
            if (!_loudnessSnapshotsRequested)
            {
                _loudnessSnapshotsRequested = true;
                _contextsWithFilesDirty = true;
            }

            // Bounded prewarm set: one track per context + a second for the playing
            // context. It never refills with the rest of the library — that's the point.
            if (BuildMenuPrewarmQueue() == 0)
            {
                // Nothing queued: either snapshots aren't ready yet (retry next tick)
                // or every prewarm track is already measured (goal met → stop).
                if (SnapshotsReady()) _menuPrescanComplete = true;
                yield break;
            }

            while (CMMConfig.LoudnessNormalizationEnabled
                   && !inGame && !_gameIsLoading && _crossFadeCoroutine == null)
            {
                // Fill any idle slots from the bounded queue (no refill once drained).
                int busy = 0;
                for (int i = 0; i < width; i++)
                {
                    if (slots[i].Handle != null) { busy++; continue; }
                    if (!TryNextMeasurable(out string path, out long mtime)) continue;

                    slots[i].Path = path;
                    slots[i].Mtime = mtime;
                    slots[i].Handle = StartMeasure(path, _prescanScratch[i]);
                    busy++;
                }

                if (busy == 0) break;   // bounded set drained

                yield return null;

                // Harvest finished slots.
                for (int i = 0; i < width; i++)
                    if (slots[i].Handle != null && slots[i].Handle.Done)
                        ApplyPrescanResult(ref slots[i]);
            }

            // Done or interrupted: drain any still-running slots so their results are saved.
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].Handle == null) continue;
                while (!slots[i].Handle.Done) yield return null;
                ApplyPrescanResult(ref slots[i]);
            }

            // If the bounded set is now fully measured, stop the menu scanner; a game
            // loading mid-drain just leaves the flag clear so we resume on next menu.
            if (!inGame && !_gameIsLoading && BuildMenuPrewarmQueue() == 0 && SnapshotsReady())
                _menuPrescanComplete = true;
            _loudnessQueue.Clear();
        }

        // Builds the BOUNDED menu prewarm set into _loudnessQueue: the staged pin of every
        // context (the exact track its first switch-in will play), so each context has one
        // normalized track ready before it's ever heard. PinStagedFile chooses the pin (a
        // uniform-random pick, so coverage still spreads across sessions) and is idempotent,
        // so an already-pinned context keeps its track. A pin that's already measured needs
        // no work and counts as done; files shared by two contexts are queued once. Returns
        // the number of files enqueued — 0 means every pin is measured (the completion gate).
        private static int BuildMenuPrewarmQueue()
        {
            _loudnessQueue.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Priority contexts (Menu, Ambient) first, so the things heard first warm first.
            foreach (string ctxName in cacheMap.Keys.OrderByDescending(IsPriorityContext))
            {
                if (!cacheMap.TryGetValue(ctxName, out ContextCache cache)) continue;
                if (cache.directorySnapshot == null) continue;   // not enumerated yet → skip

                string pin = PinStagedFile(ctxName);
                if (pin == null) continue;
                if (LoudnessStore.IsMeasured(pin)) continue;     // already warm → goal met for it
                if (_loudnessProcessed.Contains(pin) || _loudnessInFlight.ContainsKey(pin)
                    || _invalidFiles.Contains(pin)) continue;
                if (seen.Add(pin)) LoudnessEnqueue(pin);
            }
            return _loudnessQueue.Count;
        }

        // True once at least one context folder has been enumerated, so an empty
        // prewarm queue can be read as "goal met" rather than "snapshots not ready".
        //
        // This must require EVERY configured context (one with a music folder) to be
        // enumerated, NOT merely one of them. The playing context (Menu) gets its
        // snapshot immediately via PickNextFile, long before the background presence
        // scan enumerates the rest; an "any snapshot" test would therefore report
        // ready while most contexts are still null. Since BuildMenuPrewarmQueue skips
        // null-snapshot contexts (`snap == null → continue`), the prescan would then
        // latch _menuPrescanComplete with only the Menu track measured and never
        // revisit the contexts whose snapshots arrived later — leaving loudness.xml
        // with one track from the Menu context instead of one from every context.
        // Empty-path contexts (Vanilla, Silence) have no folder and are excluded; a
        // missing folder still yields a non-null (empty) snapshot once scanned.
        private static bool SnapshotsReady()
        {
            foreach (var ctx in contexts.Values)
            {
                if (string.IsNullOrEmpty(ctx.Path)) continue;
                // Not in cacheMap → BuildMenuPrewarmQueue can't measure it anyway, so
                // don't block completion on it (that would stall the prescan forever).
                if (!cacheMap.TryGetValue(ctx.Context, out var cache)) continue;
                if (cache.directorySnapshot == null) return false;
            }
            return true;
        }

        // Stores one finished prescan slot's result (main thread) and frees the slot.
        private void ApplyPrescanResult(ref PrescanSlot slot)
        {
            ClearInFlight(slot.Path, slot.Handle);
            _loudnessProcessed.Add(slot.Path);
            TrackLoudness res = slot.Handle.Result;
            if (res.Ok)
            {
                LoudnessStore.Store(slot.Path, res.Size, slot.Mtime, res.Hash, res.Lufs, res.Peak);
                CMMConfig.D($"[CMM] Loudness prescanned {Path.GetFileName(slot.Path)}: {res.Lufs:F1} LUFS (peak {res.Peak:F3})");
                if (ActiveChannelContext != null
                    && cacheMap.TryGetValue(ActiveChannelContext, out ContextCache cc)
                    && string.Equals(cc.playingFile, slot.Path, StringComparison.OrdinalIgnoreCase))
                    UpdateVolume();
            }
            slot = default;
        }

        // Dequeues the next path that actually needs measuring, skipping (and marking
        // processed) ones already resolved, invalid, or current. Mirrors the serial
        // sweep's per-file gate. Returns false when the queue is exhausted.
        private bool TryNextMeasurable(out string path, out long mtime)
        {
            while (_loudnessQueue.Count > 0)
            {
                string p = LoudnessDequeue();
                if (string.IsNullOrEmpty(p) || _loudnessProcessed.Contains(p)) continue;
                if (_invalidFiles.Contains(p)) { _loudnessProcessed.Add(p); continue; }

                long size = 0, mt = 0;
                bool ok = true;
                try
                {
                    var fi = new FileInfo(p);
                    if (!fi.Exists) ok = false;
                    else { size = fi.Length; mt = fi.LastWriteTimeUtc.Ticks; }
                }
                catch { ok = false; }
                if (!ok) { _loudnessProcessed.Add(p); continue; }

                if (LoudnessStore.HasCurrent(p, size, mt)) { _loudnessProcessed.Add(p); continue; }

                path = p; mtime = mt;
                return true;
            }
            path = null; mtime = 0;
            return false;
        }

        // Worker count for the prescan: auto = cores − 2, clamped 1..8.
        private static int PrescanThreadCount()
            => Mathf.Clamp(Environment.ProcessorCount - 2, 1, 8);

        private static void EnsurePrescanScratch(int width)
        {
            if (_prescanScratch != null && _prescanScratch.Length >= width) return;
            _prescanScratch = new AudioDecoder.MeasureScratch[width];
            for (int i = 0; i < width; i++)
                _prescanScratch[i] = new AudioDecoder.MeasureScratch();
        }

        // ── Loudness: background measurement worker ───────────────────────────
        // Runs one MeasureLoudness pass (a full decode + R128 analysis) on a
        // dedicated BELOW-NORMAL priority thread, signalling completion via a flag
        // the caller polls with `yield return null`. ThreadPool workers run at Normal
        // priority and compete equally with Unity's render thread, so a CPU-heavy
        // decode (Opus/Concentus especially) caused visible in-game frame stutter.
        // Lowering the priority lets the OS scheduler keep the game smooth and just
        // stretches measurement out a little under load.
        //
        // BelowNormal is used for ALL callers — the menu prewarm AND the block-before-play
        // path (EnsureLoudnessMeasured), which awaits this before an unscanned track starts.
        // That is a deliberate tradeoff: a smooth frame rate is preferred over a fast track
        // start, so we accept that an unscanned track takes a little longer to begin rather
        // than risk hitching the game while it's measured. (Do NOT bump the block path to
        // Normal/AboveNormal to shave the start delay — that reintroduces the stutter this is
        // here to avoid.)
        //
        // `scratch`, when supplied, gives MeasureLoudness a reused buffer so it allocates
        // nothing on the LOH — the fix for the in-game GC stutter. Two callers pass one: the
        // menu prewarm (per-worker _prescanScratch) and the look-ahead prefetch
        // (_lookAheadScratch); each owns a distinct buffer and serializes its own use, so no
        // buffer is ever touched by two decodes at once. The block-before-play and stinger
        // paths pass null and allocate per file (rare).
        //
        // De-duplicated by path: a second request for a file already being measured joins
        // the existing handle instead of spawning a competing decode — INCLUDING a handle
        // that has just finished (Done) but whose consumer hasn't run ClearInFlight yet.
        // (Joining only when !Done left a one-frame window where a Done-but-not-yet-removed
        // handle was skipped, spawning a duplicate decode and then having the consumer's
        // blind Remove delete the *newer* handle — the exact double-decode this map exists
        // to prevent.) Removal is the consumer's job via ClearInFlight, which deletes the
        // entry only if it is still this same handle.
        private static MeasureHandle StartMeasure(string path, AudioDecoder.MeasureScratch scratch)
        {
            if (_loudnessInFlight.TryGetValue(path, out MeasureHandle existing))
                return existing;

            var h = new MeasureHandle();
            _loudnessInFlight[path] = h;
            var t = new Thread(() =>
            {
                try { h.Result = AudioDecoder.MeasureLoudness(path, scratch); } catch { }
                h.Done = true;
            })
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.BelowNormal,
                Name = "CMM-Loudness",
            };
            t.Start();
            return h;
        }

        // Removes the in-flight entry for `path` only if it is still `h`. A consumer that
        // JOINED an existing handle (StartMeasure dedup) — or whose own handle was already
        // replaced — must not delete a newer measurement started for the same path. Called
        // on the main-thread coroutine, like every other _loudnessInFlight access.
        private static void ClearInFlight(string path, MeasureHandle h)
        {
            if (_loudnessInFlight.TryGetValue(path, out MeasureHandle cur) && cur == h)
                _loudnessInFlight.Remove(path);
        }

        // Reused by the look-ahead prefetch (ScheduleNextCacheLoad) so the common
        // per-transition next-track measurement doesn't allocate a whole-file byte[] on the
        // LOH every track change. It is lent out only when free (_lookAheadScratchBusy), so it
        // is never touched by two decodes at once. A look-ahead that finds it busy falls back
        // to per-file allocation for that one call.
        private static readonly AudioDecoder.MeasureScratch _lookAheadScratch =
            new AudioDecoder.MeasureScratch();
        private static bool _lookAheadScratchBusy;

        private sealed class MeasureHandle
        {
            public volatile bool Done;
            public TrackLoudness Result;
        }

        // Contexts whose first track the player is most likely to hear first, so the
        // pre-warm covers them before reactive contexts (combat/POI/trader/etc.): the
        // Menu music and the baseline Ambient exploration tracks.
        private static bool IsPriorityContext(string name)
            => name != null
               && (name.Equals("Menu", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith("Ambient", StringComparison.OrdinalIgnoreCase));

        // ── Loudness: debounced cache save ────────────────────────────────────
        private IEnumerator LoudnessSaveLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(LoudnessSaveDebounce);
                if (LoudnessStore.Dirty) LoudnessStore.Save();
            }
        }

        // ── Crossfade ─────────────────────────────────────────────────────────
        private IEnumerator CrossFade(AudioSource outgoing, AudioSource incoming,
            float duration, int cooldownSeconds, float outgoingStartVol, float incomingStartVol = 0f,
            string incomingContext = null, float outDuration = -1f)
        {
            // The incoming side fades to its context volume INCLUDING the loudness gain
            // of its current track; the outgoing side always fades to 0 (its gain is
            // irrelevant). incomingContext == null falls back to a neutral global volume.
            float targetVolume = incomingContext != null
                ? GetContextVolume(incomingContext)
                : CMMConfig.GetVolume(inGame);
            // The outgoing track may fade over a different span than the incoming one
            // (e.g. a PlayOnce stinger enters fast but lets the previous track leave
            // gracefully). When unset, both share the same duration.
            if (outDuration < 0f) outDuration = duration;
            float total = Mathf.Max(duration, outDuration);
            float elapsed = 0f;
            while (elapsed < total)
            {
                elapsed += Time.deltaTime;
                float tIn  = duration    > 0f ? Mathf.Clamp01(elapsed / duration)    : 1f;
                float tOut = outDuration > 0f ? Mathf.Clamp01(elapsed / outDuration) : 1f;
                incoming.volume = Mathf.Lerp(incomingStartVol, targetVolume, tIn);
                outgoing.volume = Mathf.Lerp(outgoingStartVol, 0f, tOut);
                yield return null;
            }

            incoming.volume = targetVolume;
            outgoing.Stop();
            outgoing.clip = null;
            outgoing.volume = 0f;
            CMMConfig.D($"[CMM] CrossFade done: vol={incoming.volume:F3} target={targetVolume:F3} isPlaying={incoming.isPlaying}");

            // FIX: Defer destroy past the end of the crossfade coroutine so the
            // audio thread has fully moved off the outgoing clip. One frame is
            // sufficient; the crossfade already takes several seconds.
            yield return new WaitForEndOfFrame();
            DestroyPendingClipAndAudio();

            if (cooldownSeconds > 0)
            {
                if (_cooldownCoroutine != null) CMM_CoRoutines.StopCoroutine(_cooldownCoroutine);
                // Associate the cooldown with the LEAVING context (_cooldownTarget),
                // captured before currentContext was reassigned to the incoming one.
                // currentContext here is already the incoming context, so using it
                // would put the just-started context on cooldown instead.
                _cooldownCoroutine = CMM_CoRoutines.StartCoroutine(
                    ContextCooldownTimer(_cooldownTarget?.Context ?? "None", cooldownSeconds));
            }
            _crossFadeCoroutine = null;
        }

        // fadeSeconds < 0 uses the normal in-game CrossFadeDuration; pass a longer value
        // (e.g. MenuTransitionFade) for a graceful, deliberate fade such as entering the game.
        public static void FadeToSilence(float fadeSeconds = -1f)
        {
            float fade = fadeSeconds >= 0f ? fadeSeconds : CrossFadeDuration;
            CMMConfig.D($"[CMM] Fading to silence ({fade:F1}s).");
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }
            if (_nextCacheCoroutine != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine); _nextCacheCoroutine = null; }
            if (_trackEndCoroutine != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine); _trackEndCoroutine = null; }
            if (_crossFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine); _crossFadeCoroutine = null; }
            // If the native mixer is the current output, fade it out too (covers
            // native→silence and, via ActivateVanilla→FadeToSilence, native→vanilla).
            StopNativeIfActive(fade);
            _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeOutCoroutine(fade));
        }

        private IEnumerator FadeOutCoroutine(float fadeSeconds = -1f)
        {
            float CrossFadeDuration = fadeSeconds >= 0f ? fadeSeconds : ContextMusicManager.CrossFadeDuration;
            AudioSource outgoing = ActiveChannel;
            // If a crossfade was interrupted, the inactive channel (previous outgoing)
            // never received its Stop() call and may still be audible at high volume.
            AudioSource orphan = InactiveChannel;
            float startVol = outgoing.volume;
            float orphanStartVol = (orphan != null && orphan.isPlaying) ? orphan.volume : 0f;
            float elapsed = 0f;
            while (elapsed < CrossFadeDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / CrossFadeDuration);
                outgoing.volume = Mathf.Lerp(startVol, 0f, t);
                if (orphan != null && orphan.isPlaying)
                    orphan.volume = Mathf.Lerp(orphanStartVol, 0f, t);
                yield return null;
            }
            outgoing.Stop();
            outgoing.clip = null;
            outgoing.volume = 0f;
            if (orphan != null)
            {
                orphan.Stop();
                orphan.clip = null;
                orphan.volume = 0f;
            }
            yield return new WaitForEndOfFrame();
            DestroyPendingClipAndAudio();
            _crossFadeCoroutine = null;
        }

        // Menu → game graceful fade (entering a world). The menu music plays through the native
        // mixer behind a DEEP load cushion, so a producer-side gain ramp would be buried — the
        // fade must run CONSUMER-side (output gain in the audio callback) to be heard. The mixer
        // voices also fade producer-side so they retire, but the device stays OPEN so the Spawn
        // stinger reuses it (its Play snaps the output gain back to full). Any audible AudioSource
        // (vanilla-borrow menu) is faded too. Stops the menu control coroutines first so nothing
        // re-triggers mid-fade.
        internal static void FadeMenuOutputForGameStart(float seconds)
        {
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }
            if (_nextCacheCoroutine  != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine);  _nextCacheCoroutine  = null; }
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
            _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeOutCoroutine(seconds));
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
        // from PlayFromCache when a real custom menu track is actually starting, so we
        // never silence vanilla unless there's a custom track to replace it.
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

        // ── Track-end watcher ─────────────────────────────────────────────────
        private IEnumerator TrackEndWatcher(string contextName, AudioSource watchTarget, int transitionId)
        {
            // FIX: Record startTime after the initial sleep so the minimum-play
            // guard is measured from when we actually start watching, not from
            // when the coroutine was spawned.
            yield return new WaitForSeconds(1f);
            float startTime = Time.realtimeSinceStartup;
            float minimumPlaySeconds = 2f;

            if (transitionId != _transitionId) { _trackEndCoroutine = null; yield break; }

            float notPlayingDuration = 0f;
            while (true)
            {
                yield return new WaitForSeconds(TrackEndPollInterval);
                if (transitionId != _transitionId)
                {
                    CMMConfig.D($"[CMM] TrackEndWatcher: transition superseded for {contextName}, exiting.");
                    _trackEndCoroutine = null;
                    yield break;
                }
                if (currentContext.Context != contextName)
                {
                    CMMConfig.D($"[CMM] TrackEndWatcher: context changed mid-watch for {contextName}, bailing.");
                    _trackEndCoroutine = null;
                    yield break;
                }
                if (watchTarget == null) { _trackEndCoroutine = null; yield break; }
                if (watchTarget.isPlaying) { notPlayingDuration = 0f; continue; }
                if (Time.realtimeSinceStartup - startTime < minimumPlaySeconds) continue;
                notPlayingDuration += TrackEndPollInterval;
                if (notPlayingDuration >= 0.5f) break;
            }

            if (transitionId != _transitionId || currentContext.Context != contextName)
            {
                CMMConfig.D($"[CMM] TrackEndWatcher: context changed away from {contextName}, ignoring.");
                _trackEndCoroutine = null;
                yield break;
            }

            CMMConfig.D($"[CMM] Track ended: {contextName}");
            _trackEndCoroutine = null;

            if (GetContext(contextName).PlayOnce)
            {
                if (playerIsDead)
                {
                    CMMConfig.D($"[CMM] PlayOnce {contextName} finished but player is dead — fading to silence.");
                    _pendingContext = null;
                    currentContext = GetContext("Silence");
                    FadeToSilence();
                    yield break;
                }
                string returnTo = _contextStack
                    .Where(c => c.ContextName != contextName)
                    .Select(c => ResolvePlayableContext(c.ContextName))
                    .FirstOrDefault(c => c != null) ?? "Ambient";
                CMMConfig.D($"[CMM] PlayOnce finished, returning to: {returnTo}");
                SendContextUpdate(returnTo, 0);
                yield break;
            }

            if (_scheduleHandledTransition)
            {
                CMMConfig.D($"[CMM] TrackEndWatcher: ScheduleNextCacheLoad already handled transition for {contextName}, skipping safety net.");
                _scheduleHandledTransition = false;
                yield break;
            }

            if (currentContext.Context == contextName && !ActiveChannel.isPlaying)
            {
                CMMConfig.D($"[CMM] TrackEndWatcher safety net firing for {contextName}");
                ContextCache cache = cacheMap[contextName];
                cache.resumePosition = 0f;
                cache.lastLeftTime = -1f;

                if (cache.Current.Clip != null)
                {
                    Destroy(cache.Current.Clip);
                    cache.Current.Clip = null;
                    yield return new WaitForEndOfFrame();
                    cache.Current.Release();
                }

                yield return StartCoroutine(PreloadContext(contextName));
                if (cache.Current.Clip == null)
                {
                    CMMConfig.D($"[CMM] Safety net load failed for {contextName}. Giving up.");
                    yield break;
                }

                int newId = ++_transitionId;
                _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(PlayFromCache(contextName, true, newId));
            }
        }

        // ── ScheduleNextCacheLoad ─────────────────────────────────────────────
        // FIX: Pick the next file once here (for the size estimate) and pass
        // it directly to LoadNextClip so the random/sequential selector does not
        // advance twice and land on different files.
        private IEnumerator ScheduleNextCacheLoad(string contextName, float clipLength, int transitionId)
        {
            ContextCache cache = cacheMap[contextName];

            string avoidForPick = GlobalTrackOrder == TrackOrderMode.RandomNoRepeat
                                   ? cache.lastPlayedFile : null;
            // Within-context advance: uniform-random pick (see PickNextFile). The loudness
            // measurement of this file is deferred to the preload lead window below (just
            // before LoadNextClip), NOT fired here at track START. Measuring here meant every
            // brief visit to a resume-capable context — swap in, resume the same (already
            // measured) track, swap straight back out — speculatively measured a fresh RANDOM
            // next track that was never heard; and since auto measurements are retained for
            // the session, each such visit permanently grew the in-memory store by one track.
            // Past the waitBeforeLoad guard the measure only runs once we're committed to
            // actually loading this next track, so a context left before its lead window adds
            // nothing — a resume context keeps just its one prewarmed track (plus the genuine
            // next track once we stay long enough to hear it).
            string prePickedFile = PickNextFile(contextName, avoidFile: avoidForPick);

            // File-size heuristic for decode-lead estimate.
            // For the streaming path this is almost always negligible (just a
            // file-open), so the estimate acts as a safety margin.
            float estimatedOpenSeconds = 0f;
            if (prePickedFile != null)
            {
                try
                {
                    long fileBytes = new FileInfo(prePickedFile).Length;
                    estimatedOpenSeconds = fileBytes / (1024f * 1024f) * 0.0002f; // ~0.2ms/MB
                }
                catch { }
            }

            float effectiveFade = Mathf.Clamp(TrackTransitionFadeDuration, 0.25f, clipLength * 0.5f);
            float safeLeadTime = TrackTransitionPreloadLead + estimatedOpenSeconds * 3f;
            float waitBeforeLoad = Mathf.Max(0f, clipLength - effectiveFade - safeLeadTime);
            float waitBeforePlay = Mathf.Max(0f, clipLength - effectiveFade);

            CMMConfig.D($"[CMM] {contextName}: clip={clipLength:F1}s fade={effectiveFade:F1}s " +
                    $"openEst={estimatedOpenSeconds * 1000f:F1}ms loadIn={waitBeforeLoad:F1}s playIn={waitBeforePlay:F1}s");

            yield return new WaitForSeconds(waitBeforeLoad);
            if (transitionId != _transitionId || currentContext.Context != contextName) yield break;

            // Committed to this next track now (context still active, transition current):
            // measure it here, in the lead window, so PlayFromCache's block-before-play is
            // already satisfied and the track starts without a measurement stall — while a
            // context we leave before this point never measures a track it won't play.
            if (prePickedFile != null)
                CMM_CoRoutines.StartCoroutine(EnsureLoudnessMeasured(prePickedFile, useScratch: true));

            if (!cache.nextClipLoading && cache.Next.Clip == null)
            {
                cache.nextClipLoading = true;
                CMMConfig.D($"[CMM] Pre-loading next clip for {contextName}");
                yield return StartCoroutine(LoadNextClip(contextName, prePickedFile));
                cache.nextClipLoading = false;
            }

            float remaining = waitBeforePlay - waitBeforeLoad;
            if (remaining > 0f)
                yield return new WaitForSeconds(remaining);

            if (transitionId != _transitionId || currentContext.Context != contextName) yield break;

            if (cache.Next.Clip == null)
            {
                CMMConfig.D($"[CMM] Next clip not ready in time for {contextName}, loading inline.");
                if (cache.Current.Clip != null)
                {
                    StagePendingDisposal(cache.Current);
                    cache.Current = default;
                }
                yield return StartCoroutine(PreloadContext(contextName));
            }
            else
            {
                // Promote next → current; stage current for deferred disposal.
                if (cache.Current.Clip != null)
                    StagePendingDisposal(cache.Current);
                cache.Current = cache.Next;
                cache.Next = default;
            }

            cache.resumePosition = 0f;
            cache.lastLeftTime = -1f;

            if (cache.Current.Clip == null)
            {
                CMMConfig.D($"[CMM] No clip available for {contextName} transition. Giving up.");
                yield break;
            }

            _scheduleHandledTransition = true;
            CMMConfig.D($"[CMM] Starting crossfade to next track in {contextName}");
            int newId = ++_transitionId;
            _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(PlayFromCache(contextName, true, newId));
            _nextCacheCoroutine = null;
        }

        // ── LoadNextClip ──────────────────────────────────────────────────────
        // FIX: Accept a pre-picked file path from ScheduleNextCacheLoad so the
        // track selector doesn't advance twice.
        private IEnumerator LoadNextClip(string contextName, string prePickedFile = null)
        {
            ContextCache cache = cacheMap[contextName];

            // A single pick can be unloadable for reasons that DON'T mean "give up": the
            // file was moved out from under us, or it's a freshly dropped-in track still
            // being written/locked (a partial read decodes to garbage). Bailing after one
            // failure strands a skip/advance on Vanilla even though other valid files
            // remain — so mark/prune the bad pick (mirroring the Preload + HotSwap paths)
            // and try another candidate, up to a small bound. Only the FIRST attempt
            // honors the caller's pre-picked file; once it fails we must pick fresh.
            const int maxAttempts = 8;
            string failedFile = null;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                string avoid = GlobalTrackOrder == TrackOrderMode.RandomNoRepeat
                                  ? cache.lastPlayedFile : null;
                string filePath = (attempt == 0 && prePickedFile != null)
                                      ? prePickedFile
                                      : PickNextFile(contextName, avoidFile: failedFile ?? avoid);

                if (filePath == null)
                { CMMConfig.D($"[CMM] LoadNextClip: no files for {contextName}"); yield break; }

                string clipName = Path.GetFileNameWithoutExtension(filePath);

                // Menu (and PreloadCacheEnabled) get a fully-decoded clip on EVERY track, not
                // just the first — native non-streaming for Menu so the audio thread never
                // runs managed decode code and the menu can't stutter while a world loads.
                // This mirrors the initial load in PreloadContextCore so the "menu is fully
                // buffered" guarantee holds across the whole menu sequence: without it, only
                // the opening menu track was native and track #2+ streamed (decoding on the
                // audio thread), which hitched during the load screen.
                if (CMMConfig.PreloadCacheEnabled || contextName == "Menu")
                {
                    byte[] rawBytes = null;
                    bool ioOk = false;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { rawBytes = File.ReadAllBytes(filePath); } catch { }
                        ioOk = true;
                    });
                    while (!ioOk) yield return null;

                    DecodedAudio decoded = null;
                    if (rawBytes != null)
                    {
                        bool decodeOk = false;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            try { decoded = AudioDecoder.DecodeFileFromBytes(rawBytes, filePath); } catch { }
                            decodeOk = true;
                        });
                        while (!decodeOk) yield return null;
                    }

                    if (decoded == null)
                    {
                        // Distinguish moved-out (transient) from genuinely corrupt, exactly
                        // like PreloadContextCore, then prune/refresh so PickNextFile stops
                        // re-selecting this same file on the retry.
                        MarkInvalidIfCorrupt(filePath, $"LoadNextClip: full decode failed for {contextName}");
                        if (!File.Exists(filePath)) PruneMissingFromSnapshot(contextName, filePath);
                        else                        InvalidateContextFileCache(contextName);
                        failedFile = filePath;
                        continue;
                    }

                    bool isMenu = contextName == "Menu";
                    AudioClip fullClip = isMenu
                        ? AudioDecoder.CreateNonStreamingClip(decoded, clipName)
                        : AudioDecoder.CreateStreamingClip(decoded, clipName);
                    if (fullClip == null) { failedFile = filePath; continue; }

                    if (cache.Next.Clip != null) { Destroy(cache.Next.Clip); cache.Next.Release(); }
                    cache.Next.Clip = fullClip;
                    cache.Next.Reader = null;
                    // Menu: native buffer owns the samples now; drop the managed copy. Other
                    // full-decode contexts keep it — CreateStreamingClip's callback reads from it.
                    if (isMenu) { decoded.Samples = null; decoded = null; }
                    cache.Next.Decoded = decoded;
                    cache.previousPlayedFile = cache.lastPlayedFile;
                    cache.lastPlayedFile = filePath;
                    CMMConfig.D($"[CMM] Next clip ready (full decode{(isMenu ? "/native" : "")}) for {contextName}: {Path.GetFileName(filePath)}");
                    yield break;
                }

                // ── Streaming path (default, in-game contexts) ─────────────────────
                {
                    WaveStream reader = null;
                    yield return StartCoroutine(OpenReaderAsync(filePath, r => reader = r));

                    if (reader == null)
                    {
                        // OpenReaderAsync already blacklisted (corrupt) or skipped (transient);
                        // drop a genuinely-missing pick from the snapshot so the retry doesn't
                        // re-select it, then try another candidate.
                        if (!File.Exists(filePath)) PruneMissingFromSnapshot(contextName, filePath);
                        else                        InvalidateContextFileCache(contextName);
                        failedFile = filePath;
                        continue;
                    }

                    AudioClip clip = AudioDecoder.CreateStreamingClipFromReader(reader, clipName);

                    if (clip == null) { reader?.Dispose(); failedFile = filePath; continue; }

                    if (cache.Next.Clip != null) { Destroy(cache.Next.Clip); cache.Next.Release(); }
                    cache.Next.Clip   = clip;
                    cache.Next.Reader = reader;
                    cache.previousPlayedFile = cache.lastPlayedFile;
                    cache.lastPlayedFile = filePath;
                    CMMConfig.D($"[CMM] Next clip ready (streaming) for {contextName}: {Path.GetFileName(filePath)}");
                    yield break;
                }
            }

            CMMConfig.D($"[CMM] LoadNextClip: no decodable file for {contextName} after {maxAttempts} attempts.");
        }

        // ── Subfolder recursion + subtree claiming ────────────────────────────
        // Shared by every context-folder enumeration site (PickNextFile, the
        // presence scans, and FileWatchLoop). Two independent concerns:
        //
        //   • REACH (IncludeSubfolders): a flat context lists only files directly in
        //     its folder; a subfolders="true" context lists its whole subtree.
        //   • OWNERSHIP (claiming): when a recursive context's subtree contains a
        //     MORE-SPECIFIC child context's folder, those files belong to the child,
        //     not the parent. Each track maps to exactly one context — the deepest
        //     one whose Path is a prefix — so a recursive parent never double-counts
        //     a child's library. Mirrors the resolver's "most-specific wins" rule.
        private static readonly string[] _noClaimPrefixes = new string[0];

        // One context's pre-resolved scan parameters, handed to the background
        // PresenceScanLoop thread so it never touches the shared `contexts` dict.
        private struct PresenceScanItem
        {
            public string Name;
            public string Dir;
            public SearchOption Option;
            public string[] Claimed;
        }

        private static SearchOption SearchOptionFor(Contexts ctx) =>
            ctx != null && ctx.IncludeSubfolders
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

        // Absolute, separator-terminated folder prefixes of every OTHER context whose
        // folder is nested strictly beneath this context's folder. A file under any of
        // these is claimed by the deeper context and must be dropped from this one.
        // Empty (the cheap, common case) unless this context recurses AND has at least
        // one child context inside it — so non-recursive contexts pay nothing.
        private static string[] GetClaimedSubtreePrefixes(string contextName)
        {
            Contexts self = GetContext(contextName);
            if (self == null || string.IsNullOrEmpty(self.Path) || !self.IncludeSubfolders)
                return _noClaimPrefixes;

            string selfDir = NormalizedContextDir(self.Path);
            if (selfDir == null) return _noClaimPrefixes;
            string selfPrefix = selfDir + Path.DirectorySeparatorChar;

            List<string> prefixes = null;
            foreach (Contexts other in contexts.Values)
            {
                if (other == self || string.IsNullOrEmpty(other.Path)) continue;
                if (string.Equals(other.Context, self.Context, StringComparison.OrdinalIgnoreCase)) continue;

                string otherDir = NormalizedContextDir(other.Path);
                if (otherDir == null) continue;
                // Strictly nested beneath self (a context at the SAME folder is a
                // pre-existing ambiguity claiming doesn't touch).
                if (otherDir.Length > selfDir.Length &&
                    otherDir.StartsWith(selfPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    (prefixes ?? (prefixes = new List<string>()))
                        .Add(otherDir + Path.DirectorySeparatorChar);
                }
            }
            return prefixes == null ? _noClaimPrefixes : prefixes.ToArray();
        }

        // True if the file lives under a deeper context's folder (so this context must
        // not enumerate it). Cheap no-op when there are no claim prefixes.
        private static bool IsClaimedByDeeperContext(string file, string[] claimedPrefixes)
        {
            if (claimedPrefixes.Length == 0) return false;
            string full;
            try { full = Path.GetFullPath(file); }
            catch { return false; }
            foreach (string p in claimedPrefixes)
                if (full.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Folder-change signature for the FileWatchLoop mtime fast-path. A flat
        // context is just the folder's own write time. A recursive context must also
        // fold in every subdirectory's write time: adding or removing a file bumps
        // its PARENT directory's mtime but not any ancestor's, so the root mtime
        // alone would miss a change made two levels down. Walking directories (not
        // files) keeps this far cheaper than a full recursive file enumeration.
        private static DateTime ContextWriteSignatureUtc(string dir, bool includeSubfolders)
        {
            if (!Directory.Exists(dir)) return DateTime.MinValue;
            DateTime t = Directory.GetLastWriteTimeUtc(dir);
            if (includeSubfolders)
            {
                try
                {
                    foreach (string sub in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
                    {
                        DateTime st = Directory.GetLastWriteTimeUtc(sub);
                        if (st > t) t = st;
                    }
                }
                catch { }
            }
            return t;
        }

        // Resolve a context Path (relative to MusicDirectory, or absolute) to a full,
        // non-separator-terminated directory path for prefix comparisons. Null on error.
        private static string NormalizedContextDir(string ctxPath)
        {
            try
            {
                return Path.GetFullPath(Path.Combine(MusicDirectory, ctxPath))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return null; }
        }

        // Enumerate a context's eligible files: applies reach (flat vs recursive) and
        // subtree claiming in one place so every caller stays consistent. Returns full
        // paths of supported, non-blacklisted, non-claimed files.
        private static string[] EnumerateContextFiles(Contexts ctx, string dir)
        {
            string[] claimed = GetClaimedSubtreePrefixes(ctx.Context);
            return Directory.GetFiles(dir, "*", SearchOptionFor(ctx))
                .Where(f => AudioDecoder.IsSupported(f)
                            && !_invalidFiles.Contains(f)
                            && !IsClaimedByDeeperContext(f, claimed))
                .ToArray();
        }

        // ── PickNextFile ──────────────────────────────────────────────────────
        // UNIFORM random selection over the whole eligible folder (RandomNoRepeat also
        // excludes avoidFile). There is deliberately NO bias toward already-measured
        // tracks: under on-demand scanning only a tiny handful of a context's files are
        // ever measured (the menu prewarm measures one per context), so biasing toward
        // them collapsed every pick onto that one track — the "same track out of 3000"
        // bug. Normalization is handled independently of selection: the about-to-play
        // track is measured just-in-time (block-before-play in PlayFromCache), and the
        // next track is pre-measured during the preload lead window (ScheduleNextCacheLoad).
        // ── Native-output debug driver (Phase 2 prototype) ────────────────────
        // Drives CmmMixer from the `cmm native*` console commands so the new output
        // path can be heard in-game without disturbing the AudioSource path. Plays one
        // looped track per context; `next` crossfades to another pick; `stop` fades out.
        // Real context-driven routing (through PlayFromCache) is Phase 3.
        private static string _nativeDebugContext;
        private static string _nativeDebugLastFile;

        public static string NativeDebugPlay(string contextName)
        {
            if (!CMMConfig.NativeAudioOutput)
                return "NativeAudioOutput is off — set <NativeAudioOutput>true</NativeAudioOutput> in Config/settings.xml and reload.";
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
                    WaveStream reader = AudioDecoder.OpenStreamingReader(file);
                    if (reader == null) { Log.Warning($"[CMM] native: failed to open {file}"); return; }
                    long id = CmmMixer.GetOrCreate().Play(reader, fadeSeconds, loop);
                    Log.Out($"[CMM] native: {(id != 0 ? "playing" : "rejected")} {System.IO.Path.GetFileName(file)}");
                }
                catch (Exception ex) { Log.Error($"[CMM] native play error: {ex.Message}"); }
            });
            return true;
        }

        // ── Native playback controller (Phase 3c) ─────────────────────────────
        // When CMMConfig.NativeAudioOutput is on, custom-file contexts play through
        // CmmMixer instead of AudioSource. Context RESOLUTION (what should play) is
        // unchanged — only the output path differs. Vanilla/DMS-borrow + Silence stay
        // on the AudioSource path (decision A: hybrid), so the two outputs never both
        // produce the current context: StartContextCoroutine / FadeToSilence stop the
        // mixer whenever an AudioSource (or silent) context takes over, and the mixer
        // crossfades itself for native→native switches.
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
                Priority = System.Threading.ThreadPriority.AboveNormal,
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

                    WaveStream reader = null;
                    try { reader = AudioDecoder.OpenStreamingReader(req.File); }
                    catch { }
                    if (reader == null)
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
                        // is stale — drop it instead of resurrecting dead audio.
                        if (req.TransitionId != _transitionId
                            || !string.Equals(_nativeActiveContext, req.Context, StringComparison.OrdinalIgnoreCase))
                        {
                            reader.Dispose();
                            continue;
                        }

                        long startFrame = 0;
                        if (req.StartSeconds > 0f)
                            startFrame = (long)(req.StartSeconds * reader.WaveFormat.SampleRate);

                        long voiceId = CmmMixer.GetOrCreate().Play(reader, req.Fade, req.Loop, startFrame, req.TrackGain, req.DuckOthersTo, req.SnapTight);
                        if (voiceId == 0)
                        {
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

        // The single branch point for starting a context's playback: native mixer when
        // eligible, else the AudioSource PlayFromCache. Used by TriggerMusic, the skip
        // path, and the native auto-advance. The AudioSource branch owns _activeMusicCoroutine
        // (so TriggerMusic/FadeToSilence StopCoroutine cancels it); the native branch owns no
        // coroutine — it's torn down by _transitionId bumps + StopNativeIfActive instead.
        internal static void StartContextCoroutine(string contextName, bool isSameContextSwitchingToNextTrack, int newId)
        {
            bool nat = CMMConfig.NativeAudioOutput && ContextHasFiles(contextName);
            if (nat)
            {
                FadeOutAudioSourcesIfActive(CrossFadeDuration); // native taking over from an AudioSource (vanilla / DMS-borrow) context
                Instance.StartNativeContext(contextName, isSameContextSwitchingToNextTrack, newId, null);
            }
            else
            {
                StopNativeIfActive(CrossFadeDuration);          // an AudioSource/silent context is taking over
                _activeMusicCoroutine = CMM_CoRoutines.StartCoroutine(
                    Instance.PlayFromCache(contextName, isSameContextSwitchingToNextTrack, newId));
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
        // (which uses no AudioSource and no _activeMusicCoroutine) always looked "dead", so a
        // PlayOnce stinger was treated as strandable and got interrupted ~0.3s in.
        internal static bool NativeOutputBusy()
        {
            if (_nativeActiveContext == null) return false;
            long vid = Interlocked.Read(ref _nativeVoiceId);
            if (vid == 0) return true;   // dispatched; worker hasn't committed the voice yet
            return CmmMixer.Instance != null && CmmMixer.Instance.RemainingSeconds(vid) != -1;
        }

        // Mirror of StopNativeIfActive for the opposite direction: the native mixer is
        // taking over the current context, so fade out any audible AudioSource channel
        // (a leftover vanilla bed or a DMS-borrowed trader theme). The new native voice
        // never touches the AudioSource channels, so without this the old theme keeps
        // playing UNDER the mixer — e.g. leaving Trader_Rekt (default theme on AudioSource)
        // for a custom-file context left Rekt's theme audible out of context. Decision A:
        // the two outputs never both drive the current context.
        private static void FadeOutAudioSourcesIfActive(float fade)
        {
            bool audible =
                (CMM_Primary_Channel   != null && CMM_Primary_Channel.isPlaying   && CMM_Primary_Channel.volume   > 0.0001f) ||
                (CMM_Secondary_Channel != null && CMM_Secondary_Channel.isPlaying && CMM_Secondary_Channel.volume > 0.0001f);
            if (!audible) return;
            CMMConfig.D("[CMM] native: fading out AudioSource channels (mixer taking over).");
            if (_crossFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine); _crossFadeCoroutine = null; }
            _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeOutCoroutine());
            // The channels no longer carry the current context once the mixer owns output —
            // clear their labels so stale state (e.g. a DMS-borrow trader name) can't shadow
            // the native context in the HUD or the AudioSource reverse-fade-reclaim check.
            ActiveChannelContext = null;
            InactiveChannelContext = null;
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

        // Native counterpart of PlayFromCache. SYNCHRONOUS and MAIN-THREAD: it does every
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
        internal void StartNativeContext(string contextName, bool isSameContextSwitchingToNextTrack,
                                         int transitionId, string forcedFile)
        {
            _activeMusicCoroutine = null;   // native path owns no coroutine; teardown is by transitionId
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
            // reclaims the ducked bed when a stinger ends. Native analogue of PlayFromCache's
            // reverse-fade reclaim.
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
                // the transitionId bump).
                if (_trackEndCoroutine != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine); _trackEndCoroutine = null; }
                if (!ctx.Loop && !ctx.PlayOnce)
                    _trackEndCoroutine = CMM_CoRoutines.StartCoroutine(NativeTrackLoop(contextName, transitionId));
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
                else
                    file = PickNextFile(contextName, isSameContextSwitchingToNextTrack ? cache.lastPlayedFile : null);
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
            float fade;
            if (ctx.PlayOnce) fade = 0.25f;
            else if (isSameContextSwitchingToNextTrack) fade = Mathf.Max(0.25f, TrackTransitionFadeDuration);
            else fade = ctx.FadeInDuration > 0f ? ctx.FadeInDuration : CrossFadeDuration;

            // ── Cache bookkeeping (main thread, before dispatch) ──
            cache.previousPlayedFile = cache.lastPlayedFile;
            cache.playingFile        = file;
            cache.lastPlayedFile     = file;
            cache.resumePosition     = 0f;
            cache.lastLeftTime       = -1f;
            _nativeActiveContext = contextName;
            Interlocked.Exchange(ref _nativeVoiceId, 0);   // the worker sets the real id once Play succeeds
            CmmMixer.MasterVolume = CMMConfig.GetVolume(inGame, ctx.VolumeOverride);

            // Menu cold-start (A20): the vanilla title theme can already be playing when the mod
            // loads (BackgroundMusicMono.Start() ran before our Start-postfix mute could catch it).
            // The AudioSource path fades that leftover bed out from PlayFromCache, but native menu
            // music never goes through PlayFromCache — so without this the vanilla theme keeps
            // playing under our custom menu track. Fade it out here as our track takes over (no-op
            // in-game, or when the bed is already muted/silent). Native menu music only runs when the
            // Menu context HAS files, so this never silences vanilla without a custom track to replace it.
            if (!inGame) FadeOutMenuVanillaIfAudible();

            // A PlayOnce stinger (Spawn / Death / POI_Cleared) OVERRIDES the music underneath:
            // the bed fades fully OUT, the stinger plays alone. After it ends the resolved context
            // re-resolves on its own — POI_Cleared → ambient, Spawn → the zone. For Death this is
            // what produces the wanted SILENCE between the death stinger and the respawn Spawn
            // stinger: the bed is gone and the scan loop is dead-gated (playerIsDead) so nothing
            // new resolves until respawn. duckTo = -1 = normal full crossfade-out (NOT a duck —
            // ducking would swell the bed back when the stinger retires and play music in that gap).
            // CmmMixer still supports ducking via Play(duckOthersTo>=0); we just don't ask for it.
            float duckTo = -1f;

            // ── Hand the open + Play to the start worker (survives a main-thread freeze) ──
            EnsureNativeWorker();
            var req = new NativeStartReq
            {
                Context = contextName, File = file, TransitionId = transitionId,
                StartSeconds = startSeconds, Fade = fade, Loop = loop, TrackGain = trackGain,
                DuckOthersTo = duckTo,
                // Only a genuine context change may flush a deep cushion. A same-context Next/Prev
                // must NOT — flushing teleports the outgoing track being crossfaded (jump + swell).
                SnapTight = !isSameContextSwitchingToNextTrack,
            };
            lock (_natQueueLock) _natQueue.Enqueue(req);
            _natSignal.Set();

            CMMConfig.D($"[CMM] native: dispatched {Path.GetFileName(file)} for {contextName} " +
                    $"(fade {fade:F1}s, loop {loop}, gain {trackGain:F2}, start {startSeconds:F1}s" +
                    $"{(duckTo >= 0f ? $", duck→{duckTo:F2}" : "")})");

            // ── Auto-advance (non-loop, non-stinger): crossfade to the next pick at end ──
            if (_trackEndCoroutine != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine); _trackEndCoroutine = null; }
            if (!loop && !ctx.PlayOnce)
                _trackEndCoroutine = CMM_CoRoutines.StartCoroutine(NativeTrackLoop(contextName, transitionId));

            CMMPerf.EndTransition();
        }

        // Polls the active voice and, at the transition lead window (or once it has
        // ended), crossfades in the context's next track — the native equivalent of
        // TrackEndWatcher + ScheduleNextCacheLoad. Reads _nativeVoiceId (set by the start
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
            _scheduleHandledTransition = false;
            StartNativeContext(contextName, true, newId, null);
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
            if (string.IsNullOrEmpty(ctx.Path)) return null;

            ContextCache cache = cacheMap[contextName];

            string[] files = cache.directorySnapshot;
            if (files == null)
            {
                string dir = Path.Combine(MusicDirectory, ctx.Path);
                if (!Directory.Exists(dir)) return null;
                files = EnumerateContextFiles(ctx, dir);
                cache.directorySnapshot = files;
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
                    {
                        return files[UnityEngine.Random.Range(0, files.Length)];
                    }
            }
        }

        // ── File presence check ───────────────────────────────────────────────
        // ContextHasFiles is now a pure in-memory read of _contextsWithFiles and
        // never touches the disk. The presence set is kept current off the main
        // thread by PresenceScanLoop (which does the Directory.GetFiles work on a
        // ThreadPool thread) and by FileWatchLoop's change detection. This removes
        // the synchronous directory enumeration that previously ran on the main
        // thread every time the resolver evaluated a context — including the
        // per-call disk hit for PlayOnce contexts (Death/Spawn) on the audio path.
        //
        // Trade-off: presence can lag a real folder edit by up to one scan tick
        // (PresenceScanInterval), versus the old "exact but main-thread-blocking"
        // behaviour. For a music mod that staleness window is harmless.
        public static bool ContextHasFiles(string contextName)
        {
            return _contextsWithFiles.Contains(contextName);
        }

        // Recomputes presence for one context from its in-memory directory snapshot
        // (no disk). Used by the FileWatchLoop callers that have just refreshed the
        // snapshot, so the presence set updates immediately and disk-free.
        private static void ApplyPresenceFromSnapshot(string contextName, ContextCache cache)
        {
            string[] snap = cache.directorySnapshot;
            bool has = snap != null && snap.Any(f => !_invalidFiles.Contains(f));
            if (has) _contextsWithFiles.Add(contextName);
            else _contextsWithFiles.Remove(contextName);
        }

        // Blacklist a file ONLY when it genuinely exists but can't be opened/decoded
        // (corrupt or unsupported). A file that is missing at open time was moved or
        // deleted mid-operation — a transient, not corruption: blacklisting it would
        // permanently exclude a perfectly valid track that is still (or again) on disk,
        // because _invalidFiles is only cleared when the watcher sees an actual removal.
        // Returns true if the file was blacklisted.
        private static bool MarkInvalidIfCorrupt(string filePath, string reason)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Log.Warning($"[CMM] {reason}: {Path.GetFileName(filePath)} not on disk — " +
                                "transient (moved/removed), not blacklisting.");
                    return false;
                }
            }
            catch { /* path error — fall through and treat as invalid */ }

            _invalidFiles.Add(filePath);
            Log.Error($"[CMM] {reason}: {Path.GetFileName(filePath)}, marking invalid");
            return true;
        }

        // Marked private so FileWatchLoop can call it for a single context.
        // Never performs disk I/O: if a current snapshot exists we derive presence
        // from it in memory; otherwise we queue the context for the background
        // PresenceScanLoop to (re)scan off the main thread.
        private static void InvalidateContextFileCache(string contextName = null)
        {
            if (contextName == null)
            {
                _contextsWithFilesDirty = true;
                return;
            }

            if (cacheMap.TryGetValue(contextName, out var cache) && cache.directorySnapshot != null)
                ApplyPresenceFromSnapshot(contextName, cache);
            else
                _presenceDirty.Add(contextName);
        }

        // A file we tried to play is gone from disk. Remove it from the context's
        // directory snapshot right now and re-derive presence, so the resolver and
        // PickNextFile stop re-selecting the same missing file on every scan tick.
        // Without this, a removed file that isn't blacklisted ("not on disk" is treated
        // as a transient move/lock, not corruption) keeps the context in a replay loop
        // until a background rescan happens to refresh the snapshot — which it won't,
        // because the failure path only re-derives presence from the STALE snapshot.
        // Also queues an authoritative disk rescan to catch any sibling changes.
        private static void PruneMissingFromSnapshot(string contextName, string filePath)
        {
            if (cacheMap.TryGetValue(contextName, out ContextCache cache))
            {
                string[] snap = cache.directorySnapshot;
                if (snap != null && snap.Length > 0)
                {
                    string[] pruned = snap.Where(f =>
                        !string.Equals(f, filePath, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (pruned.Length != snap.Length)
                    {
                        cache.directorySnapshot = pruned;
                        ApplyPresenceFromSnapshot(contextName, cache);
                        CMMConfig.D($"[CMM] Pruned missing file from {contextName} snapshot: {Path.GetFileName(filePath)}");
                    }
                }
            }
            _presenceDirty.Add(contextName);
        }

        // Synchronous full rebuild. Disk-bound, so it is only used as the one-time
        // bootstrap during Init (mod load, before menu music starts) — never on a
        // gameplay frame. Ongoing updates go through the background PresenceScanLoop.
        private static void RebuildContextsWithFiles()
        {
            _contextsWithFiles.Clear();
            foreach (var ctx in contexts.Values)
            {
                if (string.IsNullOrEmpty(ctx.Path)) continue;
                // Recursive contexts are populated by RecursiveCrawlLoop's first sweep,
                // not by a synchronous full walk here — that walk would freeze mod load
                // on a tree with thousands of folders. Their presence flips on as soon
                // as the crawler sees the first file (a short delay after load).
                if (ctx.IncludeSubfolders) continue;
                string d = Path.Combine(MusicDirectory, ctx.Path);
                if (!Directory.Exists(d)) continue;
                if (EnumerateContextFiles(ctx, d).Length > 0)
                    _contextsWithFiles.Add(ctx.Context);
            }
            _contextsWithFilesDirty = false;
            _presenceDirty.Clear();
        }

        // ── Background presence scan ───────────────────────────────────────────
        // Keeps _contextsWithFiles current without ever calling Directory.GetFiles
        // on the main thread. Disk enumeration runs on a ThreadPool thread; results
        // are applied back on the main thread (the only writer of _contextsWithFiles
        // and directorySnapshot, so no locking is needed).
        private IEnumerator PresenceScanLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(PresenceScanInterval);

                // Gather the work for this tick. A full rebuild supersedes the
                // per-context dirty set. Resolve context → directory pairs here on
                // the main thread so the background thread never reads the shared
                // `contexts` dictionary (which ConfigWatchLoop may mutate).
                List<string> names;
                if (_contextsWithFilesDirty)
                {
                    names = contexts.Values
                        .Where(c => !string.IsNullOrEmpty(c.Path))
                        .Select(c => c.Context)
                        .ToList();
                    _contextsWithFilesDirty = false;
                    _presenceDirty.Clear();
                }
                else if (_presenceDirty.Count > 0)
                {
                    names = _presenceDirty.ToList();
                    _presenceDirty.Clear();
                }
                else continue;

                // Resolve directory, reach (flat/recursive) and claim prefixes here on
                // the main thread — the background thread must never read the shared
                // `contexts` dictionary. IsClaimedByDeeperContext (used below) only
                // does path string math, so it is safe off-thread.
                var toScan = new List<PresenceScanItem>(names.Count);
                foreach (string name in names)
                {
                    Contexts ctx = GetContext(name);
                    if (ctx == null || string.IsNullOrEmpty(ctx.Path)) continue;
                    // Recursive contexts are owned by RecursiveCrawlLoop — never re-walk
                    // their whole tree here (that's the O(files) cost we're avoiding).
                    if (ctx.IncludeSubfolders) continue;
                    toScan.Add(new PresenceScanItem
                    {
                        Name    = name,
                        Dir     = Path.Combine(MusicDirectory, ctx.Path),
                        Option  = SearchOptionFor(ctx),
                        Claimed = GetClaimedSubtreePrefixes(name),
                    });
                }
                if (toScan.Count == 0) continue;

                // Enumerate the directories on a background thread.
                var results = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                bool done = false;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    foreach (var item in toScan)
                    {
                        try
                        {
                            results[item.Name] = Directory.Exists(item.Dir)
                                ? Directory.GetFiles(item.Dir, "*", item.Option)
                                    .Where(f => AudioDecoder.IsSupported(f)
                                                && !IsClaimedByDeeperContext(f, item.Claimed))
                                    .ToArray()
                                : new string[0];
                        }
                        // One unreadable folder (ACL, removed media, or an over-long path
                        // under a recursive subfolders="true" context) must not strand every
                        // LATER context in this scan: a single swallowed throw would leave
                        // their snapshots null, and SnapshotsReady — which now requires ALL
                        // contexts enumerated — could then never latch. Give the bad folder
                        // an empty (non-null) snapshot so it reads as "no files" and recovers
                        // on a later tick.
                        catch { results[item.Name] = new string[0]; }
                    }
                    done = true;
                });
                while (!done) yield return null;

                // Apply on the main thread: refresh the snapshot and presence flag.
                foreach (var kv in results)
                {
                    if (cacheMap.TryGetValue(kv.Key, out var cache))
                        cache.directorySnapshot = kv.Value;
                    bool has = kv.Value.Any(f => !_invalidFiles.Contains(f));
                    if (has) _contextsWithFiles.Add(kv.Key);
                    else _contextsWithFiles.Remove(kv.Key);
                }
            }
        }

        // ── Memory summary ────────────────────────────────────────────────────
        public static long GetCacheMemoryBytes()
        {
            long total = 0;
            foreach (var cache in cacheMap.Values)
            {
                if (cache.Current.Decoded?.Samples != null) total += (long)cache.Current.Decoded.Samples.Length * 4;
                if (cache.Next.Decoded?.Samples    != null) total += (long)cache.Next.Decoded.Samples.Length * 4;
                if (cache.Current.Clip != null && cache.Current.Reader == null && cache.Current.Decoded == null)
                    total += (long)cache.Current.Clip.samples * cache.Current.Clip.channels * 4;
                if (cache.Next.Clip    != null && cache.Next.Reader    == null && cache.Next.Decoded    == null)
                    total += (long)cache.Next.Clip.samples * cache.Next.Clip.channels * 4;
            }
            return total;
        }

        public static string GetCacheMemorySummary(bool includePerContext = true)
        {
            long totalBytes = 0;
            var lines = new System.Text.StringBuilder();

            foreach (var kv in cacheMap)
            {
                ContextCache c = kv.Value;

                bool curStreaming  = c.Current.Clip != null && c.Current.Reader != null;
                bool nextStreaming = c.Next.Clip    != null && c.Next.Reader    != null;

                long cur  = c.Current.Decoded?.Samples != null ? (long)c.Current.Decoded.Samples.Length * 4 : 0;
                long next = c.Next.Decoded?.Samples    != null ? (long)c.Next.Decoded.Samples.Length    * 4 : 0;
                if (cur  == 0 && c.Current.Clip != null && !curStreaming)
                    cur  = (long)c.Current.Clip.samples * c.Current.Clip.channels * 4;
                if (next == 0 && c.Next.Clip    != null && !nextStreaming)
                    next = (long)c.Next.Clip.samples    * c.Next.Clip.channels    * 4;

                bool anyLoaded = c.Current.Clip != null || c.Next.Clip != null;
                if (!anyLoaded) continue;

                totalBytes += cur + next;
                if (includePerContext)
                {
                    string curLabel = curStreaming ? "streaming" : FormatBytes(cur);
                    string nextLabel = nextStreaming ? "streaming" : (next > 0 ? FormatBytes(next) : null);
                    lines.AppendLine($"  {kv.Key}: {curLabel} current" +
                                     (nextLabel != null ? $" + {nextLabel} next" : ""));
                }
            }

            string footer = $"  PCM total: {FormatBytes(totalBytes)}" +
                            (totalBytes == 0 ? " (all contexts streaming)" : "");
            if (includePerContext) lines.Append(footer);
            else lines.Append(totalBytes == 0 ? "streaming" : FormatBytes(totalBytes));
            return lines.ToString();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024f:F1} KB";
            return $"{bytes} B";
        }
    }
}
