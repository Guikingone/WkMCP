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

### Foundation (unlocks everything)
| Tool | Role |
|---|---|
| `live_status` | Bridge connectivity (transport, heartbeat, folder). Works with the game shut down. |
| `live_execute_lua` | Executes Lua (side effects; `print()` output captured). |
| `live_eval` | Evaluates a Lua expression and returns its value (CET types serialized). |
| `live_batch` | Several Lua statements in one round trip. |

### State reading
`live_player_info`, `live_game_state`, `live_inventory`, `live_equipped`,
`live_active_effects`, `live_appearance`, `live_vehicles`, `live_nearby_entities`,
`live_scanner`.

### Player & world mutation
`live_add_item`, `live_remove_item`, `live_teleport`, `live_set_stat`, `live_apply_effect`,
`live_remove_effect`, `live_god_mode`, `live_set_level`, `live_spawn_vehicle`,
`live_set_time`, `live_set_weather`, `live_kill_nearby`, `live_notify`, `live_play_sound`.

### TweakDB in live memory + RTTI
`live_tweakdb_get`, `live_tweakdb_set`, `live_dump_type`, `live_tweakdb_search`.

### Quests & events
`live_get_quest_fact`, `live_set_quest_fact`, `live_observe`, `live_observations`.

Everything goes through the same 3 protocol verbs (`exec`/`eval`/`query`): the first-class
tools above are merely ergonomic shortcuts on top of named Lua handlers.
Anything else is doable via `live_execute_lua` / `live_eval`.

## Security & limits (no beating around the bush)

- **Executing Lua in the live game is powerful and risky**: an infinite loop can
  **freeze** the game. On the Lua side, each execution is protected by `pcall`; on the server side a
  **timeout** (5 s) protects the agent — **not** the game.
- The TCP listener is restricted to **127.0.0.1** (no public network, no auth — a local
  development tool).
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
