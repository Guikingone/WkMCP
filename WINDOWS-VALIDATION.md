# Checklist de validation Windows — WolvenKit MCP

Le serveur expose 85 outils, 5 prompts et 3 ressources. Cette checklist les
valide sur Windows avec une vraie installation de Cyberpunk 2077.

## Résultat de la validation — 29/05/2026

Validée sur **Windows 11** avec **Cyberpunk 2077** installé (jeu de base
+ 318 mods .archive + 170 REDmods). Les **60 outils, 5 prompts et 3 ressources**
ont été exercés de bout en bout via le serveur MCP par le script
**`validate-windows.py`** (voir plus bas). Les outils renvoient un résultat
**JSON structuré** (`ok`, `status`, `summary`, `produced`, `warnings`, `errors`,
`log` tronqué en préservant tête + erreurs + queue) ; les listings d'archives
sont servis par un **cache LRU** (×6 plus rapide sur appels successifs, stats +
métriques par verbe visibles via `wolvenkit_status`, purgeables via
`clear_cache`) ; le daemon supporte le **pipelining IPC** (plusieurs requêtes
en vol).

**Bilan : 66 OK · 1 réserve · 0 partiel · 0 échec.**

- **Round 2 (29/05/2026)** : +5 outils (`export_animation`, `export_morphtarget`,
  `export_mlmask`, `extract_audio`, `generate_modproj`), `create_mod_project`
  étendu (émet un `.cpmodproj` + `source/customSounds`), `lint_script` enrichi
  (checks sémantiques). `extract_audio` a extrait **82 578 fichiers opus** d'une
  archive vocale. **19 tests unitaires xUnit** verts. Bundle **`.mcpb`** + **CI**.
- **Round 3 (29/05/2026)** : +2 outils → **60**. `loc_resolve` (LocKey → texte ;
  **70 579 entrées en_us** chargées, clé `40` → « News »), `import_audio`
  (WAV → `.opus` via `OpusTools.ImportWavs`, câblage vérifié). `export_mlmask` et
  `export_morphtarget` validés sur assets réels ; `export_animation` s'exécute mais
  une `.anims` sans rig ne produit pas de glTF (contrainte WolvenKit).
- **Round 4 (29/05/2026)** : `lint_script` repose désormais sur un **vrai parser de
  grammaire REDscript** (`RedscriptParser.cs`) — erreurs ligne:colonne, **0 faux positif
  calibré sur les 1374 `.reds` de `r6/scripts`**, détection vérifiée sur du code cassé.
  **26 tests xUnit** verts. (Le type-checking via `scc` reste hors périmètre : compiler
  tout `r6/scripts` prend ~15 min et échoue sur les deps manquantes des mods installés.)
- **Round 10 (29/05/2026)** : +2 outils → **85** — fiabilisation apparences.
  `list_entity_appearances` (15 apparences listées sur une entité NPC réelle : name +
  appearanceName + .app) et `validate_appearance` (validation profonde `.app`→`.mesh` :
  sur un `.app` de base valide → **0 erreur, 24 réf. mesh, 4 meshes résolus** = zéro faux
  positif ; détecte un `meshAppearance` absent = « mesh invisible »). `export_entity` rendu
  gracieux (découvre/valide l'apparence, remonte « can not be exported » — limite WolvenKit
  headless confirmée sur les entités NPC, non maquillée). **50 tests xUnit**.
