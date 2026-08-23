// CmmRadioLab — the station-change tuning bench, with a UI.
//
// Renders a dial press two ways and A/Bs them: GAME (CmmRadioSim, driving the shipping
// CMM_Decoder.dll headless) against REFERENCE (CmmTuneRender, the model approved by ear). Those
// two agreeing is the acceptance test the radio never had.
//
// Every knob is a Radio* key in Config\radio.xml. Load reports every value in a live install that
// differs from the shipped default — the missing check that sent the whole tuning effort to values
// nobody approved.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Media;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace CmmRadioLab
{
    // One tunable: the settings.xml key, a range for the slider, and the shipped default so the
    // UI can say "this install disagrees with the code" out loud.
    internal sealed class Knob
    {
        public string Key, Group, Help;
        public float Min, Max, Default, Value;
        public int Decimals;
        public TrackBar Bar; public NumericUpDown Num; public Label Tag;
        public Knob(string g, string k, float min, float max, float def, int dec, string help)
        { Group = g; Key = k; Min = min; Max = max; Default = def; Value = def; Decimals = dec; Help = help; }
        public bool Differs => Math.Abs(Value - Default) > 1e-6f;
    }

    internal static class Program
    {
        static readonly List<Knob> Knobs = new List<Knob>
        {
            // ── the shape of the event ────────────────────────────────────────────────────────
            new Knob("Timing", "RadioTuneLeaveMs",      100, 6000, 1200, 0, "How long the station you are leaving takes to sink under the static."),
            new Knob("Timing", "RadioTuneMinStaticMs",  100, 6000, 1500, 0, "Least time spent in pure static. A slow file makes it longer, never shorter."),
            new Knob("Timing", "RadioTuneArriveMs",     100, 8000, 2800, 0, "How long the new station takes to climb out of the static."),

            // ── what the static sounds like ───────────────────────────────────────────────────
            new Knob("Static", "RadioRecvNoise",        0f, 2.0f, 0.55f, 3, "How loud the hiss gets. ABOVE ~1.0 IT HITS AN INTERNAL CEILING and stops moving — a flat wall instead of a receiver."),
            new Knob("Static", "RadioRecvClicks",       0f, 1.5f, 0.18f, 3, "Crackle depth. These are meant to be separate ticks."),
            new Knob("Static", "RadioRecvClickRate",    10f, 1200f, 180f, 0, "Ticks per second at the very bottom. Much above ~300 they merge into a buzz rather than getting denser."),
            new Knob("Static", "RadioRecvWander",       0f, 1.5f, 0.55f, 3, "Slow drift in the hiss level — a real set breathing. This is why two renders never measure the same."),
            new Knob("Static", "RadioRecvFlutter",      0f, 1.5f, 0.30f, 3, "Faster movement on top. Built from filtered noise, so it has no rate you can hear counting."),
            new Knob("Static", "RadioRecvFlutterHz",    1f, 60f, 14f, 1, "Top of the flutter's speed range."),
            new Knob("Static", "RadioRecvDeemphHz",     500f, 6000f, 2122f, 0, "The hiss's own tone. 2122 = US 75us, 3183 = EU 50us. Lower is a darker roar."),

            // ── the break-up ─────────────────────────────────────────────────────────────────
            new Knob("Break-up", "RadioRecvKnee",       0.05f, 0.95f, 0.45f, 3, "Where in the fade the break-up happens."),
            new Knob("Break-up", "RadioRecvKneeWidth",  0.02f, 1.0f, 0.22f, 3, "How much of the fade it occupies. NARROW IS RIGHT — widening it is what makes the whole thing smooth and fake."),
            new Knob("Break-up", "RadioRecvFloorDb",    0f, 40f, 5f, 1, "How far the station is allowed to sink, in dB. SMALL IS RIGHT: pull it down far and it is a fade, whatever noise you put over it."),
            new Knob("Break-up", "RadioRecvMuteWidth",  0.5f, 40f, 5f, 1, "The span that sink happens over, so the station drops WHILE the hiss rises."),
            new Knob("Break-up", "RadioRecvDrive",      0f, 1.0f, 0.15f, 3, "How hard a weak station is squashed."),
            new Knob("Break-up", "RadioRecvHiFloorHz",  200f, 12000f, 1800f, 0, "How muffled it gets at the bottom."),
            new Knob("Break-up", "RadioTuneDullness",   0f, 1f, 0.85f, 3, "How much of that muffling to apply. 0 = no treble loss at all."),
            new Knob("Break-up", "RadioTuneMononess",   0f, 1f, 1f, 3, "How far the stereo image collapses. 0 = stays stereo throughout."),

            // ── the way back in ──────────────────────────────────────────────────────────────
            new Knob("Arrival", "RadioRecvMangle",      0f, 1f, 1f, 3, "How wrecked the arriving station is before it is repaired. 0 = it just fades in, which is the thing this all exists to avoid."),
            new Knob("Arrival", "RadioRecvLowCutHz",    20f, 2000f, 400f, 0, "Bass removed while it is weak. Cutting this is ALSO what turns Drive into grit instead of mud."),
            new Knob("Arrival", "RadioRecvHighCutHz",   300f, 15000f, 2600f, 0, "Treble removed while it is weak. With the low cut, roughly a telephone."),
            new Knob("Arrival", "RadioRecvMakeupDb",    -6f, 24f, 9f, 1, "Level put back. Without it a station gets QUIETER as it arrives, which reads as a fade."),
            new Knob("Arrival", "RadioRecvNoiseHold",   0f, 3f, 1f, 3, "How far into the arrival the hiss lasts. Too short and clean music fades in with nothing over it."),
            new Knob("Arrival", "RadioRecvNoiseFloor",  0f, 0.5f, 0.06f, 3, "Residual hiss a locked station keeps. Absolute zero reads as 'sound effect ended'."),
            new Knob("Arrival", "RadioRecvWindowA",     0f, 0.9f, 0.28f, 3, "Where the treble starts coming back."),
            new Knob("Arrival", "RadioRecvWindowB",     0.1f, 1f, 0.912f, 3, "...and where it finishes. NARROW is what stops it sounding like somebody turning a filter."),
            new Knob("Arrival", "RadioRecvLockAt",      0.05f, 0.95f, 0.52f, 3, "Where the stereo decoder latches. It is the LAST thing to come good."),
            new Knob("Arrival", "RadioRecvLockMs",      1f, 500f, 9f, 1, "How fast the image opens once it does."),
        };

        static readonly Dictionary<string, Knob> ByKey = new Dictionary<string, Knob>();
        static TextBox _aPath, _bPath, _aStart, _bStart, _aGain, _bGain;
        static TextBox _log;
        static Button _renderGame, _renderRef, _playGame, _playRef, _stop;
        static Label _status;
        static SoundPlayer _player = new SoundPlayer();
        static string _outDir, _simExe, _refExe, _iniPath;
        static string GameWav => Path.Combine(_outDir, "game.wav");
        static string RefWav => Path.Combine(_outDir, "reference.wav");

        [STAThread]
        static void Main()
        {
            foreach (var k in Knobs) ByKey[k.Key] = k;
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _outDir = Path.Combine(Path.GetTempPath(), "CmmRadioLab");
            Directory.CreateDirectory(_outDir);
            _iniPath = Path.Combine(baseDir, "lab.ini");
            // tools\CmmRadioLab\bin\Debug\net48 -> tools\
            string tools = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\.."));
            _simExe = Path.Combine(tools, @"CmmRadioSim\bin\Debug\net48\CmmRadioSim.exe");
            _refExe = Path.Combine(tools, @"CmmTuneRender\tunerender.exe");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(Build());
        }

        static Form Build()
        {
            var f = new Form
            {
                Text = "CmmRadioLab — station change bench",
                Size = new Size(1060, 900),
                StartPosition = FormStartPosition.CenterScreen,
                MinimumSize = new Size(900, 640)
            };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                SplitterDistance = 560, FixedPanel = FixedPanel.Panel2
            };
            f.Controls.Add(split);

            // ── knobs ────────────────────────────────────────────────────────────────────────
            var scroll = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, AutoScroll = true, Padding = new Padding(8)
            };
            split.Panel1.Controls.Add(scroll);

            scroll.Controls.Add(Sources());
            string group = null;
            foreach (var k in Knobs)
            {
                if (k.Group != group)
                {
                    group = k.Group;
                    scroll.Controls.Add(new Label
                    {
                        Text = group, AutoSize = true, Margin = new Padding(2, 12, 2, 2),
                        Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold)
                    });
                }
                scroll.Controls.Add(Row(k));
            }

            // ── actions + log ────────────────────────────────────────────────────────────────
            var bottom = new Panel { Dock = DockStyle.Fill };
            split.Panel2.Controls.Add(bottom);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 6, 6, 0) };
            _renderGame = Btn("Render GAME", (s, e) => RenderGame(true));
            _renderRef = Btn("Render REFERENCE", (s, e) => RenderReference(true));
            _playGame = Btn("Play game", (s, e) => Play(GameWav));
            _playRef = Btn("Play reference", (s, e) => Play(RefWav));
            _stop = Btn("Stop", (s, e) => { try { _player.Stop(); } catch { } });
            var loadBtn = Btn("Load radio.xml…", (s, e) => LoadXml());
            var saveBtn = Btn("Save radio.xml…", (s, e) => SaveXml());
            var defBtn = Btn("Defaults", (s, e) => { foreach (var k in Knobs) SetKnob(k, k.Default); Log("reset to shipped defaults"); });
            foreach (var b in new[] { _renderGame, _renderRef, _playGame, _playRef, _stop, loadBtn, saveBtn, defBtn })
                bar.Controls.Add(b);
            bottom.Controls.Add(bar);

            _status = new Label { Dock = DockStyle.Top, Height = 20, Padding = new Padding(8, 2, 0, 0), Text = "ready" };
            bottom.Controls.Add(_status);
            _status.BringToFront();

            _log = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 8.5f), BackColor = Color.White
            };
            bottom.Controls.Add(_log);
            _log.BringToFront();
            bar.BringToFront(); _status.BringToFront();

            LoadIni();
            Log("GAME  = " + _simExe + (File.Exists(_simExe) ? "" : "   *** NOT BUILT ***"));
            Log("REF   = " + _refExe + (File.Exists(_refExe) ? "" : "   *** NOT BUILT ***"));
            Log("out   = " + _outDir);
            Log("");
            Log("Render both, then A/B them. They should sound the same — that is the test.");
            f.FormClosing += (s, e) => SaveIni();
            return f;
        }

        static Control Sources()
        {
            var p = new TableLayoutPanel { AutoSize = true, ColumnCount = 7, Margin = new Padding(2, 2, 2, 6) };
            _aPath = new TextBox { Width = 420 };
            _bPath = new TextBox { Width = 420 };
            _aStart = new TextBox { Width = 50, Text = "21" };
            _bStart = new TextBox { Width = 50, Text = "36" };
            _aGain = new TextBox { Width = 50, Text = "0.557" };
            _bGain = new TextBox { Width = 50, Text = "0.557" };
            p.Controls.Add(new Label { Text = "Leaving", AutoSize = true, Padding = new Padding(0, 5, 4, 0) }, 0, 0);
            p.Controls.Add(_aPath, 1, 0);
            p.Controls.Add(Btn("…", (s, e) => Pick(_aPath)), 2, 0);
            p.Controls.Add(new Label { Text = "at s", AutoSize = true, Padding = new Padding(6, 5, 2, 0) }, 3, 0);
            p.Controls.Add(_aStart, 4, 0);
            p.Controls.Add(new Label { Text = "gain", AutoSize = true, Padding = new Padding(6, 5, 2, 0) }, 5, 0);
            p.Controls.Add(_aGain, 6, 0);
            p.Controls.Add(new Label { Text = "Joining", AutoSize = true, Padding = new Padding(0, 5, 4, 0) }, 0, 1);
            p.Controls.Add(_bPath, 1, 1);
            p.Controls.Add(Btn("…", (s, e) => Pick(_bPath)), 2, 1);
            p.Controls.Add(new Label { Text = "at s", AutoSize = true, Padding = new Padding(6, 5, 2, 0) }, 3, 1);
            p.Controls.Add(_bStart, 4, 1);
            p.Controls.Add(new Label { Text = "gain", AutoSize = true, Padding = new Padding(6, 5, 2, 0) }, 5, 1);
            p.Controls.Add(_bGain, 6, 1);
            p.Controls.Add(new Label
            {
                Text = "Two DIFFERENT tracks — with the same song both sides you cannot tell a handover from a dropout.",
                AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 4, 0, 0)
            }, 1, 2);
            return p;
        }

        static Control Row(Knob k)
        {
            var p = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Margin = new Padding(2, 1, 2, 1) };
            k.Tag = new Label { Text = k.Key.Replace("RadioRecv", "").Replace("RadioTune", ""), Width = 130, AutoSize = false, Padding = new Padding(0, 5, 0, 0) };
            k.Bar = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Width = 330, Value = ToBar(k, k.Value) };
            k.Num = new NumericUpDown
            {
                Width = 84, DecimalPlaces = k.Decimals, Minimum = (decimal)k.Min, Maximum = (decimal)k.Max,
                Increment = (decimal)Math.Max(0.001, (k.Max - k.Min) / 200.0), Value = (decimal)k.Value
            };
            bool guard = false;
            k.Bar.Scroll += (s, e) => { if (guard) return; guard = true; k.Value = FromBar(k, k.Bar.Value); k.Num.Value = (decimal)k.Value; Mark(k); guard = false; };
            k.Num.ValueChanged += (s, e) => { if (guard) return; guard = true; k.Value = (float)k.Num.Value; k.Bar.Value = ToBar(k, k.Value); Mark(k); guard = false; };
            p.Controls.Add(k.Tag, 0, 0); p.Controls.Add(k.Bar, 1, 0); p.Controls.Add(k.Num, 2, 0);
            p.Controls.Add(new Label { Text = k.Help, AutoSize = false, Width = 380, Height = 34, ForeColor = Color.DimGray, Padding = new Padding(6, 2, 0, 0) }, 3, 0);
            Mark(k);
            return p;
        }

        static int ToBar(Knob k, float v) => (int)Math.Round(1000.0 * (v - k.Min) / (k.Max - k.Min));
        static float FromBar(Knob k, int b) => k.Min + (k.Max - k.Min) * (b / 1000f);
        static void SetKnob(Knob k, float v)
        {
            k.Value = Math.Max(k.Min, Math.Min(k.Max, v));
            if (k.Num != null) k.Num.Value = (decimal)k.Value;
            if (k.Bar != null) k.Bar.Value = ToBar(k, k.Value);
            Mark(k);
        }
        // A knob that disagrees with the shipped default is shown in red. That is the entire
        // lesson of the effort that produced this tool.
        static void Mark(Knob k)
        {
            if (k.Tag == null) return;
            k.Tag.ForeColor = k.Differs ? Color.Firebrick : SystemColors.ControlText;
            k.Tag.Font = new Font(SystemFonts.DefaultFont, k.Differs ? FontStyle.Bold : FontStyle.Regular);
        }

        static Button Btn(string text, EventHandler h)
        { var b = new Button { Text = text, AutoSize = true, Margin = new Padding(2) }; b.Click += h; return b; }

        static void Pick(TextBox t)
        {
            using (var d = new OpenFileDialog { Filter = "Audio|*.mp3;*.flac;*.ogg;*.wav;*.opus|All files|*.*" })
            {
                if (!string.IsNullOrEmpty(t.Text) && File.Exists(t.Text)) d.InitialDirectory = Path.GetDirectoryName(t.Text);
                if (d.ShowDialog() == DialogResult.OK) t.Text = d.FileName;
            }
        }

        static float V(string key) => ByKey[key].Value;
        static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        // ── render: GAME ─────────────────────────────────────────────────────────────────────
        static void RenderGame(bool play)
        {
            if (!Check(_simExe, "CmmRadioSim")) return;
            string cfg = Path.Combine(_outDir, "knobs.xml");
            WriteXml(cfg, onlyDifferences: false);
            string args = $"\"{_aPath.Text}\" \"{_bPath.Text}\" \"{GameWav}\" --config \"{cfg}\"" +
                          $" --startA {_aStart.Text} --startB {_bStart.Text}" +
                          $" --gainA {_aGain.Text} --gainB {_bGain.Text}";
            if (Run(_simExe, args) && play) Play(GameWav);
        }

        // ── render: REFERENCE ────────────────────────────────────────────────────────────────
        // tunerender is positional and long, deliberately — it is driven from a shell loop. The
        // mapping is here rather than in a script so the two renders cannot drift apart.
        static void RenderReference(bool play)
        {
            if (!Check(_refExe, "tunerender")) return;
            float leaveS = V("RadioTuneLeaveMs") / 1000f;
            float searchS = V("RadioTuneMinStaticMs") / 1000f;
            float arriveS = V("RadioTuneArriveMs") / 1000f;
            // The reference holds fully-tuned for (secs - leave - search - arrive) * 0.45 before
            // the press. 15 s total keeps that pre-roll near 4 s at the settled timings.
            var a = new StringBuilder();
            a.Append($"\"{_aPath.Text}\" {_aStart.Text} \"{RefWav}\" ");
            a.Append($"{F(V("RadioRecvNoise"))} {F(V("RadioRecvClicks"))} {F(V("RadioRecvClickRate"))} {F(V("RadioRecvDeemphHz"))} 15 ");
            a.Append($"{F(V("RadioRecvWander"))} {F(V("RadioRecvFlutter"))} {F(V("RadioRecvFlutterHz"))} 0 ");
            a.Append($"{F(-V("RadioRecvFloorDb"))} {F(V("RadioRecvDrive"))} {F(V("RadioRecvMuteWidth"))} ");
            a.Append($"\"{_bPath.Text}\" {_bStart.Text} ");
            a.Append($"{F(leaveS)} {F(searchS)} {F(arriveS)} {F(V("RadioRecvKneeWidth"))} ");
            a.Append($"1.0 0.9 {F(V("RadioRecvNoiseHold"))} {F(V("RadioRecvNoiseFloor"))} ");
            a.Append($"{F(V("RadioRecvLockAt"))} 0.0 {F(V("RadioRecvLockMs"))} {F(V("RadioRecvHiFloorHz"))} ");
            a.Append($"{F(V("RadioRecvWindowA"))} {F(V("RadioRecvWindowB"))} 0 0 0 0  0 0 ");
            a.Append($"{F(V("RadioRecvMangle"))} {F(V("RadioRecvMakeupDb"))} {F(V("RadioRecvLowCutHz"))} {F(V("RadioRecvHighCutHz"))}");
            if (Run(_refExe, a.ToString()) && play) Play(RefWav);
        }

        static bool Check(string exe, string what)
        {
            if (File.Exists(exe)) return true;
            Log($"!! {what} is not built — expected at {exe}");
            return false;
        }

        static bool Run(string exe, string args)
        {
            _status.Text = "rendering…"; Application.DoEvents();
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                using (var p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd(), se = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    Log($"$ {Path.GetFileName(exe)}");
                    foreach (var line in (so + se).Split('\n')) if (line.Trim().Length > 0) Log("  " + line.TrimEnd());
                    _status.Text = p.ExitCode == 0 ? "done" : $"FAILED (exit {p.ExitCode})";
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex) { Log("!! " + ex.Message); _status.Text = "failed"; return false; }
        }

        static void Play(string wav)
        {
            if (!File.Exists(wav)) { Log("!! not rendered yet: " + Path.GetFileName(wav)); return; }
            try { _player.Stop(); _player.SoundLocation = wav; _player.Load(); _player.Play(); _status.Text = "playing " + Path.GetFileName(wav); }
            catch (Exception ex) { Log("!! playback: " + ex.Message); }
        }

        // ── radio.xml ────────────────────────────────────────────────────────────────────────
        static void LoadXml()
        {
            using (var d = new OpenFileDialog { Filter = "radio.xml|radio.xml|XML|*.xml", FileName = "radio.xml" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                var doc = new XmlDocument(); doc.Load(d.FileName);
                int diff = 0, found = 0;
                Log(""); Log("loaded " + d.FileName);
                foreach (var k in Knobs)
                {
                    var n = doc.SelectSingleNode("//" + k.Key);
                    if (n == null) continue;
                    found++;
                    if (!float.TryParse(n.InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) continue;
                    SetKnob(k, v);
                    if (k.Differs) { diff++; Log($"  DIFFERS  {k.Key,-24} file {F(v),-9} shipped default {F(k.Default)}"); }
                }
                Log(diff == 0
                    ? $"  {found} values read, all match the shipped defaults."
                    : $"  {found} values read, {diff} DIFFER from the shipped defaults — those are what the game actually plays.");
            }
        }

        static void SaveXml()
        {
            using (var d = new SaveFileDialog { Filter = "radio.xml|radio.xml|XML|*.xml", FileName = "radio.xml" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                if (File.Exists(d.FileName))
                {
                    // Edit in place so comments, stamp and every key this tool does not own survive.
                    File.Copy(d.FileName, d.FileName + ".bak", true);
                    var doc = new XmlDocument(); doc.Load(d.FileName);
                    int wrote = 0;
                    foreach (var k in Knobs)
                    {
                        var n = doc.SelectSingleNode("//" + k.Key);
                        if (n == null) continue;
                        n.InnerText = F(k.Value); wrote++;
                    }
                    doc.Save(d.FileName);
                    Log($"saved {wrote} values into {d.FileName}  (backup: .bak)");
                }
                else { WriteXml(d.FileName, false); Log("wrote a new " + d.FileName); }
            }
        }

        static void WriteXml(string path, bool onlyDifferences)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<CMMRadioSettings schema=\"11\">");
            foreach (var k in Knobs)
                if (!onlyDifferences || k.Differs)
                    sb.AppendLine($"  <{k.Key}>{F(k.Value)}</{k.Key}>");
            sb.AppendLine("</CMMRadioSettings>");
            File.WriteAllText(path, sb.ToString());
        }

        // ── window state ─────────────────────────────────────────────────────────────────────
        static void LoadIni()
        {
            try
            {
                if (!File.Exists(_iniPath)) return;
                foreach (var line in File.ReadAllLines(_iniPath))
                {
                    int eq = line.IndexOf('='); if (eq <= 0) continue;
                    string key = line.Substring(0, eq), val = line.Substring(eq + 1);
                    switch (key)
                    {
                        case "A": _aPath.Text = val; break;
                        case "B": _bPath.Text = val; break;
                        case "AS": _aStart.Text = val; break;
                        case "BS": _bStart.Text = val; break;
                        case "AG": _aGain.Text = val; break;
                        case "BG": _bGain.Text = val; break;
                        default:
                            if (ByKey.TryGetValue(key, out Knob k) &&
                                float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                                SetKnob(k, v);
                            break;
                    }
                }
            }
            catch { }
        }

        static void SaveIni()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("A=" + _aPath.Text); sb.AppendLine("B=" + _bPath.Text);
                sb.AppendLine("AS=" + _aStart.Text); sb.AppendLine("BS=" + _bStart.Text);
                sb.AppendLine("AG=" + _aGain.Text); sb.AppendLine("BG=" + _bGain.Text);
                foreach (var k in Knobs) sb.AppendLine(k.Key + "=" + F(k.Value));
                File.WriteAllText(_iniPath, sb.ToString());
            }
            catch { }
        }

        static void Log(string s)
        {
            if (_log == null) return;
            _log.AppendText(s + Environment.NewLine);
        }
    }
}
