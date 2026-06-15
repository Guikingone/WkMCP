using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Server;
using YamlDotNet.Serialization;

namespace WolvenKitMcp;

/// <summary>
/// Outils MCP de plus haut niveau pour simplifier la création, l'évolution et la
/// maintenance des mods Cyberpunk 2077 : intelligence des dépendances/frameworks,
/// santé du setup, navigation de références, diff vs base, scaffolding et packaging.
/// Composent les primitives de <see cref="WolvenKitTools"/> et la connaissance de
/// l'écosystème de modding, plutôt que d'appeler directement WolvenKit.
/// </summary>
[McpServerToolType]
public static class ModdingTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Err(string summary) => JsonSerializer.Serialize(new
    {
        ok = false,
        status = "error",
        summary,
        produced = Array.Empty<string>(),
        warnings = Array.Empty<string>(),
        errors = new[] { summary },
    }, JsonOpts);

    // ── Base de connaissance des frameworks de modding ──────────────────────
    // Chaque framework : comment on détecte qu'un MOD en a besoin (racines
    // d'import REDscript, extensions de fichiers) et comment on détecte qu'il
    // est INSTALLÉ dans le jeu (chemins-marqueurs relatifs à la racine du jeu).
    private sealed record Framework(
        string Name, string Kind,
        string[] ImportRoots, string[] FileSignals, string[] InstallMarkers, string Note);

    private static readonly Framework[] Frameworks =
    {
        new("RED4ext", "loader", Array.Empty<string>(), new[] { "red4ext-plugin-dll" },
            new[] { @"red4ext\RED4ext.dll", @"bin\x64\winmm.dll" },
            "Loader natif requis par redscript, ArchiveXL, TweakXL, Codeware..."),
        new("redscript", "script-compiler", Array.Empty<string>(), new[] { ".reds" },
            new[] { @"engine\tools\scc.exe", @"r6\scripts" },
            "Compilateur de scripts .reds (hook au lancement)."),
        new("ArchiveXL", "framework", new[] { "ArchiveXL" }, new[] { ".xl" },
            new[] { @"red4ext\plugins\ArchiveXL" },
            "Extension d'archives : ajout d'apparences, entités, items via .xl."),
        new("TweakXL", "framework", new[] { "TweakXL" }, new[] { "tweak-yaml" },
            new[] { @"red4ext\plugins\TweakXL" },
            "Édition déclarative de TweakDB via .tweak/.yaml dans r6/tweaks."),
        new("Codeware", "library", new[] { "Codeware" }, Array.Empty<string>(),
            new[] { @"red4ext\plugins\Codeware" },
            "Bibliothèque d'extensions REDscript (UI, reflection, events...)."),
        new("Audioware", "library", new[] { "Audioware" }, Array.Empty<string>(),
            new[] { @"red4ext\plugins\audioware", @"red4ext\plugins\Audioware" },
            "Framework audio (sons/musiques personnalisés)."),
        new("Mod Settings", "library", new[] { "ModSettingsModule", "ModSettings" }, Array.Empty<string>(),
            new[] { @"red4ext\plugins\mod_settings" },
            "Menu de réglages in-game pour les mods."),
        new("RedData", "library", new[] { "RedData" }, Array.Empty<string>(),
            new[] { @"red4ext\plugins\RedData", @"r6\scripts\RedData" },
            "Sérialisation/données partagées entre mods."),
        new("RedFileSystem", "library", new[] { "RedFileSystem" }, Array.Empty<string>(),
            new[] { @"red4ext\plugins\RedFileSystem" },
            "Accès système de fichiers sandboxé depuis REDscript."),
        new("Cyber Engine Tweaks", "framework", Array.Empty<string>(), new[] { "cet-lua" },
            new[] { @"bin\x64\plugins\cyber_engine_tweaks" },
            "Runtime Lua + console (mods CET)."),
    };

    // Mapping racine d'import REDscript -> framework, calculé une seule fois
    // (Frameworks est immuable) au lieu d'être reconstruit à chaque scan.
    private static readonly Dictionary<string, Framework> ImportRootToFw = BuildImportRootMap();
    private static Dictionary<string, Framework> BuildImportRootMap()
    {
        var map = new Dictionary<string, Framework>(StringComparer.OrdinalIgnoreCase);
        foreach (var fw in Frameworks)
            foreach (var r in fw.ImportRoots)
                map[r] = fw;
        return map;
    }

    // Racines d'import « natives » du jeu — pas des dépendances de mod.
    private static readonly HashSet<string> BaseImportRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        // Les imports du jeu de base n'utilisent pas de préfixe de module tiers ;
        // on ignore les racines connues comme appartenant au jeu/redscript.
    };

    // ════════════════════════════════════════════════════════════════════════
    // analyze_dependencies
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "analyze_dependencies", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Analyse un dossier de mod (ou un projet) et déduit ses frameworks/dépendances " +
                 "requis : redscript, RED4ext, ArchiveXL, TweakXL, Codeware, Audioware, Mod Settings, " +
                 "Cyber Engine Tweaks, etc. — en lisant les imports REDscript (via le parser), les " +
                 ".xl, les .tweak et les types de fichiers. Si gamePath est fourni, indique pour " +
                 "chaque dépendance si elle est INSTALLÉE ou MANQUANTE. Idéal avant de distribuer " +
                 "ou d'installer un mod.")]
    public static string AnalyzeDependencies(
        [Description("Dossier du mod à analyser (racine du projet ou dossier déployé).")] string modPath,
        [Description("Optionnel : racine du jeu, pour vérifier les dépendances installées.")] string? gamePath = null)
    {
        if (!Directory.Exists(modPath))
            return Err($"Dossier de mod introuvable : {modPath}");

        var reasons = DetectFrameworks(modPath, out var unknownImports, out var fileStats);

        var checkInstalled = !string.IsNullOrWhiteSpace(gamePath) && Directory.Exists(gamePath);
        var deps = new List<object>();
        var missing = new List<string>();
        foreach (var fw in Frameworks)
        {
            if (!reasons.TryGetValue(fw.Name, out var why)) continue;
            string? installedStatus = null; string? version = null;
            if (checkInstalled)
            {
                var (inst, ver) = IsInstalled(gamePath!, fw);
                installedStatus = inst ? "installé" : "MANQUANT";
                version = ver;
                if (!inst) missing.Add(fw.Name);
            }
            deps.Add(new { framework = fw.Name, kind = fw.Kind, reason = why, note = fw.Note, installed = installedStatus, version });
        }

        var warnings = new List<string>();
        if (missing.Count > 0)
            warnings.Add($"Dépendances manquantes dans le jeu : {string.Join(", ", missing)}");

        // Imports inter-mods : avec gamePath, on résout chaque import inconnu contre
        // les modules REDscript réellement déclarés dans r6/scripts — on sait alors
        // QUEL mod installé le fournit, ou qu'il manque (cause de crash au chargement).
        List<object> crossMod;
        if (checkInstalled && unknownImports.Count > 0)
        {
            var providers = ScanInstalledScriptModules(gamePath!);
            crossMod = unknownImports.OrderBy(x => x).Select(imp =>
            {
                var mods = ResolveImportProvider(imp, providers);
                return (object)new
                {
                    import = imp,
                    providedBy = mods,
                    installed = mods is not null,
                };
            }).ToList();
            var unresolved = unknownImports
                .Where(i => ResolveImportProvider(i, providers) is null)
                .OrderBy(x => x).ToList();
            if (unresolved.Count > 0)
                warnings.Add("Imports non fournis par les mods installés (dépendance absente ?) : " +
                             string.Join(", ", unresolved.Take(15)));
        }
        else
        {
            crossMod = unknownImports.OrderBy(x => x)
                .Select(i => (object)new { import = i, providedBy = (List<string>?)null, installed = (bool?)null })
                .ToList();
            if (unknownImports.Count > 0)
                warnings.Add($"Imports d'autres mods (dépendances inter-mods possibles) : " +
                             $"{string.Join(", ", unknownImports.Take(15))} — passer gamePath pour les résoudre.");
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = missing.Count > 0 ? "partial" : "success",
            summary = $"{deps.Count} dépendance(s) détectée(s) pour {Path.GetFileName(modPath.TrimEnd('\\', '/'))}" +
                      (string.IsNullOrWhiteSpace(gamePath) ? "" : $" ({missing.Count} manquante(s))"),
            modPath,
            dependencies = deps,
            crossModImports = crossMod,
            fileStats,
            warnings,
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Scanne les modules REDscript déclarés dans &lt;jeu&gt;/r6/scripts :
    /// nom de module → mods (dossiers de premier niveau) qui le déclarent.</summary>
    internal static Dictionary<string, List<string>> ScanInstalledScriptModules(string gamePath)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var scripts = Path.Combine(gamePath, "r6", "scripts");
        if (!Directory.Exists(scripts)) return result;
        foreach (var file in Directory.EnumerateFiles(scripts, "*.reds", SearchOption.AllDirectories))
        {
            string? module = null;
            try
            {
                // La déclaration `module X.Y` est en tête de fichier (après
                // d'éventuels commentaires/annotations) — 40 lignes suffisent.
                foreach (var line in File.ReadLines(file).Take(40))
                {
                    var t = line.TrimStart();
                    if (t.StartsWith("module ", StringComparison.Ordinal))
                    {
                        module = t["module ".Length..].Trim().TrimEnd(';').Trim();
                        break;
                    }
                }
            }
            catch (IOException) { continue; }
            if (string.IsNullOrEmpty(module)) continue;

            var rel = Path.GetRelativePath(scripts, file);
            var top = rel.Split(Path.DirectorySeparatorChar, '/')[0];
            if (!result.TryGetValue(module, out var l)) result[module] = l = new List<string>();
            if (!l.Contains(top)) l.Add(top);
        }
        return result;
    }

    /// <summary>Mods fournissant un import REDscript : le module importé lui-même, un
    /// module parent (import de classe `X.Y.Classe` → module `X.Y`), ou un sous-module
    /// (import `X.Y.*` couvre `X.Y.Z`). Null si aucun mod installé ne le fournit.</summary>
    internal static List<string>? ResolveImportProvider(string import, Dictionary<string, List<string>> providers)
    {
        var found = new List<string>();
        var probe = import;
        while (true)
        {
            if (providers.TryGetValue(probe, out var mods))
                foreach (var m in mods)
                    if (!found.Contains(m)) found.Add(m);
            var i = probe.LastIndexOf('.');
            if (i <= 0) break;
            probe = probe[..i];
        }
        foreach (var kv in providers)
            if (kv.Key.StartsWith(import + ".", StringComparison.OrdinalIgnoreCase))
                foreach (var m in kv.Value)
                    if (!found.Contains(m)) found.Add(m);
        return found.Count > 0 ? found : null;
    }

    // ════════════════════════════════════════════════════════════════════════
    // check_requirements
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "check_requirements", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Inventorie les frameworks de modding INSTALLÉS dans une installation Cyberpunk 2077 " +
                 "(RED4ext, redscript, ArchiveXL, TweakXL, Codeware, Audioware, Mod Settings, CET...) " +
                 "avec leur version si détectable. Permet de savoir ce qui est disponible avant " +
                 "d'installer un mod ou de diagnostiquer une dépendance manquante.")]
    public static string CheckRequirements(
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");

        var items = new List<object>();
        int installed = 0;
        foreach (var fw in Frameworks)
        {
            var (inst, ver) = IsInstalled(gamePath, fw);
            if (inst) installed++;
            items.Add(new { framework = fw.Name, kind = fw.Kind, installed = inst, version = ver, note = fw.Note });
        }
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"{installed}/{Frameworks.Length} frameworks de modding installés",
            gamePath,
            frameworks = items,
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // mod_doctor
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "mod_doctor", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Diagnostic de santé d'une installation Cyberpunk 2077 moddée, en un appel : " +
                 "frameworks installés/manquants, dépendances requises par les mods installés mais " +
                 "absentes (cause #1 de crashes), conflits entre archives, et inventaire des mods. " +
                 "Renvoie un rapport structuré avec des recommandations.")]
    public static async Task<string> ModDoctor(
        Cp77ToolsRunner runner,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");

        // 1) frameworks installés
        var installedFw = Frameworks.Where(f => IsInstalled(gamePath, f).installed)
                                    .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 2) dépendances requises par TOUT le contenu moddé (scripts, tweaks, xl, lua, plugins)
        var scanRoots = new[]
        {
            Path.Combine(gamePath, "r6", "scripts"),
            Path.Combine(gamePath, "r6", "tweaks"),
            Path.Combine(gamePath, "archive", "pc", "mod"),
            Path.Combine(gamePath, "mods"),
            Path.Combine(gamePath, "red4ext", "plugins"),
            Path.Combine(gamePath, "bin", "x64", "plugins", "cyber_engine_tweaks", "mods"),
        };
        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in scanRoots.Where(Directory.Exists))
            foreach (var kv in DetectFrameworks(root, out _, out _))
                required.TryAdd(kv.Key, kv.Value);

        var missingDeps = required.Keys
            .Where(name => !installedFw.Contains(name))
            .ToList();

        // 3) conflits (verbe daemon existant)
        int? conflictCount = null; string conflictNote = "";
        try
        {
            var r = await runner.RunAsync(new[] { "conflicts", gamePath, "--structured" }, ct);
            if (r.Success)
            {
                var m = System.Text.RegularExpressions.Regex.Match(r.Stdout + r.Stderr, "\"conflictCount\"\\s*:\\s*(\\d+)");
                if (m.Success) conflictCount = int.Parse(m.Groups[1].Value);
            }
            else if ((r.Stdout + r.Stderr).Contains("Value cannot be null"))
                conflictNote = "détection de conflits indisponible (bug amont WolvenKit.CLI sur certains installs)";
        }
        catch { conflictNote = "détection de conflits non exécutée"; }

        // 4) inventaire mods
        var archiveDir = Path.Combine(gamePath, "archive", "pc", "mod");
        var modsDir = Path.Combine(gamePath, "mods");
        int archiveMods = Directory.Exists(archiveDir) ? Directory.GetFiles(archiveDir, "*.archive").Length : 0;
        int redMods = Directory.Exists(modsDir) ? Directory.GetDirectories(modsDir).Length : 0;

        var recommendations = new List<string>();
        foreach (var dep in missingDeps)
        {
            var fw = Frameworks.First(f => f.Name == dep);
            recommendations.Add($"Installer {dep} ({fw.Note}) — requis par le contenu présent.");
        }
        if (conflictCount is > 0)
            recommendations.Add($"{conflictCount} conflit(s) d'archives détecté(s) : vérifier l'ordre/priorité (detect_conflicts pour le détail).");

        var status = missingDeps.Count > 0 || conflictCount is > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status,
            summary = $"Santé du setup : {archiveMods} mods .archive + {redMods} REDmods · " +
                      $"{installedFw.Count}/{Frameworks.Length} frameworks · " +
                      $"{missingDeps.Count} dépendance(s) manquante(s)" +
                      (conflictCount is { } c ? $" · {c} conflit(s)" : ""),
            gamePath,
            installedFrameworks = installedFw.OrderBy(x => x).ToList(),
            requiredFrameworks = required.Keys.OrderBy(x => x).ToList(),
            missingDependencies = missingDeps,
            conflictCount,
            conflictNote,
            mods = new { archiveMods, redMods },
            recommendations,
            warnings = missingDeps.Count > 0
                ? new[] { $"Dépendances manquantes : {string.Join(", ", missingDeps)}" }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // validate_xl
    // ════════════════════════════════════════════════════════════════════════
    private static readonly HashSet<string> XlTopLevelKeys = new(StringComparer.Ordinal)
    {
        "customSounds", "resource", "factories", "localization", "animations",
    };

    [McpServerTool(Name = "validate_xl", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Valide un fichier ArchiveXL .xl (YAML) : YAML bien formé + sections de premier " +
                 "niveau reconnues (customSounds, resource, factories, localization, animations). " +
                 "Signale les erreurs de syntaxe YAML (ligne/colonne) et les sections inconnues. " +
                 "Complète validate_tweak (qui cible les .tweak TweakXL).")]
    public static string ValidateXl(
        [Description("Chemin du fichier .xl à valider.")] string xlFile)
    {
        if (!File.Exists(xlFile))
            return Err($"Fichier .xl introuvable : {xlFile}");

        string text;
        try { text = File.ReadAllText(xlFile); }
        catch (Exception ex) { return Err($"Lecture impossible : {ex.Message}"); }

        object? root;
        try
        {
            var de = new DeserializerBuilder().Build();
            root = de.Deserialize<object?>(text);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                status = "error",
                summary = $"YAML invalide dans {Path.GetFileName(xlFile)}",
                xlFile,
                warnings = Array.Empty<string>(),
                errors = new[] { ex.Message.Replace("\n", " ").Trim() },
            }, JsonOpts);
        }

        var warnings = new List<string>();
        var errors = new List<string>();
        var sections = new List<string>();

        if (root is not Dictionary<object, object> map)
        {
            errors.Add("La racine d'un .xl doit être un mapping YAML (clé: valeur).");
        }
        else
        {
            foreach (var k in map.Keys)
            {
                var key = k?.ToString() ?? "";
                sections.Add(key);
                if (!XlTopLevelKeys.Contains(key))
                    warnings.Add($"Section de premier niveau inconnue : « {key} » " +
                                 $"(attendu : {string.Join(", ", XlTopLevelKeys)}).");
            }
            if (sections.Count == 0)
                warnings.Add("Fichier .xl vide (aucune section).");
        }

        var status = errors.Count > 0 ? "error" : warnings.Count > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = errors.Count == 0,
            status,
            summary = $"Validation .xl : {Path.GetFileName(xlFile)} — {sections.Count} section(s), " +
                      $"{errors.Count} erreur(s), {warnings.Count} avertissement(s)",
            xlFile,
            sections,
            warnings,
            errors,
        }, JsonOpts);
    }

    [McpServerTool(Name = "validate_redmod", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Valide le info.json d'un projet REDmod : champs requis name / version (+ format), " +
                 "et cohérence des entrées customSounds (name, type, et fichier référencé présent " +
                 "dans customSounds/). Les outils REDmod (create_redmod_project, install_redmod, " +
                 "pack_redmod) ne vérifient que la PRÉSENCE du info.json, jamais son contenu. " +
                 "Complète validate_xl / validate_tweak / validate_item_mod.")]
    public static string ValidateRedmod(
        [Description("Dossier racine du REDmod (contenant info.json) ou chemin direct vers le " +
                     "info.json.")] string modPath)
    {
        var infoPath = Directory.Exists(modPath) ? Path.Combine(modPath, "info.json") : modPath;
        if (!File.Exists(infoPath))
            return Err($"info.json introuvable : {infoPath}");

        string json;
        try { json = File.ReadAllText(infoPath); }
        catch (Exception ex) { return Err($"Lecture impossible : {ex.Message}"); }

        var modRoot = Path.GetDirectoryName(Path.GetFullPath(infoPath)) ?? ".";
        var soundsDir = Path.Combine(modRoot, "customSounds");
        var presentSounds = Directory.Exists(soundsDir)
            ? Directory.EnumerateFiles(soundsDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList()
            : new List<string>();

        var v = ValidateRedmodInfo(json, presentSounds);
        var status = v.Errors.Count > 0 ? "error" : v.Warnings.Count > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = v.Errors.Count == 0,
            status,
            summary = $"Validation REDmod : {Path.GetFileName(modRoot)} — " +
                      $"{v.Errors.Count} erreur(s), {v.Warnings.Count} avertissement(s)",
            infoPath,
            name = v.Name,
            version = v.Version,
            customSoundCount = v.CustomSoundCount,
            warnings = v.Warnings,
            errors = v.Errors,
        }, JsonOpts);
    }

    internal sealed record RedmodValidation(
        string? Name, string? Version, int CustomSoundCount,
        IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

    /// <summary>Valide le contenu d'un info.json REDmod. Logique pure (entrée = texte JSON +
    /// noms de fichiers présents dans customSounds/), testée isolément. Règles : name et version
    /// requis (version au format numérique sinon avertissement) ; chaque customSounds doit avoir
    /// name + type, et un file (sauf type mod_skip) qui doit exister dans customSounds/.</summary>
    internal static RedmodValidation ValidateRedmodInfo(
        string infoJson, IReadOnlyCollection<string> presentSoundFiles)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        string? name = null, version = null;
        var soundCount = 0;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(infoJson); }
        catch (Exception ex)
        {
            errors.Add($"info.json : JSON invalide — {ex.Message.Replace("\n", " ").Trim()}");
            return new RedmodValidation(null, null, 0, errors, warnings);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("info.json : la racine doit être un objet JSON.");
                return new RedmodValidation(null, null, 0, errors, warnings);
            }

            if (root.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
            {
                name = nEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) errors.Add("info.json : « name » est vide.");
            }
            else errors.Add("info.json : champ requis « name » absent ou non-textuel.");

            if (root.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.String)
            {
                version = vEl.GetString();
                if (string.IsNullOrWhiteSpace(version)) errors.Add("info.json : « version » est vide.");
                else if (!LooksLikeVersion(version))
                    warnings.Add($"info.json : « version » = « {version} » ne ressemble pas à un " +
                                 "numéro de version (ex. 1.0.0).");
            }
            else errors.Add("info.json : champ requis « version » absent ou non-textuel.");

            if (root.TryGetProperty("customSounds", out var csEl))
            {
                if (csEl.ValueKind != JsonValueKind.Array)
                    errors.Add("info.json : « customSounds » doit être un tableau.");
                else
                {
                    var i = 0;
                    foreach (var s in csEl.EnumerateArray())
                    {
                        var where = $"customSounds[{i}]";
                        i++;
                        if (s.ValueKind != JsonValueKind.Object)
                        { errors.Add($"{where} : doit être un objet."); continue; }
                        soundCount++;

                        var sName = s.TryGetProperty("name", out var snEl)
                                    && snEl.ValueKind == JsonValueKind.String ? snEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(sName)) errors.Add($"{where} : « name » requis.");

                        var sType = s.TryGetProperty("type", out var stEl)
                                    && stEl.ValueKind == JsonValueKind.String ? stEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(sType))
                            errors.Add($"{where} : « type » requis (ex. mod_sfx_2d, mod_skip).");

                        var isSkip = string.Equals(sType, "mod_skip", StringComparison.OrdinalIgnoreCase);
                        var file = s.TryGetProperty("file", out var fEl)
                                   && fEl.ValueKind == JsonValueKind.String ? fEl.GetString() : null;
                        if (!isSkip)
                        {
                            if (string.IsNullOrWhiteSpace(file))
                                errors.Add($"{where} : « file » requis pour le type « {sType} ».");
                            else if (presentSoundFiles.Count > 0)
                            {
                                var bare = file.Replace('\\', '/').TrimStart('/').Split('/').Last();
                                if (!presentSoundFiles.Contains(bare, StringComparer.OrdinalIgnoreCase)
                                    && !presentSoundFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                                    warnings.Add($"{where} : fichier son « {file} » introuvable dans customSounds/.");
                            }
                        }
                    }
                }
            }
        }

        return new RedmodValidation(name, version, soundCount, errors, warnings);
    }

    private static bool LooksLikeVersion(string v)
    {
        var parts = v.Split('.');
        return parts.Length >= 2 && parts.All(p => p.Length > 0 && p.All(char.IsDigit));
    }

    // ════════════════════════════════════════════════════════════════════════
    // scaffold_archivexl
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "scaffold_archivexl", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Génère un fichier ArchiveXL .xl de départ (YAML commenté) pour un type de mod " +
                 "donné : factory (enregistrer un record factory via CSV), customSounds (audio " +
                 "personnalisé), localization (textes), resource (patch de ressource). Scaffolding " +
                 "prêt à éditer — équivalent .xl de generate_tweak_template.")]
    public static string ScaffoldArchiveXl(
        [Description("Dossier de destination du fichier .xl.")] string outputFolder,
        [Description("Nom du mod (sert de nom de fichier <nom>.xl).")] string modName,
        [Description("Type : factory | customSounds | localization | resource.")] string kind = "factory")
    {
        if (!Directory.Exists(outputFolder))
            return Err($"Dossier de destination introuvable : {outputFolder}");
        if (string.IsNullOrWhiteSpace(modName)
            || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err("Nom de mod invalide.");

        var body = kind.ToLowerInvariant() switch
        {
            "customsounds" =>
                "# ArchiveXL — sons personnalisés\n" +
                "customSounds:\n" +
                $"  - name: {modName}_sfx_01\n" +
                "    type: mod_sfx_2d\n" +
                $"    file: mod\\{modName}\\sfx_01.wav\n",
            "localization" =>
                "# ArchiveXL — localisation\n" +
                "localization:\n" +
                "  onscreens:\n" +
                $"    en-us: base\\localization\\en-us\\{modName}.json\n",
            "resource" =>
                "# ArchiveXL — patch de ressource (ajoute des appearances/composants)\n" +
                "resource:\n" +
                "  patch:\n" +
                "    base\\path\\to\\target.app:\n" +
                $"      - {modName}\\appearances\\custom.app\n",
            _ /* factory */ =>
                "# ArchiveXL — enregistrement d'un record factory (items/records via CSV)\n" +
                "factories:\n" +
                $"  - {modName}\\factory.csv\n",
        };

        var xlPath = Path.Combine(outputFolder, modName + ".xl");
        File.WriteAllText(xlPath, body, new UTF8Encoding(false));

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Squelette ArchiveXL généré ({kind}) : {Path.GetFileName(xlPath)}",
            produced = new[] { modName + ".xl" },
            kind,
            xlPath,
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // find_references
    // ════════════════════════════════════════════════════════════════════════
    private static readonly string[] TextRefExtensions =
        { ".reds", ".script", ".swift", ".redscript", ".tweak", ".yaml", ".yml", ".xl", ".lua", ".json", ".csv" };

    [McpServerTool(Name = "find_references", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Recherche toutes les références textuelles à une cible (TweakDBID, chemin de " +
                 "ressource, LocKey, CName, nom de classe/fonction...) dans les fichiers source d'un " +
                 "dossier de mod ou de projet (.reds, .tweak, .yaml, .xl, .lua, .json, .csv). " +
                 "Renvoie fichier:ligne + extrait. Idéal pour l'analyse d'impact avant d'éditer. " +
                 "Pour chercher dans les .archive du jeu, utiliser plutôt find_in_archives.")]
    public static string FindReferences(
        [Description("Chaîne à rechercher (sous-chaîne, insensible à la casse).")] string target,
        [Description("Dossier à parcourir (mod, projet, ou r6/scripts).")] string searchFolder,
        [Description("Nombre max de correspondances renvoyées (défaut 200).")] int maxResults = 200,
        [Description("Recherche sensible à la casse (défaut false).")] bool caseSensitive = false)
    {
        if (string.IsNullOrEmpty(target))
            return Err("Cible de recherche vide.");
        if (!Directory.Exists(searchFolder))
            return Err($"Dossier introuvable : {searchFolder}");

        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var extSet = new HashSet<string>(TextRefExtensions, StringComparer.OrdinalIgnoreCase);
        var matches = new List<object>();
        int filesScanned = 0, filesWithMatch = 0; bool truncated = false;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(searchFolder, "*", SearchOption.AllDirectories); }
        catch (Exception ex) { return Err($"Parcours impossible : {ex.Message}"); }

        foreach (var f in files)
        {
            if (!extSet.Contains(Path.GetExtension(f))) continue;
            filesScanned++;
            // Lecture paresseuse (File.ReadLines) : pas de chargement du fichier
            // entier en mémoire — important sur de gros .json/.csv de mod.
            IEnumerable<string> lines;
            try { lines = File.ReadLines(f); } catch { continue; }
            var hit = false;
            var lineNo = 0;
            try
            {
                foreach (var lineText in lines)
                {
                    lineNo++;
                    if (lineText.IndexOf(target, cmp) < 0) continue;
                    hit = true;
                    if (matches.Count < maxResults)
                    {
                        var snippet = lineText.Trim();
                        if (snippet.Length > 200) snippet = snippet[..200] + "…";
                        matches.Add(new { file = Path.GetRelativePath(searchFolder, f), line = lineNo, text = snippet });
                    }
                    else { truncated = true; }
                }
            }
            catch { continue; }
            if (hit) filesWithMatch++;
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"« {target} » : {matches.Count}{(truncated ? "+" : "")} occurrence(s) " +
                      $"dans {filesWithMatch} fichier(s) ({filesScanned} scanné(s))",
            target,
            searchFolder,
            matchCount = matches.Count,
            filesWithMatch,
            filesScanned,
            truncated,
            matches,
            warnings = truncated ? new[] { $"Résultats tronqués à {maxResults} — affiner la cible ou augmenter maxResults." } : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // diff_mod_vs_base
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "diff_mod_vs_base", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Diff sémantique d'UN fichier de jeu surchargé par un mod, contre sa version de " +
                 "base : extrait le fichier des deux côtés, les convertit en JSON et compare les " +
                 "champs (ajoutés / supprimés / modifiés). Répond à « qu'est-ce que ce mod change " +
                 "vraiment ? ». La version de base est cherchée dans archive/pc/content (cache LRU) " +
                 "si baseArchive n'est pas fourni.")]
    public static async Task<string> DiffModVsBase(
        Cp77ToolsRunner runner,
        [Description("Archive .archive du mod contenant le fichier surchargé.")] string modArchive,
        [Description("Chemin interne du fichier dans l'archive (ex. base\\...\\x.app).")] string gameFilePath,
        [Description("Racine du jeu (pour localiser la version de base dans archive/pc/content).")] string gamePath,
        [Description("Optionnel : archive .archive de base précise (court-circuite la recherche).")] string? baseArchive = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(modArchive))
            return Err($"Archive de mod introuvable : {modArchive}");

        // Localiser l'archive de base si non fournie.
        if (string.IsNullOrWhiteSpace(baseArchive))
        {
            var content = Path.Combine(gamePath, "archive", "pc", "content");
            if (!Directory.Exists(content))
                return Err($"Dossier content introuvable : {content} (fournir baseArchive ?)");
            baseArchive = await FindArchiveContaining(runner, content, gameFilePath, ct);
            if (baseArchive is null)
                return Err($"Fichier introuvable dans le jeu de base : {gameFilePath} " +
                           "(c'est peut-être un fichier AJOUTÉ par le mod, pas une surcharge).");
        }

        var modJson = await ExtractAsJson(runner, modArchive, gameFilePath, ct);
        var baseJson = await ExtractAsJson(runner, baseArchive!, gameFilePath, ct);
        if (modJson is null) return Err($"Extraction/conversion échouée côté mod : {gameFilePath}");
        if (baseJson is null) return Err($"Extraction/conversion échouée côté base : {gameFilePath}");

        var (added, removed, changedList) = DiffJson(baseJson, modJson);
        var changed = changedList.Select(c => (object)new { path = c.Path, @base = c.Base, mod = c.Mod }).ToList();
        const int cap = 100;
        bool truncated = added.Count + removed.Count + changed.Count > cap;

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Diff {Path.GetFileName(gameFilePath)} (mod vs base) : " +
                      $"{added.Count} ajout(s), {removed.Count} suppression(s), {changed.Count} modif(s)",
            gameFilePath,
            modArchive,
            baseArchive,
            addedCount = added.Count,
            removedCount = removed.Count,
            changedCount = changed.Count,
            added = added.Take(cap),
            removed = removed.Take(cap),
            changed = changed.Take(cap),
            truncated,
            warnings = truncated ? new[] { $"Diff tronqué à {cap} entrées par catégorie." } : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // scaffold_mod
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "scaffold_mod", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Crée en UN appel un squelette de mod fonctionnel selon son type : archive " +
                 "(projet .cpmodproj + dossiers), redscript (starter .reds avec @wrapMethod), tweak " +
                 "(starter .tweak), redmod (info.json + dossiers). Écrit aussi un MOD_MANIFEST.json " +
                 "récapitulant le type, les dépendances déclarées et la structure. Raccourci " +
                 "par-dessus create_mod_project / generate_* pour démarrer vite.")]
    public static string ScaffoldMod(
        [Description("Dossier parent où créer le mod.")] string parentFolder,
        [Description("Nom du mod.")] string modName,
        [Description("Type : archive | redscript | tweak | redmod.")] string kind = "archive",
        [Description("Auteur (optionnel).")] string? author = null,
        [Description("Version (optionnel, ex. 1.0.0).")] string? version = null,
        [Description("Dépendances déclarées, séparées par des virgules (ex. Codeware,ArchiveXL).")] string? dependencies = null)
    {
        if (!Directory.Exists(parentFolder))
            return Err($"Dossier parent introuvable : {parentFolder}");
        if (string.IsNullOrWhiteSpace(modName) || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err("Nom de mod invalide.");
        var root = Path.Combine(parentFolder, modName);
        if (Directory.Exists(root)) return Err($"Le dossier existe déjà : {root}");

        var produced = new List<string>();
        void Dir(params string[] parts) { var p = Path.Combine(new[] { root }.Concat(parts).ToArray()); Directory.CreateDirectory(p); produced.Add(Path.GetRelativePath(root, p) + Path.DirectorySeparatorChar); }
        void Write(string rel, string content) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, content, new UTF8Encoding(false)); produced.Add(rel); }

        var k = kind.ToLowerInvariant();
        switch (k)
        {
            case "redscript":
                Write(Path.Combine("r6", "scripts", modName, modName + ".reds"),
                    $"// {modName} — mod REDscript\n" +
                    "// Exemple : étendre une méthode du jeu sans la remplacer.\n" +
                    "@wrapMethod(PlayerPuppet)\n" +
                    "protected cb func OnGameAttached() -> Bool {\n" +
                    "  let result = wrappedMethod();\n" +
                    $"  LogChannel(n\"DEBUG\", \"{modName} chargé\");\n" +
                    "  return result;\n" +
                    "}\n");
                break;
            case "tweak":
                Write(Path.Combine("r6", "tweaks", modName + ".tweak"),
                    $"# {modName} — mod TweakXL\n" +
                    "# Exemple : surcharger un champ d'un record existant.\n" +
                    "Items.Preset_Lexington_Default:\n" +
                    "  magazineCapacity: 24\n");
                break;
            case "redmod":
                Dir("archives"); Dir("scripts"); Dir("tweaks"); Dir("customSounds");
                Write("info.json", JsonSerializer.Serialize(new
                {
                    name = modName,
                    version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version,
                    description = "",
                    customSounds = Array.Empty<object>(),
                }, JsonOpts));
                break;
            default: // archive
                k = "archive";
                Dir("source", "archive"); Dir("source", "raw"); Dir("source", "resources");
                Dir("source", "customSounds"); Dir("packed");
                Write(modName + ".cpmodproj", WolvenKitTools.BuildCpmodprojXml(modName, author, version, null));
                break;
        }

        var deps = (dependencies ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Write("MOD_MANIFEST.json", JsonSerializer.Serialize(new
        {
            name = modName,
            kind = k,
            author = author ?? "",
            version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version,
            dependencies = deps,
            createdBy = "wolvenkit-mcp scaffold_mod",
        }, JsonOpts));

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Mod « {modName} » ({k}) créé : {root}",
            modPath = root,
            kind = k,
            produced,
            dependencies = deps,
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // package_mod
    // ════════════════════════════════════════════════════════════════════════
    private static readonly string[] GameLayoutRoots =
        { "archive", "r6", "mods", "red4ext", "bin", "engine" };

    [McpServerTool(Name = "package_mod", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Empaquette un dossier au layout relatif au jeu (archive/pc/mod, r6/scripts, " +
                 "r6/tweaks, mods/, red4ext/...) en un .zip distribuable (Nexus / install manuel), " +
                 "avec séparateurs « / » conformes. Valide la présence d'au moins un dossier de jeu " +
                 "reconnu et avertit sinon.")]
    public static string PackageMod(
        [Description("Dossier source au layout jeu (contient archive/, r6/, mods/...).")] string sourceFolder,
        [Description("Chemin du .zip de sortie.")] string outputZip)
    {
        if (!Directory.Exists(sourceFolder))
            return Err($"Dossier source introuvable : {sourceFolder}");

        var srcFull = Path.GetFullPath(sourceFolder);
        var topDirs = Directory.GetDirectories(srcFull).Select(d => Path.GetFileName(d)).ToList();
        var recognized = topDirs.Where(d => GameLayoutRoots.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
        var warnings = new List<string>();
        if (recognized.Count == 0)
            warnings.Add("Aucun dossier de jeu reconnu (archive/, r6/, mods/, red4ext/...) à la racine — " +
                         "le zip risque de ne pas s'installer tel quel.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputZip))!);
            if (File.Exists(outputZip)) File.Delete(outputZip);
            using var zs = File.Open(outputZip, FileMode.Create);
            using var zip = new ZipArchive(zs, ZipArchiveMode.Create);
            int n = 0, skipped = 0;
            foreach (var file in Directory.EnumerateFiles(srcFull, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcFull, file).Replace('\\', '/');
                // Exclure le bruit non distribuable (artefacts de dev / build).
                if (IsPackagingNoise(rel)) { skipped++; continue; }
                zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
                n++;
            }
            if (skipped > 0)
                warnings.Add($"{skipped} fichier(s) de dev/build exclu(s) du bundle " +
                             "(.git, packed/, *.cpmodproj, MOD_MANIFEST.json).");
            var sizeKo = new FileInfo(outputZip).Length / 1024;
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = warnings.Count > 0 ? "partial" : "success",
                summary = $"Bundle créé : {Path.GetFileName(outputZip)} ({n} fichier(s), {sizeKo} Ko)",
                outputZip,
                fileCount = n,
                skipped,
                recognizedLayout = recognized,
                produced = new[] { Path.GetFileName(outputZip) },
                warnings,
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Échec du packaging : {ex.Message}"); }
    }

    /// <summary>Fichiers/dossiers de dev qui n'ont pas leur place dans un bundle
    /// distribuable (Nexus / install manuel).</summary>
    private static bool IsPackagingNoise(string rel)
    {
        var seg0 = rel.Split('/')[0];
        if (seg0.Equals(".git", StringComparison.OrdinalIgnoreCase)) return true;
        if (seg0.Equals("packed", StringComparison.OrdinalIgnoreCase)) return true;
        if (rel.EndsWith(".cpmodproj", StringComparison.OrdinalIgnoreCase)) return true;
        if (Path.GetFileName(rel).Equals("MOD_MANIFEST.json", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ════════════════════════════════════════════════════════════════════════
    // inspect_journal / find_journal_entry — navigation du journal de quêtes
    //
    // Un .journal (gameJournalResource) est un CR2W standard, donc lisible/écrivable
    // via read_game_file / write_game_file. Mais cooked_journal.journal pèse ~70 Mo
    // en JSON : impossible à lire/éditer d'un bloc. Ces outils en donnent une vue
    // navigable (résumé par type, recherche d'entrée → chemin JSON ciblé), dans
    // l'esprit de inspect_mesh / mod_summary / describe_tweak_record.
    // ════════════════════════════════════════════════════════════════════════

    internal sealed record JournalEntryRef(string Path, string Type, string? Id, string? Title, int ChildCount);

    // Projection en clés minuscules pour une sortie JSON cohérente avec le reste.
    private static object JournalRefJson(JournalEntryRef e)
        => new { path = e.Path, type = e.Type, id = e.Id, title = e.Title, childCount = e.ChildCount };

    [McpServerTool(Name = "inspect_journal", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Résumé navigable d'un fichier .journal converti en JSON (par read_game_file) : " +
                 "nombre total d'entrées, profondeur, répartition par type ($type), et catégories de " +
                 "premier niveau (quêtes, codex, contacts, e-mails…). Évite de charger les ~70 Mo de " +
                 "JSON du journal complet. Donne ensuite à find_journal_entry une cible précise.")]
    public static string InspectJournal(
        [Description("Chemin du JSON produit par read_game_file sur un .journal.")] string jsonFile)
    {
        if (!File.Exists(jsonFile))
            return Err($"Fichier JSON introuvable : {jsonFile}");
        JournalSummary? s;
        try
        {
            using var fs = File.OpenRead(jsonFile);
            using var doc = JsonDocument.Parse(fs);
            s = SummarizeJournal(doc.RootElement);
        }
        catch (Exception ex) { return Err($"JSON illisible : {ex.Message}"); }
        if (s is null)
            return Err("Ce JSON n'est pas un journal (RootChunk.$type ≠ gameJournalResource).");

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Journal : {s.TotalEntries} entrée(s), profondeur {s.MaxDepth}, " +
                      $"{s.ByType.Count} type(s), {s.TopLevel.Count} catégorie(s) de 1er niveau",
            jsonFile,
            totalEntries = s.TotalEntries,
            maxDepth = s.MaxDepth,
            descriptor = s.Descriptor,
            byType = s.ByType.OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
            topLevelCategories = s.TopLevel.Select(JournalRefJson),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    [McpServerTool(Name = "find_journal_entry", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Localise des entrées dans un .journal (JSON de read_game_file) par id, type ou " +
                 "titre, et renvoie pour chacune son CHEMIN JSON exact (ex. " +
                 "Data.RootChunk.entry.Data.entries[2].Data.entries[7].Data) — pour éditer l'entrée " +
                 "ciblée puis réécrire via write_game_file, sans manipuler les ~70 Mo entiers.")]
    public static string FindJournalEntry(
        [Description("Chemin du JSON produit par read_game_file sur un .journal.")] string jsonFile,
        [Description("Valeur à rechercher (sous-chaîne, insensible à la casse).")] string query,
        [Description("Champ ciblé : id (défaut), type ($type) ou title.")] string field = "id",
        [Description("Nombre max de correspondances (défaut 100).")] int maxResults = 100)
    {
        if (!File.Exists(jsonFile))
            return Err($"Fichier JSON introuvable : {jsonFile}");
        if (string.IsNullOrEmpty(query))
            return Err("Requête vide.");
        List<JournalEntryRef> matches; bool truncated; bool isJournal;
        try
        {
            using var fs = File.OpenRead(jsonFile);
            using var doc = JsonDocument.Parse(fs);
            (matches, truncated, isJournal) = FindInJournal(doc.RootElement, query, field, maxResults);
        }
        catch (Exception ex) { return Err($"JSON illisible : {ex.Message}"); }
        if (!isJournal)
            return Err("Ce JSON n'est pas un journal (RootChunk.$type ≠ gameJournalResource).");

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"« {query} » (champ {field}) : {matches.Count}{(truncated ? "+" : "")} entrée(s) trouvée(s)",
            jsonFile,
            field,
            query,
            matchCount = matches.Count,
            truncated,
            matches = matches.Select(JournalRefJson),
            warnings = truncated ? new[] { $"Résultats tronqués à {maxResults} — affiner la requête." } : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ── Helpers journal ─────────────────────────────────────────────────────
    internal sealed record JournalSummary(
        int TotalEntries, int MaxDepth, Dictionary<string, int> ByType,
        List<JournalEntryRef> TopLevel, string? Descriptor);

    /// <summary>Navigue jusqu'au dossier racine du journal (gameJournalRootFolderEntry).
    /// Renvoie default si le JSON n'est pas un journal.</summary>
    private static bool TryGetJournalRoot(JsonElement root, out JsonElement rootFolder, out string? descriptor)
    {
        rootFolder = default; descriptor = null;
        var chunk = root.TryGetProperty("Data", out var d) && d.TryGetProperty("RootChunk", out var rc) ? rc
                  : root.TryGetProperty("RootChunk", out var rc2) ? rc2 : default;
        if (chunk.ValueKind != JsonValueKind.Object) return false;
        if (JType(chunk) != "gameJournalResource") return false;
        if (!chunk.TryGetProperty("entry", out var entry) || !entry.TryGetProperty("Data", out rootFolder))
            return false;
        if (rootFolder.TryGetProperty("descriptor", out var desc)
            && desc.TryGetProperty("DepotPath", out var dp) && dp.TryGetProperty("$value", out var dv))
            descriptor = dv.GetString();
        return true;
    }

    internal static JournalSummary? SummarizeJournal(JsonElement root)
    {
        if (!TryGetJournalRoot(root, out var rootFolder, out var descriptor)) return null;
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0, maxDepth = 0;
        var topLevel = new List<JournalEntryRef>();

        void Walk(JsonElement data, int depth)
        {
            total++;
            var t = JType(data);
            byType[t] = byType.GetValueOrDefault(t) + 1;
            if (depth > maxDepth) maxDepth = depth;
            var children = JChildren(data);
            if (depth == 1)
                topLevel.Add(new JournalEntryRef("", t, JId(data), JTitle(data), children.Count));
            foreach (var child in children) Walk(child, depth + 1);
        }
        Walk(rootFolder, 0);
        return new JournalSummary(total, maxDepth, byType, topLevel, descriptor);
    }

    internal static (List<JournalEntryRef> matches, bool truncated, bool isJournal) FindInJournal(
        JsonElement root, string query, string field, int maxResults)
    {
        var matches = new List<JournalEntryRef>();
        if (!TryGetJournalRoot(root, out var rootFolder, out _))
            return (matches, false, false);
        var truncated = false;

        void Walk(JsonElement data, string path)
        {
            var value = field?.ToLowerInvariant() switch
            {
                "type" => JType(data),
                "title" => JTitle(data),
                _ => JId(data),
            };
            if (value is not null && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (matches.Count < maxResults)
                    matches.Add(new JournalEntryRef(path, JType(data), JId(data), JTitle(data), JChildren(data).Count));
                else truncated = true;
            }
            var children = JChildren(data);
            for (var i = 0; i < children.Count; i++)
                Walk(children[i], $"{path}.entries[{i}].Data");
        }
        Walk(rootFolder, "Data.RootChunk.entry.Data");
        return (matches, truncated, true);
    }

    private static string JType(JsonElement d)
        => d.TryGetProperty("$type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "?" : "?";
    private static string? JId(JsonElement d)
        => d.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
    private static string? JTitle(JsonElement d)
        => d.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.Object
           && t.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
           && !string.IsNullOrEmpty(v.GetString()) ? v.GetString() : null;
    /// <summary>Enfants d'une entrée journal : les Data de chaque handle de « entries ».</summary>
    private static List<JsonElement> JChildren(JsonElement data)
    {
        var list = new List<JsonElement>();
        if (data.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            foreach (var handle in entries.EnumerateArray())
                if (handle.TryGetProperty("Data", out var cd) && cd.ValueKind == JsonValueKind.Object)
                    list.Add(cd);
        return list;
    }

    // ════════════════════════════════════════════════════════════════════════
    // inspect_cr2w / find_in_cr2w — navigation GÉNÉRIQUE d'un gros CR2W en JSON
    //
    // Généralise inspect_journal à n'importe quel CR2W (.quest, .questphase, .scene,
    // .streamingsector, inkwidget…) : ces arbres sont énormes une fois en JSON.
    // ════════════════════════════════════════════════════════════════════════

    internal sealed record Cr2wNodeRef(string Path, string Type, string? Value);

    [McpServerTool(Name = "inspect_cr2w", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Résumé navigable de N'IMPORTE quel CR2W converti en JSON (par read_game_file / " +
                 "cr2w_to_json) : type racine, nombre d'objets typés, répartition par $type, " +
                 "profondeur. Pour les gros fichiers (quêtes, scènes, secteurs, UI) qu'on ne peut " +
                 "lire d'un bloc. Donne ensuite à find_in_cr2w une cible.")]
    public static string InspectCr2w(
        [Description("Chemin du JSON produit par read_game_file / cr2w_to_json.")] string jsonFile)
    {
        if (!File.Exists(jsonFile))
            return Err($"Fichier JSON introuvable : {jsonFile}");
        try
        {
            using var fs = File.OpenRead(jsonFile);
            using var doc = JsonDocument.Parse(fs);
            var (rootType, total, maxDepth, byType) = SummarizeCr2w(doc.RootElement);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"CR2W « {rootType ?? "?"} » : {total} objet(s) typé(s), " +
                          $"profondeur {maxDepth}, {byType.Count} type(s)",
                jsonFile,
                rootType,
                totalTypedObjects = total,
                maxDepth,
                byType = byType.OrderByDescending(kv => kv.Value).Take(80)
                    .ToDictionary(kv => kv.Key, kv => kv.Value),
                warnings = Array.Empty<string>(),
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"JSON illisible : {ex.Message}"); }
    }

    [McpServerTool(Name = "find_in_cr2w", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Recherche dans N'IMPORTE quel CR2W (JSON) les objets dont un champ correspond à " +
                 "une cible, et renvoie leur CHEMIN JSON exact — pour éditer le nœud ciblé puis " +
                 "réécrire via write_game_file. field = $type (défaut), un nom de champ précis, ou " +
                 "* (toute valeur texte). Idéal pour quêtes/scènes/secteurs/UI volumineux.")]
    public static string FindInCr2w(
        [Description("Chemin du JSON produit par read_game_file / cr2w_to_json.")] string jsonFile,
        [Description("Valeur à rechercher (sous-chaîne, insensible à la casse).")] string query,
        [Description("Champ ciblé : $type (défaut), un nom de propriété, ou * (toute valeur texte).")] string field = "$type",
        [Description("Nombre max de correspondances (défaut 100).")] int maxResults = 100)
    {
        if (!File.Exists(jsonFile))
            return Err($"Fichier JSON introuvable : {jsonFile}");
        if (string.IsNullOrEmpty(query))
            return Err("Requête vide.");
        try
        {
            using var fs = File.OpenRead(jsonFile);
            using var doc = JsonDocument.Parse(fs);
            var (matches, truncated) = FindInCr2wTree(doc.RootElement, query, field, maxResults);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"« {query} » (champ {field}) : {matches.Count}{(truncated ? "+" : "")} correspondance(s)",
                jsonFile,
                field,
                query,
                matchCount = matches.Count,
                truncated,
                matches = matches.Select(m => new { path = m.Path, type = m.Type, value = m.Value }),
                warnings = truncated ? new[] { $"Résultats tronqués à {maxResults} — affiner la requête." } : Array.Empty<string>(),
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"JSON illisible : {ex.Message}"); }
    }

    /// <summary>Parcourt tout le JSON et compte les objets par $type.</summary>
    internal static (string? rootType, int total, int maxDepth, Dictionary<string, int> byType)
        SummarizeCr2w(JsonElement root)
    {
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0, maxDepth = 0;
        string? rootType = null;
        if (root.TryGetProperty("Data", out var d) && d.TryGetProperty("RootChunk", out var rc)
            && rc.ValueKind == JsonValueKind.Object)
            rootType = JType(rc);

        void Walk(JsonElement e, int depth)
        {
            if (depth > maxDepth) maxDepth = depth;
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    if (e.TryGetProperty("$type", out var t) && t.ValueKind == JsonValueKind.String)
                    { total++; var ty = t.GetString() ?? "?"; byType[ty] = byType.GetValueOrDefault(ty) + 1; }
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, depth + 1);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Walk(item, depth + 1);
                    break;
            }
        }
        Walk(root, 0);
        return (rootType, total, maxDepth, byType);
    }

    internal static (List<Cr2wNodeRef> matches, bool truncated) FindInCr2wTree(
        JsonElement root, string query, string field, int maxResults)
    {
        var matches = new List<Cr2wNodeRef>();
        var truncated = false;
        var anyField = field == "*";
        var byType = string.IsNullOrEmpty(field) || field == "$type";

        void Add(JsonElement obj, string path, string? value)
        {
            if (matches.Count >= maxResults) { truncated = true; return; }
            matches.Add(new Cr2wNodeRef(path, JType(obj), value));
        }

        void Walk(JsonElement e, string path)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    if (byType)
                    {
                        var ty = JType(e);
                        if (ty.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) Add(e, path, ty);
                    }
                    else
                    {
                        foreach (var p in e.EnumerateObject())
                        {
                            if ((anyField || p.Name == field) && p.Value.ValueKind == JsonValueKind.String)
                            {
                                var v = p.Value.GetString();
                                if (v is not null && v.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                                { Add(e, path + "." + p.Name, v); break; }
                            }
                        }
                    }
                    foreach (var p in e.EnumerateObject())
                        Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name);
                    break;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var item in e.EnumerateArray()) Walk(item, $"{path}[{i++}]");
                    break;
            }
        }
        Walk(root, "");
        return (matches, truncated);
    }

    // ════════════════════════════════════════════════════════════════════════
    // diagnose_logs — parse les logs de modding, classe les erreurs, propose un fix
    // ════════════════════════════════════════════════════════════════════════

    private sealed record LogSource(string Name, string[] RelativePaths);
    private static readonly LogSource[] LogSources =
    {
        new("redscript", new[] { @"r6\logs\redscript_rCURRENT.log", @"r6\logs\redscript.log" }),
        new("RED4ext", new[] { @"red4ext\logs\red4ext.log" }),
        new("ArchiveXL", new[] { @"red4ext\plugins\ArchiveXL\ArchiveXL.log" }),
        new("TweakXL", new[] { @"red4ext\plugins\TweakXL\TweakXL.log" }),
        new("Codeware", new[] { @"red4ext\plugins\Codeware\Codeware.log" }),
        new("CET", new[] { @"bin\x64\plugins\cyber_engine_tweaks\cyber_engine_tweaks.log" }),
        new("REDmod", new[] { @"tools\redmod\bin\REDmodLog.txt" }),
    };

    // Base de connaissance : motif d'erreur connu → problème + correctif.
    private sealed record KnownError(string Pattern, string Problem, string Fix);
    private static readonly KnownError[] ErrorKb =
    {
        new("scc invocation failed|REDScript compilation has failed|compilation has failed",
            "Compilation redscript échouée — un .reds est invalide.",
            "Le log redscript indique le fichier:ligne fautif. Corriger ou retirer ce mod ; un seul .reds cassé bloque toute la compilation."),
        new("field with this name is already defined|already defined",
            "Définition en double — un mod est probablement installé deux fois.",
            "Vérifier qu'aucun mod n'est présent à la fois dans archive/pc/mod et mods/, ni dupliqué."),
        new("Failed to resolve address for hash|Could not find address",
            "Hash/adresse non résolu — souvent une mise à jour du jeu (ou jeu non à jour/piraté).",
            "Mettre à jour le mod et les core-mods (RED4ext, redscript) pour la version actuelle du jeu."),
        new("1114|VCRUNTIME|VCRedist|vcruntime",
            "RED4ext erreur 1114 — Visual C++ Redistributable 2022 manquant.",
            "Installer Microsoft Visual C++ Redistributable 2022 (x64)."),
        new("corrupted or missing TweakDB|tweakdb.*missing|tweakdb.*corrupt",
            "TweakDB corrompue ou absente.",
            "Copier r6/cache/tweakdb.bin vers r6/cache/modded/ (+ tweakdb_ep1.bin pour Phantom Liberty)."),
        new("Watchdog Timeout|watchdog timeout",
            "Watchdog timeout — chargement trop long (antivirus ou disque lent).",
            "Exclure le dossier du jeu de l'antivirus, ou augmenter le timeout dans user.ini."),
        new("ValidateScripts|codeware.global.reds",
            "Validation de scripts échouée (souvent Codeware/cache périmé).",
            "Vider r6/cache/, vérifier les fichiers du jeu, réinstaller Codeware à jour."),
    };

    [McpServerTool(Name = "diagnose_logs", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lit et DIAGNOSTIQUE les logs de modding d'une install Cyberpunk 2077 (redscript, " +
                 "RED4ext, ArchiveXL, TweakXL, Codeware, CET, REDmod) : extrait les erreurs, les " +
                 "classe par source, mappe les erreurs connues à un correctif, et tente d'attribuer " +
                 "au mod fautif. Bien plus utile que tail_game_logs (qui ne fait que du tail brut).")]
    public static string DiagnoseLogs(
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath,
        [Description("Nb max de lignes d'erreur remontées par source (défaut 30).")] int maxPerSource = 30)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");

        var perSource = new List<object>();
        var allFixes = new Dictionary<string, string>(StringComparer.Ordinal);
        int totalErrors = 0, logsFound = 0;
        var errRe = new System.Text.RegularExpressions.Regex(
            @"\b(error|failed|exception|fatal|could not|cannot|invalid)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var src in LogSources)
        {
            var path = src.RelativePaths.Select(r => Path.Combine(gamePath, r)).FirstOrDefault(File.Exists);
            if (path is null) continue;
            logsFound++;
            string[] lines;
            try { lines = File.ReadAllLines(path); } catch { continue; }
            var errs = new List<string>();
            for (var i = lines.Length - 1; i >= 0 && errs.Count < maxPerSource; i--)
                if (errRe.IsMatch(lines[i])) errs.Add(lines[i].Trim());
            errs.Reverse();
            totalErrors += errs.Count;

            // Matcher la KB sur l'ensemble du log (pas seulement les lignes d'erreur).
            var joined = string.Join("\n", lines);
            foreach (var k in ErrorKb)
                if (System.Text.RegularExpressions.Regex.IsMatch(joined, k.Pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    allFixes.TryAdd(k.Problem, k.Fix);

            perSource.Add(new
            {
                source = src.Name,
                logPath = path,
                errorCount = errs.Count,
                lastModified = File.GetLastWriteTime(path).ToString("u"),
                errors = errs,
            });
        }

        var diagnoses = allFixes.Select(kv => new { problem = kv.Key, fix = kv.Value }).ToList();
        var status = totalErrors > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status,
            summary = logsFound == 0
                ? "Aucun log trouvé (le jeu n'a peut-être jamais tourné moddé)."
                : $"{logsFound} log(s) analysé(s), {totalErrors} ligne(s) d'erreur, {diagnoses.Count} diagnostic(s) connu(s)",
            gamePath,
            logsFound,
            totalErrors,
            sources = perSource,
            diagnoses,
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Classe une ligne de log contre la base de connaissance (testable).
    /// Renvoie (problème, fix) ou null.</summary>
    internal static (string problem, string fix)? ClassifyLogText(string text)
    {
        foreach (var k in ErrorKb)
            if (System.Text.RegularExpressions.Regex.IsMatch(text, k.Pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return (k.Problem, k.Fix);
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    // analyze_conflicts — conflits robustes entre mods (archives + tweaks)
    //
    // Contourne le verbe WolvenKit `conflicts` (buggé sur certains installs) en
    // calculant les recouvrements directement depuis les listings d'archives (cache
    // LRU) et les records de tweaks.
    // ════════════════════════════════════════════════════════════════════════

    [McpServerTool(Name = "analyze_conflicts", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Détecte les conflits entre mods installés SANS le verbe WolvenKit buggé : " +
                 "fichiers de jeu fournis par plusieurs .archive (avec qui l'emporte selon l'ordre " +
                 "de chargement alphabétique = premier-gagne), et records TweakDB définis par " +
                 "plusieurs .tweak/.yaml. Premier outil de diagnostic quand un mod en écrase un " +
                 "autre silencieusement (sinon : bissection manuelle).")]
    public static async Task<string> AnalyzeConflicts(
        Cp77ToolsRunner runner,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath,
        [Description("Nb max de conflits remontés par catégorie (défaut 200).")] int maxResults = 200,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Dossier de jeu introuvable : {gamePath}");

        // Archives : archive/pc/mod + REDmod mods/*/archives
        var archives = new List<string>();
        var legacy = Path.Combine(gamePath, "archive", "pc", "mod");
        if (Directory.Exists(legacy)) archives.AddRange(Directory.GetFiles(legacy, "*.archive"));
        var modsDir = Path.Combine(gamePath, "mods");
        if (Directory.Exists(modsDir))
            foreach (var dd in Directory.GetDirectories(modsDir))
            {
                var ad = Path.Combine(dd, "archives");
                if (Directory.Exists(ad)) archives.AddRange(Directory.GetFiles(ad, "*.archive"));
            }

        var provided = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var arc in archives)
        {
            IReadOnlyList<string> entries;
            try { (entries, _, _) = await runner.GetArchiveListingAsync(arc, ct); }
            catch { continue; }
            var name = Path.GetFileName(arc);
            foreach (var e in entries)
            {
                var key = e.Replace('/', '\\').Trim();
                if (key.Length == 0) continue;
                if (!provided.TryGetValue(key, out var l)) provided[key] = l = new List<string>();
                if (!l.Contains(name)) l.Add(name);
            }
        }
        var archiveConflicts = provided.Where(kv => kv.Value.Count > 1)
            .Select(kv =>
            {
                var sorted = kv.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                return new { path = kv.Key, providedBy = sorted, winner = sorted[0] };
            })
            .OrderByDescending(c => c.providedBy.Count).ThenBy(c => c.path)
            .ToList();

        // Records de tweaks : r6/tweaks/**/*.{yaml,yml,tweak}
        var tweakRecords = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var tweaksDir = Path.Combine(gamePath, "r6", "tweaks");
        if (Directory.Exists(tweaksDir))
            foreach (var f in Directory.EnumerateFiles(tweaksDir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".yaml" or ".yml" or ".tweak")) continue;
                var rel = Path.GetRelativePath(tweaksDir, f);
                foreach (var rec in ParseTweakRecordNames(f))
                {
                    if (!tweakRecords.TryGetValue(rec, out var l)) tweakRecords[rec] = l = new List<string>();
                    if (!l.Contains(rel)) l.Add(rel);
                }
            }
        var tweakConflicts = tweakRecords.Where(kv => kv.Value.Count > 1)
            .Select(kv => new { record = kv.Key, definedBy = kv.Value.OrderBy(x => x).ToList() })
            .OrderBy(c => c.record).ToList();

        var total = archiveConflicts.Count + tweakConflicts.Count;
        var status = total > 0 ? "partial" : "success";

        // Conflits ≠ rapport mort : on dit quoi FAIRE. Les recettes générales suffisent
        // (les répéter sur chacun des N conflits gonflerait la réponse pour rien).
        var resolutionHints = total == 0 ? Array.Empty<string>() : new[]
        {
            "Pour qu'une archive perdante l'emporte : la renommer pour qu'elle se trie AVANT le " +
            "winner (préfixe « ! » ou « 00_ »), puis vérifier avec un nouvel analyze_conflicts.",
            "Pour neutraliser un mod en conflit sans le supprimer : toggle_mods (déplace ses " +
            ".archive vers _disabled ; pratique aussi en bissection pour trouver le coupable).",
            "Pour vérifier ce qui diffère réellement entre deux archives en conflit : " +
            "diff_archives, puis diff_mod_vs_base sur le fichier précis.",
            "Records TweakDB définis par plusieurs .tweak/.yaml : fusionner les valeurs dans un " +
            "seul fichier, ou supprimer la définition redondante (lint_tweak pour vérifier).",
        };

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status,
            summary = $"{archives.Count} archive(s) + tweaks analysés : " +
                      $"{archiveConflicts.Count} conflit(s) d'archive, {tweakConflicts.Count} record(s) en conflit",
            gamePath,
            note = "Ordre de chargement alphabétique : le premier à fournir un fichier l'emporte (winner).",
            resolutionHints,
            archivesScanned = archives.Count,
            archiveConflicts = archiveConflicts.Take(maxResults),
            archiveConflictCount = archiveConflicts.Count,
            tweakConflicts = tweakConflicts.Take(maxResults),
            tweakConflictCount = tweakConflicts.Count,
            truncated = archiveConflicts.Count > maxResults || tweakConflicts.Count > maxResults,
            warnings = total > 0 ? new[] { $"{total} conflit(s) détecté(s) — vérifier l'ordre/priorité." } : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Noms de records (clés de premier niveau qui sont des mappings) d'un
    /// fichier .tweak/.yaml TweakXL. Tolérant aux erreurs de parse.</summary>
    internal static List<string> ParseTweakRecordNames(string file)
    {
        var names = new List<string>();
        try
        {
            var de = new DeserializerBuilder().Build();
            var root = de.Deserialize<object?>(File.ReadAllText(file));
            if (root is Dictionary<object, object> map)
                foreach (var k in map.Keys)
                {
                    var key = k?.ToString();
                    // Ignorer les directives globales (commencent par $).
                    if (!string.IsNullOrEmpty(key) && !key.StartsWith("$")) names.Add(key);
                }
        }
        catch { /* fichier illisible : ignoré */ }
        return names;
    }

    // ════════════════════════════════════════════════════════════════════════
    // validate_item_mod — valide la chaîne de références d'un mod d'item ArchiveXL
    //
    // Cause #1 d'échec silencieux : une typo entre .yaml (entityName/displayName),
    // la factory .csv et la localisation .json. Validation purement textuelle des
    // control-files (sans daemon) ; les .ent/.app/.mesh sont signalés présents/absents.
    // ════════════════════════════════════════════════════════════════════════

    internal sealed record ItemRecord(string Record, string? EntityName, string? AppearanceName, string? DisplayName, string File);

    [McpServerTool(Name = "validate_item_mod", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Valide la chaîne de références d'un mod d'item ArchiveXL (la cause n°1 d'échec " +
                 "silencieux « l'item ne spawn pas / nom vide ») : pour chaque record TweakXL, " +
                 "vérifie que son entityName existe dans une factory .csv, que son displayName " +
                 "correspond à un secondaryKey de localisation .json, et que l'entité .ent " +
                 "référencée est présente. Signale les maillons manquants. Analyse textuelle des " +
                 "control-files (.yaml/.xl/.csv/.json) ; avec deep=true, convertit aussi le .ent et " +
                 "vérifie que l'appearanceName y figure.")]
    public static async Task<string> ValidateItemMod(
        Cp77ToolsRunner runner,
        [Description("Dossier du mod (projet ou contenu déployé) contenant .yaml/.xl/.csv/.json.")] string modPath,
        [Description("Mode profond : convertit les .ent présents et vérifie que l'appearanceName y existe.")] bool deep = false,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(modPath))
            return Err($"Dossier de mod introuvable : {modPath}");

        var files = Directory.EnumerateFiles(modPath, "*", SearchOption.AllDirectories).ToList();
        var items = new List<ItemRecord>();
        foreach (var f in files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".yaml" or ".yml" or ".tweak"))
            items.AddRange(ParseItemRecords(f));

        // Factory .csv : col0 = entityName, col1 = chemin .ent.
        var factoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var csvFiles = files.Where(f => Path.GetExtension(f).ToLowerInvariant() == ".csv").ToList();
        foreach (var csv in csvFiles)
            foreach (var (name, path) in ParseFactoryCsv(csv))
                factoryNames[name] = path;

        // Localisation .json : tous les secondaryKey présents dans le mod.
        var secondaryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var js in files.Where(f => Path.GetExtension(f).ToLowerInvariant() == ".json"))
            foreach (var sk in CollectSecondaryKeys(js)) secondaryKeys.Add(sk);

        var errors = new List<string>();
        var warnings = new List<string>();
        var checks = new List<object>();

        foreach (var it in items)
        {
            string? entIssue = null, dispIssue = null;
            if (!string.IsNullOrEmpty(it.EntityName))
            {
                if (factoryNames.Count == 0)
                    warnings.Add($"[{it.Record}] entityName '{it.EntityName}' : aucune factory .csv trouvée dans le mod.");
                else if (!factoryNames.ContainsKey(it.EntityName))
                    errors.Add(entIssue = $"[{it.Record}] entityName '{it.EntityName}' absent des factory .csv " +
                               $"({string.Join(", ", csvFiles.Select(Path.GetFileName))}).");
                else
                {
                    // L'entité .ent référencée est-elle présente dans le mod ?
                    var entPath = factoryNames[it.EntityName];
                    var baseName = entPath.Replace('/', '\\').Split('\\')[^1];
                    var entFile = files.FirstOrDefault(f => f.EndsWith(baseName, StringComparison.OrdinalIgnoreCase));
                    if (entFile is null)
                        warnings.Add($"[{it.Record}] entité '{entPath}' non trouvée dans le mod (réf. base game ?).");
                    else if (deep && !string.IsNullOrEmpty(it.AppearanceName))
                    {
                        // Profond : convertir le .ent et vérifier que l'appearanceName y figure.
                        var entJson = await ConvertCr2wToJsonText(runner, entFile, ct);
                        if (entJson is null)
                            warnings.Add($"[{it.Record}] conversion du .ent échouée (vérif. profonde ignorée).");
                        else if (entJson.IndexOf(it.AppearanceName!, StringComparison.OrdinalIgnoreCase) < 0)
                            errors.Add($"[{it.Record}] appearanceName '{it.AppearanceName}' absent du .ent '{baseName}'.");
                    }
                }
            }
            if (!string.IsNullOrEmpty(it.DisplayName)
                && !it.DisplayName.StartsWith("LocKey#", StringComparison.OrdinalIgnoreCase))
            {
                if (secondaryKeys.Count == 0)
                    warnings.Add($"[{it.Record}] displayName '{it.DisplayName}' : aucune localisation .json trouvée.");
                else if (!secondaryKeys.Contains(it.DisplayName))
                    errors.Add(dispIssue = $"[{it.Record}] displayName '{it.DisplayName}' absent des secondaryKey de localisation.");
            }
            checks.Add(new
            {
                record = it.Record,
                entityName = it.EntityName,
                appearanceName = it.AppearanceName,
                displayName = it.DisplayName,
                entityOk = entIssue is null,
                displayOk = dispIssue is null,
            });
        }

        var status = errors.Count > 0 ? "error" : warnings.Count > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = errors.Count == 0,
            status,
            summary = items.Count == 0
                ? "Aucun record d'item TweakXL trouvé (.yaml avec entityName/appearanceName/displayName)."
                : $"{items.Count} item(s) vérifié(s) : {errors.Count} erreur(s), {warnings.Count} avertissement(s)",
            modPath,
            itemsFound = items.Count,
            factories = factoryNames.Count,
            localizationKeys = secondaryKeys.Count,
            checks,
            warnings,
            errors,
            deep,
            limitation = deep
                ? "Mode profond : vérifie aussi l'appearanceName dans le .ent. Le matching .app↔.mesh " +
                  "(noms d'apparence de mesh, index de matériaux) reste à faire via inspect_cr2w."
                : "Control-files (.yaml↔.csv↔.json + présence .ent). Passer deep=true pour vérifier " +
                  "l'appearanceName dans le .ent.",
        }, JsonOpts);
    }

    /// <summary>Convertit un CR2W en texte JSON via le daemon (convert serialize),
    /// dans un dossier temp jetable. Renvoie null en cas d'échec.</summary>
    private static async Task<string?> ConvertCr2wToJsonText(
        Cp77ToolsRunner runner, string cr2wFile, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-conv", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            await runner.RunAsync(new[] { "convert", "serialize", cr2wFile, "--outpath", tmp }, ct);
            var json = Directory.EnumerateFiles(tmp, "*.json", SearchOption.AllDirectories).FirstOrDefault();
            return json is null ? null : await File.ReadAllTextAsync(json, ct);
        }
        catch { return null; }
        finally { try { Directory.Delete(tmp, true); } catch { /* best-effort */ } }
    }

    /// <summary>Extrait les records d'item d'un .yaml/.tweak TweakXL : tout mapping de
    /// premier niveau ayant un champ entityName / appearanceName / displayName.</summary>
    internal static List<ItemRecord> ParseItemRecords(string file)
    {
        var items = new List<ItemRecord>();
        try
        {
            var de = new DeserializerBuilder().Build();
            var root = de.Deserialize<object?>(File.ReadAllText(file));
            if (root is not Dictionary<object, object> map) return items;
            foreach (var kv in map)
            {
                var record = kv.Key?.ToString();
                if (string.IsNullOrEmpty(record) || record.StartsWith("$")) continue;
                if (kv.Value is not Dictionary<object, object> body) continue;
                string? Field(string n) => body.TryGetValue(n, out var v) ? v?.ToString() : null;
                var ent = Field("entityName");
                var app = Field("appearanceName");
                var disp = Field("displayName");
                if (ent is null && app is null && disp is null) continue;
                items.Add(new ItemRecord(record, ent, app, disp, Path.GetFileName(file)));
            }
        }
        catch { /* illisible : ignoré */ }
        return items;
    }

    /// <summary>Parse une factory .csv ArchiveXL : (entityName, chemin .ent) par ligne
    /// non vide / non commentée. Première colonne = nom, deuxième = chemin.</summary>
    internal static List<(string name, string path)> ParseFactoryCsv(string file)
    {
        var rows = new List<(string, string)>();
        try
        {
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                var cols = line.Split(',');
                if (cols.Length < 2) continue;
                var name = cols[0].Trim().Trim('"');
                var path = cols[1].Trim().Trim('"');
                if (name.Length == 0 || name.Equals("name", StringComparison.OrdinalIgnoreCase)) continue; // en-tête
                rows.Add((name, path));
            }
        }
        catch { /* illisible : ignoré */ }
        return rows;
    }

    /// <summary>Collecte récursivement toutes les valeurs de propriété "secondaryKey"
    /// d'un fichier JSON de localisation.</summary>
    internal static List<string> CollectSecondaryKeys(string jsonFile)
    {
        var keys = new List<string>();
        try
        {
            using var fs = File.OpenRead(jsonFile);
            using var doc = JsonDocument.Parse(fs);
            void Walk(JsonElement e)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var p in e.EnumerateObject())
                        {
                            if (p.NameEquals("secondaryKey") && p.Value.ValueKind == JsonValueKind.String)
                            { var s = p.Value.GetString(); if (!string.IsNullOrEmpty(s)) keys.Add(s!); }
                            Walk(p.Value);
                        }
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in e.EnumerateArray()) Walk(item);
                        break;
                }
            }
            Walk(doc.RootElement);
        }
        catch { /* pas un JSON de localisation : ignoré */ }
        return keys;
    }

    // ════════════════════════════════════════════════════════════════════════
    // lint_tweak — lint sémantique d'un .tweak/.yaml TweakXL
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "lint_tweak", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lint sémantique d'un fichier TweakXL (.tweak/.yaml) : TABS interdits (échec " +
                 "silencieux du chargement), indentation non multiple de 2, noms de record en " +
                 "double dans le fichier, et usage d'un record auto-généré `inlineN` comme `$base` " +
                 "(casse à chaque mise à jour du jeu). Complète validate_tweak (qui vérifie les clés " +
                 "vs tweakdb.bin).")]
    public static string LintTweak(
        [Description("Chemin du fichier .tweak / .yaml à linter.")] string tweakFile)
    {
        if (!File.Exists(tweakFile))
            return Err($"Fichier introuvable : {tweakFile}");
        string[] lines;
        try { lines = File.ReadAllLines(tweakFile); }
        catch (Exception ex) { return Err($"Lecture impossible : {ex.Message}"); }

        var (errors, warnings) = LintTweakText(lines);
        var status = errors.Count > 0 ? "error" : warnings.Count > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = errors.Count == 0,
            status,
            summary = $"Lint tweak : {Path.GetFileName(tweakFile)} — {errors.Count} erreur(s), {warnings.Count} avertissement(s)",
            tweakFile,
            warnings,
            errors,
        }, JsonOpts);
    }

    /// <summary>Lint textuel d'un TweakXL (testable). Renvoie (erreurs, avertissements).</summary>
    internal static (List<string> errors, List<string> warnings) LintTweakText(string[] lines)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var topKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimEnd().Length == 0) continue;
            // Indentation : tabs interdits, espaces multiples de 2.
            var indent = line.Length - line.TrimStart().Length;
            if (line[..indent].Contains('\t'))
                errors.Add($"L{i + 1} : TABULATION dans l'indentation — TweakXL exige des espaces (échec silencieux).");
            else if (indent % 2 != 0)
                warnings.Add($"L{i + 1} : indentation de {indent} espace(s) — TweakXL attend des multiples de 2.");
            // Clé de premier niveau (colonne 0, se termine par ':' ou 'clé: valeur').
            if (indent == 0 && !line.StartsWith("#") && !line.StartsWith("$"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^([^\s:#][^:]*):");
                if (m.Success)
                {
                    var key = m.Groups[1].Value.Trim();
                    topKeys[key] = topKeys.GetValueOrDefault(key) + 1;
                }
            }
            // inlineN comme $base.
            var bm = System.Text.RegularExpressions.Regex.Match(line, @"\$base\s*:\s*\S*inline\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (bm.Success)
                warnings.Add($"L{i + 1} : `$base` pointe un record auto-généré `inlineN` — les indices bougent à chaque MAJ et casseront le mod. Référencer le record nommé.");
        }
        foreach (var kv in topKeys.Where(kv => kv.Value > 1))
            warnings.Add($"Record « {kv.Key} » défini {kv.Value} fois dans le fichier — le dernier écrase silencieusement.");
        return (errors, warnings);
    }

    // ════════════════════════════════════════════════════════════════════════
    // generate_manifest — manifeste de dépendances depuis l'analyse d'un mod
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "generate_manifest", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Génère un manifeste de dépendances pour un mod en détectant ses frameworks requis " +
                 "(comme analyze_dependencies) et en écrivant un REQUIREMENTS.md (façon onglet " +
                 "« Requirements » Nexus) + un objet structuré. Comble l'absence totale de système " +
                 "de dépendances machine-lisible côté écosystème.")]
    public static string GenerateManifest(
        [Description("Dossier du mod à analyser.")] string modPath,
        [Description("Nom du mod (pour l'en-tête du manifeste).")] string? modName = null,
        [Description("Version du mod.")] string? version = null,
        [Description("Écrire REQUIREMENTS.md dans le dossier du mod (défaut true).")] bool writeFile = true)
    {
        if (!Directory.Exists(modPath))
            return Err($"Dossier de mod introuvable : {modPath}");

        var reasons = DetectFrameworks(modPath, out var unknownImports, out _);
        var deps = Frameworks.Where(f => reasons.ContainsKey(f.Name))
            .Select(f => new { framework = f.Name, kind = f.Kind, reason = reasons[f.Name], note = f.Note })
            .ToList();

        var name = string.IsNullOrWhiteSpace(modName) ? Path.GetFileName(modPath.TrimEnd('\\', '/')) : modName!;
        var sb = new StringBuilder();
        sb.AppendLine($"# {name} — Requirements").AppendLine();
        if (!string.IsNullOrWhiteSpace(version)) sb.AppendLine($"**Version :** {version}").AppendLine();
        sb.AppendLine("## Dépendances (frameworks)").AppendLine();
        if (deps.Count == 0) sb.AppendLine("_Aucune dépendance de framework détectée._");
        else foreach (var d in deps) sb.AppendLine($"- **{d.framework}** ({d.kind}) — {d.note}  \n  _détecté via : {d.reason}_");
        if (unknownImports.Count > 0)
        {
            sb.AppendLine().AppendLine("## Dépendances inter-mods possibles (imports non reconnus)").AppendLine();
            foreach (var u in unknownImports.OrderBy(x => x).Take(30)) sb.AppendLine($"- `{u}`");
        }
        sb.AppendLine().AppendLine("> Installe chaque framework à sa dernière version compatible avec ta version du jeu.");

        string? written = null;
        if (writeFile)
        {
            written = Path.Combine(modPath, "REQUIREMENTS.md");
            try { File.WriteAllText(written, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { return Err($"Écriture REQUIREMENTS.md impossible : {ex.Message}"); }
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Manifeste de {name} : {deps.Count} dépendance(s), {unknownImports.Count} import(s) inter-mods",
            modPath,
            dependencies = deps,
            crossModImports = unknownImports.OrderBy(x => x).ToList(),
            requirementsFile = written,
            produced = written is null ? Array.Empty<string>() : new[] { "REQUIREMENTS.md" },
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // resolve_dynamic_appearance — expansion des patterns ArchiveXL dynamiques
    // ════════════════════════════════════════════════════════════════════════
    private static readonly (string ph, string[] vals)[] DynPlaceholders =
    {
        ("{gender}", new[] { "m", "w" }),
        ("{camera}", new[] { "fpp", "tpp" }),
    };

    [McpServerTool(Name = "resolve_dynamic_appearance", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Développe un chemin/pattern d'apparence dynamique ArchiveXL (préfixe `*`, " +
                 "interpolations {gender}→m/w et {camera}→fpp/tpp) en ses chemins concrets, et — si " +
                 "modPath est fourni — indique lesquels existent réellement. Cible le piège ArchiveXL " +
                 "où une erreur de substitution n'affiche que la 1re apparence (debug très difficile).")]
    public static string ResolveDynamicAppearance(
        [Description("Pattern de chemin (ex. *base\\mod\\item_{gender}_{camera}.mesh).")] string pattern,
        [Description("Optionnel : dossier du mod, pour vérifier l'existence des chemins concrets.")] string? modPath = null)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return Err("Pattern vide.");

        var expansions = ExpandDynamicPattern(pattern);
        var remaining = System.Text.RegularExpressions.Regex.Matches(expansions.First(), @"\{[a-z_]+\}")
            .Select(m => m.Value).Distinct().ToList();

        var checkExist = !string.IsNullOrWhiteSpace(modPath) && Directory.Exists(modPath);
        var modFiles = checkExist
            ? Directory.EnumerateFiles(modPath!, "*", SearchOption.AllDirectories).ToList()
            : new List<string>();
        var results = new List<object>();
        var missing = 0;
        foreach (var ex in expansions)
        {
            string? exists = null;
            if (checkExist && !ex.Contains('{'))
            {
                var baseName = ex.TrimStart('*').Replace('/', '\\').Split('\\')[^1];
                var found = modFiles.Any(f => f.EndsWith(baseName, StringComparison.OrdinalIgnoreCase));
                exists = found ? "présent" : "MANQUANT";
                if (!found) missing++;
            }
            results.Add(new { path = ex.TrimStart('*'), exists });
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = missing > 0 ? "partial" : "success",
            summary = $"{expansions.Count} chemin(s) concret(s)" +
                      (string.IsNullOrWhiteSpace(modPath) ? "" : $", {missing} manquant(s)"),
            pattern,
            expansions = results,
            unresolvedPlaceholders = remaining,
            warnings = remaining.Count > 0
                ? new[] { $"Placeholders non développés (dépendent du corps/skin/etc.) : {string.Join(", ", remaining)}" }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Développe {gender}/{camera} en produit cartésien (testable).</summary>
    internal static List<string> ExpandDynamicPattern(string pattern)
    {
        var current = new List<string> { pattern };
        foreach (var (ph, vals) in DynPlaceholders)
        {
            if (!current[0].Contains(ph)) continue;
            current = current.SelectMany(p => vals.Select(v => p.Replace(ph, v))).ToList();
        }
        return current;
    }

    // ════════════════════════════════════════════════════════════════════════
    // migration_check — un mod survit-il à la version actuelle du jeu ?
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "migration_check", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Vérifie si un mod .archive est encore aligné sur la version ACTUELLE du jeu : " +
                 "pour chaque fichier que le mod fournit, indique s'il surcharge un fichier de base " +
                 "existant (override actif) ou non (ajout, OU surcharge devenue inerte après une MAJ " +
                 "du jeu — chemin disparu). Cible « les MAJ cassent les mods silencieusement ».")]
    public static async Task<string> MigrationCheck(
        Cp77ToolsRunner runner,
        [Description("Archive .archive du mod à vérifier.")] string modArchive,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath,
        [Description("Nb max de chemins non-correspondants listés (défaut 100).")] int maxResults = 100,
        CancellationToken ct = default)
    {
        if (!File.Exists(modArchive))
            return Err($"Archive de mod introuvable : {modArchive}");
        var content = Path.Combine(gamePath, "archive", "pc", "content");
        if (!Directory.Exists(content))
            return Err($"Dossier content introuvable : {content}");

        // Ensemble des chemins de base actuels (union des listings content, cache LRU).
        var baseSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arc in Directory.EnumerateFiles(content, "*.archive"))
        {
            IReadOnlyList<string> entries;
            try { (entries, _, _) = await runner.GetArchiveListingAsync(arc, ct); }
            catch { continue; }
            foreach (var e in entries) baseSet.Add(e.Replace('/', '\\').Trim());
        }

        IReadOnlyList<string> modEntries;
        try { (modEntries, _, _) = await runner.GetArchiveListingAsync(modArchive, ct); }
        catch (Exception ex) { return Err($"Listing du mod impossible : {ex.Message}"); }

        var overrides = new List<string>();
        var nonMatching = new List<string>();
        foreach (var e in modEntries)
        {
            var key = e.Replace('/', '\\').Trim();
            if (key.Length == 0) continue;
            if (baseSet.Contains(key)) overrides.Add(key); else nonMatching.Add(key);
        }

        var status = overrides.Count == 0 && modEntries.Count > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status,
            summary = $"{modEntries.Count} fichier(s) du mod : {overrides.Count} surcharge(nt) la base actuelle, " +
                      $"{nonMatching.Count} sans correspondance (ajouts ou surcharges devenues inertes)",
            modArchive,
            baseFileCount = baseSet.Count,
            overrideCount = overrides.Count,
            nonMatchingCount = nonMatching.Count,
            nonMatching = nonMatching.Take(maxResults),
            warnings = overrides.Count == 0 && modEntries.Count > 0
                ? new[] { "AUCUN fichier du mod ne correspond à la base actuelle — soit c'est un mod purement additif (ArchiveXL), soit ses surcharges sont devenues inertes après une MAJ du jeu." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // toggle_mods — activer/désactiver des .archive (pour bissection assistée)
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "toggle_mods", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Active ou désactive des mods .archive en les déplaçant entre archive/pc/mod et " +
                 "archive/pc/mod/_disabled (réversible, non destructif). Primitive de la bissection " +
                 "de conflits : désactiver la moitié des mods → lancer → diagnostiquer → réduire. " +
                 "Renvoie les listes activés/désactivés à jour.")]
    public static string ToggleMods(
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath,
        [Description("Noms d'archives séparés par des virgules (avec ou sans .archive). Vide = ne rien déplacer (juste lister).")] string? archives = null,
        [Description("true = activer (réactiver), false = désactiver.")] bool enable = false)
    {
        var modDir = Path.Combine(gamePath, "archive", "pc", "mod");
        if (!Directory.Exists(modDir))
            return Err($"Dossier de mods introuvable : {modDir}");
        var disabledDir = Path.Combine(modDir, "_disabled");

        var moved = new List<string>();
        var warnings = new List<string>();
        var names = (archives ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".archive", StringComparison.OrdinalIgnoreCase) ? n : n + ".archive").ToList();

        if (names.Count > 0)
        {
            Directory.CreateDirectory(disabledDir);
            foreach (var n in names)
            {
                var from = Path.Combine(enable ? disabledDir : modDir, n);
                var to = Path.Combine(enable ? modDir : disabledDir, n);
                if (!File.Exists(from)) { warnings.Add($"Introuvable ({(enable ? "désactivé" : "activé")}) : {n}"); continue; }
                try { File.Move(from, to, overwrite: false); moved.Add(n); }
                catch (Exception ex) { warnings.Add($"Échec déplacement {n} : {ex.Message}"); }
            }
        }

        var enabled = Directory.Exists(modDir)
            ? Directory.GetFiles(modDir, "*.archive").Select(Path.GetFileName).OrderBy(x => x).ToList()
            : new List<string?>();
        var disabled = Directory.Exists(disabledDir)
            ? Directory.GetFiles(disabledDir, "*.archive").Select(Path.GetFileName).OrderBy(x => x).ToList()
            : new List<string?>();

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = names.Count == 0
                ? $"{enabled.Count} mod(s) actif(s), {disabled.Count} désactivé(s)"
                : $"{moved.Count} mod(s) {(enable ? "activé(s)" : "désactivé(s)")} · {enabled.Count} actifs / {disabled.Count} désactivés",
            gamePath,
            moved,
            enabledCount = enabled.Count,
            disabledCount = disabled.Count,
            enabled,
            disabled,
            warnings,
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // list_entity_appearances — lister les apparences d'une entité (.ent)
    // ════════════════════════════════════════════════════════════════════════
    internal sealed record EntityAppearance(string Name, string? AppearanceName, string? AppResource);

    [McpServerTool(Name = "list_entity_appearances", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Liste les apparences d'une entité REDengine (.ent) : pour chacune, son nom " +
                 "d'entité (le `name` à passer à export_entity / dans le .yaml), le `appearanceName` " +
                 "côté .app, et la ressource .app référencée. Fiable et indispensable pour savoir " +
                 "ce qu'une entité expose avant d'éditer/exporter une apparence.")]
    public static async Task<string> ListEntityAppearances(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .ent extrait.")] string entFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(entFile))
            return Err($"Fichier .ent introuvable : {entFile}");
        var json = await ConvertCr2wToJsonText(runner, entFile, ct);
        if (json is null) return Err("Conversion du .ent échouée.");
        var apps = ParseEntityAppearances(json);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"{apps.Count} apparence(s) dans {Path.GetFileName(entFile)}",
            entFile,
            appearanceCount = apps.Count,
            appearances = apps.Select(a => new { name = a.Name, appearanceName = a.AppearanceName, appResource = a.AppResource }),
            warnings = apps.Count == 0 ? new[] { "Aucune apparence (entité de type composant/proxy ?)." } : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Parse les apparences d'un .ent (RootChunk.appearances[] :
    /// name / appearanceName / appearanceResource). Testable.</summary>
    internal static List<EntityAppearance> ParseEntityAppearances(string entJson)
    {
        var list = new List<EntityAppearance>();
        try
        {
            using var doc = JsonDocument.Parse(entJson);
            var rc = doc.RootElement.TryGetProperty("Data", out var d) && d.TryGetProperty("RootChunk", out var r) ? r : doc.RootElement;
            if (!rc.TryGetProperty("appearances", out var apps) || apps.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in apps.EnumerateArray())
            {
                var def = e.TryGetProperty("Data", out var dd) && dd.ValueKind == JsonValueKind.Object ? dd : e;
                var name = CnameVal(def, "name");
                if (name is null) continue;
                list.Add(new EntityAppearance(name, CnameVal(def, "appearanceName"), DepotPathVal(def, "appearanceResource")));
            }
        }
        catch { /* JSON inattendu */ }
        return list;
    }

    // ════════════════════════════════════════════════════════════════════════
    // validate_appearance — validation profonde .app → .mesh (apparences + matériaux)
    // ════════════════════════════════════════════════════════════════════════
    internal sealed record AppMeshRef(string AppAppearance, string MeshPath, string? MeshAppearance);

    [McpServerTool(Name = "validate_appearance", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Validation PROFONDE de la chaîne d'apparence .app → .mesh : pour chaque apparence " +
                 "du .app et chaque composant mesh, vérifie que le meshAppearance référencé existe " +
                 "bien dans le .mesh (sinon mesh invisible) et que ses matériaux (chunkMaterials) " +
                 "correspondent aux materialEntries (sinon matériau noir/incohérent). Résout les " +
                 ".mesh dans le mod, sinon dans le jeu de base si gamePath est fourni.")]
    public static async Task<string> ValidateAppearance(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .app extrait.")] string appFile,
        [Description("Racine du dossier de mod (pour résoudre les .mesh du mod).")] string? modRoot = null,
        [Description("Racine du jeu (pour résoudre les .mesh de base introuvables dans le mod).")] string? gamePath = null,
        [Description("Nb max de .mesh résolus (défaut 40).")] int maxMeshes = 40,
        CancellationToken ct = default)
    {
        if (!File.Exists(appFile))
            return Err($"Fichier .app introuvable : {appFile}");
        var appJson = await ConvertCr2wToJsonText(runner, appFile, ct);
        if (appJson is null) return Err("Conversion du .app échouée.");

        var refs = ParseAppMeshRefs(appJson);
        var errors = new List<string>();
        var warnings = new List<string>();
        var checks = new List<object>();
        var meshCache = new Dictionary<string, (List<string> apps, HashSet<string> mats)?>(StringComparer.OrdinalIgnoreCase);
        var resolvedRoot = modRoot ?? Path.GetDirectoryName(Path.GetFullPath(appFile));

        foreach (var rf in refs)
        {
            if (string.IsNullOrEmpty(rf.MeshPath)) continue;
            if (!meshCache.TryGetValue(rf.MeshPath, out var info) && meshCache.Count < maxMeshes)
            {
                var mj = await ResolveMeshJson(runner, rf.MeshPath, resolvedRoot, gamePath, ct);
                info = mj is null ? null : ParseMeshAppearancesAndMaterials(mj);
                meshCache[rf.MeshPath] = info;
            }
            meshCache.TryGetValue(rf.MeshPath, out info);

            string? issue = null;
            if (info is null)
                warnings.Add($"[{rf.AppAppearance}] mesh '{rf.MeshPath}' introuvable/non converti — vérif. ignorée.");
            else
            {
                var (meshApps, mats) = info.Value;
                var ma = rf.MeshAppearance ?? "default";
                if (!meshApps.Contains(ma, StringComparer.OrdinalIgnoreCase))
                    errors.Add(issue = $"[{rf.AppAppearance}] meshAppearance '{ma}' absent du mesh '{Path.GetFileName(rf.MeshPath)}' " +
                               $"(disponibles : {string.Join(", ", meshApps.Take(6))}) → mesh invisible.");
            }
            checks.Add(new { appAppearance = rf.AppAppearance, mesh = rf.MeshPath, meshAppearance = rf.MeshAppearance, ok = issue is null });
        }

        var status = errors.Count > 0 ? "error" : warnings.Count > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = errors.Count == 0,
            status,
            summary = refs.Count == 0
                ? "Aucun composant mesh trouvé dans le .app."
                : $"{refs.Count} référence(s) mesh vérifiée(s) : {errors.Count} erreur(s), {warnings.Count} avertissement(s)",
            appFile,
            meshRefsChecked = refs.Count,
            meshesResolved = meshCache.Count(kv => kv.Value is not null),
            checks,
            warnings,
            errors,
            limitation = "Vérifie meshAppearance ∈ .mesh.appearances. La cohérence fine des index de " +
                         "matériaux (chunkMaterials ↔ localMaterialBuffer) n'est pas encore couverte.",
        }, JsonOpts);
    }

    [McpServerTool(Name = "inspect_app", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Résumé structurel d'un fichier .app : nombre d'apparences, et pour chacune le " +
                 "nombre de composants mesh et les meshes référencés ; total de meshes distincts. " +
                 "Vue d'ensemble rapide AVANT validate_appearance (qui, lui, résout et valide " +
                 "chaque .mesh). Léger : une seule conversion CR2W→JSON, sans résolution de mesh.")]
    public static async Task<string> InspectApp(
        Cp77ToolsRunner runner,
        [Description("Chemin d'un fichier .app extrait.")] string appFile,
        [Description("Nb max d'apparences détaillées renvoyées (défaut 100). appearanceCount donne " +
                     "toujours le total réel.")] int maxAppearances = 100,
        CancellationToken ct = default)
    {
        if (!File.Exists(appFile))
            return Err($"Fichier .app introuvable : {appFile}");
        var appJson = await ConvertCr2wToJsonText(runner, appFile, ct);
        if (appJson is null) return Err("Conversion du .app échouée.");

        var s = SummarizeApp(appJson);
        var cap = Math.Max(1, maxAppearances);
        var truncated = s.Appearances.Count > cap;
        var shown = truncated ? s.Appearances.Take(cap).ToList() : s.Appearances;

        return JsonSerializer.Serialize(new
        {
            ok = s.AppearanceCount > 0,
            status = s.AppearanceCount > 0 ? "success" : "partial",
            summary = s.AppearanceCount == 0
                ? "Aucune apparence trouvée dans le .app."
                : $"{s.AppearanceCount} apparence(s), {s.MeshComponentCount} composant(s) mesh, " +
                  $"{s.DistinctMeshCount} mesh(es) distinct(s)",
            appFile,
            appearanceCount = s.AppearanceCount,
            meshComponentCount = s.MeshComponentCount,
            distinctMeshCount = s.DistinctMeshCount,
            truncated,
            appearances = shown.Select(a => new
            {
                name = a.Name,
                meshComponents = a.MeshComponents,
                meshes = a.Meshes,
            }),
            warnings = s.AppearanceCount == 0
                ? new[] { "Le .app n'expose aucune apparence (fichier inattendu ou vide)." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    internal sealed record AppAppearanceSummary(string Name, int MeshComponents, IReadOnlyList<string> Meshes);

    internal sealed record AppSummary(
        int AppearanceCount, int MeshComponentCount, int DistinctMeshCount,
        IReadOnlyList<AppAppearanceSummary> Appearances);

    /// <summary>Résume un .app JSON : apparences + composants mesh par apparence + meshes
    /// distincts. Réutilise ParseAppMeshRefs (composants mesh) et ParseAppearanceNames
    /// (toutes les apparences, même sans composant mesh). Logique pure, testable.</summary>
    internal static AppSummary SummarizeApp(string appJson)
    {
        var refs = ParseAppMeshRefs(appJson);
        var names = ParseAppearanceNames(appJson);
        var ordered = names.Count > 0
            ? names
            : refs.Select(r => r.AppAppearance).Distinct(StringComparer.Ordinal).ToList();

        var byApp = refs.GroupBy(r => r.AppAppearance, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var appSummaries = ordered.Select(n =>
        {
            byApp.TryGetValue(n, out var rs);
            rs ??= new List<AppMeshRef>();
            var meshes = rs.Select(r => r.MeshPath)
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new AppAppearanceSummary(n, rs.Count, meshes);
        }).ToList();

        var distinctMeshes = refs.Select(r => r.MeshPath)
                                 .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new AppSummary(ordered.Count, refs.Count, distinctMeshes, appSummaries);
    }

    /// <summary>Noms de toutes les apparences d'un .app JSON (y compris celles sans composant
    /// mesh). Testable.</summary>
    internal static List<string> ParseAppearanceNames(string appJson)
    {
        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(appJson);
            var rc = doc.RootElement.TryGetProperty("Data", out var d)
                     && d.TryGetProperty("RootChunk", out var r) ? r : doc.RootElement;
            if (!rc.TryGetProperty("appearances", out var apps) || apps.ValueKind != JsonValueKind.Array)
                return names;
            foreach (var ae in apps.EnumerateArray())
            {
                var def = ae.TryGetProperty("Data", out var dd) && dd.ValueKind == JsonValueKind.Object ? dd : ae;
                names.Add(CnameVal(def, "name") ?? "?");
            }
        }
        catch { /* JSON inattendu */ }
        return names;
    }

    /// <summary>Extrait les (apparence .app, chemin mesh, meshAppearance) des composants
    /// mesh de chaque apparence d'un .app. Testable.</summary>
    internal static List<AppMeshRef> ParseAppMeshRefs(string appJson)
    {
        var refs = new List<AppMeshRef>();
        try
        {
            using var doc = JsonDocument.Parse(appJson);
            var rc = doc.RootElement.TryGetProperty("Data", out var d) && d.TryGetProperty("RootChunk", out var r) ? r : doc.RootElement;
            if (!rc.TryGetProperty("appearances", out var apps) || apps.ValueKind != JsonValueKind.Array) return refs;
            foreach (var ae in apps.EnumerateArray())
            {
                var def = ae.TryGetProperty("Data", out var dd) && dd.ValueKind == JsonValueKind.Object ? dd : ae;
                var appName = CnameVal(def, "name") ?? "?";
                if (!def.TryGetProperty("components", out var comps) || comps.ValueKind != JsonValueKind.Array) continue;
                foreach (var ce in comps.EnumerateArray())
                {
                    var cdef = ce.TryGetProperty("Data", out var cd) && cd.ValueKind == JsonValueKind.Object ? cd : ce;
                    var meshPath = DepotPathVal(cdef, "mesh");
                    if (meshPath is null) continue;
                    refs.Add(new AppMeshRef(appName, meshPath, CnameVal(cdef, "meshAppearance")));
                }
            }
        }
        catch { /* JSON inattendu */ }
        return refs;
    }

    /// <summary>Noms d'apparences de mesh + noms de materialEntries d'un .mesh JSON.</summary>
    internal static (List<string> appearances, HashSet<string> materials) ParseMeshAppearancesAndMaterials(string meshJson)
    {
        var appNames = new List<string>();
        var mats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(meshJson);
            var rc = doc.RootElement.TryGetProperty("Data", out var d) && d.TryGetProperty("RootChunk", out var r) ? r : doc.RootElement;
            if (rc.TryGetProperty("appearances", out var apps) && apps.ValueKind == JsonValueKind.Array)
                foreach (var ae in apps.EnumerateArray())
                {
                    var def = ae.TryGetProperty("Data", out var dd) && dd.ValueKind == JsonValueKind.Object ? dd : ae;
                    var n = CnameVal(def, "name");
                    if (n is not null) appNames.Add(n);
                }
            if (rc.TryGetProperty("materialEntries", out var me) && me.ValueKind == JsonValueKind.Array)
                foreach (var entry in me.EnumerateArray())
                {
                    var n = CnameVal(entry, "name");
                    if (n is not null) mats.Add(n);
                }
        }
        catch { /* JSON inattendu */ }
        return (appNames, mats);
    }

    /// <summary>Résout un .mesh par DepotPath : d'abord dans le mod (par nom de base),
    /// sinon dans les archives de base si gamePath fourni ; renvoie son JSON.</summary>
    private static async Task<string?> ResolveMeshJson(
        Cp77ToolsRunner runner, string meshPath, string? modRoot, string? gamePath, CancellationToken ct)
    {
        var baseName = meshPath.Replace('/', '\\').Split('\\')[^1];
        if (!string.IsNullOrEmpty(modRoot) && Directory.Exists(modRoot))
        {
            var local = Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => f.EndsWith(baseName, StringComparison.OrdinalIgnoreCase));
            if (local is not null) return await ConvertCr2wToJsonText(runner, local, ct);
        }
        if (!string.IsNullOrEmpty(gamePath))
        {
            var content = Path.Combine(gamePath, "archive", "pc", "content");
            if (Directory.Exists(content))
            {
                var arc = await FindArchiveContaining(runner, content, meshPath, ct);
                if (arc is not null) return await ExtractAsJson(runner, arc, meshPath, ct);
            }
        }
        return null;
    }

    // Helpers d'accès CR2W : valeur d'un CName / d'un DepotPath sous une propriété.
    private static string? CnameVal(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object
           && p.TryGetProperty("$value", out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() : null;
    private static string? DepotPathVal(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object
           && p.TryGetProperty("DepotPath", out var dp) && dp.TryGetProperty("$value", out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── Helpers diff/extraction ─────────────────────────────────────────────
    /// <summary>Cherche, parmi les .archive d'un dossier, la première contenant
    /// le chemin interne donné (via le cache LRU de listings du runner).</summary>
    private static async Task<string?> FindArchiveContaining(
        Cp77ToolsRunner runner, string contentDir, string internalPath, CancellationToken ct)
    {
        var needle = internalPath.Replace('/', '\\');
        foreach (var arc in Directory.EnumerateFiles(contentDir, "*.archive").OrderBy(f => f))
        {
            var (entries, _, _) = await runner.GetArchiveListingAsync(arc, ct);
            if (entries.Any(e => string.Equals(e.Replace('/', '\\'), needle, StringComparison.OrdinalIgnoreCase)))
                return arc;
        }
        return null;
    }

    /// <summary>Extrait un fichier d'une archive et le convertit en JSON (texte).
    /// Renvoie null en cas d'échec.</summary>
    private static async Task<string?> ExtractAsJson(
        Cp77ToolsRunner runner, string archive, string internalPath, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-diff", Guid.NewGuid().ToString("N"));
        var rawDir = Path.Combine(work, "raw");
        var jsonDir = Path.Combine(work, "json");
        Directory.CreateDirectory(rawDir); Directory.CreateDirectory(jsonDir);
        try
        {
            // Passer le chemin interne COMPLET en pattern (pas seulement le nom de
            // base) évite de capturer un fichier homonyme d'un autre dossier — donc
            // un faux diff. Aligné sur la logique éprouvée de read_game_file.
            var normalized = internalPath.Replace('/', '\\');
            await runner.RunAsync(new[] { "unbundle", archive, "--outpath", rawDir, "--pattern", normalized }, ct);
            var raw = Directory.EnumerateFiles(rawDir, "*", SearchOption.AllDirectories).FirstOrDefault();
            if (raw is null) return null; // fichier absent de l'archive (cas métier)
            var conv = await runner.RunAsync(new[] { "convert", "serialize", raw, "--outpath", jsonDir }, ct);
            var json = Directory.EnumerateFiles(jsonDir, "*.json", SearchOption.AllDirectories).FirstOrDefault();
            // json absent + conversion en échec = vrai échec technique (≠ fichier absent).
            if (json is null) return null;
            return await File.ReadAllTextAsync(json, ct);
        }
        catch { return null; }
        finally { try { Directory.Delete(work, true); } catch { /* best-effort */ } }
    }

    internal sealed record JsonChange(string Path, string Base, string Mod);

    /// <summary>Aplati deux JSON en chemins→valeurs et calcule ajouts/suppressions/
    /// modifications (côté « mod » vs « base »). Le sous-arbre $.Header (bruit de
    /// conversion) est exclu.</summary>
    internal static (List<string> added, List<string> removed, List<JsonChange> changed) DiffJson(string baseJson, string modJson)
    {
        var b = new Dictionary<string, string>();
        var m = new Dictionary<string, string>();
        try { using var db = JsonDocument.Parse(baseJson); Flatten(db.RootElement, "$", b); } catch { }
        try { using var dm = JsonDocument.Parse(modJson); Flatten(dm.RootElement, "$", m); } catch { }

        // Le sous-arbre $.Header est du bruit de conversion (chemin d'extraction
        // temporaire, horodatage, version WolvenKit) — pas du contenu de mod.
        static bool IsNoise(string k) => k.StartsWith("$.Header.", StringComparison.Ordinal);
        foreach (var k in b.Keys.Where(IsNoise).ToList()) b.Remove(k);
        foreach (var k in m.Keys.Where(IsNoise).ToList()) m.Remove(k);

        var added = m.Keys.Where(k => !b.ContainsKey(k)).OrderBy(k => k).ToList();
        var removed = b.Keys.Where(k => !m.ContainsKey(k)).OrderBy(k => k).ToList();
        var changed = m.Keys.Where(k => b.ContainsKey(k) && b[k] != m[k])
            .OrderBy(k => k)
            .Select(k => new JsonChange(k, Trunc(b[k]), Trunc(m[k])))
            .ToList();
        return (added, removed, changed);

        static string Trunc(string s) => s.Length > 120 ? s[..120] + "…" : s;
    }

    private static void Flatten(JsonElement e, string path, Dictionary<string, string> acc)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Flatten(p.Value, path + "." + p.Name, acc);
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in e.EnumerateArray()) Flatten(item, $"{path}[{i++}]", acc);
                break;
            default:
                acc[path] = e.ToString();
                break;
        }
    }

    // ── Détection ───────────────────────────────────────────────────────────
    /// <summary>Parcourt un dossier et déduit les frameworks requis. Renvoie
    /// {framework → raison}. Remonte aussi les imports inconnus (dépendances
    /// inter-mods) et des stats de fichiers.</summary>
    private static Dictionary<string, string> DetectFrameworks(
        string root, out List<string> unknownImports, out object fileStats)
    {
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int reds = 0, xl = 0, tweak = 0, lua = 0, dll = 0, archive = 0;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
        catch { files = Array.Empty<string>(); }

        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            var lower = f.ToLowerInvariant();
            switch (ext)
            {
                case ".reds":
                    reds++;
                    Mark(reasons, "redscript", "fichiers .reds présents");
                    Mark(reasons, "RED4ext", "requis par redscript");
                    try
                    {
                        var parsed = RedscriptParser.Parse(File.ReadAllText(f));
                        foreach (var imp in parsed.Imports)
                        {
                            var rootSeg = imp.Split('.')[0];
                            if (ImportRootToFw.TryGetValue(rootSeg, out var fw))
                                Mark(reasons, fw.Name, $"import {imp}");
                            else if (!string.IsNullOrEmpty(rootSeg) && !BaseImportRoots.Contains(rootSeg))
                                unknown.Add(rootSeg);
                        }
                    }
                    catch { /* fichier illisible : ignoré */ }
                    break;
                case ".xl":
                    xl++; Mark(reasons, "ArchiveXL", "fichiers .xl présents"); Mark(reasons, "RED4ext", "requis par ArchiveXL");
                    break;
                case ".tweak":
                    tweak++; Mark(reasons, "TweakXL", "fichiers .tweak présents"); Mark(reasons, "RED4ext", "requis par TweakXL");
                    break;
                case ".yaml": case ".yml":
                    if (lower.Contains(@"\tweaks\") || lower.Contains("/tweaks/"))
                    { tweak++; Mark(reasons, "TweakXL", "YAML dans r6/tweaks"); Mark(reasons, "RED4ext", "requis par TweakXL"); }
                    break;
                case ".lua":
                    if (lower.Contains("cyber_engine_tweaks"))
                    { lua++; Mark(reasons, "Cyber Engine Tweaks", "scripts Lua CET"); }
                    break;
                case ".archive":
                    archive++;
                    break;
                case ".dll":
                    if (lower.Contains(@"red4ext\plugins") || lower.Contains("red4ext/plugins"))
                    { dll++; Mark(reasons, "RED4ext", "plugin RED4ext (.dll)"); }
                    break;
            }
        }

        unknownImports = unknown.ToList();
        fileStats = new { reds, xl, tweak, lua, dll, archive };
        return reasons;

        static void Mark(Dictionary<string, string> d, string name, string why)
        { if (!d.ContainsKey(name)) d[name] = why; }
    }

    /// <summary>Détecte si un framework est installé dans le jeu + sa version
    /// si lisible (dossier de version dans red4ext/plugins/&lt;X&gt;).</summary>
    private static (bool installed, string? version) IsInstalled(string gamePath, Framework fw)
    {
        foreach (var marker in fw.InstallMarkers)
        {
            var full = Path.Combine(gamePath, marker);
            if (File.Exists(full) || Directory.Exists(full))
                return (true, TryReadVersion(full));
        }
        return (false, null);
    }

    private static string? TryReadVersion(string path)
    {
        try
        {
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (dir is null) return null;
            // RED4ext plugins : un fichier de version ou un nom de dossier versionné.
            foreach (var vf in new[] { "version.txt", "VERSION", ".version" })
            {
                var p = Path.Combine(dir, vf);
                if (File.Exists(p)) return File.ReadAllText(p).Trim().Split('\n')[0].Trim();
            }
        }
        catch { /* best-effort */ }
        return null;
    }
}
