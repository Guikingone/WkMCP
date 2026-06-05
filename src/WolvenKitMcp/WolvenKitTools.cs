using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using ModelContextProtocol.Server;

namespace WolvenKitMcp;

/// <summary>
/// Outils MCP pour le modding de Cyberpunk 2077 — inspection, conversion et
/// création de mods. La plupart délèguent à une commande de <c>cp77tools</c>
/// (le CLI de WolvenKit) ; quelques-uns opèrent directement sur le système de
/// fichiers.
///
/// Chaque outil renvoie un objet JSON structuré (cf. <see cref="Structured"/>) :
/// <c>{ ok, status, summary, produced, warnings, errors, exitCode, log }</c> —
/// bien plus fiable à analyser pour un agent que le texte de log brut. Le succès
/// est déterminé d'après les fichiers réellement produits, pas d'après un simple
/// marqueur de log (qu'une erreur non fatale, p. ex. l'export de matériaux d'un
/// mesh, pouvait fausser).
/// </summary>
[McpServerToolType]
public static class WolvenKitTools
{
    // ── Diagnostic ────────────────────────────────────────────────────────

    [McpServerTool(Name = "wolvenkit_status")]
    [Description("Vérifie que le CLI WolvenKit (cp77tools) est disponible et fonctionnel " +
                 "sur cette machine, et renvoie sa version + stats du cache LRU des listings " +
                 "d'archives (hits/misses depuis le démarrage du serveur). À appeler en premier " +
                 "pour diagnostiquer l'installation.")]
    public static async Task<string> Status(Cp77ToolsRunner runner, CancellationToken ct)
    {
        if (!runner.ToolExists)
            return Err($"cp77tools introuvable : {runner.ToolPath}. " +
                       "Installer avec : dotnet tool install -g WolvenKit.CLI");

        var r = await runner.RunAsync(new[] { "--version" }, ct);
        var (hits, misses, entries) = runner.CacheStats;
        var total = hits + misses;
        var hitRate = total > 0 ? Math.Round((double)hits / total, 3) : 0.0;
        var metricsTop = runner.MetricsSnapshot().Take(10)
            .Select(m => new { verb = m.verb, calls = m.calls, totalMs = m.totalMs,
                               p50 = m.p50, p95 = m.p95 })
            .ToList();
        var log = (r.Stdout + r.Stderr).Trim();

        return JsonSerializer.Serialize(new
        {
            ok = r.ExitCode == 0,
            status = r.ExitCode == 0 ? "success" : "error",
            summary = r.ExitCode == 0
                ? $"cp77tools opérationnel — {runner.ToolPath}"
                : $"cp77tools présent mais --version a échoué (exit={r.ExitCode}) — {runner.ToolPath}",
            produced = Array.Empty<string>(),
            warnings = LogLines(log, "Warning"),
            errors = LogLines(log, "Error"),
            toolPath = runner.ToolPath,
            cache = new { hits, misses, entries, hitRate },
            metrics = metricsTop,
            exitCode = r.ExitCode,
            log = Truncate(log, 12_000),
        }, JsonOpts);
    }

    [McpServerTool(Name = "extract_localization")]
    [Description("Extrait depuis une tweakdb.bin tous les champs « traduisibles » des records " +
                 "(displayName, localizedDescription, description, etc.) — base pour faire un " +
                 "mod de traduction UI. Sortie : JSON `{recordId: {field: value}}`. Le filtre " +
                 "optionnel (substring sur le recordId) permet de cibler une partie du jeu " +
                 "(ex. `Items.` pour n'extraire que les items). Limitation : ne couvre que " +
                 "les strings UI dans TweakDB ; les sous-titres audio (.opusinfo) restent en " +
                 "roadmap.")]
    public static async Task<string> ExtractLocalization(
        Cp77ToolsRunner runner,
        [Description("Chemin du fichier tweakdb.bin (typiquement <jeu>/r6/cache/tweakdb.bin).")] string tweakdbPath,
        [Description("Chemin du fichier JSON de sortie (sera créé/écrasé).")] string outputJson,
        [Description("Filtre optionnel : sous-chaîne à chercher dans les recordId (ex. \"Items.\").")] string? filter = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(tweakdbPath))
            return Err($"Fichier tweakdb.bin introuvable : {tweakdbPath}");

