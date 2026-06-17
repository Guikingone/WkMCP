# WolvenKit MCP — MCP server for Cyberpunk 2077 modding

Prototype **MCP (Model Context Protocol)** server exposing the WolvenKit modding
CLI (`cp77tools`) as tools usable by Claude.

**Status: working prototype**, read + write, validated end-to-end on
**Windows 11** (with Cyberpunk 2077 installed) and on macOS Apple Silicon.

## Why the project has two parts

WolvenKit does not work as-is on macOS: the `WolvenKit.CLI` NuGet package
ships a native Oodle library (`libkraken`) that is broken on Apple Silicon —
x86_64 only, with mangled C++ symbols. So two things were needed:

1. **`native/`** — rebuild `libkraken.dylib` as native arm64, compression +
   decompression (see `native/README.md`);
2. **`src/`** — the MCP server itself, in C# / .NET 8.

## Architecture

**C# / .NET 8** MCP server (official `ModelContextProtocol` SDK 1.3.0). Tool
calls go through a **persistent WolvenKit daemon**: the expensive load of the
hash database (~6 s) is paid only once, at daemon startup; subsequent calls cost
only a few milliseconds.

```
Claude ─MCP/JSON-RPC─▶ WolvenKitMcp ─IPC stdio─▶ WolvenKitDaemon ─▶ WolvenKit libs + libkraken
                                    └─fallback─▶ cp77tools (subprocess) if daemon unavailable
```

The daemon — which links WolvenKit's GPL-3.0 libraries — is a **separate**
process: the MCP server only talks to it over IPC, so it stays outside the scope
of the copyleft. If the daemon is unavailable, each call falls back to a
`cp77tools` subprocess (functional, but ~6 s/call).

## Documentation

Detailed documentation in [`docs/`](docs/):

- **[docs/USER_GUIDE.md](docs/USER_GUIDE.md)** — modder's guide: installation, wiring up to Claude, and step-by-step workflows (read a file, edit a tweak, create/pack/install a mod, check dependencies, package).
- **[docs/TOOLS.md](docs/TOOLS.md)** — exhaustive reference of the 123 tools + 8 prompts + 4 resources (parameters included).
- **[docs/MODDING_RECIPES.md](docs/MODDING_RECIPES.md)** — copy-paste recipes by mod type (tweak, redscript, ArchiveXL, REDmod, localization, texture, analysis).
- **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** — for contributors: IPC, cache, parser, and how to add an MCP tool or a daemon verb.
- **[docs/LIVE_BRIDGE.md](docs/LIVE_BRIDGE.md)** — **live in-game** bridge: 35 `live_*` tools to drive a **running** game (Lua execution, state, spawn, teleport, weather, in-memory TweakDB, observation) via the CETBridge / Cyber Engine Tweaks mod. Optional, separate prerequisites.
- **[docs/HTTP_TRANSPORT.md](docs/HTTP_TRANSPORT.md)** — **remote access**: the server can run over **HTTP/Streamable** (instead of stdio) via `WOLVENKIT_MCP_TRANSPORT=http`. Secure by default (loopback bind + bearer token + fail-closed). Opt-in.

## Prerequisites

- macOS Apple Silicon (arm64)
- .NET 8 SDK — `brew install dotnet@8`
- WolvenKit CLI — `dotnet tool install -g WolvenKit.CLI`
- `libkraken.dylib` arm64 deployed (step 1 below)

## Installation

### 1. Rebuild and deploy libkraken (once)

```sh
cd native
./build-libkraken.sh
PKG=~/.dotnet/tools/.store/wolvenkit.cli/8.18.0/wolvenkit.cli/8.18.0/tools/net8.0/any
mkdir -p "$PKG/runtimes/osx-arm64/native"
cp build/libkraken.dylib "$PKG/runtimes/osx-arm64/native/"
```

### 2. Build the daemon and the MCP server

```sh
dotnet build src/WolvenKitDaemon   # its build deploys libkraken.dylib there
dotnet build src/WolvenKitMcp
```

