using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // A model of CMM's music layer, in simulated time.
    //
    // Linked and REAL:  TrackCooldown (every cooldown rule), ResumePolicy, ContextCache /
    //                   Contexts, TrackRotation (the pick).
    // MIRRORED here:    the Unity-side wiring, method for method, named as in the source:
    //                   SendContextUpdate, TriggerMusic, StartContextCoroutine,
    //                   StartNativeContext (+ RestGate), ArmNativeAutoAdvance,
    //                   PreStageNextAdvance, NativeAdvanceTick, ReconcilePreStagedAdvance,
    //                   OnAwaitedTrackEnd, CooldownTick, the ContextScanLoop pass (eviction
    //                   + convergence), ApplyFileWatchScan's reactions, SkipTrack,
    //                   RecoverFromFailedStart, CaptureNativeResume, reverse-fade reclaim.
    // MODELLED:         the mixer (voices with a playhead, fades, EOF), the start worker
    //                   (one step of latency), the scheduler (0.25 s) and scan (0.1 s)
    //                   cadences, the output cushion (0.2 s), the file watcher (1 s).
    //
    // Simplifications, chosen because none of them touches a cooldown path: no warmup
    // dwell, no vanilla DMS (fallback chains end at Stack/Silence), no loudness hold, no
    // bans/favourites, one music voice per context.
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class Voice
    {
        public long Id;
        public string Ctx, File;
        public double TrueLen;        // when it actually ends
        public bool KnownLen;         // false = mixer reports length 0 (raw ADTS etc.)
        public double Pos, Gain, Target, Rate;
    }

    internal sealed class SimMixer
    {
        public readonly List<Voice> Voices = new List<Voice>();
        long _next = 1;
        public const double CushionSeconds = 0.2;   // the tight in-game output ring

        public long Add(string ctx, string file, double trueLen, bool knownLen, double start, double fade)
        {
            foreach (var v in Voices) FadeOut(v, fade);           // cmm_mix_add crossfades the others out
            var nv = new Voice
            {
                Id = _next++, Ctx = ctx, File = file, TrueLen = trueLen, KnownLen = knownLen,
                Pos = start, Gain = fade <= 0 ? 1 : 0, Target = 1, Rate = fade <= 0 ? 1e9 : 1 / fade,
            };
            Voices.Add(nv);
            return nv.Id;
        }

        static void FadeOut(Voice v, double fade) { v.Target = 0; v.Rate = fade <= 0 ? 1e9 : 1 / fade; }
        public void Stop(double fade) { foreach (var v in Voices) FadeOut(v, fade); }
        public void StopVoice(long id, double fade) { var v = Find(id); if (v != null) FadeOut(v, fade); }

        // Decode error / device reset: the voice is gone mid-track.
        public bool Kill(long id) => Voices.RemoveAll(v => v.Id == id) > 0;

        public Voice Find(long id) => Voices.FirstOrDefault(v => v.Id == id);

        public bool TryGetState(long id, out double pos, out double len, out bool active)
        {
            var v = Find(id);
            pos = v?.Pos ?? 0; len = v != null && v.KnownLen ? v.TrueLen : 0; active = v != null;
            return v != null;
        }

        public bool TryReclaim(long id, double fade)
        {
            var keep = Find(id);
            if (keep == null) return false;
            foreach (var v in Voices) { if (v == keep) { v.Target = 1; v.Rate = 1 / Math.Max(fade, 1e-6); } else FadeOut(v, fade); }
            return true;
        }

        // Returns voices that reached EOF this step (natural ends).
        public List<Voice> Tick(double dt)
        {
            var ended = new List<Voice>();
            foreach (var v in Voices)
            {
                v.Pos += dt;                                     // plays on under a pause (ducked, not stopped)
                if (v.Gain < v.Target) v.Gain = Math.Min(v.Target, v.Gain + v.Rate * dt);
                else if (v.Gain > v.Target) v.Gain = Math.Max(v.Target, v.Gain - v.Rate * dt);
            }
            foreach (var v in Voices) if (v.Pos >= v.TrueLen) ended.Add(v);
            Voices.RemoveAll(v => v.Pos >= v.TrueLen || (v.Target == 0 && v.Gain <= 0));
            return ended;
        }
    }

    // A music library on disk. Track files are named  <id>__<seconds>s.ogg  (or
    // <id>__u<seconds>s.ogg for a track whose length the mixer cannot report) so a file's
    // length travels with it through delete / re-add without a side table.
    internal sealed class Library : IDisposable
    {
        public readonly string Root;
        public Library()
        {
            Root = Path.Combine(Path.GetTempPath(), "CmmCooldownSim_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Root);
        }
        public int Version;
        public string Folder(string source) { string d = Path.Combine(Root, source); Directory.CreateDirectory(d); Version++; return d; }
        public string Add(string source, string id, double seconds, bool knownLen = true)
        {
            string p = Path.Combine(Folder(source), $"{id}__{(knownLen ? "" : "u")}{seconds.ToString(CultureInfo.InvariantCulture)}s.ogg");
            File.WriteAllText(p, "");
            Version++;
            return p;
        }
        public void Remove(string path) { if (File.Exists(path)) File.Delete(path); Version++; }
        public void Empty(string source) { foreach (var f in List(source)) File.Delete(f); Version++; }
        public string[] List(string source)
        {
            string d = Path.Combine(Root, source);
            return Directory.Exists(d) ? Directory.GetFiles(d, "*.ogg").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray() : new string[0];
        }
        public static void Meta(string file, out double len, out bool known)
        {
            string n = Path.GetFileNameWithoutExtension(file);
            string tail = n.Substring(n.LastIndexOf("__", StringComparison.Ordinal) + 2).TrimEnd('s');
            known = !tail.StartsWith("u");
            len = double.Parse(known ? tail : tail.Substring(1), CultureInfo.InvariantCulture);
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    internal sealed class Ev
    {
        public double T; public string Kind, Ctx, File, Note;
        public override string ToString() => $"{T,8:F2}  {Kind,-12} {Ctx,-22} {(File != null ? Path.GetFileName(File) : "")} {Note}";
    }

    internal sealed class World : IDisposable
    {
        // ── Knobs a scenario sets ──────────────────────────────────────────────
        public float DefaultTrackCooldown = 180f;          // CMMConfig.DefaultTrackCooldown
        public bool TrackCooldownsEnabled = true;          // CMMConfig.TrackCooldownsEnabled
        public const float TrackTransitionFadeDuration = 5f;
        public const float CrossFadeDuration = 2f;
        public const float ManualSkipFadeDuration = 0.25f;
        public const double TrackEndPollInterval = 0.25;
        public readonly HashSet<string> Active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string PoiId = "None";
        public bool RadioOn;
        public bool Trace;

        // ── Real, linked state ─────────────────────────────────────────────────
        public readonly Dictionary<string, Contexts> contexts = new Dictionary<string, Contexts>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ContextCache> cacheMap = new Dictionary<string, ContextCache>(StringComparer.OrdinalIgnoreCase);
        public readonly TrackCooldown Tc;

        // ── Mirrored wiring state (names as in the source) ─────────────────────
        public double Now;
        public readonly SimMixer Mixer = new SimMixer();
        public readonly Library Lib = new Library();
        public readonly List<Ev> Log = new List<Ev>();
        public readonly List<ActiveContext> _contextStack = new List<ActiveContext>();
        public Contexts currentContext;
        public string _pendingContext;
        public int _transitionId;
        public string _nativeActiveContext;
        public long _nativeVoiceId;
        bool _nativeAdvanceArmed, _advancePending, _advanceAwaitsEnd;
        double _advLastRemaining = double.NaN;
        public bool _pauseDucked;
        public bool playerIsDead;
        readonly HashSet<string> _invalidFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> _recentNativeVoice = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, double> _reentryUntil = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        readonly Queue<Action> _mainQueue = new Queue<Action>();        // MainThreadDispatcher.Post
        readonly Queue<StartReq> _natQueue = new Queue<StartReq>();     // the start worker
        sealed class StartReq { public string Context, File; public int TransitionId; public double Start, Fade; public bool Loop; }
        sealed class PreStaged { public StartReq Req; public string Context, NextFile; public int TransitionId; }
        PreStaged _preStaged;
        readonly Random _rng;
        double _nextSched, _nextScan, _nextWatch;
        readonly HashSet<string> _prevActive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int _failedStartTid = int.MinValue, _failedStartCount;

        public int GateViolations;          // a start of a resting context that did not override
        public int Strands;                 // seconds-long silences nobody meant
        public int DoubleVoices;            // two non-fading music voices at once
        double _silentSince = -1;

        public World(int seed = 1)
        {
            _rng = new Random(seed);
            Tc = new TrackCooldown(() => Now, EffectiveTrackCooldown, n => GetContext(n)?.CooldownReset ?? false);
            InitContexts();
            currentContext = GetContext("Silence");
        }

        public void Dispose() => Lib.Dispose();

        // ── The context table (a slice of InitContexts, same shapes) ──────────
        void Add(string name, string source, string fallback = "Stack", int priority = 0,
                 float cooldown = 0f, bool reset = true, bool once = false, bool loop = false,
                 int stale = 120, int reentry = 5, float fadein = 5f, bool bypass = false, bool yield = false)
        {
            contexts[name] = new Contexts(name, source, fallback, true, stale, loop, reentry, fadein, bypass,
                                          priority, once, -1f, 0, null, false, null, cooldown, reset, yield);
            cacheMap[name] = new ContextCache();
        }

        void InitContexts()
        {
            Add("Combat",               "combat",               priority: 50, stale: 20, reentry: 3, fadein: 1.2f, bypass: true);
            Add("POI_Cleared",          "events/poi_cleared",   priority: 24, once: true, fadein: 0f, bypass: true);
            // Home / Ambient ship cooldownreset=false; Home also yields (2026-11-07).
            Add("Home_Night",           "home/night", "Home",   priority: 19, cooldown: -1, reset: false, yield: true);
            Add("Home",                 "home",                 priority: 19, cooldown: -1, reset: false, yield: true);
            Add("POI_T3",               "poi/t3",  "POI",       priority: 18, cooldown: -1);
            Add("POI",                  "poi",                  priority: 16, cooldown: -1);
            Add("Ambient_Forest_Night", "ambient/forest/_night", "Ambient_Night", priority: 12, cooldown: -1, reset: false);
            Add("Ambient_Forest",       "ambient/forest", "Ambient", priority: 11, cooldown: -1, reset: false);
            Add("Ambient_Night",        "ambient/_night",       priority: 11, cooldown: -1, reset: false);
            Add("Ambient",              "ambient",              priority: 10, cooldown: -1, reset: false);
            Add("Looping",              "looping",              priority: 13, cooldown: -1, loop: true);
            Add("Silence",              "",  "Silence",         priority: 0, reentry: 0, fadein: 0, bypass: true);
        }

        public void SetCooldown(string name, float cooldown, bool? reset = null, bool? yield = null)
        {
            var c = contexts[name];
            contexts[name] = new Contexts(c.Context, c.Source, c.Fallback, c.CanBeResumed, c.TimeUntilContextStale,
                c.Loop, c.Reentry, c.FadeInDuration, c.BypassesReentry, c.Priority, c.PlayOnce, c.VolumeOverride,
                c.WarmUp, c.WarmGroup, c.IncludeSubfolders, c.Link, cooldown, reset ?? c.CooldownReset,
                yield ?? c.CooldownYield);
        }

        public Contexts GetContext(string n) => n != null && contexts.TryGetValue(n, out var c) ? c : null;
        public string Src(string ctx) => GetContext(ctx).Source;

        void Note(string kind, string ctx, string file = null, string note = null)
        {
            var e = new Ev { T = Now, Kind = kind, Ctx = ctx, File = file, Note = note };
            Log.Add(e);
            if (Trace) Console.WriteLine("        " + e);
        }

        // ── ContextMusicManager.Cooldown.cs ───────────────────────────────────
        public float EffectiveTrackCooldown(string name)
        {
            if (!TrackCooldownsEnabled) return 0f;
            var c = GetContext(name);
            if (c == null || c.Loop || c.PlayOnce || string.IsNullOrEmpty(c.Source)) return 0f;
            return TrackCooldown.Effective(c.TrackCooldown, DefaultTrackCooldown);
        }
        static double NaturalEndSlack => Math.Max(1.0, 3.0 * TrackEndPollInterval);

        void ArmAdvanceCooldown(string ctx, bool should)
        {
            _advLastRemaining = double.NaN;
            _advanceAwaitsEnd = should && EffectiveTrackCooldown(ctx) > 0f;
        }

        bool PollAwaitedEnd(bool got, bool active, double pos, double len, out bool natural)
        {
            natural = false;
            if (got && active) { _advLastRemaining = len > 0 ? Math.Max(0, len - pos) : -1; return false; }
            natural = TrackCooldown.IsNaturalEnd(_advLastRemaining, NaturalEndSlack);
            return true;
        }

        void OnAwaitedTrackEnd(string ctx, int tid, bool natural)
        {
            _advancePending = false;
            if (tid != _transitionId || !string.Equals(_nativeActiveContext, ctx, StringComparison.OrdinalIgnoreCase)) return;
            Note("end", ctx, cacheMap[ctx].playingFile, natural ? "natural" : "EARLY");
            // Cooldown.cs RestPoiId: only POI music ties its rest to a POI.
            string restPoi = ctx.StartsWith("POI", StringComparison.OrdinalIgnoreCase) ? PoiId : null;
            if (Tc.TrackEnded(ctx, natural, restPoi, SimMixer.CushionSeconds))
            {
                _restBeganAt[ctx] = Now;
                var cache = cacheMap[ctx];
                cache.resumePosition = 0; cache.lastLeftTime = -1; cache.playingFile = null;
                Note("rest", ctx, null, $"{Tc.Remaining(ctx):F1}s");
                HoldSilentForRest(ctx, tid, 0f);
                return;
            }
            int newId = ++_transitionId;
            StartNativeContext(ctx, true, newId, null);
        }

        void HoldSilentForRest(string ctx, int tid, float fade)
        {
            _nativeAdvanceArmed = false;
            _advanceAwaitsEnd = false;
            if (_nativeActiveContext != null) StopNativeIfActive(fade);
            Tc.BeginWait(ctx, tid);
        }

        bool RestGate(string ctx, int tid, bool overrideRest)
        {
            if (overrideRest) { Tc.Override(ctx); return false; }
            if (!Tc.IsResting(ctx)) return false;
            Note("held", ctx, null, $"{Tc.Remaining(ctx):F1}s left");
            HoldSilentForRest(ctx, tid, CrossFadeDuration);
            return true;
        }

        public bool IsHeldForRest(string ctx) => Tc.IsWaitingOn(ctx, _transitionId);
        bool AwaitingTrackEnd => _nativeActiveContext != null && _nativeAdvanceArmed && EffectiveTrackCooldown(_nativeActiveContext) > 0f;

        void CooldownTick()
        {
            if (_nativeAdvanceArmed && _nativeActiveContext != null)
                _advanceAwaitsEnd = EffectiveTrackCooldown(_nativeActiveContext) > 0f;
            var cleared = Tc.PoiTick(PoiId);
            if (cleared != null) foreach (var c in cleared) Note("poi-reset", c);
            if (_pauseDucked || RadioOn) return;
            string due = Tc.DueToStart(currentContext?.Context, _transitionId);
            if (due == null) return;
            Tc.EndWait();
            var ctx = GetContext(due);
            if (ctx == null || !ContextHasFiles(due)) return;
            Note("rest-over", due);
            int id = ++_transitionId;
            StartNativeContext(due, true, id, null, ctx.FadeInDuration > 0 ? ctx.FadeInDuration : CrossFadeDuration);
        }

        void NoteContextEvicted(string ctx)
        {
            var cleared = Tc.Evicted(ctx, ResolveOwnChain(ctx));
            foreach (var c in cleared) Note("leave-reset", c, null, "via " + ctx);
        }

        string ResolveOwnChain(string ctx)
        {
            string cur = ctx;
            for (int i = 0; i < 5 && cur != null; i++)
            {
                if (ContextHasFiles(cur)) return cur;
                string fb = GetContext(cur)?.Fallback;
                if (string.IsNullOrEmpty(fb) || fb == cur || GetContext(fb) == null || fb == "Silence" || fb == "Vanilla") return null;
                cur = fb;
            }
            return null;
        }

        // ── Files (presence + snapshot, as the crawler/file watch keep them) ───
        public bool ContextHasFiles(string ctx)
        {
            var c = GetContext(ctx);
            if (c == null || string.IsNullOrEmpty(c.Source)) return false;
            var snap = cacheMap[ctx].directorySnapshot;
            return snap != null && snap.Length > 0;
        }

        // ── ContextStack.cs resolver (no DMS: VanillaIf* behave as Stack) ──────
        readonly HashSet<string> _resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string ResolvePlayableContext(string name)
        {
            if (_resolving.Contains(name)) return null;
            _resolving.Add(name);
            try
            {
                string cur = name;
                for (int i = 0; i < 5; i++)
                {
                    if (_restYieldExcluded.Count > 0 && _restYieldExcluded.Contains(cur)) return null;
                    if (ContextHasFiles(cur)) return RestYieldTarget(cur) ?? cur;
                    string fb = GetContext(cur)?.Fallback ?? "Silence";
                    if (fb == "Stack") return ResolveViaStack();
                    if (fb == "Silence" || fb == cur) return null;
                    cur = fb;
                }
                return null;
            }
            finally { _resolving.Remove(name); }
        }
        string ResolveViaStack()
        {
            foreach (var a in _contextStack) { string r = ResolvePlayableContext(a.ContextName); if (r != null) return r; }
            return null;
        }
        string GetTopContext() => ResolveViaStack();

        // ── Cooldown.cs RestYieldTarget (cooldownyield) ───────────────────────
        readonly HashSet<string> _restYieldExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, double> _restBeganAt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string RestYieldTarget(string ctx)
        {
            if (_restYieldExcluded.Count > 0) return null;
            var c = GetContext(ctx);
            if (c == null || !c.CooldownYield || !Tc.IsResting(ctx)) return null;
            string playing = _nativeActiveContext;
            if (string.Equals(playing, ctx, StringComparison.OrdinalIgnoreCase)) return null;
            string cur = ctx;
            for (int i = 0; i < 5 && cur != null; i++)
            {
                _restYieldExcluded.Add(cur);
                string fb = GetContext(cur)?.Fallback;
                if (string.IsNullOrEmpty(fb) || GetContext(fb) == null || fb == "Silence" || fb == "Vanilla") break;
                cur = fb;
            }
            string beneath;
            try { beneath = ResolveViaStack(); }
            finally { _restYieldExcluded.Clear(); }
            if (beneath == null || beneath == "Vanilla") return null;
            if (string.Equals(beneath, playing, StringComparison.OrdinalIgnoreCase) && NativeOutputBusy()) return beneath;
            if (!_restBeganAt.TryGetValue(ctx, out double began)) return null;
            var b = GetContext(beneath);
            if (b == null || !b.CanBeResumed || !cacheMap.TryGetValue(beneath, out var bc)) return null;
            if (bc.resumePosition <= 0f || bc.lastLeftTime < began || string.IsNullOrEmpty(bc.lastPlayedFile)) return null;
            return Now - bc.lastLeftTime < b.TimeUntilContextStale ? beneath : null;
        }

        // ── SendContextUpdate / TriggerMusic (ContextMusicManager.cs) ─────────
        public void SendContextUpdate(string contextIn, int bypassPriority = -1)
        {
            var incoming = GetContext(contextIn);
            if (incoming == null) return;
            if (_pendingContext == contextIn) return;
            int comparePriority = bypassPriority >= 0 ? bypassPriority : (currentContext?.Priority ?? 0);
            bool isReloadOfCurrent = currentContext != null && contextIn == currentContext.Context;
            bool priorityOk = incoming.Priority > comparePriority;
            if (!priorityOk && !isReloadOfCurrent) return;
            _pendingContext = contextIn;
            if (currentContext != null) CaptureNativeResume(currentContext);

            if (incoming.PlayOnce && !ContextHasFiles(contextIn))
            {
                _pendingContext = "Silence"; currentContext = GetContext("Silence"); ++_transitionId; StopNativeIfActive(CrossFadeDuration);
                return;
            }
            string toPlay = ResolvePlayableContext(contextIn);
            if (toPlay == null)
            {
                _pendingContext = "Silence"; currentContext = GetContext("Silence"); ++_transitionId; StopNativeIfActive(CrossFadeDuration);
                Note("silence", contextIn);
                return;
            }
            var candidate = GetContext(toPlay);
            bool bypass = incoming.BypassesReentry || candidate.BypassesReentry;
            if (!bypass && bypassPriority < 0 && _reentryUntil.TryGetValue(toPlay, out double until) && Now < until)
            {
                _pendingContext = currentContext?.Context ?? "Silence";
                return;
            }
            var leaving = currentContext;
            if (leaving != null && leaving.Reentry > 0) _reentryUntil[leaving.Context] = Now + leaving.Reentry;
            currentContext = candidate;
            TriggerMusic(toPlay);
        }

        void TriggerMusic(string ctx)
        {
            int newId = ++_transitionId;
            StartContextCoroutine(ctx, false, newId);
        }

        void StartContextCoroutine(string ctx, bool same, int id)
        {
            if (RadioOn && !GetContext(ctx).PlayOnce) { ReleaseNativeVoiceForRadio(); return; }
            if (ContextHasFiles(ctx)) StartNativeContext(ctx, same, id, null);
            else StopNativeIfActive(CrossFadeDuration);
        }

        void ReleaseNativeVoiceForRadio()
        {
            if (_nativeActiveContext == null) return;
            Mixer.StopVoice(_nativeVoiceId, CrossFadeDuration);
            _nativeActiveContext = null; _nativeVoiceId = 0;
        }

        void StopNativeIfActive(double fade)
        {
            if (_nativeActiveContext == null) return;
            _nativeActiveContext = null; _nativeVoiceId = 0;
            Mixer.Stop(fade);
        }

        void CaptureNativeResume(Contexts leaving)
        {
            if (leaving == null || !leaving.CanBeResumed) return;
            if (!string.Equals(_nativeActiveContext, leaving.Context, StringComparison.OrdinalIgnoreCase)) return;
            if (Mixer.TryGetState(_nativeVoiceId, out double pos, out _, out bool active) && active)
            {
                var lc = cacheMap[leaving.Context];
                lc.resumePosition = (float)pos; lc.lastLeftTime = (float)Now;
            }
        }

        // ── StartNativeContext (AudioPlayer.cs), in source order ──────────────
        public void StartNativeContext(string contextName, bool same, int transitionId, string forcedFile,
                                       double fadeOverride = -1, bool overrideRest = false)
        {
            if (transitionId != _transitionId) return;
            var ctx = GetContext(contextName);
            if (ctx == null) return;
            var cache = cacheMap[contextName];

            if (RadioOn && !ctx.PlayOnce) { ReleaseNativeVoiceForRadio(); return; }

            if (RestGate(contextName, transitionId, overrideRest)) return;

            if (_nativeActiveContext != null && !string.Equals(_nativeActiveContext, contextName, StringComparison.OrdinalIgnoreCase)
                && _nativeVoiceId != 0)
                _recentNativeVoice[_nativeActiveContext] = _nativeVoiceId;

            // C: reverse-fade reclaim
            if (!same && forcedFile == null && !ctx.PlayOnce
                && _recentNativeVoice.TryGetValue(contextName, out long reclaimId)
                && Mixer.TryReclaim(reclaimId, ctx.FadeInDuration > 0 ? ctx.FadeInDuration : CrossFadeDuration))
            {
                _recentNativeVoice.Remove(contextName);
                _nativeActiveContext = contextName;
                _nativeVoiceId = reclaimId;
                cache.lastLeftTime = -1;
                Note("reclaim", contextName, cache.lastPlayedFile);
                ArmNativeAutoAdvance(contextName, transitionId, !ctx.Loop && !ctx.PlayOnce, cache.lastPlayedFile);
                return;
            }

            var rd = ResumePolicy.Decide(ctx.CanBeResumed, cache.resumePosition, cache.lastPlayedFile, cache.lastLeftTime,
                                         (float)Now, ctx.TimeUntilContextStale, same, forcedFile != null,
                                         f => !_invalidFiles.Contains(f) && File.Exists(f));
            double startSeconds = rd.StartSeconds;
            if (rd.ClearPosition) cache.resumePosition = 0;

            string file = forcedFile ?? rd.ResumeFile;
            if (file == null)
                file = same ? PreferPreStagedNext(contextName, cache) : PickNextFile(contextName, null);
            cache.stagedFile = null;
            if (file == null)
            {
                StopNativeIfActive(CrossFadeDuration);
                if (currentContext?.Context == contextName) { _pendingContext = null; currentContext = GetContext("Silence"); }
                Note("no-file", contextName);
                return;
            }

            double fade = ctx.PlayOnce ? ctx.FadeInDuration
                        : same ? Math.Max(0.25, TrackTransitionFadeDuration)
                        : (ctx.FadeInDuration > 0 ? ctx.FadeInDuration : CrossFadeDuration);
            if (fadeOverride >= 0) fade = fadeOverride;

            cache.CommitToHistory(file);
            cache.playingFile = file; cache.lastPlayedFile = file;
            cache.resumePosition = 0; cache.lastLeftTime = -1;
            Tc.TrackStarted(contextName);
            _nativeActiveContext = contextName;
            _nativeVoiceId = 0;

            _natQueue.Enqueue(new StartReq { Context = contextName, File = file, TransitionId = transitionId,
                                             Start = startSeconds, Fade = fade, Loop = ctx.Loop });
            Note(startSeconds > 0 ? "start(resume)" : "start", contextName, file, $"fade {fade:F2}");
            ArmNativeAutoAdvance(contextName, transitionId, !ctx.Loop && !ctx.PlayOnce, file);
        }

        // The start worker: opens and plays on its own thread a moment later.
        void RunWorker()
        {
            while (_natQueue.Count > 0)
            {
                var req = _natQueue.Dequeue();
                if (req.TransitionId != _transitionId
                    || !string.Equals(_nativeActiveContext, req.Context, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(req.File))
                {
                    Note("open-FAIL", req.Context, req.File);
                    string c = req.Context, f = req.File; int t = req.TransitionId;
                    _mainQueue.Enqueue(() => RecoverFromFailedStart(c, f, t));
                    continue;
                }
                Library.Meta(req.File, out double len, out bool known);
                // THE invariant: audio never starts for a context that is resting. Every path
                // to the mixer comes through here (fresh, advance, pre-staged, skip, recovery).
                if (Tc.IsResting(req.Context)) { GateViolations++; Note("GATE-BYPASS", req.Context, req.File); }
                _nativeVoiceId = Mixer.Add(req.Context, req.File, req.Loop ? 1e12 : len, known, req.Start, req.Fade);
            }
        }

        void RecoverFromFailedStart(string ctx, string file, int tid)
        {
            if (!string.IsNullOrEmpty(file)) _invalidFiles.Add(file);
            if (tid != _transitionId) return;
            if (!string.Equals(_nativeActiveContext, ctx, StringComparison.OrdinalIgnoreCase)) return;
            if (tid != _failedStartTid) { _failedStartTid = tid; _failedStartCount = 0; }
            if (++_failedStartCount > 8) return;
            _advancePending = false;
            StartNativeContext(ctx, true, tid, null);
        }

        string PreferPreStagedNext(string ctx, ContextCache cache)
        {
            string fwd = cache.HistoryForward();
            if (fwd != null && !_invalidFiles.Contains(fwd)) return fwd;
            var ps = _preStaged;
            if (ps != null && string.Equals(ps.Context, ctx, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(ps.NextFile) && !_invalidFiles.Contains(ps.NextFile)
                && !string.Equals(ps.NextFile, cache.lastPlayedFile, StringComparison.OrdinalIgnoreCase)
                && File.Exists(ps.NextFile))
                return ps.NextFile;
            return PickNextFile(ctx, cache.lastPlayedFile);
        }

        string PickNextFile(string ctx, string avoid)
        {
            var cache = cacheMap[ctx];
            var files = cache.directorySnapshot;
            if (files == null) return null;
            files = files.Where(f => !_invalidFiles.Contains(f)).ToArray();
            if (files.Length == 0) return null;
            return cache.rotation.Select(files, TrackRotation.WholePool, avoid, null, null, 1f, () => _rng.NextDouble());
        }

        void ArmNativeAutoAdvance(string ctx, int tid, bool should, string currentFile)
        {
            ArmAdvanceCooldown(ctx, should);
            _advancePending = false;
            _nativeAdvanceArmed = should;
            if (should) PreStageNextAdvance(ctx, tid, currentFile); else _preStaged = null;
        }

        void PreStageNextAdvance(string ctx, int tid, string currentFile)
        {
            var c = GetContext(ctx);
            if (c == null || c.Loop || c.PlayOnce) { _preStaged = null; return; }
            var cache = cacheMap[ctx];
            string next = cache.HistoryForward();
            if (next == null || _invalidFiles.Contains(next)) next = PickNextFile(ctx, currentFile);
            if (next == null) { _preStaged = null; return; }
            _preStaged = new PreStaged
            {
                Context = ctx, TransitionId = tid, NextFile = next,
                Req = new StartReq { Context = ctx, File = next, TransitionId = tid, Start = 0, Fade = Math.Max(0.25, TrackTransitionFadeDuration) },
            };
        }

        // ── NativeAdvanceTick (scheduler thread) ──────────────────────────────
        void NativeAdvanceTick()
        {
            if (!_nativeAdvanceArmed || _advancePending) return;
            if (_pauseDucked && !_advanceAwaitsEnd) return;
            string ctx = _nativeActiveContext;
            if (ctx == null) return;
            long vid = _nativeVoiceId;
            if (vid == 0) return;
            int tid = _transitionId;

            if (_advanceAwaitsEnd)
            {
                bool got = Mixer.TryGetState(vid, out double p0, out double l0, out bool a0);
                if (!PollAwaitedEnd(got, a0, p0, l0, out bool natural)) return;
                if (_pauseDucked) return;
                _advancePending = true;
                _mainQueue.Enqueue(() => OnAwaitedTrackEnd(ctx, tid, natural));
                return;
            }

            bool advance;
            if (!Mixer.TryGetState(vid, out double pos, out double len, out bool active) || !active) advance = true;
            else if (len > 0)
            {
                double lead = Math.Max(TrackTransitionFadeDuration, 0.5);
                advance = len - pos <= Math.Min(lead, len * 0.5);
            }
            else advance = false;
            if (!advance) return;

            var ps = _preStaged;
            if (ps != null && ps.TransitionId == tid && string.Equals(ps.Context, ctx, StringComparison.OrdinalIgnoreCase))
            {
                _preStaged = null;
                _advancePending = true;
                _nativeVoiceId = 0;
                _natQueue.Enqueue(ps.Req);
                Note("crossfade", ctx, ps.NextFile);
                _mainQueue.Enqueue(() => ReconcilePreStagedAdvance(ps));
                return;
            }
            _advancePending = true;
            _mainQueue.Enqueue(() =>
            {
                if (tid == _transitionId && string.Equals(_nativeActiveContext, ctx, StringComparison.OrdinalIgnoreCase))
                {
                    int newId = ++_transitionId;
                    StartNativeContext(ctx, true, newId, null);
                }
                _advancePending = false;
            });
        }

        void ReconcilePreStagedAdvance(PreStaged ps)
        {
            if (ps.TransitionId == _transitionId && string.Equals(_nativeActiveContext, ps.Context, StringComparison.OrdinalIgnoreCase))
            {
                var cache = cacheMap[ps.Context];
                cache.CommitToHistory(ps.NextFile);
                cache.playingFile = ps.NextFile; cache.lastPlayedFile = ps.NextFile;
                cache.resumePosition = 0; cache.lastLeftTime = -1; cache.stagedFile = null;
                ArmNativeAutoAdvance(ps.Context, ps.TransitionId, true, ps.NextFile);
            }
            else _advancePending = false;
        }

        // ── ContextScanLoop pass ──────────────────────────────────────────────
        bool NativeOutputBusy()
        {
            if (_nativeActiveContext == null) return false;
            if (_nativeVoiceId == 0) return true;
            return Mixer.Find(_nativeVoiceId) != null;
        }

        public bool CurrentAudioDead() =>
            currentContext != null && currentContext.Context != "Silence"
            && !RadioOn && !IsHeldForRest(currentContext.Context)
            && !AwaitingTrackEnd
            && !_advancePending && !NativeOutputBusy();

        void ScanPass()
        {
            if (playerIsDead) return;
            CooldownTick();

            var activeList = Active.Where(n => GetContext(n) != null).ToList();
            bool namesChanged = !_prevActive.SetEquals(activeList);
            bool dead = CurrentAudioDead();
            bool stuck = currentContext == null || currentContext.Context == "Silence";
            bool pendingEvict = _contextStack.Any(a => !Active.Contains(a.ContextName));
            bool livePlayOnce = currentContext != null && currentContext.PlayOnce && !dead;
            // Playability changes (a folder emptied/refilled) are caught by re-resolving each pass.
            if (!namesChanged && !dead && !stuck && !pendingEvict && !livePlayOnce && !PlayabilityChanged(activeList)) return;
            _prevActive.Clear(); foreach (var n in activeList) _prevActive.Add(n);

            foreach (var n in activeList)
                if (_contextStack.All(c => c.ContextName != n) && ResolvePlayableContext(n) != null)
                    _contextStack.Add(new ActiveContext { ContextName = n, Priority = GetContext(n).Priority, ActivatedAt = (float)Now, FirstSeenAt = (float)Now });
            foreach (var a in _contextStack) if (Active.Contains(a.ContextName)) a.ActivatedAt = (float)Now;
            foreach (var ev in _contextStack.ToList())
                if (!Active.Contains(ev.ContextName) && (Now - ev.ActivatedAt) >= 1.5) NoteContextEvicted(ev.ContextName);
            _contextStack.RemoveAll(c => !Active.Contains(c.ContextName) && (Now - c.ActivatedAt) >= 1.5);
            _contextStack.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            string top = GetTopContext();
            if (top == null)
            {
                if (currentContext != null && currentContext.PlayOnce && !dead) return;
                if (currentContext?.Context != "Silence")
                {
                    _pendingContext = null; currentContext = GetContext("Silence"); ++_transitionId; StopNativeIfActive(CrossFadeDuration);
                    Note("silence", "(nothing playable)");
                }
            }
            else if (currentContext == null || top != currentContext.Context)
            {
                if (currentContext != null && currentContext.PlayOnce && !dead)
                {
                    var t = GetContext(top);
                    if (t == null || t.Priority <= currentContext.Priority) return;
                }
                _pendingContext = null;
                SendContextUpdate(top, 0);
            }
            else if (dead)
            {
                Note("converge", top);
                _pendingContext = null;
                SendContextUpdate(top, 0);
            }
        }

        readonly Dictionary<string, string> _prevPlayable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool PlayabilityChanged(List<string> active)
        {
            bool changed = false;
            var now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in active) { var r = ResolvePlayableContext(n); if (r != null) now[n] = r; }
            if (now.Count != _prevPlayable.Count) changed = true;
            else foreach (var kv in now) if (!_prevPlayable.TryGetValue(kv.Key, out var p) || p != kv.Value) { changed = true; break; }
            _prevPlayable.Clear(); foreach (var kv in now) _prevPlayable[kv.Key] = kv.Value;
            return changed;
        }

        // ── File watch (ApplyFileWatchScan's reactions) ───────────────────────
        int _seenLibVersion = -1;
        void FileWatchPass()
        {
            if (Lib.Version == _seenLibVersion) return;   // nothing on disk changed (only the sim writes it)
            _seenLibVersion = Lib.Version;
            foreach (var kv in contexts)
            {
                string name = kv.Key; var ctx = kv.Value;
                if (string.IsNullOrEmpty(ctx.Source)) continue;
                var cache = cacheMap[name];
                var now = Lib.List(ctx.Source);
                var prev = cache.directorySnapshot ?? new string[0];
                bool changed = !now.SequenceEqual(prev, StringComparer.OrdinalIgnoreCase);
                if (!changed) continue;
                foreach (var f in now) _invalidFiles.Remove(f);     // re-validated on re-appearance
                cache.directorySnapshot = now;
                if (currentContext == null || name != currentContext.Context) continue;   // scan loop picks it up

                bool lastPlayedMissing = false;
                if (!string.IsNullOrEmpty(cache.lastPlayedFile) && !now.Contains(cache.lastPlayedFile, StringComparer.OrdinalIgnoreCase))
                {
                    cache.lastPlayedFile = null; lastPlayedMissing = true;
                }
                if (now.Length == 0)
                {
                    Note("fw-emptied", name);
                    _pendingContext = null;
                    StopNativeIfActive(CrossFadeDuration);
                    if (ctx.PlayOnce) { ++_transitionId; currentContext = GetContext("Silence"); }
                    else SendContextUpdate(GetTopContext() ?? "Ambient", 0);
                }
                else if (lastPlayedMissing)
                {
                    Note("fw-hotswap", name);
                    _pendingContext = null;
                    StopNativeIfActive(0.25);
                    currentContext = GetContext(name);
                    TriggerMusic(name);
                }
                else
                {
                    string top = GetTopContext();
                    if (top != null && top != currentContext.Context) { _pendingContext = null; SendContextUpdate(top, 0); }
                }
            }
        }

        // ── Player actions ────────────────────────────────────────────────────
        public void SkipTrack(bool previous)
        {
            if (RadioOn) return;
            string ctxName = _nativeActiveContext ?? currentContext?.Context;
            var ctx = GetContext(ctxName);
            if (ctx == null || string.IsNullOrEmpty(ctx.Source) || ctx.PlayOnce || !ContextHasFiles(ctxName)) return;
            var cache = cacheMap[ctxName];
            string forced = null;
            if (previous) { forced = cache.HistoryBack(); if (forced == null) return; }
            int natId = ++_transitionId;
            Note(previous ? "PREV" : "NEXT", ctxName);
            StartNativeContext(ctxName, true, natId, forced, ManualSkipFadeDuration, overrideRest: true);
        }

        public void SetPaused(bool p)
        {
            if (p == _pauseDucked) return;
            _pauseDucked = p;
            Tc.SetPaused(p);
            Note(p ? "PAUSE" : "UNPAUSE", "");
        }

        public void Die()
        {
            playerIsDead = true;
            Tc.ClearAll();
            _pendingContext = null; currentContext = GetContext("Silence"); ++_transitionId; StopNativeIfActive(1);
            Note("DIED", "");
        }
        public void Respawn() { playerIsDead = false; Note("RESPAWN", ""); }

        public void SetRadio(bool on)
        {
            if (on == RadioOn) return;
            RadioOn = on;
            Note(on ? "RADIO-ON" : "RADIO-OFF", "");
            if (on) { if (_nativeActiveContext != null) { CaptureNativeResume(currentContext); ReleaseNativeVoiceForRadio(); } }
            else
            {
                string top = GetTopContext();
                if (top != null && top != currentContext?.Context) { _pendingContext = null; SendContextUpdate(top, 0); }
            }
        }

        public void KillCurrentVoice() { Note("KILL", _nativeActiveContext); Mixer.Kill(_nativeVoiceId); }

        // Source repointed by a contexts.xml hot-reload (ConfigWatch): the rest is for music
        // it no longer plays.
        public void Repoint(string ctx) { Tc.Clear(ctx); Note("repoint", ctx); }

        // ── The clock ─────────────────────────────────────────────────────────
        public void Run(double seconds)
        {
            const double dt = 0.05;
            double end = Now + seconds;
            while (Now < end - 1e-9)
            {
                Now += dt;
                RunWorker();
                foreach (var v in Mixer.Tick(dt)) Note("eof", v.Ctx, v.File);   // the advance poll notices later
                if (Now >= _nextSched) { _nextSched = Now + TrackEndPollInterval; NativeAdvanceTick(); }
                while (_mainQueue.Count > 0) _mainQueue.Dequeue()();
                if (Now >= _nextWatch) { _nextWatch = Now + 1.0; FileWatchPass(); }
                if (Now >= _nextScan) { _nextScan = Now + 0.1; ScanPass(); }
                RunWorker();
                CheckInvariants();
            }
        }

        void CheckInvariants()
        {
            int live = Mixer.Voices.Count(v => v.Target > 0);
            if (live > 1) DoubleVoices++;

            // Unintended silence: a context with music is current, nothing holds it silent,
            // and nothing is audible. Allow the transition machinery a few seconds.
            bool shouldPlay = !playerIsDead && !RadioOn && !_pauseDucked
                              && currentContext != null && currentContext.Context != "Silence"
                              && ContextHasFiles(currentContext.Context)
                              && !Tc.IsResting(currentContext.Context);
            bool audible = Mixer.Voices.Any(v => v.Target > 0);
            if (shouldPlay && !audible)
            {
                if (_silentSince < 0) _silentSince = Now;
                else if (Now - _silentSince > 3.0) { Strands++; _silentSince = Now; Note("STRAND", currentContext.Context, null, StateLine()); }
            }
            else _silentSince = -1;
        }

        public string StateLine() =>
            $"nativeCtx={_nativeActiveContext ?? "null"} vid={_nativeVoiceId} armed={_nativeAdvanceArmed} awaits={_advanceAwaitsEnd} " +
            $"pending={_advancePending} pendingCtx={_pendingContext ?? "null"} waiting={Tc.Waiting ?? "null"} " +
            $"dead={CurrentAudioDead()} top={GetTopContext() ?? "null"} stack=[{string.Join(",", _contextStack.Select(a => a.ContextName))}] " +
            $"active=[{string.Join(",", Active)}] radio={RadioOn} paused={_pauseDucked} deadPlayer={playerIsDead} voices={Mixer.Voices.Count}";

        // ── Readouts for assertions ───────────────────────────────────────────
        public Voice Playing => Mixer.Voices.Where(v => v.Target > 0).OrderByDescending(v => v.Id).FirstOrDefault();
        public string PlayingCtx => Playing?.Ctx;
        public IEnumerable<Ev> Since(double t, string kind = null, string ctx = null) =>
            Log.Where(e => e.T >= t - 1e-9 && (kind == null || e.Kind == kind) && (ctx == null || e.Ctx == ctx));
        public Ev FirstSince(double t, string kind, string ctx = null) => Since(t, kind, ctx).FirstOrDefault();
    }
}
