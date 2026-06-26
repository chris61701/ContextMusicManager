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

        public bool   InPoi;
        public string PoiName;
        public int    PoiTier;
        public int    PoiEnemiesRemaining;

        // Reused buffer; cleared and refilled each BuildSnapshot. Holds pooled
        // EnemyInfo instances (see ContextResolver), so iteration is alloc-free.
        public readonly List<EnemyInfo> Enemies = new List<EnemyInfo>();
    }
}