### 3. Test

```sh
python3 test-daemon.py       # daemon alone — per-request latency
python3 test-mcp-server.py   # end-to-end MCP server
```

## Wire up to a client

### Claude Desktop

`~/Library/Application Support/Claude/claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "wolvenkit": {
      "command": "/opt/homebrew/opt/dotnet@8/libexec/dotnet",
      "args": ["ABSOLUTE/PATH/wolvenkit-mcp/src/WolvenKitMcp/bin/Debug/net8.0/WolvenKitMcp.dll"],
      "env": { "DOTNET_ROOT": "/opt/homebrew/opt/dotnet@8/libexec" }
    }
  }
}
```

### Claude Code

```sh
claude mcp add wolvenkit \
  -e DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec \
  -- /opt/homebrew/opt/dotnet@8/libexec/dotnet \
     ABSOLUTE/PATH/wolvenkit-mcp/src/WolvenKitMcp/bin/Debug/net8.0/WolvenKitMcp.dll
```

## Installation on Windows

On Windows, `cp77tools` works natively — the `native/` folder
(libkraken rebuild for macOS) is unnecessary and can be ignored.

1. Install the .NET 8 SDK or higher — https://dotnet.microsoft.com/download
2. Install the WolvenKit CLI: `dotnet tool install -g WolvenKit.CLI`
3. Build the daemon **then** the server:
   `dotnet build src\WolvenKitDaemon` then `dotnet build src\WolvenKitMcp`
   (the daemon build automatically deploys `kraken.dll` and `DirectXTexNet.dll`)
4. Wire up to Claude Desktop (`%APPDATA%\Claude\claude_desktop_config.json`):

   ```json
   {
     "mcpServers": {
       "wolvenkit": {
         "command": "dotnet",
         "args": ["C:\\path\\to\\wolvenkit-mcp\\src\\WolvenKitMcp\\bin\\Debug\\net8.0\\WolvenKitMcp.dll"]
       }
     }
   }
   ```

   or Claude Code: `claude mcp add wolvenkit -s user -- dotnet "C:\path\...\WolvenKitMcp.dll"`

No `DOTNET_ROOT` variable is needed on Windows. `cp77tools` there also handles
textures and audio (Windows native binaries present).

## Exposed tools (88 "classic": 63 base + 25 workflow — +35 `live_*` = 123 total)

Each tool returns a **structured JSON** result (`ok`, `status`, `summary`,
`produced`, `warnings`, `errors`, `log`) — reliable for an agent to parse. The
bulky log is truncated while preserving head + errors + tail.

