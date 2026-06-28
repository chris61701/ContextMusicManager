
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
        // Per-context start offset for the prewarm scan, chosen once per SESSION (process)
        // and reused for every BuildMenuPrewarmQueue call. The offset is random so the one
        // track we prewarm per context differs from launch to launch (coverage spreads
        // across startups), but it MUST be stable within a session: BuildMenuPrewarmQueue
        // doubles as the completion gate, so re-randomizing each call would keep landing on
        // a different unmeasured track and never let the bounded goal read as "met" — it
        // would drain the whole library instead of one track per context.
        private static readonly Dictionary<string, int> _prewarmStartOffset =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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
            while (_pendingDisposals.Count > 0)
            {
                TrackSlot slot = _pendingDisposals.Dequeue();
                if (slot.Clip != null) Destroy(slot.Clip);
                slot.Release();
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

        // Builds the BOUNDED menu prewarm set into _loudnessQueue: the first
        // still-unmeasured track of every context (so each gets one normalized track
        // ready for its first play), plus a SECOND track for the context currently
        // playing (the menu music) so it has a follow-up ready. An already-measured
        // track counts toward a context's quota without re-queueing. Files shared by
        // two contexts are queued once. Returns the number of files enqueued.
        private static int BuildMenuPrewarmQueue()
        {
            _loudnessQueue.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string playing = ActiveChannelContext;

            // Priority contexts (Menu, Ambient) first, so the things heard first warm first.
            foreach (string ctxName in cacheMap.Keys.OrderByDescending(IsPriorityContext))
            {
                if (!cacheMap.TryGetValue(ctxName, out ContextCache cache)) continue;
                string[] snap = cache.directorySnapshot;
                if (snap == null) continue;

                int want = string.Equals(ctxName, playing, StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                int taken = 0;
                // Start the scan at a RANDOM offset (wrapping) rather than always snap[0],
                // so the one track we prewarm per context differs from session to session
                // instead of forever measuring the same first file. Coverage then spreads
                // across the library over repeated startups. The offset is cached per
                // context for the whole session: this method is also the completion gate,
                // so re-rolling it each call would never let the bounded goal read as met.
                int n = snap.Length;
                if (!_prewarmStartOffset.TryGetValue(ctxName, out int start))
                {
                    start = n > 1 ? UnityEngine.Random.Range(0, n) : 0;
                    _prewarmStartOffset[ctxName] = start;
                }
                for (int k = 0; k < n; k++)
                {
                    if (taken >= want) break;
                    string f = snap[(start + k) % n];
                    // An already-measured track satisfies the quota — check this FIRST,
                    // before the processed/in-flight skip, since a track measured this
                    // session is in BOTH sets and must still count (else we'd keep
                    // enqueuing the next unmeasured file per context and drain the whole
                    // library — never reaching the bounded goal).
                    if (LoudnessStore.IsMeasured(f)) { taken++; continue; }
                    if (_loudnessProcessed.Contains(f) || _loudnessInFlight.ContainsKey(f)
                        || _invalidFiles.Contains(f)) continue;
                    if (seen.Add(f)) { LoudnessEnqueue(f); taken++; }
                }
            }
            return _loudnessQueue.Count;
        }

        // True once at least one context folder has been enumerated, so an empty
        // prewarm queue can be read as "goal met" rather than "snapshots not ready".
        private static bool SnapshotsReady()
        {
            foreach (var cache in cacheMap.Values)
                if (cache.directorySnapshot != null) return true;
            return false;
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

        public static void FadeToSilence()
        {
            CMMConfig.D("[CMM] Fading to silence.");
            if (_activeMusicCoroutine != null) { CMM_CoRoutines.StopCoroutine(_activeMusicCoroutine); _activeMusicCoroutine = null; }
            if (_nextCacheCoroutine != null) { CMM_CoRoutines.StopCoroutine(_nextCacheCoroutine); _nextCacheCoroutine = null; }
            if (_trackEndCoroutine != null) { CMM_CoRoutines.StopCoroutine(_trackEndCoroutine); _trackEndCoroutine = null; }
            if (_crossFadeCoroutine != null) { CMM_CoRoutines.StopCoroutine(_crossFadeCoroutine); _crossFadeCoroutine = null; }
            _crossFadeCoroutine = CMM_CoRoutines.StartCoroutine(Instance.FadeOutCoroutine());
        }

        private IEnumerator FadeOutCoroutine()
        {
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
            // Within-context advance: uniform-random pick (see PickNextFile), then measure
            // THIS exact file now — during the preload lead window (~10s before the track
            // ends) — so the block-before-play in PlayFromCache is already satisfied and the
            // next track starts without a measurement stall.
            string prePickedFile = PickNextFile(contextName, avoidFile: avoidForPick);
            if (prePickedFile != null)
                CMM_CoRoutines.StartCoroutine(EnsureLoudnessMeasured(prePickedFile, useScratch: true));

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
            string avoid = GlobalTrackOrder == TrackOrderMode.RandomNoRepeat
                              ? cache.lastPlayedFile : null;
            string filePath = prePickedFile ?? PickNextFile(contextName, avoidFile: avoid);

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
                    Log.Error($"[CMM] LoadNextClip: full decode failed for {Path.GetFileName(filePath)}");
                    yield break;
                }

                bool isMenu = contextName == "Menu";
                AudioClip fullClip = isMenu
                    ? AudioDecoder.CreateNonStreamingClip(decoded, clipName)
                    : AudioDecoder.CreateStreamingClip(decoded, clipName);
                if (fullClip == null) yield break;

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
                    Log.Error($"[CMM] LoadNextClip: failed to open {Path.GetFileName(filePath)}");
                    yield break;
                }

                AudioClip clip = AudioDecoder.CreateStreamingClipFromReader(reader, clipName);

                if (clip == null) { reader?.Dispose(); yield break; }

                if (cache.Next.Clip != null) { Destroy(cache.Next.Clip); cache.Next.Release(); }
                cache.Next.Clip   = clip;
                cache.Next.Reader = reader;
                cache.previousPlayedFile = cache.lastPlayedFile;
                cache.lastPlayedFile = filePath;
                CMMConfig.D($"[CMM] Next clip ready (streaming) for {contextName}: {Path.GetFileName(filePath)}");
            }
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
                files = Directory.GetFiles(dir)
                    .Where(f => AudioDecoder.IsSupported(f) && !_invalidFiles.Contains(f))
                    .ToArray();
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
                string d = Path.Combine(MusicDirectory, ctx.Path);
                if (!Directory.Exists(d)) continue;
                if (Directory.GetFiles(d).Any(f => AudioDecoder.IsSupported(f) && !_invalidFiles.Contains(f)))
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

                var toScan = new List<KeyValuePair<string, string>>(names.Count);
                foreach (string name in names)
                {
                    Contexts ctx = GetContext(name);
                    if (ctx == null || string.IsNullOrEmpty(ctx.Path)) continue;
                    toScan.Add(new KeyValuePair<string, string>(
                        name, Path.Combine(MusicDirectory, ctx.Path)));
                }
                if (toScan.Count == 0) continue;

                // Enumerate the directories on a background thread.
                var results = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                bool done = false;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        foreach (var pair in toScan)
                        {
                            results[pair.Key] = Directory.Exists(pair.Value)
                                ? Directory.GetFiles(pair.Value).Where(f => AudioDecoder.IsSupported(f)).ToArray()
                                : new string[0];
                        }
                    }
                    catch { }
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
