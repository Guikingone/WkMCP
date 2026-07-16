using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WkMcp;

/// <summary>
/// Builds the stable top-level response contract shared by every MCP tool.
/// Tool-specific payloads are added as extra properties without replacing the
/// universal fields consumed by agents.
/// </summary>
internal static class ToolResult
{
    internal static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string Create(
        bool ok,
        string status,
        string summary,
        IEnumerable<string>? produced = null,
        IEnumerable<string>? warnings = null,
        IEnumerable<string>? errors = null,
        string? log = null,
        int? exitCode = null,
        object? data = null,
        IReadOnlyDictionary<string, object?>? extra = null,
        JsonSerializerOptions? options = null)
    {
        options ??= DefaultJsonOptions;
        var root = new JsonObject
        {
            ["ok"] = ok,
            ["status"] = status,
            ["summary"] = summary,
            ["produced"] = JsonSerializer.SerializeToNode(produced?.ToArray() ?? [], options),
            ["warnings"] = JsonSerializer.SerializeToNode(warnings?.ToArray() ?? [], options),
            ["errors"] = JsonSerializer.SerializeToNode(errors?.ToArray() ?? [], options),
            ["log"] = log ?? "",
        };

        if (exitCode is not null)
            root["exitCode"] = exitCode.Value;
        if (data is not null)
            root["data"] = JsonSerializer.SerializeToNode(data, options);
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                if (root.ContainsKey(key))
                    throw new ArgumentException($"Tool-specific property '{key}' conflicts with the universal result contract.", nameof(extra));
                root[key] = JsonSerializer.SerializeToNode(value, options);
            }
        }

        return root.ToJsonString(options);
    }

    internal static string Error(string summary, JsonSerializerOptions? options = null)
        => Create(false, "error", summary, errors: [summary], exitCode: -1, options: options);
}
