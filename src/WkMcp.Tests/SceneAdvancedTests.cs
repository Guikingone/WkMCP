using System.Linq;
using System.Text.Json.Nodes;
using WkMcp;
using Xunit;

namespace WkMcp.Tests;

// Tests for the advanced scene tools (dependencies, events, editing, scaffold), with fixtures
// shaped after the real scnSceneResource (resouresReferences anim sets, ridResources, actor
// specCharacterRecordId, handle-wrapped section events).

public class SceneDependencyTests
{
    private static JsonObject Rc() => (JsonObject)JsonNode.Parse("""
        {
          "$type": "scnSceneResource",
          "actors": [ { "actorId": {"$type":"scnActorId","id":0},
                        "specCharacterRecordId": {"$type":"TweakDBID","$storage":"string","$value":"Character.Judy"} } ],
          "playerActors": [],
          "resouresReferences": { "$type":"scnSRRefCollection", "cinematicAnimSets": [
            { "$type":"scnCinematicAnimSetSRRef", "asyncAnimSet": { "DepotPath": {"$type":"ResourcePath","$storage":"string","$value":"base\\anim\\x.anims"} } }
          ] },
          "ridResources": [ { "$type":"scnRidResourceHandler",
            "ridResource": { "DepotPath": {"$type":"ResourcePath","$storage":"string","$value":"custom\\y.anims"} } } ]
        }
        """)!;

    [Fact]
    public void Extracts_anims_rid_and_actor_record()
    {
        var deps = SceneTools.ExtractDependencies(Rc());
        Assert.Contains(deps, d => d.kind == "anims" && d.value == "base\\anim\\x.anims");
        Assert.Contains(deps, d => d.kind == "anims" && d.value == "custom\\y.anims");
        Assert.Contains(deps, d => d.kind == "tweakRecord" && d.value == "Character.Judy");
    }

    [Fact]
    public void Skips_empty_and_zero_paths()
    {
        var rc = Rc();
        ((JsonObject)rc["ridResources"]![0]!["ridResource"]!["DepotPath"]!)["$value"] = "0";
        var deps = SceneTools.ExtractDependencies(rc);
        Assert.DoesNotContain(deps, d => d.value == "0");
    }
}

public class SceneEventsTests
{
    private static JsonObject Rc() => (JsonObject)JsonNode.Parse("""
        {
          "$type": "scnSceneResource",
          "actors": [ { "actorId":{"$type":"scnActorId","id":0}, "actorName":"Judy" } ],
          "playerActors": [],
          "screenplayStore": { "$type":"scnscreenplayStore",
            "lines": [ { "itemId":{"$type":"scnscreenplayItemId","id":10}, "speaker":{"$type":"scnActorId","id":0},
                         "locstringId":{"$type":"scnlocLocstringId","ruid":"100"} } ], "options": [] },
          "locStore": { "$type":"scnlocLocStoreEmbedded",
            "vdEntries":[ {"locstringId":{"$type":"scnlocLocstringId","ruid":"100"},"localeId":"en-us","vpeIndex":0} ],
            "vpEntries":[ {"content":"Hey there"} ] },
          "sceneGraph": { "HandleId":"0", "Data": { "$type":"scnSceneGraph",
            "startNodes":[{"$type":"scnNodeId","id":1}], "endNodes":[{"$type":"scnNodeId","id":1}],
            "graph":[ { "HandleId":"1", "Data": { "$type":"scnSectionNode", "nodeId":{"$type":"scnNodeId","id":1},
              "events":[
                {"HandleId":"e1","Data":{"$type":"scnDialogLineEvent","startTime":0,"duration":2000,"screenplayLineId":{"$type":"scnscreenplayItemId","id":10}}},
                {"HandleId":"e2","Data":{"$type":"scnPlaySkAnimEvent","startTime":100,"duration":1500,"gameplayAnimName":{"$type":"CName","$value":"wave_hello"}}}
              ], "outputSockets":[] } } ] } }
        }
        """)!;

