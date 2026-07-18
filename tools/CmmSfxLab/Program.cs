// CmmSfxLab — radio-SFX tuning lab. Plays the mod's synthesized dial sounds (tuning static,
// power on/off clicks) through the REAL native synth (CMM_Decoder.dll, same cmm_mix_* calls
// the game makes) with a slider per settings.xml knob. "Static" sustains via the same 4 Hz
// keepalive ping PersonalRadio uses, so slider edits are audible mid-hash exactly as a
// hot-reloaded config edit is in-game. Load/Save round-trip Config\settings.xml in place.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Xml;

namespace CmmSfxLab
{
    internal static class Native
    {
        const string Dll = "CMM_Decoder";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int  CMM_Decoder_abi();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern int  cmm_mix_start(int busRate, int busChannels, int tightFrames, int maxFrames, out int err);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_stop(float fade, int closeWhenIdle);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_play_sfx(int kind, float gain);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_stop_sfx(int kind);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_set_static_tone(float lowHz, float highHz, float crackle);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmm_mix_set_click_tone(float onHz, float onSnap, float onLenMs,
                                                                                                                         float onBody, float onBodyHz, float onDouble,
                                                                                                                         float offHz, float offSnap, float offLenMs,
                                                                                                                         float offBody, float offBodyHz, float offDouble);
        internal const int SfxStatic = 1, SfxClickOn = 2, SfxClickOff = 3;
    }

    // One settings knob bound to a slider: settings.xml key, display scale (slider is integer
    // ticks; value = ticks / Scale) and a formatter for the value label.
    internal sealed class Knob
    {
        public string Key;
        public TrackBar Bar;
        public Label Val;
        public float Scale = 1f;
        public string Fmt = "0";
        public float Value => Bar.Value / Scale;
        public void Set(float v)
        {
            int t = (int)Math.Round(v * Scale);
            if (t < Bar.Minimum) t = Bar.Minimum;
            if (t > Bar.Maximum) t = Bar.Maximum;
            Bar.Value = t;
        }
    }

    internal sealed class LabForm : Form
    {
        readonly List<Knob> _knobs = new List<Knob>();
        readonly Dictionary<string, Knob> _byKey = new Dictionary<string, Knob>();
        readonly Dictionary<string, CheckBox> _inherit = new Dictionary<string, CheckBox>();

        TextBox _pathBox;
        Button _staticBtn;
        Timer _staticTimer;      // the 4 Hz keepalive ping, exactly PersonalRadio's cadence
        bool _staticOn;
        int _y;                  // layout cursor
        Control _host;           // scrollable content panel

        // Auto-preview: touching a knob plays its sound without pressing a button. STATIC knobs
        // ping the keepalive immediately (throttled), so dragging sustains a live hash that
        // tracks the slider; CLICK knobs are one-shots, so they replay debounced after the drag
        // settles. LoadXml sets sliders programmatically — _loading mutes preview during it.
        CheckBox _previewCb;
        Timer _previewTimer;     // click-knob debounce
        int _pendingClickKind;
        DateTime _lastStaticPing = DateTime.MinValue;
        bool _loading;

