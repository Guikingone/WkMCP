# Référence des outils — WolvenKit MCP

Référence exhaustive des **outils**, **prompts** et **ressources** exposés par le serveur MCP WolvenKit pour le modding de Cyberpunk 2077.

> **Note sur le décompte.** Le serveur expose **88 outils MCP** (63 dans `WolvenKitTools.cs`, 25 dans `ModdingTools.cs`), **8 prompts** (`WolvenKitPrompts.cs`) et **4 ressources** (`WolvenKitResources.cs`) — chiffre confirmé par `tools/list`. L'aide-mémoire interne (ressource `wolvenkit://reference`) évoque encore « 53 » : c'est historique. S'y ajoutent **35 outils `live_*`** (pont live in-game, `LiveTools.cs`), documentés dans [LIVE_BRIDGE.md](LIVE_BRIDGE.md) — soit **123 outils** au total.

## Convention de résultat

Chaque outil renvoie un objet JSON structuré, typiquement :

```json
{ "ok": true, "status": "success", "summary": "...", "produced": [], "warnings": [], "errors": [], "exitCode": 0, "log": "..." }
```

- `status` ∈ `success` | `partial` | `error` | `timeout`.
- Pour les outils **producteurs de fichiers**, le succès est jugé sur les fichiers réellement produits (`produced`), pas sur un marqueur de log.
- Pour les outils **d'information**, le succès se fonde sur le code de sortie.
- Le log volumineux est tronqué (tête + erreurs + queue, ~12 000 car.) ; de nombreux outils acceptent `verbose=true` pour récupérer le log complet.
- En cas d'erreur de validation des arguments, `ok=false`, `status="error"`, `exitCode=-1`.

---

## Live in-game (pont CETBridge)

35 outils `live_*` pilotent un jeu **en cours d'exécution** (exécution Lua, lecture/écriture
d'état, spawn, téléportation, météo, TweakDB en mémoire vive, observation d'événements).
Documentés à part : voir **[LIVE_BRIDGE.md](LIVE_BRIDGE.md)**. Prérequis : jeu lancé + Cyber
Engine Tweaks (+ RedSocket pour le transport TCP). Les outils ci-dessous sont, eux, **hors-ligne**.

---

## 1. Diagnostic

### `wolvenkit_status`
Vérifie que le CLI WolvenKit (cp77tools) est disponible et fonctionnel, et renvoie sa version + stats du cache LRU des listings d'archives (hits/misses) et métriques par verbe. À appeler en premier pour diagnostiquer l'installation.

_Aucun paramètre._

### `clear_cache`
Vide manuellement les caches du serveur. Utile après des modifs hors-bande ou pour reset les stats avant un benchmark.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `scope` | string | non (défaut `archives`) | Portée à vider : `archives` \| `metrics` \| `all`. |

### `compute_hash`
Calcule le hash FNV1a64 utilisé par REDengine pour chaque chaîne fournie (typiquement des chemins de fichiers de jeu).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `inputs` | string[] | oui | Une ou plusieurs chaînes à hacher. |

### `resolve_hash`
Recherche inverse : retrouve le chemin de fichier de jeu correspondant à un hash FNV1a64. L'inverse de `compute_hash`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `hashes` | string[] | oui | Un ou plusieurs hash FNV1a64 (entiers non signés). |

### `tweakdb_resolve`
Recherche inverse d'identifiants TweakDB : un hash → le nom de l'identifiant. Utilise la base de noms TweakDB chargée au démarrage.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `hashes` | string[] | oui | Un ou plusieurs hash d'identifiant TweakDB (entiers non signés). |

### `tweakdb_query`
Interroge la TweakDB : charge un `tweakdb.bin` et liste les records et flats dont l'identifiant contient le filtre. Résultats plafonnés à 100 records + 100 flats.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `tweakdbPath` | string | oui | Chemin d'un fichier `tweakdb.bin`. |
| `filter` | string | non (défaut `""`) | Sous-chaîne à chercher dans les identifiants (vide = tout, 100 max). |

---

## 2. Lecture / inspection d'archives

### `archive_info`
Affiche les informations d'une archive `.archive` : nombre de fichiers et liste optionnelle filtrée. Listing servi par cache LRU.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archivePath` | string | oui | Chemin absolu du fichier `.archive`. |
| `list` | bool | non (défaut `false`) | Lister le contenu (sinon résumé seulement). |
| `pattern` | string? | non | Filtre glob optionnel sur les noms, ex. `*.mesh`. |

### `archive_stats`
Donne la répartition du contenu d'une archive `.archive` par extension de fichier (combien de `.mesh`, `.ent`, `.xbm`, `.app`…). Vue d'ensemble rapide sans lister l'archive entière. Listing servi par le cache LRU. Renvoie `byExtension` (table extension → compte, triée), `categoryCount` (total réel de types) et `fileCount`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archivePath` | string | oui | Chemin absolu du fichier `.archive`. |
| `maxCategories` | int | non (défaut `100`) | Nombre max de catégories d'extension renvoyées ; `categoryCount` donne toujours le total réel. |

