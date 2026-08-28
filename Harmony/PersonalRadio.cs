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
        // The handover crossfade. Long enough not to click, short enough to finish well inside
        // the static it hides under.
        private const float SwapFade = 0.25f;
        private const float AdvanceFade = 0.2f;    // EOF → next track (near-seamless)
        private const float SilenceFade = 0.4f;    // off-air / world-exit fade to silent

        private static volatile bool _on;          // volatile: the async start worker reads it (pre-Play guard)
        // When the radio took the speaker, so TurnOff can push every resume clock forward by the
        // length of the broadcast (see ContextMusicManager.HoldResumeClocks). -1 = not suppressing.
        private static float _suppressedAt = -1f;
        private static string _tunedName;          // survives the station being off air
        private static long   _voiceId;
        // THE VOICE Tune LET GO OF, AND WHY IT HAS TO BE REMEMBERED.
        // ReleaseVoiceStillPlaying clears _voiceId on purpose (that is what moves the state
        // machine into "searching") and the released station carries on playing, buried under the
        // hiss, until the NEXT station's add crossfades it out. If no next station ever arrives --
        // dial fast enough and none of them lands, then roll onto Off -- nothing retires it, and
        // TurnOff's FadeVoiceOut sees _voiceId == 0 and does nothing. Off then disarms the
        // receiver, which removes the only thing hiding it: the level law cuts a zero-signal
        // programme by RadioRecvFloorDb (5 dB) and no more, deliberately, so the station is
        // revealed at ~56% and plays its whole track with the radio showing Off.
        // Reported as "reach Radio Off and the music just keeps on playing".
        private static long   _releasedVoiceId;
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

        // ── THE RECEIVER (spec: docs/plan-radio-tuning.md) ──────────────────────────────────────
        // One signal value, 0..1, mirrored on both sides of the boundary: the native voice ramps
        // it per sample (gain + treble + width), and this side tracks the SAME linear ramp so it
        // can answer "where is it right now" when the player interrupts. Both move at the same
        // rate from the same instant, so the two agree to within a tick without anything being
        // read back across the boundary.
        //
        // Interrupting is the whole reason this is a rate rather than a duration. Press again half
        // way through a tune-in and the leave starts from a half — not from full, which would swell
        // upward before falling, and not over a fixed 260 ms, which would drag.
        private static float _sigFrom = 1f, _sigTo = 1f, _sigRate, _sigT0;
        private static float _searchUntil;
        // When the station being left hands over to the station being joined. Down in the middle
        // of the static, where neither of them is audible — the same place the sound preview
        // swaps its two source files.
        private static float _swapAt;
        // The same instant as _swapAt, in wall clock, because the worker thread that opens the
        // next station cannot read Unity's clock. Set together with it, always.
        private static DateTime _swapAtUtc;    // realtime the minimum static floor expires
        private static float _searchGiveUp;   // realtime the backstop abandons a wedged load
        private static bool  _arriving;       // the new voice exists and is being brought up

        private static float Signal
        {
            get
            {
                if (_sigRate <= 0f) return _sigTo;
                float d = (Time.realtimeSinceStartup - _sigT0) * _sigRate;
                return _sigTo > _sigFrom ? Mathf.Min(_sigTo, _sigFrom + d)
                                         : Mathf.Max(_sigTo, _sigFrom - d);
            }
        }

        private static float LeaveRate  => 1000f / Mathf.Max(20f, CMMConfig.RadioTuneLeaveMs);
        private static float ArriveRate => 1000f / Mathf.Max(20f, CMMConfig.RadioTuneArriveMs);

        // Aim the signal, and tell the voice to do the same. Starts from wherever the ramp has
        // actually reached, never from the last target.
        private static void AimSignal(long voiceId, float to, float ratePerSec)
        {
            _sigFrom = Signal; _sigTo = to; _sigRate = ratePerSec;
            _sigT0 = Time.realtimeSinceStartup;
            CmmMixer.Instance?.SetReceiverShape(CMMConfig.RadioRecvFloorDb, CMMConfig.RadioRecvMuteWidth,
                                                CMMConfig.RadioRecvDrive, CMMConfig.RadioRecvWander,
                                                CMMConfig.RadioRecvFlutter, CMMConfig.RadioRecvFlutterHz);
            CmmMixer.Instance?.SetReceiverTone(CMMConfig.RadioRecvKnee, CMMConfig.RadioRecvKneeWidth,
                                               CMMConfig.RadioRecvNoise, CMMConfig.RadioRecvClicks,
                                               CMMConfig.RadioRecvClickRate, CMMConfig.RadioRecvHiFloorHz,
                                               CMMConfig.RadioRecvDeemphHz);
            CmmMixer.Instance?.SetReceiverArrival(CMMConfig.RadioRecvNoiseHold,
                                                  CMMConfig.RadioRecvNoiseFloor,
                                                  CMMConfig.RadioRecvWindowA,
                                                  CMMConfig.RadioRecvWindowB,
                                                  CMMConfig.RadioRecvLockAt,
                                                  CMMConfig.RadioRecvLockMs);
            CmmMixer.Instance?.SetReceiverRadio(CMMConfig.RadioRecvMangle,
                                                CMMConfig.RadioRecvMakeupDb,
                                                CMMConfig.RadioRecvLowCutHz,
                                                CMMConfig.RadioRecvHighCutHz);
            // THE RECEIVER IS NOT A VOICE. It is armed for as long as the radio is on, so the
            // voice-less window between stations is covered by the SAME noise as either side of
            // it, and an instruction can no longer be dropped because the voice it named had
            // already retired — which is exactly how the radio went silent once before.
            CmmMixer.Instance?.SetReceiverSignal(to, ratePerSec,
                                                 CMMConfig.RadioTuneDullness,
                                                 CMMConfig.RadioTuneMononess,
                                                 _on, 1f);
            if (voiceId != 0) CmmMixer.Instance?.SetVoiceRadio(voiceId, true);
        }
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
        // Which way the dial was last turned. A station that fails to open seeks ON in the same
        // direction rather than reversing into the one the player was trying to leave.
        private static int _lastStepDir = 1;

        // THE DIAL IS A LIVING PLAYER'S CONTROL. Corpses do not tune radios, and the death
        // screen is exactly where a stray PgUp/PgDn lands. Gating here rather than inside the
        // keybind block means a future radio UI inherits the rule instead of having to
        // remember it — every PLAYER-initiated station change funnels through this method.
        // The mod's own auto-seek (a station that failed to open) calls StepInternal below and
        // is deliberately NOT gated: it is housekeeping, not input, and leaving it blocked
        // would strand the receiver in static until the player respawned.
        internal static void Step(int dir)
        {
            if (ContextMusicManager.playerIsDead)
            {
                CMMConfig.D("[CMM] Radio: dial ignored - player is dead.");
                return;
            }
            StepInternal(dir);
        }

        internal static void StepInternal(int dir)
        {
            if (!ContextMusicManager.inGame) return;
            if (dir != 0) _lastStepDir = dir > 0 ? 1 : -1;
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
            // Disarm the receiver, or its noise outlives the radio. Off is not a station and gets
            // no tuning — rolling onto Off goes silent at once.
            CmmMixer.Instance?.SetReceiverActive(false, 1f);
            // OFF IS NOT A STATION, SO IT GETS NO TUNING. Chris's call: rolling onto Off goes
            // silent at once — no leave ramp, no static, nothing to search for. The plain fade
            // below is short enough to read as immediate and long enough not to click.
            //
            // Reset the receiver so the next station starts from nothing. That is also what gives
            // "rolling off Off onto a station" its no-leave-phase behaviour for free: the leave
            // is computed from the current signal, and the current signal is zero.
            _sigFrom = _sigTo = 0f; _sigRate = 0f; _sigT0 = Time.realtimeSinceStartup;
            _arriving = false; _searchUntil = 0f; _searchGiveUp = 0f; _swapAt = 0f;
            Save();
            RefreshOverlayCaches();
            FadeVoiceOut(CMMConfig.ManualSkipFadeDuration);
            Sfx(CmmMixer.SfxClickOff);   // the power-down clunk (manual dial AND the all-stations-deleted auto-off)
            Notify(player, notice);
            // The broadcast borrowed the speaker; it did not move you out of any context. Un-pause
            // the resume clocks BEFORE re-resolving, or the restart below reads a lastLeftTime
            // stamped back at the takeover and throws a perfectly good resume away as stale.
            if (_suppressedAt >= 0f)
            {
                ContextMusicManager.HoldResumeClocks(Time.realtimeSinceStartup - _suppressedAt);
                _suppressedAt = -1f;
            }

            // Hand music back: the resolver restarts whatever context should be playing.
            ContextMusicManager.ReResolveNow();
        }

        // ── Recorded click sounds (Sounds/radio_dial.wav, Sounds/radio_power.wav) ───────────────
        // The synthesized clicks stay in the build and stay the FALLBACK: if a file is missing,
        // unreadable, or an exotic WAV variant, the radio still clicks. A cosmetic asset must
        // never be able to make the mod look broken.
        //
        // Loaded once and re-loaded whenever settings reload, so a replaced file is heard without
        // a restart -- same contract as every other tunable here.
        private static bool _sfxSamplesLoaded;
        private static string _sfxSamplesFrom;

        // READ ONCE, HELD IN RAM, never re-read. Two files totalling 33 KB on disk and about 66 KB
        // as floats -- there is nothing to gain by going back to the filesystem, and a path that
        // re-reads is a path that can fail later, in front of a player, for a reason that has
        // nothing to do with what they were doing. Deliberately NOT hot-swappable; the tunables
        // around the sounds still are, which is where the tuning actually happens.
        //
        // The managed copies are kept so the recorded/synthesized toggle can flip back and forth
        // without touching disk again.
        private static readonly float[][] _sfxPcm = new float[3][];
        private static readonly int[] _sfxRate = new int[3];
        private static readonly string[] _sfxWhy = new string[3];

        internal static void LoadClickSamples()
        {
            try
            {
                CmmMixer mixer = CmmMixer.GetOrCreate();
                if (!_sfxSamplesLoaded)
                {
                    string dir = ContextMusicManager.ModPath;
                    if (string.IsNullOrEmpty(dir)) return;   // too early; retried on the next click
                    string sounds = Path.Combine(dir, "Sounds");
                    // One file serves both throws of the toggle; the per-kind Volume settings still
                    // separate them, and a switch does not sound meaningfully different on and off.
                    ReadInto(0, Path.Combine(sounds, "radio_power.wav"));
                    ReadInto(1, Path.Combine(sounds, "radio_power.wav"));
                    ReadInto(2, Path.Combine(sounds, "radio_dial.wav"));
                    _sfxSamplesLoaded = true;
                    _sfxSamplesFrom = dir;
                }
                ApplySampleMode(mixer);
            }
            catch (Exception e) { CMMConfig.D($"[CMM] radio click samples failed: {e.Message}"); }
        }

        // Push the cached PCM (or nothing, for the synthesized fallback) plus the live rate
        // variance. Cheap and idempotent, so a config change to either knob just calls it again.
        private static void ApplySampleMode(CmmMixer mixer)
        {
            mixer.SetSfxRateJitter(CMMConfig.RadioClickSampleJitterHz);
            for (int k = 0; k < 3; k++)
            {
                float[] pcm = CMMConfig.RadioClickSamples ? _sfxPcm[k] : null;
                mixer.SetSfxSample(k, pcm, pcm != null ? _sfxRate[k] : 0);
            }
        }

        private static void ReadInto(int kind, string path)
        {
            if (!File.Exists(path))
            {
                _sfxWhy[kind] = "missing";
                CMMConfig.D($"[CMM] radio click sample missing, synthesizing kind {kind}: {path}");
                return;
            }
            if (!ReadWav(path, out float[] pcm, out int rate) || pcm.Length < 2)
            {
                _sfxWhy[kind] = "unreadable";
                Log.Warning($"[CMM] radio click sample unreadable, synthesizing kind {kind}: {path}");
                return;
            }
            _sfxPcm[kind] = pcm; _sfxRate[kind] = rate; _sfxWhy[kind] = null;
            CMMConfig.D($"[CMM] radio click kind {kind}: {Path.GetFileName(path)} "
                      + $"({pcm.Length} frames @ {rate} Hz, {1000.0 * pcm.Length / rate:0} ms, in RAM)");
        }

        // ── In-game readout + audition (cmm sfx) ────────────────────────────────────────────
        // The tunables around the clicks are live, and the property that matters most — whether a
        // run of detents sounds like a mechanism or like one recording retriggered — can only be
        // judged by firing several in a row, which no amount of dialing lets you do on demand.
        private static int _spinLeft;
        private static float _spinNextAt;

        // Serviced by the radio tick: fired back-to-back in one frame the eight would pile into a
        // single noise rather than a run you can hear.
        private static void SpinTick()
        {
            if (_spinLeft <= 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < _spinNextAt) return;
            _spinNextAt = now + 0.16f;
            _spinLeft--;
            Sfx(CmmMixer.SfxClickDial);
        }

        internal static string TestClick(string what)
        {
            LoadClickSamples();
            switch (what)
            {
                case "on":   Sfx(CmmMixer.SfxClickOn);  return "fired: power ON";
                case "off":  Sfx(CmmMixer.SfxClickOff); return "fired: power OFF";
                case "spin": _spinLeft = 8; _spinNextAt = 0f;
                             return "firing eight detents over ~1.3 s - listen for whether they differ";
                default:     Sfx(CmmMixer.SfxClickDial); return "fired: dial detent";
            }
        }

        internal static string DescribeClicks()
        {
            LoadClickSamples();
            var sb = new System.Text.StringBuilder();
            sb.Append("radio clicks: ").Append(CMMConfig.RadioClickSamples ? "RECORDED" : "SYNTHESIZED");
            if (!CMMConfig.RadioSfxEnabled) sb.Append("   [RadioSfxEnabled false - silent]");
            sb.Append("\n  volume:    ").Append(CMMConfig.RadioSfxVolume.ToString("0.##"));
            sb.Append("\n  variance:  ").Append(CMMConfig.RadioClickSampleJitterHz.ToString("0"))
              .Append(" Hz per firing")
              .Append(CMMConfig.RadioClickSampleJitterHz <= 0f ? "  (every click identical)" : "");
            string[] names = { "radio_power.wav", "radio_dial.wav" };
            int[] kinds = { 0, 2 };
            for (int i = 0; i < names.Length; i++)
            {
                int k = kinds[i];
                sb.Append("\n  ").Append(names[i].PadRight(18));
                if (_sfxPcm[k] != null)
                    sb.Append(_sfxPcm[k].Length).Append(" frames @ ").Append(_sfxRate[k])
                      .Append(" Hz  (").Append((1000.0 * _sfxPcm[k].Length / _sfxRate[k]).ToString("0"))
                      .Append(" ms, in RAM)");
                else
                    sb.Append(_sfxWhy[k] ?? "not loaded").Append(" - synthesized instead");
            }
            return sb.ToString();
        }

        // Minimal RIFF/WAVE reader: 8/16/24/32-bit PCM and 32/64-bit float, any channel count,
        // mixed down to mono. Deliberately hand-rolled rather than routed through the decoder --
        // these are two tiny files read once, and the decoder is built around streaming a track
        // off disk with its own thread and lifetime. Chunks are walked rather than assumed, since
        // plenty of editors write LIST/fact chunks ahead of `data`.
        private static bool ReadWav(string path, out float[] mono, out int rate)
        {
            mono = null; rate = 0;
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 44 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') return false;
            if (b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E') return false;
            int ch = 0, bits = 0, fmt = 0, pos = 12, dataAt = -1, dataLen = 0;
            while (pos + 8 <= b.Length)
            {
                string id = "" + (char)b[pos] + (char)b[pos + 1] + (char)b[pos + 2] + (char)b[pos + 3];
                int len = BitConverter.ToInt32(b, pos + 4);
                if (len < 0 || pos + 8 + len > b.Length) len = b.Length - pos - 8;
                if (id == "fmt ")
                {
                    fmt  = BitConverter.ToUInt16(b, pos + 8);
                    ch   = BitConverter.ToUInt16(b, pos + 10);
                    rate = BitConverter.ToInt32 (b, pos + 12);
                    bits = BitConverter.ToUInt16(b, pos + 22);
                    if (fmt == 0xFFFE && len >= 40) fmt = BitConverter.ToUInt16(b, pos + 32);  // EXTENSIBLE
                }
                else if (id == "data") { dataAt = pos + 8; dataLen = len; }
                pos += 8 + len + (len & 1);        // chunks are word-aligned
            }
            if (dataAt < 0 || ch <= 0 || rate <= 0) return false;
            int bytes = bits / 8;
            if (bytes <= 0) return false;
            int frames = dataLen / (bytes * ch);
            if (frames < 2) return false;
            mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                double acc = 0;
                for (int c = 0; c < ch; c++)
                {
                    int o = dataAt + (f * ch + c) * bytes;
                    if (fmt == 3) acc += bytes == 8 ? BitConverter.ToDouble(b, o) : BitConverter.ToSingle(b, o);
                    else if (bits == 8)  acc += (b[o] - 128) / 128.0;
                    else if (bits == 16) acc += BitConverter.ToInt16(b, o) / 32768.0;
                    else if (bits == 24) acc += ((b[o] | (b[o + 1] << 8) | ((sbyte)b[o + 2] << 16))) / 8388608.0;
                    else if (bits == 32) acc += BitConverter.ToInt32(b, o) / 2147483648.0;
                }
                mono[f] = (float)(acc / ch);
            }
            return true;
        }

        // Fire one radio-UI one-shot (native synth; no asset files). Config-gated + hot-tunable;
        // the native side ensures the output device, so a power-on click can precede the radio
        // voice's own add, and it never touches the output gain (pause duck stays ducked).
        private static void Sfx(int kind, float scale = 1f)
        {
            if (!CMMConfig.RadioSfxEnabled) return;
            float vol = CMMConfig.RadioSfxVolume;
            try
            {
                CmmMixer mixer = CmmMixer.GetOrCreate();
                LoadClickSamples();   // reads the files once; after that it just re-pushes the knobs
                if (kind == CmmMixer.SfxTuningStatic)
                {
                    // The static has its own level (-1 = inherit) and a live tone: pushing the
                    // tone on EVERY ping is what makes radio.xml edits audible mid-hash.
                    if (CMMConfig.RadioStaticVolume >= 0f) vol = CMMConfig.RadioStaticVolume;
                    vol *= scale;   // the arrival fades the hash out from under the station
                    mixer.SetStaticTone(CMMConfig.RadioStaticLowHz,
                                        CMMConfig.RadioStaticHighHz,
                                        CMMConfig.RadioStaticCrackle);
                }
                else
                {
                    // Clicks likewise: their own levels (-1 = inherit) + the live tone pair,
                    // pushed before every click so a hot-reloaded edit lands on the next dial.
                    if (kind == CmmMixer.SfxClickOn   && CMMConfig.RadioClickOnVolume   >= 0f) vol = CMMConfig.RadioClickOnVolume;
                    if (kind == CmmMixer.SfxClickOff  && CMMConfig.RadioClickOffVolume  >= 0f) vol = CMMConfig.RadioClickOffVolume;
                    if (kind == CmmMixer.SfxClickDial && CMMConfig.RadioClickDialVolume >= 0f) vol = CMMConfig.RadioClickDialVolume;
                    mixer.SetClickToneKind(0, CMMConfig.RadioClickOnHz,   CMMConfig.RadioClickOnSnap,   CMMConfig.RadioClickOnLengthMs,
                                              CMMConfig.RadioClickOnBody,   CMMConfig.RadioClickOnBodyHz,   CMMConfig.RadioClickOnDouble,
                                              CMMConfig.RadioClickVary);
                    mixer.SetClickToneKind(1, CMMConfig.RadioClickOffHz,  CMMConfig.RadioClickOffSnap,  CMMConfig.RadioClickOffLengthMs,
                                              CMMConfig.RadioClickOffBody,  CMMConfig.RadioClickOffBodyHz,  CMMConfig.RadioClickOffDouble,
                                              CMMConfig.RadioClickVary);
                    mixer.SetClickToneKind(2, CMMConfig.RadioClickDialHz, CMMConfig.RadioClickDialSnap, CMMConfig.RadioClickDialLengthMs,
                                              CMMConfig.RadioClickDialBody, CMMConfig.RadioClickDialBodyHz, CMMConfig.RadioClickDialDouble,
                                              CMMConfig.RadioClickVary);
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
                // SAVE THE PLAYHEAD FIRST. StopNativeIfActive tears the voice down without
                // capturing where it had got to -- it nulls _nativeActiveContext and zeroes
                // _nativeVoiceId, and CaptureNativeResume needs BOTH of those to read the
                // mixer position. Every other way of leaving a context goes through
                // SendContextUpdate, which captures before it switches; the radio takeover was
                // the one path that did not, so dialing the radio off dropped you back into a
                // resumable context at the start of a DIFFERENT track instead of where you left
                // it. Capture is self-guarding (no-op unless the context is resumable and is the
                // one actually on the mixer), so this is safe regardless of what was playing.
                ContextMusicManager.CaptureNativeResume(ContextMusicManager.currentContext);
                _suppressedAt = Time.realtimeSinceStartup;

                // Take the music layer over: tear the context voice down properly (auto-advance,
                // transition bookkeeping). The radio's own Play would crossfade it anyway, but the
                // context system must KNOW its voice is gone.
                ContextMusicManager.StopNativeIfActive(CMMConfig.ManualSkipFadeDuration);
            }

            // Retune = RELEASE the old voice now (it keeps playing, buried) and let the tick
            // dispatch the ASYNC start (open + seek on a worker — see StartFromClock). A keypress
            // never blocks the frame, and rapid dialing just re-aims the single in-flight start
            // because ReleaseVoiceStillPlaying bumps _startGen exactly as FadeVoiceOut does.
            // ── LEAVE ───────────────────────────────────────────────────────────────────────
            // The receiver takes the signal down, which dulls and narrows the old station on its
            // way out rather than just turning it down. The gain fade runs alongside over the
            // SAME time so the voice actually retires — computed from where the signal is now, so
            // interrupting a half-finished arrival fades from half and takes half as long.
            float leaveSec = Mathf.Max(0.02f, Signal / LeaveRate);
            AimSignal(_voiceId, 0f, LeaveRate);
            // THE STATION BEING LEFT KEEPS PLAYING. It is not faded and it is not stopped — the
            // receiver buries it, which is a different thing and the only one that sounds right.
            // Cutting it was the single worst defect in the old behaviour: loading the next
            // station was also what killed this one, so the moment the file opened (~150 ms in,
            // long before the static is loud enough to hide anything) the old track vanished
            // mid-note and the new one appeared in the clear. Click, silence, then static, then
            // a tune-in on a station you could already hear.
            //
            // So: released rather than faded. It plays on, sinking under the rising hiss, until
            // the handover retires it at _swapAt.
            ReleaseVoiceStillPlaying();
            _nextStartFade = SwapFade;   // a real crossfade, spent entirely underneath the static
            _nextTick = 0f;              // dispatch next frame, not in ≤250 ms

            // ── SEARCH ──────────────────────────────────────────────────────────────────────
            // A floor on the static, restarted by every PRESS (so hammering the key gives one
            // continuous search that settles a beat after you stop) but NOT by the auto-advance
            // that skips a dead station — three dead folders in a row must not cost three floors.
            // ANCHORED AFTER THE LEAVE, NOT AT THE PRESS. Anchoring it at the press meant the
            // 1.2 s fade-out ate most of the 1.5 s floor and left about a third of a second of
            // actual static — a fifth of what was auditioned.
            float searchSec = CMMConfig.RadioTuneMinStaticMs / 1000f;
            _swapAt      = Time.realtimeSinceStartup + leaveSec + searchSec * 0.5f;
            _swapAtUtc   = DateTime.UtcNow.AddSeconds(leaveSec + searchSec * 0.5f);
            _searchUntil = Time.realtimeSinceStartup + leaveSec + searchSec;
            _searchGiveUp = Time.realtimeSinceStartup + CMMConfig.RadioTuneGiveUpMs / 1000f;
            _arriving = false;
            // A CLICK ON EVERY DIAL MOVE, and the right KIND of click. Powering the radio on is a
            // toggle going over centre; turning the knob one station is a detent dropping into a
            // groove. They are different objects and they get different sounds. It fires BEFORE
            // _staticWanted below so the order reads the way the device would: click, hash, station.
            Sfx(wasOn ? CmmMixer.SfxClickDial : CmmMixer.SfxClickOn);
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
            SpinTick();   // services `cmm sfx test spin`
            // Async start results land on ANY frame (ahead of the 4 Hz throttle): the audio is
            // already playing the moment the worker's add returned — this is just bookkeeping,
            // but a stale orphan should be muted promptly.
            if (_doneReady) CommitPendingStart();

            if (Time.realtimeSinceStartup < _nextTick) return;
            _nextTick = Time.realtimeSinceStartup + TickInterval;

            // ARM THE RECEIVER HERE, ABOVE EVERY RETURN BELOW. There are seven of them between
            // this point and the end of the method, and the first version of this push sat under
            // all of them — so during ordinary playback it effectively never ran. This is the
            // second time a receiver instruction has been put in dead code in this file; if you
            // are adding another one, put it here.
            //
            // The receiver is not a voice, so it does not inherit the duck a stinger applies to
            // everything else — Death and Spawn take the bus outright, and a roar continuing
            // underneath one would be worse than silence. The SIGNAL is never re-pushed on this
            // timer: it is ramped natively and re-stating its target would snap it.
            // A RECEIVER'S NOISE FLOOR IS FIXED. Level 1, deliberately — not the track's
            // loudness gain, which is what this was and which was wrong.
            //
            // The reasoning that put the gain here was that the programme carries it, so the noise
            // must follow to hold the ratio. That is true of a RAW programme and false of a
            // normalised one, and CMM normalises: every track is pulled toward the same loudness,
            // so the music comes out at the same level whatever its gain was, and scaling the
            // noise by that gain moves ONLY the noise. A quiet song needing +12 dB of boost got
            // +12 dB of static with it. Constant here means constant against a constant
            // programme, which is the ratio that was actually settled by ear. RadioRecvNoise is
            // the one knob that sets it.
            CmmMixer.Instance?.SetReceiverActive(
                _on && !ContextMusicManager.PlayOnceStingerActive, 1f);

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

            // ── THE RECEIVER RUNS BEFORE ANYTHING CAN RETURN ────────────────────────────────
            // This block used to sit at the BOTTOM of Tick, below the `if (_voiceId != 0) { …
            // return; }` immediately following. Two of its three branches require a live voice,
            // so they could only ever be evaluated when there wasn't one: dead code from the
            // moment it was written. Every station armed itself silent and nothing ever raised
            // it, which is 'the radio plays no music at all'. A follow-up fix added the RESCUE
            // into the same dead region and therefore changed nothing.
            //
            // It lives here now, and the ordering is deliberate: bring the station up FIRST,
            // then ping the static, so the tick that arrives doesn't fire one more burst of hash
            // immediately before stopping it.

            // ARRIVE. The station is open and holding silent, and the minimum has expired. Bring
            // it up — dull and mono under the static, opening out as the signal rises.
            if (_staticWanted && !_arriving && _voiceId != 0
                && Time.realtimeSinceStartup >= _searchUntil)
            {
                _arriving = true;
                _searchGiveUp = 0f;          // landed: the backstop must not fire later
                AimSignal(_voiceId, 1f, ArriveRate);
                CMMConfig.D($"[CMM] Radio: arriving — voice {_voiceId}, {CMMConfig.RadioTuneArriveMs:0} ms.");
                // NOTE: _staticWanted stays TRUE. The hash is not switched off in front of the
                // station; it fades out from under it, in the keepalive below. A station that
                // starts after the noise stops is a crossfade — the thing this is meant not to be.
            }
            // RESCUE, and it is deliberately NOT part of the chain above. A voice sitting at zero
            // with nothing searching for it is a silent radio, which is a far worse failure than
            // a missing flourish — so it gets its own unconditional test rather than an `else if`
            // that a guard above can shadow. That is exactly how the first attempt was disabled.
            if (!_arriving && _voiceId != 0 && !_staticWanted)
            {
                _arriving = true;
                _searchGiveUp = 0f;
                AimSignal(_voiceId, 1f, ArriveRate);
            }
            // The backstop: a station that neither opened nor errored (a wedged file — a missing
            // one is handled by the failure path seeking on). Rather than sit in static for ever,
            // move along in the direction of travel.
            if (_staticWanted && _voiceId == 0 && _searchGiveUp > 0f
                && Time.realtimeSinceStartup >= _searchGiveUp)
            {
                Log.Warning($"[CMM] Radio: '{_tunedName}' did not open in " +
                            $"{CMMConfig.RadioTuneGiveUpMs:0} ms — seeking on.");
                _searchGiveUp = 0f;
                StepInternal(_lastStepDir >= 0 ? 1 : -1);
                return;
            }
            // Keep the tuning hash alive while a dial move is still searching. Native KEEPALIVE:
            // each ping extends ONE seamless hash rather than layering bursts. It must be above
            // the return too, or it stops the instant the voice lands and never covers the floor.
            // THE RECEIVER OWNS THE NOISE WHENEVER THERE IS A VOICE TO OWN IT. The station's
            // hiss is generated inside its own signal path now, through the same filter and the
            // same stereo width, so pinging the separate hash on top would put the two-objects
            // problem straight back. This keepalive is for the genuinely voice-less window only:
            // after the old station has gone and before the new one has opened.
            // THE HASH IS GONE. There is one receiver and it is armed for as long as the radio
            // is on, so the voice-less window between stations is covered by the same noise as
            // everything either side of it. It used to be filled by a separate generator — fixed
            // band-pass, sample-and-hold sputter, no de-emphasis, no wander, no clicks — pinged
            // every 250 ms, which is a different sound arriving in the middle of the one event
            // this whole system exists to make continuous.
            if (_staticWanted && _voiceId != 0) _staticWanted = false;

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

            // Idle but on (world entry with a persisted on-state, a gain-wait retry, or it just
            // came back on air): join the broadcast. The carry re-arms a gated EOF advance.
            //
            // DISPATCHED AT THE PRESS, not at the handover. Holding the whole start until _swapAt
            // meant the file's open and seek were spent AFTER the static had already run, so every
            // change cost the load time on top of the timings — dead air nobody asked for. The
            // open happens during the leave now, hidden under the station going out, and only the
            // ADD waits (see the worker). What is left is the seek, which is the small half.
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
                    _gainWaitUntil = Time.realtimeSinceStartup + CMMConfig.LoudnessStartHoldMs / 1000f;
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
                        if (total > 0 && startFrame >= total) startFrame = 0;
                        // HOLD THE ADD UNTIL THE HANDOVER. The add is what retires the outgoing
                        // station -- cmm_mix_add crossfades every other music voice out -- so
                        // doing it as soon as the file is open is what used to cut that station
                        // off in the clear, before the static was loud enough to hide anything.
                        // Waiting here rather than deferring the whole dispatch keeps the open
                        // (the slow half) underneath the leave, so the change costs no more than
                        // the timings say it should.
                        //
                        // Wall clock, not Unity's: this is a ThreadPool thread. Re-checks the
                        // generation throughout so a second dial press abandons this one promptly
                        // instead of adding a station nobody is waiting for any more.
                        while (DateTime.UtcNow < _swapAtUtc)
                        {
                            if (System.Threading.Volatile.Read(ref _startGen) != gen) break;
                            System.Threading.Thread.Sleep(5);
                        }
                        // OFF IS ALSO A REASON NOT TO ADD. Checking only the generation left a
                        // window: TurnOff bumps _startGen, but between this guard and the add below
                        // the dial can reach Off and the add lands anyway. It was survivable while
                        // commit could still find the voice; it is not once the hand-off slot has
                        // been overwritten (see the orphan mute below), because then nothing knows
                        // the voice exists and it plays its whole track at full gain.
                        if (System.Threading.Volatile.Read(ref _startGen) != gen || !_on
                            || ContextMusicManager.playerIsDead)
                        {
                            AudioDecoder.CloseNativeHandle(dec, kind);
                            dec = IntPtr.Zero;
                        }   // clock/length mismatch safety
                        // Play crossfades every other music voice out (the retune/takeover
                        // transition) and runs the resume SEEK on this worker thread.
                        if (dec != IntPtr.Zero)
                            id = mixer.Play(dec, kind, srcRate, srcCh, total, fade,
                                            loop: false, startFrame, trackGain,
                                            duckOthersTo: -1f, snapTight: !advance);
                    }
                }
                catch { /* commit treats id 0 without openFailed as a no-op */ }
                long orphan = 0;
                lock (_startLock)
                {
                    // THE HAND-OFF IS ONE SLOT AND THIS OVERWRITES IT. `_startPending` is supposed
                    // to keep a single start in flight, but its 10 s hang safety deliberately lets
                    // a second dispatch go out when an open runs long -- a big FLAC off a network
                    // drive does, and rapid dialling is how you get there. Both workers then reach
                    // here, and the first one's voice id was silently dropped: CommitPendingStart
                    // only ever sees the LAST write, so nothing muted it and it played its whole
                    // track at full gain. Reported as "reach Radio Off and the music just keeps on
                    // playing", which is exactly what it is.
                    //
                    // An un-consumed slot holding a live voice is that case. Mute it here rather
                    // than dropping it. SetVoiceTrackGain goes through the mixer's own lock, so
                    // it is safe from this thread, and a muted loop:false voice retires itself at
                    // EOF -- the same disposal every other orphan in this file gets.
                    if (_doneReady && _doneVoiceId != 0) orphan = _doneVoiceId;
                    _doneVoiceId = id; _doneFile = fileCap; _doneStation = stName;
                    _doneGain = trackGain; _doneOffset = offCap; _doneGen = gen;
                    _doneOpenFailed = openFailed;
                    _doneReady = true;
                }
                if (orphan != 0)
                {
                    CmmMixer.Instance?.SetVoiceTrackGain(orphan, 0f, 0.1f);
                    Log.Warning($"[CMM] Radio: two starts were in flight — muted the orphaned voice {orphan}.");
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
                CMMConfig.D($"[CMM] Radio: discarding a start the dial outran "
                          + $"({(gen != _startGen ? "dial moved" : !_on ? "radio off" : "player dead")}) "
                          + $"— muting voice {id}.");
                CmmMixer.Instance?.SetVoiceTrackGain(id, 0f, 0.1f);
                if (!SuppressesMusic) ContextMusicManager.ReResolveNow();
                return;
            }
            // The add just crossfaded the released station out for real, so it is no longer ours.
            _releasedVoiceId = 0;
            _voiceId = id; _track = file; _gainApplied = gain;
            // ── ONLY A DIAL MOVE GETS TUNED IN ─────────────────────────────────────────────
            // Most voice starts are not the player turning a knob: the radio being restored on
            // world entry, a track ending and the next one joining, a resync after a pause. Those
            // must come up at full signal immediately — there is nothing being tuned, and putting
            // the ceremony on them would dull and quieten the start of every single track.
            //
            // A DIAL MOVE (_staticWanted, set by Tune) holds at signal 0 instead: open, decoding,
            // in step with the broadcast clock, and silent, while the static keeps rolling. The
            // tick starts the arrival once the minimum floor expires. That is what makes "it was
            // already loaded" and "it took a second" sound the same — the floor is spent either
            // way and the station is genuinely there throughout rather than being started late.
            bool tuning = _staticWanted;
            CmmMixer.Instance?.SetVoiceRadio(id, true);
            // The voice only carries programme now. The SIGNAL is the receiver's, so a dial move
            // does not re-aim it here — it is already sitting at 0 from the leave, and re-pushing
            // it per voice is what used to produce a second full-scale roar the moment the new
            // station finished opening. A non-dial join (world entry, a track ending, a resync
            // after a pause) still snaps the receiver to fully tuned, because nothing is being
            // tuned and putting the ceremony on those would dull the start of every track.
            if (!tuning) AimSignal(0, 1f, 0f);
            _sigFrom = _sigTo = tuning ? 0f : 1f;
            _sigRate = 0f; _sigT0 = Time.realtimeSinceStartup;
            _arriving = !tuning;
            _advanceCarry = null;   // consumed — the next idle start is a plain join
            ContextMusicManager.RequestPrewarm(file);   // settle the gain in later if unmeasured
            Log.Out($"[CMM] Radio '{station}': {Path.GetFileName(file)}");
            CMMConfig.D($"[CMM] Radio: joined @ {off:0}s "
                      + $"(B={RadioStationCatalog.BroadcastSeconds():0}s).");
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
            // The released station too. This is the "stop being responsible for any radio audio"
            // path (TurnOff, ResyncToClock, world exit), and a voice Tune let go of is still ours
            // until an incoming add retires it. Deliberately NOT done in Tune -- a retune must
            // leave the old station playing under the static, which is the whole leave.
            if (_releasedVoiceId != 0)
            {
                CmmMixer.Instance?.SetVoiceTrackGain(_releasedVoiceId, 0f, fade);
                CMMConfig.D($"[CMM] Radio: retiring the released voice {_releasedVoiceId} (no station ever landed).");
                _releasedVoiceId = 0;
            }
            if (_voiceId == 0) return;
            CmmMixer.Instance?.SetVoiceTrackGain(_voiceId, 0f, fade);
            _voiceId = 0;
            _track = null;
        }

        // RELEASE THE VOICE WITHOUT SILENCING IT. Everything FadeVoiceOut does except the part
        // that makes it inaudible: the station carries on playing at full level and the receiver
        // buries it under the rising hiss, which is the whole point of the leave. It is retired
        // for real when the incoming station's add crossfades it out at _swapAt, down in the
        // static where the swap cannot be heard.
        //
        // Clearing _voiceId is NOT incidental — it is what moves the state machine into
        // "searching". Tick returns early whenever a voice is live (PersonalRadio's dispatch for
        // the next station sits in the _voiceId == 0 branch), so a leave that keeps the id would
        // never load anything and the dial would simply stop working. That is exactly what
        // happened when this was first written as "delete the FadeVoiceOut call".
        private static void ReleaseVoiceStillPlaying()
        {
            _startGen++;
            if (_voiceId != 0)
            {
                // A burst of presses releases at most one real voice (the rest see _voiceId == 0),
                // but never drop one silently if it happens twice: the second would leak exactly
                // the way this whole field exists to stop.
                if (_releasedVoiceId != 0 && _releasedVoiceId != _voiceId)
                    CmmMixer.Instance?.SetVoiceTrackGain(_releasedVoiceId, 0f, 0.1f);
                _releasedVoiceId = _voiceId;
            }
            _voiceId = 0;
            _track = null;
        }

        // ── Loudness integration (called from the shared loudness pipeline, all versions) ──────

        // A measurement for `file` just landed — ease it into the live radio voice if it's airing.
        // rampSeconds is chosen by the caller from the SIZE of the correction (see
        // ContextMusicManager.SettleRamp): a first measurement replacing the unmeasured guess can
        // move the level by the full boost, while a progressive refinement moves it by a fraction
        // of a dB and gets a long glide so it is never heard happening. The old fixed 0.2 s was
        // right for one of those and wrong for the other.
        internal static void SettleLoudness(string file, float rampSeconds = 0.2f)
        {
            if (_voiceId == 0 || string.IsNullOrEmpty(file)) return;
            if (!string.Equals(_track, file, StringComparison.OrdinalIgnoreCase)) return;
            float g = LoudnessStore.GetTrackGain(file);
            if (Mathf.Abs(g - _gainApplied) < 1e-4f) return;
            _gainApplied = g;
            CmmMixer.Instance?.SetVoiceTrackGain(_voiceId, g, rampSeconds);
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
