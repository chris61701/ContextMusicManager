using System.Collections.Generic;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // GAME STATE
    // ─────────────────────────────────────────────────────────────────────────
    // A version-agnostic snapshot of everything the context decision logic needs,
    // captured once per scan tick by ContextResolver.BuildSnapshot(). All cross-version
    // game-API divergence is absorbed by BuildSnapshot's reflection bridges (a single
    // 3.0-compiled binary runs on A20/A21/2.6/3.0); from here on the data is plain
    // primitives, so the decision layer — and the Lua script that will eventually own
    // it — never touches a raw game type and stays identical across all versions.
    //
    // The instance is reused across ticks (ResolveContext runs single-threaded on
    // the main thread, never re-entrant), so neither it nor its Enemies list/items
    // allocate after warmup. Treat it as live only for the duration of one resolve.

    // One nearby enemy, already range-filtered to CMMConfig.EnemyDetectRange.
    // IsTargetingPlayer implies IsAlert (an enemy can only target while alert).
    public sealed class EnemyInfo
    {
        public string ClassName;          // entity config class name (e.g. "zombieBoeFeral")
        public float  Distance;           // game units from the player
        public bool   IsAlert;
        public bool   IsTargetingPlayer;
    }

    public sealed class GameState
    {
        public float Now;                 // Time.realtimeSinceStartup — the script never reads engine time
        public bool  IsDay;
        public bool  IsBloodMoon;
        public float HealthPct;           // 0..1
        public bool  IsSwimming;
        public bool  IsPanicked;          // normalized ThreatLevel == Panicked (vanilla combat bridge)
        public bool  IsCrouching;         // sneaking; drives a stealth ambient layer

        // ── Time of day ────────────────────────────────────────────────────
        // Derived from world.GetWorldTime(); lets the script vary music by dawn/
        // dusk/midnight instead of only the coarse IsDay flag.
        public float Hour;                // 0..24 (fractional, minute-resolved)
        public int   Day;                 // in-game day number (0-based, as the game counts)

        // ── Weather ────────────────────────────────────────────────────────
        // All 0..1 except WindSpeed/Temperature. Rain/snow getters were renamed
        // across versions (…Percent on 3.0, …Value on A20/A21) — bridged in the
        // resolver. The "is it a storm" decision (rain+wind thresholds) stays in
        // the Lua script so it's user-tunable.
        public float Rainfall;            // 0..1
        public float Snowfall;            // 0..1
        public float WindSpeed;           // raw game units
        public float CloudThickness;      // 0..1 (overcast)
        public float Temperature;         // degrees (game's Fahrenheit scale)

        // ── Player vitals / motion ─────────────────────────────────────────
        public float StaminaPct;          // 0..1
        public float FoodPct;             // 0..1
        public float WaterPct;            // 0..1
        // Blocks the player has fallen in the current fall (0 when grounded or
        // rising). A big value = a long plummet — cue vertigo/impact beds.
        public float FallDistance;

        public string VehicleType;        // lowercased vehicle type name, or null when not riding
        public string TraderName;         // lowercased trader name, or null unless inside an OPEN trader
        public string Biome;              // raw biome name; "" = unknown name; null = chunk not streamed in

        public bool   NearHome;

        // Living party members / ACL friends within CMMConfig.PlayerDetectRange.
        // Multiplayer only (0 in single-player). Drives combat-intensity scaling —
        // fighting alongside a squad escalates the combat tier (see context.lua).
        public int    AlliesNearby;

        // Living NON-friendly players (neither party member nor ACL friend — a
        // "stranger" in the game's PvP sense) within CMMConfig.PlayerDetectRange.
        // Multiplayer only (0 in single-player). Drives the Online_Stranger context.
        // Relationship-only: whether PvP rules actually permit harm between you is a
        // separate server setting (PlayerKillingMode) and is not considered here.
        public int    StrangersNearby;

        public bool   InPoi;
        public string PoiName;
        // Per-INSTANCE identity of the current POI (PrefabInstance.name = "<location>.<id>",
        // unique per placed prefab). PoiName is only the prefab TYPE (its file name), shared
        // by every copy of a POI design in the world, so the script must key same-POI
        // continuity (the infested→cleared guard) on PoiId — NOT PoiName. Otherwise fleeing
        // an infested POI straight into another copy of the same type reads as one continuous
        // POI and misfires POI_Cleared. Null when not in a POI.
        public string PoiId;
        public int    PoiTier;
        public int    PoiEnemiesRemaining;
        // True for one tick when the quest system reports this POI's sleepers cleared
        // (the only authoritative clear signal a multiplayer client receives). Drives
        // the POI_Cleared stinger explicitly.
        public bool   PoiQuestCleared;
        // When true, POI_Cleared must come ONLY from PoiQuestCleared — the script must
        // NOT derive it from the infested→0 entity-count transition. Set on the MP
        // client fallback (where the count bounces room-to-room and can't tell a real
        // clear from an un-triggered far room); false on the accurate single-player /
        // host path, which keeps deriving POI_Cleared from real sleeper-volume state.
        public bool   PoiClearFromQuestOnly;

        // Reused buffer; cleared and refilled each BuildSnapshot. Holds pooled
        // EnemyInfo instances (see ContextResolver), so iteration is alloc-free.
        public readonly List<EnemyInfo> Enemies = new List<EnemyInfo>();

        // Active status-effect (buff) names, RAW as the game stores them
        // (e.g. "buffInjuryBleeding", "buffOnFire"). Reused buffer, refilled each
        // tick; the strings are the buff objects' own (no per-tick allocation).
        // Match case-insensitively via cmm.hasBuff — they are NOT lowercased here
        // precisely so the poll allocates nothing.
        public readonly List<string> Buffs = new List<string>();
    }
}
