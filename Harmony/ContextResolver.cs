using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using MusicUtils.Enums;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // CONTEXT RESOLVER
    // ─────────────────────────────────────────────────────────────────────────
    // BuildSnapshot() polls the game into a version-agnostic GameState. Every game-API member
    // that differs across A20/A21/2.6/3.0 is bound by name at load (the reflection bridges above
    // BuildSnapshot), so one 3.0-compiled binary runs on all four with no #if GV_* forks. The
    // decision logic — turning that snapshot into active context tokens — lives in the Lua script
    // (see ScriptHost and Config/context.lua), so ResolveContext() is just "poll, then ask the script".

    public static class ContextResolver
    {
        // Reused snapshot + buffers. ResolveContext runs once per scan tick on the
        // main thread (never re-entrant), so a single shared GameState, the entity
        // scratch list, and a pool of EnemyInfo objects let the whole per-tick poll
        // run without allocating after warmup.
        static readonly GameState _gameState = new GameState();
        static readonly List<Entity> _entityScratch = new List<Entity>();
        static readonly List<EnemyInfo> _enemyPool = new List<EnemyInfo>();
        // Reused entity-list snapshot for the pre-3.0 trader fallback (see FindTraderForArea).
        // Separate from _entityScratch: the trader scan runs before the enemy scan refills that.
        static readonly List<Entity> _traderScanScratch = new List<Entity>();
        // Shared empty result for the "no world / no player yet" scan tick — the callers only
        // ever read the returned list, so a single immutable-by-contract instance avoids a
        // per-tick allocation on the load-screen path. NEVER mutate this.
        static readonly List<string> _noContexts = new List<string>();

        // ── POI quest-clear stinger (the only authoritative clear signal) ─────────
        // POI_Cleared fires solely off the quest system's SleepersCleared event — the one
        // sleeper signal that crosses the network (NetPackageQuestEvent's ClearSleeper
        // case raises QuestEventManager.Current.ClearedSleepers on the client). It reaches
        // a client only while subscribed via an active clear/fetch quest objective
        // (ObjectiveClearSleepers), i.e. it fires once you complete the clear part of a POI
        // quest. We stage the cleared POI's boundingBoxPosition as a ONE-SHOT here; the next
        // BuildSnapshot that finds the player standing in that POI consumes it (removes it)
        // and sets GameState.PoiQuestCleared for a single tick. QuestEventManager.Current is
        // a process-wide singleton that survives world reloads, so we subscribe once; the
        // pending set is cleared on world-unload.
        static readonly HashSet<Vector3i> _poiClearedPending = new HashSet<Vector3i>();
        static bool _poiClearHooked;

        // Subscribe (once) to the quest system's sleepers-cleared event. Idempotent and
        // cheap to call every tick — guarded by _poiClearHooked.
        static void EnsurePoiClearHook()
        {
            if (_poiClearHooked) return;
            _poiClearHooked = true;
            QuestEventManager.Current.SleepersCleared += OnSleepersCleared;
            CMMConfig.D("[CMM POI] Subscribed to QuestEventManager.SleepersCleared.");
        }

        // The event fires with a world position inside the cleared POI. Resolve it back to
        // the prefab and stage a one-shot POI_Cleared for that POI. Logged unconditionally
        // so the in-game test can confirm whether/when this event reaches the client.
        static void OnSleepersCleared(Vector3 pos)
        {
            var world = GameManager.Instance?.World;
            PrefabInstance pi = PoiAt(world, pos);
            if (pi != null)
            {
                _poiClearedPending.Add(pi.boundingBoxPosition);
                CMMConfig.D($"[CMM POI] SleepersCleared FIRED at {pos} → '{(pi.prefab != null ? _prefabName(pi.prefab) : null) ?? pi.name}' " +
                            $"(bbox {pi.boundingBoxPosition}) → POI_Cleared staged.");
            }
            else
            {
                CMMConfig.D($"[CMM POI] SleepersCleared FIRED at {pos} but no POI resolved there.");
            }
        }

        // Clears the pending one-shots on world unload (the subscription itself persists
        // with the QuestEventManager singleton). Called from Patch_GameEnded.
        public static void ResetPoiClearState() => _poiClearedPending.Clear();

        // ── Version bridge: 3.0-only game members, resolved by reflection ────────
        // Two APIs used below exist only on 3.0:
        //   • Prefab.SleeperVolumeList — the design sleeper list (a richer "real dungeon"
        //     test than DifficultyTier).
        //   • TraderArea.GetTrader()   — a direct area→trader link.
        // Resolving them by name at load time (null when absent) lets ONE compiled DLL run
        // on both 2.6 and 3.0: where the member exists (3.0) we call it; where it doesn't
        // (2.6 / A20 / A21) we fall back to the pre-3.0 logic — so every version behaves
        // exactly as its dedicated build did. Cost is negligible: the sleeper test runs
        // only in the MP-client POI fallback, the trader call only while inside a trader
        // area. Kept out of the alpha #if because reflection references no absent symbol,
        // so the same source compiles unchanged against every game version.
        static readonly System.Reflection.FieldInfo _sleeperVolumeListField =
            typeof(Prefab).GetField("SleeperVolumeList");
        static System.Reflection.PropertyInfo _sleeperVolumeListCount;  // bound lazily off the live list object
        static readonly System.Reflection.MethodInfo _getTraderMethod =
            typeof(TraderArea).GetMethod("GetTrader", System.Type.EmptyTypes);

        // ── Version bridge: members that differ in KIND/ARITY between 2.6 and 3.0 ─────
        // These compile cleanly per-version but a SINGLE binary compiled against one version
        // MissingMember-throws on the other (ldfld vs callvirt get_X, or a baked optional-arg
        // count), so bind each by name at load and dispatch to whatever the loaded assembly has:
        //   • EntityPlayer.IsFriendOfLocalPlayer — field ≤2.6, property 3.0.
        //   • TraderInfo.IsOpen                  — property ≤2.6, field 3.0.
        //   • World.GetPOIAtPosition             — (Vector3,bool) on ≤2.6, (Vector3,FastTags?,
        //     FastTags?) on 3.0. C# resolves optional-arg defaults at the CALL site, so a
        //     direct one-arg call binds to only one version's overload.
        // The bool accessors compile to a typed, allocation-free delegate (these read on the
        // per-tick scan / MP scan paths); PoiAt likewise runs through a compiled delegate
        // (_poiAt) so the per-tick POI poll allocates nothing, with the reflection MethodInfo
        // kept only as the host-Mono compile fallback.
        static readonly Func<EntityPlayer, bool> _isFriendOfLocal = BoolAccessor<EntityPlayer>("IsFriendOfLocalPlayer");
        static readonly Func<TraderInfo, bool>   _traderIsOpen    = BoolAccessor<TraderInfo>("IsOpen");
        static readonly MethodInfo _getPoiAtMethod    = ResolveGetPoiAt();
        static readonly object[]   _getPoiArgsTemplate = BuildPoiArgsTemplate();
        // Compiled call form of GetPOIAtPosition: a typed Func that bakes each version's
        // trailing optional args as constants, so the per-tick POI poll runs with NO allocation
        // (MethodInfo.Invoke clones the args array + boxes the Vector3 on every call). Null when
        // the method is absent or Expression.Compile is unavailable on the host Mono, in which
        // case PoiAt falls back to the reflection path.
        static readonly Func<World, Vector3, PrefabInstance> _poiAt = BuildPoiAt();

        // ── Version bridge: A20/A21 ↔ 2.6/3.0 shape splits (same principle) ───────────
        //   • EntityAlive.IsSwimming() — absent on A20 (present A21+); A20 approximates from the
        //     public water fields (isSwimming / inWaterPercent), its dedicated-build expression.
        //   • Prefab.PrefabName — plain field on A20/A21, property on 2.6/3.0.
        // Each binds against whatever the loaded assembly has; every symbol referenced below exists
        // on the 3.0 compile target, so the same source compiles unchanged for every game version.
        // (Blood moon needs NO bridge: World.IsWorldEvent(WorldEvent.BloodMoon) is a public method
        // with the same signature on all four versions, so BuildSnapshot calls it directly.)
        static readonly Func<EntityPlayer, bool>       _isSwimming  = BuildIsSwimming();
        static readonly Func<Prefab, string>           _prefabName  = StringAccessor<Prefab>("PrefabName");

        // ── Version bridge: weather rain/snow getter RENAME ───────────────────────────
        // WeatherManager's per-tick precipitation getters were renamed:
        //   3.0:      GetCurrentRainfallPercent() / GetCurrentSnowfallPercent()  (0..1)
        //   A20/A21:  GetCurrentRainfallValue()   / GetCurrentSnowfallValue()    (0..1)
        // Both are no-arg instance floats, so bind whichever the loaded assembly has
        // (prefer the 3.0 name, fall back to the alpha name) into a compiled accessor.
        // Wind (GetWindSpeed), temperature (GetTemperature) and cloud thickness
        // (GetCurrentCloudThicknessPercent) kept their names on every version, so
        // BuildSnapshot calls those directly — no bridge needed.
        static readonly Func<WeatherManager, float> _rainfall =
            FloatGetter<WeatherManager>("GetCurrentRainfallPercent", "GetCurrentRainfallValue");
        static readonly Func<WeatherManager, float> _snowfall =
            FloatGetter<WeatherManager>("GetCurrentSnowfallPercent", "GetCurrentSnowfallValue");

        // Binds a no-arg float instance method that was RENAMED across versions to a typed
        // accessor: tries `primary`, then `fallback`; compiled once (no per-call boxing),
        // reflection fallback if Expression.Compile is unavailable, const-zero if neither
        // name exists. Same compile-once / fallback / null-safe shape as BoolAccessor.
        static Func<T, float> FloatGetter<T>(string primary, string fallback)
        {
            MethodInfo m = typeof(T).GetMethod(primary, Type.EmptyTypes)
                        ?? typeof(T).GetMethod(fallback, Type.EmptyTypes);
            if (m == null) return _ => 0f;
            var e = Expression.Parameter(typeof(T), "e");
            try { return Expression.Lambda<Func<T, float>>(Expression.Call(e, m), e).Compile(); }
            catch { return t => (float)m.Invoke(t, null); }
        }

        // Binds a bool member that is a FIELD on some game versions and a PROPERTY on others
        // to a typed accessor. Compiled once (no per-call boxing); falls back to boxed
        // reflection if Expression.Compile is unavailable on the host Mono, or to const-false
        // if the member is absent (never the case on the 2.6/3.0 targets).
        static Func<T, bool> BoolAccessor<T>(string member)
        {
            PropertyInfo prop = typeof(T).GetProperty(member);
            FieldInfo    fld  = prop == null ? typeof(T).GetField(member) : null;
            if (prop == null && fld == null) return _ => false;
            var e = Expression.Parameter(typeof(T), "e");
            Expression access = prop != null ? Expression.Property(e, prop) : (Expression)Expression.Field(e, fld);
            try { return Expression.Lambda<Func<T, bool>>(access, e).Compile(); }
            catch { return prop != null ? (Func<T, bool>)(t => (bool)prop.GetValue(t)) : (t => (bool)fld.GetValue(t)); }
        }

        // String counterpart of BoolAccessor: binds a member that is a FIELD on some versions and a
        // PROPERTY on others (Prefab.PrefabName). Same compile-once / reflection-fallback / null-if-
        // absent shape.
        static Func<T, string> StringAccessor<T>(string member)
        {
            PropertyInfo prop = typeof(T).GetProperty(member);
            FieldInfo    fld  = prop == null ? typeof(T).GetField(member) : null;
            if (prop == null && fld == null) return _ => null;
            var e = Expression.Parameter(typeof(T), "e");
            Expression access = prop != null ? Expression.Property(e, prop) : (Expression)Expression.Field(e, fld);
            try { return Expression.Lambda<Func<T, string>>(access, e).Compile(); }
            catch { return prop != null ? (Func<T, string>)(t => (string)prop.GetValue(t)) : (t => (string)fld.GetValue(t)); }
        }

        // Swimming. A21/2.6/3.0 have EntityAlive.IsSwimming(); A20 does not, so fall back to the public
        // water fields (isSwimming || inWaterPercent > 0.4f) — A20's exact dedicated-build expression.
        // Both fields are public on every version, so the fallback compiles against the 3.0 target.
        static Func<EntityPlayer, bool> BuildIsSwimming()
        {
            var p = Expression.Parameter(typeof(EntityPlayer), "p");
            var method = typeof(EntityPlayer).GetMethod("IsSwimming", Type.EmptyTypes);
            Expression body;
            if (method != null)
                body = Expression.Call(p, method);
            else
            {
                var isSw = typeof(EntityPlayer).GetField("isSwimming");
                var inW  = typeof(EntityPlayer).GetField("inWaterPercent");
                body = Expression.OrElse(
                    Expression.Field(p, isSw),
                    Expression.GreaterThan(Expression.Field(p, inW), Expression.Constant(0.4f)));
            }
            try { return Expression.Lambda<Func<EntityPlayer, bool>>(body, p).Compile(); }
            catch
            {
                if (method != null) return pl => (bool)method.Invoke(pl, null);
                var isSw = typeof(EntityPlayer).GetField("isSwimming");
                var inW  = typeof(EntityPlayer).GetField("inWaterPercent");
                return pl => (bool)isSw.GetValue(pl) || (float)inW.GetValue(pl) > 0.4f;
            }
        }

        // World.GetPOIAtPosition regardless of how many optional params the version added
        // (match by name + Vector3 first param). The args template carries each trailing
        // optional param's OWN default value, so an invoke reproduces the direct one-arg call.
        static MethodInfo ResolveGetPoiAt()
        {
            foreach (var m in typeof(World).GetMethods())
                if (m.Name == "GetPOIAtPosition")
                {
                    var ps = m.GetParameters();
                    if (ps.Length >= 1 && ps[0].ParameterType == typeof(Vector3)) return m;
                }
            return null;
        }
        static object[] BuildPoiArgsTemplate()
        {
            var ps = _getPoiAtMethod?.GetParameters();
            if (ps == null) return null;
            var a = new object[ps.Length];
            for (int i = 1; i < ps.Length; i++) a[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
            return a;
        }
        // Compile GetPOIAtPosition into a typed Func(world, pos), baking each trailing optional
        // param's OWN default value as a constant — the exact values BuildPoiArgsTemplate carries —
        // so the compiled call reproduces the direct one-arg call on whatever version's overload we
        // bound. Zero per-call allocation (no args array, no Vector3 box). Null if the method is
        // absent or Expression.Compile can't run on the host Mono (→ PoiAt uses the reflection path).
        static Func<World, Vector3, PrefabInstance> BuildPoiAt()
        {
            if (_getPoiAtMethod == null) return null;
            var ps = _getPoiAtMethod.GetParameters();
            var world = Expression.Parameter(typeof(World), "w");
            var pos   = Expression.Parameter(typeof(Vector3), "p");
            var callArgs = new Expression[ps.Length];
            callArgs[0] = pos;
            for (int i = 1; i < ps.Length; i++)
                callArgs[i] = ps[i].HasDefaultValue
                    ? Expression.Constant(ps[i].DefaultValue, ps[i].ParameterType)
                    : (Expression)Expression.Default(ps[i].ParameterType);
            try
            {
                Expression call = Expression.Call(world, _getPoiAtMethod, callArgs);
                return Expression.Lambda<Func<World, Vector3, PrefabInstance>>(call, world, pos).Compile();
            }
            catch { return null; }
        }
        static PrefabInstance PoiAt(World world, Vector3 pos)
        {
            if (world == null) return null;
            if (_poiAt != null) return _poiAt(world, pos);      // compiled: no per-call allocation
            // Reflection fallback (Expression.Compile unavailable on the host Mono): clone the
            // args template per call so the two call sites stay re-entrancy-safe.
            if (_getPoiAtMethod == null || _getPoiArgsTemplate == null) return null;
            var args = (object[])_getPoiArgsTemplate.Clone();
            args[0] = pos;
            return (PrefabInstance)_getPoiAtMethod.Invoke(world, args);
        }

        // "Real dungeon" gate for the MP client fallback, using only client-available
        // design data (sleeper VOLUMES aren't synced, but the prefab's design definition
        // is — clients load it to render the POI). On 3.0 the prefab's SleeperVolumeList
        // is the exact equivalent of the server's sleeperVolumes>0 guard and catches
        // tier-0 dungeons (e.g. abandoned_house_06) that a DifficultyTier>0 test would
        // miss. 2.6 / A20 / A21 have no SleeperVolumeList, so fall back to DifficultyTier>0.
        static bool IsRealPoiByDesign(Prefab prefab)
        {
            if (prefab == null) return false;
            if (_sleeperVolumeListField != null)                       // 3.0
            {
                object list = _sleeperVolumeListField.GetValue(prefab);
                if (list == null) return false;
                if (_sleeperVolumeListCount == null)
                    _sleeperVolumeListCount = list.GetType().GetProperty("Count");
                return _sleeperVolumeListCount != null
                    && (int)_sleeperVolumeListCount.GetValue(list) > 0;
            }
            return prefab.DifficultyTier > 0;                          // 2.6 / A20 / A21
        }

        // Resolves an entity's config class name (e.g. "zombieBoeFeral") for threat
        // weighting. EntityClass.list is keyed by the entity's entityClass hash; guarded
        // because a despawning entity can briefly carry an unmapped class id.
        static string GetEntityClassName(Entity e)
        {
            try { return EntityClass.list[e.entityClass]?.entityClassName; }
            catch { return null; }
        }

        // Pre-3.0 fallback (also compiled into the 3.0/unified build, where it's the
        // reflection path's fallback but never actually reached): resolve the trader entity
        // by finding the EntityTrader whose area is this one. EntityTrader.traderArea is a
        // public field in every supported version (2.6/3.0/A20/A21), so this compiles and
        // runs anywhere. Only called when the player is inside a trader area, so the
        // entity-list scan is rare. Snapshots the list — the game may mutate it mid-scan.
        static EntityTrader FindTraderForArea(World world, TraderArea area)
        {
            var list = world?.Entities?.list;
            if (list == null) return null;
            // Snapshot into a reused scratch list (the game may mutate the live list mid-scan);
            // main-thread only, like the rest of BuildSnapshot, so one shared buffer is safe.
            _traderScanScratch.Clear();
            _traderScanScratch.AddRange(list);
            foreach (var e in _traderScanScratch)
                if (e is EntityTrader t && ReferenceEquals(t.traderArea, area))
                    return t;
            return null;
        }

        // A live, allied player who is "with" the local player: in the local player's
        // party OR an ACL friend, and within PlayerDetectRange. Both IsInPartyOfLocalPlayer
        // and IsFriendOfLocalPlayer are replicated/derivable on a client (party membership
        // via NetPackagePartyData; friend status via the synced persistent-player ACL), so
        // this is valid client-side. Excludes the local player itself and the dead.
        static bool IsNearbyAlly(EntityPlayer other, EntityPlayer local)
        {
            if (other == null || other == local || !other.IsAlive()) return false;
            if (!other.IsInPartyOfLocalPlayer && !_isFriendOfLocal(other)) return false;
            return Vector3.Distance(local.position, other.position) < CMMConfig.PlayerDetectRange;
        }

        // A live player within PlayerDetectRange who is NOT an ally: neither in the
        // local player's party nor an ACL friend — a "stranger" in the game's PvP
        // sense. The exact inverse relationship test of IsNearbyAlly, reading the same
        // client-replicated state (party membership + the synced persistent-player
        // ACL), so it's valid client-side. Excludes the local player itself and the
        // dead. This is a relationship check only; whether PvP rules actually let the
        // stranger harm you is a separate server setting (PlayerKillingMode) and is
        // intentionally not gated here.
        static bool IsNearbyStranger(EntityPlayer other, EntityPlayer local)
        {
            if (other == null || other == local || !other.IsAlive()) return false;
            if (other.IsInPartyOfLocalPlayer || _isFriendOfLocal(other)) return false;
            return Vector3.Distance(local.position, other.position) < CMMConfig.PlayerDetectRange;
        }

        // A live player close enough for their fight to become OUR combat (squad-shared
        // combat). Uses its own CombatShareRange and, when CombatShareRequireAlly is set,
        // the same party/friend test as IsNearbyAlly; with it off, any nearby living player
        // qualifies. Both knobs are independent of PlayerDetectRange / the ally-count logic
        // so the "share a squadmate's fight" radius and the ally requirement can be tuned
        // on their own. Excludes the local player itself and the dead.
        static bool SharesCombat(EntityPlayer other, EntityPlayer local)
        {
            if (other == null || other == local || !other.IsAlive()) return false;
            if (CMMConfig.CombatShareRequireAlly
                && !other.IsInPartyOfLocalPlayer && !_isFriendOfLocal(other)) return false;
            return Vector3.Distance(local.position, other.position) < CMMConfig.CombatShareRange;
        }

        // ─────────────────────────────────────────────────────────────────────
        // STAGE 1 — POLL: game state → version-agnostic GameState
        // ─────────────────────────────────────────────────────────────────────
        // All cross-version game-API divergence is absorbed by the reflection bridges (declared
        // above), so this method reads as straight-line version-agnostic code. Returns null when
        // the world or primary player isn't available yet; callers treat that as "no contexts".
        public static GameState BuildSnapshot()
        {
            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            if (world == null || player == null) return null;
            var localPlayer = player as EntityPlayerLocal;

            // Lazily hook the quest sleepers-cleared event (used by the MP POI fallback).
            EnsurePoiClearHook();

            var gameState = _gameState;
            gameState.Now   = Time.realtimeSinceStartup;
            gameState.IsDay = world.IsDaytime();
            // Blood moon: World.IsWorldEvent(WorldEvent.BloodMoon) is a public method on every version
            // (A20/A21/2.6/3.0) returning the game's authoritative isEventBloodMoon flag. The old Alpha
            // path read the player's EntityAlive.IsBloodMoon, but that field is only ever set on blood-
            // moon HORDE ZOMBIES, never the player — so blood moon never registered on A20/A21.
            gameState.IsBloodMoon = world.IsWorldEvent(World.WorldEvent.BloodMoon);
            gameState.HealthPct = player.Health / (float)player.GetMaxHealth();

            // Swimming: in deep enough water to swim (not mere shallow wading). The
            // 8s WarmUp dwell on the context keeps brief dips from interrupting music.
            // A20 has no IsSwimming(): its dedicated build approximated it as
            // (isSwimming || inWaterPercent > 0.4f). The game's IsSwimming() requires
            // inWaterPercent > 0.7f (head mostly under), so floating at the surface (head above
            // water, ~0.4–0.65) is missed; > 0.4f covers waist-deep+ and clears shallow wading
            // (the game's own speed-reduction starts at 0.3f). _isSwimming picks the method where
            // present (A21+) and this field expression on A20 (see BuildIsSwimming).
            gameState.IsSwimming = _isSwimming(player);

            gameState.IsPanicked = localPlayer?.ThreatLevel?.Category == ThreatLevelType.Panicked;
            gameState.IsCrouching = player.IsCrouching;

            // Time of day. WorldTimeToElements has the same (Days,Hours,Minutes) tuple
            // signature on every supported version, so call it directly. Hour is minute-
            // resolved so the script can do smooth dawn/dusk transitions, not just IsDay.
            var (days, hours, minutes) = GameUtils.WorldTimeToElements(world.GetWorldTime());
            gameState.Day  = days;
            gameState.Hour = hours + minutes / 60f;

            // Weather. WindSpeed/GetTemperature are static and identically named on every
            // version; cloud thickness is a same-named instance getter; only the rain/snow
            // getters were renamed (see _rainfall/_snowfall bridges). WeatherManager.Instance
            // can be null very early in load — treat that as calm/clear.
            var weather = WeatherManager.Instance;
            gameState.WindSpeed      = WeatherManager.GetWindSpeed();
            gameState.Temperature    = WeatherManager.GetTemperature();
            gameState.Rainfall       = weather != null ? _rainfall(weather) : 0f;
            gameState.Snowfall       = weather != null ? _snowfall(weather) : 0f;
            gameState.CloudThickness = weather != null ? weather.GetCurrentCloudThicknessPercent() : 0f;

            // Player vitals (0..1). Stats is populated for the whole life of a live player,
            // but guard defensively so a mid-teardown tick can't NRE the scan.
            var stats = player.Stats;
            gameState.StaminaPct = stats?.Stamina != null ? stats.Stamina.ValuePercent : 1f;
            gameState.FoodPct    = stats?.Food    != null ? stats.Food.ValuePercent    : 1f;
            gameState.WaterPct   = stats?.Water   != null ? stats.Water.ValuePercent   : 1f;

            // Fall distance: blocks fallen in the current fall, 0 when grounded/rising.
            // Public field on Entity in every version.
            gameState.FallDistance = player.fallDistance;

            // ── Status effects (buffs) ─────────────────────────────────────────
            // Snapshot the active buff names. Store the buff objects' OWN name strings
            // (no ToLower, no copy) so the poll allocates nothing; cmm.hasBuff matches
            // case-insensitively. EntityAlive.Buffs.ActiveBuffs / BuffValue.BuffName are
            // present and identically shaped on A20/A21/2.6/3.0.
            gameState.Buffs.Clear();
            var activeBuffs = player.Buffs?.ActiveBuffs;
            if (activeBuffs != null)
                for (int bi = 0; bi < activeBuffs.Count; bi++)
                {
                    string bn = activeBuffs[bi]?.BuffName;
                    if (!string.IsNullOrEmpty(bn)) gameState.Buffs.Add(bn);
                }

            // Vehicle: store the lowercased type name; the token mapping is a decision.
            gameState.VehicleType = player.AttachedToEntity is EntityVehicle vehicle
                ? vehicle.GetType().Name.ToLower()
                : null;

            // Trader detection: mirror the game's actual check — player must be inside the
            // prefab's protect bounds AND the trader must be open (daytime). TraderName is
            // left null unless both hold; "" means open but unnamed (still → generic Trader).
            gameState.TraderName = null;
            TraderArea traderArea = world.GetTraderAreaAt(player.GetBlockPosition());
            if (traderArea != null)
            {
                // TraderArea.GetTrader() exists only in 3.0 (resolved via _getTraderMethod);
                // older versions have no area→trader link, so resolve the trader entity by
                // its area instead. 3.0 runs its real GetTrader() (owningTrader, then a
                // bounds scan); 2.6/A20/A21 run the reference-match scan — each identical to
                // that version's dedicated build.
                EntityTrader traderEntity = _getTraderMethod != null
                    ? (EntityTrader)_getTraderMethod.Invoke(traderArea, null)
                    : FindTraderForArea(world, traderArea);
                var traderInfo = traderEntity?.TraderInfo;
                if (traderInfo != null && _traderIsOpen(traderInfo))
                    gameState.TraderName = traderEntity.EntityName?.ToLower() ?? "";
            }

            // Biome: store the raw name. null when the chunk at the player's position
            // isn't streamed in yet (e.g. immediately after a debug teleport in A20);
            // the decision layer skips the ambient block entirely in that case. "" (a
            // non-null empty) means the chunk is loaded but the name is unrecognized,
            // which still yields the generic Ambient base — matching the old behavior.
            var biome = world.GetBiome((int)player.position.x, (int)player.position.z);
            gameState.Biome = biome != null ? (biome.m_sBiomeName ?? "") : null;

            // ── Enemy scan ────────────────────────────────────────────────────
            // Range-gate ALL combat/tension counts here so the decision layer never
            // sees an out-of-range enemy. Snapshot the entity list before iterating —
            // the game can add or remove entities (spawns, deaths, chunk unloads) while
            // this loop runs, which causes InvalidOperationException on the live list.
            // EnemyInfo instances are drawn from a pool so refilling Enemies each tick
            // doesn't allocate.
            // In multiplayer, an enemy aggroed onto a NEARBY ALLY counts as combat too:
            // it's a fight happening right next to us (the scan is already range-gated to
            // EnemyDetectRange around the local player), so squadmate aggro should drive
            // combat music even when nothing is on us specifically. ConnectionManager
            // .IsSinglePlayer is false on any client and false on a host once someone else
            // has joined; it's true only for a solo host — so !IsSinglePlayer is exactly
            // "another player could be present." Defaults to single-player (ally logic off)
            // if the manager isn't available, leaving SP behavior identical to before.
            bool isMultiplayer =
                !(SingletonMonoBehaviour<ConnectionManager>.Instance?.IsSinglePlayer ?? true);

            gameState.Enemies.Clear();
            _entityScratch.Clear();
            _entityScratch.AddRange(world.Entities.list);
            int used = 0;
            foreach (var entity in _entityScratch)
            {
                if (entity == null) continue;
                if (entity is EntityEnemy enemy && enemy.IsAlive())
                {
                    float dist = Vector3.Distance(player.position, enemy.position);

                    bool alert = enemy.IsAlert;
                    // Targeting is only meaningful (and only read) while alert.
                    // GetAttackTargetLocal() (not GetAttackTarget()) so this works on a
                    // multiplayer CLIENT: enemy AI runs server-side, so the authoritative
                    // attackTarget field is null on a client — the server replicates the
                    // target into attackTargetClient instead, which GetAttackTargetLocal()
                    // returns for remote entities (and falls back to attackTarget for local
                    // entities, i.e. host/singleplayer — correct in both cases).
                    EntityAlive attackTarget = alert ? enemy.GetAttackTargetLocal() : null;

                    bool targetingMe = attackTarget == player && dist < CMMConfig.EnemyDetectRange;

                    // ── Squad-shared combat (multiplayer) ──────────────────────
                    // An enemy fighting a nearby player (within CombatShareRange, and — when
                    // CombatShareRequireAlly is set — a party member / friend) counts as OUR
                    // combat too, gated by the enemy's distance to THAT PLAYER, not to us. So
                    // the whole in-range squad shares one combat state: a squadmate being
                    // attacked puts us in combat even when their zombies are past our own
                    // EnemyDetectRange (and even when nothing is on us at all). In single-player
                    // there is never another player, so this is always false and behavior is
                    // unchanged.
                    bool targetingAlly =
                        isMultiplayer && !targetingMe
                        && attackTarget is EntityPlayer ally && ally != player
                        && SharesCombat(ally, player)
                        && Vector3.Distance(ally.position, enemy.position) < CMMConfig.EnemyDetectRange;

                    // Keep the enemy in the snapshot if it's physically near US (drives
                    // tension + our own combat) OR it's part of a squadmate's fight we're
                    // sharing. Enemies that are neither are irrelevant and dropped.
                    if (dist >= CMMConfig.EnemyDetectRange && !targetingAlly) continue;

                    bool targeting = targetingMe || targetingAlly;

                    EnemyInfo info;
                    if (used < _enemyPool.Count) info = _enemyPool[used];
                    else { info = new EnemyInfo(); _enemyPool.Add(info); }
                    used++;

                    info.ClassName         = GetEntityClassName(enemy);
                    info.Distance          = dist;
                    info.IsAlert           = alert;
                    info.IsTargetingPlayer = targeting;
                    gameState.Enemies.Add(info);
                }
            }

            // ── Nearby-ally scan (multiplayer only) ────────────────────────────
            // Count living party members / friends within PlayerDetectRange. The
            // decision layer uses this to scale combat intensity — fighting shoulder
            // to shoulder with a squad is a bigger musical moment than soloing the
            // same swarm. Single-player leaves this at 0 (the scan is skipped), so SP
            // behavior is unchanged. world.GetPlayers() is the full replicated player
            // list, so this works on a client.
            gameState.AlliesNearby = 0;
            gameState.StrangersNearby = 0;
            if (isMultiplayer)
            {
                var players = world.GetPlayers();
                for (int pi = 0; pi < players.Count; pi++)
                {
                    if (IsNearbyAlly(players[pi], player))         gameState.AlliesNearby++;
                    else if (IsNearbyStranger(players[pi], player)) gameState.StrangersNearby++;
                }
            }

            // ── Home ──────────────────────────────────────────────────────────
            gameState.NearHome = false;
            if (localPlayer != null)
            {
                // persistentPlayerData is null on the entity for the entire first session of a
                // new game: GameManager.setLocalPlayerEntity() assigns it BEFORE CreatePlayerData()
                // runs, so it captures null and is never re-assigned. GetPersistentLocalPlayer()
                // always reflects the current authoritative PPD, including after the new-game path.
                var ppd = localPlayer.persistentPlayerData
                       ?? GameManager.Instance?.GetPersistentLocalPlayer();
                if (ppd != null)
                {
                    bool nearHome = false;
                    if (ppd.HasBedrollPos)
                    {
                        Vector3i bed = ppd.BedrollPos;
                        nearHome = Vector3.Distance(player.position, bed) < CMMConfig.HomeDetectRange;
                    }
                    if (!nearHome)
                    {
                        // "Nearest claim within range" is equivalent to "any claim within
                        // range", so early-exit on the first hit instead of sorting all
                        // claims (OrderBy().First()). Squared compare avoids the per-claim sqrt.
                        float homeRangeSqr = CMMConfig.HomeDetectRange * CMMConfig.HomeDetectRange;
                        foreach (var c in ppd.GetLandProtectionBlocks())
                        {
                            if ((player.position - c.ToVector3()).sqrMagnitude < homeRangeSqr)
                            {
                                nearHome = true;
                                break;
                            }
                        }
                    }
                    gameState.NearHome = nearHome;
                }
            }

            // ── POI ───────────────────────────────────────────────────────────
            gameState.InPoi = false;
            gameState.PoiName = "None";
            gameState.PoiId = "None";
            gameState.PoiTier = 0;
            gameState.PoiEnemiesRemaining = 0;
            gameState.PoiQuestCleared = false;
            gameState.PoiClearFromQuestOnly = false;
            PrefabInstance prefab = PoiAt(world, player.position);
            // GetPOIAtPosition returns any prefab footprint, including RWG road/gateway
            // tiles and decoration "parts" (all DifficultyTier 0, no sleepers). Those were
            // flooding the stack with POI_T0 in the open world. Require at least one sleeper
            // volume so only real dungeon POIs count — design-time data that survives
            // clearing, so a fully-cleared POI still registers (POI_Cleared keeps working).
            if (prefab != null && prefab.sleeperVolumes.Count > 0)
            {
                // In 3.0, PrefabName is a property backed by location.FileNameNoExtension and
                // is always populated. In A21 it is a plain field that defaults to "" unless
                // the prefab XML explicitly sets it (most don't). Fall back to PrefabInstance.name
                // (locationName.id) so the cleared-POI guard has a stable, non-empty identifier
                // on all versions.
                string prefabName = prefab.prefab != null ? _prefabName(prefab.prefab) : null;
                string currentPoI = !string.IsNullOrEmpty(prefabName) ? prefabName : prefab.name;
                // One pass instead of four LINQ Count() enumerations (each with its own
                // delegate). enemiesRemainingInPoi drives the logic; the rest feed the log.
                int totalVolumes = prefab.sleeperVolumes.Count;
                int clearedVolumes = 0, excludedVolumes = 0, enemiesRemainingInPoi = 0;
                foreach (var v in prefab.sleeperVolumes)
                {
                    if (v.wasCleared) clearedVolumes++;
                    if (v.isQuestExclude) excludedVolumes++;
                    if (!v.wasCleared && !v.isQuestExclude) enemiesRemainingInPoi++;
                }
                int tier = prefab.prefab?.DifficultyTier ?? 0;
                string tierSuffix = (tier >= 0 && tier <= 5) ? "_T" + tier : "";

                // Guard the interpolation: D() re-checks the flag, but the string is built at the
                // call site BEFORE D() runs — and this fires every scan tick while inside a POI.
                if (CMMConfig.DebugLogging)
                    CMMConfig.D($"[CMM POI] name='{currentPoI}' tier={tier} tierSuffix='{tierSuffix}' " +
                                $"volumes={totalVolumes} cleared={clearedVolumes} excluded={excludedVolumes} remaining={enemiesRemainingInPoi}");

                gameState.InPoi = true;
                gameState.PoiName = currentPoI;
                // Instance-unique key ("<location>.<id>"); distinguishes two copies of the
                // same prefab type so the script's same-POI continuity guard can't bleed
                // across them. Always populated at PrefabInstance construction.
                gameState.PoiId = prefab.name;
                gameState.PoiTier = tier;
                gameState.PoiEnemiesRemaining = enemiesRemainingInPoi;
            }
            else if (isMultiplayer && prefab != null && IsRealPoiByDesign(prefab.prefab))
            {
                // ── MP CLIENT FALLBACK ──────────────────────────────────────────
                // On a multiplayer client the world's SleeperVolume objects are never
                // synced (server-only save load; only the quest system replicates
                // anything), so prefab.sleeperVolumes is always empty and the accurate
                // path above can't run. Use client-available design data + live entities:
                //   • presence/tier  → GetPOIAtPosition + design data (IsRealPoiByDesign).
                //   • POI_Infested vs POI → count of spawned enemies inside the POI bounds.
                //     A spawned sleeper IS an EntityEnemy in bounds, and dormant sleepers
                //     are replicated as IsSleeping entities, so they count too. >0 ⇒
                //     infested, 0 ⇒ plain POI. The un-triggered far-room gap is inherent:
                //     the server stages sleeper spawns and never replicates un-spawned
                //     volumes, so a deep POI can read 0 between rooms — which is exactly
                //     why POI_Cleared must NOT come from this count (see below).
                //   • POI_Cleared → ONLY the quest SleepersCleared one-shot. PoiQuestCleared
                //     fires it; PoiClearFromQuestOnly tells the script not to derive the
                //     stinger from the count bouncing to 0.
                string prefabName = prefab.prefab != null ? _prefabName(prefab.prefab) : null;
                string currentPoI = !string.IsNullOrEmpty(prefabName) ? prefabName : prefab.name;
                int tier = prefab.prefab?.DifficultyTier ?? 0;

                // Count living enemies whose position is inside the POI's bounding box.
                // _entityScratch is the entity-list snapshot taken by the enemy scan above,
                // so this reuses it (no extra allocation / no second live-list walk).
                Bounds aabb = prefab.GetAABB();
                int spawnedEnemies = 0;
                foreach (var e in _entityScratch)
                    if (e is EntityEnemy en && en.IsAlive() && aabb.Contains(en.position))
                        spawnedEnemies++;

                // Consume the one-shot quest-clear marker for this POI (if any).
                bool questCleared = _poiClearedPending.Remove(prefab.boundingBoxPosition);

                if (CMMConfig.DebugLogging)
                    CMMConfig.D($"[CMM POI] (MP client) name='{currentPoI}' tier={tier} " +
                                $"spawnedEnemies={spawnedEnemies} questCleared={questCleared} (client heuristic)");

                gameState.InPoi = true;
                gameState.PoiName = currentPoI;
                gameState.PoiId = prefab.name;
                gameState.PoiTier = tier;
                gameState.PoiEnemiesRemaining = spawnedEnemies;
                gameState.PoiQuestCleared = questCleared;
                gameState.PoiClearFromQuestOnly = true;
            }

            return gameState;
        }

        // ─────────────────────────────────────────────────────────────────────
        // STAGE 2 — DECIDE: handed to the Lua script (Config/context.lua)
        // ─────────────────────────────────────────────────────────────────────
        // ScriptHost always has a script loaded (the on-disk Config/context.lua, or
        // the default embedded in the DLL) and never throws, so this is the whole
        // resolver — there is no built-in C# decision path anymore.
        public static List<string> ResolveContext()
        {
            GameState gameState;
            using (CMMPerf.Measure(CMMPerf.Phase.ScanPoll))
                gameState = BuildSnapshot();
            if (gameState == null) return _noContexts;
            using (CMMPerf.Measure(CMMPerf.Phase.ScanResolve))
                return ScriptHost.Resolve(gameState);
        }
    }
}
