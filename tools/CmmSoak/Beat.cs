using System;
using System.Collections.Generic;
using CMM;

// The play session: what the player is doing, minute by minute, expressed the only way
// the mod can see it — as a GameState the decision script reads.
//
// A scripted loop rather than random noise, because the point is to exercise the ORDER
// things happen in: you do not wander into a cleared POI, and combat does not start
// while you are stood in a trader. Random state would spend most of its time in
// combinations the game cannot produce, and would miss the transitions that matter —
// the fade interrupted by a second fade, the POI clear landing during a fight.
//
// One loop is roughly twenty minutes of play: out exploring, in to the trader for a
// quest, out to the POI, a fight inside it, the clear, and back home. Night falls on
// its own schedule underneath, so the same beat plays differently depending on when it
// lands, and blood moons arrive every seventh day.

internal sealed class Beat
{
    readonly Random _rng;
    readonly GameState _gs = new GameState();

    // The phases of one loop, and how long each lasts in simulated seconds. Deliberately
    // uneven: equal phases would make every transition land on the same clock edge.
    static readonly (string Phase, double Seconds)[] Loop =
    {
        ("explore",    260),
        ("trader",     140),
        ("travel",     180),
        ("poi",        220),
        ("combat",      90),
        ("poi",        110),
        ("combat_big",  70),
        ("cleared",     40),
        ("poi",         60),
        ("travel",     150),
        ("home",       200),
        ("explore",    300),
        ("death",       25),
        ("spawn",       30),
    };

    static readonly double LoopSeconds;
    static Beat()
    {
        double s = 0;
        foreach (var p in Loop) s += p.Seconds;
        LoopSeconds = s;
    }

    internal Beat(Random rng) { _rng = rng; }

    internal GameState Advance(double simSeconds)
    {
        // Where in the loop are we?
        double into = simSeconds % LoopSeconds;
        string phase = "explore";
        foreach (var p in Loop)
        {
            if (into < p.Seconds) { phase = p.Phase; break; }
            into -= p.Seconds;
        }

        // A 60-minute day, as the shipped default has it.
        double dayLen = 3600;
        double dayPos = (simSeconds % dayLen) / dayLen;
        int day = (int)(simSeconds / dayLen);

        _gs.Now = (float)simSeconds;
        _gs.Hour = (float)(dayPos * 24);
        _gs.Day = day;
        _gs.IsDay = _gs.Hour >= 6 && _gs.Hour < 22;
        _gs.IsBloodMoon = (day % 7) == 6 && !_gs.IsDay;

        _gs.HealthPct = 1f;
        _gs.StaminaPct = 0.8f;
        _gs.FoodPct = 0.7f;
        _gs.WaterPct = 0.7f;
        _gs.IsSwimming = false;
        _gs.IsCrouching = phase == "poi";
        _gs.IsPanicked = false;

        _gs.Enemies.Clear();
        _gs.InPoi = false;
        _gs.PoiName = null;
        _gs.PoiId = null;
        _gs.PoiTier = 0;
        _gs.PoiEnemiesRemaining = 0;
        _gs.PoiQuestCleared = false;
        _gs.TraderName = null;
        _gs.NearHome = false;
        _gs.Biome = "pine_forest";

        switch (phase)
        {
            case "trader":
                // The script only counts you as at a trader when one is OPEN, which is what
                // TraderName being set means.
                _gs.TraderName = "trader jen";
                break;

            case "poi":
                InPoi(2);
                break;

            case "combat":
                InPoi(2);
                AddEnemies(2);
                _gs.PoiEnemiesRemaining = 2;
                break;

            case "combat_big":
                InPoi(4);
                _gs.HealthPct = 0.45f;
                AddEnemies(6);
                _gs.PoiEnemiesRemaining = 6;
                break;

            case "cleared":
                InPoi(4);
                _gs.PoiQuestCleared = true;
                break;

            case "home":
                _gs.NearHome = true;
                break;

            case "death":
                // Death and spawn are PlayOnce stingers fired by game EVENTS, not by state,
                // so there is nothing to set here -- the phase exists to put a gap in the
                // music where one really happens.
                _gs.HealthPct = 0.05f;
                break;
        }

        // A blood moon puts enemies on you wherever you are, which is what makes the same
        // beat resolve differently on day 7 than on day 1.
        if (_gs.IsBloodMoon && _gs.Enemies.Count == 0 && (_rng.Next(4) == 0)) AddEnemies(3);

        return _gs;
    }

    void InPoi(int tier)
    {
        _gs.InPoi = true;
        _gs.PoiName = "abandoned_house_" + tier;
        _gs.PoiId = "poi-" + tier;
        _gs.PoiTier = tier;
    }

    void AddEnemies(int n)
    {
        for (int i = 0; i < n; i++)
            _gs.Enemies.Add(new EnemyInfo
            {
                ClassName = i == 0 ? "zombieBoeFeral" : "zombieArlene",
                Distance = 4f + i * 3f,
                IsAlert = true,
                IsTargetingPlayer = i < 2,
            });
    }
}
