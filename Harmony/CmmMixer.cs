using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // NATIVE AUDIO OUTPUT  (CmmMixer — miniaudio via CMM_Decoder.dll cmm_out_*)
    // ─────────────────────────────────────────────────────────────────────────
    // CMM's own audio output, fully independent of Unity. The pieces:
    //
    //   NativeOut       — P/Invoke for the cmm_out_* device/ring exports.
    //   NativeConv      — P/Invoke for the cmm_dc_* format converter (resample +
    //                     channel map) used to land every voice on the mix bus.
    //   NativeAudioOut  — thin IDisposable wrapper over one cmm_out handle.
    //   CmmMixer        — a background thread + a list of voices. It produces each
    //                     voice at the fixed 48 kHz / stereo bus format (via that
    //                     voice's converter when its native format differs), applies
    //                     a per-voice gain ramp (this is how crossfades are done) +
    //                     the master volume, sums into a pinned bus buffer, and pushes
    //                     it to the device ring. miniaudio's realtime callback drains
    //                     the ring. So decode→convert→mix→output runs only on threads
    //                     CMM owns; no managed code runs on the audio callback.
    //
    // A FIXED bus (48 kHz stereo, opened once) is what lets tracks of ANY sample
    // rate crossfade — each voice converts to the bus instead of forcing a device
    // reopen on every rate change (the Phase-2 behaviour).
    //
    // This is the sole output path for custom-file contexts (the legacy AudioSource/
    // AudioClip path has been removed); the `cmm native*` console commands drive it
    // manually for testing.

    internal static class NativeOut
    {
        const string Dll = "CMM_Decoder";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_out_open(int sampleRate, int channels, int ringFrames, out int err);

        // const float* data — passed as a base+offset pointer into the mixer's
        // pinned bus buffer, so a partial write can retry the remainder with no copy.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "cmm_out_write")]
        internal static extern int cmm_out_write(IntPtr o, IntPtr data, int frames);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_out_avail(IntPtr o);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_out_queued(IntPtr o);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_out_reset(IntPtr o);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_out_set_gain(IntPtr o, float target, int rampFrames);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_out_reset_gain(IntPtr o, float target);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_out_close(IntPtr o);
    }

    internal static class NativeConv
    {
        const string Dll = "CMM_Decoder";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_dc_open(int inRate, int inChannels, int outRate, int outChannels, out int err);

        // Blittable float[] arrays are pinned by the marshaller for the call.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_dc_process(IntPtr c, float[] inBuf, long inFrames, out long inConsumed,
                                                  float[] outBuf, long outCap, out long outWritten);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_dc_close(IntPtr c);
    }

    internal sealed class NativeAudioOut : IDisposable
    {
        IntPtr _handle;
        public int Channels   { get; }
        public int SampleRate { get; }

        public NativeAudioOut(int sampleRate, int channels, int ringFrames)
        {
            _handle = NativeOut.cmm_out_open(sampleRate, channels, ringFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_out_open failed (err={err})");
            SampleRate = sampleRate;
            Channels   = channels;
        }

        public int AvailFrames  => _handle != IntPtr.Zero ? NativeOut.cmm_out_avail(_handle)  : 0;
        public int QueuedFrames => _handle != IntPtr.Zero ? NativeOut.cmm_out_queued(_handle) : 0;

        public int Write(IntPtr data, int frames) =>
            _handle != IntPtr.Zero ? NativeOut.cmm_out_write(_handle, data, frames) : 0;

        // Discard all queued frames on the next callback — snap the cushion tight.
        public void Reset() { if (_handle != IntPtr.Zero) NativeOut.cmm_out_reset(_handle); }

        // Consumer-side output gain ramp (transition fades). rampFrames<=0 snaps.
        public void SetGain(float target, int rampFrames)
        { if (_handle != IntPtr.Zero) NativeOut.cmm_out_set_gain(_handle, target, rampFrames); }

        // Flush the stale queue AND restore gain atomically (no old-track bleed on restart).
        public void ResetGain(float target)
        { if (_handle != IntPtr.Zero) NativeOut.cmm_out_reset_gain(_handle, target); }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) { NativeOut.cmm_out_close(_handle); _handle = IntPtr.Zero; }
        }
    }

    internal sealed class CmmMixer
    {
        // ── Singleton ─────────────────────────────────────────────────────────
        static readonly object _createLock = new object();
        public static CmmMixer Instance { get; private set; }
        public static CmmMixer GetOrCreate()
        {
            if (Instance == null)
                lock (_createLock)
                    if (Instance == null) Instance = new CmmMixer();
            return Instance;
        }

        // Master volume (game music slider × modifier). Written from the main thread
        // (VolumeSyncLoop), read by the mixer thread — volatile is enough for a float.
        public static volatile float MasterVolume = 1f;

        const int BusRate     = 48000;   // fixed mix bus rate; every voice converts to it
        const int BusChannels = 2;       // mix bus is always stereo
        const int MaxBusFrames = 2048;   // largest block produced per device write
        const int RefillFrames = MaxBusFrames;   // per-iteration input top-up for a converted voice
        // Hard ceiling on simultaneous voices. A crossfade needs 2 (out + in); a few
        // overlapping fades can briefly need 3. Beyond this, rapid re-triggers are piling
        // up faster than fades complete — drop the most-faded extras so layers can never
        // accumulate into audible overlap (defensive against any caller mis-sequencing).
        const int MaxVoices = 4;

        readonly object _lock = new object();
        readonly List<Voice> _voices = new List<Voice>(4);
        long _nextId;   // monotonic voice id; 0 is reserved for "none"

        NativeAudioOut _device;
        bool _closeWhenIdle;
        volatile bool _reinitRequested;   // set by RequestDeviceReinit; serviced by the Pump thread

        // ── Adaptive output ring (v10) ────────────────────────────────────────────
        // The ring is allocated at the MAX depth; the Pump keeps only _targetFillFrames
        // queued. Tight in normal play (low latency / responsive crossfades); grown toward
        // the ceiling when the producer is being starved (game world-gen / other apps) or
        // proactively while DeepBuffer is set (loading / menu); snapped tight again on a
        // context change. Driven from the control layer via DeepBuffer.
        internal static volatile bool DeepBuffer;   // hold a deep cushion proactively (loading/menu)
        const int CalmMs       = 3000;   // no starvation for this long → ease back to the tight target
        const int FlushGraceMs = 750;    // after a flush, ignore the (intentionally) empty ring this long
        int _maxFillFrames, _tightFillFrames, _lowWaterFrames;
        int _targetFillFrames;
        int _lastStarveMs, _flushUntilMs, _lastStarveLogMs;
        bool _prevDeep;

        // Managed mirror of the consumer-side output gain target (the native callback owns the
        // actual ramp). 1 = full. A transition fade ramps it toward 0; the next Play() snaps it
        // back to 1 (and flushes the faded-out queue) so new music is audible. See SetOutputGain.
        float _outputGainTarget = 1f;

        // Bus buffer is pinned for the mixer's lifetime so we can hand the native
        // device a base+offset pointer with no per-write pinning, and so the GC can
        // never move it while the device reads from the ring it was copied into.
        readonly float[] _bus;
        GCHandle _busPin;
        readonly IntPtr _busPtr;
        readonly float[] _voiceStereo;   // one voice's output at bus format (stereo)
        readonly float[] _convTmp;       // converter output scratch for one process() call

        sealed class Voice
        {
            public long  Id;
            public WaveStream      Reader;
            public IFloatPcmSource Src;
            public int   SrcRate;
            public int   SrcChannels;
            public long  TotalFrames;    // source frames (<=0 = unknown, e.g. raw ADTS)
            public long  SourceFramesRead; // source frames consumed from disk so far (≈ playhead)
            public IntPtr Conv;          // cmm_dc handle (Zero when native == bus format)
            public float[] InBuf;        // converter input scratch (native channels); null when no converter
            public int   InFill;         // input frames currently buffered in InBuf (carried across ticks)
            public float Gain;           // current crossfade gain
            public float TargetGain;     // gain we're ramping toward
            public float GainStep;       // per-frame ramp increment (0 = steady)
            public float TrackGain;       // per-track loudness gain (× crossfade gain); has its own ramp below
            public float TrackGainTarget; // loudness gain we're ramping toward (late measurement / trim key)
            public float TrackGainStep;   // per-frame ramp increment for TrackGain (0 = steady)
            public bool  Loop;
            public bool  Ended;          // reader hit EOF and isn't looping
            public bool  IsStinger;      // PlayOnce overlay: when it retires, ducked voices are restored
            public bool  Ducked;         // this voice was DUCKED (not killed) under a stinger overlay
        }

        // Stinger overlay tuning: how far the underlying bed is ducked while a PlayOnce
        // stinger plays over it, and how quickly it swells back when the stinger ends.
        internal const float StingerDuckLevel = 0.35f;
        const float StingerRestoreSeconds = 0.6f;

        CmmMixer()
        {
            _bus         = new float[MaxBusFrames * BusChannels];
            _busPin      = GCHandle.Alloc(_bus, GCHandleType.Pinned);
            _busPtr      = _busPin.AddrOfPinnedObject();
            _voiceStereo = new float[MaxBusFrames * BusChannels];
            _convTmp     = new float[MaxBusFrames * BusChannels];

            var t = new Thread(Pump)
            {
                IsBackground = true,
                Name = "CMM-Mixer",
                Priority = ThreadPriority.AboveNormal,
            };
            t.Start();
        }

        // ── Public control surface (main / threadpool threads) ──────────────────

        // Install a new track: everything already playing fades out over the same
        // window while this fades in (a crossfade). Returns a voice id (0 on failure)
        // the caller polls via TryGetState / RemainingSeconds. loop=true restarts at
        // EOF; startFrame seeks the source before play (resume); trackGain is the
        // per-track loudness gain applied on top of the crossfade. Takes ownership of
        // `reader` (disposed when the voice retires).
        // duckOthersTo < 0 (default): existing voices fade fully OUT (a normal crossfade).
        // duckOthersTo >= 0: this is a PlayOnce STINGER overlay — existing voices DUCK to
        // duckOthersTo (staying audible) and the new voice is flagged IsStinger so they're
        // restored to full when it retires (see RestoreDuckedLocked in Pump). That keeps the
        // underlying bed playing under the stinger instead of cutting it.
        // snapTight=true: a CONTEXT change — if the cushion grew deep, flush it so the new
        // context is heard promptly (the outgoing context is discarded anyway). snapTight=false:
        // a same-context track switch (Next/Prev) — do NOT flush; flushing would teleport the
        // outgoing track you're crossfading away from to the decode-ahead position (a jump +
        // volume swing). A track switch just crossfades; any deep cushion drains naturally.
        public long Play(WaveStream reader, float fadeSeconds, bool loop, long startFrame = 0, float trackGain = 1f, float duckOthersTo = -1f, bool snapTight = true)
        {
            var src = reader as IFloatPcmSource;
            if (src == null) { reader?.Dispose(); return 0; }

            int rate = reader.WaveFormat.SampleRate;
            int ch   = reader.WaveFormat.Channels;
            int blockAlign = reader.WaveFormat.BlockAlign;
            EnsureDevice();

            // A converter is needed unless the track is already exactly bus format.
            IntPtr conv  = IntPtr.Zero;
            float[] inBuf = null;
            if (rate != BusRate || ch != BusChannels)
            {
                conv = NativeConv.cmm_dc_open(rate, ch, BusRate, BusChannels, out int derr);
                if (conv == IntPtr.Zero)
                {
                    Log.Warning($"[CMM] native: converter open failed (err={derr}) for {rate}Hz/{ch}ch");
                    reader.Dispose();
                    return 0;
                }
                inBuf = new float[RefillFrames * ch];
            }

            // Resume: seek the source before installing (runs on the caller's
            // background thread, off the audio path). Plain Position set — no Unity
            // SetPosition intercept needed on this path.
            long total = blockAlign > 0 && reader.Length > 0 ? reader.Length / blockAlign : -1;
            if (startFrame > 0 && blockAlign > 0)
            {
                if (total > 0 && startFrame >= total) startFrame = 0;
                try
                {
                    reader.Position = startFrame * blockAlign;
                    // Trust where the reader ACTUALLY landed, not what we asked for: the
                    // playhead bookkeeping (SourceFramesRead) must reflect the decoder, or a
                    // seek that silently no-ops makes the resume-save look correct while the
                    // audio plays from 0. A mismatch means the native seek failed.
                    long landed = reader.Position / blockAlign;
                    if (landed != startFrame)
                        Log.Warning($"[CMM] native: resume seek wanted frame {startFrame} but landed {landed} — playing from there.");
                    startFrame = landed;
                }
                catch (Exception ex) { Log.Warning($"[CMM] native: resume seek threw ({ex.Message}); from 0."); startFrame = 0; }
            }

            long id;
            lock (_lock)
            {
                id = ++_nextId;
                _closeWhenIdle = false;

                // A transition fade left the output gain ramping toward silence; this is new music
                // that must be audible. Discard the faded-out queue and restore the output to full —
                // ATOMICALLY (cmm_out_reset_gain): the callback drains the stale queue THEN snaps the
                // gain, so a callback mid-drain can't emit those stale full-volume frames at the
                // restored gain (the "hint of the previous track bleeds through" on restart).
                float reviveFrom = (_device != null && _outputGainTarget != 1f) ? _outputGainTarget : 1f;
                if (reviveFrom != 1f)
                {
                    _device.ResetGain(1f);
                    _outputGainTarget = 1f;
                    _flushUntilMs = Environment.TickCount + FlushGraceMs;
                }
                bool stinger = duckOthersTo >= 0f;
                foreach (var ev in _voices)        // fade OUT (or DUCK, for a stinger) whatever is audible
                {
                    // If we just snapped the output gain back to full while a menu→game fade was
                    // still mid-ramp (a fast world load interrupting the 5s fade), the still-audible
                    // outgoing voices would JUMP from (outputGain × voiceGain) up to (1 × voiceGain)
                    // — an audible swell, then a fast drop. Fold the output attenuation we just
                    // removed into each voice's own gain so its audible level is CONTINUOUS across
                    // the revive; it then fades out from there over fadeSeconds with no swell.
                    if (reviveFrom != 1f) ev.Gain *= reviveFrom;
                    float to = stinger ? duckOthersTo : 0f;
                    ev.TargetGain = to;
                    ev.GainStep   = ComputeStep(ev.Gain, to, fadeSeconds);
                    ev.Ducked     = stinger && to > 0f;   // remember to restore it when the stinger ends
                }
                var v = new Voice
                {
                    Id = id, Reader = reader, Src = src, SrcRate = rate, SrcChannels = ch,
                    TotalFrames = total, SourceFramesRead = startFrame,
                    Conv = conv, InBuf = inBuf, Loop = loop, TargetGain = 1f,
                    TrackGain = trackGain > 0f ? trackGain : 1f,
                    TrackGainTarget = trackGain > 0f ? trackGain : 1f,
                    IsStinger = stinger,
                };
                if (fadeSeconds <= 0f) { v.Gain = 1f; v.GainStep = 0f; }
                else                   { v.Gain = 0f; v.GainStep = ComputeStep(0f, 1f, fadeSeconds); }
                _voices.Add(v);

                // Bound overlap (defensive): never accumulate beyond MaxVoices — drop the
                // most-faded extras instantly so churn/mis-sequencing can't pile up audible
                // layers (the incoming voice `id` is always kept).
                while (_voices.Count > MaxVoices)
                {
                    int q = -1; float low = float.MaxValue;
                    for (int i = 0; i < _voices.Count; i++)
                        if (_voices[i].Id != id && _voices[i].Gain < low) { low = _voices[i].Gain; q = i; }
                    if (q < 0) break;
                    RetireVoice(_voices[q]);
                    _voices.RemoveAt(q);
                }

                // Context change (snapTight): if a deep cushion is queued — whether it grew reactively
                // after a CPU storm OR we're deliberately holding it deep for a load — snap it tight so
                // THIS new context is heard promptly instead of behind seconds of queued outgoing audio.
                // The discarded queue is the outgoing context this voice crossfades out anyway, so the
                // discard is masked by the fade; the deep cushion is rebuilt behind the new voice.
                // NOT for a same-context track switch (snapTight=false): there the outgoing track is
                // crossfaded (the listener is following it), and flushing would teleport it forward to
                // the decode-ahead position — the Prev/Next "jumps to a different part + volume swell".
                if (snapTight && _device != null && _device.QueuedFrames > _tightFillFrames * 2)
                    FlushTightLocked();
            }
            return id;
        }

        // Playhead + length of a voice, in seconds. active=false (and false return)
        // when the id is unknown — i.e. it finished and was retired, or never existed.
        public bool TryGetState(long id, out double positionSec, out double lengthSec, out bool active)
        {
            positionSec = 0; lengthSec = 0; active = false;
            if (id == 0) return false;
            lock (_lock)
            {
                foreach (var v in _voices)
                {
                    if (v.Id != id) continue;
                    active = true;
                    if (v.SrcRate > 0) positionSec = (double)v.SourceFramesRead / v.SrcRate;
                    if (v.SrcRate > 0 && v.TotalFrames > 0) lengthSec = (double)v.TotalFrames / v.SrcRate;
                    return true;
                }
            }
            return false;
        }

        // Seconds left in a voice: -1 if it's gone (ended/retired), +Inf if its length
        // is unknown (so it's never treated as "near end" by length — rely on EOF),
        // else the remaining play time. Drives next-track scheduling in 3c.
        public double RemainingSeconds(long id)
        {
            if (id == 0) return -1;
            lock (_lock)
            {
                foreach (var v in _voices)
                {
                    if (v.Id != id) continue;
                    if (v.SrcRate <= 0 || v.TotalFrames <= 0) return double.PositiveInfinity;
                    return Math.Max(0.0, (double)(v.TotalFrames - v.SourceFramesRead) / v.SrcRate);
                }
            }
            return -1;
        }

        // Fade everything out (per-voice). closeWhenIdle=true closes the device once silent;
        // pass false to keep the device OPEN (e.g. a menu→game output fade that hands off to the
        // Spawn stinger on the same device — closing it mid-cushion would cut the consumer fade).
        public void Stop(float fadeSeconds, bool closeWhenIdle = true)
        {
            lock (_lock)
            {
                foreach (var v in _voices)
                {
                    v.TargetGain = 0f;
                    v.GainStep   = ComputeStep(v.Gain, 0f, fadeSeconds);
                }
                _closeWhenIdle = closeWhenIdle;
            }
        }

        // Synchronously retire and DISCARD every voice right now — no per-voice fade — and flush
        // the queued output frames. Used by the graceful menu↔game teardown AFTER its audible fade
        // has run CONSUMER-side (SetOutputGain): that fade silences the output but leaves the per-
        // voice gains at full, so the voices are still live. If they survive into the next Play
        // (the menu track), it restores the output gain to 1 and its fade-out foreach re-ramps those
        // still-at-full voices from the top — the just-faded game track audibly RETURNS and fades a
        // second time (the reported "old music bleeds back" on quit-to-menu). The frame flush only
        // ever discards stale data, never the live voices; this is what actually removes them. Caller
        // must have already faded the output, since this is an instant cut.
        public void StopAllNow()
        {
            lock (_lock)
            {
                foreach (var v in _voices) RetireVoice(v);
                _voices.Clear();
                _device?.Reset();   // drop the now-orphaned queued frames too
            }
        }

        // Update a live voice's per-track gain (loudness × user trim) in place — no restart — so the
        // per-track volume keybind is heard immediately on the currently-playing native track, and a
        // loudness measurement that lands AFTER the track started (it played at neutral 1.0) settles to
        // its normalized level without a restart. The gain was baked into the Voice at Play time; without
        // this, changing it only updated the STORED value and the next track, never the one you're hearing.
        // rampSeconds > 0 ramps TrackGain over that wall-clock time (declick / smooth correction) instead
        // of snapping — the correction from un-normalized 1.0 down to a normalized level can be large, so a
        // snap would click. rampSeconds <= 0 snaps. Returns false when the voice is gone. Takes effect from
        // the next rendered block (RenderVoiceAdd ramps and reads TrackGain).
        public bool SetVoiceTrackGain(long id, float trackGain, float rampSeconds = 0f)
        {
            if (id == 0) return false;
            float g = trackGain > 0f ? trackGain : 0f;
            lock (_lock)
            {
                foreach (var v in _voices)
                    if (v.Id == id)
                    {
                        v.TrackGainTarget = g;
                        if (rampSeconds <= 0f) { v.TrackGain = g; v.TrackGainStep = 0f; }
                        else                   v.TrackGainStep = ComputeStep(v.TrackGain, g, rampSeconds);
                        return true;
                    }
            }
            return false;
        }

        // Ramp the CONSUMER-side output gain toward `target` over `seconds` (0 = immediate). Used
        // for menu↔game transition fades: applied in the native audio callback, so the fade is heard
        // in real output time no matter how deep the producer cushion is or how starved the Pump gets
        // (the producer-side per-voice ramp can't — it sits buried behind the queued cushion).
        public void SetOutputGain(float target, float seconds)
        {
            lock (_lock)
            {
                _outputGainTarget = target;
                _device?.SetGain(target, seconds > 0f ? (int)(seconds * BusRate) : 0);
            }
        }

        // Reverse-fade reclaim: a voice we recently started fading out (a crossfade we're
        // interrupting by re-entering its context) is still audible — ramp it back to full
        // instead of restarting its track from the top, and fade every OTHER voice out (the
        // track we were fading in). Returns false when the voice is gone/inaudible, so the
        // caller falls back to a normal Play. Also un-ducks the voice (it may be the bed a
        // stinger ducked). Drives the native equivalent of PlayFromCache's reverse-fade reclaim.
        public bool TryReclaim(long id, float fadeSeconds)
        {
            if (id == 0) return false;
            lock (_lock)
            {
                Voice keep = null;
                foreach (var v in _voices) if (v.Id == id) { keep = v; break; }
                if (keep == null || keep.Ended || keep.Gain <= 0.0001f) return false;
                foreach (var v in _voices)
                {
                    bool isKeep = v == keep;
                    float to = isKeep ? 1f : 0f;
                    v.TargetGain = to;
                    v.GainStep   = ComputeStep(v.Gain, to, fadeSeconds);
                    if (isKeep) v.Ducked = false;
                }
                return true;
            }
        }

        // Ask the mixer thread to reopen its output device (default endpoint changed). The
        // swap runs on the Pump thread under _lock — NOT here — so it can't race the audio
        // callback / Write on a device being disposed. Voices keep playing across the swap.
        public void RequestDeviceReinit() { _reinitRequested = true; }

        // ── Device lifecycle ────────────────────────────────────────────────────

        void EnsureDevice()
        {
            lock (_lock)
            {
                if (_device != null) return;
                OpenDeviceLocked();
                Log.Out($"[CMM] Native mixer device opened: {BusRate}Hz {BusChannels}ch, " +
                        $"ring {_tightFillFrames * 1000 / BusRate}–{_maxFillFrames * 1000 / BusRate}ms (adaptive)");
            }
        }

        // Open (or reopen) the output device on the current OS default endpoint. Caller holds _lock.
        // The ring is allocated at the MAX adaptive depth; the live cushion floats between the
        // tight target and that ceiling (see ComputeFillTargetLocked).
        void OpenDeviceLocked()
        {
            _tightFillFrames = Math.Max(1, BusRate * Math.Max(50, CMMConfig.NativeOutputRingMs) / 1000);
            _maxFillFrames   = Math.Max(_tightFillFrames,
                                        BusRate * Math.Max(CMMConfig.NativeOutputRingMs, CMMConfig.NativeOutputMaxRingMs) / 1000);
            _lowWaterFrames  = Math.Max(BusRate / 50, _tightFillFrames / 2);   // ≥20ms; danger threshold
            _targetFillFrames = _tightFillFrames;
            _flushUntilMs    = Environment.TickCount + FlushGraceMs;   // don't read the initial empty ring as starvation
            // Seed the DeepBuffer falling-edge detector to the CURRENT state. Without this it
            // keeps the value from the PREVIOUS device (e.g. true from a deep load), so the first
            // Pump tick on a fresh device after entering the world sees prevDeep=true & deep=false
            // and fires a spurious flush — clipping the START of the first track (the Spawn stinger).
            _prevDeep = DeepBuffer;
            _outputGainTarget = 1f;   // a fresh native device starts at full gain
            _device = new NativeAudioOut(BusRate, BusChannels, _maxFillFrames);
        }

        // Decide how many frames to keep queued this tick. Caller holds _lock; runs on the Pump.
        // DeepBuffer (loading/menu) or a starvation dip grows the target toward the ceiling so
        // the Pump banks cushion whenever it's scheduled; sustained calm eases it back to tight.
        // A post-flush grace window keeps the (intentionally) emptied ring from looking like
        // starvation. queued = frames currently queued for playback.
        int ComputeFillTargetLocked(int queued)
        {
            int now = Environment.TickCount;
            if (DeepBuffer)
                _targetFillFrames = _maxFillFrames;                         // hold deep (load/menu)
            else if (now - _flushUntilMs < 0)
                _targetFillFrames = _tightFillFrames;                       // just flushed — refill tight, ignore the empty ring
            else if (queued < _lowWaterFrames)
            {
                _targetFillFrames = _maxFillFrames;                         // starving — bank cushion to the ceiling
                _lastStarveMs = now;
                if (now - _lastStarveLogMs > 1000)
                {
                    _lastStarveLogMs = now;
                    Log.Warning($"[CMM] native: output cushion low ({queued * 1000 / BusRate}ms) — " +
                                $"growing toward {_maxFillFrames * 1000 / BusRate}ms (CPU contention).");
                }
            }
            else if (now - _lastStarveMs > CalmMs)
                _targetFillFrames = _tightFillFrames;                       // calm — ease back (drains naturally)
            return _targetFillFrames;
        }

        // Snap the output cushion tight: discard the queued (outgoing-context) audio so a new
        // context is heard promptly after the buffer grew deep. Caller holds _lock. Guarded by
        // a grace window so the Pump doesn't immediately re-read the empty ring as starvation.
        void FlushTightLocked()
        {
            if (_device == null) return;
            _device.Reset();
            _targetFillFrames = _tightFillFrames;
            _flushUntilMs = Environment.TickCount + FlushGraceMs;
        }

        // Restore voices a stinger overlay ducked, back to full. Caller holds _lock; invoked
        // when the stinger voice retires so the bed swells back without a context restart.
        void RestoreDuckedLocked()
        {
            foreach (var v in _voices)
            {
                if (!v.Ducked) continue;
                v.Ducked     = false;
                v.TargetGain = 1f;
                v.GainStep   = ComputeStep(v.Gain, 1f, StingerRestoreSeconds);
            }
        }

        void TeardownDeviceLocked()
        {
            foreach (var v in _voices) RetireVoice(v);
            _voices.Clear();
            _device?.Dispose();
            _device = null;
        }

        static void RetireVoice(Voice v)
        {
            v.Reader?.Dispose();
            if (v.Conv != IntPtr.Zero) NativeConv.cmm_dc_close(v.Conv);
            v.Conv = IntPtr.Zero;
        }

        // ── Mixer thread ──────────────────────────────────────────────────────

        void Pump()
        {
            while (true)
            {
                NativeAudioOut dev;
                int free = 0, queued = 0, target = 0;
                lock (_lock)
                {
                    // Service a device reinit here (on the Pump thread) so disposing the old
                    // device can't race a concurrent AvailFrames/Write below. Voices survive.
                    if (_reinitRequested)
                    {
                        _reinitRequested = false;
                        if (_device != null)
                        {
                            _device.Dispose();
                            OpenDeviceLocked();
                            Log.Out("[CMM] Native mixer device reinitialized (audio config changed).");
                        }
                    }
                    dev = _device;
                    if (dev != null)
                    {
                        // Leaving the deep-buffer window (load finished) → snap the cushion tight so
                        // in-game context changes aren't queued behind a deep load cushion — but ONLY
                        // if the ring is actually deep right now. Guarding on QueuedFrames stops a
                        // spurious flush on a freshly-opened device (e.g. the Spawn stinger's device,
                        // opened just as _gameIsLoading clears): without the guard the first tick sees
                        // the falling edge and flushes the stinger's opening frames → it starts offset.
                        bool deep = DeepBuffer;
                        if (_prevDeep && !deep && dev.QueuedFrames > _tightFillFrames * 2) FlushTightLocked();
                        _prevDeep = deep;

                        queued = dev.QueuedFrames;
                        free   = dev.AvailFrames;
                        target = ComputeFillTargetLocked(queued);
                    }
                }
                if (dev == null) { Thread.Sleep(5); continue; }

                // Fill only up to the current target cushion (not the whole ring), so latency
                // stays at the target depth instead of always topping the (large) ring off.
                if (queued >= target || free < 64) { Thread.Sleep(2); continue; }

                int n = Math.Min(Math.Min(target - queued, free), MaxBusFrames);
                if (n < 1) { Thread.Sleep(2); continue; }
                Array.Clear(_bus, 0, n * BusChannels);

                lock (_lock)
                {
                    for (int i = _voices.Count - 1; i >= 0; i--)
                    {
                        if (!RenderVoiceAdd(_voices[i], n))
                        {
                            bool wasStinger = _voices[i].IsStinger;
                            RetireVoice(_voices[i]);
                            _voices.RemoveAt(i);
                            // A stinger overlay just ended — swell the bed it ducked back to full.
                            if (wasStinger) RestoreDuckedLocked();
                        }
                    }
                    if (_closeWhenIdle && _voices.Count == 0)
                    {
                        TeardownDeviceLocked();
                        _closeWhenIdle = false;
                        continue;
                    }
                }

                // Master volume + hard clip-guard (raw f32 device wraps past 0dBFS).
                float master = MasterVolume;
                int total = n * BusChannels;
                for (int i = 0; i < total; i++)
                {
                    float s = _bus[i] * master;
                    _bus[i] = s > 1f ? 1f : (s < -1f ? -1f : s);
                }

                int pushed = 0;
                while (pushed < n)
                {
                    IntPtr at = IntPtr.Add(_busPtr, pushed * BusChannels * sizeof(float));
                    int w = dev.Write(at, n - pushed);
                    pushed += w;
                    if (pushed < n) Thread.Sleep(2);
                }
            }
        }

        // Produce n bus frames from `v` (stereo, 48k), apply its gain ramp, and ADD
        // into the bus. Returns false when the voice should be retired.
        bool RenderVoiceAdd(Voice v, int n)
        {
            int frames = v.Conv == IntPtr.Zero ? ProduceStereoDirect(v, n) : ProduceStereoConv(v, n);

            for (int f = 0; f < frames; f++)
            {
                if (v.GainStep != 0f)
                {
                    v.Gain += v.GainStep;
                    if ((v.GainStep > 0f && v.Gain >= v.TargetGain) ||
                        (v.GainStep < 0f && v.Gain <= v.TargetGain))
                    {
                        v.Gain = v.TargetGain;
                        v.GainStep = 0f;
                    }
                }
                if (v.TrackGainStep != 0f)
                {
                    v.TrackGain += v.TrackGainStep;
                    if ((v.TrackGainStep > 0f && v.TrackGain >= v.TrackGainTarget) ||
                        (v.TrackGainStep < 0f && v.TrackGain <= v.TrackGainTarget))
                    {
                        v.TrackGain = v.TrackGainTarget;
                        v.TrackGainStep = 0f;
                    }
                }
                int o = f * BusChannels;     // _voiceStereo and _bus share the stereo layout
                float g = v.Gain * v.TrackGain;   // crossfade gain × per-track loudness gain
                _bus[o]     += _voiceStereo[o]     * g;
                _bus[o + 1] += _voiceStereo[o + 1] * g;
            }

            // Keep the fade on a wall-clock schedule even when this block produced fewer
            // than n frames (loop reseek / converter priming / a starved voice): advance
            // the ramp across the remainder so a fading-out voice still retires on time
            // instead of lingering audibly.
            int silent = n - frames;
            if (silent > 0 && v.GainStep != 0f)
            {
                v.Gain += v.GainStep * silent;
                if ((v.GainStep > 0f && v.Gain >= v.TargetGain) ||
                    (v.GainStep < 0f && v.Gain <= v.TargetGain))
                { v.Gain = v.TargetGain; v.GainStep = 0f; }
            }
            if (silent > 0 && v.TrackGainStep != 0f)
            {
                v.TrackGain += v.TrackGainStep * silent;
                if ((v.TrackGainStep > 0f && v.TrackGain >= v.TrackGainTarget) ||
                    (v.TrackGainStep < 0f && v.TrackGain <= v.TrackGainTarget))
                { v.TrackGain = v.TrackGainTarget; v.TrackGainStep = 0f; }
            }

            if (v.Ended && frames < n) return false;              // track finished and drained
            if (v.TargetGain == 0f && v.Gain <= 0f) return false; // crossfaded out
            return true;
        }

        // Voice already at bus format (48k stereo): read straight into the stereo
        // scratch, looping at EOF when asked.
        int ProduceStereoDirect(Voice v, int n)
        {
            int frames = ReadFramesLoop(v, _voiceStereo, 0, n, BusChannels);
            if (frames < n) Array.Clear(_voiceStereo, frames * BusChannels, (n - frames) * BusChannels);
            return frames;
        }

        // Voice at some other rate/channel count: pull decoded input frames and run
        // them through the converter until n bus-stereo frames are produced (or the
        // track ends and the converter has drained). Leftover unconsumed input is
        // compacted to the front of InBuf and carried to the next tick.
        int ProduceStereoConv(Voice v, int n)
        {
            int produced = 0;
            int guard = 0;
            while (produced < n)
            {
                if (v.InFill < RefillFrames && !v.Ended)
                    v.InFill += ReadFramesLoop(v, v.InBuf, v.InFill, RefillFrames - v.InFill, v.SrcChannels);

                NativeConv.cmm_dc_process(v.Conv, v.InBuf, v.InFill, out long inC,
                                          _convTmp, n - produced, out long outW);

                if (outW > 0)
                    Array.Copy(_convTmp, 0, _voiceStereo, produced * BusChannels, (int)outW * BusChannels);
                produced += (int)outW;

                if (inC > 0)
                {
                    int rem = v.InFill - (int)inC;
                    if (rem > 0) Array.Copy(v.InBuf, (int)inC * v.SrcChannels, v.InBuf, 0, rem * v.SrcChannels);
                    v.InFill = rem;
                }

                if (v.Ended && v.InFill == 0 && outW == 0) break;     // ended and fully drained
                if (outW == 0 && inC == 0) { if (++guard > 8) break; } // no progress (safety)
                else guard = 0;
            }
            if (produced < n) Array.Clear(_voiceStereo, produced * BusChannels, (n - produced) * BusChannels);
            return produced;
        }

        // Read `wantFrames` frames (of `ch` channels) into buf at frame offset
        // `offsetFrames`, restarting at 0 on EOF when the voice loops. Returns frames read.
        static int ReadFramesLoop(Voice v, float[] buf, int offsetFrames, int wantFrames, int ch)
        {
            int need = wantFrames * ch;
            int baseOff = offsetFrames * ch;
            int got = 0;
            while (got < need)
            {
                int r = v.Src.ReadFloats(buf, baseOff + got, need - got);
                if (r <= 0)
                {
                    if (v.Loop && !v.Ended)
                    {
                        try { v.Reader.Position = 0; }
                        catch { v.Ended = true; break; }
                        continue;
                    }
                    v.Ended = true;
                    break;
                }
                got += r;
            }
            int frames = got / ch;
            v.SourceFramesRead += frames;   // advance the playhead (source frames consumed)
            return frames;
        }

        // Per-frame gain increment to ramp from→to over `seconds` at the bus rate.
        static float ComputeStep(float from, float to, float seconds)
        {
            if (seconds <= 0f) return 0f;
            float frames = seconds * BusRate;
            if (frames < 1f) frames = 1f;
            return (to - from) / frames;
        }
    }
}