### `find_in_archives`
Recherche des fichiers à travers toutes les archives `.archive` d'un dossier. Indique dans quelle archive se trouve chaque fichier. Listings servis par cache LRU.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archivesFolder` | string | oui | Dossier contenant des archives `.archive`. |
| `pattern` | string? | non* | Motif glob à rechercher, ex. `*player*.ent`. |
| `regex` | string? | non* | Expression régulière (alternative au glob). |

\* Au moins l'un des deux (`pattern` ou `regex`) est requis.

### `diff_archives`
Compare deux archives `.archive` et liste les fichiers ajoutés (présents dans B seul) et supprimés (présents dans A seul). Calcule un vrai diff en croisant les deux listings.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archiveA` | string | oui | Première archive (référence). |
| `archiveB` | string | oui | Deuxième archive (à comparer). |

---

## 3. Extraction / uncook

### `extract_files`
Extrait des fichiers d'une archive `.archive` vers un dossier. Filtrage optionnel par glob ou regex.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archivePath` | string | oui | Chemin absolu du `.archive`. |
| `outputPath` | string | oui | Dossier de destination. |
| `pattern` | string? | non | Filtre glob optionnel, ex. `*.mesh`. |
| `regex` | string? | non | Filtre regex optionnel. |
| `verbose` | bool | non (défaut `false`) | Renvoie le log complet (debug). |

### `uncook`
Extrait **et** convertit en une passe (mesh → glTF, textures → image). Combine extraction et conversion.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archivePath` | string | oui | Fichier `.archive` (ou dossier d'archives). |
| `outputPath` | string | oui | Dossier de destination des fichiers convertis. |
| `pattern` | string? | non | Filtre glob optionnel. |
| `textureFormat` | string? | non | Format d'image : `png`, `dds`, `tga`, `bmp` ou `jpg`. |
| `meshExportType` | string? | non | `MeshOnly`, `WithRig`, `Multimesh` (défaut `WithMaterials`). |
| `meshExporterType` | string? | non | `Default`, `Experimental`, `REDmod`. |
| `meshExportLodFilter` | bool | non (défaut `false`) | Filtre les LOD du mesh export. |
| `verbose` | bool | non (défaut `false`) | Renvoie le log complet (debug). |

---

## 4. Conversion

### `cr2w_to_json`
Convertit des fichiers REDengine CR2W extraits (`.mesh`, `.ent`, `.app`...) en JSON lisible et éditable.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `path` | string | oui | Fichier CR2W ou dossier en contenant. |
| `outputPath` | string | oui | Dossier de destination des JSON. |

### `json_to_cr2w`
Reconvertit des fichiers JSON (produits par `cr2w_to_json`) en fichiers CR2W binaires.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `path` | string | oui | Fichier JSON ou dossier en contenant. |
| `outputPath` | string | oui | Dossier de destination des CR2W. |

### `export_files`
Exporte des fichiers REDengine extraits vers des formats raw (mesh → glTF, texture → image...).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `path` | string | oui | Fichier REDengine ou dossier en contenant. |
| `outputPath` | string | oui | Dossier de destination des fichiers raw. |
| `textureFormat` | string? | non | Format d'image : `png`, `dds`, `tga`, `bmp` ou `jpg`. |

### `export_animation`
Exporte une animation REDengine (`.anims`) vers glTF binaire (`.glb`). ⚠ Une `.anims` seule (sans son `.rig`) peut ne rien produire.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `path` | string | oui | Fichier `.anims` ou dossier en contenant. |
| `outputPath` | string | oui | Dossier de destination des `.glb`. |
| `verbose` | bool | non (défaut `false`) | Renvoie le log complet (debug). |

### `export_morphtarget`
Exporte une morphtarget REDengine (`.morphtarget` — blendshapes) vers glTF binaire (`.glb`).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `path` | string | oui | Fichier `.morphtarget` ou dossier en contenant. |
| `outputPath` | string | oui | Dossier de destination des `.glb`. |
| `verbose` | bool | non (défaut `false`) | Renvoie le log complet (debug). |

### `export_mlmask`
Exporte un masque multilayer REDengine (`.mlmask`) vers des images (une par couche).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `path` | string | oui | Fichier `.mlmask` ou dossier en contenant. |
| `outputPath` | string | oui | Dossier de destination des images. |
| `textureFormat` | string? | non | `png` (défaut), `dds`, `tga`, `bmp`, `jpg` ou `tiff`. |
| `verbose` | bool | non (défaut `false`) | Renvoie le log complet (debug). |

---

## 5. Lecture / écriture directe d'un fichier de jeu

### `read_game_file`
Lit un fichier de jeu en un appel : extrait de l'archive, convertit en JSON REDengine et renvoie son contenu. Le JSON complet est aussi écrit sur disque (`jsonFile`).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archivePath` | string | oui | Archive contenant le fichier voulu. |
| `gameFilePath` | string | oui | Chemin interne du fichier dans l'archive. |

