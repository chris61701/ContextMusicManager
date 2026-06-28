using System.Collections.Generic;
using MusicUtils.Enums;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // CONTEXT RESOLVER
    // ─────────────────────────────────────────────────────────────────────────
    // BuildSnapshot() polls the game into a version-agnostic GameState (ALL game-
    // version #if GV_* forks live here). The decision logic — turning that snapshot
    // into active context tokens — lives in the Lua script (see ScriptHost and
    // Config/context.lua), so ResolveContext() is just "poll, then ask the script".

    public static class ContextResolver
    {
        // Reused snapshot + buffers. ResolveContext runs once per scan tick on the
        // main thread (never re-entrant), so a single shared GameState, the entity
        // scratch list, and a pool of EnemyInfo objects let the whole per-tick poll
        // run without allocating after warmup.
        static readonly GameState _gameState = new GameState();
        static readonly List<Entity> _entityScratch = new List<Entity>();
        static readonly List<EnemyInfo> _enemyPool = new List<EnemyInfo>();

        // Resolves an entity's config class name (e.g. "zombieBoeFeral") for threat
        // weighting. EntityClass.list is keyed by the entity's entityClass hash; guarded
        // because a despawning entity can briefly carry an unmapped class id.
        static string GetEntityClassName(Entity e)
        {
            try { return EntityClass.list[e.entityClass]?.entityClassName; }
            catch { return null; }
        }

#if !GV_V3
        // Pre-3.0 TraderArea has no GetTrader(): resolve the trader entity by finding the
        // EntityTrader whose area is this one (EntityTrader.traderArea is public in all
        // older versions). Only called when the player is inside a trader area, so the
        // entity-list scan is rare. Snapshots the list — the game may mutate it mid-scan.
        static EntityTrader FindTraderForArea(World world, TraderArea area)
        {
            var list = world?.Entities?.list;
            if (list == null) return null;
            foreach (var e in new List<Entity>(list))
                if (e is EntityTrader t && ReferenceEquals(t.traderArea, area))
                    return t;
            return null;
        }
#endif

        // ─────────────────────────────────────────────────────────────────────
        // STAGE 1 — POLL: game state → version-agnostic GameState
        // ─────────────────────────────────────────────────────────────────────
        // Every #if GV_* fork lives in this method. Returns null when the world or
        // primary player isn't available yet; callers treat that as "no contexts".
        public static GameState BuildSnapshot()
        {
            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            if (world == null || player == null) return null;
            var localPlayer = player as EntityPlayerLocal;

            var gameState = _gameState;
            gameState.Now   = Time.realtimeSinceStartup;
            gameState.IsDay = world.IsDaytime();
            // World.isEventBloodMoon is public in 2.6/3.0 but private in A20/A21; the
            // Alphas fall back to the player's public EntityAlive.IsBloodMoon flag, which
            // is set while a blood moon is active.
            gameState.IsBloodMoon =
#if GV_A20 || GV_A21
                player.IsBloodMoon;
#else
                world.isEventBloodMoon;
#endif
            gameState.HealthPct = player.Health / (float)player.GetMaxHealth();

            // Swimming: in deep enough water to swim (not mere shallow wading). The
            // 8s WarmUp dwell on the context keeps brief dips from interrupting music.
            // A20: IsSwimming() requires inWaterPercent > 0.7f (head mostly under),
            // so floating at the surface (head above water, ~0.4–0.65 range) is missed.
            // inWaterPercent > 0.4f covers waist-deep+; the game's own speed-reduction
            // starts at 0.3f, so 0.4f safely clears shallow ankle/knee wading.
#if GV_A20
            gameState.IsSwimming = player.isSwimming || player.inWaterPercent > 0.4f;
#else
            gameState.IsSwimming = player.IsSwimming();
#endif

            gameState.IsPanicked = localPlayer?.ThreatLevel?.Category == ThreatLevelType.Panicked;

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
                // TraderArea.GetTrader() exists only in 3.0; older versions have no
                // area→trader link, so resolve the trader entity by its area instead.
                EntityTrader traderEntity =
#if GV_V3
                    traderArea.GetTrader();
#else
                    FindTraderForArea(world, traderArea);
#endif
                if (traderEntity?.TraderInfo?.IsOpen == true)
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
                    if (dist >= CMMConfig.EnemyDetectRange) continue;

                    bool alert = enemy.IsAlert;
                    // Combat intensity is driven by who is actually targeting us, so
                    // targeting is only meaningful (and only recorded) while alert.
                    bool targeting = alert && enemy.GetAttackTarget() == player;

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
            gameState.PoiTier = 0;
            gameState.PoiEnemiesRemaining = 0;
            PrefabInstance prefab = world?.GetPOIAtPosition(player.position);
            if (prefab != null)
            {
                // In 3.0, PrefabName is a property backed by location.FileNameNoExtension and
                // is always populated. In A21 it is a plain field that defaults to "" unless
                // the prefab XML explicitly sets it (most don't). Fall back to PrefabInstance.name
                // (locationName.id) so the cleared-POI guard has a stable, non-empty identifier
                // on all versions.
                string prefabName = prefab.prefab?.PrefabName;
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

                CMMConfig.D($"[CMM POI] name='{currentPoI}' tier={tier} tierSuffix='{tierSuffix}' " +
                            $"volumes={totalVolumes} cleared={clearedVolumes} excluded={excludedVolumes} remaining={enemiesRemainingInPoi}");

                gameState.InPoi = true;
                gameState.PoiName = currentPoI;
                gameState.PoiTier = tier;
                gameState.PoiEnemiesRemaining = enemiesRemainingInPoi;
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
            if (gameState == null) return new List<string>();
            using (CMMPerf.Measure(CMMPerf.Phase.ScanResolve))
                return ScriptHost.Resolve(gameState);
        }
    }
}
