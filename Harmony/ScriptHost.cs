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

        // Reused marshalling state (resolve runs single-threaded on the scan tick), so a tick
        // re-sets fields instead of allocating fresh Lua tables:
        //   _enemiesTable  — the enemies array, set into _gameStateTable ONCE (identity persists).
        //   _enemyTablePool — per-enemy tables, each wrapped in a DynValue once and reused.
        //   _prevEnemyCount — how many enemy slots were populated last tick (to nil the extras).
        //   _cfgDirty      — cfg is marshalled only when settings change (MarkConfigDirty).
        //   _tokens        — the reused resolve result (the caller copies it within the tick).
        //   _lastGood      — the last successful token set, the runtime-error fallback.
        static Table _gameStateTable;
        static Table _cfgTable;
        static Table _enemiesTable;
        static readonly List<DynValue> _enemyTablePool = new List<DynValue>();
        static int  _prevEnemyCount;
        static bool _cfgDirty = true;
        static readonly List<string> _lastGood = new List<string>();
        static readonly List<string> _tokens   = new List<string>();

        // Per-field marshal cache. MarshalSnapshot writes a Lua DynValue for a scalar field
        // only when its value changed since last tick — each write boxes the primitive and
        // allocates a DynValue, and almost every snapshot field is stable tick-to-tick (biome,
        // isDay, poi*, ...), so this cuts the steady-state per-tick marshal churn from ~20
        // allocations to ~1 (just `now`, which always advances). The reused _gameStateTable
        // keeps the previous tick's DynValue for any skipped field. _snapshotAllDirty forces a
        // full re-marshal after a (re)compile, when _gameStateTable is a fresh, empty table.
        static bool   _snapshotAllDirty = true;
        static bool   _pIsDay, _pIsBloodMoon, _pIsSwimming, _pIsPanicked, _pNearHome,
                      _pInPoi, _pPoiQuestCleared, _pPoiClearFromQuestOnly;
        static float  _pHealthPct;
        static int    _pAlliesNearby, _pStrangersNearby, _pPoiTier, _pPoiEnemiesRemaining;
        static string _pVehicleType, _pTraderName, _pBiome, _pPoiName, _pPoiId;

        // A script is essentially always loaded (on-disk override or embedded default);
        // Active is false only if even the embedded baseline fails to compile (e.g.
        // MoonSharp can't run), in which case ResolveContext returns no contexts.
        public static bool Active => _resolveFn != null;

        // Diagnostics (debug HUD): whether the embedded default is in use, and the
        // most recent resolved token set.
        public static bool UsingEmbeddedDefault => _usingEmbedded;
        public static IReadOnlyList<string> LastContexts => _lastGood;

        // The marshalled cfg table is static between settings reloads, so it's built into Lua
        // only when it changes. CMMConfigLoader's reload path calls this; the next Resolve
        // re-marshals cfg once, then reverts to the cheap snapshot-only marshal.
        public static void MarkConfigDirty() => _cfgDirty = true;

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
                _cfgTable       = new Table(script);
                _enemiesTable   = new Table(script);
                _gameStateTable["enemies"] = _enemiesTable;   // identity persists; contents refilled each tick
                _enemyTablePool.Clear();                      // pooled tables were bound to the previous script
                _prevEnemyCount = 0;
                _cfgDirty = true;                             // re-marshal cfg against the new script
                _snapshotAllDirty = true;                     // fresh _gameStateTable → write every field once

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
                    if (_cfgDirty) { MarshalConfig(); _cfgDirty = false; }   // cfg is static between reloads
                }

                _tokens.Clear();
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
                            if (v.Type == DataType.String && !_tokens.Contains(v.String))
                                _tokens.Add(v.String);
                        }
                    }
                }

                _lastRunError = null;
                _lastGood.Clear();
                _lastGood.AddRange(_tokens);
                return _tokens;
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

        // Lua nil for a null string, else a string DynValue (avoids the object-indexer's
        // boxing + FromObject round-trip). Setting a key to nil reads back as nil in Lua,
        // exactly as the old `t[key] = null` did.
        static DynValue Str(string s) => s == null ? DynValue.Nil : DynValue.NewString(s);

        static void MarshalSnapshot(GameState gameState)
        {
            Table t = _gameStateTable;
            bool all = _snapshotAllDirty;

            // `now` advances every tick, so it is always (re)written; every other field is
            // written only when it changed (see _snapshotAllDirty / the _p* cache above).
            t.Set("now", DynValue.NewNumber(gameState.Now));

            if (all || gameState.IsDay != _pIsDay)
                { t.Set("isDay", DynValue.NewBoolean(gameState.IsDay)); _pIsDay = gameState.IsDay; }
            if (all || gameState.IsBloodMoon != _pIsBloodMoon)
                { t.Set("isBloodMoon", DynValue.NewBoolean(gameState.IsBloodMoon)); _pIsBloodMoon = gameState.IsBloodMoon; }
            if (all || gameState.HealthPct != _pHealthPct)
                { t.Set("healthPct", DynValue.NewNumber(gameState.HealthPct)); _pHealthPct = gameState.HealthPct; }
            if (all || gameState.IsSwimming != _pIsSwimming)
                { t.Set("isSwimming", DynValue.NewBoolean(gameState.IsSwimming)); _pIsSwimming = gameState.IsSwimming; }
            if (all || gameState.IsPanicked != _pIsPanicked)
                { t.Set("isPanicked", DynValue.NewBoolean(gameState.IsPanicked)); _pIsPanicked = gameState.IsPanicked; }
            if (all || !string.Equals(gameState.VehicleType, _pVehicleType, StringComparison.Ordinal))
                { t.Set("vehicleType", Str(gameState.VehicleType)); _pVehicleType = gameState.VehicleType; }
            if (all || !string.Equals(gameState.TraderName, _pTraderName, StringComparison.Ordinal))
                { t.Set("traderName", Str(gameState.TraderName)); _pTraderName = gameState.TraderName; }
            if (all || !string.Equals(gameState.Biome, _pBiome, StringComparison.Ordinal))
                { t.Set("biome", Str(gameState.Biome)); _pBiome = gameState.Biome; }
            if (all || gameState.NearHome != _pNearHome)
                { t.Set("nearHome", DynValue.NewBoolean(gameState.NearHome)); _pNearHome = gameState.NearHome; }
            if (all || gameState.AlliesNearby != _pAlliesNearby)
                { t.Set("alliesNearby", DynValue.NewNumber(gameState.AlliesNearby)); _pAlliesNearby = gameState.AlliesNearby; }
            if (all || gameState.StrangersNearby != _pStrangersNearby)
                { t.Set("strangersNearby", DynValue.NewNumber(gameState.StrangersNearby)); _pStrangersNearby = gameState.StrangersNearby; }
            if (all || gameState.InPoi != _pInPoi)
                { t.Set("inPoi", DynValue.NewBoolean(gameState.InPoi)); _pInPoi = gameState.InPoi; }
            if (all || !string.Equals(gameState.PoiName, _pPoiName, StringComparison.Ordinal))
                { t.Set("poiName", Str(gameState.PoiName)); _pPoiName = gameState.PoiName; }
            if (all || !string.Equals(gameState.PoiId, _pPoiId, StringComparison.Ordinal))
                { t.Set("poiId", Str(gameState.PoiId)); _pPoiId = gameState.PoiId; }
            if (all || gameState.PoiTier != _pPoiTier)
                { t.Set("poiTier", DynValue.NewNumber(gameState.PoiTier)); _pPoiTier = gameState.PoiTier; }
            if (all || gameState.PoiEnemiesRemaining != _pPoiEnemiesRemaining)
                { t.Set("poiEnemiesRemaining", DynValue.NewNumber(gameState.PoiEnemiesRemaining)); _pPoiEnemiesRemaining = gameState.PoiEnemiesRemaining; }
            if (all || gameState.PoiQuestCleared != _pPoiQuestCleared)
                { t.Set("poiQuestCleared", DynValue.NewBoolean(gameState.PoiQuestCleared)); _pPoiQuestCleared = gameState.PoiQuestCleared; }
            if (all || gameState.PoiClearFromQuestOnly != _pPoiClearFromQuestOnly)
                { t.Set("poiClearFromQuestOnly", DynValue.NewBoolean(gameState.PoiClearFromQuestOnly)); _pPoiClearFromQuestOnly = gameState.PoiClearFromQuestOnly; }

            _snapshotAllDirty = false;

            // Enemies: reuse the array table (set into t once at compile time) + a pool of
            // per-enemy tables, each wrapped in a DynValue ONCE. A tick only overwrites the four
            // scalar fields — no per-tick Table or wrapper allocation. Trailing slots left by a
            // previous, larger tick are niled so a stale enemy can't linger in the array.
            int idx = 0;
            foreach (var e in gameState.Enemies)
            {
                DynValue slot;
                if (idx < _enemyTablePool.Count) slot = _enemyTablePool[idx];
                else { slot = DynValue.NewTable(new Table(_script)); _enemyTablePool.Add(slot); }

                Table er = slot.Table;
                er["className"]         = e.ClassName;   // null ⇒ Lua nil
                er["distance"]          = e.Distance;
                er["isAlert"]           = e.IsAlert;
                er["isTargetingPlayer"] = e.IsTargetingPlayer;
                _enemiesTable.Set(idx + 1, slot);        // 1-based Lua index; reuses the key node
                idx++;
            }
            for (int k = idx; k < _prevEnemyCount; k++)
                _enemiesTable.Set(k + 1, DynValue.Nil);  // clear last tick's extra slots
            _prevEnemyCount = idx;
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
