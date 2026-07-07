// Gated to 3.0 (GV_V3): depends on EmitterRegistry (same gate).
#if GV_V3
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // WORLD-EMITTER AUDIO LAYER  (Phase 3a — routing/decision, non-spatial)
    // ─────────────────────────────────────────────────────────────────────────
    // Turns detected in-world speakers (EmitterRegistry) into audible playback: when the nearest
    // detected speaker is within WorldEmitterRange, plays the shared "station" as an INDEPENDENT
    // emitter voice (CmmMixer.PlayEmitter) that sums ALONGSIDE the normal soundtrack instead of
    // crossfading it; stops it when every speaker leaves range. This is the BRAIN half of the
    // feature — it decides when the station plays; the native mixer's emitter voice is the hands.
    //
    // Phase 3a scope: NON-spatial (the voice renders centered) and it loops a single picked track.
    // Spatialization (pan + distance falloff), radio coloration, per-emitter mixing and station
    // advance come in later phases. Behavior model (settled): ONE shared station, always on while
    // a speaker is in range. Main-thread only (world reads + the registry poll + decoder open).
    internal static class EmitterAudioLayer
    {
        private static long _voiceId;        // active emitter voice id, 0 = none playing
        private static string[] _pool;       // cached track pool for the configured source
        private static string _poolSource;   // the source string _pool was built from (rebuild on change)
        private static DateTime _poolSig;    // folder/playlist change signature the pool was built at
        private static float _nextPoolCheck; // throttles the change-signature stat (recursive folders are pricey)
        private const float PoolCheckInterval = 2f;   // matches the context file-watch cadence
        private static readonly System.Random _rng = new System.Random();
        // Loudness normalization for the diegetic station. _voiceTrack is the file the live voice is
        // playing (so a measurement that lands after Start can settle its gain in); _voiceGainApplied is
        // the gain currently on the voice. _stationUpNext is the pre-picked, pre-MEASURED queue of the next
        // few tracks (LoudnessMeasureAhead deep) so an advance plays an already-normalized track — no
        // on-demand decode while airing.
        private static string _voiceTrack;
        private static float  _voiceGainApplied = 1f;
        private static readonly List<string> _stationUpNext = new List<string>();

        // ── Station clock (continuity) ──────────────────────────────────────────────────────────────
        // The station "keeps broadcasting" on a wall-clock playhead even while out of range, so rejoining
        // resumes MID-SONG where the station actually is — not from the top of a fresh random track. It
        // doesn't decode/buffer while idle; it just tracks which track is airing and how far in.
        //   _stationAiredAt = realtimeSinceStartup at which the CURRENT track's position 0 aired; the
        //   offset into it is (now - _stationAiredAt). While playing, the decoder's EOF is authoritative
        //   (advance then); while idle, the clock advances virtually across probed track lengths.
        private static string _stationTrack;    // the track currently "on air" (null = none picked yet)
        private static long   _stationFrames;   // its total frames (0 = unknown length)
        private static int    _stationRate;     // its sample rate
        private static float  _stationAiredAt;  // realtimeSinceStartup of the current track's position 0
        // Probed track lengths, so the idle clock doesn't re-open a file it already measured.
        private static readonly Dictionary<string, long> _lenFrames = new Dictionary<string, long>();
        private static readonly Dictionary<string, int>  _lenRate   = new Dictionary<string, int>();

        // The full-volume plateau radius + falloff shape are live config (WorldEmitterMinDistance /
        // Falloff / Muffle), pushed to the native spatializer every tick so they can be dialled in-game.

        // Max speaker positions fed to one emitter voice (matches native MAX_EMIT_PTS). Reused buffers
        // (+ per-slot distances for the nearest-N cull) so the per-tick point push allocates nothing.
        private const int MaxPoints = 8;
        private static readonly float[] _pts = new float[MaxPoints * 3];
        private static readonly float[] _ptGain = new float[MaxPoints];   // per-speaker occlusion gain
        private static readonly float[] _ptDist = new float[MaxPoints];

        // Decision + point-set refresh cadence (4 Hz). The listener itself is pushed every frame; this
        // only governs how fast start/stop and the in-range speaker SET react as you move.
        private const float TickInterval = 0.25f;
        private const float DoorTickInterval = 0.05f;   // ~20 Hz while a door is mid-swing (tight fade/mute timing)
        private static float _nextTick;
        private static bool  _forceRefresh;    // an acoustic change (door toggle) fired → run the next refresh a frame early instead of waiting up to 250 ms (main-thread only)
        private static float _nextReverbLog;   // throttle for the reverb diagnostic (DebugLogging only)

        // True while the station voice is live. Read by the door hook so it only pokes when there's something to update.
        internal static bool HasActiveVoice => _voiceId != 0;

        // Called from the door Harmony hook (Patch_DoorAcousticChange) the instant a door's open/closed state
        // flips. Just requests that the 4 Hz decision/occlusion tick run on the next frame rather than waiting
        // out the interval — so the occlusion re-floods with the door's new state immediately and the ~1 s
        // native gain glide starts WITH the swing instead of finishing after it.
        internal static void NotifyAcousticChange() => _forceRefresh = true;
        private static float _revWetSmooth, _revRoomSmooth;   // reverb inertia (EMA of the pushed wet/room)

        internal static void Tick()
        {
            if (!CMMConfig.WorldEmitterEnabled || string.IsNullOrWhiteSpace(CMMConfig.WorldEmitterSource))
            {
                Stop();
                return;
            }

            // No local player (menu / loading / world teardown): stop and bail, so a stale registry
            // snapshot can't restart the station off the world.
            EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
            if (player == null) { Stop(); return; }

            // Per-frame (unthrottled) while a voice is live: keep the listener basis fresh so the pan
            // tracks head/body movement smoothly, not in 0.5 s steps.
            if (_voiceId != 0)
            {
                PushListener(player);

                // Per-frame destruction re-check so smashing a speaker cuts the station promptly instead of
                // waiting for the 4 Hz decision tick below. Only revalidates the already-tracked speakers
                // (cheap); the POI enumeration/scan stays throttled in Poll().
                // We release the voice ONLY when the speakers are truly GONE (destroyed / chunk unloaded,
                // Count == 0) — NOT merely because we walked out of range. Walking out of range keeps the
                // voice WARM: the native distance falloff clamps it to silence past WorldEmitterRange
                // (cmm_out att = 0 for dEff ≥ max), and the per-frame PushListener above keeps that current
                // as we move. That way re-entering range is seamless — no re-open + mid-song re-seek, which
                // was a main-thread hitch (a far dr_mp3 seek scans from 0; ~133 ms at 105 s in).
                World w = GameManager.Instance?.World;
                if (w != null)
                {
                    EmitterRegistry.RefreshSnapshot(w, player.position);
                    if (EmitterRegistry.Count == 0) { Stop(); return; }
                }
            }

            // A door toggled since the last tick (hook set the flag) → collapse the up-to-250 ms wait so the
            // occlusion re-floods with the door's new open/closed state this frame.
            if (_forceRefresh) { _forceRefresh = false; _nextTick = 0f; }
            if (Time.realtimeSinceStartup < _nextTick) return;
            // While a door is mid-swing, refresh fast (~20 Hz) so its openness is sampled tightly — the fade
            // starts promptly on open and the mute is precise on close — instead of in 250 ms steps. Back to
            // 4 Hz once all doors settle (rooms/speaker-set change slowly; the flood cost stays bounded).
            _nextTick = Time.realtimeSinceStartup + (EmitterAcoustics.AnyDoorAnimating ? DoorTickInterval : TickInterval);

            // Push the live spatial-curve tuning from config (hot-reloaded), so edits to plateau /
            // range / falloff / muffle in settings.xml apply in-game without a restart.
            PushTuning();
            PushEmitterMaster();   // radio level = music slider × Radio context volume (live)

            // Keep the station "broadcasting" on its wall-clock while nothing is playing (so the next time
            // a speaker comes in range we rejoin mid-song), and — while a voice IS playing — reconcile the
            // pool with disk and hot-swap the airing track the moment its file vanishes (like a context).
            if (_voiceId == 0) StationSyncIdle();
            else MaintainPlayingStation(player);

            MaintainUpNext();   // keep the next few tracks pre-picked + measured so an advance is normalized

            EmitterRegistry.Poll();
            bool hasEmitter = EmitterRegistry.Count > 0;
            float nearest = EmitterRegistry.NearestDist;
            bool inRange = hasEmitter && nearest <= CMMConfig.WorldEmitterRange;
            // The wet reverb reaches farther than the dry (out to range × ReverbRangeMult), so the voice is
            // still "doing something audible" in that outer ring even when the dry has faded to 0. Run the
            // room probe + occlusion out to whichever is larger; past THAT everything is truly silent and we
            // let the warm voice keep decoding untouched (cheap — no flood, no probe).
            float audibleR = CMMConfig.WorldEmitterRange;
            if (CMMConfig.WorldEmitterReverbEnabled)
                audibleR *= Mathf.Max(1f, CMMConfig.WorldEmitterReverbRangeMult);
            bool audible = hasEmitter && nearest <= audibleR;

            if (_voiceId == 0)
            {
                if (inRange) Start(player, StartFade());
            }
            else if (!hasEmitter)
            {
                Stop();   // speakers gone (destroyed / chunk unloaded) → release the warm voice
            }
            else if (CmmMixer.Instance != null && !CmmMixer.Instance.TryGetState(_voiceId, out _, out _, out _))
            {
                // The voice retired = the current track finished (decoder EOF is authoritative). Advance the
                // station clock and start the next track. We do this even while out of range so the warm
                // voice stays on the SAME timeline as reality — the next re-entry rejoins seamlessly instead
                // of having to seek far into a mid-song offset. The fresh track opens at ~frame 0 (cheap).
                CMMConfig.D("[CMM] World emitter: track ended → next track.");
                _voiceId = 0;
                ForceAdvanceStation();
                Start(player, AdvanceFade);
            }
            else if (audible)
            {
                int count = BuildPointBuffer(player);   // all in-range speakers + their occlusion
                CmmMixer.Instance?.SetEmitterPoints(_voiceId, _pts, _ptGain, count);
                PushReverb(player);                     // probe the room + drive the reverb
            }
            // else: voice live but out of the whole audible radius — keep it warm and in sync, but skip the
            // occlusion flood + room probe. Distance falloff (updated by the per-frame PushListener) holds
            // it silent; re-entry resumes the heavy update with no re-open/re-seek.
        }

        // Fill _pts/_ptGain with the world-centre and occlusion gain of every detected speaker within
        // WorldEmitterRange (nearest MaxPoints when there are more), returning the count. Allocation-
        // free (reused buffers). The native voice sums all of these, so they're heard together.
        // Two phases: first CULL to the nearest speakers (writing positions), then run the ray-traced
        // occlusion probe ONCE over that set (EmitterAcoustics) to fill the gains — so the expensive
        // reflection fan is bounded to at most MaxPoints speakers.
        private static int BuildPointBuffer(EntityPlayerLocal player)
        {
            float range = CMMConfig.WorldEmitterRange;
            var snap = EmitterRegistry.Snapshot;
            World world = GameManager.Instance?.World;
            bool occlude = CMMConfig.WorldEmitterOcclusion && world != null;
            Vector3 ear = ListenerWorldPos(player);   // camera ear, same world-space point as the listener

            int count = 0;
            for (int i = 0; i < snap.Count; i++)
            {
                EmitterRegistry.Emitter e = snap[i];
                if (e.Dist > range) continue;
                if (count < MaxPoints)
                {
                    WritePoint(count, e.Pos); _ptDist[count] = e.Dist; count++;
                }
                else
                {
                    // Buffer full: replace the current farthest slot if this speaker is nearer.
                    int far = 0;
                    for (int k = 1; k < MaxPoints; k++) if (_ptDist[k] > _ptDist[far]) far = k;
                    if (e.Dist < _ptDist[far]) { WritePoint(far, e.Pos); _ptDist[far] = e.Dist; }
                }
            }

            if (occlude)
                EmitterAcoustics.ComputeOcclusion(ear, _pts, count, _ptGain, world);
            else
                for (int i = 0; i < count; i++) _ptGain[i] = 1f;

            return count;
        }

        private static void WritePoint(int slot, Vector3i p)
        {
            int o = slot * 3;
            _pts[o] = p.x + 0.5f; _pts[o + 1] = p.y + 0.5f; _pts[o + 2] = p.z + 0.5f;
        }

        // Push the listener basis (position + forward + right) to the mixer, ALL from the camera
        // transform so position is as smooth/fresh as orientation — the camera updates every render
        // frame, whereas player.position (the entity) steps on the physics tick, which made distance/
        // volume lag while turn-panning stayed instant.
        //   Position: cameraTransform.position is RENDER space (7D2D floating origin), so + Origin.
        //   position converts it back to TRUE WORLD space, matching block coords (using the render
        //   position directly would put every emitter ±260m off → wrong distance).
        //   Direction: forward/right are rotations, unaffected by the origin shift.
        private static void PushListener(EntityPlayerLocal player)
        {
            Transform t = player.cameraTransform;
            if (t == null) return;
            CmmMixer.Instance?.SetListener(t.position + Origin.position, t.forward, t.right);
        }

        // Relay the live spatial-curve tuning (plateau radius, audible range, falloff steepness,
        // distance-muffle strength) from config to the native spatializer. Cheap (4 lock-free writes);
        // config is hot-reloaded, so this is how in-game settings.xml edits take effect live.
        private static void PushTuning()
        {
            CmmMixer.Instance?.SetEmitterTuning(CMMConfig.WorldEmitterMinDistance,
                                                CMMConfig.WorldEmitterRange,
                                                CMMConfig.WorldEmitterFalloff,
                                                CMMConfig.WorldEmitterMuffle,
                                                CMMConfig.WorldEmitterStereoWidth);
        }

        // Probe the room around the listener and push the resulting reverb params to the native mixer.
        // When reverb is disabled (or no world), push wet 0 so the reverb is bypassed. Throttled (called
        // from the 4 Hz decision tick) — rooms change slowly, and the native side slews internally.
        private static void PushReverb(EntityPlayerLocal player)
        {
            World world = GameManager.Instance?.World;
            if (!CMMConfig.WorldEmitterReverbEnabled || world == null)
            {
                CmmMixer.Instance?.SetEmitterReverb(0f, 0.5f, 0.5f, 1f, 0f);
                return;
            }
            Vector3 ear = ListenerWorldPos(player);
            EmitterAcoustics.ProbeRoom(ear, world, out float avgDist, out float roomSize, out float reflect, out float openFrac, out bool roofed);
            // Prefer the FLOOD's connected-air extent for room SIZE: it walks around corners/tunnels, so a
            // winding corridor reads its real length instead of the ray probe's short sightline. Take whichever
            // sees the larger space (rays can reach farther than the flood box in a straight open shot).
            // Enclosure & reflectivity stay from the ray probe — those it measures fine. Only when the flood ran.
            if (CMMConfig.WorldEmitterOcclusion && CMMConfig.WorldEmitterPathRouting && EmitterAcoustics.AirCellCount > 0)
                roomSize = Mathf.Max(roomSize, EmitterAcoustics.AirRoomSize);
            EmitterAcoustics.ComputeReverb(roomSize, reflect, openFrac, roofed, out float wet, out float room, out float damp);
            wet  *= CMMConfig.WorldEmitterReverbWet;     // LEVEL lever
            // Fade the wet when the speakers are SOLID-sealed off (no air path) so reflections don't leak
            // through a wall; ReverbSeal=0 (an open corridor/corner) leaves it untouched so the wet still
            // carries around bends. WorldEmitterReverbOcclusion scales how hard the seal bites.
            wet  *= 1f - CMMConfig.WorldEmitterReverbOcclusion * EmitterAcoustics.ReverbSeal;
            room *= CMMConfig.WorldEmitterReverbDecay;   // TAIL-length lever (independent of level)
            // Reverb has inertia — a box you step behind for a moment shouldn't snap the room off. EMA the
            // pushed wet/room (~1.5 s glide at the 4 Hz tick) so the reverb eases as the space changes.
            _revWetSmooth  += (wet  - _revWetSmooth)  * 0.18f;
            _revRoomSmooth += (room - _revRoomSmooth) * 0.18f;
            if (Time.realtimeSinceStartup >= _nextReverbLog)
            {
                _nextReverbLog = Time.realtimeSinceStartup + 1.5f;
                CMMConfig.D($"[CMM] Reverb: enc={1f - openFrac:0.00} rayS={avgDist:0.0} airSize={EmitterAcoustics.AirRoomSize:0.0} airCells={EmitterAcoustics.AirCellCount} size={roomSize:0.0} reflect={reflect:0.00} " +
                            $"roofed={roofed} seal={EmitterAcoustics.ReverbSeal:0.00} nearest={EmitterRegistry.NearestDist:0.0}m → wet={_revWetSmooth:0.00} room={_revRoomSmooth:0.00} damp={damp:0.00}");
            }
            CmmMixer.Instance?.SetEmitterReverb(_revWetSmooth, _revRoomSmooth, damp, CMMConfig.WorldEmitterReverbRangeMult, CMMConfig.WorldEmitterReverbPan);
        }

        // The listener's world-space position (camera ear), matching block coords. Used for occlusion
        // rays. Falls back to the entity position if the camera isn't up yet.
        private static Vector3 ListenerWorldPos(EntityPlayerLocal player)
        {
            Transform t = player.cameraTransform;
            return t != null ? t.position + Origin.position : player.position;
        }

        // World-space centre of the nearest detected speaker block.
        private static Vector3 NearestWorldPos()
        {
            Vector3i p = EmitterRegistry.NearestPos;
            return new Vector3(p.x + 0.5f, p.y + 0.5f, p.z + 0.5f);
        }

        // Fade-in for advancing to the NEXT track (station continuing) — short so it's near-seamless,
        // unlike the gentle WorldEmitterFade used when the station first comes into range.
        private const float AdvanceFade = 0.2f;

        private static void Start(EntityPlayerLocal player, float fadeIn)
        {
            StationSyncIdle();                 // bring the station's on-air track up to the current moment
            if (_stationTrack == null)
            {
                CMMConfig.D($"[CMM] World emitter: no playable tracks in source '{CMMConfig.WorldEmitterSource}'.");
                return;
            }

            // Open the on-air track. If it fails to open — the file was moved/deleted out from under
            // us since the pick (a hotswap the change-signature check hasn't reconciled yet) — drop it
            // and re-pick a surviving track, rather than going SILENT until the virtual clock happens
            // to roll past the dead track. Bounded so a wholesale folder wipe can't spin.
            IntPtr dec = IntPtr.Zero;
            int kind = 0, srcCh = 0, srcRate = 0; long total = 0;
            string file = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                file = _stationTrack;
                if (file == null) return;
                dec = AudioDecoder.OpenNativeHandle(file, out kind, out srcCh, out srcRate, out total);
                if (dec != IntPtr.Zero) break;
                CMMConfig.D($"[CMM] World emitter: on-air track failed to open ({Path.GetFileName(file)}) — re-picking.");
                DropFromPool(file);
                if (_pool == null || _pool.Length == 0) { _stationTrack = null; return; }
                PickStationTrack(Time.realtimeSinceStartup);
            }
            if (dec == IntPtr.Zero) return;

            // Continuity: seek to the station's virtual playhead so we rejoin mid-song, not from 0.
            long startFrame = 0;
            float offset = Time.realtimeSinceStartup - _stationAiredAt;
            if (offset < 0f) offset = 0f;
            if (srcRate > 0) startFrame = (long)(offset * srcRate);
            if (total > 0 && startFrame >= total) startFrame = 0;   // safety (clock/length mismatch)

            // PlayEmitter → cmm_mix_add_emitter takes ownership of dec and frees it on every failure
            // path (returns 0), so a 0 id must NOT be closed again here — that would double-free.
            // loop:false so the track ENDS (retiring the voice) and the station advances to the next one.
            Vector3 pos = NearestWorldPos();
            // min/max here seed the voice; the live spatializer actually uses the tuning globals
            // (PushTuning), so range/plateau stay hot-tunable while a voice is playing.
            // Normalize the diegetic station too: apply the track's loudness gain (1.0 if off / not yet
            // measured). If it isn't measured, play un-normalized now and request a measure — SettleLoudness
            // eases the gain in the moment it lands. The N-ahead queue means this is usually already ready.
            float trackGain = LoudnessStore.GetTrackGain(file);
            long id = CmmMixer.GetOrCreate().PlayEmitter(dec, kind, srcRate, srcCh, total,
                                                         fadeIn, loop: false,
                                                         pos.x, pos.y, pos.z,
                                                         CMMConfig.WorldEmitterMinDistance, CMMConfig.WorldEmitterRange,
                                                         trackGain: trackGain, startFrame: startFrame);
            if (id == 0) return;
            _voiceId = id;
            _voiceTrack = file; _voiceGainApplied = trackGain;
            ContextMusicManager.RequestPrewarm(file);   // settle the gain in later if it wasn't measured yet
            PushTuning();           // apply the curve before the first render block
            PushEmitterMaster();    // set the radio level before the first block
            PushListener(player);   // prime the listener so the very first render block is already positioned
            // The listener→emitter world distance should match the registry's nearest distance; a big
            // mismatch would mean a coordinate-space bug (e.g. render vs world space) and → silence.
            CMMConfig.D($"[CMM] World emitter station started: {Path.GetFileName(file)} @ {offset:0}s " +
                        $"(nearest {EmitterRegistry.NearestName} @ {EmitterRegistry.NearestDist:0.0}m; " +
                        $"listener→emitter {Vector3.Distance(player.position, pos):0.0}m).");
        }

        // ── Station clock helpers ─────────────────────────────────────────────────────────────────────
        // Advance the virtual playhead across any tracks that have "finished airing" since we last looked,
        // so the current track + its offset are correct at `now`. Cheap while nothing elapses (a time
        // compare); only opens a decoder when it rolls onto a new, not-yet-measured track.
        private static void StationSyncIdle()
        {
            EnsurePool();
            if (_pool == null || _pool.Length == 0) { _stationTrack = null; return; }
            float now = Time.realtimeSinceStartup;
            // No track on air yet, or the one that was airing dropped out of the pool (removed/moved on
            // disk since we last looked) → re-pick from the current pool. No audible impact while idle.
            if (_stationTrack == null || !PoolContains(_stationTrack)) { PickStationTrack(now); return; }
            int guard = 0;
            float len = StationLenSec();
            while (len > 0f && now - _stationAiredAt >= len && guard++ < 64)
            {
                PickStationTrack(_stationAiredAt + len);   // next track went on air when this one ended
                len = StationLenSec();
            }
            if (guard >= 64) _stationAiredAt = now;   // very long absence → stop simulating, snap to now
        }

        // The track's decoder ended (authoritative): put the next one on air, effective NOW.
        private static void ForceAdvanceStation() => PickStationTrack(Time.realtimeSinceStartup);

        // While a voice is playing: keep the pool reconciled with disk (adds/removes) and — matching how
        // a normal context reacts to its playing file vanishing — INTERRUPT and hot-swap to a fresh track
        // the moment the airing track is moved/deleted or drops out of the source. The user is fine with a
        // cut, so we don't wait for the buffered voice to finish. Cheap: an O(1) File.Exists ping each tick
        // (like the context watch's active-track ping) plus the throttled pool-signature stat in EnsurePool.
        private static void MaintainPlayingStation(EntityPlayerLocal player)
        {
            EnsurePool();   // fold in folder adds/removes on the throttled cadence
            string airing = _stationTrack;
            bool gone = airing == null || !PoolContains(airing) || !SafeExists(airing);
            if (!gone) return;
            CMMConfig.D($"[CMM] World emitter: airing track gone ({(airing != null ? Path.GetFileName(airing) : "null")}) — hot-swapping (interrupt).");
            Stop();                       // cut the voice on the vanished track (short StopFade, click-free)
            ForceAdvanceStation();        // fresh track on air, effective now
            Start(player, AdvanceFade);   // start it (short fade)
        }

        // File.Exists, treating a transient IO error (an external move/delete mid-stat) as "present" so a
        // hiccup doesn't needlessly interrupt playback — same stance as the context watch's ping.
        private static bool SafeExists(string file)
        {
            try { return File.Exists(file); } catch { return true; }
        }

        // Put the next track on air at wall-time `airedAt`, probing its length for the clock. Prefers the
        // pre-picked (already-measured) up-next queue so an advance airs a normalized track; falls back to a
        // fresh random pick when the queue is empty (e.g. drained by a long out-of-range catch-up) or the
        // popped track has since left the pool.
        private static void PickStationTrack(float airedAt)
        {
            EnsurePool();
            _stationAiredAt = airedAt;
            _stationTrack = null;
            while (_stationUpNext.Count > 0 && _stationTrack == null)
            {
                string cand = _stationUpNext[0];
                _stationUpNext.RemoveAt(0);
                if (PoolContains(cand)) _stationTrack = cand;   // else it left the pool — skip it
            }
            if (_stationTrack == null && _pool != null && _pool.Length > 0)
                _stationTrack = _pool[_rng.Next(_pool.Length)];
            _stationFrames = 0; _stationRate = 0;
            if (_stationTrack != null) ProbeLength(_stationTrack, out _stationFrames, out _stationRate);
        }

        private static float StationLenSec()
        {
            if (_stationRate > 0 && _stationFrames > 0) return (float)_stationFrames / _stationRate;
            return 240f;   // unknown length ⇒ nominal 4 min so the clock still advances
        }

        // Measure a track's total frames + rate (header read), cached so idle sync doesn't re-open files.
        private static void ProbeLength(string file, out long frames, out int rate)
        {
            if (_lenFrames.TryGetValue(file, out frames)) { rate = _lenRate[file]; return; }
            frames = 0; rate = 0;
            IntPtr dec = AudioDecoder.OpenNativeHandle(file, out int kind, out _, out int r, out long total);
            if (dec != IntPtr.Zero) { frames = total; rate = r; AudioDecoder.CloseNativeHandle(dec, kind); }
            _lenFrames[file] = frames; _lenRate[file] = rate;
        }

        // Keep _pool in sync with the source: rebuild on a source-STRING change, and — the "hotswap"
        // parity with normal contexts — rebuild when the folder/playlist CONTENTS change on disk (tracks
        // added/removed), on a throttled change-signature stat. Pure pool maintenance: the REACTION to
        // the on-air track vanishing is the caller's job (idle re-picks silently in StationSyncIdle;
        // a live voice interrupts + swaps in MaintainPlayingStation), because the right reaction depends
        // on whether we're currently playing. Called from StationSyncIdle / PickStationTrack / Start /
        // MaintainPlayingStation; the throttle makes re-entry (PickStationTrack) a no-op.
        private static void EnsurePool()
        {
            Contexts ctx = RadioContext();
            string src = ctx != null ? ctx.Source : CMMConfig.WorldEmitterSource;
            bool recursive = ctx != null ? ctx.IncludeSubfolders : true;

            if (_pool == null || !string.Equals(_poolSource, src, StringComparison.OrdinalIgnoreCase))
            {
                _pool = BuildPool(src, recursive);
                _poolSource = src;
                _poolSig = SourceSignature(src, recursive);
                _stationTrack = null;   // source changed → restart the station
                _stationUpNext.Clear();  // the queued upcoming tracks belong to the old source
                return;
            }

            // Same source: watch the folder for adds/removes on a modest cadence. A recursive signature
            // walks every subdir, so don't stat it at the 4 Hz tick rate.
            if (Time.realtimeSinceStartup < _nextPoolCheck) return;
            _nextPoolCheck = Time.realtimeSinceStartup + PoolCheckInterval;

            // An EMPTY pool is the recovery-critical case: a track added to a folder that was empty must
            // bring the station back. The folder-mtime signature can't be trusted to catch that — Windows
            // can stamp the empty dir and the re-filled dir with the SAME time when the add lands inside one
            // filesystem-precision window (FAT/exFAT/network shares are coarser still). This is exactly what
            // the context FileWatch guards with its `previouslyEmpty` special-case; the pool needs the same
            // guard or a station whose folder started empty never picks up its first track. So while the
            // pool is empty, always re-enumerate rather than short-circuit on an unchanged signature.
            bool poolEmpty = _pool.Length == 0;
            DateTime sig = SourceSignature(src, recursive);
            if (!poolEmpty && sig == _poolSig) return;
            _poolSig = sig;

            _pool = BuildPool(src, recursive) ?? _pool;   // keep the old pool on a transient read failure
        }

        // Cheap change-signature for the pool source, reusing the exact helpers the context file-watch
        // uses: a playlist's own mtime, or the folder's write-time (folding in subdirs when recursive).
        private static DateTime SourceSignature(string src, bool recursive)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(src)) return DateTime.MinValue;
                string ext = Path.GetExtension(src).ToLowerInvariant();
                if (ext == ".m3u" || ext == ".m3u8")
                    return SourceScan.PlaylistWriteSignatureUtc(PlaylistSource.ResolveFullPath(src));
                string dir = Path.IsPathRooted(src) ? src : Path.Combine(ContextMusicManager.MusicDirectory, src);
                return SourceScan.ContextWriteSignatureUtc(dir, recursive);
            }
            catch { return DateTime.MinValue; }
        }

        private static bool PoolContains(string file)
        {
            if (_pool == null) return false;
            for (int i = 0; i < _pool.Length; i++)
                if (string.Equals(_pool[i], file, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Remove a single (dead) track from the pool in place — used when a pick fails to open so we
        // don't immediately re-pick the same missing file. Self-corrects on the next full pool rebuild.
        private static void DropFromPool(string file)
        {
            if (_pool == null) return;
            int n = 0;
            for (int i = 0; i < _pool.Length; i++)
                if (!string.Equals(_pool[i], file, StringComparison.OrdinalIgnoreCase)) _pool[n++] = _pool[i];
            if (n != _pool.Length) Array.Resize(ref _pool, n);
        }

        // Quick fade-out on stop (destroyed speaker / out of range / disabled). Short so destroying a
        // speaker cuts crisply — WorldEmitterFade is the gentle fade-IN; a matching long fade-OUT here
        // is what made a smashed speaker seem to keep playing. Just long enough to avoid a click.
        private const float StopFade = 0.12f;

        private static void Stop()
        {
            if (_voiceId == 0) return;
            CmmMixer.Instance?.RemoveEmitter(_voiceId, StopFade);
            CMMConfig.D("[CMM] World emitter station stopped.");
            _voiceId = 0;
            _voiceTrack = null;
        }

        // A measurement for `file` just landed — if the live station voice is airing that exact track,
        // ease its loudness gain in (it started at whatever gain was known at Start, often neutral 1.0).
        // Called from the shared loudness pipeline (ContextMusicManager.EnsureLoudnessMeasured). Main-thread.
        internal static void SettleLoudness(string file)
        {
            if (_voiceId == 0 || string.IsNullOrEmpty(file)) return;
            if (!string.Equals(_voiceTrack, file, StringComparison.OrdinalIgnoreCase)) return;
            float g = LoudnessStore.GetTrackGain(file);
            if (Mathf.Abs(g - _voiceGainApplied) < 1e-4f) return;
            _voiceGainApplied = g;
            CmmMixer.Instance?.SetVoiceTrackGain(_voiceId, g, 0.2f);   // short ramp = ease, no lurch
        }

        // The station's loudness working set — the on-air track(s) plus the whole pre-measured up-next
        // queue — appended to the loudness eviction keep-set so RetainOnly does NOT drop measurements the
        // station is about to use. Without this the look-ahead prewarm and the per-tick sweep fight: each
        // tick prewarms the queue, the sweep evicts it (the emitter voice is not a context cache entry),
        // and next tick re-decodes it — a runaway re-measure loop. Main-thread only, like the sweep.
        internal static void AppendLoudnessKeep(List<string> dst)
        {
            if (_voiceTrack != null)   dst.Add(_voiceTrack);
            if (_stationTrack != null) dst.Add(_stationTrack);
            for (int i = 0; i < _stationUpNext.Count; i++) dst.Add(_stationUpNext[i]);
        }

        // Keep the pre-picked up-next queue filled LoudnessMeasureAhead deep and request a background
        // measurement for the airing track + every queued track, so an advance always plays an
        // already-normalized track (no on-demand decode). Cheap + dedup'd: RequestPrewarm no-ops on
        // already-measured / in-flight files. Called each tick.
        private static void MaintainUpNext()
        {
            if (!CMMConfig.LoudnessNormalizationEnabled) return;
            if (_pool == null || _pool.Length == 0) return;
            int ahead = CMMConfig.LoudnessMeasureAhead;
            if (ahead < 0) ahead = 0;
            while (_stationUpNext.Count < ahead)
                _stationUpNext.Add(_pool[_rng.Next(_pool.Length)]);
            if (_stationTrack != null) ContextMusicManager.RequestPrewarm(_stationTrack);
            for (int i = 0; i < _stationUpNext.Count; i++)
                ContextMusicManager.RequestPrewarm(_stationUpNext[i]);
        }

        // ── Config via the "Radio" context ──────────────────────────────────────────────────────────
        // WorldEmitterSource may NAME a context defined in contexts.xml (e.g. "Radio"). When it does,
        // that context is the definition home for the station: its Source is the music folder/playlist,
        // its FadeInDuration is the start crossfade, its VolumeOverride is the level, its IncludeSubfolders
        // the recursion. When it doesn't match a context, the key is treated as a raw folder/playlist
        // (the historical behavior) with WorldEmitterFade + the global volume.
        private static Contexts RadioContext() => ContextMusicManager.GetContext(CMMConfig.WorldEmitterSource);

        // Start crossfade: the context's fade-in when defined, else the standalone WorldEmitterFade.
        private static float StartFade()
        {
            Contexts c = RadioContext();
            return c != null ? c.FadeInDuration : CMMConfig.WorldEmitterFade;
        }

        // Push the world-radio's OWN master: the game's music SLIDER × the Radio context's own volume
        // (unset ⇒ 1). Deliberately NOT × VolumeModifier and NOT × the current music context's volume —
        // the diegetic radio is independent of the soundtrack's level and scales only to the slider and
        // its own context volume. Pushed every tick (the slider can move live). Native has a dedicated
        // emitter master (cmm_mix_set_emitter_master) so this doesn't touch the music master.
        private static void PushEmitterMaster()
        {
            Contexts c = RadioContext();
            float ctxVol = (c != null && c.VolumeOverride >= 0f) ? c.VolumeOverride : 1f;
            float slider = GamePrefs.GetFloat(EnumGamePrefs.OptionsMusicVolumeLevel);
            CmmMixer.Instance?.SetEmitterMaster(slider * ctxVol);
        }

        // A random track from the resolved source (Radio context's Source, or a raw folder / .m3u), pool
        // cached until that source changes. The station advances track-to-track (loop:false in Start).
        // Resolve the source string to a track pool: an .m3u playlist reads its listed tracks; any
        // other value is a folder (relative to the Music dir, or absolute) scanned for supported audio
        // — mirroring how a context source is enumerated (SourceScan/PlaylistSource).
        private static string[] BuildPool(string src, bool recursive)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(src)) return null;   // sentinel/empty context source → no audio
                string ext = Path.GetExtension(src).ToLowerInvariant();
                if (ext == ".m3u" || ext == ".m3u8")
                    return PlaylistSource.ReadTracks(PlaylistSource.ResolveFullPath(src));

                string dir = Path.IsPathRooted(src) ? src : Path.Combine(ContextMusicManager.MusicDirectory, src);
                if (!Directory.Exists(dir)) return null;
                return SourceScan.EnumerateFolderPoolRaw(dir, recursive, claimed: new string[0], out _);
            }
            catch { return null; }
        }
    }
}
#endif
