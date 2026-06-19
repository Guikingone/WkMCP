using System.Collections.Generic;
using System.Linq;
using WolvenKitMcp;
using Xunit;

namespace WolvenKitMcp.Tests;

// Tests of the pure helpers of the three tools added during finalization:
// archive_stats (HistogramByExtension), validate_redmod (ValidateRedmodInfo),
// inspect_app (SummarizeApp / ParseAppearanceNames).

public class TweakTemplateTests
{
    [Fact]
    public void New_item_scaffold_emits_instanceOf_and_typed_checklist()
    {
        var outFile = Path.Combine(Path.GetTempPath(), "wkmcp-newitem-" + Guid.NewGuid().ToString("N") + ".tweak");
        try
        {
            var json = WolvenKitTools.GenerateTweakTemplate(
                "new_item",
                "{\"newId\":\"MyMod.MyGun\",\"baseId\":\"Items.Preset_Lexington\",\"itemType\":\"weapon\"}",
                outFile);

            using (var doc = System.Text.Json.JsonDocument.Parse(json))
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var yaml = File.ReadAllText(outFile);
            Assert.Contains("MyMod.MyGun:", yaml);
            Assert.Contains("$instanceOf: Items.Preset_Lexington", yaml);
            Assert.Contains("statModifiers", yaml);     // weapon-specific checklist line
            Assert.Contains("quality:", yaml);          // universally-safe flat
        }
        finally { try { File.Delete(outFile); } catch { /* ignore */ } }
    }

    [Fact]
    public void New_item_requires_newId_and_baseId()
    {
        var outFile = Path.Combine(Path.GetTempPath(), "wkmcp-newitem-" + Guid.NewGuid().ToString("N") + ".tweak");
        var json = WolvenKitTools.GenerateTweakTemplate("new_item", "{\"newId\":\"MyMod.X\"}", outFile);
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(File.Exists(outFile));
    }
}

public class ArchiveStatsTests
{
    [Fact]
    public void Histogram_groups_by_extension_and_sorts_by_count()
    {
        var entries = new[]
        {
            @"base\a\one.mesh", @"base\a\two.MESH", @"base\b\three.mesh",
            @"base\c\icon.xbm", @"base\c\logo.xbm",
            @"base\d\char.ent",
        };

        var h = WolvenKitTools.HistogramByExtension(entries);

        Assert.Equal(3, h.Count);                       // .mesh, .xbm, .ent
        Assert.Equal(".mesh", h[0].Extension);          // most frequent first
        Assert.Equal(3, h[0].Count);                    // case normalized (MESH = mesh)
        Assert.Equal(".xbm", h[1].Extension);
        Assert.Equal(2, h[1].Count);
        Assert.Equal(".ent", h[2].Extension);
        Assert.Equal(1, h[2].Count);
        Assert.Equal(6, h.Sum(x => x.Count));           // total preserved
    }

    [Fact]
    public void Files_without_extension_are_grouped()
    {
        var h = WolvenKitTools.HistogramByExtension(new[] { @"base\a\README", @"base\a\LICENSE" });
        Assert.Single(h);
        Assert.Equal("(no extension)", h[0].Extension);
        Assert.Equal(2, h[0].Count);
    }

    [Fact]
    public void Empty_list_gives_empty_histogram()
        => Assert.Empty(WolvenKitTools.HistogramByExtension(System.Array.Empty<string>()));
}

public class ValidateRedmodTests
{
    private static readonly IReadOnlyCollection<string> NoFiles = new List<string>();

    [Fact]
    public void Minimal_valid_info_gives_no_error()
    {
        var v = ModdingTools.ValidateRedmodInfo(
            """{ "name": "MonMod", "version": "1.0.0", "description": "x" }""", NoFiles);
        Assert.Empty(v.Errors);
        Assert.Equal("MonMod", v.Name);
        Assert.Equal("1.0.0", v.Version);
    }

    [Fact]
    public void Missing_required_fields_give_errors()
    {
        var v = ModdingTools.ValidateRedmodInfo("""{ "description": "rien" }""", NoFiles);
        Assert.Contains(v.Errors, e => e.Contains("name"));
        Assert.Contains(v.Errors, e => e.Contains("version"));
    }

