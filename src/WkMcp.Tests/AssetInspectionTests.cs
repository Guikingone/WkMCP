using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using WkMcp;
using Xunit;

namespace WkMcp.Tests;

// Pure-core tests for the asset-inspection tools (ModdingTools partial) on synthetic
// CR2W JSON whose shape mirrors WolvenKit's cr2w_to_json output: files wrap their root in
// Data.RootChunk, resource refs are {DepotPath:{$value:"…"}}, CName/CString are {$value:"…"}.

public class MaterialInstanceTests
{
    // values uses the "single-key wrapper" shape (one param per entry).
    private static JsonElement Mi(JsonDocument doc) => doc.RootElement;
    private const string MiJson = """
    { "Data": { "RootChunk": {
      "$type": "CMaterialInstance",
      "baseMaterial": { "DepotPath": { "$type":"ResourcePath", "$storage":"string", "$value":"base\\mat\\skin.mt" }, "Flags":"Default" },
      "values": [
        { "DiffuseTexture": { "DepotPath": { "$type":"ResourcePath", "$value":"base\\tex\\old_d.xbm" } } },
        { "DiffuseColor": { "$type":"Color", "Red":10, "Green":20, "Blue":30, "Alpha":255 } },
        { "Roughness": 0.5 }
      ]
    }}}
    """;

    [Fact]
    public void Summarize_reads_base_material_and_params()
    {
        using var doc = JsonDocument.Parse(MiJson);
        var (type, baseMat, vals) = ModdingTools.SummarizeMaterialInstance(doc.RootElement);
        Assert.Equal("CMaterialInstance", type);
        Assert.Equal("base\\mat\\skin.mt", baseMat);
        Assert.Equal(3, vals.Count);
        var tex = vals.Single(v => v.Name == "DiffuseTexture");
        Assert.Equal("texture", tex.Kind);
        Assert.Equal("base\\tex\\old_d.xbm", tex.DepotPath);
        Assert.Equal("color", vals.Single(v => v.Name == "DiffuseColor").Kind);
        Assert.Equal("scalar", vals.Single(v => v.Name == "Roughness").Kind);
    }

    [Fact]
    public void Summarize_handles_key_value_pair_shape()
    {
        using var doc = JsonDocument.Parse("""
        { "Data": { "RootChunk": { "$type":"CMaterialInstance",
          "values": [ { "$type":"CKeyValuePair", "Key":{"$type":"CName","$value":"Metalness"}, "Value": 1.0 } ] } } }
        """);
        var (_, _, vals) = ModdingTools.SummarizeMaterialInstance(doc.RootElement);
        Assert.Single(vals);
        Assert.Equal("Metalness", vals[0].Name);
        Assert.Equal("scalar", vals[0].Kind);
    }

    [Fact]
    public void Edit_sets_texture_path_and_reports_previous()
    {
        var root = JsonNode.Parse(MiJson)!;
        var (ok, err, before) = ModdingTools.SetMaterialInstanceParam(root, "DiffuseTexture", "base\\tex\\new_d.xbm", "texture");
        Assert.True(ok, err);
        Assert.Contains("old_d.xbm", before);
        var v = root["Data"]!["RootChunk"]!["values"]![0]!["DiffuseTexture"]!["DepotPath"]!["$value"]!.GetValue<string>();
        Assert.Equal("base\\tex\\new_d.xbm", v);
    }

    [Fact]
    public void Edit_sets_color_components()
    {
        var root = JsonNode.Parse(MiJson)!;
        var (ok, err, _) = ModdingTools.SetMaterialInstanceParam(root, "DiffuseColor", "255,128,0,200", "color");
        Assert.True(ok, err);
        var col = root["Data"]!["RootChunk"]!["values"]![1]!["DiffuseColor"]!;
        Assert.Equal(255, col["Red"]!.GetValue<int>());
        Assert.Equal(128, col["Green"]!.GetValue<int>());
        Assert.Equal(200, col["Alpha"]!.GetValue<int>());
    }

    [Fact]
    public void Edit_missing_param_fails_cleanly()
    {
        var root = JsonNode.Parse(MiJson)!;
        var (ok, err, _) = ModdingTools.SetMaterialInstanceParam(root, "NoSuchParam", "x", "scalar");
        Assert.False(ok);
        Assert.Contains("not found", err);
    }

