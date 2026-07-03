# context.lua API — v1

`Config/context.lua` decides which **context tokens** are active each scan tick.
CMM reads the game into a `gameState`, calls your `resolve(gameState, cfg)`, and
matches the returned tokens against `Config/contexts.xml` (priority / fallback /
warmup / volume) to choose what plays. The script decides *which* contexts are
active; `contexts.xml` decides how they're ranked and what files they map to.

Editing the file hot-reloads it. A syntax error keeps the previously loaded
script; a runtime error falls back to the last good result — so a mistake never
crashes the game. Errors (with `context.lua:line`) go to the game log.

## The script must define

```lua
API_VERSION = 1                  -- must match the host's supported version
function resolve(gameState, cfg) -- called each tick; return an array of token strings
  ...
  return { "Ambient_Desert" }
end
function reset() end             -- optional; clears your cross-tick state on world-unload
```

**Return value:** a dense array (`{1..n}`) of context-name strings. Order is
intent (earlier = higher); the host dedups. Returning a non-table or `nil` means
"no contexts this tick" (not an error). Tokens that don't exist in `contexts.xml`
are simply ignored.

**State across ticks:** keep it in a `local` table (e.g. `local memory = {...}`). It
persists between calls; `reset()` is your hook to clear it when a world unloads.

## `gameState` (read-only, rebuilt each tick)

| field | type | notes |
|-------|------|-------|
| `now` | number | seconds (monotonic); use for your own timers |
| `isDay` | bool | |
| `isBloodMoon` | bool | |
| `hour` | number | time of day, `0..24` (fractional, minute-resolved) — for dawn/dusk/midnight variation |
| `day` | number | in-game day number (as the game counts) |
| `healthPct` | number | 0..1 |
| `staminaPct` | number | 0..1 |
| `foodPct` | number | 0..1 (fullness) |
| `waterPct` | number | 0..1 (hydration) |
| `isSwimming` | bool | |
| `isCrouching` | bool | sneaking — for a stealth ambient layer |
| `isPanicked` | bool | game's vanilla "panicked" combat signal |
| `fallDistance` | number | blocks fallen in the current fall; `0` when grounded or rising (big ⇒ a long plummet) |
| `rainfall` | number | 0..1 |
| `snowfall` | number | 0..1 |
| `windSpeed` | number | raw game units (rises in storms) |
| `cloudThickness` | number | 0..1 (overcast) |
| `temperature` | number | degrees (game's Fahrenheit scale) |
| `vehicleType` | string/nil | lowercased type name (`"minibike"`), nil if not riding |
| `traderName` | string/nil | lowercased name, nil unless inside an **open** trader |
| `biome` | string/nil | raw name (`"desert"`); **nil = chunk not loaded yet** |
| `nearHome` | bool | near bedroll or a land claim |
| `alliesNearby` | number | living party members / friends within `cfg.playerDetectRange` (multiplayer only; 0 in single-player) |
| `strangersNearby` | number | living **non-friendly** players (neither party member nor ACL friend) within `cfg.playerDetectRange` (multiplayer only; 0 in single-player) |
| `inPoi` | bool | |
| `poiName` | string | prefab **type** name (shared by every copy of a POI design) — use for context tokens, not for same-POI continuity |
| `poiId` | string | per-**instance** id (unique per placed POI) — use this to tell two copies of the same type apart |
| `poiTier` | number | difficulty tier (0–5; else no `_Tn` suffix) |
| `poiEnemiesRemaining` | number | live sleepers left (>0 ⇒ infested) |
| `enemies` | array | see below; pre-filtered to `cfg.enemyDetectRange` |
| `buffs` | array | active status-effect names (raw, e.g. `"buffInjuryBleeding"`); match with `cmm.hasBuff` |

Each `enemies[i]`: `{ className, distance, isAlert, isTargetingPlayer }`.
`isTargetingPlayer` implies `isAlert`. `className` is the raw config class name
(e.g. `"zombieBoeFeral"`) — lowercase it yourself for matching.

`buffs` is a plain string array of the player's active buff names, exactly as the
game stores them (not lowercased). Prefer `cmm.hasBuff(gameState, "…")` (case-
insensitive substring) over indexing it yourself.

## `cfg` (read-only, from settings.xml)

`enemyDetectRange`, `playerDetectRange`, `combatShareRange`, `combatShareRequireAlly`,
`homeDetectRange`, `healthDangerPct`, `combatExitDelay`, `combatCooldown`,
`combatLowThreshold`, `combatHighThreshold`.

> `combatShareRange` / `combatShareRequireAlly` gate multiplayer squad-shared combat
> **in C#** (an enemy fighting a nearby player becomes your combat): the range is the
> local→other-player distance, and the ally toggle decides whether that player must be
> a party member / ACL friend. They're surfaced here for reference; the decision to
> share is made before the snapshot reaches the script.
>
> The combat **threat weights/tiers** live entirely in `context.lua` (see the
> `COMBAT_TIERS` / `TYPE_WEIGHTS` / `STATE_MULTS` tables in the default); there is
> no longer a `<CombatThreat>` block in settings.xml. The scalar tunables above
> (including `combatLowThreshold` / `combatHighThreshold`) still come from settings.xml.

## `cmm` helpers

| call | returns | |
|------|---------|--|
| `cmm.has(str, sub)` | bool | nil-safe, case-insensitive substring test |
| `cmm.count(gameState, filter)` | number | counts `enemies` passing `filter` |
| `cmm.hasBuff(gameState, sub)` | bool | case-insensitive substring test over active `buffs` |
| `cmm.hasFiles(context)` | bool | does this context currently have playable tracks? |

`filter` keys (all optional): `targetingPlayer` (bool), `alert` (bool),
`within` (number, max distance). `cmm.count(gameState, {})` counts all in-range enemies.

## Sandbox

Scripts run in a hard sandbox: `string`, `table`, `math` and the usual pure-Lua
library are available; `io`, `os`, file access, `require`, and dynamic loading
are not. `print(...)` goes to the game log.
