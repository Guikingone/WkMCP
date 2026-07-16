using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using YamlDotNet.Serialization;

namespace WkMcp;

/// <summary>Semantic CR2W inspectors and project-wide validation workflows.</summary>
public static partial class ModdingTools
{
    private static readonly HashSet<string> ProjectCr2wExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".app", ".anims", ".community", ".ent", ".inkatlas", ".inkwidget", ".mesh",
        ".mi", ".mlmask", ".mlsetup", ".morphtarget", ".questphase", ".rig", ".scene",
        ".streamingsector", ".workspot", ".xbm",
    };

    [McpServerTool(Name = "inspect_entity", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Summarizes an extracted .ent entity: component types/names, appearances and referenced resources. Accepts CR2W or WolvenKit JSON.")]
    public static async Task<string> InspectEntity(
        Cp77ToolsRunner runner,
        [Description("Path to an extracted .ent or its serialized JSON.")] string entityOrJson,
        [Description("Maximum components and references returned; totals remain exact.")] int maxResults = 250,
        CancellationToken ct = default)
    {
        var (json, error) = await LoadCr2wJsonText(runner, entityOrJson, ct);
        if (json is null) return Err(error!);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = RootChunkOf(doc.RootElement);
            var objects = WalkObjects(root).ToList();
            var components = objects
                .Select(o => new { Element = o, Type = TypeOf(o) })
                .Where(x => x.Type.Contains("Component", StringComparison.OrdinalIgnoreCase))
                .Select(x => new
                {
                    type = x.Type,
                    name = FirstString(x.Element, "name", "componentName", "id"),
                    resource = FirstDepotPath(x.Element),
                }).ToList();
            var appearances = objects
                .SelectMany(o => NamedStrings(o, "appearanceName", "appearance", "name"))
                .Where(x => x.Contains("appearance", StringComparison.OrdinalIgnoreCase)
                            || objects.Any(o => TypeOf(o).Contains("Appearance", StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            var refs = CollectDepotPaths(doc.RootElement);
            var cap = Math.Clamp(maxResults, 1, 5000);
            var warnings = new List<string>();
            if (!TypeOf(root).Contains("Entity", StringComparison.OrdinalIgnoreCase))
                warnings.Add($"Root is '{TypeOf(root)}', not an entity resource.");
            if (components.Count > cap || refs.Count > cap)
                warnings.Add($"Result lists are capped at {cap}; total counts remain exact.");

            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = warnings.Count == 0 ? "success" : "partial",
                summary = $"{TypeOf(root)}: {components.Count} component(s), {appearances.Count} appearance value(s), {refs.Count} resource reference(s)",
                produced = Array.Empty<string>(), warnings, errors = Array.Empty<string>(), log = "",
                file = entityOrJson,
                rootType = TypeOf(root),
                componentCount = components.Count,
                componentTypes = components.GroupBy(x => x.type).ToDictionary(g => g.Key, g => g.Count()),
                componentsTruncated = components.Count > cap,
                components = components.Take(cap),
                appearances,
                referenceCount = refs.Count,
                referencesTruncated = refs.Count > cap,
                references = refs.Take(cap).Select(r => new { role = r.role, path = r.path }),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Unreadable entity JSON: {ex.Message}"); }
    }

    [McpServerTool(Name = "inspect_animation", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Summarizes an extracted .anims resource: clips, durations, animation events, root-motion/SIMD signals and resource references.")]
    public static async Task<string> InspectAnimation(
        Cp77ToolsRunner runner,
        [Description("Path to an extracted .anims or its serialized JSON.")] string animsOrJson,
        [Description("Maximum clips/events returned; totals remain exact.")] int maxResults = 500,
        CancellationToken ct = default)
    {
        var (json, error) = await LoadCr2wJsonText(runner, animsOrJson, ct);
        if (json is null) return Err(error!);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = RootChunkOf(doc.RootElement);
            var objects = WalkObjects(root).ToList();
            var clips = objects
                .Select(o => new
                {
                    type = TypeOf(o),
                    name = FirstString(o, "name", "animationName", "debugName"),
                    duration = FirstNumber(o, "duration", "animationDuration", "length"),
                })
                .Where(x => x.name is not null && x.type.Contains("Anim", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => (x.type, x.name, x.duration)).Select(g => g.First()).ToList();
            var events = objects
                .Select(o => new
                {
                    type = TypeOf(o),
                    name = FirstString(o, "name", "eventName"),
                    time = FirstNumber(o, "time", "startTime", "timestamp"),
                })
                .Where(x => x.type.Contains("Event", StringComparison.OrdinalIgnoreCase)).ToList();
            var propertyNames = WalkProperties(root).Select(x => x.Name).ToList();
            var hasSimd = propertyNames.Any(x => x.Contains("simd", StringComparison.OrdinalIgnoreCase))
                          || json.Contains("SIMD", StringComparison.OrdinalIgnoreCase);
            var hasRootMotion = propertyNames.Any(x => x.Contains("rootMotion", StringComparison.OrdinalIgnoreCase));
            var cap = Math.Clamp(maxResults, 1, 5000);
            var warnings = new List<string>();
            if (clips.Count > cap || events.Count > cap)
                warnings.Add($"Result lists are capped at {cap}; total counts remain exact.");

            return JsonSerializer.Serialize(new
            {
                ok = true, status = warnings.Count == 0 ? "success" : "partial",
                summary = $"{TypeOf(root)}: {clips.Count} clip(s), {events.Count} event(s), SIMD signal={hasSimd}, root motion signal={hasRootMotion}",
                produced = Array.Empty<string>(), warnings, errors = Array.Empty<string>(), log = "",
                file = animsOrJson, rootType = TypeOf(root), hasSimd, hasRootMotion,
                clipCount = clips.Count, clipsTruncated = clips.Count > cap, clips = clips.Take(cap),
                eventCount = events.Count, eventsTruncated = events.Count > cap, events = events.Take(cap),
                references = CollectDepotPaths(doc.RootElement).Select(r => new { role = r.role, path = r.path }),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Unreadable animation JSON: {ex.Message}"); }
    }

    [McpServerTool(Name = "inspect_streamingsector", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Produces a compact semantic view of a .streamingsector: world-node histogram, resource references and transform-bearing nodes.")]
    public static async Task<string> InspectStreamingSector(
        Cp77ToolsRunner runner,
        [Description("Path to an extracted .streamingsector or its serialized JSON.")] string sectorOrJson,
        [Description("Maximum node/reference rows returned; totals remain exact.")] int maxResults = 500,
        CancellationToken ct = default)
    {
        var (json, error) = await LoadCr2wJsonText(runner, sectorOrJson, ct);
        if (json is null) return Err(error!);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = RootChunkOf(doc.RootElement);
            var nodes = WalkObjects(root)
                .Select(o => new
                {
                    Element = o,
                    Type = TypeOf(o),
                    Name = FirstString(o, "name", "debugName", "nodeName"),
                })
                .Where(x => x.Type.Contains("Node", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var refs = CollectDepotPaths(doc.RootElement);
            var cap = Math.Clamp(maxResults, 1, 10000);
            var warnings = new List<string>();
            if (nodes.Count > cap || refs.Count > cap)
                warnings.Add($"Result lists are capped at {cap}; total counts remain exact.");
            return JsonSerializer.Serialize(new
            {
                ok = true, status = warnings.Count == 0 ? "success" : "partial",
                summary = $"{TypeOf(root)}: {nodes.Count} world node(s), {refs.Count} resource reference(s)",
                produced = Array.Empty<string>(), warnings, errors = Array.Empty<string>(), log = "",
                file = sectorOrJson, rootType = TypeOf(root), nodeCount = nodes.Count,
                nodeTypes = nodes.GroupBy(x => x.Type).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
                nodesTruncated = nodes.Count > cap,
                nodes = nodes.Take(cap).Select(x => new
                {
                    type = x.Type,
                    name = x.Name,
                    hasTransform = HasAnyProperty(x.Element, "transform", "position", "orientation", "scale"),
                    resource = FirstDepotPath(x.Element),
                }),
                referenceCount = refs.Count, referencesTruncated = refs.Count > cap,
                references = refs.Take(cap).Select(r => new { role = r.role, path = r.path }),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Unreadable streaming-sector JSON: {ex.Message}"); }
    }

    [McpServerTool(Name = "validate_project", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Validates a mod project end-to-end: parses CR2W resources, checks DepotPath references against project files and optional game archives, parses JSON/YAML/XL/Tweak files, and performs a REDscript delimiter sanity check.")]
    public static async Task<string> ValidateProject(
        Cp77ToolsRunner runner,
        [Description("Project root or archive/source folder to validate recursively.")] string projectPath,
        [Description("Optional Cyberpunk 2077 root used to resolve references in archive/pc/content.")] string? gamePath = null,
        [Description("Maximum CR2W files parsed in one call (1-10000).")] int maxFiles = 2000,
        [Description("Maximum issue/reference rows returned (counts remain exact).")] int maxResults = 1000,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(projectPath)) return Err($"Project folder not found: {projectPath}");
        var allFiles = Directory.EnumerateFiles(projectPath, "*", SearchOption.AllDirectories).ToList();
        var localPaths = BuildProjectPathIndex(projectPath, allFiles);
        var cr2w = allFiles.Where(f => ProjectCr2wExtensions.Contains(Path.GetExtension(f)))
            .Take(Math.Clamp(maxFiles, 1, 10000)).ToList();
        var unreadable = new List<object>();
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < cr2w.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressNotificationValue { Progress = i + 1, Total = cr2w.Count, Message = Path.GetFileName(cr2w[i]) });
            var (json, error) = await LoadCr2wJsonText(runner, cr2w[i], ct);
            if (json is null) { unreadable.Add(new { file = cr2w[i], error }); continue; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var (_, path) in CollectDepotPaths(doc.RootElement)) references.Add(NormalizeDepotPath(path));
            }
            catch (Exception ex) { unreadable.Add(new { file = cr2w[i], error = ex.Message }); }
        }

        var textErrors = ValidateProjectTextFiles(allFiles);
        HashSet<string>? gamePaths = null;
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            var content = Path.Combine(gamePath, "archive", "pc", "content");
            if (!Directory.Exists(content)) return Err($"Game archive folder not found: {content}");
            gamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var archive in Directory.EnumerateFiles(content, "*.archive").OrderBy(x => x))
            {
                var (entries, _, _) = await runner.GetArchiveListingAsync(archive, ct);
                foreach (var entry in entries) gamePaths.Add(NormalizeDepotPath(entry));
            }
        }
        else
        {
            warnings.Add("gamePath was not supplied; non-project references are reported as unresolved rather than broken.");
        }

        var unresolved = references.Where(r => !localPaths.Contains(r) && (gamePaths is null || !gamePaths.Contains(r)))
            .OrderBy(x => x).ToList();
        var cap = Math.Clamp(maxResults, 1, 10000);
        if (cr2w.Count < allFiles.Count(f => ProjectCr2wExtensions.Contains(Path.GetExtension(f))))
            warnings.Add($"CR2W parsing capped at {cr2w.Count} file(s); raise maxFiles for a complete scan.");
        if (unreadable.Count > cap || textErrors.Count > cap || unresolved.Count > cap)
            warnings.Add($"Issue lists are capped at {cap}; total counts remain exact.");
        var brokenCount = gamePaths is null ? 0 : unresolved.Count;
        var ok = unreadable.Count == 0 && textErrors.Count == 0 && brokenCount == 0;
        return JsonSerializer.Serialize(new
        {
            ok,
            status = ok ? warnings.Count == 0 ? "success" : "partial" : "error",
            summary = $"Project validation: {cr2w.Count} CR2W parsed, {unreadable.Count} unreadable, {textErrors.Count} text error(s), " +
                      (gamePaths is null ? $"{unresolved.Count} unresolved reference(s)" : $"{brokenCount} broken reference(s)"),
            produced = Array.Empty<string>(), warnings,
            errors = unreadable.Select(x => "Unreadable CR2W resource").Concat(textErrors.Select(x => x.error)).Distinct(),
            log = "",
            projectPath, gamePath, filesScanned = allFiles.Count, cr2wScanned = cr2w.Count,
            referenceCount = references.Count,
            unreadableCount = unreadable.Count, unreadable = unreadable.Take(cap),
            textErrorCount = textErrors.Count, textErrors = textErrors.Take(cap),
            brokenReferenceCount = brokenCount,
            unresolvedReferenceCount = gamePaths is null ? unresolved.Count : 0,
            referencesTruncated = unresolved.Count > cap,
            missingReferences = unresolved.Take(cap),
        }, JsonOpts);
    }

    private static IEnumerable<JsonElement> WalkObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
                foreach (var child in WalkObjects(property.Value)) yield return child;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var child in WalkObjects(item)) yield return child;
    }

    private static IEnumerable<JsonProperty> WalkProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                yield return property;
                foreach (var child in WalkProperties(property.Value)) yield return child;
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var child in WalkProperties(item)) yield return child;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String) return value.GetString();
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$value", out var wrapped)
                    && wrapped.ValueKind == JsonValueKind.String) return wrapped.GetString();
            }
        return null;
    }

    private static IEnumerable<string> NamedStrings(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (FirstString(element, name) is { Length: > 0 } value) yield return value;
    }

    private static double? FirstNumber(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$value", out var wrapped)
                && wrapped.ValueKind == JsonValueKind.Number && wrapped.TryGetDouble(out number)) return number;
        }
        return null;
    }

    private static string? FirstDepotPath(JsonElement element)
        => CollectDepotPaths(element).FirstOrDefault().path;

    private static bool HasAnyProperty(JsonElement element, params string[] names)
        => element.ValueKind == JsonValueKind.Object && element.EnumerateObject()
            .Any(p => names.Any(n => p.Name.Contains(n, StringComparison.OrdinalIgnoreCase)));

    private static HashSet<string> BuildProjectPathIndex(string root, IEnumerable<string> files)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var path = NormalizeDepotPath(Path.GetRelativePath(root, file));
            result.Add(path);
            foreach (var marker in new[] { "source/archive/", "archive/", "mod/" })
            {
                var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0) result.Add(path[(index + marker.Length)..]);
            }
        }
        return result;
    }

    private static string NormalizeDepotPath(string path)
        => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private static List<(string file, string error)> ValidateProjectTextFiles(IEnumerable<string> files)
    {
        var errors = new List<(string file, string error)>();
        var yaml = new DeserializerBuilder().Build();
        foreach (var file in files)
        {
            try
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".json") JsonDocument.Parse(File.ReadAllText(file)).Dispose();
                else if (ext is ".yaml" or ".yml" or ".tweak" or ".xl")
                    yaml.Deserialize<object>(File.ReadAllText(file));
                else if (ext == ".reds")
                {
                    var text = File.ReadAllText(file);
                    if (text.Count(c => c == '{') != text.Count(c => c == '}'))
                        errors.Add((file, "Unbalanced REDscript braces."));
                }
            }
            catch (Exception ex) { errors.Add((file, ex.Message)); }
        }
        return errors;
    }
}
