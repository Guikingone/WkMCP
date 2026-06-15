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
               Au besoin, `describe_tweak_record` détaille les flats du record.

            2. Écrire le fichier .tweak (format TweakXL — YAML) via `write_tweak` avec les
               champs à surcharger. Exemple minimal de contenu :

                   {{tweakId}}:
                     damage: 200
                     attacksPerSecond: 2.5

            3. Valider avec `validate_tweak` (et `lint_tweak` pour les pièges d'indentation),
               puis installer avec `install_tweak` :
               - gamePath = "{{gamePath}}"
               → dépose le fichier dans {{gamePath}}\r6\tweaks\ (TweakXL requis).
               Le .tweak n'a pas besoin d'être empaqueté en .archive.

            4. Relancer le jeu (ou, si le jeu tourne avec le mod CETBridge,
               vérifier la valeur à chaud via `live_tweakdb_get`).
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

    [McpServerPrompt(Name = "create_archivexl_item")]
    [Description("Recette : créer un mod d'item ArchiveXL (vêtement, arme) de bout en bout, avec " +
                 "la validation de la chaîne record → factory → localisation — la cause n°1 " +
                 "d'item qui ne spawn pas.")]
    public static string CreateArchiveXlItem(
        [Description("Nom du mod/item à créer.")] string modName,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath)
    {
        return $$"""
            Procédure pour créer un mod d'item ArchiveXL « {{modName}} » :

            1. Vérifier les prérequis : `check_requirements` sur "{{gamePath}}" —
               ArchiveXL ET TweakXL doivent être installés.

            2. Générer le squelette : `scaffold_archivexl` (modName = "{{modName}}").
               → produit le .yaml (records TweakXL), la factory .csv, la localisation
               .json et le fichier .xl qui les relie.

            3. Éditer les fichiers générés : records (entityName, appearanceName,
               displayName), factory (entityName → chemin .ent), localisation
               (secondaryKey → texte affiché).

            4. Valider la chaîne AVANT d'empaqueter : `validate_item_mod` sur le dossier
               du mod. C'est l'étape qui attrape les typos entre yaml/csv/json —
               un seul caractère d'écart = item invisible sans message d'erreur.
               Avec deep=true, le .ent est aussi converti pour vérifier l'appearanceName.

            5. Empaqueter et installer : `package_mod` puis `install_mod`
               (gamePath = "{{gamePath}}").

            6. Tester en jeu : lancer le jeu, puis si le mod CETBridge est installé,
               `live_add_item` avec l'ID du record pour obtenir l'item directement.
               Sinon `Game.AddToInventory(...)` dans la console CET.

            En cas d'échec silencieux : `diagnose_logs` (ArchiveXL/TweakXL loggent les
            records rejetés) puis re-`validate_item_mod`.
            """;
    }

    [McpServerPrompt(Name = "diagnose_broken_mod")]
    [Description("Recette : diagnostiquer un mod qui ne marche pas ou une install moddée qui " +
                 "crashe — du diagnostic global à la bissection.")]
    public static string DiagnoseBrokenMod(
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath,
        [Description("Symptôme observé (crash au lancement, item absent, script sans effet…).")]
        string symptom)
    {
        return $$"""
            Procédure de diagnostic ({{symptom}}) sur "{{gamePath}}" :

            1. Vue d'ensemble en un appel : `mod_doctor` — frameworks manquants,
               dépendances non satisfaites, conflits, inventaire. Beaucoup de cas
               s'arrêtent ici (framework absent = cause n°1 de crash).

            2. Lire les logs du jeu : `diagnose_logs` — analyse les 6 logs (redscript,
               RED4ext, ArchiveXL, TweakXL…) avec une base d'erreurs connues qui
               mappe chaque erreur à sa cause et son correctif.

            3. Si le symptôme est « un mod en écrase un autre » : `analyze_conflicts`
               — recouvrements d'archives (premier-gagne alphabétique) et records
               TweakDB définis en double, avec les pistes de résolution.

            4. Si le mod a des dépendances : `analyze_dependencies` sur son dossier,
               avec gamePath pour vérifier ce qui est installé (y compris les
               dépendances inter-mods via les imports REDscript).

            5. Scripts : `lint_script` sur les .reds du mod (erreurs ligne:colonne) ;
               tweaks : `lint_tweak` + `validate_tweak`.

            6. Dernier recours — bissection : `toggle_mods` pour désactiver la moitié
               des archives, relancer, et réduire jusqu'au coupable. `backup_mods`
               d'abord pour pouvoir tout restaurer (`restore_mods`).
            """;
    }

    [McpServerPrompt(Name = "live_iteration_loop")]
    [Description("Recette : boucle d'itération avec le jeu LANCÉ (mod CETBridge) — tester des " +
                 "valeurs TweakDB à chaud avant de les figer dans un .tweak.")]
    public static string LiveIterationLoop(
        [Description("Identifiant TweakDB à itérer (ex. Items.Preset_Katana_Saburo).")] string tweakId,
        [Description("Dossier racine de l'installation Cyberpunk 2077.")] string gamePath)
    {
        return $$"""
            Boucle d'itération à chaud sur {{tweakId}} (jeu lancé + mod CETBridge) :

            1. Vérifier le pont : `live_status` (gamePath = "{{gamePath}}").
               Si non connecté : le jeu tourne-t-il avec CET + CETBridge ? (le mod est
               livré dans le bundle, dossier live-bridge/CETBridge à copier dans
               <jeu>/bin/x64/plugins/cyber_engine_tweaks/mods/).

            2. Lire la valeur actuelle : `live_tweakdb_get` (flatPath =
               "{{tweakId}}.<champ>", ex. ".damage").

            3. Itérer À CHAUD sans relancer le jeu : `live_tweakdb_set` avec une
               nouvelle valeur → tester immédiatement en jeu (spawn de l'item via
               `live_add_item` au besoin). Répéter jusqu'à la bonne valeur.
               ⚠ Ces changements sont volatils : perdus au prochain lancement.

            4. Figer le résultat dans un mod : `write_tweak` avec les valeurs retenues,
               `validate_tweak`, puis `install_tweak` (gamePath = "{{gamePath}}").

            5. Vérifier la persistance : relancer le jeu (TweakXL applique le .tweak)
               puis `live_tweakdb_get` à nouveau — la valeur doit être celle du mod.
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
