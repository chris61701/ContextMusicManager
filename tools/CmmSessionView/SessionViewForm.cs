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
        static readonly Color Bg = C(0x0b_0909), Panel = C(0x13_1011), Panel2 = C(0x1b_1618), Line = C(0x2c_2325);
        static readonly Color Ink = C(0xec_e4e0), Muted = C(0x9d_918d), Faint = C(0x6c_6260);
        static readonly Color Danger = C(0xee_4c2c), DangerDim = C(0x3a_1611), Audio = C(0x45_d0c1), Warn = C(0xe2_ab41), Good = C(0x77_c67c);

        private Font _fHeader, _fLabel, _fData, _fDataSm, _fBig, _fBadge;
        private void BuildFonts()
        {
            _fHeader = new Font("Bahnschrift", 9.5f, FontStyle.Bold);
            _fLabel = new Font("Bahnschrift", 9f, FontStyle.Regular);
            _fBadge = new Font("Bahnschrift", 8.5f, FontStyle.Bold);
            _fBig = new Font("Bahnschrift", 20f, FontStyle.Bold);
            _fData = new Font("Consolas", 9.5f, FontStyle.Regular);
            _fDataSm = new Font("Consolas", 8.25f, FontStyle.Regular);
        }

        private readonly Dictionary<Color, SolidBrush> _brushes = new Dictionary<Color, SolidBrush>();
        private SolidBrush B(Color c) { if (!_brushes.TryGetValue(c, out var b)) _brushes[c] = b = new SolidBrush(c); return b; }

        // ─────────────────────────── paint ───────────────────────────
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int W = ClientSize.Width, H = ClientSize.Height;
            int topH = 34, tickerH = 28, stripH = 62, leftW = 250, rightW = 330;
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
            g.FillRectangle(B(Panel), leftR); g.FillRectangle(B(Panel), rightR);
            DrawLeftPanel(g, leftR);
            DrawRightPanel(g, rightR);
            DrawControlStrip(g, stripR);
            DrawTicker(g, tickerR);
            DrawStatus(g, statusR);

            // Panel divider lines.
            using (var p = new Pen(Line))
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
            bool night = gs != null && !gs.IsDay;
            bool blood = gs != null && gs.IsBloodMoon;

            // Ground: darker at night, red-washed on a blood moon.
            Color ground = night ? C(0x0a_0807) : C(0x12_0f0d);
            using (var bg = new SolidBrush(ground)) g.FillRectangle(bg, r);
            if (blood)
                using (var bm = new PathGradientBrushSafe(r, Color.FromArgb(70, 200, 40, 20), Color.FromArgb(0, 0, 0, 0)))
                    bm.Fill(g, r);

            var inset = Rectangle.Inflate(r, -30, -30);
            PointF Map(PointF n) => new PointF(inset.X + n.X * inset.Width, inset.Y + n.Y * inset.Height);
            float minDim = Math.Min(inset.Width, inset.Height);

            // Landmarks.
            var pBase = Map(BasePos);
            Marker(g, pBase, 7, Ink, "BASE", Faint);
            foreach (var kv in Traders) Marker(g, Map(kv.Value), 6, Warn, kv.Key, Faint, diamond: true);
            foreach (var kv in Pois)
            {
                bool current = gs != null && gs.InPoi && string.Equals(gs.PoiName, kv.Key, StringComparison.OrdinalIgnoreCase);
                bool infested = current && gs.PoiEnemiesRemaining > 0;
                Color col = current ? (infested ? Danger : Good) : C(0x4a_3f3d);
                var p = Map(kv.Value);
                if (current)
                    using (var gb = new SolidBrush(Color.FromArgb(infested ? 40 : 30, col)))
                        g.FillEllipse(gb, p.X - minDim * 0.05f, p.Y - minDim * 0.05f, minDim * 0.10f, minDim * 0.10f);
                MarkerSquare(g, p, 7, col, kv.Key + (infested ? "  · INFESTED" : ""), current ? col : Faint);
            }

            // Player + combat.
            var pp = Map(_player);
            float ring = minDim * 0.15f;
            using (var rp = new Pen(Color.FromArgb(40, 232, 224, 220))) g.DrawEllipse(rp, pp.X - ring, pp.Y - ring, ring * 2, ring * 2);

            if (gs != null)
            {
                int n = Math.Min(gs.Enemies.Count, 48);
                for (int i = 0; i < n; i++)
                {
                    var en = gs.Enemies[i];
                    float a = i * 2.39996323f;
                    float rr = Math.Min(1.25f, en.Distance / 32f) * ring;
                    float ex = pp.X + (float)Math.Cos(a) * rr, ey = pp.Y + (float)Math.Sin(a) * rr;
                    Color col = en.IsTargetingPlayer ? Danger : (en.IsAlert ? Warn : Muted);
                    if (en.IsTargetingPlayer)
                        using (var lp = new Pen(Color.FromArgb(45, 238, 76, 44))) g.DrawLine(lp, ex, ey, pp.X, pp.Y);
                    g.FillEllipse(B(col), ex - 4, ey - 4, 8, 8);
                }
            }

            // Player token on top.
            using (var gb = new SolidBrush(Color.FromArgb(40, 244, 237, 233))) g.FillEllipse(gb, pp.X - 13, pp.Y - 13, 26, 26);
            g.FillEllipse(B(C(0xf4_ede9)), pp.X - 8, pp.Y - 8, 16, 16);
            g.FillEllipse(B(Bg), pp.X - 3, pp.Y - 3, 6, 6);

            // Board caption.
            if (gs != null)
                g.DrawString(PhaseLabel(gs), _fLabel, B(Ink), r.X + 12, r.Y + 10);
        }

        private void Marker(Graphics g, PointF p, float rad, Color fill, string label, Color labelColor, bool diamond = false)
        {
            if (diamond)
            {
                var pts = new[] { new PointF(p.X, p.Y - rad), new PointF(p.X + rad, p.Y), new PointF(p.X, p.Y + rad), new PointF(p.X - rad, p.Y) };
                g.FillPolygon(B(fill), pts);
            }
            else
            {
                g.FillEllipse(B(fill), p.X - rad, p.Y - rad, rad * 2, rad * 2);
                using (var pen = new Pen(Bg, 2)) g.DrawEllipse(pen, p.X - rad, p.Y - rad, rad * 2, rad * 2);
            }
            CenterLabel(g, label, p.X, p.Y + rad + 3, labelColor);
        }

        private void MarkerSquare(Graphics g, PointF p, float rad, Color fill, string label, Color labelColor)
        {
            g.FillRectangle(B(fill), p.X - rad, p.Y - rad, rad * 2, rad * 2);
            using (var pen = new Pen(Bg, 2)) g.DrawRectangle(pen, p.X - rad, p.Y - rad, rad * 2, rad * 2);
            CenterLabel(g, label, p.X, p.Y + rad + 3, labelColor);
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
            int x = r.X + 14, y = r.Y + 10, w = r.Width - 28;
            Header(g, "INPUTS · GAMESTATE", x, ref y, w);
            var gs = _gs;
            if (gs == null) { g.DrawString("waiting for first tick…", _fData, B(Faint), x, y); return; }

            Row(g, "World", $"Day {_runner.Generator.WorldDay} · h{SimClock.Now / 3600.0:F1}", x, ref y, w, Ink);
            Row(g, "Sky", (gs.IsDay ? "day" : "night") + (gs.IsBloodMoon ? " · BLOOD MOON" : ""), x, ref y, w, gs.IsBloodMoon ? Danger : Ink);
            Row(g, "Biome", gs.Biome ?? "?", x, ref y, w, Ink);
            Row(g, "Phase", ShortPhase(gs), x, ref y, w, Ink);

            y += 6; Sub(g, "THREAT", x, ref y, w);
            HealthRow(g, gs.HealthPct, x, ref y, w);
            Row(g, "Panicked", gs.IsPanicked ? "YES" : "no", x, ref y, w, gs.IsPanicked ? Danger : Faint);
            int targeting = gs.Enemies.Count(en => en.IsTargetingPlayer);
            int alert = gs.Enemies.Count(en => en.IsAlert && !en.IsTargetingPlayer);
            Row(g, "Enemies", gs.Enemies.Count.ToString(), x, ref y, w, gs.Enemies.Count > 0 ? Ink : Faint);
            Row(g, "Targeting", targeting.ToString(), x, ref y, w, targeting > 0 ? Danger : Faint);
            Row(g, "Aware only", alert.ToString(), x, ref y, w, alert > 0 ? Warn : Faint);

            y += 6; Sub(g, "POI", x, ref y, w);
            if (gs.InPoi)
            {
                Row(g, "Tier", "T" + gs.PoiTier, x, ref y, w, Ink);
                Row(g, "Status", gs.PoiEnemiesRemaining > 0 ? $"infested · {gs.PoiEnemiesRemaining} left" : "cleared", x, ref y, w,
                    gs.PoiEnemiesRemaining > 0 ? Warn : Good);
            }
            else Row(g, "Inside POI", "no", x, ref y, w, Faint);

            y += 6; Sub(g, "MULTIPLAYER", x, ref y, w);
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
            int x = r.X + 14, y = r.Y + 10, w = r.Width - 28;
            Header(g, "NOW PLAYING", x, ref y, w);

            using (var card = new SolidBrush(C(0x18_1214)))
                g.FillRectangle(card, x, y, w, 92);
            using (var p = new Pen(DangerDim)) g.DrawRectangle(p, x, y, w, 92);
            g.DrawString(_top, _fBig, B(Danger), new RectangleF(x + 10, y + 6, w - 20, 32), _clip);
            g.DrawString(_file, _fData, B(Ink), new RectangleF(x + 10, y + 44, w - 20, 18), _clip);
            string norm = _normalized ? "✓ normalized" : "· unity gain";
            g.DrawString(norm, _fDataSm, B(_normalized ? Good : Faint), x + 10, y + 68);
            int voices = TryVoices();
            string mix = (_fast ? "muted (max)" : "realtime") + (voices >= 0 ? $" · {voices}v" : "");
            g.DrawString(mix, _fDataSm, B(_fast ? Faint : Audio), x + w - 10, y + 68, _right);
            y += 104;

            Header(g, "RESOLUTION · ACTIVE STACK", x, ref y, w);
            var tokens = _runner.LastTokens;
            if (tokens == null || tokens.Count == 0) { g.DrawString("(no active contexts)", _fData, B(Faint), x, y); return; }

            var ordered = tokens
                .Select(t => (name: t, pri: ContextMusicManager.GetContext(t)?.Priority ?? -99))
                .OrderByDescending(t => t.pri).Take(13).ToList();
            foreach (var (name, pri) in ordered)
            {
                bool win = string.Equals(name, _top, StringComparison.OrdinalIgnoreCase);
                var rowRect = new Rectangle(x, y, w, 20);
                if (win)
                {
                    using (var wb = new SolidBrush(DangerDim)) g.FillRectangle(wb, rowRect);
                    using (var wp = new Pen(Color.FromArgb(120, 122, 42, 27))) g.DrawRectangle(wp, rowRect);
                }
                g.DrawString(pri.ToString(), _fData, B(win ? Danger : Faint), x + 30, y + 2, _right);
                g.DrawString(name, _fData, B(win ? Ink : Muted), x + 40, y + 2);
                if (win) g.DrawString("▶", _fData, B(Danger), x + w - 16, y + 2);
                y += 22;
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
            using (var bg = new SolidBrush(C(0x10_0d0e))) g.FillRectangle(bg, r);
            _buttons.Clear();
            _btnX = r.X + 10; _btnX0 = r.X + 10; _btnXMax = r.Right - 8;
            _btnY0 = r.Y + 8; _btnRow = 0; _btnRowH = 27;
            bool man = _mode == Mode.Manual;

            AddBtn(g, "auto", "Autopilot", _mode == Mode.Autopilot, true, seg: true);
            AddBtn(g, "manual", "Manual", man, true, seg: true);
            _btnX += 14;
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
            int w = (int)sz.Width + 18;
            if (_btnX + w > _btnXMax) { _btnRow++; _btnX = _btnX0; }   // wrap to next row
            int y = _btnY0 + _btnRow * _btnRowH;
            var rect = new Rectangle(_btnX, y, w, 22);
            Color face = seg ? Audio : Warn;
            Color faceBg = on ? Color.FromArgb(38, face) : C(0x1b_1618);
            using (var b = new SolidBrush(faceBg)) g.FillRectangle(b, rect);
            using (var p = new Pen(enabled ? (on ? face : Line) : C(0x22_1c1e))) g.DrawRectangle(p, rect);
            Color tc = enabled ? (on ? face : Ink) : C(0x4a_423f);
            g.DrawString(label, _fBadge, B(tc), _btnX + 9, y + 4);
            _buttons.Add(new Btn { R = rect, Id = id, Label = label, On = on, Enabled = enabled });
            _btnX += w + 7;
        }

        // ── Ticker ──
        private void DrawTicker(Graphics g, Rectangle r)
        {
            g.FillRectangle(B(Panel2), r);
            int x = r.X + 12; int y = r.Y + 8;
            g.DrawString("TRANSITIONS", _fBadge, B(Faint), x, y);
            x += 96;
            foreach (var ev in _ticker.Reverse())
            {
                string s = $"h{ev.t / 3600.0:F1}  {ev.top}" + (ev.file != null ? $" ← {ev.file}" : "");
                var sz = g.MeasureString(s, _fDataSm);
                if (x + sz.Width > r.Width - 10) break;
                g.FillEllipse(B(Danger), x, y + 4, 6, 6);
                g.DrawString(s, _fDataSm, B(Muted), x + 12, y);
                x += (int)sz.Width + 34;
            }
        }

        // ── Status strip ──
        private void DrawStatus(Graphics g, Rectangle r)
        {
            using (var bg = new SolidBrush(C(0x16_1213))) g.FillRectangle(bg, r);
            int y = 8;
            g.DrawString("CmmSessionView", _fHeader, B(Ink), 14, y);
            int x = 150;
            var gs = _gs;
            if (gs != null)
            {
                x = Chip(g, "Day " + _runner.Generator.WorldDay, gs.IsBloodMoon ? Danger : Ink, x, y);
                if (gs.IsBloodMoon) x = Chip(g, "BLOOD MOON", Danger, x, y);
                x = Chip(g, gs.IsDay ? "day" : "night", Muted, x, y);
            }
            x = Chip(g, "seed " + _runner.Options.Seed, Faint, x, y);

            // Right-aligned: mode + hints.
            string mode = _paused ? "PAUSED" : _mode == Mode.Manual ? "MANUAL" : _fast ? "MAX SPEED" : "REAL-TIME 1×";
            Color mc = _paused ? Warn : _mode == Mode.Manual ? Warn : _fast ? Muted : Audio;
            string hints = "[Space] pause   [Tab] speed   [M] manual   [Esc] quit";
            var hsz = g.MeasureString(hints, _fDataSm);
            g.DrawString(hints, _fDataSm, B(Faint), r.Width - hsz.Width - 12, y + 1);
            var msz = g.MeasureString(mode, _fBadge);
            float mx = r.Width - hsz.Width - msz.Width - 34;
            using (var mb = new SolidBrush(Color.FromArgb(30, mc))) g.FillRectangle(mb, mx - 6, y - 1, msz.Width + 12, 18);
            g.DrawString(mode, _fBadge, B(mc), mx, y);
        }

        private int Chip(Graphics g, string s, Color col, int x, int y)
        {
            var sz = g.MeasureString(s, _fData);
            g.DrawString(s, _fData, B(col), x, y);
            return x + (int)sz.Width + 20;
        }

        // ── HUD row helpers ──
        private void Header(Graphics g, string s, int x, ref int y, int w)
        {
            g.DrawString(s, _fHeader, B(Muted), x, y);
            using (var p = new Pen(Line)) g.DrawLine(p, x, y + 18, x + w, y + 18);
            y += 26;
        }
        private void Sub(Graphics g, string s, int x, ref int y, int w)
        {
            g.DrawString(s, _fBadge, B(Faint), x, y);
            y += 18;
        }
        private void Row(Graphics g, string k, string v, int x, ref int y, int w, Color vc)
        {
            g.DrawString(k, _fLabel, B(Muted), x, y);
            g.DrawString(v, _fData, B(vc), x + w, y + 1, _right);
            y += 22;
        }
        private void HealthRow(Graphics g, float pct, int x, ref int y, int w)
        {
            g.DrawString("Health", _fLabel, B(Muted), x, y);
            int bw = 90, bx = x + w - bw, by = y + 4;
            g.FillRectangle(B(C(0x24_1d1f)), bx, by, bw, 8);
            Color hc = pct < 0.33f ? Danger : pct < 0.6f ? Warn : Good;
            g.FillRectangle(B(hc), bx, by, Math.Max(1, (int)(bw * pct)), 8);
            g.DrawString((pct * 100).ToString("0") + "%", _fDataSm, B(hc), bx - 6, y + 1, _right);
            y += 22;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            try { _runner.Stop(); } catch { }
            foreach (var b in _brushes.Values) b.Dispose();
            _fHeader?.Dispose(); _fLabel?.Dispose(); _fData?.Dispose(); _fDataSm?.Dispose(); _fBig?.Dispose(); _fBadge?.Dispose();
            _center.Dispose(); _right.Dispose(); _clip.Dispose();
            base.OnFormClosed(e);
        }

        // Small helper: a radial blood-moon wash (kept out of the hot path when not on a blood moon).
        private sealed class PathGradientBrushSafe : IDisposable
        {
            private readonly GraphicsPath _path; private readonly PathGradientBrush _brush;
            public PathGradientBrushSafe(Rectangle r, Color center, Color edge)
            {
                _path = new GraphicsPath();
                _path.AddEllipse(r.X + r.Width * 0.45f, r.Y - r.Height * 0.2f, r.Width * 0.7f, r.Height * 0.9f);
                _brush = new PathGradientBrush(_path) { CenterColor = center, SurroundColors = new[] { edge } };
            }
            public void Fill(Graphics g, Rectangle r) => g.FillRectangle(_brush, r);
            public void Dispose() { _brush.Dispose(); _path.Dispose(); }
        }
    }
}
