using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using WkMcp;
using Xunit;

namespace WkMcp.Tests;

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

public class CloneTweakRecordTests
{
    [Fact]
    public async Task Missing_tweakdb_returns_error_without_daemon()
    {
        var outFile = Path.Combine(Path.GetTempPath(), "wkmcp-clone-" + Guid.NewGuid().ToString("N") + ".tweak");
        var json = await WolvenKitTools.CloneTweakRecord(
            Cp77ToolsRunner.Shared, "C:/no/such/tweakdb.bin",
            "Items.Preset_Lexington_Default", "MyMod.X", outFile);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(File.Exists(outFile));
    }

    [Fact]
    public async Task Invalid_overrides_json_is_rejected()
    {
        // tweakdb path doesn't exist, but overrides are parsed first — assert it errors cleanly
        // either way and never spawns the daemon (the bad JSON / missing file both short-circuit).
        var outFile = Path.Combine(Path.GetTempPath(), "wkmcp-clone-" + Guid.NewGuid().ToString("N") + ".tweak");
        var json = await WolvenKitTools.CloneTweakRecord(
            Cp77ToolsRunner.Shared, "C:/no/such/tweakdb.bin",
            "Items.X", "MyMod.X", outFile, overridesJson: "{not valid json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    // Real-file smoke test: drives the daemon against a real tweakdb.bin to clone a record and
    // checks the emitted .tweak uses $base. Set WKMCP_TEST_TWEAKDB (a tweakdb.bin); optionally
    // WKMCP_TEST_TWEAKBASE (a base record id, default a vanilla weapon). Silently passes when unset.
    [Fact]
    public async Task Real_clone_emits_base_and_inventory()
    {
        var tdb = Environment.GetEnvironmentVariable("WKMCP_TEST_TWEAKDB");
        if (string.IsNullOrEmpty(tdb) || !File.Exists(tdb)) return;
        var baseId = Environment.GetEnvironmentVariable("WKMCP_TEST_TWEAKBASE") ?? "Items.Preset_Lexington_Default";
        var outFile = Path.Combine(Path.GetTempPath(), "wkmcp-clone-real-" + Guid.NewGuid().ToString("N") + ".tweak");
        try
        {
            var json = await WolvenKitTools.CloneTweakRecord(
                Cp77ToolsRunner.Shared, tdb, baseId, "MyMod.CloneSmokeTest", outFile);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), json);
            var yaml = File.ReadAllText(outFile);
            Assert.Contains("MyMod.CloneSmokeTest:", yaml);
            Assert.Contains($"$base: {baseId}", yaml);
            Assert.Contains("# ", yaml);                       // commented value inventory present
        }
        finally { try { File.Delete(outFile); } catch { /* ignore */ } }
    }
}

public class SetTextureFormatTests
{
    // Synthetic xbm setup: textureGroup as a CName object ({$value}), compression/rawFormat as bare strings.
    private static System.Text.Json.Nodes.JsonNode Sample() => System.Text.Json.Nodes.JsonNode.Parse("""
    { "Data": { "RootChunk": { "setup": {
        "textureGroup": { "$type": "CName", "$value": "TEXG_Generic_Color" },
        "compression": "TCM_None",
        "rawFormat": "TRF_TrueColor"
    } } } }
    """)!;