| Tool | Type | Role |
|---|---|---|
| `wolvenkit_status` | diagnostic | Availability and version of cp77tools + **LRU cache stats** (hits/misses) + **per-verb metrics** (p50/p95) |
| `compute_hash` | diagnostic | FNV1a64 hash of strings (file paths) |
| `resolve_hash` | diagnostic | Reverse lookup: FNV1a64 hash → game file path |
| `archive_info` | read | Information / listing of a `.archive` (LRU cache) |
| `archive_stats` | read | Breakdown of a `.archive`'s content by extension (how many `.mesh`, `.ent`, `.xbm`…) without listing everything (LRU cache) |
| `find_in_archives` | read | Search for a file across all archives in a folder (LRU cache, ×6 faster on subsequent calls) |
| `diff_archives` | read | Compare two `.archive` files (internal additions / removals) |
| `extract_files` | read | Extract files from an archive (glob/regex) |
| `uncook` | read | Extraction + conversion in one pass (mesh → glTF, textures → image). Flags: `meshExportType`, `meshExporterType`, `meshExportLodFilter` |
| `export_animation` | export | Extracted `.anims` animation → binary glTF (`.glb`) — explicit dedicated tool |
| `export_morphtarget` | export | Morphtarget `.morphtarget` (blendshapes) → binary glTF (`.glb`) |
| `export_mlmask` | export | Multilayer mask `.mlmask` → images (one per layer), adjustable format (`textureFormat`) |
| `export_entity` | export | ⚠ exp. — entity appearance `.ent` → glTF (`IModTools.ExportEntity`; requires an entity carrying appearances + the appearance name) |
| `export_materials` | export | Materials of a `.mesh` → JSON + textures (`IModTools.ExportMaterials`, `gamePath` to resolve base materials) |
| `extract_audio` | audio | Extracts voice-over audio (opus) from a voice archive; all or targeted `opusHashes` |
| `import_audio` | audio | ⚠ exp. — WAV (named by opus hash) → `.opus` repacked into a mod (embedded `opusenc`) |
| `loc_resolve` | localization | ⚠ exp. — LocKey (hash or key) → localized text (M/F variants) via the game's on-screens |
| `detect_conflicts` | read | Conflicts between installed mods (structured JSON output) |
| `cr2w_to_json` | conversion | REDengine CR2W → editable JSON |
| `json_to_cr2w` | conversion | JSON → CR2W |
| `export_files` | conversion | Extracted REDengine files → raw formats |
| `read_game_file` | read | Reads a game file as JSON in a single call (extract + convert) |
| `write_game_file` | write | Writes an edited game file (JSON → CR2W placed for `pack_archive`) |
| `wwise_export` | audio | Wwise WEM audio → OGG (Windows). Conversions in parallel (≤ 4) |
| `oodle_compress` | utility | Oodle Kraken compression of a file |
| `oodle_decompress` | utility | Oodle Kraken decompression of a file |
| `pack_archive` | write | Packs a folder into a `.archive` |
| `import_raw` | write | Imports raw files into REDengine CR2W |
| `build_project` | write | Compiles a WolvenKit `.cpmodproj` project → `packed/archive/pc/mod/<mod>.archive` (chains with `create_mod_project` / `generate_modproj`) |
| `lint_mod` | write | Pre-install lint: non-REDengine extensions, conflicts with installed mods |
| `install_mod` | write | Installs a mod archive into the game's `archive/pc/mod` |
| `create_mod_project` | workflow | Creates a mod project structure (`source/{archive,raw,resources,customSounds}`, `packed`) **+ a `.cpmodproj`** directly compilable by `build_project` |
| `generate_modproj` | workflow | Generates a `.cpmodproj` (XML `<CP77Mod>`) in an existing project folder — makes a project without one compilable |
| `create_redmod_project` | redmod | Creates a REDmod project (`mods/<name>/info.json` + subfolders) |
| `pack_redmod` | redmod | Packs a REDmod into a `.zip` for distribution |
| `install_redmod` | redmod | Installs a REDmod into `<game>/mods/<name>/` |
| `list_installed_mods` | workflow | Lists installed mods of a game folder (archive + REDmod) |
| `read_tweak` | tweakdb | Reads a `.tweak` file (TweakXL — YAML) as editable JSON |
| `write_tweak` | tweakdb | Reconverts edited JSON into a `.tweak` (YAML) |
| `validate_tweak` | tweakdb | Validates a `.tweak` against `tweakdb.bin` (unknown keys detected) |
| `install_tweak` | tweakdb | Copies a `.tweak` into `<game>/r6/tweaks/` |
| `tweakdb_resolve` | tweakdb | Reverse lookup: TweakDB identifier hash → name |
| `tweakdb_query` | tweakdb | Loads a `tweakdb.bin` and lists filtered records / flats (cap 100 + `truncated` field) |
| `describe_tweak_record` | tweakdb | For a TweakDB record, lists all its flats with types and values |
| `generate_tweak_template` | tweakdb | Scaffolds `.tweak` (patterns: `override_field`, `new_record`, `boost_stat`) |
| `inspect_mesh` | inspection | Summary of a `.mesh` (LODs, submeshes, materials, bones) without a full uncook |
| `inspect_texture` | inspection | Metadata of a `.xbm` (resolution, format, mipmaps) without conversion |
| `read_script` | scripts | Reads a `.reds` / `.script` file + extracts its structure (func, class, @addMethod...) |
| `lint_script` | scripts | **Real REDscript grammar parser** (tokenizer + recursive descent): syntax errors with line:column (signatures/types/generics, `(){}[]` matching, strings) **+ semantic checks** (well-targeted annotations, `@wrapMethod`→`wrappedMethod()`, duplicates). Calibrated to **0 false positives** on 1374 real `.reds` |
| `backup_mods` | safety | Timestamped ZIP snapshot of `archive/pc/mod` + `mods/` + `r6/tweaks/` |
| `restore_mods` | safety | Restores a ZIP backup (`merge` / `replace` modes) |
| `uninstall_mod` | uninstall | Removes a `.archive` from `archive/pc/mod/` (sandbox safeguard) |
| `uninstall_redmod` | uninstall | Recursively removes `mods/<name>/` |
| `uninstall_tweak` | uninstall | Removes a `.tweak` from `r6/tweaks/` |
| `deploy_redmod` | redmod | Wraps `redMod.exe deploy` (compiles scripts + applies tweaks) |
| `launch_game` | in-game | ⚠ Launches `Cyberpunk2077.exe` (with optional prior `deploy_redmod`) |
| `tail_game_logs` | in-game | Tails `r6/logs/*.log` + `tools/redmod/logs/` logs (game / redmod / redscript / all) |
| `mod_summary` | intelligence | Compact summary of a mod: .archive (by extension) or REDmod (info.json + tweaks + scripts) |
| `dump_records` | intelligence | Exports all TweakDB records of a type to JSONL / CSV (e.g. all weapons) |
| `generate_redscript_template` | scaffolds | Scaffolds `.reds` (add_method, wrap_method, replace_method, add_field, new_class) |
| `extract_localization` | localization | Extracts from TweakDB all translatable fields (displayName, etc.) as JSON |
| `build_localization` | localization | Builds a translation `.tweak` from a `{recordId: {field: value}}` JSON |
| `clear_cache` | maintenance | Manually clears the LRU cache or the metrics (`scope` ∈ archives / metrics / all) |

