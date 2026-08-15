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

        // Decision + point-set refresh cadence (4 Hz). The listener itself is pushed every frame; this
        // only governs how fast start/stop and the in-range speaker SET react as you move.
        private const float TickInterval = 0.25f;
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
        private static float _nextReverbLog;           // throttle for the reverb diagnostic (DebugLogging only)

        // ── Path / stability summary (DebugLogging only) ────────────────────────────────────────
        // One line per second answering: which occlusion path is actually rendering (the cached
        // per-speaker fields, which are continuous by construction, or the legacy ear-anchored
        // flood, whose boundary-blend branch is the known jitter source), WHY the field path was
        // refused if it was, and how much the measurement is actually moving tick to tick — raw
        // (as measured) versus published (after the rise limiter, which can mask it).
        private static int   _statFieldTicks, _statLegacyTicks;
        private static string _statWhy = "-";          // why the field path was refused, latest
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
            _doorEventAt = Time.realtimeSinceStartup;
        }

        // ── Shared catalog forwarders ───────────────────────────────────────────────────────────
        // The catalog/schedule/broadcast model moved to the version-neutral RadioStationCatalog
        // (shared with PersonalRadio). Thin forwarders keep this file's call sites unchanged.
        private static double BroadcastSeconds() => RadioStationCatalog.BroadcastSeconds();
        private static void EnsureCatalog() => RadioStationCatalog.EnsureCatalog();
        private static void ResolveOnAir(Station st, double B) => RadioStationCatalog.ResolveOnAir(st, B);
        private static void AdvanceStation(Station st, string endedTrack) => RadioStationCatalog.AdvanceStation(st, endedTrack);
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
                    // Advance the native scene's world READ one slab (~1.5 ms). The read has to be
                    // here, on the main thread, because the game's chunks are not safe to touch from
                    // another one; only the solve that follows it runs on the scheduler.
                    AcousticBridge.FrameStep(w);
                }
            }

            // A door toggled since the last tick → collapse the wait; an in-flight flood was built
            // from the PRE-toggle door state — abandon it so the restart samples the swing fresh.
            if (_forceRefresh) { _forceRefresh = false; _nextTick = 0f; _occlPending = false; _floodRoomOnly = false; }
            if (Time.realtimeSinceStartup < _nextTick) return;
            _nextTick = Time.realtimeSinceStartup + (EmitterAcoustics.AnyDoorAnimating ? DoorTickInterval : TickInterval);

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
            if (CMMConfig.DebugLogging && Time.realtimeSinceStartup >= _nextPathLog)
            {
                _nextPathLog = Time.realtimeSinceStartup + 1f;
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
                            // The clamp counters, in the 1 Hz line, because they are the difference
                            // between "the model is quiet here" and "the model is switched off here"
                            // and I had to ask for them by hand three times before getting one.
                            (CMMConfig.WorldEmitterNativeAcoustics
                                ? $" | ac gain={AcousticBridge.GainPoints} sat={AcousticBridge.SaturatedPoints}"
                                  + $" pts={AcousticBridge.PublishedPoints} scene={AcousticBridge.LastStatus}"
                                  + $"\n[CMM]   ac nearest: {AcousticBridge.DescribeNearest()}"
                                : ""));
                _statFieldTicks = _statLegacyTicks = 0;
                _statPubSum = _statPubMax = 0f; _statPubN = 0;
                EmitterAcoustics.StatReset();
                EmitterFieldCache.StatDrain();
            }

            // Push the live spatial-curve tuning + radio master from config (hot-reloaded).
            PushTuning();
            PushEmitterMaster();

            EnsureCatalog();        // stations from the Radio source's subfolders (throttled disk stat)
            if (_stations.Count == 0) { StopAll(); return; }

            EmitterRegistry.Poll();
            MaintainBirths();       // stamp newly-appeared speakers / prune gone ones (placement fade-in)
            GroupSnapshot();        // per-station nearest + culled point sets from the registry snapshot
            MaintainOnAir();        // keep idle nearby stations' on-air pointers current + prewarm loudness
            if (_clockResyncPending) { _clockResyncPending = false; ResyncVoicesToClock(player); }
            MaintainPlayingStations(player);   // airing-file-vanished interrupt + EOF advance
            SelectAndVoice(player); // K-nearest start/steal/stop

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
                CmmMixer.Instance?.SetEmitterPoints(st.VoiceId, st.Pts, st.PtGain, st.PtFlags, 0);
                RecordPtPush(st, 0);
            }
            if (!anyAudible) { _occlPending = false; return; }

            World world = GameManager.Instance?.World;
            // ── PHASE 3 (native acoustics): the whole occlusion model, in C, off a scene bound to
            // GEOMETRY rather than to the ear. Sits AHEAD of both existing paths because it replaces
            // them outright — it is not a source of gains for the flood to blend, it is the flood's
            // successor. Anything it declines (warming up, door mid-swing, native DLL missing) falls
            // through to the shipped paths below unchanged, so the A/B is a live config flip.
            if (CMMConfig.WorldEmitterOcclusion && CMMConfig.WorldEmitterNativeAcoustics && world != null
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
                if (!CMMConfig.WorldEmitterFieldCache) { _statWhy = "disabled"; fieldOk = false; }
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
                        if (st.VoiceId == 0 || st.NearestDist > audibleR) continue;
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
                            CmmMixer.Instance?.SetEmitterPoints(st.VoiceId, st.Pts, st.PtGain, st.PtFlags, 0);
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
                    if (st.VoiceId == 0 || st.NearestDist > audibleR) continue;
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
                    CmmMixer.Instance?.SetEmitterPoints(st.VoiceId, st.Pts, st.PtGain, st.PtFlags, st.PtCount);
                    RecordPtPush(st, st.PtCount);
                }
                PushReverb(player);
            }
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
        private static bool TryNativeOcclusion(EntityPlayerLocal player, World world, float audibleR)
        {
            if (EmitterAcoustics.AnyDoorAnimating) { _statWhy = "doorswing"; return false; }

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
                    CmmMixer.Instance?.SetEmitterPoints(st.VoiceId, st.Pts, st.PtGain, st.PtFlags, 0);
                    RecordPtPush(st, 0);
                    continue;
                }
                if (!AcousticBridge.ComputeOcclusion(ear, st.Pts, st.PtCount, st.PtGain, world))
                { _statWhy = "ac-declined"; return false; }   // scene swapped under us — legacy covers this tick
                // Seal/Solid/Depth deliberately carried over: the reverb bus stays on the shipped
                // measure so an A/B changes ONE thing — the occlusion — and not the room sound too.
                PostProcessGains(st);
                CmmMixer.Instance?.SetEmitterPoints(st.VoiceId, st.Pts, st.PtGain, st.PtFlags, st.PtCount);
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
                    CmmMixer.Instance?.SetEmitterPoints(st.VoiceId, st.Pts, st.PtGain, st.PtFlags, 0);
                    RecordPtPush(st, 0);
                    continue;
                }
                if (!EmitterFieldCache.SampleStation(world, ear, st.Pts, st.PtCount, st.PtGain,
                                                     out float seal, out float solid, out float depth))
                { _statWhy = "flipped"; return false; }   // ready flipped under us — legacy covers this tick
                st.Seal = seal; st.Solid = solid; st.Depth = depth;
                PostProcessGains(st);
                CmmMixer.Instance?.SetEmitterPoints(st.VoiceId, st.Pts, st.PtGain, st.PtFlags, st.PtCount);
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
                CmmMixer.Instance?.SetEmitterPoints(st.VoiceId, st.Pts, st.PtGain, st.PtFlags, st.PtCount);
                RecordPtPush(st, st.PtCount);
            }
            PushReverb(player);
            _occlPending = false;
        }

        // Shape a station's freshly-computed per-speaker gains before publishing: the placement
        // fade-in first (it inflates a new speaker's extra), then the movement rise limiter.
        private static void PostProcessGains(Station st)
        {
            ApplyBirthFades(st.Pts, st.PtGain, st.PtCount);
            LimitOcclusionRise(st.Pts, st.PtGain, st.PtCount);
        }

        // Rate-limit occlusion RISES (see _publishedExtra). Falls pass through untouched; door
        // events bypass entirely; a speaker not published for >2 s republishes as-is.
        private static void LimitOcclusionRise(float[] pts, float[] gains, int count)
        {
            if (count <= 0) return;
            float rate = CMMConfig.WorldEmitterOcclusionFadeRate;
            float now = Time.realtimeSinceStartup;
            bool doorWindow = EmitterAcoustics.AnyDoorAnimating || now - _doorEventAt < 1f;
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
                if (rate > 0f && !doorWindow
                    && _publishedExtra.TryGetValue(key, out PublishedExtra prev)
                    && now - prev.At < 2f && gains[i] > prev.Value)
                {
                    float cap = prev.Value + rate * Mathf.Clamp(now - prev.At, 0.02f, 1f);
                    if (gains[i] > cap) gains[i] = cap;
                }
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
            st.LastPtPushAt = Time.realtimeSinceStartup;
        }

        // Stamp speakers newly appeared in the registry snapshot and prune ones that left, so
        // ApplyBirthFades can grade a just-placed radio in instead of popping it.
        private static void MaintainBirths()
        {
            float now = Time.realtimeSinceStartup;
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
            if (count <= 0 || _speakerFirstSeen.Count == 0) return;
            float dur = CMMConfig.WorldEmitterBirthFade >= 0f ? CMMConfig.WorldEmitterBirthFade : StartFade();
            if (dur <= 0f) return;
            float now = Time.realtimeSinceStartup;
            float range = CMMConfig.WorldEmitterRange;
            for (int i = 0; i < count; i++)
            {
                Vector3i key = SlotKey(pts, i);
                if (!_speakerFirstSeen.TryGetValue(key, out float t0)) continue;
                float age = now - t0;
                if (age >= dur) continue;
                gains[i] += (1f - age / dur) * range;
            }
        }

        // Mark every currently-tracked speaker's birth as already elapsed. Used when the FIRST
        // station voice starts fresh — its own fade-in covers the entrance, and stacking the birth
        // ramp on top would double-fade the radio that triggered the start.
        private static void ExpireBirths()
        {
            if (_speakerFirstSeen.Count == 0) return;
            _birthScratch.Clear();
            foreach (var kv in _speakerFirstSeen) _birthScratch.Add(kv.Key);
            float expired = Time.realtimeSinceStartup - 3600f;
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
            float now = Time.realtimeSinceStartup;
            var snap = EmitterRegistry.Snapshot;
            for (int i = 0; i < snap.Count; i++)
            {
                EmitterRegistry.Emitter e = snap[i];
                if (!_posPins.TryGetValue(e.Pos, out string pinned))
                {
                    pinned = _stations[e.StationHash % n].Name;
                    _posPins[e.Pos] = pinned;
                }
                // Pinned to a REMOVED station ⇒ dead air: the radio contributes to no live station
                // (silent) until the folder returns or the next world entry recomputes the pins.
                if (!RadioStationCatalog.ByName.TryGetValue(pinned, out Station st)) continue;
                st.LastSeenAt = now;
                // Render class rides per-point flags (bit0 = ham): a radioHam gets the HAM coloration
                // (always slightly distorted, mono communications speaker) in the native render while
                // shelf speakers get the (clean-by-default) speaker set. Name is the registry's interned
                // display-name string, so this compare is a reference check in practice.
                int fl = e.Name == "radioHam" ? 1 : 0;
                if (e.Dist < st.NearestDist) { st.NearestDist = e.Dist; st.NearestPos = e.Pos; st.NearestFlags = fl; }
                if (e.Dist > range) continue;
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

        private static void MaintainPlayingStations(EntityPlayerLocal player)
        {
            // Sync audit: periodic voice-vs-paper delta per voiced station, same cadence and
            // format as the personal radio's (RadioStationCatalog.AuditVoice) so one grep
            // ("CMM SYNC") lines both consumers up against the same paper schedule.
            bool audit = CMMConfig.DebugLogging && Time.realtimeSinceStartup >= _nextAuditAt;
            if (audit) _nextAuditAt = Time.realtimeSinceStartup + RadioStationCatalog.AuditInterval;

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
                    if (Time.realtimeSinceStartup - st.LastSeenAt > UnseenGrace)
                        StopStation(st);
                    else if (CmmMixer.Instance != null && !CmmMixer.Instance.TryGetState(st.VoiceId, out _, out _, out _))
                    {
                        st.VoiceId = 0; st.VoiceTrack = null;
                        StopStation(st);   // frees the slot; no restart while unseen
                    }
                    continue;
                }

                if (st.VoiceTrack != null && !SafeExists(st.VoiceTrack))
                {
                    CMMConfig.D($"[CMM] Station '{st.Name}': airing track gone ({Path.GetFileName(st.VoiceTrack)}) — hot-swapping (interrupt).");
                    string ended = st.VoiceTrack;
                    StopVoice(st);
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

                int free = FreeSlot();
                if (free < 0)
                {
                    // All slots held: steal from the FARTHEST incumbent, with hysteresis.
                    Station far = null;
                    for (int s = 0; s < MaxSlots; s++)
                    {
                        Station own = _slotOwner[s];
                        if (own == null) continue;
                        if (far == null || own.NearestDist > far.NearestDist) far = own;
                    }
                    if (far == null || cand.NearestDist + StealMargin >= far.NearestDist) return;
                    CMMConfig.D($"[CMM] Station '{cand.Name}' ({cand.NearestDist:0.0}m) steals the slot of '{far.Name}' ({far.NearestDist:0.0}m).");
                    StopStation(far);
                }
                StartStation(cand, player, StartFade(), fresh: !HasActiveVoice);
                if (cand.VoiceId == 0) return;   // start failed (no playable track) — don't spin
            }
        }

        private static int FreeSlot()
        {
            for (int s = 0; s < MaxSlots; s++) if (_slotOwner[s] == null) return s;
            return -1;
        }

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
                if (st.NearestDist > audibleR) { StopStation(st); continue; }
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
                CMMConfig.D($"[CMM] Station '{st.Name}': no playable tracks.");
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
                && Time.realtimeSinceStartup - bornAt < 2f)
            {
                CMMConfig.D($"[CMM] Station '{st.Name}': just-born nearest speaker — birth fade {CMMConfig.WorldEmitterBirthFade:0.##}s.");
                fadeIn = CMMConfig.WorldEmitterBirthFade;
            }

            // Open the on-air track. If it fails — the file was moved/deleted since the catalog
            // scan — drop it from the schedule and re-resolve, bounded so a folder wipe can't spin.
            // (Dropping shortens the cycle; the next catalog rebuild restores determinism.)
            IntPtr dec = IntPtr.Zero;
            int kind = 0, srcCh = 0, srcRate = 0; long total = 0;
            string file = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                file = st.OnAir;
                if (file == null) return;
                dec = AudioDecoder.OpenNativeHandle(file, out kind, out srcCh, out srcRate, out total);
                if (dec != IntPtr.Zero) break;
                CMMConfig.D($"[CMM] Station '{st.Name}': on-air track failed to open ({Path.GetFileName(file)}) — poisoning in schedule.");
                RadioStationCatalog.PoisonTrack(file);
                ResolveOnAir(st, BroadcastSeconds());
            }
            if (dec == IntPtr.Zero) return;

            // Continuity: seek to the schedule's offset so we rejoin the broadcast mid-song.
            long startFrame = 0;
            double offset = st.OnAirOffset;
            if (offset < 0) offset = 0;
            if (srcRate > 0) startFrame = (long)(offset * srcRate);
            if (total > 0 && startFrame >= total) startFrame = 0;   // clock/length mismatch safety

            int slot = st.Slot >= 0 ? st.Slot : FreeSlot();
            if (slot < 0) return;   // shouldn't happen (SelectAndVoice frees one first)

            float trackGain = LoudnessStore.GetTrackGain(file);
            if (fresh)
            {
                CmmMixer.GetOrCreate().ResetEmitterAcoustics();
                ExpireBirths();
            }
            // A slot last used by a DIFFERENT station still holds its queued dry audio — flush it
            // (and the slot's render state) so nothing of the old station plays under this voice.
            if (!string.Equals(_slotLastStation[slot], st.Name, StringComparison.OrdinalIgnoreCase))
                CmmMixer.GetOrCreate().FlushEmitterSlot(slot);
            _slotLastStation[slot] = st.Name;
            // PlayEmitter → cmm_mix_add_emitter takes ownership of dec and frees it on every failure
            // path (returns 0), so a 0 id must NOT be closed again here. loop:false so the track ENDS
            // (retiring the voice) and the station advances along its schedule.
            Vector3 pos = new Vector3(st.NearestPos.x + 0.5f, st.NearestPos.y + 0.5f, st.NearestPos.z + 0.5f);
            long id = CmmMixer.GetOrCreate().PlayEmitter(dec, kind, srcRate, srcCh, total,
                                                         fadeIn, loop: false,
                                                         pos.x, pos.y, pos.z,
                                                         CMMConfig.WorldEmitterMinDistance, CMMConfig.WorldEmitterRange,
                                                         trackGain: trackGain, startFrame: startFrame, slot: slot);
            if (id == 0) return;
            st.VoiceId = id;
            st.VoiceTrack = file; st.VoiceGainApplied = trackGain;
            st.Slot = slot;
            _slotOwner[slot] = st;

            // Seed the voice's occlusion before its first audible block. Advance/hot-swap: carry the
            // last landed point set (same speakers, same ear, ≤ a tick old) straight onto the new
            // voice. Fresh start (no landed field yet): seed MUTED — virtual distance ≥ range ⇒
            // silent — and let the first flood glide the true value in (the start fade masks it).
            if (CMMConfig.WorldEmitterOcclusion && CMMConfig.WorldEmitterPathRouting)
            {
                if (!fresh && st.LastPtPushCount > 0 && Time.realtimeSinceStartup - st.LastPtPushAt < 1f)
                {
                    CmmMixer.Instance?.SetEmitterPoints(id, st.Pts, st.PtGain, st.PtFlags, st.LastPtPushCount);
                }
                else
                {
                    WritePoint(st, 0, st.NearestPos, st.NearestFlags);
                    st.PtGain[0] = CMMConfig.WorldEmitterRange;   // muted until the first flood lands
                    CmmMixer.Instance?.SetEmitterPoints(id, st.Pts, st.PtGain, st.PtFlags, 1);
                }
                _forceRefresh = true;
            }
            ContextMusicManager.RequestPrewarm(file);   // settle the gain in later if it wasn't measured yet
            PushTuning();           // apply the curve before the first render block
            PushEmitterMaster();    // set the radio level before the first block
            PushListener(player);   // prime the listener so the very first render block is positioned
            CMMConfig.D($"[CMM] Station '{st.Name}' started (slot {slot}): {Path.GetFileName(file)} @ {offset:0}s " +
                        $"(nearest speaker {st.NearestDist:0.0}m; B={BroadcastSeconds():0}s).");
        }

        // Release a station's voice AND its slot.
        private static void StopStation(Station st)
        {
            StopVoice(st);
            if (st.Slot >= 0)
            {
                if (_slotOwner[st.Slot] == st) _slotOwner[st.Slot] = null;
                st.Slot = -1;
            }
        }

        // Release just the native voice (keep the slot — used by the interrupt+restart path).
        private static void StopVoice(Station st)
        {
            if (st.VoiceId == 0) return;
            CmmMixer.Instance?.RemoveEmitter(st.VoiceId, StopFade);
            CMMConfig.D($"[CMM] Station '{st.Name}' stopped.");
            st.VoiceId = 0;
            st.VoiceTrack = null;
        }

        private static void StopAll()
        {
            for (int i = 0; i < _stations.Count; i++) StopStation(_stations[i]);
            _occlPending = false;   // abandon any in-flight flood (its finish targets dead voices)
        }

        // ── Catalog rebuild hook (called by RadioStationCatalog.RebuildCatalog) ─────────────────
        // Migrate live emitter voices onto the NEW station objects by name: a same-name station
        // keeps its voice/slot (its airing track must still be in the new schedule or the interrupt
        // path swaps it next tick); anything not migrated is released. `old` = the pre-rebuild
        // name → station map.
        internal static void OnCatalogRebuilt(Dictionary<string, Station> old)
        {
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
            foreach (var kv in old)
            {
                Station prev = kv.Value;
                if (prev.VoiceId == 0) continue;
                if (prev.Slot >= 0 && _slotOwner[prev.Slot] == prev) _slotOwner[prev.Slot] = null;
                CmmMixer.Instance?.RemoveEmitter(prev.VoiceId, StopFade);
                CMMConfig.D($"[CMM] Station '{prev.Name}' removed by catalog rebuild — voice released.");
            }
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
            CmmMixer.Instance?.SetEmitterTuning(CMMConfig.WorldEmitterMinDistance,
                                                CMMConfig.WorldEmitterRange,
                                                CMMConfig.WorldEmitterFalloff,
                                                CMMConfig.WorldEmitterMuffle,
                                                CMMConfig.WorldEmitterStereoWidth);
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
            float nowRev = Time.realtimeSinceStartup;
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

            if (Time.realtimeSinceStartup >= _nextReverbLog)
            {
                _nextReverbLog = Time.realtimeSinceStartup + 1.5f;
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
        private static Vector3 ListenerWorldPos(EntityPlayerLocal player)
        {
            Transform t = player.cameraTransform;
            return t != null ? t.position + Origin.position : player.position;
        }

        // ── Loudness integration ────────────────────────────────────────────────────────────────

        // A measurement for `file` just landed — if a live station voice is airing that exact track,
        // ease its loudness gain in. Called from the shared loudness pipeline. Main-thread.
        internal static void SettleLoudness(string file)
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
                CmmMixer.Instance?.SetVoiceTrackGain(st.VoiceId, g, 0.2f);   // short ramp = ease, no lurch
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
                if (!st.Ready || st.NearestDist == float.MaxValue) continue;
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
                if (!st.Ready || st.NearestDist == float.MaxValue) continue;
                if (st.VoiceId == 0) ResolveOnAir(st, B);   // idle: track the broadcast virtually
                if (ahead == 0 || st.OnAir == null) continue;
                ContextMusicManager.RequestPrewarm(st.OnAir);
                for (int k = 1; k <= ahead && k < st.Order.Length; k++)
                    ContextMusicManager.RequestPrewarm(st.Order[(st.OnAirIdx + k) % st.Order.Length]);
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
        // The trim is WorldEmitterVolume (settings.xml), NOT the Radio context's volume in
        // contexts.xml — that one now trims ONLY the personal radio (PersonalRadio
        // .MasterVolumeOverride). The two share a station catalog but not a level: a speaker across
        // the room and a radio in your head want different trims.
        private static void PushEmitterMaster()
        {
            float trim = Mathf.Clamp01(CMMConfig.WorldEmitterVolume);
            CmmMixer.Instance?.SetEmitterMaster(CMMConfig.OverallVolume * CMMConfig.AmbientVolume * trim);
        }
    }
}
#endif
