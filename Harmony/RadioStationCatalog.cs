using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // RADIO STATION CATALOG  (version-neutral: every supported game version)
    // ─────────────────────────────────────────────────────────────────────────
    // The shared station model behind BOTH radio features:
    //   - the world-emitter layer (EmitterAudioLayer, 3.0-only) — diegetic speakers/radios
    //   - the player's PERSONAL radio (PersonalRadio, all versions) — hotkey-tuned, main bus
    //
    //   STATIONS — every SUBFOLDER of the Radio source is one station (the folder name is the
    //   station name); loose tracks in the source root are a FALLBACK single station used only
    //   when no subfolder yields a playable pool (so a flat folder keeps its old behaviour).
    //   Station folders honour .m3u/.m3u8 playlists (expanded; entries may point anywhere).
    //
    //   SCHEDULE — ANCHORED GAPLESS WINDOWS on a world-time clock: broadcast time divides into
    //   RadioAnchorSeconds windows, and each window k re-anchors the station's chain at a
    //   deterministic index of its shuffled Order; within the window, tracks play back-to-back
    //   gapless. Resolving "what's on at B" walks track lengths forward from the current anchor —
    //   ~window/4min header opens, INDEPENDENT of pool size, probed once per station+window on a
    //   ThreadPool worker and session-cached — so stations are ready instantly at any size (no
    //   upfront scan). Every session, radio and MP client computes the same song at the same
    //   offset (anchor = pure clock math; lengths come from file headers, identical everywhere).
    //   Boundary policy = SOFT HANDOFF: a live voice ignores the window boundary and finishes its
    //   song; its EOF re-resolves against the (new window's) paper schedule and joins mid-track —
    //   no song is ever cut, and cross-boundary divergence self-heals at EOF (the same tolerance
    //   the pause-drift guard already accepts). While a voice is live, decoder EOF is authoritative.
    //
    // This file was factored OUT of the GV_V3 EmitterAudioLayer so the personal radio works on
    // A20/A21/2.6 too: everything here binds only version-neutral surfaces (IO, decoder, mixer,
    // UnityEngine, and GamePrefs via GamePrefsCompat). Main-thread only (decoder opens).
    // Why a resolve found nothing on air. The caller used to get a bare `false` for all of
    // these and reported every one as "none decodable" -- which is true of exactly one. A log
    // line telling you to check the file format when the real answer is "the background chain
    // walk has not finished yet, ask again in a second" sends you hunting a problem that is not
    // there, and it fired in a normal session (14:35 log: the warning at 142.4 s, the same
    // station playing happily at 144.2 s).
    internal enum ResolveMiss
    {
        None,            // resolved: something is on air
        NotReady,        // no pool yet (catalog still enumerating, or the folder is empty)
        WalkPending,     // lengths not cached yet; the background walk is running -- TRANSIENT
        ChainTooLong,    // hit WalkCap before covering the pool; inconclusive, not a verdict
        NoUsableTracks,  // walked the WHOLE pool and every track was dead -- the real complaint
    }

    internal sealed class RadioStation
    {
        // Max speaker positions fed to one emitter voice (matches native CMM_MAX_EMIT_PTS).
        internal const int MaxPoints = 8;

        public string Name;              // subfolder name (or the root/playlist for the fallback)
        public string[] Order;           // deterministic broadcast order (sorted, then seeded shuffle)
        public uint Seed;
        // Last time we said out loud that this station has nothing playable. A station
        // whose whole pool fails to decode is retried at emitter TICK rate, so the
        // complaint arrived three or four times a second -- ~700 lines in one session
        // from a single mislabelled file. Throttled per station; the condition is real
        // and worth reporting, just not that often.
        // Playlist entries named but unusable (missing, or an undecodable format). Surfaced by the
        // coverage panel: a station short of half its music used to look identical to one that was
        // simply small, and nothing in the log distinguished them either.
        public int DroppedEntries;
        public ResolveMiss LastMiss = ResolveMiss.None;   // why the last resolve found nothing
        public float NoTracksLoggedAt = -999f;                // FNV-1a of the lowercased name (PRE-shuffle) — anchor mixing
        public bool Ready => Order != null && Order.Length > 0;   // instant — no probing gate

        // Background chain-walk state: which anchor window's chain the worker has covered (or is
        // covering). Worker writes WalkWindow then clears WalkPending; a duplicate walk kicked off
        // a stale read is harmless (it re-hits the length cache).
        public long WalkWindow = long.MinValue;
        public volatile bool WalkPending;

        // ── Fields below are OWNED BY THE GV_V3 EMITTER CONSUMER (EmitterAudioLayer) ──
        // They live here because their types are version-neutral and keeping them on the station
        // avoids a parallel per-station map; on non-3.0 games they simply stay at their defaults.
        // (CS0649 disabled: builds WITHOUT the emitter layer have no writer for them — by design.)
#pragma warning disable 0649

        // Live emitter voice (0/-1 = not voiced). The personal radio keeps its OWN voice fields.
        public long VoiceId;
        public string VoiceTrack;
        public float VoiceGainApplied = 1f;
        public int Slot = -1;

        // Per-tick registry grouping (rebuilt each emitter decision tick).
        public float NearestDist = float.MaxValue;   // over ALL tracked speakers of this station
        public Vector3i NearestPos;
        public int NearestFlags;                     // render flags of that nearest speaker (bit0 = ham)
        public float LastSeenAt = -999f;             // last tick this station had ANY tracked speaker
        public int PtCount;                          // culled in-Range points below
        public readonly float[] Pts    = new float[MaxPoints * 3];
        public readonly float[] PtGain = new float[MaxPoints];
        public readonly float[] PtDist = new float[MaxPoints];
        public readonly int[]   PtFlags = new int[MaxPoints];   // per-point render flags (bit0 = ham class)

        // The most recent landed point push (count + when): a track ADVANCE retires and re-adds
        // the native voice; seeding the new voice from this (same speakers, same ear, ≤ a tick
        // old) keeps its occlusion continuous instead of bursting unoccluded until the next flood.
        public int LastPtPushCount;
        public float LastPtPushAt = -999f;

        // Per-station acoustics captured from the last occlusion pass (drive its carry send).
        public float Seal = 1f;      // 1 = fully solid-sealed from every speaker (carry hard-gated)
        public float Solid;          // solid material on the least-blocked route (proportional damp)
        public float Depth = 512f;   // air-route blocks to the closest-by-air speaker

#pragma warning restore 0649

        // On-air resolution scratch (ResolveOnAir output; shared coarse state — the personal
        // radio resolves via the PURE ResolveAt so concurrent consumers can't fight over it).
        public string OnAir; public int OnAirIdx; public double OnAirOffset;
    }

    internal static class RadioStationCatalog
    {
        // Sorted by name (ordinal-ignore-case) — RebuildCatalog enumerates sorted subfolders.
        internal static readonly List<RadioStation> Stations = new List<RadioStation>();
        internal static readonly Dictionary<string, RadioStation> ByName =
            new Dictionary<string, RadioStation>(StringComparer.OrdinalIgnoreCase);

        // Catalog bookkeeping: the resolved source the catalog was built from + its disk signature,
        // re-checked on a modest cadence (a recursive signature walks every subdir — not at 4 Hz).
        // Forget everything the catalog watch remembers, so the next EnsureCatalog
        // rebuilds unconditionally. For `cmm scan`: the watch normally decides by
        // comparing a signature, and a manual command should not depend on that
        // comparison noticing -- a root added and another removed in the same breath
        // can leave the folder count and the newest timestamp both unchanged.
        // Deliberately does NOT rebuild here: RebuildCatalog migrates live native
        // voices, so it stays on its own main-thread tick where it always ran.
        internal static void ForceRebuild()
        {
            _catalogSrc = null;
            _catalogSig = DateTime.MinValue;
            _catalogHash = 0;
            _nextCatalogCheck = 0f;
        }

        private static string   _catalogSrc;
        private static DateTime _catalogSig;
        private static float    _nextCatalogCheck;
        private const  float    CatalogCheckInterval = 2f;   // matches the context file-watch cadence
        // Every .m3u discovered inside station subfolders at the last catalog build. Their mtimes are
        // folded into the catalog signature: a directory's mtime does NOT change when a contained
        // playlist is edited IN PLACE, so without this an in-folder playlist edit would go unnoticed
        // (the same guard the flat-context file watch has).
        private static string[] _catalogPlaylists = new string[0];
        private static readonly List<string> _plScratch = new List<string>();

        // Probed track lengths (header read), shared across stations and windows so nothing is ever
        // opened twice per session. Guarded by _lenLock: walks probe on a ThreadPool worker while
        // the main thread resolves. frames == 0 ⇒ known-dead file (open failed / poisoned) — walks
        // treat it as a zero-second skip, deterministically.
        // One cached track length, plus the stamp of the file it was measured FROM.
        //
        // The stamp is the whole point. Stations play on a shared clock: every client works out
        // what is on air by adding up track lengths from a fixed anchor, so agreement depends
        // entirely on everyone holding the same lengths. This cache used to be keyed by path
        // alone and never rechecked -- the comment said good lengths "do not go stale" -- so
        // replacing a file with a different-length version left the sum wrong from that track
        // onward: the station starts songs part way in, or cuts them off. And on a server, a
        // player who joined after the change measures the new length while one already playing
        // keeps the old, and the two of them hear different songs on the same station with
        // nothing to reconcile them.
        private struct LenEntry
        {
            public long Frames;
            public int  Rate;
            public long Size, Mtime;   // of the file this was measured from; 0/0 = unstamped
        }
        private static readonly Dictionary<string, LenEntry> _len = new Dictionary<string, LenEntry>();
        private static readonly object _lenLock = new object();

        // Hard bound on any chain walk (resolve or probe): a window is RadioAnchorSeconds long, so
        // sane pools finish in ~window/typical-track steps; the cap only bites pathological pools
        // (thousands of sub-second or dead files), which resolve as dead air.
        private const int WalkCap = 4096;

        // ── Broadcast clock ─────────────────────────────────────────────────────────────────────
        // worldTime is the shared EPOCH (every session/client of a save anchors to the same
        // broadcast position), but it is NOT a realtime-rate clock: measured in-game 2026-07-11
        // (sync audit, log 19-31-53) it advances at only ~0.89× realtime during normal play, while
        // the audio decoders run at exactly 1.0× — so a paper schedule read straight off worldTime
        // falls behind every live voice at ~0.11 s/s (6-7 s/minute). No re-glue cadence can chase
        // that, and consumers re-gluing at DIFFERENT moments (dial, EOF, unpause) was the audible
        // emitter-vs-personal desync.
        //
        // So the clock consumers actually resolve against is LOCAL: anchored to the worldTime-
        // derived position at world entry, advanced by REALTIME while playing (matching the
        // decoders — voices stay on paper indefinitely), FROZEN during the SP pause (worldTime
        // freezes too; voices decode on under the duck and the unpause ResyncToClock re-glues —
        // a pause still doesn't skip content), and carrying any genuine worldTime JUMP (admin
        // settime / server correction) so those still retune everyone identically. All consumers
        // read this one clock, so they agree with each other by construction; the worldTime
        // deficit only surfaces as a modest jump-back of the anchor on relog (some broadcast
        // replays — the save's own time authority wins across sessions).
        // Main-thread only (GameManager/Time), like every resolve path.
        private static bool   _clkInit;
        private static double _clkB;        // the local broadcast clock (seconds)
        private static float  _clkRt;       // realtime stamp of the last advance
        private static double _clkLastRaw;  // last worldTime-derived position (jump detection)
        private static bool   _clkWasPaused;// pause state at the START of the current interval

        internal static double BroadcastSeconds()
        {
            World w = GameManager.Instance?.World;
            if (w == null) { _clkInit = false; return 0; }

            int dnl = GamePrefsCompat.DayNightLengthMinutes();   // by-name: enum ordinal drifts cross-version
            double raw = w.worldTime * (dnl * 60.0 / 24000.0);
            float rt = Time.realtimeSinceStartup;
            bool pausedNow = GameManager.Instance?.IsPaused() ?? false;

            if (!_clkInit)
            {
                _clkInit = true;
                _clkB = raw; _clkRt = rt; _clkLastRaw = raw; _clkWasPaused = pausedNow;
                return _clkB;
            }

            double rawStep = raw - _clkLastRaw;
            _clkLastRaw = raw;
            float dt = rt - _clkRt;
            _clkRt = rt;

            if (Math.Abs(rawStep) > 60.0)
                _clkB += rawStep;   // worldTime JUMPED (settime / server catch-up): carry it verbatim
            else if (dt > 0f && !_clkWasPaused)
                _clkB += dt;        // normal play: realtime rate — glued to the decoders
            // Each interval is attributed to the pause state at its START, not its end: 3.0's
            // updatePauseState runs SaveWorld() synchronously on the PAUSE edge (~2.5 s main-
            // thread stall) BEFORE gamePaused flips, so the first call after the stall reads
            // paused — deciding by the current state discarded that whole stall even though the
            // mixer (own thread) played straight through it, bleeding ~a save-duration of lead
            // into every voice per ESC tap (measured: log 20-50-00). Start-state attribution
            // counts the stall and mis-books at most one call interval (~0.25 s) per edge.
            _clkWasPaused = pausedNow;

            return _clkB;
        }

        // ── Schedule (anchored gapless windows on the world-time clock) ─────────────────────────
        // Window k covers [k·A, (k+1)·A) broadcast seconds (A = RadioAnchorSeconds). At the window
        // start the station's chain re-anchors at Order[AnchorIndex(st,k)]; within the window,
        // tracks run back-to-back gapless. Resolution never needs more than the chain from the
        // anchor to "now" — bounded by the window, independent of pool size.
        internal static long WindowIndex(double B) => (long)(B / CMMConfig.RadioAnchorSeconds);

        // Deterministic per-station, per-window anchor: mix the station's name seed with the
        // window number (multiply/xor-shift avalanche) so consecutive windows land unrelated
        // rotation positions but every client computes the same one.
        private static int AnchorIndex(RadioStation st, long k) => AnchorIndex(st.Seed, k, st.Order.Length);
        private static int AnchorIndex(uint seed, long k, int n)
        {
            uint x = seed ^ (uint)k ^ (uint)((ulong)k >> 32);
            x *= 2654435761u; x ^= x >> 15;
            x *= 2246822519u; x ^= x >> 13;
            return (int)(x % (uint)n);
        }

        // What is this station broadcasting at world-broadcast-second B? Deterministic: everyone
        // with the same pool computes the same answer. PURE station-wise (no station state beyond
        // the walk kick) — the emitter layer and the personal radio can resolve concurrently.
        // CACHE-ONLY: walks cached lengths from the window's anchor; the first uncached length
        // kicks the background window walk and returns false — callers already treat false /
        // OnAir == null as "not yet", and the walk lands within ~a second.
        // Existing 4-out callers keep working; they do not care WHY, only whether.
        internal static bool ResolveAt(RadioStation st, double B, out string file, out int idx, out double offset)
            => ResolveAt(st, B, out file, out idx, out offset, out _);

        internal static bool ResolveAt(RadioStation st, double B, out string file, out int idx, out double offset,
                                       out ResolveMiss why)
        {
            file = null; idx = 0; offset = 0; why = ResolveMiss.None;
            if (!st.Ready) { why = ResolveMiss.NotReady; return false; }
            double A = CMMConfig.RadioAnchorSeconds;
            long k = WindowIndex(B);
            double rem = B - k * A;
            if (rem < 0) rem = 0;
            int n = st.Order.Length;
            int i = AnchorIndex(st, k);
            bool needWalk = false;
            lock (_lenLock)
            {
                for (int step = 0; step < WalkCap; step++)
                {
                    string f = st.Order[i];
                    if (!_len.TryGetValue(f, out LenEntry e)) { needWalk = true; break; }
                    // Cache-only, deliberately: no stat here. This walk runs under the lock,
                    // several times a second, once per track in the chain. Stamps are checked on
                    // the walk's cold path instead (ProbeLength), which re-probes every entry in
                    // the chain -- so a changed file is caught by the next window walk.
                    double len = (e.Frames > 0 && e.Rate > 0) ? (double)e.Frames / e.Rate : 0;   // 0 ⇒ dead file: skip, no airtime
                    if (len > 0)
                    {
                        if (rem < len) { file = f; idx = i; offset = rem; return true; }
                        rem -= len;
                    }
                    i = (i + 1) % n;
                }
            }
            if (needWalk) { EnsureWindowWalk(st, k); why = ResolveMiss.WalkPending; return false; }
            // Fell out of the loop with every visited entry dead. That is only a verdict on the
            // POOL if the walk actually covered it: with n > WalkCap we merely sampled a prefix.
            why = n <= WalkCap ? ResolveMiss.NoUsableTracks : ResolveMiss.ChainTooLong;
            return false;   // dead air
        }

        // Probe window k's chain on a ThreadPool worker: header-open each not-yet-cached file from
        // the anchor until the cached chain covers the whole window. ~window/4min opens at
        // ~10–25 ms each ⇒ a join costs a few hundred ms once per station+window; every later
        // resolve in the window is a cache-only dictionary walk. The job touches ONLY files and
        // the locked cache (no Unity APIs off a worker); config is captured on the caller's thread.
        // A catalog rebuild replaces station objects — an orphaned job still warms the shared
        // cache, and the replacement station's first resolve then succeeds without its own job.
        internal static void EnsureWindowWalk(RadioStation st, long k)
        {
            if (st.WalkPending || st.WalkWindow == k || !st.Ready) return;
            st.WalkPending = true;
            string[] order = st.Order;
            uint seed = st.Seed;
            double A = CMMConfig.RadioAnchorSeconds;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    int n = order.Length;
                    int i = AnchorIndex(seed, k, n);
                    double sum = 0;
                    for (int step = 0; step < WalkCap; step++)
                    {
                        sum += ProbeLength(order[i]);
                        if (sum >= A) break;
                        if (step >= n && sum <= 0) break;   // whole pool dead — stop probing
                        i = (i + 1) % n;
                    }
                    st.WalkWindow = k;   // reached only on a walk that RAN to completion
                }
                catch { /* threw: leave WalkWindow alone so the next resolve retries this window */ }
                finally { st.WalkPending = false; }
            });
        }

        // ── Sync audit (debug instrumentation) ──────────────────────────────────────────────────
        // One log line comparing a consumer's ACTUAL audible playhead (native voice position via
        // TryGetState — absolute seconds into the file, seek offset included) against the PAPER
        // schedule (ResolveAt at the same broadcast second). Both radio consumers call it on a
        // cadence plus one-shot at joins, so a desync session log answers directly: WHICH consumer
        // is off paper, by how much, and whether the delta is born at a join, grows at track
        // boundaries (the next-at-0 pause-drift guard), or jumps across an SP pause.
        //   Δ = voice − paper: positive = the voice runs AHEAD of the schedule (pause lead),
        //   negative = BEHIND (join/open latency). OFF-PAPER = playing a different track entirely.
        internal const float AuditInterval = 5f;
        internal static void AuditVoice(string consumer, RadioStation st, string voiceTrack, long voiceId)
        {
            if (!CMMConfig.DebugLogging || st == null || voiceId == 0) return;
            CmmMixer mx = CmmMixer.Instance;
            if (mx == null || !mx.TryGetState(voiceId, out double pos, out _, out _)) return;
            double B = BroadcastSeconds();
            string vn = voiceTrack != null ? Path.GetFileName(voiceTrack) : "?";
            if (!ResolveAt(st, B, out string pf, out _, out double poff))
            {
                Log.Out($"[CMM SYNC] {consumer} '{st.Name}': voice {vn} @{pos:F2}s | paper UNRESOLVED (B={B:F1})");
                return;
            }
            if (voiceTrack != null && string.Equals(pf, voiceTrack, StringComparison.OrdinalIgnoreCase))
                Log.Out($"[CMM SYNC] {consumer} '{st.Name}': {vn} voice@{pos:F2}s paper@{poff:F2}s Δ={pos - poff:+0.00;-0.00}s (B={B:F1})");
            else
                Log.Out($"[CMM SYNC] {consumer} '{st.Name}': OFF-PAPER voice {vn} @{pos:F2}s | paper {Path.GetFileName(pf)} @{poff:F2}s (B={B:F1})");
        }

        // Fills the station's shared OnAir/OnAirIdx/OnAirOffset scratch (emitter-layer consumer).
        internal static void ResolveOnAir(RadioStation st, double B)
        {
            if (ResolveAt(st, B, out string file, out int idx, out double off, out ResolveMiss why))
            { st.OnAir = file; st.OnAirIdx = idx; st.OnAirOffset = off; st.LastMiss = ResolveMiss.None; }
            else { st.OnAir = null; st.LastMiss = why; }
        }

        // A track ENDED (decoder EOF): re-sync to the schedule. Pause-drift guard: worldTime freezes
        // in the SP pause menu while the voice keeps playing, so EOF can land BEFORE the schedule
        // boundary — if the schedule still says the track that just ended, step to the next one at 0
        // instead of replaying its tail.
        internal static void AdvanceStation(RadioStation st, string endedTrack)
        {
            ResolveOnAir(st, BroadcastSeconds());
            if (st.OnAir != null && endedTrack != null
                && string.Equals(st.OnAir, endedTrack, StringComparison.OrdinalIgnoreCase))
            {
                st.OnAirIdx = (st.OnAirIdx + 1) % st.Order.Length;
                st.OnAir = st.Order[st.OnAirIdx];
                st.OnAirOffset = 0;
            }
        }

        // Measure a track's length in seconds (header open), session-cached; called on the walk
        // worker. The disk open runs OUTSIDE the lock so main-thread resolves never wait on IO.
        //
        // 0 ⇒ dead file, skipped deterministically everywhere. Caching that is DELIBERATE: the
        // broadcast schedule only works if every client computes the same chain, so a file one
        // client skips must be skipped by all of them. But it used to cache 0 for ANY open
        // failure, and that conflates two different things:
        //
        //   • the file is there and cannot be decoded — deterministic, every client agrees,
        //     cache it forever
        //   • the file could not be opened JUST NOW — a drive hiccup, a network share blinking.
        //     Client-specific and temporary; caching it is wrong
        //
        // Caching the second was a silent, permanent, whole-library failure. The cache has no
        // expiry and is never cleared, and the process outlives a world reload, so one blip during
        // one walk marked every file it touched as dead air for the rest of the RUN — the drive
        // came back and nothing recovered. 13 of the 16 stations here live on an external drive.
        //
        // So: cache 0 only when the file is genuinely reachable-and-unusable. If its ROOT is not
        // reachable, cache nothing and leave it unknown, so a later walk retries it.
        //
        // This is also where a cached length is REVALIDATED, because it is the cold path: the
        // window walk calls it for every file in the chain, cached or not, so one stat per track
        // per window boundary re-checks the whole schedule. ResolveAt deliberately does not,
        // being a lock-held cache-only walk that runs several times a second.
        private static double ProbeLength(string file)
        {
            FileStamp now = StampOf(file);
            lock (_lenLock)
                if (_len.TryGetValue(file, out LenEntry e))
                {
                    if (StampUnchanged(e, now))
                        return (e.Frames > 0 && e.Rate > 0) ? (double)e.Frames / e.Rate : 0;
                    // A different file wears this name now. Drop the length and measure again;
                    // the schedule is only as good as the numbers it adds up.
                    CMMConfig.D($"[CMM] Radio: {Path.GetFileName(file)} changed on disk — " +
                                "re-measuring its length (the schedule depends on it).");
                    _len.Remove(file);
                }
            long frames = 0; int rate = 0;
            IntPtr dec = AudioDecoder.OpenNativeHandle(file, out int kind, out _, out int r, out long total);
            if (dec != IntPtr.Zero) { frames = total; rate = r; AudioDecoder.CloseNativeHandle(dec, kind); }
            if (frames <= 0 && !RootReachable(file)) return 0;   // transient — do not poison
            // Stamp with what the file looked like when opened, NOT re-statted after: a file being
            // written while we read it would otherwise be recorded against a stamp it no longer
            // has, and the mismatch would never resolve.
            lock (_lenLock)
                _len[file] = new LenEntry { Frames = frames, Rate = rate,
                                            Size = now.Size, Mtime = now.Mtime };
            return (frames > 0 && rate > 0) ? (double)frames / rate : 0;
        }

        // What a file looks like right now. `Known` false means we could not tell — it is missing,
        // its drive is away, the read threw. That is NOT a change: treating a hiccup as one would
        // discard good lengths for a whole station at the exact moment none of them can be
        // re-probed, which is the failure ProbeLength's RootReachable guard already exists to
        // prevent. Same stance as the loudness store's stamp check.
        private struct FileStamp { public long Size, Mtime; public bool Known; }

        private static FileStamp StampOf(string file)
        {
            try
            {
                var fi = new FileInfo(file);
                if (!fi.Exists) return default(FileStamp);
                return new FileStamp { Size = fi.Length, Mtime = fi.LastWriteTimeUtc.Ticks, Known = true };
            }
            catch { return default(FileStamp); }
        }

        private static bool StampUnchanged(LenEntry e, FileStamp now)
        {
            if (!now.Known) return true;             // can't tell ⇒ keep what we have
            if (e.Size == 0 && e.Mtime == 0) return true;   // pre-stamp entry ⇒ nothing to compare
            return e.Size == now.Size && e.Mtime == now.Mtime;
        }

        // Is the volume this file lives on actually there? Distinguishes "undecodable" from
        // "unreachable". Deliberately NOT SafeExists, which reports an IO error as PRESENT — right
        // for keeping playback alive, exactly backwards for deciding what to cache.
        private static bool RootReachable(string file)
        {
            try
            {
                string root = Path.GetPathRoot(file);
                return string.IsNullOrEmpty(root) || Directory.Exists(root);
            }
            catch { return false; }   // malformed path ⇒ treat as unreachable, i.e. don't cache
        }

        // Drop the DEAD entries (length 0) so they are probed again. Good lengths are kept — they
        // are the expensive ones and they do not go stale. Called when the catalog is rebuilt,
        // which is the moment the pool has demonstrably changed, and on world entry.
        internal static void ForgetDeadLengths()
        {
            lock (_lenLock)
            {
                if (_len.Count == 0) return;
                _deadScratch.Clear();
                foreach (var kv in _len) if (kv.Value.Frames <= 0) _deadScratch.Add(kv.Key);
                foreach (string f in _deadScratch) _len.Remove(f);
                if (_deadScratch.Count > 0)
                    CMMConfig.D($"[CMM] Radio: re-probing {_deadScratch.Count} track(s) previously seen as dead.");
                _deadScratch.Clear();
            }
        }
        private static readonly List<string> _deadScratch = new List<string>();

        // A track failed to open AT PLAY time (vanished since its walk probe): mark it dead in the
        // length cache so every chain walk skips it — schedules shift identically for everyone who
        // re-resolves — until the ~2 s catalog watch rebuilds the pool properly.
        // Set by RebuildCatalog when at least one station could not be resolved because its files
        // are on an unavailable volume — as opposed to being genuinely empty. Cleared by the
        // rebuild that finally resolves everything.
        private static bool _catalogIncomplete;

        internal static void PoisonTrack(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            // Same guard, and for the same reason, as ProbeLength's. A play-time open failure
            // does not tell you WHY it failed, and the two causes want opposite responses: a
            // genuinely undecodable file should leave the schedule (poison it), while a drive
            // that is momentarily away should change nothing at all. Poisoning on the second
            // is how one blip took a whole station off the air for the rest of the process --
            // the emitter layer poisons up to five tracks per tick and the personal radio one
            // per 4 Hz tick, each immediately re-resolving onto the next track and poisoning
            // that too. Recovery needs ForgetDeadLengths, which needs a catalog rebuild, which
            // a returning drive does not trigger, because the station folder never changed.
            if (!RootReachable(file)) return;
            // Stat OUTSIDE the lock: _lenLock is held by ResolveAt's chain walk on the main
            // thread several times a second, and a blocking file stat inside it would park that
            // walk on IO -- on a removable drive, for as long as the drive takes to answer.
            FileStamp now = StampOf(file);
            lock (_lenLock)
                _len[file] = new LenEntry { Frames = 0, Rate = 0, Size = now.Size, Mtime = now.Mtime };
        }

        // File.Exists, treating a transient IO error as "present" so a hiccup doesn't needlessly
        // interrupt playback — same stance as the context watch's ping.
        internal static bool SafeExists(string file)
        {
            try { return File.Exists(file); } catch { return true; }
        }

        // ── Station catalog ─────────────────────────────────────────────────────────────────────
        // Stations = the SUBFOLDERS of the Radio source folder (station name = folder name), each
        // scanned recursively for supported audio; loose tracks in the root are the FALLBACK single
        // station used only when NO subfolder yields a playable pool (so a flat folder keeps its old
        // behaviour). An .m3u source is one station. Rebuilt on a source-string change and — hotswap
        // parity with contexts — when the folder contents change on disk (throttled signature stat;
        // an empty catalog always re-enumerates, the `previouslyEmpty` guard). Live emitter voices
        // survive a rebuild by station NAME; a renamed/removed station's voice is released.
        internal static void EnsureCatalog()
        {
            Contexts ctx = RadioContext();
            string src = ctx != null ? ctx.Source : CMMConfig.WorldEmitterSource;

            bool srcChanged = !string.Equals(_catalogSrc, src, StringComparison.OrdinalIgnoreCase);
            if (!srcChanged)
            {
                if (Time.realtimeSinceStartup < _nextCatalogCheck) return;
                _nextCatalogCheck = Time.realtimeSinceStartup + CatalogCheckInterval;
                DateTime sig = SourceSignature(src);
                long hash = ContentHash(src);
                // _catalogIncomplete forces a retry even when nothing about the SOURCE changed.
                // A station whose tracks live on a drive that was away resolves to no tracks, and
                // the signature that would normally prove "nothing changed" sits on a different
                // drive that never left — so without this the station stays missing for the rest
                // of the session and the drive returning does nothing. (Stations.Count > 0 already
                // covers the case where EVERY station failed; the hole was a partial failure.)
                if (Stations.Count > 0 && !_catalogIncomplete
                    && sig == _catalogSig && hash == _catalogHash) return;
                _catalogSig = sig;
                _catalogHash = hash;
            }
            else
            {
                _catalogSig = SourceSignature(src);
                _catalogHash = ContentHash(src);
                _nextCatalogCheck = Time.realtimeSinceStartup + CatalogCheckInterval;
            }
            _catalogSrc = src;
            RebuildCatalog(src);
        }

        // ── Sloppy-filesystem content signature ─────────────────────────────────────────────────
        // SourceSignature is a MAX of DIRECTORY write times — and on filesystems that don't bump a
        // folder's mtime when a child is added/removed/renamed (exFAT/FAT/removable/network — see
        // SourceScan.IsSloppyMtimeFilesystem) it never moves AT ALL, leaving the catalog blind to
        // station folders and tracks changing (found 2026-07-10: the user's radio library lives on
        // an exFAT drive; deleting a whole station changed nothing). On those filesystems only,
        // this folds in SourceScan.FileSetContentHash over the source tree: adds, removes and
        // renames all change it. Comparable work to the signature's existing recursive dir walk;
        // NTFS/ReFS keep the pure-stat path (returns 0 there).
        private static long _catalogHash;

        // The station parents: MusicPath's, then one per add-on mod supplying the same
        // relative folder. An ABSOLUTE or PLAYLIST source names exactly one place and is
        // never extended -- there is no relative path to append.
        private static string[] SourceDirs(string src)
        {
            if (string.IsNullOrWhiteSpace(src)) return MusicRoots.NoDirs;
            string ext = Path.GetExtension(src).ToLowerInvariant();
            if (ext == ".m3u" || ext == ".m3u8") return MusicRoots.NoDirs;
            if (Path.IsPathRooted(src)) return new[] { MusicRoots.Normalize(src) };

            MusicRoot[] roots = MusicRoots.All;
            var dirs = new string[roots.Length];
            for (int i = 0; i < roots.Length; i++) dirs[i] = MusicRoots.Combine(roots[i].Path, src);
            return dirs;
        }

        private static long ContentHash(string src)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(src)) return 0;
                string ext = Path.GetExtension(src).ToLowerInvariant();
                if (ext == ".m3u" || ext == ".m3u8") return 0;   // playlist source: its own mtime suffices
                // Every parent, and per-folder: a primary on NTFS with an add-on on exFAT
                // needs the listing for the add-on only, and gating on the primary alone
                // would leave a pack's stations permanently undetected.
                long h = 0;
                foreach (string dir in SourceDirs(src))
                {
                    if (string.IsNullOrEmpty(dir) || !SourceScan.IsSloppyMtimeFilesystem(dir)) continue;
                    if (!Directory.Exists(dir)) continue;
                    h = unchecked(h * 1099511628211L ^ SourceScan.FileSetContentHash(dir, includeSubfolders: true));
                }
                return h;
            }
            catch
            {
                // Transient IO mid-walk: report "no change" rather than flapping the catalog.
                return _catalogHash;
            }
        }

        private static void RebuildCatalog(string src)
        {
            // Give previously-dead tracks another chance. A rebuild means the pool demonstrably
            // changed, so this is the natural moment to re-probe anything a transient failure (or
            // a genuinely missing file that has since returned) left marked as zero-length.
            ForgetDeadLengths();

            // Snapshot the old stations so the emitter consumer can migrate live voices by name.
            var old = new Dictionary<string, RadioStation>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Stations.Count; i++) old[Stations[i].Name] = Stations[i];
            Stations.Clear();

            _plScratch.Clear();
            _catalogIncomplete = false;   // this rebuild's verdict, not the last one's
            try
            {
                if (!string.IsNullOrWhiteSpace(src))
                {
                    string ext = Path.GetExtension(src).ToLowerInvariant();
                    if (ext == ".m3u" || ext == ".m3u8")
                    {
                        string full = PlaylistSource.ResolveFullPath(src);
                        _plScratch.Add(full);
                        string[] plTracks = PlaylistSource.ReadTracks(full, out bool plUnresolved,
                                                                      out int plDropped);
                        if (plUnresolved) _catalogIncomplete = true;
                        AddStation(Path.GetFileNameWithoutExtension(src), plTracks, plDropped);
                    }
                    else
                    {
                        // Stations are gathered across EVERY parent first and MERGED by name,
                        // then added once each. Two packs shipping a "Country" folder make one
                        // Country station holding both pools -- adding twice would silently keep
                        // only the last, which is the worst kind of partial success.
                        // Order within a station stays deterministic: AddStation sorts and then
                        // shuffles from a seed derived from the station NAME, so the broadcast
                        // schedule still agrees across sessions and MP clients.
                        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        var order = new List<string>();
                        // Unusable playlist entries, accumulated per station NAME -- a station can
                        // be merged from a subfolder under more than one music root.
                        var droppedByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        string[] parents = SourceDirs(src);
                        foreach (string dir in parents)
                        {
                            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                            string[] subs;
                            try { subs = Directory.GetDirectories(dir); } catch { continue; }
                            Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
                            foreach (string sub in subs)
                            {
                                string[] pool = StationPool(sub, out int subDropped);
                                string name = Path.GetFileName(sub);
                                if (subDropped > 0)
                                {
                                    droppedByName.TryGetValue(name, out int had);
                                    droppedByName[name] = had + subDropped;
                                }
                                if (pool == null || pool.Length == 0) continue;
                                if (!byName.TryGetValue(name, out List<string> acc))
                                { byName[name] = acc = new List<string>(); order.Add(name); }
                                acc.AddRange(pool);
                            }
                        }
                        foreach (string name in order)
                        {
                            var seenT = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var merged = new List<string>();
                            foreach (string f in byName[name])
                                if (seenT.Add(SourceScan.FullKey(f))) merged.Add(f);
                            droppedByName.TryGetValue(name, out int nameDropped);
                            AddStation(name, merged.ToArray(), nameDropped);
                        }

                        if (Stations.Count == 0)
                        {
                            // Root fallback: no subfolder anywhere had a playable pool — the source
                            // folders themselves are one station (the pre-multi-station behaviour).
                            var seenR = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var rootPool = new List<string>();
                            string label = null;
                            foreach (string dir in parents)
                            {
                                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                                if (label == null) label = Path.GetFileName(dir.TrimEnd('\\', '/'));
                                string[] pool = SourceScan.EnumerateFolderPoolRaw(dir, false, claimed: new string[0], out string[] rootPls);
                                if (rootPls != null) _plScratch.AddRange(rootPls);
                                if (pool == null) continue;
                                foreach (string f in pool)
                                    if (seenR.Add(SourceScan.FullKey(f))) rootPool.Add(f);
                            }
                            if (rootPool.Count > 0) AddStation(label ?? "Radio", rootPool.ToArray());
                        }
                    }
                }
            }
            catch { /* transient IO — keep whatever we assembled; the throttle retries */ }
            _catalogPlaylists = _plScratch.ToArray();

            ByName.Clear();
            for (int i = 0; i < Stations.Count; i++) ByName[Stations[i].Name] = Stations[i];

