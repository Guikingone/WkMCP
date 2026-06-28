using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using WkMcp;
using Xunit;

namespace WkMcp.Tests;

// Pure-core tests for the gameplay-logic inspectors (ModdingTools partial) on synthetic
// CR2W JSON whose shape mirrors WolvenKit's cr2w_to_json output: nodes/sockets/connections
// are CHandles ({HandleId, Data:{…}}); a connection references its two sockets by HandleId
// (inline) or HandleRefId (back-ref). The fixtures reproduce that handle graph so the
// node→node edge reconstruction is exercised end-to-end.

public class QuestPhaseTests
{
    // Graph: input(0) --Out→In--> scene(1) ; plus an output(2) and an embedded sub-phase(3)
    // and a referenced sub-phase via node(3)'s phaseResource. The connection is inlined in
    // node 0's output socket; node 1's input socket references it via HandleRefId.
    private const string Json = """
    { "Data": { "RootChunk": {
      "$type": "questQuestPhaseResource",
      "graph": { "HandleId": "0", "Data": {
        "$type": "questGraphDefinition",
        "nodes": [
          { "HandleId": "1", "Data": {
            "$type": "questInputNodeDefinition", "id": 0,
            "socketName": { "$type":"CName", "$value":"In1" },
            "sockets": [ { "HandleId": "10", "Data": {
              "$type":"questSocketDefinition", "name":{"$value":"Out"}, "type":"Output",
              "connections": [ { "HandleId": "20", "Data": {
                "$type":"graphGraphConnectionDefinition",
                "source": { "HandleRefId":"10" },
                "destination": { "HandleRefId":"11" }
              } } ]
            } } ]
          } },
          { "HandleId": "2", "Data": {
            "$type": "questSceneNodeDefinition", "id": 1,
            "sceneFile": { "DepotPath": { "$type":"ResourcePath", "$value":"base\\q\\test.scene" } },
            "sockets": [ { "HandleId": "11", "Data": {
              "$type":"questSocketDefinition", "name":{"$value":"In"}, "type":"Input",
              "connections": [ { "HandleRefId":"20" } ]
            } } ]
          } },
          { "HandleId": "3", "Data": {
            "$type": "questOutputNodeDefinition", "id": 2,
            "socketName": { "$type":"CName", "$value":"quest_ends" }, "sockets": []
          } },
          { "HandleId": "4", "Data": {
            "$type": "questPhaseNodeDefinition", "id": 3,
            "phaseResource": { "DepotPath": { "$type":"ResourcePath", "$value":"base\\q\\sub.questphase" } },
            "sockets": []
          } }
        ]
      } }
    } } }
    """;

    [Fact]
    public void Summarize_reads_nodes_types_and_refs()
    {
        using var doc = JsonDocument.Parse(Json);
        var s = ModdingTools.SummarizeQuestPhase(doc.RootElement);
        Assert.Equal("questQuestPhaseResource", s.Type);
        Assert.Equal(4, s.Nodes.Count);
        Assert.Equal(1, s.NodeTypes["questSceneNodeDefinition"]);
        Assert.Contains("base\\q\\test.scene", s.SceneRefs);
        Assert.Contains("base\\q\\sub.questphase", s.PhaseRefs);
        Assert.Contains(0, s.EntryNodes);
        Assert.Contains(2, s.ExitNodes);
    }

    [Fact]
    public void Edges_are_reconstructed_from_socket_handles()
    {
        using var doc = JsonDocument.Parse(Json);
        var s = ModdingTools.SummarizeQuestPhase(doc.RootElement);
        var edge = Assert.Single(s.Edges);
        Assert.Equal(0, edge.From);
        Assert.Equal(1, edge.To);
    }

    [Fact]
    public void Embedded_phase_without_resource_is_not_a_ref()
    {
        // A questPhaseNodeDefinition whose phaseResource is a null ref ("0") holds an embedded
        // subgraph — it must NOT be reported as an external phase reference.
        using var doc = JsonDocument.Parse("""
        { "Data": { "RootChunk": { "$type":"questQuestPhaseResource",
          "graph": { "HandleId":"0", "Data": { "$type":"questGraphDefinition", "nodes": [
            { "HandleId":"1", "Data": { "$type":"questPhaseNodeDefinition", "id":0,
              "phaseResource": { "DepotPath": { "$value":"0" } }, "sockets": [] } }
          ] } } } } }
        """);
        var s = ModdingTools.SummarizeQuestPhase(doc.RootElement);
        Assert.Empty(s.PhaseRefs);
    }

