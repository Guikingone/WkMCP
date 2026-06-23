using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using WkMcp;
using Xunit;

namespace WkMcp.Tests;

// Pure-helper tests for SceneTools on synthetic scnSceneResource JSON whose shape mirrors
// WolvenKit's real cr2w_to_json output (handles = {HandleId, Data:{$type,…}}, ids =
// {$type, id}, ruid = numeric string). A guarded smoke test runs against a real .scene
// JSON when WKMCP_TEST_SCENE points at one.

public class SceneTestData
{
    // start(1) → section(2, plays dialogue line 10) → end(3). Line 10 + option 20 carry
    // embedded text; actor 0 = "V".
    public static JsonNode GoodScene() => JsonNode.Parse("""
    {
      "Data": { "RootChunk": {
        "$type": "scnSceneResource",
        "version": 5,
        "actors": [ { "$type":"scnActorDef", "actorId":{"$type":"scnActorId","id":0}, "actorName":"V" } ],
        "playerActors": [],
        "screenplayStore": {
          "$type":"scnscreenplayStore",
          "lines": [ { "$type":"scnscreenplayDialogLine", "itemId":{"$type":"scnscreenplayItemId","id":10},
                       "speaker":{"$type":"scnActorId","id":0}, "locstringId":{"$type":"scnlocLocstringId","ruid":"100"} } ],
          "options": [ { "$type":"scnscreenplayChoiceOption", "itemId":{"$type":"scnscreenplayItemId","id":20},
                         "locstringId":{"$type":"scnlocLocstringId","ruid":"200"} } ]
        },
        "locStore": {
          "$type":"scnlocLocStoreEmbedded",
          "vdEntries": [
            { "$type":"vd", "locstringId":{"$type":"scnlocLocstringId","ruid":"100"}, "localeId":"en-us", "vpeIndex":0 },
            { "$type":"vd", "locstringId":{"$type":"scnlocLocstringId","ruid":"200"}, "localeId":"en-us", "vpeIndex":1 }
          ],
          "vpEntries": [ { "$type":"vp", "content":"Hello" }, { "$type":"vp", "content":"Pick me" } ]
        },
        "entryPoints": [ { "$type":"scnEntryPoint", "name":{"$type":"CName","$storage":"string","$value":"in"},
                          "nodeId":{"$type":"scnNodeId","id":1} } ],
        "exitPoints": [],
        "sceneGraph": { "HandleId":"0", "Data": {
          "$type":"scnSceneGraph",
          "startNodes":[{"$type":"scnNodeId","id":1}],
          "endNodes":[{"$type":"scnNodeId","id":3}],
          "graph":[
            { "HandleId":"1", "Data":{ "$type":"scnStartNode", "nodeId":{"$type":"scnNodeId","id":1},
              "outputSockets":[{"$type":"scnOutputSocket","stamp":{"$type":"scnOutputSocketStamp","name":0,"ordinal":0},
                "destinations":[{"$type":"scnInputSocketId","isockStamp":{"name":0,"ordinal":0},"nodeId":{"$type":"scnNodeId","id":2}}]}] } },
            { "HandleId":"2", "Data":{ "$type":"scnSectionNode", "nodeId":{"$type":"scnNodeId","id":2},
              "events":[{"HandleId":"e","Data":{"$type":"scnDialogLineEvent","screenplayLineId":{"$type":"scnscreenplayItemId","id":10},"speaker":{"$type":"scnActorId","id":0}}}],
              "outputSockets":[{"$type":"scnOutputSocket","stamp":{"name":0,"ordinal":0},
                "destinations":[{"$type":"scnInputSocketId","nodeId":{"$type":"scnNodeId","id":3}}]}] } },
            { "HandleId":"3", "Data":{ "$type":"scnEndNode", "nodeId":{"$type":"scnNodeId","id":3}, "outputSockets":[] } }
          ]
        }}
      }}
    }
    """)!;

