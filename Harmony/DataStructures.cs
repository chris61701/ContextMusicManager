
using System;
using System.Collections.Generic;
using UnityEngine;

namespace CMM
{
    public enum TrackOrderMode { Random, RandomNoRepeat, Sequential }

    public class ContextCache
    {
        public float resumePosition;
        public float lastLeftTime = -1f;
        // The file most recently SELECTED for this context. Advances to the next track as
        // soon as the auto-advance picks it (≈end of the current track, or immediately on a
        // skip) — so it is NOT necessarily the audible file. Used for next-track avoidance
        // and loudness measurement.
        public string lastPlayedFile;
        // The file the native mixer is actually VOICING right now (set when StartNativeContext
        // dispatches it). Volume/normalization keys off this, not lastPlayedFile, so the
        // upcoming track's gain is never applied to the track still playing.
        public string playingFile;
#if CMM_DEBUG_HUD
        // Diagnostic (debug HUD only): was playingFile already loudness-measured at the instant
        // it started? true = it began pre-normalized; false = it started at unity gain and had
        // to be measured on the fly (its real gain settled in a beat later). Captured at start
        // so the HUD reflects how the track BEGAN, not whether it has since been measured.
        public bool playingFilePreMeasured;
#endif
        // ── Per-context play history ───────────────────────────────────────────
        // The tracks that actually played in this context, in order, with a cursor at
        // the playing one — drives the Prev/Next keybinds like a music player's
        // back/forward. Session-only, capped at MaxPlayHistory (oldest dropped).
        // Every track start commits here (CommitToHistory); the commit itself
        // recognizes replays by comparing against the cursor's neighbours, so the
        // navigation code never has to move the cursor.
        public const int MaxPlayHistory = 25;
        public readonly List<string> playHistory = new List<string>();
        public int historyIndex = -1;   // cursor: index of the playing track, -1 = empty

        // ── Anti-repeat rotation ───────────────────────────────────────────────
        // What this context has played THIS CYCLE, so every track gets a turn before
        // any track gets a second one. Driven from CommitToHistory below rather than
        // from the pick, because picking is not playing — see TrackRotation's header.
        internal readonly TrackRotation rotation = new TrackRotation();

        // Record `file` as the track now playing. A forward/backward REPLAY (the file
        // is the cursor's neighbour — a Prev/Next keypress or a mid-history
        // auto-advance) just moves the cursor; landing on the cursor itself (context
        // resume / convergence re-assert) is a no-op. A genuinely new track drops any
        // forward (rewound-past) entries and appends, like a browser history.
        //
        // `noRepeatMemory` is CMMConfig.TrackNoRepeatMemory, passed in rather than read
        // here so this stays a plain data structure the offline harnesses can link and
        // drive directly. Only the APPEND path commits to the rotation: a resume or a
        // Prev/Next replay is the user navigating what already played, and must not
        // spend a fresh slot in the bag.
        public void CommitToHistory(string file, int noRepeatMemory = TrackRotation.WholePool)
        {
            if (string.IsNullOrEmpty(file)) return;
            if (HistoryAt(historyIndex, file)) return;
            if (HistoryAt(historyIndex + 1, file)) { historyIndex++; return; }
            if (HistoryAt(historyIndex - 1, file)) { historyIndex--; return; }
            if (historyIndex >= 0 && historyIndex < playHistory.Count - 1)
                playHistory.RemoveRange(historyIndex + 1, playHistory.Count - historyIndex - 1);
            playHistory.Add(file);
            if (playHistory.Count > MaxPlayHistory) playHistory.RemoveAt(0);
            historyIndex = playHistory.Count - 1;
            rotation.Commit(file, noRepeatMemory);
        }

        // The nearest entry BEHIND the cursor that still exists on disk, or null at the
        // start of history. Dead entries are pruned as they're found so the cursor's
        // path stays contiguous (CommitToHistory recognizes only direct neighbours).
        // Peek-only: the cursor moves when the replayed track actually commits.
        public string HistoryBack()
        {
            while (historyIndex > 0)
            {
                string f = playHistory[historyIndex - 1];
                if (System.IO.File.Exists(f)) return f;
                playHistory.RemoveAt(historyIndex - 1);
                historyIndex--;
            }
            return null;
        }

        // Forward twin of HistoryBack: the nearest surviving entry AHEAD of the cursor
        // (non-null only after rewinding), pruning dead ones. Peek-only.
        public string HistoryForward()
        {
            while (historyIndex >= 0 && historyIndex < playHistory.Count - 1)
            {
                string f = playHistory[historyIndex + 1];
                if (System.IO.File.Exists(f)) return f;
                playHistory.RemoveAt(historyIndex + 1);
            }
            return null;
        }

        private bool HistoryAt(int i, string file) =>
            i >= 0 && i < playHistory.Count
            && string.Equals(playHistory[i], file, StringComparison.OrdinalIgnoreCase);
        // The single track pinned to play the next time this context starts FRESH (a
        // switch-in, not a resume). It is the ONLY track pre-measured for an inactive
        // context, and StartNativeContext consumes it so the track we start is exactly
        // the one we normalized — no just-in-time loudness stall. null when not yet
        // staged; kept measured by the loudness keep-set. See StageNextFile / PinStagedFile.
        public string stagedFile;
        public int sequentialIndex;
        public string[] directorySnapshot;
        // Memoized "is every track in the snapshot banned?" evaluation, re-run only
        // when a ban toggles (LoudnessStore.BanRevision) or the snapshot is replaced
        // — ContextHasFiles runs every scan tick, so the O(files) sweep can't live
        // there unmemoized. See ContextMusicManager.AllTracksBanned.
        public int banEvalRevision = -1;
        public string[] banEvalSnapshot;
        public bool allTracksBanned;
        // For a FLAT FOLDER source: the top-level .m3u/.m3u8 files discovered in the folder
        // at the last scan. The file-watch change signature folds these files' own mtimes in
        // (a folder's mtime doesn't change when a contained .m3u is edited in place), so
        // editing an in-folder playlist hot-reloads. Empty for direct-.m3u and recursive
        // sources (a direct playlist uses its own mtime; recursion ignores playlists).
        public string[] playlistFiles;
    }

