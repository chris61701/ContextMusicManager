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
            PrefabInstance pi = world?.GetPOIAtPosition(pos);
            if (pi != null)
            {
                _poiClearedPending.Add(pi.boundingBoxPosition);
                CMMConfig.D($"[CMM POI] SleepersCleared FIRED at {pos} → '{pi.prefab?.PrefabName ?? pi.name}' " +
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

        // "Real dungeon" gate for the MP client fallback, using only client-available
        // design data (sleeper VOLUMES aren't synced, but the prefab's design definition
        // is — clients load it to render the POI). On 3.0 the prefab's SleeperVolumeList
        // is the exact equivalent of the server's sleeperVolumes>0 guard and catches
        // tier-0 dungeons (e.g. abandoned_house_06) that a DifficultyTier>0 test would
        // miss. A20/A21 have no SleeperVolumeList, so fall back to DifficultyTier>0.
        static bool IsRealPoiByDesign(Prefab prefab)
        {
            if (prefab == null) return false;
#if GV_V3
            return prefab.SleeperVolumeList != null && prefab.SleeperVolumeList.Count > 0;
#else
            return prefab.DifficultyTier > 0;
#endif
        }

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

        // A live, allied player who is "with" the local player: in the local player's
        // party OR an ACL friend, and within PlayerDetectRange. Both IsInPartyOfLocalPlayer
        // and IsFriendOfLocalPlayer are replicated/derivable on a client (party membership
        // via NetPackagePartyData; friend status via the synced persistent-player ACL), so
        // this is valid client-side. Excludes the local player itself and the dead.
        static bool IsNearbyAlly(EntityPlayer other, EntityPlayer local)
        {
            if (other == null || other == local || !other.IsAlive()) return false;
            if (!other.IsInPartyOfLocalPlayer && !other.IsFriendOfLocalPlayer) return false;
            return Vector3.Distance(local.position, other.position) < CMMConfig.PlayerDetectRange;
        }

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

            // Lazily hook the quest sleepers-cleared event (used by the MP POI fallback).
            EnsurePoiClearHook();

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
                    // An enemy fighting a nearby ally (party member / friend within
                    // PlayerDetectRange) counts as OUR combat too — and is gated by the
                    // enemy's distance to THAT ALLY, not to us. So the whole in-range
                    // party shares one combat state: a squadmate being attacked puts us
                    // in combat even when their zombies are past our own EnemyDetectRange
                    // (and even when nothing is on us at all). In single-player nothing is
                    // ever a nearby ally, so this is always false and behavior is unchanged.
                    bool targetingAlly =
                        isMultiplayer && !targetingMe
                        && attackTarget is EntityPlayer ally && ally != player
                        && IsNearbyAlly(ally, player)
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
            if (isMultiplayer)
            {
                var players = world.GetPlayers();
                for (int pi = 0; pi < players.Count; pi++)
                    if (IsNearbyAlly(players[pi], player))
                        gameState.AlliesNearby++;
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
            gameState.PoiQuestCleared = false;
            gameState.PoiClearFromQuestOnly = false;
            PrefabInstance prefab = world?.GetPOIAtPosition(player.position);
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
                string prefabName = prefab.prefab?.PrefabName;
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

                CMMConfig.D($"[CMM POI] (MP client) name='{currentPoI}' tier={tier} " +
                            $"spawnedEnemies={spawnedEnemies} questCleared={questCleared} (client heuristic)");

                gameState.InPoi = true;
                gameState.PoiName = currentPoI;
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
            if (gameState == null) return new List<string>();
            using (CMMPerf.Measure(CMMPerf.Phase.ScanResolve))
                return ScriptHost.Resolve(gameState);
        }
    }
}
