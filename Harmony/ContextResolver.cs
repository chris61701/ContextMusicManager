using System.Collections.Generic;
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
        // Biome token latched when combat (re)engages (e.g. "Desert"), so the biome-specific
        // combat music stays the one the player is fighting in even if they cross into another
        // biome while combat is CONTINUOUSLY going. Unlike the combat TIER (held/resumed through
        // the longer CombatCooldown), the biome deliberately does NOT honor that cooldown: it
        // re-locks each time the active-combat window (CombatExitDelay) lapses and combat then
        // re-engages. Leave a fight in the wasteland, re-enter in the desert, and the track
        // switches to desert while the tier still resumes at the held High/Boss. Cleared when
        // the encounter fully ends (cooldown expires). "" = generic, no biome variant.
        static string combatBiomeToken = "";
        static float lastAggroTime = 0f;
        static bool wasInfested = false;
        static string lastPoI = "None";

        // Reused snapshot buffer for the per-tick entity scan. ResolveContext runs once
        // per scan tick on the main thread (never re-entrant), so a single shared buffer
        // we Clear()+AddRange() into avoids allocating a fresh List<Entity> (and its
        // backing array) every tick while keeping the mid-scan-mutation safety.
        static readonly List<Entity> _entityScratch = new List<Entity>();

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

        public static void resetValues() { combatState = "None"; combatTier = -1; combatBiomeToken = ""; }

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
        // VanillaIfPanicked) hand off to the game's combat track. If we DO have combat music
        // (Combat_High or the base Combat_Low — the combat/ root folder), we trust our own
        // threat detection instead and ignore the Panicked signal entirely, so its loose box
        // can't spuriously trigger our tracks when our scan found nothing in range.
        static bool PanickedBridgeAllowed() =>
            !ContextMusicManager.ContextHasFiles("Combat_High") &&
            !ContextMusicManager.ContextHasFiles("Combat_Low");

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
            // A20: IsSwimming() requires inWaterPercent > 0.7f (head mostly under),
            // so floating at the surface (head above water, ~0.4–0.65 range) is missed.
            // inWaterPercent > 0.4f covers waist-deep+; the game's own speed-reduction
            // starts at 0.3f, so 0.4f safely clears shallow ankle/knee wading.
#if GV_A20
            if (player.isSwimming || player.inWaterPercent > 0.4f) activeContexts.Add("Swimming");
#else
            if (player.IsSwimming()) activeContexts.Add("Swimming");
