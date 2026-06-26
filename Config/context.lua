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
-- Full field/helper reference: Config/context.api.md
-- ============================================================================

API_VERSION = 1

-- ── Cross-tick state (persists between ticks; cleared by reset() on world-unload) ──
-- tier 0 = not in combat; 1..#COMBAT_TIERS = combat intensity (only climbs mid-fight).
local S = { tier = 0, lastAggro = 0, biome = "", wasInfested = false, lastPoi = "None" }

-- ── Weighted combat threat model ────────────────────────────────────────────
-- score = Σ over enemies targeting you of (type weight × state multiplier); the summed
-- score selects the highest COMBAT_TIER whose minScore it meets. Matching is
-- longest-substring-wins against the lowercased entity class name (e.g. "zombieDireWolf"
-- matches "direwolf" 4.5, not "wolf"). Weights are calibrated so a standard walker ≈ 1.0.
local COMBAT_TIERS = {                 -- ascending intensity; list index = tier
  { context = "Combat_Low",  minScore = 0.5 },
  { context = "Combat_High", minScore = 6   },
  { context = "Combat_Boss", minScore = 18  },
}
local DEFAULT_WEIGHT = 1.0
local TYPE_WEIGHTS = {                 -- class-name substring → weight
  { "crawler", 0.6 }, { "spider", 1.3 }, { "dog", 1.4 }, { "vulture", 1.4 },
  { "coyote", 1.5 }, { "wolf", 2.0 }, { "mountainlion", 3.0 }, { "screamer", 3.5 },
  { "cop", 3.5 }, { "wight", 3.5 }, { "mutated", 3.5 }, { "bear", 4.0 },
  { "direwolf", 4.5 }, { "demolition", 8.0 }, { "grace", 10.0 },
}
local STATE_MULTS = {                  -- class-name substring → multiplier (on top of weight)
  { "feral", 1.5 }, { "radiated", 2.0 }, { "charged", 2.75 }, { "infernal", 3.25 },
}

-- ── Raw game strings → context tokens ───────────────────────────────────────
local BIOMES = { pine_forest = "Forest", desert = "Desert",
                 burnt_forest = "Burnt_Forest", snow = "Snow", wasteland = "Wasteland" }
local VEHICLES = { gyrocopter = "Vehicle_Gyrocopter", bicycle = "Vehicle_Bicycle",
                   motorcycle = "Vehicle_Motorcycle", minibike = "Vehicle_Minibike",
                   jeep = "Vehicle_4x4" }
local TRADERS = { jen = "Trader_Jen", rekt = "Trader_Rekt", bob = "Trader_Bob",
                  hugh = "Trader_Hugh", joel = "Trader_Joel" }