    [Fact]
    public void Empty_or_wrong_root_degrades_gracefully()
    {
        using var doc = JsonDocument.Parse("""{ "Data": { "RootChunk": { "$type":"CMesh" } } }""");
        var s = ModdingTools.SummarizeQuestPhase(doc.RootElement);
        Assert.Empty(s.Nodes);
        Assert.Empty(s.Edges);
    }
}

public class CommunityTests
{
    private const string Json = """
    { "Data": { "RootChunk": {
      "$type": "communityCommunityTemplate",
      "communityTemplate": { "HandleId": "0", "Data": {
        "$type": "communityCommunityTemplateData",
        "crowdEntries": [],
        "spawnSetReference": { "$type":"CName", "$value":"None" },
        "entries": [
          { "HandleId": "1", "Data": {
            "$type": "communitySpawnEntry",
            "characterRecordId": { "$type":"TweakDBID", "$value":"Character.CorpoMan" },
            "entryName": { "$type":"CName", "$value":"man" },
            "spawnInView": "default__true_",
            "initializers": [ { "HandleId":"2", "Data": {
              "$type":"communityVoiceTagInitializer", "voiceTagName": { "$value":"civ_mid_m_29" } } } ],
            "phases": [ { "HandleId":"3", "Data": {
              "$type":"communitySpawnPhase",
              "phaseName": { "$value":"spawn" },
              "appearances": [ { "$type":"CName", "$value":"default" } ],
              "timePeriods": [
                { "$type":"communityPhaseTimePeriod", "hour":"Day", "quantity": 2 },
                { "$type":"communityPhaseTimePeriod", "hour":"Night", "quantity": 0 }
              ]
            } } ]
          } }
        ]
      } }
    } } }
    """;

    [Fact]
    public void Summarize_reads_entries_characters_and_phases()
    {
        using var doc = JsonDocument.Parse(Json);
        var s = ModdingTools.SummarizeCommunity(doc.RootElement);
        Assert.Equal("communityCommunityTemplate", s.Type);
        Assert.Equal("None", s.SpawnSetReference);
        var e = Assert.Single(s.Entries);
        Assert.Equal("man", e.EntryName);
        Assert.Equal("Character.CorpoMan", e.CharacterRecord);
        Assert.Equal("default__true_", e.SpawnInView);
        Assert.Contains(e.Initializers, i => i.Contains("voiceTag", StringComparison.OrdinalIgnoreCase) || i.Contains("civ_mid_m_29"));
    }

    [Fact]
    public void Phase_aggregates_appearances_and_quantity()
    {
        using var doc = JsonDocument.Parse(Json);
        var s = ModdingTools.SummarizeCommunity(doc.RootElement);
        var phase = Assert.Single(s.Entries[0].Phases);
        Assert.Equal("spawn", phase.PhaseName);
        Assert.Contains("default", phase.Appearances);
        Assert.Equal(2, phase.TimePeriods);
        Assert.Equal(2, phase.TotalQuantity); // Day 2 + Night 0
    }

    [Fact]
    public void Wrong_root_degrades_gracefully()
    {
        using var doc = JsonDocument.Parse("""{ "Data": { "RootChunk": { "$type":"CMesh" } } }""");
        var s = ModdingTools.SummarizeCommunity(doc.RootElement);
        Assert.Empty(s.Entries);
    }
}

// Real-file smoke tests: each runs only when its env var points at a converted .json — they
// validate the parsers against the actual WolvenKit shapes (questphase handle/socket graph,
// community CHandle entries) that synthetic fixtures can't fully guarantee. Set
// WKMCP_TEST_QUESTPHASE / WKMCP_TEST_COMMUNITY to run. Silently pass when unset.
public class GameplayRealFileSmokeTests
{
    private static JsonElement? Load(string env)
    {
        var p = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
        return JsonDocument.Parse(File.ReadAllText(p)).RootElement.Clone();
    }

    [Fact]
    public void Real_questphase()
    {
        if (Load("WKMCP_TEST_QUESTPHASE") is not { } root) return;
        var s = ModdingTools.SummarizeQuestPhase(root);
        Assert.Equal("questQuestPhaseResource", s.Type);
        Assert.NotEmpty(s.Nodes);
        Assert.NotEmpty(s.Edges);                       // socket-handle edge reconstruction must resolve
        Assert.All(s.Nodes, n => Assert.False(string.IsNullOrEmpty(n.Type)));
    }

    [Fact]
    public void Real_community()
    {
        if (Load("WKMCP_TEST_COMMUNITY") is not { } root) return;
        var s = ModdingTools.SummarizeCommunity(root);
        Assert.Equal("communityCommunityTemplate", s.Type);
        Assert.NotEmpty(s.Entries);
        Assert.Contains(s.Entries, e => e.CharacterRecord is { Length: > 0 });
        Assert.Contains(s.Entries, e => e.Phases.Count > 0);
    }
}
