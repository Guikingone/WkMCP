using System.ComponentModel;
using ModelContextProtocol.Server;

namespace WolvenKitMcp;

/// <summary>
/// Ressources MCP pour WolvenKit — données lisibles exposées par URI, en
/// complément des outils. Une ressource statique de référence et deux
/// resource templates donnant accès au contenu du jeu par chemin.
/// </summary>
[McpServerResourceType]
public static class WolvenKitResources
{
    [McpServerResource(UriTemplate = "wolvenkit://reference",
                       Name = "Référence WolvenKit",
                       MimeType = "text/markdown")]
    [Description("Aide-mémoire : commandes cp77tools, formats de fichiers REDengine et " +
                 "workflow de modding de Cyberpunk 2077.")]
    public static string Reference() => ReferenceText;

    [McpServerResource(UriTemplate = "wolvenkit://archive/{+path}",
                       Name = "Contenu d'archive",
                       MimeType = "text/plain")]
    [Description("Liste le contenu d'une archive .archive de Cyberpunk 2077, identifiée " +
                 "par son chemin absolu après wolvenkit://archive/.")]
    public static async Task<string> ArchiveContents(string path)
    {
        if (!File.Exists(path))
            return $"Archive introuvable : {path}";

        var r = await Cp77ToolsRunner.Shared.RunAsync(
            new[] { "archive", path, "--list" }, CancellationToken.None);
        var output = (r.Stdout + r.Stderr).Trim();
        return output.Length > 0 ? output : "(aucune sortie)";
    }

    [McpServerResource(UriTemplate = "wolvenkit://cr2w-json/{+path}",
                       Name = "Fichier REDengine en JSON",
                       MimeType = "application/json")]
    [Description("Rend un fichier REDengine CR2W déjà extrait (.mesh, .ent, .app...) sous " +
                 "forme JSON, identifié par son chemin absolu après wolvenkit://cr2w-json/.")]
    public static async Task<string> Cr2wJson(string path)
    {
        if (!File.Exists(path))
            return $"Fichier introuvable : {path}";

        var tmp = Path.Combine(Path.GetTempPath(), "wkmcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var r = await Cp77ToolsRunner.Shared.RunAsync(
                new[] { "convert", "serialize", path, "--outpath", tmp }, CancellationToken.None);

            var json = Directory
                .EnumerateFiles(tmp, "*.json", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (json is null)
                return "Conversion CR2W → JSON échouée :\n" + (r.Stdout + r.Stderr).Trim();

            return await File.ReadAllTextAsync(json);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* nettoyage best-effort */ }
        }
    }

    // ──────────────────────────────────────────────────────────────────────

    private const string ReferenceText = """
        # WolvenKit / Cyberpunk 2077 — aide-mémoire

        ## Outils MCP disponibles (53 au total)
        - archive_info / find_in_archives : inspecter et chercher dans les .archive (cache LRU)
        - diff_archives : comparer deux .archive (ajouts, suppressions)
        - read_game_file / write_game_file : lire/éditer un fichier de jeu (JSON) en un appel
        - extract_files / uncook : extraire (mesh -> glTF, textures -> image)
          Flags mesh : meshExportType, meshExporterType, meshExportLodFilter.
        - cr2w_to_json / json_to_cr2w : conversion REDengine <-> JSON
        - export_files / import_raw : conversion fichiers extraits <-> raw
        - inspect_mesh / inspect_texture : résumés rapides sans conversion lourde
        - pack_archive : empaqueter un dossier en .archive
        - lint_mod / install_mod : valider extensions/conflits + installer
        - build_project : compiler un projet .cpmodproj
        - detect_conflicts : conflits entre mods installés
        - create_mod_project / list_installed_mods : workflow mod
        - REDmod : create_redmod_project / pack_redmod / install_redmod
        - TweakDB : tweakdb_query / tweakdb_resolve / describe_tweak_record
          + read_tweak / write_tweak / validate_tweak / install_tweak
          + generate_tweak_template (patterns : override_field, new_record, boost_stat)
        - Scripts : read_script / lint_script (.reds, .script, .swift)
        - Sécurité : backup_mods / restore_mods (snapshot ZIP horodaté)
        - Uninstall : uninstall_mod / uninstall_redmod / uninstall_tweak
        - REDmod deploy : deploy_redmod (wrap redMod.exe deploy)
        - In-game : launch_game / tail_game_logs (Cyberpunk2077.exe + r6/logs)
        - Intelligence : mod_summary (archive ou REDmod) / dump_records (CSV/JSONL par type)
        - Scaffolds REDscript : generate_redscript_template (add_method, wrap_method, ...)
        - Localisation : extract_localization (TweakDB UI strings) / build_localization (.tweak)
        - clear_cache : vide archives / metrics / all
        - compute_hash / resolve_hash : hash FNV1a64 d'un chemin <-> chemin
        - wwise_export / oodle_compress / oodle_decompress : audio + Kraken
        - wolvenkit_status : version + stats du cache LRU + métriques par verbe (p50/p95)

        Chaque outil renvoie un résultat JSON structuré : { ok, status, summary,
        produced, warnings, errors, log }. Le log volumineux est tronqué en
        préservant tête + erreurs + queue. La plupart des outils acceptent
        un paramètre `verbose=true` pour récupérer le log complet (debug).

        ## Prompts MCP (recettes)
        - read_game_file_workflow : trouver puis lire un fichier de jeu
        - edit_tweakdb_item : modifier un item TweakDB via .tweak
        - pack_and_install_mod : empaqueter et installer un mod
        - recolor_texture : extraire / éditer / réimporter une texture
        - inspect_mesh : exporter un mesh en glTF pour inspection

        ## Formats REDengine courants
        - .archive : conteneur d'assets (compression Kraken)
        - .mesh : modèle 3D            - .xbm : texture
        - .ent / .app : entités et apparences
        - .json : représentation éditable d'un fichier CR2W

        ## Workflow de modding (CLI)
        1. find_in_archives pour localiser le fichier voulu
        2. read_game_file pour le lire en JSON (ou extract_files / uncook)
        3. éditer le JSON, puis write_game_file (ou import_raw)
        4. pack_archive sur le dossier source
        5. install_mod pour déposer l'archive dans <jeu>/archive/pc/mod/

        ## Dossiers d'une installation Cyberpunk 2077
        - archive/pc/content/ : assets de base du jeu
        - archive/pc/mod/     : mods .archive installés
        - mods/               : mods REDmod
        """;
}
