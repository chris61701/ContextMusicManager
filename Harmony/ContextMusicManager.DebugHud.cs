// Compile-time gated: included only when CMM_DEBUG_HUD is defined (Debug builds, or
// any build with -p:EnableDebugHud=true). Release/shipping builds strip it entirely.
#if CMM_DEBUG_HUD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // DEBUG HUD
    // ─────────────────────────────────────────────────────────────────────────
    // A small top-left overlay describing what is playing and WHY. Toggled with
    // Numpad 8; hidden by default and purely diagnostic — it reads engine state and
    // never influences playback. Lines, top to bottom:
    //   Playing   audible context (+ "(fallback)" when we landed off the resolved
    //             set), then "Time:" elapsed / total and "Codec:" decoded format
    //             (plus "(Volume: NN%)" when the per-track trim is off its 100%
    //             default). Time and Codec appear only while a real custom track is
    //             audible — both are absent while riding a borrowed vanilla bed.
    //   Resolved  the tokens the Lua script last emitted; any with NO files are red
    //   Fallback  the chain the resolver walked to reach the playing context —
    //             shown ONLY when a fallback actually happened; empty links are red
    //   File      the audible file (name + extension)
    //   Path      the containing folder (no filename) — relative to the Music dir, or
    //             the absolute folder when the context was configured with one
    //   Source    the active decision script (on-disk context.lua / embedded default)
    //   Snapshots directory snapshots (every eligible file path per context) plus, on
    //             the same line, the normalized (loudness-measured) files in memory

    public partial class ContextMusicManager
    {
        private static bool _threatHudVisible = false;

        private GUIStyle _hudBodyStyle;
        private GUIStyle _hudHeaderStyle;
        private Texture2D _hudBgTex;

        // Codec detection sniffs the file header (Ogg can carry Vorbis or Opus), which
        // is disk I/O — far too costly to repeat every OnGUI. Cache it per audible file
        // so the probe runs once per track, not once per frame.
        private static string _hudCodecForFile;
        private static string _hudCodecLabel = "";

        // Red used for context tokens that currently resolve to no playable files.
        private const string HudRed = "#ff6b6b";

        // Cached memory/count diagnostics. Recomputed on a throttled timer (not every
        // OnGUI frame): summing string lengths across the directory snapshots is O(files)
        // and would be ruinous per-frame on a large library. Only ever computed while the
        // HUD is open (DrawDebugHud returns early when hidden).
        private const float HudStatsInterval = 2f;
        private static float _hudStatsNextTime;
        private static int _hudNormCount, _hudSnapFiles;
        private static long _hudNormBytes, _hudSnapBytes;

        // Called from the unified Update() in ContextMusicManager.Keybinds.cs (a
        // MonoBehaviour may declare Update() only once, so the HUD toggle lives here
        // as a helper rather than its own Update). Compiled only with the HUD.
        private void HandleDebugHudToggle()
        {
            // Numpad 8 toggles the HUD. Cheap per-frame check, no allocation.
            if (Input.GetKeyDown(KeyCode.Keypad8))
            {
                _threatHudVisible = !_threatHudVisible;
                CMMConfig.D($"[CMM] Context HUD {(_threatHudVisible ? "shown" : "hidden")}.");
            }
        }

        // Drawn from the unified OnGUI() in ContextMusicManager.Keybinds.cs (a
        // MonoBehaviour may declare OnGUI() only once). Compiled only with the HUD.
        private void DrawDebugHud()
        {
            // Runs on the menu too (Menu music is a real context). playerIsDead is an
            // in-game-only state, so only consult it while actually in a game.
            if (!_threatHudVisible) return;
            if (inGame && playerIsDead) return;

            EnsureHudStyles();

            // Refresh the memory/count diagnostics on a throttled cadence (see fields).
            if (Time.realtimeSinceStartup >= _hudStatsNextTime)
            {
                _hudStatsNextTime = Time.realtimeSinceStartup + HudStatsInterval;
                RecomputeHudStats();
            }

            // ── Gather state ───────────────────────────────────────────────────
            string source = ScriptHost.Active
                ? (ScriptHost.UsingEmbeddedDefault ? "context.lua (embedded default)" : "context.lua")
                : $"<color={HudRed}>NONE — script failed to load</color>";

            IReadOnlyList<string> resolved = ScriptHost.LastContexts;

            // The context the listener actually hears (the channel's context), falling
            // back to the resolver's logical pick before audio has settled.
            string audible = ActiveChannelContext;
            if (string.IsNullOrEmpty(audible)) audible = currentContext?.Context;

            cacheMap.TryGetValue(audible ?? "", out ContextCache cache);
            string playingFile = cache?.playingFile;   // null while riding a borrowed vanilla bed

            // A fallback happened when what we're playing is not one of the resolved
            // tokens — i.e. the resolver walked a chain to land somewhere else.
            bool fellBack = !string.IsNullOrEmpty(audible)
                            && resolved != null && resolved.Count > 0
                            && !resolved.Contains(audible, StringComparer.OrdinalIgnoreCase);

            // ── Build body ─────────────────────────────────────────────────────
            var lines = new List<string>();

            // Playing: context (+ fallback marker) — Time elapsed / total — Codec.
            // Time and Codec only apply to a real custom track (cache has a Clip and a
            // playingFile); both are absent while riding a borrowed vanilla bed.
            string playLine = audible ?? "(none)";
            if (fellBack) playLine += "  (fallback)";
            float total = cache?.Current.Clip != null ? cache.Current.Clip.length : 0f;
            if (total > 0f)
            {
                float elapsed = GetPlaybackPosition(ActiveChannel, cache);
                playLine += $"  —  Time: {Clock(elapsed)} / {Clock(total)}";
            }
            if (!string.IsNullOrEmpty(playingFile))
            {
                string codec = CodecLabel(playingFile);
                float trim = LoudnessStore.GetUserTrim(playingFile);
                if (Mathf.Abs(trim - 1f) > 0.001f)
                    codec += $" (Volume: {trim * 100f:0}%)";
                playLine += $"  —  Codec: {codec}";
            }
            lines.Add(Row("Playing:", playLine));

            // Resolved: the Lua tokens; empties in red.
            string resolvedLine = (resolved != null && resolved.Count > 0)
                ? string.Join(", ", resolved.Select(ColorToken))
                : "(none)";
            lines.Add(Row("Resolved:", resolvedLine));

            // Fallback: only when we actually fell back, and only if there's a chain.
            if (fellBack)
            {
                var trace = BuildFallbackTrace(resolved, audible);
                if (trace.Count > 1)
                    lines.Add(Row("Fallback:", string.Join(" → ", trace.Select(ColorToken))));
            }

            // File / Path only make sense while a real custom track is audible.
            if (!string.IsNullOrEmpty(playingFile))
            {
                lines.Add(Row("File:", Path.GetFileName(playingFile)));
                lines.Add(Row("Path:", DisplayPath(audible, playingFile)));
            }

            lines.Add(Row("Source:", source));

            // Memory diagnostics on one line: the directory snapshots (every eligible
            // file path per context) plus the normalized (loudness-measured) files held
            // in memory.
            lines.Add(Row("Snapshots: ",
                $"{_hudSnapFiles} files  —  {FmtBytes(_hudSnapBytes)}"
                + $"  —  Normalized: {_hudNormCount} files  —  {FmtBytes(_hudNormBytes)}"));

            string body = string.Join("\n", lines);

            // ── Draw ───────────────────────────────────────────────────────────
            const float x = 10f, y = 10f, w = 540f;
            const float padX = 12f, headerH = 24f;
            var bodyContent = new GUIContent(body);
            float bodyH = _hudBodyStyle.CalcHeight(bodyContent, w - padX * 2f);
            float h = headerH + bodyH + 16f;

            // Dark translucent backdrop so the text stays legible over any scene.
            GUI.DrawTexture(new Rect(x, y, w, h), _hudBgTex, ScaleMode.StretchToFill);
            GUI.Label(new Rect(x + padX, y + 6f, w - padX * 2f, 22f), "CMM • Context  [Num8]", _hudHeaderStyle);
            GUI.Label(new Rect(x + padX, y + 6f + headerH, w - padX * 2f, bodyH), bodyContent, _hudBodyStyle);
        }

        // "label" + padding so the values start at a consistent column. The font is
        // proportional so alignment is approximate — fine for a diagnostic overlay.
        private static string Row(string label, string value) => $"{label,-10}{value}";

        // Sample the in-memory caches for the HUD's memory lines. Throttled by the
        // caller — the snapshot loop is O(total files), so it must not run per frame.
        private static void RecomputeHudStats()
        {
            _hudNormCount = LoudnessStore.MeasuredFileCount;
            _hudNormBytes = LoudnessStore.EstimateMemoryBytes();

            int files = 0;
            long bytes = 0;
            foreach (ContextCache c in cacheMap.Values)
            {
                string[] snap = c?.directorySnapshot;
                if (snap == null) continue;
                files += snap.Length;
                bytes += 32;                       // array object header + length field
                for (int i = 0; i < snap.Length; i++)
                {
                    string s = snap[i];
                    bytes += 8 + (s == null ? 0L : 26L + 2L * s.Length);  // ref slot + string
                }
            }
            _hudSnapFiles = files;
            _hudSnapBytes = bytes;
        }

        // Human-readable byte count for the diagnostic lines (B / KB / MB / GB).
        private static string FmtBytes(long b)
        {
            if (b < 1024L) return $"{b} B";
            double kb = b / 1024.0;
            if (kb < 1024.0) return $"{kb:0.#} KB";
            double mb = kb / 1024.0;
            if (mb < 1024.0) return $"{mb:0.#} MB";
            return $"{mb / 1024.0:0.##} GB";
        }

        // m:ss, clamped at zero (a mid-seek reader Position can briefly read negative).
        private static string Clock(float seconds)
        {
            if (seconds < 0f || float.IsNaN(seconds)) seconds = 0f;
            int t = (int)seconds;
            return $"{t / 60}:{t % 60:00}";
        }

        // A context token, wrapped in red rich-text when it currently has no files.
        private static string ColorToken(string ctx) =>
            ContextHasFiles(ctx) ? ctx : $"<color={HudRed}>{ctx}</color>";

        // The containing folder only (filename is shown on its own File line).
        // Relative to the Music dir for the common case; the full absolute folder when
        // the context was configured to point at an absolute folder.
        private static string DisplayPath(string contextName, string absoluteFile)
        {
            string absoluteDir = Path.GetDirectoryName(absoluteFile) ?? absoluteFile;

            Contexts ctx = GetContext(contextName);
            bool rooted = ctx != null && !string.IsNullOrEmpty(ctx.Path) && Path.IsPathRooted(ctx.Path);
            if (rooted) return absoluteDir;
            try
            {
                string full = Path.GetFullPath(absoluteDir);
                string baseDir = Path.GetFullPath(MusicDirectory);
                if (full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                    return full.Substring(baseDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { }
            return absoluteDir;
        }

        // Decoded format for the extension. Ogg is sniffed (Vorbis vs Opus), so the
        // result is cached per file — the probe opens the file and must not run per frame.
        private static string CodecLabel(string file)
        {
            if (string.Equals(file, _hudCodecForFile, StringComparison.OrdinalIgnoreCase))
                return _hudCodecLabel;

            string ext = Path.GetExtension(file).ToLowerInvariant();
            string label;
            switch (ext)
            {
                case ".opus": label = "Opus"; break;
                case ".ogg":
                case ".oga":  label = AudioDecoder.IsOpusFile(file) ? "Opus (Ogg)" : "Vorbis (Ogg)"; break;
                case ".flac": label = "FLAC"; break;
                case ".mp3":  label = "MP3"; break;
                case ".wav":
                case ".wave": label = "WAV"; break;
                case ".aiff":
                case ".aif":  label = "AIFF"; break;
                case ".m4a":
                case ".mp4":  label = "AAC (MP4)"; break;
                case ".m4v":
                case ".3gp":  label = "AAC (video)"; break;
                case ".webm": label = "WebM"; break;
                case ".mkv":  label = "Matroska"; break;
                case ".aac":  label = "AAC"; break;
                default:      label = string.IsNullOrEmpty(ext) ? "?" : ext.TrimStart('.').ToUpperInvariant(); break;
            }

            _hudCodecForFile = file;
            _hudCodecLabel = label;
            return label;
        }

        // Best-effort reconstruction of the fallback chain the resolver walked to reach
        // the audible context. Starts from the highest-priority resolved token (mirroring
        // the stack) and follows each context's Fallback; a "Stack"-style fallback jumps
        // to the next not-yet-visited resolved token. Diagnostic only — capped and
        // cycle-guarded, never authoritative (ResolvePlayableContext is the real logic).
        private static List<string> BuildFallbackTrace(IReadOnlyList<string> resolved, string playing)
        {
            var path = new List<string>();
            if (resolved == null || resolved.Count == 0) return path;

            var order = resolved.OrderByDescending(c => GetContext(c)?.Priority ?? 0).ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string cur = order[0];

            for (int i = 0; i < 8 && cur != null; i++)
            {
                if (!seen.Add(cur)) break;
                path.Add(cur);
                if (string.Equals(cur, playing, StringComparison.OrdinalIgnoreCase)) break;
                if (ContextHasFiles(cur)) break;

                string fb = GetContext(cur)?.Fallback ?? "Silence";
                if (fb == "Vanilla" || fb == "Silence" || fb == "None" || fb == cur) break;
                if (fb == "Stack" || fb == "VanillaIfPanicked" || fb == "VanillaIfDMS")
                    cur = order.FirstOrDefault(c => !seen.Contains(c));   // stack-walk approximation
                else
                    cur = fb;
            }
            return path;
        }

        private void EnsureHudStyles()
        {
            if (_hudBgTex == null)
            {
                _hudBgTex = new Texture2D(1, 1);
                _hudBgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.62f));
                _hudBgTex.Apply();
            }
            if (_hudBodyStyle == null)
            {
                _hudBodyStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                    alignment = TextAnchor.UpperLeft,
                    wordWrap = true,
                    richText = true
                };
                _hudBodyStyle.normal.textColor = Color.white;
            }
            if (_hudHeaderStyle == null)
            {
                _hudHeaderStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 15,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.UpperLeft
                };
                _hudHeaderStyle.normal.textColor = new Color(1f, 0.85f, 0.4f);
            }
        }
    }
}
#endif
