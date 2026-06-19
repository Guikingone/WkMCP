# Live in-game bridge (CETBridge)

WolvenKit MCP's 88 "classic" tools are **offline**: they operate on
files and archives, with the game shut down. The **`live_*`** tools do the opposite: they drive
a **running** Cyberpunk 2077 — executing Lua, reading/writing game state,
spawning, teleportation, weather, the in-memory TweakDB, observing events.

> Prefix `live_` = the game's live memory. Without the prefix = files. E.g. `tweakdb_query`
> (offline, reads `tweakdb.bin`) vs `live_tweakdb_get` (reads the running game's DB).

## How it works

You cannot inject into the game's Lua VM from the outside: you need an **in-game
mod**. The MCP server talks to a small Lua mod (**CETBridge**, loaded by Cyber Engine
Tweaks) which runs the commands in the engine and returns the result.

```
Claude ─MCP/JSON-RPC─▶ WolvenKitMcp ──TCP 127.0.0.1:27010──▶ CETBridge mod (Lua/CET) ─▶ game
                          (CetBridge.cs)  └─file fallback──▶  (command.json / response.json)
```

Two transports, automatic switching:

| Transport | Latency | Dependency | Detail |
|---|---|---|---|
| **TCP** (recommended) | ~1 ms | RedSocket | The server listens on `127.0.0.1:27010`; the mod connects to it. |
| **File** (fallback) | ~16-33 ms | none | The server writes `command.json`, the mod replies via `response.json`. |

The server tries TCP; if the port is taken (another session) or if RedSocket is absent,
it falls back to the file transport. The mod, for its part, **always** listens on both.

## Prerequisites

- **Cyberpunk 2077** running (the live layer acts on the running game).
- **Cyber Engine Tweaks (CET)** 1.32+ — loads the Lua mod.
- **RED4ext** 1.25+.
- **RedSocket** (optional but recommended, for the ~1 ms TCP). Without it: file transport.

## Installation

1. **Copy the mod** `live-bridge/CETBridge/` (included in the repo and in the `.mcpb` bundle)
   into the CET mods folder:
   ```
   <game>/bin/x64/plugins/cyber_engine_tweaks/mods/CETBridge/
   ```
2. *(Optional, for TCP)* install **RedSocket** (RED4ext plugin).
3. **Launch the game** (or via the offline tool `launch_game`).
4. Verify from Claude: **`live_status`** → should report `connected: true` and the transport.

The MCP server has **no** additional dependency (pure network + JSON, in the server
process; the WolvenKit daemon is not involved).

## Configuration (environment variables, all optional)

| Variable | Default | Role |
|---|---|---|
| `CET_TRANSPORT` | `tcp` | `tcp` or `file` (forces the fallback, does not open the port). |
| `CET_TCP_PORT` | `27010` | Port of the TCP listener. |
| `CET_BRIDGE_DIR` | — | CETBridge mod folder (otherwise derived from the `gamePath` passed to the tools). |
| `CET_BRIDGE_TIMEOUT_MS` | `5000` | Maximum delay of a request. |

The tools' `gamePath` parameter serves the file fallback (locating the mod); it is useless
in TCP. In pure TCP, you can therefore omit it.

## Tools (35)

Every tool accepts an optional `gamePath` (string) — the root folder of the
Cyberpunk 2077 installation. It is **only** used by the file transport to locate the
mod; in TCP mode it can be omitted. The tables below list the **tool-specific**
parameters only.

> The `RO` / `D` / `I` tags after a tool name mean **read-only**, **destructive** and
> **idempotent** — they mirror the `[ReadOnly]` / `[Destructive]` / `[Idempotent]`
> attributes declared in `LiveTools.cs` (and enforced by `ConsistencyTests`).

### Foundation (unlocks everything)

| Tool | Parameters | Role |
|---|---|---|
| `live_status` `RO` `I` | — | Bridge connectivity (transport, heartbeat, folder). Works with the game shut down — call first. |
| `live_execute_lua` `D` | `code` (string) — Lua to run (one or more statements). | Executes Lua for side effects; `print()` output is captured. To read a value, prefer `live_eval`. |
| `live_eval` | `expression` (string) — Lua expression to evaluate. | Returns the serialized value (CET types handled: `CName`, `TweakDBID`, `Vector4`, `Quaternion`). |
| `live_batch` `D` | `commands` (string[]) — Lua snippets run sequentially. | Several statements in one round trip; each is independent (one failing does not stop the others). |

### State reading

| Tool | Parameters | Role |
|---|---|---|
| `live_player_info` `RO` `I` | — | Level, street cred, health, position. Requires the player spawned. |
| `live_game_state` `RO` `I` | — | In-game time, scene tier (gameplay/menu/cinematic), weather, zone type. |
| `live_inventory` `RO` `I` | `type` (string?) — filter by item type (Weapon/Clothing/Consumable/Gadget/Cyberware/Mod/Crafting/Quest/Junk). `limit` (int, default 50) — max items. | Player inventory (name, quantity, TweakDBID, quality). |
| `live_equipped` `RO` `I` | — | Equipped weapons by slot, clothing, cyberware, quickslots. |
| `live_active_effects` `RO` `I` | — | Active status effects (ID, remaining duration, stacks). |
| `live_appearance` `RO` `I` | — | Current appearance (appearance name + customization). |
| `live_vehicles` `RO` `I` | — | Vehicles owned (garage): names + TweakDBID. |
| `live_nearby_entities` `RO` `I` | `radius` (double, default 20) — search radius in meters. `type` (string?) — NPC/Vehicle/Item/Device. `limit` (int, default 20) — max entities. | Entities near the player (name, type, distance, position). |
| `live_scanner` `RO` `I` | — | Detail on the targeted entity, like a scan (type, name, health, level, faction). |

### Player & world mutation

| Tool | Parameters | Role |
|---|---|---|
| `live_add_item` | `itemId` (string) — item TweakDBID (e.g. `Items.Preset_Katana_Saburo`). `quantity` (int, default 1). | Adds an item to the inventory. |
| `live_remove_item` `D` | `itemId` (string) — TweakDBID. `quantity` (int?) — quantity to remove (omitted = all). | Removes an item from the inventory. |
| `live_teleport` `I` | `x`, `y`, `z` (double) — world coordinates. | Teleports the player (use `live_player_info` for the current position). |
| `live_set_stat` `D` `I` | `stat` (string) — Health/Stamina/Armor/Level/StreetCred. `value` (double) — new value. | Modifies a player stat (discover stats via `live_dump_type` `gamedataStatType`). |
| `live_apply_effect` | `effectId` (string) — effect TweakDBID (e.g. `BaseStatusEffect.Berserk`). | Applies a status effect to the player. |
| `live_remove_effect` `I` | `effectId` (string) — TweakDBID. | Removes a status effect from the player. |
| `live_god_mode` `I` | `enabled` (bool) — true = enable, false = disable. | Toggles player invulnerability (combat testing without dying). |
| `live_set_level` `D` `I` | `level` (int?) — 1-60. `streetCred` (int?) — 1-50. | Sets level and/or street cred directly. |
| `live_spawn_vehicle` | `vehicleId` (string) — TweakDBID (e.g. `Vehicle.v_sport2_quadra_type66`). `distance` (double, default 5) — meters in front of the player. | Spawns a vehicle near the player (find IDs via `live_tweakdb_search` `Vehicle.`). |
| `live_set_time` `I` | `hours` (int, 0-23). `minutes` (int, default 0). `seconds` (int, default 0). | Sets the in-game time of day. |
| `live_set_weather` `I` | `weather` (string) — preset (Sunny/Cloudy/Rain/HeavyRain/Fog/Toxic/Sandstorm/Pollution). | Changes the in-game weather. |
| `live_kill_nearby` `D` | `radius` (double, default 30) — meters. `allNpcs` (bool, default false) — true = all NPCs, false = hostiles only. | Kills NPCs within a radius. |
| `live_notify` | `message` (string) — text. | Shows a notification in the game UI. |
| `live_play_sound` | `soundEvent` (string) — sound event name (e.g. `ui_menu_hover`, `w_gun_reload`). | Plays a sound event in-game. |

### TweakDB in live memory + RTTI

| Tool | Parameters | Role |
|---|---|---|
| `live_tweakdb_get` `RO` `I` | `path` (string) — record/flat path (e.g. `Items.Preset_Katana_Saburo`). | Reads a flat or record **in live memory** (distinct from offline `read_tweak` / `tweakdb_query`). |
| `live_tweakdb_set` `D` `I` | `path` (string) — flat to modify. `value` (string) — text value (auto-detected, or forced via `type`). `type` (string?) — `Int` / `Float` / `Bool` / `String` / `CName`. | Writes a flat **in live memory** (persists until the game restarts; a bad value can crash the game). |
| `live_dump_type` `RO` `I` | `typeName` (string) — RTTI type (e.g. `PlayerPuppet`, `gameItemData`). | Introspects a live engine RTTI type (methods, properties, inheritance — distinct from offline `inspect_cr2w`). |
| `live_tweakdb_search` `RO` `I` | `pattern` (string) — substring, case-insensitive. `type` (string?) — record type filter (e.g. `gamedataItem_Record`). `limit` (int, default 20) — max results. | Searches TweakDB records **in live memory**. |

### Quests & events

| Tool | Parameters | Role |
|---|---|---|
| `live_get_quest_fact` `RO` `I` | `factName` (string) — quest fact (e.g. `q001_rogue_met`). | Reads a quest fact (progression flag). |
| `live_set_quest_fact` `D` `I` | `factName` (string) — fact name. `value` (int) — typically 0 (not done) or 1 (done). | Sets a quest fact (can break quest progression or unlock content). |
| `live_observe` | `className` (string) — game class (e.g. `PlayerPuppet`). `eventName` (string) — event/method (e.g. `OnDamageReceived`). `maxBuffer` (int, default 50) — buffer size before overwriting the oldest. | Subscribes to a game event via CET's Observe/ObserveAfter. |
| `live_observations` `RO` `I` | `subscriptionId` (string) — the ID returned by `live_observe`, **or** the `Class/Event` label (e.g. `PlayerPuppet/OnDamageReceived`). | Reads (and clears) the observed-event buffer. The label registry lives in the server and is lost on its restart. |

Everything goes through the same three protocol verbs (`exec` / `eval` / `query`):
the tools above are ergonomic shortcuts on top of named Lua handlers. Anything
else is doable via `live_execute_lua` / `live_eval`.

## Security & limits

> ⚠️ **Threat model — local code execution.** The bridge runs **arbitrary Lua inside
> the game process** (`live_execute_lua` / `live_eval`), and via CET that Lua can touch
> the filesystem and OS. The TCP listener is bound to **127.0.0.1 with no authentication**,
> so **any local process or user session on the machine** can connect to port 27010 and
> drive the game — or, by binding 27010 first, impersonate the bridge to the mod
> (first-bind-wins). This is acceptable for a **single-user development machine** but is a
> real exposure on shared/multi-user hosts. There is no public-network exposure (loopback
> only). An opt-in shared-secret handshake (`CET_BRIDGE_TOKEN`) is planned to close the
> local-process vector; until then, do not run the bridge on a host you do not trust.

- **Mutating `live_*` calls report submission, not confirmation.** Tools like
  `live_set_stat`, `live_set_time`, `live_set_weather`, `live_add_item` return success once
  the command was accepted by the game API — they do **not** re-read state to prove the
  effect landed. To verify, follow up with the matching read tool (`live_player_info`,
  `live_inventory`, `live_game_state`, …). Note also: a TCP **timeout** abandons the request
  on the server side, but the Lua handler may still run it — a destructive call that reports
  "timeout" may nonetheless have taken effect.
- **Executing Lua in the live game is powerful and risky**: an infinite loop can
  **freeze** the game. On the Lua side, each execution is protected by `pcall`; on the server side a
  **timeout** (5 s) protects the agent — **not** the game.
- The TCP listener is restricted to **127.0.0.1** (no public network — see threat model above).
- `live_tweakdb_set` writes persist **until the game restarts**.
- **Not testable in CI**: only the protocol layer is (see `CetBridgeProtocolTests`). The
  end-to-end path requires the game running — see `test-live-bridge.py`.

## Troubleshooting

`live_status` returns `connected: false`:
- Is the game running, out of the loading screen?
- Is the mod in `…/cyber_engine_tweaks/mods/CETBridge/`? (check the CET overlay → mod loaded)
- In TCP: is RedSocket installed? Otherwise force `CET_TRANSPORT=file` (+ `gamePath` or `CET_BRIDGE_DIR`).
- Port 27010 already taken (another session / cyber-engine-tweak-mcp) → the server switches to file;
  check `bridgeDir` in the `live_status` output.

## Provenance & license

The Lua mod `CETBridge/` is taken as-is from the **Y4rd13/cyber-engine-tweak-mcp** project
(**MIT** license, see `live-bridge/CETBridge/LICENSE.upstream`; `json.lua` = rxi, MIT). The
wire protocol (JSON `\r\n` frames, `exec`/`eval`/`query`, file fallback) is deliberately
identical, to reuse this mod without modifying it.