    [Fact]
    public void Edit_wrong_kind_fails_cleanly()
    {
        var root = JsonNode.Parse(MiJson)!;
        var (ok, err, _) = ModdingTools.SetMaterialInstanceParam(root, "DiffuseColor", "bad", "color");
        Assert.False(ok);
        Assert.Contains("Color must be", err);
    }
}

public class MlSetupTests
{
    [Fact]
    public void Summarize_reads_layers_material_and_microblend()
    {
        using var doc = JsonDocument.Parse("""
        { "Data": { "RootChunk": {
          "$type": "Multilayer_Setup",
          "layers": [
            { "$type":"Multilayer_LayerSetup",
              "material": { "DepotPath": { "$value":"base\\surf\\metal.mltemplate" } },
              "microblend": { "DepotPath": { "$value":"base\\mb\\noise.xbm" } },
              "colorScale": { "$type":"CName", "$value":"Metal_Red" },
              "opacity": 0.8, "matTile": 4.0 },
            { "$type":"Multilayer_LayerSetup",
              "material": { "DepotPath": { "$value":"base\\surf\\fabric.mltemplate" } } }
          ]
        }}}
        """);
        var (type, layers) = ModdingTools.SummarizeMlSetup(doc.RootElement);
        Assert.Equal("Multilayer_Setup", type);
        Assert.Equal(2, layers.Count);
        Assert.Equal("base\\surf\\metal.mltemplate", layers[0].Material);
        Assert.Equal("base\\mb\\noise.xbm", layers[0].Microblend);
        Assert.Equal("Metal_Red", layers[0].ColorScale);
        Assert.Equal(0.8, layers[0].Opacity);
        Assert.Equal(4.0, layers[0].MatTile);
    }
}

public class InkAtlasTests
{
    [Fact]
    public void Parse_reads_slots_parts_and_textures()
    {
        using var doc = JsonDocument.Parse("""
        { "Data": { "RootChunk": {
          "$type":"inkTextureAtlas",
          "slots": [ { "$type":"inkTextureSlot",
            "texture": { "DepotPath": { "$value":"base\\icon\\weapons.xbm" } },
            "parts": [
              { "$type":"inkTextureAtlasMapper", "partName":{"$type":"CName","$value":"katana"},
                "clippingRectInUVCoords": {"Left":0.0,"Top":0.0,"Right":0.5,"Bottom":0.5},
                "clippingRectInPixelCoords": {"left":0,"top":0,"right":64,"bottom":64} },
              { "$type":"inkTextureAtlasMapper", "partName":{"$type":"CName","$value":"pistol"} }
            ] } ]
        }}}
        """);
        var (textures, parts) = ModdingTools.ParseInkAtlasParts(doc.RootElement);
        Assert.Contains("base\\icon\\weapons.xbm", textures);
        Assert.Equal(2, parts.Count);
        var katana = parts.Single(p => p.PartName == "katana");
        Assert.Equal("base\\icon\\weapons.xbm", katana.Texture);
        Assert.Equal("[0.0,0.0,0.5,0.5]", katana.UvRect);
        Assert.Equal("[0,0,64,64]", katana.PixelRect);
    }

    [Fact]
    public void Parse_falls_back_to_tree_walk_when_no_slots()
    {
        using var doc = JsonDocument.Parse("""
        { "Data": { "RootChunk": {
          "$type":"inkTextureAtlas",
          "something": { "texture": { "DepotPath": { "$value":"t.xbm" } },
            "mappers": [ { "partName":{"$value":"icon_a"} } ] }
        }}}
        """);
        var (textures, parts) = ModdingTools.ParseInkAtlasParts(doc.RootElement);
        Assert.Contains("t.xbm", textures);
        Assert.Single(parts);
        Assert.Equal("icon_a", parts[0].PartName);
    }
}

