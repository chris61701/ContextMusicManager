-- ============================================================================
-- context.lua — Context Music Manager decision script
-- ============================================================================
-- Each scan tick CMM reads the game into `gameState` and calls resolve(gameState, cfg),
-- which returns the list of active context tokens (strings). The host matches those
-- against Config/contexts.xml (priority / fallback / warmup / volume) to decide what
-- actually plays — this script only decides WHICH contexts are active, not how they're
-- prioritized or what files they map to.
--
-- This default reproduces CMM's built-in behavior. Edit it to change how the music
-- reacts; saving the file hot-reloads it live. Mistakes here can't crash the game: a
-- syntax error keeps the previously loaded script, and a runtime error falls back to
-- the last good result. Errors are reported (with line numbers) to the game log.
--
-- HOW THIS FILE IS LAID OUT:
--   • "TUNING" section below  — the knobs most people want: which game things map to
--     which music, and how hard each enemy hits the combat meter. Safe to edit.
--   • "INTERNALS" section     — the stateful logic that turns those knobs into context
--     tokens each tick. You usually don't need to touch it.
--
-- Full field/helper reference: Config/context.api.md
-- ============================================================================

API_VERSION = 1

-- ════════════════════════════════════════════════════════════════════════════
-- TUNING — edit these
-- ════════════════════════════════════════════════════════════════════════════

-- ── Combat intensity tiers ──────────────────────────────────────────────────
-- Each tick we add up a "threat score" from the enemies targeting you (see the
-- weight tables below), then pick the highest tier whose `minScore` that total
-- reaches. The chosen tier's `context` is emitted (e.g. "Combat_High").
-- Listed in ascending intensity; raise/lower a minScore to make a tier easier or
-- harder to reach. Weights are calibrated so a standard walker zombie ≈ 1.0.
local COMBAT_TIERS = {                 -- ascending intensity; list index = tier number
  { context = "Combat_Low",  minScore = 0.5 },
  { context = "Combat_High", minScore = 6   },
  { context = "Combat_Boss", minScore = 18  },
}

-- How much each enemy contributes to the threat score. Matching is
-- longest-substring-wins against the lowercased entity class name — e.g.
-- "zombieDireWolf" matches "direwolf" (4.5), not "wolf" (2.0). Anything not
-- listed scores DEFAULT_WEIGHT. Add your own rows for modded enemies.
local DEFAULT_WEIGHT = 1.0
local TYPE_WEIGHTS = {                 -- class-name substring → weight
  { "crawler", 0.6 }, { "spider", 1.3 }, { "dog", 1.4 }, { "vulture", 1.4 },
  { "coyote", 1.5 }, { "wolf", 2.0 }, { "mountainlion", 3.0 }, { "screamer", 3.5 },
  { "cop", 3.5 }, { "wight", 3.5 }, { "mutated", 3.5 }, { "bear", 4.0 },
  { "direwolf", 4.5 }, { "demolition", 8.0 }, { "grace", 10.0 },
}

-- Multiplier applied on top of an enemy's weight for its variant (a feral bear
-- counts as bear × 1.5). Same longest-substring matching as above.
local STATE_MULTS = {                  -- class-name substring → multiplier
  { "feral", 1.5 }, { "radiated", 2.0 }, { "charged", 2.75 }, { "infernal", 3.25 },
}

-- Multiplayer combat-intensity scaling: fighting shoulder to shoulder with a squad
-- is a bigger musical moment than soloing the same swarm. Each living party member /
-- friend within cfg.playerDetectRange (gameState.alliesNearby) adds this much to the
-- threat score — but ONLY once a real fight is already underway (so standing next to
-- friends out of combat never starts combat music). 0 disables the effect. Calibrated
-- in the same units as TYPE_WEIGHTS (a walker ≈ 1.0), so 2.0 ≈ "each ally counts like
-- a couple extra zombies" toward the next combat tier. Single-player: always 0 allies.
local ALLY_COMBAT_BONUS = 2.0

-- ── Raw game strings → context tokens ───────────────────────────────────────
-- Left side = what the game reports (lowercased); right side = the context name
-- CMM looks up in contexts.xml. Add or rename freely to match your own folders.
local BIOMES = { pine_forest = "Forest", desert = "Desert",
                 burnt_forest = "Burnt_Forest", snow = "Snow", wasteland = "Wasteland" }
