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
    // A small top-left overlay describing what is playing and WHY. Cycled with
    // Numpad 8 (hidden → compact → expanded → hidden); hidden by default and purely
    // diagnostic — it reads engine state and never influences playback. Lines, top to
    // bottom (the last two appear only in the EXPANDED view):
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
    //   Heap      the whole managed heap now, its peak, and drift since the GC window armed
    //   GC        per-generation collections accrued over the window + gen0/min + scan churn
    //             — the enabled-vs-dormant A/B signal; ends with the current arm
    //             (ENABLED (A) / DORMANT (B)). Numpad 9 re-arms a fresh window; Numpad 7
    //             flips CMM's steady-state loops on/off for arm B and re-arms the window
    //   Snapshots directory snapshots (every eligible file path per context) plus, on
    //             the same line, the normalized (loudness-measured) files in memory
    //   Norm      (expanded) whether the audible track started pre-normalized (green) or
    //             had to be normalized on the fly (red)
    //   Contexts  (expanded) every OTHER custom-file context, colored by whether its next
    //             track is ready: green = staged & measured, amber = staged but measuring,
    //             red = nothing staged yet

    public partial class ContextMusicManager
    {
        // HUD display mode, cycled by Numpad 8: 0 = hidden, 1 = compact (the standard
        // overlay), 2 = expanded (compact + the loudness diagnostics — the playing track's
        // pre-normalized/on-the-fly status and every other context's next-track readiness).
        private static int _hudMode = 0;   // 0 hidden · 1 compact · 2 expanded

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
        // Readiness palette (expanded view): the per-context next-track grid and the
        // playing-track normalized indicator.
        private const string HudGreen = "#7dff7d";   // ready / pre-normalized
        private const string HudAmber = "#ffd27f";   // staged but its measurement is still running

        // Cached memory/count diagnostics. Recomputed on a throttled timer (not every
        // OnGUI frame): summing string lengths across the directory snapshots is O(files)
        // and would be ruinous per-frame on a large library. Only ever computed while the
        // HUD is open (DrawDebugHud returns early when hidden).
        private const float HudStatsInterval = 2f;
        private static float _hudStatsNextTime;
        private static int _hudNormCount, _hudSnapFiles;
        private static long _hudNormBytes, _hudSnapBytes;

        // Real-time output-ring health for the buffer tracker, polled on a fast throttle. The
        // native min-watermark resets on read, so polling on a fixed cadence makes the min the
        // lowest depth over each window (near 0 ⇒ a near/actual underrun). Process heap is
        // sampled here too so the GC line doesn't call GetTotalMemory every OnGUI frame.
        private const float HudBufInterval = 0.25f;
        private static float _hudBufNextTime;
        private static bool  _hudBufValid;
        private static float _hudBufQueuedMs, _hudBufTargetMs, _hudBufMinMs, _hudHeapMB;
        private static int   _hudBufVoices, _hudBufUnderruns;
        private static bool  _hudBufDeep;

        // Whole-heap GC gauge for the enabled-vs-dormant A/B. Sampled on the same fast
        // cadence as the buffer tracker. The baseline (window start time + per-generation
        // GC.CollectionCount + heap) is armed when the HUD opens and re-armable with Numpad
        // 9, so the line reads collections accrued over THIS window — the signal that
        // actually reflects GC pressure. Unlike the scan-churn number it captures every
        // allocation source (mixer, decoder, Unity, and CMM), so the collection rate is
        // directly comparable across A/B arms as long as the HUD is open in both (its own
        // per-frame allocation is then common-mode and cancels in the delta). Managed heap
        // only (GC.GetTotalMemory) — that IS the whole heap the collector walks.
        private static bool   _hudGcArmed;
        private static float  _hudGcBaseTime;
        private static float  _hudGcBaseHeapMB, _hudGcPeakHeapMB;
        private static readonly int[] _hudGcBaseGen = new int[3];
        private static readonly int[] _hudGcNowGen  = new int[3];

        // Cycles the HUD: hidden → compact → expanded → hidden. Driven from
        // HandleMusicKeybinds, so it inherits that method's gates for free — KeybindsEnabled,
        // the F1-console lockout (numpad digits typed as command args must not cycle the HUD),
        // and the Alt/Ctrl modifier requirement. Bound to KeyContextHud (Alt+Numpad8 default).
        internal static void CycleContextHud()
        {
            bool wasHidden = _hudMode == 0;
            _hudMode = (_hudMode + 1) % 3;
            if (wasHidden) ArmGcWindow();   // fresh GC window when it (re)opens from hidden
            CMMConfig.D($"[CMM] Context HUD {(_hudMode == 0 ? "hidden" : _hudMode == 1 ? "compact" : "expanded")}.");
        }

        // Re-arms the GC measurement window without hiding the HUD, so each arm of an
        // enabled-vs-dormant A/B starts from a clean, comparable baseline. No-op while the
        // HUD is hidden — nothing is on screen for it to affect, and silently resetting an
        // invisible gauge only confuses whoever opens it next.
        internal static void ResetContextHudWindow()
        {
            if (_hudMode == 0) return;
            ArmGcWindow();
            CMMConfig.D("[CMM] GC gauge window reset.");
        }

        // (Numpad 7 used to flip the functionally-dormant A/B arm; that key now
        // belongs to the Now Playing overlay — see KeyNowPlaying in Keybinds.cs.
        // _functionallyDormant itself remains, just with no key bound to it.)

        // (Re)start the GC measurement window: snapshot the current heap and per-generation
        // collection counts as the baseline. Boehm-mode Mono reports MaxGeneration 0 (only
        // gen0 is meaningful); higher gens then read as 0 and simply show +0.
        private static void ArmGcWindow()
        {
            _hudGcBaseTime   = Time.realtimeSinceStartup;
            _hudGcBaseHeapMB = GC.GetTotalMemory(false) / 1048576f;
            _hudGcPeakHeapMB = _hudGcBaseHeapMB;
            int maxGen = Math.Min(GC.MaxGeneration, 2);
            for (int g = 0; g <= 2; g++)
            {
                _hudGcBaseGen[g] = g <= maxGen ? GC.CollectionCount(g) : 0;
                _hudGcNowGen[g]  = _hudGcBaseGen[g];
            }
            _hudGcArmed = true;
        }

        // Drawn from the unified OnGUI() in ContextMusicManager.Keybinds.cs (a
        // MonoBehaviour may declare OnGUI() only once). Compiled only with the HUD.
        private void DrawDebugHud()
        {
            // Runs on the menu too (Menu music is a real context). playerIsDead is an
            // in-game-only state, so only consult it while actually in a game.
            if (_hudMode == 0) return;
            if (inGame && playerIsDead) return;

            EnsureHudStyles();

            // Refresh the memory/count diagnostics on a throttled cadence (see fields).
            if (Time.realtimeSinceStartup >= _hudStatsNextTime)
            {
                _hudStatsNextTime = Time.realtimeSinceStartup + HudStatsInterval;
                RecomputeHudStats();
            }

            // Poll the native mixer's ring health on a fast cadence for the buffer tracker.
            // Only when the mixer exists — the native stats call locks the mixer mutex that
            // cmm_mix_start creates, so calling before first playback would be unsafe.
            if (Time.realtimeSinceStartup >= _hudBufNextTime)
            {
                _hudBufNextTime = Time.realtimeSinceStartup + HudBufInterval;
                _hudHeapMB = GC.GetTotalMemory(false) / 1048576f;

                // Whole-heap GC gauge sample: update the peak and the per-generation
                // collection counts against the armed baseline. Arms itself if the window
                // was never started (e.g. HUD forced open without going through the toggle).
                if (!_hudGcArmed) ArmGcWindow();
                if (_hudHeapMB > _hudGcPeakHeapMB) _hudGcPeakHeapMB = _hudHeapMB;
                int maxGen = Math.Min(GC.MaxGeneration, 2);
                for (int g = 0; g <= 2; g++)
                    _hudGcNowGen[g] = g <= maxGen ? GC.CollectionCount(g) : _hudGcBaseGen[g];

                if (CmmMixer.Instance != null)
                {
                    CmmMixer.Instance.GetOutputStats(out int q, out int tgt, out int mn,
                        out int voices, out int deep, out int under, out int busRate);
                    float toMs = busRate > 0 ? 1000f / busRate : 0f;
                    _hudBufQueuedMs  = q   * toMs;
                    _hudBufTargetMs  = tgt * toMs;
                    _hudBufMinMs     = mn  * toMs;
                    _hudBufVoices    = voices;
                    _hudBufDeep      = deep != 0;
                    _hudBufUnderruns = under;
                    _hudBufValid     = true;
                }
                else _hudBufValid = false;
            }

            // ── Gather state ───────────────────────────────────────────────────
            string source = ScriptHost.Active
                ? (ScriptHost.UsingEmbeddedDefault ? "context.lua (embedded default)" : "context.lua")
                : $"<color={HudRed}>NONE — script failed to load</color>";

            IReadOnlyList<string> resolved = ScriptHost.LastContexts;

            // The context the listener actually hears: the native mixer's active context,
            // falling back to the resolver's logical pick before audio has settled.
            string audible = _nativeActiveContext;
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
            // Time/Codec apply to a real custom track; the position+length come straight
            // from the live native mixer voice. Absent while on vanilla/silence.
            string playLine = audible ?? "(none)";
            if (fellBack) playLine += "  (fallback)";
            float total = 0f, elapsed = 0f;
            if (!string.IsNullOrEmpty(_nativeActiveContext) && CmmMixer.Instance != null)
            {
                long vid = System.Threading.Interlocked.Read(ref _nativeVoiceId);
                if (vid != 0 && CmmMixer.Instance.TryGetState(vid, out double posSec, out double lenSec, out bool active)
                    && active && lenSec > 0)
                {
                    total = (float)lenSec;
                    elapsed = (float)posSec;
                }
            }
            if (total > 0f)
                playLine += $"  —  Time: {Clock(elapsed)} / {Clock(total)}";
            if (!string.IsNullOrEmpty(playingFile))
            {
                string codec = CodecLabel(playingFile);
                float trim = LoudnessStore.GetUserTrim(playingFile);
                if (Mathf.Abs(trim - 1f) > 0.001f)
                    codec += $" (Volume: {trim * 100f:0}%)";
                playLine += $"  —  Codec: {codec}";
            }
            lines.Add(Row("Playing:", playLine));

            // Buffer: live native output-ring depth / adaptive target, the min-watermark since
            // the last poll (red when near underrun), voice count (2 mid-crossfade), the cushion
            // mode, and the cumulative underrun counter. The payoff metric — it should hold near
            // target with underruns 0 even under heavy load.
            if (_hudBufValid)
            {
                string minStr = _hudBufMinMs < 20f
                    ? $"<color={HudRed}>{_hudBufMinMs:0} ms</color>" : $"{_hudBufMinMs:0} ms";
                string underStr = _hudBufUnderruns > 0
                    ? $"<color={HudRed}>{_hudBufUnderruns}</color>" : "0";
                string deepStr = _hudBufDeep ? "  •  <color=#ffd27f>DEEP</color>" : "";
                lines.Add(Row("Buffer:", $"{_hudBufQueuedMs:0}/{_hudBufTargetMs:0} ms  (min {minStr})" +
                                         $"  •  voices {_hudBufVoices}{deepStr}  •  underruns {underStr}"));
            }

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

            // Emitters: world speaker/radio detection registry (Phase 2, POI-scoped — no audio yet).
            // Self-throttled; the line reports how many emitter blocks are tracked, plus the nearest
            // and its distance, so you can walk a POI and watch them appear/update/drop.
#if GV_V3
            // EmitterCompat gate: the cross-version DLL carries this call onto A20/A21/2.6, where
            // Poll()'s voxel/prefab scan would bind 3.0-shaped game APIs.
            if (inGame && EmitterCompat.RuntimeSupported)
            {
                EmitterRegistry.Poll();
                lines.Add(Row("Emitters:", EmitterRegistry.HudLine()));
            }
#endif

            // Heap + GC gauge (the enabled-vs-dormant A/B signal). Heap: the whole managed
            // heap now, its peak since the window was armed, and the drift since arm. GC:
            // per-generation collections accrued over the window with the gen0 rate — read
            // gen0/min as the comparison metric. Trailing: CMM's own per-tick scan churn
            // (near-zero is the goal from the Lua-marshalling pooling). Num9 re-arms.
            float gcWin = Mathf.Max(0.001f, Time.realtimeSinceStartup - _hudGcBaseTime);
            int dg0 = _hudGcNowGen[0] - _hudGcBaseGen[0];
            int dg1 = _hudGcNowGen[1] - _hudGcBaseGen[1];
            int dg2 = _hudGcNowGen[2] - _hudGcBaseGen[2];
            float g0PerMin = dg0 * 60f / gcWin;
            lines.Add(Row("Heap:", $"{_hudHeapMB:0} MB  (peak {_hudGcPeakHeapMB:0}  •  " +
                                   $"Δ {_hudHeapMB - _hudGcBaseHeapMB:+0;-0;0} MB since arm)"));
            lines.Add(Row("GC:", $"gen0 +{dg0}  gen1 +{dg1}  gen2 +{dg2}  over {Clock(gcWin)}" +
                                 $"  •  {g0PerMin:0.0} g0/min  •  scan {_lastScanBytesPerTick:0} B/tick"));

            // Memory diagnostics on one line: the directory snapshots (every eligible
            // file path per context) plus the normalized (loudness-measured) files held
            // in memory.
            lines.Add(Row("Snapshots: ",
                $"{_hudSnapFiles} files  —  {FmtBytes(_hudSnapBytes)}"
                + $"  —  Normalized: {_hudNormCount} files  —  {FmtBytes(_hudNormBytes)}"));

            // ── Expanded-only loudness diagnostics ─────────────────────────────
            if (_hudMode == 2)
            {
                // Norm: did the AUDIBLE track start pre-normalized (green) or begin at unity
                // gain and get measured on the fly (red)? Captured at start time in
                // cache.playingFilePreMeasured, so it reflects how the track BEGAN — it does
                // not flip green once a late measurement settles in. Only meaningful while a
                // real custom track is audible.
                if (!string.IsNullOrEmpty(playingFile))
                {
                    string norm;
                    if (!CMMConfig.LoudnessNormalizationEnabled)
                        norm = "<color=#b0b0b0>● normalization off</color>";
                    else if (cache != null && cache.playingFilePreMeasured)
                        norm = $"<color={HudGreen}>● pre-normalized</color>";
                    else
                        norm = $"<color={HudRed}>● normalized on the fly</color>";
                    lines.Add(Row("Norm:", norm));
                }

                // Contexts: every OTHER custom-file context's next-track readiness — green =
                // a staged track is measured & ready for a stall-free switch-in, amber =
                // staged but its measurement is still running, red = nothing staged yet.
                lines.Add(Row("Contexts:", BuildContextReadiness(audible)));
            }

            string body = string.Join("\n", lines);

            // ── Draw ───────────────────────────────────────────────────────────
            // The overlay box (Now Playing readout + keybind notices) owns the very
            // top-left while it's up — step below it so it's never covered.
            const float x = 10f;
            float y = 10f + (OverlayOnScreen ? OverlayClearanceHeight : 0f);
            float w = _hudMode == 2 ? 720f : 540f;   // wider so the expanded context grid fits
            const float padX = 12f, headerH = 24f;
            var bodyContent = new GUIContent(body);
            float bodyH = _hudBodyStyle.CalcHeight(bodyContent, w - padX * 2f);
            float h = headerH + bodyH + 16f;

            // Dark translucent backdrop so the text stays legible over any scene.
            GUI.DrawTexture(new Rect(x, y, w, h), _hudBgTex, ScaleMode.StretchToFill);
            GUI.Label(new Rect(x + padX, y + 6f, w - padX * 2f, 22f),
                $"CMM • Context ({(_hudMode == 2 ? "expanded" : "compact")})  [Num8 cycle • Num9 reset GC]",
                _hudHeaderStyle);
            GUI.Label(new Rect(x + padX, y + 6f + headerH, w - padX * 2f, bodyH), bodyContent, _hudBodyStyle);
            _hudBottomY = y + h;   // the coverage panel stacks under this, not over it
        }

        // Bottom edge of the context HUD as last drawn, or 0 while it is hidden. Both panels
        // own the top-left, and Numpad8 and Numpad9 are independent now, so they have to be
        // able to share the corner instead of covering each other.
        internal static float _hudBottomY;

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

        // Expanded HUD: a colored token per OTHER custom-file context reflecting whether its
        // NEXT track is ready to play without a loudness stall. Green = a staged pin is
        // measured (or a resumable context is parked on a live resume it can return to),
        // amber = a track is staged but its measurement is still running, red = nothing
        // staged yet. Excludes the audible context (it's the Playing line) and file-less
        // contexts (Vanilla/Silence). O(contexts) with only dictionary lookups — no I/O.
        private static string BuildContextReadiness(string audible)
        {
            var toks = new List<string>();
            foreach (var kv in cacheMap)
            {
                string name = kv.Key;
                if (string.Equals(name, audible, StringComparison.OrdinalIgnoreCase)) continue;
                Contexts ctx = GetContext(name);
                if (ctx == null || string.IsNullOrEmpty(ctx.Source)) continue;   // no source → nothing to stage
                if (!ContextHasFiles(name)) continue;

                ContextCache c = kv.Value;
                string color;
                bool resumeParked = ctx.CanBeResumed && c.resumePosition > 0f
                                    && !string.IsNullOrEmpty(c.lastPlayedFile);
                if (resumeParked)
                    color = HudGreen;                              // returns to its kept (measured) track
                else if (string.IsNullOrEmpty(c.stagedFile))
                    color = HudRed;                                // nothing staged yet
                else if (!CMMConfig.LoudnessNormalizationEnabled || LoudnessStore.IsMeasured(c.stagedFile))
                    color = HudGreen;                              // staged + normalized (or normalization off)
                else
                    color = HudAmber;                              // staged, measurement still in flight
                toks.Add($"<color={color}>{name}</color>");
            }
            return toks.Count > 0 ? string.Join("   ", toks) : "(none)";
        }

        // The containing folder only (filename is shown on its own File line).
        // Relative to the Music dir for the common case; the full absolute folder when
        // the context was configured to point at an absolute folder.
        private static string DisplayPath(string contextName, string absoluteFile)
        {
            string absoluteDir = Path.GetDirectoryName(absoluteFile) ?? absoluteFile;

            Contexts ctx = GetContext(contextName);
            // A playlist's tracks can live anywhere (its own folder is meaningless here),
            // and an absolute-folder context shows its absolute dir — both display the
            // track's own folder as-is; only a relative folder context relativizes below.
            bool rooted = ctx != null && !string.IsNullOrEmpty(ctx.Source) &&
                          (ctx.IsPlaylist || Path.IsPathRooted(ctx.Source));
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
