# User guide — WkMCP for Cyberpunk 2077 modding

This guide is aimed at **modders**: it explains how to install the WkMCP
server, connect it to Claude, then chain complete end-to-end workflows
(read a game file, edit an item, create and install a mod,
check the health of the installation, package for distribution).

You don't need to write any code: you describe your intent to Claude,
and Claude calls the **MCP tools** for you. This guide names the
real tools so that you can guide Claude precisely ("use `read_game_file`
on…") and understand what is happening.

> Result convention: each tool returns a JSON
> `{ ok, status, summary, produced, warnings, errors, log }`. Success is judged
> by the **files actually produced** (`produced` field), not by a log message.

---

## 1. Prerequisites

This guide targets **Windows** (the end-to-end validated platform, with Cyberpunk
2077 installed). You will need:

1. **Windows 10/11.**
2. **Cyberpunk 2077 installed** (Steam, GOG or Epic). Locate the game's root
   folder, for example:
   `C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077`.
   In this guide, we call it `<GAME>`.
3. **.NET 8 SDK (or higher)** — https://dotnet.microsoft.com/download
4. **WolvenKit CLI**:
   ```powershell
   dotnet tool install -g WolvenKit.CLI
   ```
   This installs `cp77tools` in `~\.dotnet\tools\`.
5. **Claude**: Claude Desktop or Claude Code (the MCP client that will drive the tools).

> macOS (**experimental**, build-from-source only): the project can be built on
> Apple Silicon by rebuilding `libkraken.dylib` — see `native/README.md`. Texture
> conversion and Wwise audio are not supported there. This guide targets Windows,
> the validated v1 platform.

---

## 2. Installing the MCP server

On Windows, `cp77tools` is native: the `native/` folder (rebuilding
libkraken for macOS) is unnecessary.

### 2.1 Compile the daemon then the server

The order matters: compile **the daemon first** (its build automatically deploys
`kraken.dll` and `DirectXTexNet.dll`), **then the server**.

```powershell
dotnet build src\WkDaemon
dotnet build src\WkMcp
```

At the end, the server is located at:
`src\WkMcp\bin\Debug\net8.0\WkMcp.dll`

### 2.2 How it works (in brief)

```
Claude ─MCP/JSON-RPC─▶ WkMcp ─IPC stdio─▶ WkDaemon ─▶ WolvenKit libs + kraken
                                    └─fallback─▶ cp77tools (subprocess) if daemon unavailable
```

The WolvenKit daemon stays **persistent**: the expensive loading of the hash
database (~6 s) is paid only once, at warmup. The first call after
launch waits for this warmup (~7 s); after that everything is near-instant. If the
daemon is unavailable, each call falls back to a `cp77tools` subprocess
(functional but ~6 s/call).

---

## 3. Connecting the server to Claude

### 3.1 Claude Desktop

Edit `%APPDATA%\Claude\claude_desktop_config.json`:

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

Replace the path with yours, then **restart Claude Desktop**.

### 3.2 Claude Code

```powershell
claude mcp add wolvenkit -s user -- dotnet "C:\path\to\wkmcp\src\WkMcp\bin\Debug\net8.0\WkMcp.dll"
```

No `DOTNET_ROOT` variable is needed on Windows.

### 3.3 Verify the connection

Ask Claude to call **`wk_status`**. You should get
`ok: true`, the path of `cp77tools` and its version, as well as the cache stats.
This is the first diagnostic reflex if something is off.

---

## 4. Configuration environment variables

All optional. To be set in the `"env"` block of the Claude Desktop config,
or via `-e VAR=value` with `claude mcp add`.

| Variable | Default | Role |
|---|---|---|
| `WKMCP_DAEMON` | sibling project (local build) | Path of `WkDaemon.dll` (the fast path). |
| `WKMCP_CP77TOOLS` | `~\.dotnet\tools\cp77tools.exe` | Path of `cp77tools` (subprocess fallback). |
| `DOTNET_ROOT` / `WKMCP_DOTNET_ROOT` | auto-detected | .NET runtime root — rarely to be set on Windows. |
| `WKMCP_CLI_TIMEOUT_SECONDS` | `300` | Maximum delay of a command (in seconds). |

Claude Desktop example with an extended timeout:

```json
{
  "mcpServers": {
    "wolvenkit": {
      "command": "dotnet",
      "args": ["C:\\...\\WkMcp.dll"],
      "env": { "WKMCP_CLI_TIMEOUT_SECONDS": "600" }
    }
  }
}
```

---

## 5. Workflow — Locate and read a game file

Goal: find a file in the game archives and read its content as JSON.

1. **Locate the file.** Ask Claude to use **`find_in_archives`**
   on the content folder, with a glob pattern:
   - `archivesFolder`: `<GAME>\archive\pc\content`
   - `pattern`: for example `*player*.ent` (or `regex` for a regular
     expression)

   The tool indicates, for each match, **which archive** the
   file is in. Subsequent calls on the same folder are near-instant
   (LRU cache).

   > Variant: **`archive_info`** lists the content of a specific archive;
   > **`compute_hash`** / **`resolve_hash`** map path ↔ FNV1a64 hash.

2. **Read the file.** Once the archive and the internal path are known, use
   **`read_game_file`**:
   - `archivePath`: the `.archive` returned in step 1
   - `gameFilePath`: the internal path (e.g. `base\characters\...\x.ent`)

   The tool extracts, converts to REDengine JSON and returns the content. The full
   JSON is also written to disk (`jsonFile` field): if `truncated` is
   `true`, read this file in full for the detail.

   > Under the hood, `read_game_file` chains `extract_files` (extraction) then
   > `cr2w_to_json` (serialization). You can also call these two tools
   > separately if needed.

3. **Inspect without converting everything.** For a compact overview:
   - **`inspect_mesh`**: LODs, sub-meshes, materials, bones of a `.mesh`
   - **`inspect_texture`**: resolution, format, mipmaps of a `.xbm`
   - **`uncook`**: extraction + conversion in one pass (mesh → glTF,
     textures → image)

---

## 6. Workflow — Edit an item via TweakDB (`.tweak`)

Goal: modify the stats or properties of a game item without touching the archives.
TweakXL hot-loads the `.tweak` (taken into account at the next launch, with no
rebuild). The reference `tweakdb.bin` is typically
`<GAME>\r6\cache\tweakdb.bin`.

1. **Find the identifier.** Use **`tweakdb_query`**:
   - `tweakdbPath`: `<GAME>\r6\cache\tweakdb.bin`
   - `filter`: substring, for example `Items.Preset_Lexington` (results
     capped at 100; refine the filter if `truncated` signals it).

2. **Discover the record's fields.** Use **`describe_tweak_record`** on
   the identifier: it lists all the record's flats with their types and current
   values. Indispensable before editing (knowing which field to modify).

3. **Write the `.tweak`.** Two paths:
   - **`generate_tweak_template`** to start from a skeleton (patterns:
     `override_field`, `new_record`, `boost_stat`), then adjust the values;
   - or edit an existing `.tweak` via **`read_tweak`** (→ editable JSON),
     modify the JSON, then **`write_tweak`** (JSON → `.tweak` YAML).

4. **Validate.** Before installing, **`validate_tweak`**:
   - `tweakFile`: your `.tweak`
   - `tweakdbBin`: `<GAME>\r6\cache\tweakdb.bin`

   It flags unknown keys (except new records declaring `$instanceOf`).

5. **Install.** **`install_tweak`** copies the file to
   `<GAME>\r6\tweaks\<name>.tweak`:
   - `tweakFile`: your `.tweak`
   - `gamePath`: `<GAME>`

   Launch the game: the change is active.

   > To remove the tweak: **`uninstall_tweak`**.

---

## 7. Workflow — Create, pack and install a `.archive` mod

Goal: produce a `.archive` mod (asset replacement/addition) and install it.

### 7.1 Create the project structure

Use **`create_mod_project`**:
- `parentFolder`: parent folder (e.g. `C:\mods`)
- `modName`: mod name
- (optional) `author`, `version`, `description`

This creates the tree and a directly compilable `.cpmodproj`:

```
<modName>/
├── <modName>.cpmodproj      WolvenKit project (compilable by build_project)
├── source/archive/          Cooked REDengine files (.mesh, .ent, .xbm…) → packed
├── source/raw/              Raw files (glTF, images…) → to pass through import_raw
├── source/resources/        Free files copied as-is
├── source/customSounds/     Custom sounds (REDmod audio)
└── packed/                  Output: build_project drops the compiled mod here
```

> Already have a project folder without a `.cpmodproj`? Use
> **`generate_modproj`** to make it compilable.

### 7.2 Prepare the modified content

Depending on the case:

- **Edit an existing game file.** Read it with `read_game_file`
  (workflow §5), modify the JSON, then **`write_game_file`**:
  - `jsonFile`: the edited JSON
  - `gameFilePath`: the target internal path (e.g. `base\…\x.ent`)
  - `modArchiveFolder`: the project's `source/archive`

  The produced CR2W is placed at the correct internal path, ready to pack.

- **Import raw assets.** Place your glTF/images in `source/raw`, then
  **`import_raw`** (raw → CR2W) to `source/archive`.

### 7.3 Pack

Two options:

- **`build_project`** (recommended): compiles the `.cpmodproj`. Give
  `projectFolder` = the project folder. The output is
  `packed\archive\pc\mod\<mod>.archive`.
- **`pack_archive`**: packs a folder of REDengine files directly
  (`folderPath` = `source/archive`, `outputPath` = destination folder).

### 7.4 Lint and installation

1. **`lint_mod`** (pre-install): detects non-REDengine extensions and, if you
   pass `gamePath` = `<GAME>`, conflicts with already-installed mods.
   - `archivePath`: your `.archive`
2. **`install_mod`**: copies the archive into `<GAME>\archive\pc\mod\`.
   - `archivePath`: your `.archive`
   - `gamePath`: `<GAME>`
3. Launch the game to verify.

> Check what is installed: **`list_installed_mods`**.
> Remove a mod: **`uninstall_mod`**.
> Safety net: **`backup_mods`** (ZIP snapshot of `archive/pc/mod`,
> `mods/`, `r6/tweaks/`) and **`restore_mods`** before any risky manipulation.

> REDmods (scripts/tweaks/sounds to deploy): use instead
> **`create_redmod_project`** → **`pack_redmod`** → **`install_redmod`**, then
> **`deploy_redmod`** to compile scripts and apply tweaks.

---

## 8. Workflow — Check dependencies and setup health

Goal: ensure the required frameworks (redscript, RED4ext, ArchiveXL,
TweakXL, Codeware, Audioware, CET…) are in place before playing or distributing.

1. **Which frameworks does my mod require?** **`analyze_dependencies`**:
   - `modPath`: mod folder (project or deployed)
   - (optional) `gamePath`: `<GAME>` → marks each dependency
     **installed / missing**

   The tool deduces the required frameworks from the `.reds` imports, the
   `.xl` files, the `.tweak` and the types used.

2. **Which frameworks are installed?** **`check_requirements`**:
   - `gamePath`: `<GAME>`

   Inventory of the modding frameworks present, with version.

3. **Full diagnostic in one call.** **`mod_doctor`**:
   - `gamePath`: `<GAME>`

   Health synthesis: installed/missing frameworks, dependencies required by the
   present content but absent, conflicts, inventory and recommendations. This is
   the tool to run in case of a crash or a mod that won't load.

> Supporting tools: **`detect_conflicts`** (the same file provided by several
> mods), **`validate_xl`** (checks an ArchiveXL `.xl`), **`find_references`**
> (all the TweakDBID/path/LocKey references in a mod's sources),
> **`diff_mod_vs_base`** (semantic diff of an overridden file vs the base game).

---

## 9. Workflow — Package for distribution

Goal: produce a clean `.zip`, ready to publish (Nexus, etc.), respecting the
game layout.

1. **Build the distribution layout.** Gather in a source folder the
   structure as it should land in the game:

   ```
   <dist>/
   ├── archive/pc/mod/<mod>.archive   (.archive mod)
   ├── r6/tweaks/<mod>.tweak          (any tweaks)
   └── mods/<mod>/                    (any REDmod)
   ```

   > Shortcut: **`scaffold_mod`** creates a functional mod in one call
   > (`archive` / `redscript` / `tweak` / `redmod`) + a `MOD_MANIFEST.json`
   > (type, declared dependencies) — a good starting point for a clean layout.

2. **Pack.** **`package_mod`**:
   - `sourceFolder`: the `<dist>` folder at the game layout
   - `outputZip`: path of the output `.zip`

   The ZIP uses conformant `/` separators (compatible with
   mod installers).

3. **Final check before publication.** Rerun `analyze_dependencies` on the
   layout to document the required dependencies in your Nexus description, and
   `mod_doctor` on a test install to confirm that everything loads.

---

## 10. Going further

- **MCP prompts (ready-to-use recipes)**: `read_game_file_workflow`,
  `edit_tweakdb_item`, `pack_and_install_mod`, `recolor_texture`, `inspect_mesh`.
  Ask Claude to invoke the corresponding prompt to start a workflow.
- **MCP resources**: `wkmcp://reference` (commands/formats cheat sheet),
  `wkmcp://archive/{path}` (archive listing),
  `wkmcp://cr2w-json/{path}` (CR2W rendered as JSON).
- **REDscript scripts**: **`read_script`** (structure of a `.reds`),
  **`lint_script`** (real grammar parser, line:column errors),
  **`generate_redscript_template`** (annotation scaffolds).
- **In-game iteration**: **`launch_game`** (launches the game, with optional
  prior `deploy_redmod`) and **`tail_game_logs`** (follows the logs in
  `r6/logs` and `tools/redmod/logs`).
- **Maintenance**: **`clear_cache`** empties the archive LRU cache or the
  metrics (`scope` = `archives` | `metrics` | `all`).

### Quick troubleshooting

- **`wk_status` returns `ok: false`** → `cp77tools` not found:
  `dotnet tool install -g WolvenKit.CLI`, or point `WKMCP_CP77TOOLS`.
- **First call slow (~7 s)** → daemon warmup, normal and one-time.
- **Every call is slow (~6 s/call)** → the daemon (fast path) is not being used.
  Build it (`dotnet build src\WkDaemon`) so the server finds the sibling
  DLL, or set `WKMCP_DAEMON` to its path. `wk_status` shows which path is used.
- **A tool seems to fail** → look first at `produced` (files actually
  created) and `errors`, not just `log`; rerun with `verbose: true` on the
  tools that offer it (`extract_files`, `uncook`, `pack_archive`, `import_raw`…).
- **`File .ent` / `.mesh` not found** → the offline export/import tools work on
  **extracted** files, not archives. Extract first with `extract_files` / `uncook`,
  or use `read_game_file`, which chains extraction for you.
- **`resolve_hash` / `tweakdb_*` error out** → these rely on the daemon; if it is
  unavailable they degrade. Re-run `wk_status` and rebuild the daemon.
- **`export_entity` says "can not be exported"** → WolvenKit refuses headless export
  of certain entity types. Use `list_entity_appearances` to inspect, and `uncook`
  the referenced `.mesh` to view it instead.
- **A mod does not load in-game** → run `mod_doctor` (are the required frameworks
  installed?), `lint_mod` (non-REDengine extensions?), `detect_conflicts` (another
  mod providing the same file?), and confirm the archive is in `archive\pc\mod`.
- **`dotnet build` fails with "file in use"** → the daemon DLLs are locked by a
  running server. Stop the MCP client (Claude Desktop restarts the server) and kill
  `WkDaemon` / `dotnet`, then rebuild.
