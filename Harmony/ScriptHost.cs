using System;
using System.Collections.Generic;
using System.IO;
using MoonSharp.Interpreter;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // SCRIPT HOST
    // ─────────────────────────────────────────────────────────────────────────
    // Hosts the external Lua decision script (Config/context.lua) via MoonSharp.
    // BuildSnapshot() polls the game into a GameState; this turns that snapshot
    // into the active context tokens by calling the script's resolve(gameState, cfg).
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

        static Script  _script;        // current good interpreter (null until first good load)
        static Closure _resolveFn;     // resolve(gameState, cfg); null ⇒ host disabled, use built-in path
        static Closure _resetFn;       // optional reset()

        static string   _scriptPath;
        static DateTime _lastAttemptStamp;   // file write time of the last load ATTEMPT (success or fail),
                                             // so a broken file isn't re-parsed every reload check
        static string   _lastLoadError;      // de-dups repeated load-error logging
        static string   _lastRunError;       // de-dups repeated runtime-error logging

        // Reused marshalling tables (resolve runs single-threaded on the scan tick),
        // and the last successful token set used as the runtime-error fallback.
        static Table _gameStateTable;
        static Table _cfgTable;
        static readonly List<string> _lastGood = new List<string>();

        // True when a good script is loaded and the built-in C# resolver should defer
        // to it. False ⇒ no script (missing/broken on first load) ⇒ caller keeps the
        // built-in behavior.
        public static bool Active => _resolveFn != null;

        public static string ScriptPath(string modPath) =>
            Path.Combine(modPath, "Config", "context.lua");

        // ── Load / hot-reload ─────────────────────────────────────────────────

        public static void Load(string modPath)
        {
            _scriptPath = ScriptPath(modPath);
            TryLoad();
        }

        // Cheap stamp check; reloads only when the file's write time changed since the
        // last attempt. Call from the existing config-watch loop.
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

        static void TryLoad()
        {
            if (!File.Exists(_scriptPath))
            {
                // Only a problem once — log a single notice and stay on the built-in path.
                LogLoadError($"context.lua not found at {_scriptPath} — using built-in resolver.");
                return;
            }

            // Record the attempt stamp up front so a file that fails to load isn't
            // re-parsed on every CheckReload; a fresh edit (new stamp) retries.
            try { _lastAttemptStamp = File.GetLastWriteTimeUtc(_scriptPath); } catch { }

            try
            {
                var script = NewSandbox();
                script.DoString(File.ReadAllText(_scriptPath), null, "context.lua");

                DynValue ver = script.Globals.Get("API_VERSION");
                if (ver.Type != DataType.Number || (int)ver.Number != SupportedApi)
                {
                    LogLoadError($"context.lua targets API {DescribeApi(ver)} but this build supports v{SupportedApi} — keeping previous script.");
                    return;
                }

                DynValue resolve = script.Globals.Get("resolve");
                if (resolve.Type != DataType.Function)
                {
                    LogLoadError("context.lua defines no resolve(gameState, cfg) function — keeping previous script.");
                    return;
                }

                // Commit: swap in the new interpreter and its reused marshalling tables.
                _script    = script;
                _resolveFn = resolve.Function;
                DynValue reset = script.Globals.Get("reset");
                _resetFn   = reset.Type == DataType.Function ? reset.Function : null;
                _gameStateTable = new Table(script);
                _cfgTable  = new Table(script);

                _lastLoadError = null;
                _lastRunError  = null;
                Log.Out($"[CMM] context.lua loaded (API v{SupportedApi}).");
            }
            catch (SyntaxErrorException ex)
            {
                LogLoadError($"context.lua syntax error: {ex.DecoratedMessage} — keeping previous script.");
            }
            catch (Exception ex)
            {
                LogLoadError($"context.lua load failed: {ex.Message} — keeping previous script.");
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
                MarshalSnapshot(gameState);
                MarshalConfig();
                DynValue result = _script.Call(_resolveFn, _gameStateTable, _cfgTable);

                var tokens = new List<string>();
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

        // Calls the script's reset() (world-unload hook). Mirrors ContextResolver.resetValues().
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
            t["inPoi"]       = gameState.InPoi;
            t["poiName"]     = gameState.PoiName;
            t["poiTier"]     = gameState.PoiTier;
            t["poiEnemiesRemaining"] = gameState.PoiEnemiesRemaining;

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
