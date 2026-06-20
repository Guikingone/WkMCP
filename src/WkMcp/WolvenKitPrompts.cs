using System.ComponentModel;
using ModelContextProtocol.Server;

namespace WkMcp;

/// <summary>
/// MCP prompts for WolvenKit — ready-to-use recipes a Claude agent can invoke to
/// kick off a Cyberpunk 2077 modding workflow. Each prompt returns instructional
/// text (steps to follow + tools to call), not a direct execution: the agent stays
/// in control of the choices.
/// </summary>
[McpServerPromptType]
public static class WolvenKitPrompts
{
    [McpServerPrompt(Name = "read_game_file_workflow")]
    [Description("Recipe: locate then read a game file as JSON, in one shot. " +
                 "Useful to quickly inspect an asset (mesh, ent, app…) without packing.")]
    public static string ReadGameFileWorkflow(
        [Description("Type or partial name to search, e.g. \"player.ent\" or \"*.streamingsector\".")]
        string filePattern,
        [Description("Game content folder, e.g. C:\\Cyberpunk\\Cyberpunk 2077\\archive\\pc\\content")]
        string contentFolder)
    {
        return $$"""
            Procedure to read a Cyberpunk 2077 game file (pattern: {{filePattern}}):

            1. Call `find_in_archives`:
               - archivesFolder = "{{contentFolder}}"
               - pattern = "{{filePattern}}"
               → returns a list of internal paths and their archives.

            2. Pick the right result, then call `read_game_file`:
               - archivePath = the archive identified in step 1
               - gameFilePath = the internal path (e.g. base\\path\\file.ent)
               → returns editable JSON (`content` inline + full `jsonFile` on disk).

            If the output reports `truncated: true`, read the full file via the path
            returned in `jsonFile`.
            """;
    }

    [McpServerPrompt(Name = "edit_tweakdb_item")]
    [Description("Recipe: change a TweakDB item's parameters (damage, price, etc.) " +
                 "via a .tweak file that overrides the game's TweakDB.")]
    public static string EditTweakDbItem(
        [Description("TweakDB identifier of the item to edit (e.g. Items.w_melee_001).")]
        string tweakId,
        [Description("Root folder of the Cyberpunk 2077 installation.")]
        string gamePath)
    {
        return $$"""
            Procedure to edit the TweakDB item {{tweakId}} in Cyberpunk 2077:

            1. Confirm the identifier exists via `tweakdb_query`:
               - tweakdbPath = {{gamePath}}\r6\cache\tweakdb.bin
               - filter = "{{tweakId}}"
               → checks existence and lists that identifier's flats.
               If needed, `describe_tweak_record` details the record's flats.

            2. Write the .tweak file (TweakXL format — YAML) via `write_tweak` with the
               fields to override. Minimal example content:

                   {{tweakId}}:
                     damage: 200
                     attacksPerSecond: 2.5

            3. Validate with `validate_tweak` (and `lint_tweak` for indentation pitfalls),
               then install with `install_tweak`:
               - gamePath = "{{gamePath}}"
               → drops the file into {{gamePath}}\r6\tweaks\ (TweakXL required).
               The .tweak does not need to be packed into a .archive.

            4. Relaunch the game (or, if the game is running with the CETBridge mod,
               check the value live via `live_tweakdb_get`).
            """;
    }

