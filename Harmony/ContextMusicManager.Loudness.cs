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
    // LOUDNESS / VOLUME / TRACK STAGING (split from AudioPlayer)
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager
    {

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
        // Serializes IN-GAME loudness decodes (see StartMeasure) so concurrent prewarms can't
        // saturate several cores at once and starve the main thread.
        private static readonly object _measureGate = new object();
        private static readonly Dictionary<string, MeasureHandle> _loudnessInFlight =
            new Dictionary<string, MeasureHandle>(StringComparer.OrdinalIgnoreCase);

        // ── Failed measurements: back off, do not hammer ──────────────────────
        // A measurement that fails leaves nothing in the store, so IsMeasured stays false --
        // and every in-game request path (RequestPrewarm, StageNextFile, the block-before-play
        // hold) gates on exactly that. So the maintenance tick re-requested the same file every
        // LoudnessSweepInGameInterval, i.e. once a second, for the rest of the session: a whole
        // file re-hashed and re-opened each time, ~86,000 times over a long session. (The menu
        // prescan was never affected -- it has _loudnessProcessed, which the in-game paths do
        // not consult.) This is the 295-times bug's shape with a different root cause.
        //
        // A flat blacklist is the wrong fix and this codebase has the scar to prove it: a
        // transient drive hiccup once marked radio tracks dead for the whole process. So this
        // BACKS OFF rather than gives up -- an unreadable file costs one decode every half hour
        // instead of 1,800, and a file that comes back is picked up on the next attempt. The
        // size/mtime stamp resets it immediately when the file itself changes, which is the
        // case where waiting would be actively wrong.
        private struct MeasureFailure
        {
            public int   Attempts;
            public float RetryAt;        // CmmClock seconds
            public long  Size, Mtime;    // what failed; a change resets the backoff
        }
        private static readonly Dictionary<string, MeasureFailure> _measureFailures =
            new Dictionary<string, MeasureFailure>(StringComparer.OrdinalIgnoreCase);

        // 5 s, 30 s, 5 min, then 30 min. The early retries cover a blip; the cap is what stops
        // a permanently-unreadable file from costing anything measurable over a long session.
        private static readonly float[] MeasureBackoffSeconds = { 5f, 30f, 300f, 1800f };

        // True when `file` failed recently enough that trying again now is just noise.
        private static bool MeasureBackedOff(string file, long size, long mtime)
        {
            if (!_measureFailures.TryGetValue(file, out MeasureFailure f)) return false;
            if (f.Size != size || f.Mtime != mtime) { _measureFailures.Remove(file); return false; }
            if (CmmClock.Now >= f.RetryAt) return false;
            return true;
        }

        private static void NoteMeasureFailure(string file, long size, long mtime)
        {
            _measureFailures.TryGetValue(file, out MeasureFailure f);
            if (f.Size != size || f.Mtime != mtime) f.Attempts = 0;
            int i = Math.Min(f.Attempts, MeasureBackoffSeconds.Length - 1);
            f.Attempts++;
            f.Size = size; f.Mtime = mtime;
            f.RetryAt = CmmClock.Now + MeasureBackoffSeconds[i];
            _measureFailures[file] = f;
            CMMConfig.D($"[CMM] Loudness: {Path.GetFileName(file)} failed to measure " +
                        $"(attempt {f.Attempts}) — next try in {MeasureBackoffSeconds[i]:F0}s.");
        }

        // A measurement landed, so whatever was wrong no longer is.
        private static void ClearMeasureFailure(string file)
        {
            if (_measureFailures.Count > 0) _measureFailures.Remove(file);
        }
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
        // to the on-demand look-ahead (EnsureLoudnessMeasured / PreStageNextAdvance).
        // All orchestration here (queue, store, bookkeeping) stays on the main-thread
        // coroutine — only the decode runs on the workers (each fully independent; the
        // native loudness pass owns its own buffer, so no per-lane scratch is needed).
        // Set once the bounded menu prewarm goal is met (one track per context plus
        // a second for the playing context); stops the menu scanner.
        private static bool _menuPrescanComplete;

        // One concurrent measurement slot for the parallel prescan.
        private struct PrescanSlot
        {
            public string Path;
            public long Mtime;
            public MeasureHandle Handle;
        }


        public static bool _gameIsLoading = false;

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
                    Log.Warning($"[CMM] Validation failed for {filePath}");
                sw.Stop();
                CMMConfig.D($"[CMM] QuickValidate took {sw.Elapsed.TotalMilliseconds:F3} ms");
                callback(valid);
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

        // Ramp durations for in-place per-track gain changes on the live native voice (see
        // CmmMixer.SetVoiceTrackGain). The keypress is a small (≤10%) step — a brief declick is enough.
        // A late measurement settling in can be a large correction (un-normalized 1.0 → normalized
        // level), so it gets a slightly longer fade so the level just eases in rather than lurching.
        private const float TrackGainKeyRampSeconds   = 0.04f;
        private const float TrackGainSettleSeconds    = 0.20f;

        // Settle a freshly-committed loudness measurement into the currently-playing native voice.
        // A track that started before it was measured plays at neutral 1.0 (un-normalized); the
        // per-voice TrackGain is baked once at Play time and is NOT re-read by VolumeSyncLoop (that
        // only drives master volume), so without this the normalization never reaches the live voice
        // and the first trim keypress would apply the whole correction at once (an audible lurch).
        // No-op unless `file` is the exact track the native mixer is currently voicing.
        private static void SettleTrackGainIntoLiveVoice(string file, float rampSeconds = TrackGainSettleSeconds)
        {
            if (string.IsNullOrEmpty(file)) return;
            string active = _nativeActiveContext;
            if (active == null) return;
            if (!cacheMap.TryGetValue(active, out ContextCache cache) ||
                !string.Equals(cache.playingFile, file, StringComparison.OrdinalIgnoreCase))
                return;
            long vid = Interlocked.Read(ref _nativeVoiceId);
            if (vid != 0)
                CmmMixer.Instance?.SetVoiceTrackGain(
                        vid, LoudnessStore.GetTrackGain(file), rampSeconds);
        }

        private static bool _gameAudioMuted;
        private static float _gameAudioMutedFrom = 1f;

        private IEnumerator VolumeSyncLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(0.1f);
                if (_functionallyDormant) continue;   // A/B dormant arm
                // Testing aid: silence the GAME while CMM keeps playing. AudioListener governs
                // Unity's audio only -- our emitters and music go out through the native mixer,
                // which never reaches the listener -- so this cleanly separates the two. Restores
                // whatever the game had when switched back off, rather than assuming 1.0.
                if (CMMConfig.DebugMuteGameAudio)
                {
                    if (!_gameAudioMuted) { _gameAudioMutedFrom = AudioListener.volume; _gameAudioMuted = true; }
                    if (AudioListener.volume != 0f) AudioListener.volume = 0f;
                }
                else if (_gameAudioMuted)
                {
                    _gameAudioMuted = false;
                    AudioListener.volume = _gameAudioMutedFrom;
                }
                // Keep the native mixer's master volume in lock-step with the game's
                // music slider. Sampled here on the main thread (GamePrefs is not safe
                // to read from the mixer thread) and stored in a volatile the mixer reads.
                // Uses the active native context's volume override, mirroring GetContextVolume.
                // While the personal radio owns the bus there IS no active context — the Radio
                // context's own volume governs instead (radio and contexts share the main bus) —
                // EXCEPT while a PlayOnce stinger is airing over the radio: for its duration the
                // stinger's own context volume governs, like any context playback.
                if (PersonalRadio.SuppressesMusic && !PlayOnceStingerActive)
                    CmmMixer.MasterVolume = CMMConfig.GetVolume(inGame, PersonalRadio.MasterVolumeOverride);
                else
                {
                    Contexts nctx = _nativeActiveContext != null ? GetContext(_nativeActiveContext) : null;
                    CmmMixer.MasterVolume = CMMConfig.GetVolume(inGame, nctx?.VolumeOverride ?? -1f);
                }
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
        }


        // ── Loudness: measure-on-demand (block-before-play) ───────────────────
        // Ensures `filePath` has a current LUFS measurement, measuring it now (on a
        // background thread, awaited) if not. Kicked off non-blocking by
        // StartNativeContext for an unmeasured track. No-op when normalization is off, the
        // file is gone, or it's already current.
        //
        // When a measurement IS needed the caller awaits it, so the track's start is
        // delayed by one (low-priority) decode pass. That delay is intentional — see
        // StartMeasure: we favor a smooth frame rate over an instant start, so an
        // unscanned track waits to begin rather than stuttering the game as it scans.
        // Static entry so ANY source can ask that `file` be measured AHEAD of play — background,
        // dedup'd (cache + in-flight), settles into whatever live voice ends up playing it. No-op if
        // off, already measured, or already in flight. Used by the N-ahead look-ahead on both the main
        // context player and the world-emitter station.
        // How many measurements are running right now. RequestPrewarm starts a COROUTINE PER FILE
        // with no queue and no cap, which is fine for a handful and ruinous for a sweep: measuring
        // is decode-bound, so thirty at once starves the one track somebody is waiting to hear.
        // Callers that speculate rather than need should check this first.
        internal static int LoudnessInFlight => _loudnessInFlight.Count;

        internal static void RequestPrewarm(string file)
        {
            if (!CMMConfig.LoudnessNormalizationEnabled || string.IsNullOrEmpty(file)) return;
            if (LoudnessStore.IsMeasured(file)) return;
            if (_loudnessInFlight.ContainsKey(file)) return;
            CMM_CoRoutines.StartCoroutine(EnsureLoudnessMeasured(file));
        }

        private static IEnumerator EnsureLoudnessMeasured(string filePath)
        {
            if (!CMMConfig.LoudnessNormalizationEnabled || string.IsNullOrEmpty(filePath)) yield break;

            long size = 0, mtime = 0;
            bool ok = true;
            try
            {
                using (CMMPerf.Measure(CMMPerf.Phase.MainIo))
                {
                    var fi = new FileInfo(filePath);
                    if (!fi.Exists) ok = false;
                    else { size = fi.Length; mtime = fi.LastWriteTimeUtc.Ticks; }
                }
            }
            catch { ok = false; }
            if (!ok) yield break;
            if (LoudnessStore.HasCurrent(filePath, size, mtime)) yield break;
            if (MeasureBackedOff(filePath, size, mtime)) yield break;

            MeasureHandle h = StartMeasure(filePath);
            // A long file answers in SLICES (AudioDecoder's progressive measurement), so the wait
            // is no longer all-or-nothing: each partial reading is published as it lands and the
            // playing track glides onto it. That is what lets the start-hold release about a
            // second into a 37-minute podcast instead of expiring at 3 s and starting on the
            // unmeasured guess. A short track produces no partials at all — it reaches the end of
            // the file inside the first slice — so nothing below runs for it.
            int published = 0;
            while (!h.Done)
            {
                PartialReading part = h.Latest;
                if (part != null && part.Seq > published)
                {
                    published = part.Seq;
                    PublishMeasurement(filePath, mtime, part.Value, provisional: true);
                }
                yield return null;
            }
            ClearInFlight(filePath, h);
            TrackLoudness res = h.Result;

            _loudnessProcessed.Add(filePath);
            if (res.Ok)
            {
                ClearMeasureFailure(filePath);
                PublishMeasurement(filePath, mtime, res, provisional: false);
                CMMConfig.D(res.NoIntegrated
                    ? $"[CMM] Loudness (block) {Path.GetFileName(filePath)}: no gated block — no correction"
                    : $"[CMM] Loudness (block) {Path.GetFileName(filePath)}: {res.Lufs:F1} LUFS");
            }
            else if (published > 0)
            {
                // The decode failed AFTER publishing partials — a file yanked mid-measure is the
                // realistic case, the music drive being removable. Drop the partial rather than
                // leave it: it would be stuck otherwise, since HasCurrent rejects a provisional
                // (nothing treats it as done) while IsMeasured accepts one (nothing asks again),
                // so the track would play at a part-file gain for the rest of the session.
                LoudnessStore.ForgetProvisional(filePath);
                NoteMeasureFailure(filePath, size, mtime);
                CMMConfig.D($"[CMM] Loudness incomplete for {Path.GetFileName(filePath)} after " +
                            $"{published} partial reading(s) — dropped, will re-measure.");
            }
            else NoteMeasureFailure(filePath, size, mtime);
        }

        // Commit a reading (final or partial) and ease whatever voice is airing that file onto the
        // gain it implies. Main thread only, like every other LoudnessStore access.
        //
        // The ramp is chosen from what the correction actually is. The FIRST reading on a track
        // replaces the unmeasured guess and can be a large move, so it scales with the size of it.
        // Every reading AFTER that is a refinement — under a dB on real music — and gets a long,
        // deliberately unhurried glide instead: those should never be heard happening, only heard
        // having happened. A correction that is somehow large still gets at least its scaled ramp.
        private static void PublishMeasurement(string filePath, long mtime, TrackLoudness res, bool provisional)
        {
            bool hadReading = LoudnessStore.IsMeasured(filePath);
            float before = LoudnessStore.GetTrackGain(filePath);

            LoudnessStore.Store(filePath, res.Size, mtime, res.Hash, res.Lufs, res.Peak,
                                provisional, res.NoIntegrated);

            float after = LoudnessStore.GetTrackGain(filePath);
            if (Mathf.Abs(after - before) < 1e-4f) return;   // nothing moved; no voice to disturb

            float ramp = LoudnessStore.SettleRampSeconds(before, after);
            if (hadReading) ramp = Mathf.Max(ramp, CMMConfig.LoudnessRefineRampSeconds);

            // Settle into whichever voice is airing this file — the main context voice, the
            // personal radio's main-bus voice, or a world-emitter station's. Each is a no-op
            // unless it is actually playing this exact track.
            SettleTrackGainIntoLiveVoice(filePath, ramp);
            PersonalRadio.SettleLoudness(filePath, ramp);   // all versions
#if GV_V3
            EmitterAudioLayer.SettleLoudness(filePath, ramp);
#endif
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
                CMM_CoRoutines.StartCoroutine(EnsureLoudnessMeasured(pin));
        }

        // Rule 1 maintenance: every context that is neither the active one nor parked on a
        // live resume should have exactly one pinned, measured staged track ready for a
        // stall-free switch-in. Idempotent (only picks/measures an empty or unmeasured pin),
        // so it's cheap to run every tick. Resume-parked resumable contexts are skipped —
        // returning to them resumes the kept track, so no new track is staged (rule 3); a
        // context whose resume later goes stale gets pinned on the next pass.
        private void EnsureStagedPins()
        {
            string active = _nativeActiveContext;
            foreach (var kv in cacheMap)
            {
                string name = kv.Key;
                if (string.Equals(name, active, StringComparison.OrdinalIgnoreCase)) continue;
                Contexts ctx = GetContext(name);
                if (ctx == null || string.IsNullOrEmpty(ctx.Source)) continue;   // no source → nothing to pin
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
                if (cache.stagedFile != null)       _loudnessKeepScratch.Add(cache.stagedFile);
                // The play-history entries adjacent to the cursor: what one Prev / Next
                // press would replay, kept measured so the replay starts normalized.
                if (cache.historyIndex > 0)
                    _loudnessKeepScratch.Add(cache.playHistory[cache.historyIndex - 1]);
                if (cache.historyIndex >= 0 && cache.historyIndex < cache.playHistory.Count - 1)
                    _loudnessKeepScratch.Add(cache.playHistory[cache.historyIndex + 1]);
            }
            // Protect tracks mid-measure (their path isn't in a cache field yet) so a sweep
            // can't evict a measurement the instant before its track is loaded.
            foreach (var p in _loudnessInFlight.Keys) _loudnessKeepScratch.Add(p);
            // The world-emitter station's voice + up-next queue aren't context cache entries, so protect
            // them too — otherwise the sweep evicts what the station's look-ahead just measured, and the
            // per-tick prewarm re-decodes it forever.
#if GV_V3
            EmitterAudioLayer.AppendLoudnessKeep(_loudnessKeepScratch);
#endif
            PersonalRadio.AppendLoudnessKeep(_loudnessKeepScratch);   // all versions

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
                if (_functionallyDormant) { yield return new WaitForSeconds(0.5f); continue; }   // A/B dormant arm
                // Menu prewarm: while on the menu, measure a BOUNDED set — one track per
                // context + a second for the playing one — on a worker pool, then stop
                // (_menuPrescanComplete). This guarantees every context has a ready,
                // normalized track the first time it's switched to. The rest of each
                // context is filled in on demand by the look-ahead (EnsureLoudnessMeasured
                // / PreStageNextAdvance), so nothing is measured beyond what's about to
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
        // on the workers (each fully independent). Runs until the bounded set is measured
        // or a game starts loading, then lets in-flight slots finish (so no work is lost).
        private IEnumerator MenuPrescanDrain()
        {
            int width = PrescanThreadCount();
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
                    slots[i].Handle = StartMeasure(path);
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
                if (string.IsNullOrEmpty(ctx.Source)) continue;
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
                // Same commit-and-settle the on-demand path uses. VolumeSyncLoop only drives MASTER
                // volume on the native path — it never re-reads a voice's TrackGain — so if this
                // measured the track already playing, the settle has to be explicit.
                PublishMeasurement(slot.Path, slot.Mtime, res, provisional: false);
                CMMConfig.D($"[CMM] Loudness prescanned {Path.GetFileName(slot.Path)}: {res.Lufs:F1} LUFS (peak {res.Peak:F3})");
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
        // De-duplicated by path: a second request for a file already being measured joins
        // the existing handle instead of spawning a competing decode — INCLUDING a handle
        // that has just finished (Done) but whose consumer hasn't run ClearInFlight yet.
        // (Joining only when !Done left a one-frame window where a Done-but-not-yet-removed
        // handle was skipped, spawning a duplicate decode and then having the consumer's
        // blind Remove delete the *newer* handle — the exact double-decode this map exists
        // to prevent.) Removal is the consumer's job via ClearInFlight, which deletes the
        // entry only if it is still this same handle.
        private static MeasureHandle StartMeasure(string path)
        {
            if (_loudnessInFlight.TryGetValue(path, out MeasureHandle existing))
                return existing;

            var h = new MeasureHandle();
            _loudnessInFlight[path] = h;
            // IN-GAME, measures are SERIALIZED through _measureGate: the N-ahead prewarm can queue
            // several files at once, and each one runs a full-rate native decode on its own thread —
            // 3-4 saturated cores visibly starve the main thread even at BelowNormal priority (78 of
            // 274 logged stutters sat on loudness bursts, 2026-07-07). One decode at a time keeps the
            // burst to a single core; the others just wait their turn on their own (background)
            // threads. The MENU prescan stays parallel — decided on the main thread NOW, not inside
            // the worker, so a world loading mid-measure can't flip the mode.
            bool serialize = GameManager.Instance?.World != null;
            // Partial readings are handed back through the handle exactly as the final one is: the
            // worker only ever WRITES them, the main-thread coroutine reads and commits. Each is a
            // fresh immutable box published by a single volatile reference write, so the reader
            // either sees the whole reading or the previous one — never half of either.
            double chunk = CMMConfig.LoudnessChunkSeconds;
            Action<TrackLoudness> onPartial =
                r => h.Latest = new PartialReading { Seq = h.NextSeq(), Value = r };

            var t = new Thread(() =>
            {
                try
                {
                    if (serialize) lock (_measureGate) { CMMPerf.DecodeBegin(); try { h.Result = AudioDecoder.MeasureLoudness(path, chunk, onPartial); } finally { CMMPerf.DecodeEnd(); } }
                    else { CMMPerf.DecodeBegin(); try { h.Result = AudioDecoder.MeasureLoudness(path, chunk, onPartial); } finally { CMMPerf.DecodeEnd(); } }
                }
                catch { }
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

        // One partial reading, immutable once published. A class rather than a struct field so a
        // single volatile reference write publishes it atomically: TrackLoudness is several words
        // wide and a struct assignment across threads can tear.
        private sealed class PartialReading
        {
            public int Seq;
            public TrackLoudness Value;
        }

        private sealed class MeasureHandle
        {
            public volatile bool Done;
            public TrackLoudness Result;
            // Most recent partial reading, or null if none yet. Written by the worker, read by the
            // main-thread coroutine, which uses Seq to tell a new one from one it has already
            // committed (several coroutines can be joined to one handle).
            public volatile PartialReading Latest;
            private int _seq;
            public int NextSeq() => Interlocked.Increment(ref _seq);
        }

        // Contexts whose first track the player is most likely to hear first, so the
        // pre-warm covers them before reactive contexts (combat/POI/trader/etc.): the
        // Menu music and the baseline Ambient exploration tracks.
        private static bool IsPriorityContext(string name)
            => name != null
               && (name.Equals("Menu", StringComparison.OrdinalIgnoreCase)
                   || name.StartsWith("Ambient", StringComparison.OrdinalIgnoreCase));

        // ── Loudness: debounced cache save ────────────────────────────────────
        // Coroutine fallback (NativeScheduler off): snapshot AND write both on the main
        // thread, debounced by WaitForSeconds.
        private IEnumerator LoudnessSaveLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(LoudnessSaveDebounce);
                if (LoudnessStore.Dirty) using (CMMPerf.Measure(CMMPerf.Phase.MainIo)) LoudnessStore.Save();
            }
        }

        // Off-frame-loop twin of LoudnessSaveLoop, run by CmmScheduler when NativeScheduler
        // is on — the first loop migrated off Unity's frame loop, and the proof of the
        // scheduler + dispatcher pattern. The dirty CHECK is a thread-safe volatile read; the
        // SNAPSHOT of the (main-thread-only) store is marshalled back via Send; then the slow
        // XML serialize + file write run HERE, on the scheduler thread, off the frame loop.
        // Send briefly blocks this thread for the (fast) snapshot — acceptable for a save,
        // which is never time-critical, and it parks only the HOUSEKEEPING lane: the
        // native-advance poll runs on its own lane thread (see CmmScheduler's lane contract).
        // On write failure we re-flag dirty for a later retry.
        internal static void LoudnessSaveTick()
        {
            if (!LoudnessStore.Dirty) return;

            LoudnessStore.SaveSnapshot snap = null;
            MainThreadDispatcher.Send(() => snap = LoudnessStore.SnapshotForSave());
            if (snap == null) return;

            if (!LoudnessStore.WriteSnapshot(snap)) LoudnessStore.MarkDirty();
        }
    }
}