    [Fact]
    public void Sets_group_compression_and_rawformat_across_node_shapes()
    {
        var node = Sample();
        var (changed, warnings, err) = WolvenKitTools.ApplyTextureFormat(
            node, "TEXG_Generic_Normal", "TCM_Normalmap", "TRF_TrueColor");

        Assert.Null(err);
        Assert.Equal(3, changed);
        Assert.Empty(warnings);
        var setup = node["Data"]!["RootChunk"]!["setup"]!;
        Assert.Equal("TEXG_Generic_Normal", setup["textureGroup"]!["$value"]!.GetValue<string>());
        Assert.Equal("TCM_Normalmap", setup["compression"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Generic_Color", null, null)]   // group missing TEXG_ prefix
    [InlineData(null, "Normalmap", null)]        // compression missing TCM_
    [InlineData(null, null, "TrueColor")]        // rawFormat missing TRF_
    public void Rejects_values_with_the_wrong_enum_prefix(string? g, string? c, string? r)
    {
        var (changed, _, err) = WolvenKitTools.ApplyTextureFormat(Sample(), g, c, r);
        Assert.NotNull(err);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void No_matching_field_warns_and_changes_nothing()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{ "Data": { "other": 1 } }""")!;
        var (changed, warnings, err) = WolvenKitTools.ApplyTextureFormat(node, "TEXG_Generic_Color", null, null);
        Assert.Null(err);
        Assert.Equal(0, changed);
        Assert.NotEmpty(warnings);
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

public class AddAppearanceTests
{
    // .app with real CHandle wrapping (HandleId + Data), one appearance ("base") whose single
    // mesh component is itself a handle — so cloning must renumber BOTH handles.
    private static JsonNode Sample() => JsonNode.Parse("""
    { "Data": { "RootChunk": {
        "$type": "appearanceAppearanceResource",
        "appearances": [
          { "HandleId": "0", "Data": { "$type": "appearanceAppearanceDefinition",
              "name": { "$value": "base" },
              "components": [
                { "HandleId": "1", "Data": { "$type": "entMeshComponent",
                    "mesh": { "DepotPath": { "$value": "base\\a\\body.mesh" } },
                    "meshAppearance": { "$value": "skin" } } }
              ] } }
        ]
    } } }
    """)!;

    private static List<int> AllHandleIds(JsonNode? n)
    {
        var ids = new List<int>();
        void W(JsonNode? x)
        {
            if (x is JsonObject o)
            {
                if (o["HandleId"] is JsonValue hv && int.TryParse(hv.ToString(), out var id)) ids.Add(id);
                foreach (var kv in o) W(kv.Value);
            }
            else if (x is JsonArray a) { foreach (var it in a) W(it); }
        }
        W(n);
        return ids;
    }

    [Fact]
    public void Clones_first_appearance_by_default_and_appends_it()
    {
        var root = Sample();
        var (ok, err, from, swaps, warnings, names) =
            ModdingTools.AddAppearanceToApp(root, "variant", null, null);

        Assert.True(ok);
        Assert.Null(err);
        Assert.Equal("base", from);
        Assert.Equal(0, swaps);
        Assert.Empty(warnings);
        Assert.Equal(new[] { "base", "variant" }, names);

        // the array really grew
        var apps = (JsonArray)root["Data"]!["RootChunk"]!["appearances"]!;
        Assert.Equal(2, apps.Count);
    }

    [Fact]
    public void Cloned_handle_ids_are_renumbered_to_fresh_unique_values()
    {
        var root = Sample();
        ModdingTools.AddAppearanceToApp(root, "variant", null, null);

        var ids = AllHandleIds(root);
        // 4 handles total: source app(0)+comp(1) and clone app+comp with fresh ids.
        Assert.Equal(4, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());      // no collision
        Assert.Contains(2, ids);                              // max was 1 → clone gets 2 and 3
        Assert.Contains(3, ids);
        // source handles untouched
        Assert.Contains(0, ids);
        Assert.Contains(1, ids);
    }

    [Fact]
    public void Rejects_a_duplicate_name()
    {
        var (ok, err, _, _, _, _) = ModdingTools.AddAppearanceToApp(Sample(), "base", null, null);
        Assert.False(ok);
        Assert.Contains("already exists", err);
    }

    [Fact]
    public void Rejects_an_unknown_source_appearance()
    {
        var (ok, err, _, _, _, _) = ModdingTools.AddAppearanceToApp(Sample(), "variant", "ghost", null);
        Assert.False(ok);
        Assert.Contains("not found", err);
        Assert.Contains("base", err);          // lists what is available
    }

    [Fact]
    public void Applies_mesh_swaps_in_the_clone_only_and_warns_on_unmatched()
    {
        var root = Sample();
        var swaps = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            [@"base\a\BODY.mesh"] = @"base\a\new.mesh",   // case-insensitive match
            [@"base\a\absent.mesh"] = @"base\a\nope.mesh", // no match → warning
        };
        var (ok, _, _, count, warnings, _) = ModdingTools.AddAppearanceToApp(root, "variant", null, swaps);

        Assert.True(ok);
        Assert.Equal(1, count);
        Assert.Contains(warnings, w => w.Contains("absent.mesh"));

        var apps = (JsonArray)root["Data"]!["RootChunk"]!["appearances"]!;
        // source mesh unchanged, clone mesh swapped
        var srcMesh = apps[0]!["Data"]!["components"]![0]!["Data"]!["mesh"]!["DepotPath"]!["$value"]!.GetValue<string>();
        var newMesh = apps[1]!["Data"]!["components"]![0]!["Data"]!["mesh"]!["DepotPath"]!["$value"]!.GetValue<string>();
        Assert.Equal(@"base\a\body.mesh", srcMesh);
        Assert.Equal(@"base\a\new.mesh", newMesh);
    }

    [Fact]
    public void Reports_a_clean_error_when_there_is_no_appearances_array()
    {
        var (ok, err, _, _, _, _) =
            ModdingTools.AddAppearanceToApp(JsonNode.Parse("""{ "Data": { "RootChunk": {} } }"""), "x", null, null);
        Assert.False(ok);
        Assert.Contains("appearances", err);
    }

    [Fact]
    public void MaxHandleId_finds_the_largest_or_minus_one()
    {
        Assert.Equal(1, ModdingTools.MaxHandleId(Sample()));
        Assert.Equal(-1, ModdingTools.MaxHandleId(JsonNode.Parse("""{ "a": 1 }""")));
    }
}
