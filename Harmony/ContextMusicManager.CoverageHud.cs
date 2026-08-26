// Compile-time gated alongside the context HUD (CMM_DEBUG_HUD).
#if CMM_DEBUG_HUD
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // COVERAGE HUD  (Alt+Numpad8: off → everything → gaps only → off)
    // ─────────────────────────────────────────────────────────────────────────
    // "Which contexts actually have music, and what happens when they do not."
    //
    // One row per context: a state glyph, the context name, its playable track
    // count, and — when it is empty — the fallback chain it walks, with the
    // context that will really be heard marked in green at the end of it.
    //
    //   ● green    has its own tracks
    //   ● blue     empty, but a fallback covers it        → chain ends ● Name N
    //   ▲ amber    the chain ends at the game's own music → ▲ vanilla
    //   ▼ violet   the chain hands back to the priority stack
    //   ✕ red      every sub-variant is populated, so nothing can reach this one
    //   ✕ pink     radio station with no tracks — dead air
    //
    // EVERYTHING IS READ LIVE. The rows are rebuilt from `contexts`, `cacheMap`
    // and the station catalogue on a one-second timer while the panel is open,
    // so editing contexts.xml, editing context.lua, dropping files into a folder
    // or deleting a station all show up on their own within a second — there is
    // no cached tree to invalidate and nothing to hook into the reload path.
    //
    // The rebuild is also defensive: the config watcher runs on CmmScheduler, so
    // `contexts` and `cacheMap` can be REPLACED under this enumeration. Any throw
    // keeps the previous rows and retries shortly, which is why a hot reload
    // flickers at worst instead of taking the frame down.
    public partial class ContextMusicManager
    {
        private static int _covMode;              // 0 hidden · 1 everything · 2 gaps only
        internal static bool CoverageOnScreen => _covMode != 0;

        private const float CovInterval = 1.0f;   // live rebuild cadence while open
        private const float CovRetry    = 0.25f;  // ...and after a rebuild lost a race
        private static float _covNextAt;
        private static string[] _covCols = new string[0];
        private static string _covSummary = "";
        private static GUIStyle _covBodyStyle, _covHeadStyle;
        private static Texture2D _covBgTex;
        private static Font _covFont;

        private const string CovGreen = "#7dff7d";
        private const string CovBlue  = "#7fc7ff";
        private const string CovAmber = "#ffd27f";
        private const string CovViolet= "#b9a8ff";
        private const string CovRed   = "#ff6b6b";
        private const string CovPink  = "#ff8a8a";
        private const string CovDim   = "#7f868e";

        internal static void CycleCoverageHud()
        {
            _covMode = (_covMode + 1) % 3;
            _covNextAt = 0f;                      // redraw with fresh data immediately
            ShowNotice(_covMode == 0 ? "Coverage: off"
                     : _covMode == 1 ? "Coverage: everything"
                                     : "Coverage: gaps only");
        }

        // One row of the panel, already coloured.
        private struct CovRow { public string Text; public bool Gap; }

        // Playable track count for a context: the enumerated snapshot, which already has
        // playlists expanded into their entries (that is the same array PickNextFile draws
        // from), so a .m3u station counts its tracks rather than counting as one file.
        private static int CovCount(string name)
        {
            ContextCache c;
            if (!cacheMap.TryGetValue(name, out c) || c == null) return 0;
            string[] snap = c.directorySnapshot;
            return snap != null ? snap.Length : 0;
        }

        private static bool CovPlayable(string name)
        {
            try { return ContextHasFiles(name); } catch { return CovCount(name) > 0; }
        }

        private static string Pad(string s, int w)
        {
            if (s == null) s = "";
            if (s.Length >= w) return s.Substring(0, w);
            return s + new string(' ', w - s.Length);
        }
        private static string LPad(string s, int w)
        {
            if (s == null) s = "";
            return s.Length >= w ? s : new string(' ', w - s.Length) + s;
        }
        private static string Col(string hex, string body) => "<color=" + hex + ">" + body + "</color>";

        // Walk this context's fallback chain until something has tracks, or it ends.
        // Returns the hops taken; `term` is "ctx" / "vanilla" / "stack".
        private static void CovChain(Contexts ctx, List<string> hops, out string term, out string termName)
        {
            term = "vanilla"; termName = "";
            hops.Clear();
            Contexts cur = ctx;
            for (int guard = 0; guard < 8 && cur != null; guard++)
            {
                string f = cur.Fallback ?? "";
                if (f.Length == 0 || string.Equals(f, "Stack", StringComparison.OrdinalIgnoreCase))
                { term = "stack"; return; }
                if (f.StartsWith("Vanilla", StringComparison.OrdinalIgnoreCase))
                { term = "vanilla"; termName = f; return; }
                Contexts nx = GetContext(f);
                if (nx == null || string.Equals(f, ctx.Context, StringComparison.OrdinalIgnoreCase))
                { term = "vanilla"; termName = f; return; }
                hops.Add(f);
                if (CovPlayable(f)) { term = "ctx"; termName = f; return; }
                cur = nx;
            }
        }

        private static readonly List<CovRow> _covScratch = new List<CovRow>();
        private static readonly List<string> _covHops = new List<string>();
        private static readonly List<Contexts> _covCtx = new List<Contexts>();

        private static void RebuildCoverage()
        {
            _covScratch.Clear();
            int nPop = 0, nCov = 0, nStk = 0, nVan = 0, nSha = 0, nDead = 0;

            // Snapshot the table first. It can be swapped wholesale by the config watcher
            // on another thread, and a half-enumerated dictionary is the one failure this
            // panel must not turn into a dead frame.
            _covCtx.Clear();
            foreach (Contexts c in contexts.Values) if (c != null) _covCtx.Add(c);
            _covCtx.Sort(delegate (Contexts a, Contexts b)
            {
                int p = b.Priority.CompareTo(a.Priority);
                return p != 0 ? p : string.Compare(a.Context, b.Context, StringComparison.OrdinalIgnoreCase);
            });

            for (int i = 0; i < _covCtx.Count; i++)
            {
                Contexts ctx = _covCtx[i];
                string name = ctx.Context;
                int n = CovCount(name);
                bool playable = CovPlayable(name);

                // Sub-variants: everything that falls back to THIS context.
                int kids = 0, kidsFull = 0;
                for (int k = 0; k < _covCtx.Count; k++)
                {
                    if (k == i) continue;
                    if (string.Equals(_covCtx[k].Fallback, name, StringComparison.OrdinalIgnoreCase))
                    { kids++; if (CovPlayable(_covCtx[k].Context)) kidsFull++; }
                }
                bool allKids = kids > 0 && kidsFull == kids;

                string term, termName;
                CovChain(ctx, _covHops, out term, out termName);

                string glyph, hex, tail = "";
                bool gap;
                if (playable && allKids && kids >= 3)
                { glyph = "\u2717"; hex = CovRed; gap = true; nSha++;
                  tail = Col(CovRed, "unreachable \u2014 all sub-variants populated"); }
                else if (playable)
                { glyph = "\u25cf"; hex = CovGreen; gap = false; nPop++;
                  if (allKids) tail = Col(CovAmber, "\u26a0 all sub-variants populated \u2014 check it still applies"); }
                else if (term == "ctx")
                { glyph = "\u25cf"; hex = CovBlue; gap = false; nCov++; }
                else if (term == "stack")
                { glyph = "\u25bc"; hex = CovViolet; gap = true; nStk++; }
                else
                { glyph = "\u25b2"; hex = CovAmber; gap = true; nVan++; }

                if (tail.Length == 0 && !playable)
                {
                    var sb = new StringBuilder();
                    sb.Append(Col(CovDim, "\u2192 "));
                    if (term == "ctx")
                    {
                        for (int h = 0; h < _covHops.Count - 1; h++)
                            sb.Append(Col(CovDim, _covHops[h] + " \u2192 "));
                        sb.Append(Col(CovGreen, "\u25cf " + termName + " " + CovCount(termName)));
                    }
                    else
                    {
                        for (int h = 0; h < _covHops.Count; h++)
                            sb.Append(Col(CovDim, _covHops[h] + " \u2192 "));
                        sb.Append(term == "stack" ? Col(CovViolet, "\u25bc stack")
                                                  : Col(CovAmber, "\u25b2 vanilla"));
                    }
                    tail = sb.ToString();
                }

                _covScratch.Add(new CovRow
                {
                    Gap = gap,
                    Text = Col(hex, glyph + " " + Pad(name, 25) + LPad(n > 0 ? n.ToString() : "-", 5)) + "  " + tail
                });
            }

            // Radio stations are VIRTUAL contexts: subfolders of the Radio source, discovered
            // at runtime, with no entry in contexts.xml and no fallback of their own. An empty
            // one is not covered by anything — that dial position is silent.
            try
            {
                var st = RadioStationCatalog.Stations;
                for (int i = 0; i < st.Count; i++)
                {
                    RadioStation s = st[i];
                    if (s == null) continue;
                    int n = s.Order != null ? s.Order.Length : 0;
                    bool live = n > 0;
                    if (!live) nDead++;
                    _covScratch.Add(new CovRow
                    {
                        Gap = !live,
                        Text = Col(live ? CovGreen : CovPink,
                                   (live ? "\u25cf" : "\u2717") + "   " + Pad(s.Name, 23) + LPad(live ? n.ToString() : "-", 5))
                             + "  " + (live ? Col(CovDim, "station") : Col(CovPink, "dead air \u2014 no tracks"))
                    });
                }
            }
            catch { /* catalogue rebuilding under us — next pass picks it up */ }

            _covSummary = nPop + " with tracks \u2022 " + nCov + " with fallback \u2022 " + nStk
                        + " to stack \u2022 " + nVan + " to vanilla"
                        + (nSha > 0 ? " \u2022 " + nSha + " never play" : "")
                        + (nDead > 0 ? " \u2022 " + nDead + " dead air" : "");

            // Fold into however many columns fit the screen.
            var keep = new List<string>();
            for (int i = 0; i < _covScratch.Count; i++)
                if (_covMode == 1 || _covScratch[i].Gap) keep.Add(_covScratch[i].Text);

            int colChars = 78;
            float charPx = 7.3f;
            int maxCols = Mathf.Clamp(Mathf.FloorToInt((Screen.width - 60f) / (colChars * charPx)), 1, 3);
            int rows = Mathf.Max(1, Mathf.CeilToInt(keep.Count / (float)maxCols));
            int cols = Mathf.Max(1, Mathf.CeilToInt(keep.Count / (float)rows));

            var built = new string[cols];
            var sbs = new StringBuilder[cols];
            for (int c = 0; c < cols; c++) sbs[c] = new StringBuilder();
            for (int i = 0; i < keep.Count; i++)
            {
                int c = Mathf.Min(cols - 1, i / rows);
                sbs[c].Append(keep[i]).Append('\n');
            }
            for (int c = 0; c < cols; c++) built[c] = sbs[c].ToString();
            _covCols = built;
        }

        private static void EnsureCoverageStyles()
        {
            if (_covBgTex == null)
            {
                _covBgTex = new Texture2D(1, 1);
                _covBgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.62f));
                _covBgTex.Apply();
            }
            // A MONOSPACE FACE, because every column in this panel is aligned by padding
            // and Unity's default label font is proportional — which is exactly why the
            // context HUD's own label column has never lined up either.
            if (_covFont == null)
            {
                try { _covFont = Font.CreateDynamicFontFromOSFont(
                        new[] { "Consolas", "Cascadia Mono", "Courier New", "DejaVu Sans Mono", "Menlo" }, 13); }
                catch { _covFont = null; }
            }
            if (_covBodyStyle == null)
            {
                _covBodyStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 13, alignment = TextAnchor.UpperLeft, wordWrap = false, richText = true };
                _covBodyStyle.normal.textColor = Color.white;
                if (_covFont != null) _covBodyStyle.font = _covFont;
            }
            if (_covHeadStyle == null)
            {
                _covHeadStyle = new GUIStyle(GUI.skin.label)
                { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft, richText = true };
                _covHeadStyle.normal.textColor = new Color(1f, 0.85f, 0.4f);
            }
        }

        private static void DrawCoverageHud()
        {
            if (_covMode == 0) return;
            EnsureCoverageStyles();

            float now = Time.realtimeSinceStartup;
            if (now >= _covNextAt)
            {
                try { RebuildCoverage(); _covNextAt = now + CovInterval; }
                catch { _covNextAt = now + CovRetry; }   // reload race — keep the last good view
            }
            if (_covCols.Length == 0) return;

            const float padX = 12f, headerH = 22f, legendH = 18f;
            float colW = 78 * 7.3f;
            float lineH = _covBodyStyle.lineHeight > 0 ? _covBodyStyle.lineHeight : 15f;
            int maxLines = 0;
            for (int c = 0; c < _covCols.Length; c++)
            {
                int n = 0; string s = _covCols[c];
                for (int i = 0; i < s.Length; i++) if (s[i] == '\n') n++;
                if (n > maxLines) maxLines = n;
            }
            float bodyH = Mathf.Max(lineH, maxLines * lineH);
            float w = padX * 2f + colW * _covCols.Length;
            float h = headerH + legendH + bodyH + 14f;
            float x = 10f;
            float y = 10f + (OverlayOnScreen ? OverlayClearanceHeight : 0f);
            if (_hudMode != 0 && _hudBottomY > y) y = _hudBottomY + 8f;   // stack under the context HUD

            GUI.DrawTexture(new Rect(x, y, w, h), _covBgTex, ScaleMode.StretchToFill);
            GUI.Label(new Rect(x + padX, y + 4f, w - padX * 2f, headerH),
                "CMM \u2022 Coverage (" + (_covMode == 1 ? "everything" : "gaps only") + ")  \u2022  "
                + _covSummary + "   [Num8 cycle]", _covHeadStyle);
            GUI.Label(new Rect(x + padX, y + 4f + headerH, w - padX * 2f, legendH),
                Col(CovGreen, "\u25cf has tracks") + "   " + Col(CovBlue, "\u25cf has fallback") + "   "
                + Col(CovAmber, "\u25b2 vanilla") + "   " + Col(CovViolet, "\u25bc stack") + "   "
                + Col(CovRed, "\u2717 never plays") + "   " + Col(CovPink, "\u2717 dead air"),
                _covBodyStyle);
            for (int c = 0; c < _covCols.Length; c++)
                GUI.Label(new Rect(x + padX + c * colW, y + 4f + headerH + legendH, colW, bodyH),
                          _covCols[c], _covBodyStyle);
        }
    }
}
#endif
