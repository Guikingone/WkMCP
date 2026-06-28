using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace WkMcp;

/// <summary>
/// Gameplay-logic inspectors (third partial of <see cref="ModdingTools"/>): the two
/// file families that drive quest flow and world population and had no dedicated
/// tooling — quest phase graphs (<c>.questphase</c> / questQuestPhaseResource) and
/// communities (<c>.community</c> / communityCommunityTemplate). Like the other
/// inspectors they accept a binary CR2W (converted via the daemon) or a <c>.json</c>
/// already produced by <c>read_game_file</c>/<c>cr2w_to_json</c>.
///
/// The pure analysis cores (<see cref="SummarizeQuestPhase"/>, <see cref="SummarizeCommunity"/>)
/// operate on parsed JSON and are unit-tested. They were validated against real
/// Cyberpunk 2077 files (teddy_holocall / sq023_bd_studio questphases,
/// wbr_hil_rippdoc / sq017_caliente communities).
///
/// The questphase node→node edges are reconstructed from WolvenKit's CR2W handle
/// serialization: every socket carries a HandleId; a connection references its two
/// sockets by HandleId (inline first occurrence) or HandleRefId (back-reference).
/// We map socket-handle → owning node, then resolve each connection's source and
/// destination handles back to nodes.
/// </summary>
public static partial class ModdingTools
{
    /// <summary>The handle key of a CR2W handle entry: HandleRefId (back-ref) or HandleId
    /// (inline first occurrence). Both identify the same logical object.</summary>
    private static string? HandleKey(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("HandleRefId", out var r) && r.ValueKind == JsonValueKind.String) return r.GetString();
        if (e.TryGetProperty("HandleId", out var h) && h.ValueKind == JsonValueKind.String) return h.GetString();
        return null;
    }

    private static int? IntProp(JsonElement obj, string prop)
    {
        var n = NumProp(obj, prop);
        return n is null ? null : (int)n.Value;
    }

    // ════════════════════════════════════════════════════════════════════════
    // inspect_questphase — a .questphase (questQuestPhaseResource): node graph
    // ════════════════════════════════════════════════════════════════════════
    internal sealed record QuestNode(int Id, string Type, string? Detail);
    internal sealed record QuestEdge(int From, int To);
    internal sealed record QuestPhaseSummary(string? Type, List<QuestNode> Nodes, List<QuestEdge> Edges,
        Dictionary<string, int> NodeTypes, List<int> EntryNodes, List<int> ExitNodes,
        List<string> SceneRefs, List<string> PhaseRefs);

    [McpServerTool(Name = "inspect_questphase", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Summary of a quest phase graph (.questphase / questQuestPhaseResource): its nodes " +
                 "(with a per-type histogram), the node→node edges reconstructed from the socket " +
                 "connections, the entry/exit nodes (questInput/questOutput), and the .scene files and " +
                 "sub-phases it triggers. The map of a quest's flow — which scenes fire, in what order, " +
                 "and where it starts/ends — without hand-walking the handle graph. Pair with " +
                 "inspect_scene on the referenced .scene files. Accepts a .questphase or its .json.")]
    public static async Task<string> InspectQuestphase(
        Cp77ToolsRunner runner,
        [Description("A .questphase file or its converted .json.")] string questphaseOrJson,
        [Description("Max nodes listed (default 400). nodeCount always gives the real total.")] int maxNodes = 400,
        [Description("Max edges listed (default 600). edgeCount always gives the real total.")] int maxEdges = 600,
        CancellationToken ct = default)
    {
        var (json, err) = await LoadCr2wJsonText(runner, questphaseOrJson, ct);
        if (json is null) return Err(err!);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var s = SummarizeQuestPhase(doc.RootElement);
            var nCap = Math.Max(1, maxNodes);
            var eCap = Math.Max(1, maxEdges);
            var warnings = new List<string>();
            if (s.Type != "questQuestPhaseResource")
                warnings.Add($"Root is '{s.Type}', not questQuestPhaseResource — graph may be incomplete.");
            if (s.Nodes.Count == 0)
                warnings.Add("No node found — unexpected questphase shape or empty graph.");
            var typeOf = s.Nodes.ToDictionary(n => n.Id, n => n.Type);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = s.Nodes.Count > 0 && warnings.Count == 0 ? "success" : "partial",
                summary = $"{s.Type}: {s.Nodes.Count} node(s), {s.Edges.Count} edge(s), " +
                          $"{s.SceneRefs.Count} scene(s), {s.PhaseRefs.Count} sub-phase(s)",
                file = questphaseOrJson,
                rootType = s.Type,
                nodeCount = s.Nodes.Count,
                edgeCount = s.Edges.Count,
                nodeTypes = s.NodeTypes,
                entryNodes = s.EntryNodes,
                exitNodes = s.ExitNodes,
                sceneRefs = s.SceneRefs,
                phaseRefs = s.PhaseRefs,
                nodesTruncated = s.Nodes.Count > nCap,
                edgesTruncated = s.Edges.Count > eCap,
                nodes = s.Nodes.Take(nCap).Select(n => new { id = n.Id, type = n.Type, detail = n.Detail }),
                edges = s.Edges.Take(eCap).Select(e => new
                {
                    from = e.From,
                    to = e.To,
                    fromType = typeOf.GetValueOrDefault(e.From),
                    toType = typeOf.GetValueOrDefault(e.To),
                }),
                warnings,
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
    }

    /// <summary>(rootType, nodes, edges, type histogram, entry/exit node ids, scene refs, sub-phase
    /// refs) of a questQuestPhaseResource JSON. Edges are reconstructed by mapping each socket handle
    /// to its owning node, then resolving every connection's source/destination handles. Testable.</summary>
    internal static QuestPhaseSummary SummarizeQuestPhase(JsonElement root)
    {
        var rc = RootChunkOf(root);
        var type = TypeOf(rc);
        var nodes = new List<QuestNode>();
        var sceneRefs = new List<string>();
        var phaseRefs = new List<string>();
        var entryNodes = new List<int>();
        var exitNodes = new List<int>();
        var socketOwner = new Dictionary<string, int>(StringComparer.Ordinal);

        // graph is a CHandle → questGraphDefinition { nodes: [ … ] }.
        if (!rc.TryGetProperty("graph", out var graphRaw))
            return new QuestPhaseSummary(type, nodes, new List<QuestEdge>(),
                new Dictionary<string, int>(), entryNodes, exitNodes, sceneRefs, phaseRefs);
        var graph = UnwrapData(graphRaw);

        void AddRef(List<string> list, string? p)
        { if (!string.IsNullOrEmpty(p) && p != "0" && !list.Contains(p!)) list.Add(p!); }

        if (graph.TryGetProperty("nodes", out var ns) && ns.ValueKind == JsonValueKind.Array)
            foreach (var raw in ns.EnumerateArray())
            {
                var nd = UnwrapData(raw);
                var ntype = TypeOf(nd);
                var id = IntProp(nd, "id") ?? -1;

                // Map every socket this node owns back to the node id.
                if (nd.TryGetProperty("sockets", out var sockets) && sockets.ValueKind == JsonValueKind.Array)
                    foreach (var sk in sockets.EnumerateArray())
                        if (HandleKey(sk) is { } key) socketOwner[key] = id;

                string? detail = null;
                switch (ntype)
                {
                    case "questSceneNodeDefinition":
                        var scene = DepotPathVal(nd, "sceneFile");
                        AddRef(sceneRefs, scene);
                        detail = scene is null ? null : "scene: " + scene;
                        break;
                    case "questPhaseNodeDefinition":
                        var phase = DepotPathVal(nd, "phaseResource");
                        if (!string.IsNullOrEmpty(phase) && phase != "0") { AddRef(phaseRefs, phase); detail = "phase: " + phase; }
                        else detail = "phase: (embedded subgraph)";
                        break;
                    case "questInputNodeDefinition":
                        entryNodes.Add(id);
                        detail = "in: " + (CnameVal(nd, "socketName") ?? "?");
                        break;
                    case "questOutputNodeDefinition":
                        exitNodes.Add(id);
                        detail = "out: " + (CnameVal(nd, "socketName") ?? "?");
                        break;
                    case "questJournalNodeDefinition":
                        detail = nd.TryGetProperty("type", out _) ? "journal entry" : null;
                        break;
                }
                nodes.Add(new QuestNode(id, ntype, detail));
            }

        // Resolve every connection (graphGraphConnectionDefinition) to a node→node edge.
        var edgeSet = new HashSet<(int, int)>();
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    if (TypeOf(e) == "graphGraphConnectionDefinition"
                        && e.TryGetProperty("source", out var src) && e.TryGetProperty("destination", out var dst)
                        && HandleKey(src) is { } sk && HandleKey(dst) is { } dk
                        && socketOwner.TryGetValue(sk, out var from) && socketOwner.TryGetValue(dk, out var to))
                        edgeSet.Add((from, to));
                    foreach (var c in e.EnumerateObject()) Walk(c.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var c in e.EnumerateArray()) Walk(c);
                    break;
            }
        }
        Walk(rc);

        var edges = edgeSet.Select(t => new QuestEdge(t.Item1, t.Item2))
            .OrderBy(e => e.From).ThenBy(e => e.To).ToList();
        var histogram = nodes.GroupBy(n => n.Type)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new QuestPhaseSummary(type, nodes, edges, histogram,
            entryNodes, exitNodes, sceneRefs, phaseRefs);
    }

    // ════════════════════════════════════════════════════════════════════════
    // inspect_community — a .community (communityCommunityTemplate): spawn entries
    // ════════════════════════════════════════════════════════════════════════
    internal sealed record CommunityPhase(string? PhaseName, List<string> Appearances,
        int TimePeriods, int TotalQuantity);
    internal sealed record CommunityEntry(string? EntryName, string? CharacterRecord, string? SpawnInView,
        List<string> Initializers, List<CommunityPhase> Phases);
    internal sealed record CommunitySummary(string? Type, string? SpawnSetReference, int CrowdEntryCount,
        List<CommunityEntry> Entries);

    [McpServerTool(Name = "inspect_community", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Summary of a community / population template (.community / communityCommunityTemplate): " +
                 "each spawn entry with the Character.* record it spawns, its appearances, its spawn " +
                 "phases and per-phase time periods (Day/Night quantities), plus voice-tag initializers. " +
                 "The map of who populates a location/quest scene and when — so you know which entry to " +
                 "retune or which character to swap. Accepts a .community or its .json.")]
    public static async Task<string> InspectCommunity(
        Cp77ToolsRunner runner,
        [Description("A .community file or its converted .json.")] string communityOrJson,
        [Description("Max entries listed (default 200). entryCount always gives the real total.")] int maxEntries = 200,
        CancellationToken ct = default)
    {
        var (json, err) = await LoadCr2wJsonText(runner, communityOrJson, ct);
        if (json is null) return Err(err!);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var s = SummarizeCommunity(doc.RootElement);
            var cap = Math.Max(1, maxEntries);
            var warnings = new List<string>();
            if (s.Type != "communityCommunityTemplate")
                warnings.Add($"Root is '{s.Type}', not communityCommunityTemplate — entries may be incomplete.");
            if (s.Entries.Count == 0)
                warnings.Add("No spawn entry found — unexpected community shape or empty template.");
            var characters = s.Entries.Select(e => e.CharacterRecord).Where(c => c is not null)
                .Distinct().Cast<string>().OrderBy(c => c).ToList();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = s.Entries.Count > 0 && warnings.Count == 0 ? "success" : "partial",
                summary = $"{s.Type}: {s.Entries.Count} entry(ies), {characters.Count} distinct character(s)" +
                          (s.CrowdEntryCount > 0 ? $", {s.CrowdEntryCount} crowd entry(ies)" : ""),
                file = communityOrJson,
                rootType = s.Type,
                spawnSetReference = s.SpawnSetReference,
                entryCount = s.Entries.Count,
                crowdEntryCount = s.CrowdEntryCount,
                distinctCharacters = characters,
                entriesTruncated = s.Entries.Count > cap,
                entries = s.Entries.Take(cap).Select(e => new
                {
                    name = e.EntryName,
                    character = e.CharacterRecord,
                    spawnInView = e.SpawnInView,
                    initializers = e.Initializers,
                    phases = e.Phases.Select(p => new
                    {
                        name = p.PhaseName,
                        appearances = p.Appearances,
                        timePeriods = p.TimePeriods,
                        totalQuantity = p.TotalQuantity,
                    }),
                }),
                warnings,
                errors = Array.Empty<string>(),
            }, JsonOpts);
        }
        catch (Exception ex) { return Err($"Unreadable JSON: {ex.Message}"); }
    }

    /// <summary>(rootType, spawnSetReference, crowd entry count, spawn entries) of a
    /// communityCommunityTemplate JSON. Entries/phases are CHandles ({Data:{…}}); CName/TweakDBID
    /// values are read from their {$value}. Testable.</summary>
    internal static CommunitySummary SummarizeCommunity(JsonElement root)
    {
        var rc = RootChunkOf(root);
        var type = TypeOf(rc);
        var entries = new List<CommunityEntry>();
        string? spawnSet = null;
        var crowdCount = 0;

        if (!rc.TryGetProperty("communityTemplate", out var tplRaw))
            return new CommunitySummary(type, spawnSet, crowdCount, entries);
        var tpl = UnwrapData(tplRaw);
        spawnSet = CnameVal(tpl, "spawnSetReference");
        if (tpl.TryGetProperty("crowdEntries", out var ce) && ce.ValueKind == JsonValueKind.Array)
            crowdCount = ce.GetArrayLength();

        if (tpl.TryGetProperty("entries", out var es) && es.ValueKind == JsonValueKind.Array)
            foreach (var raw in es.EnumerateArray())
            {
                var e = UnwrapData(raw);
                var entryName = CnameVal(e, "entryName");
                var character = CnameVal(e, "characterRecordId"); // TweakDBID {$value}
                var spawnInView = e.TryGetProperty("spawnInView", out var sv) && sv.ValueKind == JsonValueKind.String
                    ? sv.GetString() : null;

                var inits = new List<string>();
                if (e.TryGetProperty("initializers", out var ins) && ins.ValueKind == JsonValueKind.Array)
                    foreach (var raw2 in ins.EnumerateArray())
                    {
                        var init = UnwrapData(raw2);
                        var it = TypeOf(init);
                        // The common one carries a voiceTagName; surface it inline.
                        var vt = CnameVal(init, "voiceTagName");
                        inits.Add(vt is null ? it : $"{it}: {vt}");
                    }

                var phases = new List<CommunityPhase>();
                if (e.TryGetProperty("phases", out var ps) && ps.ValueKind == JsonValueKind.Array)
                    foreach (var raw2 in ps.EnumerateArray())
                    {
                        var ph = UnwrapData(raw2);
                        var phaseName = CnameVal(ph, "phaseName");
                        var apps = new List<string>();
                        if (ph.TryGetProperty("appearances", out var ap) && ap.ValueKind == JsonValueKind.Array)
                            foreach (var a in ap.EnumerateArray())
                                if (NameLike(a) is { } an) apps.Add(an);
                        var tpCount = 0; var qty = 0;
                        if (ph.TryGetProperty("timePeriods", out var tps) && tps.ValueKind == JsonValueKind.Array)
                        {
                            tpCount = tps.GetArrayLength();
                            foreach (var tp in tps.EnumerateArray())
                                qty += IntProp(tp, "quantity") ?? 0;
                        }
                        phases.Add(new CommunityPhase(phaseName, apps, tpCount, qty));
                    }

                entries.Add(new CommunityEntry(entryName, character, spawnInView, inits, phases));
            }

        return new CommunitySummary(type, spawnSet, crowdCount, entries);
    }
}