public class InkWidgetTests
{
    [Fact]
    public void Summarize_lists_items_and_counts_widget_types()
    {
        using var doc = JsonDocument.Parse("""
        { "Data": { "RootChunk": {
          "$type":"inkWidgetLibraryResource",
          "libraryItems": [
            { "$type":"inkWidgetLibraryItem", "name":{"$type":"CName","$value":"Root"},
              "packageData": { "tree": [
                { "$type":"inkCanvasWidget", "children": [
                  { "$type":"inkTextWidget" }, { "$type":"inkImageWidget" }, { "$type":"inkTextWidget" }
                ] } ] } }
          ]
        }}}
        """);
        var s = ModdingTools.SummarizeInkWidget(doc.RootElement);
        Assert.Equal("inkWidgetLibraryResource", s.Type);
        Assert.Contains("Root", s.LibraryItems);
        Assert.Equal(4, s.TotalWidgets); // canvas + 2 text + image
        Assert.Equal(2, s.WidgetTypes["inkTextWidget"]);
    }
}

public class RigTests
{
    [Fact]
    public void Summarize_builds_hierarchy_with_parents_and_depth()
    {
        using var doc = JsonDocument.Parse("""
        { "Data": { "RootChunk": {
          "$type":"animRig",
          "boneNames": [ "Root", "Hips", "Spine", "LeftLeg" ],
          "boneParentIndexes": [ -1, 0, 1, 1 ]
        }}}
        """);
        var (type, bones, roots, maxDepth) = ModdingTools.SummarizeRig(doc.RootElement);
        Assert.Equal("animRig", type);
        Assert.Equal(4, bones.Count);
        Assert.Equal(new[] { 0 }, roots.ToArray());
        Assert.Equal("Hips", bones[2].ParentName);
        Assert.Equal(2, maxDepth); // Root(0) -> Hips(1) -> Spine(2)
    }

    [Fact]
    public void Summarize_tolerates_cname_and_wrapped_indexes()
    {
        using var doc = JsonDocument.Parse("""
        { "Data": { "RootChunk": {
          "$type":"animRig",
          "boneNames": [ {"$type":"CName","$value":"Root"}, {"$type":"CName","$value":"Child"} ],
          "boneParentIndexes": [ {"$value":-1}, {"$value":0} ]
        }}}
        """);
        var (_, bones, roots, maxDepth) = ModdingTools.SummarizeRig(doc.RootElement);
        Assert.Equal("Child", bones[1].Name);
        Assert.Equal("Root", bones[1].ParentName);
        Assert.Single(roots);
        Assert.Equal(1, maxDepth);
    }
}

public class DepotPathAndDiffTests
{
    [Fact]
    public void CollectDepotPaths_finds_refs_with_roles_and_dedups()
    {
        using var doc = JsonDocument.Parse("""
        { "Data": { "RootChunk": {
          "$type":"CMesh",
          "externalMaterials": [
            { "DepotPath": { "$value":"a.mi" } },
            { "DepotPath": { "$value":"a.mi" } }
          ],
          "baseRef": { "DepotPath": { "$value":"x.mt" } }
        }}}
        """);
        var refs = ModdingTools.CollectDepotPaths(doc.RootElement);
        Assert.Contains(refs, r => r.role == "externalMaterials" && r.path == "a.mi");
        Assert.Contains(refs, r => r.role == "baseRef" && r.path == "x.mt");
        Assert.Equal(1, refs.Count(r => r.path == "a.mi")); // deduped by role+path
    }

    [Fact]
    public void DiffJson_reports_added_removed_changed()
    {
        var a = """{ "Data": { "RootChunk": { "name":"old", "keep":1 } } }""";
        var b = """{ "Data": { "RootChunk": { "name":"new", "keep":1, "extra":2 } } }""";
        var (added, removed, changed) = ModdingTools.DiffJson(a, b);
        Assert.Contains(added, k => k.EndsWith("extra"));
        Assert.Contains(changed, c => c.Path.EndsWith("name") && c.Base == "old" && c.Mod == "new");
        Assert.Empty(removed);
    }
}

// Guarded smoke tests against REAL extracted game files (cr2w_to_json output). Each runs only
// when its env var points at a converted .json — they validate the parsers against the actual
// WolvenKit shapes (e.g. inkatlas slots = {Elements:[…]}, rig boneNames = CName objects) that
// synthetic fixtures can't fully guarantee. Set WKMCP_TEST_MI / _MLSETUP / _INKATLAS /
// _INKWIDGET / _RIG to run. Silently pass when unset (like SceneTests' WKMCP_TEST_SCENE).
public class RealGameFileSmokeTests
{
    private static JsonElement? Load(string env)
    {
        var p = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
        return JsonDocument.Parse(File.ReadAllText(p)).RootElement.Clone();
    }