### `write_game_file`
Écrit un fichier de jeu édité : convertit un JSON (issu de `read_game_file`) en CR2W binaire, placé au bon chemin interne dans un dossier de mod.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `jsonFile` | string | oui | Fichier JSON édité. |
| `gameFilePath` | string | oui | Chemin interne visé dans le jeu. |
| `modArchiveFolder` | string | oui | Dossier où placer le CR2W. |

---

## 6. Inspection rapide (résumés sans conversion lourde)

### `inspect_mesh`
Inspecte un `.mesh` et renvoie un résumé compact : LODs, sous-meshes, matériaux, bones. Bien plus léger qu'un `uncook` complet.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `meshFile` | string | oui | Fichier `.mesh` REDengine déjà extrait. |

### `inspect_texture`
Inspecte un `.xbm` (texture) et renvoie ses métadonnées : résolution, format, compression, mipmaps, groupe de texture — sans conversion PNG/DDS.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `xbmFile` | string | oui | Fichier `.xbm` REDengine déjà extrait. |

### `inspect_app`
Résumé structurel d'un fichier `.app` : nombre d'apparences, et pour chacune le nombre de composants mesh et les meshes référencés ; total de meshes distincts. Vue d'ensemble rapide **avant** `validate_appearance` (qui, lui, résout et valide chaque `.mesh`). Léger : une seule conversion CR2W→JSON, sans résolution de mesh. Renvoie `appearanceCount`, `meshComponentCount`, `distinctMeshCount` et `appearances` (détail par apparence).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `appFile` | string | oui | Fichier `.app` extrait. |
| `maxAppearances` | int | non (défaut `100`) | Nombre max d'apparences détaillées renvoyées ; `appearanceCount` donne toujours le total réel. |

---

## 7. TweakDB

### `describe_tweak_record`
Pour un identifiant TweakDB (record), liste tous ses flats avec types et valeurs courantes. Indispensable avant d'éditer via `write_tweak`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `tweakdbPath` | string | oui | Fichier `tweakdb.bin`. |
| `recordId` | string | oui | Identifiant TweakDB du record. |

### `read_tweak`
Lit un fichier `.tweak` (TweakXL — YAML) et renvoie son contenu en JSON éditable.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `tweakFile` | string | oui | Fichier `.tweak` (TweakXL). |

### `write_tweak`
Reconvertit un JSON (issu de `read_tweak`) en fichier `.tweak` (YAML TweakXL).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `jsonFile` | string | oui | Fichier JSON édité. |
| `outputTweakFile` | string | oui | Fichier `.tweak` à produire. |

### `validate_tweak`
Vérifie un `.tweak` contre une TweakDB : chaque clé doit exister (record/flat) sauf si elle déclare `$instanceOf`. Renvoie les clés inconnues.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `tweakFile` | string | oui | Fichier `.tweak` à valider. |
| `tweakdbBin` | string | oui | `tweakdb.bin` de référence. |

### `install_tweak`
Installe un `.tweak` dans `<jeu>/r6/tweaks/`. Pris en compte au prochain lancement (chargement à chaud).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `tweakFile` | string | oui | Fichier `.tweak` à installer. |
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

### `dump_records`
Exporte tous les records TweakDB d'un type donné en JSON Lines (`.jsonl`) ou CSV — pour analyses de balance.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `tweakdbPath` | string | oui | Fichier `tweakdb.bin`. |
| `recordType` | string | oui | Nom complet du type CLR de record (ex. `gamedataWeaponItem_Record`). |
| `outputFile` | string | oui | Fichier de sortie (`.jsonl` ou `.csv`). |
| `format` | string | non (défaut `jsonl`) | `jsonl` ou `csv`. |

---

## 8. Génération de templates (scaffolding)

### `generate_redscript_template`
Génère un `.reds` prêt à éditer depuis un catalogue de patterns : `add_method`, `wrap_method`, `replace_method`, `add_field`, `new_class`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `pattern` | string | oui | `add_method` \| `wrap_method` \| `replace_method` \| `add_field` \| `new_class`. |
| `parametersJson` | string | oui | Paramètres du template en JSON (selon le pattern). |
| `outputFile` | string | oui | Fichier `.reds` à produire. |

**Clés de `parametersJson` selon le pattern :**
- `add_method` / `replace_method` : `targetClass` (requis), `methodName` (requis), `args`, `returnType` (défaut `Void`), `body`.
- `wrap_method` : `targetClass` (requis), `methodName` (requis), `args`, `returnType` (défaut `Void`).
- `add_field` : `targetClass` (requis), `fieldName` (requis), `fieldType` (défaut `Int32`).
- `new_class` : `className` (requis), `extends`, `moduleName`.

