using System.Collections.Generic;
using System.Linq;
using WolvenKitMcp;
using Xunit;

namespace WolvenKitMcp.Tests;

// Tests des helpers purs des trois outils ajoutés en finalisation :
// archive_stats (HistogramByExtension), validate_redmod (ValidateRedmodInfo),
// inspect_app (SummarizeApp / ParseAppearanceNames).

public class ArchiveStatsTests
{
    [Fact]
    public void Histogramme_groupe_par_extension_et_trie_par_compte()
    {
        var entries = new[]
        {
            @"base\a\one.mesh", @"base\a\two.MESH", @"base\b\three.mesh",
            @"base\c\icon.xbm", @"base\c\logo.xbm",
            @"base\d\char.ent",
        };

        var h = WolvenKitTools.HistogramByExtension(entries);

        Assert.Equal(3, h.Count);                       // .mesh, .xbm, .ent
        Assert.Equal(".mesh", h[0].Extension);          // le plus fréquent en tête
        Assert.Equal(3, h[0].Count);                    // casse normalisée (MESH = mesh)
        Assert.Equal(".xbm", h[1].Extension);
        Assert.Equal(2, h[1].Count);
        Assert.Equal(".ent", h[2].Extension);
        Assert.Equal(1, h[2].Count);
        Assert.Equal(6, h.Sum(x => x.Count));           // total conservé
    }

    [Fact]
    public void Fichiers_sans_extension_sont_regroupes()
    {
        var h = WolvenKitTools.HistogramByExtension(new[] { @"base\a\README", @"base\a\LICENSE" });
        Assert.Single(h);
        Assert.Equal("(sans extension)", h[0].Extension);
        Assert.Equal(2, h[0].Count);
    }

    [Fact]
    public void Liste_vide_donne_histogramme_vide()
        => Assert.Empty(WolvenKitTools.HistogramByExtension(System.Array.Empty<string>()));
}

public class ValidateRedmodTests
{
    private static readonly IReadOnlyCollection<string> NoFiles = new List<string>();

    [Fact]
    public void Info_valide_minimal_ne_donne_aucune_erreur()
    {
        var v = ModdingTools.ValidateRedmodInfo(
            """{ "name": "MonMod", "version": "1.0.0", "description": "x" }""", NoFiles);
        Assert.Empty(v.Errors);
        Assert.Equal("MonMod", v.Name);
        Assert.Equal("1.0.0", v.Version);
    }

    [Fact]
    public void Champs_requis_manquants_donnent_des_erreurs()
    {
        var v = ModdingTools.ValidateRedmodInfo("""{ "description": "rien" }""", NoFiles);
        Assert.Contains(v.Errors, e => e.Contains("name"));
        Assert.Contains(v.Errors, e => e.Contains("version"));
    }

    [Fact]
    public void Json_invalide_donne_une_erreur_propre()
    {
        var v = ModdingTools.ValidateRedmodInfo("{ pas du json", NoFiles);
        Assert.Single(v.Errors);
        Assert.Contains("JSON invalide", v.Errors[0]);
    }

    [Fact]
    public void Version_non_numerique_donne_un_avertissement_pas_une_erreur()
    {
        var v = ModdingTools.ValidateRedmodInfo(
            """{ "name": "M", "version": "beta" }""", NoFiles);
        Assert.Empty(v.Errors);
        Assert.Contains(v.Warnings, w => w.Contains("version"));
    }

    [Fact]
    public void CustomSound_sans_fichier_present_donne_un_avertissement()
    {
        var json = """
        { "name": "M", "version": "1.0.0",
          "customSounds": [ { "name": "s1", "type": "mod_sfx_2d", "file": "missing.wav" } ] }
        """;
        var v = ModdingTools.ValidateRedmodInfo(json, new List<string>());
        Assert.Empty(v.Errors);
        Assert.Equal(1, v.CustomSoundCount);
        // presentSoundFiles vide ⇒ pas de vérification d'existence (pas d'avertissement fichier).
        Assert.DoesNotContain(v.Warnings, w => w.Contains("introuvable"));

        var v2 = ModdingTools.ValidateRedmodInfo(json, new List<string> { "autre.wav" });
        Assert.Contains(v2.Warnings, w => w.Contains("introuvable"));

        var v3 = ModdingTools.ValidateRedmodInfo(json, new List<string> { "missing.wav" });
        Assert.DoesNotContain(v3.Warnings, w => w.Contains("introuvable"));
    }

    [Fact]
    public void CustomSound_mod_skip_ne_requiert_pas_de_fichier()
    {
        var v = ModdingTools.ValidateRedmodInfo(
            """{ "name": "M", "version": "1.0.0", "customSounds": [ { "name": "s", "type": "mod_skip" } ] }""",
            NoFiles);
        Assert.Empty(v.Errors);
    }

    [Fact]
    public void CustomSound_non_tableau_est_une_erreur()
    {
        var v = ModdingTools.ValidateRedmodInfo(
            """{ "name": "M", "version": "1.0.0", "customSounds": 3 }""", NoFiles);
        Assert.Contains(v.Errors, e => e.Contains("customSounds"));
    }
}

public class InspectAppTests
{
    // .app minimal au format CR2W JSON de WolvenKit (CName = { "$value": ... },
    // DepotPath = { "DepotPath": { "$value": ... } }) : deux apparences, l'une avec deux
    // composants mesh, l'autre sans composant mesh.
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
    public void Resume_compte_apparences_composants_et_meshes_distincts()
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
    public void Apparences_sans_mesh_sont_quand_meme_listees()
    {
        var names = ModdingTools.ParseAppearanceNames(AppJson);
        Assert.Equal(new[] { "default", "naked" }, names);
    }

    [Fact]
    public void Json_inattendu_donne_un_resume_vide()
    {
        var s = ModdingTools.SummarizeApp("{ \"Data\": {} }");
        Assert.Equal(0, s.AppearanceCount);
        Assert.Empty(s.Appearances);
    }
}
