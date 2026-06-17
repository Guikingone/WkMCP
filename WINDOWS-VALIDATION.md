# Windows validation checklist — WolvenKit MCP

The server exposes 123 tools, 8 prompts and 4 resources (count verified at every
build by the E2E test of `WolvenKitMcp.Tests`). This checklist validates them on
Windows with a real installation of Cyberpunk 2077; the counts cited in the
results below are those of the date of each pass.

## Validation result — 0.4.0 — 2026-06-15

The three tools added in 0.4.0 were validated on **Windows 11** with a real
installation of Cyberpunk 2077, via `python test-new-tools.py "<game>"` (drives the
MCP server over stdio). **Result: 6 PASS · 0 FAIL.**

| Tool | Result |
| --- | --- |
| `archive_stats` | ✅ `basegame_1_engine.archive`: 4114 files, 58 types (top `.xbm`=1135, `.particle`=365, `.effect`=352…); 2nd call served by the LRU cache (`fromCache=true`). |
| `validate_redmod` | ✅ scaffolded project → `status=success`, 0 errors; `info.json` without `version` → error detected; real installed REDmod ("(2k) Immersive Bullet Holes") → `status=partial`, 0 errors. |
| `inspect_app` | ✅ `.app` extracted from the game (`a0_006_ma__launcher_fragment.app`) → 6 appearances, 24 mesh components, 4 distinct meshes. |

