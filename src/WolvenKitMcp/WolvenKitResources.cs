using System.ComponentModel;
using System.Reflection;
using System.Text;
using ModelContextProtocol.Server;

namespace WolvenKitMcp;

/// <summary>
/// Ressources MCP pour WolvenKit — données lisibles exposées par URI, en
/// complément des outils. Une ressource de référence (générée par réflexion
/// depuis les outils réellement enregistrés, donc jamais périmée) et deux
/// resource templates donnant accès au contenu du jeu par chemin.
/// </summary>
[McpServerResourceType]
public static class WolvenKitResources
{
    [McpServerResource(UriTemplate = "wolvenkit://reference",
                       Name = "Référence WolvenKit",
                       MimeType = "text/markdown")]
    [Description("Aide-mémoire : outils MCP disponibles (liste générée depuis le code), " +
                 "formats de fichiers REDengine et workflow de modding de Cyberpunk 2077.")]
    public static string Reference() => _reference ??= BuildReference();

    private static string? _reference;

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

    [McpServerResource(UriTemplate = "wolvenkit://mods/{+gamePath}",
                       Name = "Mods installés",
                       MimeType = "text/markdown")]
    [Description("Inventaire des mods installés d'une installation Cyberpunk 2077, identifiée " +
                 "par sa racine (chemin absolu) après wolvenkit://mods/ : archives .archive, " +
                 "REDmods, fichiers .tweak (TweakXL) et scripts REDscript. Contexte gratuit " +
                 "avant un diagnostic (mod_doctor, analyze_conflicts).")]
    public static string InstalledMods(string gamePath)
    {
        if (!Directory.Exists(gamePath))
            return $"Dossier de jeu introuvable : {gamePath}";

        var sb = new StringBuilder();
        sb.AppendLine($"# Mods installés — {gamePath}");

        var legacy = Path.Combine(gamePath, "archive", "pc", "mod");
        var archives = Directory.Exists(legacy)
            ? Directory.GetFiles(legacy, "*.archive").Select(Path.GetFileName).OrderBy(x => x).ToList()
            : new List<string?>();
        sb.AppendLine();
        sb.AppendLine($"## Archives (archive/pc/mod) — {archives.Count}");
        foreach (var a in archives) sb.AppendLine($"- {a}");

        var modsDir = Path.Combine(gamePath, "mods");
        var redmods = Directory.Exists(modsDir)
            ? Directory.GetDirectories(modsDir).Select(Path.GetFileName).OrderBy(x => x).ToList()
            : new List<string?>();
        sb.AppendLine();
        sb.AppendLine($"## REDmods (mods/) — {redmods.Count}");
        foreach (var m in redmods) sb.AppendLine($"- {m}");

        var tweaksDir = Path.Combine(gamePath, "r6", "tweaks");
        var tweaks = Directory.Exists(tweaksDir)
            ? Directory.EnumerateFiles(tweaksDir, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".tweak" or ".yaml" or ".yml")
                .Select(f => Path.GetRelativePath(tweaksDir, f)).OrderBy(x => x).ToList()
            : new List<string>();
        sb.AppendLine();
        sb.AppendLine($"## Tweaks (r6/tweaks) — {tweaks.Count}");
        foreach (var t in tweaks) sb.AppendLine($"- {t}");

        var scriptsDir = Path.Combine(gamePath, "r6", "scripts");
        var scriptMods = Directory.Exists(scriptsDir)
            ? Directory.GetDirectories(scriptsDir).Select(Path.GetFileName).OrderBy(x => x).ToList()
            : new List<string?>();
        sb.AppendLine();
        sb.AppendLine($"## Scripts REDscript (r6/scripts) — {scriptMods.Count} dossier(s)");
        foreach (var s in scriptMods) sb.AppendLine($"- {s}");

        sb.AppendLine();
        sb.AppendLine("Pour aller plus loin : mod_doctor (santé globale), analyze_conflicts " +
                      "(recouvrements), mod_summary (détail d'une archive).");
        return sb.ToString();
    }

    // ──────────────────────────────────────────────────────────────────────
    // Génération de la référence par réflexion sur les classes d'outils :
    // les comptes et les noms proviennent du code réel, pas d'un texte
    // entretenu à la main.

