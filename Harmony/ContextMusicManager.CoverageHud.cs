// Compile-time gated alongside the context HUD (CMM_DEBUG_HUD).
#if CMM_DEBUG_HUD
using System;
using System.Collections.Generic;
using System.IO;
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


        // ---- HOTKEY HINTS -------------------------------------------------------------
        // Both panels advertise their own keys in their header, and both used to do it with
        // a HARDCODED string. When the bindings moved (coverage took Numpad8, the context HUD
        // went to Numpad9, the GC re-arm to Numpad6) the labels kept naming the old ones, so
        // the context HUD sat there claiming "Num9 reset GC" while Num9 was the panel itself.
        // Read the binding, and colour it so it reads as a control rather than prose.
        private const string KeyHi = "#8fe3ff";

        internal static string KeyLabel(KeyCode k)
        {
            int n = (int)k - (int)KeyCode.Keypad0;
            string name = (n >= 0 && n <= 9) ? "Num" + n : k.ToString();
            string mod = "";
            if (CMMConfig.KeybindRequireCtrl) mod += "Ctrl+";
            if (CMMConfig.KeybindRequireAlt) mod += "Alt+";
            return mod + name;
        }

        // "<key> what it does", with the key highlighted.
        internal static string KeyHint(KeyCode k, string what) =>
            Col(KeyHi, KeyLabel(k)) + " " + what;

        internal static void CycleCoverageHud()
        {
            _covMode = (_covMode + 1) % 3;
            _covNextAt = 0f;                      // redraw with fresh data immediately
            // Deliberately NO ShowNotice. The notice box owns the same corner and pushes
            // this panel down for its lifetime, so the thing you just summoned jumps as it
            // appears and again when the notice expires. The context HUD is silent for the
            // same reason; the header says which mode it is in.
        }

        // One row of the panel, already coloured.
        private struct CovRow { public string Text; public bool Gap; }

        // Playable track count for a context: the enumerated snapshot, which already has
        // playlists expanded into their entries (that is the same array PickNextFile draws
        // from), so a .m3u station counts its tracks rather than counting as one file.
        // COUNT IT OURSELVES. directorySnapshot is the PLAYBACK engine's view, filled by the
        // crawler and the file watch -- neither of which has run on the main menu, so every row
        // read "-" there and nothing ever changed. This panel is a coverage tool, not a cache
        // viewer: it enumerates the source folders on its own cadence so the numbers are true
        // the moment it opens, and follow a file being dropped in without waiting for playback.
        // The engine's snapshot is still preferred when it exists -- it is authoritative about
        // what will actually be picked, including banned-track pruning.
        private static readonly Dictionary<string, int> _covCount =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _covPlCache =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // OUR SCAN WINS. Preferring directorySnapshot was wrong in the one situation this
        // panel is used in: the moment a context has been scanned once its snapshot is
        // non-null, so moving files in or out of that folder changed nothing on screen until
        // the engine's own file watch got round to it. The snapshot is only a fallback now,
        // for a folder our scan could not read at all.
        private static int CovCount(string name)
        {
            int n;
            if (_covCount.TryGetValue(name, out n)) return n;
            ContextCache c;
            if (cacheMap.TryGetValue(name, out c) && c != null && c.directorySnapshot != null)
                return c.directorySnapshot.Length;
            return 0;
        }

        // One context's playable entries, straight off disk. Playlists are expanded, which is
        // the difference between a station reading 1 and reading its real track count.
        private static int CovScan(Contexts ctx)
        {
            int total = 0;
            try
            {
                if (ctx.IsPlaylist)
                {
                    // A ROOTED playlist path is the same file whichever music root we are
                    // looking from, so it is read once. A relative one is per-root: an add-on
                    // pack can ship its own copy alongside the primary and both play.
                    bool rooted = Path.IsPathRooted(ctx.Source);
                    foreach (MusicRoot r in MusicRoots.All)
                    {
                        string p = rooted ? MusicRoots.Normalize(ctx.Source)
                                          : MusicRoots.Combine(r.Path, ctx.Source);
                        if (!File.Exists(p)) { if (rooted) break; continue; }
                        int cached;
                        string key = p + "|" + new FileInfo(p).LastWriteTimeUtc.Ticks;
                        if (!_covPlCache.TryGetValue(key, out cached))
                        {
                            string[] tr = PlaylistSource.ReadTracks(p);
                            cached = tr != null ? tr.Length : 0;
                            // Keyed by path+mtime, so every edit adds an entry. Editing a
                            // playlist while the panel is open would grow this without end.
                            if (_covPlCache.Count > 256) _covPlCache.Clear();
                            _covPlCache[key] = cached;
                        }
                        total += cached;
                        if (rooted) break;
                    }
                    return total;
                }
                foreach (string dir in MusicRoots.DirsFor(ctx))
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    // subfolders="true" recurses and ignores .m3u, per the source contract.
                    string[] files = Directory.GetFiles(dir, "*",
                        ctx.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                    for (int i = 0; i < files.Length; i++)
                    {
                        string ext = Path.GetExtension(files[i]);
                        if (AudioDecoder.IsSupported(files[i])) total++;
                        else if (!ctx.IncludeSubfolders
                              && (string.Equals(ext, ".m3u", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(ext, ".m3u8", StringComparison.OrdinalIgnoreCase)))
                        {
                            string[] tr = PlaylistSource.ReadTracks(files[i]);
                            if (tr != null) total += tr.Length;
                        }
                    }
                }
            }
            catch { }
            return total;
        }

        // A SCANNED ZERO IS A REAL ZERO. Falling through to ContextHasFiles when our own
        // count came back 0 meant emptying a folder changed the NUMBER on screen but not the
        // colour or the chain: the engine's _contextsWithFiles still said yes, so the row
        // stayed green and nothing downstream re-resolved. Adding files worked, removing them
        // did not. If the scan has an entry for this context -- even a zero -- it is the truth.
        private static bool CovPlayable(string name)
        {
            int n;
            if (_covCount.TryGetValue(name, out n)) return n > 0;
            try { return ContextHasFiles(name); } catch { return false; }
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
            long nTracks = 0;   // every playable file the panel can see, contexts + stations

            // Snapshot the table first. It can be swapped wholesale by the config watcher
            // on another thread, and a half-enumerated dictionary is the one failure this
            // panel must not turn into a dead frame.
            _covCtx.Clear();
            foreach (Contexts c in contexts.Values) if (c != null) _covCtx.Add(c);

            // Our own disk counts, and the station catalogue. EnsureCatalog is normally only
            // driven by the world-emitter layer, which does not tick on the main menu -- so
            // without this the station list was simply empty there, which is exactly what it
            // looked like. It is self-throttled, so calling it here is free once warm.
            _covCount.Clear();   // so a renamed or deleted context leaves nothing behind
            for (int i = 0; i < _covCtx.Count; i++)
                _covCount[_covCtx[i].Context] = CovScan(_covCtx[i]);
            try { RadioStationCatalog.EnsureCatalog(); } catch { }
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
                // A context that hands to the STACK cannot strand anything: when it is skipped,
                // whatever else is active takes over -- POI indoors, Ambient outdoors. So it is
                // never worth warning about, which is what Home was doing. Home has exactly one
                // sub-variant, Home_Night, and it is TIME-gated: filling home/night made
                // "all sub-variants populated" true while Home carries on playing all day, and
                // even if it did not, the stack would cover it. The warning only means something
                // where being skipped actually costs you the music you put in the folder.
                bool stackFallback = string.Equals(ctx.Fallback, "Stack", StringComparison.OrdinalIgnoreCase)
                                     || string.IsNullOrEmpty(ctx.Fallback);
                bool allKids = kids > 0 && kidsFull == kids && !stackFallback;

                string term, termName;
                CovChain(ctx, _covHops, out term, out termName);

                string glyph, hex, tail = "";
                bool gap;
                if (playable && allKids && kids >= 3)
                { glyph = "\u2717"; hex = CovRed; gap = true; nSha++;
                  tail = Col(CovRed, "unreachable \u2014 all sub-variants populated"); }
                else if (playable)
                { glyph = "\u25cf"; hex = CovGreen; gap = false; nPop++; }
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
                        sb.Append(Col(CovGreen, "\u25cf " + termName + " (" + CovCount(termName) + ")"));   // brackets: names carry digits (POI_T3)
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

                nTracks += n;
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
                    // Stations sit UNDER the Radio context but their tracks are not counted in
                    // it -- Radio's own source folder holds the station subfolders, not audio --
                    // so adding them here totals rather than double counts.
                    nTracks += n;
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

            _covSummary = nTracks.ToString("N0") + " tracks \u2022 " + nPop + " with tracks \u2022 " + nCov + " with fallback \u2022 " + nStk
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
                // DENSER THAN THE CONTEXT HUD'S 0.62. This panel is a wall of small
                // monospaced text with five colours doing real work in it, and at 0.62 a
                // bright scene bleeds through enough to make blue and violet hard to tell
                // apart. The context HUD is a dozen wide rows and gets away with it.
                _covBgTex = new Texture2D(1, 1);
                _covBgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.88f));
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

            const float padX = 12f, headerH = 22f, legendH = 18f, padBottom = 12f;
            float colW = 78 * 7.3f;

            // ASK THE STYLE, DO NOT MULTIPLY BY lineHeight. A dynamic OS font reports a
            // lineHeight smaller than the spacing GUI.Label actually renders at, so counting
            // newlines and multiplying came out short and clipped the last row or two off the
            // bottom. CalcHeight measures the block the label will really draw.
            float bodyH = 0f;
            for (int c = 0; c < _covCols.Length; c++)
            {
                float ch = _covBodyStyle.CalcHeight(new GUIContent(_covCols[c]), colW);
                if (ch > bodyH) bodyH = ch;
            }
            if (bodyH <= 0f) bodyH = 15f;
            float w = padX * 2f + colW * _covCols.Length;
            float h = headerH + legendH + bodyH + padBottom;
            float x = 10f;
            float y = 10f + (OverlayOnScreen ? OverlayClearanceHeight : 0f);
            if (_hudMode != 0 && _hudBottomY > y) y = _hudBottomY + 8f;   // stack under the context HUD

            GUI.DrawTexture(new Rect(x, y, w, h), _covBgTex, ScaleMode.StretchToFill);
            GUI.Label(new Rect(x + padX, y + 4f, w - padX * 2f, headerH),
                "CMM \u2022 Coverage (" + (_covMode == 1 ? "everything" : "gaps only") + ")  \u2022  "
                + _covSummary + "   [" + KeyHint(CMMConfig.KeyCoverageHud, "cycle") + "]", _covHeadStyle);
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
