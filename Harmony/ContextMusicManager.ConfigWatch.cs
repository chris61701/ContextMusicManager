using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // CONFIG HOT-RELOAD (settings/contexts/script watch; split from coordinator)
    // ─────────────────────────────────────────────────────────────────────────

    public partial class ContextMusicManager
    {
        // ── Config hot-reload ─────────────────────────────────────────────────
        private IEnumerator ConfigWatchLoop()
        {
            string settingsPath = CMMConfigLoader.SettingsFile(ModPath);
            string contextsPath = CMMConfigLoader.ContextsFile(ModPath);
            string radioPath    = CMMConfigLoader.RadioSettingsFile(ModPath);   // same reload as settings

            // Snapshot timestamps right after init so the first poll doesn't re-fire.
            DateTime settingsTime = File.Exists(settingsPath) ? File.GetLastWriteTimeUtc(settingsPath) : DateTime.MinValue;
            DateTime contextsTime = File.Exists(contextsPath) ? File.GetLastWriteTimeUtc(contextsPath) : DateTime.MinValue;
            DateTime radioTime    = File.Exists(radioPath)    ? File.GetLastWriteTimeUtc(radioPath)    : DateTime.MinValue;

            while (true)
            {
                yield return new WaitForSeconds(2f);
                if (_functionallyDormant) continue;   // A/B dormant arm

                bool settingsChanged = false, contextsChanged = false;
                DateTime newSettingsTime = settingsTime, newContextsTime = contextsTime, newRadioTime = radioTime;
                bool done = false;

                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (File.Exists(settingsPath))
                        {
                            var t = File.GetLastWriteTimeUtc(settingsPath);
                            if (t != settingsTime) { settingsChanged = true; newSettingsTime = t; }
                        }
                        if (File.Exists(radioPath))
                        {
                            var t = File.GetLastWriteTimeUtc(radioPath);
                            if (t != radioTime) { settingsChanged = true; newRadioTime = t; }
                        }
                        if (File.Exists(contextsPath))
                        {
                            var t = File.GetLastWriteTimeUtc(contextsPath);
                            if (t != contextsTime) { contextsChanged = true; newContextsTime = t; }
                        }
                    }
                    catch { }
                    done = true;
                });
                while (!done) yield return null;

                // Hot-reload the Lua decision script on its own write-time change.
                // Cheap stamp check on the main thread; only re-parses when it changed.
                ScriptHost.CheckReload();
                radioTime = newRadioTime;
                if (settingsChanged) settingsTime = newSettingsTime;
                if (contextsChanged) contextsTime = newContextsTime;
                if (settingsChanged || contextsChanged)
                    yield return StartCoroutine(ApplyConfigReload(settingsChanged, contextsChanged));
            }
        }

        // Shared config-reload body (settings + contexts), MAIN-THREAD only: it reloads
        // settings, swaps the contexts dict and re-triggers playback — all touching the
        // lock-free contexts/cacheMap/_contextStack/currentContext. Started by the
        // ConfigWatchLoop coroutine (the NativeScheduler-off fallback) or, off the frame
        // loop, by ConfigWatchTick → MainThreadDispatcher.Post when the scheduler-thread
        // stamp probe sees a change. The contexts-changed branch keeps its own off-thread
        // folder enumeration (yield-driven), so this stays a coroutine.
        private IEnumerator ApplyConfigReload(bool settingsChanged, bool contextsChanged)
        {
            if (settingsChanged)
            {
                CMMConfig.D("[CMM] settings changed — reloading.");
                CMMConfigLoader.LoadSettings(ModPath);
                // Re-read Sounds/ too: a swapped click file is heard without a restart, same
                // contract as every other tunable in there.
                // The click SOUNDS are read once and held in RAM, deliberately not swappable at
                // runtime. The knobs around them ARE live, so re-push those.
                PersonalRadio.LoadClickSamples();
                ScriptHost.MarkConfigDirty();   // re-marshal cfg into Lua on the next scan tick
            }

            if (contextsChanged)
            {
                CMMConfig.D("[CMM] Config/contexts.xml changed — reloading.");

                // Snapshot each context's source (folder + recursion) BEFORE the
                // reload swaps the dict, so we can tell which contexts had their
                // Path repointed. A surviving context whose folder moved keeps the
                // same name, so none of the add/remove handling below fires for it.
                var oldSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in contexts)
                    oldSources[kv.Key] = $"{kv.Value?.Source}|{kv.Value?.IncludeSubfolders}";

                CMMConfigLoader.LoadContexts(ModPath);

                // Ensure new contexts have cache entries.
                foreach (var ctx in contexts.Values)
                    if (!cacheMap.ContainsKey(ctx.Context))
                        cacheMap[ctx.Context] = new ContextCache();

                // Drop stack entries for contexts that no longer exist.
                _contextStack.RemoveAll(c => !contexts.ContainsKey(c.ContextName));

                // If the playing context was removed, fade to silence.
                if (currentContext != null && !contexts.ContainsKey(currentContext.Context))
                {
                    _pendingContext = null;
                    currentContext = GetContext("Silence");
                    ++_transitionId;
                    FadeToSilence();
                }

                // A context whose folder (or recursion flag) was repointed still
                // carries a snapshot + lastPlayedFile + (if it's the one playing) a
                // live clip from its OLD directory. Forget that stale state so the new
                // folder is used from here on, and collect the repointed contexts so we
                // can refresh their presence and recover playback below.
                string activeName = currentContext?.Context;
                var repointed = new List<PresenceScanItem>();
                foreach (var ctx in contexts.Values)
                {
                    oldSources.TryGetValue(ctx.Context, out string oldSrc);
                    string newSrc = $"{ctx.Source}|{ctx.IncludeSubfolders}";
                    if (string.Equals(oldSrc, newSrc, StringComparison.OrdinalIgnoreCase)) continue;
                    if (cacheMap.TryGetValue(ctx.Context, out var movedCache))
                    {
                        movedCache.directorySnapshot = null;   // force fresh enumeration of the new source
                        movedCache.lastPlayedFile = null;      // old source's track no longer belongs here
                        movedCache.resumePosition = 0f;
                        movedCache.lastLeftTime = -1f;
                    }
                    // Covers a flip in EITHER direction (folder→playlist, playlist→folder,
                    // or a change of playlist file / folder); MakeScanItem branches on the
                    // new source kind.
                    if (!string.IsNullOrEmpty(ctx.Source))
                        repointed.Add(MakeScanItem(ctx));
                }

                if (repointed.Count > 0)
                {
                    // Refresh presence + snapshot for the repointed contexts NOW
                    // (enumerate the NEW folders off-thread, apply on the main thread)
                    // rather than leaving it to PresenceScanLoop. That loop runs far more
                    // often than FileWatchLoop, so it would refill the snapshot from the
                    // new folder before either the recovery decision here or FileWatchLoop
                    // could act on the change — stranding the swap on the next end-of-track
                    // pick. Doing it synchronously makes the decision below deterministic.
                    var results = new Dictionary<string, ScanResult>(StringComparer.OrdinalIgnoreCase);
                    bool scanDone = false;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        foreach (var item in repointed)
                            results[item.Name] = EnumerateScanItem(item);
                        scanDone = true;
                    });
                    while (!scanDone) yield return null;

                    foreach (var kv in results)
                    {
                        if (cacheMap.TryGetValue(kv.Key, out var c))
                        {
                            c.directorySnapshot = kv.Value.Files;
                            c.playlistFiles = kv.Value.PlaylistFiles;
                        }
                        bool has = kv.Value.Files.Any(f => !_invalidFiles.Contains(f));
                        if (has) _contextsWithFiles.Add(kv.Key);
                        else _contextsWithFiles.Remove(kv.Key);
                    }

                    // ── Recover playback deterministically ───────────────────────
                    // Neither ContextScanLoop nor FileWatchLoop reliably reacts here:
                    // the playing context keeps the same name (so no add/remove fires),
                    // and once a missing folder has dropped us to Vanilla, currentContext
                    // is "Vanilla" — so the FileWatchLoop active-context reaction never
                    // matches it either. Handle both cases inline.
                    bool activeRepointed = activeName != null && results.ContainsKey(activeName)
                                           && cacheMap.ContainsKey(activeName);

                    if (activeRepointed)
                    {
                        // The context that's playing was repointed to a new folder: stop the
                        // orphaned buffered voice and re-trigger so the native mixer picks a
                        // track from the new folder (mirrors FileWatchLoop's lastPlayedMissing).
                        var activeCache = cacheMap[activeName];
                        CMMConfig.D($"[CMM] Active context {activeName} repointed — hot-swapping to new folder.");
                        activeCache.lastPlayedFile = null;
                        activeCache.stagedFile = null;   // drop any pin from the old folder
                        _pendingContext = null;

                        if (ContextHasFiles(activeName))
                        {
                            StopNativeIfActive(0.25f);
                            currentContext = GetContext(activeName);
                            TriggerMusic(activeName);
                        }
                        else if (inGame)
                            SendContextUpdate(GetTopContext() ?? "Ambient", 0);  // new folder empty — fall back
                        else
                            SendContextUpdate(activeName);                        // menu — re-resolve its chain
                    }
                    else if (currentContext == null
                             || currentContext.Context == "Vanilla"
                             || currentContext.Context == "Silence")
                    {
                        // We're on a fallback (no custom music) because the active context
                        // had no files; a repointed context has now come back with tracks.
                        // Re-assert the top context so it reclaims playback from vanilla.
                        if (inGame)
                        {
                            string top = GetTopContext();
                            if (top != null && (currentContext == null || top != currentContext.Context))
                            {
                                CMMConfig.D($"[CMM] Repointed folder repopulated — reclaiming with {top}.");
                                _pendingContext = null;
                                SendContextUpdate(top, 0);
                            }
                        }
                        else if (results.ContainsKey("Menu"))
                        {
                            CMMConfig.D("[CMM] Menu folder repointed — re-triggering menu music.");
                            SendContextUpdate("Menu");
                        }
                    }
                }

                // Re-evaluate in case priorities or fallbacks changed.
                _contextsWithFilesDirty = true;
                _previousPlayableContexts.Clear();
            }
        }

        // ── Off-frame-loop config watch (NativeScheduler) ─────────────────────
        // The cheap stamp probe runs on the scheduler thread; only an ACTUAL change marshals
        // the reload back to the main thread. So a settings/contexts/script edit is picked up
        // even while Unity's main thread is frozen (world load/unload), and there is no per-
        // frame busy-poll. Mirrors ConfigWatchLoop, which remains the flag-off fallback.
        // radio.xml is watched alongside settings.xml and reloads through the SAME path: the
        // settings load reads both files as one chain, so either changing means "settings changed".
        // Splitting the file would otherwise have quietly cost hot-reload for two thirds of the
        // settings, which is exactly the sort of thing nobody notices until they are mid-tuning.
        private static string   _cfgSettingsPath, _cfgContextsPath, _cfgRadioPath;
        private static DateTime _cfgSettingsTime, _cfgContextsTime, _cfgRadioTime;

        private static DateTime StampOf(string p) =>
            !string.IsNullOrEmpty(p) && File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue;

        internal static void ConfigWatchInit()
        {
            _cfgSettingsPath = CMMConfigLoader.SettingsFile(ModPath);
            _cfgContextsPath = CMMConfigLoader.ContextsFile(ModPath);
            _cfgRadioPath    = CMMConfigLoader.RadioSettingsFile(ModPath);
            // Snapshot timestamps right after init so the first poll doesn't re-fire.
            _cfgSettingsTime = StampOf(_cfgSettingsPath);
            _cfgContextsTime = StampOf(_cfgContextsPath);
            _cfgRadioTime    = StampOf(_cfgRadioPath);
        }

        // OUR OWN WRITE, not a hand edit. The keybind-persisted settings (CombatMusicMuted)
        // go through PersistSetting, which calls this straight after saving: re-stamping means
        // the next poll sees no change and skips a full settings reload that could only ever
        // read back the value we just wrote. Harmless if it is missed — the reload is
        // idempotent — which is why the older coroutine fallback is left alone.
        internal static void NoteConfigWritten()
        {
            // BOTH files: CMMSettingsFile writes radio.xml as well (create / backfill /
            // migrate), and settings and radio share one "settings changed" flag, so
            // re-stamping only settings.xml would leave our own radio write looking like
            // a hand edit and trigger a pointless full reload a moment later.
            try { _cfgSettingsTime = StampOf(_cfgSettingsPath); } catch { }
            try { _cfgRadioTime    = StampOf(_cfgRadioPath);    } catch { }
        }

        // Same, for CMMSettingsFile's sibling that creates / tops up contexts.xml.
        // Kept separate because a contexts change is a much heavier reload than a
        // settings one, so it is worth being precise about which file we just wrote.
        internal static void NoteContextsWritten()
        {
            try { _cfgContextsTime = StampOf(_cfgContextsPath); } catch { }
        }

        internal static void ConfigWatchTick()
        {
            if (_functionallyDormant) return;   // A/B dormant arm
            bool settingsChanged = false, contextsChanged = false;
            DateTime ns = _cfgSettingsTime, nc = _cfgContextsTime, nr = _cfgRadioTime;
            try
            {
                if (File.Exists(_cfgSettingsPath))
                {
                    var t = File.GetLastWriteTimeUtc(_cfgSettingsPath);
                    if (t != _cfgSettingsTime) { settingsChanged = true; ns = t; }
                }
                if (File.Exists(_cfgRadioPath))
                {
                    var t = File.GetLastWriteTimeUtc(_cfgRadioPath);
                    if (t != _cfgRadioTime) { settingsChanged = true; nr = t; }
                }
                if (File.Exists(_cfgContextsPath))
                {
                    var t = File.GetLastWriteTimeUtc(_cfgContextsPath);
                    if (t != _cfgContextsTime) { contextsChanged = true; nc = t; }
                }
            }
            catch { /* transient IO during an external save — retry next tick */ }
            _cfgSettingsTime = ns; _cfgContextsTime = nc; _cfgRadioTime = nr;

            bool scriptChanged = ScriptHost.ScriptChangedOnDisk();
            if (!settingsChanged && !contextsChanged && !scriptChanged) return;   // nothing changed — no main hop

            MainThreadDispatcher.Post(() =>
            {
                if (scriptChanged) ScriptHost.CheckReload();
                if (settingsChanged || contextsChanged)
                    CMM_CoRoutines.StartCoroutine(Instance.ApplyConfigReload(settingsChanged, contextsChanged));
            });
        }

    }
}
