using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace WkMcp;

/// <summary>
/// Asset-inspection MCP tools (second partial of <see cref="ModdingTools"/>): fills the
/// coverage gaps for file families that had no dedicated tooling — materials
/// (<c>.mi</c>/<c>.mlsetup</c>), UI (<c>.inkatlas</c>/<c>.inkwidget</c>), rigs
/// (<c>.rig</c>), the cross-file material chain, a generic CR2W diff, and a Nexus
/// pre-flight. Like the other inspectors they accept either a binary file (converted via
/// the daemon) or a <c>.json</c> already produced by <c>read_game_file</c>/<c>cr2w_to_json</c>.
///
/// The pure analysis cores (<see cref="SummarizeMaterialInstance"/>, <see cref="SummarizeMlSetup"/>,
/// <see cref="SetMaterialInstanceParam"/>, <see cref="ParseInkAtlasParts"/>,
/// <see cref="SummarizeInkWidget"/>, <see cref="SummarizeRig"/>, <see cref="CollectDepotPaths"/>,
/// <see cref="NexusPreflight"/>) operate on parsed JSON / a folder and are unit-tested.
///
/// Caveat: the field shapes follow WolvenKit's CR2W→JSON conventions; the inspectors are
/// deliberately defensive (extract what is present, fall back to a generic $type histogram)
/// because they are validated against synthetic fixtures, not a live game install.
/// </summary>
public static partial class ModdingTools
{
    // ── shared loader: a .json is read as-is, anything else is converted via the daemon ──
    private static async Task<(string? json, string? error)> LoadCr2wJsonText(
        Cp77ToolsRunner runner, string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return (null, $"File not found: {path}");
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try { return (await File.ReadAllTextAsync(path, ct), null); }
            catch (Exception e) { return (null, $"Unreadable JSON: {e.Message}"); }
        }
        var json = await ConvertCr2wToJsonText(runner, path, ct);
        return json is null
            ? (null, $"CR2W→JSON conversion failed: {Path.GetFileName(path)}")
            : (json, null);
    }

    /// <summary>Unwraps Data.RootChunk (a file) — or a bare RootChunk (already unwrapped).</summary>
    private static JsonElement RootChunkOf(JsonElement root)
        => root.TryGetProperty("Data", out var d) && d.TryGetProperty("RootChunk", out var rc)
           && rc.ValueKind == JsonValueKind.Object ? rc : root;

    /// <summary>Unwraps a CHandle ({HandleId, Data:{…}}) array entry to its inner object.</summary>
    private static JsonElement UnwrapData(JsonElement e)
        => e.TryGetProperty("Data", out var d) && d.ValueKind == JsonValueKind.Object ? d : e;

    private static string TypeOf(JsonElement o)
        => o.TryGetProperty("$type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "?";

    /// <summary>Reads a number from a property that is a plain number or a {$value} wrapper.</summary>
    private static double? NumProp(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n)) return n;
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("$value", out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n2)) return n2;
        return null;
    }

    /// <summary>Reads a CName-ish string: a plain string, or {$value:"…"} (CName/CString).</summary>
    private static string? NameLike(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return e.GetString();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("$value", out var v)
            && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    // diff_cr2w — generic semantic diff of two CR2W files (or their JSON)
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "diff_cr2w", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Semantic diff of TWO arbitrary CR2W files (or their cr2w_to_json output): " +
                 "field-level added / removed / changed, with each change's exact JSON path. " +
                 "Generalizes diff_mod_vs_base (which only compares a mod override against the base " +
                 "game) to any two files — review a hand-edit, compare two mod versions, etc. Each " +
                 "side may be a binary CR2W (converted internally) or a .json. The $.Header subtree " +
                 "(conversion noise) is ignored.")]
    public static async Task<string> DiffCr2w(
        Cp77ToolsRunner runner,
        [Description("First file (the \"base\" side): a CR2W binary or its .json.")] string fileA,
        [Description("Second file (the \"new\" side): a CR2W binary or its .json.")] string fileB,
        [Description("Max entries returned per category (default 100).")] int maxResults = 100,
        CancellationToken ct = default)
    {
        var (jsonA, errA) = await LoadCr2wJsonText(runner, fileA, ct);
        if (jsonA is null) return Err($"A side: {errA}");
        var (jsonB, errB) = await LoadCr2wJsonText(runner, fileB, ct);
        if (jsonB is null) return Err($"B side: {errB}");

        var (added, removed, changedList) = DiffJson(jsonA, jsonB);
        var cap = Math.Max(1, maxResults);
        var changed = changedList.Select(c => (object)new { path = c.Path, a = c.Base, b = c.Mod }).ToList();
        var truncated = added.Count > cap || removed.Count > cap || changed.Count > cap;
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Diff {Path.GetFileName(fileA)} → {Path.GetFileName(fileB)}: " +
                      $"{added.Count} added, {removed.Count} removed, {changed.Count} changed",
            fileA,
            fileB,
            addedCount = added.Count,
            removedCount = removed.Count,
            changedCount = changed.Count,
            added = added.Take(cap),
            removed = removed.Take(cap),
            changed = changed.Take(cap),
            truncated,
            warnings = truncated ? new[] { $"Truncated to {cap} entries per category — refine or raise maxResults." } : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // inspect_material — a .mi (CMaterialInstance): base material + parameters
    // ════════════════════════════════════════════════════════════════════════
    internal sealed record MaterialParam(string Name, string Kind, string? DepotPath, string Display);

    [McpServerTool(Name = "inspect_material", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Summary of a material instance (.mi / CMaterialInstance): its baseMaterial " +
                 "(.mt/.mi it derives from) and every parameter with its kind (color, scalar, " +
                 "texture, vector…) and value — texture parameters expose their DepotPath. The " +
                 "starting point for any recolor/retexture: see what a material exposes before " +
                 "editing it with edit_material_instance (or write_game_file). Accepts a .mi or its .json.")]
    public static async Task<string> InspectMaterial(
        Cp77ToolsRunner runner,
        [Description("A .mi file or its converted .json.")] string materialOrJson,
        CancellationToken ct = default)
    {
        var (json, err) = await LoadCr2wJsonText(runner, materialOrJson, ct);
        if (json is null) return Err(err!);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var (type, baseMat, values) = SummarizeMaterialInstance(doc.RootElement);
            var warnings = new List<string>();
            if (type != "CMaterialInstance")
                warnings.Add($"Root is '{type}', not CMaterialInstance — values may be incomplete.");
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = warnings.Count == 0 ? "success" : "partial",
                summary = $"{type}: baseMaterial '{baseMat ?? "(none)"}', {values.Count} parameter(s)",
                file = materialOrJson,
                rootType = type,
                baseMaterial = baseMat,
                parameterCount = values.Count,
                textureCount = values.Count(v => v.Kind == "texture"),
                parameters = values.Select(v => new { name = v.Name, kind = v.Kind, depotPath = v.DepotPath, value = v.Display }),
                warnings,
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
    }

    /// <summary>(rootType, baseMaterial DepotPath, parameters) of a CMaterialInstance JSON.
    /// Handles the WolvenKit "values = array of single-key wrappers" shape AND the
    /// {Key,Value} shape; degrades gracefully on the unexpected.</summary>
    internal static (string? type, string? baseMaterial, List<MaterialParam> values)
        SummarizeMaterialInstance(JsonElement root)
    {
        var rc = RootChunkOf(root);
        var type = TypeOf(rc);
        var baseMat = DepotPathVal(rc, "baseMaterial");
        var values = new List<MaterialParam>();
        if (rc.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
            foreach (var entry in vals.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Object)
                    values.Add(ReadMaterialParam(entry));
        return (type, baseMat, values);
    }

    private static MaterialParam ReadMaterialParam(JsonElement entry)
    {
        // Shape A: explicit { Key: <CName>, Value: <variant> } pair.
        if (entry.TryGetProperty("Value", out var explicitVal)
            && (entry.TryGetProperty("Key", out _) || entry.TryGetProperty("key", out _)))
        {
            var key = (entry.TryGetProperty("Key", out var k) ? NameLike(k) : null)
                      ?? (entry.TryGetProperty("key", out var k2) ? NameLike(k2) : null) ?? "?";
            return DescribeParam(key, explicitVal);
        }
        // Shape B: single-key wrapper { "<ParamName>": <variant>, ($type?) }.
        foreach (var p in entry.EnumerateObject())
        {
            if (p.Name == "$type") continue;
            return DescribeParam(p.Name, p.Value);
        }
        return new MaterialParam("?", "unknown", null, TypeOf(entry));
    }

    private static MaterialParam DescribeParam(string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("DepotPath", out var dp) && dp.TryGetProperty("$value", out var dpv)
                && dpv.ValueKind == JsonValueKind.String)
            {
                var path = dpv.GetString();
                return new MaterialParam(name, "texture", string.IsNullOrEmpty(path) ? null : path,
                    string.IsNullOrEmpty(path) ? "(empty ref)" : path!);
            }
            if (value.TryGetProperty("Red", out var r) && value.TryGetProperty("Green", out var g)
                && value.TryGetProperty("Blue", out var b))
            {
                var a = value.TryGetProperty("Alpha", out var al) ? al.ToString() : "255";
                return new MaterialParam(name, "color", null, $"rgba({r},{g},{b},{a})");
            }
            if (value.TryGetProperty("X", out var x) && value.TryGetProperty("Y", out var y))
            {
                var z = value.TryGetProperty("Z", out var zz) ? "," + zz : "";
                var w = value.TryGetProperty("W", out var ww) ? "," + ww : "";
                return new MaterialParam(name, "vector", null, $"({x},{y}{z}{w})");
            }
            return new MaterialParam(name, TypeOf(value) == "?" ? "object" : TypeOf(value), null, TypeOf(value));
        }
        if (value.ValueKind is JsonValueKind.Number)
            return new MaterialParam(name, "scalar", null, value.ToString());
        if (value.ValueKind is JsonValueKind.String)
            return new MaterialParam(name, "string", null, value.GetString() ?? "");
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return new MaterialParam(name, "bool", null, value.ToString());
        return new MaterialParam(name, "unknown", null, value.ToString());
    }

    // ════════════════════════════════════════════════════════════════════════
    // inspect_mlsetup — a .mlsetup (Multilayer_Setup): per-layer breakdown
    // ════════════════════════════════════════════════════════════════════════
    internal sealed record MlLayer(int Index, string? Material, string? Microblend, string? ColorScale,
        double? Opacity, double? NormalStrength, double? MatTile, double? MbTile);

    [McpServerTool(Name = "inspect_mlsetup", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Summary of a multilayer setup (.mlsetup / Multilayer_Setup): its layers, and per " +
                 "layer the material (.mltemplate/.mlmask) and microblend it references plus its " +
                 "colorScale, opacity, normalStrength and tiling. The map of which layer drives which " +
                 "look — so you know which texture/colorScale to change. Accepts a .mlsetup or its .json.")]
    public static async Task<string> InspectMlsetup(
        Cp77ToolsRunner runner,
        [Description("A .mlsetup file or its converted .json.")] string mlsetupOrJson,
        CancellationToken ct = default)
    {
        var (json, err) = await LoadCr2wJsonText(runner, mlsetupOrJson, ct);
        if (json is null) return Err(err!);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var (type, layers) = SummarizeMlSetup(doc.RootElement);
            var warnings = new List<string>();
            if (type != "Multilayer_Setup")
                warnings.Add($"Root is '{type}', not Multilayer_Setup — layers may be incomplete.");
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = warnings.Count == 0 ? "success" : "partial",
                summary = $"{type}: {layers.Count} layer(s)",
                file = mlsetupOrJson,
                rootType = type,
                layerCount = layers.Count,
                layers = layers.Select(l => new
                {
                    index = l.Index,
                    material = l.Material,
                    microblend = l.Microblend,
                    colorScale = l.ColorScale,
                    opacity = l.Opacity,
                    normalStrength = l.NormalStrength,
                    matTile = l.MatTile,
                    mbTile = l.MbTile,
                }),
                warnings,
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
    }

    /// <summary>(rootType, layers) of a Multilayer_Setup JSON. Layer entries may be plain
    /// objects or CHandles ({Data:{…}}).</summary>
    internal static (string? type, List<MlLayer> layers) SummarizeMlSetup(JsonElement root)
    {
        var rc = RootChunkOf(root);
        var type = TypeOf(rc);
        var layers = new List<MlLayer>();
        if (rc.TryGetProperty("layers", out var ls) && ls.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var raw in ls.EnumerateArray())
            {
                var l = UnwrapData(raw);
                layers.Add(new MlLayer(
                    i++,
                    DepotPathVal(l, "material"),
                    DepotPathVal(l, "microblend"),
                    l.TryGetProperty("colorScale", out var cs) ? NameLike(cs) : null,
                    NumProp(l, "opacity"),
                    NumProp(l, "normalStrength") ?? NumProp(l, "normalStrenght"), // tolerate the known engine typo
                    NumProp(l, "matTile"),
                    NumProp(l, "mbTile")));
            }
        }
        return (type, layers);
    }

    // ════════════════════════════════════════════════════════════════════════
    // edit_material_instance — set one named parameter of a .mi (JSON → JSON)
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "edit_material_instance", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Sets ONE named parameter of a material instance (.mi) and writes the edited JSON " +
                 "to outputJson — then feed it to json_to_cr2w (or write_game_file) to get the .mi " +
                 "back. type = texture (value = depot path), color (value = 'r,g,b,a' 0-255), scalar " +
                 "(value = number) or string. The parameter must already exist (use inspect_material " +
                 "to list them). Input may be a .mi or its .json; the output is always JSON.")]
    public static async Task<string> EditMaterialInstance(
        Cp77ToolsRunner runner,
        [Description("A .mi file or its converted .json.")] string materialOrJson,
        [Description("Output JSON path (feed to json_to_cr2w / write_game_file).")] string outputJson,
        [Description("Parameter name to set (e.g. 'DiffuseColor', 'BaseColor', 'MultilayerSetup').")] string parameter,
        [Description("New value: depot path (texture), 'r,g,b,a' (color), a number (scalar), or text (string).")] string value,
        [Description("Parameter kind: texture | color | scalar | string (default texture).")] string type = "texture",
        CancellationToken ct = default)
    {
        var (json, err) = await LoadCr2wJsonText(runner, materialOrJson, ct);
        if (json is null) return Err(err!);
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
        if (root is null) return Err("Empty JSON.");

        var (ok, error, before) = SetMaterialInstanceParam(root, parameter, value, type);
        if (!ok) return Err(error!);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputJson)) ?? ".");
            await File.WriteAllTextAsync(outputJson, root.ToJsonString(JsonOpts), ct);
        }
        catch (Exception ex) { return Err($"Cannot write output: {ex.Message}"); }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Set '{parameter}' ({type}) = {value} → {Path.GetFileName(outputJson)}",
            file = materialOrJson,
            parameter,
            type,
            previousValue = before,
            newValue = value,
            produced = new[] { outputJson },
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Mutates a CMaterialInstance JsonNode: sets the value of an EXISTING named
    /// parameter (in the values array). Returns (ok, error, previousDisplay). Pure + testable.</summary>
    internal static (bool ok, string? error, string? before) SetMaterialInstanceParam(
        JsonNode root, string parameter, string value, string type)
    {
        var rc = (root["Data"]?["RootChunk"] as JsonObject) ?? root as JsonObject;
        if (rc?["values"] is not JsonArray values)
            return (false, "No 'values' array in the material (not a CMaterialInstance?).", null);

        foreach (var raw in values)
        {
            // Locate the value node for this parameter under either shape.
            JsonNode? target = null;
            string? before = null;
            if (raw is not JsonObject obj) continue;

            if (obj["Value"] is { } vNode && (obj["Key"] is { } || obj["key"] is { }))
            {
                var key = NameLikeNode(obj["Key"]) ?? NameLikeNode(obj["key"]);
                if (!string.Equals(key, parameter, StringComparison.Ordinal)) continue;
                target = vNode; before = vNode.ToJsonString();
            }
            else if (obj[parameter] is { } wrapped && parameter != "$type")
            {
                target = wrapped; before = wrapped.ToJsonString();
            }
            if (target is null) continue;

            switch (type.ToLowerInvariant())
            {
                case "texture":
                    if (target["DepotPath"] is JsonObject dp) dp["$value"] = value;
                    else return (false, $"Parameter '{parameter}' is not a texture (no DepotPath).", before);
                    break;
                case "color":
                    var parts = value.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length < 3 || !parts.Take(3).All(p => byte.TryParse(p, out _)))
                        return (false, "Color must be 'r,g,b' or 'r,g,b,a' with 0-255 components.", before);
                    if (target is not JsonObject col)
                        return (false, $"Parameter '{parameter}' is not a color object.", before);
                    col["Red"] = int.Parse(parts[0]); col["Green"] = int.Parse(parts[1]); col["Blue"] = int.Parse(parts[2]);
                    col["Alpha"] = parts.Length > 3 && byte.TryParse(parts[3], out var a) ? (int)a : 255;
                    break;
                case "scalar":
                    if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var num))
                        return (false, "Scalar value must be a number.", before);
                    SetScalarNode(obj, parameter, target, num);
                    break;
                case "string":
                    SetScalarNode(obj, parameter, target, value);
                    break;
                default:
                    return (false, $"Unknown type '{type}' (use texture|color|scalar|string).", before);
            }
            return (true, null, before);
        }
        return (false, $"Parameter '{parameter}' not found. Use inspect_material to list parameters.", null);
    }

    private static void SetScalarNode(JsonObject parent, string paramName, JsonNode target, object newValue)
    {
        var node = newValue is double d ? JsonValue.Create(d) : JsonValue.Create((string)newValue);
        // If the value lives inside a wrapper object (e.g. {"$type":"Float","$value":3.0}), set $value;
        // otherwise replace the slot itself.
        if (target is JsonObject o && o.ContainsKey("$value")) o["$value"] = node;
        else if (parent["Value"] is not null) parent["Value"] = node;
        else parent[paramName] = node;
    }

    private static string? NameLikeNode(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (n is JsonObject o && o["$value"] is JsonValue vv && vv.TryGetValue<string>(out var s2)) return s2;
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    // inspect_inkatlas / resolve_inkatlas_part — UI texture atlases
    // ════════════════════════════════════════════════════════════════════════
    internal sealed record InkPart(string PartName, string? Texture, string? UvRect, string? PixelRect);

    [McpServerTool(Name = "inspect_inkatlas", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Summary of a UI texture atlas (.inkatlas / inkTextureAtlas): the texture(s) it " +
                 "packs and every named part (sprite) with its clipping rect in UV and pixel " +
                 "coordinates. UI/icon mods (weapon icons, quickhacks, map pins) live here and are " +
                 "opaque without this. Pair with resolve_inkatlas_part to look one part up by name. " +
                 "Accepts an .inkatlas or its .json.")]
    public static async Task<string> InspectInkatlas(
        Cp77ToolsRunner runner,
        [Description("An .inkatlas file or its converted .json.")] string inkatlasOrJson,
        [Description("Max parts returned (default 200). partCount always gives the real total.")] int maxParts = 200,
        CancellationToken ct = default)
    {
        var (json, err) = await LoadCr2wJsonText(runner, inkatlasOrJson, ct);
        if (json is null) return Err(err!);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var (textures, parts) = ParseInkAtlasParts(doc.RootElement);
            var cap = Math.Max(1, maxParts);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = parts.Count > 0 || textures.Count > 0 ? "success" : "partial",
                summary = $"inkatlas: {textures.Count} texture(s), {parts.Count} part(s)",
                file = inkatlasOrJson,
                textures,
                partCount = parts.Count,
                truncated = parts.Count > cap,
                parts = parts.Take(cap).Select(p => new { name = p.PartName, texture = p.Texture, uvRect = p.UvRect, pixelRect = p.PixelRect }),
                warnings = parts.Count == 0 ? new[] { "No named part found — unexpected atlas shape or empty file." } : Array.Empty<string>(),
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
    }

    [McpServerTool(Name = "resolve_inkatlas_part", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Looks up ONE part (sprite) in a .inkatlas by name and returns its backing texture " +
                 "DepotPath and clipping rect — so you know exactly which texture and region a UI icon " +
                 "maps to before replacing it. Returns the close matches if the exact name is absent. " +
                 "Accepts an .inkatlas or its .json.")]
    public static async Task<string> ResolveInkatlasPart(
        Cp77ToolsRunner runner,
        [Description("An .inkatlas file or its converted .json.")] string inkatlasOrJson,
        [Description("Part name to resolve (CName, e.g. 'weapon_icon_katana').")] string partName,
        CancellationToken ct = default)
    {
        var (json, err) = await LoadCr2wJsonText(runner, inkatlasOrJson, ct);
        if (json is null) return Err(err!);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var (_, parts) = ParseInkAtlasParts(doc.RootElement);
            var hit = parts.FirstOrDefault(p => string.Equals(p.PartName, partName, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                var near = parts.Where(p => p.PartName.IndexOf(partName, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(p => p.PartName).Take(10).ToList();
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    status = "error",
                    summary = $"Part '{partName}' not found ({parts.Count} part(s) in atlas).",
                    file = inkatlasOrJson,
                    suggestions = near,
                    errors = new[] { $"Part '{partName}' not found." },
                }, JsonOpts);
            }
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"Part '{hit.PartName}' → {hit.Texture ?? "(no texture)"}",
                file = inkatlasOrJson,
                part = new { name = hit.PartName, texture = hit.Texture, uvRect = hit.UvRect, pixelRect = hit.PixelRect },
                warnings = Array.Empty<string>(),
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
    }

    /// <summary>(textures, parts) of an inkTextureAtlas JSON. Primary parse walks slots[].parts[];
    /// a generic fallback collects any object carrying a partName. Testable.</summary>
    internal static (List<string> textures, List<InkPart> parts) ParseInkAtlasParts(JsonElement root)
    {
        var rc = RootChunkOf(root);
        var textures = new List<string>();
        var parts = new List<InkPart>();

        // A null resource ref serializes as a uint64 hash "0" — ignore it.
        void AddTexture(string? t) { if (!string.IsNullOrEmpty(t) && t != "0" && !textures.Contains(t!)) textures.Add(t!); }
        // CStatic<N> arrays serialize as { "Elements": [ … ] }; plain arrays stay arrays.
        static IEnumerable<JsonElement> Elements(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array) return node.EnumerateArray().ToList();
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("Elements", out var el)
                && el.ValueKind == JsonValueKind.Array) return el.EnumerateArray().ToList();
            return Enumerable.Empty<JsonElement>();
        }
        static string? Rect(JsonElement o, string prop)
        {
            if (!o.TryGetProperty(prop, out var r) || r.ValueKind != JsonValueKind.Object) return null;
            string? F(string n) => r.TryGetProperty(n, out var v) ? v.ToString() : null;
            // inkUITransparencyDescriptor / RectF: {Left,Top,Right,Bottom} or {top,left,right,bottom}.
            var l = F("Left") ?? F("left"); var t = F("Top") ?? F("top");
            var ri = F("Right") ?? F("right"); var b = F("Bottom") ?? F("bottom");
            return l is null && t is null ? null : $"[{l},{t},{ri},{b}]";
        }
        InkPart ReadPart(JsonElement p, string? slotTexture)
        {
            var name = (p.TryGetProperty("partName", out var pn) ? NameLike(pn) : null) ?? "?";
            // Pixel rect is "clippingRectInPixels" (real files) or "clippingRectInPixelCoords" (older).
            var pixel = Rect(p, "clippingRectInPixels") ?? Rect(p, "clippingRectInPixelCoords");
            return new InkPart(name, slotTexture, Rect(p, "clippingRectInUVCoords"), pixel);
        }

        // Primary: slots (a CStatic → {Elements:[…]}, or a plain array) each with a texture + parts[].
        var rootTexture = DepotPathVal(rc, "texture");
        if (rootTexture == "0") rootTexture = null;
        if (rc.TryGetProperty("slots", out var slots))
            foreach (var raw in Elements(slots))
            {
                var slot = UnwrapData(raw);
                var tex = DepotPathVal(slot, "texture");
                if (tex == "0") tex = null;
                tex ??= rootTexture;
                AddTexture(tex);
                if (slot.TryGetProperty("parts", out var ps))
                    foreach (var p in Elements(ps))
                        if (p.ValueKind == JsonValueKind.Object) parts.Add(ReadPart(UnwrapData(p), tex));
            }
        AddTexture(rootTexture);

        // Fallback: walk the tree for any object carrying a partName (and any DepotPath as a texture).
        if (parts.Count == 0)
        {
            void Walk(JsonElement e)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.Object:
                        if (e.TryGetProperty("partName", out _)) parts.Add(ReadPart(e, null));
                        if (e.TryGetProperty("DepotPath", out var dp) && dp.TryGetProperty("$value", out var dv)
                            && dv.ValueKind == JsonValueKind.String) AddTexture(dv.GetString());
                        foreach (var c in e.EnumerateObject()) Walk(c.Value);
                        break;
                    case JsonValueKind.Array:
                        foreach (var c in e.EnumerateArray()) Walk(c);
                        break;
                }
            }
            Walk(rc);
        }
        return (textures, parts);
    }

    // ════════════════════════════════════════════════════════════════════════
    // inspect_inkwidget — a .inkwidget (inkWidgetLibraryResource)
    // ════════════════════════════════════════════════════════════════════════
    internal sealed record InkWidgetSummary(string? Type, List<string> LibraryItems,
        Dictionary<string, int> WidgetTypes, int TotalWidgets);

    [McpServerTool(Name = "inspect_inkwidget", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Summary of a UI widget library (.inkwidget / inkWidgetLibraryResource): the named " +
                 "library items (the entry-point widgets you reference from code) and a histogram of " +
                 "the widget types used (inkTextWidget, inkImageWidget, inkCanvasWidget…). The map of " +
                 "a HUD/menu before editing it — find which item and which widget types to touch. " +
                 "Accepts an .inkwidget or its .json.")]
    public static async Task<string> InspectInkwidget(
        Cp77ToolsRunner runner,
        [Description("An .inkwidget file or its converted .json.")] string inkwidgetOrJson,
        CancellationToken ct = default)
    {
        var (json, err) = await LoadCr2wJsonText(runner, inkwidgetOrJson, ct);
        if (json is null) return Err(err!);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var s = SummarizeInkWidget(doc.RootElement);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = s.LibraryItems.Count > 0 || s.TotalWidgets > 0 ? "success" : "partial",
                summary = $"{s.Type}: {s.LibraryItems.Count} library item(s), " +
                          $"{s.TotalWidgets} widget(s) across {s.WidgetTypes.Count} type(s)",
                file = inkwidgetOrJson,
                rootType = s.Type,
                libraryItems = s.LibraryItems,
                totalWidgets = s.TotalWidgets,
                widgetTypes = s.WidgetTypes,
                warnings = s.LibraryItems.Count == 0 ? new[] { "No library item found — unexpected widget resource shape." } : Array.Empty<string>(),
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
    }

    /// <summary>(rootType, libraryItem names, widget-type histogram, total) of an
    /// inkWidgetLibraryResource JSON. The histogram counts every ink* $type in the tree.</summary>
    internal static InkWidgetSummary SummarizeInkWidget(JsonElement root)
    {
        var rc = RootChunkOf(root);
        var type = TypeOf(rc);
        var items = new List<string>();
        if (rc.TryGetProperty("libraryItems", out var li) && li.ValueKind == JsonValueKind.Array)
            foreach (var raw in li.EnumerateArray())
            {
                var item = UnwrapData(raw);
                if (item.TryGetProperty("name", out var n) && NameLike(n) is { } name) items.Add(name);
            }

        var widgetTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    if (e.TryGetProperty("$type", out var t) && t.ValueKind == JsonValueKind.String
                        && t.GetString() is { } ty && ty.StartsWith("ink", StringComparison.Ordinal)
                        && ty.EndsWith("Widget", StringComparison.Ordinal))
                    { widgetTypes[ty] = widgetTypes.GetValueOrDefault(ty) + 1; total++; }
                    foreach (var c in e.EnumerateObject()) Walk(c.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var c in e.EnumerateArray()) Walk(c);
                    break;
            }
        }
        Walk(rc);
        var sorted = widgetTypes.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
        return new InkWidgetSummary(type, items, sorted, total);
    }

    // ════════════════════════════════════════════════════════════════════════
    // inspect_rig — a .rig (animRig): bone hierarchy
    // ════════════════════════════════════════════════════════════════════════
    internal sealed record RigBone(int Index, string Name, int Parent, string? ParentName);

    [McpServerTool(Name = "inspect_rig", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Summary of a rig/skeleton (.rig / animRig): bone count, root bone(s), hierarchy " +
                 "depth, and each bone with its parent — built from boneNames + boneParentIndexes. " +
                 "Lets you check bone names/compatibility for custom clothing, weapons or animations " +
                 "(a mesh skinned to bones the rig lacks ends up mis-deformed). Accepts a .rig or its .json.")]
    public static async Task<string> InspectRig(
        Cp77ToolsRunner runner,
        [Description("A .rig file or its converted .json.")] string rigOrJson,
        [Description("Max bones listed (default 300). boneCount always gives the real total.")] int maxBones = 300,
        CancellationToken ct = default)
    {
        var (json, err) = await LoadCr2wJsonText(runner, rigOrJson, ct);
        if (json is null) return Err(err!);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var (type, bones, roots, maxDepth) = SummarizeRig(doc.RootElement);
            var cap = Math.Max(1, maxBones);
            var warnings = new List<string>();
            if (type != "animRig") warnings.Add($"Root is '{type}', not animRig — bone data may be incomplete.");
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = bones.Count > 0 && warnings.Count == 0 ? "success" : "partial",
                summary = $"{type}: {bones.Count} bone(s), {roots.Count} root(s), depth {maxDepth}",
                file = rigOrJson,
                rootType = type,
                boneCount = bones.Count,
                roots = roots.Select(i => i >= 0 && i < bones.Count ? bones[i].Name : $"#{i}"),
                maxDepth,
                truncated = bones.Count > cap,
                bones = bones.Take(cap).Select(b => new { index = b.Index, name = b.Name, parent = b.Parent, parentName = b.ParentName }),
                warnings,
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
    }

    /// <summary>(rootType, bones, root indices, maxDepth) of an animRig JSON, from
    /// boneNames + boneParentIndexes. Tolerates CName-as-string and {$value} forms.</summary>
    internal static (string? type, List<RigBone> bones, List<int> roots, int maxDepth) SummarizeRig(JsonElement root)
    {
        var rc = RootChunkOf(root);
        var type = TypeOf(rc);
        var names = new List<string>();
        if (rc.TryGetProperty("boneNames", out var bn) && bn.ValueKind == JsonValueKind.Array)
            foreach (var e in bn.EnumerateArray()) names.Add(NameLike(e) ?? "?");

        var parents = new List<int>();
        if (rc.TryGetProperty("boneParentIndexes", out var bp) && bp.ValueKind == JsonValueKind.Array)
            foreach (var e in bp.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var pi)) parents.Add(pi);
                else if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("$value", out var v)
                         && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var pi2)) parents.Add(pi2);
                else parents.Add(-1);
            }

        var bones = new List<RigBone>();
        for (var i = 0; i < names.Count; i++)
        {
            var parent = i < parents.Count ? parents[i] : -1;
            var parentName = parent >= 0 && parent < names.Count ? names[parent] : null;
            bones.Add(new RigBone(i, names[i], parent, parentName));
        }

        var roots = bones.Where(b => b.Parent < 0).Select(b => b.Index).ToList();

        // Depth via memoized walk up the parent chain (cycle-guarded).
        var depthCache = new Dictionary<int, int>();
        int Depth(int idx, HashSet<int> seen)
        {
            if (idx < 0 || idx >= bones.Count) return 0;
            if (depthCache.TryGetValue(idx, out var c)) return c;
            if (!seen.Add(idx)) return 0; // cycle guard
            var p = bones[idx].Parent;
            var d = p < 0 ? 0 : 1 + Depth(p, seen);
            depthCache[idx] = d;
            return d;
        }
        var maxDepth = bones.Count == 0 ? 0 : bones.Max(b => Depth(b.Index, new HashSet<int>()));
        return (type, bones, roots, maxDepth);
    }

    // ════════════════════════════════════════════════════════════════════════
    // trace_material_chain — follow resource refs across files to the textures
    // ════════════════════════════════════════════════════════════════════════
    [McpServerTool(Name = "trace_material_chain", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Traces the material pipeline from a starting file (.mesh/.app/.ent/.mi/.mlsetup) " +
                 "down to the textures it ends up using, following resource references across files: " +
                 ".mesh → .mi → (baseMaterial .mt/.mi, MultilayerSetup .mlsetup) → .mlmask/.xbm. " +
                 "Answers 'which texture do I actually recolor?' without hopping through 5 files by " +
                 "hand. References are resolved by base name under depotRoot (a folder of extracted/" +
                 "converted files), then in the base game if gamePath is given; unresolved refs are " +
                 "flagged. Accepts a binary or its .json as the root.")]
    public static async Task<string> TraceMaterialChain(
        Cp77ToolsRunner runner,
        [Description("Starting file (.mesh/.app/.ent/.mi/.mlsetup) or its .json.")] string fileOrJson,
        [Description("Folder of extracted/converted files used to resolve references (by base name).")] string? depotRoot = null,
        [Description("Game root, to resolve references not found under depotRoot (base archives).")] string? gamePath = null,
        [Description("Max chain depth to follow (default 6).")] int maxDepth = 6,
        CancellationToken ct = default)
    {
        var (rootJson, err) = await LoadCr2wJsonText(runner, fileOrJson, ct);
        if (rootJson is null) return Err(err!);

        var nodes = new List<object>();
        var leaves = new List<string>();
        var unresolved = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const int budget = 200;

        async Task Walk(string label, string json, int depth)
        {
            if (nodes.Count >= budget || depth > Math.Max(1, maxDepth)) return;
            List<(string role, string path)> refs;
            try { using var doc = JsonDocument.Parse(json); refs = CollectDepotPaths(doc.RootElement); }
            catch { return; }

            foreach (var (role, path) in refs)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var isTexture = ext is ".xbm" or ".mlmask" or ".png" or ".dds";
                if (isTexture) { if (!leaves.Contains(path)) leaves.Add(path); }

                nodes.Add(new { from = label, role, path, ext, depth });

                // Recurse only into chainable resources, once each.
                var chainable = ext is ".mi" or ".mt" or ".mlsetup" or ".mesh" or ".app" or ".ent";
                if (!chainable || isTexture || !visited.Add(path)) continue;

                var childJson = await ResolveDepotJson(runner, path, depotRoot, gamePath, ct);
                if (childJson is null) { if (!unresolved.Contains(path)) unresolved.Add(path); continue; }
                await Walk(Path.GetFileName(path), childJson, depth + 1);
            }
        }

        await Walk(Path.GetFileName(fileOrJson), rootJson, 0);

        var status = unresolved.Count > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status,
            summary = $"Chain from {Path.GetFileName(fileOrJson)}: {nodes.Count} ref(s), " +
                      $"{leaves.Count} texture leaf(ves), {unresolved.Count} unresolved",
            file = fileOrJson,
            depotRoot,
            textureLeaves = leaves,
            unresolved,
            nodeCount = nodes.Count,
            truncated = nodes.Count >= budget,
            chain = nodes,
            warnings = unresolved.Count > 0
                ? new[] { $"{unresolved.Count} reference(s) could not be resolved — provide depotRoot/gamePath or extract them first." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Collects every (nearestPropertyName, depotPath) resource reference in a CR2W
    /// JSON tree — an object with a non-empty DepotPath.$value. Type-agnostic, so it follows
    /// material chains regardless of the root type. Testable.</summary>
    internal static List<(string role, string path)> CollectDepotPaths(JsonElement root)
    {
        var rc = RootChunkOf(root);
        var refs = new List<(string role, string path)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Walk(JsonElement e, string role)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    if (e.TryGetProperty("DepotPath", out var dp) && dp.TryGetProperty("$value", out var v)
                        && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } path)
                    {
                        var key = role + " " + path;
                        if (seen.Add(key)) refs.Add((role, path));
                    }
                    foreach (var c in e.EnumerateObject())
                        if (c.Name != "$type") Walk(c.Value, c.Name);
                    break;
                case JsonValueKind.Array:
                    foreach (var c in e.EnumerateArray()) Walk(c, role);
                    break;
            }
        }
        Walk(rc, "$root");
        return refs;
    }

    /// <summary>Resolves a DepotPath to JSON: by base name under depotRoot (converting binaries),
    /// otherwise from the base archives under gamePath.</summary>
    private static async Task<string?> ResolveDepotJson(
        Cp77ToolsRunner runner, string depotPath, string? depotRoot, string? gamePath, CancellationToken ct)
    {
        var baseName = depotPath.Replace('/', '\\').Split('\\')[^1];
        if (!string.IsNullOrEmpty(depotRoot) && Directory.Exists(depotRoot))
        {
            // Prefer an already-converted .json, else convert the binary.
            var jsonHit = Directory.EnumerateFiles(depotRoot, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => f.EndsWith(baseName + ".json", StringComparison.OrdinalIgnoreCase));
            if (jsonHit is not null)
            { try { return await File.ReadAllTextAsync(jsonHit, ct); } catch { /* fall through */ } }
            var binHit = Directory.EnumerateFiles(depotRoot, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => f.EndsWith(baseName, StringComparison.OrdinalIgnoreCase)
                                     && !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            if (binHit is not null) return await ConvertCr2wToJsonText(runner, binHit, ct);
        }
        if (!string.IsNullOrEmpty(gamePath))
        {
            var content = Path.Combine(gamePath, "archive", "pc", "content");
            if (Directory.Exists(content))
            {
                var arc = await FindArchiveContaining(runner, content, depotPath, ct);
                if (arc is not null) return await ExtractAsJson(runner, arc, depotPath, ct);
            }
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════════════
    // package_for_nexus — Nexus pre-flight (quarantine guard + deps) then zip
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>Extensions Nexus Mods auto-quarantines on upload (no executable code).</summary>
    private static readonly HashSet<string> NexusQuarantined = new(StringComparer.OrdinalIgnoreCase)
    { ".dll", ".exe", ".so", ".dylib", ".pdb", ".bin", ".a", ".node", ".asi" };

    [McpServerTool(Name = "package_for_nexus", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Nexus pre-flight + packaging: validates a mod folder for a Nexus release — flags " +
                 "files Nexus auto-quarantines (.dll/.exe/.asi…), checks for a recognized game layout " +
                 "(archive/, r6/, mods/…) and reports the frameworks the mod depends on — then writes " +
                 "a distributable .zip with '/' separators (dev/build noise excluded). Stricter than " +
                 "package_mod, which only zips. Set allowBinaries=true for RED4ext/CET mods that " +
                 "legitimately ship a .dll (the report still lists them).")]
    public static string PackageForNexus(
        [Description("Mod folder in the game layout (contains archive/, r6/, mods/…).")] string sourceFolder,
        [Description("Output .zip path.")] string outputZip,
        [Description("Allow quarantined binaries (RED4ext/CET mods) instead of failing. Default false.")] bool allowBinaries = false)
    {
        if (!Directory.Exists(sourceFolder))
            return Err($"Source folder not found: {sourceFolder}");

        var (quarantined, layoutRoots, deps, fileCount) = NexusPreflight(sourceFolder);
        var warnings = new List<string>();
        if (layoutRoots.Count == 0)
            warnings.Add("No recognized game folder (archive/, r6/, mods/, red4ext/…) at the root — " +
                         "the zip may not install as-is.");
        if (quarantined.Count > 0)
        {
            var msg = $"{quarantined.Count} file(s) Nexus would quarantine: {string.Join(", ", quarantined.Take(10))}" +
                      (quarantined.Count > 10 ? "…" : "");
            if (!allowBinaries)
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    status = "error",
                    summary = "Nexus pre-flight failed: " + msg,
                    sourceFolder,
                    quarantinedFiles = quarantined,
                    layoutRoots,
                    dependencies = deps,
                    hint = "Distribute binaries via GitHub Releases, or pass allowBinaries=true for a RED4ext/CET mod.",
                    errors = new[] { msg },
                }, JsonOpts);
            warnings.Add(msg + " (allowed by allowBinaries).");
        }

        int n = 0, skipped = 0;
        try
        {
            var srcFull = Path.GetFullPath(sourceFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputZip))!);
            if (File.Exists(outputZip)) File.Delete(outputZip);
            using var zs = File.Open(outputZip, FileMode.Create);
            using var zip = new ZipArchive(zs, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(srcFull, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcFull, file).Replace('\\', '/');
                if (IsPackagingNoise(rel)) { skipped++; continue; }
                zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
                n++;
            }
        }
        catch (Exception ex) { return Err($"Packaging failed: {ex.Message}"); }

        var sizeKo = new FileInfo(outputZip).Length / 1024;
        if (skipped > 0) warnings.Add($"{skipped} dev/build file(s) excluded from the bundle.");
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = warnings.Count == 0 ? "success" : "partial",
            summary = $"Nexus package: {n} file(s), {sizeKo} KB → {Path.GetFileName(outputZip)}" +
                      (deps.Count > 0 ? $" · depends on {string.Join(", ", deps)}" : ""),
            sourceFolder,
            outputZip,
            fileCount = n,
            sizeKb = sizeKo,
            layoutRoots,
            dependencies = deps,
            quarantinedFiles = quarantined,
            produced = new[] { outputZip },
            warnings,
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }

    /// <summary>Nexus pre-flight over a folder: (quarantined relative paths, recognized layout
    /// roots, framework dependencies, total file count). Testable.</summary>
    internal static (List<string> quarantined, List<string> layoutRoots, List<string> dependencies, int fileCount)
        NexusPreflight(string sourceFolder)
    {
        var srcFull = Path.GetFullPath(sourceFolder);
        var quarantined = new List<string>();
        var fileCount = 0;
        foreach (var file in Directory.EnumerateFiles(srcFull, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcFull, file).Replace('\\', '/');
            if (IsPackagingNoise(rel)) continue;
            fileCount++;
            if (NexusQuarantined.Contains(Path.GetExtension(file))) quarantined.Add(rel);
        }
        var layoutRoots = Directory.GetDirectories(srcFull).Select(Path.GetFileName)
            .Where(d => d is not null && GameLayoutRoots.Contains(d, StringComparer.OrdinalIgnoreCase))
            .Select(d => d!).OrderBy(d => d).ToList();
        var deps = DetectFrameworks(srcFull, out _, out _).Keys.OrderBy(k => k).ToList();
        return (quarantined, layoutRoots, deps, fileCount);
    }
}
