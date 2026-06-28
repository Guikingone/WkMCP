# Tool reference — WkMCP

Exhaustive reference of the **tools**, **prompts** and **resources** exposed by the WkMCP server for Cyberpunk 2077 modding.

> **Counts.** The server exposes **92 offline tools**, **8 prompts** and **4 resources** (figures confirmed by `tools/list`). To these are added **36 `live_*` tools** for the in-game live bridge — see [LIVE_BRIDGE.md](LIVE_BRIDGE.md) — for **128 tools** in total.

## Contents

- [Result convention](#result-convention) · [Live in-game bridge](#live-in-game-cetbridge-bridge)
- [1. Diagnostics](#1-diagnostics) · [2. Archive reading / inspection](#2-archive-reading--inspection) · [3. Extraction / uncook](#3-extraction--uncook) · [4. Conversion](#4-conversion)
- [5. Direct read/write of a game file](#5-direct-reading--writing-of-a-game-file) · [6. Quick inspection](#6-quick-inspection-summaries-without-heavy-conversion) · [7. TweakDB](#7-tweakdb) · [8. Template generation](#8-template-generation-scaffolding)
- [9. REDscript scripts](#9-redscript-scripts-reds) · [10. Audio / low-level compression](#10-audio--low-level-compression) · [11. Localization](#11-localization) · [12. Mod writing / packing](#12-mod-writing--packing)
- [13. REDmod](#13-redmod-post-16) · [14. Installation / uninstallation](#14-installation--uninstallation) · [15. Safety](#15-safety-backup--restore) · [16. In-game (launch / logs)](#16-in-game-launch--logs)
- [17. Intelligence / workflow](#17-intelligence--workflow-high-level--moddingtools) · [18. Quest/codex journal](#18-questcodex-journal-journal) · [19. CR2W navigation & conflicts](#19-generic-cr2w-navigation-diagnostics--conflicts) · [20. Advanced creation / maintenance](#20-advanced-creation--maintenance)
- [21. Asset inspection](#21-asset-inspection-materials--ui--rig--diff--nexus) · [22. Gameplay logic](#22-gameplay-logic-quest-phases--communities)
- [MCP prompts (recipes)](#mcp-prompts-recipes) · [MCP resources](#mcp-resources)

## Result convention

Each tool returns a structured JSON object, typically:

```json
{ "ok": true, "status": "success", "summary": "...", "produced": [], "warnings": [], "errors": [], "exitCode": 0, "log": "..." }
```

- `status` ∈ `success` | `partial` | `error` | `timeout`.
- For **file-producing tools**, success is judged on the files actually produced (`produced`), not on a log marker.
- For **information tools**, success is based on the exit code.
- A large log is truncated (head + errors + tail, ~12,000 chars); many tools accept `verbose=true` to retrieve the full log.
- On an argument validation error, `ok=false`, `status="error"`, `exitCode=-1`.

---

## Live in-game (CETBridge bridge)

35 `live_*` tools drive a **running** game (Lua execution, state
reading/writing, spawn, teleportation, weather, in-memory TweakDB, observing events).
Documented separately: see **[LIVE_BRIDGE.md](LIVE_BRIDGE.md)**. Prerequisites: game running + Cyber
Engine Tweaks (+ RedSocket for the TCP transport). The tools below are, themselves, **offline**.

---

## 1. Diagnostics

### `wk_status`
Verifies that the WolvenKit CLI (cp77tools) is available and functional, and returns its version + stats of the archive listings LRU cache (hits/misses) and per-verb metrics. To call first to diagnose the installation.

_No parameters._

### `clear_cache`
Manually empties the server caches. Useful after out-of-band modifications or to reset the stats before a benchmark.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `scope` | string | no (default `archives`) | Scope to empty: `archives` \| `metrics` \| `all`. |

### `compute_hash`
Computes the FNV1a64 hash used by REDengine for each provided string (typically game file paths).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `inputs` | string[] | yes | One or more strings to hash. |

### `resolve_hash`
Reverse lookup: finds the game file path corresponding to an FNV1a64 hash. The inverse of `compute_hash`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `hashes` | string[] | yes | One or more FNV1a64 hashes (unsigned integers). |

### `tweakdb_resolve`
Reverse lookup of TweakDB identifiers: a hash → the name of the identifier. Uses the TweakDB name database loaded at startup.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `hashes` | string[] | yes | One or more TweakDB identifier hashes (unsigned integers). |

### `tweakdb_query`
Queries the TweakDB: loads a `tweakdb.bin` and lists the records and flats whose identifier contains the filter. Results capped at 100 records + 100 flats.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakdbPath` | string | yes | Path of a `tweakdb.bin` file. |
| `filter` | string | no (default `""`) | Substring to search in identifiers (empty = all, 100 max). |

---

### `find_record_by_name`
Reverse lookup: finds TweakDB record IDs by their **human-facing** name. Where `tweakdb_query` matches the identifier, this searches the localized displayName/description **text** (e.g. "Overwatch" → its record IDs). Returns `{recordId: {field: value}}`. Re-scans the tweakdb each call (backed by the `extract_localization` path).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakdbPath` | string | yes | Path of a `tweakdb.bin` file. |
| `name` | string | yes | Text to search in the localized name/description (substring, case-insensitive). |
| `recordType` | string | no | Restrict to record IDs containing this substring (e.g. `Items.`, `Vehicle.`). |
| `maxResults` | int | no (default 50) | Max matches returned. |

---

## 2. Archive reading / inspection

### `archive_info`
Displays the information of a `.archive`: number of files and an optional filtered list. Listing served by the LRU cache.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archivePath` | string | yes | Absolute path of the `.archive` file. |
| `list` | bool | no (default `false`) | List the content (otherwise summary only). |
| `pattern` | string? | no | Optional glob filter on names, e.g. `*.mesh`. |

### `archive_stats`
Gives the breakdown of an `.archive`'s content by file extension (how many `.mesh`, `.ent`, `.xbm`, `.app`…). A quick overview without listing the whole archive. Listing served by the LRU cache. Returns `byExtension` (extension → count table, sorted), `categoryCount` (real total of types) and `fileCount`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archivePath` | string | yes | Absolute path of the `.archive` file. |
| `maxCategories` | int | no (default `100`) | Max number of extension categories returned; `categoryCount` always gives the real total. |

### `find_in_archives`
Searches for files across all the `.archive` files of a folder. Indicates which archive each file is in. Listings served by the LRU cache.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archivesFolder` | string | yes | Folder containing `.archive` files. |
| `pattern` | string? | no* | Glob pattern to search, e.g. `*player*.ent`. |
| `regex` | string? | no* | Regular expression (alternative to the glob). |

\* At least one of the two (`pattern` or `regex`) is required.

### `diff_archives`
Compares two `.archive` files and lists the added files (present in B only) and removed files (present in A only). Computes a real diff by cross-referencing the two listings.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archiveA` | string | yes | First archive (reference). |
| `archiveB` | string | yes | Second archive (to compare). |

---

### `diff_against_installed`
Compares a built mod `.archive` against the copy currently installed in `<game>/archive/pc/mod` (matched by filename): files only-in-build vs only-in-installed. Answers "is my working build in sync with what's installed?". Compares the file set (paths), not per-file content.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modArchive` | string | yes | Path to the built mod `.archive`. |
| `gamePath` | string | yes | Root folder of the Cyberpunk 2077 installation. |

---

## 3. Extraction / uncook

### `extract_files`
Extracts files from an `.archive` into a folder. Optional filtering by glob or regex.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archivePath` | string | yes | Absolute path of the `.archive`. |
| `outputPath` | string | yes | Destination folder. |
| `pattern` | string? | no | Optional glob filter, e.g. `*.mesh`. |
| `regex` | string? | no | Optional regex filter. |
| `verbose` | bool | no (default `false`) | Returns the full log (debug). |

### `uncook`
Extracts **and** converts in one pass (mesh → glTF, textures → image). Combines extraction and conversion.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archivePath` | string | yes | `.archive` file (or folder of archives). |
| `outputPath` | string | yes | Destination folder for the converted files. |
| `pattern` | string? | no | Optional glob filter. |
| `textureFormat` | string? | no | Image format: `png`, `dds`, `tga`, `bmp` or `jpg`. |
| `meshExportType` | string? | no | `MeshOnly`, `WithRig`, `Multimesh` (default `WithMaterials`). |
| `meshExporterType` | string? | no | `Default`, `Experimental`, `REDmod`. |
| `meshExportLodFilter` | bool | no (default `false`) | Filters the mesh export LODs. |
| `verbose` | bool | no (default `false`) | Returns the full log (debug). |

---

## 4. Conversion

### `cr2w_to_json`
Converts extracted REDengine CR2W files (`.mesh`, `.ent`, `.app`...) into readable and editable JSON.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | CR2W file or folder containing them. |
| `outputPath` | string | yes | Destination folder for the JSON. |

### `json_to_cr2w`
Reconverts JSON files (produced by `cr2w_to_json`) into binary CR2W files.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | JSON file or folder containing them. |
| `outputPath` | string | yes | Destination folder for the CR2W. |

### `set_texture_format`
Sets the texture group / compression / raw format of an extracted `.xbm` — the #1 silent retexture failure (wrong group/compression on reimport loses alpha, breaks normal maps, drops mipmaps). Round-trips the CR2W via JSON. Provide at least one of group/compression/rawFormat; read current values with `inspect_texture`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `xbmFile` | string | yes | Extracted `.xbm` file. |
| `group` | string? | no | Texture group CName (`TEXG_…`, e.g. `TEXG_Generic_Color` / `TEXG_Generic_Normal`). |
| `compression` | string? | no | Compression enum (`TCM_…`, e.g. `TCM_QualityColor` / `TCM_Normalmap` / `TCM_DXTAlpha`). |
| `rawFormat` | string? | no | Raw format enum (`TRF_…`, e.g. `TRF_TrueColor`). |
| `outputFile` | string? | no | Output `.xbm` (default: overwrite `xbmFile`). |

### `export_files`
Exports extracted REDengine files to raw formats (mesh → glTF, texture → image...).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | REDengine file or folder containing them. |
| `outputPath` | string | yes | Destination folder for the raw files. |
| `textureFormat` | string? | no | Image format: `png`, `dds`, `tga`, `bmp` or `jpg`. |

### `export_animation`
Exports a REDengine animation (`.anims`) to binary glTF (`.glb`). ⚠ A `.anims` alone (without its `.rig`) may produce nothing.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | `.anims` file or folder containing them. |
| `outputPath` | string | yes | Destination folder for the `.glb`. |
| `verbose` | bool | no (default `false`) | Returns the full log (debug). |

### `export_morphtarget`
Exports a REDengine morphtarget (`.morphtarget` — blendshapes) to binary glTF (`.glb`).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | `.morphtarget` file or folder containing them. |
| `outputPath` | string | yes | Destination folder for the `.glb`. |
| `verbose` | bool | no (default `false`) | Returns the full log (debug). |

### `export_mlmask`
Exports a REDengine multilayer mask (`.mlmask`) to images (one per layer).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | `.mlmask` file or folder containing them. |
| `outputPath` | string | yes | Destination folder for the images. |
| `textureFormat` | string? | no | `png` (default), `dds`, `tga`, `bmp`, `jpg` or `tiff`. |
| `verbose` | bool | no (default `false`) | Returns the full log (debug). |

---

## 5. Direct reading / writing of a game file

### `read_game_file`
Reads a game file in one call: extracts from the archive, converts to REDengine JSON and returns its content. The full JSON is also written to disk (`jsonFile`).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archivePath` | string | yes | Archive containing the desired file. |
| `gameFilePath` | string | yes | Internal path of the file within the archive. |

### `write_game_file`
Writes an edited game file: converts a JSON (from `read_game_file`) into binary CR2W, placed at the correct internal path in a mod folder.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `jsonFile` | string | yes | Edited JSON file. |
| `gameFilePath` | string | yes | Target internal path in the game. |
| `modArchiveFolder` | string | yes | Folder where to place the CR2W. |

---

## 6. Quick inspection (summaries without heavy conversion)

### `inspect_mesh`
Inspects a `.mesh` and returns a compact summary: LODs, sub-meshes, materials, bones. Much lighter than a full `uncook`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `meshFile` | string | yes | Already-extracted REDengine `.mesh` file. |

### `inspect_texture`
Inspects a `.xbm` (texture) and returns its metadata: resolution, format, compression, mipmaps, texture group — without PNG/DDS conversion.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `xbmFile` | string | yes | Already-extracted REDengine `.xbm` file. |

### `inspect_app`
Structural summary of a `.app` file: number of appearances, and for each the number of mesh components and the referenced meshes; total of distinct meshes. A quick overview **before** `validate_appearance` (which, itself, resolves and validates each `.mesh`). Lightweight: a single CR2W→JSON conversion, without mesh resolution. Returns `appearanceCount`, `meshComponentCount`, `distinctMeshCount` and `appearances` (per-appearance detail).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `appFile` | string | yes | Extracted `.app` file. |
| `maxAppearances` | int | no (default `100`) | Max number of detailed appearances returned; `appearanceCount` always gives the real total. |

### `add_appearance`
Adds a new appearance to a `.app` file by **cloning** an existing one — the only robust way (authoring a valid `appearanceAppearanceDefinition` from scratch is error-prone). Renumbers the cloned CR2W `HandleId`s to fresh unique values so the copy is an independent definition (not an alias of the source), optionally swaps mesh `DepotPath`s, then round-trips the CR2W via JSON and **self-verifies** that the new appearance survives deserialization before writing. Output is reinjectable via `pack_archive` / `write_game_file`. Use `inspect_app` first to list existing appearance names.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `appFile` | string | yes | Extracted `.app` file. |
| `newName` | string | yes | Name of the new appearance (must be unique in the `.app`). |
| `fromAppearance` | string | no (default: first) | Existing appearance to clone. |
| `meshSwapsJson` | string | no | JSON object of mesh `DepotPath` swaps applied in the clone, e.g. `{"base\\a\\old.mesh":"base\\a\\new.mesh"}`. Keys match case-insensitively. |
| `outputFile` | string | no (default: in place) | Output `.app` path. |

---

## 7. TweakDB

### `describe_tweak_record`
For a TweakDB identifier (record), lists all its flats with types and current values. Indispensable before editing via `write_tweak`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakdbPath` | string | yes | `tweakdb.bin` file. |
| `recordId` | string | yes | TweakDB identifier of the record. |

### `clone_tweak_record`
Clones an existing TweakDB record into a ready-to-edit `.tweak`. Verifies `baseId` exists, emits `<newId>:` with `$base: <baseId>` (TweakXL copies every base property at load — a faithful clone), then appends a commented inventory of all the base's flats with their current values (TweakDBIDs resolved, floats in invariant form, arrays expanded, Vector3 as `{ x, y, z }`) so you can see and uncomment exactly what to override. Stronger than `generate_tweak_template` (blind skeleton). Needs the daemon (the no-binary package has no TweakDB support). Install the result with `install_tweak`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakdbBin` | string | yes | `tweakdb.bin` file (typically `<game>/r6/cache/tweakdb.bin`). |
| `baseId` | string | yes | Existing record to clone (e.g. `Items.Preset_Lexington_Default`). |
| `newId` | string | yes | Identifier of the new record (e.g. `MyMod.MyLexington`). |
| `outputTweakFile` | string | yes | Output `.tweak` path (TweakXL YAML). |
| `overridesJson` | string | no | Overrides as JSON `{"field":value,…}` emitted as active keys (rest stays inherited). |

### `read_tweak`
Reads a `.tweak` file (TweakXL — YAML) and returns its content as editable JSON.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakFile` | string | yes | `.tweak` file (TweakXL). |

### `write_tweak`
Reconverts a JSON (from `read_tweak`) into a `.tweak` file (YAML TweakXL).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `jsonFile` | string | yes | Edited JSON file. |
| `outputTweakFile` | string | yes | `.tweak` file to produce. |

### `validate_tweak`
Verifies a `.tweak` against a TweakDB: each key must exist (record/flat) unless it declares `$instanceOf`. Returns the unknown keys.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakFile` | string | yes | `.tweak` file to validate. |
| `tweakdbBin` | string | yes | Reference `tweakdb.bin`. |

### `install_tweak`
Installs a `.tweak` in `<game>/r6/tweaks/`. Taken into account at the next launch (hot loading).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakFile` | string | yes | `.tweak` file to install. |
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

### `dump_records`
Exports all TweakDB records of a given type to JSON Lines (`.jsonl`) or CSV — for balance analysis.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakdbPath` | string | yes | `tweakdb.bin` file. |
| `recordType` | string | yes | Full name of the record CLR type (e.g. `gamedataWeaponItem_Record`). |
| `outputFile` | string | yes | Output file (`.jsonl` or `.csv`). |
| `format` | string | no (default `jsonl`) | `jsonl` or `csv`. |

---

## 8. Template generation (scaffolding)

### `generate_redscript_template`
Generates a `.reds` ready to edit from a catalog of patterns: `add_method`, `wrap_method`, `replace_method`, `add_field`, `new_class`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `pattern` | string | yes | `add_method` \| `wrap_method` \| `replace_method` \| `add_field` \| `new_class`. |
| `parametersJson` | string | yes | Template parameters as JSON (depending on the pattern). |
| `outputFile` | string | yes | `.reds` file to produce. |

**Keys of `parametersJson` depending on the pattern:**
- `add_method` / `replace_method`: `targetClass` (required), `methodName` (required), `args`, `returnType` (default `Void`), `body`.
- `wrap_method`: `targetClass` (required), `methodName` (required), `args`, `returnType` (default `Void`).
- `add_field`: `targetClass` (required), `fieldName` (required), `fieldType` (default `Int32`).
- `new_class`: `className` (required), `extends`, `moduleName`.

### `generate_tweak_template`
Generates a `.tweak` (TweakXL — YAML) from a catalog of patterns: `override_field`, `new_record`, `boost_stat`, `new_item`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `pattern` | string | yes | `override_field` \| `new_record` \| `boost_stat` \| `new_item`. |
| `parametersJson` | string | yes | Template parameters as JSON (depending on the pattern). |
| `outputFile` | string | yes | `.tweak` file to produce. |

**Keys of `parametersJson` depending on the pattern:**
- `override_field`: `recordId` (required), `field` (required), `value` (required).
- `new_record`: `newId` (required), `baseId` (required), `overrides` (sub-JSON `{field: value}`).
- `boost_stat`: `recordId` (required), `stat` (default `damage`), `value` (required).
- `new_item`: `newId` (required), `baseId` (required), `itemType` (`weapon`\|`clothing`\|`cyberware`\|`consumable`\|`recipe`). Emits safe item flats + a checklist of the type-specific flats to fill (run `describe_tweak_record` on `baseId` for exact schemas).

---

## 9. REDscript scripts (.reds)

### `read_script`
Reads a script file (`.reds`, `.script`, `.swift`, `.redscript`) and returns its content + structure extracted by regex (func/class, annotations, module/import). Textual analysis only.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `scriptFile` | string | yes | Script file. |

### `lint_script`
Syntactic analysis via a real parser (tokenizer + recursive descent): syntax errors (line:column) + semantic warnings (well-placed annotations, `@wrapMethod` calling `wrappedMethod()`, duplicates). No type checking.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `scriptFile` | string | yes | Script file. |

---

## 10. Audio / low-level compression

### `wwise_export`
Converts Wwise WEM audio files to OGG. Requires the native audio binaries (Windows). Parallel conversions (up to 4).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | `.wem` file or folder containing them. |
| `outputPath` | string | yes | Destination folder for the OGG. |
| `verbose` | bool | no (default `false`) | Returns the full log (debug). |

### `extract_audio`
Extracts the voice-over audio (opus) from a voice archive (typically `lang_xx_voice.archive`). By default extracts everything.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archivePath` | string | yes | Voice `.archive`. |
| `outputPath` | string | yes | Destination folder. |
| `opusHashes` | string? | no | Specific opus hashes (uint comma-separated). Empty = all. |
| `verbose` | bool | no (default `false`) | Returns the full log (debug). |

### `import_audio`
Imports WAVs (named by opus hash) into repacked `.opus` in a mod folder — voice-over replacement. ⚠ EXPERIMENTAL.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077 (or path of the `.exe`). |
| `wavFolder` | string | yes | Folder of the `.wav` (names = opus hashes). |
| `outputPath` | string | yes | Mod output folder. |
| `verbose` | bool | no (default `false`) | Returns the full log (debug). |

### `loc_resolve`
Resolves a localization key (LocKey: uint64 hash or secondary text key) into its localized text. ⚠ EXPERIMENTAL (loads the game archives).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077 (or path of the `.exe`). |
| `key` | string | yes | Key to resolve: uint64 hash or secondary text key. |
| `language` | string? | no (default `en_us`) | REDengine code: `en_us`, `fr_fr`, `de_de`, `jp_jp`... |

### `oodle_compress`
Compresses a file with the Oodle Kraken codec.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `inputPath` | string | yes | Input file. |
| `outputPath` | string | yes | Compressed output file. |

### `oodle_decompress`
Decompresses an Oodle Kraken compressed file.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `inputPath` | string | yes | Compressed input file. |
| `outputPath` | string | yes | Decompressed output file. |

---

## 11. Localization

### `extract_localization`
Extracts from a `tweakdb.bin` all the translatable fields of the records (displayName, etc.) — base for a UI translation mod. JSON output `{recordId: {field: value}}`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakdbPath` | string | yes | `tweakdb.bin` file. |
| `outputJson` | string | yes | Output JSON file. |
| `filter` | string? | no | Substring to search in the recordId (e.g. `Items.`). |

### `build_localization`
Builds a `.tweak` (TweakXL) that overrides displayName/localizedDescription from a translations JSON (from `extract_localization` then edited).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `translationsJson` | string | yes | Translations JSON. |
| `outputTweak` | string | yes | `.tweak` file to produce. |
| `lang` | string | no (default `fr-fr`) | Language code (informative, in a comment). |

---

## 12. Mod writing / packing

### `pack_archive`
Packs a folder of REDengine resources into a `.archive` (Kraken compression).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `folderPath` | string | yes | Folder of the resources to pack. |
| `outputPath` | string | yes | Destination folder for the archive. |
| `verbose` | bool | no (default `false`) | Returns the full log (debug). |

### `import_raw`
Imports raw files (textures, glTF meshes...) into REDengine CR2W, ready to be packed.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | yes | Raw file or folder containing them. |
| `outputPath` | string | yes | Destination folder for the REDengine files. |
| `verbose` | bool | no (default `false`) | Returns the full log (debug). |

### `build_project`
Compiles the WolvenKit projects (`.cpmodproj`) found in the given folder.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `projectFolder` | string | yes | Folder containing one or more `.cpmodproj`. |

### `create_mod_project`
Creates the structure of a WolvenKit mod project (source/archive, source/raw, source/resources, source/customSounds, packed) + a `<modName>.cpmodproj`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `parentFolder` | string | yes | Parent folder where to create the project. |
| `modName` | string | yes | Mod / project name. |
| `author` | string? | no | Mod author. |
| `version` | string? | no | Version (e.g. 1.0.0). |
| `description` | string? | no | Mod description. |

### `generate_modproj`
Generates a `.cpmodproj` in an EXISTING project folder, to make it compilable by `build_project`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `projectFolder` | string | yes | Project root folder. |
| `modName` | string | yes | Mod / project name. |
| `author` | string? | no | Author. |
| `version` | string? | no | Version (e.g. 1.0.0). |
| `description` | string? | no | Description. |
| `overwrite` | bool | no (default `false`) | Overwrite an existing `.cpmodproj`. |

### `lint_mod`
Verifies a `.archive` mod before installation: extensions not recognized by REDengine + conflicts with installed mods (if `gamePath`).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archivePath` | string | yes | Mod `.archive`. |
| `gamePath` | string? | no | Game root (enables conflict detection). |

### `mod_summary`
Compact synthesis of what a mod does. Accepts a `.archive` (summary by extension) or a REDmod folder (parses info.json, enumerates subfolders, extracts `.tweak` keys and `.reds` declarations).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modPath` | string | yes | `.archive` OR REDmod folder (with info.json). |

---

## 13. REDmod (post-1.6)

### `create_redmod_project`
Creates a REDmod project: `mods/<name>/info.json` + subfolders `archives/`, `scripts/`, `tweaks/`, `customSounds/`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `parentFolder` | string | yes | Parent folder. |
| `modName` | string | yes | REDmod name (subfolder). |
| `description` | string | no (default `""`) | Description shown in the launcher. |
| `version` | string | no (default `1.0.0`) | Semantic version. |

### `pack_redmod`
Packs a REDmod project into a `.zip` for distribution. Validates the presence of `info.json`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modSourceFolder` | string | yes | REDmod source folder (with info.json). |
| `outputPath` | string | yes | Destination folder for the `.zip`. |

### `validate_redmod`
Validates the `info.json` of a REDmod project: required fields `name` / `version` (+ numeric format), and consistency of the `customSounds` entries (each entry must have `name` + `type`; a `file` is required except for the `mod_skip` type, and it must exist in `customSounds/`). The other REDmod tools only check the **presence** of the `info.json`, never its content. Complements `validate_xl` / `validate_tweak` / `validate_item_mod`. `status` = `error` / `partial` / `success`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modPath` | string | yes | REDmod root folder (containing `info.json`) or direct path to the `info.json`. |

### `install_redmod`
Installs a REDmod project: recursive copy to `<game>/mods/<name>/`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modSourceFolder` | string | yes | REDmod source folder (with info.json). |
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

### `deploy_redmod`
Runs `<game>/tools/redmod/bin/redMod.exe deploy` — activates the installed REDmods (compiles scripts + applies tweaks). Timeout 5 min.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

---

## 14. Installation / uninstallation

### `install_mod`
Installs a mod: copies a `.archive` into `<game>/archive/pc/mod/`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archivePath` | string | yes | Mod `.archive`. |
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

### `uninstall_mod`
Uninstalls a mod: removes a `.archive` from `<game>/archive/pc/mod/`. Safeguard: refuses outside the mod folder.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archivePathOrName` | string | yes | Absolute path OR name of the `.archive` file. |
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

### `uninstall_redmod`
Uninstalls a REDmod: recursively deletes `<game>/mods/<modName>/`. Safeguard: refuses outside `mods/`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modName` | string | yes | REDmod name (subfolder under mods/). |
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

### `uninstall_tweak`
Uninstalls a `.tweak`: deletes `<game>/r6/tweaks/<tweakName>`. Safeguard: refuses outside `r6/tweaks/`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakName` | string | yes | Name of the `.tweak` file. |
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

### `list_installed_mods`
Lists the installed mods: `.archive` in `archive/pc/mod` and REDmods in `mods/`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

### `detect_conflicts`
Detects conflicts between installed mods (the same file provided by several mods). Structured JSON output.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

---

## 15. Safety (backup / restore)

### `backup_mods`
Saves the state of the mods (`archive/pc/mod/`, `mods/`, `r6/tweaks/`) into a timestamped `.zip`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |
| `outputDir` | string | yes | Folder where to drop the `.zip`. |
| `backupName` | string? | no | ZIP name (default `wkmcp-mods-backup-<YYYYMMDD-HHmmss>.zip`). |

### `restore_mods`
Restores a backup. Mode `merge` (over the existing) or `replace` (empties the target folders first — destructive).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `backupZip` | string | yes | Backup ZIP to restore. |
| `gamePath` | string | yes | Target root folder of Cyberpunk 2077. |
| `mode` | string | no (default `merge`) | `merge` \| `replace`. |

---

## 16. In-game (launch / logs)

### `launch_game`
⚠ Launches Cyberpunk 2077 (`bin/x64/Cyberpunk2077.exe`). If `deployRedmod=true`, runs `redMod.exe deploy` first. The game is launched detached.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |
| `deployRedmod` | bool | no (default `true`) | Runs `redMod.exe deploy` first. |
| `extraArgs` | string? | no | Additional arguments passed to the exe. |

### `tail_game_logs`
Reads the tail of the logs: `game` (r6/logs except redscript), `redmod` (tools/redmod/logs), `redscript` (r6/logs *redscript*), `all`. Optional substring filter.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |
| `log` | string | no (default `game`) | `game` \| `redmod` \| `redscript` \| `all`. |
| `lines` | int | no (default `200`) | Number of lines to return. |
| `filter` | string? | no | Substring filter (case-insensitive). |

---

## 17. Intelligence / workflow (high level — `ModdingTools`)

### `analyze_dependencies`
Analyzes a mod folder and deduces its required frameworks/dependencies (redscript, RED4ext, ArchiveXL, TweakXL, Codeware, Audioware, Mod Settings, CET...) by reading REDscript imports, `.xl`, `.tweak`, file types. If `gamePath` provided: indicates installed/missing.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modPath` | string | yes | Mod folder to analyze. |
| `gamePath` | string? | no | Game root, to check the installed dependencies. |

### `check_requirements`
Inventories the modding frameworks INSTALLED in a Cyberpunk 2077 installation, with version if detectable.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

### `mod_doctor`
Health diagnostic of a modded installation in one call: installed/missing frameworks, required but absent dependencies, archive conflicts, mod inventory + recommendations.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

### `validate_xl`
Validates an ArchiveXL `.xl` file (YAML): well-formed YAML + recognized top-level sections (`customSounds`, `resource`, `factories`, `localization`, `animations`).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `xlFile` | string | yes | `.xl` file to validate. |

### `scaffold_archivexl`
Generates a starter ArchiveXL `.xl` (commented YAML) depending on the type: `factory`, `customSounds`, `localization`, `resource`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `outputFolder` | string | yes | Destination folder for the `.xl`. |
| `modName` | string | yes | Mod name (= file name `<name>.xl`). |
| `kind` | string | no (default `factory`) | `factory` \| `customSounds` \| `localization` \| `resource`. |

### `find_references`
Searches for all textual references to a target in the source files of a folder (`.reds`, `.tweak`, `.yaml`, `.xl`, `.lua`, `.json`, `.csv`). Returns file:line + excerpt.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `target` | string | yes | String to search (substring). |
| `searchFolder` | string | yes | Folder to traverse. |
| `maxResults` | int | no (default `200`) | Max number of matches. |
| `caseSensitive` | bool | no (default `false`) | Case-sensitive search. |

### `diff_mod_vs_base`
Semantic diff of ONE game file overridden by a mod, against its base version: extracts from both sides, converts to JSON, compares the fields (added/removed/changed).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modArchive` | string | yes | Mod `.archive`. |
| `gameFilePath` | string | yes | Internal path of the file. |
| `gamePath` | string | yes | Game root (to locate the base in archive/pc/content). |
| `baseArchive` | string? | no | Specific base archive (short-circuits the search). |

### `scaffold_mod`
Creates a functional mod skeleton in one call depending on its type: `archive`, `redscript`, `tweak`, `redmod`. Also writes a `MOD_MANIFEST.json`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `parentFolder` | string | yes | Parent folder. |
| `modName` | string | yes | Mod name. |
| `kind` | string | no (default `archive`) | `archive` \| `redscript` \| `tweak` \| `redmod`. |
| `author` | string? | no | Author. |
| `version` | string? | no | Version (e.g. 1.0.0). |
| `dependencies` | string? | no | Declared dependencies, comma-separated. |

### `package_mod`
Packs a folder at the game-relative layout (archive/pc/mod, r6/scripts, r6/tweaks, mods/, red4ext/...) into a distributable `.zip` (`/` separators).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `sourceFolder` | string | yes | Source folder at the game layout. |
| `outputZip` | string | yes | Path of the output `.zip`. |

## 18. Quest/codex journal (`.journal`)

The `.journal` is a CR2W editable via `read_game_file`/`write_game_file`, but its JSON weighs ~70 MB (28,000+ entries). These tools make it navigable.

### `inspect_journal`
Navigable summary of a `.journal` converted to JSON: total number of entries, depth, breakdown by `$type`, top-level categories. Avoids loading the ~70 MB.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `jsonFile` | string | yes | JSON produced by `read_game_file` on a `.journal`. |

### `find_journal_entry`
Locates entries by `id`, `type` or `title` and returns the **exact JSON path** of each (e.g. `Data.RootChunk.entry.Data.entries[2].Data.entries[7].Data`) — to edit the targeted entry then rewrite via `write_game_file`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `jsonFile` | string | yes | JSON produced by `read_game_file` on a `.journal`. |
| `query` | string | yes | Value to search (substring, case-insensitive). |
| `field` | string | no (default `id`) | Targeted field: `id` \| `type` \| `title`. |
| `maxResults` | int | no (default 100) | Max number of matches. |

## 19. Generic CR2W navigation, diagnostics & conflicts

### `inspect_cr2w`
Navigable summary of ANY CR2W in JSON: root type, objects per `$type`, depth. For large files (quests, scenes, sectors, UI).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `jsonFile` | string | yes | JSON produced by `read_game_file` / `cr2w_to_json`. |

### `find_in_cr2w`
Searches in a CR2W (JSON) for objects whose field matches → **exact JSON path**.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `jsonFile` | string | yes | JSON produced by `read_game_file` / `cr2w_to_json`. |
| `query` | string | yes | Substring (case-insensitive). |
| `field` | string | no (default `$type`) | `$type`, a property name, or `*` (any text value). |
| `maxResults` | int | no (default 100) | Max matches. |

### `diagnose_logs`
Parses the 6 modding logs (redscript/RED4ext/ArchiveXL/TweakXL/Codeware/CET/REDmod), extracts/classifies the errors and maps known errors → fix.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |
| `maxPerSource` | int | no (default 30) | Max error lines per source. |

### `analyze_conflicts`
Robust conflicts (without the buggy WolvenKit verb): files provided by several `.archive` (+ who wins) and records defined by several `.tweak`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |
| `maxResults` | int | no (default 200) | Max conflicts per category. |

### `validate_item_mod`
Validates the reference chain of an ArchiveXL item mod: `.yaml`(entityName)↔`.csv`, displayName↔`.json secondaryKey`, presence of `.ent`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modPath` | string | yes | Mod folder (.yaml/.xl/.csv/.json). |
| `deep` | bool | no (default false) | Converts the `.ent` and checks the appearanceName. |

## 20. Advanced creation / maintenance

### `lint_tweak`
Semantic lint of a TweakXL file (`.tweak` / `.yaml`): tabs forbidden (silent load failure), indentation not a multiple of 2, duplicate record names, and an auto-generated `inlineN` record used as `$base` (breaks on every game update). Complements `validate_tweak` (which checks the keys vs `tweakdb.bin`). Read-only.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakFile` | string | yes | Path of the `.tweak` / `.yaml` file to lint. |

### `generate_manifest`
Generates a dependency manifest for a mod (detects its required frameworks, like `analyze_dependencies`) and writes a `REQUIREMENTS.md` (Nexus "Requirements" tab style) plus a structured object. Fills the absence of a machine-readable dependency system in the ecosystem.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modPath` | string | yes | Mod folder to analyze. |
| `modName` | string | no | Mod name (for the manifest header). Defaults to the folder name. |
| `version` | string | no | Mod version (printed in the header). |
| `writeFile` | bool | no | Write `REQUIREMENTS.md` in the mod folder (default `true`). |

### `resolve_dynamic_appearance`
Expands a dynamic ArchiveXL appearance path/pattern (`*` prefix, `{gender}`→`m`/`w`, `{camera}`→`fpp`/`tpp`) into its concrete paths — and, if `modPath` is provided, which ones actually exist. Targets the ArchiveXL trap where a substitution error only shows the first appearance. Read-only.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `pattern` | string | yes | Path pattern (e.g. `*base\mod\item_{gender}_{camera}.mesh`). |
| `modPath` | string | no | Mod folder, to check the existence of the concrete paths. |

### `migration_check`
Checks whether a `.archive` mod is still aligned with the **current** game version: for each file the mod provides, indicates whether it overrides an existing base file (active override) or not (an addition, or an override gone inert after a game update). Targets "updates silently break mods". Read-only.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modArchive` | string | yes | `.archive` of the mod to check. |
| `gamePath` | string | yes | Cyberpunk 2077 installation root folder. |
| `maxResults` | int | no | Max number of non-matching paths listed (default `100`). |

### `toggle_mods`
Enables or disables `.archive` mods by moving them between `archive/pc/mod` and `archive/pc/mod/_disabled` (reversible, non-destructive). The primitive of conflict bisection: disable half the mods → launch → diagnose → narrow down. Returns the up-to-date enabled/disabled lists.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Cyberpunk 2077 installation root folder. |
| `archives` | string | no | Archive names separated by commas (with or without `.archive`). Empty = move nothing, just list. |
| `enable` | bool | no | `true` = enable (re-enable), `false` = disable (default `false`). |

### `export_entity`
Exports a REDengine entity (`.ent`) appearance to glTF (`.glb`) via `IModTools.ExportEntity`. Discovers the entity's appearances first: if `appearance` is empty, takes the first; if invalid, returns the available list. ⚠ **Experimental** — WolvenKit refuses headless export of certain entity types ("can not be exported"); use `list_entity_appearances` to inspect, and `uncook` the referenced `.mesh` to view it reliably. See §4.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `entFile` | string | yes | Path to an extracted `.ent` (entity) file. |
| `outputPath` | string | yes | Output `.glb` file. |
| `appearance` | string | no | Appearance name (the entity's `name`). Empty = the first. |
| `gamePath` | string | no | Game root (loads archives to resolve meshes/materials). |

### `export_materials`
Exports the materials of a REDengine mesh (`.mesh`) to JSON + textures via `IModTools.ExportMaterials`. `gamePath` loads the archives to resolve the base material dependencies. Writes several files (JSON + textures) into the output folder. See §4.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `meshFile` | string | yes | Path to an extracted `.mesh` file. |
| `outputPath` | string | yes | Output file (materials JSON). |
| `gamePath` | string | no | Game root (loads archives to resolve the base materials). |

### `list_entity_appearances`
Lists the appearances of a REDengine entity (`.ent`): for each one, its entity `name` (to pass to `export_entity` / in the `.yaml`), the `appearanceName` on the `.app` side, and the referenced `.app` resource. Reliable and indispensable to know what an entity exposes before editing/exporting an appearance. Read-only.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `entFile` | string | yes | Path of an extracted `.ent` file. |

### `validate_appearance`
Deep validation of the `.app` → `.mesh` appearance chain: for each appearance of the `.app` and each mesh component, checks that the referenced `meshAppearance` actually exists in the `.mesh` (otherwise an invisible mesh) and that its materials (`chunkMaterials`) match the `materialEntries` (otherwise a black/inconsistent material). Resolves the `.mesh` in the mod, otherwise in the base game if `gamePath` is provided. Read-only.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `appFile` | string | yes | Path of an extracted `.app` file. |
| `modRoot` | string | no | Root of the mod folder (to resolve the mod's `.mesh`). |
| `gamePath` | string | no | Game root (to resolve base `.mesh` files not found in the mod). |
| `maxMeshes` | int | no | Max number of resolved `.mesh` (default `40`). |

---

## 21. Asset inspection (materials / UI / rig / diff / Nexus)

Inspectors for asset families that previously had no dedicated tooling. Each accepts either a
binary CR2W file (converted internally via the daemon) or a `.json` already produced by
`read_game_file` / `cr2w_to_json`. Field shapes follow WolvenKit's CR2W→JSON conventions and the
parsers are defensive (extract what is present, degrade gracefully).

### `inspect_material`

Summary of a material instance (`.mi` / `CMaterialInstance`): its `baseMaterial` and every parameter
with its kind (color, scalar, texture, vector…) and value; texture parameters expose their DepotPath.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `materialOrJson` | string | yes | A `.mi` file or its converted `.json`. |

### `inspect_mlsetup`

Summary of a multilayer setup (`.mlsetup` / `Multilayer_Setup`): its layers, and per layer the
material / microblend referenced plus `colorScale`, `opacity`, `normalStrength` and tiling.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `mlsetupOrJson` | string | yes | A `.mlsetup` file or its converted `.json`. |

### `edit_material_instance`

Sets ONE named parameter of a `.mi` and writes the edited JSON to `outputJson` (then feed it to
`json_to_cr2w` / `write_game_file`). The parameter must already exist (use `inspect_material`).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `materialOrJson` | string | yes | A `.mi` file or its converted `.json`. |
| `outputJson` | string | yes | Output JSON path. |
| `parameter` | string | yes | Parameter name to set (e.g. `DiffuseColor`). |
| `value` | string | yes | Depot path (texture), `r,g,b,a` (color), a number (scalar) or text (string). |
| `type` | string | no | `texture` \| `color` \| `scalar` \| `string` (default `texture`). |

### `trace_material_chain`

Traces the material pipeline from a starting file (`.mesh`/`.app`/`.ent`/`.mi`/`.mlsetup`) down to
the textures it ends up using, following resource references across files. References are resolved by
base name under `depotRoot`, then in the base game under `gamePath`; unresolved refs are flagged.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `fileOrJson` | string | yes | Starting file or its `.json`. |
| `depotRoot` | string | no | Folder of extracted/converted files used to resolve references. |
| `gamePath` | string | no | Game root, to resolve references not found under `depotRoot`. |
| `maxDepth` | int | no | Max chain depth to follow (default `6`). |

### `inspect_inkatlas`

Summary of a UI texture atlas (`.inkatlas` / `inkTextureAtlas`): the texture(s) it packs and every
named part (sprite) with its clipping rect in UV and pixel coordinates.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `inkatlasOrJson` | string | yes | An `.inkatlas` file or its converted `.json`. |
| `maxParts` | int | no | Max parts returned (default `200`). |

### `resolve_inkatlas_part`

Looks up ONE part (sprite) in an `.inkatlas` by name and returns its backing texture DepotPath and
clipping rect. Returns close matches if the exact name is absent.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `inkatlasOrJson` | string | yes | An `.inkatlas` file or its converted `.json`. |
| `partName` | string | yes | Part name to resolve (CName). |

### `inspect_inkwidget`

Summary of a UI widget library (`.inkwidget` / `inkWidgetLibraryResource`): the named library items
and a histogram of the widget types used (`inkTextWidget`, `inkImageWidget`, `inkCanvasWidget`…).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `inkwidgetOrJson` | string | yes | An `.inkwidget` file or its converted `.json`. |

### `inspect_rig`

Summary of a rig/skeleton (`.rig` / `animRig`): bone count, root bone(s), hierarchy depth, and each
bone with its parent — built from `boneNames` + `boneParentIndexes`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `rigOrJson` | string | yes | A `.rig` file or its converted `.json`. |
| `maxBones` | int | no | Max bones listed (default `300`). |

### `diff_cr2w`

Semantic diff of TWO arbitrary CR2W files (or their JSON): field-level added / removed / changed with
each change's exact JSON path. Generalizes `diff_mod_vs_base` to any two files.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `fileA` | string | yes | First file (the "base" side): a CR2W binary or its `.json`. |
| `fileB` | string | yes | Second file (the "new" side): a CR2W binary or its `.json`. |
| `maxResults` | int | no | Max entries returned per category (default `100`). |

### `package_for_nexus`

Nexus pre-flight + packaging: flags files Nexus auto-quarantines (`.dll`/`.exe`/`.asi`…), checks for a
recognized game layout, reports framework dependencies, then writes a distributable `.zip`. Stricter
than `package_mod`. Set `allowBinaries=true` for RED4ext/CET mods that legitimately ship a `.dll`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `sourceFolder` | string | yes | Mod folder in the game layout. |
| `outputZip` | string | yes | Output `.zip` path. |
| `allowBinaries` | bool | no | Allow quarantined binaries instead of failing (default `false`). |

---

## 22. Gameplay logic (quest phases / communities)

Inspectors for the two file families that drive quest flow and world population. Both accept a binary
CR2W (converted via the daemon) or a `.json` already produced by `read_game_file` / `cr2w_to_json`.
Validated against real Cyberpunk 2077 files.

### `inspect_questphase`

Summary of a quest phase graph (`.questphase` / questQuestPhaseResource): its nodes (with a per-type
histogram), the node→node edges reconstructed from the socket connections, the entry/exit nodes
(`questInput`/`questOutput`), and the `.scene` files and sub-phases it triggers. The map of a quest's
flow — which scenes fire, in what order, and where it starts/ends. Pair with `inspect_scene` on the
referenced `.scene` files.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `questphaseOrJson` | string | yes | A `.questphase` file or its converted `.json`. |
| `maxNodes` | int | no | Max nodes listed (default `400`). `nodeCount` always gives the real total. |
| `maxEdges` | int | no | Max edges listed (default `600`). `edgeCount` always gives the real total. |

### `inspect_community`

Summary of a community / population template (`.community` / communityCommunityTemplate): each spawn
entry with the `Character.*` record it spawns, its appearances, its spawn phases and per-phase time
periods (Day/Night quantities), plus voice-tag initializers. The map of who populates a location/quest
scene and when — so you know which entry to retune or which character to swap.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `communityOrJson` | string | yes | A `.community` file or its converted `.json`. |
| `maxEntries` | int | no | Max entries listed (default `200`). `entryCount` always gives the real total. |

---

## MCP prompts (recipes)

Each prompt returns instructive text (steps + tools to call), not a direct execution.

### `read_game_file_workflow`
Recipe: locate then read a game file as JSON, in one go.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `filePattern` | string | yes | Type or partial name to search (e.g. `player.ent`). |
| `contentFolder` | string | yes | Game content folder (`archive/pc/content`). |

### `edit_tweakdb_item`
Recipe: modify the parameters of a TweakDB item via a `.tweak`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakId` | string | yes | TweakDB identifier of the item (e.g. `Items.w_melee_001`). |
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

### `pack_and_install_mod`
Recipe: pack a source folder into a `.archive` and install it.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modSourceFolder` | string | yes | Source folder of the mod project. |
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

### `recolor_texture`
Recipe: extract a texture, edit it, then reintegrate it into a mod.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archivePath` | string | yes | Archive containing the texture. |
| `texturePattern` | string | yes | Glob pattern of the texture (e.g. `*jacket_01*.xbm`). |

### `inspect_mesh`
Recipe: export a mesh to glTF to inspect it (Blender / viewer), with export options.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `archivePath` | string | yes | Archive containing the mesh. |
| `meshInternalPath` | string | yes | Internal path of the mesh. |

### `create_archivexl_item`
Recipe: create an ArchiveXL item mod end-to-end, with validation of the record → factory → localization chain (`validate_item_mod`).

| Parameter | Type | Required | Description |
|---|---|---|---|
| `modName` | string | yes | Name of the mod/item to create. |
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

### `diagnose_broken_mod`
Recipe: diagnose a broken mod or a crashing install — `mod_doctor` → `diagnose_logs` → `analyze_conflicts` → `toggle_mods` bisection.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |
| `symptom` | string | yes | Observed symptom (crash, missing item, script with no effect…). |

### `live_iteration_loop`
Recipe: iterate TweakDB values LIVE (game running + CETBridge) via `live_tweakdb_set`, then freeze the result into a `.tweak`.

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tweakId` | string | yes | TweakDB identifier to iterate. |
| `gamePath` | string | yes | Root folder of Cyberpunk 2077. |

---

## MCP resources

Readable data exposed by URI.

| Resource | URI Template | MIME | Description |
|---|---|---|---|
| WolvenKit reference | `wkmcp://reference` | `text/markdown` | Cheat sheet generated by reflection from the real tools: complete list, REDengine formats, modding workflow. |
| Installed mods | `wkmcp://mods/{+gamePath}` | `text/markdown` | Inventory of the mods of an installation (archives, REDmods, tweaks, scripts), game root as absolute path after `wkmcp://mods/`. |
| Archive content | `wkmcp://archive/{+path}` | `text/plain` | Lists the content of a `.archive` identified by its absolute path after `wkmcp://archive/`. |
| REDengine file as JSON | `wkmcp://cr2w-json/{+path}` | `application/json` | Renders an extracted CR2W file (`.mesh`, `.ent`, `.app`...) as JSON, identified by its absolute path after `wkmcp://cr2w-json/`. |