    [McpServerPrompt(Name = "pack_and_install_mod")]
    [Description("Recipe: close the modding loop — pack a source folder into a " +
                 ".archive and install it into the Cyberpunk 2077 installation.")]
    public static string PackAndInstallMod(
        [Description("Source folder of a mod project (containing the cooked REDengine files).")]
        string modSourceFolder,
        [Description("Root folder of the Cyberpunk 2077 installation.")]
        string gamePath)
    {
        return $$"""
            Procedure to pack and install a Cyberpunk 2077 mod:

            1. (Recommended) Lint the source folder structure before packing:
               - Make sure no non-REDengine file (.txt, .md…) is lying around in
                 {{modSourceFolder}} — `pack_archive` will silently ignore them.

            2. Call `pack_archive`:
               - folderPath = "{{modSourceFolder}}"
               - outputPath = an output folder (e.g. {{modSourceFolder}}\..\packed)
               → produces a .archive ready to install.

            3. Call `install_mod`:
               - archivePath = the .archive produced in step 2
               - gamePath = "{{gamePath}}"
               → copies the archive into {{gamePath}}\archive\pc\mod\.

            4. At the next game launch, the mod is active. To disable it,
               remove the archive from the mod/ folder.
            """;
    }

    [McpServerPrompt(Name = "recolor_texture")]
    [Description("Recipe: extract a texture from the game, edit it (recolor, retouch), " +
                 "then re-integrate it into a mod.")]
    public static string RecolorTexture(
        [Description("Path of the archive containing the texture, e.g. base_2_textures.archive.")]
        string archivePath,
        [Description("Glob pattern of the texture (e.g. *jacket_01*.xbm).")]
        string texturePattern)
    {
        return $$"""
            Procedure to recolor/replace a game texture:

            1. Call `uncook`:
               - archivePath = "{{archivePath}}"
               - outputPath = a working folder (e.g. C:\Temp\wkmod-textures)
               - pattern = "{{texturePattern}}"
               - textureFormat = "png" (to edit in GIMP / Photoshop)
               → extracts + converts the texture to an editable .png.

            2. Edit the produced .png (colors, contrast, etc.) with your preferred image
               tool. Keep the same file name.

            3. Call `import_raw`:
               - path = the modified .png
               - outputPath = source\archive\<internal path> of the mod project
               → reconverts the .png into a REDengine .xbm at the right location.

            4. Call `pack_archive` then `install_mod` (see the `pack_and_install_mod`
               prompt for the details).
            """;
    }

    [McpServerPrompt(Name = "create_archivexl_item")]
    [Description("Recipe: create an ArchiveXL item mod (clothing, weapon) end to end, with " +
                 "validation of the record → factory → localization chain — the #1 cause " +
                 "of an item that does not spawn.")]
    public static string CreateArchiveXlItem(
        [Description("Name of the mod/item to create.")] string modName,
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath)
    {
        return $$"""
            Procedure to create an ArchiveXL item mod "{{modName}}":

            1. Check the prerequisites: `check_requirements` on "{{gamePath}}" —
               ArchiveXL AND TweakXL must be installed.

            2. Generate the skeleton: `scaffold_archivexl` (modName = "{{modName}}").
               → produces the .yaml (TweakXL records), the factory .csv, the localization
               .json and the .xl file that links them.

            3. Edit the generated files: records (entityName, appearanceName,
               displayName), factory (entityName → .ent path), localization
               (secondaryKey → displayed text).

            4. Validate the chain BEFORE packing: `validate_item_mod` on the mod
               folder. This is the step that catches typos between yaml/csv/json —
               a single character off = invisible item with no error message.
               With deep=true, the .ent is also converted to verify the appearanceName.

            5. Pack and install: `package_mod` then `install_mod`
               (gamePath = "{{gamePath}}").

            6. Test in game: launch the game, then if the CETBridge mod is installed,
               `live_add_item` with the record's ID to get the item directly.
               Otherwise `Game.AddToInventory(...)` in the CET console.

            On silent failure: `diagnose_logs` (ArchiveXL/TweakXL log the rejected
            records) then re-run `validate_item_mod`.
            """;
    }