### `generate_tweak_template`
Génère un `.tweak` (TweakXL — YAML) depuis un catalogue de patterns : `override_field`, `new_record`, `boost_stat`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `pattern` | string | oui | `override_field` \| `new_record` \| `boost_stat`. |
| `parametersJson` | string | oui | Paramètres du template en JSON (selon le pattern). |
| `outputFile` | string | oui | Fichier `.tweak` à produire. |

**Clés de `parametersJson` selon le pattern :**
- `override_field` : `recordId` (requis), `field` (requis), `value` (requis).
- `new_record` : `newId` (requis), `baseId` (requis), `overrides` (sous-JSON `{field: value}`).
- `boost_stat` : `recordId` (requis), `stat` (défaut `damage`), `value` (requis).

---

## 9. Scripts REDscript (.reds)

### `read_script`
Lit un fichier script (`.reds`, `.script`, `.swift`, `.redscript`) et renvoie son contenu + structure extraite par regex (func/class, annotations, module/import). Analyse textuelle uniquement.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `scriptFile` | string | oui | Fichier script. |

### `lint_script`
Analyse syntaxique via un vrai parser (tokenizer + descente récursive) : erreurs de syntaxe (ligne:colonne) + avertissements sémantiques (annotations bien placées, `@wrapMethod` appelant `wrappedMethod()`, doublons). Pas de vérification de types.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `scriptFile` | string | oui | Fichier script. |

---

## 10. Audio / compression bas niveau

