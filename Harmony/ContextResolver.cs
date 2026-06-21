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
        static readonly float EnemyDetectRange = 30f;
        static readonly float HomeDetectRange = 70f;
        static readonly float PlayerHealthDangerThresholdPercent = 0.25f;
        static readonly int DangerThreshold = 1;
        static readonly int CriticalThreshold = 4;
        // CombatExitDelay: with no nearby aggro for this long, stop playing combat music.
        // CombatCooldown: how long the encounter's tier stays REMEMBERED after that — re-aggro
        // within this window resumes the previous (latched) intensity instead of restarting at
        // Low; past it the encounter is forgotten and the next fight starts fresh at Low. The
        // tier latches upward only (None→Low→High) and never steps back down mid-encounter.
        static readonly float CombatExitDelay = 2f;
        static readonly float CombatCooldown = 10f;

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
            bool isBloodmoon = world.isEventBloodMoon;
            float healthPct = player.Health / (float)player.GetMaxHealth();

            int enemyAggroCount = 0, enemyAlertCount = 0, enemiesNearby = 0;

            // Weighted combat-threat accumulation. Only summed when configured, so the
            // per-enemy class-name lookup is skipped entirely on the legacy path.
            var threatCfg = CMMConfig.CombatThreat;
            bool useThreatScore = threatCfg != null && threatCfg.IsConfigured;
            float threatScore = 0f;

            if (isBloodmoon) activeContexts.Add("Blood_Moon");

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
                EntityTrader traderEntity = traderArea.GetTrader();
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
                    if (Vector3.Distance(player.position, enemy.position) >= EnemyDetectRange) continue;
                    enemiesNearby++;
                    if (enemy.isAlert)
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

                if (timeSinceAggro <= CombatExitDelay && combatTier >= 0)
                {
                    activeContexts.Add(threatCfg.Tiers[combatTier].Context);
                    if (isBloodmoon) activeContexts.Add("Blood_Moon_Combat");
                    inCombat = true;
                }
                else if (timeSinceAggro > CombatCooldown)
                {
                    combatTier = -1;
                }

                // Panicked bridge → mirror the legacy path and escalate to the
                // Combat_High tier (see note below). NOT the top tier: the game's
                // Panicked check uses a wider 50u/any-player box than our in-range scan,
                // so it routinely fires with nothing actually nearby — slamming to the
                // top tier would over-escalate that generic signal all the way to
                // Combat_Boss. Boss stays reserved for an actual high threat score.
                if (!inCombat && localPlayerEarly?.ThreatLevel?.Category == ThreatLevelType.Panicked)
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

                if (timeSinceAggro <= CombatExitDelay && combatState != "None")
                {
                    activeContexts.Add(combatState == "High" ? "Combat_High" : "Combat_Low");
                    if (isBloodmoon) activeContexts.Add("Blood_Moon_Combat");
                    inCombat = true;
                }
                else if (timeSinceAggro > CombatCooldown)
                {
                    combatState = "None";
                }

                // Bridge: the game triggers combat music when ThreatLevel is Panicked
                // (≥4 awake enemies in 50-unit box + ≥1 targeting any player). Its
                // detection range and criteria differ from ours, so we mirror it here.
                if (!inCombat && localPlayerEarly?.ThreatLevel?.Category == ThreatLevelType.Panicked)
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

            if (inCombat && healthPct < PlayerHealthDangerThresholdPercent) activeContexts.Add("Low_Health_Panic");

            if (enemiesNearby >= CriticalThreshold && enemyAlertCount >= CriticalThreshold && !inCombat) activeContexts.Add("Tension_High");
            else if (enemiesNearby >= DangerThreshold && enemyAlertCount >= DangerThreshold && !inCombat) activeContexts.Add("Tension_Low");

            if (player is EntityPlayerLocal localPlayer && localPlayer.persistentPlayerData != null)
            {
                bool nearHome = false;
                if (localPlayer.persistentPlayerData.HasBedrollPos)
                {
                    Vector3i bed = localPlayer.persistentPlayerData.BedrollPos;
                    nearHome = Vector3.Distance(player.position, bed) < HomeDetectRange;
                }
                if (!nearHome)
                {
                    var claims = localPlayer.persistentPlayerData.GetLandProtectionBlocks();
                    if (claims.Count > 0)
                    {
                        Vector3i nearest = claims.OrderBy(c => Vector3.Distance(player.position, c.ToVector3())).First();
                        nearHome = Vector3.Distance(player.position, nearest.ToVector3()) < HomeDetectRange;
                    }
                }
                if (nearHome) activeContexts.Add(isDay ? "Home" : "Home_Night");
            }

            PrefabInstance prefab = world?.GetPOIAtPosition(player.position);
            if (prefab != null)
            {
                string currentPoI = prefab.prefab.PrefabName;
                // Mirror the clear quest: ignore volumes flagged isQuestExclude. Those hold
                // passive/decorative spawns (caged animals, ambient critters) that never get
                // wasCleared through combat, so counting them kept the total stuck above zero
                // even after the POI was fully cleared and the quest completed.
                int enemiesRemainingInPoi = prefab.sleeperVolumes.Count(v => !v.wasCleared && !v.isQuestExclude);
                // CMM.CMMConfig.D($"[CMM] Sleeper volumes remaining: {enemiesRemainingInPoi}");
                int tier = prefab.prefab.DifficultyTier;
                // Only tiers 0–5 have dedicated contexts; anything else falls back to the
                // tier-less POI / POI_Infested base context.
                string tierSuffix = (tier >= 0 && tier <= 5) ? "_T" + tier : "";
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
            // everyday day+night tracks). At night we prefer a night-exclusive
            // context, which falls back to the biome base when it has no files.
            activeContexts.Add(isDay ? biomeBase : biomeBase + "_Night");
            //sw.Stop();
            //Log.Out($"[CMM] ResolvedContext took {sw.Elapsed.TotalMilliseconds:F3} ms");

            return activeContexts;
        }
    }
}