        public LabForm()
        {
            Text = "CMM SFX Lab — radio dial sounds";
            ClientSize = new Size(600, 920);
            MinimumSize = new Size(560, 400);

            _host = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            Controls.Add(_host);
            _y = 12;

            BuildFileRow();
            BuildPlayRow();

            Section("General");
            AddKnob("RadioSfxVolume", "Master SFX volume", 0, 100, 100f, "0.00", 50, Native.SfxStatic);

            Section("Tuning static  (drag a slider — the static follows live)");
            AddInheritKnob("RadioStaticVolume", "Static volume", 50, Native.SfxStatic);
            AddKnob("RadioStaticLowHz", "Low cut (Hz)", 50, 4000, 1f, "0", 350, Native.SfxStatic);
            AddKnob("RadioStaticHighHz", "Top cap (Hz)", 400, 12000, 1f, "0", 2000, Native.SfxStatic);
            AddKnob("RadioStaticCrackle", "Crackle (0 smooth → 1 sputter)", 0, 100, 100f, "0.00", 80, Native.SfxStatic);

            Section("Power-ON click  (replays after each adjustment)");
            AddKnob("RadioClickOnHz", "Tone cap (Hz)", 300, 10000, 1f, "0", 3200, Native.SfxClickOn);
            AddKnob("RadioClickOnSnap", "Snap (0 thud → 1 tick)", 0, 100, 100f, "0.00", 70, Native.SfxClickOn);
            AddKnob("RadioClickOnLengthMs", "Length (ms)", 5, 120, 1f, "0", 18, Native.SfxClickOn);
            AddKnob("RadioClickOnBody", "Body knock (0 off → 1 full)", 0, 100, 100f, "0.00", 40, Native.SfxClickOn);
            AddKnob("RadioClickOnBodyHz", "Body pitch (Hz)", 200, 3000, 1f, "0", 1000, Native.SfxClickOn);
            AddKnob("RadioClickOnDouble", "Double strike (0 single → 1)", 0, 100, 100f, "0.00", 35, Native.SfxClickOn);
            AddInheritKnob("RadioClickOnVolume", "Click volume", 50, Native.SfxClickOn);

            Section("Power-OFF click  (replays after each adjustment)");
            AddKnob("RadioClickOffHz", "Tone cap (Hz)", 300, 10000, 1f, "0", 1300, Native.SfxClickOff);
            AddKnob("RadioClickOffSnap", "Snap (0 thud → 1 tick)", 0, 100, 100f, "0.00", 45, Native.SfxClickOff);
            AddKnob("RadioClickOffLengthMs", "Length (ms)", 5, 120, 1f, "0", 28, Native.SfxClickOff);
            AddKnob("RadioClickOffBody", "Body knock (0 off → 1 full)", 0, 100, 100f, "0.00", 40, Native.SfxClickOff);
            AddKnob("RadioClickOffBodyHz", "Body pitch (Hz)", 200, 3000, 1f, "0", 750, Native.SfxClickOff);
            AddKnob("RadioClickOffDouble", "Double strike (0 single → 1)", 0, 100, 100f, "0.00", 20, Native.SfxClickOff);
            AddInheritKnob("RadioClickOffVolume", "Click volume", 50, Native.SfxClickOff);

            _staticTimer = new Timer { Interval = 250 };
            _staticTimer.Tick += (s, e) => PingStatic();

            _previewTimer = new Timer { Interval = 300 };
            _previewTimer.Tick += (s, e) =>
            {
                _previewTimer.Stop();
                if (_pendingClickKind == Native.SfxClickOn)  PlayClick(Native.SfxClickOn,  "RadioClickOnVolume");
                if (_pendingClickKind == Native.SfxClickOff) PlayClick(Native.SfxClickOff, "RadioClickOffVolume");
            };

            Load += (s, e) => StartMixer();
            FormClosing += (s, e) =>
            {
                _staticTimer.Stop();
                try { Native.cmm_mix_stop_sfx(Native.SfxStatic); Native.cmm_mix_stop(0.05f, 1); } catch { }
            };

            string found = FindSettingsXml();
            if (found != null) { _pathBox.Text = found; LoadXml(found, silent: true); }
        }

        // ── Audio ───────────────────────────────────────────────────────────────────────────────

        void StartMixer()
        {
            try
            {
                int abi = Native.CMM_Decoder_abi();
                Text += $"   (native ABI {abi})";
                Native.cmm_mix_start(48000, 2, 4800, 9600, out int err);
                if (err != 0) MessageBox.Show(this, $"Mixer start failed (err {err}).", "CMM SFX Lab");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "CMM_Decoder.dll failed to load:\n" + ex.Message, "CMM SFX Lab");
            }
        }

        float EffVolume(string ownKey)
        {
            // Mirrors PersonalRadio.Sfx: the sound's own volume, or the master when inheriting.
            if (_inherit.TryGetValue(ownKey, out CheckBox cb) && !cb.Checked)
                return _byKey[ownKey].Value;
            return _byKey["RadioSfxVolume"].Value;
        }

        void PushStaticTone() =>
            Native.cmm_mix_set_static_tone(_byKey["RadioStaticLowHz"].Value,
                                           _byKey["RadioStaticHighHz"].Value,
                                           _byKey["RadioStaticCrackle"].Value);

        void PushClickTone() =>
            Native.cmm_mix_set_click_tone(_byKey["RadioClickOnHz"].Value,  _byKey["RadioClickOnSnap"].Value,  _byKey["RadioClickOnLengthMs"].Value,
                                          _byKey["RadioClickOnBody"].Value,  _byKey["RadioClickOnBodyHz"].Value,  _byKey["RadioClickOnDouble"].Value,
                                          _byKey["RadioClickOffHz"].Value, _byKey["RadioClickOffSnap"].Value, _byKey["RadioClickOffLengthMs"].Value,
                                          _byKey["RadioClickOffBody"].Value, _byKey["RadioClickOffBodyHz"].Value, _byKey["RadioClickOffDouble"].Value);

        void PingStatic()
        {
            try { PushStaticTone(); Native.cmm_mix_play_sfx(Native.SfxStatic, EffVolume("RadioStaticVolume")); }
            catch { }
        }

