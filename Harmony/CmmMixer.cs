using System;
using System.Runtime.InteropServices;

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

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_stop(float fade, int closeWhenIdle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_stop_all_now();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_master(float vol);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mix_set_voice_gain(long id, float trackGain, float rampSeconds);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mix_reclaim(long id, float fade);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mix_get_state(long id, out double posSec, out double lenSec, out int active);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern double cmm_mix_remaining(long id);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mix_set_output_gain(float target, float seconds);

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

        // Ramp the CONSUMER-side output gain toward `target` over `seconds` (0 = immediate). Used
        // for menu↔game transition fades: applied in the native audio callback, so the fade is
        // heard in real output time no matter how deep the producer cushion is.
        public void SetOutputGain(float target, float seconds)
        {
            NativeMix.cmm_mix_set_output_gain(target, seconds);
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
