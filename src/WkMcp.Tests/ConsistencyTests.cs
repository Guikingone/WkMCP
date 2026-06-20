using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using WkMcp;
using Xunit;

namespace WkMcp.Tests;

/// <summary>
/// Documentation anti-regression: the prompts and the reference resource are
/// hand-written text that cites tools by name. These tests guarantee that every
/// cited name really exists in the assembly — a prompt that recommends a
/// nonexistent (or renamed) tool misinforms the agent that reads it.
/// </summary>
public class ConsistencyTests
{
    private static readonly Type[] ToolClasses =
        { typeof(WolvenKitTools), typeof(ModdingTools), typeof(LiveTools) };

    private static HashSet<string> AllToolNames() =>
        ToolClasses.SelectMany(WolvenKitResources.ToolNames)
                   .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> AllPromptNames() =>
        WolvenKitResources.PromptInfos().Select(p => p.Name)
                          .ToHashSet(StringComparer.Ordinal);

    /// <summary>Backticked tokens entirely in snake_case: tool-name candidates.</summary>
    private static readonly Regex Backticked = new("`([a-z][a-z0-9_]{2,})`", RegexOptions.Compiled);

    /// <summary>Legitimate backticked jargon that is neither a tool nor a prompt name.</summary>
    private static readonly HashSet<string> NonToolJargon = new(StringComparer.Ordinal)
    {
        "content", "jsonfile", "truncated", "verbose", "deep", "ok", "status",
        "summary", "produced", "warnings", "errors", "log",
    };

    private static IEnumerable<(string Source, string Text)> PromptTexts()
    {
        foreach (var m in typeof(WolvenKitPrompts).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.GetCustomAttribute<McpServerPromptAttribute>() is null)
                continue;
            var args = m.GetParameters().Select(_ => (object?)"X").ToArray();
            yield return (m.Name, (string)m.Invoke(null, args)!);
        }
    }

    private static void AssertCitationsExist(string source, string text)
    {
        var known = AllToolNames();
        known.UnionWith(AllPromptNames());
        var unknown = Backticked.Matches(text)
            .Select(m => m.Groups[1].Value)
            .Where(t => !NonToolJargon.Contains(t.ToLowerInvariant()))
            .Where(t => !known.Contains(t))
            .Distinct()
            .ToList();
        Assert.True(unknown.Count == 0,
            $"{source} cites unknown names (renamed or removed tool?): " +
            string.Join(", ", unknown));
    }

    [Fact]
    public void Every_prompt_only_cites_existing_tools()
    {
        var prompts = PromptTexts().ToList();
        Assert.NotEmpty(prompts);
        foreach (var (source, text) in prompts)
            AssertCitationsExist($"Prompt {source}", text);
    }

    [Fact]
    public void The_reference_only_cites_existing_tools()
        => AssertCitationsExist("wkmcp://reference", WolvenKitResources.BuildReference());

    [Fact]
    public void The_reference_announces_the_correct_total_tools()
    {
        var total = AllToolNames().Count;
        Assert.Contains($"({total} total)", WolvenKitResources.BuildReference());
    }

    [Fact]
    public void Tool_names_are_unique_across_classes()
    {
        var all = ToolClasses.SelectMany(WolvenKitResources.ToolNames).ToList();
        var dupes = all.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, "Duplicate tool names: " + string.Join(", ", dupes));
    }

    // ── Tool annotations ──────────────────────────────────────────────────
    // The hints (readOnlyHint/destructiveHint/idempotentHint) guide the MCP
    // clients (auto-approval of reads, confirmation of destructive tools).
    // The attribute does not distinguish "unset" from "false", so completeness
    // is checked against the source; consistency is checked by reflection.

    [Fact]
    public void Every_tool_declares_its_annotations_explicitly()
    {
        var srcDir = Path.Combine(TestsDir(), "..", "WkMcp");
        var missing = new List<string>();
        foreach (var file in new[] { "WolvenKitTools.cs", "ModdingTools.cs", "LiveTools.cs" })
            foreach (var line in File.ReadLines(Path.Combine(srcDir, file)))
            {
                if (!line.Contains("[McpServerTool(")) continue;
                if (!line.Contains("ReadOnly =") || !line.Contains("Destructive =") ||
                    !line.Contains("Idempotent ="))
                    missing.Add($"{file} : {line.Trim()}");
            }
        Assert.True(missing.Count == 0,
            "Tools without explicit annotations:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void The_annotations_are_consistent()
    {
        var attrs = ToolClasses
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a?.Name is not null)
            .ToDictionary(a => a!.Name!, a => a!);

        // A read-only tool cannot be destructive.
        var contradictions = attrs.Values.Where(a => a.ReadOnly && a.Destructive)
                                  .Select(a => a.Name).ToList();
        Assert.True(contradictions.Count == 0,
            "ReadOnly + Destructive at the same time: " + string.Join(", ", contradictions!));

        // Classification spot-checks.
        Assert.True(attrs["find_in_archives"].ReadOnly);
        Assert.True(attrs["uninstall_mod"].Destructive);
        Assert.False(attrs["uninstall_mod"].ReadOnly);
        Assert.True(attrs["live_kill_nearby"].Destructive);
        Assert.False(attrs["launch_game"].Idempotent);
        Assert.True(attrs["wk_status"].ReadOnly);
    }

    // ── Code → README safeguard ───────────────────────────────────────────
    // ConsistencyTests already checks the doc → code direction (cited names
    // exist). These tests check the REVERSE: that the prompts and resources in
    // the code are documented in the README, and that the announced counts match
    // the code. That is the direction that was missing and that let the README
    // drift (5 prompts / 3 resources documented instead of 8 / 4).

    [Fact]
    public void The_README_documents_all_prompts_and_resources()
    {
        var readme = File.ReadAllText(ReadmePath());
        var missing = new List<string>();

        foreach (var (name, _) in WolvenKitResources.PromptInfos())
            if (!readme.Contains(name, StringComparison.Ordinal))
                missing.Add($"prompt `{name}`");

        foreach (var (_, uri) in WolvenKitResources.ResourceInfos())
        {
            // Stable part before a possible {+path} (the README cites the URI template).
            var stem = uri.Split('{')[0];
            if (!readme.Contains(stem, StringComparison.Ordinal))
                missing.Add($"resource `{uri}`");
        }

        Assert.True(missing.Count == 0,
            "README.md does not document: " + string.Join(", ", missing) +
            " — synchronize the Prompts/Resources tables.");
    }

    [Fact]
    public void The_README_announces_the_correct_counts()
    {
        var readme = File.ReadAllText(ReadmePath());
        var tools = AllToolNames().Count;
        var prompts = WolvenKitResources.PromptInfos().Count;
        var resources = WolvenKitResources.ResourceInfos().Count;

        Assert.Contains($"{tools} tools", readme);
        Assert.Contains($"{prompts} prompts", readme);
        Assert.Contains($"{resources} resources", readme);
    }

    private static string ReadmePath()
        => Path.GetFullPath(Path.Combine(TestsDir(), "..", "..", "README.md"));

    private static string TestsDir([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetDirectoryName(path)!;
}