        Directory.CreateDirectory(Path.GetDirectoryName(outputJson) ?? ".");
        var args = new List<string>
        {
            "tweakdb-extract-localization", tweakdbPath, outputJson,
        };
        if (!string.IsNullOrWhiteSpace(filter)) args.Add(filter);
        var r = await runner.RunAsync(args, ct);
        var produced = File.Exists(outputJson)
            ? new List<string> { outputJson }
            : new List<string>();
        return Structured(
            $"Extraction localisation TweakDB → {outputJson}" +
            (string.IsNullOrWhiteSpace(filter) ? "" : $" (filtre : {filter})"),
            r, produced);
    }

    [McpServerTool(Name = "build_localization")]
    [Description("Construit un fichier .tweak (TweakXL) qui surcharge displayName / " +
                 "localizedDescription / etc. depuis un fichier JSON de traductions au format " +
                 "`{recordId: {field: \"Traduction\"}}` produit par extract_localization puis édité. " +
                 "Le .tweak peut ensuite être installé via install_tweak. Le paramètre `lang` est " +
                 "purement informatif (ajouté en commentaire ; le jeu n'a pas de localisation " +
                 "par langue dans TweakDB).")]
    public static async Task<string> BuildLocalization(
        [Description("Chemin du fichier JSON des traductions (issu d'extract_localization, édité).")] string translationsJson,
        [Description("Chemin du fichier .tweak à produire.")] string outputTweak,
        [Description("Code langue (informatif, ajouté en commentaire). Ex. fr-fr, de-de.")] string lang = "fr-fr",
        CancellationToken ct = default)
    {
        if (!File.Exists(translationsJson))
            return Err($"Fichier de traductions introuvable : {translationsJson}");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(await File.ReadAllTextAsync(translationsJson, ct));
        }
        catch (JsonException ex)
        {
            return Err($"JSON invalide : {ex.Message}");
        }
        using var _ = doc;
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return Err("translationsJson doit avoir un objet en racine ({recordId: {field: value}}).");

        var sb = new StringBuilder();
        sb.Append("# Mod de localisation — lang ").AppendLine(lang);
        sb.AppendLine("# Généré par build_localization (WolvenKit MCP)");
        sb.AppendLine();

        int recordCount = 0, fieldCount = 0;
        foreach (var rec in doc.RootElement.EnumerateObject())
        {
            if (rec.Value.ValueKind != JsonValueKind.Object) continue;
            sb.Append(rec.Name).AppendLine(":");
            foreach (var field in rec.Value.EnumerateObject())
            {
                if (field.Value.ValueKind != JsonValueKind.String) continue;
                var value = field.Value.GetString() ?? "";
                sb.Append("  ").Append(field.Name).Append(": ")
                  .AppendLine(FormatYamlValue(value));
                fieldCount++;
            }
            sb.AppendLine();
            recordCount++;
        }

        if (recordCount == 0)
            return Err("Aucun record traduisible trouvé dans le JSON (chaque entrée doit être un objet).");

        Directory.CreateDirectory(Path.GetDirectoryName(outputTweak) ?? ".");
        await File.WriteAllTextAsync(outputTweak, sb.ToString(), ct);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Mod de localisation construit : {outputTweak} " +
                      $"({recordCount} record(s), {fieldCount} champ(s) traduit(s), lang {lang})",
            produced = new[] { outputTweak },
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            outputTweak,
            recordCount,
            fieldCount,
            lang,
        }, JsonOpts);
    }

    [McpServerTool(Name = "clear_cache")]
    [Description("Vide manuellement les caches du serveur. scope = `archives` (défaut, vide " +
                 "le cache LRU des listings d'archives), `metrics` (vide les compteurs de " +
                 "métriques par verbe), ou `all` (les deux). Utile après des modifs " +
                 "hors-bande qui invalideraient le cache, ou pour reset les stats avant " +
                 "un benchmark.")]
    public static string ClearCache(
        Cp77ToolsRunner runner,
        [Description("Portée à vider : archives | metrics | all.")] string scope = "archives")
    {
        scope = scope.ToLowerInvariant();
        if (scope is not ("archives" or "metrics" or "all"))
            return Err($"Scope inconnu : {scope} (archives | metrics | all).");

        var cleared = new Dictionary<string, int>();
        if (scope is "archives" or "all")
        {
            var (_, _, entriesBefore) = runner.CacheStats;
            runner.InvalidateArchiveCache();
            cleared["archives"] = entriesBefore;
        }
        if (scope is "metrics" or "all")
        {
            var metricsBefore = runner.MetricsSnapshot().Count;
            runner.ResetMetrics();
            cleared["metrics"] = metricsBefore;
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Cache vidé (scope {scope}) : " +
                      string.Join(", ", cleared.Select(kv => $"{kv.Value} {kv.Key}")),
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            scope,
            cleared,
        }, JsonOpts);
    }

    [McpServerTool(Name = "compute_hash")]
    [Description("Calcule le hash FNV1a64 utilisé par REDengine pour chaque chaîne fournie " +
                 "(typiquement des chemins de fichiers de jeu).")]
    public static async Task<string> ComputeHash(
        Cp77ToolsRunner runner,
        [Description("Une ou plusieurs chaînes à hacher.")] string[] inputs,
        CancellationToken ct)
    {
        if (inputs is null || inputs.Length == 0)
            return Err("Fournir au moins une chaîne à hacher.");

        var args = new List<string> { "hash" };
        args.AddRange(inputs);
        var r = await runner.RunAsync(args, ct);
        return Structured($"Hash FNV1a64 de {inputs.Length} entrée(s)", r);
    }

    [McpServerTool(Name = "resolve_hash")]
    [Description("Recherche inverse : retrouve le chemin de fichier de jeu correspondant " +
                 "à un hash FNV1a64. L'inverse de compute_hash.")]
    public static async Task<string> ResolveHash(
        Cp77ToolsRunner runner,
        [Description("Un ou plusieurs hash FNV1a64 (entiers non signés).")] string[] hashes,
        CancellationToken ct)
    {
        if (hashes is null || hashes.Length == 0)
            return Err("Fournir au moins un hash.");

        var args = new List<string> { "resolve-hash" };
        args.AddRange(hashes);
        var r = await runner.RunAsync(args, ct);
        return Structured($"Recherche inverse de {hashes.Length} hash", r);
    }

    [McpServerTool(Name = "tweakdb_resolve")]
    [Description("Recherche inverse d'identifiants TweakDB : un hash → le nom de " +
                 "l'identifiant. Utilise la base de noms TweakDB chargée au démarrage.")]
    public static async Task<string> TweakDbResolve(
        Cp77ToolsRunner runner,
        [Description("Un ou plusieurs hash d'identifiant TweakDB (entiers non signés).")] string[] hashes,
        CancellationToken ct)
    {
        if (hashes is null || hashes.Length == 0)
            return Err("Fournir au moins un hash.");

        var args = new List<string> { "tweakdb-resolve" };
        args.AddRange(hashes);
        var r = await runner.RunAsync(args, ct);
        return Structured($"Résolution de {hashes.Length} identifiant(s) TweakDB", r);
    }

    [McpServerTool(Name = "tweakdb_query")]
    [Description("Interroge la TweakDB de Cyberpunk 2077 : charge un fichier tweakdb.bin et " +
                 "liste les records et flats dont l'identifiant contient le filtre — pour " +
                 "découvrir les identifiants de tuning du jeu. Les résultats sont plafonnés " +
                 "à 100 records + 100 flats ; affiner le filtre si le champ truncated le dit.")]
    public static async Task<string> TweakDbQuery(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier tweakdb.bin (extrait des archives du jeu).")] string tweakdbPath,
        [Description("Sous-chaîne à chercher dans les identifiants (vide = tout, 100 max).")] string filter = "",
        CancellationToken ct = default)
    {
        if (!File.Exists(tweakdbPath))
            return Err($"Fichier TweakDB introuvable : {tweakdbPath}");

        var r = await runner.RunAsync(new[] { "tweakdb-query", tweakdbPath, filter }, ct);
        var log = (r.Stdout + r.Stderr).Trim();
        var (recordsTruncated, flatsTruncated) = ParseTweakHeader(log);
        var truncated = recordsTruncated || flatsTruncated;
        var status = r.ExitCode != 0
                     || log.Contains("Erreur daemon", StringComparison.Ordinal)
                     || log.Contains("Unhandled", StringComparison.OrdinalIgnoreCase)
                ? "error" : "success";

        return JsonSerializer.Serialize(new
        {
            ok = status == "success",
            status,
            summary = $"TweakDB — recherche « {filter} » dans {tweakdbPath}" +
                      (truncated ? " (résultats tronqués — affiner le filtre)" : ""),
            produced = Array.Empty<string>(),
            warnings = LogLines(log, "Warning"),
            errors = LogLines(log, "Error"),
            truncated = new { records = recordsTruncated, flats = flatsTruncated },
            exitCode = r.ExitCode,
            log = Truncate(log, 12_000),
        }, JsonOpts);
    }

    // ── Lecture / inspection ──────────────────────────────────────────────

    [McpServerTool(Name = "archive_info")]
    [Description("Affiche les informations d'une archive .archive de Cyberpunk 2077 : " +
                 "nombre de fichiers, et liste optionnelle filtrée de son contenu. " +
                 "Listing servi par un cache LRU (clé = chemin + mtime) : appels successifs " +
                 "quasi instantanés.")]
    public static async Task<string> ArchiveInfo(
        Cp77ToolsRunner runner,
        [Description("Chemin absolu du fichier .archive.")] string archivePath,
        [Description("Lister le contenu de l'archive (sinon résumé seulement).")] bool list = false,
        [Description("Filtre glob optionnel sur les noms, ex. *.mesh")] string? pattern = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            return Err($"Archive introuvable : {archivePath}");

        if (!list)
        {
            var args = new List<string> { "archive", archivePath };
            if (!string.IsNullOrWhiteSpace(pattern)) { args.Add("--pattern"); args.Add(pattern); }
            var r = await runner.RunAsync(args, ct);
            return Structured($"Archive : {archivePath}", r);
        }

        // Mode listing : passe par le cache mtime du runner.
        var (entries, fromCache, raw) = await runner.GetArchiveListingAsync(archivePath, ct);
        var filtered = string.IsNullOrWhiteSpace(pattern)
            ? entries.ToList()
            : entries.Where(e => MatchesGlob(e, pattern)).ToList();

        var log = $"{entries.Count} fichier(s) dans {Path.GetFileName(archivePath)}" +
                  (fromCache ? " (depuis le cache)" : "") +
                  (filtered.Count != entries.Count ? $" — {filtered.Count} après filtre" : "") +
                  "\n" + string.Join("\n", filtered);

        return JsonSerializer.Serialize(new
        {
            ok = entries.Count > 0,
            status = entries.Count > 0 ? "success" : "error",
            summary = $"Archive : {archivePath} — {entries.Count} fichier(s)" +
                      (filtered.Count != entries.Count ? $", {filtered.Count} après filtre" : "") +
                      (fromCache ? " (cache)" : ""),
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = entries.Count > 0 ? Array.Empty<string>()
                                       : new[] { $"Listing vide pour {archivePath}." },
            archivePath,
            fileCount = entries.Count,
            filteredCount = filtered.Count,
            fromCache,
            files = filtered,
            exitCode = raw.ExitCode,
            log = Truncate(log, 12_000),
        }, JsonOpts);
    }

    [McpServerTool(Name = "find_in_archives")]
    [Description("Recherche des fichiers à travers toutes les archives .archive d'un dossier " +
                 "(typiquement le dossier de contenu du jeu). Indique dans quelle archive se " +
                 "trouve chaque fichier correspondant. Listings servis par cache LRU : " +
                 "les appels suivants sur le même dossier sont quasi instantanés.")]
    public static async Task<string> FindInArchives(
        Cp77ToolsRunner runner,
        [Description("Dossier contenant des archives .archive, ex. <jeu>/archive/pc/content.")] string archivesFolder,
        [Description("Motif glob à rechercher, ex. *player*.ent")] string? pattern = null,
        [Description("Expression régulière à rechercher (alternative au glob).")] string? regex = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(archivesFolder))
            return Err($"Dossier introuvable : {archivesFolder}");
        if (string.IsNullOrWhiteSpace(pattern) && string.IsNullOrWhiteSpace(regex))
            return Err("Fournir un motif glob (pattern) ou une expression régulière (regex).");

        Regex? rx = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(regex))
                rx = new Regex(regex, RegexOptions.IgnoreCase);
        }
        catch (ArgumentException ex)
        {
            return Err($"Expression régulière invalide : {ex.Message}");
        }

        var archives = Directory.GetFiles(archivesFolder, "*.archive");
        var matches = new List<string>();
        var cacheHits = 0;
        var errors = new List<string>();

        foreach (var archive in archives)
        {
            ct.ThrowIfCancellationRequested();
            var (entries, fromCache, raw) = await runner.GetArchiveListingAsync(archive, ct);
            if (fromCache) cacheHits++;
            if (entries.Count == 0 && raw.ExitCode != 0)
            {
                errors.Add($"{Path.GetFileName(archive)} : listing vide (exit={raw.ExitCode})");
                continue;
            }
            foreach (var e in entries)
            {
                var match = rx is not null ? rx.IsMatch(e) : MatchesGlob(e, pattern!);
                if (match)
                    matches.Add($"{e}  ({Path.GetFileName(archive)})");
            }
        }

        var log = $"{matches.Count} correspondance(s) dans {archives.Length} archive(s) " +
                  $"(cache : {cacheHits}/{archives.Length})\n" +
                  string.Join("\n", matches);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Recherche dans {archivesFolder} : {matches.Count} match(s) " +
                      $"sur {archives.Length} archive(s) (cache : {cacheHits})",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors,
            archivesScanned = archives.Length,
            cacheHits,
            matchCount = matches.Count,
            matches,
            exitCode = 0,
            log = Truncate(log, 12_000),
        }, JsonOpts);
    }

    [McpServerTool(Name = "diff_archives")]
    [Description("Compare deux archives .archive et liste les fichiers ajoutés (présents dans B " +
                 "seulement) et supprimés (présents dans A seulement) — utile pour diagnostiquer " +
                 "exactement ce qu'un mod modifie par rapport au jeu de base. Le verbe " +
                 "`archive --diff` de cp77tools ne fait que dumper un manifest ; ici on calcule " +
                 "un vrai diff en croisant les deux listings.")]
    public static async Task<string> DiffArchives(
        Cp77ToolsRunner runner,
        [Description("Première archive (référence, ex. version de base du jeu).")] string archiveA,
        [Description("Deuxième archive (à comparer, ex. version moddée).")] string archiveB,
        CancellationToken ct = default)
    {
        if (!File.Exists(archiveA))
            return Err($"Archive A introuvable : {archiveA}");
        if (!File.Exists(archiveB))
            return Err($"Archive B introuvable : {archiveB}");

        var (entriesA, cacheA, _) = await runner.GetArchiveListingAsync(archiveA, ct);
        var (entriesB, cacheB, _) = await runner.GetArchiveListingAsync(archiveB, ct);

        var setA = new HashSet<string>(entriesA, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(entriesB, StringComparer.OrdinalIgnoreCase);
        var added = setB.Except(setA, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = setA.Except(setB, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var common = setA.Intersect(setB, StringComparer.OrdinalIgnoreCase).Count();

        var ok = setA.Count > 0 || setB.Count > 0;
        var log = $"A : {Path.GetFileName(archiveA)} ({setA.Count} fichier(s){(cacheA ? ", cache" : "")})\n" +
                  $"B : {Path.GetFileName(archiveB)} ({setB.Count} fichier(s){(cacheB ? ", cache" : "")})\n" +
                  $"Communs : {common} · Ajoutés en B : {added.Count} · Supprimés en B : {removed.Count}";

        return JsonSerializer.Serialize(new
        {
            ok,
            status = ok ? "success" : "error",
            summary = $"Diff archives : {Path.GetFileName(archiveA)} ↔ " +
                      $"{Path.GetFileName(archiveB)} (+{added.Count} / -{removed.Count})",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = ok ? Array.Empty<string>() : new[] { "Listings vides — vérifier les archives." },
            archiveA = new { path = archiveA, count = setA.Count },
            archiveB = new { path = archiveB, count = setB.Count },
            commonCount = common,
            added,
            removed,
            log = Truncate(log, 12_000),
        }, JsonOpts);
    }

    [McpServerTool(Name = "extract_files")]
    [Description("Extrait des fichiers d'une archive .archive vers un dossier. " +
                 "Filtrage optionnel par motif glob ou par expression régulière.")]
    public static async Task<string> ExtractFiles(
        Cp77ToolsRunner runner,
        [Description("Chemin absolu du fichier .archive.")] string archivePath,
        [Description("Dossier de destination des fichiers extraits.")] string outputPath,
        [Description("Filtre glob optionnel, ex. *.mesh")] string? pattern = null,
        [Description("Filtre regex optionnel (alternative au glob).")] string? regex = null,
        [Description("Si vrai, renvoie le log complet (pas de troncature) — pour debug.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            return Err($"Archive introuvable : {archivePath}");

        var args = new List<string> { "unbundle", archivePath, "--outpath", outputPath };
        if (!string.IsNullOrWhiteSpace(pattern)) { args.Add("--pattern"); args.Add(pattern); }
        if (!string.IsNullOrWhiteSpace(regex)) { args.Add("--regex"); args.Add(regex); }

        return await WithSnapshot(outputPath,
            $"Extraction de {archivePath} → {outputPath}",
            () => runner.RunAsync(args, ct), verbose);
    }

    [McpServerTool(Name = "uncook")]
    [Description("Extrait et convertit en une seule passe les fichiers d'une archive vers des " +
                 "formats exploitables (mesh → glTF, textures → image). Combine extraction et " +
                 "conversion, contrairement à extract_files qui ne fait qu'extraire.")]
    public static async Task<string> Uncook(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .archive (ou d'un dossier d'archives).")] string archivePath,
        [Description("Dossier de destination des fichiers convertis.")] string outputPath,
        [Description("Filtre glob optionnel, ex. *.mesh")] string? pattern = null,
        [Description("Format d'image pour les textures : png, dds, tga, bmp ou jpg.")] string? textureFormat = null,
        [Description("Type d'export mesh : MeshOnly, WithRig, Multimesh (défaut : WithMaterials).")] string? meshExportType = null,
        [Description("Exporteur mesh : Default, Experimental, REDmod.")] string? meshExporterType = null,
        [Description("Filtre les LOD du mesh export (réduit le bruit).")] bool meshExportLodFilter = false,
        [Description("Si vrai, renvoie le log complet (pas de troncature) — pour debug.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath) && !Directory.Exists(archivePath))
            return Err($"Chemin introuvable : {archivePath}");

        var args = new List<string> { "uncook", archivePath, "--outpath", outputPath };
        if (!string.IsNullOrWhiteSpace(pattern)) { args.Add("--pattern"); args.Add(pattern); }
        if (!string.IsNullOrWhiteSpace(textureFormat)) { args.Add("--uext"); args.Add(textureFormat); }
        if (!string.IsNullOrWhiteSpace(meshExportType)) { args.Add("--mesh-export-type"); args.Add(meshExportType); }
        if (!string.IsNullOrWhiteSpace(meshExporterType)) { args.Add("--mesh-exporter-type"); args.Add(meshExporterType); }
        if (meshExportLodFilter) args.Add("--mesh-export-lod-filter");

        return await WithSnapshot(outputPath,
            $"Extraction + conversion : {archivePath} → {outputPath}",
            () => runner.RunAsync(args, ct), verbose);
    }

    // ── Conversion ────────────────────────────────────────────────────────

    [McpServerTool(Name = "cr2w_to_json")]
    [Description("Convertit des fichiers REDengine déjà extraits (CR2W : .mesh, .ent, .app...) " +
                 "en JSON lisible et éditable.")]
    public static async Task<string> Cr2wToJson(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier CR2W, ou d'un dossier en contenant.")] string path,
        [Description("Dossier de destination des fichiers JSON.")] string outputPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Chemin introuvable : {path}");

        return await WithSnapshot(outputPath,
            $"Sérialisation CR2W → JSON : {path} → {outputPath}",
            () => runner.RunAsync(
                new[] { "convert", "serialize", path, "--outpath", outputPath }, ct));
    }

    [McpServerTool(Name = "json_to_cr2w")]
    [Description("Reconvertit des fichiers JSON (produits par cr2w_to_json) en fichiers " +
                 "REDengine CR2W binaires.")]
    public static async Task<string> JsonToCr2w(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier JSON, ou d'un dossier en contenant.")] string path,
        [Description("Dossier de destination des fichiers CR2W.")] string outputPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Chemin introuvable : {path}");

        return await WithSnapshot(outputPath,
            $"Désérialisation JSON → CR2W : {path} → {outputPath}",
            () => runner.RunAsync(
                new[] { "convert", "deserialize", path, "--outpath", outputPath }, ct));
    }

    [McpServerTool(Name = "export_files")]
    [Description("Exporte des fichiers REDengine déjà extraits vers des formats raw " +
                 "(mesh → glTF, texture → image, etc.).")]
    public static async Task<string> ExportFiles(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier REDengine, ou d'un dossier en contenant.")] string path,
        [Description("Dossier de destination des fichiers raw.")] string outputPath,
        [Description("Format d'image pour les textures : png, dds, tga, bmp ou jpg.")] string? textureFormat = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Chemin introuvable : {path}");

        var args = new List<string> { "export", path, "--outpath", outputPath };
        if (!string.IsNullOrWhiteSpace(textureFormat)) { args.Add("--uext"); args.Add(textureFormat); }

        return await WithSnapshot(outputPath,
            $"Export REDengine → raw : {path} → {outputPath}",
            () => runner.RunAsync(args, ct));
    }

    [McpServerTool(Name = "export_animation")]
    [Description("Exporte une animation REDengine (.anims) extraite vers glTF binaire (.glb), " +
                 "exploitable dans Blender. ⚠ WolvenKit exporte les animations à partir de leur " +
                 "rig/squelette : une .anims fournie SEULE (sans son .rig associé) peut ne rien " +
                 "produire, voire échouer. Pour un export fiable, extraire aussi le rig correspondant.")]
    public static async Task<string> ExportAnimation(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .anims (ou d'un dossier en contenant).")] string path,
        [Description("Dossier de destination des fichiers .glb produits.")] string outputPath,
        [Description("Si vrai, renvoie le log complet (pas de troncature) — pour debug.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Chemin introuvable : {path}");

        return await WithSnapshot(outputPath,
            $"Export animation (.anims) → glTF : {path} → {outputPath}",
            () => runner.RunAsync(new[] { "export", path, "--outpath", outputPath }, ct), verbose);
    }

    [McpServerTool(Name = "export_morphtarget")]
    [Description("Exporte une morphtarget REDengine (.morphtarget — formes de visage / blendshapes) " +
                 "extraite vers glTF binaire (.glb). Outil dédié et explicite par-dessus l'export " +
                 "générique (format déterminé par l'extension .morphtarget).")]
    public static async Task<string> ExportMorphTarget(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .morphtarget (ou d'un dossier en contenant).")] string path,
        [Description("Dossier de destination des fichiers .glb produits.")] string outputPath,
        [Description("Si vrai, renvoie le log complet (pas de troncature) — pour debug.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Chemin introuvable : {path}");

        return await WithSnapshot(outputPath,
            $"Export morphtarget → glTF : {path} → {outputPath}",
            () => runner.RunAsync(new[] { "export", path, "--outpath", outputPath }, ct), verbose);
    }

    [McpServerTool(Name = "export_mlmask")]
    [Description("Exporte un masque multilayer REDengine (.mlmask) extrait vers des images " +
                 "(une par couche). Le format d'image est réglable via textureFormat " +
                 "(png par défaut, ou dds/tga/bmp/jpg/tiff).")]
    public static async Task<string> ExportMlmask(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .mlmask (ou d'un dossier en contenant).")] string path,
        [Description("Dossier de destination des images produites.")] string outputPath,
        [Description("Format d'image : png (défaut), dds, tga, bmp, jpg ou tiff.")] string? textureFormat = null,
        [Description("Si vrai, renvoie le log complet (pas de troncature) — pour debug.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Chemin introuvable : {path}");

        var args = new List<string> { "export", path, "--outpath", outputPath };
        if (!string.IsNullOrWhiteSpace(textureFormat)) { args.Add("--uext"); args.Add(textureFormat); }

        return await WithSnapshot(outputPath,
            $"Export mlmask → images : {path} → {outputPath}",
            () => runner.RunAsync(args, ct), verbose);
    }

    [McpServerTool(Name = "export_entity")]
    [Description("Exporte une apparence d'entité REDengine (.ent) vers glTF (.glb) via " +
                 "IModTools.ExportEntity. Découvre d'abord les apparences de l'entité : si " +
                 "`appearance` est vide, prend la première ; si invalide, renvoie la liste " +
                 "disponible. ⚠ EXPÉRIMENTAL : WolvenKit refuse l'export headless de certains types " +
                 "d'entités (« can not be exported ») — utiliser list_entity_appearances pour " +
                 "inspecter, et uncook sur les .mesh référencés pour les visualiser de façon fiable.")]
    public static async Task<string> ExportEntity(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .ent (entité) extrait.")] string entFile,
        [Description("Fichier .glb de sortie.")] string outputPath,
        [Description("Nom d'apparence (le `name` de l'entité). Vide = la première.")] string? appearance = null,
        [Description("Racine du jeu (charge les archives pour résoudre meshes/matériaux).")] string? gamePath = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(entFile))
            return Err($"Fichier .ent introuvable : {entFile}");

        // Découvrir les apparences pour valider/choisir et donner des erreurs claires.
        var tmp = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-entappr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        List<string> available;
        try
        {
            await runner.RunAsync(new[] { "convert", "serialize", entFile, "--outpath", tmp }, ct);
            var json = Directory.EnumerateFiles(tmp, "*.json", SearchOption.AllDirectories).FirstOrDefault();
            available = json is null ? new List<string>()
                : ModdingTools.ParseEntityAppearances(await File.ReadAllTextAsync(json, ct))
                    .Select(a => a.Name).ToList();
        }
        catch { available = new List<string>(); }
        finally { try { Directory.Delete(tmp, true); } catch { /* best-effort */ } }

        if (available.Count == 0)
            return Err($"L'entité {Path.GetFileName(entFile)} n'expose aucune apparence " +
                       "(entité composant/proxy ?) — rien à exporter. Voir list_entity_appearances.");

        var chosen = appearance;
        if (string.IsNullOrWhiteSpace(chosen)) chosen = available[0];
        else if (!available.Contains(chosen, StringComparer.OrdinalIgnoreCase))
            return Err($"Apparence '{chosen}' introuvable dans {Path.GetFileName(entFile)}. " +
                       $"Disponibles : {string.Join(", ", available.Take(20))}.");

        var args = new List<string> { "export-entity", entFile, "--out", outputPath, "--appearance", chosen };
        if (!string.IsNullOrWhiteSpace(gamePath)) { args.Add("--game"); args.Add(gamePath); }
        var r = await runner.RunAsync(args, ct);
        var produced = File.Exists(outputPath) ? new List<string> { outputPath } : new List<string>();

        var log = (r.Stdout + r.Stderr).Trim();
        var notExportable = log.Contains("can not be exported", StringComparison.OrdinalIgnoreCase);
        var ok = produced.Count > 0;
        return JsonSerializer.Serialize(new
        {
            ok,
            status = ok ? "success" : "error",
            summary = ok
                ? $"Entité exportée [{chosen}] → {outputPath}"
                : notExportable
                    ? $"WolvenKit refuse l'export headless de cette entité [{chosen}] (« can not be exported »)."
                    : $"Export entité échoué [{chosen}].",
            entFile,
            chosenAppearance = chosen,
            availableAppearances = available,
            produced,
            warnings = notExportable
                ? new[] { "Type d'entité non exportable headless — voir list_entity_appearances + uncook des .mesh." }
                : Array.Empty<string>(),
            errors = ok ? new List<string>() : LogLines(log, "Error"),
            log = Truncate(log, 8_000),
        }, JsonOpts);
    }

    [McpServerTool(Name = "export_materials")]
    [Description("Exporte les matériaux d'un mesh REDengine (.mesh) vers JSON + textures " +
                 "(via IModTools.ExportMaterials). gamePath charge les archives pour résoudre les " +
                 "dépendances de matériaux de base.")]
    public static async Task<string> ExportMaterials(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .mesh extrait.")] string meshFile,
        [Description("Fichier de sortie (JSON des matériaux).")] string outputPath,
        [Description("Racine du jeu (charge les archives pour résoudre les matériaux de base).")] string? gamePath = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(meshFile))
            return Err($"Fichier .mesh introuvable : {meshFile}");

        var args = new List<string> { "export-materials", meshFile, "--out", outputPath };
        if (!string.IsNullOrWhiteSpace(gamePath)) { args.Add("--game"); args.Add(gamePath); }

        // ExportMaterials écrit plusieurs fichiers (JSON + textures) dans le dossier-
        // dépôt, pas seulement outputPath : on capture tout ce qui est produit.
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(dir);
        var before = Snapshot(dir);
        var r = await runner.RunAsync(args, ct);
        return Structured($"Export matériaux : {meshFile} → {outputPath}", r, ProducedIn(dir, before));
    }

    // ── Lecture / écriture directe d'un fichier de jeu ────────────────────

    [McpServerTool(Name = "read_game_file")]
    [Description("Lit un fichier de jeu en un seul appel : extrait le fichier de l'archive, " +
                 "le convertit en JSON REDengine et renvoie son contenu — au lieu d'enchaîner " +
                 "extract_files puis cr2w_to_json. Le JSON complet est aussi écrit sur disque " +
                 "(champ jsonFile), à lire à part si le contenu renvoyé est tronqué.")]
    public static async Task<string> ReadGameFile(
        Cp77ToolsRunner runner,
        [Description("Chemin du fichier .archive contenant le fichier voulu.")] string archivePath,
        [Description("Chemin interne du fichier dans l'archive (ex. base\\gameplay\\...\\x.ent). " +
                     "Le localiser au besoin avec find_in_archives.")] string gameFilePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            return Err($"Archive introuvable : {archivePath}");

        // Dossier de travail DÉTERMINISTE par (archive, fichier) : relire le même
        // fichier réécrit au même endroit au lieu d'accumuler un dossier GUID par
        // appel (fuite). jsonFile reste lisible après l'appel comme documenté.
        var work = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-read",
                                StableHash(archivePath + "|" + gameFilePath));
        var rawDir = Path.Combine(work, "raw");
        var jsonDir = Path.Combine(work, "json");
        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(jsonDir);

        var ext = await runner.RunAsync(
            new[] { "unbundle", archivePath, "--outpath", rawDir, "--pattern", gameFilePath }, ct);
        var extracted = Directory
            .EnumerateFiles(rawDir, "*", SearchOption.AllDirectories).FirstOrDefault();
        if (extracted is null)
            return Err($"Fichier introuvable dans l'archive : {gameFilePath} " +
                       "(vérifier le chemin interne avec find_in_archives).");

        var conv = await runner.RunAsync(
            new[] { "convert", "serialize", extracted, "--outpath", jsonDir }, ct);
        var jsonFile = Directory
            .EnumerateFiles(jsonDir, "*.json", SearchOption.AllDirectories).FirstOrDefault();
        var log = (ext.Stdout + "\n" + conv.Stdout).Trim();

        if (jsonFile is null)
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"Fichier extrait (type non-CR2W : non converti en JSON) : {gameFilePath}",
                gameFilePath,
                rawFile = extracted,
                jsonFile = (string?)null,
                truncated = false,
                content = (string?)null,
                warnings = LogLines(log, "Warning"),
                errors = LogLines(log, "Error"),
            }, JsonOpts);

        var content = await File.ReadAllTextAsync(jsonFile, ct);
        var truncated = content.Length > ReadContentCap;
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Fichier de jeu lu : {gameFilePath}"
                      + (truncated ? " (contenu tronqué — lire jsonFile en entier)" : ""),
            gameFilePath,
            jsonFile,
            truncated,
            content = truncated ? content[..ReadContentCap] : content,
            warnings = LogLines(log, "Warning"),
            errors = LogLines(log, "Error"),
        }, JsonOpts);
    }

    [McpServerTool(Name = "write_game_file")]
    [Description("Écrit un fichier de jeu édité : convertit un JSON (produit par read_game_file " +
                 "puis modifié) en fichier REDengine CR2W binaire, placé dans un dossier de mod " +
                 "au bon chemin interne — prêt à être empaqueté par pack_archive.")]
    public static async Task<string> WriteGameFile(
        Cp77ToolsRunner runner,
        [Description("Chemin du fichier JSON édité (issu de read_game_file).")] string jsonFile,
        [Description("Chemin interne visé dans le jeu (ex. base\\...\\x.ent) — détermine " +
                     "l'emplacement du CR2W produit.")] string gameFilePath,
        [Description("Dossier où placer le CR2W (typiquement le source/archive d'un projet de mod).")] string modArchiveFolder,
        CancellationToken ct = default)
    {
        if (!File.Exists(jsonFile))
            return Err($"Fichier JSON introuvable : {jsonFile}");

        var tmp = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-write",
                               Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var conv = await runner.RunAsync(
            new[] { "convert", "deserialize", jsonFile, "--outpath", tmp }, ct);
        var cr2w = Directory
            .EnumerateFiles(tmp, "*", SearchOption.AllDirectories)
            .FirstOrDefault(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

        if (cr2w is null)
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* nettoyage best-effort */ }
            return Structured($"Conversion JSON → CR2W échouée : {jsonFile}", conv,
                new List<string>());
        }

        var dest = Path.Combine(modArchiveFolder, gameFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(cr2w, dest, overwrite: true);
        try { Directory.Delete(tmp, recursive: true); } catch { /* nettoyage best-effort */ }

        return Structured($"Fichier de jeu écrit : {gameFilePath} → {dest}", conv,
            new List<string> { dest });
    }

    // ── Audio / compression bas niveau ────────────────────────────────────

    [McpServerTool(Name = "wwise_export")]
    [Description("Convertit des fichiers audio Wwise WEM en OGG. Nécessite les binaires audio " +
                 "natifs — présents sous Windows, indisponibles sous macOS. Conversions " +
                 "exécutées en parallèle (jusqu'à 4 simultanées) ; le vrai gain apparaît " +
                 "quand le daemon supporte le pipelining (sinon overlap I/O uniquement).")]
    public static async Task<string> WwiseExport(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .wem, ou d'un dossier en contenant.")] string path,
        [Description("Dossier de destination des fichiers OGG.")] string outputPath,
        [Description("Si vrai, renvoie le log complet (pas de troncature) — pour debug.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Chemin introuvable : {path}");

        Directory.CreateDirectory(outputPath);

        // Le verbe `wwise` attend un FICHIER .ogg de sortie (pas un dossier) ; on
        // convertit donc chaque .wem en un .ogg explicitement nommé dans outputPath.
        var wems = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.wem", SearchOption.AllDirectories)
            : new[] { path };
        if (wems.Length == 0)
            return Err($"Aucun fichier .wem trouvé dans : {path}");

        var before = Snapshot(outputPath);
        var logs = new ConcurrentBag<string>();
        var errorCodes = new ConcurrentBag<int>();

        await Parallel.ForEachAsync(wems,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
                CancellationToken = ct,
            },
            async (wem, token) =>
            {
                var ogg = Path.Combine(outputPath,
                    Path.GetFileNameWithoutExtension(wem) + ".ogg");
                var r = await runner.RunAsync(new[] { "wwise", wem, ogg, "--wem" }, token);
                logs.Add(
                    $"{Path.GetFileName(wem)} → {Path.GetFileName(ogg)}\n" +
                    (r.Stdout + r.Stderr).Trim());
                if (r.ExitCode != 0) errorCodes.Add(r.ExitCode);
            });

        var aggregate = new CliResult(
            errorCodes.IsEmpty ? 0 : errorCodes.First(),
            string.Join("\n", logs), "", false);
        return Structured(
            $"Conversion Wwise WEM → OGG : {wems.Length} fichier(s) → {outputPath} (parallèle)",
            aggregate, ProducedIn(outputPath, before), verbose);
    }

    [McpServerTool(Name = "extract_audio")]
    [Description("Extrait l'audio voix-off (opus) d'une archive vocale Cyberpunk 2077 " +
                 "(typiquement lang_xx_voice.archive). Par défaut extrait TOUS les opus de " +
                 "l'archive ; opusHashes (liste de hashes uint séparés par des virgules) cible " +
                 "des clips précis. Combine l'archive opusinfo + ses opuspak via le pipeline uncook.")]
    public static async Task<string> ExtractAudio(
        Cp77ToolsRunner runner,
        [Description("Chemin de l'archive vocale .archive (contenant l'opusinfo).")] string archivePath,
        [Description("Dossier de destination des fichiers audio produits.")] string outputPath,
        [Description("Optionnel : hashes opus précis à extraire (uint séparés par des virgules). " +
                     "Vide = tout extraire.")] string? opusHashes = null,
        [Description("Si vrai, renvoie le log complet (pas de troncature) — pour debug.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath) && !Directory.Exists(archivePath))
            return Err($"Chemin introuvable : {archivePath}");

        var args = new List<string> { "uncook", archivePath, "--outpath", outputPath };
        if (!string.IsNullOrWhiteSpace(opusHashes)) { args.Add("--opus-hashes"); args.Add(opusHashes); }
        else args.Add("--opus-export-all");

        return await WithSnapshot(outputPath,
            $"Extraction audio opus : {archivePath} → {outputPath}",
            () => runner.RunAsync(args, ct), verbose);
    }

    [McpServerTool(Name = "import_audio")]
    [Description("Importe des fichiers WAV (nommés par leur hash opus, ex. 123456.wav) en audio " +
                 ".opus repacké dans un dossier de mod — remplacement de voix-off. Les WAV sont " +
                 "encodés via opusenc et réinjectés dans l'OpusPak du jeu. ⚠ EXPÉRIMENTAL : charge " +
                 "les archives du jeu (quelques secondes) ; nécessite le chemin d'installation.")]
    public static async Task<string> ImportAudio(
        Cp77ToolsRunner runner,
        [Description("Dossier racine de l'installation Cyberpunk 2077 (ou chemin du .exe).")] string gamePath,
        [Description("Dossier contenant les .wav à importer (noms de fichiers = hashes opus).")] string wavFolder,
        [Description("Dossier de sortie du mod (recevra l'OpusPak modifié).")] string outputPath,
        [Description("Si vrai, renvoie le log complet (pas de troncature) — pour debug.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(wavFolder))
            return Err($"Dossier WAV introuvable : {wavFolder}");

        var args = new List<string>
            { "opus-import", gamePath, "--wav-dir", wavFolder, "--out", outputPath };
        return await WithSnapshot(outputPath,
            $"Import audio WAV → opus : {wavFolder} → {outputPath}",
            () => runner.RunAsync(args, ct), verbose);
    }

    [McpServerTool(Name = "loc_resolve")]
    [Description("Résout une clé de localisation (LocKey : hash uint64 ou clé secondaire texte) en " +
                 "son texte localisé (variantes masculine/féminine), via les on-screens du jeu — " +
                 "sans charger toute la TweakDB. ⚠ EXPÉRIMENTAL : charge les archives du jeu " +
                 "(quelques secondes) ; nécessite le chemin d'installation.")]
    public static async Task<string> LocResolve(
        Cp77ToolsRunner runner,
        [Description("Dossier racine de l'installation Cyberpunk 2077 (ou chemin du .exe).")] string gamePath,
        [Description("Clé à résoudre : hash uint64 (ex. 12345) ou clé secondaire texte.")] string key,
        [Description("Langue (code REDengine : en_us, fr_fr, de_de, jp_jp...). Défaut : en_us.")] string? language = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "loc-resolve", gamePath, "--key", key };
        if (!string.IsNullOrWhiteSpace(language)) { args.Add("--lang"); args.Add(language); }
        var r = await runner.RunAsync(args, ct);
        return Structured($"Résolution LocKey '{key}' ({language ?? "en_us"})", r);
    }

    [McpServerTool(Name = "oodle_compress")]
    [Description("Compresse un fichier avec le codec Oodle Kraken (utilitaire bas niveau).")]
    public static async Task<string> OodleCompress(
        Cp77ToolsRunner runner,
        [Description("Fichier d'entrée à compresser.")] string inputPath,
        [Description("Fichier de sortie compressé.")] string outputPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            return Err($"Fichier introuvable : {inputPath}");

        var r = await runner.RunAsync(new[] { "oodle", "compress", inputPath, outputPath }, ct);
        return Structured($"Compression Kraken : {inputPath} → {outputPath}", r,
            File.Exists(outputPath) ? new List<string> { outputPath } : new List<string>());
    }

    [McpServerTool(Name = "oodle_decompress")]
    [Description("Décompresse un fichier compressé avec le codec Oodle Kraken (utilitaire bas niveau).")]
    public static async Task<string> OodleDecompress(
        Cp77ToolsRunner runner,
        [Description("Fichier d'entrée compressé.")] string inputPath,
        [Description("Fichier de sortie décompressé.")] string outputPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            return Err($"Fichier introuvable : {inputPath}");

        var r = await runner.RunAsync(new[] { "oodle", "decompress", inputPath, outputPath }, ct);

        // WolvenKit écrit la sortie de `oodle decompress` dans <outputPath>.bin ;
        // on la replace au chemin demandé pour respecter le contrat de l'outil.
        var written = outputPath + ".bin";
        if (!File.Exists(outputPath) && File.Exists(written))
        {
            try { File.Move(written, outputPath, overwrite: true); }
            catch { /* à défaut, la sortie reste à <outputPath>.bin */ }
        }
        return Structured($"Décompression Kraken : {inputPath} → {outputPath}", r,
            File.Exists(outputPath) ? new List<string> { outputPath } : new List<string>());
    }

    // ── Écriture / création de mods ───────────────────────────────────────

    [McpServerTool(Name = "pack_archive")]
    [Description("Empaquette un dossier de fichiers ressources REDengine en archive .archive " +
                 "de Cyberpunk 2077 (compression Kraken).")]
    public static async Task<string> PackArchive(
        Cp77ToolsRunner runner,
        [Description("Dossier contenant les fichiers ressources à empaqueter.")] string folderPath,
        [Description("Dossier de destination de l'archive .archive produite.")] string outputPath,
        [Description("Si vrai, renvoie le log complet (pas de troncature) — pour debug.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(folderPath))
            return Err($"Dossier introuvable : {folderPath}");

        return await WithSnapshot(outputPath,
            $"Empaquetage en .archive : {folderPath} → {outputPath}",
            () => runner.RunAsync(
                new[] { "pack", folderPath, "--outpath", outputPath }, ct), verbose);
    }

    [McpServerTool(Name = "import_raw")]
    [Description("Importe des fichiers raw (textures, meshes glTF...) en fichiers REDengine CR2W, " +
                 "prêts à être empaquetés dans un mod.")]
    public static async Task<string> ImportRaw(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier raw, ou d'un dossier en contenant.")] string path,
        [Description("Dossier de destination des fichiers REDengine.")] string outputPath,
        [Description("Si vrai, renvoie le log complet (pas de troncature) — pour debug.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Chemin introuvable : {path}");

        return await WithSnapshot(outputPath,
            $"Import raw → REDengine : {path} → {outputPath}",
            () => runner.RunAsync(
                new[] { "import", path, "--outpath", outputPath }, ct), verbose);
    }

    [McpServerTool(Name = "build_project")]
    [Description("Compile les projets WolvenKit (.cpmodproj) trouvés dans le dossier donné, " +
                 "produisant un mod prêt à installer.")]
    public static async Task<string> BuildProject(
        Cp77ToolsRunner runner,
        [Description("Dossier contenant un ou plusieurs projets .cpmodproj.")] string projectFolder,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(projectFolder))
            return Err($"Dossier de projet introuvable : {projectFolder}");

        var r = await runner.RunAsync(new[] { "build", projectFolder }, ct);
        return Structured($"Build des projets .cpmodproj dans : {projectFolder}", r);
    }

    [McpServerTool(Name = "detect_conflicts")]
    [Description("Détecte les conflits entre mods installés (un même fichier de jeu fourni par " +
                 "plusieurs mods). Prend le dossier racine du jeu et analyse son archive/pc/mod. " +
                 "Sortie JSON structurée, facile à analyser.")]
    public static async Task<string> DetectConflicts(
        Cp77ToolsRunner runner,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");

        // Le verbe `conflicts` attend le dossier RACINE du jeu (il y localise
        // lui-même archive/pc/mod) et non le dossier des mods directement.
        var r = await runner.RunAsync(new[] { "conflicts", gamePath, "--structured" }, ct);
        return Structured($"Analyse des conflits de mods : {gamePath}", r);
    }

    // ── Workflow projet (système de fichiers, sans cp77tools) ─────────────

    [McpServerTool(Name = "list_installed_mods")]
    [Description("Liste les mods installés dans un dossier de jeu Cyberpunk 2077 : archives " +
                 ".archive dans archive/pc/mod et mods REDmod dans mods/.")]
    public static string ListInstalledMods(
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");

        var archiveDir = Path.Combine(gamePath, "archive", "pc", "mod");
        var archiveMods = Directory.Exists(archiveDir)
            ? Directory.GetFiles(archiveDir, "*.archive").OrderBy(f => f)
                .Select(f => new { name = Path.GetFileName(f), sizeKo = new FileInfo(f).Length / 1024 })
                .ToList()
            : null;

        var redmodDir = Path.Combine(gamePath, "mods");
        var redMods = Directory.Exists(redmodDir)
            ? Directory.GetDirectories(redmodDir).OrderBy(d => d)
                .Select(Path.GetFileName).ToList()
            : null;

        var result = new
        {
            ok = true,
            status = "success",
            summary = $"Mods installés dans {gamePath}",
            archiveMods = archiveMods ?? new(),
            archiveModsCount = archiveMods?.Count ?? 0,
            redMods = redMods ?? new(),
            redModsCount = redMods?.Count ?? 0,
            warnings = (archiveMods is null ? new[] { $"Dossier absent : {archiveDir}" }
                        : redMods is null ? new[] { $"Dossier absent : {redmodDir}" }
                        : Array.Empty<string>()),
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "create_mod_project")]
    [Description("Crée la structure de dossiers d'un projet de mod WolvenKit " +
                 "(source/archive, source/raw, source/resources, source/customSounds, packed) " +
                 "ET un fichier <modName>.cpmodproj à la racine — directement compilable par " +
                 "build_project. Prête pour le workflow : extract_files/uncook → édition → " +
                 "import_raw → build_project.")]
    public static string CreateModProject(
        [Description("Dossier parent où créer le projet.")] string parentFolder,
        [Description("Nom du mod / du projet.")] string modName,
        [Description("Auteur du mod (optionnel).")] string? author = null,
        [Description("Version du mod (optionnel, ex. 1.0.0).")] string? version = null,
        [Description("Description du mod (optionnel).")] string? description = null)
    {
        if (!Directory.Exists(parentFolder))
            return Err($"Dossier parent introuvable : {parentFolder}");
        if (string.IsNullOrWhiteSpace(modName)
            || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err("Nom de mod invalide.");

        var root = Path.Combine(parentFolder, modName);
        if (Directory.Exists(root))
            return Err($"Le dossier existe déjà : {root}");

        string[] subdirs =
        {
            Path.Combine("source", "archive"),
            Path.Combine("source", "raw"),
            Path.Combine("source", "resources"),
            Path.Combine("source", "customSounds"),
            "packed",
        };
        foreach (var s in subdirs)
            Directory.CreateDirectory(Path.Combine(root, s));

        File.WriteAllText(Path.Combine(root, "README.txt"),
            $"Projet de mod WolvenKit : {modName}\n\n" +
            "<modName>.cpmodproj  Fichier projet WolvenKit (compilable par build_project).\n" +
            "source/archive/    Fichiers REDengine cuits (.mesh, .ent, .xbm...).\n" +
            "                   C'est ce dossier qu'on empaquette avec pack_archive.\n" +
            "source/raw/        Fichiers bruts (glTF, images, .blend...) a passer par import_raw.\n" +
            "source/resources/  Fichiers libres copies tels quels.\n" +
            "source/customSounds/  Sons personnalises (REDmod audio).\n" +
            "packed/            Sortie : build_project y depose le mod compile.\n\n" +
            "Workflow : extract_files / uncook -> edition -> import_raw -> build_project.\n");

        var projFile = Path.Combine(root, modName + ".cpmodproj");
        File.WriteAllText(projFile,
            BuildCpmodprojXml(modName, author, version, description));

        var produced = subdirs.Select(s => s + Path.DirectorySeparatorChar)
            .Append(modName + ".cpmodproj").ToArray();
        var result = new
        {
            ok = true,
            status = "success",
            summary = $"Projet de mod créé : {root}",
            produced,
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "generate_modproj")]
    [Description("Génère un fichier projet WolvenKit (.cpmodproj) dans un dossier de projet " +
                 "EXISTANT (créé manuellement ou par un workflow), afin que build_project puisse " +
                 "le compiler. Utile pour rendre compilable un projet qui n'en a pas. Le format " +
                 "est l'XML <CP77Mod> attendu par WolvenKit (seul le nom est requis).")]
    public static string GenerateModProj(
        [Description("Dossier racine du projet (où déposer le .cpmodproj).")] string projectFolder,
        [Description("Nom du mod / du projet.")] string modName,
        [Description("Auteur du mod (optionnel).")] string? author = null,
        [Description("Version du mod (optionnel, ex. 1.0.0).")] string? version = null,
        [Description("Description du mod (optionnel).")] string? description = null,
        [Description("Écraser un .cpmodproj existant (défaut : false).")] bool overwrite = false)
    {
        if (!Directory.Exists(projectFolder))
            return Err($"Dossier de projet introuvable : {projectFolder}");
        if (string.IsNullOrWhiteSpace(modName)
            || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err("Nom de mod invalide.");

        var projFile = Path.Combine(projectFolder, modName + ".cpmodproj");
        if (File.Exists(projFile) && !overwrite)
            return Err($"Le projet existe déjà : {projFile} (passer overwrite=true pour écraser).");

        File.WriteAllText(projFile, BuildCpmodprojXml(modName, author, version, description));

        var result = new
        {
            ok = true,
            status = "success",
            summary = $"Projet .cpmodproj généré : {projFile}",
            produced = new[] { modName + ".cpmodproj" },
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    /// <summary>Construit le contenu XML d'un .cpmodproj (DTO CP77Mod de WolvenKit).
    /// Seul &lt;Name&gt; est requis ; ModName retombe sur Name si absent. Le chargeur
    /// XmlSerializer de WolvenKit ignore les éléments inconnus, donc ce sous-ensemble
    /// suffit pour build_project.</summary>
    /// <summary>Hash stable (FNV-1a 64 bits, hex) — déterministe entre exécutions,
    /// contrairement à string.GetHashCode(). Sert à nommer des dossiers temp
    /// réutilisables.</summary>
    internal static string StableHash(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    internal static string BuildCpmodprojXml(string modName, string? author,
        string? version, string? description)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using (var sw = new StringWriter(sb))
        using (var w = XmlWriter.Create(sw, settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("CP77Mod");
            w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
            w.WriteElementString("Name", modName);
            w.WriteElementString("ModName", modName);
            w.WriteElementString("Author", author ?? "");
            w.WriteElementString("Email", "");
            w.WriteElementString("Description", description ?? "");
            w.WriteElementString("Version", string.IsNullOrWhiteSpace(version) ? "1.0.0" : version);
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        return sb.ToString();
    }

    // ── Inspection rapide (résumés sans conversion lourde) ──────────────

    [McpServerTool(Name = "inspect_mesh")]
    [Description("Inspecte un fichier .mesh REDengine et renvoie un résumé compact : " +
                 "nombre de LODs, sous-meshes, matériaux, bones. Sérialise le CR2W en " +
                 "JSON via le daemon puis n'extrait que les agrégats — bien plus léger " +
                 "qu'un uncook complet ou un read_game_file qui renvoie tout l'arbre.")]
    public static async Task<string> InspectMesh(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .mesh REDengine (déjà extrait d'une archive).")] string meshFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(meshFile))
            return Err($"Fichier .mesh introuvable : {meshFile}");

        var tmp = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-inspect",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var r = await runner.RunAsync(
                new[] { "convert", "serialize", meshFile, "--outpath", tmp }, ct);
            var jsonFile = Directory
                .EnumerateFiles(tmp, "*.json", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (jsonFile is null)
                return Structured($"Inspection mesh échouée : {meshFile}", r,
                    new List<string>());

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonFile, ct));
            var stats = ScanMeshStats(doc.RootElement);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"Mesh inspecté : {Path.GetFileName(meshFile)} — " +
                          $"{stats.LodCount} LOD(s), {stats.SubMeshCount} sous-mesh, " +
                          $"{stats.MaterialCount} matériau(x), {stats.BoneCount} bone(s)",
                produced = Array.Empty<string>(),
                warnings = LogLines(r.Stdout, "Warning"),
                errors = LogLines(r.Stdout, "Error"),
                meshFile,
                lodCount = stats.LodCount,
                subMeshCount = stats.SubMeshCount,
                materialCount = stats.MaterialCount,
                boneCount = stats.BoneCount,
                materials = stats.MaterialNames.Take(40).ToList(),
                bones = stats.BoneNames.Take(40).ToList(),
                exitCode = r.ExitCode,
                log = Truncate(r.Stdout, 2_000),
            }, JsonOpts);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); }
            catch { /* nettoyage best-effort */ }
        }
    }

    [McpServerTool(Name = "inspect_texture")]
    [Description("Inspecte un fichier .xbm REDengine (texture) et renvoie ses métadonnées : " +
                 "résolution, format, compression, mipmaps, groupe de texture — sans conversion " +
                 "vers PNG/DDS. Sérialise le CR2W en JSON via le daemon puis n'extrait que les " +
                 "champs setup.*.")]
    public static async Task<string> InspectTexture(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .xbm REDengine (déjà extrait d'une archive).")] string xbmFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(xbmFile))
            return Err($"Fichier .xbm introuvable : {xbmFile}");

        var tmp = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-inspect",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var r = await runner.RunAsync(
                new[] { "convert", "serialize", xbmFile, "--outpath", tmp }, ct);
            var jsonFile = Directory
                .EnumerateFiles(tmp, "*.json", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (jsonFile is null)
                return Structured($"Inspection texture échouée : {xbmFile}", r,
                    new List<string>());

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonFile, ct));
            var props = ScanTextureProps(doc.RootElement);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"Texture inspectée : {Path.GetFileName(xbmFile)} — " +
                          $"{props.Width}x{props.Height} {props.Format}",
                produced = Array.Empty<string>(),
                warnings = LogLines(r.Stdout, "Warning"),
                errors = LogLines(r.Stdout, "Error"),
                xbmFile,
                width = props.Width,
                height = props.Height,
                format = props.Format,
                compression = props.Compression,
                mipLevels = props.MipLevels,
                textureGroup = props.TextureGroup,
                exitCode = r.ExitCode,
                log = Truncate(r.Stdout, 2_000),
            }, JsonOpts);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); }
            catch { /* nettoyage best-effort */ }
        }
    }

    [McpServerTool(Name = "describe_tweak_record")]
    [Description("Pour un identifiant TweakDB donné (record), liste tous ses flats avec " +
                 "leurs types et valeurs courantes — l'inverse de tweakdb_query, qui ne fait " +
                 "que chercher des identifiants. Indispensable avant d'éditer un record via " +
                 "write_tweak : permet de savoir quels champs existent et leurs valeurs.")]
    public static async Task<string> DescribeTweakRecord(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier tweakdb.bin (typiquement <jeu>/r6/cache/tweakdb.bin).")] string tweakdbPath,
        [Description("Identifiant TweakDB du record (ex. Items.Preset_Achilles_Collectible_inline0).")] string recordId,
        CancellationToken ct = default)
    {
        if (!File.Exists(tweakdbPath))
            return Err($"Fichier TweakDB introuvable : {tweakdbPath}");
        if (string.IsNullOrWhiteSpace(recordId))
            return Err("recordId vide.");

        var r = await runner.RunAsync(
            new[] { "tweakdb-describe", tweakdbPath, recordId }, ct);
        return Structured($"Description record TweakDB : {recordId}", r);
    }

    // ── TweakDB structurée (format TweakXL — YAML) ──────────────────────

    [McpServerTool(Name = "read_tweak")]
    [Description("Lit un fichier .tweak (format TweakXL — YAML) et renvoie son contenu en " +
                 "JSON éditable. Permet d'inspecter et modifier des tweaks en restant dans " +
                 "un format structuré, sans manipuler du YAML brut.")]
    public static async Task<string> ReadTweak(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .tweak (TweakXL).")] string tweakFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(tweakFile))
            return Err($"Fichier .tweak introuvable : {tweakFile}");

        var jsonFile = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-tweak",
            Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonFile)!);

        var r = await runner.RunAsync(new[] { "tweak", "read", tweakFile, jsonFile }, ct);
        if (r.ExitCode != 0 || !File.Exists(jsonFile))
        {
            return Structured($"Lecture .tweak échouée : {tweakFile}", r,
                new List<string>());
        }

        var content = await File.ReadAllTextAsync(jsonFile, ct);
        var truncated = content.Length > ReadContentCap;
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $".tweak lu : {tweakFile}" +
                      (truncated ? " (contenu tronqué — lire jsonFile en entier)" : ""),
            produced = new[] { jsonFile },
            warnings = LogLines(r.Stdout + r.Stderr, "Warning"),
            errors = LogLines(r.Stdout + r.Stderr, "Error"),
            tweakFile,
            jsonFile,
            truncated,
            content = truncated ? content[..ReadContentCap] : content,
            exitCode = r.ExitCode,
        }, JsonOpts);
    }

    [McpServerTool(Name = "write_tweak")]
    [Description("Reconvertit un JSON (produit par read_tweak puis édité) en fichier .tweak " +
                 "(YAML TweakXL) prêt à être copié dans <jeu>/r6/tweaks/ via install_tweak.")]
    public static async Task<string> WriteTweak(
        Cp77ToolsRunner runner,
        [Description("Chemin du fichier JSON édité (issu de read_tweak).")] string jsonFile,
        [Description("Chemin du fichier .tweak à produire.")] string outputTweakFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(jsonFile))
            return Err($"Fichier JSON introuvable : {jsonFile}");

        Directory.CreateDirectory(Path.GetDirectoryName(outputTweakFile) ?? ".");
        var r = await runner.RunAsync(
            new[] { "tweak", "write", jsonFile, outputTweakFile }, ct);
        return Structured($"Écriture .tweak : {jsonFile} → {outputTweakFile}", r,
            File.Exists(outputTweakFile) ? new List<string> { outputTweakFile }
                                          : new List<string>());
    }

    [McpServerTool(Name = "validate_tweak")]
    [Description("Vérifie un fichier .tweak contre une TweakDB : chaque clé du fichier doit " +
                 "exister dans TweakDB (record ou flat), sauf si elle déclare $instanceOf " +
                 "(nouveau record dérivé). Renvoie la liste des clés inconnues — utile " +
                 "avant install_tweak.")]
    public static async Task<string> ValidateTweak(
        Cp77ToolsRunner runner,
        [Description("Chemin du fichier .tweak à valider.")] string tweakFile,
        [Description("Chemin du tweakdb.bin de référence (typiquement <jeu>/r6/cache/tweakdb.bin).")] string tweakdbBin,
        CancellationToken ct = default)
    {
        if (!File.Exists(tweakFile))
            return Err($"Fichier .tweak introuvable : {tweakFile}");
        if (!File.Exists(tweakdbBin))
            return Err($"tweakdb.bin introuvable : {tweakdbBin}");

        var r = await runner.RunAsync(
            new[] { "tweak", "validate", tweakFile, tweakdbBin }, ct);
        return Structured($"Validation .tweak : {tweakFile} contre {tweakdbBin}", r);
    }

    [McpServerTool(Name = "generate_redscript_template")]
    [Description("Génère un fichier .reds (RED4Script) prêt à éditer, depuis un catalogue " +
                 "de patterns courants : add_method (@addMethod), wrap_method (@wrapMethod), " +
                 "replace_method (@replaceMethod), add_field (@addField), new_class. Évite " +
                 "d'écrire la syntaxe annotation à la main.")]
    public static string GenerateRedscriptTemplate(
        [Description("Pattern : add_method | wrap_method | replace_method | add_field | new_class.")] string pattern,
        [Description("Paramètres du template en JSON (voir description du pattern).")] string parametersJson,
        [Description("Chemin du fichier .reds à produire.")] string outputFile)
    {
        Dictionary<string, object?> p;
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Err("parametersJson doit être un objet JSON.");
            p = doc.RootElement.EnumerateObject().ToDictionary(
                k => k.Name,
                v => v.Value.ValueKind switch
                {
                    JsonValueKind.String => (object?)v.Value.GetString(),
                    JsonValueKind.Number => v.Value.TryGetInt64(out var l)
                        ? (object?)l : v.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => v.Value.GetRawText(),
                });
        }
        catch (JsonException ex)
        {
            return Err($"parametersJson invalide : {ex.Message}");
        }

        string? Str(string key)
            => p.TryGetValue(key, out var v) ? v?.ToString() : null;

        static string ExtractArgNames(string args)
        {
            if (string.IsNullOrWhiteSpace(args)) return "";
            return string.Join(", ", args.Split(',')
                .Select(a => a.Trim().Split(':')[0].Trim())
                .Where(s => s.Length > 0));
        }

        string reds;
        string desc;
        switch (pattern.ToLowerInvariant())
        {
            case "add_method":
            {
                var target = Str("targetClass");
                var name = Str("methodName");
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(name))
                    return Err("add_method : 'targetClass' et 'methodName' requis.");
                var args = Str("args") ?? "";
                var ret = Str("returnType") ?? "Void";
                var body = Str("body") ?? "// TODO";
                reds = $"@addMethod({target})\npublic func {name}({args}) -> {ret} {{\n  {body}\n}}\n";
                desc = $"add_method {name} on {target}";
                break;
            }
            case "wrap_method":
            {
                var target = Str("targetClass");
                var name = Str("methodName");
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(name))
                    return Err("wrap_method : 'targetClass' et 'methodName' requis.");
                var args = Str("args") ?? "";
                var ret = Str("returnType") ?? "Void";
                var passthrough = ExtractArgNames(args);
                var body = ret == "Void"
                    ? $"  wrappedMethod({passthrough});\n  // TODO: extra logic"
                    : $"  let result = wrappedMethod({passthrough});\n  // TODO: post-process\n  return result;";
                reds = $"@wrapMethod({target})\npublic func {name}({args}) -> {ret} {{\n{body}\n}}\n";
                desc = $"wrap_method {name} on {target}";
                break;
            }
            case "replace_method":
            {
                var target = Str("targetClass");
                var name = Str("methodName");
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(name))
                    return Err("replace_method : 'targetClass' et 'methodName' requis.");
                var args = Str("args") ?? "";
                var ret = Str("returnType") ?? "Void";
                var body = Str("body") ?? "// TODO: full replacement";
                reds = $"@replaceMethod({target})\npublic func {name}({args}) -> {ret} {{\n  {body}\n}}\n";
                desc = $"replace_method {name} on {target}";
                break;
            }
            case "add_field":
            {
                var target = Str("targetClass");
                var name = Str("fieldName");
                var type = Str("fieldType") ?? "Int32";
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(name))
                    return Err("add_field : 'targetClass' et 'fieldName' requis.");
                reds = $"@addField({target})\nlet {name}: {type};\n";
                desc = $"add_field {name}: {type} on {target}";
                break;
            }
            case "new_class":
            {
                var name = Str("className");
                if (string.IsNullOrEmpty(name))
                    return Err("new_class : 'className' requis.");
                var extends = Str("extends");
                var modName = Str("moduleName");
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(modName))
                    sb.Append("module ").Append(modName).AppendLine().AppendLine();
                sb.Append("public class ").Append(name);
                if (!string.IsNullOrEmpty(extends)) sb.Append(" extends ").Append(extends);
                sb.AppendLine(" {");
                sb.AppendLine("  // TODO: fields and methods");
                sb.AppendLine("}");
                reds = sb.ToString();
                desc = $"new_class {name}" + (extends is not null ? $" extends {extends}" : "");
                break;
            }
            default:
                return Err($"Pattern inconnu : {pattern} " +
                           "(add_method, wrap_method, replace_method, add_field, new_class).");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        File.WriteAllText(outputFile, reds);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Template .reds : {desc} → {outputFile}",
            produced = new[] { outputFile },
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            pattern,
            outputFile,
            content = reds,
        }, JsonOpts);
    }

    [McpServerTool(Name = "generate_tweak_template")]
    [Description("Génère un fichier .tweak (TweakXL — YAML) prêt à éditer, depuis un " +
                 "catalogue de patterns courants. Évite de connaître la syntaxe TweakXL à la main. " +
                 "Patterns supportés : override_field (modifie un champ d'un record existant), " +
                 "new_record (crée un nouveau record via $instanceOf), boost_stat (modifie une " +
                 "stat numérique avec une nouvelle valeur).")]
    public static string GenerateTweakTemplate(
        [Description("Pattern : override_field | new_record | boost_stat.")] string pattern,
        [Description("Paramètres du template en JSON (clés selon le pattern, voir description).")] string parametersJson,
        [Description("Chemin du fichier .tweak à produire.")] string outputFile)
    {
        Dictionary<string, object?> p;
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Err("parametersJson doit être un objet JSON.");
            p = doc.RootElement.EnumerateObject().ToDictionary(
                k => k.Name,
                v => v.Value.ValueKind switch
                {
                    JsonValueKind.String => (object?)v.Value.GetString(),
                    JsonValueKind.Number => v.Value.TryGetInt64(out var l)
                        ? (object?)l : v.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => v.Value.GetRawText(),
                });
        }
        catch (JsonException ex)
        {
            return Err($"parametersJson invalide : {ex.Message}");
        }

        string? Str(string key)
            => p.TryGetValue(key, out var v) ? v?.ToString() : null;
        object? Val(string key)
            => p.TryGetValue(key, out var v) ? v : null;

        string yaml;
        string description;
        switch (pattern.ToLowerInvariant())
        {
            case "override_field":
            {
                var id = Str("recordId");
                var field = Str("field");
                var value = Val("value");
                if (string.IsNullOrEmpty(id))
                    return Err("override_field : 'recordId' requis (ex. Items.w_melee_001).");
                if (string.IsNullOrEmpty(field))
                    return Err("override_field : 'field' requis (ex. damage).");
                if (value is null)
                    return Err("override_field : 'value' requis.");
                yaml = $"{id}:\n  {field}: {FormatYamlValue(value)}\n";
                description = $"Override : {id}.{field} = {value}";
                break;
            }
            case "new_record":
            {
                var newId = Str("newId");
                var baseId = Str("baseId");
                if (string.IsNullOrEmpty(newId))
                    return Err("new_record : 'newId' requis (ex. MyMod.NewWeapon).");
                if (string.IsNullOrEmpty(baseId))
                    return Err("new_record : 'baseId' requis (record existant à instancier).");
                var sb = new StringBuilder();
                sb.Append(newId).AppendLine(":");
                sb.Append("  $instanceOf: ").AppendLine(baseId);
                if (p.TryGetValue("overrides", out var ov) && ov is string ovJson)
                {
                    // overrides en sous-JSON {field: value, ...}
                    try
                    {
                        using var ovDoc = JsonDocument.Parse(ovJson);
                        foreach (var prop in ovDoc.RootElement.EnumerateObject())
                            sb.Append("  ").Append(prop.Name).Append(": ")
                              .AppendLine(FormatYamlValue(prop.Value.ToString()));
                    }
                    catch { /* overrides mal formé : on continue sans */ }
                }
                yaml = sb.ToString();
                description = $"Nouveau record : {newId} <- $instanceOf {baseId}";
                break;
            }
            case "boost_stat":
            {
                var id = Str("recordId");
                var stat = Str("stat") ?? "damage";
                var value = Val("value");
                if (string.IsNullOrEmpty(id))
                    return Err("boost_stat : 'recordId' requis.");
                if (value is null)
                    return Err("boost_stat : 'value' requis (nouvelle valeur de la stat).");
                yaml = $"{id}:\n  {stat}: {FormatYamlValue(value)}\n";
                description = $"Boost stat : {id}.{stat} = {value}";
                break;
            }
            default:
                return Err($"Pattern inconnu : {pattern} " +
                           "(override_field, new_record, boost_stat).");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        File.WriteAllText(outputFile, yaml);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"{description} → {outputFile}",
            produced = new[] { outputFile },
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            pattern,
            outputFile,
            content = yaml,
        }, JsonOpts);
    }

    [McpServerTool(Name = "install_tweak")]
    [Description("Installe un fichier .tweak dans Cyberpunk 2077 : copie vers " +
                 "<jeu>/r6/tweaks/<nom>.tweak. Pris en compte au prochain lancement du jeu, " +
                 "sans rebuild ni redéploiement (TweakXL est chargé à chaud).")]
    public static string InstallTweak(
        [Description("Chemin du fichier .tweak à installer.")] string tweakFile,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath)
    {
        if (!File.Exists(tweakFile))
            return Err($"Fichier .tweak introuvable : {tweakFile}");
        if (!gamePath.Contains(Path.DirectorySeparatorChar)
            && !gamePath.Contains(Path.AltDirectorySeparatorChar))
            return Err("gamePath doit être un chemin de dossier (pas un nom simple).");
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");

        var tweaksDir = Path.Combine(gamePath, "r6", "tweaks");
        Directory.CreateDirectory(tweaksDir);
        var dest = Path.Combine(tweaksDir, Path.GetFileName(tweakFile));
        var existed = File.Exists(dest);
        File.Copy(tweakFile, dest, overwrite: true);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = (existed ? ".tweak réinstallé : " : ".tweak installé : ") + dest,
            produced = new[] { dest },
            warnings = existed
                ? new[] { "Un .tweak du même nom existait déjà ; il a été remplacé." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
            installedPath = dest,
        }, JsonOpts);
    }

    // ── Scripts REDscript (.reds) — lecture et lint textuel ─────────────

    [McpServerTool(Name = "read_script")]
    [Description("Lit un fichier script REDscript (.reds, .script, .swift, .redscript) et " +
                 "renvoie son contenu + sa structure extraite par regex : declarations func/" +
                 "class, annotations @addMethod/@addField/@wrapMethod, module/import. " +
                 "Analyse textuelle uniquement — pas de validation sémantique.")]
    public static async Task<string> ReadScript(
        [Description("Chemin d'un fichier script (.reds / .script / .swift / .redscript).")] string scriptFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(scriptFile))
            return Err($"Fichier script introuvable : {scriptFile}");
        var ext = Path.GetExtension(scriptFile).TrimStart('.').ToLowerInvariant();
        if (ext is not ("reds" or "script" or "swift" or "redscript"))
            return Err($"Extension non supportée : .{ext} (.reds, .script, .swift, .redscript).");

        var content = await File.ReadAllTextAsync(scriptFile, ct);
        var (declarations, moduleName) = ScanScriptDeclarations(content);
        var truncated = content.Length > ReadContentCap;

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Script lu : {scriptFile} ({content.Length} car., " +
                      $"{declarations.Count} déclaration(s))" +
                      (truncated ? " — contenu tronqué" : ""),
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            scriptFile,
            moduleName,
            declarations,
            truncated,
            content = truncated ? content[..ReadContentCap] : content,
        }, JsonOpts);
    }

    [McpServerTool(Name = "lint_script")]
    [Description("Analyse syntaxique d'un fichier REDscript via un vrai parser de grammaire " +
                 "(tokenizer + descente récursive) : (1) ERREURS de syntaxe avec ligne:colonne " +
                 "(signatures/types/génériques mal formés, accolades/parenthèses non appariées, " +
                 "chaînes non terminées, déclarations invalides), (2) AVERTISSEMENTS sémantiques " +
                 "(annotations @addMethod/@wrapMethod/@replaceMethod bien placées et ciblant une " +
                 "classe, @wrapMethod appelant wrappedMethod(), déclarations en double). " +
                 "Calibré à zéro faux positif sur le corpus REDscript réel. N'effectue PAS de " +
                 "vérification de types (résolution des types/méthodes externes = compilateur scc " +
                 "+ écosystème, hors périmètre).")]
    public static async Task<string> LintScript(
        [Description("Chemin d'un fichier script (.reds / .script / .swift / .redscript).")] string scriptFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(scriptFile))
            return Err($"Fichier script introuvable : {scriptFile}");
        var ext = Path.GetExtension(scriptFile).TrimStart('.').ToLowerInvariant();
        if (ext is not ("reds" or "script" or "swift" or "redscript"))
            return Err($"Extension non supportée : .{ext} (.reds, .script, .swift, .redscript).");

        var content = await File.ReadAllTextAsync(scriptFile, ct);
        var parse = RedscriptParser.Parse(content);
        var (decls, moduleName) = ScanScriptDeclarations(content);

        // Erreurs de syntaxe (parser) + avertissements sémantiques (heuristiques).
        var errors = parse.Diagnostics
            .Where(d => d.Severity == "ERROR")
            .Select(d => $"ERROR L{d.Line}:{d.Col} — {d.Message}")
            .ToList();
        var warnings = LintScriptSemantics(content);

        var status = errors.Count > 0 ? "error" : warnings.Count > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = status != "error",
            status,
            summary = $"Lint script : {scriptFile} — {decls.Count} déclaration(s), " +
                      $"{errors.Count} erreur(s), {warnings.Count} avertissement(s)",
            produced = Array.Empty<string>(),
            warnings,
            errors,
            scriptFile,
            moduleName = parse.Module ?? moduleName,
            declarations = decls,
            parsedDeclarations = parse.Declarations.Count,
            limitation = "Analyse syntaxique + sémantique légère : pas de vérification de types " +
                         "(résolution externe = compilateur scc + écosystème, hors périmètre).",
        }, JsonOpts);
    }

    // ── REDmod packaging (post-1.6) ──────────────────────────────────────

    [McpServerTool(Name = "create_redmod_project")]
    [Description("Crée un projet de mod au format REDmod (post-1.6) : structure " +
                 "mods/<nom>/info.json + sous-dossiers archives/, scripts/, tweaks/, " +
                 "customSounds/. Distinct du format .archive (archive/pc/mod/) — le " +
                 "format REDmod permet aussi scripts (.reds) et tweaks (.tweak).")]
    public static string CreateRedmodProject(
        [Description("Dossier parent où créer le projet REDmod.")] string parentFolder,
        [Description("Nom du REDmod (devient le sous-dossier).")] string modName,
        [Description("Description visible dans le launcher REDmod.")] string description = "",
        [Description("Version sémantique du mod.")] string version = "1.0.0")
    {
        if (!Directory.Exists(parentFolder))
            return Err($"Dossier parent introuvable : {parentFolder}");
        if (string.IsNullOrWhiteSpace(modName)
            || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err("Nom de REDmod invalide.");

        var root = Path.Combine(parentFolder, modName);
        if (Directory.Exists(root))
            return Err($"Le dossier existe déjà : {root}");

        string[] subdirs = { "archives", "scripts", "tweaks", "customSounds" };
        foreach (var s in subdirs)
            Directory.CreateDirectory(Path.Combine(root, s));

        var info = new
        {
            name = modName,
            version,
            description = string.IsNullOrWhiteSpace(description)
                ? $"REDmod {modName}" : description,
            customSounds = Array.Empty<object>(),
        };
        var infoPath = Path.Combine(root, "info.json");
        File.WriteAllText(infoPath, JsonSerializer.Serialize(info, JsonOpts));

        var produced = subdirs
            .Select(s => s + Path.DirectorySeparatorChar)
            .Append("info.json")
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Projet REDmod créé : {root}",
            produced,
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            redmodRoot = root,
        }, JsonOpts);
    }

    [McpServerTool(Name = "pack_redmod")]
    [Description("Empaquette un projet REDmod en .zip pour distribution. Le zip contient " +
                 "le dossier <nom>/ avec son info.json à la racine ; l'utilisateur final " +
                 "décompacte dans <jeu>/mods/. Valide la présence d'info.json avant zip.")]
    public static string PackRedmod(
        [Description("Dossier source du REDmod (contient info.json à sa racine).")] string modSourceFolder,
        [Description("Dossier de destination du .zip produit.")] string outputPath)
    {
        if (!Directory.Exists(modSourceFolder))
            return Err($"Dossier de REDmod introuvable : {modSourceFolder}");
        var info = Path.Combine(modSourceFolder, "info.json");
        if (!File.Exists(info))
            return Err($"info.json manquant à la racine : {info}");

        var modName = Path.GetFileName(modSourceFolder.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Directory.CreateDirectory(outputPath);
        var zipPath = Path.Combine(outputPath, $"{modName}.zip");

        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(modSourceFolder, zipPath,
            CompressionLevel.Optimal, includeBaseDirectory: true);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"REDmod empaqueté : {zipPath} " +
                      $"({new FileInfo(zipPath).Length / 1024} Kio)",
            produced = new[] { zipPath },
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            zipPath,
            modName,
        }, JsonOpts);
    }

    [McpServerTool(Name = "install_redmod")]
    [Description("Installe un projet REDmod dans Cyberpunk 2077 : copie récursive du dossier " +
                 "source vers <jeu>/mods/<nom>/. Le mod sera pris en compte au prochain " +
                 "lancement via le launcher REDmod (ou redMod.exe deploy).")]
    public static string InstallRedmod(
        [Description("Dossier source du REDmod (avec info.json à sa racine).")] string modSourceFolder,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath)
    {
        if (!Directory.Exists(modSourceFolder))
            return Err($"Dossier de REDmod introuvable : {modSourceFolder}");
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");
        var info = Path.Combine(modSourceFolder, "info.json");
        if (!File.Exists(info))
            return Err($"info.json manquant à la racine : {info}");

        var modName = Path.GetFileName(modSourceFolder.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dest = Path.Combine(gamePath, "mods", modName);
        var existed = Directory.Exists(dest);

        CopyDirectoryRecursive(modSourceFolder, dest);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = (existed ? "REDmod réinstallé : " : "REDmod installé : ") + dest,
            produced = new[] { dest },
            warnings = existed
                ? new[] { "Un REDmod du même nom existait déjà ; ses fichiers ont été remplacés." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
            installedPath = dest,
        }, JsonOpts);
    }

    [McpServerTool(Name = "backup_mods")]
    [Description("Sauvegarde l'état des mods installés d'une installation Cyberpunk 2077 dans " +
                 "un .zip horodaté : archive/pc/mod/ (mods .archive), mods/ (REDmods), " +
                 "r6/tweaks/ (.tweak). Filet de sécurité avant une session de modding. " +
                 "Le ZIP préserve les sous-dossiers relatifs au dossier du jeu.")]
    public static string BackupMods(
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath,
        [Description("Dossier où déposer le .zip produit.")] string outputDir,
        [Description("Nom du fichier ZIP (défaut : wkmcp-mods-backup-<YYYYMMDD-HHmmss>.zip).")] string? backupName = null)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");

        Directory.CreateDirectory(outputDir);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = backupName ?? $"wkmcp-mods-backup-{timestamp}.zip";
        var zipPath = Path.Combine(outputDir, fileName);

        var staged = Path.Combine(Path.GetTempPath(),
            "wkmcp-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        try
        {
            int archiveCount = 0, redmodCount = 0, tweakCount = 0;
            var warnings = new List<string>();

            var srcArchive = Path.Combine(gamePath, "archive", "pc", "mod");
            if (Directory.Exists(srcArchive))
            {
                var dst = Path.Combine(staged, "archive", "pc", "mod");
                CopyDirectoryRecursive(srcArchive, dst);
                archiveCount = Directory.EnumerateFiles(dst, "*.archive",
                    SearchOption.AllDirectories).Count();
            }
            else warnings.Add($"Dossier absent : {srcArchive}");

            var srcRedmod = Path.Combine(gamePath, "mods");
            if (Directory.Exists(srcRedmod))
            {
                var dst = Path.Combine(staged, "mods");
                CopyDirectoryRecursive(srcRedmod, dst);
                redmodCount = Directory.EnumerateDirectories(dst).Count();
            }
            else warnings.Add($"Dossier absent : {srcRedmod}");

            var srcTweak = Path.Combine(gamePath, "r6", "tweaks");
            if (Directory.Exists(srcTweak))
            {
                var dst = Path.Combine(staged, "r6", "tweaks");
                CopyDirectoryRecursive(srcTweak, dst);
                tweakCount = Directory.EnumerateFiles(dst, "*",
                    SearchOption.AllDirectories).Count();
            }
            else warnings.Add($"Dossier absent : {srcTweak}");

            if (archiveCount + redmodCount + tweakCount == 0)
                return Err($"Aucun mod à sauvegarder dans {gamePath}.");

            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(staged, zipPath,
                CompressionLevel.Optimal, includeBaseDirectory: false);
            var size = new FileInfo(zipPath).Length;

            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"Backup créé : {zipPath} ({size / 1024} Kio · " +
                          $"{archiveCount} archives + {redmodCount} REDmods + {tweakCount} tweaks)",
                produced = new[] { zipPath },
                warnings,
                errors = Array.Empty<string>(),
                zipPath,
                size,
                archiveCount,
                redmodCount,
                tweakCount,
            }, JsonOpts);
        }
        finally
        {
            try { Directory.Delete(staged, recursive: true); }
            catch { /* nettoyage best-effort */ }
        }
    }

    [McpServerTool(Name = "restore_mods")]
    [Description("Restaure un backup de mods produit par backup_mods. Mode `merge` (défaut) : " +
                 "extrait par-dessus l'existant sans supprimer. Mode `replace` : vide d'abord " +
                 "les dossiers cibles (archive/pc/mod, mods, r6/tweaks) puis extrait — " +
                 "destructeur, idéalement précédé d'un nouveau `backup_mods` de sécurité.")]
    public static string RestoreMods(
        [Description("Chemin du ZIP de backup à restaurer.")] string backupZip,
        [Description("Dossier racine de l'installation Cyberpunk 2077 cible.")] string gamePath,
        [Description("merge (défaut) | replace.")] string mode = "merge")
    {
        if (!File.Exists(backupZip))
            return Err($"Backup ZIP introuvable : {backupZip}");
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");
        if (mode is not ("merge" or "replace"))
            return Err($"Mode inconnu : {mode} (merge | replace).");

        var warnings = new List<string>();
        if (mode == "replace")
        {
            foreach (var sub in new[]
            {
                Path.Combine("archive", "pc", "mod"),
                "mods",
                Path.Combine("r6", "tweaks"),
            })
            {
                var p = Path.Combine(gamePath, sub);
                if (Directory.Exists(p))
                {
                    try { Directory.Delete(p, recursive: true); }
                    catch (Exception ex) { warnings.Add($"Échec suppression {p} : {ex.Message}"); }
                }
            }
        }

        var extractedCount = 0;
        // Garde-fou anti « Zip Slip » : un backupZip fourni par l'utilisateur n'est
        // pas fiable ; une entrée « ../../x » écrirait hors du dossier de jeu.
        var gameFull = Path.GetFullPath(gamePath) + Path.DirectorySeparatorChar;
        using (var zip = ZipFile.OpenRead(backupZip))
        {
            foreach (var entry in zip.Entries)
            {
                var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var dest = Path.Combine(gamePath, rel);
                var destFull = Path.GetFullPath(dest);
                if (!destFull.StartsWith(gameFull, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Entrée ignorée (hors du dossier de jeu) : {entry.FullName}");
                    continue;
                }
                // Dossier (entrée vide se terminant par /)
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destFull);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                entry.ExtractToFile(destFull, overwrite: true);
                extractedCount++;
            }
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Restore depuis {Path.GetFileName(backupZip)} → {gamePath} " +
                      $"(mode {mode}, {extractedCount} fichier(s) extrait(s))",
            produced = Array.Empty<string>(),
            warnings,
            errors = Array.Empty<string>(),
            mode,
            gamePath,
            extractedCount,
        }, JsonOpts);
    }

    [McpServerTool(Name = "lint_mod")]
    [Description("Vérifie un mod .archive avant installation : signale les extensions non " +
                 "reconnues par REDengine (que le jeu ignorerait silencieusement) et, si le " +
                 "dossier du jeu est fourni, détecte les conflits avec d'autres mods déjà " +
                 "installés (chemins internes communs). Filet de sécurité à appeler avant " +
                 "install_mod.")]
    public static async Task<string> LintMod(
        Cp77ToolsRunner runner,
        [Description("Chemin de l'archive .archive du mod à vérifier.")] string archivePath,
        [Description("Dossier racine de Cyberpunk 2077 (optionnel ; active la détection " +
                     "de conflits avec les mods déjà installés).")] string? gamePath = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            return Err($"Archive de mod introuvable : {archivePath}");
        if (!archivePath.EndsWith(".archive", StringComparison.OrdinalIgnoreCase))
            return Err($"Pas une archive .archive : {archivePath}");

        var (entries, fromCache, raw) = await runner.GetArchiveListingAsync(archivePath, ct);
        if (entries.Count == 0)
            return Err($"Listing vide pour {archivePath} (exit={raw.ExitCode})");

        var warnings = new List<string>();
        var errors = new List<string>();
        var unknownExtCount = 0;
        var conflictCount = 0;
        var conflicts = new List<object>();

        // 1. Extensions REDengine inconnues — le jeu les ignorera.
        foreach (var e in entries)
        {
            var ext = Path.GetExtension(e).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext))
            {
                warnings.Add($"Fichier sans extension : {e}");
                unknownExtCount++;
            }
            else if (!RedEngineExtensions.Contains(ext))
            {
                warnings.Add($"Extension non-REDengine ({ext}) : {e} — ignorée par le jeu");
                unknownExtCount++;
            }
        }

        // 2. Conflits avec mods installés (chemins internes communs).
        if (!string.IsNullOrWhiteSpace(gamePath) && Directory.Exists(gamePath))
        {
            var modsDir = Path.Combine(gamePath, "archive", "pc", "mod");
            if (Directory.Exists(modsDir))
            {
                var myFull = Path.GetFullPath(archivePath);
                var mySet = new HashSet<string>(entries, StringComparer.OrdinalIgnoreCase);
                foreach (var other in Directory.GetFiles(modsDir, "*.archive"))
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.Equals(Path.GetFullPath(other), myFull, StringComparison.OrdinalIgnoreCase))
                        continue; // pas se comparer à soi-même

                    var (otherEntries, _, _) = await runner.GetArchiveListingAsync(other, ct);
                    var shared = otherEntries
                        .Where(e => mySet.Contains(e))
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (shared.Count > 0)
                    {
                        conflictCount += shared.Count;
                        conflicts.Add(new
                        {
                            mod = Path.GetFileName(other),
                            sharedCount = shared.Count,
                            sharedFiles = shared.Take(20).ToList(),
                            truncated = shared.Count > 20,
                        });
                        warnings.Add(
                            $"Conflit avec {Path.GetFileName(other)} : {shared.Count} chemin(s) commun(s)");
                    }
                }
            }
            else
            {
                warnings.Add($"Dossier mod absent : {modsDir} (détection de conflits désactivée)");
            }
        }

        var ok = errors.Count == 0;
        var status = errors.Count > 0 ? "error"
                   : warnings.Count > 0 ? "partial"
                   : "success";

        return JsonSerializer.Serialize(new
        {
            ok,
            status,
            summary = $"Lint mod {Path.GetFileName(archivePath)} — {entries.Count} fichier(s), " +
                      $"{unknownExtCount} extension(s) inconnues, {conflictCount} conflit(s)" +
                      (fromCache ? " (cache)" : ""),
            produced = Array.Empty<string>(),
            warnings,
            errors,
            archivePath,
            fileCount = entries.Count,
            unknownExtCount,
            conflictCount,
            conflicts,
            fromCache,
            exitCode = 0,
            log = "",
        }, JsonOpts);
    }

    [McpServerTool(Name = "mod_summary")]
    [Description("Synthèse compacte de ce qu'un mod fait. Accepte un fichier .archive (résumé " +
                 "par extension de fichier) ou un dossier REDmod (parse info.json, énumère " +
                 "archives/, scripts/, tweaks/, customSounds/, extrait les clés top-level des " +
                 ".tweak et les déclarations des .reds). Évite de chaîner lint_mod + read_tweak + " +
                 "read_script à la main.")]
    public static async Task<string> ModSummary(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un .archive OU d'un dossier REDmod (avec info.json à la racine).")] string modPath,
        CancellationToken ct = default)
    {
        if (File.Exists(modPath) && modPath.EndsWith(".archive", StringComparison.OrdinalIgnoreCase))
            return await ModSummaryArchive(runner, modPath, ct);
        if (Directory.Exists(modPath))
            return await ModSummaryRedmod(modPath, ct);
        return Err($"Chemin invalide ou type inconnu : {modPath} (attendu : .archive ou dossier REDmod)");
    }

    private static async Task<string> ModSummaryArchive(
        Cp77ToolsRunner runner, string archive, CancellationToken ct)
    {
        var (entries, fromCache, _) = await runner.GetArchiveListingAsync(archive, ct);
        if (entries.Count == 0)
            return Err($"Listing vide pour : {archive}");

        var byExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nonRed = 0;
        foreach (var e in entries)
        {
            var ext = Path.GetExtension(e).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "(no-ext)";
            byExt[ext] = byExt.GetValueOrDefault(ext) + 1;
            if (!RedEngineExtensions.Contains(ext) && ext != "(no-ext)")
                nonRed++;
        }
        var top = byExt.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Mod (archive) : {Path.GetFileName(archive)} — " +
                      $"{entries.Count} fichier(s)" +
                      (nonRed > 0 ? $" ({nonRed} hors-REDengine)" : ""),
            produced = Array.Empty<string>(),
            warnings = nonRed > 0
                ? new[] { $"{nonRed} fichier(s) hors REDengine — ignoré(s) par le jeu." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
            kind = "archive",
            path = archive,
            fileCount = entries.Count,
            fileCountsByExtension = top,
            fromCache,
        }, JsonOpts);
    }

    private static async Task<string> ModSummaryRedmod(string folder, CancellationToken ct)
    {
        var infoPath = Path.Combine(folder, "info.json");
        if (!File.Exists(infoPath))
            return Err($"Pas un REDmod : info.json manquant à la racine de {folder}");

        string? name = null, version = null, description = null;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(infoPath, ct));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("name", out var n)) name = n.GetString();
                if (doc.RootElement.TryGetProperty("version", out var v)) version = v.GetString();
                if (doc.RootElement.TryGetProperty("description", out var d)) description = d.GetString();
            }
        }
        catch { /* info.json mal formé : on continue */ }

        var archives = ListRelative(folder, "archives", new[] { ".archive" });
        var scripts = ListRelative(folder, "scripts",
            new[] { ".reds", ".script", ".swift", ".redscript" });
        var tweaksFiles = ListRelative(folder, "tweaks",
            new[] { ".tweak", ".yaml", ".yml" });
        var customSounds = ListRelative(folder, "customSounds", Array.Empty<string>());

        // Extract top-level keys from each .tweak (lecture rapide ligne par ligne).
        var tweakKeys = new List<string>();
        foreach (var rel in tweaksFiles)
        {
            var full = Path.Combine(folder, rel);
            try
            {
                foreach (var rawLine in await File.ReadAllLinesAsync(full, ct))
                {
                    var line = rawLine.TrimEnd('\r');
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (char.IsWhiteSpace(line[0])) continue;
                    var idx = line.IndexOf(':');
                    if (idx > 0)
                    {
                        var key = line[..idx].Trim();
                        if (!string.IsNullOrEmpty(key)) tweakKeys.Add(key);
                    }
                }
            }
            catch { /* fichier illisible : on continue */ }
        }

        // Parse declarations dans chaque .reds.
        var scriptDecls = new List<object>();
        foreach (var rel in scripts)
        {
            var full = Path.Combine(folder, rel);
            try
            {
                var text = await File.ReadAllTextAsync(full, ct);
                var (decls, _) = ScanScriptDeclarations(text);
                foreach (var d in decls)
                    scriptDecls.Add(new { file = rel, kind = d.Kind, name = d.Name, line = d.Line });
            }
            catch { /* fichier illisible : on continue */ }
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"REDmod : {name ?? Path.GetFileName(folder)}" +
                      (version is not null ? $" v{version}" : "") +
                      $" — {archives.Count} archive(s), {scripts.Count} script(s), " +
                      $"{tweaksFiles.Count} tweak(s), {customSounds.Count} sound(s)",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            kind = "redmod",
            path = folder,
            name,
            version,
            description,
            fileCounts = new
            {
                archives = archives.Count,
                scripts = scripts.Count,
                tweaks = tweaksFiles.Count,
                customSounds = customSounds.Count,
            },
            archives,
            scripts,
            tweaks = tweaksFiles,
            customSounds,
            tweakKeys,
            scriptDeclarations = scriptDecls,
        }, JsonOpts);
    }

    [McpServerTool(Name = "dump_records")]
    [Description("Exporte tous les records TweakDB d'un type donné en JSON Lines (.jsonl) ou " +
                 "CSV — pour analyses de balance dans un tableur. Ex. recordType=" +
                 "\"gamedataWeaponItem_Record\" produit une table de toutes les armes avec " +
                 "leurs flats (damage, attacksPerSecond, etc.).")]
    public static async Task<string> DumpRecords(
        Cp77ToolsRunner runner,
        [Description("Chemin du fichier tweakdb.bin (typiquement <jeu>/r6/cache/tweakdb.bin).")] string tweakdbPath,
        [Description("Nom complet du type CLR de record (ex. gamedataWeaponItem_Record).")] string recordType,
        [Description("Chemin du fichier de sortie (.jsonl ou .csv selon format).")] string outputFile,
        [Description("Format : jsonl (défaut, 1 record par ligne) ou csv (superset de colonnes).")] string format = "jsonl",
        CancellationToken ct = default)
    {
        if (!File.Exists(tweakdbPath))
            return Err($"Fichier tweakdb.bin introuvable : {tweakdbPath}");
        if (string.IsNullOrWhiteSpace(recordType))
            return Err("recordType vide.");
        format = format.ToLowerInvariant();
        if (format is not ("jsonl" or "csv"))
            return Err($"Format inconnu : {format} (jsonl | csv).");

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        var r = await runner.RunAsync(
            new[] { "tweakdb-dump-records", tweakdbPath, recordType, outputFile, format }, ct);
        var produced = File.Exists(outputFile)
            ? new List<string> { outputFile }
            : new List<string>();
        return Structured(
            $"Dump records {recordType} → {outputFile} (format {format})", r, produced);
    }

    [McpServerTool(Name = "launch_game")]
    [Description("⚠ Lance Cyberpunk 2077 : exécute <jeu>/bin/x64/Cyberpunk2077.exe (action " +
                 "visible et difficile à annuler — le jeu démarre vraiment). Si " +
                 "deployRedmod=true (défaut), exécute d'abord redMod.exe deploy. " +
                 "Le jeu est lancé détaché ; cet outil ne bloque pas l'attente.")]
    public static async Task<string> LaunchGame(
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath,
        [Description("Lance redMod.exe deploy avant de démarrer le jeu (recommandé si REDmods/.tweak modifiés).")] bool deployRedmod = true,
        [Description("Arguments supplémentaires à passer à Cyberpunk2077.exe (rare).")] string? extraArgs = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");
        var exe = Path.Combine(gamePath, "bin", "x64", "Cyberpunk2077.exe");
        if (!File.Exists(exe))
            return Err($"Cyberpunk2077.exe introuvable : {exe}");

        string? deploySummary = null;
        var warnings = new List<string>
        {
            "Lancement effectif du jeu — action visible et difficile à annuler.",
        };
        if (deployRedmod)
        {
            var deployResult = await DeployRedmod(gamePath, ct);
            using var doc = JsonDocument.Parse(deployResult);
            var root = doc.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
            {
                // On signale mais on lance quand même — l'agent peut décider.
                warnings.Add("Deploy REDmod a échoué : "
                    + root.GetProperty("summary").GetString());
            }
            else
            {
                deploySummary = root.GetProperty("summary").GetString();
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        if (!string.IsNullOrWhiteSpace(extraArgs)) psi.Arguments = extraArgs;

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { return Err($"Échec du lancement : {ex.Message}"); }
        if (proc is null) return Err($"Process.Start a renvoyé null pour : {exe}");

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Cyberpunk 2077 lancé (PID {proc.Id})",
            produced = Array.Empty<string>(),
            warnings,
            errors = Array.Empty<string>(),
            gameExe = exe,
            pid = proc.Id,
            deploySummary,
        }, JsonOpts);
    }

    [McpServerTool(Name = "tail_game_logs")]
    [Description("Lit la queue des logs Cyberpunk 2077. log = game (r6/logs/*.log sauf redscript) | " +
                 "redmod (tools/redmod/logs/*.log) | redscript (r6/logs/*redscript*.log) | all. " +
                 "Renvoie les N dernières lignes après filtre optionnel (substring case-insensitive).")]
    public static string TailGameLogs(
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath,
        [Description("Catégorie de log : game | redmod | redscript | all.")] string log = "game",
        [Description("Nombre de lignes à renvoyer (défaut 200).")] int lines = 200,
        [Description("Filtre substring (case-insensitive) appliqué avant le tail.")] string? filter = null)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");
        if (lines < 1) lines = 200;

        var logFiles = ResolveLogFiles(gamePath, log);
        if (logFiles.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "partial",
                summary = $"Aucun log trouvé pour catégorie : {log}",
                produced = Array.Empty<string>(),
                warnings = new[] { $"Aucun fichier log dans la catégorie '{log}' " +
                                   $"(le jeu n'a peut-être jamais été lancé)." },
                errors = Array.Empty<string>(),
                logFiles = Array.Empty<string>(),
                lineCount = 0,
                content = "",
            }, JsonOpts);
        }

        var all = new List<string>();
        foreach (var f in logFiles)
        {
            all.Add($"=== {Path.GetFileName(f)} ===");
            try
            {
                foreach (var line in File.ReadLines(f))
                    all.Add(line);
            }
            catch (IOException ex)
            {
                all.Add($"(lecture impossible : {ex.Message})");
            }
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter;
            all = all.Where(l =>
                l.StartsWith("===", StringComparison.Ordinal) ||
                l.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var tail = all.Skip(Math.Max(0, all.Count - lines)).ToList();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Tail logs ({log}) — {tail.Count} ligne(s) sur {all.Count}",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            logFiles,
            lineCount = tail.Count,
            content = string.Join("\n", tail),
        }, JsonOpts);
    }

    [McpServerTool(Name = "uninstall_mod")]
    [Description("Désinstalle un mod : retire une archive .archive de <jeu>/archive/pc/mod/. " +
                 "Accepte un chemin absolu OU juste le nom du fichier (résolu côté jeu). " +
                 "Refuse de supprimer un fichier hors du dossier mod (garde-fou).")]
    public static string UninstallMod(
        [Description("Chemin absolu de la .archive OU juste son nom (ex. mymod.archive).")] string archivePathOrName,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");
        var modsDir = Path.Combine(gamePath, "archive", "pc", "mod");
        if (!Directory.Exists(modsDir))
            return Err($"Dossier mod absent : {modsDir}");

        var target = Path.IsPathRooted(archivePathOrName)
            ? archivePathOrName
            : Path.Combine(modsDir, archivePathOrName);
        if (!File.Exists(target))
            return Err($"Archive introuvable : {target}");
        // Garde-fou : la cible doit être DANS modsDir.
        var full = Path.GetFullPath(target);
        var modsDirFull = Path.GetFullPath(modsDir) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(modsDirFull, StringComparison.OrdinalIgnoreCase))
            return Err($"Refusé : {full} n'est pas sous {modsDir}.");

        File.Delete(target);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Mod désinstallé : {Path.GetFileName(target)}",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            removedPath = target,
        }, JsonOpts);
    }

    [McpServerTool(Name = "uninstall_redmod")]
    [Description("Désinstalle un REDmod : supprime récursivement <jeu>/mods/<modName>/. " +
                 "Garde-fou : refuse de supprimer hors du dossier mods/.")]
    public static string UninstallRedmod(
        [Description("Nom du REDmod (le sous-dossier sous mods/).")] string modName,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");
        if (string.IsNullOrWhiteSpace(modName)
            || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err($"Nom de REDmod invalide : {modName}");

        var modsRoot = Path.Combine(gamePath, "mods");
        if (!Directory.Exists(modsRoot))
            return Err($"Dossier REDmod absent : {modsRoot}");

        var dir = Path.Combine(modsRoot, modName);
        if (!Directory.Exists(dir))
            return Err($"REDmod introuvable : {dir}");
        var full = Path.GetFullPath(dir);
        var modsRootFull = Path.GetFullPath(modsRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(modsRootFull, StringComparison.OrdinalIgnoreCase))
            return Err($"Refusé : {full} n'est pas sous {modsRoot}.");

        Directory.Delete(dir, recursive: true);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"REDmod désinstallé : {modName}",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            removedPath = dir,
        }, JsonOpts);
    }

    [McpServerTool(Name = "uninstall_tweak")]
    [Description("Désinstalle un .tweak : supprime <jeu>/r6/tweaks/<tweakName>. " +
                 "Garde-fou : refuse de supprimer hors du dossier r6/tweaks/.")]
    public static string UninstallTweak(
        [Description("Nom du fichier .tweak (ex. mytweak.tweak).")] string tweakName,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");
        if (string.IsNullOrWhiteSpace(tweakName)
            || tweakName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err($"Nom de .tweak invalide : {tweakName}");

        var tweaksDir = Path.Combine(gamePath, "r6", "tweaks");
        if (!Directory.Exists(tweaksDir))
            return Err($"Dossier tweaks absent : {tweaksDir}");

        var target = Path.Combine(tweaksDir, tweakName);
        if (!File.Exists(target))
            return Err($"Tweak introuvable : {target}");
        var full = Path.GetFullPath(target);
        var tweaksDirFull = Path.GetFullPath(tweaksDir) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(tweaksDirFull, StringComparison.OrdinalIgnoreCase))
            return Err($"Refusé : {full} n'est pas sous {tweaksDir}.");

        File.Delete(target);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $".tweak désinstallé : {tweakName}",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            removedPath = target,
        }, JsonOpts);
    }

    [McpServerTool(Name = "deploy_redmod")]
    [Description("Exécute <jeu>/tools/redmod/bin/redMod.exe deploy — l'étape officielle pour " +
                 "activer les REDmods installés (compile leurs scripts + applique leurs " +
                 "tweaks). À lancer après install_redmod / install_tweak avant de jouer.")]
    public static async Task<string> DeployRedmod(
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");
        var exe = Path.Combine(gamePath, "tools", "redmod", "bin", "redMod.exe");
        if (!File.Exists(exe))
            return Err($"redMod.exe introuvable : {exe}. " +
                       "REDmod doit être installé via le launcher (Cyberpunk 2077 > REDmod DLC).");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.ArgumentList.Add("deploy");
        psi.ArgumentList.Add("-root=" + gamePath);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try { proc.Start(); }
        catch (Exception ex) { return Err($"Échec du lancement de redMod.exe : {ex.Message}"); }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* déjà mort */ }
            return Structured($"Deploy REDmod interrompu (>5 min) : {gamePath}",
                new CliResult(-1, stdout.ToString(), stderr.ToString(), true));
        }
        proc.WaitForExit();

        var r = new CliResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), false);
        return Structured($"Deploy REDmod : {gamePath} (exit={proc.ExitCode})", r);
    }

    [McpServerTool(Name = "install_mod")]
    [Description("Installe un mod : copie une archive .archive (produite par pack_archive) dans " +
                 "le dossier archive/pc/mod de l'installation Cyberpunk 2077 — dernière étape de " +
                 "la boucle de modding. Le mod est actif au prochain lancement du jeu.")]
    public static string InstallMod(
        [Description("Chemin de l'archive .archive du mod à installer.")] string archivePath,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath)
    {
        if (!File.Exists(archivePath))
            return Err($"Archive de mod introuvable : {archivePath}");
        if (!archivePath.EndsWith(".archive", StringComparison.OrdinalIgnoreCase))
            return Err($"Le fichier n'est pas une archive .archive : {archivePath}");
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");

        var modDir = Path.Combine(gamePath, "archive", "pc", "mod");
        Directory.CreateDirectory(modDir);
        var dest = Path.Combine(modDir, Path.GetFileName(archivePath));
        var existed = File.Exists(dest);
        File.Copy(archivePath, dest, overwrite: true);

        var result = new
        {
            ok = true,
            status = "success",
            summary = (existed ? "Mod réinstallé (archive remplacée) : " : "Mod installé : ") + dest,
            produced = new[] { dest },
            warnings = existed
                ? new[] { $"Une archive du même nom existait déjà et a été remplacée." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Plafond du contenu JSON renvoyé inline par read_game_file
    /// (au-delà, le contenu est tronqué et seul jsonFile donne le fichier complet).</summary>
    private const int ReadContentCap = 50_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Ensemble des fichiers présents (récursivement) sous un dossier.</summary>
    private static HashSet<string> Snapshot(string dir)
        => Directory.Exists(dir)
            ? new HashSet<string>(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            : new HashSet<string>();

    /// <summary>Fichiers apparus dans <paramref name="dir"/> depuis le snapshot
    /// <paramref name="before"/> — chemins relatifs à <paramref name="dir"/>, triés.</summary>
    private static List<string> ProducedIn(string dir, HashSet<string> before)
    {
        var after = Snapshot(dir);
        after.ExceptWith(before);
        return after.Select(p => Path.GetRelativePath(dir, p)).OrderBy(p => p).ToList();
    }

    /// <summary>Pattern « snapshot avant / op / diff après » utilisé par tous les
    /// outils qui écrivent des fichiers dans un dossier de sortie : crée le dossier,
    /// snapshot, lance l'opération, juge le succès sur les fichiers réellement
    /// produits (cf. <see cref="Structured"/>). Quand <paramref name="verbose"/>
    /// est <c>true</c>, le log renvoyé n'est pas tronqué.</summary>
    private static async Task<string> WithSnapshot(
        string outputPath, string summary, Func<Task<CliResult>> op,
        bool verbose = false)
    {
        Directory.CreateDirectory(outputPath);
        var before = Snapshot(outputPath);
        var r = await op();
        return Structured(summary, r, ProducedIn(outputPath, before), verbose);
    }

    private static readonly Regex TweakHeaderRegex = new(
        @"(?<r>\d+)(?<rp>\+?) record\(s\), (?<f>\d+)(?<fp>\+?) flat\(s\)",
        RegexOptions.Compiled);

    /// <summary>Glob simple : convertit <c>*</c>/<c>?</c> en regex, matche
    /// la chaîne entière (insensible à la casse). Suffisant pour les motifs de
    /// recherche de fichiers (ex. <c>*.mesh</c>, <c>*player*.ent</c>).</summary>
    internal static bool MatchesGlob(string path, string pattern)
    {
        var rx = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(path, rx, RegexOptions.IgnoreCase);
    }

    // ── Helpers d'inspection (parcours d'arbres JSON CR2W) ──────────────

    private sealed record MeshStats(
        int LodCount, int SubMeshCount, int MaterialCount, int BoneCount,
        List<string> MaterialNames, List<string> BoneNames);

    /// <summary>Extrait les agrégats d'un mesh CR2W sérialisé en JSON. Parcourt
    /// récursivement l'arbre en cherchant <c>renderLODs</c>, <c>chunkMaterials</c>,
    /// <c>materialEntries</c>, <c>boneNames</c> — sans dépendre du chemin exact.</summary>
    private static MeshStats ScanMeshStats(JsonElement root)
    {
        int lods = 0, subMeshes = 0, materials = 0, bones = 0;
        var matNames = new List<string>();
        var boneNames = new List<string>();

        WalkJson(root, (name, el) =>
        {
            switch (name)
            {
                case "renderLODs":
                    if (el.ValueKind == JsonValueKind.Array && lods == 0)
                        lods = el.GetArrayLength();
                    break;
                case "materialEntries":
                    if (el.ValueKind == JsonValueKind.Array && materials == 0)
                    {
                        materials = el.GetArrayLength();
                        foreach (var m in el.EnumerateArray())
                        {
                            var n = GetCNameValueAt(m, "name") ?? GetCNameValueAt(m, "Name");
                            if (n is not null) matNames.Add(n);
                        }
                    }
                    break;
                case "boneNames":
                    if (el.ValueKind == JsonValueKind.Array && bones == 0)
                    {
                        bones = el.GetArrayLength();
                        foreach (var b in el.EnumerateArray())
                        {
                            var s = ReadCName(b);
                            if (s is not null) boneNames.Add(s);
                        }
                    }
                    break;
                case "chunkMaterials":
                    if (el.ValueKind == JsonValueKind.Array)
                        subMeshes += el.GetArrayLength();
                    break;
            }
        });
        return new MeshStats(lods, subMeshes, materials, bones, matNames, boneNames);
    }

    private sealed record TextureProps(
        int Width, int Height, string? Format, string? Compression,
        int MipLevels, string? TextureGroup);

    /// <summary>Extrait les métadonnées d'une texture .xbm CR2W sérialisée en JSON.</summary>
    private static TextureProps ScanTextureProps(JsonElement root)
    {
        int w = 0, h = 0, mips = 0;
        string? format = null, compression = null, group = null;

        WalkJson(root, (name, el) =>
        {
            switch (name)
            {
                case "width":
                    if (el.ValueKind == JsonValueKind.Number && w == 0)
                        w = el.GetInt32();
                    break;
                case "height":
                    if (el.ValueKind == JsonValueKind.Number && h == 0)
                        h = el.GetInt32();
                    break;
                case "rawFormat":
                case "format":
                    if (format is null) format = ReadEnumString(el);
                    break;
                case "compression":
                    if (compression is null) compression = ReadEnumString(el);
                    break;
                case "mipMapInfo":
                    if (el.ValueKind == JsonValueKind.Array && mips == 0)
                        mips = el.GetArrayLength();
                    break;
                case "numMipLevels":
                case "mipLevels":
                    if (el.ValueKind == JsonValueKind.Number && mips == 0)
                        mips = el.GetInt32();
                    break;
                case "textureGroup":
                    if (group is null) group = ReadCName(el);
                    break;
            }
        });
        return new TextureProps(w, h, format, compression, mips, group);
    }

    /// <summary>Parcours récursif d'un arbre JSON ; <paramref name="visit"/> est
    /// appelé pour chaque (nom de propriété, valeur).</summary>
    private static void WalkJson(JsonElement el, Action<string, JsonElement> visit)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    visit(prop.Name, prop.Value);
                    WalkJson(prop.Value, visit);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    WalkJson(item, visit);
                break;
        }
    }

    /// <summary>Lit un CName WolvenKit, qui peut être sérialisé comme string brut,
    /// comme objet <c>{ "$value": "Name" }</c>, ou comme objet <c>{ "Value": "Name" }</c>.</summary>
    private static string? ReadCName(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty("$value", out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        if (el.TryGetProperty("Value", out var v2) && v2.ValueKind == JsonValueKind.String)
            return v2.GetString();
        return null;
    }

    private static string? GetCNameValueAt(JsonElement el, string property)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty(property, out var v)) return ReadCName(v);
        // Souvent encapsulé dans { "Data": { ...property... } }
        if (el.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(property, out var v2))
            return ReadCName(v2);
        return null;
    }

    /// <summary>Lit un enum sérialisé en string ou en objet WolvenKit (enum CNames).</summary>
    private static string? ReadEnumString(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        if (el.ValueKind == JsonValueKind.Number) return el.GetRawText();
        return ReadCName(el);
    }

    // ── Helpers script (lint + analyse textuelle .reds) ─────────────────

    internal sealed record ScriptDeclaration(string Kind, string Name, int Line);

    private static readonly Regex ScriptDeclRegex = new(
        @"^\s*(?:@(addMethod|addField|wrapMethod|replaceMethod)\([^)]*\)\s*)?" +
        @"(?:public|private|protected|static|native|final|abstract|persistent)?\s*" +
        @"(?<kind>func|class|struct|enum|module|import)\s+(?<name>[A-Za-z_][\w.<>]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    internal static (List<ScriptDeclaration> declarations, string? moduleName) ScanScriptDeclarations(string content)
    {
        var decls = new List<ScriptDeclaration>();
        string? moduleName = null;
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = ScriptDeclRegex.Match(lines[i]);
            if (!m.Success) continue;
            var kind = m.Groups["kind"].Value;
            var name = m.Groups["name"].Value;
            if (kind == "module" && moduleName is null)
                moduleName = name;
            decls.Add(new ScriptDeclaration(kind, name, i + 1));
        }
        return (decls, moduleName);
    }

    internal static List<string> LintScriptTextually(string content)
    {
        var issues = new List<string>();
        int braces = 0, parens = 0, brackets = 0;
        int braceLine = 0, parenLine = 0, bracketLine = 0;
        int line = 1;
        var inString = false;
        var inChar = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            var next = i + 1 < content.Length ? content[i + 1] : '\0';

            if (c == '\n')
            {
                line++;
                inLineComment = false;
                continue;
            }
            if (inLineComment) continue;
            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i++; }
                continue;
            }
            if (inString)
            {
                if (c == '\\' && next != '\0') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\' && next != '\0') { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }

            if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
            if (c == '"') { inString = true; continue; }
            if (c == '\'') { inChar = true; continue; }

            switch (c)
            {
                case '{': if (braces == 0) braceLine = line; braces++; break;
                case '}': braces--; if (braces < 0) { issues.Add($"ERROR L{line}: '}}' sans '{{' correspondant"); braces = 0; } break;
                case '(': if (parens == 0) parenLine = line; parens++; break;
                case ')': parens--; if (parens < 0) { issues.Add($"ERROR L{line}: ')' sans '(' correspondant"); parens = 0; } break;
                case '[': if (brackets == 0) bracketLine = line; brackets++; break;
                case ']': brackets--; if (brackets < 0) { issues.Add($"ERROR L{line}: ']' sans '[' correspondant"); brackets = 0; } break;
            }
        }

        if (braces > 0) issues.Add($"ERROR : {braces} accolade(s) '{{' non fermée(s) (ouverte vers L{braceLine})");
        if (parens > 0) issues.Add($"ERROR : {parens} parenthèse(s) '(' non fermée(s) (ouverte vers L{parenLine})");
        if (brackets > 0) issues.Add($"ERROR : {brackets} crochet(s) '[' non fermé(s) (ouvert vers L{bracketLine})");
        if (inString) issues.Add($"ERROR : guillemet \" non fermé (ligne {line})");
        if (inChar) issues.Add($"ERROR : guillemet ' non fermé (ligne {line})");
        if (inBlockComment) issues.Add($"ERROR : commentaire /* */ non fermé (ligne {line})");

        return issues;
    }

    private static readonly Regex AnnotationRegex = new(
        @"^\s*@(?<ann>addMethod|wrapMethod|replaceMethod|addField|replaceGlobal)\s*\((?<arg>[^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex FuncStartRegex = new(
        @"^\s*(?:public|private|protected|static|native|final|cb|exec)?\s*(?:func|let|const)\s",
        RegexOptions.Compiled);

    /// <summary>Analyse sémantique légère (textuelle mais consciente de REDscript) :
    /// annotations bien placées et ciblées, <c>@wrapMethod</c> appelant
    /// <c>wrappedMethod</c>, déclarations en double. Renvoie des avertissements
    /// préfixés "WARN" (jamais "ERROR" : ce n'est pas un parser complet).</summary>
    internal static List<string> LintScriptSemantics(string content)
    {
        var issues = new List<string>();
        var lines = content.Split('\n');

        // Doublons de déclarations de même nature.
        var (decls, _) = ScanScriptDeclarations(content);
        foreach (var g in decls
                     .Where(d => d.Kind is "func" or "class" or "struct" or "enum")
                     .GroupBy(d => (d.Kind, d.Name))
                     .Where(g => g.Count() > 1))
            issues.Add($"WARN : {g.Key.Kind} '{g.Key.Name}' déclaré {g.Count()} fois " +
                       $"(lignes {string.Join(", ", g.Select(x => x.Line))})");

        for (var i = 0; i < lines.Length; i++)
        {
            var m = AnnotationRegex.Match(lines[i]);
            if (!m.Success) continue;
            var ann = m.Groups["ann"].Value;
            var arg = m.Groups["arg"].Value.Trim();

            // L'annotation doit cibler une classe (sauf replaceGlobal).
            if (ann != "replaceGlobal" && arg.Length == 0)
                issues.Add($"WARN L{i + 1} : @{ann} sans classe cible — attendu @{ann}(NomDeClasse).");

            // La déclaration suivante (hors lignes vides / commentaires) doit être un func/field.
            var j = i + 1;
            while (j < lines.Length)
            {
                var t = lines[j].Trim();
                if (t.Length == 0 || t.StartsWith("//") || t.StartsWith("@")) { j++; continue; }
                break;
            }
            if (j >= lines.Length || !FuncStartRegex.IsMatch(lines[j]))
                issues.Add($"WARN L{i + 1} : @{ann} n'est pas suivie d'une déclaration func/let.");
            else if (ann == "wrapMethod")
            {
                // Le corps d'un @wrapMethod doit appeler wrappedMethod(...) sinon la
                // chaîne d'origine est rompue (erreur de modding très courante).
                var body = ExtractBraceBlock(content, lines, j);
                if (body is not null && !body.Contains("wrappedMethod"))
                    issues.Add($"WARN L{j + 1} : @wrapMethod n'appelle pas wrappedMethod() — " +
                               "la méthode d'origine ne sera jamais exécutée.");
            }
        }
        return issues;
    }

    /// <summary>Extrait le bloc { ... } qui suit la ligne <paramref name="declLine"/>
    /// (index 0-based dans <paramref name="lines"/>), par équilibrage d'accolades.
    /// Renvoie null si aucun bloc trouvé.</summary>
    private static string? ExtractBraceBlock(string content, string[] lines, int declLine)
    {
        // Position de caractère du début de la ligne de déclaration.
        var startChar = 0;
        for (var k = 0; k < declLine && k < lines.Length; k++)
            startChar += lines[k].Length + 1;
        var open = content.IndexOf('{', Math.Min(startChar, content.Length));
        if (open < 0) return null;
        int depth = 0;
        for (var p = open; p < content.Length; p++)
        {
            if (content[p] == '{') depth++;
            else if (content[p] == '}') { depth--; if (depth == 0) return content[open..(p + 1)]; }
        }
        return null;
    }

    /// <summary>Liste les fichiers d'un sous-dossier d'un REDmod, filtrant par
    /// extensions (ou tout si vide), en chemins relatifs au dossier racine.</summary>
    private static List<string> ListRelative(string root, string subdir, string[] extensions)
    {
        var dir = Path.Combine(root, subdir);
        if (!Directory.Exists(dir)) return new List<string>();
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);
        if (extensions.Length > 0)
        {
            var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
            files = files.Where(f => extSet.Contains(Path.GetExtension(f)));
        }
        return files
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Résout les chemins de log selon la catégorie demandée par
    /// <c>tail_game_logs</c>. Renvoie une liste possiblement vide si le jeu
    /// n'a jamais tourné ou si REDmod n'est pas installé.</summary>
    private static List<string> ResolveLogFiles(string gamePath, string category)
    {
        var paths = new List<string>();
        var r6logs = Path.Combine(gamePath, "r6", "logs");
        var redmodLogs = Path.Combine(gamePath, "tools", "redmod", "logs");

        switch (category.ToLowerInvariant())
        {
            case "game":
                if (Directory.Exists(r6logs))
                    foreach (var f in Directory.GetFiles(r6logs, "*.log"))
                        if (!Path.GetFileName(f).Contains("redscript", StringComparison.OrdinalIgnoreCase))
                            paths.Add(f);
                break;
            case "redmod":
                if (Directory.Exists(redmodLogs))
                    paths.AddRange(Directory.GetFiles(redmodLogs, "*.log"));
                break;
            case "redscript":
                if (Directory.Exists(r6logs))
                    foreach (var f in Directory.GetFiles(r6logs, "*.log"))
                        if (Path.GetFileName(f).Contains("redscript", StringComparison.OrdinalIgnoreCase))
                            paths.Add(f);
                break;
            case "all":
                if (Directory.Exists(r6logs))
                    paths.AddRange(Directory.GetFiles(r6logs, "*.log"));
                if (Directory.Exists(redmodLogs))
                    paths.AddRange(Directory.GetFiles(redmodLogs, "*.log"));
                break;
        }
        return paths;
    }

    /// <summary>Formate une valeur quelconque en scalaire YAML correct : entiers
    /// et flottants nus, chaînes encadrées de doubles quotes si non triviales,
    /// booléens en true/false.</summary>
    private static string FormatYamlValue(object? value)
    {
        if (value is null) return "null";
        if (value is bool b) return b ? "true" : "false";
        if (value is long or int or double or float) return value.ToString()!;
        var s = value.ToString() ?? "";
        // Nombres parsables → écrits nus.
        if (long.TryParse(s, out _) || double.TryParse(s,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _))
            return s;
        if (s is "true" or "false" or "null") return s;
        // Chaîne quotée pour éviter qu'un ":" ou un "#" perturbe le parseur YAML.
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>Copie récursive d'un dossier (utilisée par install_redmod). Crée
    /// les sous-dossiers manquants et écrase les fichiers existants à destination.</summary>
    private static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    /// <summary>Liste canonique des extensions REDengine acceptées par Cyberpunk 2077,
    /// extraite de <c>WolvenKit.RED4.Archive.ERedExtension</c> (137 valeurs, hors
    /// « unknown »). Une archive contenant un fichier d'extension non listée verra
    /// ce fichier ignoré silencieusement par le moteur.</summary>
    private static readonly HashSet<string> RedEngineExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "acousticdata", "actionanimdb", "aiarch", "animgraph", "anims", "app", "archetypes", "areas",
        "audio_metadata", "audiovehcurveset", "behavior", "bikecurveset", "bk2", "bnk", "camcurveset", "ccstate",
        "cfoliage", "charcustpreset", "chromaset", "cminimap", "community", "conversations", "cooked_mlsetup", "cookedanims",
        "cookedapp", "cookedprefab", "credits", "csv", "cubemap", "curveresset", "curveset", "dat",
        "devices", "dlc_manifest", "dtex", "effect", "ent", "env", "envparam", "envprobe",
        "es", "facialcustom", "facialsetup", "fb2tl", "fnt", "folbrush", "foldest", "fp",
        "game", "gamedef", "garmentlayerparams", "genericanimdb", "geometry_cache", "gidata", "gradient", "hitrepresentation",
        "hp", "ies", "inkanim", "inkatlas", "inkcharcustomization", "inkenginesettings", "inkfontfamily", "inkfullscreencomposition",
        "inkgamesettings", "inkhud", "inklayers", "inkmenu", "inkshapecollection", "inkstyle", "inktypography", "inkwidget",
        "interaction", "journal", "journaldesc", "json", "lane_connections", "lane_polygons", "lane_spots", "lights",
        "lipmap", "location", "locopaths", "loot", "mappins", "matlib", "mesh", "mi",
        "mlmask", "mlsetup", "mltemplate", "morphtarget", "mt", "null_areas", "opusinfo", "opuspak",
        "particle", "phys", "physicalscene", "physmatlib", "poimappins", "psrep", "quest", "questphase",
        "redphysics", "regionset", "remt", "reps", "reslist", "rig", "scene", "scenerid",
        "scenesversions", "smartobject", "smartobjects", "sp", "spatial_representation", "streamingblock", "streamingquerydata", "streamingsector",
        "streamingsector_inplace", "streamingworld", "terrainsetup", "texarray", "traffic_collisions", "traffic_persistent", "vehcommoncurveset", "vehcurveset",
        "voicetags", "w2mesh", "w2mi", "wdyn", "wem", "workspot", "worldlist", "xbm",
        "xcube",
    };

    /// <summary>Repère dans le log du daemon l'en-tête « N(+)? record(s), M(+)? flat(s) »
    /// — le « + » indique que le cap (100) a été atteint et qu'il y avait davantage.</summary>
    private static (bool records, bool flats) ParseTweakHeader(string log)
    {
        foreach (var line in log.Split('\n'))
        {
            var m = TweakHeaderRegex.Match(line);
            if (m.Success)
                return (m.Groups["rp"].Value == "+", m.Groups["fp"].Value == "+");
        }
        return (false, false);
    }

    /// <summary>
    /// Met en forme le résultat d'un appel cp77tools en objet JSON structuré.
    ///
    /// <paramref name="produced"/> : pour un outil qui écrit des fichiers, la liste
    /// de ceux qu'il a produits (le signal de succès fiable) ; <c>null</c> pour un
    /// outil d'information (succès jugé sur le code de sortie).
    ///
    /// <paramref name="verbose"/> : si <c>true</c>, le log n'est pas tronqué (pour
    /// le debug — sortie potentiellement très volumineuse).
    /// </summary>
    private static string Structured(string summary, CliResult r,
                                     IReadOnlyList<string>? produced = null,
                                     bool verbose = false)
    {
        var log = (r.Stdout + r.Stderr).Trim();
        var warnings = LogLines(log, "Warning");
        var errors = LogLines(log, "Error");

        string status;
        if (r.TimedOut)
            status = "timeout";
        else if (produced is not null)
            // Outil producteur de fichiers : le succès se juge sur la sortie réelle.
            status = produced.Count > 0 ? (errors.Count > 0 ? "partial" : "success") : "error";
        else
            // Outil d'information : pas de fichier attendu, on se fie au code de sortie.
            status = r.ExitCode != 0
                     || log.Contains("Erreur daemon", StringComparison.Ordinal)
                     || log.Contains("Unhandled", StringComparison.OrdinalIgnoreCase)
                ? "error" : "success";

        var result = new
        {
            ok = status is "success" or "partial",
            status,
            summary,
            produced = produced ?? (IReadOnlyList<string>)Array.Empty<string>(),
            warnings,
            errors,
            exitCode = r.ExitCode,
            log = verbose ? log : Truncate(log, 12_000),
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    /// <summary>Résultat JSON d'échec — pour les erreurs de validation des arguments.</summary>
    private static string Err(string summary)
        => JsonSerializer.Serialize(new
        {
            ok = false,
            status = "error",
            summary,
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = new[] { summary },
            exitCode = -1,
            log = "",
        }, JsonOpts);

    /// <summary>Extrait les messages des lignes de log d'un niveau donné
    /// (« [ 0: Warning ] - message » → « message »).</summary>
    private static List<string> LogLines(string log, string level)
    {
        var marker = $": {level} ]";
        var lines = new List<string>();
        foreach (var raw in log.Split('\n'))
        {
            if (!raw.Contains(marker, StringComparison.Ordinal))
                continue;
            var i = raw.IndexOf("] - ", StringComparison.Ordinal);
            lines.Add((i >= 0 ? raw[(i + 4)..] : raw).Trim());
        }
        return lines;
    }

    /// <summary>Tronque un log volumineux en préservant le contexte utile :
    /// les 20 premières lignes (qui contextualisent), les lignes d'erreur du
    /// milieu (l'info la plus précieuse), et les 20 dernières lignes (résultat
    /// final). Si encore trop gros, fallback sur troncature en début de chaîne.</summary>
    internal static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;

        var lines = s.Split('\n');
        // Trop court pour un découpage en sections : fallback simple.
        if (lines.Length <= 40)
            return "…(début de sortie tronqué)…\n" + s[^max..];

        const int head = 20, tail = 20;
        var midErrors = new List<string>();
        for (var i = head; i < lines.Length - tail; i++)
        {
            if (lines[i].Contains(": Error ]", StringComparison.Ordinal))
                midErrors.Add(lines[i]);
        }

        var sb = new StringBuilder();
        for (var i = 0; i < head; i++) sb.AppendLine(lines[i].TrimEnd('\r'));
        var omitted = lines.Length - head - tail;
        sb.Append("…(milieu : ").Append(omitted).Append(" ligne(s) omises");
        if (midErrors.Count > 0) sb.Append(", ").Append(midErrors.Count).Append(" erreur(s) préservée(s)");
        sb.AppendLine(")…");
        foreach (var e in midErrors) sb.AppendLine(e.TrimEnd('\r'));
        if (midErrors.Count > 0) sb.AppendLine("…");
        for (var i = lines.Length - tail; i < lines.Length; i++) sb.AppendLine(lines[i].TrimEnd('\r'));

        var result = sb.ToString().TrimEnd();
        return result.Length <= max ? result : "…(début de sortie tronqué)…\n" + result[^max..];
    }
}
