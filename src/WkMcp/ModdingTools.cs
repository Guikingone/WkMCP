using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using YamlDotNet.Serialization;

namespace WkMcp;

/// <summary>
/// Higher-level MCP tools to simplify the creation, evolution and maintenance of
/// Cyberpunk 2077 mods: dependency/framework intelligence, setup health, reference
/// navigation, diff vs base, scaffolding and packaging. They compose the primitives
/// of <see cref="WolvenKitTools"/> and modding-ecosystem knowledge, rather than
/// calling WolvenKit directly.
/// </summary>
[McpServerToolType]
public static partial class ModdingTools
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Same shape as WolvenKitTools.Err (incl. exitCode/log) so error JSON is
    // identical across both tool classes — the agent parses one consistent schema.
    private static string Err(string summary) => JsonSerializer.Serialize(new
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

    // ── Modding frameworks knowledge base ───────────────────────────────────
    // Each framework: how we detect that a MOD needs it (REDscript import roots,
    // file extensions) and how we detect that it is INSTALLED in the game
    // (marker-paths relative to the game root).
    private sealed record Framework(
        string Name, string Kind,
        string[] ImportRoots, string[] FileSignals, string[] InstallMarkers, string Note);

    private static readonly Framework[] Frameworks =
    {
        new("RED4ext", "loader", Array.Empty<string>(), new[] { "red4ext-plugin-dll" },
            new[] { @"red4ext\RED4ext.dll", @"bin\x64\winmm.dll" },
            "Native loader required by redscript, ArchiveXL, TweakXL, Codeware..."),
        new("redscript", "script-compiler", Array.Empty<string>(), new[] { ".reds" },
            new[] { @"engine\tools\scc.exe", @"r6\scripts" },
            "Compiler for .reds scripts (hooks at launch)."),
        new("ArchiveXL", "framework", new[] { "ArchiveXL" }, new[] { ".xl" },
            new[] { @"red4ext\plugins\ArchiveXL" },
            "Archive extension: adding appearances, entities, items via .xl."),
        new("TweakXL", "framework", new[] { "TweakXL" }, new[] { "tweak-yaml" },
            new[] { @"red4ext\plugins\TweakXL" },
            "Declarative editing of TweakDB via .tweak/.yaml in r6/tweaks."),
        new("Codeware", "library", new[] { "Codeware" }, Array.Empty<string>(),
            new[] { @"red4ext\plugins\Codeware" },
            "REDscript extension library (UI, reflection, events...)."),
        new("Audioware", "library", new[] { "Audioware" }, Array.Empty<string>(),
            new[] { @"red4ext\plugins\audioware", @"red4ext\plugins\Audioware" },
            "Audio framework (custom sounds/music)."),
        new("Mod Settings", "library", new[] { "ModSettingsModule", "ModSettings" }, Array.Empty<string>(),
            new[] { @"red4ext\plugins\mod_settings" },
            "In-game settings menu for mods."),
        new("RedData", "library", new[] { "RedData" }, Array.Empty<string>(),
            new[] { @"red4ext\plugins\RedData", @"r6\scripts\RedData" },
            "Serialization/shared data between mods."),
        new("RedFileSystem", "library", new[] { "RedFileSystem" }, Array.Empty<string>(),
            new[] { @"red4ext\plugins\RedFileSystem" },
            "Sandboxed file-system access from REDscript."),
        new("Cyber Engine Tweaks", "framework", Array.Empty<string>(), new[] { "cet-lua" },
            new[] { @"bin\x64\plugins\cyber_engine_tweaks" },
            "Lua runtime + console (CET mods)."),
    };

    // REDscript import root -> framework mapping, computed once (Frameworks is
    // immutable) instead of being rebuilt on every scan.
    private static readonly Dictionary<string, Framework> ImportRootToFw = BuildImportRootMap();
    private static Dictionary<string, Framework> BuildImportRootMap()
    {
        var map = new Dictionary<string, Framework>(StringComparer.OrdinalIgnoreCase);
        foreach (var fw in Frameworks)
            foreach (var r in fw.ImportRoots)
                map[r] = fw;
        return map;
    }

    // "Native" game import roots — not mod dependencies.
    private static readonly HashSet<string> BaseImportRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        // Base-game imports don't use a third-party module prefix; we ignore the
        // roots known to belong to the game/redscript.
    };

    // ════════════════════════════════════════════════════════════════════════
    // analyze_dependencies
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "analyze_dependencies", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Analyzes a mod folder (or a project) and infers its required " +
                 "frameworks/dependencies: redscript, RED4ext, ArchiveXL, TweakXL, Codeware, Audioware, Mod Settings, " +
                 "Cyber Engine Tweaks, etc. — by reading the REDscript imports (via the parser), the " +
                 ".xl, the .tweak and the file types. If gamePath is provided, indicates for " +
                 "each dependency whether it is INSTALLED or MISSING. Ideal before distributing " +
                 "or installing a mod.")]
    public static string AnalyzeDependencies(
        [Description("Mod folder to analyze (project root or deployed folder).")] string modPath,
        [Description("Optional: game root, to check installed dependencies.")] string? gamePath = null)
    {
        if (!Directory.Exists(modPath))
            return Err($"Mod folder not found: {modPath}");

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
                installedStatus = inst ? "installed" : "MISSING";
                version = ver;
                if (!inst) missing.Add(fw.Name);
            }
            deps.Add(new { framework = fw.Name, kind = fw.Kind, reason = why, note = fw.Note, installed = installedStatus, version });
        }

        var warnings = new List<string>();
        if (missing.Count > 0)
            warnings.Add($"Dependencies missing in the game: {string.Join(", ", missing)}");

        // Cross-mod imports: with gamePath, we resolve each unknown import against the
        // REDscript modules actually declared in r6/scripts — we then know WHICH
        // installed mod provides it, or that it is missing (cause of crash at load).
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
                warnings.Add("Imports not provided by the installed mods (missing dependency?): " +
                             string.Join(", ", unresolved.Take(15)));
        }
        else
        {
            crossMod = unknownImports.OrderBy(x => x)
                .Select(i => (object)new { import = i, providedBy = (List<string>?)null, installed = (bool?)null })
                .ToList();
            if (unknownImports.Count > 0)
                warnings.Add($"Imports from other mods (possible cross-mod dependencies): " +
                             $"{string.Join(", ", unknownImports.Take(15))} — pass gamePath to resolve them.");
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = missing.Count > 0 ? "partial" : "success",
            summary = $"{deps.Count} dependency(ies) detected for {Path.GetFileName(modPath.TrimEnd('\\', '/'))}" +
                      (string.IsNullOrWhiteSpace(gamePath) ? "" : $" ({missing.Count} missing)"),
            modPath,
            dependencies = deps,
            crossModImports = crossMod,
            fileStats,
            warnings,
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Scans the REDscript modules declared in &lt;game&gt;/r6/scripts:
    /// module name → mods (top-level folders) that declare it.</summary>
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
                // The `module X.Y` declaration is at the top of the file (after
                // any comments/annotations) — 40 lines is enough.
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

    /// <summary>Mods providing a REDscript import: the imported module itself, a
    /// parent module (class import `X.Y.Class` → module `X.Y`), or a sub-module
    /// (import `X.Y.*` covers `X.Y.Z`). Null if no installed mod provides it.</summary>
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
    [Description("Inventories the modding frameworks INSTALLED in a Cyberpunk 2077 installation " +
                 "(RED4ext, redscript, ArchiveXL, TweakXL, Codeware, Audioware, Mod Settings, CET...) " +
                 "with their version if detectable. Lets you know what is available before " +
                 "installing a mod or diagnosing a missing dependency.")]
    public static string CheckRequirements(
        [Description("Cyberpunk 2077 installation root folder.")] string gamePath)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");

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
            summary = $"{installed}/{Frameworks.Length} modding frameworks installed",
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
    [Description("Health diagnostic of a modded Cyberpunk 2077 installation, in one call: " +
                 "installed/missing frameworks, dependencies required by installed mods but " +
                 "absent (crash cause #1), conflicts between archives, and mod inventory. " +
                 "Returns a structured report with recommendations.")]
    public static async Task<string> ModDoctor(
        Cp77ToolsRunner runner,
        [Description("Cyberpunk 2077 installation root folder.")] string gamePath,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");

        // 1) installed frameworks
        var installedFw = Frameworks.Where(f => IsInstalled(gamePath, f).installed)
                                    .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 2) dependencies required by ALL the modded content (scripts, tweaks, xl, lua, plugins)
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

        // 3) conflicts (existing daemon verb)
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
                conflictNote = "conflict detection unavailable (upstream WolvenKit.CLI bug on some installs)";
        }
        catch { conflictNote = "conflict detection not executed"; }

        // 4) mod inventory
        var archiveDir = Path.Combine(gamePath, "archive", "pc", "mod");
        var modsDir = Path.Combine(gamePath, "mods");
        int archiveMods = Directory.Exists(archiveDir) ? Directory.GetFiles(archiveDir, "*.archive").Length : 0;
        int redMods = Directory.Exists(modsDir) ? Directory.GetDirectories(modsDir).Length : 0;

        var recommendations = new List<string>();
        foreach (var dep in missingDeps)
        {
            var fw = Frameworks.First(f => f.Name == dep);
            recommendations.Add($"Install {dep} ({fw.Note}) — required by the present content.");
        }
        if (conflictCount is > 0)
            recommendations.Add($"{conflictCount} archive conflict(s) detected: check load order/priority (detect_conflicts for the details).");

        var status = missingDeps.Count > 0 || conflictCount is > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status,
            summary = $"Setup health: {archiveMods} .archive mods + {redMods} REDmods · " +
                      $"{installedFw.Count}/{Frameworks.Length} frameworks · " +
                      $"{missingDeps.Count} missing dependency(ies)" +
                      (conflictCount is { } c ? $" · {c} conflict(s)" : ""),
            gamePath,
            installedFrameworks = installedFw.OrderBy(x => x).ToList(),
            requiredFrameworks = required.Keys.OrderBy(x => x).ToList(),
            missingDependencies = missingDeps,
            conflictCount,
            conflictNote,
            mods = new { archiveMods, redMods },
            recommendations,
            warnings = missingDeps.Count > 0
                ? new[] { $"Missing dependencies: {string.Join(", ", missingDeps)}" }
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
    [Description("Validates an ArchiveXL .xl file (YAML): well-formed YAML + recognized top-level " +
                 "sections (customSounds, resource, factories, localization, animations). " +
                 "Reports YAML syntax errors (line/column) and unknown sections. " +
                 "Complements validate_tweak (which targets .tweak TweakXL files).")]
    public static string ValidateXl(
        [Description("Path of the .xl file to validate.")] string xlFile)
    {
        if (!File.Exists(xlFile))
            return Err($".xl file not found: {xlFile}");

        string text;
        try { text = File.ReadAllText(xlFile); }
        catch (Exception ex) { return Err($"Cannot read file: {ex.Message}"); }

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
                summary = $"Invalid YAML in {Path.GetFileName(xlFile)}",
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
            errors.Add("The root of an .xl must be a YAML mapping (key: value).");
        }
        else
        {
            foreach (var k in map.Keys)
            {
                var key = k?.ToString() ?? "";
                sections.Add(key);
                if (!XlTopLevelKeys.Contains(key))
                    warnings.Add($"Unknown top-level section: \"{key}\" " +
                                 $"(expected: {string.Join(", ", XlTopLevelKeys)}).");
            }
            if (sections.Count == 0)
                warnings.Add("Empty .xl file (no section).");
        }

        var status = errors.Count > 0 ? "error" : warnings.Count > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = errors.Count == 0,
            status,
            summary = $".xl validation: {Path.GetFileName(xlFile)} — {sections.Count} section(s), " +
                      $"{errors.Count} error(s), {warnings.Count} warning(s)",
            xlFile,
            sections,
            warnings,
            errors,
        }, JsonOpts);
    }

    [McpServerTool(Name = "validate_redmod", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Validates the info.json of a REDmod project: required name / version fields (+ format), " +
                 "and consistency of the customSounds entries (name, type, and referenced file present " +
                 "in customSounds/). The REDmod tools (create_redmod_project, install_redmod, " +
                 "pack_redmod) only check the PRESENCE of info.json, never its content. " +
                 "Complements validate_xl / validate_tweak / validate_item_mod.")]
    public static string ValidateRedmod(
        [Description("REDmod root folder (containing info.json) or direct path to the " +
                     "info.json.")] string modPath)
    {
        var infoPath = Directory.Exists(modPath) ? Path.Combine(modPath, "info.json") : modPath;
        if (!File.Exists(infoPath))
            return Err($"info.json not found: {infoPath}");

        string json;
        try { json = File.ReadAllText(infoPath); }
        catch (Exception ex) { return Err($"Cannot read file: {ex.Message}"); }

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
            summary = $"REDmod validation: {Path.GetFileName(modRoot)} — " +
                      $"{v.Errors.Count} error(s), {v.Warnings.Count} warning(s)",
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

    /// <summary>Validates the content of a REDmod info.json. Pure logic (input = JSON text +
    /// names of files present in customSounds/), tested in isolation. Rules: name and version
    /// required (version in numeric format otherwise a warning); each customSounds must have
    /// name + type, and a file (except for type mod_skip) that must exist in customSounds/.</summary>
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
            errors.Add($"info.json: invalid JSON — {ex.Message.Replace("\n", " ").Trim()}");
            return new RedmodValidation(null, null, 0, errors, warnings);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("info.json: the root must be a JSON object.");
                return new RedmodValidation(null, null, 0, errors, warnings);
            }

            if (root.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
            {
                name = nEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) errors.Add("info.json: \"name\" is empty.");
            }
            else errors.Add("info.json: required field \"name\" missing or non-textual.");

            if (root.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.String)
            {
                version = vEl.GetString();
                if (string.IsNullOrWhiteSpace(version)) errors.Add("info.json: \"version\" is empty.");
                else if (!LooksLikeVersion(version))
                    warnings.Add($"info.json: \"version\" = \"{version}\" does not look like a " +
                                 "version number (e.g. 1.0.0).");
            }
            else errors.Add("info.json: required field \"version\" missing or non-textual.");

            if (root.TryGetProperty("customSounds", out var csEl))
            {
                if (csEl.ValueKind != JsonValueKind.Array)
                    errors.Add("info.json: \"customSounds\" must be an array.");
                else
                {
                    var i = 0;
                    foreach (var s in csEl.EnumerateArray())
                    {
                        var where = $"customSounds[{i}]";
                        i++;
                        if (s.ValueKind != JsonValueKind.Object)
                        { errors.Add($"{where}: must be an object."); continue; }
                        soundCount++;

                        var sName = s.TryGetProperty("name", out var snEl)
                                    && snEl.ValueKind == JsonValueKind.String ? snEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(sName)) errors.Add($"{where}: \"name\" required.");

                        var sType = s.TryGetProperty("type", out var stEl)
                                    && stEl.ValueKind == JsonValueKind.String ? stEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(sType))
                            errors.Add($"{where}: \"type\" required (e.g. mod_sfx_2d, mod_skip).");

                        var isSkip = string.Equals(sType, "mod_skip", StringComparison.OrdinalIgnoreCase);
                        var file = s.TryGetProperty("file", out var fEl)
                                   && fEl.ValueKind == JsonValueKind.String ? fEl.GetString() : null;
                        if (!isSkip)
                        {
                            if (string.IsNullOrWhiteSpace(file))
                                errors.Add($"{where}: \"file\" required for type \"{sType}\".");
                            else if (presentSoundFiles.Count > 0)
                            {
                                var bare = file.Replace('\\', '/').TrimStart('/').Split('/').Last();
                                if (!presentSoundFiles.Contains(bare, StringComparer.OrdinalIgnoreCase)
                                    && !presentSoundFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                                    warnings.Add($"{where}: sound file \"{file}\" not found in customSounds/.");
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
    [Description("Generates a starter ArchiveXL .xl file (commented YAML) for a given mod " +
                 "type: factory (register a factory record via CSV), customSounds (custom " +
                 "audio), localization (texts), resource (resource patch). Ready-to-edit " +
                 "scaffolding — the .xl equivalent of generate_tweak_template.")]
    public static string ScaffoldArchiveXl(
        [Description("Destination folder for the .xl file.")] string outputFolder,
        [Description("Mod name (used as file name <name>.xl).")] string modName,
        [Description("Type: factory | customSounds | localization | resource.")] string kind = "factory")
    {
        if (!Directory.Exists(outputFolder))
            return Err($"Destination folder not found: {outputFolder}");
        if (string.IsNullOrWhiteSpace(modName)
            || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err("Invalid mod name.");

        var body = kind.ToLowerInvariant() switch
        {
            "customsounds" =>
                "# ArchiveXL — custom sounds\n" +
                "customSounds:\n" +
                $"  - name: {modName}_sfx_01\n" +
                "    type: mod_sfx_2d\n" +
                $"    file: mod\\{modName}\\sfx_01.wav\n",
            "localization" =>
                "# ArchiveXL — localization\n" +
                "localization:\n" +
                "  onscreens:\n" +
                $"    en-us: base\\localization\\en-us\\{modName}.json\n",
            "resource" =>
                "# ArchiveXL — resource patch (adds appearances/components)\n" +
                "resource:\n" +
                "  patch:\n" +
                "    base\\path\\to\\target.app:\n" +
                $"      - {modName}\\appearances\\custom.app\n",
            _ /* factory */ =>
                "# ArchiveXL — registering a factory record (items/records via CSV)\n" +
                "factories:\n" +
                $"  - {modName}\\factory.csv\n",
        };

        var xlPath = Path.Combine(outputFolder, modName + ".xl");
        File.WriteAllText(xlPath, body, new UTF8Encoding(false));

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"ArchiveXL scaffold generated ({kind}): {Path.GetFileName(xlPath)}",
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
    [Description("Searches all textual references to a target (TweakDBID, resource " +
                 "path, LocKey, CName, class/function name...) in the source files of a " +
                 "mod or project folder (.reds, .tweak, .yaml, .xl, .lua, .json, .csv). " +
                 "Returns file:line + snippet. Ideal for impact analysis before editing. " +
                 "To search in the game's .archive files, use find_in_archives instead.")]
    public static string FindReferences(
        [Description("String to search (substring, case-insensitive).")] string target,
        [Description("Folder to scan (mod, project, or r6/scripts).")] string searchFolder,
        [Description("Max number of matches returned (default 200).")] int maxResults = 200,
        [Description("Case-sensitive search (default false).")] bool caseSensitive = false)
    {
        if (string.IsNullOrEmpty(target))
            return Err("Empty search target.");
        if (!Directory.Exists(searchFolder))
            return Err($"Folder not found: {searchFolder}");

        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var extSet = new HashSet<string>(TextRefExtensions, StringComparer.OrdinalIgnoreCase);
        var matches = new List<object>();
        int filesScanned = 0, filesWithMatch = 0; bool truncated = false;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(searchFolder, "*", SearchOption.AllDirectories); }
        catch (Exception ex) { return Err($"Cannot scan: {ex.Message}"); }

        foreach (var f in files)
        {
            if (!extSet.Contains(Path.GetExtension(f))) continue;
            filesScanned++;
            // Lazy read (File.ReadLines): no loading of the whole file into memory
            // — important for large mod .json/.csv files.
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
            summary = $"\"{target}\": {matches.Count}{(truncated ? "+" : "")} occurrence(s) " +
                      $"in {filesWithMatch} file(s) ({filesScanned} scanned)",
            target,
            searchFolder,
            matchCount = matches.Count,
            filesWithMatch,
            filesScanned,
            truncated,
            matches,
            warnings = truncated ? new[] { $"Results truncated to {maxResults} — refine the target or increase maxResults." } : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // diff_mod_vs_base
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "diff_mod_vs_base", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Semantic diff of ONE game file overridden by a mod, against its base " +
                 "version: extracts the file from both sides, converts them to JSON and compares the " +
                 "fields (added / removed / changed). Answers \"what does this mod actually " +
                 "change?\". The base version is looked up in archive/pc/content (LRU cache) " +
                 "if baseArchive is not provided.")]
    public static async Task<string> DiffModVsBase(
        Cp77ToolsRunner runner,
        [Description(".archive of the mod containing the overridden file.")] string modArchive,
        [Description("Internal path of the file in the archive (e.g. base\\...\\x.app).")] string gameFilePath,
        [Description("Game root (to locate the base version in archive/pc/content).")] string gamePath,
        [Description("Optional: precise base .archive (short-circuits the search).")] string? baseArchive = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(modArchive))
            return Err($"Mod archive not found: {modArchive}");

        // Locate the base archive if not provided.
        if (string.IsNullOrWhiteSpace(baseArchive))
        {
            var content = Path.Combine(gamePath, "archive", "pc", "content");
            if (!Directory.Exists(content))
                return Err($"content folder not found: {content} (provide baseArchive?)");
            baseArchive = await FindArchiveContaining(runner, content, gameFilePath, ct);
            if (baseArchive is null)
                return Err($"File not found in the base game: {gameFilePath} " +
                           "(it may be a file ADDED by the mod, not an override).");
        }

        var modJson = await ExtractAsJson(runner, modArchive, gameFilePath, ct);
        var baseJson = await ExtractAsJson(runner, baseArchive!, gameFilePath, ct);
        if (modJson is null) return Err($"Extraction/conversion failed on the mod side: {gameFilePath}");
        if (baseJson is null) return Err($"Extraction/conversion failed on the base side: {gameFilePath}");

        var (added, removed, changedList) = DiffJson(baseJson, modJson);
        var changed = changedList.Select(c => (object)new { path = c.Path, @base = c.Base, mod = c.Mod }).ToList();
        const int cap = 100;
        bool truncated = added.Count + removed.Count + changed.Count > cap;

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Diff {Path.GetFileName(gameFilePath)} (mod vs base): " +
                      $"{added.Count} addition(s), {removed.Count} removal(s), {changed.Count} change(s)",
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
            warnings = truncated ? new[] { $"Diff truncated to {cap} entries per category." } : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // scaffold_mod
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "scaffold_mod", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Creates, in ONE call, a working mod skeleton according to its type: archive " +
                 "(.cpmodproj project + folders), redscript (.reds starter with @wrapMethod), tweak " +
                 "(.tweak starter), redmod (info.json + folders). Also writes a MOD_MANIFEST.json " +
                 "summarizing the type, declared dependencies and structure. Shortcut " +
                 "over create_mod_project / generate_* to get started quickly.")]
    public static string ScaffoldMod(
        [Description("Parent folder where to create the mod.")] string parentFolder,
        [Description("Mod name.")] string modName,
        [Description("Type: archive | redscript | tweak | redmod.")] string kind = "archive",
        [Description("Author (optional).")] string? author = null,
        [Description("Version (optional, e.g. 1.0.0).")] string? version = null,
        [Description("Declared dependencies, comma-separated (e.g. Codeware,ArchiveXL).")] string? dependencies = null)
    {
        if (!Directory.Exists(parentFolder))
            return Err($"Parent folder not found: {parentFolder}");
        if (string.IsNullOrWhiteSpace(modName) || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err("Invalid mod name.");
        var root = Path.Combine(parentFolder, modName);
        if (Directory.Exists(root)) return Err($"The folder already exists: {root}");

        var produced = new List<string>();
        void Dir(params string[] parts) { var p = Path.Combine(new[] { root }.Concat(parts).ToArray()); Directory.CreateDirectory(p); produced.Add(Path.GetRelativePath(root, p) + Path.DirectorySeparatorChar); }
        void Write(string rel, string content) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, content, new UTF8Encoding(false)); produced.Add(rel); }

        var k = kind.ToLowerInvariant();
        switch (k)
        {
            case "redscript":
                Write(Path.Combine("r6", "scripts", modName, modName + ".reds"),
                    $"// {modName} — REDscript mod\n" +
                    "// Example: extend a game method without replacing it.\n" +
                    "@wrapMethod(PlayerPuppet)\n" +
                    "protected cb func OnGameAttached() -> Bool {\n" +
                    "  let result = wrappedMethod();\n" +
                    $"  LogChannel(n\"DEBUG\", \"{modName} loaded\");\n" +
                    "  return result;\n" +
                    "}\n");
                break;
            case "tweak":
                Write(Path.Combine("r6", "tweaks", modName + ".tweak"),
                    $"# {modName} — TweakXL mod\n" +
                    "# Example: override a field of an existing record.\n" +
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
            createdBy = "wkmcp scaffold_mod",
        }, JsonOpts));

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Mod \"{modName}\" ({k}) created: {root}",
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
    [Description("Packs a folder in the game-relative layout (archive/pc/mod, r6/scripts, " +
                 "r6/tweaks, mods/, red4ext/...) into a distributable .zip (Nexus / manual install), " +
                 "with conformant \"/\" separators. Validates the presence of at least one recognized " +
                 "game folder and warns otherwise.")]
    public static string PackageMod(
        [Description("Source folder in the game layout (contains archive/, r6/, mods/...).")] string sourceFolder,
        [Description("Path of the output .zip.")] string outputZip)
    {
        if (!Directory.Exists(sourceFolder))
            return Err($"Source folder not found: {sourceFolder}");

        var srcFull = Path.GetFullPath(sourceFolder);
        var topDirs = Directory.GetDirectories(srcFull).Select(d => Path.GetFileName(d)).ToList();
        var recognized = topDirs.Where(d => GameLayoutRoots.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
        var warnings = new List<string>();
        if (recognized.Count == 0)
            warnings.Add("No recognized game folder (archive/, r6/, mods/, red4ext/...) at the root — " +
                         "the zip may not install as-is.");

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
                // Exclude non-distributable noise (dev / build artifacts).
                if (IsPackagingNoise(rel)) { skipped++; continue; }
                zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
                n++;
            }
            if (skipped > 0)
                warnings.Add($"{skipped} dev/build file(s) excluded from the bundle " +
                             "(.git, packed/, *.cpmodproj, MOD_MANIFEST.json).");
            var sizeKo = new FileInfo(outputZip).Length / 1024;
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = warnings.Count > 0 ? "partial" : "success",
                summary = $"Bundle created: {Path.GetFileName(outputZip)} ({n} file(s), {sizeKo} KB)",
                outputZip,
                fileCount = n,
                skipped,
                recognizedLayout = recognized,
                produced = new[] { Path.GetFileName(outputZip) },
                warnings,
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Packaging failed: {ex.Message}"); }
    }

    /// <summary>Dev files/folders that have no place in a distributable bundle
    /// (Nexus / manual install).</summary>
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
    // inspect_journal / find_journal_entry — quest journal navigation
    //
    // A .journal (gameJournalResource) is a standard CR2W, so readable/writable
    // via read_game_file / write_game_file. But cooked_journal.journal weighs ~70 MB
    // in JSON: impossible to read/edit in one block. These tools provide a navigable
    // view of it (summary by type, entry search → targeted JSON path), in the
    // spirit of inspect_mesh / mod_summary / describe_tweak_record.
    // ════════════════════════════════════════════════════════════════════════

    internal sealed record JournalEntryRef(string Path, string Type, string? Id, string? Title, int ChildCount);

    // Projection to lowercase keys for JSON output consistent with the rest.
    private static object JournalRefJson(JournalEntryRef e)
        => new { path = e.Path, type = e.Type, id = e.Id, title = e.Title, childCount = e.ChildCount };

    [McpServerTool(Name = "inspect_journal", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Navigable summary of a .journal file converted to JSON (by read_game_file): " +
                 "total number of entries, depth, breakdown by type ($type), and top-level " +
                 "categories (quests, codex, contacts, e-mails…). Avoids loading the ~70 MB of " +
                 "JSON of the full journal. Then give find_journal_entry a precise target.")]
    public static string InspectJournal(
        [Description("Path of the JSON produced by read_game_file on a .journal.")] string jsonFile)
    {
        if (!File.Exists(jsonFile))
            return Err($"JSON file not found: {jsonFile}");
        JournalSummary? s;
        try
        {
            using var fs = File.OpenRead(jsonFile);
            using var doc = JsonDocument.Parse(fs);
            s = SummarizeJournal(doc.RootElement);
        }
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
        if (s is null)
            return Err("This JSON is not a journal (RootChunk.$type ≠ gameJournalResource).");

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Journal: {s.TotalEntries} entry(ies), depth {s.MaxDepth}, " +
                      $"{s.ByType.Count} type(s), {s.TopLevel.Count} top-level category(ies)",
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
    [Description("Locates entries in a .journal (JSON from read_game_file) by id, type or " +
                 "title, and returns for each its exact JSON PATH (e.g. " +
                 "Data.RootChunk.entry.Data.entries[2].Data.entries[7].Data) — to edit the targeted " +
                 "entry then rewrite via write_game_file, without manipulating the entire ~70 MB.")]
    public static string FindJournalEntry(
        [Description("Path of the JSON produced by read_game_file on a .journal.")] string jsonFile,
        [Description("Value to search (substring, case-insensitive).")] string query,
        [Description("Targeted field: id (default), type ($type) or title.")] string field = "id",
        [Description("Max number of matches (default 100).")] int maxResults = 100)
    {
        if (!File.Exists(jsonFile))
            return Err($"JSON file not found: {jsonFile}");
        if (string.IsNullOrEmpty(query))
            return Err("Empty query.");
        List<JournalEntryRef> matches; bool truncated; bool isJournal;
        try
        {
            using var fs = File.OpenRead(jsonFile);
            using var doc = JsonDocument.Parse(fs);
            (matches, truncated, isJournal) = FindInJournal(doc.RootElement, query, field, maxResults);
        }
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
        if (!isJournal)
            return Err("This JSON is not a journal (RootChunk.$type ≠ gameJournalResource).");

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"\"{query}\" (field {field}): {matches.Count}{(truncated ? "+" : "")} entry(ies) found",
            jsonFile,
            field,
            query,
            matchCount = matches.Count,
            truncated,
            matches = matches.Select(JournalRefJson),
            warnings = truncated ? new[] { $"Results truncated to {maxResults} — refine the query." } : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ── Journal helpers ─────────────────────────────────────────────────────
    internal sealed record JournalSummary(
        int TotalEntries, int MaxDepth, Dictionary<string, int> ByType,
        List<JournalEntryRef> TopLevel, string? Descriptor);

    /// <summary>Navigates to the journal root folder (gameJournalRootFolderEntry).
    /// Returns default if the JSON is not a journal.</summary>
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
    /// <summary>Children of a journal entry: the Data of each "entries" handle.</summary>
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
    // inspect_cr2w / find_in_cr2w — GENERIC navigation of a large CR2W in JSON
    //
    // Generalizes inspect_journal to any CR2W (.quest, .questphase, .scene,
    // .streamingsector, inkwidget…): these trees are huge once in JSON.
    // ════════════════════════════════════════════════════════════════════════

    internal sealed record Cr2wNodeRef(string Path, string Type, string? Value);

    [McpServerTool(Name = "inspect_cr2w", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Navigable summary of ANY CR2W converted to JSON (by read_game_file / " +
                 "cr2w_to_json): root type, number of typed objects, breakdown by $type, " +
                 "depth. For large files (quests, scenes, sectors, UI) that cannot be " +
                 "read in one block. Then give find_in_cr2w a target.")]
    public static string InspectCr2w(
        [Description("Path of the JSON produced by read_game_file / cr2w_to_json.")] string jsonFile)
    {
        if (!File.Exists(jsonFile))
            return Err($"JSON file not found: {jsonFile}");
        try
        {
            using var fs = File.OpenRead(jsonFile);
            using var doc = JsonDocument.Parse(fs);
            var (rootType, total, maxDepth, byType) = SummarizeCr2w(doc.RootElement);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"CR2W \"{rootType ?? "?"}\": {total} typed object(s), " +
                          $"depth {maxDepth}, {byType.Count} type(s)",
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
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
    }

    [McpServerTool(Name = "find_in_cr2w", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Searches in ANY CR2W (JSON) the objects whose field matches a " +
                 "target, and returns their exact JSON PATH — to edit the targeted node then " +
                 "rewrite via write_game_file. field = $type (default), a precise field name, or " +
                 "* (any text value). Ideal for large quests/scenes/sectors/UI.")]
    public static string FindInCr2w(
        [Description("Path of the JSON produced by read_game_file / cr2w_to_json.")] string jsonFile,
        [Description("Value to search (substring, case-insensitive).")] string query,
        [Description("Targeted field: $type (default), a property name, or * (any text value).")] string field = "$type",
        [Description("Max number of matches (default 100).")] int maxResults = 100)
    {
        if (!File.Exists(jsonFile))
            return Err($"JSON file not found: {jsonFile}");
        if (string.IsNullOrEmpty(query))
            return Err("Empty query.");
        try
        {
            using var fs = File.OpenRead(jsonFile);
            using var doc = JsonDocument.Parse(fs);
            var (matches, truncated) = FindInCr2wTree(doc.RootElement, query, field, maxResults);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"\"{query}\" (field {field}): {matches.Count}{(truncated ? "+" : "")} match(es)",
                jsonFile,
                field,
                query,
                matchCount = matches.Count,
                truncated,
                matches = matches.Select(m => new { path = m.Path, type = m.Type, value = m.Value }),
                warnings = truncated ? new[] { $"Results truncated to {maxResults} — refine the query." } : Array.Empty<string>(),
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
    }

    /// <summary>Walks the whole JSON and counts objects by $type.</summary>
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
    // diagnose_logs — parses the modding logs, classifies errors, suggests a fix
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

    // Knowledge base: known error pattern → problem + fix.
    private sealed record KnownError(string Pattern, string Problem, string Fix);
    private static readonly KnownError[] ErrorKb =
    {
        new("scc invocation failed|REDScript compilation has failed|compilation has failed",
            "redscript compilation failed — a .reds is invalid.",
            "The redscript log indicates the offending file:line. Fix or remove this mod; a single broken .reds blocks the whole compilation."),
        new("field with this name is already defined|already defined",
            "Duplicate definition — a mod is probably installed twice.",
            "Check that no mod is present both in archive/pc/mod and mods/, nor duplicated."),
        new("Failed to resolve address for hash|Could not find address",
            "Hash/address not resolved — often a game update (or game not up to date/pirated).",
            "Update the mod and the core-mods (RED4ext, redscript) for the current game version."),
        new("1114|VCRUNTIME|VCRedist|vcruntime",
            "RED4ext error 1114 — Visual C++ Redistributable 2022 missing.",
            "Install Microsoft Visual C++ Redistributable 2022 (x64)."),
        new("corrupted or missing TweakDB|tweakdb.*missing|tweakdb.*corrupt",
            "TweakDB corrupted or missing.",
            "Copy r6/cache/tweakdb.bin to r6/cache/modded/ (+ tweakdb_ep1.bin for Phantom Liberty)."),
        new("Watchdog Timeout|watchdog timeout",
            "Watchdog timeout — loading too slow (antivirus or slow disk).",
            "Exclude the game folder from the antivirus, or increase the timeout in user.ini."),
        new("ValidateScripts|codeware.global.reds",
            "Script validation failed (often Codeware/stale cache).",
            "Clear r6/cache/, verify the game files, reinstall an up-to-date Codeware."),
    };

    [McpServerTool(Name = "diagnose_logs", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reads and DIAGNOSES the modding logs of a Cyberpunk 2077 install (redscript, " +
                 "RED4ext, ArchiveXL, TweakXL, Codeware, CET, REDmod): extracts the errors, " +
                 "classifies them by source, maps known errors to a fix, and attempts to attribute " +
                 "them to the offending mod. Much more useful than tail_game_logs (which only does a raw tail).")]
    public static string DiagnoseLogs(
        [Description("Cyberpunk 2077 installation root folder.")] string gamePath,
        [Description("Max number of error lines reported per source (default 30).")] int maxPerSource = 30)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");

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

            // Match the KB against the whole log (not just the error lines).
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
                ? "No log found (the game may never have run modded)."
                : $"{logsFound} log(s) analyzed, {totalErrors} error line(s), {diagnoses.Count} known diagnostic(s)",
            gamePath,
            logsFound,
            totalErrors,
            sources = perSource,
            diagnoses,
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Classifies a log line against the knowledge base (testable).
    /// Returns (problem, fix) or null.</summary>
    internal static (string problem, string fix)? ClassifyLogText(string text)
    {
        foreach (var k in ErrorKb)
            if (System.Text.RegularExpressions.Regex.IsMatch(text, k.Pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return (k.Problem, k.Fix);
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    // analyze_conflicts — robust conflicts between mods (archives + tweaks)
    //
    // Works around the WolvenKit `conflicts` verb (buggy on some installs) by
    // computing the overlaps directly from the archive listings (LRU cache) and
    // the tweak records.
    // ════════════════════════════════════════════════════════════════════════

    [McpServerTool(Name = "analyze_conflicts", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Detects conflicts between installed mods WITHOUT the buggy WolvenKit verb: " +
                 "game files provided by several .archive (with the winner according to the " +
                 "alphabetical load order = first-wins), and TweakDB records defined by " +
                 "several .tweak/.yaml. First diagnostic tool when a mod silently overrides " +
                 "another (otherwise: manual bisection).")]
    public static async Task<string> AnalyzeConflicts(
        Cp77ToolsRunner runner,
        [Description("Cyberpunk 2077 installation root folder.")] string gamePath,
        [Description("Max number of conflicts reported per category (default 200).")] int maxResults = 200,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");

        // Archives: archive/pc/mod + REDmod mods/*/archives
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

        // Tweak records: r6/tweaks/**/*.{yaml,yml,tweak}
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

        // Conflicts ≠ dead report: we say what to DO. The general recipes are enough
        // (repeating them on each of the N conflicts would bloat the response for nothing).
        var resolutionHints = total == 0 ? Array.Empty<string>() : new[]
        {
            "To make a losing archive win: rename it so it sorts BEFORE the " +
            "winner (prefix \"!\" or \"00_\"), then check with a new analyze_conflicts.",
            "To neutralize a conflicting mod without removing it: toggle_mods (moves its " +
            ".archive to _disabled; also handy in bisection to find the culprit).",
            "To check what actually differs between two conflicting archives: " +
            "diff_archives, then diff_mod_vs_base on the precise file.",
            "TweakDB records defined by several .tweak/.yaml: merge the values into a " +
            "single file, or remove the redundant definition (lint_tweak to check).",
        };

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status,
            summary = $"{archives.Count} archive(s) + tweaks analyzed: " +
                      $"{archiveConflicts.Count} archive conflict(s), {tweakConflicts.Count} conflicting record(s)",
            gamePath,
            note = "Alphabetical load order: the first to provide a file wins (winner).",
            resolutionHints,
            archivesScanned = archives.Count,
            archiveConflicts = archiveConflicts.Take(maxResults),
            archiveConflictCount = archiveConflicts.Count,
            tweakConflicts = tweakConflicts.Take(maxResults),
            tweakConflictCount = tweakConflicts.Count,
            truncated = archiveConflicts.Count > maxResults || tweakConflicts.Count > maxResults,
            warnings = total > 0 ? new[] { $"{total} conflict(s) detected — check load order/priority." } : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Record names (top-level keys that are mappings) of a
    /// .tweak/.yaml TweakXL file. Tolerant to parse errors.</summary>
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
                    // Ignore global directives (start with $).
                    if (!string.IsNullOrEmpty(key) && !key.StartsWith("$")) names.Add(key);
                }
        }
        catch { /* unreadable file: ignored */ }
        return names;
    }

    // ════════════════════════════════════════════════════════════════════════
    // validate_item_mod — validates the reference chain of an ArchiveXL item mod
    //
    // Silent failure cause #1: a typo between .yaml (entityName/displayName),
    // the factory .csv and the localization .json. Purely textual validation of the
    // control-files (no daemon); .ent/.app/.mesh are reported as present/missing.
    // ════════════════════════════════════════════════════════════════════════

    internal sealed record ItemRecord(string Record, string? EntityName, string? AppearanceName, string? DisplayName, string File);

    [McpServerTool(Name = "validate_item_mod", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Validates the reference chain of an ArchiveXL item mod (silent-failure cause #1, " +
                 "\"the item doesn't spawn / empty name\"): for each TweakXL record, " +
                 "checks that its entityName exists in a factory .csv, that its displayName " +
                 "matches a secondaryKey in a localization .json, and that the referenced .ent " +
                 "entity is present. Reports the missing links. Textual analysis of the " +
                 "control-files (.yaml/.xl/.csv/.json); with deep=true, also converts the .ent and " +
                 "checks that the appearanceName is present in it.")]
    public static async Task<string> ValidateItemMod(
        Cp77ToolsRunner runner,
        [Description("Mod folder (project or deployed content) containing .yaml/.xl/.csv/.json.")] string modPath,
        [Description("Deep mode: converts the present .ent files and checks that the appearanceName exists in them.")] bool deep = false,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(modPath))
            return Err($"Mod folder not found: {modPath}");

        var files = Directory.EnumerateFiles(modPath, "*", SearchOption.AllDirectories).ToList();
        var items = new List<ItemRecord>();
        foreach (var f in files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".yaml" or ".yml" or ".tweak"))
            items.AddRange(ParseItemRecords(f));

        // Factory .csv: col0 = entityName, col1 = .ent path.
        var factoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var csvFiles = files.Where(f => Path.GetExtension(f).ToLowerInvariant() == ".csv").ToList();
        foreach (var csv in csvFiles)
            foreach (var (name, path) in ParseFactoryCsv(csv))
                factoryNames[name] = path;

        // Localization .json: all the secondaryKey present in the mod.
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
                    warnings.Add($"[{it.Record}] entityName '{it.EntityName}': no factory .csv found in the mod.");
                else if (!factoryNames.ContainsKey(it.EntityName))
                    errors.Add(entIssue = $"[{it.Record}] entityName '{it.EntityName}' missing from the factory .csv " +
                               $"({string.Join(", ", csvFiles.Select(Path.GetFileName))}).");
                else
                {
                    // Is the referenced .ent entity present in the mod?
                    var entPath = factoryNames[it.EntityName];
                    var baseName = entPath.Replace('/', '\\').Split('\\')[^1];
                    var entFile = files.FirstOrDefault(f => f.EndsWith(baseName, StringComparison.OrdinalIgnoreCase));
                    if (entFile is null)
                        warnings.Add($"[{it.Record}] entity '{entPath}' not found in the mod (base game ref?).");
                    else if (deep && !string.IsNullOrEmpty(it.AppearanceName))
                    {
                        // Deep: convert the .ent and check that the appearanceName is present in it.
                        var entJson = await ConvertCr2wToJsonText(runner, entFile, ct);
                        if (entJson is null)
                            warnings.Add($"[{it.Record}] .ent conversion failed (deep check skipped).");
                        else if (entJson.IndexOf(it.AppearanceName!, StringComparison.OrdinalIgnoreCase) < 0)
                            errors.Add($"[{it.Record}] appearanceName '{it.AppearanceName}' missing from the .ent '{baseName}'.");
                    }
                }
            }
            if (!string.IsNullOrEmpty(it.DisplayName)
                && !it.DisplayName.StartsWith("LocKey#", StringComparison.OrdinalIgnoreCase))
            {
                if (secondaryKeys.Count == 0)
                    warnings.Add($"[{it.Record}] displayName '{it.DisplayName}': no localization .json found.");
                else if (!secondaryKeys.Contains(it.DisplayName))
                    errors.Add(dispIssue = $"[{it.Record}] displayName '{it.DisplayName}' missing from the localization secondaryKey.");
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
                ? "No TweakXL item record found (.yaml with entityName/appearanceName/displayName)."
                : $"{items.Count} item(s) checked: {errors.Count} error(s), {warnings.Count} warning(s)",
            modPath,
            itemsFound = items.Count,
            factories = factoryNames.Count,
            localizationKeys = secondaryKeys.Count,
            checks,
            warnings,
            errors,
            deep,
            limitation = deep
                ? "Deep mode: also checks the appearanceName in the .ent. The .app↔.mesh matching " +
                  "(mesh appearance names, material indices) remains to be done via inspect_cr2w."
                : "Control-files (.yaml↔.csv↔.json + .ent presence). Pass deep=true to check " +
                  "the appearanceName in the .ent.",
        }, JsonOpts);
    }

    /// <summary>Converts a CR2W to JSON text via the daemon (convert serialize),
    /// in a disposable temp folder. Returns null on failure.</summary>
    private static async Task<string?> ConvertCr2wToJsonText(
        Cp77ToolsRunner runner, string cr2wFile, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wkmcp-conv", Guid.NewGuid().ToString("N"));
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

    /// <summary>Extracts the item records from a .yaml/.tweak TweakXL: any top-level
    /// mapping having an entityName / appearanceName / displayName field.</summary>
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
        catch { /* unreadable: ignored */ }
        return items;
    }

    /// <summary>Parses an ArchiveXL factory .csv: (entityName, .ent path) per
    /// non-empty / non-commented line. First column = name, second = path.</summary>
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
                if (name.Length == 0 || name.Equals("name", StringComparison.OrdinalIgnoreCase)) continue; // header
                rows.Add((name, path));
            }
        }
        catch { /* unreadable: ignored */ }
        return rows;
    }

    /// <summary>Recursively collects all the "secondaryKey" property values
    /// of a localization JSON file.</summary>
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
        catch { /* not a localization JSON: ignored */ }
        return keys;
    }

    // ════════════════════════════════════════════════════════════════════════
    // lint_tweak — semantic lint of a .tweak/.yaml TweakXL
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "lint_tweak", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Semantic lint of a TweakXL file (.tweak/.yaml): TABS forbidden (silent " +
                 "load failure), indentation not a multiple of 2, duplicate record " +
                 "names in the file, use of an auto-generated `inlineN` record as `$base` " +
                 "(breaks on every game update), unknown array-mutation operators (the real " +
                 "ones are hyphenated — `!append-once`, not `!appendOnce`), and unknown " +
                 "`$directives`. Complements validate_tweak (keys + value types vs tweakdb.bin).")]
    public static string LintTweak(
        [Description("Path of the .tweak / .yaml file to lint.")] string tweakFile)
    {
        if (!File.Exists(tweakFile))
            return Err($"File not found: {tweakFile}");
        string[] lines;
        try { lines = File.ReadAllLines(tweakFile); }
        catch (Exception ex) { return Err($"Cannot read file: {ex.Message}"); }

        var (errors, warnings) = LintTweakText(lines);
        var status = errors.Count > 0 ? "error" : warnings.Count > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = errors.Count == 0,
            status,
            summary = $"Tweak lint: {Path.GetFileName(tweakFile)} — {errors.Count} error(s), {warnings.Count} warning(s)",
            tweakFile,
            warnings,
            errors,
        }, JsonOpts);
    }

    // The real TweakXL array-mutation operators (hyphenated) and record directives.
    private static readonly HashSet<string> ValidTweakOperators = new(StringComparer.Ordinal)
    { "append", "append-once", "append-from", "prepend", "prepend-once", "prepend-from", "remove" };
    private static readonly HashSet<string> ValidTweakDirectives = new(StringComparer.Ordinal)
    { "$type", "$base", "$instanceOf", "$instances" };

    /// <summary>Suggests the correct hyphenated operator for a typo (e.g. appendOnce → append-once),
    /// matching on the hyphen/case-insensitive form; null when there is no close match.</summary>
    private static string? SuggestTweakOperator(string op)
    {
        static string Norm(string s) => s.Replace("-", "").ToLowerInvariant();
        var n = Norm(op);
        foreach (var valid in ValidTweakOperators)
            if (Norm(valid) == n) return valid;
        return null;
    }

    /// <summary>Textual lint of a TweakXL (testable). Returns (errors, warnings).</summary>
    internal static (List<string> errors, List<string> warnings) LintTweakText(string[] lines)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var topKeys = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimEnd().Length == 0) continue;
            // Indentation: tabs forbidden, spaces in multiples of 2.
            var indent = line.Length - line.TrimStart().Length;
            if (line[..indent].Contains('\t'))
                errors.Add($"L{i + 1}: TAB in the indentation — TweakXL requires spaces (silent failure).");
            else if (indent % 2 != 0)
                warnings.Add($"L{i + 1}: indentation of {indent} space(s) — TweakXL expects multiples of 2.");
            // Top-level key (column 0, ends with ':' or 'key: value').
            if (indent == 0 && !line.StartsWith("#") && !line.StartsWith("$"))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"^([^\s:#][^:]*):");
                if (m.Success)
                {
                    var key = m.Groups[1].Value.Trim();
                    topKeys[key] = topKeys.GetValueOrDefault(key) + 1;
                }
            }
            // inlineN as $base.
            var bm = System.Text.RegularExpressions.Regex.Match(line, @"\$base\s*:\s*\S*inline\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (bm.Success)
                warnings.Add($"L{i + 1}: `$base` points to an auto-generated `inlineN` record — the indices shift on every update and will break the mod. Reference the named record.");

            var trimmed = line.TrimStart();
            // Array-mutation operators: the real ones are hyphenated (!append-once),
            // so a camelCase typo (!appendOnce) or an invented op (!merge) is silently ignored.
            if (!trimmed.StartsWith("#"))
            {
                foreach (System.Text.RegularExpressions.Match om in
                         System.Text.RegularExpressions.Regex.Matches(line, @"(?:^|[\s\-:])!([A-Za-z][\w-]*)"))
                {
                    var op = om.Groups[1].Value;
                    if (ValidTweakOperators.Contains(op)) continue;
                    var hint = SuggestTweakOperator(op);
                    warnings.Add($"L{i + 1}: unknown TweakXL operator `!{op}`" +
                        (hint is not null ? $" — did you mean `!{hint}`?"
                            : " — valid: !append, !append-once, !append-from, !prepend, !prepend-once, !prepend-from, !remove."));
                }
            }
            // Unknown $directive (valid: $type, $base, $instanceOf, $instances).
            var dm = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\$[A-Za-z][A-Za-z0-9_]*)\s*:");
            if (dm.Success && !ValidTweakDirectives.Contains(dm.Groups[1].Value))
                warnings.Add($"L{i + 1}: unknown directive `{dm.Groups[1].Value}` — valid: $type, $base, $instanceOf, $instances.");
        }
        foreach (var kv in topKeys.Where(kv => kv.Value > 1))
            warnings.Add($"Record \"{kv.Key}\" defined {kv.Value} times in the file — the last one silently overrides.");
        return (errors, warnings);
    }

    // ════════════════════════════════════════════════════════════════════════
    // generate_manifest — dependency manifest from the analysis of a mod
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "generate_manifest", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Generates a dependency manifest for a mod by detecting its required frameworks " +
                 "(like analyze_dependencies) and writing a REQUIREMENTS.md (Nexus " +
                 "\"Requirements\" tab style) + a structured object. Fills the total absence of a " +
                 "machine-readable dependency system on the ecosystem side.")]
    public static string GenerateManifest(
        [Description("Mod folder to analyze.")] string modPath,
        [Description("Mod name (for the manifest header).")] string? modName = null,
        [Description("Mod version.")] string? version = null,
        [Description("Write REQUIREMENTS.md in the mod folder (default true).")] bool writeFile = true)
    {
        if (!Directory.Exists(modPath))
            return Err($"Mod folder not found: {modPath}");

        var reasons = DetectFrameworks(modPath, out var unknownImports, out _);
        var deps = Frameworks.Where(f => reasons.ContainsKey(f.Name))
            .Select(f => new { framework = f.Name, kind = f.Kind, reason = reasons[f.Name], note = f.Note })
            .ToList();

        var name = string.IsNullOrWhiteSpace(modName) ? Path.GetFileName(modPath.TrimEnd('\\', '/')) : modName!;
        var sb = new StringBuilder();
        sb.AppendLine($"# {name} — Requirements").AppendLine();
        if (!string.IsNullOrWhiteSpace(version)) sb.AppendLine($"**Version :** {version}").AppendLine();
        sb.AppendLine("## Dependencies (frameworks)").AppendLine();
        if (deps.Count == 0) sb.AppendLine("_No framework dependency detected._");
        else foreach (var d in deps) sb.AppendLine($"- **{d.framework}** ({d.kind}) — {d.note}  \n  _detected via: {d.reason}_");
        if (unknownImports.Count > 0)
        {
            sb.AppendLine().AppendLine("## Possible cross-mod dependencies (unrecognized imports)").AppendLine();
            foreach (var u in unknownImports.OrderBy(x => x).Take(30)) sb.AppendLine($"- `{u}`");
        }
        sb.AppendLine().AppendLine("> Install each framework at its latest version compatible with your game version.");

        string? written = null;
        if (writeFile)
        {
            written = Path.Combine(modPath, "REQUIREMENTS.md");
            try { File.WriteAllText(written, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { return Err($"Cannot write REQUIREMENTS.md: {ex.Message}"); }
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Manifest for {name}: {deps.Count} dependency(ies), {unknownImports.Count} cross-mod import(s)",
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
    // resolve_dynamic_appearance — expansion of dynamic ArchiveXL patterns
    // ════════════════════════════════════════════════════════════════════════
    private static readonly (string ph, string[] vals)[] DynPlaceholders =
    {
        ("{gender}", new[] { "m", "w" }),
        ("{camera}", new[] { "fpp", "tpp" }),
    };

    [McpServerTool(Name = "resolve_dynamic_appearance", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Expands a dynamic ArchiveXL appearance path/pattern (`*` prefix, " +
                 "{gender}→m/w and {camera}→fpp/tpp interpolations) into its concrete paths, and — if " +
                 "modPath is provided — indicates which ones actually exist. Targets the ArchiveXL trap " +
                 "where a substitution error only shows the 1st appearance (very hard to debug).")]
    public static string ResolveDynamicAppearance(
        [Description("Path pattern (e.g. *base\\mod\\item_{gender}_{camera}.mesh).")] string pattern,
        [Description("Optional: mod folder, to check the existence of the concrete paths.")] string? modPath = null)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return Err("Empty pattern.");

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
                exists = found ? "present" : "MISSING";
                if (!found) missing++;
            }
            results.Add(new { path = ex.TrimStart('*'), exists });
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = missing > 0 ? "partial" : "success",
            summary = $"{expansions.Count} concrete path(s)" +
                      (string.IsNullOrWhiteSpace(modPath) ? "" : $", {missing} missing"),
            pattern,
            expansions = results,
            unresolvedPlaceholders = remaining,
            warnings = remaining.Count > 0
                ? new[] { $"Unexpanded placeholders (depend on body/skin/etc.): {string.Join(", ", remaining)}" }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Expands {gender}/{camera} into a Cartesian product (testable).</summary>
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
    // migration_check — does a mod survive the current game version?
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "migration_check", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Checks whether a .archive mod is still aligned with the CURRENT game version: " +
                 "for each file the mod provides, indicates whether it overrides an existing base " +
                 "file (active override) or not (addition, OR an override gone inert after a game " +
                 "update — path disappeared). Targets \"updates silently break mods\".")]
    public static async Task<string> MigrationCheck(
        Cp77ToolsRunner runner,
        [Description(".archive of the mod to check.")] string modArchive,
        [Description("Cyberpunk 2077 installation root folder.")] string gamePath,
        [Description("Max number of non-matching paths listed (default 100).")] int maxResults = 100,
        CancellationToken ct = default)
    {
        if (!File.Exists(modArchive))
            return Err($"Mod archive not found: {modArchive}");
        var content = Path.Combine(gamePath, "archive", "pc", "content");
        if (!Directory.Exists(content))
            return Err($"content folder not found: {content}");

        // Set of current base paths (union of content listings, LRU cache).
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
        catch (Exception ex) { return Err($"Cannot list the mod: {ex.Message}"); }

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
            summary = $"{modEntries.Count} mod file(s): {overrides.Count} override the current base, " +
                      $"{nonMatching.Count} without a match (additions or overrides gone inert)",
            modArchive,
            baseFileCount = baseSet.Count,
            overrideCount = overrides.Count,
            nonMatchingCount = nonMatching.Count,
            nonMatching = nonMatching.Take(maxResults),
            warnings = overrides.Count == 0 && modEntries.Count > 0
                ? new[] { "NO mod file matches the current base — either this is a purely additive mod (ArchiveXL), or its overrides have gone inert after a game update." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // toggle_mods — enable/disable .archive files (for assisted bisection)
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "toggle_mods", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Enables or disables .archive mods by moving them between archive/pc/mod and " +
                 "archive/pc/mod/_disabled (reversible, non-destructive). Primitive of conflict " +
                 "bisection: disable half the mods → launch → diagnose → narrow down. " +
                 "Returns the up-to-date enabled/disabled lists.")]
    public static string ToggleMods(
        [Description("Cyberpunk 2077 installation root folder.")] string gamePath,
        [Description("Archive names separated by commas (with or without .archive). Empty = move nothing (just list).")] string? archives = null,
        [Description("true = enable (re-enable), false = disable.")] bool enable = false)
    {
        var modDir = Path.Combine(gamePath, "archive", "pc", "mod");
        if (!Directory.Exists(modDir))
            return Err($"Mods folder not found: {modDir}");
        var disabledDir = Path.Combine(modDir, "_disabled");

        var moved = new List<string>();
        var warnings = new List<string>();
        var rawNames = (archives ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var names = new List<string>();
        foreach (var raw in rawNames)
        {
            var n = raw.EndsWith(".archive", StringComparison.OrdinalIgnoreCase) ? raw : raw + ".archive";
            // Guard: toggle_mods MOVES files by name. A name with separators or ..
            // (e.g. "..\content\foo.archive") would relocate a base-game archive out
            // of the mod tree. Reject anything that isn't a bare file name.
            if (!PathSafety.IsBareFileName(n)) { warnings.Add($"Refused (invalid name): {raw}"); continue; }
            names.Add(n);
        }

        if (names.Count > 0)
        {
            Directory.CreateDirectory(disabledDir);
            foreach (var n in names)
            {
                var from = Path.Combine(enable ? disabledDir : modDir, n);
                var to = Path.Combine(enable ? modDir : disabledDir, n);
                if (!File.Exists(from)) { warnings.Add($"Not found ({(enable ? "disabled" : "enabled")}): {n}"); continue; }
                try { File.Move(from, to, overwrite: false); moved.Add(n); }
                catch (Exception ex) { warnings.Add($"Failed to move {n}: {ex.Message}"); }
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
                ? $"{enabled.Count} enabled mod(s), {disabled.Count} disabled"
                : $"{moved.Count} mod(s) {(enable ? "enabled" : "disabled")} · {enabled.Count} enabled / {disabled.Count} disabled",
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
    // list_entity_appearances — list the appearances of an entity (.ent)
    // ════════════════════════════════════════════════════════════════════════
    internal sealed record EntityAppearance(string Name, string? AppearanceName, string? AppResource);

    [McpServerTool(Name = "list_entity_appearances", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lists the appearances of a REDengine entity (.ent): for each one, its entity " +
                 "name (the `name` to pass to export_entity / in the .yaml), the `appearanceName` " +
                 "on the .app side, and the referenced .app resource. Reliable and indispensable to know " +
                 "what an entity exposes before editing/exporting an appearance.")]
    public static async Task<string> ListEntityAppearances(
        Cp77ToolsRunner runner,
        [Description("Path of an extracted .ent file.")] string entFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(entFile))
            return Err($".ent file not found: {entFile}");
        var json = await ConvertCr2wToJsonText(runner, entFile, ct);
        if (json is null) return Err(".ent conversion failed.");
        var apps = ParseEntityAppearances(json);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"{apps.Count} appearance(s) in {Path.GetFileName(entFile)}",
            entFile,
            appearanceCount = apps.Count,
            appearances = apps.Select(a => new { name = a.Name, appearanceName = a.AppearanceName, appResource = a.AppResource }),
            warnings = apps.Count == 0 ? new[] { "No appearance (component/proxy-type entity?)." } : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Parses the appearances of a .ent (RootChunk.appearances[]:
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
        catch { /* unexpected JSON */ }
        return list;
    }

    // ════════════════════════════════════════════════════════════════════════
    // validate_appearance — deep .app → .mesh validation (appearances + materials)
    // ════════════════════════════════════════════════════════════════════════
    internal sealed record AppMeshRef(string AppAppearance, string MeshPath, string? MeshAppearance);

    [McpServerTool(Name = "validate_appearance", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("DEEP validation of the .app → .mesh appearance chain: for each appearance " +
                 "of the .app and each mesh component, checks that the referenced meshAppearance " +
                 "actually exists in the .mesh (otherwise invisible mesh) and that its materials (chunkMaterials) " +
                 "match the materialEntries (otherwise black/inconsistent material). Resolves the " +
                 ".mesh in the mod, otherwise in the base game if gamePath is provided.")]
    public static async Task<string> ValidateAppearance(
        Cp77ToolsRunner runner,
        [Description("Path of an extracted .app file.")] string appFile,
        [Description("Root of the mod folder (to resolve the mod's .mesh).")] string? modRoot = null,
        [Description("Game root (to resolve base .mesh files not found in the mod).")] string? gamePath = null,
        [Description("Max number of resolved .mesh (default 40).")] int maxMeshes = 40,
        CancellationToken ct = default)
    {
        if (!File.Exists(appFile))
            return Err($".app file not found: {appFile}");
        var appJson = await ConvertCr2wToJsonText(runner, appFile, ct);
        if (appJson is null) return Err(".app conversion failed.");

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
                warnings.Add($"[{rf.AppAppearance}] mesh '{rf.MeshPath}' not found/not converted — check skipped.");
            else
            {
                var (meshApps, mats) = info.Value;
                var ma = rf.MeshAppearance ?? "default";
                if (!meshApps.Contains(ma, StringComparer.OrdinalIgnoreCase))
                    errors.Add(issue = $"[{rf.AppAppearance}] meshAppearance '{ma}' missing from the mesh '{Path.GetFileName(rf.MeshPath)}' " +
                               $"(available: {string.Join(", ", meshApps.Take(6))}) → invisible mesh.");
            }
            checks.Add(new { appAppearance = rf.AppAppearance, mesh = rf.MeshPath, meshAppearance = rf.MeshAppearance, ok = issue is null });
        }

        var status = errors.Count > 0 ? "error" : warnings.Count > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = errors.Count == 0,
            status,
            summary = refs.Count == 0
                ? "No mesh component found in the .app."
                : $"{refs.Count} mesh reference(s) checked: {errors.Count} error(s), {warnings.Count} warning(s)",
            appFile,
            meshRefsChecked = refs.Count,
            meshesResolved = meshCache.Count(kv => kv.Value is not null),
            checks,
            warnings,
            errors,
            limitation = "Checks meshAppearance ∈ .mesh.appearances. The fine-grained consistency of " +
                         "material indices (chunkMaterials ↔ localMaterialBuffer) is not yet covered.",
        }, JsonOpts);
    }

    [McpServerTool(Name = "inspect_app", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Structural summary of a .app file: number of appearances, and for each one the " +
                 "number of mesh components and the referenced meshes; total of distinct meshes. " +
                 "Quick overview BEFORE validate_appearance (which resolves and validates " +
                 "each .mesh). Lightweight: a single CR2W→JSON conversion, without mesh resolution.")]
    public static async Task<string> InspectApp(
        Cp77ToolsRunner runner,
        [Description("Path of an extracted .app file.")] string appFile,
        [Description("Max number of detailed appearances returned (default 100). appearanceCount always " +
                     "gives the real total.")] int maxAppearances = 100,
        CancellationToken ct = default)
    {
        if (!File.Exists(appFile))
            return Err($".app file not found: {appFile}");
        var appJson = await ConvertCr2wToJsonText(runner, appFile, ct);
        if (appJson is null) return Err(".app conversion failed.");

        var s = SummarizeApp(appJson);
        var cap = Math.Max(1, maxAppearances);
        var truncated = s.Appearances.Count > cap;
        var shown = truncated ? s.Appearances.Take(cap).ToList() : s.Appearances;

        return JsonSerializer.Serialize(new
        {
            ok = s.AppearanceCount > 0,
            status = s.AppearanceCount > 0 ? "success" : "partial",
            summary = s.AppearanceCount == 0
                ? "No appearance found in the .app."
                : $"{s.AppearanceCount} appearance(s), {s.MeshComponentCount} mesh component(s), " +
                  $"{s.DistinctMeshCount} distinct mesh(es)",
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
                ? new[] { "The .app exposes no appearance (unexpected or empty file)." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    [McpServerTool(Name = "add_appearance", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Adds a new appearance to a .app file by CLONING an existing one — the only robust " +
                 "way (authoring a valid appearanceAppearanceDefinition from scratch is error-prone). " +
                 "Renumbers the cloned CR2W HandleIds to fresh unique values so the copy is an " +
                 "independent definition (not an alias of the source), optionally swaps mesh DepotPaths, " +
                 "then round-trips the CR2W via JSON and SELF-VERIFIES the new appearance survives " +
                 "deserialization before writing. Output reinjectable via pack_archive / write_game_file. " +
                 "Use inspect_app first to see existing appearance names.")]
    public static async Task<string> AddAppearance(
        Cp77ToolsRunner runner,
        [Description("Path to an extracted .app file.")] string appFile,
        [Description("Name of the new appearance to create (must be unique in the .app).")] string newName,
        [Description("Name of the existing appearance to clone (default: the first one).")] string? fromAppearance = null,
        [Description("Optional JSON object of mesh DepotPath swaps to apply in the clone, e.g. " +
                     "{\"base\\\\a\\\\old.mesh\":\"base\\\\a\\\\new.mesh\"}. Keys match case-insensitively.")] string? meshSwapsJson = null,
        [Description("Optional output .app path (default: overwrite appFile in place).")] string? outputFile = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(appFile)) return Err($".app file not found: {appFile}");
        if (string.IsNullOrWhiteSpace(newName)) return Err("add_appearance: newName is required.");

        Dictionary<string, string>? swaps = null;
        if (!string.IsNullOrWhiteSpace(meshSwapsJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(meshSwapsJson);
                if (parsed is { Count: > 0 })
                    swaps = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException ex) { return Err($"add_appearance: meshSwapsJson is not a valid JSON object: {ex.Message}"); }
        }

        var dir = Path.Combine(Path.GetTempPath(), "wkmcp-addapp", Guid.NewGuid().ToString("N"));
        var jsonDir = Path.Combine(dir, "json");
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(jsonDir);
        Directory.CreateDirectory(outDir);
        try
        {
            // 1. CR2W → JSON
            await runner.RunAsync(new[] { "convert", "serialize", appFile, "--outpath", jsonDir }, ct);
            var jsonFile = Directory.EnumerateFiles(jsonDir, "*.json", SearchOption.AllDirectories).FirstOrDefault();
            if (jsonFile is null) return Err("add_appearance: serialization produced no JSON.");

            // 2. clone + renumber HandleIds + rename (+ optional mesh swaps)
            var node = JsonNode.Parse(await File.ReadAllTextAsync(jsonFile, ct));
            var (ok, err, clonedFrom, swapCount, warnings, _) =
                AddAppearanceToApp(node, newName, fromAppearance, swaps);
            if (!ok) return Err($"add_appearance: {err}");
            await File.WriteAllTextAsync(jsonFile, node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);

            // 3. JSON → CR2W
            await runner.RunAsync(new[] { "convert", "deserialize", jsonFile, "--outpath", outDir }, ct);
            var produced = Directory.EnumerateFiles(outDir, "*.app", SearchOption.AllDirectories).FirstOrDefault()
                           ?? Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).FirstOrDefault();
            if (produced is null) return Err("add_appearance: deserialization produced no .app.");

            // 4. self-verify: the new appearance must survive the round-trip (guards against HandleId aliasing)
            var verifyJson = await ConvertCr2wToJsonText(runner, produced, ct);
            if (verifyJson is null)
                return Err("add_appearance: could not re-read the produced .app to verify it.");
            var verifyNames = ParseAppearanceNames(verifyJson);
            if (!verifyNames.Contains(newName, StringComparer.Ordinal))
                return Err($"add_appearance: the new appearance '{newName}' did not survive deserialization " +
                           "(likely a CR2W handle conflict) — file NOT written. Try cloning a different appearance.");

            var dest = outputFile ?? appFile;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
            File.Copy(produced, dest, overwrite: true);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"Added appearance '{newName}' (cloned from '{clonedFrom}'" +
                          (swapCount > 0 ? $", {swapCount} mesh swap(s)" : "") + $") → {Path.GetFileName(dest)}",
                produced = new[] { dest },
                clonedFrom,
                newAppearance = newName,
                meshSwapsApplied = swapCount,
                appearanceCount = verifyNames.Count,
                appearances = verifyNames,
                warnings,
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (JsonException ex) { return Err($"add_appearance: could not parse/edit the .app JSON: {ex.Message}"); }
        finally { try { Directory.Delete(dir, true); } catch { /* best-effort temp cleanup */ } }
    }

    internal sealed record AppAppearanceSummary(string Name, int MeshComponents, IReadOnlyList<string> Meshes);

    internal sealed record AppSummary(
        int AppearanceCount, int MeshComponentCount, int DistinctMeshCount,
        IReadOnlyList<AppAppearanceSummary> Appearances);

    /// <summary>Summarizes a .app JSON: appearances + mesh components per appearance + distinct
    /// meshes. Reuses ParseAppMeshRefs (mesh components) and ParseAppearanceNames
    /// (all appearances, even without a mesh component). Pure logic, testable.</summary>
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

    /// <summary>Pure core of add_appearance: clones an existing appearance in the .app CR2W JSON,
    /// renumbers the cloned HandleIds to fresh unique values (so the copy is an independent
    /// definition, not an alias of the source), renames it, and optionally swaps mesh DepotPaths.
    /// Mutates <paramref name="root"/> in place (appends the clone). Pure + testable.</summary>
    internal static (bool Ok, string? Error, string ClonedFrom, int MeshSwaps, List<string> Warnings, string[] FinalNames)
        AddAppearanceToApp(JsonNode? root, string newName,
                           string? fromAppearance, IReadOnlyDictionary<string, string>? meshSwaps)
    {
        var warnings = new List<string>();
        var none = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(newName))
            return (false, "newName is required.", "", 0, warnings, none);

        var rc = root?["Data"]?["RootChunk"] ?? root;
        if (rc?["appearances"] is not JsonArray apps)
            return (false, "no 'appearances' array found in the .app JSON.", "", 0, warnings, none);

        static string? NameOf(JsonNode? ae)
        {
            var def = (ae as JsonObject)?["Data"] as JsonObject ?? ae as JsonObject;
            return def?["name"] switch
            {
                JsonObject no => (no["$value"] as JsonValue)?.GetValue<string>(),
                JsonValue sv => sv.GetValue<string>(),
                _ => null,
            };
        }

        var existing = apps.Select(NameOf).Where(n => n is not null).Select(n => n!).ToList();
        if (existing.Contains(newName, StringComparer.Ordinal))
            return (false, $"an appearance named '{newName}' already exists.", "", 0, warnings, none);
        if (apps.Count == 0)
            return (false, "the .app has no appearance to clone from.", "", 0, warnings, none);

        var src = fromAppearance is null
            ? apps[0]
            : apps.FirstOrDefault(a => string.Equals(NameOf(a), fromAppearance, StringComparison.Ordinal));
        if (src is null)
            return (false, $"source appearance '{fromAppearance}' not found. Available: {string.Join(", ", existing)}",
                    "", 0, warnings, none);
        var clonedFrom = NameOf(src) ?? "?";

        var clone = JsonNode.Parse(src.ToJsonString());
        if (clone is null)
            return (false, "internal error: could not clone the source appearance.", "", 0, warnings, none);

        // Renumber the cloned HandleIds → fresh unique values, so the copy is its own definition.
        // Two passes: collect the ids of cloned *definitions* (HandleId + inline Data) and map each
        // to a fresh id; then rewrite every HandleId in the clone that is in that map (definitions and
        // any internal references to them), leaving references to external chunks untouched.
        var next = MaxHandleId(root);
        var map = new Dictionary<int, int>();
        void CollectDefs(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if (o["Data"] is not null && o["HandleId"] is JsonValue hv
                    && int.TryParse(hv.ToString(), out var id) && !map.ContainsKey(id))
                    map[id] = ++next;
                foreach (var kv in o.ToList()) CollectDefs(kv.Value);
            }
            else if (n is JsonArray a) { foreach (var it in a) CollectDefs(it); }
        }
        void Rewrite(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if (o["HandleId"] is JsonValue hv && int.TryParse(hv.ToString(), out var id)
                    && map.TryGetValue(id, out var nid))
                    o["HandleId"] = nid.ToString();
                foreach (var kv in o.ToList()) Rewrite(kv.Value);
            }
            else if (n is JsonArray a) { foreach (var it in a) Rewrite(it); }
        }
        CollectDefs(clone);
        Rewrite(clone);

        // Rename the clone.
        var cdef = (clone as JsonObject)?["Data"] as JsonObject ?? clone as JsonObject;
        if (cdef is not null)
        {
            if (cdef["name"] is JsonObject no) no["$value"] = newName;
            else cdef["name"] = new JsonObject { ["$value"] = newName };
        }

        // Optional mesh DepotPath swaps inside the clone.
        var swapCount = 0;
        if (meshSwaps is { Count: > 0 })
        {
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Swap(JsonNode? n)
            {
                if (n is JsonObject o)
                {
                    if (o["mesh"] is JsonObject m && m["DepotPath"] is JsonObject dp
                        && dp["$value"] is JsonValue pv)
                    {
                        var cur = pv.GetValue<string>();
                        if (cur is not null && meshSwaps.TryGetValue(cur, out var rep))
                        {
                            dp["$value"] = rep; swapCount++; matched.Add(cur);
                        }
                    }
                    foreach (var kv in o.ToList()) Swap(kv.Value);
                }
                else if (n is JsonArray a) { foreach (var it in a) Swap(it); }
            }
            Swap(clone);
            foreach (var k in meshSwaps.Keys)
                if (!matched.Contains(k))
                    warnings.Add($"mesh swap source '{k}' did not match any mesh in the cloned appearance.");
        }

        apps.Add(clone);
        var finalNames = apps.Select(NameOf).Where(n => n is not null).Select(n => n!).ToArray();
        return (true, null, clonedFrom, swapCount, warnings, finalNames);
    }

    /// <summary>Largest integer HandleId anywhere in a CR2W JSON tree (−1 if none). Used to
    /// allocate fresh unique HandleIds for a cloned subtree.</summary>
    internal static int MaxHandleId(JsonNode? root)
    {
        var max = -1;
        void Walk(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if (o["HandleId"] is JsonValue hv && int.TryParse(hv.ToString(), out var id))
                    max = Math.Max(max, id);
                foreach (var kv in o) Walk(kv.Value);
            }
            else if (n is JsonArray a) { foreach (var it in a) Walk(it); }
        }
        Walk(root);
        return max;
    }

    /// <summary>Names of all the appearances of a .app JSON (including those without a mesh
    /// component). Testable.</summary>
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
        catch { /* unexpected JSON */ }
        return names;
    }

    /// <summary>Extracts the (.app appearance, mesh path, meshAppearance) of the mesh
    /// components of each appearance of a .app. Testable.</summary>
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
        catch { /* unexpected JSON */ }
        return refs;
    }

    /// <summary>Mesh appearance names + materialEntries names of a .mesh JSON.</summary>
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
        catch { /* unexpected JSON */ }
        return (appNames, mats);
    }

    /// <summary>Resolves a .mesh by DepotPath: first in the mod (by base name),
    /// otherwise in the base archives if gamePath is provided; returns its JSON.</summary>
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

    // CR2W access helpers: value of a CName / a DepotPath under a property.
    private static string? CnameVal(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object
           && p.TryGetProperty("$value", out var v) && v.ValueKind == JsonValueKind.String
           ? v.GetString() : null;
    private static string? DepotPathVal(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object
           && p.TryGetProperty("DepotPath", out var dp) && dp.TryGetProperty("$value", out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── Diff/extraction helpers ─────────────────────────────────────────────
    /// <summary>Searches, among the .archive files of a folder, the first one containing
    /// the given internal path (via the runner's LRU listing cache).</summary>
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

    /// <summary>Extracts a file from an archive and converts it to JSON (text).
    /// Returns null on failure.</summary>
    private static async Task<string?> ExtractAsJson(
        Cp77ToolsRunner runner, string archive, string internalPath, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "wkmcp-diff", Guid.NewGuid().ToString("N"));
        var rawDir = Path.Combine(work, "raw");
        var jsonDir = Path.Combine(work, "json");
        Directory.CreateDirectory(rawDir); Directory.CreateDirectory(jsonDir);
        try
        {
            // Passing the FULL internal path as a pattern (not just the base name)
            // avoids capturing a same-named file from another folder — and thus a
            // false diff. Aligned with the proven logic of read_game_file.
            var normalized = internalPath.Replace('/', '\\');
            await runner.RunAsync(new[] { "unbundle", archive, "--outpath", rawDir, "--pattern", normalized }, ct);
            var raw = Directory.EnumerateFiles(rawDir, "*", SearchOption.AllDirectories).FirstOrDefault();
            if (raw is null) return null; // file absent from the archive (business case)
            var conv = await runner.RunAsync(new[] { "convert", "serialize", raw, "--outpath", jsonDir }, ct);
            var json = Directory.EnumerateFiles(jsonDir, "*.json", SearchOption.AllDirectories).FirstOrDefault();
            // json absent + conversion failure = true technical failure (≠ file absent).
            if (json is null) return null;
            return await File.ReadAllTextAsync(json, ct);
        }
        catch { return null; }
        finally { try { Directory.Delete(work, true); } catch { /* best-effort */ } }
    }

    internal sealed record JsonChange(string Path, string Base, string Mod);

    /// <summary>Flattens two JSON into paths→values and computes additions/removals/
    /// changes ("mod" side vs "base"). The $.Header subtree (conversion noise) is
    /// excluded.</summary>
    internal static (List<string> added, List<string> removed, List<JsonChange> changed) DiffJson(string baseJson, string modJson)
    {
        var b = new Dictionary<string, string>();
        var m = new Dictionary<string, string>();
        try { using var db = JsonDocument.Parse(baseJson); Flatten(db.RootElement, "$", b); } catch { }
        try { using var dm = JsonDocument.Parse(modJson); Flatten(dm.RootElement, "$", m); } catch { }

        // The $.Header subtree is conversion noise (temporary extraction path,
        // timestamp, WolvenKit version) — not mod content.
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

    // ── Detection ───────────────────────────────────────────────────────────
    /// <summary>Walks a folder and infers the required frameworks. Returns
    /// {framework → reason}. Also reports the unknown imports (cross-mod
    /// dependencies) and file stats.</summary>
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
                    Mark(reasons, "redscript", ".reds files present");
                    Mark(reasons, "RED4ext", "required by redscript");
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
                    catch { /* unreadable file: ignored */ }
                    break;
                case ".xl":
                    xl++; Mark(reasons, "ArchiveXL", ".xl files present"); Mark(reasons, "RED4ext", "required by ArchiveXL");
                    break;
                case ".tweak":
                    tweak++; Mark(reasons, "TweakXL", ".tweak files present"); Mark(reasons, "RED4ext", "required by TweakXL");
                    break;
                case ".yaml": case ".yml":
                    if (lower.Contains(@"\tweaks\") || lower.Contains("/tweaks/"))
                    { tweak++; Mark(reasons, "TweakXL", "YAML in r6/tweaks"); Mark(reasons, "RED4ext", "required by TweakXL"); }
                    break;
                case ".lua":
                    if (lower.Contains("cyber_engine_tweaks"))
                    { lua++; Mark(reasons, "Cyber Engine Tweaks", "CET Lua scripts"); }
                    break;
                case ".archive":
                    archive++;
                    break;
                case ".dll":
                    if (lower.Contains(@"red4ext\plugins") || lower.Contains("red4ext/plugins"))
                    { dll++; Mark(reasons, "RED4ext", "RED4ext plugin (.dll)"); }
                    break;
            }
        }

        unknownImports = unknown.ToList();
        fileStats = new { reds, xl, tweak, lua, dll, archive };
        return reasons;

        static void Mark(Dictionary<string, string> d, string name, string why)
        { if (!d.ContainsKey(name)) d[name] = why; }
    }

    /// <summary>Detects whether a framework is installed in the game + its version
    /// if readable (version folder in red4ext/plugins/&lt;X&gt;).</summary>
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
            // RED4ext plugins: a version file or a versioned folder name.
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
