using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using WolvenKitMcp;
using Xunit;

namespace WolvenKitMcp.Tests;

/// <summary>
/// Anti-régression documentaire : les prompts et la ressource de référence sont du
/// texte rédigé à la main qui cite des outils par leur nom. Ces tests garantissent
/// que chaque nom cité existe réellement dans l'assembly — un prompt qui recommande
/// un outil inexistant (ou renommé) désinforme l'agent qui le lit.
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

    /// <summary>Tokens backtickés entièrement en snake_case : candidats noms d'outils.</summary>
    private static readonly Regex Backticked = new("`([a-z][a-z0-9_]{2,})`", RegexOptions.Compiled);

    /// <summary>Jargon backtické légitime qui n'est pas un nom d'outil ni de prompt.</summary>
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
            $"{source} cite des noms inconnus (outil renommé ou supprimé ?) : " +
            string.Join(", ", unknown));
    }

    [Fact]
    public void Chaque_prompt_ne_cite_que_des_outils_existants()
    {
        var prompts = PromptTexts().ToList();
        Assert.NotEmpty(prompts);
        foreach (var (source, text) in prompts)
            AssertCitationsExist($"Prompt {source}", text);
    }

    [Fact]
    public void La_reference_ne_cite_que_des_outils_existants()
        => AssertCitationsExist("wolvenkit://reference", WolvenKitResources.BuildReference());

    [Fact]
    public void La_reference_annonce_le_bon_total_outils()
    {
        var total = AllToolNames().Count;
        Assert.Contains($"({total} au total)", WolvenKitResources.BuildReference());
    }

    [Fact]
    public void Les_noms_outils_sont_uniques_entre_classes()
    {
        var all = ToolClasses.SelectMany(WolvenKitResources.ToolNames).ToList();
        var dupes = all.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, "Noms d'outils dupliqués : " + string.Join(", ", dupes));
    }

    // ── Tool annotations ──────────────────────────────────────────────────
    // Les hints (readOnlyHint/destructiveHint/idempotentHint) guident les clients
    // MCP (auto-approbation des lectures, confirmation des outils destructifs).
    // L'attribut ne distingue pas « non défini » de « false », donc l'exhaustivité
    // se vérifie sur le source ; la cohérence se vérifie par réflexion.

    [Fact]
    public void Chaque_outil_declare_ses_annotations_explicitement()
    {
        var srcDir = Path.Combine(TestsDir(), "..", "WolvenKitMcp");
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
            "Outils sans annotations explicites :\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Les_annotations_sont_coherentes()
    {
        var attrs = ToolClasses
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a?.Name is not null)
            .ToDictionary(a => a!.Name!, a => a!);

        // Un outil en lecture seule ne peut pas être destructif.
        var contradictions = attrs.Values.Where(a => a.ReadOnly && a.Destructive)
                                  .Select(a => a.Name).ToList();
        Assert.True(contradictions.Count == 0,
            "ReadOnly + Destructive simultanés : " + string.Join(", ", contradictions!));

        // Spot-checks de classification.
        Assert.True(attrs["find_in_archives"].ReadOnly);
        Assert.True(attrs["uninstall_mod"].Destructive);
        Assert.False(attrs["uninstall_mod"].ReadOnly);
        Assert.True(attrs["live_kill_nearby"].Destructive);
        Assert.False(attrs["launch_game"].Idempotent);
        Assert.True(attrs["wolvenkit_status"].ReadOnly);
    }

    // ── Garde-fou code → README ───────────────────────────────────────────
    // ConsistencyTests vérifie déjà le sens doc → code (les noms cités existent).
    // Ces tests vérifient l'INVERSE : que les prompts et ressources du code sont
    // bien documentés dans le README, et que les comptes annoncés correspondent au
    // code. C'est ce sens qui manquait et qui a laissé le README dériver (5 prompts
    // / 3 ressources documentés au lieu de 8 / 4).

    [Fact]
    public void Le_README_documente_tous_les_prompts_et_ressources()
    {
        var readme = File.ReadAllText(ReadmePath());
        var missing = new List<string>();

        foreach (var (name, _) in WolvenKitResources.PromptInfos())
            if (!readme.Contains(name, StringComparison.Ordinal))
                missing.Add($"prompt `{name}`");

        foreach (var (_, uri) in WolvenKitResources.ResourceInfos())
        {
            // Partie stable avant un éventuel {+path} (le README cite l'URI template).
            var stem = uri.Split('{')[0];
            if (!readme.Contains(stem, StringComparison.Ordinal))
                missing.Add($"ressource `{uri}`");
        }

        Assert.True(missing.Count == 0,
            "README.md ne documente pas : " + string.Join(", ", missing) +
            " — synchroniser les tableaux Prompts/Ressources.");
    }

    [Fact]
    public void Le_README_annonce_les_bons_comptes()
    {
        var readme = File.ReadAllText(ReadmePath());
        var tools = AllToolNames().Count;
        var prompts = WolvenKitResources.PromptInfos().Count;
        var resources = WolvenKitResources.ResourceInfos().Count;

        Assert.Contains($"{tools} outils", readme);
        Assert.Contains($"{prompts} prompts", readme);
        Assert.Contains($"{resources} ressources", readme);
    }

    private static string ReadmePath()
        => Path.GetFullPath(Path.Combine(TestsDir(), "..", "..", "README.md"));

    private static string TestsDir([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetDirectoryName(path)!;
}
