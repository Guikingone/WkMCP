using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace WkMcp;

/// <summary>
/// MCP tools for Cyberpunk 2077 <b>.scene files</b> (scnSceneResource — the quest/dialogue
/// scene system). Scenes are CR2W: a graph of nodes (sections, choices, hubs, quest
/// bridges…) wired by output→input sockets, with dialogue split across a screenplay store
/// (logical lines/options + speaker) and an embedded loc store (the subtitle text).
///
/// These tools work offline via the daemon's <c>convert serialize/deserialize</c>: inspect
/// a scene's structure, render its graph, find a node/line, validate graph + dialogue
/// integrity, extract dialogue for translation, and write translations back into the
/// embedded loc store. Each tool accepts either a <c>.scene</c> (converted internally) or a
/// <c>.json</c> already produced by <c>read_game_file</c>/<c>cr2w_to_json</c>.
///
/// The pure analysis cores (<see cref="SummarizeScene"/>, <see cref="BuildGraph"/>,
/// <see cref="FindInScene"/>, <see cref="ValidateScene"/>, <see cref="ExtractDialogue"/>,
/// <see cref="ApplyLocalization"/>) operate on a parsed JsonNode and are unit-tested.
/// </summary>
[McpServerToolType]
public static class SceneTools
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>scnCutControlNode's failsafe "backup" output socket looks dangling but is
    /// intentional — whitelist it (community-identified stamp).</summary>
    internal const int CutControlBackupSocketName = 1026;

    /// <summary>uint "none" sentinel used by scnNodeId/scnActorId/scnscreenplayItemId.</summary>
    internal const long NoneId = 4294967295L;

    internal static string Err(string msg) => JsonSerializer.Serialize(new
    {
        ok = false,
        status = "error",
        summary = msg,
        errors = new[] { msg },
    }, JsonOpts);

    internal static string Result(string summary, IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings, object? data = null)
        => JsonSerializer.Serialize(new
        {
            ok = errors.Count == 0,
            status = errors.Count == 0 ? "success" : "error",
            summary,
            errors,
            warnings,
            data,
        }, JsonOpts);

    // ── inspect_scene ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "inspect_scene", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Structural summary of a .scene (scnSceneResource): node count + histogram by " +
                 "node type, #actors/playerActors, #screenplay lines/choice-options, " +
                 "entry/exit/notable points, start/end node ids, version, #workspots/props/" +
                 "effects. Accepts a .scene (converted internally) or its .json. Pair with " +
                 "scene_graph (flow) and validate_scene (integrity).")]
    public static async Task<string> InspectScene(
        Cp77ToolsRunner runner,
        [Description("Path to a .scene file or its converted .json.")] string sceneOrJson,
        CancellationToken ct = default)
    {
        var (root, error) = await SceneToJsonAsync(runner, sceneOrJson, ct);
        if (root is null) return Err(error!);
        var (errors, warnings, summary) = SummarizeScene(root);
        return Result($"Scene inspected: {Path.GetFileName(sceneOrJson)}", errors, warnings, summary);
    }

    internal static (List<string> errors, List<string> warnings, object summary) SummarizeScene(JsonNode root)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var rc = RootChunk(root);
        if (rc is null || Type(rc) != "scnSceneResource")
        {
            errors.Add($"Root is not a scnSceneResource (got '{(rc is null ? "?" : Type(rc))}'). " +
                       "Pass a .scene or its cr2w_to_json output.");
            return (errors, warnings, new { });
        }

        var sg = SceneGraph(rc);
        var nodes = GraphNodes(sg).ToList();
        var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var n in nodes)
            byType[Type(n)] = byType.GetValueOrDefault(Type(n)) + 1;

        long? version = TryLong(rc["version"], out var v) ? v : null;
        var summary = new
        {
            rootType = "scnSceneResource",
            version,
            nodes = nodes.Count,
            byType,
            startNodes = IdArray(sg?["startNodes"]).Count,
            endNodes = IdArray(sg?["endNodes"]).Count,
            actors = (rc["actors"] as JsonArray)?.Count ?? 0,
            playerActors = (rc["playerActors"] as JsonArray)?.Count ?? 0,
            screenplayLines = (rc["screenplayStore"]?["lines"] as JsonArray)?.Count ?? 0,
            screenplayOptions = (rc["screenplayStore"]?["options"] as JsonArray)?.Count ?? 0,
            entryPoints = (rc["entryPoints"] as JsonArray)?.Count ?? 0,
            exitPoints = (rc["exitPoints"] as JsonArray)?.Count ?? 0,
            notablePoints = (rc["notablePoints"] as JsonArray)?.Count ?? 0,
            workspots = (rc["workspots"] as JsonArray)?.Count ?? 0,
            props = (rc["props"] as JsonArray)?.Count ?? 0,
            effects = (rc["effectDefinitions"] as JsonArray)?.Count ?? 0,
            locStoreEntries = (rc["locStore"]?["vpEntries"] as JsonArray)?.Count ?? 0,
        };
        return (errors, warnings, summary);
    }

    // ── scene_graph ───────────────────────────────────────────────────────────

    [McpServerTool(Name = "scene_graph", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Renders a .scene's flow: nodes {id, type, label, choices?} and edges " +
                 "{from, fromSocket, to} from output→input sockets, plus entry/exit points. " +
                 "Choice nodes list their option captions. Output is capped (maxNodes) with a " +
                 "truncated flag. Accepts a .scene or its .json.")]
    public static async Task<string> SceneGraph(
        Cp77ToolsRunner runner,
        [Description("Path to a .scene file or its converted .json.")] string sceneOrJson,
        [Description("Max nodes/edges to return (default 500).")] int maxNodes = 500,
        CancellationToken ct = default)
    {
        var (root, error) = await SceneToJsonAsync(runner, sceneOrJson, ct);
        if (root is null) return Err(error!);
        var rc = RootChunk(root);
        if (rc is null || Type(rc) != "scnSceneResource")
            return Err("Root is not a scnSceneResource.");
        var data = BuildGraph(root, maxNodes);
        return Result($"Scene graph: {Path.GetFileName(sceneOrJson)}",
            Array.Empty<string>(), Array.Empty<string>(), data);
    }

    internal static object BuildGraph(JsonNode root, int maxNodes = 500)
    {
        var rc = RootChunk(root)!;
        var sg = SceneGraph(rc);
        var nodes = GraphNodes(sg).ToList();
        var truncated = nodes.Count > maxNodes;

        var outNodes = new List<object>();
        var edges = new List<object>();
        foreach (var n in nodes.Take(maxNodes))
        {
            var id = NodeId(n);
            var type = Type(n);
            string? label = null;
            List<string>? choices = null;
            if (type == "scnChoiceNode" && n["options"] is JsonArray opts)
            {
                choices = opts.OfType<JsonObject>()
                    .Select(o => o["caption"]?["$value"]?.GetValue<string>() ?? "")
                    .Where(s => s.Length > 0).ToList();
                label = choices.Count > 0 ? string.Join(" | ", choices) : null;
            }
            outNodes.Add(new { id, type, label, choices });

            foreach (var so in (n["outputSockets"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var fromOrd = TryLong(so["stamp"]?["ordinal"], out var fo) ? fo : 0;
                foreach (var dest in (so["destinations"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                {
                    if (!TryLong(dest["nodeId"]?["id"], out var toId)) continue;
                    edges.Add(new { from = id, fromSocket = fromOrd, to = toId });
                    if (edges.Count >= maxNodes * 8) { truncated = true; break; }
                }
            }
        }

        return new
        {
            nodeCount = nodes.Count,
            startNodes = IdArray(sg?["startNodes"]),
            endNodes = IdArray(sg?["endNodes"]),
            entryPoints = NamedPoints(rc["entryPoints"]),
            exitPoints = NamedPoints(rc["exitPoints"]),
            nodes = outNodes,
            edges,
            truncated,
        };
    }

    // ── find_in_scene ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "find_in_scene", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Locates scene elements by 'field': id (a node/screenplay id), type (a node " +
                 "$type substring), or text (choice captions + resolved dialogue text). Returns " +
                 "matches with their JSON path (for read_game_file/write_game_file), node/item " +
                 "type, id and any text. Accepts a .scene or its .json.")]
    public static async Task<string> FindInSceneTool(
        Cp77ToolsRunner runner,
        [Description("Path to a .scene file or its converted .json.")] string sceneOrJson,
        [Description("Value to search (substring, case-insensitive).")] string query,
        [Description("Field: text (default), id, or type.")] string field = "text",
        [Description("Max matches (default 100).")] int maxResults = 100,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query)) return Err("query is required.");
        var (root, error) = await SceneToJsonAsync(runner, sceneOrJson, ct);
        if (root is null) return Err(error!);
        var rc = RootChunk(root);
        if (rc is null || Type(rc) != "scnSceneResource")
            return Err("Root is not a scnSceneResource.");

        var (matches, truncated) = FindInScene(root, query, field, maxResults);
        return Result($"\"{query}\" (field {field}): {matches.Count}{(truncated ? "+" : "")} match(es)",
            Array.Empty<string>(),
            truncated ? new[] { $"Truncated to {maxResults} — refine the query." } : Array.Empty<string>(),
            new { field, query, matchCount = matches.Count, truncated, matches });
    }

    internal static (List<object> matches, bool truncated) FindInScene(
        JsonNode root, string query, string field, int maxResults)
    {
        var rc = RootChunk(root)!;
        var sg = SceneGraph(rc);
        var matches = new List<object>();
        var truncated = false;
        bool Hit(string? s) => s is not null && s.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        bool Add(object m) { if (matches.Count >= maxResults) { truncated = true; return false; } matches.Add(m); return true; }

        var locIndex = BuildLocIndex(rc, null);
        var nodes = GraphNodes(sg).ToList();
        var f = field.ToLowerInvariant();

        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var path = $"Data.RootChunk.sceneGraph.Data.graph[{i}].Data";
            var type = Type(n);
            var id = NodeId(n);
            var matched = f switch
            {
                "id" => Hit(id.ToString()),
                "type" => Hit(type),
                _ => false,
            };
            // text: choice captions on this node
            string? text = null;
            if (f == "text" && type == "scnChoiceNode" && n["options"] is JsonArray opts)
            {
                var cap = opts.OfType<JsonObject>()
                    .Select(o => o["caption"]?["$value"]?.GetValue<string>())
                    .FirstOrDefault(Hit);
                if (cap is not null) { matched = true; text = cap; }
            }
            if (matched && !Add(new { path, kind = "node", type, id, text })) break;
        }

        // Screenplay lines + options (text/id search resolved via loc store).
        void ScanScreenplay(string arrName, string kind)
        {
            if (rc["screenplayStore"]?[arrName] is not JsonArray arr) return;
            for (var j = 0; j < arr.Count; j++)
            {
                if (arr[j] is not JsonObject it) continue;
                var itemId = TryLong(it["itemId"]?["id"], out var iid) ? iid : -1;
                var ruid = it["locstringId"]?["ruid"]?.GetValue<string>();
                var text = ruid is not null && locIndex.TryGetValue(ruid, out var t) ? t : null;
                var matched = f switch
                {
                    "id" => Hit(itemId.ToString()),
                    "type" => false,
                    _ => Hit(text),
                };
                if (matched && !Add(new { path = $"Data.RootChunk.screenplayStore.{arrName}[{j}]", kind, itemId, ruid, text }))
                    return;
            }
        }
        ScanScreenplay("lines", "line");
        ScanScreenplay("options", "choice");

        return (matches, truncated);
    }

    // ── validate_scene ────────────────────────────────────────────────────────

    [McpServerTool(Name = "validate_scene", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Validates a .scene: graph integrity (unique node ids; start/end present and " +
                 "resolve; every output-socket destination resolves to an existing node; " +
                 "reachability), actor references, dialogue refs (dialogLineEvent → screenplay " +
                 "line; choice option → screenplay option; locstrings resolve to non-empty text), " +
                 "and choice-option/socket consistency. Correctly ignores the scnCutControlNode " +
                 "backup socket and scnDeletionMarkerNode tombstones. Accepts a .scene or .json.")]
    public static async Task<string> ValidateSceneTool(
        Cp77ToolsRunner runner,
        [Description("Path to a .scene file or its converted .json.")] string sceneOrJson,
        CancellationToken ct = default)
    {
        var (root, error) = await SceneToJsonAsync(runner, sceneOrJson, ct);
        if (root is null) return Err(error!);
        var (errors, warnings, summary) = ValidateScene(root);
        return Result($"Scene validation: {Path.GetFileName(sceneOrJson)}", errors, warnings, summary);
    }

    internal static (List<string> errors, List<string> warnings, object summary) ValidateScene(JsonNode root)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var rc = RootChunk(root);
        if (rc is null || Type(rc) != "scnSceneResource")
        {
            errors.Add("Root is not a scnSceneResource.");
            return (errors, warnings, new { });
        }

        var sg = SceneGraph(rc);
        var nodes = GraphNodes(sg).ToList();
        if (nodes.Count == 0) errors.Add("Scene graph is empty (no nodes).");

        // Node id map + duplicates.
        var idToType = new Dictionary<long, string>();
        var dupes = new HashSet<long>();
        var deletionMarkers = new HashSet<long>();
        foreach (var n in nodes)
        {
            var id = NodeId(n);
            if (!idToType.TryAdd(id, Type(n))) dupes.Add(id);
            if (Type(n) == "scnDeletionMarkerNode") deletionMarkers.Add(id);
        }
        foreach (var d in dupes) errors.Add($"Duplicate node id {d}.");

        // Start/end declarations resolve, and concrete start/end node types exist.
        var startIds = IdArray(sg?["startNodes"]);
        var endIds = IdArray(sg?["endNodes"]);
        if (startIds.Count == 0) errors.Add("No startNodes declared.");
        if (endIds.Count == 0) warnings.Add("No endNodes declared.");
        foreach (var s in startIds.Where(s => !idToType.ContainsKey(s)))
            errors.Add($"startNodes id {s} does not resolve to a node.");
        foreach (var e in endIds.Where(e => !idToType.ContainsKey(e)))
            errors.Add($"endNodes id {e} does not resolve to a node.");
        if (!idToType.Values.Contains("scnStartNode")) warnings.Add("No scnStartNode in the graph.");
        if (!idToType.Values.Contains("scnEndNode")) warnings.Add("No scnEndNode in the graph.");

        // Sockets: destinations resolve; flag dangling (whitelisting the Cut Control backup
        // socket) and live edges into deletion markers.
        var adjacency = new Dictionary<long, List<long>>();
        foreach (var n in nodes)
        {
            var from = NodeId(n);
            var type = Type(n);
            adjacency[from] = new List<long>();
            foreach (var so in (n["outputSockets"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var stampName = TryLong(so["stamp"]?["name"], out var sn) ? sn : -1;
                var dests = (so["destinations"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
                if (dests.Count == 0)
                {
                    var isBackup = stampName == CutControlBackupSocketName;
                    if (!isBackup && type is not ("scnEndNode" or "scnDeletionMarkerNode"))
                        warnings.Add($"Node {from} ({type}): output socket with no destinations (dangling).");
                    continue;
                }
                foreach (var dest in dests)
                {
                    if (!TryLong(dest["nodeId"]?["id"], out var to)) continue;
                    if (!idToType.ContainsKey(to))
                        errors.Add($"Node {from} ({type}): socket points to missing node id {to}.");
                    else
                    {
                        adjacency[from].Add(to);
                        if (deletionMarkers.Contains(to))
                            warnings.Add($"Node {from} ({type}): live edge into deletion-marker node {to}.");
                    }
                }
            }
        }

        // Reachability from start nodes → orphans.
        if (startIds.Count > 0 && nodes.Count > 0)
        {
            var seen = new HashSet<long>();
            var queue = new Queue<long>(startIds.Where(idToType.ContainsKey));
            foreach (var s in queue) seen.Add(s);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var nx in adjacency.GetValueOrDefault(cur) ?? new())
                    if (seen.Add(nx)) queue.Enqueue(nx);
            }
            var orphans = idToType.Keys.Where(id => !seen.Contains(id) && idToType[id] != "scnDeletionMarkerNode").ToList();
            if (orphans.Count > 0)
                warnings.Add($"{orphans.Count} node(s) not reachable from a start node (e.g. {string.Join(", ", orphans.Take(8))}).");
        }

        // Actors.
        var actorIds = new HashSet<long>();
        foreach (var arr in new[] { "actors", "playerActors" })
            foreach (var a in (rc[arr] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                if (TryLong(a["actorId"]?["id"], out var aid)) actorIds.Add(aid);

        // Screenplay item ids (for dialogue ref checks).
        var lineItemIds = ScreenplayItemIds(rc, "lines");
        var optionItemIds = ScreenplayItemIds(rc, "options");

        // Dialogue events in section nodes → screenplay lines.
        foreach (var n in nodes.Where(n => Type(n) is "scnSectionNode" or "scnRewindableSectionNode"))
            foreach (var ev in (n["events"] as JsonArray) ?? new JsonArray())
            {
                var e = Unwrap(ev);
                if (e is null || Type(e) != "scnDialogLineEvent") continue;
                if (TryLong(e["screenplayLineId"]?["id"], out var lid) && lid != NoneId && !lineItemIds.Contains(lid))
                    errors.Add($"Node {NodeId(n)}: dialogLineEvent references missing screenplay line {lid}.");
                if (TryLong(e["speaker"]?["id"] ?? e["speakerOverride"]?["id"], out var sp) && sp != NoneId && !actorIds.Contains(sp))
                    warnings.Add($"Node {NodeId(n)}: dialogue speaker actor {sp} not in actors/playerActors.");
            }

        // Choice nodes → screenplay options + option/socket count.
        foreach (var n in nodes.Where(n => Type(n) == "scnChoiceNode"))
        {
            var opts = (n["options"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
            foreach (var o in opts)
                if (TryLong(o["screenplayOptionId"]?["id"], out var oid) && oid != NoneId && !optionItemIds.Contains(oid))
                    errors.Add($"Node {NodeId(n)} (choice): option references missing screenplay option {oid}.");
            var liveSockets = (n["outputSockets"] as JsonArray)?.OfType<JsonObject>()
                .Count(so => (TryLong(so["stamp"]?["name"], out var sn) ? sn : -1) != CutControlBackupSocketName) ?? 0;
            if (opts.Count > 0 && liveSockets > 0 && opts.Count != liveSockets)
                warnings.Add($"Node {NodeId(n)} (choice): {opts.Count} option(s) but {liveSockets} live socket(s).");
        }

        // Speaker + locstring resolution for screenplay lines/options.
        var locIndex = BuildLocIndex(rc, null);
        var locStorePresent = (rc["locStore"]?["vpEntries"] as JsonArray)?.Count > 0;
        void CheckLoc(string arrName)
        {
            foreach (var it in (rc["screenplayStore"]?[arrName] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                if (TryLong(it["speaker"]?["id"], out var sp) && sp != NoneId && !actorIds.Contains(sp))
                    warnings.Add($"Screenplay {arrName}: speaker actor {sp} not in actors/playerActors.");
                var ruid = it["locstringId"]?["ruid"]?.GetValue<string>();
                if (locStorePresent && ruid is not null && (!locIndex.TryGetValue(ruid, out var t) || string.IsNullOrEmpty(t)))
                    warnings.Add($"Screenplay {arrName}: locstring {ruid} has no embedded text.");
            }
        }
        CheckLoc("lines");
        CheckLoc("options");

        if (rc["version"] is null) warnings.Add("Missing 'version'.");

        var summary = new
        {
            nodes = nodes.Count,
            actors = actorIds.Count,
            screenplayLines = lineItemIds.Count,
            screenplayOptions = optionItemIds.Count,
            startNodes = startIds.Count,
            endNodes = endIds.Count,
            deletionMarkers = deletionMarkers.Count,
            locStoreEmbedded = locStorePresent,
        };
        return (errors, warnings, summary);
    }

    // ── extract_scene_localization ────────────────────────────────────────────

    [McpServerTool(Name = "extract_scene_localization", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Extracts a scene's dialogue (screenplay lines + choice options) to a " +
                 "translations JSON: { \"<ruid>\": { text, speaker, kind, usage } }. Text comes " +
                 "from the embedded loc store (a scene with no embedded loc store yields entries " +
                 "with text=null — its text lives in the game's external localization). Edit the " +
                 "JSON then feed it to apply_scene_localization. Accepts a .scene or its .json.")]
    public static async Task<string> ExtractSceneLocalization(
        Cp77ToolsRunner runner,
        [Description("Path to a .scene file or its converted .json.")] string sceneOrJson,
        [Description("Output translations JSON path.")] string outputJson,
        [Description("Optional locale id filter (e.g. en-us); default: all locales.")] string? locale = null,
        CancellationToken ct = default)
    {
        var (root, error) = await SceneToJsonAsync(runner, sceneOrJson, ct);
        if (root is null) return Err(error!);
        var rc = RootChunk(root);
        if (rc is null || Type(rc) != "scnSceneResource")
            return Err("Root is not a scnSceneResource.");

        var (entries, withText) = ExtractDialogue(rc, locale);
        var obj = new JsonObject();
        foreach (var (ruid, e) in entries) obj[ruid] = JsonSerializer.SerializeToNode(e, JsonOpts);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputJson)) ?? ".");
        File.WriteAllText(outputJson, obj.ToJsonString(JsonOpts));

        var warnings = new List<string>();
        if (entries.Count > 0 && withText == 0)
            warnings.Add("No embedded text found — this scene's dialogue is localized externally; " +
                         "apply_scene_localization can only write text that is embedded in the scene.");
        return Result($"Scene localization extracted → {outputJson} ({entries.Count} string(s), {withText} with text).",
            Array.Empty<string>(), warnings,
            new { produced = new[] { outputJson }, count = entries.Count, withText });
    }

    /// <summary>Returns (ruid → {text, speaker, kind, usage}) for screenplay lines + options.</summary>
    internal static (Dictionary<string, object> entries, int withText) ExtractDialogue(JsonObject rc, string? locale)
    {
        var locIndex = BuildLocIndex(rc, locale);
        var actorNames = ActorNames(rc);
        var entries = new Dictionary<string, object>(StringComparer.Ordinal);
        var withText = 0;

        void Scan(string arrName, string kind, bool hasSpeaker)
        {
            foreach (var it in (rc["screenplayStore"]?[arrName] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var ruid = it["locstringId"]?["ruid"]?.GetValue<string>();
                if (ruid is null) continue;
                var text = locIndex.TryGetValue(ruid, out var t) ? t : null;
                if (!string.IsNullOrEmpty(text)) withText++;
                string? speaker = null;
                if (hasSpeaker && TryLong(it["speaker"]?["id"], out var sp))
                    speaker = actorNames.TryGetValue(sp, out var nm) ? nm : sp.ToString();
                entries[ruid] = new { text, speaker, kind };
            }
        }
        Scan("lines", "line", hasSpeaker: true);
        Scan("options", "choice", hasSpeaker: false);
        return (entries, withText);
    }

    // ── apply_scene_localization ──────────────────────────────────────────────

    [McpServerTool(Name = "apply_scene_localization", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Writes an edited translations JSON (from extract_scene_localization) back into " +
                 "the scene's embedded loc store and re-serializes to a .scene. Each key is a " +
                 "ruid; its value is the new text (a string, or an object with a 'text' field). " +
                 "Performs a control round-trip (re-reads the output) and warns on mismatch — " +
                 "WolvenKit can mis-serialize some scenes. Only works on scenes that embed their " +
                 "text. Input must be a .scene (not a .json).")]
    public static async Task<string> ApplySceneLocalization(
        Cp77ToolsRunner runner,
        [Description("Path to the source .scene file.")] string sceneFile,
        [Description("Path to the edited translations JSON.")] string translationsJson,
        [Description("Output .scene path to write.")] string outputScene,
        [Description("Optional locale id filter (e.g. en-us); default: all matching locales.")] string? locale = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(sceneFile)) return Err($".scene not found: {sceneFile}");
        if (!File.Exists(translationsJson)) return Err($"Translations JSON not found: {translationsJson}");

        var (root, error) = await SceneToJsonAsync(runner, sceneFile, ct);
        if (root is null) return Err(error!);
        var rc = RootChunk(root);
        if (rc is null || Type(rc) != "scnSceneResource")
            return Err("Source is not a scnSceneResource.");

        JsonObject? translations;
        try { translations = JsonNode.Parse(File.ReadAllText(translationsJson)) as JsonObject; }
        catch (Exception e) { return Err($"Invalid translations JSON: {e.Message}"); }
        if (translations is null) return Err("Translations JSON must be an object keyed by ruid.");

        var trMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (ruid, val) in translations)
        {
            var text = val switch
            {
                JsonObject o => o["text"]?.GetValue<string>(),
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => null,
            };
            if (text is not null) trMap[ruid] = text;
        }

        var (applied, warnings) = ApplyLocalization(rc, trMap, locale);
        if (applied == 0)
            return Result("apply_scene_localization: nothing applied (no matching embedded loc entries). " +
                          "This scene's text is likely localized externally; nothing written.",
                Array.Empty<string>(),
                warnings.Append("No output written.").ToList());

        var (ok, log) = await DeserializeSceneAsync(runner, (JsonObject)root, outputScene, ct);
        if (!ok) return Err($"Re-serialize to .scene failed — {Trunc(log)}");

        // Control round-trip: re-read the produced scene and confirm the edits stuck.
        var (verifyRoot, _) = await SceneToJsonAsync(runner, outputScene, ct);
        if (verifyRoot is null)
            warnings.Add("Output written but could not be re-read for verification.");
        else
        {
            var (entries, _) = ExtractDialogue(RootChunk(verifyRoot)!, locale);
            var mismatches = trMap.Count(kv => !(entries.TryGetValue(kv.Key, out var e)
                && (e.GetType().GetProperty("text")?.GetValue(e) as string) == kv.Value));
            if (mismatches > 0)
                warnings.Add($"{mismatches} translated string(s) did not survive the round-trip — " +
                             "WolvenKit may have re-serialized this scene incorrectly; verify in-game/GUI.");
        }

        return Result($"Applied {applied} translation(s) → {outputScene}.",
            Array.Empty<string>(), warnings, new { produced = new[] { outputScene }, applied });
    }

    /// <summary>Writes each ruid's text into the matching loc-store payloads. Mutates
    /// <paramref name="rc"/> in place. Returns (appliedCount, warnings).</summary>
    internal static (int applied, List<string> warnings) ApplyLocalization(
        JsonObject rc, IReadOnlyDictionary<string, string> translations, string? locale)
    {
        var warnings = new List<string>();
        var vd = rc["locStore"]?["vdEntries"] as JsonArray;
        var vp = rc["locStore"]?["vpEntries"] as JsonArray;
        if (vd is null || vp is null || vp.Count == 0)
        {
            warnings.Add("Scene has no embedded loc store (vpEntries empty); text is localized externally.");
            return (0, warnings);
        }

        var applied = 0;
        foreach (var d in vd.OfType<JsonObject>())
        {
            var ruid = d["locstringId"]?["ruid"]?.GetValue<string>();
            if (ruid is null || !translations.TryGetValue(ruid, out var text)) continue;
            if (locale is not null && d["localeId"]?.GetValue<string>() is { } loc &&
                !string.Equals(loc, locale, StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryLong(d["vpeIndex"], out var idx) || idx < 0 || idx >= vp.Count) continue;
            if (vp[(int)idx] is JsonObject payload) { payload["content"] = text; applied++; }
        }
        return (applied, warnings);
    }

    // ── scene_dependencies ────────────────────────────────────────────────────

    [McpServerTool(Name = "scene_dependencies", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lists a scene's EXTERNAL dependencies — every resource it references (animation " +
                 ".anims via resouresReferences, rid resources, prop .ent, plus actor TweakDB " +
                 "character records) — which validate_scene does NOT check. A scene can be " +
                 "internally valid yet break in-game if one is missing. Optionally resolve against " +
                 "a mod folder (modRoot): paths the mod ships are 'inMod', base-game-prefixed paths " +
                 "(base\\, ep1\\, dlc\\) are assumed present, the rest are flagged unresolved. " +
                 "Accepts a .scene or its .json.")]
    public static async Task<string> SceneDependencies(
        Cp77ToolsRunner runner,
        [Description("Path to a .scene file or its converted .json.")] string sceneOrJson,
        [Description("Optional mod root folder; depot paths existing under it are marked resolved.")] string? modRoot = null,
        CancellationToken ct = default)
    {
        var (root, error) = await SceneToJsonAsync(runner, sceneOrJson, ct);
        if (root is null) return Err(error!);
        var rc = RootChunk(root);
        if (rc is null || Type(rc) != "scnSceneResource") return Err("Root is not a scnSceneResource.");

        var deps = ExtractDependencies(rc);
        var byKind = deps.GroupBy(d => d.kind).ToDictionary(g => g.Key, g => g.Count());

        var warnings = new List<string>();
        var resolved = new List<object>();
        var unresolved = new List<string>();
        foreach (var d in deps)
        {
            if (d.kind == "tweakRecord") { resolved.Add(new { d.kind, d.value, where = "tweakdb" }); continue; }
            string where;
            if (modRoot is not null && File.Exists(Path.Combine(modRoot, d.value.Replace('\\', Path.DirectorySeparatorChar))))
                where = "inMod";
            else if (LooksBaseGame(d.value)) where = "assumedBaseGame";
            else { where = "unresolved"; unresolved.Add(d.value); }
            resolved.Add(new { d.kind, d.value, where });
        }
        if (unresolved.Count > 0)
            warnings.Add($"{unresolved.Count} reference(s) not found in the mod and not base-game-prefixed — " +
                         "include them in the mod or verify the paths.");

        return Result($"Scene dependencies: {Path.GetFileName(sceneOrJson)} — {deps.Count} reference(s)",
            Array.Empty<string>(), warnings,
            new { total = deps.Count, byKind, unresolved, dependencies = resolved });
    }

    /// <summary>Collects every external reference: all ResourcePath depot-path $values (anims,
    /// .ent, etc.) plus actor TweakDB character records. Categorized by file extension.</summary>
    internal static List<(string kind, string value)> ExtractDependencies(JsonObject rc)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectResourcePaths(rc, paths);
        var deps = new List<(string, string)>();
        foreach (var p in paths.OrderBy(p => p, StringComparer.Ordinal))
            deps.Add((KindOf(p), p));

        // Actor TweakDB character records (entity comes from the record).
        foreach (var arr in new[] { "actors", "playerActors" })
            foreach (var a in (rc[arr] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var rec = a["specCharacterRecordId"]?["$value"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(rec) && rec != "0")
                    deps.Add(("tweakRecord", rec));
            }
        return deps;
    }

    private static void CollectResourcePaths(JsonNode? node, HashSet<string> into)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["$type"]?.GetValue<string>() == "ResourcePath" &&
                    o["$value"]?.GetValue<string>() is { Length: > 0 } v && v != "0")
                    into.Add(v);
                foreach (var (_, child) in o) CollectResourcePaths(child, into);
                break;
            case JsonArray a:
                foreach (var child in a) CollectResourcePaths(child, into);
                break;
        }
    }

    private static string KindOf(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "anims" => "anims",
            "ent" => "entity",
            "app" => "appearance",
            "mesh" => "mesh",
            "wem" or "opusinfo" or "opuspak" => "audio",
            "" => "resource",
            _ => ext,
        };
    }

    private static bool LooksBaseGame(string path)
    {
        var p = path.Replace('/', '\\');
        return p.StartsWith("base\\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("ep1\\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("dlc\\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("engine\\", StringComparison.OrdinalIgnoreCase);
    }

    // ── scene_events ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "scene_events", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lists what plays in a scene: per section node, the timeline events (dialogue, " +
                 "animation, audio, camera, VFX) with startTime/duration and a detail (resolved " +
                 "dialogue text for dialog lines, anim name for anim events). Complements " +
                 "inspect_scene. Bounded by maxEvents. Accepts a .scene or its .json.")]
    public static async Task<string> SceneEvents(
        Cp77ToolsRunner runner,
        [Description("Path to a .scene file or its converted .json.")] string sceneOrJson,
        [Description("Max events to return (default 600).")] int maxEvents = 600,
        CancellationToken ct = default)
    {
        var (root, error) = await SceneToJsonAsync(runner, sceneOrJson, ct);
        if (root is null) return Err(error!);
        var rc = RootChunk(root);
        if (rc is null || Type(rc) != "scnSceneResource") return Err("Root is not a scnSceneResource.");
        var data = BuildEvents(rc, maxEvents);
        return Result($"Scene events: {Path.GetFileName(sceneOrJson)}",
            Array.Empty<string>(), Array.Empty<string>(), data);
    }

    internal static object BuildEvents(JsonObject rc, int maxEvents = 600)
    {
        var locIndex = BuildLocIndex(rc, null);
        var actorNames = ActorNames(rc);
        // screenplay line id -> (ruid, speakerId)
        var lineInfo = new Dictionary<long, (string? ruid, long speaker)>();
        foreach (var it in (rc["screenplayStore"]?["lines"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            if (TryLong(it["itemId"]?["id"], out var iid))
                lineInfo[iid] = (it["locstringId"]?["ruid"]?.GetValue<string>(),
                                 TryLong(it["speaker"]?["id"], out var sp) ? sp : -1);

        var sg = SceneGraph(rc);
        var sections = new List<object>();
        var total = 0;
        var truncated = false;
        foreach (var n in GraphNodes(sg).Where(n => Type(n) is "scnSectionNode" or "scnRewindableSectionNode"))
        {
            var evList = new List<object>();
            foreach (var ev in (n["events"] as JsonArray) ?? new JsonArray())
            {
                var e = Unwrap(ev);
                if (e is null) continue;
                if (total >= maxEvents) { truncated = true; break; }
                total++;
                var et = Type(e);
                string? detail = null;
                if (et == "scnDialogLineEvent" && TryLong(e["screenplayLineId"]?["id"], out var lid) && lineInfo.TryGetValue(lid, out var li))
                {
                    var text = li.ruid is not null && locIndex.TryGetValue(li.ruid, out var t) ? t : null;
                    var spk = actorNames.TryGetValue(li.speaker, out var nm) ? nm : null;
                    detail = spk is not null ? $"{spk}: {text ?? "(external loc)"}" : text;
                }
                else if (et is "scnPlaySkAnimEvent" or "scnPlayAnimEvent" or "scnPlayRidAnimEvent")
                    detail = e["gameplayAnimName"]?["$value"]?.GetValue<string>() is { Length: > 0 } an && an != "None" ? an : "(anim)";
                evList.Add(new
                {
                    type = et,
                    startTime = TryLong(e["startTime"], out var st) ? st : 0,
                    duration = TryLong(e["duration"], out var du) ? du : 0,
                    detail,
                });
            }
            sections.Add(new { nodeId = NodeId(n), type = Type(n), events = evList });
            if (truncated) break;
        }
        return new { sections, totalEvents = total, truncated };
    }

    // ── scene_set_actor ───────────────────────────────────────────────────────

    [McpServerTool(Name = "scene_set_actor", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Retargets a scene actor: sets its specCharacterRecordId (TweakDBID) and/or " +
                 "specAppearance (CName) by actorId, then re-serializes to a .scene with a control " +
                 "round-trip. Find actorIds with inspect_scene. Input must be a .scene.")]
    public static async Task<string> SceneSetActor(
        Cp77ToolsRunner runner,
        [Description("Path to the source .scene file.")] string sceneFile,
        [Description("Output .scene path to write.")] string outputScene,
        [Description("Actor id to retarget (the scnActorId 'id').")] long actorId,
        [Description("New character record TweakDBID (e.g. Character.Judy); omit to leave unchanged.")] string? recordId = null,
        [Description("New appearance name (CName); omit to leave unchanged.")] string? appearance = null,
        CancellationToken ct = default)
    {
        if (recordId is null && appearance is null) return Err("Provide recordId and/or appearance.");
        if (recordId is not null && !recordId.Contains('.') && !recordId.All(char.IsDigit))
            return Err("recordId should look like a TweakDBID (e.g. Character.Judy) or a numeric hash.");
        if (!File.Exists(sceneFile)) return Err($".scene not found: {sceneFile}");
        var (root, error) = await SceneToJsonAsync(runner, sceneFile, ct);
        if (root is null) return Err(error!);
        var rc = RootChunk(root);
        if (rc is null || Type(rc) != "scnSceneResource") return Err("Source is not a scnSceneResource.");

        if (!SetActor(rc, actorId, recordId, appearance))
            return Err($"Actor id {actorId} not found in actors/playerActors.");

        var (ok, log) = await DeserializeSceneAsync(runner, (JsonObject)root, outputScene, ct);
        if (!ok) return Err($"Re-serialize to .scene failed — {Trunc(log)}");

        var warnings = new List<string>();
        var (verify, _) = await SceneToJsonAsync(runner, outputScene, ct);
        if (verify is null) warnings.Add("Output written but could not be re-read for verification.");
        return Result($"Actor {actorId} updated → {outputScene}.", Array.Empty<string>(), warnings,
            new { produced = new[] { outputScene }, actorId, recordId, appearance });
    }

    /// <summary>Sets an actor's specCharacterRecordId/specAppearance by id. Returns false if not found.</summary>
    internal static bool SetActor(JsonObject rc, long actorId, string? recordId, string? appearance)
    {
        foreach (var arr in new[] { "actors", "playerActors" })
            foreach (var a in (rc[arr] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                if (!TryLong(a["actorId"]?["id"], out var id) || id != actorId) continue;
                if (recordId is not null && a["specCharacterRecordId"] is JsonObject rec)
                {
                    rec["$value"] = recordId;
                    rec["$storage"] = recordId.All(char.IsDigit) ? "uint64" : "string";
                }
                if (appearance is not null && a["specAppearance"] is JsonObject app)
                    app["$value"] = appearance;
                return true;
            }
        return false;
    }

    // ── scene_replace_resource ──────────────────────────────────────────────────

    [McpServerTool(Name = "scene_replace_resource", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Replaces a depot path everywhere it appears in a scene (e.g. swap an old .anims " +
                 "for a new one) across resouresReferences, ridResources and any DepotPath, then " +
                 "re-serializes to a .scene with a control round-trip. Use scene_dependencies to " +
                 "find the paths. Input must be a .scene.")]
    public static async Task<string> SceneReplaceResource(
        Cp77ToolsRunner runner,
        [Description("Path to the source .scene file.")] string sceneFile,
        [Description("Output .scene path to write.")] string outputScene,
        [Description("Existing depot path to replace (exact match).")] string oldPath,
        [Description("New depot path.")] string newPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) return Err("oldPath and newPath are required.");
        if (!File.Exists(sceneFile)) return Err($".scene not found: {sceneFile}");
        var (root, error) = await SceneToJsonAsync(runner, sceneFile, ct);
        if (root is null) return Err(error!);
        var rc = RootChunk(root);
        if (rc is null || Type(rc) != "scnSceneResource") return Err("Source is not a scnSceneResource.");

        var count = ReplaceResource(rc, oldPath, newPath);
        if (count == 0) return Err($"Path not found in the scene: {oldPath}");

        var (ok, log) = await DeserializeSceneAsync(runner, (JsonObject)root, outputScene, ct);
        if (!ok) return Err($"Re-serialize to .scene failed — {Trunc(log)}");
        return Result($"Replaced {count} occurrence(s) of the resource → {outputScene}.",
            Array.Empty<string>(), Array.Empty<string>(),
            new { produced = new[] { outputScene }, replaced = count, oldPath, newPath });
    }

    /// <summary>Replaces every ResourcePath $value equal to oldPath. Returns the replacement count.</summary>
    internal static int ReplaceResource(JsonNode? node, string oldPath, string newPath)
    {
        var count = 0;
        switch (node)
        {
            case JsonObject o:
                if (o["$type"]?.GetValue<string>() == "ResourcePath" &&
                    string.Equals(o["$value"]?.GetValue<string>(), oldPath, StringComparison.OrdinalIgnoreCase))
                { o["$value"] = newPath; count++; }
                foreach (var (_, child) in o) count += ReplaceResource(child, oldPath, newPath);
                break;
            case JsonArray a:
                foreach (var child in a) count += ReplaceResource(child, oldPath, newPath);
                break;
        }
        return count;
    }

    // ── scaffold_scene ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "scaffold_scene", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Generates a minimal valid scnSceneResource JSON skeleton: a start node → N " +
                 "section nodes (chained) → an end node, with auto-generated node ids and wired " +
                 "sockets, and empty actor/screenplay/loc stores. The result passes validate_scene " +
                 "and converts to a .scene via json_to_cr2w (then open it in WolvenKit's scene " +
                 "editor to flesh it out). This is a skeleton generator, not a full authoring suite.")]
    public static string ScaffoldScene(
        [Description("Output JSON path (feed to json_to_cr2w to get a .scene).")] string outputJson,
        [Description("Number of section nodes between start and end (default 1).")] int sectionCount = 1,
        [Description("Optional scene name (informational comment).")] string? name = null)
    {
        if (sectionCount < 0) return Err("sectionCount must be >= 0.");
        var root = BuildSkeleton(sectionCount);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputJson)) ?? ".");
        File.WriteAllText(outputJson, root.ToJsonString(JsonOpts));
        // Self-check: the skeleton must validate clean.
        var (errors, _, _) = ValidateScene(root);
        return Result($"Scene skeleton ({sectionCount} section(s)) → {outputJson}. Next: json_to_cr2w.",
            errors, Array.Empty<string>(), new { produced = new[] { outputJson }, sectionCount, name });
    }

    /// <summary>Builds a minimal scnSceneResource: start(1) → section(2..) → end(last).</summary>
    internal static JsonObject BuildSkeleton(int sectionCount)
    {
        JsonObject Id(long id) => new() { ["$type"] = "scnNodeId", ["id"] = id };
        JsonObject Socket(long toId) => new()
        {
            ["$type"] = "scnOutputSocket",
            ["stamp"] = new JsonObject { ["$type"] = "scnOutputSocketStamp", ["name"] = 0, ["ordinal"] = 0 },
            ["destinations"] = new JsonArray(new JsonObject
            {
                ["$type"] = "scnInputSocketId",
                ["isockStamp"] = new JsonObject { ["$type"] = "scnInputSocketStamp", ["name"] = 0, ["ordinal"] = 0 },
                ["nodeId"] = Id(toId),
            }),
        };
        JsonObject Handle(string handleId, JsonObject data) => new() { ["HandleId"] = handleId, ["Data"] = data };

        // HandleIds are unique sequential integers (sceneGraph="0", then nodes "1"..).
        var graph = new JsonArray();
        long startId = 1, endId = 2 + sectionCount;
        graph.Add(Handle("1", new JsonObject
        {
            ["$type"] = "scnStartNode", ["ffStrategy"] = "automatic", ["nodeId"] = Id(startId),
            ["outputSockets"] = new JsonArray(Socket(sectionCount > 0 ? 2 : endId)),
        }));
        for (var i = 0; i < sectionCount; i++)
        {
            long nid = 2 + i;
            long next = i + 1 < sectionCount ? nid + 1 : endId;
            graph.Add(Handle((nid).ToString(), new JsonObject
            {
                ["$type"] = "scnSectionNode", ["ffStrategy"] = "automatic", ["nodeId"] = Id(nid),
                ["events"] = new JsonArray(),
                ["outputSockets"] = new JsonArray(Socket(next)),
            }));
        }
        graph.Add(Handle(endId.ToString(), new JsonObject
        {
            ["$type"] = "scnEndNode", ["nodeId"] = Id(endId), ["outputSockets"] = new JsonArray(),
        }));

        var rootChunk = new JsonObject
        {
            ["$type"] = "scnSceneResource",
            ["version"] = 195,
            ["actors"] = new JsonArray(),
            ["playerActors"] = new JsonArray(),
            ["screenplayStore"] = new JsonObject { ["$type"] = "scnscreenplayStore", ["lines"] = new JsonArray(), ["options"] = new JsonArray() },
            ["locStore"] = new JsonObject { ["$type"] = "scnlocLocStoreEmbedded", ["vdEntries"] = new JsonArray(), ["vpEntries"] = new JsonArray() },
            ["entryPoints"] = new JsonArray(),
            ["exitPoints"] = new JsonArray(),
            ["sceneGraph"] = Handle("0", new JsonObject
            {
                ["$type"] = "scnSceneGraph",
                ["graph"] = graph,
                ["startNodes"] = new JsonArray(Id(startId)),
                ["endNodes"] = new JsonArray(Id(endId)),
            }),
        };
        return new JsonObject
        {
            ["Header"] = new JsonObject { ["WKitJsonVersion"] = "0.0.9", ["GameVersion"] = 2310, ["DataType"] = "CR2W" },
            ["Data"] = new JsonObject { ["Version"] = 195, ["BuildVersion"] = 0, ["RootChunk"] = rootChunk },
        };
    }

    // ── conversion helpers ─────────────────────────────────────────────────────

    /// <summary>Loads a .scene (via daemon convert serialize) or a .json (as-is) into a JsonNode.</summary>
    private static async Task<(JsonNode? root, string? error)> SceneToJsonAsync(
        Cp77ToolsRunner runner, string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return (null, $"File not found: {path}");
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try { return (JsonNode.Parse(await File.ReadAllTextAsync(path, ct)), null); }
            catch (Exception e) { return (null, $"Invalid JSON: {e.Message}"); }
        }
        var tmp = Path.Combine(Path.GetTempPath(), "wkmcp-scene", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            await runner.RunAsync(new[] { "convert", "serialize", path, "--outpath", tmp }, ct);
            var json = Directory.EnumerateFiles(tmp, "*.json", SearchOption.AllDirectories).FirstOrDefault();
            if (json is null) return (null, $"Could not convert {Path.GetFileName(path)} to JSON.");
            return (JsonNode.Parse(await File.ReadAllTextAsync(json, ct)), null);
        }
        catch (Exception e) { return (null, $"Conversion failed: {e.Message}"); }
        finally { try { Directory.Delete(tmp, true); } catch { /* best-effort */ } }
    }

    /// <summary>Re-serializes an edited scene JSON to a .scene binary (convert deserialize).
    /// `convert` returns 0 even on per-file errors, so success = a non-JSON file was produced.</summary>
    private static async Task<(bool ok, string log)> DeserializeSceneAsync(
        Cp77ToolsRunner runner, JsonObject sceneJson, string dest, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wkmcp-scene-out", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var name = Path.GetFileNameWithoutExtension(dest);
            if (!name.EndsWith(".scene", StringComparison.OrdinalIgnoreCase)) name += ".scene";
            var jsonPath = Path.Combine(tmp, name + ".json");
            await File.WriteAllTextAsync(jsonPath, sceneJson.ToJsonString(JsonOpts), ct);
            var outTmp = Path.Combine(tmp, "out");
            Directory.CreateDirectory(outTmp);
            var r = await runner.RunAsync(new[] { "convert", "deserialize", jsonPath, "--outpath", outTmp }, ct);
            var bin = Directory.EnumerateFiles(outTmp, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            if (bin is null) return (false, (r.Stdout + r.Stderr).Trim());
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(bin, dest);
            return (true, "");
        }
        finally { try { Directory.Delete(tmp, true); } catch { /* best-effort */ } }
    }

    // ── pure JSON helpers ──────────────────────────────────────────────────────

    private static JsonObject? RootChunk(JsonNode? root) => root?["Data"]?["RootChunk"] as JsonObject;

    private static string Type(JsonObject? o) => o?["$type"]?.GetValue<string>() ?? "?";

    /// <summary>Unwraps a CHandle ({HandleId, Data:{…}}) to its inner object; passes through plain objects.</summary>
    private static JsonObject? Unwrap(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        if (o.ContainsKey("HandleId") && o["Data"] is JsonObject d) return d;
        return o;
    }

    private static JsonObject? SceneGraph(JsonObject rc) => Unwrap(rc["sceneGraph"]);

    private static IEnumerable<JsonObject> GraphNodes(JsonObject? sg)
        => (sg?["graph"] as JsonArray)?.Select(Unwrap).OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>();

    private static long NodeId(JsonObject node) => TryLong(node["nodeId"]?["id"], out var id) ? id : -1;

    private static List<long> IdArray(JsonNode? arr)
    {
        var list = new List<long>();
        foreach (var e in (arr as JsonArray) ?? new JsonArray())
            if (TryLong((e as JsonObject)?["id"], out var id)) list.Add(id);
        return list;
    }

    private static List<object> NamedPoints(JsonNode? arr)
        => ((arr as JsonArray) ?? new JsonArray()).OfType<JsonObject>()
            .Select(p => (object)new
            {
                name = p["name"]?["$value"]?.GetValue<string>() ?? p["name"]?.GetValue<string>(),
                nodeId = TryLong(p["nodeId"]?["id"], out var id) ? id : (long?)null,
            }).ToList();

    private static HashSet<long> ScreenplayItemIds(JsonObject rc, string arrName)
    {
        var ids = new HashSet<long>();
        foreach (var it in (rc["screenplayStore"]?[arrName] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            if (TryLong(it["itemId"]?["id"], out var id)) ids.Add(id);
        return ids;
    }

    private static Dictionary<long, string> ActorNames(JsonObject rc)
    {
        var map = new Dictionary<long, string>();
        foreach (var a in (rc["actors"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            if (TryLong(a["actorId"]?["id"], out var id))
                map[id] = a["actorName"]?.GetValue<string>() ?? $"actor{id}";
        foreach (var a in (rc["playerActors"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            if (TryLong(a["actorId"]?["id"], out var id))
                map[id] = a["playerName"]?.GetValue<string>() ?? "Player";
        return map;
    }

    /// <summary>ruid → embedded subtitle text, from locStore (optionally filtered by locale).</summary>
    private static Dictionary<string, string> BuildLocIndex(JsonObject rc, string? locale)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var vd = rc["locStore"]?["vdEntries"] as JsonArray;
        var vp = rc["locStore"]?["vpEntries"] as JsonArray;
        if (vd is null || vp is null) return index;
        foreach (var d in vd.OfType<JsonObject>())
        {
            var ruid = d["locstringId"]?["ruid"]?.GetValue<string>();
            if (ruid is null) continue;
            if (locale is not null && d["localeId"]?.GetValue<string>() is { } loc &&
                !string.Equals(loc, locale, StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryLong(d["vpeIndex"], out var idx) || idx < 0 || idx >= vp.Count) continue;
            if (vp[(int)idx] is JsonObject payload && payload["content"]?.GetValue<string>() is { } content)
                index[ruid] = content;
        }
        return index;
    }

    /// <summary>Reads an integer from a JSON node backed by JsonElement (file) or a CLR
    /// number/string ("3414..." ruids and ids both appear).</summary>
    internal static bool TryLong(JsonNode? n, out long value)
    {
        value = 0;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<long>(out value)) return true;
        if (v.TryGetValue<int>(out var i)) { value = i; return true; }
        if (v.TryGetValue<double>(out var d) && !double.IsInfinity(d) && d == Math.Floor(d)) { value = (long)d; return true; }
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, out value)) return true;
        return false;
    }

    private static string Trunc(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