    // start(1) → choice(2: "Yes"/"No") → end(3). Option "No" references a missing screenplay option.
    public static JsonNode ChoiceScene() => JsonNode.Parse("""
    {
      "Data": { "RootChunk": {
        "$type": "scnSceneResource", "version": 5, "actors": [], "playerActors": [],
        "screenplayStore": { "$type":"scnscreenplayStore", "lines": [],
          "options": [ { "$type":"scnscreenplayChoiceOption", "itemId":{"$type":"scnscreenplayItemId","id":20},
                         "locstringId":{"$type":"scnlocLocstringId","ruid":"200"} } ] },
        "locStore": { "$type":"scnlocLocStoreEmbedded", "vdEntries": [], "vpEntries": [] },
        "sceneGraph": { "HandleId":"0", "Data": {
          "$type":"scnSceneGraph", "startNodes":[{"$type":"scnNodeId","id":1}], "endNodes":[{"$type":"scnNodeId","id":3}],
          "graph":[
            { "HandleId":"1", "Data":{ "$type":"scnStartNode", "nodeId":{"$type":"scnNodeId","id":1},
              "outputSockets":[{"$type":"scnOutputSocket","stamp":{"name":0,"ordinal":0},
                "destinations":[{"$type":"scnInputSocketId","nodeId":{"$type":"scnNodeId","id":2}}]}] } },
            { "HandleId":"2", "Data":{ "$type":"scnChoiceNode", "nodeId":{"$type":"scnNodeId","id":2},
              "options":[
                {"$type":"scnChoiceNodeOption","caption":{"$type":"CName","$storage":"string","$value":"Yes"},"screenplayOptionId":{"$type":"scnscreenplayItemId","id":20}},
                {"$type":"scnChoiceNodeOption","caption":{"$type":"CName","$storage":"string","$value":"No"},"screenplayOptionId":{"$type":"scnscreenplayItemId","id":888}}
              ],
              "outputSockets":[
                {"$type":"scnOutputSocket","stamp":{"name":0,"ordinal":0},"destinations":[{"$type":"scnInputSocketId","nodeId":{"$type":"scnNodeId","id":3}}]},
                {"$type":"scnOutputSocket","stamp":{"name":0,"ordinal":1},"destinations":[{"$type":"scnInputSocketId","nodeId":{"$type":"scnNodeId","id":3}}]}
              ] } },
            { "HandleId":"3", "Data":{ "$type":"scnEndNode", "nodeId":{"$type":"scnNodeId","id":3}, "outputSockets":[] } }
          ]
        }}
      }}
    }
    """)!;

    // Unwrapped node object at graph index i (the {$type,…} inside the handle's Data).
    public static JsonObject Node(JsonNode root, int i)
        => (JsonObject)root["Data"]!["RootChunk"]!["sceneGraph"]!["Data"]!["graph"]![i]!["Data"]!;

    public static JsonObject Rc(JsonNode root) => (JsonObject)root["Data"]!["RootChunk"]!;
}

public class SceneSummaryGraphTests
{
    [Fact]
    public void Summarize_counts_nodes_actors_and_version()
    {
        var (errors, _, summary) = SceneTools.SummarizeScene(SceneTestData.GoodScene());
        Assert.Empty(errors);
        var s = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(summary))!;
        Assert.Equal(3, (int)s["nodes"]!);
        Assert.Equal(1, (int)s["actors"]!);
        Assert.Equal(1, (int)s["screenplayLines"]!);
        Assert.Equal(5, (int)s["version"]!);
    }

    [Fact]
    public void Summarize_rejects_non_scene()
    {
        var (errors, _, _) = SceneTools.SummarizeScene(JsonNode.Parse("""{"Data":{"RootChunk":{"$type":"CMesh"}}}""")!);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Graph_resolves_edges_and_choice_labels()
    {
        var g = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(SceneTools.BuildGraph(SceneTestData.GoodScene())))!;
        var edges = (JsonArray)g["edges"]!;
        Assert.Contains(edges, e => (int)e!["from"]! == 1 && (int)e["to"]! == 2);
        Assert.Contains(edges, e => (int)e!["from"]! == 2 && (int)e["to"]! == 3);

        var cg = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(SceneTools.BuildGraph(SceneTestData.ChoiceScene())))!;
        var choice = ((JsonArray)cg["nodes"]!).First(n => (string?)n!["type"] == "scnChoiceNode")!;
        Assert.Equal("Yes | No", (string?)choice["label"]);
    }
}

