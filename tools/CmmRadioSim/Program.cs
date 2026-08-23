// CmmRadioSim — render a personal-radio station change offline, through the REAL native mixer.
//
// Drives CMM_Decoder.dll headless (null output device, no sound card, no realtime pacing) and
// drains its output into a WAV via cmm_mix_pump_capture. Every stage the game uses is the game's:
// the decoders, cmm_mix_add's crossfade and eviction, the voice gain ramps, the bus-level receiver
// with its noise/hi-cut/stereo latch, the master slew and the clip guard.
//
// What it does NOT run is PersonalRadio.cs itself — that needs Unity. Instead the timeline below
// replays the exact sequence of native calls a dial press produces, with the same timings, read
// off that file. Keep them in step: if the state machine changes, change Timeline() to match, or
// this becomes a simulator of something that no longer exists.
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace CmmRadioSim
{
    internal static class N
    {
        const string Dll = "CMM_Decoder";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int CMM_Decoder_abi();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmm_mix_start_ex(int busRate, int busChannels, int tightFrames, int maxFrames, int headless, out int err);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_stop_all_now();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmm_mix_pump_capture([Out] float[] dst, int frames);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_set_master(float vol);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern long cmm_mix_add(IntPtr dec, int kind, int srcRate, int srcChannels, long totalFrames, float fade, int loop, long startFrame, float trackGain, float duckOthersTo, int snapTight);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmm_mix_set_voice_radio(long id, int on);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_set_receiver_active(int active, float level);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_set_receiver_signal(float signal, float ratePerSec, float dullness, float mononess, int active, float level);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_set_receiver_tone(float knee, float kneeW, float noiseTrim, float clickAmt, float clickMax, float hiFloor, float deemHz);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_set_receiver_shape(float floorDb, float muteW, float drive, float wander, float flutter, float flutHz);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_set_receiver_arrival(float noiseHold, float noiseFloor, float winA, float winB, float lockU, float lockMs);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_set_receiver_radio(float amount, float makeupDb, float lowCutHz, float highCutHz);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal static extern IntPtr cmm_mp3_open_file(string path, out int channels, out int rate, out long totalFrames, out int err);
    }

    internal static class Program
    {
        const int Rate = 48000, Ch = 2;

        // ── The knobs, defaulted to the SETTLED values (tools/CmmTuneRender/README.md). These are
        //    what CMMConfig.cs ships; the game reads Config/radio.xml on top of them, and that file
        //    disagreeing with these is exactly how six weeks of in-game listening went to values
        //    nobody approved. `--config <radio.xml>` loads a real one so you can hear what a player
        //    actually gets rather than what the code intends.
        static float Knee = 0.45f, KneeW = 0.22f, Noise = 0.55f, Clicks = 0.18f, ClickRate = 180f;
        static float HiFloor = 1800f, Deemph = 2122f;
        static float FloorDb = 5f, MuteW = 5f, Drive = 0.15f, Wander = 0.55f, Flutter = 0.30f, FlutterHz = 14f;
        static float NoiseHold = 1f, NoiseFloor = 0.06f, WinA = 0.28f, WinB = 0.912f, LockAt = 0.52f, LockMs = 9f;
        static float Mangle = 1f, MakeupDb = 9f, LowCut = 400f, HighCut = 2600f;
        static float Dullness = 0.85f, Mononess = 1f;
        static float LeaveMs = 1200f, MinStaticMs = 1500f, ArriveMs = 2800f;

        // THE LOUDNESS GAIN, and it is not cosmetic. Every track the game plays is normalised
        // toward -18 LUFS, so a voice's gain is typically well under 1 (median 0.557 over this
        // machine's cache). The receiver's noise is levelled by the same figure, so the ratio the
        // ear settled on is preserved -- but the HEADROOM is not: the reference renderer scales
        // its whole output by 0.55 after every nonlinearity, which is what keeps its noise peak
        // (1.74 before scaling) under full scale. The game has no such guarantee. Rendering at 1.0
        // clips; rendering at a real gain may not. That difference is the whole question, so it is
        // a knob and not an assumption.
        static float GainA = 0.557f, GainB = 0.557f;

        // Matches PersonalRadio: SwapFade, and what still lands AFTER the handover instant.
        // The dispatch happens at the press now and the worker parks until the swap, so the file's
        // OPEN is spent under the leave and only the seek is left here. Kept as a knob because it
        // is the one number in this file measured rather than read out of the source.
        const float SwapFade = 0.25f, SeekSec = 0.05f;
        // Seconds of fully-tuned station before the press, and of locked station after.
        const float PreRoll = 3.0f, PostRoll = 3.0f;

        static int Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: CmmRadioSim <A.mp3> <B.mp3> <out.wav> [--config radio.xml] [--startA sec] [--startB sec]");
                return 2;
            }
            string aPath = args[0], bPath = args[1], outPath = args[2];
            double startA = 20, startB = 30;
            for (int i = 3; i < args.Length - 1; i++)
            {
                if (args[i] == "--config") LoadConfig(args[i + 1]);
                else if (args[i] == "--startA") startA = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
                else if (args[i] == "--startB") startB = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
                else if (args[i] == "--gainA") GainA = float.Parse(args[i + 1], CultureInfo.InvariantCulture);
                else if (args[i] == "--gainB") GainB = float.Parse(args[i + 1], CultureInfo.InvariantCulture);
            }

            Console.WriteLine($"native ABI {N.CMM_Decoder_abi()}");

            int err;
            // Headless: null device, so the producer runs as fast as we drain it. Cushion kept
            // small — a deep one only delays what we capture.
            if (N.cmm_mix_start_ex(Rate, Ch, 4096, 16384, 1, out err) != 0)
            { Console.Error.WriteLine($"cmm_mix_start_ex failed: {err}"); return 1; }
            N.cmm_mix_set_master(1f);
            PushReceiverConfig();

            var wav = new WavWriter(outPath, Rate, Ch);
            float total = PreRoll + LeaveMs / 1000f + MinStaticMs / 1000f + ArriveMs / 1000f + PostRoll;
            var tl = new Timeline(aPath, bPath, startA, startB);
            var buf = new float[2048 * Ch];

            long want = (long)(total * Rate), done = 0;
            int idle = 0;
            while (done < want)
            {
                tl.ServiceUpTo((double)done / Rate);
                int got = N.cmm_mix_pump_capture(buf, 2048);
                if (got <= 0)
                {
                    // The producer thread hasn't filled the ring yet. It is a real thread doing
                    // real decoding, so it genuinely needs wall-clock time; this is the only place
                    // the harness waits, and it is not paced by the audio rate.
                    if (++idle > 4000) { Console.Error.WriteLine("producer stalled"); break; }
                    System.Threading.Thread.Sleep(1);
                    continue;
                }
                idle = 0;
                wav.Write(buf, got * Ch);
                done += got;
            }
            wav.Close();
            N.cmm_mix_stop_all_now();
            Console.WriteLine($"wrote {outPath}  ({done / (double)Rate:0.00}s)");
            tl.Report();
            return 0;
        }

        static void PushReceiverConfig()
        {
            N.cmm_mix_set_receiver_tone(Knee, KneeW, Noise, Clicks, ClickRate, HiFloor, Deemph);
            N.cmm_mix_set_receiver_shape(FloorDb, MuteW, Drive, Wander, Flutter, FlutterHz);
            N.cmm_mix_set_receiver_arrival(NoiseHold, NoiseFloor, WinA, WinB, LockAt, LockMs);
            N.cmm_mix_set_receiver_radio(Mangle, MakeupDb, LowCut, HighCut);
        }

        // Read a real Config/radio.xml so the simulator can render what a PLAYER hears, not what
        // the defaults intend. The two diverging is a failure mode this tool exists to catch.
        static void LoadConfig(string path)
        {
            if (!File.Exists(path)) { Console.Error.WriteLine($"config not found: {path}"); return; }
            var doc = new System.Xml.XmlDocument(); doc.Load(path);
            void G(string name, ref float f)
            {
                var n = doc.SelectSingleNode("//" + name);
                if (n != null && float.TryParse(n.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                { if (Math.Abs(v - f) > 1e-6f) Console.WriteLine($"  config: {name} {f} -> {v}"); f = v; }
            }
            G("RadioRecvKnee", ref Knee); G("RadioRecvKneeWidth", ref KneeW);
            G("RadioRecvNoise", ref Noise); G("RadioRecvClicks", ref Clicks); G("RadioRecvClickRate", ref ClickRate);
            G("RadioRecvHiFloorHz", ref HiFloor); G("RadioRecvDeemphHz", ref Deemph);
            G("RadioRecvFloorDb", ref FloorDb); G("RadioRecvMuteWidth", ref MuteW); G("RadioRecvDrive", ref Drive);
            G("RadioRecvWander", ref Wander); G("RadioRecvFlutter", ref Flutter); G("RadioRecvFlutterHz", ref FlutterHz);
            G("RadioRecvNoiseHold", ref NoiseHold); G("RadioRecvNoiseFloor", ref NoiseFloor);
            G("RadioRecvWindowA", ref WinA); G("RadioRecvWindowB", ref WinB);
            G("RadioRecvLockAt", ref LockAt); G("RadioRecvLockMs", ref LockMs);
            G("RadioRecvMangle", ref Mangle); G("RadioRecvMakeupDb", ref MakeupDb);
            G("RadioRecvLowCutHz", ref LowCut); G("RadioRecvHighCutHz", ref HighCut);
            G("RadioTuneDullness", ref Dullness); G("RadioTuneMononess", ref Mononess);
            G("RadioTuneLeaveMs", ref LeaveMs); G("RadioTuneMinStaticMs", ref MinStaticMs);
            G("RadioTuneArriveMs", ref ArriveMs);
        }

        // ── The dial press, as a sequence of native calls on a clock ──────────────────────────
        // Mirrors PersonalRadio.Tune() → the tick's deferred dispatch → CommitPendingStart() →
        // the ARRIVE branch. Read alongside that file.
        sealed class Timeline
        {
            readonly string _a, _b; readonly double _startA, _startB;
            double _pressAt, _swapAt, _arriveAt;
            int _step; long _voiceA, _voiceB;
            public string Log = "";

            public Timeline(string a, string b, double sa, double sb)
            { _a = a; _b = b; _startA = sa; _startB = sb; _pressAt = PreRoll; }

            public void ServiceUpTo(double t)
            {
                // 0: station A is on and fully tuned.
                if (_step == 0)
                {
                    _voiceA = Add(_a, _startA, fade: 0.01f, gain: GainA);
                    N.cmm_mix_set_voice_radio(_voiceA, 1);
                    N.cmm_mix_set_receiver_signal(1f, 0f, Dullness, Mononess, 1, GainA);
                    Note(t, $"station A up, voice {_voiceA}");
                    _step = 1;
                }
                // 1: THE PRESS. Aim the signal down; the outgoing voice is RELEASED, not faded —
                //    it plays on and the receiver buries it. Swap and arrive are anchored after
                //    the leave, exactly as Tune() does.
                if (_step == 1 && t >= _pressAt)
                {
                    double leaveSec = LeaveMs / 1000.0, searchSec = MinStaticMs / 1000.0;
                    N.cmm_mix_set_receiver_signal(0f, 1000f / LeaveMs, Dullness, Mononess, 1, GainA);
                    _swapAt   = t + leaveSec + searchSec * 0.5;
                    _arriveAt = t + leaveSec + searchSec;
                    Note(t, $"PRESS — leave {leaveSec:0.00}s, swap +{_swapAt - t:0.00}s, arrive +{_arriveAt - t:0.00}s");
                    _step = 2;
                }
                // 2: the handover, down in the static. Adding B crossfades A out over SwapFade —
                //    the add IS the retire, which is why it must not happen before now.
                if (_step == 2 && t >= _swapAt + SeekSec)
                {
                    _voiceB = Add(_b, _startB, fade: SwapFade, gain: GainB);
                    N.cmm_mix_set_voice_radio(_voiceB, 1);
                    Note(t, $"SWAP — station B added, voice {_voiceB} (A crossfades out over {SwapFade:0.00}s)");
                    _step = 3;
                }
                // 3: ARRIVE.
                if (_step == 3 && t >= _arriveAt)
                {
                    N.cmm_mix_set_receiver_signal(1f, 1000f / ArriveMs, Dullness, Mononess, 1, GainB);
                    Note(t, $"ARRIVE — {ArriveMs / 1000f:0.00}s");
                    _step = 4;
                }
                // The 4 Hz re-arm the game pushes from the top of Tick.
                N.cmm_mix_set_receiver_active(1, _step >= 3 ? GainB : GainA);
            }

            long Add(string path, double startSec, float fade, float gain)
            {
                IntPtr dec = N.cmm_mp3_open_file(path, out int ch, out int rate, out long total, out int oerr);
                if (dec == IntPtr.Zero) throw new IOException($"cannot open {path} (err {oerr})");
                long startFrame = (long)(startSec * rate);
                if (total > 0 && startFrame >= total) startFrame = 0;
                // kind 3 = mp3, matching AudioDecoder's mapping. duckOthersTo -1 = an ordinary
                // music add (crossfades every other music voice out), snapTight 1 as a dial does.
                return N.cmm_mix_add(dec, 3, rate, ch, total, fade, 0, startFrame, gain, -1f, 1);
            }

            void Note(double t, string what) { Log += $"  {t,6:0.000}s  {what}\n"; }
            public void Report() { Console.Write(Log); }
        }

        sealed class WavWriter
        {
            readonly BinaryWriter _w; long _bytes;
            public WavWriter(string path, int rate, int ch)
            {
                _w = new BinaryWriter(File.Create(path));
                _w.Write(new char[] { 'R', 'I', 'F', 'F' }); _w.Write(0);
                _w.Write(new char[] { 'W', 'A', 'V', 'E' });
                _w.Write(new char[] { 'f', 'm', 't', ' ' }); _w.Write(16); _w.Write((short)1);
                _w.Write((short)ch); _w.Write(rate); _w.Write(rate * ch * 2);
                _w.Write((short)(ch * 2)); _w.Write((short)16);
                _w.Write(new char[] { 'd', 'a', 't', 'a' }); _w.Write(0);
            }
            public void Write(float[] s, int n)
            {
                for (int i = 0; i < n; i++)
                {
                    int v = (int)(s[i] * 32767f);
                    if (v > 32767) v = 32767; if (v < -32768) v = -32768;
                    _w.Write((short)v);
                }
                _bytes += n * 2;
            }
            public void Close()
            {
                _w.Seek(4, SeekOrigin.Begin); _w.Write((int)(36 + _bytes));
                _w.Seek(40, SeekOrigin.Begin); _w.Write((int)_bytes);
                _w.Close();
            }
        }
    }
}
