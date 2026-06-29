using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using WkMcp;
using Xunit;

namespace WkMcp.Tests;

// Tests for the import/round-trip + audio-export tools. The underlying import/wwise CLI ops are
// WolvenKit's own (and shared with the proven import_raw); these tests cover the new logic:
// input-type validation and the early-return error branches (which never touch the daemon).

public class ImportToolsTests
{
    private static readonly HashSet<string> Glb = new(StringComparer.OrdinalIgnoreCase) { ".glb", ".gltf" };

    [Fact]
    public void BadExt_accepts_allowed_rejects_others_and_passes_folders()
    {
        Assert.Null(WolvenKitTools.BadExt("x.png", WolvenKitTools.TextureRawExt, "texture"));
        Assert.Null(WolvenKitTools.BadExt("x.DDS", WolvenKitTools.TextureRawExt, "texture")); // case-insensitive
        Assert.NotNull(WolvenKitTools.BadExt("x.txt", WolvenKitTools.TextureRawExt, "texture"));
        Assert.Null(WolvenKitTools.BadExt("x.glb", Glb, "glTF mesh"));
        Assert.NotNull(WolvenKitTools.BadExt("x.png", Glb, "glTF mesh"));
        // an existing directory is allowed (import filters by type itself)
        var dir = Path.Combine(Path.GetTempPath(), "imptests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.Null(WolvenKitTools.BadExt(dir, WolvenKitTools.TextureRawExt, "texture"));
    }

    [Fact]
    public async System.Threading.Tasks.Task ImportTexture_rejects_missing_and_wrong_type()
    {
        // missing path → Err (runner never used)
        var missing = JsonNode.Parse(await WolvenKitTools.ImportTexture(null!, @"C:\nope\x.png", @"C:\out"))!;
        Assert.False((bool)missing["ok"]!);

        // wrong extension → Err (runner never used)
        var dir = Path.Combine(Path.GetTempPath(), "imptests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var txt = Path.Combine(dir, "note.txt");
        File.WriteAllText(txt, "x");
        var wrong = JsonNode.Parse(await WolvenKitTools.ImportTexture(null!, txt, dir))!;
        Assert.False((bool)wrong["ok"]!);
        Assert.Contains("texture", (string?)wrong["summary"] ?? "");
    }

    [Fact]
    public async System.Threading.Tasks.Task Parity_imports_validate_their_input_type()
    {
        var dir = Path.Combine(Path.GetTempPath(), "imptests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "x.png"); File.WriteAllText(png, "x");
        // morphtarget wants glTF
        Assert.False((bool)JsonNode.Parse(await WolvenKitTools.ImportMorphtarget(null!, png, dir))!["ok"]!);
        // mlmask wants a .masklist
        Assert.False((bool)JsonNode.Parse(await WolvenKitTools.ImportMlmask(null!, png, dir))!["ok"]!);
        // material wants a .json
        Assert.False((bool)JsonNode.Parse(await WolvenKitTools.ImportMaterial(null!, png, dir))!["ok"]!);
        var masklist = Path.Combine(dir, "m.masklist"); File.WriteAllText(masklist, "x");
        // a .masklist passes the type gate (then would hit the runner — not called here)
        Assert.Null(WolvenKitTools.BadExt(masklist, new(System.StringComparer.OrdinalIgnoreCase) { ".masklist" }, "mlmask"));
    }

    [Fact]
    public async System.Threading.Tasks.Task FindAndExtract_requires_folder_and_filter()
    {
        // missing folder
        Assert.False((bool)JsonNode.Parse(await WolvenKitTools.FindAndExtract(null!, @"C:\nope", @"C:\out", "*.mesh"))!["ok"]!);
        // existing folder but no pattern/regex → refuse (don't extract everything)
        var dir = Path.Combine(Path.GetTempPath(), "fxtests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.False((bool)JsonNode.Parse(await WolvenKitTools.FindAndExtract(null!, dir, Path.Combine(dir, "o")))!["ok"]!);
    }

    [Fact]
    public async System.Threading.Tasks.Task ImportMesh_rejects_non_glb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "imptests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var png = Path.Combine(dir, "tex.png");
        File.WriteAllText(png, "x");
        var r = JsonNode.Parse(await WolvenKitTools.ImportMesh(null!, png, dir))!;
        Assert.False((bool)r["ok"]!);
    }

}