#endif

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
            _entityScratch.Clear();
            _entityScratch.AddRange(world.Entities.list);
            foreach (var entity in _entityScratch)
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

            // Resolve the biome ONCE up front. Both the combat block (biome-specific
            // combat music) and the ambient block at the end of this method consume it,
            // so a single lookup feeds both. GetBiome returns null when the chunk at the
            // player's position isn't streamed in yet (e.g. immediately after a debug
            // teleport in A20); biomeToken stays "" in that case, so combat falls back to
            // the plain tier and the ambient block is skipped entirely (see its note).
            var biome = world.GetBiome((int)player.position.x, (int)player.position.z);
            string biomeToken = "";
            if (biome != null)
            {
                switch (biome.m_sBiomeName)
                {
                    case "pine_forest":  biomeToken = "Forest"; break;
                    case "desert":       biomeToken = "Desert"; break;
                    case "burnt_forest": biomeToken = "Burnt_Forest"; break;
                    case "snow":         biomeToken = "Snow"; break;
                    case "wasteland":    biomeToken = "Wasteland"; break;
                    // Unknown biome → no token; combat uses the plain tier and ambient
                    // falls through to the generic "Ambient" base below.
                }
            }

            // Adds a combat tier context plus, when the encounter's biome is known, its
            // biome-specific variant (e.g. Combat_High + Combat_High_Forest). The variant
            // outranks its base tier (priority band 59–62, see contexts.xml) so biome combat
            // music wins when it has files, and falls back to the plain tier — today's behavior
            // — when it doesn't. Uses combatBiomeToken (latched at encounter start), NOT the
            // live biomeToken, so crossing into another biome mid-fight doesn't swap the track.
            // Only one combat tier is active per tick, so no cross-tier priority inversion.
            void AddCombatTier(string tierContext)
            {
                if (!activeContexts.Contains(tierContext)) activeContexts.Add(tierContext);
                if (combatBiomeToken.Length > 0)
                {
                    string biomeTier = tierContext + "_" + combatBiomeToken;
                    if (!activeContexts.Contains(biomeTier)) activeContexts.Add(biomeTier);
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
                // Whether the ACTIVE-combat window had lapsed as of the previous aggro (the
                // music had stopped) — computed before lastAggroTime is refreshed. Drives the
                // biome RE-capture below, which intentionally ignores the longer tier cooldown.
                bool combatHadLapsed = (now - lastAggroTime) > CMMConfig.CombatExitDelay;
                if (instantTier >= 0)
                {
                    lastAggroTime = now;
                    // (Re)latch the biome whenever combat (re)engages: a brand-new encounter
                    // (combatTier < 0) OR a re-engagement after the active-combat window lapsed
                    // (combatHadLapsed). So leaving combat in the wasteland and re-entering in
                    // the desert switches Combat_*_Wasteland → Combat_*_Desert, even though the
                    // tier itself still resumes at the held High/Boss. The else-if back-fills the
                    // token when the biome chunk wasn't streamed in yet on the opening tick.
                    if (combatTier < 0 || combatHadLapsed) combatBiomeToken = biomeToken;
                    else if (combatBiomeToken.Length == 0) combatBiomeToken = biomeToken;
                }
                float timeSinceAggro = now - lastAggroTime;

                if (instantTier > combatTier) combatTier = instantTier;   // climb only

                if (timeSinceAggro <= CMMConfig.CombatExitDelay && combatTier >= 0)
                {
                    AddCombatTier(threatCfg.Tiers[combatTier].Context);
                    if (isBloodmoon) activeContexts.Add("Blood_Moon_Combat");
                    inCombat = true;
                }
                else if (timeSinceAggro > CMMConfig.CombatCooldown)
                {
                    combatTier = -1;
                    combatBiomeToken = "";   // encounter over — forget the latched biome
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
                    // No tier literally named Combat_High: fall back to the second-from-top
                    // tier, never the top one. Slamming the loose Panicked signal to the top
                    // (e.g. Combat_Boss) is exactly what the note above forbids.
                    if (bridgeTier < 0) bridgeTier = threatCfg.Tiers.Count - 2;
                    if (bridgeTier < 0) bridgeTier = 0;   // single-tier config
                    if (bridgeTier > combatTier) combatTier = bridgeTier;        // climb only, like the score path
                    lastAggroTime = now;
                    AddCombatTier(threatCfg.Tiers[combatTier].Context);
                    if (isBloodmoon && !activeContexts.Contains("Blood_Moon_Combat")) activeContexts.Add("Blood_Moon_Combat");
                    inCombat = true;
                }
            }
            else
            {
                // ── Legacy two-threshold count path ───────────────────────────
                // See the weighted path: re-capture the biome on combat re-engagement once
                // the active-combat window has lapsed, independent of the tier cooldown.
                bool combatHadLapsed = (now - lastAggroTime) > CMMConfig.CombatExitDelay;
                if (enemyAggroCount >= CMMConfig.CombatLowEnemyThreshold)
                {
                    // (Re)latch the biome on a fresh encounter (combatState "None") OR a
                    // re-engagement after the active-combat window lapsed (combatHadLapsed),
                    // so re-entering combat in a new biome switches the biome track even while
                    // the tier is still held. The else-if back-fills if the biome wasn't loaded
                    // yet — same hold-for-the-fight behavior as the weighted path above.
                    if (combatState == "None" || combatHadLapsed) combatBiomeToken = biomeToken;
                    else if (combatBiomeToken.Length == 0) combatBiomeToken = biomeToken;
                    lastAggroTime = now;
                }
                float timeSinceAggro = now - lastAggroTime;

                if (enemyAggroCount >= CMMConfig.CombatHighEnemyThreshold)
                    combatState = "High";
                else if (enemyAggroCount >= CMMConfig.CombatLowEnemyThreshold && combatState == "None")
                    combatState = "Low";

                if (timeSinceAggro <= CMMConfig.CombatExitDelay && combatState != "None")
                {
                    AddCombatTier(combatState == "High" ? "Combat_High" : "Combat_Low");
                    if (isBloodmoon) activeContexts.Add("Blood_Moon_Combat");
                    inCombat = true;
                }
                else if (timeSinceAggro > CMMConfig.CombatCooldown)
                {
                    combatState = "None";
                    combatBiomeToken = "";   // encounter over — forget the latched biome
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
                    AddCombatTier("Combat_High");
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

            if (player is EntityPlayerLocal localPlayer)
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
                    if (nearHome) activeContexts.Add(isDay ? "Home" : "Home_Night");
                }
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

            // Guard on the biome object itself: GetBiome() (resolved up front, above the
            // combat block) returns null when the chunk at the player's position isn't
            // loaded yet (e.g. immediately after a debug teleport in A20 before destination
            // chunks stream in). Falling through to default:"Ambient" in that case would lock
            // in the wrong generic Ambient_Night context for as long as chunks stay unloaded,
            // because the scan loop keeps refreshing it. Skipping the ambient block entirely
            // when biome is null lets the current music continue until the next scan tick when
            // chunks are ready.
            if (biome != null)
            {
                // biomeToken is "" for an unknown biome name, which yields the generic
                // "Ambient" base — the same fallback the old default: case produced.
                string biomeBase = biomeToken.Length > 0 ? "Ambient_" + biomeToken : "Ambient";
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
            }
            //sw.Stop();
            //Log.Out($"[CMM] ResolvedContext took {sw.Elapsed.TotalMilliseconds:F3} ms");

            return activeContexts;
        }
    }
}