    [McpServerPrompt(Name = "diagnose_broken_mod")]
    [Description("Recipe: diagnose a mod that does not work or a modded install that " +
                 "crashes — from the global diagnostic down to bisection.")]
    public static string DiagnoseBrokenMod(
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath,
        [Description("Observed symptom (crash on launch, missing item, script with no effect…).")]
        string symptom)
    {
        return $$"""
            Diagnostic procedure ({{symptom}}) on "{{gamePath}}":

            1. One-call overview: `mod_doctor` — missing frameworks, unmet
               dependencies, conflicts, inventory. Many cases stop here
               (missing framework = #1 cause of a crash).

            2. Read the game logs: `diagnose_logs` — analyzes the 6 logs (redscript,
               RED4ext, ArchiveXL, TweakXL…) against a known-error database that
               maps each error to its cause and its fix.

            3. If the symptom is "one mod overrides another": `analyze_conflicts`
               — archive overlaps (alphabetical first-wins) and TweakDB records
               defined twice, with resolution leads.

            4. If the mod has dependencies: `analyze_dependencies` on its folder,
               with gamePath to check what is installed (including inter-mod
               dependencies via the REDscript imports).

            5. Scripts: `lint_script` on the mod's .reds (line:column errors);
               tweaks: `lint_tweak` + `validate_tweak`.

            6. Last resort — bisection: `toggle_mods` to disable half the
               archives, relaunch, and narrow down to the culprit. `backup_mods`
               first so you can restore everything (`restore_mods`).
            """;
    }

    [McpServerPrompt(Name = "live_iteration_loop")]
    [Description("Recipe: iteration loop with the game RUNNING (CETBridge mod) — test " +
                 "TweakDB values live before freezing them into a .tweak.")]
    public static string LiveIterationLoop(
        [Description("TweakDB identifier to iterate on (e.g. Items.Preset_Katana_Saburo).")] string tweakId,
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath)
    {
        return $$"""
            Live iteration loop on {{tweakId}} (game running + CETBridge mod):

            1. Check the bridge: `live_status` (gamePath = "{{gamePath}}").
               If not connected: is the game running with CET + CETBridge? (the mod is
               shipped in the bundle, folder live-bridge/CETBridge to copy into
               <game>/bin/x64/plugins/cyber_engine_tweaks/mods/).

            2. Read the current value: `live_tweakdb_get` (flatPath =
               "{{tweakId}}.<field>", e.g. ".damage").

            3. Iterate LIVE without relaunching the game: `live_tweakdb_set` with a
               new value → test immediately in game (spawn the item via
               `live_add_item` if needed). Repeat until the right value.
               ⚠ These changes are volatile: lost at the next launch.

            4. Freeze the result into a mod: `write_tweak` with the chosen values,
               `validate_tweak`, then `install_tweak` (gamePath = "{{gamePath}}").

            5. Check persistence: relaunch the game (TweakXL applies the .tweak)
               then `live_tweakdb_get` again — the value should be the mod's.
            """;
    }

    [McpServerPrompt(Name = "inspect_mesh")]
    [Description("Recipe: export a mesh to glTF to inspect it in Blender / a viewer, " +
                 "with the export options suited to your use case.")]
    public static string InspectMesh(
        [Description("Path of the archive containing the mesh.")] string archivePath,
        [Description("Internal path of the mesh (e.g. base\\characters\\common\\hairs\\...\\h_001.mesh) " +
                     "— locate it if needed with find_in_archives.")] string meshInternalPath)
    {
        return $$"""
            Procedure to inspect a REDengine mesh:

            1. Call `uncook`:
               - archivePath = "{{archivePath}}"
               - outputPath = a working folder
               - pattern = "{{meshInternalPath}}"
               - meshExportType = "WithRig" if you want the skeleton, otherwise leave empty
                 (defaults to WithMaterials, which includes the materials)
               - meshExporterType = "REDmod" if you target a REDmod-certified reimport
               - meshExportLodFilter = true to ignore the LODs (reduces noise)
               → produces a .glb (binary glTF) openable in Blender / a 3D viewer.

            2. If you also want the mesh's CR2W JSON (raw structure of the sub-meshes,
               materials, etc.): call `read_game_file` on the same file.
            """;
    }
}
