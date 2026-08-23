using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // NATIVE AUDIO OUTPUT  (CmmMixer — a thin facade over the native cmm_mix_* mixer)
    // ─────────────────────────────────────────────────────────────────────────
    // CMM's own audio output, fully independent of Unity. The ENTIRE producer loop —
    // decode → convert → per-voice gain ramp (crossfades) → sum → output — now runs in
    // native C on its own thread (CMM_Decoder.dll cmm_mix_*), so the game's GC (which
    // stops the world for ALL managed threads) can never suspend it mid-stream and
    // underrun the output ring. See CMM_Decoder/cmm_mix.c for the implementation; it is
    // a faithful port of what used to be the managed Pump/Voice/RenderVoiceAdd loop here.
    //
    // The managed side stays the BRAIN: it decides which file plays and when, opens each
    // decoder off the audio thread (AudioDecoder.OpenNativeHandle), and hands the native
    // cmm_decoder* + a format `kind` to cmm_mix_add — the mixer takes ownership and reads
    // it natively. This facade keeps the exact method/property names the control layer
    // already calls (Play/Stop/StopAllNow/TryGetState/RemainingSeconds/SetVoiceTrackGain/
    // SetOutputGain/TryReclaim/RequestDeviceReinit + MasterVolume/DeepBuffer); each is now
    // a one-line P/Invoke. A FIXED 48 kHz/stereo bus (each voice converts to it natively)
    // is what lets tracks of ANY sample rate crossfade.
    //
    // This is the sole output path for custom-file contexts (the legacy AudioSource/
    // AudioClip path is gone); the `cmm native*` console commands drive it for testing.

    internal static class NativeMix
    {
        const string Dll = "CMM_Decoder";

        // Create the mixer + start the native producer thread (idempotent). The device
        // opens lazily on the first cmm_mix_add. tight/maxFrames = cushion depths in frames.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mix_start(int busRate, int busChannels, int tightFrames, int maxFrames, out int err);

        // v15 — start with a headless (no-device) output for the offline stress harness, and
        // drain that ring manually. In-game code never sets headless; these are unused there.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mix_start_ex(int busRate, int busChannels, int tightFrames, int maxFrames, int headless, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mix_pump_out(int frames);

        // Install a track + crossfade to it. dec+kind name a decoder the caller opened
        // (AudioDecoder.OpenNativeHandle). Takes ownership of dec (closes it on retire OR on
        // failure). Returns a voice id (0 on failure) to poll via get_state / remaining.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long cmm_mix_add(IntPtr dec, int kind, int srcRate, int srcChannels,
            long totalFrames, float fade, int loop, long startFrame, float trackGain, float duckOthersTo, int snapTight);

        // v16 — emitter voices: an independent positional layer that plays alongside the music
        // voices (exempt from the music crossfade + eviction). Same decoder handoff as cmm_mix_add;
        // takes ownership of dec. v31: `slot` (0..MaxEmitterSlots-1) pins the voice to a station
        // render slot — voices sharing a slot sum (a track-advance crossfade). Returns an emitter
        // voice id (0 on failure).
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern long cmm_mix_add_emitter(IntPtr dec, int kind, int srcRate, int srcChannels,
            long totalFrames, float fadeIn, int loop, float trackGain,
            float posX, float posY, float posZ, float minDist, float maxDist, long startFrame, int slot);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_remove_emitter(long id, float fade);

        // v30 — reset the emitter acoustic render state (reverb tails, slews, per-slot smooth state)
        // before a FRESH station voice starts, so the previous voice's leftovers never bleed under it.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_emitter_reset_acoustics();

        // v32 — flush one render slot (queued dry ring + per-slot render state) before a DIFFERENT
        // station acquires it, so the old station's buffered audio never plays under the new voice.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_emitter_flush_slot(int slot);

        // v17 — listener basis (pos + forward + right, from the camera) + live emitter reposition.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_listener(float px, float py, float pz,
            float fx, float fy, float fz, float rx, float ry, float rz);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_emitter_pos(long id, float x, float y, float z);

        // v18/v19 — every speaker position playing the station (interleaved xyz) + per-speaker
        // occlusion gain (1=clear, <1=muffled; may be null for all-clear).
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_emitter_points(long id, float[] xyz, float[] gains, int[] flags, int count);

        // v43 — per-slot ring health. The music ring's counters said "underruns 0" while the
        // radios were dropping out, because nothing watched these.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mix_emitter_ring_stat(int slot, int what);

        // v42: drive emitter TONE from occlusion rather than from virtual distance. Off restores the
        // shipped behaviour exactly; the native acoustic model turns it on, because the number it
        // publishes is an attenuation whose worth in BLOCKS depends on where the listener stands.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_emitter_occl_tone(int enable, float fullDb);

        // v21 — live spatial-curve tuning (plateau, range, falloff steepness, distance-muffle), pushed
        // from config each tick so it can be dialled in-game without a rebuild.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_emitter_tuning(float minDist, float maxDist, float falloffExp, float muffle,
                                                               float stereoWidth);

        // v23 — live emitter reverb (room reflections) from the managed room probe.
        // v27 adds depth (air-route blocks to the station) + depthAmt (0..1 grading strength): the wet
        // gets quieter/darker/more damped the deeper you are from the source.
        // v28 splits the buses: CARRY (seal-gated next-room cue) vs localWet = LOCAL (your room
        // ringing off the arriving occluded signal — inherits every dry occlusion rule natively).
        // v31: wets/depths are PER-SLOT arrays (MaxEmitterSlots floats) — each voiced station's own
        // carry send + air-route depth; the room-shape params stay shared (the listener's room).
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_emitter_reverb(float[] wets, float[] depths,
                                                               float room, float damp, float rangeMult, float pan,
                                                               float depthAmt, float localWet);

        // v26 — live radio coloration ("through a speaker"): soft-clip drive, static-hiss bed,
        // band-pass voicing blend. All 0..1; all 0 = clean bypass.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_emitter_color(float drive, float staticLvl, float voice,
                                                              float hamDrive, float hamStatic, float hamVoice);

        // v24 — the world-radio's OWN master (game music slider × Radio context volume), separate from
        // the music master so the radio isn't scaled by VolumeModifier or the current music context.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_emitter_master(float vol);

        // v35 — synthesized radio-UI one-shots (tuning static / power clicks); see PlaySfx.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_play_sfx(int kind, float gain);

        // v36 — release a live SFX now (keepalive static ends the moment its reason does).
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_stop_sfx(int kind);

        // v37 — tuning static tone: band edges (Hz) + crackle depth (0 = smooth hiss).
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_static_tone(float lowHz, float highHz, float crackle);

        // v41 — one click kind at a time (0 = power-on toggle, 1 = power-off toggle, 2 = dial
        // detent): tone cap Hz, snap (crack vs shell), length ms, body (upper shell modes), bodyHz
        // (shell pitch), double (second impact), vary (how much each firing differs — shared).
        // v44 — a RECORDED click for a kind (0 = on, 1 = off, 2 = dial), mono float PCM, copied
        // native-side. rateJitter is how far playback speed wanders per firing, Hz either side of
        // the file's own rate.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mix_set_sfx_sample(int which, float[] pcm, int frames, int rate);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_sfx_rate_jitter(float hz);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_click_tone_kind(int which, float hz, float snap,
                                                                float lenMs, float body,
                                                                float bodyHz, float dbl, float vary);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_stop(float fade, int closeWhenIdle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_stop_all_now();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_master(float vol);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mix_set_voice_gain(long id, float trackGain, float rampSeconds);

        // v46 — the receiver: signal 0..1 driving gain, treble and stereo width together, so a
        // station leaves and arrives like a radio instead of just changing level. ratePerSec is a
        // RATE, so a move from half way takes half as long. See cmm_mix.c.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mix_set_voice_receiver(long id, float signal, float ratePerSec,
                                                              float dullness, float mononess,
                                                              float gainPow);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mix_reclaim(long id, float fade);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mix_get_state(long id, out double posSec, out double lenSec, out int active);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double cmm_mix_remaining(long id);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_output_gain(float target, float seconds);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_hold_output(int hold);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_cushion(int deep, int extra);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_request_reinit();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_get_output_stats(out int queued, out int target,
            out int minQueued, out int voices, out int deep, out int underruns, out int busRate);
    }

    internal sealed class CmmMixer
    {
        const int BusRate     = 48000;   // fixed mix bus rate; every voice converts to it natively
        const int BusChannels = 2;       // mix bus is always stereo

        // Stinger overlay tuning: how far the underlying bed ducks while a PlayOnce stinger
        // plays over it (passed as duckOthersTo to Play). The swell-back timing lives native-side.
        internal const float StingerDuckLevel = 0.35f;

        // Headless output for the offline stress harness (tools/CmmSession): when set BEFORE
        // the first GetOrCreate, the mixer starts with a null output device (no audio hardware)
        // and the harness drives playback by draining the ring via PumpOut on its sim clock —
        // so the real producer/voice/retire loop runs at compressed speed. Never set in-game
        // (hence the pragma: the shipped mod only ever reads its default false).
#pragma warning disable CS0649
        internal static bool Headless;
#pragma warning restore CS0649

        // Drain up to `frames` from the headless output ring (no-op unless started headless).
        // The harness calls this each sim tick as the stand-in for the realtime device callback.
        internal int PumpOut(int frames) => NativeMix.cmm_mix_pump_out(frames);

        // ── Singleton ─────────────────────────────────────────────────────────
        // The native mixer is a process-wide singleton; this managed instance is just the
        // facade. Creating it starts the native producer thread (cmm_mix_start, idempotent).
        static readonly object _createLock = new object();
        public static CmmMixer Instance { get; private set; }
        public static CmmMixer GetOrCreate()
        {
            if (Instance == null)
                lock (_createLock)
                    if (Instance == null) Instance = new CmmMixer();
            return Instance;
        }

        // Master volume (game music slider × modifier). The native mixer owns the live value;
        // this property caches it (so reads are free) and forwards writes. cmm_mix_set_master is
        // a lock-free global write, safe to call even before the mixer thread is started.
        static float _masterVolume = 1f;
        public static float MasterVolume
        {
            get { return _masterVolume; }
            set { _masterVolume = value; NativeMix.cmm_mix_set_master(value); }
        }

        // Hold a deep output cushion proactively while the world is loading / on the menu (its
        // falling edge snap-flushes the ring tight — see cmm_mix.c). Lock-free global write.
        static bool _deepBuffer;
        internal static bool DeepBuffer
        {
            get { return _deepBuffer; }
            set { _deepBuffer = value; NativeMix.cmm_mix_set_cushion(value ? 1 : 0, 0); }
        }

        CmmMixer()
        {
            // Tight (~200ms) and deep (up to MaxRingMs) cushion depths, in frames, from config.
            int tight = Math.Max(1, BusRate * Math.Max(50, CMMConfig.NativeOutputRingMs) / 1000);
            int max   = Math.Max(tight,
                                 BusRate * Math.Max(CMMConfig.NativeOutputRingMs, CMMConfig.NativeOutputMaxRingMs) / 1000);
            int err;
            int rc = NativeMix.cmm_mix_start_ex(BusRate, BusChannels, tight, max, Headless ? 1 : 0, out err);
            if (rc != 0)
                Log.Warning($"[CMM] Native mixer start failed (rc={rc}, err={err}) — custom audio will not play.");
            else
            {
                // The native g_lock is a CRITICAL_SECTION created INSIDE cmm_mix_start. Entering
                // it before that is undefined behaviour, and on Windows it faults inside ntdll
                // rather than throwing anything managed. Nothing that locks may be called until
                // this is set. See EmitterRingStat.
                _nativeStarted = true;
            }
            if (rc == 0)
                Log.Out($"[CMM] Native mixer started{(Headless ? " (headless)" : "")}: {BusRate}Hz {BusChannels}ch, " +
                        $"ring {tight * 1000 / BusRate}–{max * 1000 / BusRate}ms (adaptive).");

            // cmm_mix_start resets master to 1; re-apply anything the control layer set before
            // the mixer existed (VolumeSyncLoop / a DeepBuffer set can precede the first Play).
            NativeMix.cmm_mix_set_master(_masterVolume);
            NativeMix.cmm_mix_set_cushion(_deepBuffer ? 1 : 0, 0);
        }

        // ── Public control surface (main / threadpool threads) ──────────────────

        // Install a new track: everything already playing fades out over the same window while
        // this fades in (a crossfade). `dec`+`kind` name a decoder the caller opened via
        // AudioDecoder.OpenNativeHandle (srcRate/srcChannels/totalFrames describe it); the mixer
        // TAKES OWNERSHIP of `dec` (closes it when the voice retires, or immediately on failure).
        // Returns a voice id (0 on failure) to poll via TryGetState / RemainingSeconds. loop
        // restarts at EOF; startFrame seeks the source before play (resume); trackGain is the
        // per-track loudness gain. duckOthersTo < 0: normal crossfade (others fade fully out);
        // duckOthersTo >= 0: a PlayOnce STINGER overlay (others DUCK to that level and swell back
        // when it ends). snapTight=true: a CONTEXT change — flush a deep cushion so the new context
        // is heard promptly. snapTight=false: a same-context track switch (Next/Prev) — do NOT
        // flush (flushing would teleport the outgoing track being crossfaded).
        public long Play(IntPtr dec, int kind, int srcRate, int srcChannels, long totalFrames,
                         float fadeSeconds, bool loop, long startFrame = 0, float trackGain = 1f,
                         float duckOthersTo = -1f, bool snapTight = true)
        {
            if (dec == IntPtr.Zero) return 0;
            return NativeMix.cmm_mix_add(dec, kind, srcRate, srcChannels, totalFrames, fadeSeconds,
                                         loop ? 1 : 0, startFrame, trackGain, duckOthersTo, snapTight ? 1 : 0);
        }

        // Playhead + length of a voice, in seconds. active=false (and false return) when the id
        // is unknown — i.e. it finished and was retired, or never existed.
        // Max simultaneously-voiced stations (native render slots). MUST match the native
        // CMM_MAX_EMIT_VOICES — the per-slot reverb arrays are exactly this long.
        internal const int MaxEmitterSlots = 3;

        // Install an EMITTER voice: an independent positional layer that plays ALONGSIDE the music
        // voices instead of crossfading them (a diegetic world speaker keeps playing while the
        // soundtrack changes underneath). `dec`+`kind` name a decoder the caller opened via
        // AudioDecoder.OpenNativeHandle; the mixer TAKES OWNERSHIP (closes it on retire or failure).
        // loop restarts at EOF; trackGain is the per-track loudness gain; slot pins the voice to a
        // station render slot (0..MaxEmitterSlots-1 — the emitter layer keeps one station per slot).
        // Returns an emitter voice id (0 on failure) to stop via RemoveEmitter.
        public long PlayEmitter(IntPtr dec, int kind, int srcRate, int srcChannels, long totalFrames,
                                float fadeInSeconds, bool loop, float posX, float posY, float posZ,
                                float minDist, float maxDist, float trackGain = 1f, long startFrame = 0,
                                int slot = 0)
        {
            if (dec == IntPtr.Zero) return 0;
            return NativeMix.cmm_mix_add_emitter(dec, kind, srcRate, srcChannels, totalFrames,
                                                 fadeInSeconds, loop ? 1 : 0, trackGain,
                                                 posX, posY, posZ, minDist, maxDist, startFrame, slot);
        }

        // Fade an emitter voice out (over fadeSeconds; 0 snaps) and retire it. No-op if it's gone.
        public void RemoveEmitter(long id, float fadeSeconds)
        {
            NativeMix.cmm_mix_remove_emitter(id, fadeSeconds);
        }

        // Reset the emitter layer's acoustic render state (reverb tails, LPF/pan/mix slews, per-slot
        // smooth state). Call BEFORE a station voice that starts FRESH (not a track advance) so a
        // destroyed radio's still-ringing reverb / stale slews never bleed under the replacement.
        public void ResetEmitterAcoustics()
        {
            NativeMix.cmm_mix_emitter_reset_acoustics();
        }

        // Flush one station render slot (queued dry + LPF/color/send state). Call before starting a
        // voice on a slot whose previous occupant was a DIFFERENT station; never on a same-station
        // track advance (the ring residual there is the outgoing track's tail — wanted continuity).
        public void FlushEmitterSlot(int slot)
        {
            NativeMix.cmm_mix_emitter_flush_slot(slot);
        }

        // Push the listener basis (world position + forward + right unit vectors, straight from the
        // camera transform) for emitter spatialization. Lock-free; call every frame emitters may be
        // audible. No-op cost when no emitter voice exists (the producer just reads unused globals).
        public void SetListener(Vector3 pos, Vector3 forward, Vector3 right)
        {
            NativeMix.cmm_mix_set_listener(pos.x, pos.y, pos.z, forward.x, forward.y, forward.z,
                                           right.x, right.y, right.z);
        }

        // Move a live emitter voice to a new world position (nearest speaker changed). No-op if gone.
        public void SetEmitterPos(long id, Vector3 pos)
        {
            NativeMix.cmm_mix_set_emitter_pos(id, pos.x, pos.y, pos.z);
        }

        // Set the FULL set of speaker positions playing this station: `xyz` holds `count` interleaved
        // x,y,z triples and `gains` holds `count` per-speaker occlusion gains (1=clear, <1=muffled by
        // walls; null = all clear). `flags` holds `count` per-speaker render flags (v34: bit0 = ham
        // class — that point gets the HAM coloration; null = all speaker-class). Caller reuses the
        // buffers to avoid GC. All heard at once from the one decode. count 0 silences the voice's
        // spatial output until the next update.
        // True once cmm_mix_start has actually run, which is the moment the native g_lock
        // becomes a valid CRITICAL_SECTION. Volatile: set on whichever thread first creates
        // the mixer, read from the frame loop.
        static volatile bool _nativeStarted;
        internal static bool NativeStarted => _nativeStarted;

        // Per-slot ring health, for the 1 Hz emitter line. 0 = frames queued, 1 = frames lost to
        // starvation, 2 = times re-cushioned. Returns -1 for a slot with no ring.
        //
        // MUST NOT be called before the mixer has started. cmm_mix_emitter_ring_stat takes
        // g_lock, and g_lock is a CRITICAL_SECTION initialised inside cmm_mix_start -- entering
        // it beforehand is undefined behaviour that faults inside ntdll. The try/catch below is
        // no protection at all against that: an access violation in native code is not a
        // managed exception, so nothing catches it and the process dies.
        //
        // This is exactly how the game died on a spawn where the mixer had never started: the
        // emitter layer's 1 Hz diagnostic line calls this for all 8 slots regardless of whether
        // anything is playing, so a session that only ever used vanilla audio still reached it.
        public static int EmitterRingStat(int slot, int what)
        {
            if (!_nativeStarted) return -1;      // no mixer, therefore no rings, and no lock to take
            try { return NativeMix.cmm_mix_emitter_ring_stat(slot, what); }
            catch { return -1; }
        }

        public void SetEmitterPoints(long id, float[] xyz, float[] gains, int[] flags, int count)
        {
            NativeMix.cmm_mix_set_emitter_points(id, xyz, gains, flags, count);
        }

        // Push the live spatial-curve tuning (full-volume plateau radius, audible range, falloff
        // steepness, distance-muffle strength). Applies to the shared station; safe to call every tick.
        public void SetEmitterTuning(float minDist, float maxDist, float falloffExp, float muffle, float stereoWidth)
        {
            NativeMix.cmm_mix_set_emitter_tuning(minDist, maxDist, falloffExp, muffle, stereoWidth);
        }

        // v42: tone from occlusion instead of virtual distance. Follows the acoustic model, since it
        // is that model's output the old rule mis-reads (see cmm_out.c).
        public void SetEmitterOcclTone(bool enable, float fullDb)
        {
            NativeMix.cmm_mix_set_emitter_occl_tone(enable ? 1 : 0, fullDb);
        }

        // Push the live emitter reverb (room reflections) from the room probe. Every tick.
        // wets/depths = per-SLOT arrays (MaxEmitterSlots long; caller reuses the buffers): each voiced
        // station's own seal-gated CARRY send level + air-route depth. room/damp/pan/localWet = the
        // listener's room (shared); depthAmt = how much depth grades the carry (0 = flat).
        public void SetEmitterReverb(float[] wets, float[] depths, float room, float damp, float rangeMult, float pan, float depthAmt, float localWet)
        {
            NativeMix.cmm_mix_set_emitter_reverb(wets, depths, room, damp, rangeMult, pan, depthAmt, localWet);
        }

        // Push the live radio coloration (soft-clip drive, static bed, band-pass voicing) — the
        // speaker set and the ham set (per-point flags pick which set a speaker gets). Every tick.
        public void SetEmitterColor(float drive, float staticLvl, float voice,
                                    float hamDrive, float hamStatic, float hamVoice)
        {
            NativeMix.cmm_mix_set_emitter_color(drive, staticLvl, voice, hamDrive, hamStatic, hamVoice);
        }

        // Push the world-radio's own master (game music slider × Radio context volume). Every tick.
        public void SetEmitterMaster(float vol)
        {
            NativeMix.cmm_mix_set_emitter_master(vol);
        }

        // Fire one synthesized radio-UI one-shot (v35). Rides the music master like the radio
        // voice; never part of the crossfade/stinger machinery; ensures the device (a power-on
        // click may precede the radio voice's own add).
        public const int SfxTuningStatic = 1;
        public const int SfxClickOn      = 2;
        public const int SfxClickOff     = 3;
        // The dial detent is its OWN sound, not the power-on click reused: a radio switch is a
        // toggle (lever over centre, then the stop — two impacts, low heavy shell) and a tuning
        // knob is a detent (a ball into a groove — one bright tick, small tight shell, gone in
        // milliseconds). Reusing one for the other is why the dial did not read as a dial.
        public const int SfxClickDial    = 4;
        public void PlaySfx(int kind, float gain)
        {
            NativeMix.cmm_mix_play_sfx(kind, gain);
        }

        // Release a live SFX now (v36). The tuning static is a KEEPALIVE: PlaySfx pings sustain
        // one seamless hash, and this ends it (~90ms release) the moment the station voice lands.
        public void StopSfx(int kind)
        {
            NativeMix.cmm_mix_stop_sfx(kind);
        }

        // Push the tuning static's tone (v37): low cut / top cap in Hz + crackle depth 0..1.
        // Captured at every ping, so a hot-reloaded settings.xml edit is heard mid-hash.
        public void SetStaticTone(float lowHz, float highHz, float crackle)
        {
            NativeMix.cmm_mix_set_static_tone(lowHz, highHz, crackle);
        }

        // Push the power-click tones (v38/v39), both kinds at once: tone cap Hz (bright tick ↔
        // dull thud), snap 0..1 (bright ping vs low thump), length ms, body 0..1 (shell-knock
        // resonator — the dial's "tok"), bodyHz (its ring pitch), double 0..1 (second detent
        // micro-impact ~12ms later). Captured when a click fires.
        // which: 0 = power-on toggle, 1 = power-off toggle, 2 = dial detent. vary 0..1 is shared
        // by all three — how much each firing differs from the last, so a run of detents sounds
        // like a mechanism instead of one waveform played repeatedly. Captured when a click fires,
        // so a hot-reloaded radio.xml edit is heard on the very next dial.
        // Hand a kind its recorded sound; pcm == null reverts it to the synthesized click, which
        // stays the fallback so a missing file is a quieter mod rather than a broken one.
        public bool SetSfxSample(int which, float[] pcm, int rate)
        {
            return NativeMix.cmm_mix_set_sfx_sample(which, pcm, pcm != null ? pcm.Length : 0, rate) != 0;
        }

        public void SetSfxRateJitter(float hz) => NativeMix.cmm_mix_set_sfx_rate_jitter(hz);

        public void SetClickToneKind(int which, float hz, float snap, float lenMs,
                                     float body, float bodyHz, float dbl, float vary)
        {
            NativeMix.cmm_mix_set_click_tone_kind(which, hz, snap, lenMs, body, bodyHz, dbl, vary);
        }

        public bool TryGetState(long id, out double positionSec, out double lengthSec, out bool active)
        {
            int act;
            int r = NativeMix.cmm_mix_get_state(id, out positionSec, out lengthSec, out act);
            active = act != 0;
            return r != 0;
        }

        // Seconds left in a voice: -1 if it's gone, +Inf if its length is unknown, else the
        // remaining play time. Drives next-track scheduling.
        public double RemainingSeconds(long id)
        {
            return NativeMix.cmm_mix_remaining(id);
        }

        // Fade everything out (per-voice). closeWhenIdle=true tears the device down once silent;
        // pass false to keep it OPEN (e.g. a menu→game output fade that hands off to the Spawn
        // stinger on the same device).
        public void Stop(float fadeSeconds, bool closeWhenIdle = true)
        {
            NativeMix.cmm_mix_stop(fadeSeconds, closeWhenIdle ? 1 : 0);
        }

        // Synchronously retire + DISCARD every voice now — no per-voice fade — and flush the
        // queued output. Used by the graceful menu↔game teardown AFTER its audible CONSUMER-side
        // fade (SetOutputGain) has run, to actually remove the (still-at-full-gain) voices so they
        // can't audibly return on the next Play.
        public void StopAllNow()
        {
            NativeMix.cmm_mix_stop_all_now();
        }

        // Update a live voice's per-track gain (loudness × user trim) in place — no restart — so a
        // trim keybind is heard immediately and a loudness measurement that lands AFTER the track
        // started settles to its normalized level. rampSeconds > 0 ramps (declick); <= 0 snaps.
        // Returns false when the voice is gone.
        public bool SetVoiceTrackGain(long id, float trackGain, float rampSeconds = 0f)
        {
            return NativeMix.cmm_mix_set_voice_gain(id, trackGain, rampSeconds) != 0;
        }

        // Aim a voice's receiver at `signal` (0 = gone, 1 = fully tuned). Idempotent, so it can be
        // pushed every tick. Returns false when the voice has already retired, which is the normal
        // race when a station is abandoned mid-load.
        public bool SetVoiceReceiver(long id, float signal, float ratePerSec, float dullness,
                                     float mononess, float gainPow)
        {
            return NativeMix.cmm_mix_set_voice_receiver(id, signal, ratePerSec, dullness, mononess, gainPow) != 0;
        }

        // Ramp the CONSUMER-side output gain toward `target` over `seconds` (0 = immediate). Used
        // for menu↔game transition fades: applied in the native audio callback, so the fade is
        // heard in real output time no matter how deep the producer cushion is.
        public void SetOutputGain(float target, float seconds)
        {
            NativeMix.cmm_mix_set_output_gain(target, seconds);
        }

        // While held, a voice add no longer revives the output gain (v33). The revive exists
        // for menu-transition fades that park the device silent; the SP pause duck uses the
        // SAME gain, so a mid-pause track advance / radio / emitter start used to snap the
        // whole device back to full through the pause screen. Hold on the pause edge, release
        // on unpause AFTER the gain target is back at 1 (then adds have nothing to revive).
        public void HoldOutputGain(bool hold)
        {
            NativeMix.cmm_mix_hold_output(hold ? 1 : 0);
        }

        // Reverse-fade reclaim: a voice we recently started fading out (a crossfade we're
        // interrupting by re-entering its context) is still audible — ramp it back to full instead
        // of restarting its track, and fade every OTHER voice out. Returns false when the voice is
        // gone/inaudible, so the caller falls back to a normal Play.
        public bool TryReclaim(long id, float fadeSeconds)
        {
            return NativeMix.cmm_mix_reclaim(id, fadeSeconds) != 0;
        }

        // Ask the native mixer thread to reopen its output device (default endpoint changed). The
        // swap runs on the producer thread under its own lock; voices keep playing across it.
        public void RequestDeviceReinit()
        {
            NativeMix.cmm_mix_request_reinit();
        }

        // Live output-ring health for the debug HUD (frames + the bus rate to convert to ms).
        // minQueued is the lowest depth since the LAST call — reset on read, so poll it on a
        // fixed cadence to get a rolling min. Only call when the mixer exists (Instance != null),
        // since the native side locks the mixer mutex that cmm_mix_start creates.
        public void GetOutputStats(out int queuedFrames, out int targetFrames, out int minQueuedFrames,
                                   out int voices, out int deep, out int underruns, out int busRate)
        {
            NativeMix.cmm_mix_get_output_stats(out queuedFrames, out targetFrames, out minQueuedFrames,
                                               out voices, out deep, out underruns, out busRate);
        }
    }
}