    [Fact]
    public void Invalid_json_gives_a_clean_error()
    {
        var v = ModdingTools.ValidateRedmodInfo("{ not json", NoFiles);
        Assert.Single(v.Errors);
        Assert.Contains("invalid JSON", v.Errors[0]);
    }

    [Fact]
    public void Non_numeric_version_gives_a_warning_not_an_error()
    {
        var v = ModdingTools.ValidateRedmodInfo(
            """{ "name": "M", "version": "beta" }""", NoFiles);
        Assert.Empty(v.Errors);
        Assert.Contains(v.Warnings, w => w.Contains("version"));
    }

    [Fact]
    public void CustomSound_without_present_file_gives_a_warning()
    {
        var json = """
        { "name": "M", "version": "1.0.0",
          "customSounds": [ { "name": "s1", "type": "mod_sfx_2d", "file": "missing.wav" } ] }
        """;
        var v = ModdingTools.ValidateRedmodInfo(json, new List<string>());
        Assert.Empty(v.Errors);
        Assert.Equal(1, v.CustomSoundCount);
        // presentSoundFiles empty => no existence check (no file warning).
        Assert.DoesNotContain(v.Warnings, w => w.Contains("not found"));

        var v2 = ModdingTools.ValidateRedmodInfo(json, new List<string> { "other.wav" });
        Assert.Contains(v2.Warnings, w => w.Contains("not found"));

        var v3 = ModdingTools.ValidateRedmodInfo(json, new List<string> { "missing.wav" });
        Assert.DoesNotContain(v3.Warnings, w => w.Contains("not found"));
    }

    [Fact]
    public void CustomSound_mod_skip_does_not_require_a_file()
    {
        var v = ModdingTools.ValidateRedmodInfo(
            """{ "name": "M", "version": "1.0.0", "customSounds": [ { "name": "s", "type": "mod_skip" } ] }""",
            NoFiles);
        Assert.Empty(v.Errors);
    }

    [Fact]
    public void CustomSound_non_array_is_an_error()
    {
        var v = ModdingTools.ValidateRedmodInfo(
            """{ "name": "M", "version": "1.0.0", "customSounds": 3 }""", NoFiles);
        Assert.Contains(v.Errors, e => e.Contains("customSounds"));
    }
}

public class InspectAppTests
{
    // Minimal .app in WolvenKit CR2W JSON format (CName = { "$value": ... },
    // DepotPath = { "DepotPath": { "$value": ... } }): two appearances, one with two
    // mesh components, the other without any mesh component.
    private const string AppJson = """
    {
      "Data": { "RootChunk": {
        "appearances": [
          { "Data": { "name": { "$value": "default" }, "components": [
              { "Data": { "mesh": { "DepotPath": { "$value": "base\\a\\body.mesh" } }, "meshAppearance": { "$value": "skin" } } },
              { "Data": { "mesh": { "DepotPath": { "$value": "base\\a\\head.mesh" } }, "meshAppearance": { "$value": "skin" } } }
          ] } },
          { "Data": { "name": { "$value": "naked" }, "components": [] } }
        ]
      } }
    }
    """;

    [Fact]
    public void Summary_counts_appearances_components_and_distinct_meshes()
    {
        var s = ModdingTools.SummarizeApp(AppJson);
        Assert.Equal(2, s.AppearanceCount);
        Assert.Equal(2, s.MeshComponentCount);
        Assert.Equal(2, s.DistinctMeshCount);

        var def = s.Appearances.First(a => a.Name == "default");
        Assert.Equal(2, def.MeshComponents);
        Assert.Equal(2, def.Meshes.Count);

        var naked = s.Appearances.First(a => a.Name == "naked");
        Assert.Equal(0, naked.MeshComponents);
    }

    [Fact]
    public void Appearances_without_mesh_are_still_listed()
    {
        var names = ModdingTools.ParseAppearanceNames(AppJson);
        Assert.Equal(new[] { "default", "naked" }, names);
    }

    [Fact]
    public void Unexpected_json_gives_an_empty_summary()
    {
        var s = ModdingTools.SummarizeApp("{ \"Data\": {} }");
        Assert.Equal(0, s.AppearanceCount);
        Assert.Empty(s.Appearances);
    }
}