    internal static IReadOnlyList<string> ToolNames(Type toolType) =>
        toolType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    internal static IReadOnlyList<(string Name, string Description)> PromptInfos() =>
        typeof(WolvenKitPrompts).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (Attr: m.GetCustomAttribute<McpServerPromptAttribute>(),
                          Desc: m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? ""))
            .Where(t => t.Attr?.Name is not null)
            .Select(t => (t.Attr!.Name!, FirstSentence(t.Desc)))
            .OrderBy(t => t.Item1, StringComparer.Ordinal)
            .ToList();

    internal static IReadOnlyList<(string Name, string UriTemplate)> ResourceInfos() =>
        typeof(WolvenKitResources).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<McpServerResourceAttribute>())
            .Where(a => a?.UriTemplate is not null)
            .Select(a => (a!.Name ?? a.UriTemplate!, a.UriTemplate!))
            .OrderBy(t => t.Item2, StringComparer.Ordinal)
            .ToList();

    private static string FirstSentence(string text)
    {
        var idx = text.IndexOf(". ", StringComparison.Ordinal);
        return idx > 0 ? text[..(idx + 1)] : text;
    }

    internal static string BuildReference()
    {
        var offline = ToolNames(typeof(WolvenKitTools));
        var modding = ToolNames(typeof(ModdingTools));
        var live = ToolNames(typeof(LiveTools));
        var prompts = PromptInfos();
        var total = offline.Count + modding.Count + live.Count;

        var sb = new StringBuilder();
        sb.AppendLine("# WolvenKit / Cyberpunk 2077 — aide-mémoire");
        sb.AppendLine();
        sb.AppendLine($"## Outils MCP disponibles ({total} au total)");
        sb.AppendLine();
        sb.AppendLine("### Outils clés par tâche");
        sb.AppendLine("- Explorer : find_in_archives / archive_info (cache LRU), find_in_cr2w / inspect_cr2w (tout fichier CR2W), inspect_journal / find_journal_entry (.journal)");
        sb.AppendLine("- Lire/éditer un fichier de jeu : read_game_file / write_game_file (JSON en un appel), cr2w_to_json / json_to_cr2w");
        sb.AppendLine("- Extraire : extract_files / uncook (mesh -> glTF, textures -> image ; flags meshExportType, meshExporterType, meshExportLodFilter), extract_audio (voix opus)");
        sb.AppendLine("- Packager/installer : pack_archive, install_mod, build_project, package_mod, scaffold_mod / scaffold_archivexl");
        sb.AppendLine("- TweakDB : tweakdb_query / tweakdb_resolve / describe_tweak_record / dump_records + read_tweak / write_tweak / validate_tweak / install_tweak / lint_tweak");
        sb.AppendLine("- Scripts REDscript : read_script / lint_script (validation syntaxique ligne:colonne), generate_redscript_template");
        sb.AppendLine("- Diagnostiquer : mod_doctor, diagnose_logs (6 logs + base d'erreurs connues), analyze_conflicts, analyze_dependencies / check_requirements, migration_check");
        sb.AppendLine("- Valider : validate_xl, validate_item_mod (chaîne ArchiveXL complète), validate_appearance (.app -> .mesh), lint_mod");
        sb.AppendLine("- Apparences : list_entity_appearances, resolve_dynamic_appearance ; export_entity reste expérimental (refus headless sur les NPC)");
        sb.AppendLine("- Sécurité : backup_mods / restore_mods, toggle_mods (bissection), uninstall_mod / uninstall_redmod / uninstall_tweak");
        sb.AppendLine("- In-game (offline) : launch_game, tail_game_logs");
        sb.AppendLine("- Live (jeu lancé + mod CETBridge) : live_status d'abord, puis les outils live_* (état joueur, inventaire, téléport, TweakDB à chaud, quest facts, observation d'événements)");
        sb.AppendLine();
        sb.AppendLine($"### Liste complète — outils archives/CR2W/TweakDB/projets ({offline.Count})");
        sb.AppendLine(string.Join(", ", offline));
        sb.AppendLine();
        sb.AppendLine($"### Liste complète — outils workflow modding ({modding.Count})");
        sb.AppendLine(string.Join(", ", modding));
        sb.AppendLine();
        sb.AppendLine($"### Liste complète — outils live in-game ({live.Count})");
        sb.AppendLine(string.Join(", ", live));
        sb.AppendLine();
        sb.AppendLine("Chaque outil renvoie un résultat JSON structuré : { ok, status, summary,");
        sb.AppendLine("produced, warnings, errors, log }. Le log volumineux est tronqué en");
        sb.AppendLine("préservant tête + erreurs + queue. La plupart des outils acceptent");
        sb.AppendLine("un paramètre `verbose=true` pour récupérer le log complet (debug).");
        sb.AppendLine();
        sb.AppendLine($"## Prompts MCP ({prompts.Count} recettes)");
        foreach (var (name, desc) in prompts)
            sb.AppendLine($"- {name} : {desc}");
        sb.AppendLine();
        sb.AppendLine("## Formats REDengine courants");
        sb.AppendLine("- .archive : conteneur d'assets (compression Kraken)");
        sb.AppendLine("- .mesh : modèle 3D            - .xbm : texture");
        sb.AppendLine("- .ent / .app : entités et apparences");
        sb.AppendLine("- .tweak : surcharge TweakDB (TweakXL, YAML)   - .reds : script REDscript");
        sb.AppendLine("- .json : représentation éditable d'un fichier CR2W");
        sb.AppendLine();
        sb.AppendLine("## Workflow de modding (asset replacement)");
        sb.AppendLine("1. find_in_archives pour localiser le fichier voulu");
        sb.AppendLine("2. read_game_file pour le lire en JSON (ou extract_files / uncook)");
        sb.AppendLine("3. éditer le JSON, puis write_game_file (ou import_raw)");
        sb.AppendLine("4. pack_archive sur le dossier source");
        sb.AppendLine("5. install_mod pour déposer l'archive dans <jeu>/archive/pc/mod/");
        sb.AppendLine();
        sb.AppendLine("## Dossiers d'une installation Cyberpunk 2077");
        sb.AppendLine("- archive/pc/content/ : assets de base du jeu");
        sb.AppendLine("- archive/pc/mod/     : mods .archive installés");
        sb.AppendLine("- mods/               : mods REDmod");
        sb.AppendLine("- r6/tweaks/          : fichiers .tweak (TweakXL)");
        sb.AppendLine("- r6/scripts/         : scripts .reds (redscript)");
        sb.AppendLine("- r6/logs/            : logs (diagnose_logs / tail_game_logs)");
        return sb.ToString();
    }
}