- **Round 9 (29/05/2026)** : +7 outils → **83** — « on prend tout ». Vague 1 (MCP pur) :
  `lint_tweak`, `generate_manifest`, `resolve_dynamic_appearance`, `migration_check`
  (4 surcharges actives / 14 inertes sur un vrai mod), `toggle_mods`. Vague 2 (verbes
  daemon) : `export_materials` (**18 fichiers matériaux** produits, après fix `MaterialRepo`),
  `export_entity` (câblé sur `IModTools.ExportEntity` — expérimental : requiert une entité
  porteuse d'apparences), `validate_item_mod` étendu `deep`. **`compile_scripts` abandonné**
  (scc compile tout l'arbre `r6/scripts`, isolation impossible ; `diagnose_logs` couvre
  l'attribution). **47 tests xUnit**. **Bilan : 92 OK · 2 réserves · 0 échec.**
- **Round 8 (29/05/2026)** : +5 outils → **76** — le « Top 4 » des manques identifiés.
  `inspect_cr2w`/`find_in_cr2w` (navigation de TOUT gros CR2W — journal → 159 907 objets
  typés, 62 types), `diagnose_logs` (parse 6 logs + KB d'erreurs → correctif ; a classé
  l'échec de compilation redscript réel), `analyze_conflicts` (conflits robustes sans le
  verbe buggé — **1015 conflits d'archive + 13 records** sur l'install réelle, 1,35 s),
  `validate_item_mod` (chaîne de références ArchiveXL — détecte les 2 erreurs d'un mod cassé).
  **42 tests xUnit** verts. **Bilan : 85 OK · 2 réserves · 0 échec.**
- **Round 7 (29/05/2026)** : +2 outils → **71** — intelligence **journal** (`.journal`).
  `inspect_journal` (résumé du journal : sur `cooked_journal.journal` → **28 476 entrées,
  41 types, 9 catégories** en 0,4 s) et `find_journal_entry` (localise par id/type/titre →
  chemin JSON exact). L'édition de `.journal` était déjà possible via read/write_game_file
  (round-trip vérifié : JSON 71 Mo ↔ CR2W 4,79 Mo) ; ces outils la rendent praticable.
  **33 tests xUnit** verts. Recette ajoutée dans `docs/MODDING_RECIPES.md`.
- **Round 6 (29/05/2026)** : **audit multi-agents** (workflow : 6 dimensions + vérification
  adversariale) → correctifs appliqués : **bug HIGH TweakDB** (rechargement quand le chemin
  change — vérifié : `.bin` original + copie renvoient les bons records), **sécurité Zip-Slip**
  dans `restore_mods` (entrée hors cible bloquée + avertissement), fuite temp `read_game_file`
  (dossier déterministe), `Status()` cohérent, `ExtractAsJson` anti-collision, + optimisations
  (boolFlags static, modulo métriques, mapping framework compilé une fois, tokens pré-dimensionnés,
  `package_mod` exclut le bruit, `find_references` en lecture paresseuse, parser : segment de type
  mot-clé après `.`). 4 docs ajoutées dans `docs/`. **0 régression** (78 OK / 2 réserves / 0 échec,
  29 tests xUnit, parser 0 faux positif sur 1374 fichiers).
- **Round 5 (29/05/2026)** : +9 **outils de workflow** → **69** (`ModdingTools.cs`).
  Intelligence deps/santé (`analyze_dependencies`, `check_requirements` → 10/10
  frameworks, `mod_doctor`), stack ArchiveXL (`validate_xl`, `scaffold_archivexl`),
  navigation/diff (`find_references`, `diff_mod_vs_base` — bruit `$.Header` filtré),
  scaffolding/packaging (`scaffold_mod`, `package_mod`). **29 tests xUnit** verts.
  **Bilan : 78 OK · 2 réserves · 0 échec.**
- `build_project` — *désormais OK* : compile le `.cpmodproj` émis par
  `create_mod_project` et produit `packed/archive/pc/mod/<mod>.archive`.
- `detect_conflicts` — *warn* : outil correctement câblé, mais le verbe
  `conflicts` de WolvenKit.CLI 8.18.0 lève une `ArgumentNullException` sur un
  install réel : **bug amont**, reproductible tel quel avec `cp77tools`.

### Tableau récapitulatif des extensions livrées

**Round 1** (post-validation 19/05/2026)

| Catégorie | Outils ajoutés |
|---|---|
| Diagnostic mod | `lint_mod` |
| Inspection | `diff_archives` |
| TweakDB structurée | `read_tweak`, `write_tweak`, `validate_tweak`, `install_tweak` |
| REDmod packaging | `create_redmod_project`, `pack_redmod`, `install_redmod` |
| Prompts MCP | 5 recettes (`read_game_file_workflow`, etc.) |
| Optimisations | Cache LRU · pipelining daemon · parallel `wwise_export` · cap `tweakdb_query` · troncature log contextuelle · flags `uncook` avancés |

**Round 2** (20/05/2026)

| Catégorie | Outils ajoutés |
|---|---|
| Inspection rapide | `inspect_mesh`, `inspect_texture`, `describe_tweak_record` |
| Scaffolds | `generate_tweak_template` (patterns `override_field`, `new_record`, `boost_stat`) |
| Scripts `.reds` | `read_script`, `lint_script` (analyse textuelle) |
| Sécurité | `backup_mods`, `restore_mods` (ZIP horodaté) |
| Observabilité | Stats du cache LRU dans `wolvenkit_status` · paramètre `verbose` sur les outils générant de gros logs |

**Round 3** (20/05/2026)

| Catégorie | Outils ajoutés |
|---|---|
| Uninstall + deploy | `uninstall_mod`, `uninstall_redmod`, `uninstall_tweak`, `deploy_redmod` |
| Itération en jeu | `launch_game`, `tail_game_logs` |
| Intelligence mod | `mod_summary` (archive + REDmod), `dump_records` (JSONL/CSV par type) |
| Scaffolds REDscript | `generate_redscript_template` (add_method, wrap_method, replace_method, add_field, new_class) |
| Localisation UI | `extract_localization` (depuis TweakDB), `build_localization` (vers `.tweak`) |
| Maintenance | `clear_cache` (archives / metrics / all) · métriques par verbe (p50/p95) dans `wolvenkit_status` |

### Bugs trouvés et corrigés pendant cette validation

| Symptôme | Correctif |
|---|---|
| `archive --list` : listing écrit par `Console.WriteLine`, redirigé vers stderr puis perdu → `archive_info`, `find_in_archives` et la ressource d'archive renvoyaient du vide | Le daemon capture `Console.Out` dans le tampon du logger (`WolvenKitDaemon/Program.cs`) |
| `kraken.dll` absent de la sortie de build → daemon incapable de démarrer | Cible MSBuild `DeployNativeWindows` + références explicites (`WolvenKitDaemon.csproj`) |
| `DirectXTexNet.dll` non déployé puis non résolu (absent du `deps.json`) → `uncook` de textures en échec | Déploiement via le csproj + résolveur `AssemblyLoadContext` dans le daemon |
| `detect_conflicts` envoyait `archive/pc/mod` ; le verbe `conflicts` attend la racine du jeu | Paramètre renommé en `gamePath` (`WolvenKitTools.cs`) |
| `wwise_export` passait un dossier ; le verbe `wwise` attend un fichier `.ogg` de sortie | Conversion `.wem` → `.ogg` fichier par fichier, sortie nommée (`WolvenKitTools.cs`) |
| `oodle_decompress` : WolvenKit écrit la sortie dans `<chemin>.bin` | L'outil replace le résultat au chemin demandé (`WolvenKitTools.cs`) |
| Scripts de test : chemins POSIX `/tmp` codés en dur ; `print()` plantait sur la console Windows (cp1252) | Chemins via `tempfile.gettempdir()`, sorties forcées en UTF-8 |

### Rejouer la validation

```sh
python validate-windows.py "C:\chemin\vers\Cyberpunk 2077"
```

`validate-windows.py` pilote le serveur MCP (JSON-RPC stdio) et exerce les 53
outils, 5 prompts et 3 ressources sur de vrais assets, puis affiche un tableau
de résultats. Inclut aussi un test de **pipelining IPC** (4 requêtes en vol),
un test du **cache LRU** (find_in_archives chaud vs froid), backup/restore +
uninstall sur faux dossier de jeu, lint d'un `.reds` cassé, 5 patterns de
`generate_redscript_template` re-lintés, `dump_records` sur 1500+ armes,
extract/build_localization avec roundtrip JSON → `.tweak`, et `clear_cache`
qui remet le cache à zéro.

## 0. Déjà vérifié (inutile de re-tester le mécanisme)

`compute_hash` (bit-exact) · `oodle_compress`/`oodle_decompress` (aller-retour
byte-exact) · `create_mod_project` · handshake MCP · daemon (latence ~ms) ·
`pack_archive` (mécanisme). **Tout le reste est à valider ci-dessous.**

## 1. Installation (Windows)

- [ ] .NET 8+ SDK installé — https://dotnet.microsoft.com/download
- [ ] `dotnet tool install -g WolvenKit.CLI`
- [ ] Projet `wolvenkit-mcp` récupéré sur la machine
- [ ] `dotnet build src\WolvenKitDaemon`
- [ ] `dotnet build src\WolvenKitMcp`
- [ ] Le dossier `native/` est **inutile sur Windows** (c'est le correctif libkraken macOS) — ignorer
- [ ] Dossier d'installation de Cyberpunk 2077 repéré (ex. `...\steamapps\common\Cyberpunk 2077`)

## 2. Smoke test (scripts, sans le jeu)

- [ ] `python test-daemon.py` → « Daemon prêt », `hash` en ~1 ms, `pack` OK
  - ⚠️ Si le daemon **échoue au démarrage** sur une erreur `kraken` : copier `kraken.dll`
    depuis `%USERPROFILE%\.dotnet\tools\.store\wolvenkit.cli\<version>\...\tools\net8.0\any\kraken.dll`
    à côté de `WolvenKitDaemon.dll` (le package NuGet ne le fournit pas toujours)
- [ ] `python test-mcp-server.py` → « 21 outils », handshake OK

## 3. Branchement sur Claude

- [ ] Config Claude Desktop ou `claude mcp add` (cf. `README.md` § Installation sur Windows)
- [ ] **Nouvelle session** → 21 outils + 3 ressources visibles
- [ ] 1ᵉʳ appel ~7 s (démarrage du daemon), appels suivants quasi-instantanés

## 4. Inspection — sur de vraies archives (`<jeu>\archive\pc\content\`)

- [ ] `archive_info` sur une `.archive` du jeu → nombre de fichiers cohérent (`list=true` pour le contenu)
- [ ] `find_in_archives` sur `archive\pc\content` + motif `*.ent` → fichiers trouvés + leur archive
- [ ] `extract_files` — extraire quelques fichiers d'une archive → présents sur le disque
- [ ] `uncook` — une archive avec meshes/textures → `.glb`/glTF + images générés
- [ ] **Aller-retour hash** : prendre un chemin affiché par `find_in_archives`, faire
      `compute_hash` dessus, puis `resolve_hash` du résultat → doit **redonner le chemin**

## 5. Conversion

- [ ] `cr2w_to_json` sur un `.ent`/`.app`/`.mesh` extrait → JSON lisible
- [ ] éditer le JSON, `json_to_cr2w` → CR2W binaire régénéré
- [ ] `export_files` sur un fichier REDengine → format raw (glTF / image)

## 6. Création de mod (écriture)

- [ ] `create_mod_project` → structure de dossiers créée
- [ ] déposer de **vrais fichiers cuits** dans `source\archive`, puis `pack_archive`
      → `.archive` produite **sans** l'avertissement « Unknown file extension »
- [ ] `import_raw` sur une texture / un glTF → fichier REDengine
- [ ] `build_project` sur un dossier contenant un `.cpmodproj` (créé via l'app WolvenKit)

## 7. Mods installés

- [ ] `list_installed_mods` sur le dossier du jeu → `.archive` de `archive\pc\mod` + REDmods de `mods\`
- [ ] `detect_conflicts` sur `archive\pc\mod` → JSON des conflits

## 8. TweakDB

- [ ] Localiser le `tweakdb.bin` du jeu (typiquement `<jeu>\r6\cache\tweakdb.bin`)
- [ ] `tweakdb_query` sur ce fichier + filtre `Items.` → records / flats listés
- [ ] `tweakdb_resolve` sur un hash d'identifiant TweakDB → nom

## 9. Audio — LE point spécifique Windows

- [ ] `extract_files` d'un `.wem` depuis une archive audio
- [ ] `wwise_export` sur ce `.wem` → `.ogg` jouable
      *(les binaires audio natifs n'existent que sous Windows — c'est ici qu'on les valide)*

## 10. Ressources MCP

- [ ] lire `wolvenkit://reference` → l'aide-mémoire
- [ ] lire `wolvenkit://archive/<chemin d'une .archive>` → listing du contenu
- [ ] lire `wolvenkit://cr2w-json/<chemin d'un fichier extrait>` → JSON

## Points de vigilance

- Avertissement « Oodle couldn't be loaded. Using Kraken... » : **sans gravité** — le
  codec Kraken open-source prend le relais.
- `pack_archive` ignore les fichiers dont l'extension n'est pas un type REDengine — normal.
- Chemins Windows : passer des chemins **absolus** ; attention aux espaces (`Program Files`).
- Si un outil échoue toujours : vérifier que le daemon démarre. Sinon le serveur retombe
  sur le sous-processus `cp77tools` (lent ~6 s mais fonctionnel) ; si même ça échoue,
  le problème est dans l'installation de cp77tools.
- `resolve_hash`, `tweakdb_resolve`, `tweakdb_query` ne fonctionnent **que via le daemon**
  (pas de repli sous-processus — ce ne sont pas des verbes de cp77tools).

## Tableau de résultats — validation du 20/05/2026

| Outil / ressource | OK ? | Détail |
|---|---|---|
| `handshake + tools/list` | ✅ | 85 outils exposés. |
| `prompts/list` | ✅ | 5 prompts MCP exposés (recettes). |
| `archive_info` | ✅ | Infos + listing filtré (cache LRU). |
| `find_in_archives` (froid) | ✅ | ~2 s sur 33 archives. |
| `find_in_archives` (cache chaud) | ✅ | **×6 plus rapide** (~350 ms) ; cache hits = 33/33. |
| `diff_archives` | ✅ | +18 / -53 entre archive de base et un mod réel. |
| `extract_files` | ✅ | 15 `.streamingsector` extraits de `basegame_2_mainmenu`. |
| `uncook` | ✅ | Mesh → `.glb`, texture → `.png` (flags `meshExportType` etc. exposés). |
| `resolve_hash` | ✅ | Aller-retour `compute_hash` ↔ `resolve_hash` exact. |
| `cr2w_to_json` | ✅ | `.streamingsector` → JSON éditable. |
| `json_to_cr2w` | ✅ | JSON → CR2W binaire régénéré. |
| `export_files` | ✅ | Mesh extrait → glTF (`.glb`). |
| `read_game_file` | ✅ | Fichier de jeu lu en JSON en un seul appel. |
| `write_game_file` | ✅ | JSON édité → CR2W replacé au bon chemin interne. |
| `pack_archive` | ✅ | `.archive` produite, sans avertissement « Unknown file extension ». |
| `lint_mod` | ✅ | 18 fichiers · 0 extension inconnue · 0 conflit (vs autres mods installés). |
| `install_mod` | ✅ | Archive de mod copiée dans `archive/pc/mod` (faux jeu de test). |
| `create_mod_project` | ✅ | Structure `source/archive + packed` créée. |
| `create_redmod_project` | ✅ | `info.json` + `archives/` + `tweaks/` + `scripts/` + `customSounds/`. |
| `pack_redmod` | ✅ | `<nom>.zip` produit (~760 o pour structure vide). |
| `install_redmod` | ✅ | REDmod copié dans `mods/<nom>/` (faux jeu de test). |
| `build_project` | ✅ | Compile le `.cpmodproj` (émis par `create_mod_project`) → `packed/archive/pc/mod/<mod>.archive`. |
| `list_installed_mods` | ✅ | 318 mods `.archive` + 170 REDmods listés. |
| `detect_conflicts` | ⚠️ | *Warn* : verbe `conflicts` de WolvenKit.CLI 8.18.0 plante — bug amont. |
| `tweakdb_query` | ✅ | `tweakdb.bin` chargée, records/flats listés (cap 100 + `truncated`). |
| `tweakdb_resolve` | ✅ | `176750402310` → `Items.Preset_Achilles_Collectible_inline0`. |
| `read_tweak` | ✅ | `.tweak` (YAML TweakXL) → JSON éditable. |
| `write_tweak` | ✅ | JSON → `.tweak` (aller-retour). |
| `validate_tweak` | ✅ | Validation contre `tweakdb.bin` (exit=0 pour clés connues). |
| `install_tweak` | ✅ | `.tweak` copié dans `r6/tweaks/` (faux jeu de test). |
| `inspect_mesh` | ✅ | Résumé : LODs, sous-meshes, matériaux, bones (sans uncook complet). |
| `inspect_texture` | ✅ | Métadonnées xbm : résolution, format, mipmaps (sans conversion). |
| `describe_tweak_record` | ✅ | Tous les flats d'un record TweakDB avec types et valeurs. |
| `generate_tweak_template` | ✅ | 3 patterns validés (`override_field`, `new_record`, `boost_stat`). |
| `read_script` | ✅ | Lit `.reds` + extrait declarations (module, func, class, @addMethod). |
| `lint_script` | ✅ | Détecte accolades non équilibrées (testé sur fichier sain + cassé). |
| `backup_mods` / `restore_mods` | ✅ | ZIP horodaté → restore dans faux jeu vide ; aller-retour OK. |
| `uninstall_mod` / `uninstall_redmod` / `uninstall_tweak` | ✅ | Suppression vérifiée sur faux jeu (garde-fou sandbox actif). |
| `deploy_redmod` | ✅ | `redMod.exe deploy` exécuté sur vraie install (exit=0). |
| `launch_game` (faux jeu) | ✅ | Refuse propre quand l'exe est absent — comportement attendu. |
| `tail_game_logs` | ✅ | 10 lignes lues depuis logs factices, filtre / catégorie OK. |
| `mod_summary` (archive) | ✅ | Catégorisation par extension sur un mod .archive réel. |
| `mod_summary` (redmod) | ✅ | Lit info.json + énumère archives/scripts/tweaks/customSounds. |
| `dump_records` | ✅ | 1585 armes (`gamedataWeaponItem_Record`) exportées en JSONL (~12 MB). |
| `generate_redscript_template` | ✅ | 5 patterns générés, chacun re-passe `lint_script` sans erreur. |
| `extract_localization` | ✅ | Champs displayName/localizedDescription extraits depuis TweakDB en JSON (filtre supporté). |
| `build_localization` | ✅ | JSON `{recordId: {field: value}}` → `.tweak` valide. |
| `clear_cache` (archives) | ✅ | Cache vidé ; `entries=0` après. |
| `pipelining IPC` | ✅ | 4 `compute_hash` concurrents → 1 ms total (réponses out-of-order tolérées). |
| `oodle_compress` / `oodle_decompress` | ✅ | Aller-retour byte-exact (64 500 o ↔ 88 o compressés). |
| `wwise_export` | ✅ | WEM → OGG (parallel jusqu'à 4). |
| ressource `reference` | ✅ | Aide-mémoire à jour avec nouveaux outils + prompts. |
| ressource `archive/{+path}` | ✅ | Listing du contenu d'archive. |
| ressource `cr2w-json/{+path}` | ✅ | Fichier CR2W rendu en JSON. |
