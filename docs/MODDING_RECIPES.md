# Modding recipes — WkMCP

Concrete, copy-paste-ready recipes for the most common Cyberpunk 2077 mod
types. Each recipe chains **MCP tool calls** (with example
arguments) and ends with a **verification**.

## Which recipe for what?

| If you want to… | Recipe | Needs |
|---|---|---|
| Change a stat/field of an existing item | 1. Weapon / item tweak | TweakXL, `tweakdb.bin` |
| Add behavior with REDscript (hook a game method) | 2. REDscript mod | redscript |
| Add an item/appearance/resource via ArchiveXL | 3. Appearance / item | ArchiveXL |
| Ship a mod in the official REDmod format | 4. REDmod | REDmod DLC (`redMod.exe`) |
| Translate UI strings (items, etc.) | 5. Localization | TweakXL, `tweakdb.bin` |
| Replace a texture (retexture) | 6. Texture replacement | an image editor |
| Understand/audit an existing mod | 7. Analyzing a mod | the mod to inspect |
| Edit a quest/codex/journal entry | 8. Quest/codex journal | a mod project (`source/archive`) |

## Conventions

- All tools return a JSON `{ ok, status, summary, produced, warnings, errors, log }`.
  **Success is judged on `produced`/`ok`**, not on the content of the `log`.
- Paths must be **absolute** (Windows: `C:\...`). In the examples, the game
  folder is written `<GAME>` (e.g. `C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077`).
- Before any recipe, verify that the CLI is operational with **`wk_status`**.
- Tools whose arguments carry default values (e.g. `pattern`,
  `verbose`) may be omitted.

---

## 1. Weapon / item tweak (TweakXL)

**Goal**: modify a stat of an existing weapon (here: the magazine capacity of the
Lexington) without touching the game files, via a `.tweak` hot-loaded by TweakXL.

1. **`generate_tweak_template`** — generate the `.tweak`.
   - `pattern`: `"override_field"`
   - `parametersJson`: `{"recordId":"Items.Preset_Lexington_Default","field":"magazineCapacity","value":24}`
   - `outputFile`: `C:\mods\lexington_mag\lexington.tweak`

   "Increase a numeric stat" variant: `pattern` = `"boost_stat"` with
   `{"recordId":"Items.Preset_Lexington_Default","stat":"damage","value":150}`.

   "New item derived from an existing one" variant: `pattern` = `"new_record"` with
   `{"newId":"MyMod.SuperLexington","baseId":"Items.Preset_Lexington_Default","overrides":"{\"magazineCapacity\":48}"}`.

2. **`validate_tweak`** — verify that each key actually exists in the TweakDB (except
   new records declaring `$base` or `$type`).
   - `tweakFile`: `C:\mods\lexington_mag\lexington.tweak`
   - `tweakdbBin`: `<GAME>\r6\cache\tweakdb.bin`