### `wwise_export`
Convertit des fichiers audio Wwise WEM en OGG. Nécessite les binaires audio natifs (Windows). Conversions parallèles (jusqu'à 4).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `path` | string | oui | Fichier `.wem` ou dossier en contenant. |
| `outputPath` | string | oui | Dossier de destination des OGG. |
| `verbose` | bool | non (défaut `false`) | Renvoie le log complet (debug). |

### `extract_audio`
Extrait l'audio voix-off (opus) d'une archive vocale (typiquement `lang_xx_voice.archive`). Par défaut tout extraire.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archivePath` | string | oui | Archive vocale `.archive`. |
| `outputPath` | string | oui | Dossier de destination. |
| `opusHashes` | string? | non | Hashes opus précis (uint séparés par virgules). Vide = tout. |
| `verbose` | bool | non (défaut `false`) | Renvoie le log complet (debug). |

### `import_audio`
Importe des WAV (nommés par hash opus) en `.opus` repacké dans un dossier de mod — remplacement de voix-off. ⚠ EXPÉRIMENTAL.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077 (ou chemin du `.exe`). |
| `wavFolder` | string | oui | Dossier des `.wav` (noms = hashes opus). |
| `outputPath` | string | oui | Dossier de sortie du mod. |
| `verbose` | bool | non (défaut `false`) | Renvoie le log complet (debug). |

### `loc_resolve`
Résout une clé de localisation (LocKey : hash uint64 ou clé secondaire texte) en son texte localisé. ⚠ EXPÉRIMENTAL (charge les archives du jeu).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077 (ou chemin du `.exe`). |
| `key` | string | oui | Clé à résoudre : hash uint64 ou clé secondaire texte. |
| `language` | string? | non (défaut `en_us`) | Code REDengine : `en_us`, `fr_fr`, `de_de`, `jp_jp`... |

### `oodle_compress`
Compresse un fichier avec le codec Oodle Kraken.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `inputPath` | string | oui | Fichier d'entrée. |
| `outputPath` | string | oui | Fichier de sortie compressé. |

### `oodle_decompress`
Décompresse un fichier compressé Oodle Kraken.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `inputPath` | string | oui | Fichier d'entrée compressé. |
| `outputPath` | string | oui | Fichier de sortie décompressé. |

---

## 11. Localisation

### `extract_localization`
Extrait d'une `tweakdb.bin` tous les champs traduisibles des records (displayName, etc.) — base pour un mod de traduction UI. Sortie JSON `{recordId: {field: value}}`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `tweakdbPath` | string | oui | Fichier `tweakdb.bin`. |
| `outputJson` | string | oui | Fichier JSON de sortie. |
| `filter` | string? | non | Sous-chaîne à chercher dans les recordId (ex. `Items.`). |

### `build_localization`
Construit un `.tweak` (TweakXL) qui surcharge displayName/localizedDescription depuis un JSON de traductions (issu d'`extract_localization` puis édité).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `translationsJson` | string | oui | JSON des traductions. |
| `outputTweak` | string | oui | Fichier `.tweak` à produire. |
| `lang` | string | non (défaut `fr-fr`) | Code langue (informatif, en commentaire). |

---

## 12. Écriture / empaquetage de mods

### `pack_archive`
Empaquette un dossier de ressources REDengine en archive `.archive` (compression Kraken).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `folderPath` | string | oui | Dossier des ressources à empaqueter. |
| `outputPath` | string | oui | Dossier de destination de l'archive. |
| `verbose` | bool | non (défaut `false`) | Renvoie le log complet (debug). |

### `import_raw`
Importe des fichiers raw (textures, meshes glTF...) en CR2W REDengine, prêts à être empaquetés.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `path` | string | oui | Fichier raw ou dossier en contenant. |
| `outputPath` | string | oui | Dossier de destination des fichiers REDengine. |
| `verbose` | bool | non (défaut `false`) | Renvoie le log complet (debug). |

### `build_project`
Compile les projets WolvenKit (`.cpmodproj`) trouvés dans le dossier donné.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `projectFolder` | string | oui | Dossier contenant un ou plusieurs `.cpmodproj`. |

### `create_mod_project`
Crée la structure d'un projet de mod WolvenKit (source/archive, source/raw, source/resources, source/customSounds, packed) + un `<modName>.cpmodproj`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `parentFolder` | string | oui | Dossier parent où créer le projet. |
| `modName` | string | oui | Nom du mod / du projet. |
| `author` | string? | non | Auteur du mod. |
| `version` | string? | non | Version (ex. 1.0.0). |
| `description` | string? | non | Description du mod. |

### `generate_modproj`
Génère un `.cpmodproj` dans un dossier de projet EXISTANT, pour le rendre compilable par `build_project`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `projectFolder` | string | oui | Dossier racine du projet. |
| `modName` | string | oui | Nom du mod / du projet. |
| `author` | string? | non | Auteur. |
| `version` | string? | non | Version (ex. 1.0.0). |
| `description` | string? | non | Description. |
| `overwrite` | bool | non (défaut `false`) | Écraser un `.cpmodproj` existant. |

### `lint_mod`
Vérifie un mod `.archive` avant installation : extensions non reconnues par REDengine + conflits avec mods installés (si `gamePath`).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archivePath` | string | oui | Archive `.archive` du mod. |
| `gamePath` | string? | non | Racine du jeu (active la détection de conflits). |

### `mod_summary`
Synthèse compacte de ce qu'un mod fait. Accepte un `.archive` (résumé par extension) ou un dossier REDmod (parse info.json, énumère sous-dossiers, extrait clés `.tweak` et déclarations `.reds`).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `modPath` | string | oui | `.archive` OU dossier REDmod (avec info.json). |

---

## 13. REDmod (post-1.6)

### `create_redmod_project`
Crée un projet REDmod : `mods/<nom>/info.json` + sous-dossiers `archives/`, `scripts/`, `tweaks/`, `customSounds/`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `parentFolder` | string | oui | Dossier parent. |
| `modName` | string | oui | Nom du REDmod (sous-dossier). |
| `description` | string | non (défaut `""`) | Description visible dans le launcher. |
| `version` | string | non (défaut `1.0.0`) | Version sémantique. |

### `pack_redmod`
Empaquette un projet REDmod en `.zip` pour distribution. Valide la présence d'`info.json`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `modSourceFolder` | string | oui | Dossier source du REDmod (avec info.json). |
| `outputPath` | string | oui | Dossier de destination du `.zip`. |

### `validate_redmod`
Valide le `info.json` d'un projet REDmod : champs requis `name` / `version` (+ format numérique), et cohérence des entrées `customSounds` (chaque entrée doit avoir `name` + `type` ; un `file` est requis sauf pour le type `mod_skip`, et il doit exister dans `customSounds/`). Les autres outils REDmod ne vérifient que la **présence** du `info.json`, jamais son contenu. Complète `validate_xl` / `validate_tweak` / `validate_item_mod`. `status` = `error` / `partial` / `success`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `modPath` | string | oui | Dossier racine du REDmod (contenant `info.json`) ou chemin direct vers le `info.json`. |

### `install_redmod`
Installe un projet REDmod : copie récursive vers `<jeu>/mods/<nom>/`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `modSourceFolder` | string | oui | Dossier source du REDmod (avec info.json). |
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

### `deploy_redmod`
Exécute `<jeu>/tools/redmod/bin/redMod.exe deploy` — active les REDmods installés (compile scripts + applique tweaks). Timeout 5 min.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

---

## 14. Installation / désinstallation

### `install_mod`
Installe un mod : copie une `.archive` dans `<jeu>/archive/pc/mod/`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archivePath` | string | oui | Archive `.archive` du mod. |
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

### `uninstall_mod`
Désinstalle un mod : retire une `.archive` de `<jeu>/archive/pc/mod/`. Garde-fou : refuse hors du dossier mod.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archivePathOrName` | string | oui | Chemin absolu OU nom du fichier `.archive`. |
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

### `uninstall_redmod`
Désinstalle un REDmod : supprime récursivement `<jeu>/mods/<modName>/`. Garde-fou : refuse hors de `mods/`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `modName` | string | oui | Nom du REDmod (sous-dossier sous mods/). |
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

### `uninstall_tweak`
Désinstalle un `.tweak` : supprime `<jeu>/r6/tweaks/<tweakName>`. Garde-fou : refuse hors de `r6/tweaks/`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `tweakName` | string | oui | Nom du fichier `.tweak`. |
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

### `list_installed_mods`
Liste les mods installés : `.archive` dans `archive/pc/mod` et REDmods dans `mods/`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

### `detect_conflicts`
Détecte les conflits entre mods installés (un même fichier fourni par plusieurs mods). Sortie JSON structurée.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

---

## 15. Sécurité (backup / restore)

### `backup_mods`
Sauvegarde l'état des mods (`archive/pc/mod/`, `mods/`, `r6/tweaks/`) dans un `.zip` horodaté.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |
| `outputDir` | string | oui | Dossier où déposer le `.zip`. |
| `backupName` | string? | non | Nom du ZIP (défaut `wkmcp-mods-backup-<YYYYMMDD-HHmmss>.zip`). |

### `restore_mods`
Restaure un backup. Mode `merge` (par-dessus l'existant) ou `replace` (vide d'abord les dossiers cibles — destructeur).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `backupZip` | string | oui | ZIP de backup à restaurer. |
| `gamePath` | string | oui | Dossier racine cible de Cyberpunk 2077. |
| `mode` | string | non (défaut `merge`) | `merge` \| `replace`. |

---

## 16. In-game (lancement / logs)

### `launch_game`
⚠ Lance Cyberpunk 2077 (`bin/x64/Cyberpunk2077.exe`). Si `deployRedmod=true`, exécute d'abord `redMod.exe deploy`. Le jeu est lancé détaché.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |
| `deployRedmod` | bool | non (défaut `true`) | Lance `redMod.exe deploy` avant. |
| `extraArgs` | string? | non | Arguments supplémentaires passés à l'exe. |

### `tail_game_logs`
Lit la queue des logs : `game` (r6/logs sauf redscript), `redmod` (tools/redmod/logs), `redscript` (r6/logs *redscript*), `all`. Filtre substring optionnel.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |
| `log` | string | non (défaut `game`) | `game` \| `redmod` \| `redscript` \| `all`. |
| `lines` | int | non (défaut `200`) | Nombre de lignes à renvoyer. |
| `filter` | string? | non | Filtre substring (insensible à la casse). |

---

## 17. Intelligence / workflow (haut niveau — `ModdingTools`)

### `analyze_dependencies`
Analyse un dossier de mod et déduit ses frameworks/dépendances requis (redscript, RED4ext, ArchiveXL, TweakXL, Codeware, Audioware, Mod Settings, CET...) en lisant imports REDscript, `.xl`, `.tweak`, types de fichiers. Si `gamePath` fourni : indique installé/manquant.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `modPath` | string | oui | Dossier du mod à analyser. |
| `gamePath` | string? | non | Racine du jeu, pour vérifier les dépendances installées. |

### `check_requirements`
Inventorie les frameworks de modding INSTALLÉS dans une installation Cyberpunk 2077, avec version si détectable.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

### `mod_doctor`
Diagnostic de santé d'une installation moddée en un appel : frameworks installés/manquants, dépendances requises mais absentes, conflits d'archives, inventaire des mods + recommandations.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

### `validate_xl`
Valide un fichier ArchiveXL `.xl` (YAML) : YAML bien formé + sections de premier niveau reconnues (`customSounds`, `resource`, `factories`, `localization`, `animations`).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `xlFile` | string | oui | Fichier `.xl` à valider. |

### `scaffold_archivexl`
Génère un `.xl` ArchiveXL de départ (YAML commenté) selon le type : `factory`, `customSounds`, `localization`, `resource`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `outputFolder` | string | oui | Dossier de destination du `.xl`. |
| `modName` | string | oui | Nom du mod (= nom de fichier `<nom>.xl`). |
| `kind` | string | non (défaut `factory`) | `factory` \| `customSounds` \| `localization` \| `resource`. |

### `find_references`
Recherche toutes les références textuelles à une cible dans les fichiers source d'un dossier (`.reds`, `.tweak`, `.yaml`, `.xl`, `.lua`, `.json`, `.csv`). Renvoie fichier:ligne + extrait.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `target` | string | oui | Chaîne à rechercher (sous-chaîne). |
| `searchFolder` | string | oui | Dossier à parcourir. |
| `maxResults` | int | non (défaut `200`) | Nombre max de correspondances. |
| `caseSensitive` | bool | non (défaut `false`) | Recherche sensible à la casse. |

### `diff_mod_vs_base`
Diff sémantique d'UN fichier de jeu surchargé par un mod, contre sa version de base : extrait des deux côtés, convertit en JSON, compare les champs (ajoutés/supprimés/modifiés).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `modArchive` | string | oui | Archive `.archive` du mod. |
| `gameFilePath` | string | oui | Chemin interne du fichier. |
| `gamePath` | string | oui | Racine du jeu (localiser la base dans archive/pc/content). |
| `baseArchive` | string? | non | Archive de base précise (court-circuite la recherche). |

### `scaffold_mod`
Crée en un appel un squelette de mod fonctionnel selon son type : `archive`, `redscript`, `tweak`, `redmod`. Écrit aussi un `MOD_MANIFEST.json`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `parentFolder` | string | oui | Dossier parent. |
| `modName` | string | oui | Nom du mod. |
| `kind` | string | non (défaut `archive`) | `archive` \| `redscript` \| `tweak` \| `redmod`. |
| `author` | string? | non | Auteur. |
| `version` | string? | non | Version (ex. 1.0.0). |
| `dependencies` | string? | non | Dépendances déclarées, séparées par des virgules. |

### `package_mod`
Empaquette un dossier au layout relatif au jeu (archive/pc/mod, r6/scripts, r6/tweaks, mods/, red4ext/...) en un `.zip` distribuable (séparateurs `/`).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `sourceFolder` | string | oui | Dossier source au layout jeu. |
| `outputZip` | string | oui | Chemin du `.zip` de sortie. |

## 18. Journal de quêtes/codex (`.journal`)

Le `.journal` est un CR2W éditable via `read_game_file`/`write_game_file`, mais son JSON pèse ~70 Mo (28 000+ entrées). Ces outils le rendent navigable.

### `inspect_journal`
Résumé navigable d'un `.journal` converti en JSON : nombre total d'entrées, profondeur, répartition par `$type`, catégories de premier niveau. Évite de charger les ~70 Mo.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `jsonFile` | string | oui | JSON produit par `read_game_file` sur un `.journal`. |

### `find_journal_entry`
Localise des entrées par `id`, `type` ou `title` et renvoie le **chemin JSON exact** de chacune (ex. `Data.RootChunk.entry.Data.entries[2].Data.entries[7].Data`) — pour éditer l'entrée ciblée puis réécrire via `write_game_file`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `jsonFile` | string | oui | JSON produit par `read_game_file` sur un `.journal`. |
| `query` | string | oui | Valeur à rechercher (sous-chaîne, insensible à la casse). |
| `field` | string | non (défaut `id`) | Champ ciblé : `id` \| `type` \| `title`. |
| `maxResults` | int | non (défaut 100) | Nombre max de correspondances. |

## 19. Navigation CR2W générique, diagnostic & conflits

### `inspect_cr2w`
Résumé navigable de N'IMPORTE quel CR2W en JSON : type racine, objets par `$type`, profondeur. Pour les gros fichiers (quêtes, scènes, secteurs, UI).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `jsonFile` | string | oui | JSON produit par `read_game_file` / `cr2w_to_json`. |

### `find_in_cr2w`
Cherche dans un CR2W (JSON) les objets dont un champ correspond → **chemin JSON exact**.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `jsonFile` | string | oui | JSON produit par `read_game_file` / `cr2w_to_json`. |
| `query` | string | oui | Sous-chaîne (insensible à la casse). |
| `field` | string | non (défaut `$type`) | `$type`, un nom de propriété, ou `*` (toute valeur texte). |
| `maxResults` | int | non (défaut 100) | Max de correspondances. |

### `diagnose_logs`
Parse les 6 logs de modding (redscript/RED4ext/ArchiveXL/TweakXL/Codeware/CET/REDmod), extrait/classe les erreurs et mappe les erreurs connues → correctif.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |
| `maxPerSource` | int | non (défaut 30) | Max de lignes d'erreur par source. |

### `analyze_conflicts`
Conflits robustes (sans le verbe WolvenKit buggé) : fichiers fournis par plusieurs `.archive` (+ qui gagne) et records définis par plusieurs `.tweak`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |
| `maxResults` | int | non (défaut 200) | Max de conflits par catégorie. |

### `validate_item_mod`
Valide la chaîne de références d'un mod d'item ArchiveXL : `.yaml`(entityName)↔`.csv`, displayName↔`.json secondaryKey`, présence `.ent`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `modPath` | string | oui | Dossier du mod (.yaml/.xl/.csv/.json). |
| `deep` | bool | non (défaut false) | Convertit le `.ent` et vérifie l'appearanceName. |

## 20. Création / maintenance avancées

### `lint_tweak`
Lint sémantique TweakXL : tabs interdits, indentation, records en double, `inlineN` comme `$base`. — `tweakFile` (requis).

### `generate_manifest`
Manifeste de dépendances + `REQUIREMENTS.md`. — `modPath` (requis), `modName`/`version` (opt), `writeFile` (défaut true).

### `resolve_dynamic_appearance`
Développe un pattern d'apparence dynamique ArchiveXL (`{gender}`/`{camera}`). — `pattern` (requis), `modPath` (opt, vérif d'existence).