        void ToggleStatic()
        {
            _staticOn = !_staticOn;
            if (_staticOn) { PingStatic(); _staticTimer.Start(); _staticBtn.Text = "■  Stop static"; }
            else { _staticTimer.Stop(); try { Native.cmm_mix_stop_sfx(Native.SfxStatic); } catch { } _staticBtn.Text = "▶  Static"; }
        }

        void PlayClick(int kind, string volKey)
        {
            try { PushClickTone(); Native.cmm_mix_play_sfx(kind, EffVolume(volKey)); } catch { }
        }

        // A knob was touched: audition it. Static knobs ping the keepalive NOW (throttled to
        // ~80ms) — dragging holds one live hash that tracks the slider, and it self-releases
        // ~0.4s after the drag stops. Click knobs debounce, then replay their one-shot.
        void Preview(int kind)
        {
            if (_loading || _previewCb == null || !_previewCb.Checked) return;
            if (kind == Native.SfxStatic)
            {
                if (_staticOn) return;   // the toggle's 4 Hz timer already keeps it live
                if ((DateTime.UtcNow - _lastStaticPing).TotalMilliseconds < 80) { PushStaticTone(); return; }
                _lastStaticPing = DateTime.UtcNow;
                PingStatic();
            }
            else
            {
                _pendingClickKind = kind;
                _previewTimer.Stop();
                _previewTimer.Start();
            }
        }

        // ── Settings.xml round-trip ─────────────────────────────────────────────────────────────

        static string FindSettingsXml()
        {
            // Walk up from the exe (tools\CmmSfxLab\bin\...) to the repo root's Config\settings.xml.
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string probe = Path.Combine(dir, "Config", "settings.xml");
                if (File.Exists(probe)) return probe;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return null;
        }

