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
| `healthPct` | number | 0..1 |
| `isSwimming` | bool | |
| `isPanicked` | bool | game's vanilla "panicked" combat signal |
| `vehicleType` | string/nil | lowercased type name (`"minibike"`), nil if not riding |
| `traderName` | string/nil | lowercased name, nil unless inside an **open** trader |
| `biome` | string/nil | raw name (`"desert"`); **nil = chunk not loaded yet** |
| `nearHome` | bool | near bedroll or a land claim |
| `inPoi` | bool | |
| `poiName` | string | stable POI identifier |
| `poiTier` | number | difficulty tier (0–5; else no `_Tn` suffix) |
| `poiEnemiesRemaining` | number | live sleepers left (>0 ⇒ infested) |
| `enemies` | array | see below; pre-filtered to `cfg.enemyDetectRange` |

Each `enemies[i]`: `{ className, distance, isAlert, isTargetingPlayer }`.
`isTargetingPlayer` implies `isAlert`. `className` is the raw config class name
(e.g. `"zombieBoeFeral"`) — lowercase it yourself for matching.

## `cfg` (read-only, from settings.xml)

`enemyDetectRange`, `homeDetectRange`, `healthDangerPct`, `combatExitDelay`,
`combatCooldown`, `combatLowThreshold`, `combatHighThreshold`.

> The combat **threat weights/tiers** are no longer read from settings.xml's
> `<CombatThreat>` when a script is active — they live in `context.lua` (see the
> `COMBAT_TIERS` / `TYPE_WEIGHTS` / `STATE_MULTS` tables in the default). The
> scalar tunables above still come from settings.xml.

## `cmm` helpers

| call | returns | |
|------|---------|--|
| `cmm.has(str, sub)` | bool | nil-safe, case-insensitive substring test |
| `cmm.count(gameState, filter)` | number | counts `enemies` passing `filter` |
| `cmm.hasFiles(context)` | bool | does this context currently have playable tracks? |

`filter` keys (all optional): `targetingPlayer` (bool), `alert` (bool),
`within` (number, max distance). `cmm.count(gameState, {})` counts all in-range enemies.

## Sandbox

Scripts run in a hard sandbox: `string`, `table`, `math` and the usual pure-Lua
library are available; `io`, `os`, file access, `require`, and dynamic loading
are not. `print(...)` goes to the game log.