public class SceneValidationTests
{
    [Fact]
    public void Good_scene_is_clean()
    {
        var (errors, _, _) = SceneTools.ValidateScene(SceneTestData.GoodScene());
        Assert.Empty(errors);
    }

    [Fact]
    public void Socket_to_missing_node_is_an_error()
    {
        var s = SceneTestData.GoodScene();
        SceneTestData.Node(s, 1)["outputSockets"]![0]!["destinations"]![0]!["nodeId"]!["id"] = 99;
        var (errors, _, _) = SceneTools.ValidateScene(s);
        Assert.Contains(errors, e => e.Contains("missing node id 99"));
    }

    [Fact]
    public void Duplicate_node_id_is_an_error()
    {
        var s = SceneTestData.GoodScene();
        SceneTestData.Node(s, 2)["nodeId"]!["id"] = 2; // end node now collides with section
        var (errors, _, _) = SceneTools.ValidateScene(s);
        Assert.Contains(errors, e => e.Contains("Duplicate node id 2"));
    }

    [Fact]
    public void Cut_control_backup_socket_is_not_flagged_dangling()
    {
        var s = SceneTestData.GoodScene();
        // Turn the end node into a Cut Control with an empty backup socket (name=1026), reachable via section.
        var n = SceneTestData.Node(s, 2);
        n["$type"] = "scnCutControlNode";
        n["outputSockets"] = new JsonArray(new JsonObject
        {
            ["stamp"] = new JsonObject { ["name"] = SceneTools.CutControlBackupSocketName, ["ordinal"] = 0 },
            ["destinations"] = new JsonArray(),
        });
        var (_, warnings, _) = SceneTools.ValidateScene(s);
        Assert.DoesNotContain(warnings, w => w.Contains("dangling"));
    }

    [Fact]
    public void Non_backup_empty_socket_warns_dangling()
    {
        var s = SceneTestData.GoodScene();
        SceneTestData.Node(s, 1)["outputSockets"]![0]!["destinations"] = new JsonArray(); // start node, ordinary socket
        var (_, warnings, _) = SceneTools.ValidateScene(s);
        Assert.Contains(warnings, w => w.Contains("dangling"));
    }

    [Fact]
    public void Live_edge_into_deletion_marker_warns_but_not_errors()
    {
        // graph index 2 is the end node (id 3); the section already has a live edge 2→3.
        var s = SceneTestData.GoodScene();
        SceneTestData.Node(s, 2)["$type"] = "scnDeletionMarkerNode";
        var (errors, warnings, _) = SceneTools.ValidateScene(s);
        Assert.Contains(warnings, w => w.Contains("deletion-marker"));
        Assert.DoesNotContain(errors, e => e.Contains("missing node"));
    }

    [Fact]
    public void Dangling_dialogue_event_is_an_error()
    {
        // graph index 1 is the section node; point its dialogLineEvent at a non-existent line.
        var s = SceneTestData.GoodScene();
        SceneTestData.Node(s, 1)["events"]![0]!["Data"]!["screenplayLineId"]!["id"] = 999;
        var (errors, _, _) = SceneTools.ValidateScene(s);
        Assert.Contains(errors, e => e.Contains("missing screenplay line 999"));
    }

    [Fact]
    public void Choice_option_missing_screenplay_option_is_an_error()
    {
        var (errors, _, _) = SceneTools.ValidateScene(SceneTestData.ChoiceScene());
        Assert.Contains(errors, e => e.Contains("missing screenplay option 888"));
    }

