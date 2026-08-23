using System;
using System.Runtime.InteropServices;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // NATIVE AUDIO  (CMM_Decoder.dll — libopusfile + libvorbisfile + dr_flac/mp3/wav + fdk-aac/minimp4)
    // ─────────────────────────────────────────────────────────────────────────
    // P/Invoke surface for the native decode shim, covering EVERY supported format:
    // Opus / Vorbis / FLAC / MP3 / WAV / AIFF / AAC / Matroska-WebM. Consumers work
    // with BARE cmm_decoder* handles (AudioDecoder.OpenNativeHandle → the native
    // mixer's cmm_mix_add, or cmm_loudness_analyze) — there is no managed reader
    // wrapper. The whole point is GC pressure: the old managed decoders allocated on
    // the GC heap — the worst loudness-sweep / playback offender under the game's
    // non-generational Boehm GC. The native decoder does its work in native (malloc)
    // memory the GC never scans.
    //
    // CMM_Decoder.dll is REQUIRED — there is no managed fallback (all managed codec
    // libraries were removed). If the DLL is missing or the ABI mismatches, every
    // format fails to open and the track is skipped.

    internal static class NativeOpus
    {
        const string Dll = "CMM_Decoder";
        const int ExpectedAbi = 43;  // v1 Opus, v2 +FLAC, v3 +Vorbis, v4 +open_file, v5 +MP3/WAV/AIFF, v6 +AAC, v7 +container, v8 +output (cmm_out_*), v9 +converter (cmm_dc_*), v10 +cmm_out_queued/reset (adaptive ring), v11 +cmm_out_set_gain (consumer fade), v12 +native mixer (cmm_mix_*), v13 +native loudness (cmm_loudness_*), v14 +cmm_mix_get_output_stats (HUD buffer tracker), v15 +headless output (cmm_out_open_null/drain, cmm_mix_start_ex/pump_out) for the offline stress harness, v16 +emitter voices (cmm_mix_add_emitter/remove_emitter) — independent positional layer, v17 +spatial emitters (add_emitter pos+dist, cmm_mix_set_listener/set_emitter_pos), v18 +multi-point emitters (cmm_mix_set_emitter_points), v19 +per-speaker occlusion gain, v20 consumer-side emitter spatialization (no signature change), v21 +live emitter tuning (cmm_mix_set_emitter_tuning), v22 +stereo width (tuning gains stereoWidth), v23 +emitter reverb (cmm_mix_set_emitter_reverb), v24 +emitter master (cmm_mix_set_emitter_master), v25 +emitter continuity seek (cmm_mix_add_emitter startFrame), v26 +radio coloration (cmm_mix_set_emitter_color), v27 depth-graded reverb (cmm_mix_set_emitter_reverb gains depth + depthAmt), v28 two-bus reverb (+localWet: LOCAL bus fed the arriving occluded signal; wet becomes the seal-gated CARRY), v29 carry range curve = power decay over the whole reach (no signature change; kills the flat post-dry reverb plateau), v30 +emitter acoustic reset (cmm_mix_emitter_reset_acoustics) + fresh point slots init their smooth state from target, v31 multi-station slots (cmm_mix_add_emitter gains `slot`; cmm_mix_set_emitter_reverb takes per-slot send/depth arrays; per-slot rings/spatial/LPF/color, one shared room-reverb pair), v32 +cmm_mix_emitter_flush_slot (discard a slot's queued dry + render state when a different station acquires it), v33 +cmm_mix_hold_output (suppress the add-revive while the SP pause duck owns the output gain), v34 +per-point emitter render flags (cmm_mix_set_emitter_points gains `flags`: bit0 = ham class; bits 1-2 reserved for the stereo-pair channel split) + per-class coloration (cmm_mix_set_emitter_color gains the ham set's drive/static/voice — radioHam stays slightly distorted while shelf speakers render clean), v35 +radio UI SFX (cmm_mix_play_sfx: synthesized tuning-static burst + power on/off clicks, rendered outside the voice list), v36 keepalive static + cmm_mix_stop_sfx (a static ping extends the live hash instead of layering a second burst — the "static fires twice per dial" fix; stop_sfx releases it when the station voice lands), v37 +tuneable static tone (cmm_mix_set_static_tone: band edges Hz + crackle sputter depth, captured per ping for live dial-in), v38 +tuneable clicks (cmm_mix_set_click_tone: per-click tone cap Hz + snap ping/thump balance + length ms), v39 dial-detent clicks (set_click_tone gains per-click body resonator mix/pitch + double-strike level), v40 device lifecycle off g_lock (no signature change; producer thread solely owns device open/close so a wedged WASAPI endpoint can no longer freeze every mixer-lock caller — the spawn-time main-thread hard-freeze vector), v41 slewed masters (no signature change; cmm_mix_set_master/set_emitter_master set a target the producer ramps toward over 25ms, so a slider drag no longer clicks), v42 half-voxel occlusion field, v43 +the radio receiver (cmm_mix_set_voice_receiver: one signal 0..1 per voice driving gain, treble and stereo width together, so a station leaves and arrives like a radio instead of just changing level)

        // The mod loads from a Mods subfolder that is NOT on the OS native search path,
        // so a bare DllImport("CMM_Decoder") would fail to locate CMM_Decoder.dll. Preload it
        // by full path at mod init: once the module is in the process, the marshaller's
        // later DllImport resolves it by base name. Call Preload(ModPath) before the first
        // decode (i.e. before Available is probed). Safe to call once; no-op if already loaded.
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibrary(string lpFileName);

        static bool _preloaded;
        public static void Preload(string modPath)
        {
            if (_preloaded) return;
            _preloaded = true;
            try
            {
                // In a "Libs" SUBFOLDER, not the mod root — a native DLL in the root
                // makes the game's mod loader (Assembly.LoadFrom on every root *.dll) throw
                // "Invalid Image" and abort the entire mod. A subfolder is never scanned.
                string dllPath = System.IO.Path.Combine(modPath, "Libs", Dll + ".dll");
                if (!System.IO.File.Exists(dllPath))
                {
                    Log.Warning($"[CMM] {Dll}.dll not found at {dllPath} — NO audio will play (all formats decode natively).");
                    return;
                }
                IntPtr h = LoadLibrary(dllPath);
                if (h == IntPtr.Zero)
                    Log.Warning($"[CMM] LoadLibrary({Dll}.dll) failed (Win32 error {Marshal.GetLastWin32Error()}) — NO audio will play.");
                else
                    Log.Out($"[CMM] Preloaded native decoder from {dllPath}");
            }
            catch (Exception ex)
            {
                // kernel32 absent (non-Windows host) or any load failure: no audio
                // plays — every format now decodes through CMM_Decoder.
                Log.Warning($"[CMM] Native decoder preload skipped: {ex.Message}");
            }
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        static extern int CMM_Decoder_abi();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_opus_open_memory(byte[] data, long len,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        // Large-file path: decode straight from disk (wide path → _wfopen native-side).
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern IntPtr cmm_opus_open_file(string path,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        // Blittable float[] is pinned by the marshaller and written in place; [Out]
        // documents that the native side fills it.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_opus_read_float(IntPtr d, [Out] float[] outBuf, int maxFrames);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_opus_seek(IntPtr d, long frame);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_opus_close(IntPtr d);

        // 0 = not probed yet, 1 = available, -1 = unavailable. Probed once.
        static int _state;
        public static bool Available
        {
            get
            {
                if (_state == 0)
                {
                    try { _state = CMM_Decoder_abi() == ExpectedAbi ? 1 : -1; }
                    catch { _state = -1; }   // DllNotFound / BadImageFormat / EntryPointNotFound
                    if (_state == 1) Log.Out("[CMM] CMM_Decoder native decoder loaded (Opus/Vorbis/FLAC/MP3/WAV/AIFF/AAC).");
                    else Log.Warning("[CMM] CMM_Decoder native decoder unavailable — NO audio will play (every format decodes natively).");
                }
                return _state == 1;
            }
        }
    }

    // FLAC backend (dr_flac) in the same CMM_Decoder.dll. Shares NativeOpus's load
    // probe (one DLL, one ABI), so Available just forwards.
    internal static class NativeFlac
    {
        const string Dll = "CMM_Decoder";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_flac_open_memory(byte[] data, long len,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern IntPtr cmm_flac_open_file(string path,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_flac_read_float(IntPtr d, [Out] float[] outBuf, int maxFrames);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_flac_seek(IntPtr d, long frame);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_flac_close(IntPtr d);

        public static bool Available => NativeOpus.Available;
    }

    // Vorbis backend (libvorbisfile) in the same CMM_Decoder.dll. Shares NativeOpus's
    // load probe (one DLL, one ABI), so Available just forwards.
    internal static class NativeVorbis
    {
        const string Dll = "CMM_Decoder";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_vorbis_open_memory(byte[] data, long len,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern IntPtr cmm_vorbis_open_file(string path,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_vorbis_read_float(IntPtr d, [Out] float[] outBuf, int maxFrames);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_vorbis_seek(IntPtr d, long frame);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_vorbis_close(IntPtr d);

        public static bool Available => NativeOpus.Available;
    }

    // MP3 backend (dr_mp3) in the same CMM_Decoder.dll. Shares NativeOpus's load
    // probe (one DLL, one ABI), so Available just forwards.
    internal static class NativeMp3
    {
        const string Dll = "CMM_Decoder";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_mp3_open_memory(byte[] data, long len,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern IntPtr cmm_mp3_open_file(string path,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mp3_read_float(IntPtr d, [Out] float[] outBuf, int maxFrames);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_mp3_seek(IntPtr d, long frame);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_mp3_close(IntPtr d);

        public static bool Available => NativeOpus.Available;
    }

    // WAV/AIFF backend (dr_wav, one decoder for both containers) in the same
    // CMM_Decoder.dll. Shares NativeOpus's load probe, so Available just forwards.
    internal static class NativeWav
    {
        const string Dll = "CMM_Decoder";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_wav_open_memory(byte[] data, long len,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern IntPtr cmm_wav_open_file(string path,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_wav_read_float(IntPtr d, [Out] float[] outBuf, int maxFrames);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_wav_seek(IntPtr d, long frame);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_wav_close(IntPtr d);

        public static bool Available => NativeOpus.Available;
    }

    // AAC backend (fdk-aac + minimp4, one handle for MP4/M4A and raw ADTS) in the
    // same CMM_Decoder.dll. Shares NativeOpus's load probe, so Available forwards.
    internal static class NativeAac
    {
        const string Dll = "CMM_Decoder";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_aac_open_memory(byte[] data, long len,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern IntPtr cmm_aac_open_file(string path,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_aac_read_float(IntPtr d, [Out] float[] outBuf, int maxFrames);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_aac_seek(IntPtr d, long frame);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_aac_close(IntPtr d);

        public static bool Available => NativeOpus.Available;
    }

    // Matroska / WebM container backend (cmm_container_*). The native side demuxes
    // one audio track and remuxes it into an existing codec backend (Opus/Vorbis →
    // Ogg, AAC → ADTS, MP3 raw, FLAC native), so the managed surface is the same
    // shape as the others — open → read_float → seek → close.
    internal static class NativeContainer
    {
        const string Dll = "CMM_Decoder";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_container_open_memory(byte[] data, long len,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern IntPtr cmm_container_open_file(string path,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_container_read_float(IntPtr d, [Out] float[] outBuf, int maxFrames);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_container_seek(IntPtr d, long frame);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void cmm_container_close(IntPtr d);

        public static bool Available => NativeOpus.Available;
    }

    // Native EBU R128 loudness analyzer (cmm_loudness_*). One-shot: the caller opens a
    // decoder off the audio thread (AudioDecoder.OpenNativeHandle) and hands the raw
    // cmm_decoder* + its format kind here; native decodes to EOF, measures, and TAKES
    // OWNERSHIP (closes the decoder). Returns 1 with a gated result, 0 when the track is
    // too short/quiet to gate. The whole sweep then runs in native (malloc) memory — no
    // managed-heap churn to trip the GC and hitch the game (the point of the port).
    internal static class NativeLoudness
    {
        const string Dll = "CMM_Decoder";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int cmm_loudness_analyze(IntPtr dec, int kind, int srcRate,
            int srcChannels, out double outLufs, out double outPeak);

        public static bool Available => NativeOpus.Available;
    }

}
