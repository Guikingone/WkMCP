# WkMCP — an MCP server for Cyberpunk 2077 modding

An **MCP (Model Context Protocol)** server that exposes WolvenKit's modding CLI
(`cp77tools`) as **164 tools** an agent (Claude) can call — so you steer Cyberpunk
2077 modding from a chat window without writing code: read and edit game files,
query and patch the TweakDB, create/pack/install mods, export meshes and textures,
lint REDscript, diagnose a broken install, and even drive a **running** game live.

**Status: stable v2.0.0 · Windows.** Built on the official `ModelContextProtocol`
SDK 1.4.0 and [WolvenKit](https://github.com/WolvenKit/WolvenKit) 8.18.0; validated end-to-end on Windows 11 with a real
Cyberpunk 2077 install.

## Highlights

- **Read & write game files** — extract from archives, convert CR2W ↔ JSON, export meshes / textures / animations / audio.
- **TweakDB editing** — query `tweakdb.bin`, describe records, generate / validate / install `.tweak` (TweakXL).
- **Create, pack & install mods** — `.archive` mods, REDmods, REDscript, ArchiveXL items, localization.
- **Diagnostics** — `mod_doctor` (frameworks health), `lint_mod`, `detect_conflicts`, log analysis, conflict bisection.
- **REDscript lint** — a real grammar parser, line:column errors (0 false positives on 1374 real `.reds`).
- **Live in-game bridge** — 36 `live_*` tools to drive a *running* game (Lua, state, spawn, teleport, weather, in-memory TweakDB, event observation).
- **Scenes** — inspect, graph, validate, translate and edit `.scene` (quest/dialogue) files headlessly: catch broken dialogue graphs and edit subtitles without the scene editor.
- **Structured JSON** — every tool returns `{ ok, status, summary, produced, warnings, errors, log }`, reliable for an agent to parse.

## Quickstart (Windows, ~5 min)

1. **Prerequisites.** Windows 10/11, the **.NET 8 SDK** (<https://dotnet.microsoft.com/download>), the **WolvenKit CLI** (`dotnet tool install -g WolvenKit.CLI`), and Cyberpunk 2077 installed (its root folder is written `<GAME>` below).
2. **Build** — the daemon first (its build deploys `kraken.dll` and `DirectXTexNet.dll`), then the server:
   ```powershell
   dotnet build src\WkDaemon
   dotnet build src\WkMcp
   ```
   The server is at `src\WkMcp\bin\Debug\net8.0\WkMcp.dll`.
3. **Wire it to Claude Desktop** — edit `%APPDATA%\Claude\claude_desktop_config.json`:
   ```json
   {
     "mcpServers": {
       "wolvenkit": {
         "command": "dotnet",
         "args": ["C:\\path\\to\\wkmcp\\src\\WkMcp\\bin\\Debug\\net8.0\\WkMcp.dll"]
       }
     }
   }
   ```
   Restart Claude Desktop. (Claude Code: `claude mcp add wolvenkit -s user -- dotnet "C:\path\to\...\WkMcp.dll"`.)
4. **Verify.** Ask Claude to call **`wk_status`** — it returns the CLI version and the cache/metrics stats, confirming the server is live.
5. **Call a tool.** Try `find_in_archives` with `archivesFolder = <GAME>\archive\pc\content` and `pattern = *player*.ent`. Every tool returns the same structured shape:
   ```json
   { "ok": true, "status": "success", "summary": "…", "produced": [],
     "warnings": [], "errors": [], "log": "…" }
   ```
   Judge success on `produced` (files actually created) and `ok`, not the log text. See [docs/TOOLS.md](docs/TOOLS.md) for every tool's parameters, and [docs/USER_GUIDE.md](docs/USER_GUIDE.md) for the full walkthrough.

> Prefer a one-click install? Once a release is published, download
> `wkmcp.mcpb` from the [latest release](https://github.com/Guikingone/WkMCP/releases)
> and add it to Claude Desktop as a Desktop Extension (Developer Mode) — see [Installation](#installation-windows).
>
> **Where to get it:** the binary bundle (`wkmcp.mcpb`) ships on **GitHub Releases**.
> The [Nexus Mods page](https://www.nexusmods.com/cyberpunk2077/mods/30526) hosts a
> no-binary companion (`wkmcp-nexus.zip` = this README + the optional CETBridge Lua
> mod), because Nexus does not host executables — the `.mcpb` is downloaded from GitHub.

## Installation (Windows)

The Quickstart above is the standard path. In detail:

1. Install the **.NET 8 SDK** or higher — <https://dotnet.microsoft.com/download>.
2. Install the WolvenKit CLI: `dotnet tool install -g WolvenKit.CLI` (puts `cp77tools` in `~\.dotnet\tools\`).
3. Build the **daemon then the server** — the daemon build automatically deploys the Windows native binaries (`kraken.dll` for Oodle, `DirectXTexNet.dll` for textures):
   ```powershell
   dotnet build src\WkDaemon
   dotnet build src\WkMcp
   ```
4. Wire it to a client (see [Wire up to a client](#wire-up-to-a-client)), or install the **Desktop Extension** bundle (`.mcpb`, built by `build-mcpb.ps1` and attached to each GitHub Release) for one-click setup in Claude Desktop.

No `DOTNET_ROOT` is needed on Windows. On Windows, `cp77tools` also handles texture conversion and Wwise audio (the native binaries are present).

## Wire up to a client

**Claude Desktop** — `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "wolvenkit": {
      "command": "dotnet",
      "args": ["C:\\path\\to\\wkmcp\\src\\WkMcp\\bin\\Debug\\net8.0\\WkMcp.dll"]
    }
  }
}
```

Restart Claude Desktop after editing.

**Claude Code:**

```powershell
claude mcp add wolvenkit -s user -- dotnet "C:\path\to\wkmcp\src\WkMcp\bin\Debug\net8.0\WkMcp.dll"
```

Optional environment variables go in the `"env"` block (Desktop) or via `-e VAR=value` (`claude mcp add`) — see [Configuration](#configuration-environment-variables).

## Supported platforms

- **Windows** — the validated v1 platform. Native Oodle (`kraken.dll`), texture conversion (`DirectXTexNet.dll`) and Wwise audio are all available; the `.mcpb` bundle ships Windows binaries.
- **macOS (experimental, build-from-source only)** — works on Apple Silicon after rebuilding `libkraken.dylib` (see [`native/README.md`](native/README.md)); texture conversion and Wwise audio are **not** supported there. Not shipped in the `.mcpb`, not validated for release.

## Tools, prompts, resources

The server exposes **164 tools** (128 offline + 36 `live_*`), **11 prompts** and **4 resources**.

**Tools by category** (full parameter reference: [docs/TOOLS.md](docs/TOOLS.md)):

- *Diagnostics* — `wk_status`, `clear_cache`, `compute_hash`, `resolve_hash`, `tweakdb_resolve`, `tweakdb_query`, `find_record_by_name`.
- *Archives* — `archive_info`, `archive_stats`, `find_in_archives`, `find_and_extract` (locate + extract across all archives in a folder), `diff_archives`, `diff_against_installed`, `extract_files`, `uncook`.
- *Conversion / export* — `cr2w_to_json`, `json_to_cr2w`, `export_files`, `export_animation`, `export_morphtarget`, `export_mlmask`, `export_entity`, `export_materials`, `set_texture_format`.
- *Read / write game files* — `read_game_file`, `write_game_file`, `inspect_mesh`, `inspect_texture`, `inspect_app`.
- *Import (raw → cooked, round-trip)* — `import_texture` (png/dds → .xbm), `import_mesh` (glTF → .mesh), `import_anim` (glTF → .anims), `import_morphtarget` (glTF → .morphtarget), `import_mlmask` (→ .mlmask), `import_material` (→ .mi), `import_raw` (generic); all support `keep` for the export → edit → reimport flow (mirror the `export_*` family).
- *TweakDB* — `describe_tweak_record`, `clone_tweak_record` (faithful `$base` clone of an existing record + commented value inventory), `read_tweak`, `write_tweak`, `validate_tweak`, `preview_tweak`, `install_tweak`, `dump_records`, `generate_tweak_template`.
- *REDscript* — `read_script`, `lint_script`, `script_api_index`, `type_check_scripts`, `generate_redscript_template`.
- *Audio / compression* — `wwise_export`, `extract_audio`, `import_audio`, `loc_resolve`, `oodle_compress`, `oodle_decompress`.
- *Localization* — `extract_localization`, `build_localization`.
- *Mod creation / packing* — `pack_archive`, `import_raw`, `build_project`, `create_mod_project`, `generate_modproj`, `lint_mod`, `mod_summary`.
- *REDmod* — `create_redmod_project`, `pack_redmod`, `validate_redmod`, `install_redmod`, `deploy_redmod`.
- *Install / uninstall* — `install_mod`, `uninstall_mod`, `uninstall_redmod`, `uninstall_tweak`, `list_installed_mods`, `detect_conflicts`.
- *Safety* — `backup_mods`, `restore_mods`.
- *In-game* — `launch_game`, `tail_game_logs`.
- *Workflow / intelligence* (23 high-level) — `analyze_dependencies`, `check_requirements`, `mod_doctor`, `validate_xl`, `scaffold_archivexl`, `find_references`, `diff_mod_vs_base`, `scaffold_mod`, `package_mod`, `inspect_journal`, `find_journal_entry`, `inspect_cr2w`, `find_in_cr2w`, `diagnose_logs`, `analyze_conflicts`, `validate_item_mod`, `lint_tweak`, `generate_manifest`, `resolve_dynamic_appearance`, `migration_check`, `toggle_mods`, `list_entity_appearances`, `validate_appearance`.
- *Asset inspection* (10) — `inspect_material` (.mi), `inspect_mlsetup`, `edit_material_instance`, `trace_material_chain` (mesh → .mi → .mlsetup → textures), `inspect_inkatlas` / `resolve_inkatlas_part` (UI sprites), `inspect_inkwidget` (HUD/menus), `inspect_rig` (skeletons), `diff_cr2w` (generic two-file diff), `package_for_nexus` (Nexus pre-flight + zip).
- *Scenes (.scene)* (11) — `inspect_scene`, `scene_graph` (flow), `find_in_scene`, `validate_scene` (graph + dialogue integrity), `scene_dependencies` (external refs), `scene_events` (timeline), `extract_scene_localization` / `apply_scene_localization` (translation), `scene_set_actor` / `scene_replace_resource` (edit), `scaffold_scene` (new scene). See [docs/SCENES.md](docs/SCENES.md).
- *Gameplay logic* (2) — `inspect_questphase` (.questphase quest graph: nodes/edges, entry/exit, scene & sub-phase refs), `inspect_community` (.community population: spawn entries, characters, phases & Day/Night quantities).
- *Appearance authoring* (3) — `add_appearance` (add an appearance to a `.app`), `set_mesh_material` (set a component's `meshAppearance` material selector + optional mesh swap), `scaffold_appearance_mod` (ArchiveXL appearance-swap mod skeleton).
- *Live in-game* (36 `live_*`) — drive a running game via the CETBridge mod; see [docs/LIVE_BRIDGE.md](docs/LIVE_BRIDGE.md).

### MCP prompts (11)

Ready-to-use recipes an agent can invoke to kick off a workflow.

| Prompt | Role |
|---|---|
| `read_game_file_workflow` | Locate and read a game file in one call |
| `edit_tweakdb_item` | Modify a TweakDB item via `.tweak` (TweakXL) |
| `pack_and_install_mod` | Pack and install a `.archive` mod |
| `recolor_texture` | Extract / edit / re-import a texture |
| `create_archivexl_item` | Create an ArchiveXL item mod end-to-end |
| `diagnose_broken_mod` | Global diagnostic → logs → bisection of a broken mod |
| `live_iteration_loop` | Iterate on TweakDB live, game running |
| `inspect_mesh` | Export a mesh to glTF for inspection |
| `translate_scene` | Extract, edit and write back a `.scene`'s dialogue (headless translation) |
| `audit_scene` | Audit a `.scene`: structure, integrity, dependencies, and what it plays |
| `import_art` | The export → edit → reimport round-trip for textures / meshes / animations |

### MCP resources (4)

Readable data addressed by URI that the client can consult or attach to context.

| URI | Type | Content |
|---|---|---|
| `wkmcp://reference` | direct | Cheat sheet: commands, REDengine formats, modding workflow |
| `wkmcp://archive/{+path}` | template | Listing of the `.archive` content at the given path |
| `wkmcp://cr2w-json/{+path}` | template | REDengine CR2W file rendered as JSON |
| `wkmcp://mods/{+gamePath}` | template | Inventory of installed mods (`.archive` archives + REDmod) |

## Architecture

C# / .NET 8 MCP server. Tool calls go through a **persistent WolvenKit daemon**: the expensive load of the hash database (~6 s) is paid once at daemon startup; subsequent calls cost only a few milliseconds. If the daemon is unavailable, each call falls back to a `cp77tools` subprocess (functional, but ~6 s/call).

```
Claude ─MCP/JSON-RPC─▶ WkMcp ─IPC stdio─▶ WkDaemon ─▶ WolvenKit libs + libkraken
                                    └─fallback─▶ cp77tools (subprocess) if daemon unavailable
```

The daemon links WolvenKit's GPL-3.0 libraries, so it runs as a **separate process** — the MIT-licensed MCP server only talks to it over IPC and stays out of the copyleft. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for IPC, caching, the parser, and how to add a tool or daemon verb.

## Configuration (environment variables)

All optional. Set in the `"env"` block of the Claude Desktop config, or via `-e VAR=value` with `claude mcp add`.

| Variable | Default | Role |
|---|---|---|
| `WKMCP_DAEMON` | sibling project | Path to `WkDaemon.dll` (the fast path) |
| `WKMCP_CP77TOOLS` | `~\.dotnet\tools\cp77tools.exe` | Path to cp77tools (subprocess fallback) |
| `WKMCP_DOTNET_ROOT` / `DOTNET_ROOT` | auto-detected | .NET runtime root — rarely needs setting on Windows |
| `WKMCP_CLI_TIMEOUT_SECONDS` | `300` | Maximum delay for a command (seconds) |
| `WKMCP_TRANSPORT` | `stdio` | `stdio` (default) or `http` (Streamable HTTP — see [docs/HTTP_TRANSPORT.md](docs/HTTP_TRANSPORT.md)) |

## License

This project is **dual-licensed by component** (see [`NOTICE`](NOTICE)):

- **Server source** (`src/WkMcp`, the `.py` test helpers, `build-mcpb.ps1`) — **MIT**, © 2026 Guillaume Loulier ([`LICENCE`](LICENCE)).
- **Daemon** (`src/WkDaemon`) — **GPL-3.0**, because it links [WolvenKit](https://github.com/WolvenKit/WolvenKit)'s GPL-3.0 libraries (© WolvenKit contributors; [`src/WkDaemon/LICENSE`](src/WkDaemon/LICENSE)). It runs as a separate process, so the MIT server stays out of the copyleft.
- **CETBridge** (`live-bridge/CETBridge`) — **MIT**, © y4rd13 ([`live-bridge/CETBridge/LICENSE.upstream`](live-bridge/CETBridge/LICENSE.upstream)).
- **The shipped `.mcpb` bundle** includes the daemon, so that portion is GPL-3.0 (matches the `license` field in `manifest.json`).

## Credits

This project stands on the work of the Cyberpunk 2077 modding community — full attributions in [`NOTICE`](NOTICE):

- **[WolvenKit](https://github.com/WolvenKit/WolvenKit)** (GPL-3.0) — the modding toolkit and CLI this server wraps.
- **[Cyber Engine Tweaks](https://github.com/maximegmd/CyberEngineTweaks)** — hosts the live in-game Lua bridge.
- **[CETBridge](https://github.com/Y4rd13/cyber-engine-tweak-mcp)** by y4rd13 (MIT) — the live-bridge Lua mod.
- **[ooz](https://github.com/powzix/ooz)** (powzix / rarten) — the open-source Kraken decompressor (no proprietary Oodle is redistributed).

WolvenKit is licensed GPL-3.0; that license is itself the permission to build on, wrap and redistribute it — this project complies (the daemon is GPL-3.0, notices preserved, full source here). It is **not** affiliated with or endorsed by the WolvenKit team.

## Documentation

- **[docs/USER_GUIDE.md](docs/USER_GUIDE.md)** — modder's guide: install, wire up to Claude, step-by-step workflows (read a file, edit a tweak, create/pack/install a mod, check dependencies, package).
- **[docs/TOOLS.md](docs/TOOLS.md)** — exhaustive reference of the 164 tools + 11 prompts + 4 resources (parameters included).
- **[docs/MODDING_RECIPES.md](docs/MODDING_RECIPES.md)** — copy-paste recipes by mod type (tweak, redscript, ArchiveXL, REDmod, localization, texture, analysis).
- **[docs/LIVE_BRIDGE.md](docs/LIVE_BRIDGE.md)** — the 36 `live_*` tools to drive a running game (CETBridge / Cyber Engine Tweaks). Optional, separate prerequisites.
- **[docs/HTTP_TRANSPORT.md](docs/HTTP_TRANSPORT.md)** — remote access over HTTP/Streamable (instead of stdio); secure by default.
- **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** — for contributors: IPC, cache, parser, and how to add an MCP tool or daemon verb.

## Contributing & security

- [Contributing guide](.github/CONTRIBUTING.md) — build/test, how to add a tool, and the `ConsistencyTests` anti-drift contract you must keep green.
- [Security policy](.github/SECURITY.md) — how to report a vulnerability (please use private GitHub security advisories, not public issues).
- [Code of conduct](.github/CODE_OF_CONDUCT.md) — Contributor Covenant 2.1.

## Troubleshooting

- **`wk_status` returns `ok: false`** — `cp77tools` not found: `dotnet tool install -g WolvenKit.CLI`, or set `WKMCP_CP77TOOLS`.
- **First call is slow (~7 s)** — daemon warmup; one-time, then instant.
- **Every call is slow (~6 s/call)** — the daemon isn't being used; build it or set `WKMCP_DAEMON`.
- **A tool "fails" but files were produced** — judge on `produced` and `ok`, not the `log`; rerun with `verbose: true`.
- **A mod won't load in-game** — run `mod_doctor` (frameworks), `lint_mod` (extensions), `detect_conflicts` (overrides); confirm the archive is in `archive\pc\mod`.
- **`dotnet build` fails with "file in use"** — the daemon DLLs are locked by a running server; stop the MCP client / kill `WkDaemon` and `dotnet`, then rebuild.

More in [docs/USER_GUIDE.md](docs/USER_GUIDE.md) (§10 troubleshooting) and [docs/MODDING_RECIPES.md](docs/MODDING_RECIPES.md).

## Structure

```
wkmcp/
├── src/WkMcp/         C# / .NET 8 MCP server (164 tools, 11 prompts, 4 resources)
│   ├── Program.cs            Host + stdio/http transport + DI + daemon warmup
│   ├── Cp77ToolsRunner.cs    Drives the daemon (pipelined IPC, LRU cache, cp77tools fallback)
│   ├── WolvenKitTools.cs     77 base MCP tools + helpers
│   ├── ModdingTools.cs       28 workflow tools (deps, health, scaffolding, refs, diff, appearance)
│   ├── AssetInspectionTools.cs 10 asset-inspection tools (materials / UI / rig / diff / Nexus) — partial of ModdingTools
│   ├── GameplayInspectionTools.cs 2 gameplay-logic tools (.questphase / .community) — partial of ModdingTools
│   ├── SceneTools.cs         11 scene (.scene) tools (inspect / graph / validate / translate / edit)
│   ├── LiveTools.cs          36 live_* tools (running game, via CetBridge.cs)
│   ├── CetBridge.cs          TCP/file bridge to the CETBridge Lua mod
│   ├── RedscriptParser.cs    REDscript grammar parser (lint_script, script_api_index)
│   ├── ScriptApi.cs          REDscript symbol index (script_api_index)
│   ├── SccDiagnostics.cs     scc output parser (type_check_scripts)
│   ├── TweakValidation.cs    TweakXL typed-validation / preview core (validate_tweak, preview_tweak)
│   ├── WolvenKitPrompts.cs   11 MCP prompts (recipes)
│   └── WolvenKitResources.cs 4 MCP resources (reference generated by reflection)
├── src/WkDaemon/      Persistent daemon — links the WolvenKit GPL-3.0 libraries
├── src/WkMcp.Tests/   xUnit tests (incl. ConsistencyTests anti-drift guard)
├── live-bridge/CETBridge/    Lua mod driven by the live_* tools (MIT, upstream)
├── native/                   macOS-only libkraken.dylib arm64 rebuild (experimental)
├── docs/                     USER_GUIDE · TOOLS · MODDING_RECIPES · LIVE_BRIDGE · HTTP_TRANSPORT · ARCHITECTURE
├── dev/                      Internal QA logs (e.g. WINDOWS-VALIDATION.md) — not user docs
├── .github/                  CONTRIBUTING · SECURITY · CODE_OF_CONDUCT · issue/PR templates · workflows
├── manifest.json             Desktop Extension manifest (.mcpb)
├── build-mcpb.ps1            Builds dist/wkmcp.mcpb (one-click install)
├── LICENCE                   MIT (server source)
├── NOTICE                    Per-component license split (MIT server + GPL-3.0 daemon)
└── README.md
```