> Note: a JSON casing bug (nested `byExtension`/`appearances` keys in
> PascalCase instead of the envelope's camelCase) was detected by this test and fixed
> before this result. The full validation pass below remains that of the
> earlier version (120 tools).

## Validation result — 2026-05-29

Validated on **Windows 11** with **Cyberpunk 2077** installed (base game
+ 318 .archive mods + 170 REDmods). The **60 tools, 5 prompts and 3 resources**
were exercised end-to-end via the MCP server by the script
**`validate-windows.py`** (see below). The tools return a **structured JSON**
result (`ok`, `status`, `summary`, `produced`, `warnings`, `errors`,
`log` truncated preserving head + errors + tail); archive listings
are served by an **LRU cache** (×6 faster on subsequent calls, stats +
per-verb metrics visible via `wolvenkit_status`, clearable via
`clear_cache`); the daemon supports **IPC pipelining** (several requests
in flight).

**Result: 66 OK · 1 reservation · 0 partial · 0 failures.**

- **Round 2 (2026-05-29)**: +5 tools (`export_animation`, `export_morphtarget`,
  `export_mlmask`, `extract_audio`, `generate_modproj`), `create_mod_project`
  extended (emits a `.cpmodproj` + `source/customSounds`), `lint_script` enriched
  (semantic checks). `extract_audio` extracted **82,578 opus files** from a voice
  archive. **19 xUnit unit tests** green. **`.mcpb`** bundle + **CI**.
- **Round 3 (2026-05-29)**: +2 tools → **60**. `loc_resolve` (LocKey → text;
  **70,579 en_us entries** loaded, key `40` → "News"), `import_audio`
  (WAV → `.opus` via `OpusTools.ImportWavs`, wiring verified). `export_mlmask` and
  `export_morphtarget` validated on real assets; `export_animation` runs but
  a `.anims` without a rig does not produce glTF (WolvenKit constraint).
- **Round 4 (2026-05-29)**: `lint_script` now relies on a **real REDscript
  grammar parser** (`RedscriptParser.cs`) — line:column errors, **0 false positives
  calibrated on the 1374 `.reds` of `r6/scripts`**, detection verified on broken code.
  **26 xUnit tests** green. (Type-checking via `scc` remains out of scope: compiling
  all of `r6/scripts` takes ~15 min and fails on the missing deps of installed mods.)
- **Round 10 (2026-05-29)**: +2 tools → **85** — appearance hardening.
  `list_entity_appearances` (15 appearances listed on a real NPC entity: name +
  appearanceName + .app) and `validate_appearance` (deep `.app`→`.mesh` validation:
  on a valid base `.app` → **0 errors, 24 mesh refs, 4 meshes resolved** = zero false
  positives; detects an absent `meshAppearance` = "invisible mesh"). `export_entity` made
  graceful (discovers/validates the appearance, reports "can not be exported" — WolvenKit
  headless limit confirmed on NPC entities, not papered over). **50 xUnit tests**.
- **Round 9 (2026-05-29)**: +7 tools → **83** — "take it all". Wave 1 (pure MCP):
  `lint_tweak`, `generate_manifest`, `resolve_dynamic_appearance`, `migration_check`
  (4 active overrides / 14 inert on a real mod), `toggle_mods`. Wave 2 (daemon
  verbs): `export_materials` (**18 material files** produced, after `MaterialRepo` fix),
  `export_entity` (wired to `IModTools.ExportEntity` — experimental: requires an entity
  carrying appearances), `validate_item_mod` extended with `deep`. **`compile_scripts` abandoned**
  (scc compiles the entire `r6/scripts` tree, isolation impossible; `diagnose_logs` covers
  attribution). **47 xUnit tests**. **Result: 92 OK · 2 reservations · 0 failures.**
- **Round 8 (2026-05-29)**: +5 tools → **76** — the "Top 4" of the identified gaps.
  `inspect_cr2w`/`find_in_cr2w` (navigation of ANY large CR2W — journal → 159,907 typed
  objects, 62 types), `diagnose_logs` (parses 6 logs + error KB → fix; classified
  the real redscript compilation failure), `analyze_conflicts` (robust conflicts without the
  buggy verb — **1015 archive conflicts + 13 records** on the real install, 1.35 s),
  `validate_item_mod` (ArchiveXL reference chain — detects the 2 errors of a broken mod).
  **42 xUnit tests** green. **Result: 85 OK · 2 reservations · 0 failures.**
- **Round 7 (2026-05-29)**: +2 tools → **71** — **journal** intelligence (`.journal`).
  `inspect_journal` (journal summary: on `cooked_journal.journal` → **28,476 entries,
  41 types, 9 categories** in 0.4 s) and `find_journal_entry` (locates by id/type/title →
  exact JSON path). Editing `.journal` was already possible via read/write_game_file
  (round-trip verified: 71 MB JSON ↔ 4.79 MB CR2W); these tools make it practical.
  **33 xUnit tests** green. Recipe added in `docs/MODDING_RECIPES.md`.
- **Round 6 (2026-05-29)**: **multi-agent audit** (workflow: 6 dimensions + adversarial
  verification) → fixes applied: **HIGH TweakDB bug** (reload when the path
  changes — verified: original `.bin` + copy return the right records), **Zip-Slip security**
  in `restore_mods` (entry outside target blocked + warning), `read_game_file` temp leak
  (deterministic folder), consistent `Status()`, anti-collision `ExtractAsJson`, + optimizations
  (static boolFlags, metrics modulo, framework mapping compiled once, pre-sized tokens,
  `package_mod` excludes the noise, `find_references` in lazy read, parser: keyword-type segment
  after `.`). 4 docs added in `docs/`. **0 regressions** (78 OK / 2 reservations / 0 failures,
  29 xUnit tests, parser 0 false positives on 1374 files).
- **Round 5 (2026-05-29)**: +9 **workflow tools** → **69** (`ModdingTools.cs`).
  Deps/health intelligence (`analyze_dependencies`, `check_requirements` → 10/10
  frameworks, `mod_doctor`), ArchiveXL stack (`validate_xl`, `scaffold_archivexl`),
  navigation/diff (`find_references`, `diff_mod_vs_base` — `$.Header` noise filtered),
  scaffolding/packaging (`scaffold_mod`, `package_mod`). **29 xUnit tests** green.
  **Result: 78 OK · 2 reservations · 0 failures.**
- `build_project` — *now OK*: compiles the `.cpmodproj` emitted by
  `create_mod_project` and produces `packed/archive/pc/mod/<mod>.archive`.
- `detect_conflicts` — *warn*: tool correctly wired, but the `conflicts`
  verb of WolvenKit.CLI 8.18.0 throws an `ArgumentNullException` on a real
  install: **upstream bug**, reproducible as-is with `cp77tools`.

### Summary table of delivered extensions

**Round 1** (post-validation 2026-05-19)

| Category | Tools added |
|---|---|
| Mod diagnostic | `lint_mod` |
| Inspection | `diff_archives` |
| Structured TweakDB | `read_tweak`, `write_tweak`, `validate_tweak`, `install_tweak` |
| REDmod packaging | `create_redmod_project`, `pack_redmod`, `install_redmod` |
| MCP prompts | 5 recipes (`read_game_file_workflow`, etc.) |
| Optimizations | LRU cache · daemon pipelining · parallel `wwise_export` · `tweakdb_query` cap · contextual log truncation · advanced `uncook` flags |

**Round 2** (2026-05-20)

| Category | Tools added |
|---|---|
| Quick inspection | `inspect_mesh`, `inspect_texture`, `describe_tweak_record` |
| Scaffolds | `generate_tweak_template` (patterns `override_field`, `new_record`, `boost_stat`) |
| `.reds` scripts | `read_script`, `lint_script` (textual analysis) |
| Safety | `backup_mods`, `restore_mods` (timestamped ZIP) |
| Observability | LRU cache stats in `wolvenkit_status` · `verbose` parameter on the tools generating large logs |

**Round 3** (2026-05-20)

| Category | Tools added |
|---|---|
| Uninstall + deploy | `uninstall_mod`, `uninstall_redmod`, `uninstall_tweak`, `deploy_redmod` |
| In-game iteration | `launch_game`, `tail_game_logs` |
| Mod intelligence | `mod_summary` (archive + REDmod), `dump_records` (JSONL/CSV per type) |
| REDscript scaffolds | `generate_redscript_template` (add_method, wrap_method, replace_method, add_field, new_class) |
| UI localization | `extract_localization` (from TweakDB), `build_localization` (to `.tweak`) |
| Maintenance | `clear_cache` (archives / metrics / all) · per-verb metrics (p50/p95) in `wolvenkit_status` |

### Bugs found and fixed during this validation

| Symptom | Fix |
|---|---|
| `archive --list`: listing written by `Console.WriteLine`, redirected to stderr then lost → `archive_info`, `find_in_archives` and the archive resource returned empty | The daemon captures `Console.Out` in the logger's buffer (`WolvenKitDaemon/Program.cs`) |
| `kraken.dll` missing from the build output → daemon unable to start | `DeployNativeWindows` MSBuild target + explicit references (`WolvenKitDaemon.csproj`) |
| `DirectXTexNet.dll` not deployed then not resolved (absent from `deps.json`) → texture `uncook` failing | Deployment via the csproj + `AssemblyLoadContext` resolver in the daemon |
| `detect_conflicts` was sending `archive/pc/mod`; the `conflicts` verb expects the game root | Parameter renamed to `gamePath` (`WolvenKitTools.cs`) |
| `wwise_export` was passing a folder; the `wwise` verb expects an output `.ogg` file | `.wem` → `.ogg` conversion file by file, named output (`WolvenKitTools.cs`) |
| `oodle_decompress`: WolvenKit writes the output to `<path>.bin` | The tool moves the result to the requested path (`WolvenKitTools.cs`) |
| Test scripts: hardcoded `/tmp` POSIX paths; `print()` crashed on the Windows console (cp1252) | Paths via `tempfile.gettempdir()`, outputs forced to UTF-8 |

### Replay the validation

```sh
python validate-windows.py "C:\path\to\Cyberpunk 2077"
```

`validate-windows.py` drives the MCP server (JSON-RPC stdio) and exercises the 53
tools, 5 prompts and 3 resources on real assets, then prints a results
table. It also includes an **IPC pipelining** test (4 requests in flight),
an **LRU cache** test (find_in_archives hot vs cold), backup/restore +
uninstall on a fake game folder, lint of a broken `.reds`, 5 patterns of
`generate_redscript_template` re-linted, `dump_records` on 1500+ weapons,
extract/build_localization with JSON → `.tweak` round-trip, and `clear_cache`
which resets the cache.

## 0. Already verified (no need to re-test the mechanism)

`compute_hash` (bit-exact) · `oodle_compress`/`oodle_decompress` (byte-exact
round-trip) · `create_mod_project` · MCP handshake · daemon (~ms latency) ·
`pack_archive` (mechanism). **Everything else is to be validated below.**

## 1. Installation (Windows)

- [ ] .NET 8+ SDK installed — https://dotnet.microsoft.com/download
- [ ] `dotnet tool install -g WolvenKit.CLI`
- [ ] `wolvenkit-mcp` project retrieved on the machine
- [ ] `dotnet build src\WolvenKitDaemon`
- [ ] `dotnet build src\WolvenKitMcp`
- [ ] The `native/` folder is **unnecessary on Windows** (it is the macOS libkraken fix) — ignore it
- [ ] Cyberpunk 2077 installation folder located (e.g. `...\steamapps\common\Cyberpunk 2077`)

## 2. Smoke test (scripts, without the game)

- [ ] `python test-daemon.py` → "Daemon ready", `hash` in ~1 ms, `pack` OK
  - ⚠️ If the daemon **fails to start** on a `kraken` error: copy `kraken.dll`
    from `%USERPROFILE%\.dotnet\tools\.store\wolvenkit.cli\<version>\...\tools\net8.0\any\kraken.dll`
    next to `WolvenKitDaemon.dll` (the NuGet package does not always provide it)
- [ ] `python test-mcp-server.py` → "21 tools", handshake OK

## 3. Wiring up to Claude

- [ ] Claude Desktop config or `claude mcp add` (cf. `README.md` § Installation on Windows)
- [ ] **New session** → 21 tools + 3 resources visible
- [ ] 1st call ~7 s (daemon startup), subsequent calls near-instant

## 4. Inspection — on real archives (`<game>\archive\pc\content\`)

- [ ] `archive_info` on a game `.archive` → coherent file count (`list=true` for the content)
- [ ] `find_in_archives` on `archive\pc\content` + pattern `*.ent` → files found + their archive
- [ ] `extract_files` — extract a few files from an archive → present on disk
- [ ] `uncook` — an archive with meshes/textures → `.glb`/glTF + images generated
- [ ] **Hash round-trip**: take a path shown by `find_in_archives`, run
      `compute_hash` on it, then `resolve_hash` of the result → must **give back the path**

## 5. Conversion

- [ ] `cr2w_to_json` on an extracted `.ent`/`.app`/`.mesh` → readable JSON
- [ ] edit the JSON, `json_to_cr2w` → CR2W binary regenerated
- [ ] `export_files` on a REDengine file → raw format (glTF / image)

## 6. Mod creation (writing)

- [ ] `create_mod_project` → folder structure created
- [ ] drop **real cooked files** into `source\archive`, then `pack_archive`
      → `.archive` produced **without** the "Unknown file extension" warning
- [ ] `import_raw` on a texture / a glTF → REDengine file
- [ ] `build_project` on a folder containing a `.cpmodproj` (created via the WolvenKit app)

## 7. Installed mods

- [ ] `list_installed_mods` on the game folder → `.archive` of `archive\pc\mod` + REDmods of `mods\`
- [ ] `detect_conflicts` on `archive\pc\mod` → JSON of conflicts

## 8. TweakDB

- [ ] Locate the game's `tweakdb.bin` (typically `<game>\r6\cache\tweakdb.bin`)
- [ ] `tweakdb_query` on this file + filter `Items.` → records / flats listed
- [ ] `tweakdb_resolve` on a TweakDB identifier hash → name

## 9. Audio — THE Windows-specific point

- [ ] `extract_files` of a `.wem` from an audio archive
- [ ] `wwise_export` on this `.wem` → playable `.ogg`
      *(the native audio binaries exist only on Windows — this is where we validate them)*

## 10. MCP resources

- [ ] read `wolvenkit://reference` → the cheat sheet
- [ ] read `wolvenkit://archive/<path of a .archive>` → content listing
- [ ] read `wolvenkit://cr2w-json/<path of an extracted file>` → JSON

## Points of vigilance

- "Oodle couldn't be loaded. Using Kraken..." warning: **harmless** — the
  open-source Kraken codec takes over.
- `pack_archive` ignores files whose extension is not a REDengine type — normal.
- Windows paths: pass **absolute** paths; mind the spaces (`Program Files`).
- If a tool keeps failing: check that the daemon starts. Otherwise the server falls
  back to the `cp77tools` subprocess (slow ~6 s but functional); if even that fails,
  the problem is in the cp77tools installation.
- `resolve_hash`, `tweakdb_resolve`, `tweakdb_query` work **only via the daemon**
  (no subprocess fallback — these are not cp77tools verbs).

## Results table — validation of 2026-05-20

| Tool / resource | OK? | Detail |
|---|---|---|
| `handshake + tools/list` | ✅ | 85 tools exposed. |
| `prompts/list` | ✅ | 5 MCP prompts exposed (recipes). |
| `archive_info` | ✅ | Info + filtered listing (LRU cache). |
| `find_in_archives` (cold) | ✅ | ~2 s on 33 archives. |
| `find_in_archives` (hot cache) | ✅ | **×6 faster** (~350 ms); cache hits = 33/33. |
| `diff_archives` | ✅ | +18 / -53 between a base archive and a real mod. |
| `extract_files` | ✅ | 15 `.streamingsector` extracted from `basegame_2_mainmenu`. |
| `uncook` | ✅ | Mesh → `.glb`, texture → `.png` (flags `meshExportType` etc. exposed). |
| `resolve_hash` | ✅ | `compute_hash` ↔ `resolve_hash` round-trip exact. |
| `cr2w_to_json` | ✅ | `.streamingsector` → editable JSON. |
| `json_to_cr2w` | ✅ | JSON → CR2W binary regenerated. |
| `export_files` | ✅ | Extracted mesh → glTF (`.glb`). |
| `read_game_file` | ✅ | Game file read as JSON in a single call. |
| `write_game_file` | ✅ | Edited JSON → CR2W placed back at the right internal path. |
| `pack_archive` | ✅ | `.archive` produced, without the "Unknown file extension" warning. |
| `lint_mod` | ✅ | 18 files · 0 unknown extensions · 0 conflicts (vs other installed mods). |
| `install_mod` | ✅ | Mod archive copied into `archive/pc/mod` (fake test game). |
| `create_mod_project` | ✅ | `source/archive + packed` structure created. |
| `create_redmod_project` | ✅ | `info.json` + `archives/` + `tweaks/` + `scripts/` + `customSounds/`. |
| `pack_redmod` | ✅ | `<name>.zip` produced (~760 bytes for an empty structure). |
| `install_redmod` | ✅ | REDmod copied into `mods/<name>/` (fake test game). |
| `build_project` | ✅ | Compiles the `.cpmodproj` (emitted by `create_mod_project`) → `packed/archive/pc/mod/<mod>.archive`. |
| `list_installed_mods` | ✅ | 318 `.archive` mods + 170 REDmods listed. |
| `detect_conflicts` | ⚠️ | *Warn*: `conflicts` verb of WolvenKit.CLI 8.18.0 crashes — upstream bug. |
| `tweakdb_query` | ✅ | `tweakdb.bin` loaded, records/flats listed (cap 100 + `truncated`). |
| `tweakdb_resolve` | ✅ | `176750402310` → `Items.Preset_Achilles_Collectible_inline0`. |
| `read_tweak` | ✅ | `.tweak` (YAML TweakXL) → editable JSON. |
| `write_tweak` | ✅ | JSON → `.tweak` (round-trip). |
| `validate_tweak` | ✅ | Validation against `tweakdb.bin` (exit=0 for known keys). |
| `install_tweak` | ✅ | `.tweak` copied into `r6/tweaks/` (fake test game). |
| `inspect_mesh` | ✅ | Summary: LODs, submeshes, materials, bones (without a full uncook). |
| `inspect_texture` | ✅ | xbm metadata: resolution, format, mipmaps (without conversion). |
| `describe_tweak_record` | ✅ | All the flats of a TweakDB record with types and values. |
| `generate_tweak_template` | ✅ | 3 patterns validated (`override_field`, `new_record`, `boost_stat`). |
| `read_script` | ✅ | Reads `.reds` + extracts declarations (module, func, class, @addMethod). |
| `lint_script` | ✅ | Detects unbalanced braces (tested on healthy + broken file). |
| `backup_mods` / `restore_mods` | ✅ | Timestamped ZIP → restore into an empty fake game; round-trip OK. |
| `uninstall_mod` / `uninstall_redmod` / `uninstall_tweak` | ✅ | Removal verified on a fake game (sandbox safeguard active). |
| `deploy_redmod` | ✅ | `redMod.exe deploy` executed on a real install (exit=0). |
| `launch_game` (fake game) | ✅ | Clean refusal when the exe is absent — expected behavior. |
| `tail_game_logs` | ✅ | 10 lines read from fake logs, filter / category OK. |
| `mod_summary` (archive) | ✅ | Categorization by extension on a real .archive mod. |
| `mod_summary` (redmod) | ✅ | Reads info.json + enumerates archives/scripts/tweaks/customSounds. |
| `dump_records` | ✅ | 1585 weapons (`gamedataWeaponItem_Record`) exported to JSONL (~12 MB). |
| `generate_redscript_template` | ✅ | 5 patterns generated, each re-passes `lint_script` without error. |
| `extract_localization` | ✅ | displayName/localizedDescription fields extracted from TweakDB as JSON (filter supported). |
| `build_localization` | ✅ | JSON `{recordId: {field: value}}` → valid `.tweak`. |
| `clear_cache` (archives) | ✅ | Cache cleared; `entries=0` afterwards. |
| `pipelining IPC` | ✅ | 4 concurrent `compute_hash` → 1 ms total (out-of-order responses tolerated). |
| `oodle_compress` / `oodle_decompress` | ✅ | Byte-exact round-trip (64,500 bytes ↔ 88 bytes compressed). |
| `wwise_export` | ✅ | WEM → OGG (parallel up to 4). |
| resource `reference` | ✅ | Cheat sheet up to date with new tools + prompts. |
| resource `archive/{+path}` | ✅ | Archive content listing. |
| resource `cr2w-json/{+path}` | ✅ | CR2W file rendered as JSON. |