    [Fact]
    public void Real_mi()
    {
        if (Load("WKMCP_TEST_MI") is not { } root) return;
        var (type, baseMat, vals) = ModdingTools.SummarizeMaterialInstance(root);
        Assert.Equal("CMaterialInstance", type);
        Assert.False(string.IsNullOrEmpty(baseMat));
        Assert.NotEmpty(vals);
        Assert.Contains(vals, v => v.Kind == "texture" && v.DepotPath is { Length: > 0 });
    }

    [Fact]
    public void Real_mlsetup()
    {
        if (Load("WKMCP_TEST_MLSETUP") is not { } root) return;
        var (type, layers) = ModdingTools.SummarizeMlSetup(root);
        Assert.Equal("Multilayer_Setup", type);
        Assert.NotEmpty(layers);
        Assert.Contains(layers, l => l.Material is { Length: > 0 });
    }

    [Fact]
    public void Real_inkatlas()
    {
        if (Load("WKMCP_TEST_INKATLAS") is not { } root) return;
        var (textures, parts) = ModdingTools.ParseInkAtlasParts(root);
        Assert.NotEmpty(parts);                       // slots = {Elements:[…]} must be unwrapped
        Assert.NotEmpty(textures);
        Assert.Contains(parts, p => p.UvRect is { Length: > 0 });
        Assert.Contains(parts, p => p.Texture is { Length: > 0 });
    }

    [Fact]
    public void Real_inkwidget()
    {
        if (Load("WKMCP_TEST_INKWIDGET") is not { } root) return;
        var s = ModdingTools.SummarizeInkWidget(root);
        Assert.Equal("inkWidgetLibraryResource", s.Type);
        Assert.NotEmpty(s.LibraryItems);
        Assert.True(s.TotalWidgets > 0);
    }

    [Fact]
    public void Real_rig()
    {
        if (Load("WKMCP_TEST_RIG") is not { } root) return;
        var (type, bones, roots, maxDepth) = ModdingTools.SummarizeRig(root);
        Assert.Equal("animRig", type);
        Assert.NotEmpty(bones);
        Assert.NotEmpty(roots);
        Assert.All(bones, b => Assert.False(string.IsNullOrEmpty(b.Name)));
        Assert.True(maxDepth >= 1);
    }

    // End-to-end: drives the real daemon to follow a mesh's material refs across a depot folder.
    // Set WKMCP_TEST_MESH (a real .mesh) and WKMCP_TEST_DEPOT (a folder of its extracted refs).
    [Fact]
    public async Task Real_trace_material_chain()
    {
        var mesh = Environment.GetEnvironmentVariable("WKMCP_TEST_MESH");
        var depot = Environment.GetEnvironmentVariable("WKMCP_TEST_DEPOT");
        if (string.IsNullOrEmpty(mesh) || !File.Exists(mesh) || string.IsNullOrEmpty(depot)) return;

        var json = await ModdingTools.TraceMaterialChain(Cp77ToolsRunner.Shared, mesh, depot, null, 6);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("nodeCount").GetInt32() > 0); // followed at least one ref across files
    }
}

public class NexusPreflightTests
{
    [Fact]
    public void Preflight_flags_quarantined_detects_layout_and_deps()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wkmcp-nexus-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "archive", "pc", "mod"));
            Directory.CreateDirectory(Path.Combine(dir, "r6", "scripts"));
            Directory.CreateDirectory(Path.Combine(dir, "red4ext", "plugins"));
            File.WriteAllText(Path.Combine(dir, "archive", "pc", "mod", "x.archive"), "x");
            File.WriteAllText(Path.Combine(dir, "r6", "scripts", "foo.reds"), "module Foo\n");
            File.WriteAllText(Path.Combine(dir, "red4ext", "plugins", "My.dll"), "BINARY");

            var (quarantined, layoutRoots, deps, fileCount) = ModdingTools.NexusPreflight(dir);

            Assert.Contains(quarantined, q => q.EndsWith("My.dll"));
            Assert.Contains("archive", layoutRoots);
            Assert.Contains("r6", layoutRoots);
            Assert.Contains("redscript", deps);
            Assert.Equal(3, fileCount);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best-effort */ } }
    }
}