### High-level workflow tools (25)

Compose the primitives above + ecosystem knowledge to simplify the
**creation / evolution / maintenance** of mods.

| Tool | Type | Role |
|---|---|---|
| `analyze_dependencies` | maintenance | Infers a mod's required frameworks (redscript, RED4ext, ArchiveXL, TweakXL, Codeware, Audioware, Mod Settings, CET…) via imports/.xl/.tweak/types, and marks installed/missing if `gamePath` is provided |
| `check_requirements` | maintenance | Inventory of the modding frameworks **installed** (+ version) in an install |
| `mod_doctor` | maintenance | One-call health diagnostic: installed/missing frameworks, dependencies required by the present content but absent, conflicts, inventory + recommendations |
| `validate_xl` | archivexl | Validates an ArchiveXL `.xl` file (well-formed YAML + recognized sections) |
| `scaffold_archivexl` | archivexl | Generates a commented starter `.xl` (factory / customSounds / localization / resource) |
| `find_references` | evolution | Searches all references (TweakDBID / path / LocKey / name) in a mod's sources (.reds/.tweak/.yaml/.xl/.lua/.json/.csv) → file:line |
| `diff_mod_vs_base` | evolution | Semantic diff of an overridden file vs its base version (additions/removals/changes, `$.Header` noise filtered) |
| `scaffold_mod` | creation | Creates in 1 call a working mod (archive / redscript / tweak / redmod) + `MOD_MANIFEST.json` (type, declared deps) |
| `package_mod` | distribution | Packs a game layout (`archive/`, `r6/`, `mods/`…) into a distributable `.zip` (compliant `/` separators) |
| `inspect_journal` | journal | Navigable summary of a `.journal` (28,000+ entries, ~70 MB): total, breakdown by `$type`, top-level categories — without loading everything |
| `find_journal_entry` | journal | Locates a journal entry by id / type / title → **exact JSON path** to edit it then re-inject via `write_game_file` |
| `inspect_cr2w` | navigation | Navigable summary of ANY large CR2W (quest/scene/sector/UI): root type, objects by `$type`, depth — generalizes `inspect_journal` |
| `find_in_cr2w` | navigation | Searches in a CR2W by `$type` / field / `*` → **exact JSON path** of the node (targeted edit then `write_game_file`) |
| `diagnose_logs` | debug | Parses the 6 logs (redscript/RED4ext/ArchiveXL/TweakXL/Codeware/CET/REDmod), extracts/classifies errors and maps known errors → fix |
| `analyze_conflicts` | maintenance | **Robust** conflicts (without the buggy WolvenKit verb): files provided by several `.archive` (+ who wins) and records defined by several `.tweak` |
| `validate_item_mod` | creation | Validates the reference chain of an ArchiveXL item (`.yaml` entityName ↔ `.csv`, displayName ↔ `.json secondaryKey`, presence of `.ent`; `deep` checks the appearanceName in the `.ent`) — kills the #1 cause of silent failure |
| `lint_tweak` | creation | TweakXL semantic lint: forbidden TABS, indentation, duplicate records, `inlineN` used as `$base` (breaks on updates) |
| `generate_manifest` | maintenance | Dependency manifest + `REQUIREMENTS.md` (Nexus-style) from framework detection |
| `resolve_dynamic_appearance` | creation | Expands an ArchiveXL dynamic appearance pattern (`{gender}`/`{camera}`) into concrete paths + existence check |
| `migration_check` | maintenance | Is a `.archive` mod still aligned with the game version? (active overrides vs ones that became inert after an update) |
| `toggle_mods` | maintenance | Enables/disables `.archive` files (reversible move to `_disabled`) — conflict-bisection primitive |
| `list_entity_appearances` | creation | Lists the appearances of a `.ent` entity (`name` + `appearanceName` + `.app`) — to know what it exposes before editing/exporting |
| `inspect_app` | inspection | Summary of an `.app` (appearances, mesh components per appearance, distinct meshes) — overview before `validate_appearance` |
| `validate_appearance` | creation | **Deep validation** `.app`→`.mesh`: does the referenced `meshAppearance` exist in the `.mesh`? (otherwise invisible mesh) — resolves mod or base meshes (`gamePath`) |
| `validate_redmod` | maintenance | Validates a REDmod's `info.json` (required fields `name`/`version` + format, consistency of `customSounds` entries) |

