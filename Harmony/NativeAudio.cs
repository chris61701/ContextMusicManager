using System;
using System.Runtime.InteropServices;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // NATIVE AUDIO  (CMM_Decoder.dll — libopusfile + stb_vorbis + dr_flac/mp3/wav + fdk-aac/minimp4)
    // ─────────────────────────────────────────────────────────────────────────
    // P/Invoke surface for the native decode shim plus WaveStreams that decode
    // EVERY supported format through it: Opus / Vorbis / FLAC / MP3 / WAV / AIFF / AAC.
    // The whole point is GC pressure: the old managed decoders (Concentus / NVorbis
    // / CSCore, and NAudio for wav/mp3/aiff) allocated on the GC heap — the worst
    // loudness-sweep / playback offender under the game's non-generational Boehm GC.
    // The native decoder does its work in native (malloc) memory the GC never scans;
    // the only managed allocation left is the reusable output buffer the caller owns.
    //
    // CMM_Decoder.dll is REQUIRED — there is no managed fallback (all codec libraries,
    // including NAudio's decoders, were removed). If the DLL is missing or the ABI
    // mismatches, every format fails to open (the constructors throw, caught by the
    // caller and the track is skipped).

    // Implemented by the native Opus/Vorbis/FLAC readers so AnalyzeToEnd and the
    // clip factory share one interleaved-float fast path regardless of codec.
    internal interface IFloatPcmSource
    {
        int ReadFloats(float[] data, int offset, int count);

        // Playback-only seek surface (AnalyzeToEnd/loudness never calls these). A resume
        // pre-seeks the reader to the resume frame before Play() (SeekBeforePlay), which
        // also arms a one-shot intercept: when Unity then fires its redundant SetPosition
        // from `AudioSource.time = safeStart`, the reader swallows it instead of repeating
        // the seek. Without this the decode-scan runs twice — once on the background thread
        // (SeekBeforePlay) and again synchronously on the main thread — which for AAC's
        // O(offset) scan is a 70–192ms transition hitch. ClearInitialSeekIntercept disarms
        // once the time-set has committed so a later loop restart's SetPosition(0) is honored.
        void SeekBeforePlay(long targetFrame);
        void ClearInitialSeekIntercept();
    }

    internal static class NativeOpus
    {
        const string Dll = "CMM_Decoder";
        const int ExpectedAbi = 13;  // v1 Opus, v2 +FLAC, v3 +Vorbis, v4 +open_file, v5 +MP3/WAV/AIFF, v6 +AAC, v7 +container, v8 +output (cmm_out_*), v9 +converter (cmm_dc_*), v10 +cmm_out_queued/reset (adaptive ring), v11 +cmm_out_set_gain (consumer fade), v12 +native mixer (cmm_mix_*), v13 +native loudness (cmm_loudness_*)

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

        // Buffered-playback path: same native export, file bytes passed as an unmanaged
        // pointer so they never touch the GC heap (see AudioDecoder.OpenBufferedReader).
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "cmm_opus_open_memory")]
        internal static extern IntPtr cmm_opus_open_memory_ptr(IntPtr data, long len,
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

        // Buffered-playback path: same native export, file bytes passed as an unmanaged pointer.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "cmm_flac_open_memory")]
        internal static extern IntPtr cmm_flac_open_memory_ptr(IntPtr data, long len,
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

    // Vorbis backend (stb_vorbis) in the same CMM_Decoder.dll. Shares NativeOpus's
    // load probe (one DLL, one ABI), so Available just forwards.
    internal static class NativeVorbis
    {
        const string Dll = "CMM_Decoder";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr cmm_vorbis_open_memory(byte[] data, long len,
            out int channels, out int sampleRate, out long totalFrames, out int err);

        // Buffered-playback path: same native export, file bytes passed as an unmanaged pointer.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "cmm_vorbis_open_memory")]
        internal static extern IntPtr cmm_vorbis_open_memory_ptr(IntPtr data, long len,
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

        // Buffered-playback path: same native export, file bytes passed as an unmanaged pointer.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "cmm_mp3_open_memory")]
        internal static extern IntPtr cmm_mp3_open_memory_ptr(IntPtr data, long len,
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

        // Buffered-playback path: same native export, file bytes passed as an unmanaged pointer.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "cmm_wav_open_memory")]
        internal static extern IntPtr cmm_wav_open_memory_ptr(IntPtr data, long len,
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

        // Buffered-playback path: same native export, file bytes passed as an unmanaged pointer.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "cmm_aac_open_memory")]
        internal static extern IntPtr cmm_aac_open_memory_ptr(IntPtr data, long len,
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

        // Buffered-playback path: same native export, file bytes passed as an unmanaged pointer.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "cmm_container_open_memory")]
        internal static extern IntPtr cmm_container_open_memory_ptr(IntPtr data, long len,
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

    // Native-backed Vorbis WaveStream (stb_vorbis). Same shape as NativeFlacReader:
    // preserves the stream's native sample rate, implements IFloatPcmSource for the
    // direct-float fast path. SeekBeforePlay arms the one-shot intercept (see IFloatPcmSource).
    internal sealed class NativeVorbisReader : WaveStream, IFloatPcmSource
    {
        readonly object _lock = new object();
        IntPtr _handle;
        readonly WaveFormat _format;
        readonly int _channels;
        readonly long _totalFrames;
        long _posFrames;
        bool _skipNextExternalSeek;   // armed by SeekBeforePlay to swallow Unity's redundant post-Play re-seek
        float[] _offsetScratch;
        float[] _byteReadScratch;

        public NativeVorbisReader(byte[] data, int length)
        {
            _handle = NativeVorbis.cmm_vorbis_open_memory(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_vorbis_open_memory failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Buffered playback: file bytes in a native (off-GC-heap) buffer; the decoder copies them.
        public NativeVorbisReader(IntPtr data, long length)
        {
            _handle = NativeVorbis.cmm_vorbis_open_memory_ptr(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_vorbis_open_memory(ptr) failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Large-file path: decode straight from disk (no full-file buffer).
        public NativeVorbisReader(string filePath)
        {
            _handle = NativeVorbis.cmm_vorbis_open_file(filePath,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_vorbis_open_file failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        public override WaveFormat WaveFormat => _format;
        public override long Length => _totalFrames > 0 ? _totalFrames * _format.BlockAlign : 0;

        public override long Position
        {
            get { lock (_lock) { return _posFrames * _format.BlockAlign; } }
            set
            {
                lock (_lock)
                {
                    if (_handle == IntPtr.Zero || _format.BlockAlign <= 0) return;
                    // Swallow Unity's redundant post-Play SetPosition (see SeekBeforePlay):
                    // a resume already pre-seeked the reader, so repeating the decode-scan
                    // here — synchronously on the main thread — is pure stall (192ms for AAC).
                    if (_skipNextExternalSeek) { _skipNextExternalSeek = false; return; }
                    long frame = value / _format.BlockAlign;
                    if (NativeVorbis.cmm_vorbis_seek(_handle, frame) == 0) _posFrames = frame;
                }
            }
        }

        public int ReadFloats(float[] data, int offset, int count)
        {
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return 0;
                int frames = count / _channels;
                if (frames <= 0) return 0;

                int got;
                if (offset == 0)
                {
                    got = NativeVorbis.cmm_vorbis_read_float(_handle, data, frames);
                }
                else
                {
                    if (_offsetScratch == null || _offsetScratch.Length < count)
                        _offsetScratch = new float[count];
                    got = NativeVorbis.cmm_vorbis_read_float(_handle, _offsetScratch, frames);
                    if (got > 0) Array.Copy(_offsetScratch, 0, data, offset, got * _channels);
                }

                if (got <= 0) return 0;
                _posFrames += got;
                return got * _channels;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int floatCount = count / 4;
            if (_byteReadScratch == null || _byteReadScratch.Length < floatCount)
                _byteReadScratch = new float[floatCount];
            int filledFloats = ReadFloats(_byteReadScratch, 0, floatCount);
            Buffer.BlockCopy(_byteReadScratch, 0, buffer, offset, filledFloats * 4);
            return filledFloats * 4;
        }

        // Pre-seek the reader to the resume frame before Play() (runs on a background
        // thread), then arm the one-shot intercept so the redundant post-Play seek Unity
        // fires from `AudioSource.time = safeStart` is swallowed rather than repeating the
        // decode-scan synchronously on the main thread.
        public void SeekBeforePlay(long targetFrame)
        {
            lock (_lock) { _skipNextExternalSeek = false; }   // never swallow our own pre-seek
            Position = targetFrame * _format.BlockAlign;       // real seek
            lock (_lock) { _skipNextExternalSeek = true; }     // arm for Unity's redundant re-seek
        }
        // Disarm once the explicit time-set has committed (called from PlayFromCache), so a
        // later loop restart's SetPosition(0) — or an interrupted resume that armed but never
        // reached the time-set — does not get swallowed.
        public void ClearInitialSeekIntercept() { lock (_lock) { _skipNextExternalSeek = false; } }

        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_handle != IntPtr.Zero) { NativeVorbis.cmm_vorbis_close(_handle); _handle = IntPtr.Zero; }
            }
            base.Dispose(disposing);
        }
    }

    // Native-backed FLAC WaveStream (dr_flac). Same shape as NativeOggOpusReader,
    // but FLAC preserves its native sample rate (read from the stream, not fixed at
    // 48k). Implements IFloatPcmSource so the clip factory uses the direct-float
    // fast path. SeekBeforePlay arms the one-shot intercept (see IFloatPcmSource).
    internal sealed class NativeFlacReader : WaveStream, IFloatPcmSource
    {
        readonly object _lock = new object();
        IntPtr _handle;
        readonly WaveFormat _format;
        readonly int _channels;
        readonly long _totalFrames;
        long _posFrames;
        bool _skipNextExternalSeek;   // armed by SeekBeforePlay to swallow Unity's redundant post-Play re-seek
        float[] _offsetScratch;
        float[] _byteReadScratch;

        public NativeFlacReader(byte[] data, int length)
        {
            _handle = NativeFlac.cmm_flac_open_memory(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_flac_open_memory failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Buffered playback: file bytes in a native (off-GC-heap) buffer; the decoder copies them.
        public NativeFlacReader(IntPtr data, long length)
        {
            _handle = NativeFlac.cmm_flac_open_memory_ptr(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_flac_open_memory(ptr) failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Large-file path: decode straight from disk (no full-file buffer).
        public NativeFlacReader(string filePath)
        {
            _handle = NativeFlac.cmm_flac_open_file(filePath,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_flac_open_file failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        public override WaveFormat WaveFormat => _format;
        public override long Length => _totalFrames > 0 ? _totalFrames * _format.BlockAlign : 0;

        public override long Position
        {
            get { lock (_lock) { return _posFrames * _format.BlockAlign; } }
            set
            {
                lock (_lock)
                {
                    if (_handle == IntPtr.Zero || _format.BlockAlign <= 0) return;
                    // Swallow Unity's redundant post-Play SetPosition (see SeekBeforePlay):
                    // a resume already pre-seeked the reader, so repeating the decode-scan
                    // here — synchronously on the main thread — is pure stall (192ms for AAC).
                    if (_skipNextExternalSeek) { _skipNextExternalSeek = false; return; }
                    long frame = value / _format.BlockAlign;
                    if (NativeFlac.cmm_flac_seek(_handle, frame) == 0) _posFrames = frame;
                }
            }
        }

        public int ReadFloats(float[] data, int offset, int count)
        {
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return 0;
                int frames = count / _channels;
                if (frames <= 0) return 0;

                int got;
                if (offset == 0)
                {
                    got = NativeFlac.cmm_flac_read_float(_handle, data, frames);
                }
                else
                {
                    if (_offsetScratch == null || _offsetScratch.Length < count)
                        _offsetScratch = new float[count];
                    got = NativeFlac.cmm_flac_read_float(_handle, _offsetScratch, frames);
                    if (got > 0) Array.Copy(_offsetScratch, 0, data, offset, got * _channels);
                }

                if (got <= 0) return 0;
                _posFrames += got;
                return got * _channels;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int floatCount = count / 4;
            if (_byteReadScratch == null || _byteReadScratch.Length < floatCount)
                _byteReadScratch = new float[floatCount];
            int filledFloats = ReadFloats(_byteReadScratch, 0, floatCount);
            Buffer.BlockCopy(_byteReadScratch, 0, buffer, offset, filledFloats * 4);
            return filledFloats * 4;
        }

        // Pre-seek the reader to the resume frame before Play() (runs on a background
        // thread), then arm the one-shot intercept so the redundant post-Play seek Unity
        // fires from `AudioSource.time = safeStart` is swallowed rather than repeating the
        // decode-scan synchronously on the main thread.
        public void SeekBeforePlay(long targetFrame)
        {
            lock (_lock) { _skipNextExternalSeek = false; }   // never swallow our own pre-seek
            Position = targetFrame * _format.BlockAlign;       // real seek
            lock (_lock) { _skipNextExternalSeek = true; }     // arm for Unity's redundant re-seek
        }
        // Disarm once the explicit time-set has committed (called from PlayFromCache), so a
        // later loop restart's SetPosition(0) — or an interrupted resume that armed but never
        // reached the time-set — does not get swallowed.
        public void ClearInitialSeekIntercept() { lock (_lock) { _skipNextExternalSeek = false; } }

        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_handle != IntPtr.Zero) { NativeFlac.cmm_flac_close(_handle); _handle = IntPtr.Zero; }
            }
            base.Dispose(disposing);
        }
    }

    // Native-backed Ogg/Opus WaveStream. Mirrors the surface of SeekableOpusReader
    // that the rest of the decoder uses (ReadFloats / Position / Length / WaveFormat /
    // SeekBeforePlay), so it is a drop-in via the IFloatPcmSource fast path.
    internal sealed class NativeOggOpusReader : WaveStream, IFloatPcmSource
    {
        readonly object _lock = new object();
        IntPtr _handle;
        readonly WaveFormat _format;
        readonly int _channels;
        readonly long _totalFrames;
        long _posFrames;
        bool _skipNextExternalSeek;   // armed by SeekBeforePlay to swallow Unity's redundant post-Play re-seek
        float[] _offsetScratch;   // only if a caller ever reads at offset > 0 (none today)
        float[] _byteReadScratch; // backs the byte[] Read path

        public NativeOggOpusReader(byte[] data, int length)
        {
            _handle = NativeOpus.cmm_opus_open_memory(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_opus_open_memory failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Buffered playback: file bytes in a native (off-GC-heap) buffer; the decoder copies
        // them (cmm_*_open_memory), so the caller frees the buffer right after construction.
        public NativeOggOpusReader(IntPtr data, long length)
        {
            _handle = NativeOpus.cmm_opus_open_memory_ptr(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_opus_open_memory(ptr) failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Large-file path: decode straight from disk (no full-file buffer).
        public NativeOggOpusReader(string filePath)
        {
            _handle = NativeOpus.cmm_opus_open_file(filePath,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_opus_open_file failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        public override WaveFormat WaveFormat => _format;
        public override long Length => _totalFrames > 0 ? _totalFrames * _format.BlockAlign : 0;

        public override long Position
        {
            get { lock (_lock) { return _posFrames * _format.BlockAlign; } }
            set
            {
                lock (_lock)
                {
                    if (_handle == IntPtr.Zero || _format.BlockAlign <= 0) return;
                    // Swallow Unity's redundant post-Play SetPosition (see SeekBeforePlay):
                    // a resume already pre-seeked the reader, so repeating the decode-scan
                    // here — synchronously on the main thread — is pure stall (192ms for AAC).
                    if (_skipNextExternalSeek) { _skipNextExternalSeek = false; return; }
                    long frame = value / _format.BlockAlign;
                    if (NativeOpus.cmm_opus_seek(_handle, frame) == 0) _posFrames = frame;
                }
            }
        }

        // Interleaved float fast path (used by AnalyzeToEnd and the clip factory).
        // `count` is a float count; returns floats written (0 = EOF).
        public int ReadFloats(float[] data, int offset, int count)
        {
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return 0;
                int frames = count / _channels;
                if (frames <= 0) return 0;

                int got;
                if (offset == 0)
                {
                    got = NativeOpus.cmm_opus_read_float(_handle, data, frames);
                }
                else
                {
                    if (_offsetScratch == null || _offsetScratch.Length < count)
                        _offsetScratch = new float[count];
                    got = NativeOpus.cmm_opus_read_float(_handle, _offsetScratch, frames);
                    if (got > 0) Array.Copy(_offsetScratch, 0, data, offset, got * _channels);
                }

                if (got <= 0) return 0;
                _posFrames += got;
                return got * _channels;
            }
        }

        // byte[] WaveStream.Read — float decode then blockcopy, mirroring SeekableOpusReader.
        public override int Read(byte[] buffer, int offset, int count)
        {
            int floatCount = count / 4;
            if (_byteReadScratch == null || _byteReadScratch.Length < floatCount)
                _byteReadScratch = new float[floatCount];
            int filledFloats = ReadFloats(_byteReadScratch, 0, floatCount);
            Buffer.BlockCopy(_byteReadScratch, 0, buffer, offset, filledFloats * 4);
            return filledFloats * 4;
        }

        // Pre-seek the reader to the resume frame before Play() (runs on a background
        // thread), then arm the one-shot intercept so the redundant post-Play seek Unity
        // fires from `AudioSource.time = safeStart` is swallowed rather than repeating the
        // decode-scan synchronously on the main thread.
        public void SeekBeforePlay(long targetFrame)
        {
            lock (_lock) { _skipNextExternalSeek = false; }   // never swallow our own pre-seek
            Position = targetFrame * _format.BlockAlign;       // real seek
            lock (_lock) { _skipNextExternalSeek = true; }     // arm for Unity's redundant re-seek
        }
        // Disarm once the explicit time-set has committed (called from PlayFromCache), so a
        // later loop restart's SetPosition(0) — or an interrupted resume that armed but never
        // reached the time-set — does not get swallowed.
        public void ClearInitialSeekIntercept() { lock (_lock) { _skipNextExternalSeek = false; } }

        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_handle != IntPtr.Zero) { NativeOpus.cmm_opus_close(_handle); _handle = IntPtr.Zero; }
            }
            base.Dispose(disposing);
        }
    }

    // Native-backed MP3 WaveStream (dr_mp3). Same shape as NativeFlacReader:
    // preserves the stream's native sample rate, implements IFloatPcmSource for the
    // direct-float fast path. SeekBeforePlay arms the one-shot intercept (see IFloatPcmSource).
    internal sealed class NativeMp3Reader : WaveStream, IFloatPcmSource
    {
        readonly object _lock = new object();
        IntPtr _handle;
        readonly WaveFormat _format;
        readonly int _channels;
        readonly long _totalFrames;
        long _posFrames;
        bool _skipNextExternalSeek;   // armed by SeekBeforePlay to swallow Unity's redundant post-Play re-seek
        float[] _offsetScratch;
        float[] _byteReadScratch;

        public NativeMp3Reader(byte[] data, int length)
        {
            _handle = NativeMp3.cmm_mp3_open_memory(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_mp3_open_memory failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Buffered playback: file bytes in a native (off-GC-heap) buffer; the decoder copies them.
        public NativeMp3Reader(IntPtr data, long length)
        {
            _handle = NativeMp3.cmm_mp3_open_memory_ptr(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_mp3_open_memory(ptr) failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Large-file path: decode straight from disk (no full-file buffer).
        public NativeMp3Reader(string filePath)
        {
            _handle = NativeMp3.cmm_mp3_open_file(filePath,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_mp3_open_file failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        public override WaveFormat WaveFormat => _format;
        public override long Length => _totalFrames > 0 ? _totalFrames * _format.BlockAlign : 0;

        public override long Position
        {
            get { lock (_lock) { return _posFrames * _format.BlockAlign; } }
            set
            {
                lock (_lock)
                {
                    if (_handle == IntPtr.Zero || _format.BlockAlign <= 0) return;
                    // Swallow Unity's redundant post-Play SetPosition (see SeekBeforePlay):
                    // a resume already pre-seeked the reader, so repeating the decode-scan
                    // here — synchronously on the main thread — is pure stall (192ms for AAC).
                    if (_skipNextExternalSeek) { _skipNextExternalSeek = false; return; }
                    long frame = value / _format.BlockAlign;
                    if (NativeMp3.cmm_mp3_seek(_handle, frame) == 0) _posFrames = frame;
                }
            }
        }

        public int ReadFloats(float[] data, int offset, int count)
        {
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return 0;
                int frames = count / _channels;
                if (frames <= 0) return 0;

                int got;
                if (offset == 0)
                {
                    got = NativeMp3.cmm_mp3_read_float(_handle, data, frames);
                }
                else
                {
                    if (_offsetScratch == null || _offsetScratch.Length < count)
                        _offsetScratch = new float[count];
                    got = NativeMp3.cmm_mp3_read_float(_handle, _offsetScratch, frames);
                    if (got > 0) Array.Copy(_offsetScratch, 0, data, offset, got * _channels);
                }

                if (got <= 0) return 0;
                _posFrames += got;
                return got * _channels;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int floatCount = count / 4;
            if (_byteReadScratch == null || _byteReadScratch.Length < floatCount)
                _byteReadScratch = new float[floatCount];
            int filledFloats = ReadFloats(_byteReadScratch, 0, floatCount);
            Buffer.BlockCopy(_byteReadScratch, 0, buffer, offset, filledFloats * 4);
            return filledFloats * 4;
        }

        // Pre-seek the reader to the resume frame before Play() (runs on a background
        // thread), then arm the one-shot intercept so the redundant post-Play seek Unity
        // fires from `AudioSource.time = safeStart` is swallowed rather than repeating the
        // decode-scan synchronously on the main thread.
        public void SeekBeforePlay(long targetFrame)
        {
            lock (_lock) { _skipNextExternalSeek = false; }   // never swallow our own pre-seek
            Position = targetFrame * _format.BlockAlign;       // real seek
            lock (_lock) { _skipNextExternalSeek = true; }     // arm for Unity's redundant re-seek
        }
        // Disarm once the explicit time-set has committed (called from PlayFromCache), so a
        // later loop restart's SetPosition(0) — or an interrupted resume that armed but never
        // reached the time-set — does not get swallowed.
        public void ClearInitialSeekIntercept() { lock (_lock) { _skipNextExternalSeek = false; } }

        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_handle != IntPtr.Zero) { NativeMp3.cmm_mp3_close(_handle); _handle = IntPtr.Zero; }
            }
            base.Dispose(disposing);
        }
    }

    // Native-backed WAV/AIFF WaveStream (dr_wav — one decoder for both containers).
    // dr_wav converts any source sample format to IEEE float on read, so this is an
    // IFloatPcmSource like the rest; native sample rate preserved, seek is reliable.
    internal sealed class NativeWavReader : WaveStream, IFloatPcmSource
    {
        readonly object _lock = new object();
        IntPtr _handle;
        readonly WaveFormat _format;
        readonly int _channels;
        readonly long _totalFrames;
        long _posFrames;
        bool _skipNextExternalSeek;   // armed by SeekBeforePlay to swallow Unity's redundant post-Play re-seek
        float[] _offsetScratch;
        float[] _byteReadScratch;

        public NativeWavReader(byte[] data, int length)
        {
            _handle = NativeWav.cmm_wav_open_memory(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_wav_open_memory failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Buffered playback: file bytes in a native (off-GC-heap) buffer; the decoder copies them.
        public NativeWavReader(IntPtr data, long length)
        {
            _handle = NativeWav.cmm_wav_open_memory_ptr(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_wav_open_memory(ptr) failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Large-file path: decode straight from disk (no full-file buffer).
        public NativeWavReader(string filePath)
        {
            _handle = NativeWav.cmm_wav_open_file(filePath,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_wav_open_file failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        public override WaveFormat WaveFormat => _format;
        public override long Length => _totalFrames > 0 ? _totalFrames * _format.BlockAlign : 0;

        public override long Position
        {
            get { lock (_lock) { return _posFrames * _format.BlockAlign; } }
            set
            {
                lock (_lock)
                {
                    if (_handle == IntPtr.Zero || _format.BlockAlign <= 0) return;
                    // Swallow Unity's redundant post-Play SetPosition (see SeekBeforePlay):
                    // a resume already pre-seeked the reader, so repeating the decode-scan
                    // here — synchronously on the main thread — is pure stall (192ms for AAC).
                    if (_skipNextExternalSeek) { _skipNextExternalSeek = false; return; }
                    long frame = value / _format.BlockAlign;
                    if (NativeWav.cmm_wav_seek(_handle, frame) == 0) _posFrames = frame;
                }
            }
        }

        public int ReadFloats(float[] data, int offset, int count)
        {
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return 0;
                int frames = count / _channels;
                if (frames <= 0) return 0;

                int got;
                if (offset == 0)
                {
                    got = NativeWav.cmm_wav_read_float(_handle, data, frames);
                }
                else
                {
                    if (_offsetScratch == null || _offsetScratch.Length < count)
                        _offsetScratch = new float[count];
                    got = NativeWav.cmm_wav_read_float(_handle, _offsetScratch, frames);
                    if (got > 0) Array.Copy(_offsetScratch, 0, data, offset, got * _channels);
                }

                if (got <= 0) return 0;
                _posFrames += got;
                return got * _channels;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int floatCount = count / 4;
            if (_byteReadScratch == null || _byteReadScratch.Length < floatCount)
                _byteReadScratch = new float[floatCount];
            int filledFloats = ReadFloats(_byteReadScratch, 0, floatCount);
            Buffer.BlockCopy(_byteReadScratch, 0, buffer, offset, filledFloats * 4);
            return filledFloats * 4;
        }

        // Pre-seek the reader to the resume frame before Play() (runs on a background
        // thread), then arm the one-shot intercept so the redundant post-Play seek Unity
        // fires from `AudioSource.time = safeStart` is swallowed rather than repeating the
        // decode-scan synchronously on the main thread.
        public void SeekBeforePlay(long targetFrame)
        {
            lock (_lock) { _skipNextExternalSeek = false; }   // never swallow our own pre-seek
            Position = targetFrame * _format.BlockAlign;       // real seek
            lock (_lock) { _skipNextExternalSeek = true; }     // arm for Unity's redundant re-seek
        }
        // Disarm once the explicit time-set has committed (called from PlayFromCache), so a
        // later loop restart's SetPosition(0) — or an interrupted resume that armed but never
        // reached the time-set — does not get swallowed.
        public void ClearInitialSeekIntercept() { lock (_lock) { _skipNextExternalSeek = false; } }

        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_handle != IntPtr.Zero) { NativeWav.cmm_wav_close(_handle); _handle = IntPtr.Zero; }
            }
            base.Dispose(disposing);
        }
    }

    // Native-backed AAC WaveStream (fdk-aac + minimp4). One reader for both an
    // MP4/M4A container and a raw ADTS .aac stream — the native open detects the
    // container. Same shape as the others: native sample rate preserved (HE-AAC's
    // SBR rate is settled after the first decoded frame, which the native open
    // performs), IFloatPcmSource direct-float path, reliable seek (for ADTS the
    // native side restarts-and-discards, since ADTS has no seek index).
    internal sealed class NativeAacReader : WaveStream, IFloatPcmSource
    {
        readonly object _lock = new object();
        IntPtr _handle;
        readonly WaveFormat _format;
        readonly int _channels;
        readonly long _totalFrames;
        long _posFrames;
        bool _skipNextExternalSeek;   // armed by SeekBeforePlay to swallow Unity's redundant post-Play re-seek
        float[] _offsetScratch;
        float[] _byteReadScratch;

        public NativeAacReader(byte[] data, int length)
        {
            _handle = NativeAac.cmm_aac_open_memory(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_aac_open_memory failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Buffered playback: file bytes in a native (off-GC-heap) buffer; the decoder copies them.
        public NativeAacReader(IntPtr data, long length)
        {
            _handle = NativeAac.cmm_aac_open_memory_ptr(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_aac_open_memory(ptr) failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Large-file path: decode straight from disk (MP4 streams via the demuxer;
        // raw ADTS is read whole native-side).
        public NativeAacReader(string filePath)
        {
            _handle = NativeAac.cmm_aac_open_file(filePath,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_aac_open_file failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        public override WaveFormat WaveFormat => _format;
        public override long Length => _totalFrames > 0 ? _totalFrames * _format.BlockAlign : 0;

        public override long Position
        {
            get { lock (_lock) { return _posFrames * _format.BlockAlign; } }
            set
            {
                lock (_lock)
                {
                    if (_handle == IntPtr.Zero || _format.BlockAlign <= 0) return;
                    // Swallow Unity's redundant post-Play SetPosition (see SeekBeforePlay):
                    // a resume already pre-seeked the reader, so repeating the decode-scan
                    // here — synchronously on the main thread — is pure stall (192ms for AAC).
                    if (_skipNextExternalSeek) { _skipNextExternalSeek = false; return; }
                    long frame = value / _format.BlockAlign;
                    if (NativeAac.cmm_aac_seek(_handle, frame) == 0) _posFrames = frame;
                }
            }
        }

        public int ReadFloats(float[] data, int offset, int count)
        {
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return 0;
                int frames = count / _channels;
                if (frames <= 0) return 0;

                int got;
                if (offset == 0)
                {
                    got = NativeAac.cmm_aac_read_float(_handle, data, frames);
                }
                else
                {
                    if (_offsetScratch == null || _offsetScratch.Length < count)
                        _offsetScratch = new float[count];
                    got = NativeAac.cmm_aac_read_float(_handle, _offsetScratch, frames);
                    if (got > 0) Array.Copy(_offsetScratch, 0, data, offset, got * _channels);
                }

                if (got <= 0) return 0;
                _posFrames += got;
                return got * _channels;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int floatCount = count / 4;
            if (_byteReadScratch == null || _byteReadScratch.Length < floatCount)
                _byteReadScratch = new float[floatCount];
            int filledFloats = ReadFloats(_byteReadScratch, 0, floatCount);
            Buffer.BlockCopy(_byteReadScratch, 0, buffer, offset, filledFloats * 4);
            return filledFloats * 4;
        }

        // Pre-seek the reader to the resume frame before Play() (runs on a background
        // thread), then arm the one-shot intercept so the redundant post-Play seek Unity
        // fires from `AudioSource.time = safeStart` is swallowed rather than repeating the
        // decode-scan synchronously on the main thread.
        public void SeekBeforePlay(long targetFrame)
        {
            lock (_lock) { _skipNextExternalSeek = false; }   // never swallow our own pre-seek
            Position = targetFrame * _format.BlockAlign;       // real seek
            lock (_lock) { _skipNextExternalSeek = true; }     // arm for Unity's redundant re-seek
        }
        // Disarm once the explicit time-set has committed (called from PlayFromCache), so a
        // later loop restart's SetPosition(0) — or an interrupted resume that armed but never
        // reached the time-set — does not get swallowed.
        public void ClearInitialSeekIntercept() { lock (_lock) { _skipNextExternalSeek = false; } }

        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_handle != IntPtr.Zero) { NativeAac.cmm_aac_close(_handle); _handle = IntPtr.Zero; }
            }
            base.Dispose(disposing);
        }
    }

    // Native-backed Matroska/WebM container WaveStream (cmm_container_*). Identical
    // shape to NativeAacReader — the native side demuxes the audio track and remuxes
    // it into an existing backend, exposing the same float/seek contract. The native
    // rate is preserved (settled by the inner decoder's open). For codecs whose remux
    // restarts-and-discards on seek (raw ADTS via the AAC backend) the native side
    // handles it; Opus/Vorbis/FLAC seek precisely through their Ogg/native streams.
    internal sealed class NativeContainerReader : WaveStream, IFloatPcmSource
    {
        readonly object _lock = new object();
        IntPtr _handle;
        readonly WaveFormat _format;
        readonly int _channels;
        readonly long _totalFrames;
        long _posFrames;
        bool _skipNextExternalSeek;
        float[] _offsetScratch;
        float[] _byteReadScratch;

        public NativeContainerReader(byte[] data, int length)
        {
            _handle = NativeContainer.cmm_container_open_memory(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_container_open_memory failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Buffered playback: file bytes in a native (off-GC-heap) buffer; the decoder copies them.
        public NativeContainerReader(IntPtr data, long length)
        {
            _handle = NativeContainer.cmm_container_open_memory_ptr(data, length,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_container_open_memory(ptr) failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        // Large-file path: the native demuxer streams the (possibly huge) movie from
        // disk and remuxes only the audio track into memory.
        public NativeContainerReader(string filePath)
        {
            _handle = NativeContainer.cmm_container_open_file(filePath,
                out _channels, out int sampleRate, out _totalFrames, out int err);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException($"cmm_container_open_file failed (err={err})");
            _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        }

        public override WaveFormat WaveFormat => _format;
        public override long Length => _totalFrames > 0 ? _totalFrames * _format.BlockAlign : 0;

        public override long Position
        {
            get { lock (_lock) { return _posFrames * _format.BlockAlign; } }
            set
            {
                lock (_lock)
                {
                    if (_handle == IntPtr.Zero || _format.BlockAlign <= 0) return;
                    if (_skipNextExternalSeek) { _skipNextExternalSeek = false; return; }
                    long frame = value / _format.BlockAlign;
                    if (NativeContainer.cmm_container_seek(_handle, frame) == 0) _posFrames = frame;
                }
            }
        }

        public int ReadFloats(float[] data, int offset, int count)
        {
            lock (_lock)
            {
                if (_handle == IntPtr.Zero) return 0;
                int frames = count / _channels;
                if (frames <= 0) return 0;

                int got;
                if (offset == 0)
                {
                    got = NativeContainer.cmm_container_read_float(_handle, data, frames);
                }
                else
                {
                    if (_offsetScratch == null || _offsetScratch.Length < count)
                        _offsetScratch = new float[count];
                    got = NativeContainer.cmm_container_read_float(_handle, _offsetScratch, frames);
                    if (got > 0) Array.Copy(_offsetScratch, 0, data, offset, got * _channels);
                }

                if (got <= 0) return 0;
                _posFrames += got;
                return got * _channels;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int floatCount = count / 4;
            if (_byteReadScratch == null || _byteReadScratch.Length < floatCount)
                _byteReadScratch = new float[floatCount];
            int filledFloats = ReadFloats(_byteReadScratch, 0, floatCount);
            Buffer.BlockCopy(_byteReadScratch, 0, buffer, offset, filledFloats * 4);
            return filledFloats * 4;
        }

        public void SeekBeforePlay(long targetFrame)
        {
            lock (_lock) { _skipNextExternalSeek = false; }
            Position = targetFrame * _format.BlockAlign;
            lock (_lock) { _skipNextExternalSeek = true; }
        }
        public void ClearInitialSeekIntercept() { lock (_lock) { _skipNextExternalSeek = false; } }

        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_handle != IntPtr.Zero) { NativeContainer.cmm_container_close(_handle); _handle = IntPtr.Zero; }
            }
            base.Dispose(disposing);
        }
    }
}
