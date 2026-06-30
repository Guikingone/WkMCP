using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WkMcp;
using Xunit;

namespace WkMcp.Tests;

// Tests for the coverage-gap tools: import_rig (input-type gate), install_redscript /
// install_cet_mod (pure file-copy installers — happy paths fully exercised), and the
// scaffold_mod `cet` kind. The daemon-backed import path is not touched here.

public class CoverageGapsToolsTests
{
    private static string TempDir(string tag)
    {
        var d = Path.Combine(Path.GetTempPath(), $"cgaps-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task ImportRig_rejects_missing_and_non_glb()
    {
        var dir = TempDir("rig");
        // missing path → Err (runner never used)
        Assert.False((bool)JsonNode.Parse(await WolvenKitTools.ImportRig(null!, Path.Combine(dir, "nope.glb"), dir))!["ok"]!);
        // wrong extension → Err (runner never used)
        var png = Path.Combine(dir, "x.png"); File.WriteAllText(png, "x");
        var r = JsonNode.Parse(await WolvenKitTools.ImportRig(null!, png, dir))!;
        Assert.False((bool)r["ok"]!);
        Assert.Contains("rig", (string?)r["summary"] ?? "");
    }

    [Fact]
    public void InstallRedscript_rejects_bad_inputs()
    {
        var dir = TempDir("reds");
        // missing script
        Assert.False((bool)JsonNode.Parse(WolvenKitTools.InstallRedscript(Path.Combine(dir, "x.reds"), dir))!["ok"]!);
        // not a .reds
        var txt = Path.Combine(dir, "note.txt"); File.WriteAllText(txt, "x");
        Assert.False((bool)JsonNode.Parse(WolvenKitTools.InstallRedscript(txt, dir))!["ok"]!);
        // gamePath that is a bare name (not a path)
        var reds = Path.Combine(dir, "mod.reds"); File.WriteAllText(reds, "// reds");
        Assert.False((bool)JsonNode.Parse(WolvenKitTools.InstallRedscript(reds, "BareName"))!["ok"]!);
    }

    [Fact]
    public void InstallRedscript_installs_file_into_r6_scripts()
    {
        var src = TempDir("reds-src");
        var game = TempDir("game");
        var reds = Path.Combine(src, "MyMod.reds"); File.WriteAllText(reds, "// my reds");

        var res = JsonNode.Parse(WolvenKitTools.InstallRedscript(reds, game))!;
        Assert.True((bool)res["ok"]!);
        var expected = Path.Combine(game, "r6", "scripts", "MyMod", "MyMod.reds");
        Assert.True(File.Exists(expected), $"expected installed file at {expected}");
    }

    [Fact]
    public void InstallRedscript_installs_folder_with_custom_modName()
    {
        var src = TempDir("reds-folder");
        var game = TempDir("game");
        File.WriteAllText(Path.Combine(src, "a.reds"), "// a");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "sub", "b.reds"), "// b");

        var res = JsonNode.Parse(WolvenKitTools.InstallRedscript(src, game, "CoolMod"))!;
        Assert.True((bool)res["ok"]!);
        Assert.True(File.Exists(Path.Combine(game, "r6", "scripts", "CoolMod", "a.reds")));
        Assert.True(File.Exists(Path.Combine(game, "r6", "scripts", "CoolMod", "sub", "b.reds")));
    }

    [Fact]
    public void InstallCetMod_requires_init_lua_then_installs()
    {
        var src = TempDir("cet-src");
        var game = TempDir("game");
        // no init.lua → refuse
        Assert.False((bool)JsonNode.Parse(WolvenKitTools.InstallCetMod(src, game))!["ok"]!);

        File.WriteAllText(Path.Combine(src, "init.lua"), "-- init");
        var res = JsonNode.Parse(WolvenKitTools.InstallCetMod(src, game, "MyCet"))!;
        Assert.True((bool)res["ok"]!);
        var expected = Path.Combine(game, "bin", "x64", "plugins", "cyber_engine_tweaks", "mods", "MyCet", "init.lua");
        Assert.True(File.Exists(expected), $"expected installed CET init.lua at {expected}");
    }

    [Fact]
    public void ScaffoldMod_cet_kind_writes_init_lua()
    {
        var parent = TempDir("scaffold");
        var res = JsonNode.Parse(ModdingTools.ScaffoldMod(parent, "MyCetMod", "cet"))!;
        Assert.True((bool)res["ok"]!);
        Assert.True(File.Exists(Path.Combine(parent, "MyCetMod", "init.lua")));
        Assert.True(File.Exists(Path.Combine(parent, "MyCetMod", "MOD_MANIFEST.json")));
    }
}
