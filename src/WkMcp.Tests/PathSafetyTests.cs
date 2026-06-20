using WkMcp;
using Xunit;

namespace WkMcp.Tests;

/// <summary>
/// Guards the path-containment helpers behind the destructive file tools
/// (write_game_file, toggle_mods). A regression here is an arbitrary-write hole,
/// so these are deliberately exhaustive on the traversal cases.
/// </summary>
public class PathSafetyTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "wkmcp-pathsafety-root");

    // ── TryResolveInside ──────────────────────────────────────────────────

    [Theory]
    [InlineData("base/gameplay/x.ent")]
    [InlineData("a/b/c/d.mesh")]
    [InlineData("single.tweak")]
    public void Relative_inside_is_accepted(string rel)
    {
        Assert.True(PathSafety.TryResolveInside(Root, rel, out var full));
        Assert.StartsWith(Path.GetFullPath(Root), full);
    }

    [Theory]
    [InlineData("..\\..\\Windows\\System32\\evil.dll")]
    [InlineData("../../etc/passwd")]
    [InlineData("a/../../escape.txt")]
    [InlineData("..")]
    public void Traversal_is_rejected(string rel)
        => Assert.False(PathSafety.TryResolveInside(Root, rel, out _));

    [Theory]
    [InlineData("C:\\Windows\\System32\\evil.dll")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x")]
    public void Rooted_relative_is_rejected(string rel)
        => Assert.False(PathSafety.TryResolveInside(Root, rel, out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_is_rejected(string? rel)
        => Assert.False(PathSafety.TryResolveInside(Root, rel!, out _));

    [Fact]
    public void A_prefix_sibling_dir_does_not_count_as_inside()
    {
        // "<root>extra" shares the string prefix but is NOT inside "<root>/".
        var sibling = Root + "extra";
        Assert.False(PathSafety.TryResolveInside(Root, Path.Combine("..", Path.GetFileName(sibling), "x"), out _));
    }

    // ── IsBareFileName ────────────────────────────────────────────────────

    [Theory]
    [InlineData("mymod.archive")]
    [InlineData("Cool_Mod-v2.archive")]
    public void Bare_names_are_accepted(string name)
        => Assert.True(PathSafety.IsBareFileName(name));

    [Theory]
    [InlineData("..\\content\\base.archive")]
    [InlineData("../base.archive")]
    [InlineData("sub/dir.archive")]
    [InlineData("a\\b.archive")]
    [InlineData("")]
    [InlineData("   ")]
    public void Names_with_separators_or_empty_are_rejected(string name)
        => Assert.False(PathSafety.IsBareFileName(name));
}