    [Fact]
    public void Unresolved_speaker_and_empty_locstring_warn()
    {
        var s = SceneTestData.GoodScene();
        SceneTestData.Rc(s)["screenplayStore"]!["lines"]![0]!["speaker"]!["id"] = 77; // no such actor
        SceneTestData.Rc(s)["locStore"]!["vpEntries"]![0]!["content"] = "";           // line 100 text empty
        var (_, warnings, _) = SceneTools.ValidateScene(s);
        Assert.Contains(warnings, w => w.Contains("speaker actor 77"));
        Assert.Contains(warnings, w => w.Contains("no embedded text"));
    }
}

public class SceneFindExtractApplyTests
{
    [Fact]
    public void Find_by_id_type_and_text()
    {
        var s = SceneTestData.GoodScene();
        Assert.NotEmpty(SceneTools.FindInScene(s, "2", "id", 50).matches);
        Assert.NotEmpty(SceneTools.FindInScene(s, "Section", "type", 50).matches);
        Assert.NotEmpty(SceneTools.FindInScene(s, "Hello", "text", 50).matches);   // resolved line text
        Assert.NotEmpty(SceneTools.FindInScene(SceneTestData.ChoiceScene(), "Yes", "text", 50).matches); // caption
    }

    [Fact]
    public void Extract_resolves_text_and_speaker()
    {
        var (entries, withText) = SceneTools.ExtractDialogue(SceneTestData.Rc(SceneTestData.GoodScene()), null);
        Assert.Equal(2, withText);
        var line = (object)entries["100"];
        Assert.Equal("Hello", line.GetType().GetProperty("text")!.GetValue(line));
        Assert.Equal("V", line.GetType().GetProperty("speaker")!.GetValue(line));
        Assert.Equal("Pick me", entries["200"].GetType().GetProperty("text")!.GetValue(entries["200"]));
    }

    [Fact]
    public void Apply_writes_text_into_loc_store_and_round_trips()
    {
        var s = SceneTestData.GoodScene();
        var rc = SceneTestData.Rc(s);
        var (applied, _) = SceneTools.ApplyLocalization(rc, new Dictionary<string, string> { ["100"] = "Bonjour" }, null);
        Assert.Equal(1, applied);
        Assert.Equal("Bonjour", (string?)rc["locStore"]!["vpEntries"]![0]!["content"]);

        var (entries, _) = SceneTools.ExtractDialogue(rc, null);
        Assert.Equal("Bonjour", entries["100"].GetType().GetProperty("text")!.GetValue(entries["100"]));
    }

    [Fact]
    public void Apply_reports_zero_when_no_embedded_loc()
    {
        var (applied, warnings) = SceneTools.ApplyLocalization(
            SceneTestData.Rc(SceneTestData.ChoiceScene()),
            new Dictionary<string, string> { ["200"] = "Oui" }, null);
        Assert.Equal(0, applied);
        Assert.Contains(warnings, w => w.Contains("no embedded loc store"));
    }
}

// Runs only when WKMCP_TEST_SCENE points at a real .scene cr2w_to_json output — proves the
// parser against ground-truth WolvenKit JSON without making CI depend on a game install.
public class SceneRealFileSmokeTests
{
    private static string? ScenePath() => Environment.GetEnvironmentVariable("WKMCP_TEST_SCENE");

    [Fact]
    public void Real_scene_inspects_and_validates()
    {
        var path = ScenePath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return; // no-op unless WKMCP_TEST_SCENE is set
        var root = JsonNode.Parse(File.ReadAllText(path!))!;

        var (sErr, _, _) = SceneTools.SummarizeScene(root);
        Assert.Empty(sErr);
        var g = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(SceneTools.BuildGraph(root)))!;
        Assert.True((int)g["nodeCount"]! > 0);
        SceneTools.ValidateScene(root);                 // must not throw
        SceneTools.ExtractDialogue(SceneTestData.Rc(root), null); // must not throw
    }
}
