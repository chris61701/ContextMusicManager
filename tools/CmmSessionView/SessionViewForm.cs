using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────────
    // SessionViewForm — the top-down viewer. Owns a SessionRunner and drives it on a
    // UI-thread game loop (a timer), rendering the live session as a board + HUD with
    // GDI+. Sim and paint both run on the UI thread, so no cross-thread state guarding.
    //
    //   • real-time : advance one sim tick per Options.Tick wall-seconds → the native
    //                 mixer plays at natural rate (DecodeEnabled on, volume up).
    //   • max       : free-run the sim as fast as the frame allows, muted + decode-gated
    //                 off (the realtime mixer is never fed while fast-forwarding).
    //
    // The world (SessionGenerator) is abstract, not spatial — the board is a PROJECTION:
    // fixed landmarks, and the player token EASES toward the landmark the GameState says
    // we're at. Positions are cosmetic; every event (context, combat, blood moon) is real.
    // ─────────────────────────────────────────────────────────────────────────────
    internal sealed class SessionViewForm : Form
    {
        private readonly SessionRunner _runner;
        private readonly float _tickSec;
        private readonly Timer _timer;
        private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();
        private double _lastWall;
        private float _animT;       // wall seconds, drives pulses/animation

        // ── Speed / run state ──
        private bool _fast;         // false = real-time (audio), true = max (silent free-run)
        private bool _paused;
        private double _rtAccum;    // real-time pacing accumulator (wall seconds)
        private const int FastBatchCap = 400;   // sim ticks advanced per frame in max mode

        // ── Live render state (updated as ticks are ingested) ──
        private GameState _gs;
        private string _top = "—";
        private string _file = "—";
        private bool _normalized;
        private long _ticksRun;
        private readonly Queue<(float t, string top, string file)> _ticker = new Queue<(float, string, string)>();

        // Player token position in normalized board space [0..1], eased toward the target.
        private PointF _player = new PointF(0.14f, 0.20f);
        // Eased lighting so day↔night and blood-moon transitions are smooth, not instant.
        private float _dayLevel = 1f, _bloodLevel = 0f;
        private Point _mousePt = new Point(-1, -1);

        // ── Interactive sandbox ──
        private enum Mode { Autopilot, Manual }
        private Mode _mode = Mode.Autopilot;
        private readonly GameState _manual = new GameState();   // hand-authored snapshot fed in manual mode
        private int _locIdx, _biomeIdx;
        private static readonly string[] LocNames = { "Home", "Trader", "POI", "Travel" };
        private static readonly string[] BiomeNames = { "pine_forest", "desert", "burnt_forest", "snow", "wasteland" };
        private static readonly string[] WeakClasses = { "zombieStdWalk", "animalZombieDog", "zombieBoe", "zombieSteveCrawler" };
        private static readonly string[] HeavyClasses = { "zombieDemolition", "zombieRadiatedCop", "zombieFeralWight", "zombieFatCopRadiated" };

        // Custom control-strip buttons, rebuilt each paint; hit-tested on mouse-down.
        private struct Btn { public Rectangle R; public string Id, Label; public bool On, Enabled; }
        private readonly List<Btn> _buttons = new List<Btn>();

        public SessionViewForm(SessionRunner runner, bool startFast = false)
        {
            _runner = runner;
            _tickSec = runner.Options.Tick;

            Text = "CmmSessionView";
            BackColor = C(0x08_0606);
            ClientSize = new Size(1360, 820);
            MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            BuildFonts();

            // Start in real-time (audio) unless asked to launch straight into max-speed.
            _fast = startFast;
            _runner.DecodeEnabled = !startFast;
            CmmMixer.MasterVolume = startFast ? 0f : 1f;

            _timer = new Timer { Interval = 16 };   // ~60 fps
            _timer.Tick += OnFrame;
            _timer.Start();
        }

        // ─────────────────────────── game loop ───────────────────────────
        private void OnFrame(object sender, EventArgs e)
        {
            double now = _wall.Elapsed.TotalSeconds;
            double dt = now - _lastWall;
            _lastWall = now;
            _animT = (float)now;
            if (dt > 0.25) dt = 0.25;   // clamp after a stall so we don't spiral

            if (!_paused)
            {
                if (_mode == Mode.Manual)
                {
                    // Feed the hand-authored snapshot at real-time cadence so warmup/dwell timing
                    // runs and the mixer plays the auditioned context.
                    _rtAccum += dt;
                    int guard = 0;
                    while (_rtAccum >= _tickSec && guard++ < 8)
                    {
                        _rtAccum -= _tickSec;
                        _manual.Now = SimClock.Now;
                        Ingest(SafeTick(_manual));
                    }
                }
                else if (_fast)
                {
                    for (int i = 0; i < FastBatchCap; i++) Ingest(SafeTick());
                }
                else
                {
                    _rtAccum += dt;
                    int guard = 0;
                    while (_rtAccum >= _tickSec && guard++ < 8)   // cap catch-up bursts
                    {
                        _rtAccum -= _tickSec;
                        Ingest(SafeTick());
                    }
                }
            }

            AnimatePlayer(dt);
            UpdateLighting(dt);
            Invalidate();
        }

        private TickOutcome SafeTick(GameState supplied = null)
        {
            try { _ticksRun++; return _runner.Tick(supplied); }
            catch { return default; }
        }

        private void Ingest(TickOutcome o)
        {
            if (o.State != null) _gs = o.State;
            if (o.Transitioned)
            {
                _top = o.Top ?? "—";
                if (o.PlayedFile != null) { _file = System.IO.Path.GetFileName(o.PlayedFile); _normalized = o.Normalized; }
                else if (o.Top == "Vanilla") _file = "(vanilla DMS)";
                else if (o.Top == "Silence") _file = "—";

                _ticker.Enqueue((SimClock.Now, o.Top, o.PlayedFile != null ? System.IO.Path.GetFileName(o.PlayedFile) : null));
                while (_ticker.Count > 7) _ticker.Dequeue();
            }
        }

        private void AnimatePlayer(double dt)
        {
            PointF target = LocationOf(_gs);
            // Ease smoothly toward the landmark. Only autopilot max-speed snaps (phases change
            // every frame there, so easing just lags the token to the middle).
            bool snap = _fast && _mode == Mode.Autopilot;
            float k = snap ? 1f : (float)Math.Min(1.0, dt * 2.6);
            _player = new PointF(_player.X + (target.X - _player.X) * k,
                                 _player.Y + (target.Y - _player.Y) * k);
        }

        private void UpdateLighting(double dt)
        {
            var gs = _gs;
            float dayTarget = gs == null || gs.IsDay ? 1f : 0f;
            float bloodTarget = gs != null && gs.IsBloodMoon ? 1f : 0f;
            float k = (float)Math.Min(1.0, dt * 1.8);
            _dayLevel += (dayTarget - _dayLevel) * k;
            _bloodLevel += (bloodTarget - _bloodLevel) * k;
        }

        // ─────────────────────────── input ───────────────────────────
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Space: _paused = !_paused; break;
                case Keys.Tab: if (_mode == Mode.Autopilot) SetFast(!_fast); break;   // speed toggle is autopilot-only
                case Keys.M: ToggleMode(); break;
                case Keys.Escape: Close(); break;
                // Manual-mode shortcuts (Dispatch self-guards: no-op in autopilot).
                case Keys.P: Dispatch("loc"); break;
                case Keys.G: Dispatch("biome"); break;
                case Keys.N: Dispatch("day"); break;
                case Keys.K: Dispatch("blood"); break;
                case Keys.B: Dispatch("boss"); break;
                case Keys.V: Dispatch("low"); break;
                case Keys.C: Dispatch("clear"); break;
                case Keys.A: Dispatch("ally"); break;
                case Keys.S: Dispatch("stranger"); break;
                case Keys.I: Dispatch("infest"); break;
            }
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            foreach (var b in _buttons)
                if (b.Enabled && b.R.Contains(e.Location)) { Dispatch(b.Id); Invalidate(); break; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mousePt = e.Location;
            bool over = false;
            foreach (var b in _buttons) if (b.Enabled && b.R.Contains(e.Location)) { over = true; break; }
            Cursor = over ? Cursors.Hand : Cursors.Default;
        }

        private void SetFast(bool fast)
        {
            _fast = fast;
            _rtAccum = 0;
            _runner.DecodeEnabled = !fast;       // don't feed the realtime mixer while fast-forwarding
            CmmMixer.MasterVolume = fast ? 0f : 1f;
        }

        // ── Autopilot ⇄ Manual ──
        private void ToggleMode() => SetMode(_mode == Mode.Autopilot ? Mode.Manual : Mode.Autopilot);

        private void SetMode(Mode m)
        {
            if (m == _mode) return;
            _mode = m;
            _rtAccum = 0;
            if (m == Mode.Manual)
            {
                SeedManualFromCurrent();             // continuous switch: start from where autopilot was
                _runner.DecodeEnabled = true;        // manual always plays (you're auditioning states)
                CmmMixer.MasterVolume = 1f;
            }
            else
            {
                _runner.DecodeEnabled = !_fast;      // restore autopilot audio per the speed toggle
                CmmMixer.MasterVolume = _fast ? 0f : 1f;
            }
        }

        private void SeedManualFromCurrent()
        {
            var s = _gs;
            _manual.Enemies.Clear();
            _manual.Buffs.Clear();
            _manual.IsPanicked = false;
            _manual.HealthPct = s?.HealthPct ?? 1f;
            _manual.IsDay = s?.IsDay ?? true;
            _manual.IsBloodMoon = s?.IsBloodMoon ?? false;
            _manual.AlliesNearby = 0; _manual.StrangersNearby = 0;
            _biomeIdx = Math.Max(0, Array.IndexOf(BiomeNames, s?.Biome ?? "pine_forest"));
            _manual.Biome = BiomeNames[_biomeIdx];
            _locIdx = s == null ? 3 : s.NearHome ? 0 : s.TraderName != null ? 1 : s.InPoi ? 2 : 3;
            ApplyLocation();
        }

        private void Dispatch(string id)
        {
            switch (id)
            {
                case "auto": SetMode(Mode.Autopilot); return;
                case "manual": SetMode(Mode.Manual); return;
            }
            if (_mode != Mode.Manual) return;
            switch (id)
            {
                case "loc": _locIdx = (_locIdx + 1) % LocNames.Length; ApplyLocation(); break;
                case "biome": _biomeIdx = (_biomeIdx + 1) % BiomeNames.Length; _manual.Biome = BiomeNames[_biomeIdx]; break;
                case "day": _manual.IsDay = !_manual.IsDay; break;
                case "blood": _manual.IsBloodMoon = !_manual.IsBloodMoon; break;
                case "low": SpawnManual(false); break;
                case "boss": SpawnManual(true); break;
                case "clear": _manual.Enemies.Clear(); _manual.IsPanicked = false; if (_manual.InPoi) _manual.PoiEnemiesRemaining = 0; break;
                case "ally": _manual.AlliesNearby++; break;
                case "stranger": _manual.StrangersNearby++; break;
                case "mpclear": _manual.AlliesNearby = 0; _manual.StrangersNearby = 0; break;
                case "hurt": _manual.HealthPct = Math.Max(0.05f, _manual.HealthPct - 0.15f); break;
                case "heal": _manual.HealthPct = Math.Min(1f, _manual.HealthPct + 0.15f); break;
                case "infest": if (_manual.InPoi) _manual.PoiEnemiesRemaining = _manual.PoiEnemiesRemaining > 0 ? 0 : 5; break;
            }
        }

        private void ApplyLocation()
        {
            _manual.NearHome = false; _manual.TraderName = null;
            _manual.InPoi = false; _manual.PoiName = null; _manual.PoiId = null; _manual.PoiEnemiesRemaining = 0;
            switch (LocNames[_locIdx])
            {
                case "Home": _manual.NearHome = true; break;
                case "Trader": _manual.TraderName = "jen"; break;
                case "POI":
                    _manual.InPoi = true; _manual.PoiName = "factory_03"; _manual.PoiId = "factory_03#manual";
                    _manual.PoiTier = 4; _manual.PoiEnemiesRemaining = _manual.Enemies.Count;
                    break;
                // "Travel": everything cleared above.
            }
        }

        private void SpawnManual(bool boss)
        {
            _manual.Enemies.Clear();
            int n = boss ? 10 : 3;
            var pool = boss ? HeavyClasses : WeakClasses;
            for (int i = 0; i < n; i++)
                _manual.Enemies.Add(new EnemyInfo { ClassName = pool[i % pool.Length], Distance = 3f + i * 2f, IsAlert = true, IsTargetingPlayer = true });
            _manual.IsPanicked = true;
            if (_manual.InPoi) _manual.PoiEnemiesRemaining = n;
        }

        // ─────────────────────────── projection ───────────────────────────
        // Fixed landmark layout (normalized board coords). Positions are cosmetic; the
        // player eases toward whichever the GameState says we're at.
        private static readonly Dictionary<string, PointF> Traders = new Dictionary<string, PointF>(StringComparer.OrdinalIgnoreCase)
        {
            { "jen", new PointF(0.30f, 0.62f) }, { "rekt", new PointF(0.62f, 0.13f) },
            { "bob", new PointF(0.85f, 0.42f) }, { "hugh", new PointF(0.19f, 0.83f) },
            { "joel", new PointF(0.72f, 0.80f) },
        };
        private static readonly Dictionary<string, PointF> Pois = new Dictionary<string, PointF>(StringComparer.OrdinalIgnoreCase)
        {
            { "house_01", new PointF(0.42f, 0.28f) }, { "store_05", new PointF(0.76f, 0.56f) },
            { "skyscraper_02", new PointF(0.88f, 0.19f) }, { "factory_03", new PointF(0.53f, 0.52f) },
            { "church_01", new PointF(0.30f, 0.44f) },
        };
        private static readonly Dictionary<string, PointF> Biomes = new Dictionary<string, PointF>(StringComparer.OrdinalIgnoreCase)
        {
            { "pine_forest", new PointF(0.50f, 0.34f) }, { "desert", new PointF(0.80f, 0.68f) },
            { "burnt_forest", new PointF(0.24f, 0.55f) }, { "snow", new PointF(0.50f, 0.86f) },
            { "wasteland", new PointF(0.66f, 0.40f) },
        };
        private static readonly PointF BasePos = new PointF(0.14f, 0.20f);

        private static PointF LocationOf(GameState gs)
        {
            if (gs == null || gs.NearHome) return BasePos;
            if (gs.TraderName != null && Traders.TryGetValue(gs.TraderName, out var tp)) return tp;
            if (gs.InPoi && gs.PoiName != null && Pois.TryGetValue(gs.PoiName, out var pp)) return pp;
            if (gs.Biome != null && Biomes.TryGetValue(gs.Biome, out var bp)) return bp;
            return new PointF(0.5f, 0.5f);
        }

        // ─────────────────────────── palette + fonts ───────────────────────────
        static Color C(int rgb) => Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
        static Color A(int a, Color c) => Color.FromArgb(a, c);
        static Color Lerp(Color x, Color y, float t)
        {
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return Color.FromArgb(
                (int)(x.A + (y.A - x.A) * t), (int)(x.R + (y.R - x.R) * t),
                (int)(x.G + (y.G - x.G) * t), (int)(x.B + (y.B - x.B) * t));
        }
        static readonly Color Bg = C(0x0a_0808), Panel = C(0x14_1113), Panel2 = C(0x0f_0c0d), Line = C(0x2c_2325);
        static readonly Color Ink = C(0xec_e4e0), Muted = C(0x9d_918d), Faint = C(0x6c_6260);
        static readonly Color Danger = C(0xf2_5636), DangerDim = C(0x3a_1611), Audio = C(0x49_d6c6), Warn = C(0xe6_ad44), Good = C(0x7c_cc80);

        private Font _fHeader, _fLabel, _fData, _fDataSm, _fBig, _fBadge, _fTiny;
        private void BuildFonts()
        {
            _fHeader = new Font("Bahnschrift", 9.5f, FontStyle.Bold);
            _fLabel = new Font("Bahnschrift", 9.25f, FontStyle.Regular);
            _fBadge = new Font("Bahnschrift", 8.5f, FontStyle.Bold);
            _fTiny = new Font("Bahnschrift", 7.75f, FontStyle.Bold);
            _fBig = new Font("Bahnschrift", 20f, FontStyle.Bold);
            _fData = new Font("Consolas", 9.5f, FontStyle.Regular);
            _fDataSm = new Font("Consolas", 8.25f, FontStyle.Regular);
        }

        private readonly Dictionary<Color, SolidBrush> _brushes = new Dictionary<Color, SolidBrush>();
        private SolidBrush B(Color c) { if (!_brushes.TryGetValue(c, out var b)) _brushes[c] = b = new SolidBrush(c); return b; }

        // ── GDI+ shape helpers ──
        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d < 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        void FillRound(Graphics g, RectangleF r, float rad, Color c)
        { using (var p = RoundRect(r, rad)) g.FillPath(B(c), p); }
        void GradRound(Graphics g, RectangleF r, float rad, Color top, Color bot)
        {
            using (var p = RoundRect(r, rad))
            using (var b = new LinearGradientBrush(new RectangleF(r.X, r.Y, r.Width, r.Height + 1), top, bot, LinearGradientMode.Vertical))
            { b.WrapMode = WrapMode.TileFlipXY; g.FillPath(b, p); }
        }
        void StrokeRound(Graphics g, RectangleF r, float rad, Color c, float w = 1f)
        { using (var p = RoundRect(r, rad)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p); }
        void VFill(Graphics g, Rectangle r, Color top, Color bot)
        {
            using (var b = new LinearGradientBrush(new Rectangle(r.X, r.Y, r.Width, r.Height + 1), top, bot, LinearGradientMode.Vertical))
            { b.WrapMode = WrapMode.TileFlipXY; g.FillRectangle(b, r); }
        }
        void Glow(Graphics g, float cx, float cy, float radius, Color c, int alpha)
        {
            if (radius < 1) return;
            var rect = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(rect);
                using (var b = new PathGradientBrush(path) { CenterColor = A(alpha, c), SurroundColors = new[] { A(0, c) } })
                    g.FillEllipse(b, rect);
            }
        }
        float Pulse(float speed, float phase = 0f) => 0.5f + 0.5f * (float)Math.Sin(_animT * speed + phase);

        // ─────────────────────────── paint ───────────────────────────
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int W = ClientSize.Width, H = ClientSize.Height;
            int topH = 36, tickerH = 30, stripH = 64, leftW = 252, rightW = 336;
            if (W < 700 || H < 400) { g.Clear(Bg); return; }
            int bodyBottom = H - tickerH - stripH;

            var statusR = new Rectangle(0, 0, W, topH);
            var boardR = new Rectangle(leftW, topH, W - leftW - rightW, bodyBottom - topH);
            var leftR = new Rectangle(0, topH, leftW, bodyBottom - topH);
            var rightR = new Rectangle(W - rightW, topH, rightW, bodyBottom - topH);
            var stripR = new Rectangle(0, bodyBottom, W, stripH);
            var tickerR = new Rectangle(0, H - tickerH, W, tickerH);

            g.Clear(Bg);
            DrawBoard(g, boardR);
            VFill(g, leftR, C(0x16_1315), C(0x0f_0c0d));
            VFill(g, rightR, C(0x16_1315), C(0x0f_0c0d));
            DrawLeftPanel(g, leftR);
            DrawRightPanel(g, rightR);
            DrawControlStrip(g, stripR);
            DrawTicker(g, tickerR);
            DrawStatus(g, statusR);

            using (var p = new Pen(A(150, Line)))
            {
                g.DrawLine(p, leftR.Right, topH, leftR.Right, boardR.Bottom);
                g.DrawLine(p, rightR.Left, topH, rightR.Left, boardR.Bottom);
                g.DrawLine(p, 0, topH, W, topH);
                g.DrawLine(p, 0, stripR.Top, W, stripR.Top);
                g.DrawLine(p, 0, tickerR.Top, W, tickerR.Top);
            }
        }

        // ── Board (the projection) ──
        private void DrawBoard(Graphics g, Rectangle r)
        {
            var gs = _gs;
            bool panicked = gs != null && gs.IsPanicked;

            // Ground: smoothly interpolated day↔night, warmed slightly toward red under a blood moon.
            Color ground = Lerp(C(0x0b_0a09), C(0x17_1310), _dayLevel);
            ground = Lerp(ground, C(0x1c_0f0b), _bloodLevel * 0.7f);
            using (var bg = new SolidBrush(ground)) g.FillRectangle(bg, r);

            var inset = Rectangle.Inflate(r, -30, -30);
            PointF Map(PointF n) => new PointF(inset.X + n.X * inset.Width, inset.Y + n.Y * inset.Height);
            float minDim = Math.Min(inset.Width, inset.Height);

            // Blood moon: a red disc high on the board + a broad wash, both scaled by the eased level.
            if (_bloodLevel > 0.01f)
            {
                float ba = _bloodLevel;
                Glow(g, r.X + r.Width * 0.82f, r.Y + r.Height * 0.16f, minDim * 0.42f, C(0xd0_2c18), (int)(70 * ba));
                float mr = 10 + minDim * 0.02f;
                float mx = r.X + r.Width * 0.82f, my = r.Y + r.Height * 0.16f;
                Glow(g, mx, my, mr * 2.4f, C(0xff_5a30), (int)(150 * ba));
                using (var mb = new SolidBrush(A((int)(220 * ba), C(0xcf_3a20)))) g.FillEllipse(mb, mx - mr, my - mr, mr * 2, mr * 2);
            }

            // Travelled route: an animated dashed line from base to where we are now.
            var pBaseEarly = Map(BasePos);
            var ppEarly = Map(_player);
            if (Dist(pBaseEarly, ppEarly) > 24)
                using (var pen = new Pen(A(38, C(0xc8_a08c)), 1.4f) { DashStyle = DashStyle.Dash, DashPattern = new[] { 2f, 5f }, DashOffset = -_animT * 7f })
                    g.DrawLine(pen, pBaseEarly, ppEarly);

            // Landmarks.
            MarkerBase(g, pBaseEarly);
            foreach (var kv in Traders) MarkerDiamond(g, Map(kv.Value), kv.Key);
            foreach (var kv in Pois)
            {
                bool current = gs != null && gs.InPoi && string.Equals(gs.PoiName, kv.Key, StringComparison.OrdinalIgnoreCase);
                bool infested = current && gs.PoiEnemiesRemaining > 0;
                MarkerPoi(g, Map(kv.Value), kv.Key, current, infested, minDim);
            }

            // Player detect ring + combat.
            var pp = ppEarly;
            float ring = minDim * 0.155f;
            using (var rp = new Pen(A(panicked ? 55 : 34, panicked ? Danger : Ink), 1f)) g.DrawEllipse(rp, pp.X - ring, pp.Y - ring, ring * 2, ring * 2);

            if (gs != null)
            {
                int n = Math.Min(gs.Enemies.Count, 48);
                for (int i = 0; i < n; i++)
                {
                    var en = gs.Enemies[i];
                    float ang = i * 2.39996323f + _animT * 0.12f;
                    float rr = Math.Min(1.22f, en.Distance / 32f) * ring;
                    float ex = pp.X + (float)Math.Cos(ang) * rr, ey = pp.Y + (float)Math.Sin(ang) * rr;
                    if (en.IsTargetingPlayer)
                    {
                        float pl = Pulse(4.2f, i);
                        using (var lp = new Pen(A(30 + (int)(35 * pl), Danger), 1f)) g.DrawLine(lp, ex, ey, pp.X, pp.Y);
                        using (var rp = new Pen(A(70 + (int)(90 * pl), Danger), 1.4f)) g.DrawEllipse(rp, ex - 6, ey - 6, 12, 12);
                        g.FillEllipse(B(Danger), ex - 4, ey - 4, 8, 8);
                    }
                    else
                    {
                        Color col = en.IsAlert ? Warn : Muted;
                        g.FillEllipse(B(col), ex - 3.5f, ey - 3.5f, 7, 7);
                    }
                }
            }

            // Player token: glow (reddens in combat) + pulse ring + core.
            float gp = Pulse(2.4f);
            Color glowCol = Lerp(C(0xf4_ede9), Danger, panicked ? 0.7f : 0f);
            Glow(g, pp.X, pp.Y, 20 + gp * 5, glowCol, panicked ? 70 : 46);
            using (var pr = new Pen(A(90 + (int)(90 * gp), glowCol), 1.5f)) g.DrawEllipse(pr, pp.X - 12, pp.Y - 12, 24, 24);
            g.FillEllipse(B(C(0xf6_efeb)), pp.X - 7.5f, pp.Y - 7.5f, 15, 15);
            g.FillEllipse(B(C(0x14_100f)), pp.X - 3, pp.Y - 3, 6, 6);

            // Edge vignette (ellipse fully contains r so the corners sample the surround, not undefined).
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r.X - r.Width * 0.32f, r.Y - r.Height * 0.32f, r.Width * 1.64f, r.Height * 1.64f);
                using (var vb = new PathGradientBrush(path) { CenterColor = A(0, Color.Black), SurroundColors = new[] { A(175, C(0x05_0303)) }, FocusScales = new PointF(0.62f, 0.58f) })
                    g.FillRectangle(vb, r);
            }

            // Caption pill.
            if (gs != null)
            {
                string cap = PhaseLabel(gs);
                var sz = g.MeasureString(cap, _fLabel);
                var pill = new RectangleF(r.X + 12, r.Y + 10, sz.Width + 22, 24);
                GradRound(g, pill, 12, A(190, C(0x1a_1517)), A(190, C(0x12_0f10)));
                StrokeRound(g, pill, 12, A(120, Line));
                g.FillEllipse(B(gs.IsBloodMoon ? Danger : gs.InPoi ? Warn : Audio), pill.X + 11, pill.Y + 9, 6, 6);
                g.DrawString(cap, _fLabel, B(Ink), pill.X + 24, pill.Y + 4);
            }
        }

        static float Dist(PointF a, PointF b) { float dx = a.X - b.X, dy = a.Y - b.Y; return (float)Math.Sqrt(dx * dx + dy * dy); }

        private void MarkerBase(Graphics g, PointF p)
        {
            Glow(g, p.X, p.Y, 16, C(0xe8_e0dc), 30);
            using (var pen = new Pen(A(120, Ink), 1.4f)) g.DrawEllipse(pen, p.X - 8, p.Y - 8, 16, 16);
            g.FillEllipse(B(Ink), p.X - 4, p.Y - 4, 8, 8);
            CenterLabel(g, "BASE", p.X, p.Y + 12, Muted);
        }
        private void MarkerDiamond(Graphics g, PointF p, string label)
        {
            var pts = new[] { new PointF(p.X, p.Y - 6), new PointF(p.X + 6, p.Y), new PointF(p.X, p.Y + 6), new PointF(p.X - 6, p.Y) };
            using (var pen = new Pen(A(120, Warn), 1f)) g.DrawPolygon(pen, pts);
            g.FillPolygon(B(A(210, Warn)), pts);
            CenterLabel(g, label, p.X, p.Y + 10, Faint);
        }
        private void MarkerPoi(Graphics g, PointF p, string label, bool current, bool infested, float minDim)
        {
            Color col = current ? (infested ? Danger : Good) : C(0x50_4543);
            if (current)
            {
                float pl = Pulse(2.2f);
                Glow(g, p.X, p.Y, minDim * 0.06f + pl * 6, col, infested ? 60 : 44);
                using (var pen = new Pen(A(90 + (int)(90 * pl), col), 1.6f))
                { float rr = 13 + pl * 5; g.DrawEllipse(pen, p.X - rr, p.Y - rr, rr * 2, rr * 2); }
            }
            var sq = new RectangleF(p.X - 7, p.Y - 7, 14, 14);
            FillRound(g, sq, 3, A(current ? 235 : 200, col));
            StrokeRound(g, sq, 3, A(160, C(0x14_100f)), 1.6f);
            CenterLabel(g, label + (infested ? "  · INFESTED" : ""), p.X, p.Y + 12, current ? col : Faint);
        }

        private readonly StringFormat _center = new StringFormat { Alignment = StringAlignment.Center };
        private readonly StringFormat _right = new StringFormat { Alignment = StringAlignment.Far };
        private readonly StringFormat _clip = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
        private void CenterLabel(Graphics g, string s, float cx, float y, Color col)
            => g.DrawString(s, _fDataSm, B(col), cx, y, _center);

        private static string PhaseLabel(GameState gs)
        {
            if (gs.NearHome) return "Home · base";
            if (gs.TraderName != null) return "Trader · " + gs.TraderName;
            if (gs.InPoi) return $"POI · {gs.PoiName} (T{gs.PoiTier})" + (gs.PoiEnemiesRemaining > 0 ? " · infested" : " · cleared");
            if (gs.IsSwimming) return "Traveling · swimming";
            return "Traveling · " + (gs.VehicleType ?? "on foot") + " · " + (gs.Biome ?? "?");
        }

        // ── Left panel: Inputs (GameState) ──
        private void DrawLeftPanel(Graphics g, Rectangle r)
        {
            int x = r.X + 16, y = r.Y + 12, w = r.Width - 32;
            Header(g, "INPUTS · GAMESTATE", Audio, x, ref y, w);
            var gs = _gs;
            if (gs == null) { g.DrawString("waiting for first tick…", _fData, B(Faint), x, y); return; }

            Row(g, "World", $"Day {_runner.Generator.WorldDay} · h{SimClock.Now / 3600.0:F1}", x, ref y, w, Ink);
            Row(g, "Sky", (gs.IsDay ? "day" : "night") + (gs.IsBloodMoon ? " · BLOOD MOON" : ""), x, ref y, w, gs.IsBloodMoon ? Danger : Ink);
            Row(g, "Biome", gs.Biome ?? "?", x, ref y, w, Ink);
            Row(g, "Phase", ShortPhase(gs), x, ref y, w, Ink);

            y += 8; Sub(g, "THREAT", x, ref y, w);
            HealthRow(g, gs.HealthPct, x, ref y, w);
            Row(g, "Panicked", gs.IsPanicked ? "YES" : "no", x, ref y, w, gs.IsPanicked ? Danger : Faint);
            int targeting = gs.Enemies.Count(en => en.IsTargetingPlayer);
            int alert = gs.Enemies.Count(en => en.IsAlert && !en.IsTargetingPlayer);
            Row(g, "Enemies", gs.Enemies.Count.ToString(), x, ref y, w, gs.Enemies.Count > 0 ? Ink : Faint);
            Row(g, "Targeting", targeting.ToString(), x, ref y, w, targeting > 0 ? Danger : Faint);
            Row(g, "Aware only", alert.ToString(), x, ref y, w, alert > 0 ? Warn : Faint);

            y += 8; Sub(g, "POI", x, ref y, w);
            if (gs.InPoi)
            {
                Row(g, "Tier", "T" + gs.PoiTier, x, ref y, w, Ink);
                Row(g, "Status", gs.PoiEnemiesRemaining > 0 ? $"infested · {gs.PoiEnemiesRemaining} left" : "cleared", x, ref y, w,
                    gs.PoiEnemiesRemaining > 0 ? Warn : Good);
            }
            else Row(g, "Inside POI", "no", x, ref y, w, Faint);

            y += 8; Sub(g, "MULTIPLAYER", x, ref y, w);
            Row(g, "Allies", gs.AlliesNearby.ToString(), x, ref y, w, gs.AlliesNearby > 0 ? Audio : Faint);
            Row(g, "Strangers", gs.StrangersNearby.ToString(), x, ref y, w, gs.StrangersNearby > 0 ? Warn : Faint);
        }

        private static string ShortPhase(GameState gs)
        {
            if (gs.NearHome) return "Home";
            if (gs.TraderName != null) return "Trader (" + gs.TraderName + ")";
            if (gs.InPoi) return "POI (" + gs.PoiName + ")";
            if (gs.IsSwimming) return "Swimming";
            return "Travel (" + (gs.VehicleType ?? "foot") + ")";
        }

        // ── Right panel: Now Playing + Resolution ──
        private void DrawRightPanel(Graphics g, Rectangle r)
        {
            int x = r.X + 16, y = r.Y + 12, w = r.Width - 32;
            Header(g, "NOW PLAYING", Danger, x, ref y, w);

            var card = new RectangleF(x, y, w, 96);
            GradRound(g, card, 9, C(0x22_1416), C(0x15_0f10));
            StrokeRound(g, card, 9, A(150, DangerDim));
            // left accent bar
            using (var ap = RoundRect(new RectangleF(x + 2, y + 8, 3, 80), 1.5f)) g.FillPath(B(Danger), ap);

            g.DrawString(_top, _fBig, B(Danger), new RectangleF(x + 14, y + 8, w - 28, 32), _clip);
            g.DrawString(_file, _fData, B(Ink), new RectangleF(x + 14, y + 46, w - 92, 18), _clip);

            // "playing" EQ flourish (only animates when audio is actually flowing).
            int voices = TryVoices();
            bool playing = !_fast && !_paused && voices > 0;
            DrawEq(g, x + w - 46, y + 40, playing);

            string norm = _normalized ? "✓ normalized" : "· unity gain";
            g.DrawString(norm, _fDataSm, B(_normalized ? Good : Faint), x + 14, y + 70);
            string mix = (_fast ? "muted · max" : "realtime") + (voices >= 0 ? $" · {voices}v" : "");
            g.DrawString(mix, _fDataSm, B(_fast ? Faint : Audio), x + w - 14, y + 70, _right);
            y += (int)card.Height + 16;

            Header(g, "RESOLUTION · ACTIVE STACK", Muted, x, ref y, w);
            var tokens = _runner.LastTokens;
            if (tokens == null || tokens.Count == 0) { g.DrawString("(no active contexts)", _fData, B(Faint), x, y); return; }

            var ordered = tokens
                .Select(t => (name: t, pri: ContextMusicManager.GetContext(t)?.Priority ?? -99))
                .OrderByDescending(t => t.pri).Take(14).ToList();
            foreach (var (name, pri) in ordered)
            {
                bool win = string.Equals(name, _top, StringComparison.OrdinalIgnoreCase);
                var row = new RectangleF(x, y, w, 21);
                // faint priority bar behind each row (0..100 → 0..w)
                float frac = Math.Max(0.04f, Math.Min(1f, pri / 100f));
                FillRound(g, new RectangleF(x, y, w * frac, 21), 5, A(win ? 26 : 14, win ? Danger : Ink));
                if (win)
                {
                    float wp = Pulse(2.6f);
                    Glow(g, x + 8, y + 10, 18, Danger, 22 + (int)(20 * wp));
                    GradRound(g, row, 5, A(70, DangerDim), A(38, DangerDim));
                    StrokeRound(g, row, 5, A(150, Danger));
                    using (var ap = RoundRect(new RectangleF(x + 2, y + 4, 3, 13), 1.5f)) g.FillPath(B(Danger), ap);
                }
                g.DrawString(pri.ToString(), _fData, B(win ? Danger : Faint), x + 34, y + 3, _right);
                g.DrawString(name, _fData, B(win ? Ink : Muted), x + 44, y + 3);
                if (win) g.DrawString("▶", _fData, B(Danger), x + w - 16, y + 3);
                y += 23;
            }
        }

        private void DrawEq(Graphics g, float x, float y, bool playing)
        {
            for (int i = 0; i < 4; i++)
            {
                float h = playing ? 4 + 14 * Pulse(6f + i * 1.7f, i * 1.3f) : 3;
                var bar = new RectangleF(x + i * 8, y + (18 - h), 5, h);
                FillRound(g, bar, 2, playing ? A(200, Audio) : A(70, Faint));
            }
        }

        private int TryVoices()
        {
            var m = _runner.Mixer;
            if (m == null) return -1;
            try { m.GetOutputStats(out _, out _, out _, out int v, out _, out _, out _); return v; }
            catch { return -1; }
        }

        // ── Control strip (custom buttons; hit-tested in OnMouseDown) ──
        private int _btnX, _btnRow, _btnY0, _btnRowH, _btnX0, _btnXMax;
        private void DrawControlStrip(Graphics g, Rectangle r)
        {
            VFill(g, r, C(0x14_1012), C(0x0d_0a0b));
            _buttons.Clear();
            _btnX = r.X + 12; _btnX0 = r.X + 12; _btnXMax = r.Right - 10;
            _btnY0 = r.Y + 9; _btnRow = 0; _btnRowH = 27;
            bool man = _mode == Mode.Manual;

            AddBtn(g, "auto", "Autopilot", _mode == Mode.Autopilot, true, seg: true);
            AddBtn(g, "manual", "Manual", man, true, seg: true);
            _btnX += 16;
            AddBtn(g, "loc", "Loc: " + LocNames[_locIdx], false, man);
            AddBtn(g, "biome", "Biome: " + BiomeNames[_biomeIdx], false, man);
            AddBtn(g, "day", "Sky: " + (man ? (_manual.IsDay ? "day" : "night") : "—"), man && !_manual.IsDay, man);
            AddBtn(g, "blood", "Blood moon", man && _manual.IsBloodMoon, man);
            AddBtn(g, "low", "Spawn Low", false, man);
            AddBtn(g, "boss", "Spawn Boss", false, man);
            AddBtn(g, "clear", "Clear", false, man);
            AddBtn(g, "ally", "+ Ally", false, man);
            AddBtn(g, "stranger", "+ Stranger", false, man);
            AddBtn(g, "mpclear", "MP clr", false, man);
            AddBtn(g, "hurt", "Hurt", false, man);
            AddBtn(g, "heal", "Heal", false, man);
            AddBtn(g, "infest", "Infest", man && _manual.PoiEnemiesRemaining > 0, man && _manual.InPoi);
        }

        private void AddBtn(Graphics g, string id, string label, bool on, bool enabled, bool seg = false)
        {
            var sz = g.MeasureString(label, _fBadge);
            int w = (int)sz.Width + 20;
            if (_btnX + w > _btnXMax) { _btnRow++; _btnX = _btnX0; }   // wrap to next row
            int y = _btnY0 + _btnRow * _btnRowH;
            var rect = new Rectangle(_btnX, y, w, 23);
            bool hover = enabled && rect.Contains(_mousePt);
            Color face = seg ? Audio : Warn;

            if (on) GradRound(g, rect, 6, A(58, face), A(34, face));
            else if (hover) GradRound(g, rect, 6, C(0x26_2023), C(0x1c_1719));
            else GradRound(g, rect, 6, C(0x1d_181a), C(0x15_1113));
            StrokeRound(g, rect, 6, enabled ? (on ? A(200, face) : hover ? A(150, Muted) : A(110, Line)) : A(90, C(0x22_1c1e)));

            Color tc = enabled ? (on ? face : hover ? Ink : C(0xc9_beb9)) : C(0x4a_423f);
            g.DrawString(label, _fBadge, B(tc), _btnX + 10, y + 4);
            _buttons.Add(new Btn { R = rect, Id = id, Label = label, On = on, Enabled = enabled });
            _btnX += w + 8;
        }

        // ── Ticker ──
        private void DrawTicker(Graphics g, Rectangle r)
        {
            VFill(g, r, C(0x12_0f10), C(0x0c_0a0b));
            int x = r.X + 14; int y = r.Y + 9;
            g.DrawString("TRANSITIONS", _fTiny, B(Faint), x, y + 1);
            x += 92;
            int idx = 0, total = _ticker.Count;
            foreach (var ev in _ticker.Reverse())
            {
                string s = $"h{ev.t / 3600.0:F1}  {ev.top}" + (ev.file != null ? $"  ←  {ev.file}" : "");
                var sz = g.MeasureString(s, _fDataSm);
                if (x + sz.Width > r.Width - 12) break;
                int fade = 255 - Math.Min(150, idx * 26);       // newest brightest, older dim
                bool newest = idx == 0;
                if (newest) Glow(g, x + 3, y + 7, 8, Danger, 120);
                g.FillEllipse(B(A(fade, newest ? Danger : Muted)), x, y + 4, 6, 6);
                g.DrawString(s, _fDataSm, B(A(fade, newest ? Ink : Muted)), x + 12, y);
                x += (int)sz.Width + 30; idx++;
            }
        }

        // ── Status strip ──
        private void DrawStatus(Graphics g, Rectangle r)
        {
            VFill(g, r, C(0x1a_1517), C(0x12_0e10));
            int y = 9;
            g.DrawString("CmmSessionView", _fHeader, B(Ink), 16, y + 1);
            int x = 158;
            var gs = _gs;
            if (gs != null)
            {
                x = Chip(g, "Day " + _runner.Generator.WorldDay, gs.IsBloodMoon ? Danger : Ink, x, y);
                if (gs.IsBloodMoon) x = Chip(g, "BLOOD MOON", Danger, x, y);
                x = Chip(g, gs.IsDay ? "day" : "night", Muted, x, y);
            }
            x = Chip(g, "seed " + _runner.Options.Seed, Faint, x, y);

            // Right-aligned: mode badge + key hints.
            string mode = _paused ? "PAUSED" : _mode == Mode.Manual ? "MANUAL" : _fast ? "MAX SPEED" : "REAL-TIME 1×";
            Color mc = _paused ? Warn : _mode == Mode.Manual ? Warn : _fast ? Muted : Audio;
            string hints = "[Space] pause   [Tab] speed   [M] manual   [Esc] quit";
            var hsz = g.MeasureString(hints, _fDataSm);
            g.DrawString(hints, _fDataSm, B(Faint), r.Width - hsz.Width - 14, y + 2);
            var msz = g.MeasureString(mode, _fBadge);
            var badge = new RectangleF(r.Width - hsz.Width - msz.Width - 40, y - 1, msz.Width + 18, 20);
            if (!_paused && _mode != Mode.Manual && !_fast) Glow(g, badge.X + badge.Width / 2, badge.Y + 10, 22, mc, 30);
            GradRound(g, badge, 10, A(46, mc), A(24, mc));
            StrokeRound(g, badge, 10, A(150, mc));
            g.DrawString(mode, _fBadge, B(mc), badge.X + 9, y + 1);
        }

        private int Chip(Graphics g, string s, Color col, int x, int y)
        {
            var sz = g.MeasureString(s, _fData);
            var pill = new RectangleF(x - 8, y - 1, sz.Width + 16, 20);
            FillRound(g, pill, 9, A(70, C(0x24_1e20)));
            g.DrawString(s, _fData, B(col), x, y);
            return x + (int)sz.Width + 24;
        }

        // ── HUD row helpers ──
        private void Header(Graphics g, string s, Color accent, int x, ref int y, int w)
        {
            using (var ap = RoundRect(new RectangleF(x, y + 1, 3, 12), 1.5f)) g.FillPath(B(accent), ap);
            g.DrawString(s, _fHeader, B(Muted), x + 9, y);
            using (var lb = new LinearGradientBrush(new Rectangle(x, y + 19, w, 2), A(120, accent), A(0, accent), 0f))
            { lb.WrapMode = WrapMode.TileFlipXY; g.FillRectangle(lb, x, y + 19, w, 1); }
            y += 27;
        }
        private void Sub(Graphics g, string s, int x, ref int y, int w)
        {
            g.DrawString(s, _fTiny, B(Faint), x, y);
            y += 17;
        }
        private void Row(Graphics g, string k, string v, int x, ref int y, int w, Color vc)
        {
            g.DrawString(k, _fLabel, B(Muted), x, y);
            g.DrawString(v, _fData, B(vc), x + w, y + 1, _right);
            y += 23;
        }
        private void HealthRow(Graphics g, float pct, int x, ref int y, int w)
        {
            g.DrawString("Health", _fLabel, B(Muted), x, y);
            int bw = 92, bx = x + w - bw, by = y + 4;
            var track = new RectangleF(bx, by, bw, 9);
            FillRound(g, track, 4.5f, C(0x26_1e20));
            Color hc = pct < 0.33f ? Danger : pct < 0.6f ? Warn : Good;
            float fw = Math.Max(3, bw * pct);
            using (var p = RoundRect(new RectangleF(bx, by, fw, 9), 4.5f))
            using (var b = new LinearGradientBrush(new RectangleF(bx, by, fw, 10), A(180, hc), hc, LinearGradientMode.Vertical))
            { b.WrapMode = WrapMode.TileFlipXY; g.FillPath(b, p); }
            g.DrawString((pct * 100).ToString("0") + "%", _fDataSm, B(hc), bx - 6, y + 1, _right);
            y += 23;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            try { _runner.Stop(); } catch { }
            foreach (var b in _brushes.Values) b.Dispose();
            _fHeader?.Dispose(); _fLabel?.Dispose(); _fData?.Dispose(); _fDataSm?.Dispose(); _fBig?.Dispose(); _fBadge?.Dispose(); _fTiny?.Dispose();
            _center.Dispose(); _right.Dispose(); _clip.Dispose();
            base.OnFormClosed(e);
        }
    }
}
