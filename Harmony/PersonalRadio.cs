using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // PERSONAL RADIO  (version-neutral: every supported game version)
    // ─────────────────────────────────────────────────────────────────────────
    // The player's own radio: a 2-key DIAL (next/prev, with OFF as a stop on the dial) through
    // the shared station catalog (RadioStationCatalog — the same stations the 3.0-only world
    // emitters broadcast). Plays NON-spatially on the main music bus, joined to the world-clock
    // broadcast (same song, same position as every emitter and MP client of the station). While
    // ON and in-game it OWNS the music layer: context music is suppressed (StartContextCoroutine
    // gate) and vanilla DMS sections are muted (VanillaSectionSuppressed) — dialing to OFF hands
    // music straight back to the resolver.
    //
    // Notifications use the game's NATIVE toolbelt tooltip (the vanilla notification area just
    // above the toolbelt), resolved by reflection: ShowTooltip's overload shapes differ across
    // versions (A20/A21 have a true 2-arg form; 3.0's shortest is 5-arg with defaults), and a
    // compiled call would bake one shape's method ref into the cross-version DLL.
    //
    // Station-removal survival: the tuned station is held by NAME. If its folder disappears the
    // radio goes silent ("off air" — static comes later) but stays tuned + on; the ~2 s catalog
    // watch brings it back the moment the folder returns. With NO stations at all the dial still
    // works (Off ↔ nothing) but reports "no stations".
    //
    // Persistence: {on, station} in Cache\radio.xml — if it was on at quit, it starts playing on
    // the next world entry (rejoining the live broadcast wherever it is now).
    //
    // Voice lifecycle notes: cmm_mix_add crossfades every other MUSIC voice out, so starting a
    // track IS the retune/takeover transition; a voice is "stopped" by ramping its per-voice
    // track gain to 0 and forgetting it (loop:false ⇒ it retires itself at EOF) — there is no
    // per-voice remove on the main bus, and a bus-wide Stop could race menu music starting.
    internal static class PersonalRadio
    {
        private const float TickInterval = 0.25f;
        private const float AdvanceFade = 0.2f;    // EOF → next track (near-seamless)
        private const float SilenceFade = 0.4f;    // off-air / world-exit fade to silent

        private static bool   _on;
        private static string _tunedName;          // survives the station being off air
        private static long   _voiceId;
        private static string _track;
        private static float  _gainApplied = 1f;
        private static bool   _offAir;             // edge-detect for the "(off air)" notice
        private static float  _nextTick;
        private static float  _nextStartFade = -1f;   // fade for the NEXT voice start; -1 ⇒ gentle StartFade()
        private static string _stateFile;
        private static string _gainWaitFile;          // join track we're holding for (loudness measure)
        private static float  _gainWaitUntil;         // bounded — see StartFromClock
        private static string _advanceCarry;          // AdvanceOverride preserved across gated retries
        private static float  _nextAuditAt;           // sync-audit cadence (RadioStationCatalog.AuditVoice)

        // ── Radio UI sounds (v35/v36 native synth) ─────────────────────────────────────────────
        // A USER DIAL arms the tuning static; Tick PINGS the native keepalive hash at 4 Hz until
        // the dialed station's voice lands (StopStatic then releases it), so the async open/seek
        // and the loudness gain-gate's "tuning…" hold are audibly "searching" instead of dead —
        // one seamless noise per dial, never stacked bursts. Spontaneous joins (world entry, EOF
        // advance, stinger rejoin, unpause resync) never arm it.
        private static bool  _staticWanted;
        private static float _nextStaticAt;

        // ── Async start state ───────────────────────────────────────────────────────────────────
        // A radio start opens a decoder and SEEKS minutes into the file (cmm_mix_add runs the
        // resume seek on the CALLER's thread — a mid-file mp3 seek walks every frame to the
        // target, ~50–130 ms), so it must not run on the main thread: StartFromClock resolves and
        // gates on the main thread, then hands open+seek+add to a ThreadPool job whose result the
        // next Tick commits. ONE job in flight at a time (mix_add crossfades every other music
        // voice — two racing adds would fight); _startGen invalidates a job the dial outran, and
        // its orphan voice is muted at commit (retires at EOF — no per-voice remove, see header).
        private static volatile bool _startPending;
        private static float _startPendingAt;         // hang safety: re-allow dispatch after 10 s
        private static int _startGen;                 // bumped by FadeVoiceOut (every wanted-voice change)
        private static readonly object _startLock = new object();
        private static volatile bool _doneReady;
        private static long _doneVoiceId; private static string _doneFile; private static string _doneStation;
        private static float _doneGain; private static double _doneOffset; private static int _doneGen;
        private static bool _doneOpenFailed;

        // ── Public surface ──────────────────────────────────────────────────────────────────────

        internal static bool On => _on;

        // True while the radio owns the music layer: context playback is gated and vanilla DMS
        // muted. In-game only — on the menu, normal menu music plays even with the radio on
        // (it resumes on re-entry).
        internal static bool SuppressesMusic => _on && ContextMusicManager.inGame;

        // Master-volume override for VolumeSyncLoop while the radio owns the bus: the Radio
        // context's own volume (unset ⇒ -1 = the global VolumeModifier), mirroring contexts.
        internal static float MasterVolumeOverride
        {
            get { Contexts c = RadioStationCatalog.RadioContext(); return c != null ? c.VolumeOverride : -1f; }
        }

        // ── Now Playing overlay surface ─────────────────────────────────────────────────────────
        // Cached (no per-OnGUI allocation, matching the overlay's own caches): the airing file,
        // the voice id for the time readout, the "Radio: <station>" line-2 name, and the fixed
        // title/subtitle shown while on but silent (off air / still tuning).
        internal static string PlayingTrack => _on ? _track : null;
        internal static long VoiceId => _voiceId;
        internal static string OverlayContextName => _overlayContextName;
        internal static GUIContent OverlayIdleTitle { get; } = new GUIContent("Radio");
        internal static GUIContent OverlayIdleSub => _overlayIdleSub;

        private static string _overlayContextName;
        private static GUIContent _overlayIdleSub;

        // Rebuilt only on state edges (tune, off, off-air flips) — never per frame.
        private static void RefreshOverlayCaches()
        {
            if (!_on || _tunedName == null) { _overlayContextName = null; _overlayIdleSub = null; return; }
            _overlayContextName = "Radio: " + _tunedName;
            _overlayIdleSub = new GUIContent("♪  " + _tunedName + (_offAir ? " — off air" : " — tuning…"));
        }

        // ── Persistence ─────────────────────────────────────────────────────────────────────────

        // Called once at init (next to LoudnessStore.Load). Tiny state: on + station name.
        internal static void Load(string modPath)
        {
            _stateFile = Path.Combine(modPath, "Cache", "radio.xml");
            try
            {
                if (!File.Exists(_stateFile)) return;
                var doc = new XmlDocument();
                doc.Load(_stateFile);
                XmlElement root = doc.DocumentElement;
                if (root == null || root.Name != "CMMRadio") return;
                _tunedName = root.GetAttribute("station");
                if (string.IsNullOrEmpty(_tunedName)) _tunedName = null;
                _on = string.Equals(root.GetAttribute("on"), "true", StringComparison.OrdinalIgnoreCase)
                      && _tunedName != null;
                if (_on) CMMConfig.D($"[CMM] Radio: restored — tuned to '{_tunedName}' (on).");
                RefreshOverlayCaches();
            }
            catch { _on = false; }
        }

        private static void Save()
        {
            if (_stateFile == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_stateFile));
                File.WriteAllText(_stateFile,
                    "<CMMRadio on=\"" + (_on ? "true" : "false") + "\" station=\"" +
                    System.Security.SecurityElement.Escape(_tunedName ?? "") + "\" />");
            }
            catch { /* a failed save costs one forgotten dial position */ }
        }

        // ── The dial ────────────────────────────────────────────────────────────────────────────
        // Positions: Off, then the catalog's stations in sorted order; both keys wrap through Off.
        // A tuned name that's currently off air still holds its alphabetical slot, so stepping
        // away from a dead station lands where you'd expect.
        internal static void Step(int dir)
        {
            if (!ContextMusicManager.inGame) return;
            EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null) return;

            RadioStationCatalog.EnsureCatalog();   // dial against fresh names
            List<RadioStation> sts = RadioStationCatalog.Stations;
            int n = sts.Count;
            if (n == 0)
            {
                if (_on) { TurnOff(player, "Radio: off (no stations)"); return; }
                Notify(player, "Radio: no stations found");
                return;
            }

            int target;   // -1 = Off, else station index
            if (!_on) target = dir > 0 ? 0 : n - 1;
            else
            {
                // Current position: the tuned name's index, or — off air — its insertion point
                // (idx = first station sorting AFTER it), from which next lands on idx and prev
                // on idx-1. `exact` compensates the +1 step over the phantom slot.
                int idx = 0; bool exact = false;
                for (int i = 0; i < n; i++)
                {
                    int cmp = string.Compare(sts[i].Name, _tunedName, StringComparison.OrdinalIgnoreCase);
                    if (cmp == 0) { idx = i; exact = true; break; }
                    if (cmp < 0) idx = i + 1;
                }
                target = dir > 0 ? (exact ? idx + 1 : idx) : idx - 1;
                if (target >= n || target < -1) target = -1;   // wrap through Off
            }

            if (target < 0) { TurnOff(player); return; }
            Tune(sts[target].Name, player);
        }

        private static void TurnOff(EntityPlayerLocal player, string notice = "Radio: off")
        {
            _on = false;
            _offAir = false;
            _staticWanted = false;
            StopStatic();
            Save();
            RefreshOverlayCaches();
            FadeVoiceOut(CMMConfig.ManualSkipFadeDuration);
            Sfx(CmmMixer.SfxClickOff);   // the power-down clunk (manual dial AND the all-stations-deleted auto-off)
            Notify(player, notice);
            // Hand music back: the resolver restarts whatever context should be playing.
            ContextMusicManager.ReResolveNow();
        }

        // Fire one radio-UI one-shot (native synth; no asset files). Config-gated + hot-tunable;
        // the native side ensures the output device, so a power-on click can precede the radio
        // voice's own add, and it never touches the output gain (pause duck stays ducked).
        private static void Sfx(int kind)
        {
            if (!CMMConfig.RadioSfxEnabled) return;
            float vol = CMMConfig.RadioSfxVolume;
            try
            {
                CmmMixer mixer = CmmMixer.GetOrCreate();
                if (kind == CmmMixer.SfxTuningStatic)
                {
                    // The static has its own level (-1 = inherit) and a live tone: pushing the
                    // tone on EVERY ping is what makes radio.xml edits audible mid-hash.
                    if (CMMConfig.RadioStaticVolume >= 0f) vol = CMMConfig.RadioStaticVolume;
                    mixer.SetStaticTone(CMMConfig.RadioStaticLowHz,
                                        CMMConfig.RadioStaticHighHz,
                                        CMMConfig.RadioStaticCrackle);
                }
                else
                {
                    // Clicks likewise: their own levels (-1 = inherit) + the live tone pair,
                    // pushed before every click so a hot-reloaded edit lands on the next dial.
                    if (kind == CmmMixer.SfxClickOn  && CMMConfig.RadioClickOnVolume  >= 0f) vol = CMMConfig.RadioClickOnVolume;
                    if (kind == CmmMixer.SfxClickOff && CMMConfig.RadioClickOffVolume >= 0f) vol = CMMConfig.RadioClickOffVolume;
                    mixer.SetClickTone(CMMConfig.RadioClickOnHz,  CMMConfig.RadioClickOnSnap,  CMMConfig.RadioClickOnLengthMs,
                                       CMMConfig.RadioClickOnBody,  CMMConfig.RadioClickOnBodyHz,  CMMConfig.RadioClickOnDouble,
                                       CMMConfig.RadioClickOffHz, CMMConfig.RadioClickOffSnap, CMMConfig.RadioClickOffLengthMs,
                                       CMMConfig.RadioClickOffBody, CMMConfig.RadioClickOffBodyHz, CMMConfig.RadioClickOffDouble);
                }
                if (vol <= 0f) return;
                mixer.PlaySfx(kind, vol);
            }
            catch { /* a missing native DLL already disables all playback — a silent click is moot */ }
        }

        // End the keepalive tuning static NOW (its ~90ms release runs) — call wherever
        // _staticWanted goes false, so the hash stops the moment its reason does instead of
        // running out its ping hold over the music.
        private static void StopStatic()
        {
            try { CmmMixer.Instance?.StopSfx(CmmMixer.SfxTuningStatic); }
            catch { /* see Sfx */ }
        }

        private static void Tune(string name, EntityPlayerLocal player)
        {
            bool wasOn = _on;
            _on = true;
            _offAir = false;
            _tunedName = name;
            Save();
            RefreshOverlayCaches();

            if (!wasOn)
            {
                // Take the music layer over: tear the context voice down properly (auto-advance,
                // transition bookkeeping). The radio's own Play would crossfade it anyway, but the
                // context system must KNOW its voice is gone.
                ContextMusicManager.StopNativeIfActive(CMMConfig.ManualSkipFadeDuration);
            }

            // Retune = fade the old voice NOW and let the tick dispatch the ASYNC start (open +
            // seek on a worker — see StartFromClock). A keypress never blocks the frame, rapid
            // dialing just re-aims the single in-flight start (FadeVoiceOut bumped _startGen),
            // and the old station can never keep playing under the new dial position.
            FadeVoiceOut(CMMConfig.ManualSkipFadeDuration);
            _nextStartFade = CMMConfig.ManualSkipFadeDuration;   // keypress-fast
            _nextTick = 0f;                                      // dispatch next frame, not in ≤250 ms
            // A CLICK ON EVERY DIAL MOVE, not only on power-on. Turning the knob while the radio is
            // already playing is the same physical action as turning it on -- a detent -- and
            // hearing nothing made a retune feel like the game had swallowed the keypress. It fires
            // BEFORE _staticWanted below so the order reads the way the device would: click, then
            // the hash, then the station. Shares the power-on click's voice and its RadioClickOn*
            // tuning rather than adding a fourth sound with its own set of knobs.
            Sfx(CmmMixer.SfxClickOn);
            _staticWanted = true;                                // tuning static rolls until the voice lands
            _nextStaticAt = 0f;
            Notify(player, RadioStationCatalog.ByName.ContainsKey(name)
                ? $"Radio: {name}" : $"Radio: {name} (tuning…)");
        }

        // After an SP pause: the broadcast clock (worldTime) froze while our voice kept decoding
        // in realtime under the pause duck, so it's now AHEAD of the paper schedule by the pause
        // length — a lead the EOF pause-drift guard would lock in for the session. Rejoin the
        // paper: the song resumes where the listener stopped hearing it, staying within a beat
        // of the world emitters. Called from the unpause edge (OnGamePauseChanged, pauses > 1 s).
        internal static void ResyncToClock()
        {
            if (!_on || _voiceId == 0) return;
            FadeVoiceOut(AdvanceFade);
            _nextStartFade = AdvanceFade;
            _nextTick = 0f;   // the idle branch redials from the clock next frame (async start)
        }

        // ── Tick (4 Hz, called every frame from the keybinds Update on every version) ──────────
        internal static void Tick()
        {
            // Async start results land on ANY frame (ahead of the 4 Hz throttle): the audio is
            // already playing the moment the worker's add returned — this is just bookkeeping,
            // but a stale orphan should be muted promptly.
            if (_doneReady) CommitPendingStart();

            if (Time.realtimeSinceStartup < _nextTick) return;
            _nextTick = Time.realtimeSinceStartup + TickInterval;
            if (!_on) return;

            EntityPlayerLocal player = ContextMusicManager.inGame
                ? GameManager.Instance?.World?.GetPrimaryPlayer() : null;
            if (player == null)
            {
                // Menu / loading / teardown: silence the voice but STAY on — re-entry rejoins the
                // broadcast (gently: the world is fading in around you, not a keypress).
                FadeVoiceOut(SilenceFade);
                _nextStartFade = -1f;
                return;
            }

            // Not spawned yet (world entry) or dead (death screen): the radio waits — silent,
            // still on. playerIsDead clears in AfterPlayerRespawn, which also fires the Spawn
            // stinger; the stinger hold below then defers the rejoin until it finishes.
            if (ContextMusicManager.playerIsDead)
            {
                FadeVoiceOut(SilenceFade);
                _nextStartFade = -1f;
                return;
            }

            // A PlayOnce stinger (Death / Spawn / POI_Cleared) owns the moment. Death/Spawn
            // crossfade our voice out fully (forget it — it's retiring — and queue a quick
            // rejoin); POI_Cleared only DUCKS it (voice stays live: fall through, do nothing).
            if (ContextMusicManager.PlayOnceStingerActive)
            {
                if (_voiceId != 0 && CmmMixer.Instance != null
                    && !CmmMixer.Instance.TryGetState(_voiceId, out _, out _, out _))
                {
                    _voiceId = 0;
                    _track = null;
                    _nextStartFade = AdvanceFade;
                }
                if (_voiceId == 0) return;   // hold the rejoin until the stinger finishes
            }

            // The radio drives the shared catalog itself, so it works with the world emitters
            // disabled AND on game versions that have no emitter layer at all. (Resolves kick
            // their own background window walks — there is no probe loop to tick.)
            RadioStationCatalog.EnsureCatalog();

            // EVERY station/track is gone (folders deleted or emptied): power the radio OFF —
            // with the power-down clunk — instead of holding silent dead air forever. The tuned
            // name persists, so restoring the files and dialing back on resumes as usual. (One
            // missing station with others still present stays the revivable "off air" hold below.)
            if (RadioStationCatalog.Stations.Count == 0)
            {
                TurnOff(player, "Radio: off (no stations)");
                return;
            }

            if (_tunedName == null || !RadioStationCatalog.ByName.TryGetValue(_tunedName, out RadioStation st))
            {
                // Tuned station is gone (folder removed/renamed, or the whole catalog emptied):
                // dead air — silent, still on, still suppressing music. The catalog watch revives
                // it within ~2 s of the folder returning. Static comes later.
                FadeVoiceOut(SilenceFade);
                _staticWanted = false;   // off air = deliberate silence, not "searching"
                StopStatic();
                if (!_offAir)
                {
                    _offAir = true;
                    RefreshOverlayCaches();
                    if (_tunedName != null) Notify(player, $"Radio: {_tunedName} (off air)");
                }
                return;
            }
            if (_offAir)
            {
                _offAir = false;   // station came back — resume below (voice is 0)
                RefreshOverlayCaches();
                Notify(player, $"Radio: {st.Name}");
            }

            double Bnow = RadioStationCatalog.BroadcastSeconds();

            // Prewarm the tuned station's on-air + deterministic up-next loudness so an
            // advance always lands on an already-normalized track (never hot-then-settle).
            // The emitter layer's MaintainOnAir only covers stations with a NEARBY SPEAKER —
            // the personally-tuned station usually has none. RequestPrewarm dedups (no-ops
            // on measured / in-flight files), so 4 Hz is fine.
            int ahead = CMMConfig.LoudnessNormalizationEnabled ? CMMConfig.LoudnessMeasureAhead : 0;
            if (ahead > 0 && RadioStationCatalog.ResolveAt(st, Bnow, out string paOnAir, out int paIdx, out _))
            {
                ContextMusicManager.RequestPrewarm(paOnAir);
                for (int k = 1; k <= ahead && k < st.Order.Length; k++)
                    ContextMusicManager.RequestPrewarm(st.Order[(paIdx + k) % st.Order.Length]);
            }

            if (_voiceId != 0)
            {
                // Sync audit: periodic voice-vs-paper delta (see RadioStationCatalog.AuditVoice).
                if (Time.realtimeSinceStartup >= _nextAuditAt)
                {
                    _nextAuditAt = Time.realtimeSinceStartup + RadioStationCatalog.AuditInterval;
                    RadioStationCatalog.AuditVoice("personal", st, _track, _voiceId);
                }

                // Soft-handoff prep: within a minute of the window boundary, warm the NEXT
                // window's chain so the EOF that crosses the boundary rejoins instantly.
                long kNow = RadioStationCatalog.WindowIndex(Bnow);
                if ((kNow + 1) * CMMConfig.RadioAnchorSeconds - Bnow < 60.0)
                    RadioStationCatalog.EnsureWindowWalk(st, kNow + 1);

                // Airing file vanished mid-play → hot-swap (interrupt), parity with the emitters.
                if (_track != null && !RadioStationCatalog.SafeExists(_track))
                {
                    CMMConfig.D($"[CMM] Radio: airing track gone ({Path.GetFileName(_track)}) — hot-swapping.");
                    string ended = _track;
                    FadeVoiceOut(AdvanceFade);
                    _nextStartFade = AdvanceFade;
                    StartFromClock(st, AdvanceOverride: ended);
                    return;
                }
                // Track ended (decoder EOF is authoritative) → next track on the schedule.
                if (CmmMixer.Instance != null && !CmmMixer.Instance.TryGetState(_voiceId, out _, out _, out _))
                {
                    CMMConfig.D("[CMM] Radio: track ended → next track.");
                    string ended = _track;
                    _voiceId = 0; _track = null;
                    _nextStartFade = AdvanceFade;
                    StartFromClock(st, AdvanceOverride: ended);
                }
                return;
            }

            // A user dial is still waiting for its voice (async open/seek in flight, or the
            // loudness gain-gate's "tuning…" hold): keep the tuning static alive. The static is
            // a native KEEPALIVE (v36) — each ping extends ONE seamless hash rather than layering
            // bursts (which audibly double-fired), and StopStatic ends it the moment the voice
            // lands.
            if (_staticWanted && Time.realtimeSinceStartup >= _nextStaticAt)
            {
                _nextStaticAt = Time.realtimeSinceStartup + TickInterval;
                Sfx(CmmMixer.SfxTuningStatic);
            }

            // Idle but on (world entry with a persisted on-state, a gain-wait retry, or it just
            // came back on air): join the broadcast. The carry re-arms a gated EOF advance.
            if (st.Ready) StartFromClock(st, AdvanceOverride: _advanceCarry);
        }

        // ── Voice ───────────────────────────────────────────────────────────────────────────────

        // Resolve the tuned station's broadcast position and DISPATCH the voice start (open +
        // seek + add run on a worker — see the async-start header). AdvanceOverride carries the
        // just-ended track for the pause-drift guard: worldTime freezes in the SP pause menu
        // while audio keeps playing, so EOF can land BEFORE the schedule boundary — if the
        // schedule still says the ended track, step to the next one at 0 instead of replaying
        // its tail. An open failure poisons the track at commit; the next tick re-resolves the
        // shifted chain and dispatches again (the old bounded retry loop, now across ticks).
        private static void StartFromClock(RadioStation st, string AdvanceOverride)
        {
            if (_startPending && Time.realtimeSinceStartup - _startPendingAt < 10f) return;

            if (!RadioStationCatalog.ResolveAt(st, RadioStationCatalog.BroadcastSeconds(),
                                               out string file, out int idx, out double offset))
                return;
            if (AdvanceOverride != null && string.Equals(file, AdvanceOverride, StringComparison.OrdinalIgnoreCase))
            {
                idx = (idx + 1) % st.Order.Length;
                file = st.Order[idx];
                offset = 0;
            }

            // Hold the start until the join track's loudness is known: a sub-second wait behind
            // the "tuning…" state beats starting hot and settling audibly, and the broadcast
            // clock keeps running so we simply rejoin a moment later in the song. BOUNDED: a
            // track that can't be measured (decode error) starts anyway and settles as before.
            // (Main thread only — LoudnessStore's threading contract.)
            if (CMMConfig.LoudnessNormalizationEnabled && !LoudnessStore.IsMeasured(file))
            {
                if (!string.Equals(_gainWaitFile, file, StringComparison.OrdinalIgnoreCase))
                {
                    _gainWaitFile = file;
                    _gainWaitUntil = Time.realtimeSinceStartup + 6f;
                }
                if (Time.realtimeSinceStartup < _gainWaitUntil)
                {
                    ContextMusicManager.RequestPrewarm(file);
                    // The 4 Hz tick retries via the idle branch; _nextStartFade survives for the
                    // real start, and the carry keeps the pause-drift guard across the retries
                    // (dropping it could replay the just-ended track's tail on a frozen clock).
                    _advanceCarry = AdvanceOverride;
                    return;
                }
            }

            if (offset < 0) offset = 0;
            float fade = _nextStartFade >= 0f ? _nextStartFade : RadioStationCatalog.StartFade();
            _nextStartFade = -1f;
            float trackGain = LoudnessStore.GetTrackGain(file);   // main-thread read, captured

            // Seed the master before the first block (VolumeSyncLoop keeps it current after).
            CmmMixer mixer = CmmMixer.GetOrCreate();
            CmmMixer.MasterVolume = CMMConfig.GetVolume(true, MasterVolumeOverride);

            // snapTight only on dial/entry starts — an EOF advance must not flush the cushion,
            // that would cut the ended track's queued tail.
            bool advance = AdvanceOverride != null;
            int gen = _startGen;
            string fileCap = file; double offCap = offset; string stName = st.Name;
            _startPending = true;
            _startPendingAt = Time.realtimeSinceStartup;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                long id = 0; bool openFailed = false;
                try
                {
                    IntPtr dec = AudioDecoder.OpenNativeHandle(fileCap, out int kind, out int srcCh,
                                                               out int srcRate, out long total);
                    if (dec == IntPtr.Zero) openFailed = true;
                    else if (System.Threading.Volatile.Read(ref _startGen) != gen
                             || ContextMusicManager.playerIsDead)
                    {
                        // The dial moved — or the player died — while we were opening: abandon
                        // before the add (an add crossfades every other music voice; a stale one
                        // landing after the Death stinger's would crossfade the stinger out).
                        AudioDecoder.CloseNativeHandle(dec, kind);
                    }
                    else
                    {
                        long startFrame = srcRate > 0 ? (long)(offCap * srcRate) : 0;
                        if (total > 0 && startFrame >= total) startFrame = 0;   // clock/length mismatch safety
                        // Play crossfades every other music voice out (the retune/takeover
                        // transition) and runs the resume SEEK on this worker thread.
                        id = mixer.Play(dec, kind, srcRate, srcCh, total, fade,
                                        loop: false, startFrame, trackGain,
                                        duckOthersTo: -1f, snapTight: !advance);
                    }
                }
                catch { /* commit treats id 0 without openFailed as a no-op */ }
                lock (_startLock)
                {
                    _doneVoiceId = id; _doneFile = fileCap; _doneStation = stName;
                    _doneGain = trackGain; _doneOffset = offCap; _doneGen = gen;
                    _doneOpenFailed = openFailed;
                    _doneReady = true;
                }
            });
        }

        // MAIN THREAD (from Tick, any frame): land the async start's result — commit the voice,
        // or poison a dead file, or mute an orphan the dial outran.
        private static void CommitPendingStart()
        {
            long id; string file, station; float gain; double off; int gen; bool openFailed;
            lock (_startLock)
            {
                if (!_doneReady) return;
                _doneReady = false;
                id = _doneVoiceId; file = _doneFile; station = _doneStation;
                gain = _doneGain; off = _doneOffset; gen = _doneGen; openFailed = _doneOpenFailed;
            }
            _startPending = false;

            if (openFailed)
            {
                CMMConfig.D($"[CMM] Radio: track failed to open ({Path.GetFileName(file)}) — poisoning in schedule.");
                RadioStationCatalog.PoisonTrack(file);
                return;   // next tick re-resolves the shifted chain and dispatches again
            }
            if (id == 0) return;
            if (gen != _startGen || !_on || ContextMusicManager.playerIsDead)
            {
                // Stale: the dial moved (or the radio went off / hit the menu) while the start
                // was in flight. Mute the orphan (it retires at EOF); its add may have crossfaded
                // a legitimate voice down, so if the radio no longer owns the bus, hand music
                // back to the resolver once more.
                CmmMixer.Instance?.SetVoiceTrackGain(id, 0f, 0.1f);
                if (!SuppressesMusic) ContextMusicManager.ReResolveNow();
                return;
            }
            _voiceId = id; _track = file; _gainApplied = gain;
            _staticWanted = false;  // the dialed station is voicing — the tuning static ends
            StopStatic();
            _advanceCarry = null;   // consumed — the next idle start is a plain join
            ContextMusicManager.RequestPrewarm(file);   // settle the gain in later if unmeasured
            Log.Out($"[CMM] Radio: '{station}' playing {Path.GetFileName(file)} @ {off:0}s " +
                    $"(B={RadioStationCatalog.BroadcastSeconds():0}s).");
            // Sync audit, one-shot at the join: Δ here isolates the async-start latency (offset
            // was resolved at DISPATCH on the main thread; open+seek ran on a worker since).
            if (_tunedName != null && RadioStationCatalog.ByName.TryGetValue(_tunedName, out RadioStation ast))
                RadioStationCatalog.AuditVoice("personal-join", ast, file, id);
            _nextAuditAt = Time.realtimeSinceStartup + RadioStationCatalog.AuditInterval;
        }

        // Ramp the voice's track gain to 0 and forget it: silent immediately, retires itself at
        // EOF (loop:false). See the header for why there's no per-voice remove here. Also
        // invalidates any in-flight async start — every "the wanted voice changed" path
        // (retune, off, menu, hot-swap) comes through here.
        private static void FadeVoiceOut(float fade)
        {
            _startGen++;
            if (_voiceId == 0) return;
            CmmMixer.Instance?.SetVoiceTrackGain(_voiceId, 0f, fade);
            _voiceId = 0;
            _track = null;
        }

        // ── Loudness integration (called from the shared loudness pipeline, all versions) ──────

        // A measurement for `file` just landed — ease it into the live radio voice if it's airing.
        internal static void SettleLoudness(string file)
        {
            if (_voiceId == 0 || string.IsNullOrEmpty(file)) return;
            if (!string.Equals(_track, file, StringComparison.OrdinalIgnoreCase)) return;
            float g = LoudnessStore.GetTrackGain(file);
            if (Mathf.Abs(g - _gainApplied) < 1e-4f) return;
            _gainApplied = g;
            CmmMixer.Instance?.SetVoiceTrackGain(_voiceId, g, 0.2f);
        }

        // Protect the airing track + the tuned station's deterministic up-next from loudness
        // eviction (mirrors the emitter stations' keep-set).
        internal static void AppendLoudnessKeep(List<string> dst)
        {
            if (!_on) return;
            if (_track != null) dst.Add(_track);
            if (_tunedName == null || !RadioStationCatalog.ByName.TryGetValue(_tunedName, out RadioStation st)
                || !st.Ready) return;
            int ahead = CMMConfig.LoudnessMeasureAhead;
            if (ahead < 0) ahead = 0;
            if (!RadioStationCatalog.ResolveAt(st, RadioStationCatalog.BroadcastSeconds(),
                                               out string onAir, out int idx, out _)) return;
            dst.Add(onAir);
            for (int k = 1; k <= ahead && k < st.Order.Length; k++)
                dst.Add(st.Order[(idx + k) % st.Order.Length]);
        }

        // ── Native toolbelt notification (reflection-bound; see header) ─────────────────────────

        private static MethodInfo _tooltip;
        private static object[] _tooltipArgs;
        private static bool _tooltipProbed;

        private static void Notify(EntityPlayerLocal player, string text)
        {
            CMMConfig.D($"[CMM] {text}");
            try
            {
                if (!_tooltipProbed)
                {
                    _tooltipProbed = true;
                    // Fewest-parameter static ShowTooltip(EntityPlayerLocal, string, ...) wins;
                    // trailing parameters are filled from their declared defaults (or type
                    // defaults), so any overload shape a version ships works.
                    foreach (MethodInfo m in typeof(GameManager).GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "ShowTooltip") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length < 2
                            || ps[0].ParameterType != typeof(EntityPlayerLocal)
                            || ps[1].ParameterType != typeof(string)) continue;
                        if (_tooltip != null && ps.Length >= _tooltip.GetParameters().Length) continue;
                        _tooltip = m;
                    }
                    if (_tooltip != null)
                    {
                        ParameterInfo[] ps = _tooltip.GetParameters();
                        _tooltipArgs = new object[ps.Length];
                        for (int i = 2; i < ps.Length; i++)
                            _tooltipArgs[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                                : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                        // PRE-EMPT, DON'T QUEUE. The game's tooltip is a FIFO: each dial press
                        // enqueued another line and every one of them served its full dwell, so
                        // spinning through six stations left the last five reading out one after
                        // another long after the dial had stopped -- the text lagging the radio.
                        // _showImmediately replaces what is on screen instead of falling in behind
                        // it (XUiC_PopupToolTip.QueueTooltipInternal: it sets immediateTip and
                        // displays at once rather than enqueuing), which is what a dial readout
                        // should do -- only the CURRENT station is ever worth reading.
                        //
                        // Found by NAME rather than by position because the overload shape differs
                        // across versions, and absent entirely on A20/A21, where the two-parameter
                        // ShowTooltip is all there is and no public API dismisses the current tip.
                        // Those two keep the old queueing behaviour; 2.6 and 3.x pre-empt.
                        for (int i = 2; i < ps.Length; i++)
                            if (ps[i].ParameterType == typeof(bool) && ps[i].Name == "_showImmediately")
                                _tooltipArgs[i] = true;
                    }
                }
                if (_tooltip == null) return;
                _tooltipArgs[0] = player;
                _tooltipArgs[1] = text;
                _tooltip.Invoke(null, _tooltipArgs);
                _tooltipArgs[0] = null;   // don't pin the player entity
            }
            catch { /* a missing tooltip must never break the radio */ }
        }
    }
}