### `migration_check`
Le mod surcharge-t-il encore la version actuelle du jeu ? — `modArchive` + `gamePath` (requis), `maxResults` (défaut 100).

### `toggle_mods`
Active/désactive des `.archive` (réversible, vers `_disabled`) — bissection. — `gamePath` (requis), `archives` (opt, vide = lister), `enable` (défaut false).

### `export_entity` / `export_materials`
Apparence d'entité `.ent` → glTF / matériaux d'un `.mesh` → JSON+textures (`IModTools`). Voir §4. — `entFile`/`meshFile` + `outputPath` (requis), `appearance`/`gamePath` (opt). `export_entity` découvre/valide l'apparence et remonte « can not be exported » (limite WolvenKit headless).

### `list_entity_appearances`
Liste les apparences d'un `.ent` : `name` (à passer à export_entity / dans le .yaml), `appearanceName` (côté .app), `.app` référencé. — `entFile` (requis).

### `validate_appearance`
Validation profonde `.app`→`.mesh` : le `meshAppearance` de chaque composant existe-t-il dans le `.mesh` (sinon mesh invisible) ? — `appFile` (requis), `modRoot`/`gamePath` (opt, pour résoudre les meshes), `maxMeshes` (défaut 40).

---

## Prompts MCP (recettes)

