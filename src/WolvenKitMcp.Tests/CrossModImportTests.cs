using WolvenKitMcp;
using Xunit;

namespace WolvenKitMcp.Tests;

/// <summary>Résolution des imports REDscript inter-mods (analyze_dependencies).</summary>
public class CrossModImportTests
{
    private static Dictionary<string, List<string>> Providers => new(StringComparer.OrdinalIgnoreCase)
    {
        ["EquipmentEx"] = new() { "EquipmentEx" },
        ["Codeware.UI"] = new() { "Codeware" },
        ["Audioware.Core.Engine"] = new() { "Audioware" },
    };

    [Fact]
    public void Import_exact_est_resolu()
        => Assert.Equal(new[] { "EquipmentEx" },
            ModdingTools.ResolveImportProvider("EquipmentEx", Providers));

    [Fact]
    public void Import_de_classe_remonte_au_module_parent()
        // import Codeware.UI.InkWidget → fourni par le module Codeware.UI
        => Assert.Equal(new[] { "Codeware" },
            ModdingTools.ResolveImportProvider("Codeware.UI.InkWidget", Providers));

    [Fact]
    public void Import_de_prefixe_couvre_les_sous_modules()
        // import Audioware.* (stocké "Audioware") → le sous-module déclaré suffit
        => Assert.Equal(new[] { "Audioware" },
            ModdingTools.ResolveImportProvider("Audioware", Providers));

    [Fact]
    public void Import_inconnu_renvoie_null()
        => Assert.Null(ModdingTools.ResolveImportProvider("ModInexistant.Truc", Providers));
}
