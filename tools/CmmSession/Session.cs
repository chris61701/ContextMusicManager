using System;

namespace CMM
{
    // ─────────────────────────────────────────────────────────────────────────
    // SessionGenerator — a deterministic, seeded synthetic gameplay timeline that
    // follows the real CMM gameplay loop, producing one GameState per scan tick
    // (the same contract ContextResolver.BuildSnapshot() fills in-game) without a
    // game world. The GameState + enemy list/items are REUSED across ticks exactly
    // as the real resolver pools them, so the only per-tick managed allocation is
    // inside the linked ScriptHost marshalling — the GC suspect the bench measures.
    //
    // THE LOOP (one iteration ≈ 8–20 min, repeated for the whole session):
    //   Home          ~1 min at base (nearHome).
    //   →Trader       ~20 s travelling a biome (on foot or a vehicle).
    //   Trader        20–30 s inside an open trader (pick up a quest).
    //   →POI          a few min through a biome (day/night), combat a few times.
    //   POI           2–10 min, infested: combat every 2–20 s, escalating to a
    //                 Combat_High/Boss wave at the end, then it clears → POI_Cleared.
    //   →Home         travel back through the biome, a few more fights.
    //
    // A day/night cycle runs throughout (blood-moon flag on every 7th night, so a
    // fight that lands on one emits Blood_Moon_Combat). Vehicle use and biome are
    // re-rolled per travel leg.
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class SessionGenerator
    {
        private readonly Random _rng;
        private readonly float _tickSeconds;
        private readonly float _gameHoursPerTick;

        // Reused snapshot + pooled enemies (no per-tick allocation here).
        private readonly GameState _gs = new GameState();
        private readonly EnemyInfo[] _enemyPool;

        // World clock.
        private double _gameHour;       // continuous in-game hours since world start
        private int _worldDay = 1;

        // ── Phase state machine ──
        private enum Phase { Home, TravelToTrader, Trader, TravelToPoi, Poi, TravelHome }
        private Phase _phase;
        private int _phaseTicks;        // total ticks this phase lasts
        private int _phaseElapsed;      // ticks elapsed in this phase

        // Per-leg / per-location attributes.
        private string _biome = "pine_forest";
        private string _vehicle;        // travel legs only; null = on foot
        private string _trader;
        private bool _swimming;
        private float _health = 1f;

        // POI state.
        private int _poiTier;
        private string _poiName;
        private int _poiInfestedTicks;  // infested portion of the POI phase
        private bool _poiBossDone;

        // Combat overlay. Each encounter runs Approach (alert, NOT targeting → Tension) →
        // Engaged (targeting → Combat) → Tail (disengaged → Tension/Combat-latch), so the
        // realistic combat↔tension↔combat↔poi-infested churn is exercised, not just on/off.
        private enum Tier { None, Low, High, Boss }
        private enum CombatMode { None, Approach, Engaged, Tail }
        private CombatMode _mode = CombatMode.None;
        private int _modeTicksLeft;
        private int _engagedDur, _tailDur;
        private int _nextEncounterTicks;
        private Tier _encounterTier;
        private int _encounterSize;
        // Active combat profile for the current phase (gaps/durations in ticks).
        private int _gapMin, _gapMax, _durMin, _durMax;
        private bool _combatEnabled;

        private static readonly string[] Biomes = { "pine_forest", "desert", "burnt_forest", "snow", "wasteland" };
        private static readonly string[] Vehicles = { "gyrocopter", "bicycle", "motorcycle", "minibike", "jeep" };
        private static readonly string[] Traders = { "jen", "rekt", "bob", "hugh", "joel" };
        private static readonly string[] PoiNames = { "house_01", "store_05", "skyscraper_02", "factory_03", "church_01" };
        // Class names chosen to land on the Lua weight/state tables (walker≈1, dog 1.4,
        // wight/cop 3.5, bear 4, demolition 8; feral ×1.5, radiated ×2).
        private static readonly string[] WeakEnemies  = { "zombieStdWalk", "zombieBoe", "animalZombieDog", "zombieSteveCrawler" };
        private static readonly string[] MidEnemies   = { "zombieFeralWight", "zombieCop", "animalDireWolf", "zombieBoeFeral" };
        private static readonly string[] HeavyEnemies = { "zombieDemolition", "zombieRadiatedCop", "zombieFatCopRadiated", "zombieBoeRadiated" };

        public SessionGenerator(int seed, float tickSeconds, float gameDayRealMinutes)
        {
            _rng = new Random(seed);
            _tickSeconds = tickSeconds;
            _gameHoursPerTick = (float)(tickSeconds / (gameDayRealMinutes * 60.0) * 24.0);
            _enemyPool = new EnemyInfo[64];
            for (int i = 0; i < _enemyPool.Length; i++) _enemyPool[i] = new EnemyInfo();
            _gameHour = 8.0; // start at 08:00, day 1
            EnterPhase(Phase.Home);
        }

        public int WorldDay => _worldDay;
        private bool IsDay => (_gameHour % 24.0) >= 4.0 && (_gameHour % 24.0) < 22.0;

        // Advances the world one tick and returns the freshly-filled (reused) snapshot.
        public GameState Next()
        {
            // ── Clock ──
            double prevHour = _gameHour;
            _gameHour += _gameHoursPerTick;
            if ((int)(_gameHour / 24.0) != (int)(prevHour / 24.0)) _worldDay++;
            bool isDay = IsDay;
            bool bloodMoonNight = (_worldDay % 7 == 0) && !isDay;

            // ── Phase advance ──
            if (++_phaseElapsed > _phaseTicks) AdvancePhase();

            // ── Combat overlay ──
            TickCombat();

            FillSnapshot(isDay, bloodMoonNight);
            return _gs;
        }

        // ── Phase transitions (the fixed loop order) ──
        private void AdvancePhase()
        {
            switch (_phase)
            {
                case Phase.Home:           EnterPhase(Phase.TravelToTrader); break;
                case Phase.TravelToTrader: EnterPhase(Phase.Trader);         break;
                case Phase.Trader:         EnterPhase(Phase.TravelToPoi);    break;
                case Phase.TravelToPoi:    EnterPhase(Phase.Poi);            break;
                case Phase.Poi:            EnterPhase(Phase.TravelHome);      break;
                case Phase.TravelHome:     EnterPhase(Phase.Home);           break;
            }
        }

        private void EnterPhase(Phase p)
        {
            _phase = p;
            _phaseElapsed = 0;
            _vehicle = null;
            _swimming = false;
            _trader = null;
            _mode = CombatMode.None;
            _combatEnabled = false;

            switch (p)
            {
                case Phase.Home:
                    _phaseTicks = Secs(50f + Rand(20f));            // ~1 min at base
                    break;

                case Phase.TravelToTrader:
                    _phaseTicks = Secs(15f + Rand(12f));           // ~20 s leg
                    StartTravelLeg(combat: false);                 // short hop, no fights
                    break;

                case Phase.Trader:
                    _phaseTicks = Secs(20f + Rand(10f));           // 20–30 s, quest pickup
                    _trader = Traders[_rng.Next(Traders.Length)];
                    break;

                case Phase.TravelToPoi:
                    _phaseTicks = Secs(120f + Rand(120f));         // "a few minutes"
                    StartTravelLeg(combat: true);                  // a few fights en route
                    SetCombatProfile(gapSec: (20, 55), durSec: (4, 12));
                    break;

                case Phase.Poi:
                    _poiTier = _rng.Next(0, 6);
                    _poiName = PoiNames[_rng.Next(PoiNames.Length)];
                    _poiBossDone = false;
                    _phaseTicks = Secs(120f + Rand(480f));         // 2–10 min quest
                    _poiInfestedTicks = _phaseTicks - Secs(8f);    // last ~8 s = cleared tail
                    SetCombatProfile(gapSec: (2, 20), durSec: (4, 12)); // combat every 2–20 s
                    break;

                case Phase.TravelHome:
                    _phaseTicks = Secs(90f + Rand(120f));          // head home through the biome
                    StartTravelLeg(combat: true);                  // a few more enemies
                    SetCombatProfile(gapSec: (20, 55), durSec: (4, 12));
                    break;
            }
        }

        // Re-roll biome + vehicle for a travel leg; optionally enable combat.
        private void StartTravelLeg(bool combat)
        {
            _biome = Biomes[_rng.Next(Biomes.Length)];
            _vehicle = _rng.Next(100) < 50 ? Vehicles[_rng.Next(Vehicles.Length)] : null; // may or may not drive
            _swimming = _vehicle == null && _rng.Next(100) < 6;
            _combatEnabled = combat;
        }

        private void SetCombatProfile((int min, int max) gapSec, (int min, int max) durSec)
        {
            _combatEnabled = true;
            _gapMin = Secs(gapSec.min); _gapMax = Secs(gapSec.max);
            _durMin = Secs(durSec.min); _durMax = Secs(durSec.max);
            _nextEncounterTicks = RandRange(_gapMin, _gapMax);
        }

        // ── Combat scheduling ──
        private void TickCombat()
        {
            // Walk the approach→engaged→tail state machine of an active encounter.
            if (_mode != CombatMode.None)
            {
                if (--_modeTicksLeft > 0) return;
                switch (_mode)
                {
                    case CombatMode.Approach: _mode = CombatMode.Engaged; _modeTicksLeft = _engagedDur; return;
                    case CombatMode.Engaged:  _mode = CombatMode.Tail;    _modeTicksLeft = _tailDur;    return;
                    default: // Tail done → idle until the next encounter is scheduled.
                        _mode = CombatMode.None;
                        _nextEncounterTicks = RandRange(_gapMin <= 0 ? Secs(20f) : _gapMin, _gapMax <= 0 ? Secs(55f) : _gapMax);
                        return;
                }
            }

            // Force the climactic Combat_High/Boss wave in the last stretch of an infested POI.
            if (_phase == Phase.Poi && !_poiBossDone && _phaseElapsed >= _poiInfestedTicks - Secs(16f)
                && _phaseElapsed < _poiInfestedTicks)
            {
                BeginEncounter(Tier.Boss);
                _poiBossDone = true;
                return;
            }

            if (!_combatEnabled || (_phase == Phase.Poi && _phaseElapsed >= _poiInfestedTicks)) return;

            if (--_nextEncounterTicks <= 0)
            {
                // POI fights skew harder than open-world travel fights.
                Tier tier = _phase == Phase.Poi
                    ? (_rng.Next(100) < 45 ? Tier.High : Tier.Low)
                    : (_rng.Next(100) < 20 ? Tier.High : Tier.Low);
                BeginEncounter(tier);
            }
        }

        private void BeginEncounter(Tier tier)
        {
            _encounterTier = tier;
            _encounterSize = tier == Tier.Boss ? _rng.Next(9, 15)
                           : tier == Tier.High ? _rng.Next(4, 8)
                           : _rng.Next(1, 4);
            _engagedDur = tier == Tier.Boss ? RandRange(Secs(10f), Secs(20f)) : RandRange(_durMin, _durMax);
            _tailDur = RandRange(Secs(1f), Secs(4f));
            // Most fights you see coming (an alert Tension lead-in); some are ambushes (straight in).
            if (_rng.Next(100) < 60) { _mode = CombatMode.Approach; _modeTicksLeft = RandRange(Secs(3f), Secs(7f)); }
            else { _mode = CombatMode.Engaged; _modeTicksLeft = _engagedDur; }
        }

        // ── Snapshot fill ──
        private void FillSnapshot(bool isDay, bool bloodMoonNight)
        {
            var gs = _gs;
            gs.Now = SimClock.Now;
            gs.IsDay = isDay;
            gs.IsBloodMoon = bloodMoonNight;
            gs.IsSwimming = _swimming;
            gs.VehicleType = _vehicle;
            gs.TraderName = _phase == Phase.Trader ? _trader : null;
            gs.Biome = _biome;
            gs.NearHome = _phase == Phase.Home;

            bool inPoi = _phase == Phase.Poi;
            gs.InPoi = inPoi;
            gs.PoiName = inPoi ? _poiName : null;
            gs.PoiTier = _poiTier;
            // Infested (enemies remaining) until the cleared tail; then 0 → POI_Cleared.
            gs.PoiEnemiesRemaining = inPoi && _phaseElapsed < _poiInfestedTicks ? Math.Max(1, _encounterSize) : 0;

            // ── Enemies (pooled) ──
            // Engaged → enemies target the player (Combat). Approach/Tail → alert but NOT
            // targeting (a smaller leading/trailing group → the Lua emits Tension).
            gs.Enemies.Clear();
            bool engaged = _mode == CombatMode.Engaged;
            bool aware = _mode == CombatMode.Approach || _mode == CombatMode.Tail;
            int n = engaged ? Math.Min(_encounterSize, _enemyPool.Length)
                  : aware ? Math.Min(Math.Max(1, _encounterSize / 2), _enemyPool.Length)
                  : 0;
            bool danger = false;
            for (int i = 0; i < n; i++)
            {
                var e = _enemyPool[i];
                e.ClassName = PickEnemyClass(_encounterTier, bloodMoonNight);
                e.Distance = 2f + (float)_rng.NextDouble() * 25f;
                e.IsAlert = true;
                // Engaged: boss waves are all-in, lighter fights mostly. Approach/Tail: none target.
                e.IsTargetingPlayer = engaged && (_encounterTier == Tier.Boss || _rng.Next(100) < 82);
                gs.Enemies.Add(e);
                if (e.IsTargetingPlayer) danger = true;
            }

            // Health drains under fire (harder in boss waves), recovers otherwise. Tuned so
            // Low_Health_Panic is an occasional serious-fight event, not every skirmish.
            float drain = _encounterTier == Tier.Boss ? 0.012f : _encounterTier == Tier.High ? 0.006f : 0.003f;
            _health = danger ? Math.Max(0.05f, _health - drain) : Math.Min(1f, _health + 0.01f);
            gs.HealthPct = _health;
            // The game reports Panicked under sustained aggro (vanilla bridge flag).
            gs.IsPanicked = danger;
        }

        private string PickEnemyClass(Tier tier, bool bloodMoon)
        {
            int r = _rng.Next(100);
            switch (tier)
            {
                case Tier.Boss:
                    // Guarantee real weight: a chunk of heavies (incl. demolition) + mids.
                    return r < 45 ? HeavyEnemies[_rng.Next(HeavyEnemies.Length)]
                         : r < 85 ? MidEnemies[_rng.Next(MidEnemies.Length)]
                         : WeakEnemies[_rng.Next(WeakEnemies.Length)];
                case Tier.High:
                    return r < 35 ? MidEnemies[_rng.Next(MidEnemies.Length)]
                                  : WeakEnemies[_rng.Next(WeakEnemies.Length)];
                default:
                    if (bloodMoon && r < 25) return MidEnemies[_rng.Next(MidEnemies.Length)];
                    return WeakEnemies[_rng.Next(WeakEnemies.Length)];
            }
        }

        // ── Helpers ──
        private int Secs(float seconds) => Math.Max(1, (int)(seconds / _tickSeconds));
        private float Rand(float span) => (float)_rng.NextDouble() * span;
        private int RandRange(int lo, int hi) => hi <= lo ? Math.Max(1, lo) : _rng.Next(lo, hi + 1);
    }
}