#if GV_V3
            // Emitter-voice migration/release is the 3.0 consumer's business (slots, native emitter
            // voices). Direct gated call rather than an event: deterministic ordering, no
            // registration timing to get wrong.
            EmitterAudioLayer.OnCatalogRebuilt(old);
#endif

            if (Stations.Count > 0)
            {
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < Stations.Count; i++)
                {
                    if (i > 0) names.Append(", ");
                    names.Append(Stations[i].Name).Append('(').Append(Stations[i].Order.Length).Append(')');
                }
                CMMConfig.D($"[CMM] Radio catalog: {Stations.Count} station(s) — {names}");
            }
        }

        // One station subfolder's pool: the recursive audio scan PLUS every playlist in the
        // subfolder's tree, expanded and deduped. The shared recursive scan deliberately ignores
        // .m3u (context-source semantics); a station folder honours them — a station can be a
        // folder of tracks, a folder holding just a playlist, or any mix. Playlist entries may
        // point anywhere (absolute, or relative to the playlist). Discovered playlists are noted
        // for the catalog signature (in-place edits must trigger a rebuild).
        private static string[] StationPool(string dir) => StationPool(dir, out _);

        // `dropped` accumulates the unusable entries across every .m3u in the station folder, so a
        // folder-backed station reports the same shortfall a playlist-backed one does.
        private static string[] StationPool(string dir, out int dropped)
        {
            dropped = 0;
            string[] audio = SourceScan.EnumerateFolderPoolRaw(dir, true, claimed: new string[0], out _);
            string[] m3us = null;
            try { m3us = Directory.GetFiles(dir, "*.m3u*", SearchOption.AllDirectories); } catch { }
            if (m3us == null || m3us.Length == 0) return audio;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pool = new List<string>((audio?.Length ?? 0) + 16);
            if (audio != null)
                foreach (string a in audio)
                    if (seen.Add(SourceScan.FullKey(a))) pool.Add(a);
            Array.Sort(m3us, StringComparer.OrdinalIgnoreCase);   // stable expansion order
            foreach (string m3u in m3us)
            {
                if (!Contexts.IsPlaylistSource(m3u)) continue;    // "*.m3u*" also matches odd extensions
                _plScratch.Add(m3u);
                string[] tracks = PlaylistSource.ReadTracks(m3u, out bool m3uUnresolved, out int m3uDropped);
                if (m3uUnresolved) _catalogIncomplete = true;
                dropped += m3uDropped;
                if (tracks == null) continue;
                foreach (string t in tracks)
                    if (seen.Add(SourceScan.FullKey(t))) pool.Add(t);
            }
            return pool.ToArray();
        }

        private static void AddStation(string name, string[] pool, int dropped = 0)
        {
            if (pool == null || pool.Length == 0) return;
            var st = new RadioStation { Name = name, DroppedEntries = dropped };
            // Deterministic broadcast order: sort (path-stable), then Fisher-Yates with an LCG
            // seeded by the STATION NAME — every client with the same folder computes the same
            // order, and adding a different station doesn't reshuffle this one.
            Array.Sort(pool, StringComparer.OrdinalIgnoreCase);
            uint seed = 2166136261u;   // FNV-1a over the lowercased name
            string ln = name.ToLowerInvariant();
            for (int i = 0; i < ln.Length; i++) { seed ^= ln[i]; seed *= 16777619u; }
            if (seed == 0) seed = 1;
            st.Seed = seed;            // PRE-shuffle value — the window anchors mix this
            for (int i = pool.Length - 1; i > 0; i--)
            {
                seed = seed * 1664525u + 1013904223u;
                int j = (int)(seed % (uint)(i + 1));
                string tmp = pool[i]; pool[i] = pool[j]; pool[j] = tmp;
            }
            st.Order = pool;
            Stations.Add(st);
        }

        // Cheap change-signature for the catalog source, reusing the context file-watch helpers: a
        // playlist's own mtime, or the folder's recursive write-time (covers station subfolders) —
        // plus the mtime of every playlist the last build discovered, since editing a playlist
        // IN PLACE doesn't bump any directory's write time.
        private static DateTime SourceSignature(string src)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(src)) return DateTime.MinValue;
                DateTime t;
                string ext = Path.GetExtension(src).ToLowerInvariant();
                if (ext == ".m3u" || ext == ".m3u8")
                    t = SourceScan.PlaylistWriteSignatureUtc(PlaylistSource.ResolveFullPath(src));
                else
                {
                    // Every parent. A MAX alone would absorb a whole add-on folder
                    // disappearing (a missing folder reports MinValue, which is the identity
                    // element of max), so the COUNT of live parents is folded in as a
                    // synthetic tick offset: parents coming and going always moves the value.
                    t = DateTime.MinValue;
                    int live = 0;
                    foreach (string dir in SourceDirs(src))
                    {
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                        live++;
                        DateTime dt = SourceScan.ContextWriteSignatureUtc(dir, true);
                        if (dt > t) t = dt;
                    }
                    if (t != DateTime.MinValue && live > 1) t = t.AddTicks(live);
                }
                foreach (string pf in _catalogPlaylists)
                {
                    try { if (File.Exists(pf)) { DateTime mt = File.GetLastWriteTimeUtc(pf); if (mt > t) t = mt; } }
                    catch { }
                }
                return t;
            }
            catch { return DateTime.MinValue; }
        }

        // ── Config via the "Radio" context ──────────────────────────────────────────────────────
        // WorldEmitterSource may NAME a context defined in contexts.xml (e.g. "Radio"). When it does,
        // that context is the definition home for the radio net: its Source is the stations' parent
        // folder, its FadeInDuration the start crossfade, its VolumeOverride the level. Otherwise the
        // key is a raw folder/playlist with WorldEmitterFade + the global volume.
        internal static Contexts RadioContext() => ContextMusicManager.GetContext(CMMConfig.WorldEmitterSource);

        // Start crossfade: the context's fade-in when defined, else the standalone WorldEmitterFade.
        internal static float StartFade()
        {
            Contexts c = RadioContext();
            return c != null ? c.FadeInDuration : CMMConfig.WorldEmitterFade;
        }
    }
}
