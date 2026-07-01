using System.Collections.Generic;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // GAME STATE
    // ─────────────────────────────────────────────────────────────────────────
    // A version-agnostic snapshot of everything the context decision logic needs,
    // captured once per scan tick by ContextResolver.BuildSnapshot(). All of the
    // game-version forks (#if GV_A20 / GV_A21 / GV_V3) live in BuildSnapshot; from
    // here on the data is plain primitives, so the decision layer — and the Lua
    // script that will eventually own it — never touches a raw game type and stays
    // identical across A20/A21/3.0.
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
    }
}