### MCP prompts (8)

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

## MCP resources (4)

In addition to tools, the server exposes **resources** — readable data
addressed by URI, that the client can consult or attach to the context.

| URI | Type | Content |
|---|---|---|
| `wolvenkit://reference` | direct | Cheat sheet: commands, REDengine formats, modding workflow |
| `wolvenkit://archive/{+path}` | template | Listing of the `.archive` content at the given path |
| `wolvenkit://cr2w-json/{+path}` | template | REDengine CR2W file rendered as JSON |
| `wolvenkit://mods/{+gamePath}` | template | Inventory of installed mods (`.archive` archives + REDmod) |

## Configuration (environment variables)

| Variable | Default | Role |
|---|---|---|
| `WOLVENKIT_DAEMON` | sibling project | Path to `WolvenKitDaemon.dll` (the fast path) |
| `WOLVENKIT_CP77TOOLS` | `~/.dotnet/tools/cp77tools[.exe]` | Path to cp77tools (subprocess fallback) |
| `DOTNET_ROOT` | *auto-detected* | .NET runtime root — rarely needs setting |
| `WOLVENKIT_CLI_TIMEOUT_SECONDS` | `300` | Maximum delay for a command |

## Validation status

The server today exposes **123 tools, 8 prompts and 4 resources**; the MCP
handshake, the full registration of tools, their annotations and their schemas
are verified at every build by an **automatic E2E test** (129 xUnit tests,
Windows CI). The most recent validation **on a real game** (Windows 11 +
Cyberpunk 2077, `validate-windows.py` script) exercised the 69 tools of the time
on real assets: result **78 OK · 2 reservations · 0 failures** — detail and
fixed bugs in `WINDOWS-VALIDATION.md`.

