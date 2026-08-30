// Gated to 3.0 (GV_V3): depends on EmitterRegistry (same gate).
#if GV_V3
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Station = CMM.RadioStation;   // catalog/schedule model is version-neutral (RadioStationCatalog)

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // WORLD-EMITTER AUDIO LAYER  (multi-station: the player's diegetic radio net)
    // ─────────────────────────────────────────────────────────────────────────
    // Turns detected in-world speakers (EmitterRegistry) into audible RADIO STATIONS. The station
    // MODEL — catalog (subfolders/playlists/root fallback), deterministic schedules, world-time
    // broadcast clock — lives in the version-neutral RadioStationCatalog (shared with the
    // personal radio, which runs on every game version); THIS layer is the 3.0-only spatial
    // consumer:
    //
    //   ASSIGNMENT — each detected emitter carries a stable StationHash (per-POI: every speaker
    //   in one POI shares it; player-placed radios hash their block position). The hash is PINNED
    //   to a station NAME on first sight (sorted list, hash % count) — a station folder removed
    //   mid-session leaves its radios SILENT (dead station) rather than retuning the world.
    //
    //   VOICING — up to CmmMixer.MaxEmitterSlots (native CMM_MAX_EMIT_VOICES) nearest audible
    //   stations play SIMULTANEOUSLY, each on its own native render slot (own ring/points/muffle/
    //   coloration state); an incumbent keeps its slot unless a challenger is decisively nearer
    //   (hysteresis). Reverb stays ONE shared room pair, fed per-slot carry sends.
    //
    // Each voice is an INDEPENDENT emitter layer that sums ALONGSIDE the normal soundtrack
    // instead of crossfading it. Main-thread only (world reads + registry poll + decoder opens).
    internal static class EmitterAudioLayer
    {
        // Max speaker positions fed to one emitter voice (matches native CMM_MAX_EMIT_PTS) and
        // max simultaneously-voiced stations (matches native CMM_MAX_EMIT_VOICES).
        private const int MaxPoints = RadioStation.MaxPoints;
        private const int MaxSlots  = CmmMixer.MaxEmitterSlots;

        // A challenger must be this many blocks nearer than the farthest incumbent to steal its
        // slot — otherwise two stations straddling a boundary would flap (every flap is a decoder
        // open + a possibly-far seek).
        private const float StealMargin = 5f;

        // How long a voiced station survives having NO tracked speaker before its voice is released.
        // Chunk-load flicker at the tracked fringe drops a POI from the registry for a few seconds
        // until its rescan; stopping instantly churned open+seek every ~10 s. The voice is silent
        // throughout (empty point set ⇒ native gains 0), so the grace costs one idle decode stream.
        private const float UnseenGrace = 12f;
        // How fast the receiver drops into search on a dial turn, and how slowly it locks back on.
        // Asymmetric on purpose: losing a station is abrupt (you turned the knob), finding one is
        // not. Both are short relative to the level hold they cover.
        // THE FOUR TIMINGS OF A RETUNE, all in radio.xml because this is tuned by ear and a
        // rebuild per adjustment is the wrong loop:
        //
        //   (all four now read the POCKET RADIO's RadioTune* knobs -- see LeaveRamp below.
        //    The WorldEmitter* entries remain in the schema so no existing file breaks, but
        //    nothing reads them: one instrument, one set of timings.)
        //   RadioTuneLeaveMs         how fast the station drops away into static
        //   WorldEmitterLevelHoldMs  the wait for the new track's measured level (it starts after)
        //   RadioTuneMinStaticMs     the shortest the dial can travel before anything is revealed
        //   RadioTuneArriveMs        how long the station takes to come out from under the noise
        //
        // The last one is the longest and was the one hardcoded, which is why "where do I change
        // the delay" kept landing on knobs that were not the one being heard.
        // A search arms from wherever the signal is, over the same span the radio's dial takes to
        // leave a station. The emitter used to snap there in 120 ms, which is why a borrow sounded
        // abrupt next to the pocket radio's unhurried slide off a station.
        private static float SearchRamp => CMMConfig.RadioTuneLeaveMs / 1000f;
        // The LOCK, and it is a FLOOR rather than the answer. Reported as "the static will stop
        // while the music is still fading in": a fixed 0.9 s beat any voice fade longer than that,
        // so the noise cleared and left the station still climbing underneath — a fade with
        // nothing over it, which is the exact failure v47 records against the personal radio. The
        // arrival now takes at least as long as the fade it is covering, plus a tail so the hiss
        // is still there when the music finishes arriving rather than ending with it.
        private static float ArriveRamp => CMMConfig.RadioTuneArriveMs / 1000f;
        // Added to the VOICE's own fade when that is longer than ArriveRamp, so the noise is still
        // there when the music finishes arriving rather than ending exactly with it.
        private const float ArriveTail = 0.35f;
        private static float _emPendingFade;
        // When each slot's receiver was last put into search, so the sweep can tell a slot that is
        // waiting from one that is stuck. Generous by design: releasing early is an audible cut,
        // releasing late is a second of extra hiss nobody will attribute to a bug.
        private static readonly float[] _slotArmedAt = new float[MaxSlots];
        private static readonly float[] _slotReleasing = new float[MaxSlots];
        // A lock deferred until the sweep has run its minimum, and the ramp it will use.
        private static readonly float[] _slotLockAt = new float[MaxSlots];
        private static readonly float[] _slotLockRamp = new float[MaxSlots];

        // ── THE SWEEP BORROW ────────────────────────────────────────────────────────────────
        // Retuning a speaker to a station that is ALREADY PLAYING somewhere else used to be
        // instant and silent, and it could not be otherwise: the receiver belongs to the slot,
        // which means to the station, so hissing it would hiss every other speaker on that
        // station — mid-song, for a station nobody touched.
        //
        // A slot is already the thing we need, though. It has its own receiver AND its own point
        // set, and since the points moved off the voice a slot with NO VOICE renders anyway, as
        // pure noise at its own speakers. So the retuned speaker borrows a spare slot: lifted out
        // of its station's point set, put alone on the borrowed one with the receiver searching,
        // and handed back when the sweep is done. Nothing else on that station is on the borrowed
        // slot, so nothing else hears it.
        //
        // Costs a slot for the length of a sweep. There are three; if all are busy at that moment
        // the fallback is the old behaviour — instant, no sweep — which is the honest failure for
        // "three stations are already playing around you".
        private static int _borrowSlot = -1;
        private static Vector3i _borrowPos;
        private static float _borrowUntil, _borrowReleaseAt, _borrowGiveUp;
        // The lane this borrow swaps its mirror to once the leave has finished, and when.
        private static int   _borrowMirrorTo = -1;
        private static float _borrowSwapAt;
        private static string _borrowTarget;
        // A station that never opens must not hold a slot for ever. Comfortably past the level
        // hold plus a decode plus a retry.
        // Long enough for the level hold plus opening a file, and no longer. Six flat seconds
        // was chosen when the wait could never be satisfied, so it was only ever a ceiling on a
        // hang; now that the station really does open underneath, this is a genuine deadline and
        // an over-long one is just hiss.
        private static float BorrowGiveUp =>
            CMMConfig.WorldEmitterLevelHoldMs / 1000f + 2.5f;

        // Has the station this speaker is joining got a voice yet?
        private static bool TargetAudible()
        {
            if (string.IsNullOrEmpty(_borrowTarget)) return true;
            for (int i = 0; i < _stations.Count; i++)
                if (string.Equals(_stations[i].Name, _borrowTarget, StringComparison.OrdinalIgnoreCase))
                    return _stations[i].VoiceId != 0;
            return true;   // station gone from the catalog: stop waiting for it
        }
        private static float _handledTuneAt = -999f;
        // The retune the borrow actually got a slot for. Distinct from _handledTuneAt, which
        // records the ones we have LOOKED at: when no slot was spare the borrow declines, and then
        // the station's own arm must still sweep or the retune lands silently.
        private static float _borrowOwnsTune = -999f;
        // How long a deliberate retune stays "recent". Long enough to cover the decision tick plus
        // the registry noticing the pin moved; far short of anything the world does on its own.
        // Generous, because it is the delay between the press and the START, not between the
        // press and the sound. One start is in flight at a time, so dialling quickly queues them:
        // three seconds was short enough that a press could miss its own window and arrive silent.
        private const float RetuneWindow = 8f;
        private const float ReleaseRamp  = 0.45f;
        // THE BACKSTOP, AND IT IS ONLY A BACKSTOP. Sized off LoudnessStartHoldMs -- the POCKET
        // RADIO's three-second hold -- this was seven seconds, and a search that reached it ran for
        // all of them: "the static feels like it goes on forever, 6-9 seconds at times". It also
        // changed how the noise SOUNDS, which is the other half of that report. The wander is an
        // AGC breathing as a signal fades, with a time constant meant for a receiver that sits
        // between stations; over a 250 ms sweep you never hear it move, and over seven it wanders
        // far enough down to vanish. The emitter has its own hold, and this is that plus room for
        // an open.
        private static float SearchGiveUp => CMMConfig.WorldEmitterLevelHoldMs / 1000f + 2f;

        // The station list lives on the shared catalog (RadioStation class, version-neutral); this
        // layer owns the emitter-side fields on it (VoiceId/Slot/points/acoustics).
        private static List<Station> _stations => RadioStationCatalog.Stations;

        private static readonly Station[] _slotOwner = new Station[MaxSlots];
        // Which station NAME last played on each native slot: a slot acquired by a DIFFERENT station
        // is flushed native-side first (its ring still holds up to a cushion of the previous
        // station's audio, which otherwise plays through the new station's speakers — the
        // "two stations through one radio" report). A same-name reacquire (track advance, airing-
        // file hot-swap) keeps the ring: that residual is the outgoing track's tail, wanted.
        private static readonly string[] _slotLastStation = new string[MaxSlots];

        // ── Name-pinned station assignment ──────────────────────────────────────────────────────
        // Speaker POSITION → station NAME, pinned the first time a position is seen (from the
        // sorted list, its entry's StationHash % count at that moment). Keyed by POSITION, not by
        // StationHash: overlapping POI prefabs (house + part_ sub-prefab + tiles) are scanned
        // progressively as the player approaches, and the position-dedup tiebreak can hand the
        // SAME radio a different hash seconds after it first voiced — a hash-keyed pin then
        // audibly retuned it mid-listen ("station comes in, immediately flips to another"). The
        // physical radio is what the listener perceives, so the pin sticks to it. Later ticks
        // resolve the pinned name through the catalog: a station folder REMOVED mid-session
        // leaves its radios silent (dead station) instead of retuning the world; a folder ADDED
        // doesn't reshuffle existing pins either. Cleared on world exit (the player == null
        // bail), so a re-entry recomputes from the then-current folder set — deterministic for
        // identical folders (the MP stance) — reviving/retuning dead stations.
        private static readonly Dictionary<Vector3i, string> _posPins = new Dictionary<Vector3i, string>();

        // What the layer currently plays at a speaker position, or null if it has not pinned one
        // yet. Read-only, for the tuner's "you are looking at X, it plays Y" line — it deliberately
        // does NOT consult the override, so a caller can show the underlying automatic choice.
        internal static string PinnedStationAt(Vector3i pos)
            => _posPins.TryGetValue(pos, out string name) ? name : null;

        // ── Placement fade-in ("birth" ramp) ────────────────────────────────────────────────────
        // First-seen time per tracked speaker position (station-agnostic — keyed by block pos). A
        // speaker that APPEARS while a station voice is already live fades in over the start fade
        // instead of popping (its published occlusion gain carries a decaying extra virtual distance).
        private static readonly Dictionary<Vector3i, float> _speakerFirstSeen = new Dictionary<Vector3i, float>();
        private static readonly List<Vector3i> _birthScratch = new List<Vector3i>();

        // ── Movement fade-out (occlusion rise limiter) ─────────────────────────────────────────
        // Last PUBLISHED occlusion extra per speaker, so a movement-driven RISE can be rate-limited
        // (WorldEmitterOcclusionFadeRate, virtual blocks/s). Door events bypass the cap; drops are
        // never capped. Keyed by block pos like the birth map; pruned with it.
        private struct PublishedExtra { public float Value; public float At; }
        private static readonly Dictionary<Vector3i, PublishedExtra> _publishedExtra = new Dictionary<Vector3i, PublishedExtra>();
        private static float _doorEventAt = -999f;   // last door toggle (see NotifyAcousticChange)
        // The _doorEventAt value ReportDoorLatency has already printed. Separate from the timestamp
        // itself so the diagnostic cannot expire the door exemption -- see ReportDoorLatency.
        private static float _doorLatencyLoggedFor = -999f;
        // A door moved just now, or is still moving. The field's slew stands aside for this: a
        // door is a discrete act the player performed and watched, and the sound belongs with the
        // latch rather than a second and a half later.
        internal static bool DoorMovingOrJustMoved =>
            EmitterAcoustics.AnyDoorAnimating || CmmClock.Now - _doorEventAt < 1.5f;

        // Decision + point-set refresh cadence. The listener itself is pushed every FRAME (pan and
        // distance already track smoothly); this governs how fast the OCCLUSION -- what a wall
        // between you and a speaker is doing -- catches up as you move, and it is the thing that
        // reads as "it takes a moment to settle on what I should be hearing".
        //
        // It was a hardcoded 4 Hz, chosen when a tick cost 6.1 ms. Measured 2026-08-23 after the
        // chunk-lookup hoist and the field reader's 3x redundant read were fixed, a tick costs
        // 2.1 ms -- so 10 Hz now costs 21 ms/s against the 24 ms/s that 4 Hz used to. The
        // responsiveness is bought with the optimisation rather than charged on top of it.
        //
        // Config, not a constant, because the right number is a judgement about feel and the only
        // instrument for it is a person sprinting at a speaker. Raise it if the shaping still lags;
        // watch EmitPoll in the perf line, which is what it costs.
        private static float TickInterval =>
            1f / Mathf.Clamp(CMMConfig.WorldEmitterTickHz, 1f, 30f);
        private const float DoorTickInterval = 0.05f;   // ~20 Hz while a door is mid-swing (tight fade/mute timing)
        private static float _nextTick;
        private static bool _unsupportedLogged;         // one-shot "needs 3.0+" notice when configured on an older game

        // ── Amortized occlusion flood ─────────────────────────────────────────────────────────
        // The tick only BEGINS the flood; every frame advances it by a small time slice
        // (FloodBudgetMs), and when it completes the finish lands EVERY culled station's gains +
        // points + the reverb push from the one finished field. No frame pays the full Dijkstra.
        private const float FloodBudgetMs = 2.5f;
        private const float DoorFloodBudgetMs = 16f;   // per-frame slice while a door is mid-swing
        private const float DoorRushBudgetMs = 32f;    // one-shot inline budget at tick start during a swing
        private static bool _occlPending;              // a begun flood is being stepped; finish not yet landed
        private static Vector3 _occlEar;               // the ear the flood was anchored at
        private static readonly List<Station> _occlStations = new List<Station>();   // stations culled for this flood
        private static bool  _forceRefresh;            // door toggled → run the next tick a frame early
        // Publish on the next frame rather than waiting for the 4 Hz tick. Called from the field's
        // flood when it lands during a door window: _forceRefresh is set at the LATCH, which
        // collapses the wait for a publish of the field as it was BEFORE the flood ran. The new
        // field then arrives ~100 ms later and, without this, sat until the next scheduled tick --
        // up to 250 ms of the door already being shut before it could be heard. Thread-safe by
        // being a bool the main thread clears; a missed write costs one tick, never correctness.
        internal static void RequestImmediatePublish() { _forceRefresh = true; }

        // MEASURE the door, do not estimate it. Every latency number I have produced tonight has
        // been arithmetic over a chain I did not time, and the chain has surprised me each round.
        // Logged once per door, on the first flood that lands after the latch: latch to field, in
        // milliseconds, with the frame time beside it so a slow frame is not read as slow code.
        internal static void ReportDoorLatency(float floodStartedAt)
        {
            if (_doorEventAt < 0f) return;
#if GV_V3
            // THE FLOOD MUST HAVE SEEN THE DOOR. The reader is main-thread, the flood is not, so a
            // flood can finish after a latch having solved entirely PRE-TOGGLE geometry. Reporting
            // that as the door's latency is what made every previous number meaningless. Only a
            // flood that STARTED after the door's cells finished entering the arena carries it;
            // any earlier one is skipped, and because the logged-marker is not set here, the next
            // qualifying flood still gets to report.
            float readAt = FieldOcclusion.DoorReadAt;
            bool verified = FieldOcclusion.DoorWaitUsable;
            if (verified)
            {
                if (readAt < 0f) return;                    // cells not all re-read yet
                if (floodStartedAt < readAt) return;        // this flood predates them
            }
#else
            float readAt = -999f; bool verified = false;
#endif
            if (_doorLatencyLoggedFor == _doorEventAt) return;
            _doorLatencyLoggedFor = _doorEventAt;

            float now = CmmClock.Now;
            float ms = (now - _doorEventAt) * 1000f;
            if (ms < 0f || ms >= 5000f) return;
            if (verified)
            {
                // The split is the point: read = latch to the door's geometry being in the arena
                // (main-thread budget), solve = the flood that then carried it (scheduler thread).
                float readMs = (readAt - _doorEventAt) * 1000f;
                float waitMs = (floodStartedAt - readAt) * 1000f;
                float solveMs = (now - floodStartedAt) * 1000f;
                CMMConfig.D($"[CMM] door -> occlusion: {ms:0} ms VERIFIED "
                          + $"(read {readMs:0} + wait {waitMs:0} + solve {solveMs:0}, "
                          + $"frame {Time.unscaledDeltaTime * 1000f:0} ms)");
            }
            else
            {
                CMMConfig.D($"[CMM] door -> occlusion: {ms:0} ms UNVERIFIED "
                          + $"(cell step != 1, cannot confirm the flood saw the door; "
                          + $"frame {Time.unscaledDeltaTime * 1000f:0} ms)");
            }
        }
        private static float _nextReverbLog;           // throttle for the reverb diagnostic (DebugLogging only)

        // ── Path / stability summary (DebugLogging only) ────────────────────────────────────────
        // One line per second answering: which occlusion path is actually rendering (the cached
        // per-speaker fields, which are continuous by construction, or the legacy ear-anchored
        // flood, whose boundary-blend branch is the known jitter source), WHY the field path was
        // refused if it was, and how much the measurement is actually moving tick to tick — raw
        // (as measured) versus published (after the rise limiter, which can mask it).
        private static int   _statFieldTicks, _statLegacyTicks;
        private static string _statWhy = "-";          // why the field path was refused, latest

        // ── MODEL PIN (`cmm acoustics model auto|native|legacy`) ──────────────────────────────
        // Occlusion is computed three different ways — native scene, per-speaker field cache,
        // listener flood — and the selection changes LIVE, per tick, on conditions like "a door is
        // animating" and "the scene is still settling". Nothing has ever checked that the three
        // AGREE, so every switch is a potential step in the player's ear, and a switch happens on
        // events the player is not thinking about. That is a prime suspect for "the level is all
        // over the place" and for "one fix breaks something else", and it cannot be settled by
        // reading code: it is settled by hearing each model alone.
        //
        // auto   = ship behaviour, all three, switched as before
        // native = the native scene model only; never hands back, even mid-swing (a door WILL sound
        //          wrong for a moment — that is the cost of the isolation, not a defect)
        // legacy = the shipped flood only; native and the field cache are both skipped
        //
        // Diagnostic only, never persisted: it resets to auto on every launch.
        internal static bool PinNative, PinLegacy, PinField;
        internal static string PinName =>
            PinField ? "field" : PinNative ? "native" : PinLegacy ? "legacy" : "auto";
        internal static void SetPin(string mode)
        {
            PinNative = mode == "native";
            PinLegacy = mode == "legacy";
            PinField  = mode == "field";
            // NOTE what this no longer does: it used to write PinField straight into
            // CMMConfig.WorldEmitterFieldOcclusion, which was fine while the field was an opt-in
            // experiment and a trap the moment it became the default -- `model auto` would have
            // switched the shipped model OFF and left it off. The config flag now means "the field
            // is the model"; a pin overrides it for as long as the pin is set. See FieldActive.
            if (!FieldActive) FieldOcclusion.Reset();
        }

        // IS THE FIELD THE MODEL RIGHT NOW? The config flag says what ships; an explicit pin wins
        // over it in both directions, so `model field` still forces it on for a config that has it
        // off, and `model native` / `model legacy` still force it off for one that has it on.
        internal static bool FieldActive =>
            PinField || (!PinNative && !PinLegacy && CMMConfig.WorldEmitterFieldOcclusion);
        private static float _statPubSum, _statPubMax;
        private static int   _statPubN;
        private static float _nextPathLog;

        // Reverb inertia: per-SLOT carry-send EMA (asymmetric — room character is EARNED by staying,
        // not granted by walking past a doorway), plus the shared local/room EMAs.
        private static readonly float[] _revWetSmooth = new float[MaxSlots];
        private static readonly float[] _revWets   = new float[MaxSlots];   // reused push buffers
        private static readonly float[] _revDepths = new float[MaxSlots];
        private static float _revLocSmooth;
        private static float _revRoomSmooth;
        private static float _lastRevPush;

        // True while any station voice is live. Read by the door hook so it only pokes when there's
        // something to update.
        internal static bool HasActiveVoice
        {
            get
            {
                // A SWEEP COUNTS. It renders on a slot with no station voice, so counting only
                // voices reports "nothing playing" mid-hiss — and SelectAndVoice passes exactly
                // this as `fresh:`, which fires ResetEmitterAcoustics across ALL slots (filter
                // state, smoothing, both reverbs re-inited inside the audio callback). Straight
                // through the sweep the player is listening to.
                if (_borrowSlot >= 0) return true;
                for (int i = 0; i < _stations.Count; i++) if (_stations[i].VoiceId != 0) return true;
                return false;
            }
        }

        // Called from the door Harmony hook (Patch_DoorAcousticChange) the instant a door's open/closed
        // state flips: collapse the up-to-250 ms tick wait so the occlusion re-floods with the door's
        // new state immediately.
        internal static void NotifyAcousticChange()
        {
            _forceRefresh = true;
            _doorEventAt = CmmClock.Now;
        }

        // ── Shared catalog forwarders ───────────────────────────────────────────────────────────
        // The catalog/schedule/broadcast model moved to the version-neutral RadioStationCatalog
        // (shared with PersonalRadio). Thin forwarders keep this file's call sites unchanged.
        private static double BroadcastSeconds() => RadioStationCatalog.BroadcastSeconds();
        private static void EnsureCatalog() => RadioStationCatalog.EnsureCatalog();
        private static void ResolveOnAir(Station st, double B) => RadioStationCatalog.ResolveOnAir(st, B);
        // AN ADVANCE IS A DECISION, AND IT HAS TO SURVIVE UNTIL IT IS ACTED ON.
        //
        // AdvanceStation works out what plays next and writes it to st.OnAir. RadioStationCatalog
        // does that carefully: when the broadcast clock is frozen or has drifted, the paper
        // schedule still names the track that just ENDED, so it deliberately steps to the next
        // entry at offset 0 instead of believing the paper.
        //
        // MaintainOnAir then overwrote it. It re-derives OnAir from the paper for any station with
        // no voice, every tick, and a station that has advanced but not yet STARTED has no voice —
        // so the paper's answer (the track that just ended, at its old offset) replaced the
        // considered one. The station replayed the tail it had just finished, hit EOF, advanced,
        // was clobbered again, and looped. In the log that is Enclave Radio restarting every 20.1
        // seconds, which is exactly the tail length.
        //
        // Any start that does not land inside one tick is exposed: an async open spanning a tick,
        // a start superseded by a slot steal — and, since today, the LOUDNESS LEVEL HOLD, which
        // returns early by design and made a rare race into the common case.
        //
        // So an advance pins OnAir for a bounded window. Bounded because a pin that could not
        // expire would strand a station on a track it never managed to start.
        private static readonly Dictionary<string, float> _onAirPinned =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private const float OnAirPinSeconds = 15f;   // > level hold + open + a steal retry

        private static void AdvanceStation(Station st, string endedTrack)
        {
            RadioStationCatalog.AdvanceStation(st, endedTrack);
            _onAirPinned[st.Name] = CmmClock.Now + OnAirPinSeconds;
        }

        private static bool OnAirPinned(Station st)
        {
            float until;
            if (!_onAirPinned.TryGetValue(st.Name, out until)) return false;
            if (CmmClock.Now < until) return true;
            _onAirPinned.Remove(st.Name);
            return false;
        }
        private static bool SafeExists(string file) => RadioStationCatalog.SafeExists(file);
        private static Contexts RadioContext() => RadioStationCatalog.RadioContext();
        private static float StartFade() => RadioStationCatalog.StartFade();

        internal static void Tick()
        {
            // Runtime version gate FIRST: the cross-version DLL carries this code onto A20/A21/2.6,
            // where the registry/acoustics paths below would bind 3.0-shaped game APIs. Bail before
            // touching anything (lazy JIT keeps the deeper methods from ever resolving there).
            if (!EmitterCompat.RuntimeSupported)
            {
                if (!_unsupportedLogged && CMMConfig.WorldEmitterEnabled && !string.IsNullOrWhiteSpace(CMMConfig.WorldEmitterSource))
                {
                    _unsupportedLogged = true;
                    Log.Out("[CMM] World-emitter radio requires 7 Days to Die 3.0+ — feature disabled on this game version.");
                }
                return;
            }

            if (!CMMConfig.WorldEmitterEnabled || string.IsNullOrWhiteSpace(CMMConfig.WorldEmitterSource))
            {
                StopAll();
                return;
            }

            // No local player (menu / loading / world teardown): stop and bail, so a stale registry
            // snapshot can't restart a station off the world. Station pins reset with the world —
            // re-entry recomputes them from the then-current catalog (see _posPins).
            EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null) { _posPins.Clear(); StopAll(); return; }

            // Per-frame (unthrottled) while any voice is live: keep the listener basis fresh so the
            // pan tracks head/body movement smoothly, and re-check the tracked speakers so smashing
            // the last radio cuts promptly. Voices are released ONLY when speakers are truly GONE —
            // walking out of range keeps them WARM (native distance falloff clamps them silent), so
            // re-entering range is seamless (no re-open + mid-song re-seek hitch).
            if (HasActiveVoice)
            {
                PushListener(player);
                World w = GameManager.Instance?.World;
                if (w != null)
                {
                    // Keeps destroyed blocks dropping out promptly; the audible cut lands via the
                    // next tick's point publish (empty set ⇒ native gains 0), and an ALL-speakers
                    // dropout goes through the per-station UnseenGrace instead of an instant StopAll
                    // (chunk flicker was churning voices).
                    EmitterRegistry.RefreshSnapshot(w, player.position);

                    // Advance the amortized occlusion flood by one time slice per FRAME; when it
                    // completes, land every culled station's output from the finished field —
                    // unless it was a ROOM-ONLY flood (field-cache path): its job is just the
                    // connected-air reverb measure, which FloodStep publishes on completion; the
                    // station gains came from the cached fields and must not be overwritten.
                    if (_occlPending && EmitterAcoustics.OcclusionStep(w, EmitterAcoustics.AnyDoorAnimating ? DoorFloodBudgetMs : FloodBudgetMs))
                    {
                        if (_floodRoomOnly) _occlPending = false;
                        else FinishOcclusion(player, w);
                    }
                    // Advance any in-flight per-speaker field build one slice (Phase 2 cache).
                    if (CMMConfig.WorldEmitterFieldCache) EmitterFieldCache.BuildStep(w);
                    // The native scene bakes door state into its voxel grid, so it goes stale on the
                    // same edge a cached field does. Watched HERE, per frame and independently of the
                    // field cache: a swing can start and settle between two 4 Hz ticks, and the
                    // native path must not depend on an unrelated feature being switched on.
                    AcousticBridge.WatchDoors(EmitterAcoustics.AnyDoorAnimating);
                    // KEEP re-reading while the leaf swings. CellLoss blends a door's loss by how
                    // far it has physically opened, so re-reading each frame makes the occlusion
                    // track the swing instead of stepping once it is over. A door dirties about
                    // 200 one-block cells, which is nothing against the frame's read budget.
                    if (EmitterAcoustics.AnyDoorAnimating)
                        EmitterAcoustics.InvalidateFieldForAnimatingDoors();
                    // Advance the native scene's world READ one slab (~1.5 ms). The read has to be
                    // here, on the main thread, because the game's chunks are not safe to touch from
                    // another one; only the solve that follows it runs on the scheduler.
                    AcousticBridge.FrameStep(w);
                    // The field's own world read, on the same main-thread budget. Cheap when the
                    // model is off (it returns immediately) and self-limiting when on.
                    FieldOcclusion.FrameStep(player, w, 1.5f);
                }
            }

            // A door toggled since the last tick → collapse the wait; an in-flight flood was built
            // from the PRE-toggle door state — abandon it so the restart samples the swing fresh.
            if (_forceRefresh) { _forceRefresh = false; _nextTick = 0f; _occlPending = false; _floodRoomOnly = false; }
            if (CmmClock.Now < _nextTick) return;
            EmitterBlockHook.NoteTickEntered();   // block stopwatch: the tick that carries it began
            _nextTick = CmmClock.Now + (EmitterAcoustics.AnyDoorAnimating ? DoorTickInterval : TickInterval);
            // Everything below is what WorldEmitterTickHz multiplies. Measured here and NOT via
            // EmitPoll, which is the registry's own 4 Hz loop and is unaffected by the tick rate.
            using (CMMPerf.Measure(CMMPerf.Phase.EmitTick)) {

            // ── 1 Hz path/stability summary ─────────────────────────────────────────────────────
            // path=  which occlusion path landed, in ticks over the last second (field = cached
            //        per-speaker fields, continuous by construction; legacy = ear-anchored flood).
            // why=   why the field path was refused on the last refusal.
            // br=    per-speaker estimator tallies: R reached the field (stable), B boundary blend
            //        (the documented lottery), F no route at all (straight ray).
            // rawJit/pubJit = mean and max change in a speaker's virtual distance between
            //        consecutive measurements, in BLOCKS, before/after the rise limiter. Big numbers
            //        on B are the smoking gun for "volume changes when it shouldn't".
            // fields=ready/wanted — the field path needs ALL of them; builds run one at a time.
            if (CMMConfig.DebugLogging && CmmClock.Now >= _nextPathLog)
            {
                _nextPathLog = CmmClock.Now + 1f;
                int n = EmitterAcoustics.StatSamples;
                float rawAvg = n > 0 ? EmitterAcoustics.StatJitterSum / n : 0f;
                float pubAvg = _statPubN > 0 ? _statPubSum / _statPubN : 0f;
                CMMConfig.D($"[CMM] EmitPath: field={_statFieldTicks} legacy={_statLegacyTicks} why={_statWhy} " +
                            $"br R={EmitterAcoustics.StatBrR} B={EmitterAcoustics.StatBrB} F={EmitterAcoustics.StatBrF} " +
                            $"rawJit avg={rawAvg:0.0} max={EmitterAcoustics.StatJitterMax:0.0} blk " +
                            $"pubJit avg={pubAvg:0.0} max={_statPubMax:0.0} blk " +
                            $"fields={EmitterFieldCache.StatReady}/{EmitterFieldCache.StatWanted} " +
                            $"building={EmitterFieldCache.StatBuilding} built={EmitterFieldCache.StatBuilds} " +
                            $"lastBuildCpuMs={EmitterFieldCache.StatLastBuildMs:0} doorDrops={EmitterFieldCache.StatDoorDrops} " +
                            $"floodR={CMMConfig.WorldEmitterFloodRadius} range={CMMConfig.WorldEmitterRange:0}" +
                            // RING HEALTH PER SLOT — queued frames / frames lost to starvation.
                            // Nothing watched these. The music ring's counters (which the HUD does
                            // show) say "underruns 0, queued at target" while a radio lane is
                            // starving, because the lanes are separate rings and only the music one
                            // was ever instrumented. If `u` climbs, the dip is the audio pipeline
                            // and no occlusion setting will touch it.
                            $" | rings {RingHealth()}" +
                            // The clamp counters, in the 1 Hz line, because they are the difference
                            // between "the model is quiet here" and "the model is switched off here"
                            // and I had to ask for them by hand three times before getting one.
                            (CMMConfig.WorldEmitterNativeAcoustics
                                ? $" | ac gain={AcousticBridge.GainPoints} sat={AcousticBridge.SaturatedPoints}"
                                  + $" pts={AcousticBridge.PublishedPoints} scene={AcousticBridge.LastStatus}"
                                  + $"\n[CMM]   ac nearest: {AcousticBridge.DescribeNearest()}"
                                : "")
                            // The field model's own line. Deliberately separate from the native
                            // model's: one readout serving two models is how "ac nearest" came to
                            // describe a speaker ninety blocks away for a week.
                            + (FieldActive
                                ? $"\n[CMM]   field: {FieldOcclusion.Status()}"
                                  + $"\n[CMM]   field nearest: {FieldOcclusion.DescribeNearest()}"
                                : ""));
                _statFieldTicks = _statLegacyTicks = 0;
                _statPubSum = _statPubMax = 0f; _statPubN = 0;
                EmitterAcoustics.StatReset();
                EmitterFieldCache.StatDrain();
            }

            // Push the live spatial-curve tuning + radio master from config (hot-reloaded).
            PushTuning();
            PushReceiverTuning();
            PushEmitterMaster();

            EnsureCatalog();        // stations from the Radio source's subfolders (throttled disk stat)
            if (_stations.Count == 0) { StopAll(); return; }

            EmitterRegistry.Poll();
            MaintainBirths();       // stamp newly-appeared speakers / prune gone ones (placement fade-in)
            // THE SAVED TUNINGS, BEFORE ANYTHING IS PINNED. Reported as "sometimes the radio
            // starts on the wrong station, then goes to the one I set before I quit".
            //
            // EmitterTuner loads Cache/emitters.xml lazily, and the only two things that used to
            // trigger it were tuning a speaker and LOOKING at one. The audio layer consults
            // TryPin without ever asking for the file, and TryPin answers "no override" whenever
            // the map is empty -- which, on a fresh world entry, it always is. So the first
            // GroupSnapshot fell through to the derived per-POI assignment, cached it in _posPins
            // and started that station; the overrides only arrived when the player happened to
            // aim at a radio, and the correction was the audible retune.
            //
            // It self-corrected because the override is consulted BEFORE _posPins, which is why
            // it read as a glitch rather than as lost settings. Loading here costs one early-out
            // per tick once the save is open.
            EmitterTuner.EnsureLoaded();
            // Decided BEFORE the snapshot, so this tick's point sets already reflect it.
            MaintainTune();
            if (_tunePhase == 0) MaintainSweepBorrow();
            GroupSnapshot();        // per-station nearest + culled point sets from the registry snapshot
            // AFTER GroupSnapshot so the landing voice is seeded from this tick's speaker
            // positions, and BEFORE the start/stop logic so a station that just landed a voice is
            // not started a second time.
            CommitPendingStart();   // land any station start whose open finished off-thread
            MaintainOnAir();        // keep idle nearby stations' on-air pointers current + prewarm loudness
            if (_clockResyncPending) { _clockResyncPending = false; ResyncVoicesToClock(player); }
            MaintainPlayingStations(player);   // airing-file-vanished interrupt + EOF advance
            SelectAndVoice(player); // K-nearest start/steal/stop
            ReleaseIdleReceivers(); // a slot nothing is tuned to must not keep hissing

            // The wet reverb reaches farther than the dry (range × ReverbRangeMult), so occlusion +
            // the room probe run out to whichever is larger; past that a warm voice keeps decoding
            // untouched (cheap — no flood, no probe).
            float audibleR = CMMConfig.WorldEmitterRange;
            if (CMMConfig.WorldEmitterReverbEnabled)
                audibleR *= Mathf.Max(1f, CMMConfig.WorldEmitterReverbRangeMult);

            // Split the voiced stations into the audible set (occlusion + points land) and the warm
            // set (publish the EMPTY point set: native gains go to 0 and the tail rings out — also
            // covers "the nearby radio you just smashed was this station's last audible one").
            bool anyAudible = false;
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.VoiceId == 0) continue;
                if (st.NearestDist <= audibleR) { anyAudible = true; continue; }
                CmmMixer.Instance?.SetSlotPoints(st.Slot, st.Pts, st.PtGain, st.PtFlags, 0);
                RecordPtPush(st, 0);
            }
            if (!anyAudible) { _occlPending = false; return; }

            World world = GameManager.Instance?.World;
            // ── PHASE 3 (native acoustics): the whole occlusion model, in C, off a scene bound to
            // GEOMETRY rather than to the ear. Sits AHEAD of both existing paths because it replaces
            // them outright — it is not a source of gains for the flood to blend, it is the flood's
            // successor. Anything it declines (warming up, door mid-swing, native DLL missing) falls
            // through to the shipped paths below unchanged, so the A/B is a live config flip.
            // ── THE FIELD MODEL ───────────────────────────────────────────────────────────────
            // Sits ahead of everything because it replaces all of it. It never hands back: there is
            // no door window, no settling state and no second model, which is the entire point --
            // the previous design's worst bugs were all in the handover, not in the physics. If a
            // flood has not landed yet it simply publishes nothing and the speakers stay where they
            // were, which is honest; a fallback that computes the same quantity differently is a
            // second model wearing a disguise.
            if (CMMConfig.WorldEmitterOcclusion && FieldActive && world != null
                && TryFieldModel(player, world, audibleR))
            {
                if (!_occlPending)
                {
                    _occlEar = ListenerWorldPos(player);
                    EmitterAcoustics.OcclusionBegin(_occlEar, world);
                    _occlPending = true;
                }
                _floodRoomOnly = true;      // keep the reverb's room measure running underneath
            }
            // THE COMMENT ABOVE WAS NOT TRUE OF THE CODE. "It never hands back ... if a flood has
            // not landed yet it simply publishes nothing" — but a false return fell through this
            // else-if chain into the native path or the field CACHE, which is exactly the handover
            // the design exists to delete, and every one of them is an audible step because the two
            // models do not agree on a number. Now a declined tick with the field pinned publishes
            // nothing and the speakers hold, which is what was written on the tin.
            else if (CMMConfig.WorldEmitterOcclusion && FieldActive && world != null)
            {
                _statLegacyTicks++;      // counted as a miss so the rate is visible in the log
            }
            else if (CMMConfig.WorldEmitterOcclusion && CMMConfig.WorldEmitterNativeAcoustics && world != null
                && !PinLegacy && !PinField
                && TryNativeOcclusion(player, world, audibleR))
            {
                // Keep the listener-anchored ROOM flood running underneath, exactly as the field
                // path does: AirRoomSize follows bends the ray probe cannot see, and the reverb
                // reads it. Room-ONLY — the native gains have already been published.
                if (!_occlPending)
                {
                    _occlEar = ListenerWorldPos(player);
                    EmitterAcoustics.OcclusionBegin(_occlEar, world);
                    _occlPending = true;
                }
                _floodRoomOnly = true;
            }
            else if (CMMConfig.WorldEmitterOcclusion && CMMConfig.WorldEmitterPathRouting && world != null)
            {
                // ── PHASE 2 (field cache): sample each speaker's CACHED static field instead of
                // re-flooding around the moving ear — spatial continuity by construction (the
                // offline judge measured −83…97% adjacent-cell jumps; tools/CmmAcousticsJudge).
                // Falls back to the legacy ear-anchored path while fields warm up, and ALWAYS
                // during a door swing — the tuned mid-swing acoustics live on the legacy path;
                // fields drop + rebuild once the swing settles (see EmitterFieldCache.Maintain).
                bool fieldOk;
                if (PinLegacy) { _statWhy = "pin-legacy"; fieldOk = false; }
                else if (!CMMConfig.WorldEmitterFieldCache) { _statWhy = "disabled"; fieldOk = false; }
                else if (EmitterAcoustics.AnyDoorAnimating) { _statWhy = "doorswing"; fieldOk = false; }
                else fieldOk = TryFieldOcclusion(player, world, audibleR);
                if (fieldOk) _statFieldTicks++; else _statLegacyTicks++;

                if (fieldOk)
                {
                    // Keep a listener-anchored ROOM flood running alongside the field samples:
                    // AirRoomSize follows bends the ray probe can't see, and losing it made caves
                    // read drier on the field path (user 2026-07-11). Room-ONLY: when it lands,
                    // only the Air* statics publish — never the station gains (see the stepper).
                    // An in-flight LEGACY flood is simply demoted to room-only (harmless: same
                    // field, we just skip its FinishSpeakers).
                    if (!_occlPending)
                    {
                        _occlEar = ListenerWorldPos(player);
                        EmitterAcoustics.OcclusionBegin(_occlEar, world);
                        _occlPending = true;
                    }
                    _floodRoomOnly = true;
                }
                // Routed occlusion = the expensive flood → AMORTIZE. ONE flood serves every audible
                // station (the field is per-ear; each station's speakers sample it independently in
                // the finish). If the previous tick's flood is still in flight, let it land first.
                else if (!_occlPending)
                {
                    _occlEar = ListenerWorldPos(player);
                    _occlStations.Clear();
                    int totalPts = 0;
                    for (int i = 0; i < _stations.Count; i++)
                    {
                        Station st = _stations[i];
                        // A station HOLDING A SLOT is published even with no voice: that is a receiver
                    // searching, and its speakers must stay current (occlusion, doors, the listener
                    // moving) for the whole of the open, not only once it is playing.
                    if ((st.VoiceId == 0 && st.Slot < 0) || st.NearestDist > audibleR) continue;
                        _occlStations.Add(st);
                        totalPts += st.PtCount;
                    }
                    if (totalPts > 0)
                    {
                        EmitterAcoustics.OcclusionBegin(_occlEar, world);
                        _occlPending = true;
                        _floodRoomOnly = false;   // full legacy flood: FinishSpeakers on landing
                        // DOOR RUSH: while a door is mid-swing, try to complete the flood in ONE big
                        // inline slice so the openness change lands THIS frame (door acoustics were
                        // tuned on the synchronous path).
                        if (EmitterAcoustics.AnyDoorAnimating && EmitterAcoustics.OcclusionStep(world, DoorRushBudgetMs))
                            FinishOcclusion(player, world);
                    }
                    else
                    {
                        for (int i = 0; i < _occlStations.Count; i++)
                        {
                            Station st = _occlStations[i];
                            CmmMixer.Instance?.SetSlotPoints(st.Slot, st.Pts, st.PtGain, st.PtFlags, 0);
                            RecordPtPush(st, 0);
                        }
                        PushReverb(player);
                    }
                }
            }
            else
            {
                // Occlusion off / direct-line only: cheap, stays synchronous. Per station: occlusion
                // (or all-clear), birth fade + rise limiter, publish; capture its seal/solid/depth.
                Vector3 ear = ListenerWorldPos(player);
                bool occlude = CMMConfig.WorldEmitterOcclusion && world != null;
                for (int i = 0; i < _stations.Count; i++)
                {
                    Station st = _stations[i];
                    // A station HOLDING A SLOT is published even with no voice: that is a receiver
                    // searching, and its speakers must stay current (occlusion, doors, the listener
                    // moving) for the whole of the open, not only once it is playing.
                    if ((st.VoiceId == 0 && st.Slot < 0) || st.NearestDist > audibleR) continue;
                    if (occlude)
                    {
                        EmitterAcoustics.ComputeOcclusion(ear, st.Pts, st.PtCount, st.PtGain, world);
                        st.Seal = EmitterAcoustics.ReverbSeal;
                        st.Solid = EmitterAcoustics.ReverbSolid;
                        st.Depth = EmitterAcoustics.ReverbDepth;
                    }
                    else
                        for (int k = 0; k < st.PtCount; k++) st.PtGain[k] = 0f;   // clear line (virtual distance 0)
                    PostProcessGains(st);
                    CmmMixer.Instance?.SetSlotPoints(st.Slot, st.Pts, st.PtGain, st.PtFlags, st.PtCount);
                    RecordPtPush(st, st.PtCount);
                }
                PushReverb(player);
            }
            }   // EmitTick
        }

        // Queued frames and starvation count for each live emitter slot, compactly.
        private static string RingHealth()
        {
            var sb = new System.Text.StringBuilder();
            for (int s = 0; s < 8; s++)
            {
                int q = CmmMixer.EmitterRingStat(s, 0);
                if (q < 0) continue;                       // no ring on this slot
                int u = CmmMixer.EmitterRingStat(s, 1);
                int p = CmmMixer.EmitterRingStat(s, 2);
                if (sb.Length > 0) sb.Append(' ');
                sb.Append('s').Append(s).Append('=').Append(q).Append('/').Append(u);
                if (p > 0) sb.Append('/').Append(p).Append('p');
            }
            return sb.Length > 0 ? sb.ToString() : "none";
        }

        // The nearest published speaker POINT to a position, across every voiced station. Used by
        // `cmm acoustics column` to walk the actual blocks between the listener and the speaker the
        // model is talking about -- rather than the one a human guesses is nearest.
        internal static bool TryGetNearestSpeaker(Vector3 from, out Vector3 nearest)
        {
            nearest = Vector3.zero;
            float best = float.MaxValue;
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.VoiceId == 0) continue;
                for (int k = 0; k < st.PtCount; k++)
                {
                    var p = new Vector3(st.Pts[k * 3], st.Pts[k * 3 + 1], st.Pts[k * 3 + 2]);
                    float d = Vector3.Distance(from, p);
                    if (d < best) { best = d; nearest = p; }
                }
            }
            return best < float.MaxValue;
        }

        // ── PHASE 3: occlusion from the native acoustic model ───────────────────────────────
        // Publishes every audible station from the native solve, or returns false and lets the
        // shipped paths run this tick. It declines in three cases, all deliberate:
        //
        //   DOOR MID-SWING — the door acoustics are the most carefully tuned behaviour in the
        //     emitter system and they live on the legacy synchronous path. Until the native model
        //     is measured against them, a swing hands straight back. Same rule the field cache uses.
        //   NO SCENE YET — the scene is a ~310 ms voxel walk on the scheduler thread; until the
        //     first one lands, and after every re-centre, the shipped model covers.
        //   UNSEEDED REVERB STATE — the native model produces a level, not a seal/solid/depth, so
        //     a station that has never been through a legacy or field tick has nothing for the
        //     reverb to carry. One legacy tick seeds it, then this path takes over.
        // Publish every audible station from the one field. Named apart from TryFieldOcclusion,
        // which is the per-speaker field CACHE and a different thing entirely -- one caches a field
        // per source, this samples one field for all of them.
        //
        // No readiness gate beyond "a flood has landed": the field is conservative before it is
        // complete (unread space is opaque), so a half-filled window is quiet-but-correct near the
        // player rather than wrong everywhere.
        private static bool TryFieldModel(EntityPlayerLocal player, World world, float audibleR)
        {
            if (!FieldOcclusion.Ready) { _statWhy = "field-warming"; return false; }
            Vector3 ear = ListenerWorldPos(player);
            bool any = false;
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.VoiceId == 0 || st.NearestDist > audibleR) continue;
                if (st.PtCount == 0)
                {
                    CmmMixer.Instance?.SetSlotPoints(st.Slot, st.Pts, st.PtGain, st.PtFlags, 0);
                    RecordPtPush(st, 0);
                    continue;
                }
                // SKIP, don't abandon. Returning here left the stations already pushed this tick
                // holding new gains while the rest kept old ones, and then handed the whole tick to
                // another model — two inconsistent pictures and an audible step. A station the
                // field cannot answer for simply keeps the gains it already has.
                if (!FieldOcclusion.ComputeOcclusion(ear, st.Pts, st.PtCount, st.PtGain))
                { _statWhy = "field-declined"; continue; }
                PostProcessGains(st);
                CmmMixer.Instance?.SetSlotPoints(st.Slot, st.Pts, st.PtGain, st.PtFlags, st.PtCount);
                RecordPtPush(st, st.PtCount);
                any = true;
            }
            if (!any) { _statWhy = "field-nopoints"; return false; }
            _statWhy = "field";
            _statFieldTicks++;      // this path returned before ever reaching the counter below,
                                    // so the log has been reading "field=0" while the field ran
            PushReverb(player);
            return true;
        }

        private static bool TryNativeOcclusion(EntityPlayerLocal player, World world, float audibleR)
        {
            // HAND BACK UNTIL THE SCENE HAS CAUGHT UP, not merely until the swing stops.
            // The old model runs during a swing and models the opening gap correctly. This model
            // then took over the INSTANT the swing ended -- using a scene that still held the door's
            // PREVIOUS state, because the patch and rebuild need ~0.6 s. So the sound went to the
            // right level during the swing, snapped to the WRONG one for half a second, then
            // corrected. Reported exactly, and in both directions:
            //
            //   opening: "quick unmuffled, muffled, then unmuffled"   (correct, stale-shut, correct)
            //   closing: "delay before muffle"                        (stale-open, then correct)
            //
            // The boundary was drawn where the ANIMATION ends rather than where this model becomes
            // able to answer, and those are 0.6 s apart.
            //
            // ...UNLESS THE MODEL IS PINNED. `cmm acoustics model native` makes this path refuse to
            // hand back, so the ear hears ONE model and nothing else. It exists because three
            // mutually uncalibrated models switching live is itself a candidate cause of "the level
            // is all over the place", and no amount of reading the code settles whether it is --
            // whereas pinning each one in turn and listening settles it in two minutes. Pinned to
            // native, a door mid-swing WILL sound wrong (the scene still holds its old state); that
            // is the point. The question the pin answers is whether each model is stable on its own.
            if (!PinNative)
            {
                if (EmitterAcoustics.AnyDoorAnimating) { _statWhy = "doorswing"; return false; }
                if (AcousticBridge.GeometrySettling) { _statWhy = "geom-settling"; return false; }
            }

            Vector3 ear = ListenerWorldPos(player);
            if (!AcousticBridge.EnsureScene(world, ear)) { _statWhy = "ac-warming"; return false; }

            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.VoiceId == 0 || st.NearestDist > audibleR || st.PtCount == 0) continue;
                if (st.Seal == 0f && st.Solid == 0f && st.Depth == 0f) { _statWhy = "ac-unseeded"; return false; }
            }

            bool any = false;
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.VoiceId == 0 || st.NearestDist > audibleR) continue;
                if (st.PtCount == 0)
                {
                    CmmMixer.Instance?.SetSlotPoints(st.Slot, st.Pts, st.PtGain, st.PtFlags, 0);
                    RecordPtPush(st, 0);
                    continue;
                }
                if (!AcousticBridge.ComputeOcclusion(ear, st.Pts, st.PtCount, st.PtGain, world))
                { _statWhy = "ac-declined"; return false; }   // scene swapped under us — legacy covers this tick
                // Seal/Solid/Depth deliberately carried over: the reverb bus stays on the shipped
                // measure so an A/B changes ONE thing — the occlusion — and not the room sound too.
                PostProcessGains(st);
                CmmMixer.Instance?.SetSlotPoints(st.Slot, st.Pts, st.PtGain, st.PtFlags, st.PtCount);
                RecordPtPush(st, st.PtCount);
                any = true;
            }
            if (!any) { _statWhy = "ac-nopoints"; return false; }
            _statWhy = "native";
            PushReverb(player);
            return true;
        }

        // ── PHASE 2: occlusion from the per-speaker field cache ─────────────────────────────
        // Synchronous (sampling static fields is O(points)): request/maintain fields for every
        // audible station's speakers; if ALL are ready, publish every station from field samples
        // and return true. Any not-ready field ⇒ false, and the caller runs the legacy amortized
        // flood this tick (warm-up / post-door rebuild window). The reverb room measure is
        // listener-anchored by nature, so the field path zeroes AirCellCount — PushReverb then
        // takes its existing ray-probe fallback instead of a stale legacy flood's room.
        private static readonly List<Vector3i> _fieldWanted = new List<Vector3i>();
        private static bool _floodRoomOnly;   // the pending ear flood is for the ROOM measure only
        private static bool TryFieldOcclusion(EntityPlayerLocal player, World world, float audibleR)
        {
            _fieldWanted.Clear();
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.VoiceId == 0 || st.NearestDist > audibleR) continue;
                for (int k = 0; k < st.PtCount; k++)
                    _fieldWanted.Add(new Vector3i(Mathf.FloorToInt(st.Pts[k * 3]),
                                                  Mathf.FloorToInt(st.Pts[k * 3 + 1]),
                                                  Mathf.FloorToInt(st.Pts[k * 3 + 2])));
            }
            if (_fieldWanted.Count == 0) { _statWhy = "nopoints"; return false; }
            EmitterFieldCache.Maintain(world, _fieldWanted);
            for (int i = 0; i < _fieldWanted.Count; i++)
                if (!EmitterFieldCache.Ready(_fieldWanted[i])) { _statWhy = "warming"; return false; }

            Vector3 ear = ListenerWorldPos(player);
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.VoiceId == 0 || st.NearestDist > audibleR) continue;
                if (st.PtCount == 0)
                {
                    CmmMixer.Instance?.SetSlotPoints(st.Slot, st.Pts, st.PtGain, st.PtFlags, 0);
                    RecordPtPush(st, 0);
                    continue;
                }
                if (!EmitterFieldCache.SampleStation(world, ear, st.Pts, st.PtCount, st.PtGain,
                                                     out float seal, out float solid, out float depth))
                { _statWhy = "flipped"; return false; }   // ready flipped under us — legacy covers this tick
                st.Seal = seal; st.Solid = solid; st.Depth = depth;
                PostProcessGains(st);
                CmmMixer.Instance?.SetSlotPoints(st.Slot, st.Pts, st.PtGain, st.PtFlags, st.PtCount);
                RecordPtPush(st, st.PtCount);
            }
            // Air*/room measure comes from the room-only ear flood the caller keeps running —
            // PushReverb blends the last completed flood's connected-air size with the probe.
            PushReverb(player);
            return true;
        }

        // Land a completed occlusion flood: for EVERY station culled at begin, per-speaker gains +
        // directional points from the finished field (and its seal/solid/depth for the reverb),
        // then the room probe + reverb push. Called from the per-frame stepper on completion, or
        // inline at tick start during a door rush.
        private static void FinishOcclusion(EntityPlayerLocal player, World w)
        {
            for (int i = 0; i < _occlStations.Count; i++)
            {
                Station st = _occlStations[i];
                if (st.VoiceId == 0 || st.PtCount == 0) continue;   // stopped/emptied while the flood ran
                EmitterAcoustics.OcclusionFinish(_occlEar, st.Pts, st.PtCount, st.PtGain, w);
                st.Seal  = EmitterAcoustics.ReverbSeal;    // the statics are per-Finish-call — capture now
                st.Solid = EmitterAcoustics.ReverbSolid;
                st.Depth = EmitterAcoustics.ReverbDepth;
                PostProcessGains(st);
                CmmMixer.Instance?.SetSlotPoints(st.Slot, st.Pts, st.PtGain, st.PtFlags, st.PtCount);
                RecordPtPush(st, st.PtCount);
            }
            PushReverb(player);
            _occlPending = false;
        }

        // Shape a station's freshly-computed per-speaker gains before publishing: the placement
        // fade-in first (it inflates a new speaker's extra), then the movement rise limiter.
        private static void PostProcessGains(Station st)
        {
            // THE LIMITER FIRST, THE DELIBERATE FADES AFTER IT. Order matters, and having it the
            // other way round is what made a station tuning in stutter.
            //
            // LimitOcclusionRise rate-limits RISES in virtual distance and keeps per-point history
            // of what it published. A join or birth fade is expressed as a rise -- it adds a whole
            // Range of distance and decays it to nothing -- so running it first fed the limiter a
            // synthetic jump it then throttled, recorded the throttled value as the baseline, and
            // let the following fall straight through. The two then fought over the same number on
            // every tick of the arrival: a stepped, jumping level exactly while a station comes in.
            //
            // A fade is not an occlusion change. It is a decision, it is already a smooth ramp, and
            // it must neither be limited nor enter the limiter's history.
            LimitOcclusionRise(st.Pts, st.PtGain, st.PtCount);
            ApplyBirthFades(st.Pts, st.PtGain, st.PtCount);
        }

        // Rate-limit occlusion RISES (see _publishedExtra). Falls pass through untouched; door
        // events bypass entirely; a speaker not published for >2 s republishes as-is.
        private static void LimitOcclusionRise(float[] pts, float[] gains, int count)
        {
            if (count <= 0) return;
            float rate = CMMConfig.WorldEmitterOcclusionFadeRate;
            float now = CmmClock.Now;
            // ...and stays bypassed until the acoustic model has actually DELIVERED the change.
            // The 1 s window is measured from the door toggle, but the native scene needs the swing
            // to finish (~1 s) plus a rebuild (~0.6 s) before it can publish the new occlusion -- by
            // which time the exemption had lapsed and the rate cap throttled the very change it
            // exists to let through. Total lag to a shut door was animation + rebuild + ramp.
            // GeometrySettling NO LONGER EXEMPTS. It was added because the door-toggle window expired
            // before the native rebuild finished, so the one change the exemption existed for was
            // the one it throttled -- but that was a symptom of native being handed a scene it had
            // not rebuilt yet, which is fixed at the source now (AcousticBridge.GeometrySettling:
            // delivery beats the give-up timer). Settling can last 30 s, and an exemption that wide
            // means the limiter is simply not running in any house with doors in it -- while the
            // handover back to native, a genuine large step, is exactly the moment it should be.
            // A BLOCK CHANGE IS A DELIBERATE EVENT, LIKE A DOOR. The limiter exists for the model
            // changing its mind between floods; a player walling a speaker in did it on purpose
            // and is listening for the result, which is the same argument the door exemption makes.
            //
            // Measured, and it is worse than merely slow. The step is rate x elapsed, so a tick
            // that fires PROMPTLY gets a SMALL step: collapsing the tick shrank dt, the first tick
            // could only move the gain by about 1.0, and the real change waited a whole interval
            // for the next one. The stopwatch shows it exactly -- intick is (ticks-1) x 113 ms,
            // and the two-tick cases all report "gain moved 5.65", which is 50 x 0.113 to the
            // decimal. Speeding the tick up made the limiter throttle harder.
            bool doorWindow = EmitterAcoustics.AnyDoorAnimating || now - _doorEventAt < 1f
                            || EmitterBlockHook.RecentChange;
            for (int i = 0; i < count; i++)
            {
                Vector3i key = SlotKey(pts, i);
                // PUBLISHED jitter: what the native spatializer actually receives, i.e. after this
                // limiter. Compared against the RAW jitter (EmitterAcoustics.StatJitter*) it says
                // whether the limiter is absorbing the measurement noise or passing it through.
                if (CMMConfig.DebugLogging
                    && _publishedExtra.TryGetValue(key, out PublishedExtra prevStat) && now - prevStat.At < 2f)
                {
                    float dd = gains[i] - prevStat.Value; if (dd < 0f) dd = -dd;
                    _statPubSum += dd; _statPubN++;
                    if (dd > _statPubMax) _statPubMax = dd;
                }
                // SYMMETRIC. This capped RISES only, so the same non-event -- the reverberant term
                // blinking on and off across a voxel boundary, extra 33 <-> 5 -- was a 0.6 s fade
                // getting louder and an instant 8.7 dB jump getting quieter. A rate limit that runs
                // in one direction does not limit a rate; it just makes noise asymmetric, which is
                // most of "the volume is all over the place".
                // The model's answer, BEFORE the limiter. The stopwatch needs both this and the
                // clamped value: timing "when did the published number move" cannot tell a slow
                // delivery from a fast one, but timing "when did it REACH this" can.
                float target = gains[i];
                bool clamped = false;
                if (rate > 0f && !doorWindow
                    && _publishedExtra.TryGetValue(key, out PublishedExtra prev)
                    && now - prev.At < 2f)
                {
                    float step = rate * Mathf.Clamp(now - prev.At, 0.02f, 1f);
                    if (gains[i] > prev.Value + step) { gains[i] = prev.Value + step; clamped = true; }
                    else if (gains[i] < prev.Value - step) { gains[i] = prev.Value - step; clamped = true; }
                }
                // Block stopwatch: this is the number the mixer is handed, AFTER the rate cap, so
                // it is the first instant the change is genuinely on its way to the ear.
                EmitterBlockHook.NoteGainProgress(gains[i], target, clamped);
                _publishedExtra[key] = new PublishedExtra { Value = gains[i], At = now };
            }
        }

        // Block position of point slot i (inverse of WritePoint's +0.5 centring).
        private static Vector3i SlotKey(float[] pts, int i) =>
            new Vector3i(Mathf.FloorToInt(pts[i * 3]), Mathf.FloorToInt(pts[i * 3 + 1]), Mathf.FloorToInt(pts[i * 3 + 2]));

        // Remember a station's latest point-set push so a track advance can seed the replacement
        // voice from it (occlusion continuity across the retire/re-add).
        private static void RecordPtPush(Station st, int count)
        {
            st.LastPtPushCount = count;
            st.LastPtPushAt = CmmClock.Now;
        }

        // Stamp speakers newly appeared in the registry snapshot and prune ones that left, so
        // ApplyBirthFades can grade a just-placed radio in instead of popping it.
        private static void MaintainBirths()
        {
            float now = CmmClock.Now;
            var snap = EmitterRegistry.Snapshot;
            for (int i = 0; i < snap.Count; i++)
                if (!_speakerFirstSeen.ContainsKey(snap[i].Pos)) _speakerFirstSeen.Add(snap[i].Pos, now);
            if (_speakerFirstSeen.Count == snap.Count) return;   // nothing left the set
            _birthScratch.Clear();
            foreach (var kv in _speakerFirstSeen)
            {
                bool present = false;
                for (int i = 0; i < snap.Count; i++)
                    if (snap[i].Pos.Equals(kv.Key)) { present = true; break; }
                if (!present) _birthScratch.Add(kv.Key);
            }
            for (int i = 0; i < _birthScratch.Count; i++)
            {
                _speakerFirstSeen.Remove(_birthScratch[i]);
                _publishedExtra.Remove(_birthScratch[i]);   // limiter state follows the same lifetime
            }
        }

        // A speaker that appeared while a station voice is live fades in rather than popping: its
        // published occlusion gain (virtual distance, blocks) carries a decaying extra that starts
        // silent and reaches 0 over WorldEmitterBirthFade (< 0 ⇒ inherit the start fade, so the
        // placement ramp can be tuned without dragging station-approach / radio-join fades along).
        private static void ApplyBirthFades(float[] pts, float[] gains, int count)
        {
            if (count <= 0) return;
            float now = CmmClock.Now;
            float range = CMMConfig.WorldEmitterRange;

            // ── JOINING A STATION IS NOT THE SAME EVENT AS BEING PLACED ───────────────────────
            // A speaker that changes station has to fade in, always. It shares this mechanism
            // (virtual distance decaying to the real occlusion) but NOT its duration: birth is
            // about a block appearing in the world and defaults to 0 in every shipped config,
            // which is why a retune arrived at full level -- "music comes in hot (not from 0)".
            // The station it joins is very often already at full for other speakers, so there is
            // no voice fade to ride either; the point itself is the only thing that can ramp.
            float jdur = ArriveRamp;
            if (jdur > 0f && _joinedAt.Count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    Vector3i key = SlotKey(pts, i);
                    if (!_joinedAt.TryGetValue(key, out float j0)) continue;
                    float jage = now - j0;
                    if (jage >= jdur) continue;
                    gains[i] += FadeExtra(1f - jage / jdur, range);
                }
            }

            if (_speakerFirstSeen.Count == 0) return;
            float dur = CMMConfig.WorldEmitterBirthFade >= 0f ? CMMConfig.WorldEmitterBirthFade : StartFade();
            if (dur <= 0f) return;
            for (int i = 0; i < count; i++)
            {
                Vector3i key = SlotKey(pts, i);
                if (!_speakerFirstSeen.TryGetValue(key, out float t0)) continue;
                float age = now - t0;
                if (age >= dur) continue;
                gains[i] += FadeExtra(1f - age / dur, range);
            }
        }

        // A FADE HAS TO BE LINEAR IN LEVEL, AND VIRTUAL DISTANCE IS NOT.
        //
        // These fades work by adding distance and decaying it to nothing, but the output turns
        // distance into level through a power law -- level ~ ((Range - d)/span)^Falloff, Falloff
        // 2.5. Decaying the DISTANCE linearly therefore produces, in dB: silent until 3% of the
        // way, -57 dB at a quarter, -34 dB at half, -19 dB at three quarters, and the whole
        // remaining 18.5 dB in the last third. Measured across a one-second arrive, 72% of it sits
        // below -19 dB and the audible rise is squeezed into the final 320 ms.
        //
        // That is "the audio comes through too quickly": not a fast ramp, a ramp whose audible
        // part is compressed into its own tail. Inverting the falloff spreads it evenly -- ask for
        // the distance that yields THIS level, rather than assuming distance and level are the
        // same shape.
        private static float FadeExtra(float attenuate01, float range)
        {
            if (attenuate01 <= 0f) return 0f;
            if (attenuate01 >= 1f) return range;
            float falloff = CMMConfig.WorldEmitterFalloff;
            if (falloff < 0.1f) falloff = 0.1f;
            float wanted = 1f - attenuate01;                       // the level we want, 0..1
            return (1f - Mathf.Pow(wanted, 1f / falloff)) * range;  // ...as added distance
        }

        // When each speaker last CHANGED station. Stamped in GroupSnapshot, which is the one place
        // that knows -- and it is deliberately not stamped at the dial turn, because through the
        // whole leave the speaker is still on the station it is leaving and must not duck.
        private static readonly Dictionary<Vector3i, float> _joinedAt = new Dictionary<Vector3i, float>();

        // Mark every currently-tracked speaker's birth as already elapsed. Used when the FIRST
        // station voice starts fresh — its own fade-in covers the entrance, and stacking the birth
        // ramp on top would double-fade the radio that triggered the start.
        private static void ExpireBirths()
        {
            if (_speakerFirstSeen.Count == 0) return;
            _birthScratch.Clear();
            foreach (var kv in _speakerFirstSeen) _birthScratch.Add(kv.Key);
            float expired = CmmClock.Now - 3600f;
            for (int i = 0; i < _birthScratch.Count; i++) _speakerFirstSeen[_birthScratch[i]] = expired;
        }

        // ── Registry grouping ───────────────────────────────────────────────────────────────────
        // Partition the registry snapshot by station: each speaker POSITION is PINNED to a station
        // NAME on first sight (sorted list, its StationHash % count at that moment) and resolves
        // through the catalog thereafter — see _posPins for the identity-churn and removal
        // semantics. Per station: the nearest speaker
        // over ALL tracked distances (start/stop/steal ranking) plus the culled point set of its
        // nearest in-Range speakers (what the native voice sums). Allocation-free steady-state
        // (a pin allocates once per new hash).
        // The index in the registry snapshot of a speaker tuned to a station that does not
        // resolve, or -1. One is enough: a lane for each would spend the whole budget on stations
        // that are not playing.
        // The speaker sitting on the carrier, if any. A POSITION, not a snapshot index and not a
        // lane: it is a property of the speaker, and the two things that can put it there disagree
        // about everything else.
        //
        //   1. GroupSnapshot -- the pin names a station that does not resolve, or resolves to one
        //      with no tracks at all.
        //   2. The tune give-up -- the station is perfectly Ready and simply will not OPEN. This
        //      is the one that matters in practice: a folder with broken files has tracks, so it
        //      is Ready, so detector 1 cannot see it. "Broken FM" is exactly that, and testing
        //      !Ready was never going to catch it.
        private static Vector3i _deadAirPos;
        private static bool     _deadAirOn;
        private static float    _deadAirMissingAt;

        private static void GroupSnapshot()
        {
            int n = _stations.Count;
            for (int i = 0; i < n; i++)
            {
                Station st = _stations[i];
                st.NearestDist = float.MaxValue;
                st.PtCount = 0;
            }
            float range = CMMConfig.WorldEmitterRange;
            float now = CmmClock.Now;
            var snap = EmitterRegistry.Snapshot;
            for (int i = 0; i < snap.Count; i++)
            {
                EmitterRegistry.Emitter e = snap[i];
                // Being swept on its own slot (the sweep borrow). It must not be a POINT on the
                // station it is joining — the station's own slot would play music out of a speaker
                // that is currently hissing — but it MUST still count as a reason for that station
                // to exist.
                //
                // SKIPPING IT OUTRIGHT WAS A DEADLOCK, and it was every symptom at once. The
                // borrow holds the speaker until the station it is joining becomes audible; a
                // station only opens when a speaker is near it; and this was that speaker. So the
                // borrow sat waiting for a station it was itself preventing from opening, and let
                // go only when its give-up timer fired. The log measures it at 7.05s, 6.92s and
                // 7.03s across three retunes — "upwards of 6000ms" — with the station's "opening"
                // line landing one millisecond AFTER "sweep borrow ended" every time.
                //
                // The second arc of static came from the same line: 7s is still inside
                // RetuneWindow, so the station finally opening was scored as a fresh retune and
                // armed a SECOND search on its own slot. Static, out, static again, then music.
                //
                // It also left stations owning a slot with no speakers at all, which is the
                // float.MaxValue in "steals the slot of 'Rust Belt Rock' (34028230000...0.0m)".
                bool borrowed = _borrowSlot >= 0 && e.Pos.Equals(_borrowPos);
                // STILL ON THE STATION IT IS LEAVING, and still one of its speakers, until the
                // handover. This is the pocket radio's rule -- "the station being left keeps
                // playing... it is not faded and it is not stopped, the receiver buries it" -- and
                // it is the half the emitter never had. The pin has already moved; resolving it
                // here takes the old station away on the instant, which is the audio vanishing as
                // the dial turns and the reason there was never anything under the hiss.
                bool leaving = _tunePhase == 1 && e.Pos.Equals(EmitterTuner.LastTunePos);
                // ...and from the instant the dial turned, before we have decided anything.
                if (EmitterTuner.SweepHeld(e.Pos)) continue;
                // The PLAYER'S choice outranks the derived one (EmitterTuner: look at a speaker,
                // press the tune key). Deliberately NOT written into _posPins: leaving the override
                // outside the cache means retuning a building lands on the next decision tick with
                // nothing to invalidate, and clearing it back to Automatic exposes the pin that was
                // always underneath rather than inventing a new one.
                string pinned;
                if (leaving) pinned = _tuneFrom;
                else if (!EmitterTuner.TryPin(e, out pinned) &&
                    !_posPins.TryGetValue(e.Pos, out pinned))
                {
                    pinned = _stations[e.StationHash % n].Name;
                    _posPins[e.Pos] = pinned;
                }
                // Pinned to a REMOVED station ⇒ dead air: the radio contributes to no live station
                // (silent) until the folder returns or the next world entry recomputes the pins.
                // NOT READY IS DEAD AIR TOO. Ready is `Order != null && Order.Length > 0`, so a
                // station whose folder is empty is very much IN the catalog -- it resolves, it just
                // has nothing to play. It therefore never took this branch, which is why the one
                // station built to be broken was the one that never got the off-air tone.
                if (!RadioStationCatalog.ByName.TryGetValue(pinned, out Station st) || !st.Ready)
                {
                    // SILENCED, OR PINNED TO A STATION THAT NO LONGER EXISTS. The speaker is on
                    // nothing, and _posStation must say so -- it is only ever WRITTEN below, so
                    // leaving it kept the station this speaker played before it was switched off,
                    // for as long as it stayed off. Turning it back on then read that stale name as
                    // "the station being left" and ran a whole leave against a station the speaker
                    // was not on: music bleeding in at full through the other lane before any ramp
                    // caught it, then fading in again from low. Both halves of that report.
                    _posStation.Remove(e.Pos);
                    // SWITCHED OFF IS NOT THE SAME AS TUNED TO NOTHING, and both landed here.
                    //
                    // An empty pin is the player having switched the speaker off: silence is
                    // correct, and the carrier is one of the things being switched off (not a
                    // retune, so no tuning path would ever have cleared it).
                    //
                    // A pin that names a station which no longer resolves is the OTHER thing --
                    // the folder is gone or empty, "Broken FM". That speaker is tuned to a dead
                    // frequency, which is precisely what the carrier is for, and clearing it here
                    // was why the tone never played for the one station built to be broken.
                    if (string.IsNullOrEmpty(pinned)) EndDeadAir();
                    else if (!_deadAirOn || !_deadAirPos.Equals(e.Pos)) GoDeadAir(e.Pos, -1);
                    continue;
                }
                st.LastSeenAt = now;
                // Changed station? Then it fades in. See _joinedAt.
                if (!_posStation.TryGetValue(e.Pos, out string wasOn)
                    || !string.Equals(wasOn, st.Name, StringComparison.OrdinalIgnoreCase))
                    _joinedAt[e.Pos] = now;
                // WHICH STATION THIS SPEAKER IS ON, as of this tick. A retune overwrites the pin
                // before the audio layer ever sees it, so without this there is no record of where
                // the speaker came FROM -- and the station being left is half of a dial turn.
                _posStation[e.Pos] = st.Name;
                // Render class rides per-point flags (bit0 = ham): a radioHam gets the HAM coloration
                // (always slightly distorted, mono communications speaker) in the native render while
                // shelf speakers get the (clean-by-default) speaker set. Name is the registry's interned
                // display-name string, so this compare is a reference check in practice.
                int fl = e.Name == "radioHam" ? 1 : 0;
                if (e.Dist < st.NearestDist) { st.NearestDist = e.Dist; st.NearestPos = e.Pos; st.NearestFlags = fl; }
                // Out of range, or hissing on the borrowed slot: either way not a point HERE.
                if (e.Dist > range || borrowed) continue;
                if (st.PtCount < MaxPoints)
                {
                    WritePoint(st, st.PtCount, e.Pos, fl); st.PtDist[st.PtCount] = e.Dist; st.PtCount++;
                }
                else
                {
                    // Buffer full: replace the current farthest slot if this speaker is nearer.
                    int far = 0;
                    for (int k = 1; k < MaxPoints; k++) if (st.PtDist[k] > st.PtDist[far]) far = k;
                    if (e.Dist < st.PtDist[far]) { WritePoint(st, far, e.Pos, fl); st.PtDist[far] = e.Dist; }
                }
            }
        }

        private static void WritePoint(Station st, int slot, Vector3i p, int flags)
        {
            int o = slot * 3;
            st.Pts[o] = p.x + 0.5f; st.Pts[o + 1] = p.y + 0.5f; st.Pts[o + 2] = p.z + 0.5f;
            st.PtFlags[slot] = flags;
        }

        // ── Voice lifecycle ─────────────────────────────────────────────────────────────────────
        // Per decision tick: interrupt a voiced station whose airing file vanished (hot-swap parity
        // with contexts — the user is fine with a cut) and advance stations whose track ENDED (the
        // decoder EOF is authoritative; runs even out of range so a warm voice stays on the
        // schedule's timeline and re-entry rejoins seamlessly).
        // Sync-audit cadence (shared across all voiced stations; see MaintainPlayingStations).
        private static float _nextAuditAt;

        // How many measurements may be in flight before a station NOBODY is listening to stops
        // adding more. Deliberately small: the point of premeasuring every station is that the one
        // you dial to next is ready, and that goal is served by a slow trickle just as well as by
        // a stampede -- while the stampede actively harms the track playing right now.
        private const int SpeculativeMeasureCap = 2;

        private static void MaintainPlayingStations(EntityPlayerLocal player)
        {
            // Sync audit: periodic voice-vs-paper delta per voiced station, same cadence and
            // format as the personal radio's (RadioStationCatalog.AuditVoice) so one grep
            // ("CMM SYNC") lines both consumers up against the same paper schedule.
            bool audit = CMMConfig.DebugLogging && CmmClock.Now >= _nextAuditAt;
            if (audit) _nextAuditAt = CmmClock.Now + RadioStationCatalog.AuditInterval;

            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.VoiceId == 0) continue;
                if (audit) RadioStationCatalog.AuditVoice("emitter", st, st.VoiceTrack, st.VoiceId);

                if (st.NearestDist == float.MaxValue)
                {
                    // No tracked speaker this tick. That can be a MOMENTARY registry dropout — chunk
                    // load flicker at the tracked fringe dropped the POI cache until its rescan (log
                    // 2026-07-09: GNR stop/start every ~10 s at ~82 m, each a decoder open + seek) —
                    // so hold the WARM voice through a grace window instead of stopping instantly.
                    // It is already inaudible: the warm/audible split publishes its EMPTY point set,
                    // which zeroes the native gains (a destroyed radio still cuts within a tick).
                    // Truly gone (grace expired) → release. A track that ENDS while unseen just
                    // releases quietly — the schedule rejoins mid-song when the speakers return.
                    if (CmmClock.Now - st.LastSeenAt > UnseenGrace)
                        StopStation(st, "unseen past grace");
                    else if (CmmMixer.Instance != null && !CmmMixer.Instance.TryGetState(st.VoiceId, out _, out _, out _))
                    {
                        st.VoiceId = 0; st.VoiceTrack = null;
                        StopStation(st, "voice gone while unseen");   // frees the slot; no restart while unseen
                    }
                    continue;
                }

                if (st.VoiceTrack != null && !SafeExists(st.VoiceTrack))
                {
                    CMMConfig.D($"[CMM] Station '{st.Name}': airing track gone ({Path.GetFileName(st.VoiceTrack)}) — hot-swapping (interrupt).");
                    string ended = st.VoiceTrack;
                    StopVoice(st, "airing track deleted");
                    AdvanceStation(st, ended);
                    StartStation(st, player, AdvanceFade, fresh: false);
                    continue;
                }

                if (CmmMixer.Instance != null && !CmmMixer.Instance.TryGetState(st.VoiceId, out _, out _, out _))
                {
                    CMMConfig.D($"[CMM] Station '{st.Name}': track ended → next track.");
                    string ended = st.VoiceTrack;
                    st.VoiceId = 0; st.VoiceTrack = null;
                    AdvanceStation(st, ended);
                    StartStation(st, player, AdvanceFade, fresh: false);
                }
            }
        }

        // K-nearest voicing: start the nearest schedule-ready stations that have a speaker within
        // dry Range, up to the slot count; a challenger may STEAL the farthest incumbent's slot only
        // when decisively nearer (StealMargin) — the old voice fades on the shared bus while the new
        // station starts on the freed slot (an ordinary station-change crossfade).
        private static void SelectAndVoice(EntityPlayerLocal player)
        {
            float range = CMMConfig.WorldEmitterRange;

            // NOTHING CAN LAND THIS TICK, SO TAKE NOTHING. StartStation returns immediately while a
            // start is in flight, but SelectAndVoice used to run the whole selection first — and
            // could STEAL a slot on the way, stopping a station for a start that then never
            // dispatched. Worse, StopStation -> StopVoice -> InvalidatePendingStart bumps the
            // generation, so the steal also killed the in-flight open it was queued behind.
            //
            // That is the churn in the log: one dial turn stopping three stations and opening
            // 'Lit FM' twice, 550 ms apart, the first one dropped as superseded. The next tick
            // costs a tenth of a second and takes nothing that was playing.
            if (_emStartPending && CmmClock.Now - _emStartPendingAt < 10f) return;

            for (;;)
            {
                // Nearest unvoiced candidate.
                Station cand = null;
                for (int i = 0; i < _stations.Count; i++)
                {
                    Station st = _stations[i];
                    if (st.VoiceId != 0 || !st.Ready) continue;
                    if (st.NearestDist > range) continue;
                    if (cand == null || st.NearestDist < cand.NearestDist) cand = st;
                }
                if (cand == null) return;

                // THE ONE STATION THAT MUST NOT TAKE THE BORROW'S SLOT IS THE ONE IT IS
                // SWEEPING ONTO. Measured, five times in one log: the borrow takes the last free
                // slot, and two milliseconds later the station it is sweeping onto reaches the
                // yield below, finds nothing free BECAUSE the borrow has it, and takes it back.
                // The retune then lands with no sweep at all — the whole feature defeated by its
                // own beneficiary.
                //
                // And needlessly: in every one of those five the ordinary steal was available.
                // 'Lit FM' opened on the yielded slot and then re-opened 100 ms later on a slot
                // stolen from 'Galaxy News Radio' anyway. So the target goes to the steal FIRST
                // and falls back to taking the borrow's slot only when no steal is possible,
                // because a station that never opens is worse than a lost sweep.
                bool candIsSweepTarget = _borrowSlot >= 0 && _borrowTarget != null
                    && string.Equals(cand.Name, _borrowTarget, StringComparison.OrdinalIgnoreCase);

                int free = FreeSlot();
                if (free < 0 && _borrowSlot >= 0 && !candIsSweepTarget)
                {
                    // A SWEEP NEVER COSTS SOMEBODY THEIR MUSIC. The borrow holds a slot with no
                    // owner, so a THIRD station starting while it is held finds nothing free and
                    // steals from the farthest one playing — reported as the audio cutting out
                    // while tuning another station. A quarter second of hiss is worth less than a
                    // station somebody is listening to, so the borrow gives its slot back first
                    // and only a genuine shortage reaches the steal below.
                    CMMConfig.D("[CMM] Sweep borrow yields its slot — an unrelated station needs it.");
                    EndSweepBorrowNow();
                    free = FreeSlot();
                }
                if (free < 0)
                {
                    // All slots held: steal from the FARTHEST incumbent, with hysteresis.
                    Station far = null;
                    for (int s = 0; s < CmmMixer.StationSlots; s++)
                    {
                        Station own = _slotOwner[s];
                        if (own == null) continue;
                        if (far == null || own.NearestDist > far.NearestDist) far = own;
                    }
                    if (far == null || cand.NearestDist + StealMargin >= far.NearestDist)
                    {
                        // Nothing stealable. If this is the station the borrow is sweeping onto,
                        // the borrow gives way after all — see above.
                        if (!candIsSweepTarget) return;
                        CMMConfig.D("[CMM] Sweep borrow yields its slot — its own station cannot start otherwise.");
                        EndSweepBorrowNow();
                        free = FreeSlot();
                        if (free < 0) return;
                    }
                    else
                    {
                        CMMConfig.D($"[CMM] Station '{cand.Name}' ({cand.NearestDist:0.0}m) steals the slot of '{far.Name}' ({far.NearestDist:0.0}m).");
                        StopStation(far, "slot taken by a nearer station");
                    }
                }
                StartStation(cand, player, StartFade(), fresh: !HasActiveVoice);
                if (cand.VoiceId == 0) return;   // start failed (no playable track) — don't spin
            }
        }

        // Both slot rules live in EmitterSlots so they can be tested; see the header there.
        // A LANE FOR TUNING, from the reserved tail. Never handed to a station, never reaped by
        // the idle sweep while it is in use, and never in competition with the music -- which is
        // the whole point: the sweep exists precisely when every station lane is busy.
        private static int FreeTuneSlot()
        {
            for (int s = CmmMixer.StationSlots; s < MaxSlots; s++)
            {
                if (s == _borrowSlot || s == _deadAirSlot) continue;
                if (_slotOwner[s] != null) continue;   // cannot happen, but cheap to be sure
                return s;
            }
            return -1;
        }

        private static int FreeSlot() =>
            EmitterSlots.FreeSlot(_slotOwner, _emStartPending, _emPendingSlot,
                                  _borrowSlot >= 0 ? _borrowSlot : _deadAirSlot);

        // ── Pause resync ────────────────────────────────────────────────────────────────────────
        // Set from the unpause edge (OnGamePauseChanged, pauses > 1 s); consumed by the next tick.
        private static bool _clockResyncPending;
        internal static void ResyncToClock() { _clockResyncPending = true; }

        // After an SP pause: the broadcast clock (worldTime) froze while every live voice kept
        // decoding in realtime under the pause duck, so each playing station is now AHEAD of the
        // paper schedule by the pause length — and the EOF pause-drift guard locks that lead in
        // (next-at-0) for the rest of the session. Fresh joins (a personal-radio dial, a station
        // coming into range) always resolve the paper, so without this re-glue the world emitters
        // and the personal radio audibly drift apart with every pause. WARM out-of-range voices
        // are simply released — inaudible now, and re-voicing on approach joins the paper for
        // free; audible ones restart at the paper position (the song resumes where the listener
        // stopped hearing it — a pause no longer skips content).
        private static void ResyncVoicesToClock(EntityPlayerLocal player)
        {
            float audibleR = CMMConfig.WorldEmitterRange;
            if (CMMConfig.WorldEmitterReverbEnabled)
                audibleR *= Mathf.Max(1f, CMMConfig.WorldEmitterReverbRangeMult);
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.VoiceId == 0) continue;
                if (st.NearestDist > audibleR) { StopStation(st, $"out of range {st.NearestDist:0.0}m > {audibleR:0.0}m"); continue; }
                StopVoice(st);
                st.OnAir = null;   // force a fresh paper resolve in StartStation
                StartStation(st, player, AdvanceFade, fresh: false);
            }
        }

        // ── Start / stop ────────────────────────────────────────────────────────────────────────

        // Fade-in for advancing to the NEXT track (station continuing) — short so it's near-seamless,
        // unlike the gentle start fade used when a station first comes into range.
        private const float AdvanceFade = 0.2f;

        // Quick fade-out on stop (destroyed speaker / slot steal / disabled). Short so destroying a
        // speaker cuts crisply; just long enough to avoid a click.
        private const float StopFade = 0.12f;

        // fresh = the FIRST station voice coming into existence (no other station live): clear the
        // shared native acoustic state — a destroyed radio's still-ringing reverb tail must not be
        // re-voiced under the new voice's fade-in — and skip the birth ramp (the voice fade covers
        // it). With another station already playing, the shared reverb legitimately carries on.
        private static void StartStation(Station st, EntityPlayerLocal player, float fadeIn, bool fresh)
        {
            if (st.VoiceId != 0) return;
            if (st.OnAir == null) ResolveOnAir(st, BroadcastSeconds());
            if (st.OnAir == null)
            {
                // WARN ONLY WHEN THE POOL IS ACTUALLY THE PROBLEM. This used to warn on every
                // failed resolve, but a resolve fails for several reasons and only one of them is
                // "none decodable": most of the time at startup the background chain walk simply
                // has not cached the track lengths yet. That is transient and self-correcting, and
                // telling the player to go and check their file formats over it sends them after a
                // problem that does not exist -- observed in a normal session, the warning at
                // 142.4 s and the same station playing happily at 144.2 s.
                //
                // Throttled either way: this path retries every tick while a speaker is in range,
                // so an genuinely dead pool used to put ~700 lines in one session.
                if (st.LastMiss == ResolveMiss.NoUsableTracks
                    && CmmClock.Now - st.NoTracksLoggedAt > 30f)
                {
                    st.NoTracksLoggedAt = CmmClock.Now;
                    Log.Warning($"[CMM] Station '{st.Name}': no playable tracks "
                              + $"({st.Order?.Length ?? 0} file(s) in the pool, none decodable). "
                              + "Check the format, or `cmm roots` for where they came from.");
                }
                else if (st.LastMiss != ResolveMiss.NoUsableTracks)
                {
                    CMMConfig.D($"[CMM] Station '{st.Name}': nothing on air yet ({st.LastMiss}).");
                }
                return;
            }

            // A start TRIGGERED BY a just-appeared speaker should feel like placement, not like a
            // station drifting into range — use the birth fade for the voice too. (The birth RAMP
            // only covers speakers joining an already-live station; when the placed radio is the
            // station's first audible speaker, THIS voice fade is what the player hears.) Gate =
            // the nearest speaker's birth stamp only: a chunk-streamed speaker starting a station
            // at the range fringe also matches, but out there distance falloff hides the fade.
            if (CMMConfig.WorldEmitterBirthFade >= 0f
                && _speakerFirstSeen.TryGetValue(st.NearestPos, out float bornAt)
                && CmmClock.Now - bornAt < 2f)
            {
                CMMConfig.D($"[CMM] Station '{st.Name}': just-born nearest speaker — birth fade {CMMConfig.WorldEmitterBirthFade:0.##}s.");
                fadeIn = CMMConfig.WorldEmitterBirthFade;
            }

            // One start in flight at a time. A station that misses its turn simply gets the next
            // tick; the alternative is racing opens fighting over the same slot.
            if (_emStartPending)
            {
                if (CmmClock.Now - _emStartPendingAt < 10f) return;
                CMMConfig.D("[CMM] Emitter start job did not return within 10 s — re-allowing dispatch.");
                _emStartPending = false;
                _emPendingSlot = -1;
            }

            string file = st.OnAir;
            if (file == null) return;

            // ── THE SLOT, AND THE RECEIVER, BEFORE ANYTHING CAN RETURN ────────────────────────
            // This used to sit below the level hold, and that was the bug behind "the audio is
            // still cutting to nothing between stations". The hold returns early and is retried on
            // the next decision tick, for up to LoudnessStartHoldMs — three seconds in the log —
            // so arming the receiver after it meant the static began at the moment the voice did.
            // The hold is the LONGEST part of the gap and the exact thing the noise exists to
            // cover; anything that covers a wait has to be armed before the wait, not after it.
            int slot = st.Slot >= 0 ? st.Slot : FreeSlot();
            if (slot < 0) return;   // shouldn't happen (SelectAndVoice frees one first)

            // A slot last used by a DIFFERENT station still holds its queued dry audio — flush it
            // (and the slot's render state) so nothing of the old station plays under this voice.
            ClearOffAir(slot);   // something real is landing here; the carrier is done
            // A RETUNE FADES IN OVER THE ARRIVE, so the music rising and the noise falling are the
            // same span and cross in the middle. A 200 ms advance fade under a 900 ms noise fade
            // is what left the hiss sitting on top of a finished station.
            // (moved below, next to `retuned`, so it covers every deliberate retune and not only
            // the ones the tune machine owns)
            bool changedStation =
                !string.Equals(_slotLastStation[slot], st.Name, StringComparison.OrdinalIgnoreCase);
            // ONLY A HAND ON THE DIAL SEARCHES. A slot taking its FIRST station (world entry), a
            // station arriving because you walked into range, and a nearer station stealing a slot
            // are all "changes" and none of them is a retune — greeting the player with static on
            // load is the report this came from. A recent deliberate tune is the whole test.
            // A RECENT TUNE IS THE WHOLE TEST, and it used to also require the slot to have held
            // a station before -- which was wrong and is why the static sometimes did not happen
            // at all. A slot released while idle has its last station cleared to null, and a
            // station that lands on a FREE slot has never held one, so both of those retunes were
            // silently excluded. World entry is already excluded by the tune stamp alone (a fresh
            // world has never had one), so the extra condition bought nothing and cost the cases
            // most likely to occur.
            // ...and NOT when the borrow already has this one. The borrow is the sweep for the
            // retune it took, running on its own slot at the same speaker; arming here as well
            // put two receivers on one dial turn. Hidden until the deadlock above was fixed,
            // because the two used to be seven seconds apart instead of simultaneous.
            bool retuned = changedStation
                        && CmmClock.Now - EmitterTuner.LastTuneAt < RetuneWindow
                        && _borrowOwnsTune != EmitterTuner.LastTuneAt
                        // ...and not while MaintainTune owns this slot's receiver. It is already
                        // searching, deliberately, with the old station buried under it; arming
                        // again here restarts the ramp and throws the leave away.
                        && !(_tunePhase != 0 && slot == _tuneSlot);
            // A RETUNE'S MUSIC RISES OVER THE SAME SPAN THE NOISE FALLS, so they cross in the
            // middle and finish together. Left to itself this is the Radio context's fadeintime,
            // five seconds, which is a context changing rather than a dial settling -- and against
            // a 900 ms noise fade it left the hiss gone long before the station was up.
            // THE HANDOVER IS A RETUNE TOO. `retuned` is false by construction on the tune
            // machine's path -- it excludes `_tunePhase != 0 && slot == _tuneSlot`, because the
            // machine owns that lane's RECEIVER. But it does not own the station's FADE, and this
            // line only ever ran for the other paths. So a handed-over station fell back to
            // StartFade(), which resolves to the Radio context's fadeintime="5": the noise cleared
            // in a second over music that was a fifth of the way up and still climbing for another
            // four. Exactly the defect the comment above claims to have fixed.
            bool ownedByTune = _tunePhase != 0 && slot == _tuneSlot;
            if (retuned || ownedByTune) fadeIn = ArriveRamp;

            if (changedStation)
            {
                // FLUSH ONLY WHEN THERE IS A STATION TO FLUSH. The ring can still hold a cushion of
                // the previous station's dry audio, and discarding it is what stops that playing
                // under the new voice. But it discards whatever is queued, and on a slot with no
                // voice the only thing queued is OUR NOISE — so retuning again while the first
                // retune was still searching cut the static dead mid-roar. Reported as a hard cut
                // when changing station twice quickly.
                //
                // _slotOwner is the test: it is set when a voice lands and cleared when one stops,
                // so it is exactly "is there a station's audio in this ring". A searching slot has
                // none, and nothing needs discarding.
                // ...AND NOT THROUGH A HANDOVER. MaintainTune pins the incoming station to the
                // slot before it starts, so _slotOwner is set while the lane still holds nothing
                // but OUR NOISE -- and the flush discards it, then re-primes from empty with 40 ms
                // of zeros. That is the "fraction of a second of no audio, almost a pop", landing
                // exactly where the two stations change hands.
                // FLUSH WHATEVER WE DID NOT PUT THERE. The old test was "does a station own this
                // lane", which is false for a lane that was RELEASED -- and releasing does not
                // empty the ring. So a cold start onto a recently freed lane played out the
                // previous station's queued tail (measured at up to 240 ms) and then jumped into
                // the new one: "the audio pops and jumps forward or backward, like it's trying to
                // connect two clips together".
                //
                // The only thing that must survive a flush is noise WE are generating, so that is
                // the test: a lane a tune or a borrow is searching on keeps its ring, everything
                // else starts clean.
                bool oursAlready = (_tunePhase != 0 && slot == _tuneSlot) || slot == _borrowSlot;
                if (!oursAlready) CmmMixer.GetOrCreate().FlushEmitterSlot(slot);
                // THE RECEIVER GOES SEARCHING, AT THE SPEAKERS. Only on a real station change: a
                // track advancing within one station is not a dial being turned, and hiss over it
                // would be a fault rather than a feature.
                if (retuned)
                {
                    CmmMixer.GetOrCreate().SetEmitterRecv(slot, 0f, SearchRamp);
                    _slotArmedAt[slot] = CmmClock.Now;
                    _slotLockAt[slot] = 0f;   // a new sweep cancels the one it interrupted
                }
                // ...AND DO NOT ERASE THE LEAVE'S OWN STAMP. `retuned` is false on the tune
                // machine's lane, so this else-branch wiped the _slotArmedAt the leave had set --
                // which is what the arrive's minimum-static deadline is measured from. minEnd
                // became 0, the lock fired the instant the voice landed, and the static floor the
                // leave had just spent 700 ms earning was thrown away.
                else if (!ownedByTune) _slotArmedAt[slot] = 0f;
                // ...and its speakers, so the noise has somewhere in the world to come from. The
                // point set is already current — GroupSnapshot filled it earlier this tick.
                // CLEAR LINE, NOT st.PtGain. PtGain is not a gain -- it is added VIRTUAL DISTANCE
                // in blocks, and the output mutes a point outright once distance+extra reaches
                // Range. GroupSnapshot rebuilds Pts/PtFlags/PtCount every tick but never PtGain;
                // only PostProcessGains writes it, and that runs for VOICED stations. So a search
                // published with it carried whatever the occlusion model last wrote for a
                // different, re-ordered speaker set -- and if the station last played far away, or
                // had never played (seeded to Range, "muted until the first flood lands"), the
                // static rendered at exactly zero for the whole search. Nothing repaired it
                // either: every occlusion path needs a voice or a committed slot, and a search has
                // neither. The sweep borrow already got this right with _borrowGain[0] = 0f.
                // ...EXCEPT ACROSS A HANDOVER, where the occlusion is not stale at all: it was
                // just carried over from the station this speaker is leaving, for the same speaker
                // in the same room. Publishing clear line there is a step UP in level for anyone
                // listening from behind a wall, one tick before PostProcessGains puts it back.
                bool tuned = _tunePhase != 0 && slot == _tuneSlot && _tuneGainValid;
                float[] gains = st.PtGain;
                if (!tuned)
                {
                    for (int k = 0; k < st.PtCount && k < st.PtGain.Length; k++) _searchGain[k] = 0f;
                    gains = _searchGain;
                }
                CmmMixer.GetOrCreate().SetSlotPoints(slot, st.Pts, gains, st.PtFlags, st.PtCount);
                // Claimed NOW, so the retries the hold causes do not re-flush and re-arm every
                // tick, which would restart the search ramp four times a second and stop the
                // noise ever settling into its wander.
                _slotLastStation[slot] = st.Name;
            }

            // ── HOLD FOR THE LEVEL, exactly as the personal radio does ─────────────────────────
            // Reported as "the audio goes up before it changes". An unmeasured track starts at
            // LoudnessUnmeasuredDb (-5 dB, a deliberate bias against starting hot), and its real
            // gain lands only when the measurement finishes -- which the log times at over a
            // second for an m4a. Against a -18 LUFS target a quiet track then RISES by ten dB or
            // more, a second after the station changed: exactly the audible ramp reported.
            //
            // The personal radio has always held its start for this (StartFromClock's gain gate);
            // the emitter layer never did, which is the whole asymmetry. Bounded, so a track that
            // cannot be measured starts anyway and settles as it used to, and the broadcast clock
            // keeps running so the hold just rejoins a moment later in the song.
            if (CMMConfig.LoudnessNormalizationEnabled && !LoudnessStore.IsMeasured(file))
            {
                float now = Time.realtimeSinceStartup;
                string key = st.Name + "|" + file;
                float until;
                if (!_gainWaitUntil.TryGetValue(key, out until))
                {
                    // A SHORTER HOLD THAN THE PERSONAL RADIO'S. Reported as stations taking
                    // forever to tune, and the arithmetic agrees: LoudnessStartHoldMs is three
                    // seconds, and an unmeasured track pays all of it before the voice even
                    // starts. That is a fair trade for the radio in your pocket, which is the only
                    // thing playing and whose level jump you would hear plainly. A speaker across
                    // the room is background, its level error is smaller against distance and
                    // occlusion, and three seconds of hiss to hear it is a worse deal than
                    // arriving slightly hot. Its own knob, so the two can differ.
                    until = now + CMMConfig.WorldEmitterLevelHoldMs / 1000f;
                    _gainWaitUntil.Clear();          // one station is joining at a time
                    _gainWaitUntil[key] = until;
                    CMMConfig.D($"[CMM] Station '{st.Name}': holding start for level ({Path.GetFileName(file)}).");
                }
                if (now < until)
                {
                    ContextMusicManager.RequestPrewarm(file);
                    return;                           // the decision tick retries
                }
                CMMConfig.D($"[CMM] Station '{st.Name}': level hold expired, starting unmeasured.");
            }
            _gainWaitUntil.Clear();

            float trackGain = LoudnessStore.GetTrackGain(file);
            if (fresh)
            {
                CmmMixer.GetOrCreate().ResetEmitterAcoustics();
                ExpireBirths();
            }

            // Everything the job needs, captured on this thread: it must touch no Unity API and no
            // station object, since a catalog rebuild can replace one while the job runs.
            double offset = st.OnAirOffset;
            if (offset < 0) offset = 0;
            Vector3 pos = new Vector3(st.NearestPos.x + 0.5f, st.NearestPos.y + 0.5f, st.NearestPos.z + 0.5f);
            float minDist = CMMConfig.WorldEmitterMinDistance, range = CMMConfig.WorldEmitterRange;
            string stationName = st.Name;
            int gen = _emStartGen;
            float fade = fadeIn;
            bool freshCap = fresh;

            _emPendingFade = fade;
            _emStartPending = true;
            _emStartPendingAt = CmmClock.Now;
            _emPendingSlot = slot;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                long id = 0; bool openFailed = false;
                try
                {
                    IntPtr dec = AudioDecoder.OpenNativeHandle(file, out int kind, out int srcCh,
                                                               out int srcRate, out long total);
                    if (dec == IntPtr.Zero) openFailed = true;
                    else if (System.Threading.Volatile.Read(ref _emStartGen) != gen)
                    {
                        // Superseded while opening. Close it here rather than adding a voice the
                        // commit would only have to remove.
                        AudioDecoder.CloseNativeHandle(dec, kind);
                    }
                    else
                    {
                        // Continuity: seek to the schedule's offset so we rejoin the broadcast
                        // mid-song. This is the expensive half on a long file, and it is why the
                        // whole job is off the frame loop.
                        long startFrame = 0;
                        if (srcRate > 0) startFrame = (long)(offset * srcRate);
                        if (total > 0 && startFrame >= total) startFrame = 0;   // clock/length mismatch safety
                        // PlayEmitter → cmm_mix_add_emitter takes ownership of dec and frees it on
                        // every failure path (returns 0), so a 0 id must NOT be closed again here.
                        // loop:false so the track ENDS (retiring the voice) and the station advances.
                        id = CmmMixer.GetOrCreate().PlayEmitter(dec, kind, srcRate, srcCh, total,
                                                                fade, loop: false,
                                                                pos.x, pos.y, pos.z,
                                                                minDist, range,
                                                                trackGain: trackGain,
                                                                startFrame: startFrame, slot: slot);
                    }
                }
                catch { openFailed = id == 0; }
                _emDoneVoiceId = id;
                _emDoneStation = stationName;
                _emDoneFile = file;
                _emDoneGain = trackGain;
                _emDoneSlot = slot;
                _emDoneGen = gen;
                _emDoneOpenFailed = openFailed;
                _emDoneFresh = freshCap;
                _emStartDone = true;   // publishes every field above
            });

            ContextMusicManager.RequestPrewarm(file);   // settle the gain in later if it wasn't measured yet
            // Prime the mixer BEFORE the job can add the voice, so the very first render block is
            // already positioned and at level rather than snapping into place a tick later.
            PushTuning();           // apply the curve before the first render block
            PushEmitterMaster();    // set the radio level before the first block
            PushListener(player);   // prime the listener so the very first render block is positioned
            CMMConfig.D($"[CMM] Station '{st.Name}' opening (slot {slot}): {Path.GetFileName(file)} @ {offset:0}s " +
                        $"(nearest speaker {st.NearestDist:0.0}m; B={BroadcastSeconds():0}s).");
        }

        // Release a station's voice AND its slot.
        // ── Asynchronous station start ──────────────────────────────────────────────────────────
        // Starting a station is a decoder open plus a seek to the broadcast position, and both used
        // to run on this tick, on the main thread. Measured in a normal session: 403 ms for Lit FM
        // joining 645 seconds into a podcast, and four of the twelve worst frame spikes in the log
        // were station starts. Deep seeks are the common case, not the exotic one — the whole point
        // of the anchored schedule is that you join mid-song.
        //
        // So the open and the add go to a ThreadPool job and the result is committed on a later
        // tick. The personal radio has worked this way since it was written; this is the same
        // shape, and cmm_mix_add_emitter is as safe off-thread as cmm_mix_add is.
        //
        // ONE in flight at a time, globally. Starts are events (a speaker comes into range, a
        // track ends), so serializing them costs a station at most a tick or two, and it keeps the
        // invalidation story to a single generation counter instead of one per station.
        private static volatile bool _emStartPending;
        private static float _emStartPendingAt;          // hang safety: re-allow dispatch after 10 s
        private static int _emStartGen;                  // bumped by anything that invalidates a pending start
        private static volatile bool _emStartDone;
        private static long   _emDoneVoiceId;
        private static string _emDoneStation, _emDoneFile;
        private static float  _emDoneGain;
        private static int    _emDoneSlot, _emDoneGen;
        // The slot the in-flight start is opening for. Reserved rather than owned: a failed open
        // leaves no claim to unwind, matching what the synchronous version did.
        private static int    _emPendingSlot = -1;
        private static bool   _emDoneOpenFailed;
        private static bool   _emDoneFresh;

        // Everything that means "the voice we were opening for is no longer the voice we want".
        // Cheap enough to call liberally; the cost of missing one is a voice that starts after the
        // reason for it has gone.
        private static void InvalidatePendingStart() => _emStartGen++;

        // Main thread, start of tick: land a finished open, or drop it if the world moved on.
        private static void CommitPendingStart()
        {
            if (!_emStartDone) return;
            _emStartDone = false;
            _emStartPending = false;
            _emPendingSlot = -1;

            long id = _emDoneVoiceId;
            string stationName = _emDoneStation, file = _emDoneFile;
            _emDoneVoiceId = 0; _emDoneStation = null; _emDoneFile = null;

            // The open failed. Drop the track from the schedule and let the next tick re-resolve --
            // one poisoning per tick rather than the five-in-five-milliseconds burst the old inline
            // retry loop produced.
            if (_emDoneOpenFailed)
            {
                _emDoneOpenFailed = false;
                if (file != null)
                {
                    CMMConfig.D($"[CMM] Station '{stationName}': on-air track failed to open " +
                                $"({Path.GetFileName(file)}) — poisoning in schedule.");
                    RadioStationCatalog.PoisonTrack(file);
                }
                return;
            }
            if (id == 0) return;

            Station st = null;
            for (int i = 0; i < _stations.Count; i++)
                if (string.Equals(_stations[i].Name, stationName, StringComparison.OrdinalIgnoreCase))
                { st = _stations[i]; break; }

            // Superseded while we were opening -- the station stopped, went out of range, was
            // replaced by a rebuild, or already has a voice. Retire the orphan rather than leaving
            // it playing to nobody: an emitter voice is not crossfaded out by the next add, so an
            // abandoned one would simply keep going.
            bool stale = _emDoneGen != _emStartGen || st == null || st.VoiceId != 0
                         || st.NearestDist == float.MaxValue;
            if (stale)
            {
                CmmMixer.Instance?.RemoveEmitter(id, StopFade);
                CMMConfig.D($"[CMM] Station '{stationName}': start superseded while opening — voice dropped.");
                return;   // the slot's receiver is released by ReleaseIdleReceivers below
            }

            // The decision has been acted on; the paper is authoritative again.
            _onAirPinned.Remove(st.Name);
            st.VoiceId = id;
            st.VoiceTrack = file;
            st.VoiceGainApplied = _emDoneGain;
            st.Slot = _emDoneSlot;
            _slotOwner[_emDoneSlot] = st;
            // LOCK ON. Ramped, not switched: the noise has to outlast the arrival (g_recvNoiseHold)
            // or the station is just a fade with hiss laid over it, which is the finding recorded
            // against the personal radio's v47. Harmless when the receiver was never armed — the
            // ramp simply runs from 1 to 1.
            // THE DIAL HAS TO TRAVEL. A warm station opens in milliseconds, and locking the
            // moment its voice landed meant a retune to something already decoded produced no
            // sweep at all -- which is not how a set behaves. Nothing about turning a knob cares
            // whether the station you are moving to happens to be easy to receive; the dial passes
            // through the same noise either way, and the station is BURIED under it until you
            // settle, not withheld until you arrive. So the programme starts now, on time, and the
            // lock is scheduled for when the sweep has run its minimum.
            // THE STATIC DOES NOT START LIFTING UNTIL THE MUSIC IS ALL THE WAY UP.
            //
            // It used to ramp 0 -> 1 over `fade + ArriveTail` STARTING when the voice landed, on
            // the theory that a longer ramp than the fade keeps the hiss around longer. It does
            // not: `signal` is not a volume. By half way the station is already largely out from
            // under the noise, so at the moment the music finally reached full level the signal
            // was already most of the way home and the hiss nearly gone. That is "static fades out
            // before the music is at full volume" -- an overlap expressed as a ratio when what was
            // asked for is a guarantee.
            //
            // So the deadline is the music, not a proportion of it: full level first, buried the
            // whole way, and only then does the receiver lift. The minimum sweep still applies, so
            // a station that opens instantly cannot skip the dial travel.
            // THEY RISE AND FALL TOGETHER, and finish together. Two readings of "the static is
            // never gone until the music is at 100%" are possible and only one of them sounds
            // right. Starting the fade-out AT full level satisfies the words, and buys 900 ms of
            // hiss laid over a station that has already completely arrived -- measured at 2.27 s
            // for a retune, against about 1.8 s on the pocket radio, with the extra second spent
            // talking over the music. The radio does not do that: its arrive runs CONCURRENTLY
            // with the station coming up, so the two cross and the noise is gone at the moment the
            // music is there.
            //
            // So the ramp ENDS at full rather than beginning there. The station's own fade is what
            // sets the length -- StartStation stretches it to ArriveRamp for a retune, so both
            // sides of the crossover are the same span and land together. The minimum sweep still
            // holds the start off if the dial has not travelled yet.
            // ANCHORED AFTER THE RAMP, NOT AT THE ARM -- the pocket radio's own lesson, which I
            // reintroduced here the moment its 700 ms leave replaced the emitter's 120 ms search:
            //
            //   "ANCHORED AFTER THE LEAVE, NOT AT THE PRESS. Anchoring it at the press meant the
            //    1.2 s fade-out ate most of the 1.5 s floor and left about a third of a second of
            //    actual static -- a fifth of what was auditioned."
            //
            // Against a 100 ms floor and a 700 ms slide into the noise, the arrive began when the
            // signal was about a seventh of the way down and turned straight back. The hiss never
            // reached a level you could hear, and on the shortest changes it never audibly
            // happened at all -- "static so low I couldn't hear it", and "several station changes
            // didn't play static at all".
            float minEnd = _slotArmedAt[_emDoneSlot] > 0f
                         ? _slotArmedAt[_emDoneSlot] + SearchRamp + MinStatic
                         : 0f;
            // THE NOISE SETS THE SPAN AND THE MUSIC FOLLOWS IT. Reading the length off the
            // station's own fade inverted that, and the Radio context ships fadeintime="5" -- so a
            // retune's hiss took FIVE SECONDS to clear, against about two on the pocket radio.
            // A context fade is for a context changing; it has no business setting how long a dial
            // takes to settle. ArriveRamp is the tuned, bounded value for that, and StartStation
            // stretches a retuning station's fade to match it so the two still cross symmetrically.
            float ramp = ArriveRamp;
            if (minEnd > CmmClock.Now)
            {
                _slotLockAt[_emDoneSlot]   = minEnd;   // dial still travelling; start when it lands
                _slotLockRamp[_emDoneSlot] = ramp;
            }
            else CmmMixer.Instance?.SetEmitterRecv(_emDoneSlot, 1f, ramp);
            CMMConfig.D($"[CMM] Station '{st.Name}' started (slot {_emDoneSlot}): " +
                        $"{Path.GetFileName(file)} (nearest speaker {st.NearestDist:0.0}m).");

            // Seed the voice's occlusion before its first audible block. Advance/hot-swap: carry the
            // last landed point set (same speakers, same ear, ≤ a tick old) straight onto the new
            // voice. Fresh start (no landed field yet): seed MUTED — virtual distance ≥ range ⇒
            // silent — and let the first flood glide the true value in (the start fade masks it).
            if (CMMConfig.WorldEmitterOcclusion && CMMConfig.WorldEmitterPathRouting)
            {
                if (!_emDoneFresh && st.LastPtPushCount > 0 && CmmClock.Now - st.LastPtPushAt < 1f)
                {
                    CmmMixer.Instance?.SetSlotPoints(st.Slot, st.Pts, st.PtGain, st.PtFlags, st.LastPtPushCount);
                }
                else
                {
                    WritePoint(st, 0, st.NearestPos, st.NearestFlags);
                    st.PtGain[0] = CMMConfig.WorldEmitterRange;   // muted until the first flood lands
                    CmmMixer.Instance?.SetSlotPoints(st.Slot, st.Pts, st.PtGain, st.PtFlags, 1);
                }
                _forceRefresh = true;
            }
        }

        private static void StopStation(Station st, string why = "?")
        {
            StopVoice(st, why);
            if (st.Slot >= 0)
            {
                // ONLY IF THIS SLOT IS STILL OURS. st.Slot holds an index until the station
                // stops, and a slot can be REASSIGNED before that happens — a nearer station
                // steals it, or a retune starts searching on it. The owner assignment below has
                // always been guarded for exactly that reason; these two were not, so a station
                // shutting down cleared the receiver and zeroed the speakers of whatever had
                // taken its slot over. That is the brief cut to nothing: the noise and the
                // programme share one lane, one ring and one point set, so silencing the slot
                // silences the search running on it.
                //
                // The guard has to come FIRST, because clearing st.Slot is what ends the question.
                // THE RECEIVER IS NOT TOUCHED HERE, and that is the point. Releasing it was
                // spread over this path, the borrow, and the idle sweep, and every one of them
                // was a chance to silence a slot something else was using — which is every cut
                // reported against this feature. ReleaseIdleReceivers is now the ONLY thing that
                // releases a receiver or clears a slot's speakers. It already knows the full
                // picture (no owner, no start in flight, no borrow, past the search deadline);
                // this path knows only that one station is finishing, which was never enough to
                // decide whether the slot is idle.
                if (_slotOwner[st.Slot] == st) _slotOwner[st.Slot] = null;
                // AND THE LOCK IT WAS STILL OWED. A deferred lock outlives the station it was
                // scheduled for, and the next thing on this slot inherits it — see the ordering
                // note in ReleaseIdleReceivers.
                _slotLockAt[st.Slot] = 0f;
                st.Slot = -1;
            }
        }

        // Release just the native voice (keep the slot — used by the interrupt+restart path).
        // WHY IT STOPPED, NOT JUST THAT IT DID. A restart is a decoder open plus a seek to the
        // broadcast position -- real disk work, which is why players report the radios "seeming
        // like they are loading" after being briefly cut off. Whether that is correct depends
        // entirely on what stopped them, and "Station 'X' stopped." said nothing. Same lesson as
        // the door stopwatch: a diagnostic that omits the deciding term settles no argument.
        private static void StopVoice(Station st, string why = "?")
        {
            // Even with no live voice there may be one being OPENED for this station; the
            // generation bump is what stops it landing after the reason for it has gone.
            InvalidatePendingStart();
            if (st.VoiceId == 0) return;
            CmmMixer.Instance?.RemoveEmitter(st.VoiceId, StopFade);
            CMMConfig.D($"[CMM] Station '{st.Name}' stopped ({why}).");
            st.VoiceId = 0;
            st.VoiceTrack = null;
        }

        private static void StopAll()
        {
            for (int i = 0; i < _stations.Count; i++) StopStation(_stations[i], "stop all");
            _occlPending = false;   // abandon any in-flight flood (its finish targets dead voices)
        }

        // ── Catalog rebuild hook (called by RadioStationCatalog.RebuildCatalog) ─────────────────
        // Migrate live emitter voices onto the NEW station objects by name: a same-name station
        // keeps its voice/slot (its airing track must still be in the new schedule or the interrupt
        // path swaps it next tick); anything not migrated is released. `old` = the pre-rebuild
        // name → station map.
        internal static void OnCatalogRebuilt(Dictionary<string, Station> old)
        {
            // Station objects are about to be replaced, so anything mid-open is opening for a
            // station that will not exist by the time it lands.
            InvalidatePendingStart();
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (!old.TryGetValue(st.Name, out Station prev)) continue;
                st.VoiceId = prev.VoiceId; st.VoiceTrack = prev.VoiceTrack;
                st.VoiceGainApplied = prev.VoiceGainApplied;
                st.Slot = prev.Slot;
                if (st.Slot >= 0) _slotOwner[st.Slot] = st;
                st.LastPtPushCount = prev.LastPtPushCount; st.LastPtPushAt = prev.LastPtPushAt;
                st.Seal = prev.Seal; st.Solid = prev.Solid; st.Depth = prev.Depth;
                prev.VoiceId = 0; prev.Slot = -1;
                // An airing track that left the new schedule is interrupted by MaintainPlayingStations
                // only if the FILE vanished; a pool re-shuffle alone keeps it playing to its end.
            }
            // The rule itself lives in EmitterSlots -- no UnityEngine, no game types -- so a test
            // can drive it directly. It cannot be tested here: this method needs a live catalog,
            // and the interleaving that produced the leak (a station holding a slot with no voice
            // yet) is reached by timing, so a test against the real thing would pass by luck.
            int reclaimed = EmitterSlots.ReleaseAfterRebuild(
                old, _stations, _slotOwner,
                voiceId => CmmMixer.Instance?.RemoveEmitter(voiceId, StopFade));
            if (reclaimed > 0)
                Log.Warning($"[CMM] {reclaimed} emitter slot(s) were still held by a station that no "
                          + "longer exists after a catalog rebuild — reclaimed.");
        }

        // ── Listener / tuning / reverb pushes ───────────────────────────────────────────────────

        // Push the listener basis (position + forward + right) to the mixer, ALL from the camera
        // transform (position is RENDER space under 7D2D's floating origin, so + Origin.position
        // converts back to TRUE WORLD space; directions are origin-invariant).
        private static void PushListener(EntityPlayerLocal player)
        {
            Transform t = player.cameraTransform;
            if (t == null) return;
            CmmMixer.Instance?.SetListener(t.position + Origin.position, t.forward, t.right);
        }

        // Relay the live spatial-curve tuning + coloration from config (hot-reloaded) to the native
        // spatializer — how in-game settings.xml edits take effect live.
        private static void PushTuning()
        {
            // Tone follows the model that produced the number. The shipped model's occlusion IS a
            // virtual distance and reads correctly as one; the native model's is an attenuation, and
            // reading it as distance dulled a speaker the listener was standing on.
            CmmMixer.Instance?.SetEmitterOcclTone(CMMConfig.WorldEmitterNativeAcoustics,
                                                  CMMConfig.WorldEmitterOcclusionFullDb);
            CmmMixer.Instance?.SetEmitterSlew(CMMConfig.WorldEmitterOcclusionSlewMs);
            CmmMixer.Instance?.SetEmitterTuning(CMMConfig.WorldEmitterMinDistance,
                                                CMMConfig.WorldEmitterRange,
                                                CMMConfig.WorldEmitterFalloff,
                                                CMMConfig.WorldEmitterMuffle,
                                                CMMConfig.WorldEmitterStereoWidth);
            CmmMixer.Instance?.SetEmitterRecvTrim(CMMConfig.WorldEmitterStaticTrim);
            CmmMixer.Instance?.SetEmitterColor(CMMConfig.WorldEmitterColorDrive,
                                               CMMConfig.WorldEmitterColorStatic,
                                               CMMConfig.WorldEmitterColorVoice,
                                               CMMConfig.WorldEmitterHamColorDrive,
                                               CMMConfig.WorldEmitterHamColorStatic,
                                               CMMConfig.WorldEmitterHamColorVoice);
        }

        // Probe the room around the listener once and push the reverb params: the ROOM (size, damp,
        // local level) is the listener's and is shared; the CARRY send + depth are PER-SLOT — each
        // voiced station's own seal/solid gating from ITS occlusion pass, so a sealed-off station's
        // reverb doesn't leak just because another station is in the open.
        private static void PushReverb(EntityPlayerLocal player)
        {
            World world = GameManager.Instance?.World;
            if (!CMMConfig.WorldEmitterReverbEnabled || world == null)
            {
                for (int s = 0; s < MaxSlots; s++) { _revWets[s] = 0f; _revDepths[s] = 0f; _revWetSmooth[s] = 0f; }
                CmmMixer.Instance?.SetEmitterReverb(_revWets, _revDepths, 0.5f, 0.5f, 1f, 0f, 0f, 0f);
                return;
            }
            Vector3 ear = ListenerWorldPos(player);
            long probeT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            EmitterAcoustics.ProbeRoom(ear, world, out float avgDist, out float roomSize, out float reflect, out float openFrac, out bool roofed);
            float probeMs = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - probeT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            // Prefer the FLOOD's connected-air extent for room SIZE (walks around corners/tunnels).
            if (CMMConfig.WorldEmitterOcclusion && CMMConfig.WorldEmitterPathRouting && EmitterAcoustics.AirCellCount > 0)
                roomSize = Mathf.Max(roomSize, EmitterAcoustics.AirRoomSize);
            EmitterAcoustics.ComputeReverb(roomSize, reflect, openFrac, roofed, out float roomWet, out float room, out float damp);
            float master = CMMConfig.WorldEmitterReverbWet;   // LEVEL lever (both buses)
            float local = roomWet * master;
            float halfWall = 0.5f * Mathf.Max(1f, CMMConfig.WorldEmitterOcclusionWallDistance);

            room *= CMMConfig.WorldEmitterReverbDecay;   // TAIL-length lever (independent of level)

            // Asymmetric inertia (the 2026-07-08 blip fix): slow RISE (~4 s) crushes the enclosure
            // transients of walking under an overhang; the FALL keeps the ~1.3 s glide so stepping
            // out dries promptly. dt-based so a stalled push cadence can't turn one tick into a jump.
            float nowRev = CmmClock.Now;
            float dtRev = Mathf.Clamp(nowRev - _lastRevPush, 0.05f, 1f);
            _lastRevPush = nowRev;
            float aUp = 1f - Mathf.Exp(-dtRev / 4f);
            float aDn = 1f - Mathf.Exp(-dtRev / 1.3f);

            for (int s = 0; s < MaxSlots; s++)
            {
                Station st = _slotOwner[s];
                float carry = 0f, depth = 0f;
                // Carry only for stations with a speaker inside the DRY range: the wet's larger
                // native reach (range × ReverbRangeMult) exists so a station you're LEAVING rings
                // out gracefully (the 2 s tail + this EMA's fall cover that) — it must not keep a
                // beyond-range station's music continuously washing into your room's reverb (the
                // "hearing another station's reverb" report with two radios ~100-150 m apart).
                if (st != null && st.VoiceId != 0 && st.NearestDist <= CMMConfig.WorldEmitterRange)
                {
                    // CARRY: the next-room cue — HARD-gated by this station's solid seal (bleed
                    // through solids shouldn't exist at any setting; open-air corners still carry),
                    // scaled by the ReverbCarry taste knob, then PROPORTIONALLY damped by the solid
                    // on its least-blocked route (a shut wooden door ≈ −3 dB long before the seal).
                    carry = roomWet * master * CMMConfig.WorldEmitterReverbCarry * (1f - st.Seal);
                    if (CMMConfig.WorldEmitterOcclusion)
                        carry *= Mathf.Pow(0.5f, st.Solid / halfWall);
                    depth = (CMMConfig.WorldEmitterOcclusion && st.Depth < 511f)
                        ? st.Depth
                        : (st.NearestDist < float.MaxValue ? st.NearestDist : 0f);

                    // BOUNCE-ENERGY depth ("sound should bounce until it runs out of energy",
                    // user 2026-07-11): instead of grading the carry by raw route METERS, grade
                    // it by energy surviving the BOUNCES along that route — bounces ≈ depth /
                    // mean-free-path (the room's typical wall-to-wall run), each keeping
                    // `reflect` of the energy. A reflective stone cave barely loses energy per
                    // bounce ⇒ the carry stays alive around corner after corner; an absorptive
                    // wood room eats it in a few. Implemented as a WARP of the depth we push, so
                    // the native curve exp(−1.2·DepthAmt·(d−1)/48) lands on reflect^bounces —
                    // managed-only, no ABI change. Uses the LISTENER's room reflect/size as a
                    // proxy for the path's spaces (honest approximation; the route mostly runs
                    // through the space you're in).
                    if (CMMConfig.WorldEmitterReverbBounce && CMMConfig.WorldEmitterOcclusion)
                    {
                        float mfp = Mathf.Max(4f, roomSize);
                        float r = Mathf.Clamp(reflect, 0.15f, 0.97f);
                        float A = Mathf.Max(0.05f, CMMConfig.WorldEmitterReverbDepth);
                        depth = 1f + 48f * (depth / mfp) * (-Mathf.Log(r)) / (1.2f * A);
                    }
                }
                _revWetSmooth[s] += (carry - _revWetSmooth[s]) * (carry > _revWetSmooth[s] ? aUp : aDn);
                _revWets[s] = _revWetSmooth[s];
                _revDepths[s] = depth;   // native slews depth itself (~0.5 s)
            }
            _revLocSmooth  += (local - _revLocSmooth) * (local > _revLocSmooth ? aUp : aDn);
            _revRoomSmooth += (room - _revRoomSmooth) * aDn;   // tail length: symmetric, size isn't a level blip

            if (CmmClock.Now >= _nextReverbLog)
            {
                _nextReverbLog = CmmClock.Now + 1.5f;
                Station dom = null;
                for (int s = 0; s < MaxSlots; s++)
                    if (_slotOwner[s] != null && (dom == null || _revWetSmooth[s] > 0f)) dom = _slotOwner[s];
                CMMConfig.D($"[CMM] Reverb: enc={1f - openFrac:0.00} rayS={avgDist:0.0} airSize={EmitterAcoustics.AirRoomSize:0.0} airCells={EmitterAcoustics.AirCellCount} size={roomSize:0.0} reflect={reflect:0.00} " +
                            $"roofed={roofed} dom={(dom != null ? dom.Name : "-")} seal={(dom != null ? dom.Seal : 1f):0.00} solid={(dom != null ? dom.Solid : 0f):0.0} depth={(dom != null ? dom.Depth : 0f):0.0} probeMs={probeMs:0.0} " +
                            $"→ carry={_revWets[0]:0.00}/{(MaxSlots > 1 ? _revWets[1] : 0f):0.00}/{(MaxSlots > 2 ? _revWets[2] : 0f):0.00} loc={_revLocSmooth:0.00} room={_revRoomSmooth:0.00} damp={damp:0.00}");
            }
            CmmMixer.Instance?.SetEmitterReverb(_revWets, _revDepths, _revRoomSmooth, damp,
                                                CMMConfig.WorldEmitterReverbRangeMult, CMMConfig.WorldEmitterReverbPan,
                                                CMMConfig.WorldEmitterReverbDepth, _revLocSmooth);
        }

        // The listener's world-space position (camera ear), matching block coords. Falls back to
        // the entity position if the camera isn't up yet.
        internal static Vector3 ListenerWorldPos(EntityPlayerLocal player)
        {
            Transform t = player.cameraTransform;
            return t != null ? t.position + Origin.position : player.position;
        }

        // Station|track → the moment its level hold expires. Cleared on any start, so it never
        // holds a stale deadline; one entry at a time because one station joins at a time.
        private static readonly Dictionary<string, float> _gainWaitUntil =
            new Dictionary<string, float>(StringComparer.Ordinal);

        // ── TUNING, THE WAY THE POCKET RADIO DOES IT ────────────────────────────────────────
        //
        // PersonalRadio has ONE receiver, always in the path, and a dial turn is three moves
        // through it: take the signal DOWN so the old station sinks under rising hiss while it
        // goes on playing; swap the voice underneath, entirely hidden; take the signal UP so the
        // new station rises out of the noise. Its own comment names what happens otherwise --
        // "click, silence, then static, then a tune-in on a station you could already hear" --
        // which is precisely what the emitters did, all day.
        //
        // The emitter could not copy it because its receiver lives on a SLOT (a station's lane,
        // shared by up to eight speakers), not on a speaker, so hissing one speaker hissed
        // everyone on that station. Hence the sweep borrow: hand the speaker a spare slot purely
        // to own a receiver. But a borrowed slot carries NO VOICE, so the hiss had nothing under
        // it -- no station sinking away, none rising -- and the arrival could only be the
        // crossfade the pocket radio is deliberately not.
        //
        // The way out is that the premise is usually false. When the retuned speaker is the ONLY
        // one on its station's slot -- one radio in a room, the ordinary case and the one players
        // actually dial -- that slot IS the speaker's tuner. Nobody else is on it, so it can hiss
        // freely, and it already carries the old station's audio. All three moves are then
        // available exactly as the pocket radio makes them, on the lane the speaker is already
        // listening to, with no borrow and no competition for slots at all.
        //
        // The borrow stays for the genuinely shared case, where there is no honest way to hiss one
        // speaker without hissing the rest.
        private static readonly Dictionary<Vector3i, string> _posStation =
            new Dictionary<Vector3i, string>();
        private static int    _tuneSlot = -1;
        private static string _tuneFrom, _tuneTo;
        private static float  _tuneSwapAt, _tuneGiveUp;
        // The handover carried the leaving station's occlusion onto the arriving one, so the
        // changeover can publish that instead of clear line.
        private static bool   _tuneGainValid;
        // The lane sitting on the off-air carrier, if any. Cleared the instant a station lands on
        // it or the speaker is retuned, so the tone can never outlive the reason for it.
        private static int    _offAirSlot = -1;
        private static readonly float[] _offAirHz  = new float[3];
        private static readonly float[] _offAirLvl = new float[3];

        private static void GoOffAir(int slot)
        {
            _offAirHz[0] = CMMConfig.WorldEmitterOffAirHz;
            _offAirHz[1] = CMMConfig.WorldEmitterOffAirHz2;
            _offAirHz[2] = CMMConfig.WorldEmitterOffAirHz3;
            for (int i = 0; i < 3; i++) _offAirLvl[i] = CMMConfig.WorldEmitterOffAirLevel;
            CmmMixer.Instance?.SetEmitterTone(slot, _offAirHz, _offAirLvl, 3,
                                              CMMConfig.WorldEmitterOffAirFadeMs / 1000f);
            _offAirSlot = slot;
        }

        // OFF THE CARRIER. `slot` < 0 means "wherever it is" -- the caller knows the reason has
        // gone, not which lane it landed on.
        //
        // It used to take a slot and match on it, so anything that did not happen to land on the
        // SAME lane left the tone sounding: changing station put the new one on another lane and
        // the carrier played under it until some later retune happened to reuse that lane, and
        // switching the radio off never cleared it at all. The reason is always a speaker, never a
        // lane, so the default is now "clear it wherever it is".
        internal static void ClearOffAir(int slot = -1)
        {
            if (_offAirSlot < 0) return;
            if (slot >= 0 && slot != _offAirSlot) return;
            CmmMixer.Instance?.SetEmitterTone(_offAirSlot, _offAirHz, _offAirLvl, 0,
                                              CMMConfig.WorldEmitterOffAirFadeMs / 1000f);
            _offAirSlot = -1;
        }
        private static int    _tunePhase;         // 0 = idle, 1 = leaving, 2 = arriving

        // ── ONE SET OF TIMINGS, AND THEY ARE THE POCKET RADIO'S ─────────────────────────────
        // These read RadioTune* directly rather than the WorldEmitter* pair that used to shadow
        // them. Two sets of knobs for one instrument is not a feature: they drifted (900 against
        // 1000, a 250 ms floor against 100), every fix had to be made twice, and the emitters
        // ended up sounding like a near-miss of the radio in your pocket rather than the same
        // receiver heard across a room -- which is the entire design.
        //
        // Tuning the radio now tunes both, which is what was asked for when the receiver was first
        // shared. The WorldEmitter* timing entries stay in the schema so nobody's file breaks;
        // they simply no longer drive anything.
        private static float LeaveRamp  => CMMConfig.RadioTuneLeaveMs  / 1000f;
        private static float MinStatic  => CMMConfig.RadioTuneMinStaticMs / 1000f;
        // The crossfade the pocket radio spends entirely underneath the static (PersonalRadio's
        // SwapFade). The station being left and the one arriving overlap by this much, buried.
        private const  float SwapFade = 0.25f;

        private static Station ByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < _stations.Count; i++)
                if (string.Equals(_stations[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return _stations[i];
            return null;
        }

        // A speaker tuned to a station that does not resolve sits on the carrier. It needs a lane
        // of its own to do that -- there is no station behind it by definition -- which is the same
        // shape as the sweep borrow, held for as long as the pin stays dead rather than for a
        // sweep. Released the moment the pin resolves again or the speaker is switched off.
        // RAISE THE CARRIER, on a named lane if the caller already owns one.
        //
        // The previous shape asked MaintainDeadAir to find the speaker in the registry snapshot
        // and allocate a lane for itself, and it had two silent exits: the lookup failing, and
        // _deadAirSlot being left non-negative. ClearOffAir() cleared _offAirSlot but never
        // _deadAirSlot, so after ONE dial turn the stale lane index made every later carrier skip
        // the raise entirely -- no tone, and no log line either way.
        //
        // The give-up already holds a lane and already knows the speaker. It does not need to look
        // anything up, so it does not.
        private static void GoDeadAir(Vector3i pos, int preferredSlot)
        {
            _deadAirPos = pos; _deadAirOn = true;
            if (_deadAirSlot < 0)
            {
                _deadAirSlot = preferredSlot >= CmmMixer.StationSlots ? preferredSlot : FreeTuneSlot();
                if (_deadAirSlot < 0)
                {
                    CMMConfig.D("[CMM] Dead air: no tuning lane spare — the carrier waits.");
                    _deadAirOn = false;
                    return;
                }
            }
            CMMConfig.D($"[CMM] Dead air: slot {_deadAirSlot} carries the off-air tone at {pos} — "
                      + $"{CMMConfig.WorldEmitterOffAirHz:0} Hz at level "
                      + $"{CMMConfig.WorldEmitterOffAirLevel:0.00}.");
            GoOffAir(_deadAirSlot);
        }

        // GIVE THE CARRIER'S LANE BACK. Separate from ClearOffAir because that one is about the
        // TONE and this one is about the LANE, and conflating them is what stranded the index.
        private static void EndDeadAir()
        {
            _deadAirOn = false;
            _deadAirMissingAt = 0f;
            if (_deadAirSlot < 0) return;
            ClearOffAir(_deadAirSlot);
            CmmMixer.Instance?.SetSlotPoints(_deadAirSlot, _emptyPts, _emptyGain, _emptyFlags, 0);
            CMMConfig.D($"[CMM] Dead air on slot {_deadAirSlot} ends.");
            _deadAirSlot = -1;
        }

        private static void MaintainDeadAir()
        {
            var snap = EmitterRegistry.Snapshot;
            int at = -1;
            if (_deadAirOn)
                for (int i = 0; i < snap.Count; i++)
                    if (snap[i].Pos.Equals(_deadAirPos)) { at = i; break; }
            // A MISS IS NOT AN ENDING. This tore the carrier down on any tick whose registry
            // snapshot did not happen to contain the speaker -- measured at 2 ms after it was
            // raised, twice, so the tone never sounded for longer than a tick. Whether the speaker
            // is in THIS tick's snapshot is not the question; whether anything still wants dead
            // air is, and that has explicit owners (a dial turn, an empty pin). A miss just means
            // there is nothing to republish right now.
            if (_deadAirSlot < 0) return;
            if (at < 0)
            {
                // ...unless it stays missing, which means the speaker is gone for good.
                if (_deadAirMissingAt <= 0f) _deadAirMissingAt = CmmClock.Now;
                else if (CmmClock.Now - _deadAirMissingAt > 2f) EndDeadAir();
                return;
            }
            _deadAirMissingAt = 0f;

            EmitterRegistry.Emitter e = snap[at];
            // Republished every tick so the tone tracks the listener's occlusion like any other
            // sound out of that speaker -- it is coming from the same box.
            _deadPts[0] = e.Pos.x + 0.5f; _deadPts[1] = e.Pos.y + 0.5f; _deadPts[2] = e.Pos.z + 0.5f;
            _deadGain[0] = 0f;
            _deadFlags[0] = e.Name == "radioHam" ? 1 : 0;
            CmmMixer.Instance?.SetSlotPoints(_deadAirSlot, _deadPts, _deadGain, _deadFlags, 1);
        }

        private static int _deadAirSlot = -1;
        private static readonly float[] _deadPts = new float[3];
        private static readonly float[] _deadGain = new float[1];
        private static readonly int[] _deadFlags = new int[1];

        // WHY A DIAL TURN GOT NO STATIC. Every declining branch in both handlers used to return
        // in silence, so a log of a silent retune showed the dial click, then the station starting,
        // with nothing at all in between -- which is why thirteen hours of logs never named the
        // cause. One line each. They are rare enough to be free and the only thing that makes the
        // next report diagnosable.
        private static void TuneDeclined(string who, string why)
        {
            if (_declinedAt == EmitterTuner.LastTuneAt) return;   // once per press, not once per tick
            _declinedAt = EmitterTuner.LastTuneAt;
            CMMConfig.D($"[CMM] Tune declined by {who}: {why}.");
        }
        private static float _declinedAt = -999f;

        private static void MaintainTune()
        {
            float now = CmmClock.Now;

            // ANY DIAL TURN ENDS THE CARRIER, before we work out which path this retune takes.
            // Doing it inside a path meant the paths that do not run it -- the borrow, and a plain
            // start -- left the tone sounding under the new station.
            if (_handledTuneAt != EmitterTuner.LastTuneAt
                && now - EmitterTuner.LastTuneAt < RetuneWindow) EndDeadAir();

            // AFTER that clear, never before it. Landing on a dead station IS a dial turn, so
            // running this first meant the carrier was raised and then wiped in the same tick by
            // the very retune that called for it -- which is why the one station built to be
            // broken still made no sound.
            MaintainDeadAir();


            // A DIAL TURN DURING A DIAL TURN. The pocket radio has nothing to resolve here: one
            // receiver, one signal, and a fresh press just re-aims from wherever that signal
            // happens to be ("interrupting a half-finished arrival fades from half and takes half
            // as long"). This used to IGNORE the new tune while a phase was running -- but the pin
            // had already moved, so in the arriving phase the snapshot handed the speaker straight
            // to the new station on another lane, at full level, with the old one gone. A hard cut,
            // and precisely the one reported when changing station mid tune-in.
            //
            // Re-aimed instead: same slot, same receiver, ramp back down from wherever it is, and
            // the station now on the lane becomes the one being left. Nothing is stopped, nothing
            // is flushed, and the crossfade under the noise happens again.
            bool fresh = _handledTuneAt != EmitterTuner.LastTuneAt
                      && now - EmitterTuner.LastTuneAt < RetuneWindow;
            if (fresh && _tunePhase != 0 && _tuneSlot >= 0)
            {
                Station on = _slotOwner[_tuneSlot];
                var snapR = EmitterRegistry.Snapshot;
                EmitterRegistry.Emitter er = default(EmitterRegistry.Emitter);
                bool foundR = false;
                for (int i = 0; i < snapR.Count; i++)
                    if (snapR[i].Pos.Equals(EmitterTuner.LastTunePos)) { er = snapR[i]; foundR = true; break; }
                string wantR;
                if (foundR && on != null && EmitterTuner.TryPin(er, out wantR)
                    && !string.IsNullOrEmpty(wantR)
                    && !string.Equals(wantR, on.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Station toR = ByName(wantR);
                    if (toR == null || toR.VoiceId == 0 || toR.Slot == _tuneSlot)
                    {
                        _handledTuneAt = EmitterTuner.LastTuneAt;
                        EmitterTuner.ReleaseSweepHold();
                        _tuneFrom = on.Name; _tuneTo = wantR;
                        _tunePhase = 1;
                        _tuneSwapAt = now + LeaveRamp + MinStatic;
                        _tuneGiveUp = _tuneSwapAt + CMMConfig.WorldEmitterLevelHoldMs / 1000f + 2f;
                        _slotLockAt[_tuneSlot] = 0f;   // the arrive we were waiting on is off
                        CmmMixer.GetOrCreate().SetEmitterRecv(_tuneSlot, 0f, LeaveRamp);
                        CMMConfig.D($"[CMM] Tune: re-aimed mid-tune on slot {_tuneSlot} — "
                                  + $"'{on.Name}' is now the one being left, for '{wantR}'.");
                        return;
                    }
                }
            }

            if (_tunePhase == 1 && now >= _tuneSwapAt)
            {
                // ── HANDOVER, UNDER THE NOISE ───────────────────────────────────────────────
                // The signal is at 0 by now, so none of this is audible: the old station retires
                // and the new one is pinned to the SAME slot. That pinning is what keeps the
                // receiver -- mid-search, at this speaker -- attached to the station it is
                // searching for. Letting the new station find a lane of its own is exactly how
                // the sweep and the arrival ended up on different slots.
                Station from = ByName(_tuneFrom), to = ByName(_tuneTo);
                // OCCLUSION BELONGS TO THE SPEAKER, NOT THE STATION. Carried across the handover
                // because it is the same speaker in the same room: a wall between it and the
                // listener does not care which station is playing. Without this the changeover
                // republishes CLEAR LINE (see the note in StartStation) and the speaker jumps to
                // fully unoccluded for a tick before PostProcessGains puts it back -- a step up in
                // level exactly where the stations change hands, which is the volume pop.
                _tuneGainValid = false;
                if (from != null && to != null && from.PtCount > 0
                    && from.PtGain.Length > 0 && to.PtGain.Length > 0)
                {
                    int n = from.PtCount;
                    if (n > to.PtGain.Length) n = to.PtGain.Length;
                    if (n > from.PtGain.Length) n = from.PtGain.Length;
                    for (int k = 0; k < n; k++) to.PtGain[k] = from.PtGain[k];
                    _tuneGainValid = n > 0;
                }
                if (from != null && from.Slot == _tuneSlot) StopStation(from, "handed over to the dial");
                if (to != null && to.VoiceId == 0)
                {
                    to.Slot = _tuneSlot;
                    _slotOwner[_tuneSlot] = to;
                }
                // IT TOOK THE LANE OR THE TUNE IS OVER. Hissing on a slot the arriving station is
                // not going to use means hissing at a speaker that is already hearing that station
                // from somewhere else. Better a retune with no sweep than a sweep laid over the
                // music it was supposed to introduce.
                if (to == null || to.Slot != _tuneSlot)
                {
                    CmmMixer.Instance?.SetEmitterRecv(_tuneSlot, 1f, ArriveRamp);
                    CMMConfig.D($"[CMM] Tune: '{_tuneTo}' could not take slot {_tuneSlot} "
                              + "(playing elsewhere) — ending the sweep rather than talking over it.");
                    _tunePhase = 0; _tuneSlot = -1;
                    return;
                }
                _tunePhase = 2;
                CMMConfig.D($"[CMM] Tune: handover on slot {_tuneSlot} — '{_tuneFrom}' retires, "
                          + $"'{_tuneTo}' takes the lane under the noise.");
            }

            if (_tunePhase == 2)
            {
                Station to = ByName(_tuneTo);
                // ARRIVE ONLY ONCE THERE IS SOMETHING TO ARRIVE INTO. Waiting on the voice is the
                // whole point: the noise covers the open and the level hold rather than the
                // speaker sitting silent through them. The station comes up FIRST and the static
                // then fades out from under it -- never the other way round, which is a crossfade.
                // THE ARRIVE IS NOT OURS TO FIRE. CommitPendingStart already raises the receiver
                // the moment the voice lands, and it does it over `_emPendingFade + ArriveTail`
                // floored at ArriveRamp -- the track's OWN fade-in plus a tail, so the hiss is
                // still there while the music is climbing out from under it. Setting it here as a
                // flat ArriveRamp ignored the fade entirely, so on any track whose fade runs
                // longer than 900 ms the static finished first. "Static goes before the track has
                // faded in", exactly.
                //
                // So this phase only watches. The voice landing IS the arrival.
                if (to != null && to.VoiceId != 0 && to.Slot == _tuneSlot)
                {
                    CMMConfig.D($"[CMM] Tune: '{_tuneTo}' landed on slot {_tuneSlot} — "
                              + "the start path raises the receiver over its own fade.");
                    _tunePhase = 0; _tuneSlot = -1;
                }
                else if (now >= _tuneGiveUp)
                {
                    // OFF AIR, NOT SILENT -- but NOT A SECOND CARRIER. The station is !Ready,
                    // so GroupSnapshot is already flagging this speaker as dead air and
                    // MaintainDeadAir raises the tone on a lane of its own. Raising one here too
                    // gave two tones and a single _offAirSlot to track them: the first was
                    // orphaned and could never be cleared again.
                    //
                    // The lane also has to be given back. The handover pinned a !Ready station to
                    // it, and nothing ever stops a station that never started, so it would be held
                    // for ever.
                    // THE SECOND DETECTOR. This station is Ready -- it has tracks -- they just do
                    // not open, so GroupSnapshot's test cannot see it and this is the only place
                    // that knows. Hand the speaker to MaintainDeadAir, which owns the carrier and
                    // republishes the point every tick; releasing the lane here without doing that
                    // left the speaker with no point at all, which is silence rather than a tone.
                    if (to != null && to.Slot == _tuneSlot) { _slotOwner[_tuneSlot] = null; to.Slot = -1; }
                    CmmMixer.Instance?.SetEmitterRecv(_tuneSlot, 1f, ArriveRamp);
                    // It already holds a tuning lane and already knows the speaker -- hand both
                    // straight over rather than making the carrier rediscover them.
                    GoDeadAir(EmitterTuner.LastTunePos, _tuneSlot);
                    CMMConfig.D($"[CMM] Tune: '{_tuneTo}' never opened — slot {_tuneSlot} goes off air.");
                    _tunePhase = 0; _tuneSlot = -1;
                }
                return;
            }
            if (_tunePhase == 1) return;   // still leaving

            // ── BEGIN ───────────────────────────────────────────────────────────────────────
            if (_handledTuneAt == EmitterTuner.LastTuneAt) return;
            if (now - EmitterTuner.LastTuneAt > RetuneWindow) return;

            string fromName;
            if (!_posStation.TryGetValue(EmitterTuner.LastTunePos, out fromName))
            { TuneDeclined("tune", "the speaker was on no station"); return; }
            Station fromSt = ByName(fromName);
            // Alone on the lane? Only then is this slot this speaker's tuner. PtCount is last
            // tick's snapshot, taken while the speaker was still on it, which is the state we want.
            if (fromSt == null || fromSt.Slot < 0 || fromSt.VoiceId == 0 || fromSt.PtCount != 1)
            {
                TuneDeclined("tune", fromSt == null ? "the station it was on is gone"
                           : fromSt.VoiceId == 0 ? $"'{fromName}' is not playing"
                           : $"'{fromName}' has {fromSt.PtCount} speakers on this lane, not 1");
                return;   // shared, or not playing — the borrow handles those
            }

            var snap = EmitterRegistry.Snapshot;
            EmitterRegistry.Emitter e = default(EmitterRegistry.Emitter);
            bool found = false;
            for (int i = 0; i < snap.Count; i++)
                if (snap[i].Pos.Equals(EmitterTuner.LastTunePos)) { e = snap[i]; found = true; break; }
            if (!found) return;

            string want;
            if (!EmitterTuner.TryPin(e, out want) || string.IsNullOrEmpty(want))
            { TuneDeclined("tune", "no pin resolves for this speaker (Automatic / Off)"); return; }
            if (string.Equals(want, fromName, StringComparison.OrdinalIgnoreCase)) return;   // no change

            // THE TARGET MUST BE ABLE TO COME TO THIS LANE. A station already voiced on ANOTHER
            // slot cannot: it has its own speakers there and moving it would cut them off. The
            // machine used to start anyway, hand over, and then wait for a station that was never
            // going to arrive -- six seconds of hiss on this slot while the speaker, whose pin HAD
            // moved, played that same station at full volume off the other one. Both of the
            // standing reports in one: "static playing over music already at full volume", and the
            // give-up that follows it reading as the audio cutting out.
            //
            // That case is exactly what the sweep borrow is for, so leave it alone and let the
            // borrow have it -- _handledTuneAt is deliberately NOT stamped here.
            Station toSt = ByName(want);
            if (toSt != null && toSt.VoiceId != 0 && toSt.Slot != fromSt.Slot)
            { TuneDeclined("tune", $"'{want}' is already playing on lane {toSt.Slot} — over to the borrow"); return; }

            _handledTuneAt = EmitterTuner.LastTuneAt;
            // The blanket hold kept the speaker out of EVERY station; from here it stays on the
            // one it is leaving instead, which is the entire point.
            EmitterTuner.ReleaseSweepHold();

            ClearOffAir(-1);
            _tuneFrom = fromName; _tuneTo = want;
            _tuneSlot = fromSt.Slot;
            _tunePhase = 1;
            // Leave, then a floor of static, then the swap. Anchored AFTER the leave for the same
            // reason the pocket radio anchors it there: measured from the press, the fade eats
            // most of the floor and leaves a fraction of the static anyone actually auditioned.
            _tuneSwapAt = now + LeaveRamp + MinStatic;
            // Sized to opening a file and its level hold, not to a hang. Six seconds was a
            // ceiling on a wait that could not be satisfied; now that it can, an over-long one is
            // just hiss the player has to sit through.
            _tuneGiveUp = _tuneSwapAt + CMMConfig.WorldEmitterLevelHoldMs / 1000f + 2f;
            // DOWN, not off. The old station plays on into this lane and sinks under the hiss.
            CmmMixer.GetOrCreate().SetEmitterRecv(_tuneSlot, 0f, LeaveRamp);
            _slotArmedAt[_tuneSlot] = now;
            _slotLockAt[_tuneSlot] = 0f;
            CMMConfig.D($"[CMM] Tune: leaving '{fromName}' on slot {_tuneSlot} over "
                      + $"{CMMConfig.RadioTuneLeaveMs:0} ms — it plays on, buried.");
        }

        // Begin, hold and end the sweep borrow. Runs before GroupSnapshot so a borrow started
        // this tick excludes its speaker from the same tick's point sets — a tick of delay here
        // would be most of the sweep.
        private static void MaintainSweepBorrow()
        {
            float now = CmmClock.Now;

            // Finish: hand the speaker back, then let the noise fall away rather than cutting it.
            if (_borrowSlot >= 0)
            {
                // THE SWAP, UNDER THE NOISE. Signal is at zero by now, so changing which station
                // this lane copies is inaudible -- the same handover the single-speaker path makes,
                // except nothing has to start or stop because both stations are already playing.
                if (_borrowSwapAt > 0f && now >= _borrowSwapAt)
                {
                    if (_borrowMirrorTo >= 0) CmmMixer.Instance?.SetLaneMirror(_borrowSlot, _borrowMirrorTo);
                    CMMConfig.D($"[CMM] Sweep borrow: slot {_borrowSlot} swaps its copy to lane "
                              + $"{_borrowMirrorTo} under the noise.");
                    _borrowSwapAt = 0f; _borrowMirrorTo = -1;
                }
                // Not until the station it is joining can actually be heard. Waiting here is the
                // whole point: the noise covers the decode instead of the speaker sitting silent
                // through it.
                if (_borrowReleaseAt <= 0f && now >= _borrowUntil && now < _borrowGiveUp
                    && !TargetAudible()) return;
                if (_borrowReleaseAt <= 0f && now >= _borrowUntil)
                {
                    // AN ARRIVAL, NOT A RELEASE. The station this speaker is joining is already
                    // playing, so what happens at the end of the sweep is a station being revealed
                    // from under the noise — the same event StartStation's lock ramp covers, and
                    // it earns the same unhurried curve. ReleaseRamp is for a slot going idle with
                    // nothing behind it, and using it here was the "fades out too quickly" report.
                    _borrowReleaseAt = now + ArriveRamp;
                    CmmMixer.Instance?.SetEmitterRecv(_borrowSlot, 1f, ArriveRamp);
                }
                if (_borrowReleaseAt > 0f && now >= _borrowReleaseAt)
                {
                    CmmMixer.Instance?.ClearEmitterRecv(_borrowSlot);
                    CmmMixer.Instance?.SetLaneMirror(_borrowSlot, -1);
                    CmmMixer.Instance?.SetSlotPoints(_borrowSlot, _emptyPts, _emptyGain, _emptyFlags, 0);
                    CMMConfig.D($"[CMM] Sweep borrow on slot {_borrowSlot} ended — speaker rejoins its station.");
                    _borrowSlot = -1; _borrowReleaseAt = 0f;
                }
                else
                {
                    // THE HOLD IS RELEASED EVEN WHEN WE CANNOT ACT. Returning here without it left
                    // a speaker retuned during a live borrow held out of EVERY station for the
                    // full 1.5 s dead-man window -- no station, no borrow, no static, silence at
                    // that radio. It rejoins its station instead and simply goes without a sweep,
                    // which is the same graceful failure as having no spare slot.
                    // RELEASE THE HOLD, BUT DO NOT CLAIM THE RETUNE. Stamping _handledTuneAt is
                    // terminal -- every other path tests it first -- so marking a press handled
                    // here while giving it no sweep at all made it permanently silent. This was
                    // the only one of the four stamp sites that did not arm anything, and its
                    // window is the whole length of a borrow: press again inside it and the
                    // station simply changed, with nothing in the log to say why.
                    //
                    // The hold still has to go, or the speaker sits out of every station for the
                    // full dead-man window. Released without being claimed, so the borrow can pick
                    // this retune up the moment its lane frees.
                    if (_handledTuneAt != EmitterTuner.LastTuneAt)
                    {
                        EmitterTuner.ReleaseSweepHold();
                        CMMConfig.D("[CMM] Sweep borrow busy — the new dial turn waits for the lane.");
                    }
                    return;   // one borrow at a time
                }
            }

            // Begin: EVERY retune, not only the ones onto an already-playing station.
            //
            // THE MISSING HALF. The receiver was armed on the slot of the station being tuned TO,
            // which means the station being tuned AWAY FROM got no treatment at all — it simply
            // stopped when the speaker left it. Reported as "there's no transition out of an
            // existing station", and it is also both of the other reports: turning the radio on
            // from Off bled the new station in cleanly before any noise, and changing station cut
            // to nothing before the static arrived. All three are the same hole.
            //
            // The personal radio does not have this problem because it has ONE receiver and the
            // signal ramps down and then up through it; leaving and arriving are the same object.
            // Here they are two different slots, so nothing owned the leaving.
            //
            // The borrow already is that object. It takes the speaker out of every station and
            // gives it a slot of its own that renders noise — which is exactly what a receiver
            // between stations is. Routing every retune through it makes one mechanism cover both
            // halves, and the special case for "the target is already playing" disappears: it was
            // never about the target, it was about the speaker having somewhere to be.
            if (_handledTuneAt == EmitterTuner.LastTuneAt) return;
            if (now - EmitterTuner.LastTuneAt > RetuneWindow) return;
            // THE HOLD ENDS WHEN SOMETHING IS ARMED, NOT WHEN WE DECIDE TO TRY. It used to end
            // here, before the slot was acquired -- so on the path where no lane was spare the
            // speaker was released into a station that is ALREADY PLAYING, at full level, with no
            // receiver on it. Clean audio hops in; the arm that finally lands a tick later then
            // takes SearchRamp (120 ms) to bury it. "Audio will play before the static, hop in,
            // fade out in 50-100 ms, then static, then it comes back" -- that whole sequence, and
            // the 120 ms is SearchRamp exactly.
            //
            // Nor is _handledTuneAt stamped up here any more: a retune that could not be served
            // this tick has not been handled, and must be tried again when a lane frees, rather
            // than counted as done and dropped.
            // A RETUNED SPEAKER IS A NEW SPEAKER, as far as arriving goes. The birth ramp already
            // fades in "a speaker that APPEARS while a station voice is already live... instead of
            // popping" -- but a speaker being retuned never appears. It has been in the registry
            // the whole time; only its pin changed. Turning a SILENCED radio back on is the plain
            // case: it was pinned to a name that resolves to nothing, so it contributed to no
            // station and made no sound, and the instant the pin became real it joined a station
            // already at full level and arrived there with no ramp at all. That is the clear audio
            // bleeding through before any static.
            //
            // Re-stamping it (below, once a lane is secured) makes the existing ramp apply
            // wherever it lands.
            Vector3i pos = EmitterTuner.LastTunePos;
            var snap = EmitterRegistry.Snapshot;
            EmitterRegistry.Emitter e = default(EmitterRegistry.Emitter);
            bool found = false;
            for (int i = 0; i < snap.Count; i++)
                if (snap[i].Pos.Equals(pos)) { e = snap[i]; found = true; break; }
            if (!found) return;

            string want;
            if (!EmitterTuner.TryPin(e, out want) || string.IsNullOrEmpty(want))
            { TuneDeclined("borrow", "no pin resolves for this speaker (Automatic / Off)"); return; }
            Station target = null;
            for (int i = 0; i < _stations.Count; i++)
                if (string.Equals(_stations[i].Name, want, StringComparison.OrdinalIgnoreCase))
                { target = _stations[i]; break; }
            // ONLY WHEN THE LANE IS GENUINELY SHARED. A borrow renders noise over an EMPTY lane,
            // so it can never be the pocket radio's "station rising out of the noise" -- there is
            // nothing under it to rise. When the target is NOT already playing, the ordinary start
            // path is strictly better: the station opens on a slot, that slot's own receiver
            // searches, and CommitPendingStart raises it over the track's fade. Routing those
            // through the borrow is why turning a radio on gave static, then silence, then music
            // arriving on its own somewhere else.
            //
            // A target already voiced elsewhere is the one case with no better answer: its slot
            // carries other speakers, who must not be hissed.
            if (target == null || target.VoiceId == 0)
            { TuneDeclined("borrow", "the target is not playing anywhere — the start path sweeps it"); return; }

            int slot = FreeTuneSlot();
            if (slot < 0)
            {
                // Not handled, not released: the speaker stays out of every station and we try
                // again next tick. Held silent for a moment beats arriving clean and then being
                // buried in front of the player.
                TuneDeclined("borrow", "every lane is busy — holding the speaker for one more tick");
                return;
            }
            _handledTuneAt = EmitterTuner.LastTuneAt;
            EmitterTuner.ReleaseSweepHold();
            _speakerFirstSeen[EmitterTuner.LastTunePos] = CmmClock.Now;

            _borrowSlot = slot;
            _borrowPos = pos;
            _borrowTarget = target.Name;
            _borrowOwnsTune = EmitterTuner.LastTuneAt;
            // At LEAST the sweep, and then for as long as the new station has nothing to hand the
            // speaker back to. Handing it back to a station with no voice is the cut this exists to
            // remove — the speaker would rejoin something silent and wait out the decode in the
            // clear. Capped, because a station that never opens must not hold a slot for ever.
            _borrowUntil = now + LeaveRamp + MinStatic;
            _borrowGiveUp = now + BorrowGiveUp;
            _borrowReleaseAt = 0f;
            // One point, clear line. The occlusion the station computes for this speaker belongs to
            // the station's own publish, which this speaker is out of for the moment; a quarter of a
            // second of unoccluded hiss from a radio you are standing at is not worth a second
            // acoustics pass.
            _borrowPts[0] = pos.x + 0.5f; _borrowPts[1] = pos.y + 0.5f; _borrowPts[2] = pos.z + 0.5f;
            // OCCLUDED LIKE EVERY OTHER SOUND THIS SPEAKER MAKES. This published 0, and 0 is not a
            // gain -- it is added virtual distance, so it meant CLEAR LINE: no walls, no doors.
            // Every other sound from the speaker was occluded properly and the tuning hiss alone
            // ignored geometry, so from behind a wall the music was muffled and quiet while the
            // static arrived at full strength, as though it came from somewhere else entirely.
            //
            // The value belongs to the speaker, not the station, so the one the station it is
            // leaving computed for this very point is correct and current.
            _borrowGain[0] = PointGain(ByName(_posStation.TryGetValue(pos, out string pg) ? pg : null), pos);
            _borrowFlags[0] = e.Name == "radioHam" ? 1 : 0;
            // AND THE MUSIC UNDERNEATH IT. The borrow used to render noise over an EMPTY lane,
            // which is why the shared case could never sound like the pocket radio: there was no
            // station to duck away and none to rise out of the hiss. The target is already decoded
            // on its own lane, so this one takes a copy and runs its own receiver over that -- one
            // decode, sample-aligned, and every other speaker on the source lane is untouched.
            // THE LANE FOLLOWS THE SPEAKER, AND IT STARTS WHERE THE SPEAKER IS. Mirroring the
            // TARGET immediately was "it comes in way too quickly": the station being tuned to is
            // already at full level somewhere else, so the copy arrived at full the instant the
            // borrow began and the only thing left to happen was the hiss lifting off it. No
            // station going away, no station coming in -- just a curtain pulled off a finished
            // picture.
            //
            // So the borrow opens on the station the speaker is LEAVING, at the signal it is
            // already locked at, and takes the same three moves as everywhere else: ramp the
            // receiver down over that station, swap the mirror to the target while the signal is
            // at zero and nothing can be heard, then ramp up. The mirror makes it free -- both
            // stations are already decoded, and this lane only ever holds a copy.
            Station leave = ByName(_posStation.TryGetValue(pos, out string ls) ? ls : null);
            if (leave != null && leave.Slot >= 0 && leave.VoiceId != 0)
            {
                CmmMixer.GetOrCreate().SetLaneMirror(slot, leave.Slot);
                CmmMixer.GetOrCreate().SetSlotPoints(slot, _borrowPts, _borrowGain, _borrowFlags, 1);
                CmmMixer.GetOrCreate().SetEmitterRecv(slot, 1f, 0f);          // locked on what it has
                CmmMixer.GetOrCreate().SetEmitterRecv(slot, 0f, LeaveRamp);   // ...and now leaving it
                _borrowMirrorTo = target.Slot;
                _borrowSwapAt = now + LeaveRamp;
            }
            else
            {
                // NOTHING TO LEAVE MEANS NOTHING UNDER THE NOISE -- YET. Mirroring the TARGET here
                // put its music on this lane from the very first sample, and the receiver only
                // buries a programme by RadioRecvFloorDb (5 dB), so the station arrived at once,
                // 5 dB down, with the hiss on top of it. That is "turning the radio on, the music
                // comes in before the static, or at the same time as it" exactly -- and it is also
                // why a search over a mirrored lane reads as "static, but barely".
                //
                // The mirror is deferred to the swap instead, the same moment the leave case uses.
                // Until then this lane has no source at all and the receiver has only its own
                // noise to render, which is what a set between stations actually sounds like.
                CmmMixer.GetOrCreate().SetLaneMirror(slot, -1);
                CmmMixer.GetOrCreate().SetSlotPoints(slot, _borrowPts, _borrowGain, _borrowFlags, 1);
                CmmMixer.GetOrCreate().SetEmitterRecv(slot, 1f, 0f);          // nothing on it yet
                CmmMixer.GetOrCreate().SetEmitterRecv(slot, 0f, SearchRamp);  // ...and now searching
                _borrowMirrorTo = target.Slot;
                _borrowSwapAt = now + LeaveRamp;
            }
            _slotArmedAt[slot] = now;
            _slotLockAt[slot] = 0f;   // a new sweep cancels whatever this slot was still owed
            CMMConfig.D($"[CMM] Sweep borrow: slot {slot} sweeps {e.Name} at {pos} onto '{want}' "
                      + (target.VoiceId != 0 ? "(already playing)." : "(opening under the noise)."));
        }

        // A STATION WITH NO SPEAKERS IS NOT USING ITS SLOT. UnseenGrace keeps a station warm for
        // twelve seconds after its last speaker goes, so walking out of a room and back does not
        // pay for a re-decode. Ruinous at a radio you are TURNING, though: every station you pass
        // through sits on a slot for twelve seconds and there are only three. Measured, six
        // retunes in one log gave up with "no spare slot", and the stations holding those slots
        // had no speakers at all.
        //
        // So the borrow may take one back. Nobody is listening to a station with no speakers, and
        // the cost is a re-decode if it is rejoined later — against a retune that would otherwise
        // land in silence. Strictly "no speakers ANYWHERE", never "none in range": a station just
        // outside the ring is one step from being heard again and keeps its warm slot.
        private static int TakeInaudibleSlot()
        {
            int best = -1;
            for (int s = 0; s < MaxSlots; s++)
            {
                Station own = _slotOwner[s];
                if (own == null) continue;
                if (own.NearestDist != float.MaxValue) continue;
                if (best < 0 || _slotOwner[best].LastSeenAt > own.LastSeenAt) best = s;   // coldest first
            }
            if (best < 0) return -1;
            CMMConfig.D($"[CMM] Sweep borrow takes slot {best} from '{_slotOwner[best].Name}' "
                      + "(no speakers — warm only).");
            StopStation(_slotOwner[best], "slot taken for a retune sweep");
            return FreeSlot();
        }

        // Give the slot back at once: the speaker rejoins its station on the next snapshot and
        // the station is already playing, so what the ear gets is the noise stopping rather than
        // anything starting. Abrupt only when something else needed the slot, which is the trade.
        private static void EndSweepBorrowNow()
        {
            if (_borrowSlot < 0) return;
            CmmMixer.Instance?.ClearEmitterRecv(_borrowSlot);
            CmmMixer.Instance?.SetLaneMirror(_borrowSlot, -1);
            CmmMixer.Instance?.SetSlotPoints(_borrowSlot, _emptyPts, _emptyGain, _emptyFlags, 0);
            _borrowSlot = -1; _borrowReleaseAt = 0f;
            // Handing the slot to a station mid-sweep ends the borrow's claim on this retune, so
            // the station's own arm picks the search back up on the slot it just took. The noise
            // continues rather than stopping dead halfway through a dial turn.
            _borrowOwnsTune = -999f;
        }

        // A clear-line virtual-distance array for a slot that is searching: the noise is not the
        // station and does not inherit the station's occlusion history.
        private static readonly float[] _searchGain = new float[RadioStation.MaxPoints];

        // The occlusion a station last computed for one speaker of its own, as added virtual
        // distance. 0 (clear line) when the station has no point there -- the honest default for a
        // speaker nothing has flooded yet.
        private static float PointGain(Station st, Vector3i pos)
        {
            if (st == null || st.PtCount <= 0) return 0f;
            float x = pos.x + 0.5f, y = pos.y + 0.5f, z = pos.z + 0.5f;
            for (int i = 0; i < st.PtCount && i < st.PtGain.Length; i++)
            {
                int o = i * 3;
                if (st.Pts[o] == x && st.Pts[o + 1] == y && st.Pts[o + 2] == z) return st.PtGain[i];
            }
            return 0f;
        }

        private static readonly float[] _borrowPts = new float[3];
        private static readonly float[] _borrowGain = new float[1];
        private static readonly int[] _borrowFlags = new int[1];

        // A SLOT NOTHING IS TUNED TO MUST NOT KEEP HISSING, and this is a sweep rather than a
        // release on each exit path ON PURPOSE. Reported as "sometimes the static won't stop".
        //
        // Arming happens in one place; giving up on a start happens in five. The open can fail,
        // the id can come back zero, the station can be superseded while the decoder was working
        // (which is what rapid dialling does — every press supersedes the one before it), the
        // station can go out of range, or it can be stopped outright while still searching. That
        // last one could not be released by StopStation even in principle: it clears the receiver
        // under `st.Slot >= 0`, and a station that is still searching has no slot yet — Slot is
        // only assigned when the voice lands. So the one case most likely to happen was the one
        // case structurally guaranteed to leak.
        //
        // Asking five exit paths to each remember is how the leak got here. A slot with no owner
        // and no start in flight is not tuned to anything, whatever route it took to get there,
        // and one sweep of three slots per decision tick closes all of them including the ones I
        // have not thought of.
        private static void ReleaseIdleReceivers()
        {
            for (int s = 0; s < MaxSlots; s++)
            {
                // NOT ON THE SLOT SOMEBODY IS SWEEPING ON. This guard used to sit two lines lower,
                // below the deferred lock, and that ordering is a silent kill: SetEmitterRecv(s, 1f)
                // on a slot with NO VOICE ramps its receiver to LOCKED over a silent lane, which is
                // the hiss disappearing mid-sweep. Nothing repaired it and nothing logged it — the
                // borrow's own arrival ramp is then a no-op and the release just clears.
                //
                // It needs a stale deadline to fire, and the churn provides one: a lock is
                // scheduled on a slot whose station is then stolen within the sweep window, the
                // slot is released, the next retune borrows it, and the orphan fires into the
                // borrow. StopStation never cleared it and neither did the borrow's arm — only
                // StartStation's did. Both now do, below; this guard is the belt to that braces.
                if (s == _borrowSlot) continue;                         // a speaker is sweeping on it
                if (s == _deadAirSlot) continue;                        // a speaker is off air on it

                // A lock held back so the dial had time to travel (see the start-completion path).
                if (_slotLockAt[s] > 0f && CmmClock.Now >= _slotLockAt[s])
                {
                    CmmMixer.Instance?.SetEmitterRecv(s, 1f, _slotLockRamp[s]);
                    _slotLockAt[s] = 0f;
                }

                if (_slotOwner[s] != null) continue;                    // a station holds it
                if (_emStartPending && _emPendingSlot == s) continue;   // a start is on its way
                // ARMED COUNTS, EVEN WITH NO STATION BEHIND IT. This used to be
                // `_slotLastStation[s] == null -> continue`, and a borrow lane has never held a
                // station, so its receiver could not be released HERE by construction. If the
                // borrow's own teardown was missed for any reason the hiss had nothing left that
                // could ever turn it off -- static that plays until you change station or toggle
                // the radio, which is exactly how it was reported.
                if (_slotLastStation[s] == null && _slotArmedAt[s] <= 0f) continue;
                // AND NOT WHILE IT IS LEGITIMATELY SEARCHING. The level hold returns before the
                // decoder job is dispatched, so through the whole of it there is no owner AND no
                // pending start -- the two conditions above -- and a sweep without this one would
                // reap the slot every tick and kill the exact static the hold exists to cover.
                // A deadline instead: the hold is bounded by LoudnessStartHoldMs, so anything
                // still ownerless well past that is stuck rather than working.
                if (CmmClock.Now - _slotArmedAt[s] < SearchGiveUp) continue;

                // RELEASED IN TWO STEPS, because cutting a receiver mid-roar is a click and was
                // reported as the static not fading gracefully. Signal 1 IS the quiet end of this
                // stage — a locked receiver over a silent lane is near silence — so ramping there
                // fades the noise out through the same curve it faded in through, and only then
                // is the stage taken out of the path.
                if (_slotReleasing[s] <= 0f)
                {
                    _slotReleasing[s] = CmmClock.Now;
                    CmmMixer.Instance?.SetEmitterRecv(s, 1f, ReleaseRamp);
                    continue;
                }
                if (CmmClock.Now - _slotReleasing[s] < ReleaseRamp) continue;

                _slotReleasing[s] = 0f;
                // AND THE ARM STAMP, or this never ends. The guard above is
                // `_slotLastStation[s] == null && _slotArmedAt[s] <= 0f -> continue`, so leaving
                // _slotArmedAt set meant a released lane failed the test again on the very next
                // tick and went round the whole release again: arm, wait 0.45 s, clear, repeat,
                // for ever. Visible in the log as "Emitter slot 3 released" every 0.45 s
                // indefinitely, and it kept re-arming a receiver on a lane nothing was using.
                _slotArmedAt[s] = 0f;
                CmmMixer.Instance?.ClearEmitterRecv(s);
                CmmMixer.Instance?.SetSlotPoints(s, _emptyPts, _emptyGain, _emptyFlags, 0);
                _slotLastStation[s] = null;
                CMMConfig.D($"[CMM] Emitter slot {s} released — no station, receiver off.");
            }
        }

        // Empty push buffers for a released slot: SetSlotPoints with count 0 ignores the arrays,
        // but passing null across the interop boundary is not worth the argument.
        private static readonly float[] _emptyPts   = new float[3];
        private static readonly float[] _emptyGain  = new float[1];
        private static readonly int[]   _emptyFlags = new int[1];

        // ── Loudness integration ────────────────────────────────────────────────────────────────

        // A measurement for `file` just landed — if a live station voice is airing that exact track,
        // ease its loudness gain in. Called from the shared loudness pipeline. Main-thread.
        // rampSeconds is chosen by the caller from the SIZE of the correction (see
        // ContextMusicManager.SettleRamp) — a first measurement can move the level by the full
        // boost, a progressive refinement by a fraction of a dB — rather than one fixed ease that
        // is too short for the first and too abrupt for the rest.
        internal static void SettleLoudness(string file, float rampSeconds = 0.2f)
        {
            if (string.IsNullOrEmpty(file)) return;
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.VoiceId == 0) continue;
                if (!string.Equals(st.VoiceTrack, file, StringComparison.OrdinalIgnoreCase)) continue;
                float g = LoudnessStore.GetTrackGain(file);
                if (Mathf.Abs(g - st.VoiceGainApplied) < 1e-4f) continue;
                st.VoiceGainApplied = g;
                CmmMixer.Instance?.SetVoiceTrackGain(st.VoiceId, g, rampSeconds);
            }
        }

        // The stations' loudness working set — every airing track plus the deterministic up-next of
        // each station with tracked speakers — appended to the loudness eviction keep-set so
        // RetainOnly doesn't drop measurements a station is about to use.
        internal static void AppendLoudnessKeep(List<string> dst)
        {
            int ahead = CMMConfig.LoudnessMeasureAhead;
            if (ahead < 0) ahead = 0;
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                if (st.VoiceTrack != null) dst.Add(st.VoiceTrack);
                // MUST TRACK THE PREWARM SOURCE BELOW. Anything measured but absent from the keep
                // set is evicted straight after being measured, so the two gates move together --
                // widening one alone would measure every station's next tracks and then throw the
                // results away.
                if (!st.Ready) continue;
                if (st.OnAir != null) dst.Add(st.OnAir);
                for (int k = 1; k <= ahead && k < st.Order.Length; k++)
                    dst.Add(st.Order[(st.OnAirIdx + k) % st.Order.Length]);
            }
        }

        // Keep every idle nearby station's on-air pointer current (so a start is instant and never
        // stale) and request background loudness measurement for the airing track + the next few
        // DETERMINISTIC schedule entries — an advance always plays an already-normalized track.
        // Cheap + dedup'd (RequestPrewarm no-ops on measured / in-flight files). Each decision tick.
        private static void MaintainOnAir()
        {
            double B = BroadcastSeconds();
            int ahead = CMMConfig.LoudnessNormalizationEnabled ? CMMConfig.LoudnessMeasureAhead : 0;
            if (ahead < 0) ahead = 0;
            for (int i = 0; i < _stations.Count; i++)
            {
                Station st = _stations[i];
                // EVERY READY STATION, not just the ones already being heard. The NearestDist test
                // made the premeasured set exactly "the stations you are already listening to" --
                // and the one you are about to dial to is, by definition, never in it. So every
                // first tune to a cold station paid a full measurement while the player sat in
                // static, which reads as the retune being slow. The schedule is deterministic, so
                // what a station plays next is knowable with no speaker anywhere near it. Cheap and
                // dedup'd: RequestPrewarm no-ops on measured and in-flight files.
                if (!st.Ready) continue;
                // Idle: track the broadcast virtually — UNLESS this station has just decided what
                // to play next and is still trying to start it. See AdvanceStation above.
                if (st.VoiceId == 0 && !OnAirPinned(st)) ResolveOnAir(st, B);
                if (ahead == 0 || st.OnAir == null) continue;
                // NEED FIRST, SPECULATION SECOND. RequestPrewarm starts a coroutine per file with
                // no queue, so widening this loop to every station (so the one you are about to
                // dial to is already measured) also let ~30 decode-bound measurements start at
                // once -- and the track actually playing was one of thirty rather than the only
                // one. It came in unnormalized and crawled up to level, or never got there.
                //
                // A station with a speaker on it is a need: something is playing it now, or is one
                // step from it. Everything else is speculation and waits for a quiet moment.
                bool needed = st.NearestDist != float.MaxValue;
                if (!needed && ContextMusicManager.LoudnessInFlight >= SpeculativeMeasureCap) continue;
                ContextMusicManager.RequestPrewarm(st.OnAir);
                for (int k = 1; k <= ahead && k < st.Order.Length; k++)
                {
                    if (!needed && ContextMusicManager.LoudnessInFlight >= SpeculativeMeasureCap) break;
                    ContextMusicManager.RequestPrewarm(st.Order[(st.OnAirIdx + k) % st.Order.Length]);
                }
            }
        }

        // Push the world-radio's OWN master: the game's OVERALL × AMBIENT sliders × the emitters'
        // own WorldEmitterVolume trim. Deliberately NOT × VolumeModifier and NOT × the current
        // music context's volume.
        //
        // AMBIENT, not the music slider (which is what this used to read): a radio playing out of a
        // block in the world is a diegetic environmental sound — the same category the game puts
        // every other world sound in — so muting MUSIC must leave it playing. OVERALL is applied
        // by hand for the same reason CMMConfig.GetVolume does it: the game applies that slider as
        // AudioListener.volume, which only reaches Unity AudioSources, and our audio leaves through
        // its own device.
        //
        // The trim is WorldEmitterVolume (radio.xml), NOT the Radio context's volume in
        // contexts.xml — that one now trims ONLY the personal radio (PersonalRadio
        // .MasterVolumeOverride). The two share a station catalog but not a level: a speaker across
        // the room and a radio in your head want different trims.
        // THE RECEIVER'S TUNING, PUSHED BY US TOO.
        //
        // Every one of these parameters is a file-static in cmm_mix.c shared by both receivers --
        // deliberately, so one sound is tuned once. But only PersonalRadio.AimSignal ever pushed
        // them, and nothing in this layer did. So world speakers ran on the C file's COMPILED
        // defaults until the player happened to dial the pocket radio once, and then silently
        // flipped to radio.xml's values for the rest of the session.
        //
        // Measured, same chain, same distance: the hiss is 12.0 dB quieter after the pocket radio
        // has been touched (-25.06 dBFS against -37.02), and the knee moves 0.45 -> 0.90, which
        // shifts where the station comes out from under the noise from signal 0.45 to 0.88. The
        // world radios audibly changed character mid-session according to something the player did
        // somewhere else entirely -- and "sometimes the static is so quiet I cannot hear it" is
        // exactly what a 12 dB drop nobody asked for sounds like.
        //
        // Pushing the same config values from here makes it deterministic. Both writers now agree
        // because both read radio.xml; whoever writes last writes the same numbers.
        private static void PushReceiverTuning()
        {
            var m = CmmMixer.Instance;
            if (m == null) return;
            m.SetReceiverShape(CMMConfig.RadioRecvFloorDb, CMMConfig.RadioRecvMuteWidth,
                               CMMConfig.RadioRecvDrive, CMMConfig.RadioRecvWander,
                               CMMConfig.RadioRecvFlutter, CMMConfig.RadioRecvFlutterHz);
            m.SetReceiverTone(CMMConfig.RadioRecvKnee, CMMConfig.RadioRecvKneeWidth,
                              CMMConfig.RadioRecvNoise, CMMConfig.RadioRecvClicks,
                              CMMConfig.RadioRecvClickRate, CMMConfig.RadioRecvHiFloorHz,
                              CMMConfig.RadioRecvDeemphHz);
            m.SetReceiverArrival(CMMConfig.RadioRecvNoiseHold, CMMConfig.RadioRecvNoiseFloor,
                                 CMMConfig.RadioRecvWindowA, CMMConfig.RadioRecvWindowB,
                                 CMMConfig.RadioRecvLockAt, CMMConfig.RadioRecvLockMs);
            m.SetReceiverRadio(CMMConfig.RadioRecvMangle, CMMConfig.RadioRecvMakeupDb,
                               CMMConfig.RadioRecvLowCutHz, CMMConfig.RadioRecvHighCutHz);
        }

        private static void PushEmitterMaster()
        {
            float trim = Mathf.Clamp01(CMMConfig.WorldEmitterVolume);
            CmmMixer.Instance?.SetEmitterMaster(CMMConfig.OverallVolume * CMMConfig.AmbientVolume * trim);
        }
    }
}
#endif