        void LoadXml(string path, bool silent = false)
        {
            try
            {
                _loading = true;   // programmatic slider moves must not auto-preview
                var doc = new XmlDocument();
                doc.Load(path);
                foreach (Knob k in _knobs)
                {
                    XmlNode n = doc.DocumentElement?.SelectSingleNode(k.Key);
                    if (n == null) continue;
                    if (!float.TryParse(n.InnerText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) continue;
                    if (_inherit.TryGetValue(k.Key, out CheckBox cb))
                    {
                        cb.Checked = v < 0f;
                        if (v >= 0f) k.Set(v);
                    }
                    else k.Set(v);
                }
                if (!silent) MessageBox.Show(this, "Loaded.", "CMM SFX Lab");
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show(this, "Load failed:\n" + ex.Message, "CMM SFX Lab");
            }
            finally { _loading = false; }
        }

        void SaveXml(string path)
        {
            try
            {
                var doc = new XmlDocument { PreserveWhitespace = true };   // keep the file's comments/layout
                doc.Load(path);
                XmlElement root = doc.DocumentElement;
                if (root == null) throw new InvalidOperationException("no root element");
                foreach (Knob k in _knobs)
                {
                    string text = _inherit.TryGetValue(k.Key, out CheckBox cb) && cb.Checked
                        ? "-1"
                        : k.Value.ToString(k.Fmt.Contains(".") ? "0.##" : "0", CultureInfo.InvariantCulture);
                    XmlNode n = root.SelectSingleNode(k.Key);
                    if (n == null)
                    {
                        root.AppendChild(doc.CreateTextNode("  "));
                        n = root.AppendChild(doc.CreateElement(k.Key));
                        root.AppendChild(doc.CreateTextNode("\n"));
                    }
                    n.InnerText = text;
                }
                doc.Save(path);
                MessageBox.Show(this, "Saved to:\n" + path +
                    "\n\nA running game hot-reloads a deployed copy; the repo template ships on the next build.",
                    "CMM SFX Lab");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Save failed:\n" + ex.Message, "CMM SFX Lab");
            }
        }

        // ── UI construction ─────────────────────────────────────────────────────────────────────

        void BuildFileRow()
        {
            _pathBox = new TextBox { Left = 12, Top = _y, Width = 380, ReadOnly = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            var open = new Button { Left = 398, Top = _y - 1, Width = 56, Text = "Open…", Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var load = new Button { Left = 458, Top = _y - 1, Width = 56, Text = "Reload", Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var save = new Button { Left = 518, Top = _y - 1, Width = 56, Text = "Save", Anchor = AnchorStyles.Top | AnchorStyles.Right };
            open.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = "settings.xml|settings.xml|XML|*.xml", Title = "Open a settings.xml (repo template or a deployed game copy)" })
                {
                    if (!string.IsNullOrEmpty(_pathBox.Text)) dlg.InitialDirectory = Path.GetDirectoryName(_pathBox.Text);
                    if (dlg.ShowDialog(this) == DialogResult.OK) { _pathBox.Text = dlg.FileName; LoadXml(dlg.FileName); }
                }
            };
            load.Click += (s, e) => { if (File.Exists(_pathBox.Text)) LoadXml(_pathBox.Text); };
            save.Click += (s, e) => { if (File.Exists(_pathBox.Text)) SaveXml(_pathBox.Text); else MessageBox.Show(this, "Open a settings.xml first.", "CMM SFX Lab"); };
            _host.Controls.Add(_pathBox); _host.Controls.Add(open); _host.Controls.Add(load); _host.Controls.Add(save);
            _y += 34;
        }

        void BuildPlayRow()
        {
            _staticBtn = new Button { Left = 12, Top = _y, Width = 130, Height = 34, Text = "▶  Static" };
            var on  = new Button { Left = 150, Top = _y, Width = 130, Height = 34, Text = "Click ON" };
            var off = new Button { Left = 288, Top = _y, Width = 130, Height = 34, Text = "Click OFF" };
            var dial = new Button { Left = 426, Top = _y, Width = 148, Height = 34, Text = "Dial: on→static→off" };
            _staticBtn.Click += (s, e) => ToggleStatic();
            on.Click  += (s, e) => PlayClick(Native.SfxClickOn, "RadioClickOnVolume");
            off.Click += (s, e) => PlayClick(Native.SfxClickOff, "RadioClickOffVolume");
            dial.Click += async (s, e) =>
            {
                // The full power-on gesture as the game plays it: click, static while "joining", off clunk.
                if (_staticOn) ToggleStatic();
                PlayClick(Native.SfxClickOn, "RadioClickOnVolume");
                await System.Threading.Tasks.Task.Delay(150);
                ToggleStatic();
                await System.Threading.Tasks.Task.Delay(1500);
                if (_staticOn) ToggleStatic();
                await System.Threading.Tasks.Task.Delay(250);
                PlayClick(Native.SfxClickOff, "RadioClickOffVolume");
            };
            _host.Controls.Add(_staticBtn); _host.Controls.Add(on); _host.Controls.Add(off); _host.Controls.Add(dial);
            _y += 40;
            _previewCb = new CheckBox
            {
                Left = 12, Top = _y, Width = 400, Checked = true,
                Text = "Preview on change (drag = live static; clicks replay after adjusting)",
            };
            _host.Controls.Add(_previewCb);
            _y += 26;
        }

        void Section(string title)
        {
            var lbl = new Label
            {
                Left = 12, Top = _y + 6, Width = 560, Text = title,
                Font = new Font(Font, FontStyle.Bold),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            _host.Controls.Add(lbl);
            _y += 30;
        }

        Knob AddKnob(string key, string label, int min, int max, float scale, string fmt, int initialTicks, int previewKind)
        {
            var name = new Label { Left = 12, Top = _y + 4, Width = 210, Text = label };
            var bar = new TrackBar
            {
                Left = 226, Top = _y, Width = 280, Minimum = min, Maximum = max,
                TickStyle = TickStyle.None, AutoSize = false, Height = 28,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            var val = new Label { Left = 512, Top = _y + 4, Width = 64, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var k = new Knob { Key = key, Bar = bar, Val = val, Scale = scale, Fmt = fmt };
            bar.Value = Math.Max(min, Math.Min(max, initialTicks));
            bar.ValueChanged += (s, e) =>
            {
                val.Text = k.Value.ToString(fmt, CultureInfo.InvariantCulture);
                if (_staticOn) PushStaticTone();   // live mid-hash, like a hot config edit in-game
                Preview(previewKind);
            };
            val.Text = k.Value.ToString(fmt, CultureInfo.InvariantCulture);
            _host.Controls.Add(name); _host.Controls.Add(bar); _host.Controls.Add(val);
            _knobs.Add(k); _byKey[key] = k;
            _y += 32;
            return k;
        }

        void AddInheritKnob(string key, string label, int initialTicks, int previewKind)
        {
            Knob k = AddKnob(key, label, 0, 100, 100f, "0.00", initialTicks, previewKind);
            var cb = new CheckBox { Left = 226, Top = _y - 4, Width = 280, Text = "inherit master SFX volume (-1)", Checked = true };
            cb.CheckedChanged += (s, e) => { k.Bar.Enabled = !cb.Checked; k.Val.Enabled = !cb.Checked; Preview(previewKind); };
            k.Bar.Enabled = false; k.Val.Enabled = false;
            _host.Controls.Add(cb);
            _inherit[key] = cb;
            _y += 26;
        }
    }

    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LabForm());
        }
    }
}