- ✅ MCP handshake, `tools/list`, `prompts/list`, `tools/call`, resources
- ✅ **Workflow tools**: `check_requirements` (10/10 frameworks detected),
  `analyze_dependencies`, `mod_doctor` (setup health), `validate_xl` +
  `scaffold_archivexl`, `find_references`, `diff_mod_vs_base` (`$.Header` noise
  filtered), `scaffold_mod`, `package_mod` — all verified on the real install
- ✅ Read (`archive_info`, `find_in_archives` cache ×6, `diff_archives`,
  `extract_files`, `uncook`), conversion (`cr2w_to_json`/`json_to_cr2w`,
  `export_files`, `import_raw`), write (`pack_archive`, `lint_mod`,
  `install_mod`), audio (`wwise_export`), TweakDB (`tweakdb_query` capped,
  `read_tweak`/`write_tweak`/`validate_tweak`/`install_tweak`), REDmod
  (`create_redmod_project`/`pack_redmod`/`install_redmod`), hash — on real
  Cyberpunk 2077 assets
- ✅ Native Kraken compression: `oodle compress`/`decompress` round-trip byte-exact
- ✅ Daemon: after startup (~3 s, once), subsequent calls drop to a few
  milliseconds; **several requests in flight simultaneously** (pipelining)
- ✅ LRU cache of archive listings (33 content archives + 318 mods);
  `find_in_archives` ×6 faster on subsequent calls
- ✅ **New tools**: `export_morphtarget`/`export_mlmask` (verified on real
  assets), `extract_audio` (**82,578 opus files** extracted from a voice
  archive), `generate_modproj`, extended `create_mod_project`, semantic `lint_script`,
  **`loc_resolve`** (LocKey→text, 70,579 en_us entries; key `40`→"News"),
  **`import_audio`** (WAV→opus, wiring verified)
- ✅ **`build_project`**: now compiles the `.cpmodproj` generated by
  `create_mod_project` → `packed/archive/pc/mod/<mod>.archive`
- ✅ **C# unit tests** (`WolvenKitMcp.Tests`, xUnit): 129 tests green (pure helpers
  `Truncate`/`MatchesGlob`/`BuildCpmodprojXml` + REDscript parser: acceptance of the
  realistic corpus, syntax error detection, module/declaration extraction)
- ✅ **REDscript parser** (`lint_script`): 0 errors on the 1374 `.reds` of `r6/scripts`,
  line:column errors on broken code (verified end-to-end via the MCP server)
- ⚠️ `detect_conflicts`: the `conflicts` verb of WolvenKit.CLI 8.18.0 crashes on
  a real install — upstream bug, reproducible as-is with `cp77tools`

## Known limitations

- **Textures / audio.** Texture conversion (`texconv`) and Wwise audio depend on
  Windows native binaries — outside the macOS scope.
- **Daemon startup.** The very first call after launching the server waits for
  the daemon warm-up (~7 s, once); afterwards everything is instant.
- **Compressor.** `rarten/ooz` is not guaranteed byte-identical to Oodle for
  very small Mermaid/Selkie blocks — no impact (the game decodes any valid stream).

## Remaining work

