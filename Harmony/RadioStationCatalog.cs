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
    internal sealed class RadioStation
    {
        // Max speaker positions fed to one emitter voice (matches native CMM_MAX_EMIT_PTS).
        internal const int MaxPoints = 8;

        public string Name;              // subfolder name (or the root/playlist for the fallback)
        public string[] Order;           // deterministic broadcast order (sorted, then seeded shuffle)
        public uint Seed;                // FNV-1a of the lowercased name (PRE-shuffle) — anchor mixing
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
        public float LastSeenAt = -999f;             // last tick this station had ANY tracked speaker
        public int PtCount;                          // culled in-Range points below
        public readonly float[] Pts    = new float[MaxPoints * 3];
        public readonly float[] PtGain = new float[MaxPoints];
        public readonly float[] PtDist = new float[MaxPoints];

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
        private static readonly Dictionary<string, long> _lenFrames = new Dictionary<string, long>();
        private static readonly Dictionary<string, int>  _lenRate   = new Dictionary<string, int>();
        private static readonly object _lenLock = new object();

        // Hard bound on any chain walk (resolve or probe): a window is RadioAnchorSeconds long, so
        // sane pools finish in ~window/typical-track steps; the cap only bites pathological pools
        // (thousands of sub-second or dead files), which resolve as dead air.
        private const int WalkCap = 4096;

        // ── Broadcast clock ─────────────────────────────────────────────────────────────────────
        // The world's elapsed time in REAL seconds: worldTime advances 24000 ticks per in-game day,
        // and a day lasts DayNightLength real minutes — so every session/client of a save derives
        // the same broadcast position (a relog rejoins the same song at the same point). Freezes
        // with the SP pause menu; the EOF-advance paths guard against the resulting small drift.
        internal static double BroadcastSeconds()
        {
            World w = GameManager.Instance?.World;
            if (w == null) return 0;
            int dnl = GamePrefsCompat.DayNightLengthMinutes();   // by-name: enum ordinal drifts cross-version
            return w.worldTime * (dnl * 60.0 / 24000.0);
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
        internal static bool ResolveAt(RadioStation st, double B, out string file, out int idx, out double offset)
        {
            file = null; idx = 0; offset = 0;
            if (!st.Ready) return false;
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
                    if (!_lenFrames.TryGetValue(f, out long fr)) { needWalk = true; break; }
                    int rt = _lenRate[f];
                    double len = (fr > 0 && rt > 0) ? (double)fr / rt : 0;   // 0 ⇒ dead file: skip, no airtime
                    if (len > 0)
                    {
                        if (rem < len) { file = f; idx = i; offset = rem; return true; }
                        rem -= len;
                    }
                    i = (i + 1) % n;
                }
            }
            if (needWalk) EnsureWindowWalk(st, k);
            return false;   // walk pending, or the whole chain is dead (WalkCap) — dead air
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
                }
                catch { /* a failed walk resolves as dead air; the next window retries */ }
                finally { st.WalkWindow = k; st.WalkPending = false; }
            });
        }

        // Fills the station's shared OnAir/OnAirIdx/OnAirOffset scratch (emitter-layer consumer).
        internal static void ResolveOnAir(RadioStation st, double B)
        {
            if (ResolveAt(st, B, out string file, out int idx, out double off))
            { st.OnAir = file; st.OnAirIdx = idx; st.OnAirOffset = off; }
            else st.OnAir = null;
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
        // 0 ⇒ dead file (open failed) — cached too, so it's skipped deterministically everywhere.
        private static double ProbeLength(string file)
        {
            lock (_lenLock)
                if (_lenFrames.TryGetValue(file, out long fr))
                    return (fr > 0 && _lenRate[file] > 0) ? (double)fr / _lenRate[file] : 0;
            long frames = 0; int rate = 0;
            IntPtr dec = AudioDecoder.OpenNativeHandle(file, out int kind, out _, out int r, out long total);
            if (dec != IntPtr.Zero) { frames = total; rate = r; AudioDecoder.CloseNativeHandle(dec, kind); }
            lock (_lenLock) { _lenFrames[file] = frames; _lenRate[file] = rate; }
            return (frames > 0 && rate > 0) ? (double)frames / rate : 0;
        }

        // A track failed to open AT PLAY time (vanished since its walk probe): mark it dead in the
        // length cache so every chain walk skips it — schedules shift identically for everyone who
        // re-resolves — until the ~2 s catalog watch rebuilds the pool properly.
        internal static void PoisonTrack(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            lock (_lenLock) { _lenFrames[file] = 0; _lenRate[file] = 0; }
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
                if (Stations.Count > 0 && sig == _catalogSig && hash == _catalogHash) return;
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

        private static long ContentHash(string src)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(src)) return 0;
                string ext = Path.GetExtension(src).ToLowerInvariant();
                if (ext == ".m3u" || ext == ".m3u8") return 0;   // playlist source: its own mtime suffices
                string dir = Path.IsPathRooted(src) ? src : Path.Combine(ContextMusicManager.MusicDirectory, src);
                if (!SourceScan.IsSloppyMtimeFilesystem(dir)) return 0;
                return SourceScan.FileSetContentHash(dir, includeSubfolders: true);
            }
            catch
            {
                // Transient IO mid-walk: report "no change" rather than flapping the catalog.
                return _catalogHash;
            }
        }

        private static void RebuildCatalog(string src)
        {
            // Snapshot the old stations so the emitter consumer can migrate live voices by name.
            var old = new Dictionary<string, RadioStation>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Stations.Count; i++) old[Stations[i].Name] = Stations[i];
            Stations.Clear();

            _plScratch.Clear();
            try
            {
                if (!string.IsNullOrWhiteSpace(src))
                {
                    string ext = Path.GetExtension(src).ToLowerInvariant();
                    if (ext == ".m3u" || ext == ".m3u8")
                    {
                        string full = PlaylistSource.ResolveFullPath(src);
                        _plScratch.Add(full);
                        AddStation(Path.GetFileNameWithoutExtension(src), PlaylistSource.ReadTracks(full));
                    }
                    else
                    {
                        string dir = Path.IsPathRooted(src) ? src : Path.Combine(ContextMusicManager.MusicDirectory, src);
                        if (Directory.Exists(dir))
                        {
                            string[] subs = Directory.GetDirectories(dir);
                            Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
                            foreach (string sub in subs)
                            {
                                string[] pool = StationPool(sub);
                                if (pool != null && pool.Length > 0)
                                    AddStation(Path.GetFileName(sub), pool);
                            }
                            if (Stations.Count == 0)
                            {
                                // Root fallback: no subfolder had a playable pool — the source folder
                                // itself is the single station (the pre-multi-station behaviour). The
                                // flat scan already expands the root's own top-level playlists.
                                string[] pool = SourceScan.EnumerateFolderPoolRaw(dir, false, claimed: new string[0], out string[] rootPls);
                                if (rootPls != null) _plScratch.AddRange(rootPls);
                                if (pool != null && pool.Length > 0)
                                    AddStation(Path.GetFileName(dir.TrimEnd('\\', '/')), pool);
                            }
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
        private static string[] StationPool(string dir)
        {
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
                string[] tracks = PlaylistSource.ReadTracks(m3u);
                if (tracks == null) continue;
                foreach (string t in tracks)
                    if (seen.Add(SourceScan.FullKey(t))) pool.Add(t);
            }
            return pool.ToArray();
        }

        private static void AddStation(string name, string[] pool)
        {
            if (pool == null || pool.Length == 0) return;
            var st = new RadioStation { Name = name };
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
                    string dir = Path.IsPathRooted(src) ? src : Path.Combine(ContextMusicManager.MusicDirectory, src);
                    t = SourceScan.ContextWriteSignatureUtc(dir, true);
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