    public class ActiveContext
    {
        public string ContextName;
        public int Priority;
        public float ActivatedAt;
        public bool Manual;
        // When this entry first entered the stack. Unlike ActivatedAt (refreshed
        // every scan while the context stays active), this is set once on insertion
        // and drives the WarmUp dwell gate. A context that leaves and re-enters gets
        // a fresh ActiveContext, so the dwell restarts from zero.
        public float FirstSeenAt;
    }

    public class Contexts
    {
        public string Context { get; private set; }
        // The music SOURCE for this context. One of:
        //   • a folder path relative to MusicDirectory (the historical default),
        //   • an absolute folder path, or
        //   • a playlist file (.m3u / .m3u8), relative to MusicDirectory or absolute.
        // Empty = sentinel (Vanilla / Silence), no audio. Formerly named Path; renamed
        // because it may now point at a playlist file rather than a folder.
        public string Source { get; private set; }
        public string Fallback { get; private set; }

        // True when Source names a playlist file (.m3u / .m3u8) rather than a folder.
        // Playlists are a flat pool of the tracks listed inside; folder-only concepts
        // (IncludeSubfolders, the recursive crawler, subtree claiming) don't apply.
        public bool IsPlaylist => IsPlaylistSource(Source);

        // A Source is a playlist when it points at a .m3u/.m3u8 file. Folder sources
        // never carry that extension, so the distinction is unambiguous without disk I/O.
        public static bool IsPlaylistSource(string source) =>
            !string.IsNullOrEmpty(source) &&
            (source.EndsWith(".m3u",  StringComparison.OrdinalIgnoreCase) ||
             source.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
        public bool CanBeResumed { get; private set; }
        public int TimeUntilContextStale { get; private set; }
        public bool Loop { get; private set; }
        public int Cooldown { get; private set; }
        public float FadeInDuration { get; private set; }
        public bool BypassesCooldown { get; private set; }
        public int Priority { get; private set; }
        public bool PlayOnce { get; private set; }
        // Per-context replacement for the global VolumeModifier. Negative means
        // "unset" — fall back to CMMConfig.VolumeModifier.
        public float VolumeOverride { get; private set; }
        // Seconds this context must remain continuously active before it becomes
        // eligible to trigger music. 0 = trigger immediately. Acts as an anti-flicker
        // dwell so momentary contexts don't interrupt what's already playing.
        public int WarmUp { get; private set; }
        // Optional warmup-group tag. Contexts sharing a non-empty WarmGroup carry the
        // dwell across a handoff: when one joins the stack while a sibling is still
        // present, it inherits the group's oldest FirstSeenAt instead of restarting
        // its WarmUp from zero. Null/empty = not grouped (the default). See
        // ContextMusicManager.InheritedFirstSeen.
        public string WarmGroup { get; private set; }
        // When true, this context's music pool includes tracks found in subfolders
        // of its Path (recursive enumeration), not just files sitting directly in
        // the folder. Default false = the historical flat, one-level behavior.
        // Tracks living under a more-specific child context's folder are claimed by
        // that child and excluded here, so each file belongs to exactly one context
        // (see ContextMusicManager.GetClaimedSubtreePrefixes).
        public bool IncludeSubfolders { get; private set; }
        // Optional name of the vanilla DMS SectionType this context governs (e.g.
        // "TraderRekt", "Exploration"). When the game's Dynamic Music System wants to
        // play that section (while CMM is on the Vanilla fallback), it must obey THIS
        // context's rules — chiefly its WarmUp dwell. Stored as the raw string; parsed
        // to a game SectionType in the reverse map (ContextMusicManager._sectionToContext)
        // so this data structure stays free of any game-assembly enum dependency. Null =
        // this context governs no vanilla section (the default).
        public string Link { get; private set; }

        // NOTE: every call site (InitContexts, CMMConfigLoader, the tools) passes each
        // argument explicitly, so these parameter defaults are inert — the effective
        // defaults live in InitContexts' Add helper and the contexts.xml attribute
        // readers, which must be kept in sync with each other.
        public Contexts(string context = "None", string source = "", string fallback = "None",
            bool canberesumed = false, int timeuntilcontextstale = 120,
            bool loop = false, int cooldown = 5, float fadeInDuration = 3f,
            bool bypassescooldown = false,
            int priority = 0, bool playOnce = false, float volumeOverride = -1f,
            int warmup = 0, string warmGroup = null, bool includeSubfolders = false,
            string link = null)
        {
            Context = context;
            Source = source;
            Fallback = fallback;
            CanBeResumed = canberesumed;
            TimeUntilContextStale = timeuntilcontextstale;
            Loop = loop;
            Cooldown = cooldown;
            FadeInDuration = fadeInDuration;
            BypassesCooldown = bypassescooldown;
            Priority = priority;
            PlayOnce = playOnce;
            VolumeOverride = volumeOverride;
            WarmUp = warmup;
            WarmGroup = warmGroup;
            IncludeSubfolders = includeSubfolders;
            Link = link;
        }
    }
}