Delivered so far: structured JSON output, `read_game_file` / `write_game_file`,
`install_mod`, `diff_archives`, `lint_mod`, REDmod packaging, structured TweakDB
editing + `describe_tweak_record` + `generate_tweak_template`, mesh/texture
inspection, reading + linting `.reds` scripts + `generate_redscript_template`,
backup/restore mods, **uninstall trio + `deploy_redmod`**, **launch_game /
tail_game_logs** (in-game iteration loop), **`mod_summary` + `dump_records`**
(intelligence), **`extract_localization` / `build_localization`** (UI strings),
5 MCP prompts, LRU cache + stats + **per-verb metrics** + `clear_cache`,
`wwise_export` parallelization, daemon pipelining, `verbose` mode for debugging,
**anim/morphtarget/mlmask export**, **`extract_audio` (opus voice-over)**,
**`.cpmodproj` generation** (`generate_modproj` + `create_mod_project` →
`build_project` end-to-end), **semantic `.reds` lint**, **C# xUnit unit
tests**, **`.mcpb` bundle** (`build-mcpb.ps1`) + **GitHub Actions CI**.

Round 3: **`loc_resolve`** (LocKey → text, verified: 70,579 en_us entries, hash
and secondary-key resolution OK), **`import_audio`** (WAV → `.opus` via `OpusTools.ImportWavs`,
embedded `opusenc` encoder), anim/morphtarget/mlmask exports verified on real assets.

### Remaining

- **`import_audio`** — wired and verified at the verb level (loads the ArchiveManager,
  reaches `OpusTools.ImportWavs`); the **full round-trip** has not been tested for lack
  of WAVs named by a real opus hash. To be validated on a real voice-replacement case.
- **`export_animation`** — works, but a `.anims` **alone** (without an associated rig)
  does not produce glTF (WolvenKit constraint); an "anim + rig" mode would be useful.
- **Fine export toggles** — `IsBinary`/`incRootMotion` (anim), `ExportTextures`
  (morphtarget) do not go through `ConsoleFunctions.ExportTask`; they would require
  calling `IModTools.Export` directly with a constructed `GlobalExportArgs`.
- **`.reds` type-checking** — **syntactic** validation is now done by a real grammar
  parser (`RedscriptParser.cs`, 0 false positives on 1374 real files); resolving
  **external types/signatures** would require the `scc` compiler + the whole mod
  ecosystem (slow ~15 min, fails on missing deps — see note above).
- **macOS re-validation** — has not been redone since the Windows extensions.

## Structure

```
wolvenkit-mcp/
├── native/                  Rebuild of libkraken.dylib (arm64)
│   ├── build-libkraken.sh
│   ├── README.md
│   ├── ooz-rarten/          rarten/ooz source (modified — see native/README.md)
│   └── build/libkraken.dylib
├── src/WolvenKitMcp/        C# / .NET 8 MCP server
│   ├── Program.cs           Host + stdio transport
│   ├── Cp77ToolsRunner.cs   Drives the daemon (pipelined IPC, archive cache, cp77tools fallback)
│   ├── WolvenKitTools.cs    The 62 base MCP tools
│   ├── ModdingTools.cs      The 23 workflow tools (deps, health, scaffolding, refs, diff)
│   ├── LiveTools.cs         The 35 live_* tools (running game, via CetBridge.cs)
│   ├── RedscriptParser.cs   REDscript grammar parser (lint_script)
│   ├── WolvenKitPrompts.cs  The 8 MCP prompts (recipes)
│   └── WolvenKitResources.cs  The 4 MCP resources (reference generated by reflection)
├── docs/                    USER_GUIDE · TOOLS · MODDING_RECIPES · ARCHITECTURE
├── src/WolvenKitMcp.Tests/  xUnit unit tests of the pure helpers
├── src/WolvenKitDaemon/     Persistent daemon — host of the WolvenKit libraries
│   └── Program.cs           DI + verb dispatcher + pipelined stdio IPC
├── manifest.json            Desktop Extension manifest (.mcpb)
├── build-mcpb.ps1           Builds dist/wolvenkit-mcp.mcpb (1-click install)
├── .github/workflows/ci.yml CI: build daemon + server + tests + .mcpb bundle
├── test-mcp-server.py       MCP server test client
├── test-daemon.py           Daemon-only test client
├── validate-windows.py      Validates tools + prompts on real game assets
├── WINDOWS-VALIDATION.md    Windows validation checklist + results
└── README.md
```