    [Fact]
    public void Lists_section_events_with_resolved_dialogue_and_anim_name()
    {
        var data = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(SceneTools.BuildEvents(Rc())))!;
        Assert.Equal(2, (int)data["totalEvents"]!);
        var evs = (JsonArray)data["sections"]![0]!["events"]!;
        Assert.Contains(evs, e => (string?)e!["type"] == "scnDialogLineEvent" && ((string?)e["detail"])!.Contains("Judy: Hey there"));
        Assert.Contains(evs, e => (string?)e!["type"] == "scnPlaySkAnimEvent" && (string?)e["detail"] == "wave_hello");
    }
}

public class SceneEditTests
{
    private static JsonObject Rc() => (JsonObject)JsonNode.Parse("""
        {
          "$type": "scnSceneResource",
          "actors": [ { "actorId":{"$type":"scnActorId","id":3},
            "specCharacterRecordId":{"$type":"TweakDBID","$storage":"uint64","$value":"0"},
            "specAppearance":{"$type":"CName","$storage":"string","$value":"default"} } ],
          "playerActors": [],
          "resouresReferences": { "$type":"scnSRRefCollection", "cinematicAnimSets": [
            { "asyncAnimSet": { "DepotPath": {"$type":"ResourcePath","$value":"old\\a.anims"} } },
            { "asyncAnimSet": { "DepotPath": {"$type":"ResourcePath","$value":"old\\a.anims"} } }
          ] }
        }
        """)!;

    [Fact]
    public void SetActor_updates_record_and_appearance()
    {
        var rc = Rc();
        Assert.True(SceneTools.SetActor(rc, 3, "Character.Judy", "judy_bikini"));
        var a = (JsonObject)rc["actors"]![0]!;
        Assert.Equal("Character.Judy", (string?)a["specCharacterRecordId"]!["$value"]);
        Assert.Equal("string", (string?)a["specCharacterRecordId"]!["$storage"]); // non-numeric -> string
        Assert.Equal("judy_bikini", (string?)a["specAppearance"]!["$value"]);
    }

    [Fact]
    public void SetActor_returns_false_for_unknown_id()
        => Assert.False(SceneTools.SetActor(Rc(), 99, "Character.X", null));

    [Fact]
    public void ReplaceResource_swaps_all_occurrences()
    {
        var rc = Rc();
        var n = SceneTools.ReplaceResource(rc, "old\\a.anims", "new\\b.anims");
        Assert.Equal(2, n); // both occurrences swapped
        var after = SceneTools.ExtractDependencies(rc); // note: dependency list is de-duplicated
        Assert.DoesNotContain(after, d => d.value == "old\\a.anims");
        Assert.Contains(after, d => d.value == "new\\b.anims");
    }
}

public class SceneScaffoldTests
{
    [Fact]
    public void Skeleton_validates_clean_and_has_expected_nodes()
    {
        var root = SceneTools.BuildSkeleton(2); // start + 2 sections + end = 4 nodes
        var (errors, _, _) = SceneTools.ValidateScene(root);
        Assert.Empty(errors);

        var g = root["Data"]!["RootChunk"]!["sceneGraph"]!["Data"]!;
        Assert.Equal(4, ((JsonArray)g["graph"]!).Count);
        Assert.Equal(1L, g["startNodes"]![0]!["id"]!.GetValue<long>());
        Assert.Equal(4L, g["endNodes"]![0]!["id"]!.GetValue<long>()); // 2 + sectionCount(2)
    }

    [Fact]
    public void Zero_sections_is_start_then_end()
    {
        var root = SceneTools.BuildSkeleton(0);
        Assert.Empty(SceneTools.ValidateScene(root).errors);
        var graph = (JsonArray)root["Data"]!["RootChunk"]!["sceneGraph"]!["Data"]!["graph"]!;
        Assert.Equal(2, graph.Count);
    }
}
