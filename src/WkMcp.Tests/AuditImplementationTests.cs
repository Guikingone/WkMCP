using System.Text.Json.Nodes;
using WkMcp;
using Xunit;

namespace WkMcp.Tests;

public class AuditImplementationTests
{
    [Fact]
    public void Tool_result_contract_is_present_on_bridge_success_and_failure()
    {
        foreach (var response in new[]
                 {
                     new BridgeResponse("1", true, "{}", null, false, "tcp"),
                     new BridgeResponse("2", false, null, "boom", false, "file"),
                 })
        {
            var root = JsonNode.Parse(LiveTools.Wrap("bridge", response))!.AsObject();
            foreach (var property in new[] { "ok", "status", "summary", "produced", "warnings", "errors", "log" })
                Assert.True(root.ContainsKey(property), $"Missing universal property: {property}");
        }
    }

    [Fact]
    public async Task Import_mesh_rejects_unsupported_direct_creation()
    {
        var dir = TempDir();
        var glb = Path.Combine(dir, "new.mesh.glb");
        await File.WriteAllTextAsync(glb, "dummy");
        var result = JsonNode.Parse(await WolvenKitTools.ImportMesh(null!, glb, dir, keep: false))!;
        Assert.False((bool)result["ok"]!);
        Assert.Contains("cannot create", (string)result["summary"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Semantic_inspectors_summarize_json_without_a_daemon()
    {
        var dir = TempDir();
        var entity = WriteJson(dir, "x.ent.json", """
        {"Data":{"RootChunk":{"$type":"entEntityTemplate","components":[
          {"Data":{"$type":"entMeshComponent","name":{"$value":"body"},"mesh":{"DepotPath":{"$value":"base/body.mesh"}}}}
        ],"appearances":[{"Data":{"$type":"entTemplateAppearance","appearanceName":{"$value":"default"}}}]}}}
        """);
        var anim = WriteJson(dir, "x.anims.json", """
        {"Data":{"RootChunk":{"$type":"animAnimSet","animations":[
          {"Data":{"$type":"animAnimation","name":{"$value":"idle"},"duration":1.5,
          "events":[{"Data":{"$type":"animEvent","eventName":{"$value":"step"},"time":0.25}}],"simdBuffer":{}}}
        ]}}}
        """);
        var sector = WriteJson(dir, "x.streamingsector.json", """
        {"Data":{"RootChunk":{"$type":"worldStreamingSector","nodes":[
          {"Data":{"$type":"worldStaticMeshNode","debugName":{"$value":"prop"},"transform":{},
          "mesh":{"DepotPath":{"$value":"base/prop.mesh"}}}}
        ]}}}
        """);

        var entityResult = JsonNode.Parse(await ModdingTools.InspectEntity(null!, entity))!;
        var animResult = JsonNode.Parse(await ModdingTools.InspectAnimation(null!, anim))!;
        var sectorResult = JsonNode.Parse(await ModdingTools.InspectStreamingSector(null!, sector))!;
        Assert.Equal(1, (int)entityResult["componentCount"]!);
        Assert.Equal(1, (int)animResult["eventCount"]!);
        Assert.Equal(1, (int)sectorResult["nodeCount"]!);
        Assert.True((bool)sectorResult["nodes"]![0]!["hasTransform"]!);
    }

    [Fact]
    public async Task Validate_project_reports_bad_text_files_without_a_daemon()
    {
        var dir = TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir, "broken.json"), "{");
        await File.WriteAllTextAsync(Path.Combine(dir, "broken.reds"), "public class Broken {");
        var result = JsonNode.Parse(await ModdingTools.ValidateProject(null!, dir))!;
        Assert.False((bool)result["ok"]!);
        Assert.Equal(2, (int)result["textErrorCount"]!);
    }

    [Fact]
    public async Task New_pagination_and_atlas_gates_fail_before_runner_access()
    {
        var dir = TempDir();
        var tweak = Path.Combine(dir, "tweakdb.bin");
        await File.WriteAllTextAsync(tweak, "dummy");
        Assert.False((bool)JsonNode.Parse(await WolvenKitTools.TweakDbQuery(null!, tweak, limit: 0))!["ok"]!);
        Assert.False((bool)JsonNode.Parse(await WolvenKitTools.LocResolve(null!, dir))!["ok"]!);
        Assert.False((bool)JsonNode.Parse(await WolvenKitTools.GenerateInkAtlas(null!, dir, dir, dir, "base/ui", "atlas"))!["ok"]!);
        Assert.False((bool)JsonNode.Parse(await WolvenKitTools.ReadGameFile(null!, Path.Combine(dir, "missing"), "base/x.ent"))!["ok"]!);
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wkmcp-audit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteJson(string dir, string name, string json)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, json);
        return path;
    }
}