local VEHICLES = { gyrocopter = "Vehicle_Gyrocopter", bicycle = "Vehicle_Bicycle",
                   motorcycle = "Vehicle_Motorcycle", minibike = "Vehicle_Minibike",
                   jeep = "Vehicle_4x4" }
local TRADERS = { jen = "Trader_Jen", rekt = "Trader_Rekt", bob = "Trader_Bob",
                  hugh = "Trader_Hugh", joel = "Trader_Joel" }

-- ════════════════════════════════════════════════════════════════════════════
-- INTERNALS — you usually don't need to edit below here
-- ════════════════════════════════════════════════════════════════════════════

-- ── Cross-tick memory (persists between ticks; cleared by reset() on world-unload) ──
-- Distinct from `gameState`, which is rebuilt fresh every tick. This is the small
-- amount of history the combat/POI logic has to remember on its own.
-- tier 0 = not in combat; 1..#COMBAT_TIERS = combat intensity (only climbs mid-fight).
local memory = { tier = 0, lastAggro = 0, biome = "", wasInfested = false, lastPoiId = "None" }

-- Append a token. The host dedups, so insertion order just expresses intent (earlier = higher).
local function add(list, token) list[#list + 1] = token end

-- Longest-substring-wins lookup over a { {substr, value}, ... } list. `lowerName`
-- must already be lowercased. Returns `default` when nothing matches.
local function bestMatch(lowerName, list, default)
  local value, bestLen = default, -1
  for _, entry in ipairs(list) do
    if #entry[1] > bestLen and string.find(lowerName, entry[1], 1, true) then
      value, bestLen = entry[2], #entry[1]
    end
  end
  return value
end

-- One enemy's threat contribution: its type weight × its variant multiplier.
local function scoreEnemy(className)
  if not className or className == "" then return DEFAULT_WEIGHT end
  local lower = string.lower(className)
  return bestMatch(lower, TYPE_WEIGHTS, DEFAULT_WEIGHT) * bestMatch(lower, STATE_MULTS, 1.0)
end

-- Highest tier whose minScore the score meets; 0 = below the lowest band (not in combat).
local function tierForScore(score)
  local tier = 0
  for i = 1, #COMBAT_TIERS do
    if score >= COMBAT_TIERS[i].minScore then tier = i else break end
  end
  return tier
end

-- Emits the active combat tier plus its biome variant (e.g. Combat_High + Combat_High_Desert),
-- using the biome latched at encounter start (memory.biome), not the live biome.
local function addCombatTier(list, tier)
  local name = COMBAT_TIERS[tier].context
  add(list, name)
  if memory.biome ~= "" then add(list, name .. "_" .. memory.biome) end
end

-- ── Entry point: called once per scan tick ──────────────────────────────────
function resolve(gameState, cfg)
  local active = {}
  local now = gameState.now

  if gameState.isSwimming then add(active, "Swimming") end

  if gameState.vehicleType then
    add(active, "Vehicle")
    for key, token in pairs(VEHICLES) do
      if cmm.has(gameState.vehicleType, key) then add(active, token); break end
    end
  end

  -- traderName is set only while inside an OPEN trader
  if gameState.traderName then
    local specific
    for key, token in pairs(TRADERS) do
      if cmm.has(gameState.traderName, key) then specific = token; break end
    end
    add(active, specific or "Trader")
  end

  local biomeToken = (gameState.biome and BIOMES[gameState.biome]) or ""

  -- ── Combat: weighted threat + held tier ───────────────────────────────────
  local score = 0
  for _, e in ipairs(gameState.enemies) do
    if e.isTargetingPlayer then score = score + scoreEnemy(e.className) end
  end
  -- Squad bonus: only escalate an already-live fight, never start one from allies alone.
  if score > 0 and gameState.alliesNearby > 0 then
    score = score + gameState.alliesNearby * ALLY_COMBAT_BONUS
  end
  local instantTier  = tierForScore(score)
  local windowLapsed = (now - memory.lastAggro) > cfg.combatExitDelay

  if instantTier > 0 then
    -- (Re)latch the biome on a fresh encounter or a re-engage after the active window
    -- lapsed; otherwise back-fill it if it wasn't known on the opening tick.
    if memory.tier == 0 or windowLapsed then memory.biome = biomeToken
    elseif memory.biome == ""           then memory.biome = biomeToken end
    memory.lastAggro = now
  end
  if instantTier > memory.tier then memory.tier = instantTier end   -- climb only; never steps down mid-fight

  local sinceAggro = now - memory.lastAggro
  local inCombat = false
  if sinceAggro <= cfg.combatExitDelay and memory.tier > 0 then
    addCombatTier(active, memory.tier)
    if gameState.isBloodMoon then add(active, "Blood_Moon_Combat") end
    inCombat = true
  elseif sinceAggro > cfg.combatCooldown then
    memory.tier, memory.biome = 0, ""                               -- encounter over — forget tier + biome
  end

  -- Vanilla Panicked bridge: only when we ship NO combat music of our own, escalate to
  -- the Combat_High tier so the fallback chain can hand off to the game's combat track.
  if not inCombat and gameState.isPanicked
     and not cmm.hasFiles("Combat_High") and not cmm.hasFiles("Combat_Low") then
    local bridge = 0
    for i = 1, #COMBAT_TIERS do
      if COMBAT_TIERS[i].context == "Combat_High" then bridge = i; break end
    end
    if bridge == 0 then bridge = math.max(#COMBAT_TIERS - 1, 1) end   -- second-from-top, never the top
    if bridge > memory.tier then memory.tier = bridge end
    memory.lastAggro = now
    addCombatTier(active, memory.tier)
    if gameState.isBloodMoon then add(active, "Blood_Moon_Combat") end
    inCombat = true
  end

  -- ── Tension / low-health ──────────────────────────────────────────────────
  if inCombat and gameState.healthPct < cfg.healthDangerPct then add(active, "Low_Health_Panic") end
  if not inCombat
     and cmm.count(gameState, {}) >= 1
     and cmm.count(gameState, { alert = true }) >= 1 then
    add(active, "Tension")
  end

  -- ── Online: a non-friendly player is nearby ────────────────────────────────
  -- Any living player within cfg.playerDetectRange who is neither a party member nor
  -- an ACL friend (multiplayer only; always 0 in single-player). Relationship-only —
  -- this fires whether or not the server's PvP rules actually permit harm between you.
  if gameState.strangersNearby > 0 then add(active, "Online_Stranger") end

  -- ── Online: a friendly player is nearby ───────────────────────────────────
  if gameState.alliesNearby > 0 then add(active, "Online_Friend") end

  -- ── Home ──────────────────────────────────────────────────────────────────
  if gameState.nearHome then add(active, gameState.isDay and "Home" or "Home_Night") end

  -- ── Points of Interest ────────────────────────────────────────────────────
  if gameState.inPoi then
    local tier = gameState.poiTier
    local suffix = (tier >= 0 and tier <= 5) and ("_T" .. tier) or ""
    -- POI_Cleared sources:
    --   • poiQuestCleared — the authoritative one-shot from the quest system (the only
    --     clear signal a multiplayer client gets; fires once the clear part of a POI
    --     quest completes). Always honored.
    --   • the infested→0 transition — derived locally from real sleeper state. Only used
    --     when poiClearFromQuestOnly is false (single-player / host). On a MP client the
    --     entity count bounces room-to-room and can't tell a real clear from an
    --     un-triggered far room, so that path is suppressed there.
    -- Continuity is keyed on poiId (the per-INSTANCE id), NOT poiName (the prefab TYPE,
    -- shared by every copy of a POI). Keying on the type let a wasInfested latch from one
    -- POI "clear" the next copy of the same type you ran into (see the cross-POI misfire).
    if gameState.poiQuestCleared then add(active, "POI_Cleared") end
    if gameState.poiEnemiesRemaining > 0 then
      memory.wasInfested = true
      add(active, "POI_Infested" .. suffix)
    else
      if not gameState.poiClearFromQuestOnly
         and memory.wasInfested and gameState.poiId == memory.lastPoiId then
        add(active, "POI_Cleared")
      end
      memory.wasInfested = false
      add(active, "POI" .. suffix)
    end
    memory.lastPoiId = gameState.poiId
  else
    memory.wasInfested = false
  end

  -- ── Ambient (skipped while the biome chunk isn't loaded: gameState.biome == nil) ──
  if gameState.biome then
    local base = (biomeToken ~= "") and ("Ambient_" .. biomeToken) or "Ambient"
    if gameState.isDay then
      add(active, base)
    else
      add(active, base .. "_Night")
      add(active, base)
    end
  end

  return active
end

-- Cleared on world-unload (mirrors ContextResolver.resetValues(): combat tier + biome).
function reset()
  memory.tier, memory.biome = 0, ""
end
