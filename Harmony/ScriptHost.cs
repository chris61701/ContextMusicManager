using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MoonSharp.Interpreter;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // SCRIPT HOST
    // ─────────────────────────────────────────────────────────────────────────
    // Hosts the Lua decision script via MoonSharp. ContextResolver.BuildSnapshot()
    // polls the game into a GameState; this turns that snapshot into the active
    // context tokens by calling the script's resolve(gameState, cfg). There is no
    // built-in C# decision path — the script IS the resolver.
    //
    // The script is loaded in two tiers, so a working baseline always exists:
    //   1. on-disk Config/context.lua  — the user-editable copy
    //   2. embedded default            — the same file baked into the DLL, used when
    //                                    the on-disk copy is missing or fails to compile
    //                                    (a user can't corrupt what's inside the assembly)
    //
    // Contract (API v1):
    //   resolve(gameState, cfg) -> table   array of context-token strings (order = intent;
    //                                  the host dedups). Non-table / nil return ⇒ no
    //                                  contexts this tick (not an error).
    //   reset()                       optional; clears the script's cross-tick state.
    //   API_VERSION                   number the script targets; must equal SupportedApi.
    //
    // Safety: the script runs in a HARD SANDBOX (no io/os/file/require) and every
    // call is guarded — a syntax error on (re)load keeps the previous good script,
    // and a runtime error returns the last good token set so a script bug can never
    // crash audio or the main thread.

    public static class ScriptHost
    {
        const int SupportedApi = 1;
        const string EmbeddedResourceName = "context.lua";   // see <EmbeddedResource> in the .csproj

        static Script  _script;        // current good interpreter
        static Closure _resolveFn;     // resolve(gameState, cfg)
        static Closure _resetFn;       // optional reset()

        static string   _scriptPath;
        static DateTime _lastAttemptStamp;   // write time of the last on-disk load ATTEMPT (success or
                                             // fail), so a broken file isn't re-parsed every reload check
        static string   _lastLoadError;      // de-dups repeated load-error logging
        static string   _lastRunError;       // de-dups repeated runtime-error logging
        static bool     _usingEmbedded;      // active script is the DLL-embedded default, not on-disk

        // Reused marshalling tables (resolve runs single-threaded on the scan tick),
        // and the last successful token set used as the runtime-error fallback.
        static Table _gameStateTable;
        static Table _cfgTable;
        static readonly List<string> _lastGood = new List<string>();

        // A script is essentially always loaded (on-disk override or embedded default);
        // Active is false only if even the embedded baseline fails to compile (e.g.
        // MoonSharp can't run), in which case ResolveContext returns no contexts.
        public static bool Active => _resolveFn != null;

        // Diagnostics (debug HUD): whether the embedded default is in use, and the
        // most recent resolved token set.
        public static bool UsingEmbeddedDefault => _usingEmbedded;
        public static IReadOnlyList<string> LastContexts => _lastGood;

        public static string ScriptPath(string modPath) =>
            Path.Combine(modPath, "Config", "context.lua");

        // ── Load / hot-reload ─────────────────────────────────────────────────

        public static void Load(string modPath)
        {
            _scriptPath = ScriptPath(modPath);
            TryLoad();
        }

        // Off-thread stamp probe: true when the on-disk script's write time differs from the
        // last load attempt — i.e. CheckReload would actually do work. Pure stat, no parse, so
        // it is safe to call from the scheduler thread to gate the main-thread CheckReload hop
        // (the TryLoad/compile itself must still run on the main thread). _lastAttemptStamp is a
        // plain DateTime written on the main thread; a torn read here only ever costs one extra
        // (idempotent) CheckReload or a one-tick-late reload, which is fine for a 2s poll.
        public static bool ScriptChangedOnDisk()
        {
            if (_scriptPath == null) return false;
            try { return File.Exists(_scriptPath) && File.GetLastWriteTimeUtc(_scriptPath) != _lastAttemptStamp; }
            catch { return false; }
        }

        // Cheap stamp check; reloads only when the on-disk file's write time changed
        // since the last attempt. Call from the existing config-watch loop.
        public static void CheckReload()
        {
            if (_scriptPath == null) return;
            try
            {
                if (!File.Exists(_scriptPath)) return;
                if (File.GetLastWriteTimeUtc(_scriptPath) != _lastAttemptStamp) TryLoad();
            }
            catch { /* transient IO during an external save — retry next check */ }
        }

        // Tier 1: on-disk Config/context.lua. Tier 2: the embedded default — used when
        // the on-disk copy is missing or fails to compile, so there is ALWAYS a working
        // baseline a user can't corrupt.
        static void TryLoad()
        {
            if (File.Exists(_scriptPath))
            {
                // Record the attempt stamp up front so a file that fails to load isn't
                // re-parsed on every CheckReload; a fresh edit (new stamp) retries.
                try { _lastAttemptStamp = File.GetLastWriteTimeUtc(_scriptPath); } catch { }

                string src = null;
                try { src = File.ReadAllText(_scriptPath); }
                catch (Exception ex) { LogLoadError($"context.lua read failed: {ex.Message} — using embedded default."); }

                if (src != null && TryCompile(src, "context.lua", "Config/context.lua"))
                {
                    _usingEmbedded = false;
                    return;
                }
                // Present but unreadable or broken → fall back to the embedded baseline.
                LoadEmbedded();
                return;
            }

            // No on-disk override → embedded baseline.
            LoadEmbedded();
        }

        static void LoadEmbedded()
        {
            string src = ReadEmbeddedDefault();
            if (src == null)
            {
                LogLoadError("embedded default context.lua is missing from the assembly — no resolver available!");
                return;
            }
            if (TryCompile(src, "context.lua", "embedded default context.lua"))
                _usingEmbedded = true;
        }

        static string ReadEmbeddedDefault()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName))
                {
                    if (s == null) return null;
                    using (var r = new StreamReader(s)) return r.ReadToEnd();
                }
            }
            catch { return null; }
        }

        // Compiles + validates a script source. On success swaps it in as the active
        // interpreter (with fresh reused marshalling tables) and returns true; on failure
        // logs (rate-limited) and returns false, leaving the previous good script in place.
        static bool TryCompile(string source, string codeName, string origin)
        {
            try
            {
                var script = NewSandbox();
                script.DoString(source, null, codeName);

                DynValue ver = script.Globals.Get("API_VERSION");
                if (ver.Type != DataType.Number || (int)ver.Number != SupportedApi)
                {
                    LogLoadError($"{origin} targets API {DescribeApi(ver)} but this build supports v{SupportedApi}.");
                    return false;
                }

                DynValue resolve = script.Globals.Get("resolve");
                if (resolve.Type != DataType.Function)
                {
                    LogLoadError($"{origin} defines no resolve(gameState, cfg) function.");
                    return false;
                }

                _script    = script;
                _resolveFn = resolve.Function;
                DynValue reset = script.Globals.Get("reset");
                _resetFn   = reset.Type == DataType.Function ? reset.Function : null;
                _gameStateTable = new Table(script);
                _cfgTable  = new Table(script);

                _lastLoadError = null;
                _lastRunError  = null;
                Log.Out($"[CMM] {origin} loaded (API v{SupportedApi}).");
                return true;
            }
            catch (SyntaxErrorException ex)
            {
                LogLoadError($"{origin} syntax error: {ex.DecoratedMessage}");
                return false;
            }
            catch (Exception ex)
            {
                LogLoadError($"{origin} load failed: {ex.Message}");
                return false;
            }
        }

        // ── Resolve ───────────────────────────────────────────────────────────

        // Runs resolve(gameState, cfg) and returns the deduped, order-preserving token list.
        // On any error returns the last good token set (empty before the first success),
        // so a faulty script degrades silently instead of throwing on the scan tick.
        public static List<string> Resolve(GameState gameState)
        {
            if (_resolveFn == null || gameState == null) return new List<string>();

            try
            {
                // Our marshalling (per-tick table builds — the per-enemy table alloc) and
                // MoonSharp's own call+walk are timed/allocated separately so the perf dump
                // shows which half of "resolve" generates the garbage.
                using (CMMPerf.Measure(CMMPerf.Phase.ScanMarshal))
                {
                    MarshalSnapshot(gameState);
                    MarshalConfig();
                }

                var tokens = new List<string>();
                using (CMMPerf.Measure(CMMPerf.Phase.ScanLuaCall))
                {
                    DynValue result = _script.Call(_resolveFn, _gameStateTable, _cfgTable);
                    if (result.Type == DataType.Table)
                    {
                        Table t = result.Table;
                        // Dense array 1..n; stop at the first hole, ignore non-strings, dedup.
                        for (int i = 1; ; i++)
                        {
                            DynValue v = t.Get(i);
                            if (v.Type == DataType.Nil) break;
                            if (v.Type == DataType.String && !tokens.Contains(v.String))
                                tokens.Add(v.String);
                        }
                    }
                }

                _lastRunError = null;
                _lastGood.Clear();
                _lastGood.AddRange(tokens);
                return tokens;
            }
            catch (ScriptRuntimeException ex)
            {
                LogRunError($"context.lua runtime error: {ex.DecoratedMessage}");
            }
            catch (Exception ex)
            {
                LogRunError($"context.lua error: {ex.Message}");
            }
            return new List<string>(_lastGood);
        }

        // Calls the script's reset() — the world-unload hook that clears its cross-tick state.
        public static void Reset()
        {
            if (_resetFn == null) return;
            try { _script.Call(_resetFn); }
            catch (Exception ex) { CMMConfig.D($"[CMM] context.lua reset() error: {ex.Message}"); }
        }

        // ── Sandbox + cmm helper library ──────────────────────────────────────

        static Script NewSandbox()
        {
            var script = new Script(CoreModules.Preset_HardSandbox);   // no io/os/file/require
            script.Options.DebugPrint = s => CMMConfig.D($"[lua] {s}");

            var cmm = new Table(script);
            cmm["has"]      = DynValue.NewCallback(Cmm_Has);
            cmm["count"]    = DynValue.NewCallback(Cmm_Count);
            cmm["hasFiles"] = DynValue.NewCallback(Cmm_HasFiles);
            script.Globals["cmm"] = cmm;

            return script;
        }

        // cmm.has(str, sub) -> bool : nil-safe, case-insensitive substring test.
        static DynValue Cmm_Has(ScriptExecutionContext ctx, CallbackArguments args)
        {
            string s   = args.Count > 0 && args[0].Type == DataType.String ? args[0].String : null;
            string sub = args.Count > 1 && args[1].Type == DataType.String ? args[1].String : null;
            bool hit = s != null && sub != null &&
                       s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;
            return DynValue.NewBoolean(hit);
        }

        // cmm.count(gameState, filter) -> number : counts gameState.enemies passing the filter
        // table { targetingPlayer = bool, alert = bool, within = number }.
        static DynValue Cmm_Count(ScriptExecutionContext ctx, CallbackArguments args)
        {
            Table gameStateT = args.Count > 0 && args[0].Type == DataType.Table ? args[0].Table : null;
            if (gameStateT == null) return DynValue.NewNumber(0);
            DynValue enemiesDv = gameStateT.Get("enemies");
            if (enemiesDv.Type != DataType.Table) return DynValue.NewNumber(0);

            Table filt = args.Count > 1 && args[1].Type == DataType.Table ? args[1].Table : null;
            bool needTargeting = filt != null && filt.Get("targetingPlayer").CastToBool();
            bool needAlert     = filt != null && filt.Get("alert").CastToBool();
            DynValue withinDv  = filt != null ? filt.Get("within") : DynValue.Nil;
            bool hasWithin     = withinDv.Type == DataType.Number;
            double within      = hasWithin ? withinDv.Number : 0;

            int n = 0;
            foreach (var pair in enemiesDv.Table.Pairs)
            {
                Table e = pair.Value.Table;
                if (e == null) continue;
                if (needTargeting && !e.Get("isTargetingPlayer").CastToBool()) continue;
                if (needAlert     && !e.Get("isAlert").CastToBool()) continue;
                if (hasWithin     && e.Get("distance").Number > within) continue;
                n++;
            }
            return DynValue.NewNumber(n);
        }

        // cmm.hasFiles(context) -> bool : does this context currently have playable files?
        static DynValue Cmm_HasFiles(ScriptExecutionContext ctx, CallbackArguments args)
        {
            string c = args.Count > 0 && args[0].Type == DataType.String ? args[0].String : null;
            return DynValue.NewBoolean(c != null && ContextMusicManager.ContextHasFiles(c));
        }

        // ── Marshalling: GameState / CMMConfig → reused Lua tables ──────────

        static void MarshalSnapshot(GameState gameState)
        {
            Table t = _gameStateTable;
            t["now"]         = gameState.Now;
            t["isDay"]       = gameState.IsDay;
            t["isBloodMoon"] = gameState.IsBloodMoon;
            t["healthPct"]   = gameState.HealthPct;
            t["isSwimming"]  = gameState.IsSwimming;
            t["isPanicked"]  = gameState.IsPanicked;
            t["vehicleType"] = gameState.VehicleType;   // null ⇒ Lua nil
            t["traderName"]  = gameState.TraderName;
            t["biome"]       = gameState.Biome;
            t["nearHome"]    = gameState.NearHome;
            t["alliesNearby"] = gameState.AlliesNearby;
            t["strangersNearby"] = gameState.StrangersNearby;
            t["inPoi"]       = gameState.InPoi;
            t["poiName"]     = gameState.PoiName;
            t["poiTier"]     = gameState.PoiTier;
            t["poiEnemiesRemaining"] = gameState.PoiEnemiesRemaining;
            t["poiQuestCleared"]     = gameState.PoiQuestCleared;
            t["poiClearFromQuestOnly"] = gameState.PoiClearFromQuestOnly;

            // Fresh enemies array each tick — the count varies, and the per-enemy
            // tables are tiny; building them is well within the sub-Hz scan budget.
            Table enemies = new Table(_script);
            int i = 1;
            foreach (var e in gameState.Enemies)
            {
                Table er = new Table(_script);
                er["className"]         = e.ClassName;   // null ⇒ Lua nil
                er["distance"]          = e.Distance;
                er["isAlert"]           = e.IsAlert;
                er["isTargetingPlayer"] = e.IsTargetingPlayer;
                enemies[i++] = er;
            }
            t["enemies"] = enemies;
        }

        static void MarshalConfig()
        {
            Table c = _cfgTable;
            c["enemyDetectRange"]   = CMMConfig.EnemyDetectRange;
            c["playerDetectRange"]  = CMMConfig.PlayerDetectRange;
            c["combatShareRange"]        = CMMConfig.CombatShareRange;
            c["combatShareRequireAlly"]  = CMMConfig.CombatShareRequireAlly;
            c["homeDetectRange"]    = CMMConfig.HomeDetectRange;
            c["healthDangerPct"]    = CMMConfig.PlayerHealthDangerThresholdPercent;
            c["combatExitDelay"]    = CMMConfig.CombatExitDelay;
            c["combatCooldown"]     = CMMConfig.CombatCooldown;
            c["combatLowThreshold"] = CMMConfig.CombatLowEnemyThreshold;
            c["combatHighThreshold"]= CMMConfig.CombatHighEnemyThreshold;
        }

        // ── Error logging (rate-limited) ──────────────────────────────────────

        static string DescribeApi(DynValue ver) =>
            ver.Type == DataType.Number ? "v" + (int)ver.Number : "an unspecified version";

        static void LogLoadError(string msg)
        {
            if (msg == _lastLoadError) return;
            _lastLoadError = msg;
            Log.Error($"[CMM] {msg}");
        }

        static void LogRunError(string msg)
        {
            if (msg == _lastRunError) return;   // suppress per-tick floods of the same error
            _lastRunError = msg;
            Log.Error($"[CMM] {msg}");
        }
    }
}