Chaque prompt renvoie un texte instructif (étapes + outils à appeler), pas une exécution directe.

### `read_game_file_workflow`
Recette : localiser puis lire un fichier de jeu en JSON, en un coup.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `filePattern` | string | oui | Type ou nom partiel à chercher (ex. `player.ent`). |
| `contentFolder` | string | oui | Dossier de contenu du jeu (`archive/pc/content`). |

### `edit_tweakdb_item`
Recette : modifier les paramètres d'un item TweakDB via un `.tweak`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `tweakId` | string | oui | Identifiant TweakDB de l'item (ex. `Items.w_melee_001`). |
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

### `pack_and_install_mod`
Recette : empaqueter un dossier source en `.archive` et l'installer.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `modSourceFolder` | string | oui | Dossier source du projet de mod. |
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

### `recolor_texture`
Recette : extraire une texture, l'éditer, puis la réintégrer dans un mod.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archivePath` | string | oui | Archive contenant la texture. |
| `texturePattern` | string | oui | Motif glob de la texture (ex. `*jacket_01*.xbm`). |

### `inspect_mesh`
Recette : exporter un mesh en glTF pour l'inspecter (Blender / visualiseur), avec options d'export.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `archivePath` | string | oui | Archive contenant le mesh. |
| `meshInternalPath` | string | oui | Chemin interne du mesh. |