-- ── Helpers ─────────────────────────────────────────────────────────────────
local function add(ctx, token) ctx[#ctx + 1] = token end   -- host dedups; insertion order = intent

-- Longest-substring-wins lookup over a { {substr, value}, ... } list. cn must be lowercase.
local function bestMatch(cn, list, default)
  local val, bestLen = default, -1
  for _, kv in ipairs(list) do
    if #kv[1] > bestLen and string.find(cn, kv[1], 1, true) then val, bestLen = kv[2], #kv[1] end
  end
  return val
end

local function scoreEnemy(className)
  if not className or className == "" then return DEFAULT_WEIGHT end
  local cn = string.lower(className)
  return bestMatch(cn, TYPE_WEIGHTS, DEFAULT_WEIGHT) * bestMatch(cn, STATE_MULTS, 1.0)
end

-- Highest tier whose minScore the score meets; 0 = below the lowest band (not in combat).
local function tierForScore(score)
  local t = 0
  for i = 1, #COMBAT_TIERS do
    if score >= COMBAT_TIERS[i].minScore then t = i else break end
  end
  return t
end

-- Emits the active combat tier plus its biome variant (e.g. Combat_High + Combat_High_Desert),
-- using the biome latched at encounter start (S.biome), not the live biome.
local function addCombatTier(ctx, tier)
  local name = COMBAT_TIERS[tier].context
  add(ctx, name)
  if S.biome ~= "" then add(ctx, name .. "_" .. S.biome) end
end

-- ── Entry point: called once per scan tick ──────────────────────────────────
function resolve(gameState, cfg)
  local ctx = {}
  local now = gameState.now

  if gameState.isSwimming then add(ctx, "Swimming") end

  if gameState.vehicleType then
    add(ctx, "Vehicle")
    for key, token in pairs(VEHICLES) do
      if cmm.has(gameState.vehicleType, key) then add(ctx, token); break end
    end
  end

  -- traderName is set only while inside an OPEN trader
  if gameState.traderName then
    local specific
    for key, token in pairs(TRADERS) do
      if cmm.has(gameState.traderName, key) then specific = token; break end
    end
    add(ctx, specific or "Trader")
  end

  local biomeTok = (gameState.biome and BIOMES[gameState.biome]) or ""

  -- ── Combat: weighted threat + held tier ───────────────────────────────────
  local score = 0
  for _, e in ipairs(gameState.enemies) do
    if e.isTargetingPlayer then score = score + scoreEnemy(e.className) end
  end
  local instant   = tierForScore(score)
  local hadLapsed = (now - S.lastAggro) > cfg.combatExitDelay

  if instant > 0 then
    -- (Re)latch the biome on a fresh encounter or a re-engage after the active window
    -- lapsed; otherwise back-fill it if it wasn't known on the opening tick.
    if S.tier == 0 or hadLapsed then S.biome = biomeTok
    elseif S.biome == ""        then S.biome = biomeTok end
    S.lastAggro = now
  end
  if instant > S.tier then S.tier = instant end             -- climb only; never steps down mid-fight

  local sinceAggro = now - S.lastAggro
  local inCombat = false
  if sinceAggro <= cfg.combatExitDelay and S.tier > 0 then
    addCombatTier(ctx, S.tier)
    if gameState.isBloodMoon then add(ctx, "Blood_Moon_Combat") end
    inCombat = true
  elseif sinceAggro > cfg.combatCooldown then
    S.tier, S.biome = 0, ""                                  -- encounter over — forget tier + biome
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
    if bridge > S.tier then S.tier = bridge end
    S.lastAggro = now
    addCombatTier(ctx, S.tier)
    if gameState.isBloodMoon then add(ctx, "Blood_Moon_Combat") end
    inCombat = true
  end

  -- ── Tension / low-health ──────────────────────────────────────────────────
  if inCombat and gameState.healthPct < cfg.healthDangerPct then add(ctx, "Low_Health_Panic") end
  if not inCombat
     and cmm.count(gameState, {}) >= 1
     and cmm.count(gameState, { alert = true }) >= 1 then
    add(ctx, "Tension")
  end

  -- ── Home ──────────────────────────────────────────────────────────────────
  if gameState.nearHome then add(ctx, gameState.isDay and "Home" or "Home_Night") end

  -- ── Points of Interest ────────────────────────────────────────────────────
  if gameState.inPoi then
    local tier = gameState.poiTier
    local suffix = (tier >= 0 and tier <= 5) and ("_T" .. tier) or ""
    if gameState.poiEnemiesRemaining > 0 then
      S.wasInfested = true
      add(ctx, "POI_Infested" .. suffix)
    else
      if S.wasInfested and gameState.poiName == S.lastPoi then add(ctx, "POI_Cleared") end
      S.wasInfested = false
      add(ctx, "POI" .. suffix)
    end
    S.lastPoi = gameState.poiName
  else
    S.wasInfested = false
  end

  -- ── Ambient (skipped while the biome chunk isn't loaded: gameState.biome == nil) ──
  if gameState.biome then
    local base = (biomeTok ~= "") and ("Ambient_" .. biomeTok) or "Ambient"
    if gameState.isDay then
      add(ctx, base)
    else
      add(ctx, base .. "_Night")
      add(ctx, base)
    end
  end

  return ctx
end

-- Cleared on world-unload (mirrors ContextResolver.resetValues(): combat tier + biome).
function reset()
  S.tier, S.biome = 0, ""
end
