using System.ComponentModel;
using ModelContextProtocol.Server;

namespace WolvenKitMcp;

/// <summary>
/// Prompts MCP pour WolvenKit — recettes prêtes à l'emploi qu'un agent Claude peut
/// invoquer pour démarrer un workflow modding sur Cyberpunk 2077. Chaque prompt
/// renvoie un texte instructif (étapes à suivre + outils à appeler), pas une
/// exécution directe : l'agent garde la main sur les choix.
/// </summary>
[McpServerPromptType]
public static class WolvenKitPrompts
{
    [McpServerPrompt(Name = "read_game_file_workflow")]
    [Description("Recette : localiser puis lire un fichier de jeu en JSON, en un coup. " +
                 "Utile pour inspecter rapidement un asset (mesh, ent, app…) sans empaqueter.")]
    public static string ReadGameFileWorkflow(
        [Description("Type ou nom partiel à chercher, ex. \"player.ent\" ou \"*.streamingsector\".")]
        string filePattern,
        [Description("Dossier de contenu du jeu, ex. C:\\Cyberpunk\\Cyberpunk 2077\\archive\\pc\\content")]
        string contentFolder)
    {
        return $$"""
            Procédure pour lire un fichier de jeu Cyberpunk 2077 (motif : {{filePattern}}) :

            1. Appeler `find_in_archives` :
               - archivesFolder = "{{contentFolder}}"
               - pattern = "{{filePattern}}"
               → renvoie une liste de chemins internes et leurs archives.

            2. Choisir le bon résultat, puis appeler `read_game_file` :
               - archivePath = l'archive identifiée à l'étape 1
               - gameFilePath = le chemin interne (ex. base\\path\\fichier.ent)
               → renvoie un JSON éditable (`content` inline + `jsonFile` complet sur disque).

            Si la sortie indique `truncated: true`, lire le fichier complet via le chemin
            renvoyé dans `jsonFile`.
            """;
    }

    [McpServerPrompt(Name = "edit_tweakdb_item")]
    [Description("Recette : modifier les paramètres d'un item TweakDB (dégâts, prix, etc.) " +
                 "via un fichier .tweak qui surcharge la TweakDB du jeu.")]
    public static string EditTweakDbItem(
        [Description("Identifiant TweakDB de l'item à modifier (ex. Items.w_melee_001).")]
        string tweakId,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")]
        string gamePath)
    {
        return $$"""
            Procédure pour modifier l'item TweakDB {{tweakId}} dans Cyberpunk 2077 :

            1. Confirmer que l'identifiant existe via `tweakdb_query` :
               - tweakdbPath = {{gamePath}}\r6\cache\tweakdb.bin
               - filter = "{{tweakId}}"
               → vérifie l'existence et liste les flats de cet identifiant.

            2. Créer un fichier .tweak (format TweakXL — YAML) avec les champs à surcharger.
               Exemple minimal :

                   {{tweakId}}:
                     damage: 200
                     attacksPerSecond: 2.5

            3. Installer le .tweak dans {{gamePath}}\r6\tweaks\<mod-name>.tweak puis relancer
               le jeu. Le .tweak n'a pas besoin d'être empaqueté en .archive.

            NOTE : L'outillage .tweak structuré (`read_tweak`, `write_tweak`, `validate_tweak`,
            `install_tweak`) arrive en Phase 5 — d'ici là, l'édition reste manuelle.
            """;
    }

    [McpServerPrompt(Name = "pack_and_install_mod")]
    [Description("Recette : boucler le workflow modding — empaqueter un dossier source en " +
                 ".archive et l'installer dans l'installation Cyberpunk 2077.")]
    public static string PackAndInstallMod(
        [Description("Dossier source d'un projet de mod (contenant les fichiers REDengine cuits).")]
        string modSourceFolder,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")]
        string gamePath)
    {
        return $$"""
            Procédure d'empaquetage et d'installation d'un mod Cyberpunk 2077 :

            1. (Recommandé) Linter la structure du dossier source avant empaquetage :
               - Vérifier qu'aucun fichier hors REDengine (.txt, .md…) ne traîne dans
                 {{modSourceFolder}} — `pack_archive` les ignorera silencieusement.

            2. Appeler `pack_archive` :
               - folderPath = "{{modSourceFolder}}"
               - outputPath = un dossier de sortie (ex. {{modSourceFolder}}\..\packed)
               → produit une .archive prête à installer.

            3. Appeler `install_mod` :
               - archivePath = la .archive produite à l'étape 2
               - gamePath = "{{gamePath}}"
               → copie l'archive dans {{gamePath}}\archive\pc\mod\.

            4. Au prochain lancement du jeu, le mod est actif. Pour le désactiver,
               supprimer l'archive du dossier mod/.
            """;
    }

    [McpServerPrompt(Name = "recolor_texture")]
    [Description("Recette : extraire une texture du jeu, l'éditer (recoloriser, retoucher), " +
                 "puis la réintégrer dans un mod.")]
    public static string RecolorTexture(
        [Description("Chemin de l'archive contenant la texture, ex. base_2_textures.archive.")]
        string archivePath,
        [Description("Motif glob de la texture (ex. *jacket_01*.xbm).")]
        string texturePattern)
    {
        return $$"""
            Procédure pour recoloriser/remplacer une texture du jeu :

            1. Appeler `uncook` :
               - archivePath = "{{archivePath}}"
               - outputPath = un dossier de travail (ex. C:\Temp\wkmod-textures)
               - pattern = "{{texturePattern}}"
               - textureFormat = "png" (pour éditer dans GIMP / Photoshop)
               → extrait + convertit la texture en .png éditable.

            2. Éditer le .png produit (couleurs, contraste, etc.) avec ton outil image
               préféré. Garder le même nom de fichier.

            3. Appeler `import_raw` :
               - path = le .png modifié
               - outputPath = source\archive\<chemin interne> du projet de mod
               → reconvertit le .png en .xbm REDengine au bon emplacement.

            4. Appeler `pack_archive` puis `install_mod` (voir le prompt
               `pack_and_install_mod` pour les détails).
            """;
    }

    [McpServerPrompt(Name = "inspect_mesh")]
    [Description("Recette : exporter un mesh en glTF pour l'inspecter dans Blender / un visualiseur, " +
                 "avec les options d'export adaptées à ton cas d'usage.")]
    public static string InspectMesh(
        [Description("Chemin de l'archive contenant le mesh.")] string archivePath,
        [Description("Chemin interne du mesh (ex. base\\characters\\common\\hairs\\...\\h_001.mesh) " +
                     "— le localiser au besoin avec find_in_archives.")] string meshInternalPath)
    {
        return $$"""
            Procédure pour inspecter un mesh REDengine :

            1. Appeler `uncook` :
               - archivePath = "{{archivePath}}"
               - outputPath = un dossier de travail
               - pattern = "{{meshInternalPath}}"
               - meshExportType = "WithRig" si tu veux le squelette, sinon laisser vide
                 (par défaut WithMaterials, qui inclut les matériaux)
               - meshExporterType = "REDmod" si tu vises une réimportation certifiée REDmod
               - meshExportLodFilter = true pour ignorer les LOD (réduit le bruit)
               → produit un .glb (glTF binaire) ouvrable dans Blender / un visualiseur 3D.

            2. Si tu veux aussi le JSON CR2W du mesh (structure brute des sous-meshes,
               matériaux, etc.) : appeler `read_game_file` sur le même fichier.
            """;
    }
}