3. **`install_tweak`** — copy to `<GAME>\r6\tweaks\`.
   - `tweakFile`: `C:\mods\lexington_mag\lexington.tweak`
   - `gamePath`: `<GAME>`

**Verification**: `validate_tweak` returns `status: "success"` and an empty list of
unknown keys; `install_tweak` returns `installedPath` pointing into `r6\tweaks`.
TweakXL loads the file at the next launch, with no rebuild.

---

## 2. REDscript mod with `@wrapMethod`

**Goal**: extend a game method (e.g. `PlayerPuppet.OnGameAttached`) without
replacing it, via a generated REDscript starter then syntactically verified.

1. **`scaffold_mod`** — create the REDscript skeleton.
   - `parentFolder`: `C:\mods`
   - `modName`: `MyHook`
   - `kind`: `"redscript"`
   - `dependencies` (optional): `"redscript"`

   Produces `C:\mods\MyHook\r6\scripts\MyHook\MyHook.reds` already containing a
   `@wrapMethod(PlayerPuppet)` on `OnGameAttached` that calls `wrappedMethod()`, plus
   a `MOD_MANIFEST.json`.

   Targeted alternative: **`generate_redscript_template`** with `pattern` = `"wrap_method"`
   to produce a single parameterized `.reds`.

2. *(edit)* — adapt the body of the method in the `.reds`.

3. **`lint_script`** — syntactic + semantic analysis (real parser, 0 false positives
   calibrated).
   - `scriptFile`: `C:\mods\MyHook\r6\scripts\MyHook\MyHook.reds`

   Checks in particular that the annotation targets a class and that a `@wrapMethod`
   calls `wrappedMethod()`.

4. **Deployment**: copy `r6\scripts\...` to `<GAME>\r6\scripts\...` (the "classic"
   redscript is loaded directly; in the REDmod pipeline, see recipe 4).

**Verification**: `lint_script` returns `status: "success"` (or `"partial"` with mere
warnings), `errors: []`, and a non-null `declarations`/`parsedDeclarations`.
`lint_script` does NOT perform type checking (external resolution = `scc`).

---

## 3. Appearance / item via ArchiveXL

**Goal**: declare a factory record (or a resource patch / sounds /
localization) via an ArchiveXL `.xl` file.

1. **`scaffold_archivexl`** — generate the starter `.xl`.
   - `outputFolder`: `C:\mods\MyAppearance`
   - `modName`: `MyAppearance`
   - `kind`: `"factory"`  (others: `"customSounds"`, `"localization"`, `"resource"`)

   Produces `C:\mods\MyAppearance\MyAppearance.xl`. For `factory`, it references a
   `MyAppearance\factory.csv`; for `resource`, it pre-fills a
   `resource: patch:` block; etc.

2. *(edit)* — complete the `.xl` (paths of the `.app`/CSV) and prepare the associated
   resource files.

3. **`validate_xl`** — validate the YAML and the top-level sections.
   - `xlFile`: `C:\mods\MyAppearance\MyAppearance.xl`

   Recognized sections: `customSounds`, `resource`, `factories`, `localization`,
   `animations`. An unknown section generates a warning (`status: "partial"`),
   a malformed YAML an error (`status: "error"`).

4. **Deployment**: place the `.xl` in `<GAME>\archive\pc\mod\` next to the mod's
   archive (ArchiveXL reads the `.xl` files of this folder).

**Verification**: `validate_xl` returns `status: "success"`, `errors: []` and lists the
expected `sections`.

---

## 4. REDmod (post-1.6 format)

**Goal**: produce a mod in REDmod format (archives + scripts + tweaks), then
install and officially deploy it via `redMod.exe`.

1. **`create_redmod_project`** — create the `mods/<name>/` structure.
   - `parentFolder`: `C:\mods`
   - `modName`: `MyRedmod`
   - `description`: `"My first REDmod"`
   - `version`: `"1.0.0"`

   Creates `C:\mods\MyRedmod\` with `info.json` + subfolders `archives/`, `scripts/`,
   `tweaks/`, `customSounds/`.

2. *(filling)* — drop the `.archive` files into `archives/`, the `.reds` into
   `scripts/`, the `.tweak` into `tweaks/`.

3. **`pack_redmod`** *(optional, distribution)* — produce a distributable `.zip`.
   - `modSourceFolder`: `C:\mods\MyRedmod`
   - `outputPath`: `C:\dist`

   The `.zip` includes the `MyRedmod/` folder (with `info.json`); the end user
   decompresses it into `<GAME>\mods\`.

4. **`install_redmod`** — recursive copy to `<GAME>\mods\<name>\`.
   - `modSourceFolder`: `C:\mods\MyRedmod`
   - `gamePath`: `<GAME>`

5. **`deploy_redmod`** — runs `<GAME>\tools\redmod\bin\redMod.exe deploy` (compiles the
   scripts + applies the tweaks of installed REDmods).
   - `gamePath`: `<GAME>`

**Verification**: `install_redmod` returns a `produced` in `<GAME>\mods\<name>`;
`deploy_redmod` returns `exit=0` in its `summary`. (The REDmod DLC must be installed,
otherwise `redMod.exe` is not found.) You can confirm with **`list_installed_mods`**
(`gamePath` = `<GAME>`) which should list the mod in `redMods`.

---

## 5. Localization (UI translation)

**Goal**: extract the translatable strings from the TweakDB, translate them, then
build a `.tweak` that overrides `displayName` / `localizedDescription` / etc.

1. **`extract_localization`** — extract the translatable fields to a JSON
   `{recordId: {field: value}}`.
   - `tweakdbPath`: `<GAME>\r6\cache\tweakdb.bin`
   - `outputJson`: `C:\mods\loc_fr\strings.json`
   - `filter` (optional): `"Items."` to target only items.

2. *(edit)* — translate the values in `strings.json` (keep the structure
   `{recordId: {field: "Translation"}}`).

3. **`build_localization`** — build the TweakXL `.tweak` from the translated JSON.
   - `translationsJson`: `C:\mods\loc_fr\strings.json`
   - `outputTweak`: `C:\mods\loc_fr\loc_fr.tweak`
   - `lang` (informative): `"fr-fr"`

4. **`install_tweak`** — copy to `<GAME>\r6\tweaks\`.
   - `tweakFile`: `C:\mods\loc_fr\loc_fr.tweak`
   - `gamePath`: `<GAME>`

**Verification**: `build_localization` returns `recordCount`/`fieldCount` > 0 and
`status: "success"`. (Limitation: it only covers TweakDB UI strings; audio
subtitles `.opusinfo` are not concerned.)

---

## 6. Texture replacement

**Goal**: extract a game texture, edit it as PNG, reimport it, pack it
into a `.archive` and install it.

1. **`uncook`** — extract + convert the texture into an editable image.
   - `archivePath`: `<GAME>\archive\pc\content\basegame_4_gamedata.archive`
     (or the archive containing the target texture)
   - `outputPath`: `C:\work\uncooked`
   - `pattern`: `"*.xbm"` (or a specific texture path)
   - `textureFormat`: `"png"`

   *(To only extract without converting: **`extract_files`** with the same
   `archivePath`/`outputPath`/`pattern`.)*

2. *(edit)* — modify the PNG in an image editor, keeping the
   relative tree `base\...` produced by `uncook`.

3. **`import_raw`** — reimport the PNG into REDengine CR2W.
   - `path`: `C:\work\uncooked`  (file or folder)
   - `outputPath`: `C:\work\cooked`

   The folder structure `base\...` must be preserved (= the game path of the
   resource).

4. **`pack_archive`** — pack the folder into a `.archive` (Kraken compression).
   - `folderPath`: `C:\work\cooked`
   - `outputPath`: `C:\dist`

5. **`install_mod`** — copy the archive into `<GAME>\archive\pc\mod\`.
   - `archivePath`: `C:\dist\cooked.archive`
   - `gamePath`: `<GAME>`

**Verification**: each step returns a non-empty `produced` (`uncook` → image,
`import_raw` → CR2W, `pack_archive` → `.archive`); `install_mod` returns the destination
path in `archive\pc\mod`. Confirm with **`list_installed_mods`** or
**`mod_summary`** on the produced archive.

---

## 7. Analyzing an existing mod

**Goal**: understand what a mod does, its dependencies, where a target is
referenced, and what it actually changes relative to the base game.

1. **`mod_summary`** — compact synthesis (accepts a `.archive` OR a REDmod folder).
   - `modPath`: `C:\mods\SomeMod\SomeMod.archive`
     (or `C:\mods\SomeRedmod` with `info.json` at the root)

   For an archive: number of files + breakdown by extension. For a REDmod:
   `info.json`, content of `archives/`/`scripts/`/`tweaks/`/`customSounds/`, top-level
   keys of the `.tweak` and declarations of the `.reds`.

2. **`analyze_dependencies`** — deduce the required frameworks (redscript, RED4ext,
   ArchiveXL, TweakXL, Codeware, Audioware, etc.).
   - `modPath`: `C:\mods\SomeRedmod`
   - `gamePath` (optional): `<GAME>` — indicates for each dependency whether it is
     `installed` or `MISSING`.

3. **`find_references`** — find all textual references to a target in the
   mod's sources (`.reds`, `.tweak`, `.xl`, `.yaml`, `.lua`, `.json`, `.csv`).
   - `target`: `"Items.Preset_Lexington_Default"`
   - `searchFolder`: `C:\mods\SomeRedmod`
   - `maxResults` (optional): `200`

   *(To search in the game's `.archive` files, use `find_in_archives` instead.)*

4. **`diff_mod_vs_base`** — semantic diff of an overridden file against its base
   version (fields added / removed / changed).
   - `modArchive`: `C:\mods\SomeMod\SomeMod.archive`
   - `gameFilePath`: `base\path\to\file.app`
   - `gamePath`: `<GAME>`  (locates the base in `archive\pc\content`)
   - `baseArchive` (optional): a specific base archive to short-circuit the
     search.

**Verification**: `mod_summary`/`analyze_dependencies` return `status: "success"`
(`"partial"` if dependencies are missing); `find_references` returns a `matchCount` and the
`matches` list (file:line); `diff_mod_vs_base` summarizes `added`/`removed`/`changed`.
If `diff_mod_vs_base` does not find the file in the base, it is probably a file
**added** by the mod (not an override).

---

## 8. Editing the quest/codex journal (`.journal`)

The journal (`base\journal\cooked_journal.journal`) is a standard CR2W, so editable
via the generic pipeline — but its JSON weighs **~70 MB** (28,000+ entries). You
navigate it first, then edit only the targeted entry.

**Goal**: modify a specific entry (quest, contact, codex, e-mail…).

1. **Locate**: `find_in_archives` (`pattern: "*.journal"`,
   `archivesFolder: <GAME>\archive\pc\content`) → `base\journal\cooked_journal.journal`
   (in `basegame_4_gamedata.archive`).
2. **Extract to JSON**: `read_game_file` (`archivePath` = the archive,
   `gameFilePath: base\journal\cooked_journal.journal`). The returned content is truncated;
   note the **`jsonFile`** field (the full JSON on disk).
3. **Map**: `inspect_journal` (`jsonFile`) → total entries, breakdown by
   `$type`, top-level categories (`quests`, `codex`, `contacts`, `briefings`…).
4. **Target**: `find_journal_entry` (`jsonFile`, `query`, `field` ∈ `id`|`type`|`title`).
   E.g. `field:"id", query:"holofixer_used_1"` or `field:"type", query:"gameJournalContact"`.
   Returns the **exact JSON path** of each entry
   (e.g. `Data.RootChunk.entry.Data.entries[3].Data.entries[1].Data`).
5. **Edit** the `jsonFile` at that path (modify the entry's fields).
6. **Rewrite**: `write_game_file` (`jsonFile`, identical `gameFilePath`,
   `modArchiveFolder` = the project's `source/archive`) → CR2W rebuilt.
7. **Pack/install**: `pack_archive` → `install_mod`.

**Verification**: `inspect_journal` confirms total/types; the CR2W `.journal` produced by
`write_game_file` is non-empty (a few MB); `diff_mod_vs_base` confirms what changed.

---

## Appendix — useful cross-cutting calls

- **`wk_status`**: to call first (CLI availability + cache stats).
- **`detect_conflicts`** (`gamePath` = `<GAME>`): spots two mods providing the same
  game file.
- **`lint_mod`**: global lint of a mod folder.
- **`package_mod`** (`sourceFolder` at the game layout `archive/`, `r6/`, `mods/...` →
  `outputZip`): produces a distributable `.zip` (Nexus / manual install) with `/`
  separators.
- **`check_requirements`** (`gamePath` = `<GAME>`): inventories the installed frameworks.
