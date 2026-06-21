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
        static readonly float CombatCooldown = 10f;

        static string combatState = "None";
        static float lastAggroTime = 0f;
        static bool wasInfested = false;
        static string lastPoI = "None";

        public static void resetValues() { combatState = "None"; }

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
                    if (Vector3.Distance(player.position, enemy.position) < EnemyDetectRange) enemiesNearby++;
                    if (enemy.isAlert)
                    {
                        if (enemy.GetAttackTarget() == player) { lastAggroTime = Time.realtimeSinceStartup; enemyAggroCount++; }
                        enemyAlertCount++;
                    }
                }
            }

            float timeSinceAggro = Time.realtimeSinceStartup - lastAggroTime;
            bool inCombat = false;

            if (enemyAggroCount >= CMMConfig.CombatHighEnemyThreshold)
            {
                combatState = "High";
                activeContexts.Add("Combat_High");
                if (isBloodmoon) activeContexts.Add("Blood_Moon_Combat");
                inCombat = true;
            }
            else if (enemyAggroCount >= CMMConfig.CombatLowEnemyThreshold)
            {
                if (combatState != "High") combatState = "Low";
                activeContexts.Add(combatState == "High" ? "Combat_High" : "Combat_Low");
                if (isBloodmoon) activeContexts.Add("Blood_Moon_Combat");
                inCombat = true;
            }
            else if (timeSinceAggro <= CombatCooldown && enemyAlertCount > 0)
            {
                activeContexts.Add(combatState == "High" ? "Combat_High" : "Combat_Low");
                inCombat = true;
            }
            else if (timeSinceAggro > CombatCooldown && enemyAlertCount == 0)
            {
                combatState = "None";
            }

            // Bridge: the game triggers combat music when ThreatLevel is Panicked
            // (≥4 awake enemies in 50-unit box + ≥1 targeting any player).
            // Its detection range and criteria differ from ours, so we mirror it here.
            if (!inCombat && localPlayerEarly?.ThreatLevel?.Category == ThreatLevelType.Panicked)
            {
                combatState = "High";
                if (!activeContexts.Contains("Combat_High")) activeContexts.Add("Combat_High");
                if (isBloodmoon && !activeContexts.Contains("Blood_Moon_Combat")) activeContexts.Add("Blood_Moon_Combat");
                inCombat = true;
            }

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
                int enemiesRemainingInPoi = prefab.sleeperVolumes.Count(v => !v.wasCleared);
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