### `create_archivexl_item`
Recette : créer un mod d'item ArchiveXL de bout en bout, avec validation de la chaîne record → factory → localisation (`validate_item_mod`).

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `modName` | string | oui | Nom du mod/item à créer. |
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

### `diagnose_broken_mod`
Recette : diagnostiquer un mod cassé ou une install qui crashe — `mod_doctor` → `diagnose_logs` → `analyze_conflicts` → bissection `toggle_mods`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |
| `symptom` | string | oui | Symptôme observé (crash, item absent, script sans effet…). |

### `live_iteration_loop`
Recette : itérer des valeurs TweakDB À CHAUD (jeu lancé + CETBridge) via `live_tweakdb_set`, puis figer le résultat en `.tweak`.

| Paramètre | Type | Requis | Description |
|---|---|---|---|
| `tweakId` | string | oui | Identifiant TweakDB à itérer. |
| `gamePath` | string | oui | Dossier racine de Cyberpunk 2077. |

---

## Ressources MCP

Données lisibles exposées par URI.

| Ressource | URI Template | MIME | Description |
|---|---|---|---|
| Référence WolvenKit | `wolvenkit://reference` | `text/markdown` | Aide-mémoire généré par réflexion depuis les outils réels : liste complète, formats REDengine, workflow de modding. |
| Mods installés | `wolvenkit://mods/{+gamePath}` | `text/markdown` | Inventaire des mods d'une installation (archives, REDmods, tweaks, scripts), racine du jeu en chemin absolu après `wolvenkit://mods/`. |
| Contenu d'archive | `wolvenkit://archive/{+path}` | `text/plain` | Liste le contenu d'une archive `.archive` identifiée par son chemin absolu après `wolvenkit://archive/`. |
| Fichier REDengine en JSON | `wolvenkit://cr2w-json/{+path}` | `application/json` | Rend un fichier CR2W extrait (`.mesh`, `.ent`, `.app`...) sous forme JSON, identifié par son chemin absolu après `wolvenkit://cr2w-json/`. |
