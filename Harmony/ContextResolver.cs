using System.Collections.Generic;
using System.Linq;
using MusicUtils.Enums;
using UnityEngine;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // CONTEXT RESOLVER
    // ─────────────────────────────────────────────────────────────────────────

    public static class ContextResolver
    {
        static readonly int DangerThreshold = 1;
        // EnemyDetectRange, HomeDetectRange, PlayerHealthDangerThresholdPercent,
        // CombatExitDelay and CombatCooldown are tunable in Config/settings.xml (see
        // CMMConfig). CombatExitDelay/CombatCooldown govern how the latched combat tier
        // is held and forgotten — see CMMConfig for the full description.

        static string combatState = "None";
        // Latched combat tier index into CombatThreatConfig.Tiers (-1 = not in combat).
        // Used by the weighted-threat path in place of the combatState string.
        static int combatTier = -1;
        static float lastAggroTime = 0f;
        static bool wasInfested = false;
        static string lastPoI = "None";

#if CMM_DEBUG_HUD
        // ── Live debug snapshot ───────────────────────────────────────────────
        // Last values computed by ResolveContext, surfaced for the in-game threat
        // HUD (see ContextMusicManager.DebugHud). Written on the main thread each
        // scan tick; read by OnGUI on the same thread, so no synchronization needed.
        // Compiled in only with CMM_DEBUG_HUD so shipping builds carry no overhead.
        public static bool   ThreatScoringActive;   // weighted model configured & in use
        public static float  ThreatScore;           // summed score this tick
        public static int    ThreatTier = -1;        // latched tier index (-1 = none)
        public static string ThreatTierContext = "None";
        public static bool   InCombat;
        public static int    EnemiesNearby, EnemyAlertCount, EnemyAggroCount;
#endif

        public static void resetValues() { combatState = "None"; combatTier = -1; }

        // Resolves an entity's config class name (e.g. "zombieBoeFeral") for threat
        // weighting. EntityClass.list is keyed by the entity's entityClass hash; guarded
        // because a despawning entity can briefly carry an unmapped class id.
        static string GetEntityClassName(Entity e)
        {
            try { return EntityClass.list[e.entityClass]?.entityClassName; }
            catch { return null; }
        }

        // The game's Panicked signal uses a wider 50u/any-player box than our in-range
        // scan and routinely fires with nothing actually nearby. We only honor it as a
        // BRIDGE TO VANILLA combat music: when we ship no combat music of our own,
        // escalating to Combat_High lets the fallback chain (Combat_High → Combat_Low →
        // Combat → VanillaIfPanicked) hand off to the game's combat track. If we DO have combat
        // music (Combat_High, Combat_Low, or the base Combat), we trust our own threat detection instead
        // and ignore the Panicked signal entirely, so its loose box can't spuriously
        // trigger our tracks when our scan found nothing in range.
        static bool PanickedBridgeAllowed() =>
            !ContextMusicManager.ContextHasFiles("Combat_High") &&
            !ContextMusicManager.ContextHasFiles("Combat_Low") &&
            !ContextMusicManager.ContextHasFiles("Combat");

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

        public static List<string> ResolveContext()
        {
            //Stopwatch sw = Stopwatch.StartNew();
            var world = GameManager.Instance?.World;
            var player = world?.GetPrimaryPlayer();
            if (world == null || player == null)
                return new List<string>();
            var localPlayerEarly = player as EntityPlayerLocal;

            var activeContexts = new List<string>();
            bool isDay = world.IsDaytime();
            // World.isEventBloodMoon is public in 2.6/3.0 but private in A20/A21; the
            // Alphas fall back to the player's public EntityAlive.IsBloodMoon flag, which
            // is set while a blood moon is active.
            bool isBloodmoon =
#if GV_A20 || GV_A21
                player.IsBloodMoon;
#else
                world.isEventBloodMoon;
#endif
            float healthPct = player.Health / (float)player.GetMaxHealth();

            int enemyAggroCount = 0, enemyAlertCount = 0, enemiesNearby = 0;

            // Weighted combat-threat accumulation. Only summed when configured, so the
            // per-enemy class-name lookup is skipped entirely on the legacy path.
            var threatCfg = CMMConfig.CombatThreat;
            bool useThreatScore = threatCfg != null && threatCfg.IsConfigured;
            float threatScore = 0f;

            // Swimming: in deep enough water to swim (not mere shallow wading). The
            // 8s WarmUp dwell on the context keeps brief dips from interrupting music.
            if (player.IsSwimming()) activeContexts.Add("Swimming");

            if (player.AttachedToEntity is EntityVehicle vehicle)
            {
                string vn = vehicle.GetType().Name.ToLower();
                activeContexts.Add("Vehicle");
                if (vn.Contains("gyrocopter")) activeContexts.Add("Vehicle_Gyrocopter");
                else if (vn.Contains("bicycle")) activeContexts.Add("Vehicle_Bicycle");
                else if (vn.Contains("motorcycle")) activeContexts.Add("Vehicle_Motorcycle");
                else if (vn.Contains("minibike")) activeContexts.Add("Vehicle_Minibike");
                else if (vn.Contains("jeep")) activeContexts.Add("Vehicle_4x4");
            }

            // Trader detection: mirror the game's actual check — player must be inside the
            // prefab's protect bounds AND the trader must be open (daytime).
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
                {
                    string n = traderEntity.EntityName?.ToLower() ?? "";
                    string specific = null;
                    if (n.Contains("jen")) specific = "Trader_Jen";
                    else if (n.Contains("rekt")) specific = "Trader_Rekt";
                    else if (n.Contains("bob")) specific = "Trader_Bob";
                    else if (n.Contains("hugh")) specific = "Trader_Hugh";
                    else if (n.Contains("joel")) specific = "Trader_Joel";
                    activeContexts.Add(specific ?? "Trader");
                }
            }

            // Snapshot the entity list before iterating — the game can add or remove
            // entities (spawns, deaths, chunk unloads) while this loop runs, which
            // causes InvalidOperationException on the live List<Entity>.
            var entitySnapshot = new List<Entity>(world.Entities.list);
            foreach (var entity in entitySnapshot)
            {
                if (entity == null) continue;
                if (entity is EntityEnemy enemy && enemy.IsAlive())
                {
                    // Range-gate ALL combat/tension counts. Previously only enemiesNearby was
                    // distance-checked while the alert/aggro counts spanned every loaded chunk,
                    // so alert enemies in other POIs or across the map drove our combat state
                    // (kept combat from ending, and pushed it to High off non-nearby aggro).
                    if (Vector3.Distance(player.position, enemy.position) >= CMMConfig.EnemyDetectRange) continue;
                    enemiesNearby++;
                    if (enemy.IsAlert)
                    {
                        if (enemy.GetAttackTarget() == player)
                        {
                            enemyAggroCount++;
                            // Combat intensity is driven by who is actually targeting us,
                            // so only player-targeting enemies contribute to the score.
                            if (useThreatScore)
                                threatScore += threatCfg.ScoreEnemy(GetEntityClassName(enemy));
                        }
                        enemyAlertCount++;
                    }
                }
            }

            float now = Time.realtimeSinceStartup;
            bool inCombat = false;

            // The combat tier is held by lastAggroTime — not the instantaneous count/score —
            // so brief target losses (zombies dropping and reacquiring aggro) and mid-fight
            // lulls don't drop the music. Escalation is immediate; de-escalation is not:
            // within one encounter the tier only ever climbs and never steps down mid-fight,
            // resetting only once the cooldown fully expires (the encounter is genuinely over).
            // Between CombatExitDelay and CombatCooldown the tier is held silently — re-aggroing
            // resumes at the latched tier, while a fully fresh fight starts at the bottom tier.
            if (useThreatScore)
            {
                // ── Weighted-threat path ──────────────────────────────────────
                // A configurable score over nearby player-targeting enemies (type ×
                // state) selects a combat tier, replacing the flat enemy-count test.
                int instantTier = threatCfg.TierForScore(threatScore);
                if (instantTier >= 0) lastAggroTime = now;
                float timeSinceAggro = now - lastAggroTime;

                if (instantTier > combatTier) combatTier = instantTier;   // climb only

                if (timeSinceAggro <= CMMConfig.CombatExitDelay && combatTier >= 0)
                {
                    activeContexts.Add(threatCfg.Tiers[combatTier].Context);
                    if (isBloodmoon) activeContexts.Add("Blood_Moon_Combat");
                    inCombat = true;
                }
                else if (timeSinceAggro > CMMConfig.CombatCooldown)
                {
                    combatTier = -1;
                }

                // Panicked bridge → escalate to the Combat_High tier (see note below).
                // NOT the top tier: the game's Panicked check uses a wider 50u/any-player
                // box than our in-range scan, so it routinely fires with nothing actually
                // nearby — slamming to the top tier would over-escalate that generic signal
                // all the way to Combat_Boss. Boss stays reserved for an actual high threat
                // score. Gated by PanickedBridgeAllowed(): only when we have no combat music
                // of our own, so this stays a pure vanilla-fallback bridge (see its note).
                if (!inCombat && PanickedBridgeAllowed() && localPlayerEarly?.ThreatLevel?.Category == ThreatLevelType.Panicked)
                {
                    int bridgeTier = threatCfg.Tiers.FindIndex(t => t.Context == "Combat_High");
                    if (bridgeTier < 0) bridgeTier = threatCfg.Tiers.Count - 1; // no High tier configured
                    if (bridgeTier > combatTier) combatTier = bridgeTier;        // climb only, like the score path
                    lastAggroTime = now;
                    string top = threatCfg.Tiers[combatTier].Context;
                    if (!activeContexts.Contains(top)) activeContexts.Add(top);
                    if (isBloodmoon && !activeContexts.Contains("Blood_Moon_Combat")) activeContexts.Add("Blood_Moon_Combat");
                    inCombat = true;
                }
            }
            else
            {
                // ── Legacy two-threshold count path ───────────────────────────
                if (enemyAggroCount >= CMMConfig.CombatLowEnemyThreshold)
                    lastAggroTime = now;
                float timeSinceAggro = now - lastAggroTime;

                if (enemyAggroCount >= CMMConfig.CombatHighEnemyThreshold)
                    combatState = "High";
                else if (enemyAggroCount >= CMMConfig.CombatLowEnemyThreshold && combatState == "None")
                    combatState = "Low";

                if (timeSinceAggro <= CMMConfig.CombatExitDelay && combatState != "None")
                {
                    activeContexts.Add(combatState == "High" ? "Combat_High" : "Combat_Low");
                    if (isBloodmoon) activeContexts.Add("Blood_Moon_Combat");
                    inCombat = true;
                }
                else if (timeSinceAggro > CMMConfig.CombatCooldown)
                {
                    combatState = "None";
                }

                // Bridge: the game triggers combat music when ThreatLevel is Panicked
                // (≥4 awake enemies in 50-unit box + ≥1 targeting any player). Its
                // detection range and criteria differ from ours, so we mirror it here.
                // Gated by PanickedBridgeAllowed(): only when we have no combat music of
                // our own, so this stays a pure vanilla-fallback bridge (see its note).
                if (!inCombat && PanickedBridgeAllowed() && localPlayerEarly?.ThreatLevel?.Category == ThreatLevelType.Panicked)
                {
                    combatState = "High";
                    lastAggroTime = now;   // treat as live aggro so it sustains through the cooldown tail
                    if (!activeContexts.Contains("Combat_High")) activeContexts.Add("Combat_High");
                    if (isBloodmoon && !activeContexts.Contains("Blood_Moon_Combat")) activeContexts.Add("Blood_Moon_Combat");
                    inCombat = true;
                }
            }

#if CMM_DEBUG_HUD
            // Publish the snapshot for the in-game threat HUD.
            ThreatScoringActive = useThreatScore;
            ThreatScore         = threatScore;
            ThreatTier          = useThreatScore ? combatTier : -1;
            ThreatTierContext   = useThreatScore
                ? (combatTier >= 0 ? threatCfg.Tiers[combatTier].Context : "None")
                : (combatState == "None" ? "None" : combatState == "High" ? "Combat_High" : "Combat_Low");
            InCombat        = inCombat;
            EnemiesNearby   = enemiesNearby;
            EnemyAlertCount = enemyAlertCount;
            EnemyAggroCount = enemyAggroCount;
#endif

            if (inCombat && healthPct < CMMConfig.PlayerHealthDangerThresholdPercent) activeContexts.Add("Low_Health_Panic");

            if (enemiesNearby >= DangerThreshold && enemyAlertCount >= DangerThreshold && !inCombat) activeContexts.Add("Tension");

            if (player is EntityPlayerLocal localPlayer && localPlayer.persistentPlayerData != null)
            {
                bool nearHome = false;
                if (localPlayer.persistentPlayerData.HasBedrollPos)
                {
                    Vector3i bed = localPlayer.persistentPlayerData.BedrollPos;
                    nearHome = Vector3.Distance(player.position, bed) < CMMConfig.HomeDetectRange;
                }
                if (!nearHome)
                {
                    var claims = localPlayer.persistentPlayerData.GetLandProtectionBlocks();
                    if (claims.Count > 0)
                    {
                        Vector3i nearest = claims.OrderBy(c => Vector3.Distance(player.position, c.ToVector3())).First();
                        nearHome = Vector3.Distance(player.position, nearest.ToVector3()) < CMMConfig.HomeDetectRange;
                    }
                }
                if (nearHome) activeContexts.Add(isDay ? "Home" : "Home_Night");
            }

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
                int totalVolumes   = prefab.sleeperVolumes.Count;
                int clearedVolumes = prefab.sleeperVolumes.Count(v => v.wasCleared);
                int excludedVolumes = prefab.sleeperVolumes.Count(v => v.isQuestExclude);
                int enemiesRemainingInPoi = prefab.sleeperVolumes.Count(v => !v.wasCleared && !v.isQuestExclude);
                int tier = prefab.prefab?.DifficultyTier ?? 0;
                string tierSuffix = (tier >= 0 && tier <= 5) ? "_T" + tier : "";

                CMMConfig.D($"[CMM POI] name='{currentPoI}' tier={tier} tierSuffix='{tierSuffix}' " +
                            $"volumes={totalVolumes} cleared={clearedVolumes} excluded={excludedVolumes} remaining={enemiesRemainingInPoi} " +
                            $"wasInfested={wasInfested} lastPoI={lastPoI}");

                // Only tiers 0–5 have dedicated contexts; anything else falls back to the
                // tier-less POI / POI_Infested base context.
                if (enemiesRemainingInPoi > 0)
                {
                    wasInfested = true;
                    activeContexts.Add("POI_Infested" + tierSuffix);
                }
                else
                {
                    if (wasInfested && currentPoI == lastPoI) activeContexts.Add("POI_Cleared");
                    wasInfested = false;
                    activeContexts.Add("POI" + tierSuffix);
                }
                lastPoI = currentPoI;
            }
            else
            {
                wasInfested = false;
            }

            string biomeName = world.GetBiome((int)player.position.x, (int)player.position.z)?.m_sBiomeName;
            string biomeBase;
            switch (biomeName)
            {
                case "pine_forest": biomeBase = "Ambient_Forest"; break;
                case "desert": biomeBase = "Ambient_Desert"; break;
                case "burnt_forest": biomeBase = "Ambient_Burnt_Forest"; break;
                case "snow": biomeBase = "Ambient_Snow"; break;
                case "wasteland": biomeBase = "Ambient_Wasteland"; break;
                default: biomeBase = "Ambient"; break;
            }
            // During the day the biome base folder is used directly (it holds the
            // everyday day+night tracks). At night we prefer the biome's night-exclusive
            // context, but ALSO push the biome's day context (at lower priority) so a
            // biome with no night music — and no generic Ambient_Night music — falls back
            // to its OWN day theme before the generic Ambient. The night context falls
            // back to Ambient_Night, which falls back to the Stack, where this day context
            // is then found (the night context itself is skipped by the re-entry guard).
            if (isDay)
            {
                activeContexts.Add(biomeBase);
            }
            else
            {
                activeContexts.Add(biomeBase + "_Night");
                activeContexts.Add(biomeBase);
            }
            //sw.Stop();
            //Log.Out($"[CMM] ResolvedContext took {sw.Elapsed.TotalMilliseconds:F3} ms");

            return activeContexts;
        }
    }
}
