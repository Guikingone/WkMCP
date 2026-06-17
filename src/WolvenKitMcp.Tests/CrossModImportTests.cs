using WolvenKitMcp;
using Xunit;

namespace WolvenKitMcp.Tests;

/// <summary>Resolution of cross-mod REDscript imports (analyze_dependencies).</summary>
public class CrossModImportTests
{
    private static Dictionary<string, List<string>> Providers => new(StringComparer.OrdinalIgnoreCase)
    {
        ["EquipmentEx"] = new() { "EquipmentEx" },
        ["Codeware.UI"] = new() { "Codeware" },
        ["Audioware.Core.Engine"] = new() { "Audioware" },
    };

    [Fact]
    public void Exact_import_is_resolved()
        => Assert.Equal(new[] { "EquipmentEx" },
            ModdingTools.ResolveImportProvider("EquipmentEx", Providers));

    [Fact]
    public void Class_import_walks_up_to_parent_module()
        // import Codeware.UI.InkWidget → provided by the Codeware.UI module
        => Assert.Equal(new[] { "Codeware" },
            ModdingTools.ResolveImportProvider("Codeware.UI.InkWidget", Providers));

    [Fact]
    public void Prefix_import_covers_submodules()
        // import Audioware.* (stored as "Audioware") → the declared submodule is enough
        => Assert.Equal(new[] { "Audioware" },
            ModdingTools.ResolveImportProvider("Audioware", Providers));

    [Fact]
    public void Unknown_import_returns_null()
        => Assert.Null(ModdingTools.ResolveImportProvider("ModInexistant.Truc", Providers));
}
