# WolvenKit MCP — serveur MCP pour le modding de Cyberpunk 2077

Prototype de serveur **MCP (Model Context Protocol)** exposant le CLI de modding
WolvenKit (`cp77tools`) comme outils utilisables par Claude.

**État : prototype fonctionnel**, lecture + écriture, validé de bout en bout sur
**Windows 11** (avec Cyberpunk 2077 installé) et sur macOS Apple Silicon.

## Pourquoi le projet a deux parties

WolvenKit ne fonctionne pas tel quel sur macOS : le package NuGet `WolvenKit.CLI`
livre une bibliothèque native Oodle (`libkraken`) cassée sur Apple Silicon —
x86_64 uniquement, et symboles C++ manglés. Il a donc fallu :

1. **`native/`** — reconstruire `libkraken.dylib` en arm64 natif, compression +
   décompression (cf. `native/README.md`) ;
2. **`src/`** — le serveur MCP lui-même, en C# / .NET 8.

## Architecture

Serveur MCP **C# / .NET 8** (SDK officiel `ModelContextProtocol` 1.3.0). Les
appels d'outils passent par un **daemon WolvenKit persistant** : le coûteux
chargement de la base de hashes (~6 s) n'est payé qu'une fois, au démarrage du
daemon ; les appels suivants ne coûtent que quelques millisecondes.

```
Claude ─MCP/JSON-RPC─▶ WolvenKitMcp ─IPC stdio─▶ WolvenKitDaemon ─▶ libs WolvenKit + libkraken
                                    └─repli───▶ cp77tools (sous-processus) si daemon indisponible
```

Le daemon — qui lie les bibliothèques GPL-3.0 de WolvenKit — est un processus
**séparé** : le serveur MCP ne fait que dialoguer avec lui par IPC, il reste donc
hors du périmètre du copyleft. Si le daemon est indisponible, chaque appel se
replie sur un sous-processus `cp77tools` (fonctionnel, mais ~6 s/appel).

## Documentation

Documentation détaillée dans [`docs/`](docs/) :

- **[docs/USER_GUIDE.md](docs/USER_GUIDE.md)** — guide du moddeur : installation, branchement sur Claude, et workflows pas-à-pas (lire un fichier, éditer un tweak, créer/empaqueter/installer un mod, vérifier les dépendances, packager).
- **[docs/TOOLS.md](docs/TOOLS.md)** — référence exhaustive des 69 outils + 5 prompts + 3 ressources (paramètres compris).
- **[docs/MODDING_RECIPES.md](docs/MODDING_RECIPES.md)** — recettes copier-coller par type de mod (tweak, redscript, ArchiveXL, REDmod, localisation, texture, analyse).
- **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** — pour contribuer : IPC, cache, parser, et comment ajouter un outil MCP ou un verbe daemon.
- **[docs/LIVE_BRIDGE.md](docs/LIVE_BRIDGE.md)** — pont **live in-game** : 35 outils `live_*` pour piloter un jeu **en cours d'exécution** (exécution Lua, état, spawn, téléportation, météo, TweakDB en mémoire vive, observation) via le mod CETBridge / Cyber Engine Tweaks. Optionnel, prérequis à part.
- **[docs/HTTP_TRANSPORT.md](docs/HTTP_TRANSPORT.md)** — **accès distant** : le serveur peut tourner en **HTTP/Streamable** (au lieu de stdio) via `WOLVENKIT_MCP_TRANSPORT=http`. Sécurisé par défaut (bind loopback + bearer token + fail-closed). Opt-in.

## Prérequis

- macOS Apple Silicon (arm64)
- .NET 8 SDK — `brew install dotnet@8`
- WolvenKit CLI — `dotnet tool install -g WolvenKit.CLI`
- `libkraken.dylib` arm64 déployée (étape 1 ci-dessous)

## Installation

### 1. Reconstruire et déployer libkraken (une fois)

```sh
cd native
./build-libkraken.sh
PKG=~/.dotnet/tools/.store/wolvenkit.cli/8.18.0/wolvenkit.cli/8.18.0/tools/net8.0/any
mkdir -p "$PKG/runtimes/osx-arm64/native"
cp build/libkraken.dylib "$PKG/runtimes/osx-arm64/native/"
```

### 2. Compiler le daemon et le serveur MCP

```sh
dotnet build src/WolvenKitDaemon   # son build y déploie libkraken.dylib
dotnet build src/WolvenKitMcp
```

### 3. Tester

```sh
python3 test-daemon.py       # daemon seul — latence par requête
python3 test-mcp-server.py   # serveur MCP de bout en bout
```

## Brancher sur un client

### Claude Desktop

`~/Library/Application Support/Claude/claude_desktop_config.json` :

```json
{
  "mcpServers": {
    "wolvenkit": {
      "command": "/opt/homebrew/opt/dotnet@8/libexec/dotnet",
      "args": ["ABSOLUTE/PATH/wolvenkit-mcp/src/WolvenKitMcp/bin/Debug/net8.0/WolvenKitMcp.dll"],
      "env": { "DOTNET_ROOT": "/opt/homebrew/opt/dotnet@8/libexec" }
    }
  }
}
```

### Claude Code

```sh
claude mcp add wolvenkit \
  -e DOTNET_ROOT=/opt/homebrew/opt/dotnet@8/libexec \
  -- /opt/homebrew/opt/dotnet@8/libexec/dotnet \
     ABSOLUTE/PATH/wolvenkit-mcp/src/WolvenKitMcp/bin/Debug/net8.0/WolvenKitMcp.dll
```

## Installation sur Windows

Sur Windows, `cp77tools` fonctionne nativement — le dossier `native/`
(reconstruction de libkraken pour macOS) est inutile et peut être ignoré.

1. Installer le SDK .NET 8 ou supérieur — https://dotnet.microsoft.com/download
2. Installer le CLI WolvenKit : `dotnet tool install -g WolvenKit.CLI`
3. Compiler le daemon **puis** le serveur :
   `dotnet build src\WolvenKitDaemon` puis `dotnet build src\WolvenKitMcp`
   (le build du daemon déploie automatiquement `kraken.dll` et `DirectXTexNet.dll`)
4. Brancher sur Claude Desktop (`%APPDATA%\Claude\claude_desktop_config.json`) :

   ```json
   {
     "mcpServers": {
       "wolvenkit": {
         "command": "dotnet",
         "args": ["C:\\chemin\\vers\\wolvenkit-mcp\\src\\WolvenKitMcp\\bin\\Debug\\net8.0\\WolvenKitMcp.dll"]
       }
     }
   }
   ```

   ou Claude Code : `claude mcp add wolvenkit -s user -- dotnet "C:\chemin\...\WolvenKitMcp.dll"`

Aucune variable `DOTNET_ROOT` n'est nécessaire sur Windows. `cp77tools` y gère
aussi les textures et l'audio (binaires natifs Windows présents).

## Outils exposés (85)

Chaque outil renvoie un résultat **JSON structuré** (`ok`, `status`, `summary`,
`produced`, `warnings`, `errors`, `log`) — fiable à analyser pour un agent. Le
log volumineux est tronqué en préservant tête + erreurs + queue.

| Outil | Type | Rôle |
|---|---|---|
| `wolvenkit_status` | diagnostic | Disponibilité et version de cp77tools + **stats du cache LRU** (hits/misses) + **métriques par verbe** (p50/p95) |
| `compute_hash` | diagnostic | Hash FNV1a64 de chaînes (chemins de fichiers) |
| `resolve_hash` | diagnostic | Recherche inverse : hash FNV1a64 → chemin de fichier de jeu |
| `archive_info` | lecture | Informations / listing d'une archive `.archive` (cache LRU) |
| `find_in_archives` | lecture | Recherche un fichier à travers toutes les archives d'un dossier (cache LRU, ×6 plus rapide sur appels successifs) |
| `diff_archives` | lecture | Compare deux archives `.archive` (ajouts / suppressions internes) |
| `extract_files` | lecture | Extraction de fichiers d'une archive (glob/regex) |
| `uncook` | lecture | Extraction + conversion en une passe (mesh → glTF, textures → image). Flags : `meshExportType`, `meshExporterType`, `meshExportLodFilter` |
| `export_animation` | export | Animation `.anims` extraite → glTF binaire (`.glb`) — outil dédié explicite |
| `export_morphtarget` | export | Morphtarget `.morphtarget` (blendshapes) → glTF binaire (`.glb`) |
| `export_mlmask` | export | Masque multilayer `.mlmask` → images (une par couche), format réglable (`textureFormat`) |
| `export_entity` | export | ⚠ exp. — apparence d'entité `.ent` → glTF (`IModTools.ExportEntity` ; nécessite une entité porteuse d'apparences + le nom d'apparence) |
| `export_materials` | export | Matériaux d'un `.mesh` → JSON + textures (`IModTools.ExportMaterials`, `gamePath` pour résoudre les matériaux de base) |
| `extract_audio` | audio | Extrait l'audio voix-off (opus) d'une archive vocale ; tout ou `opusHashes` ciblés |
| `import_audio` | audio | ⚠ exp. — WAV (nommés par hash opus) → `.opus` repacké dans un mod (`opusenc` embarqué) |
| `loc_resolve` | localisation | ⚠ exp. — LocKey (hash ou clé) → texte localisé (variantes M/F) via on-screens du jeu |
| `detect_conflicts` | lecture | Conflits entre mods installés (sortie JSON structurée) |
| `cr2w_to_json` | conversion | REDengine CR2W → JSON éditable |
| `json_to_cr2w` | conversion | JSON → CR2W |
| `export_files` | conversion | Fichiers REDengine extraits → formats raw |
| `read_game_file` | lecture | Lit un fichier de jeu en JSON en un seul appel (extract + convert) |
| `write_game_file` | écriture | Écrit un fichier de jeu édité (JSON → CR2W placé pour `pack_archive`) |
| `wwise_export` | audio | Audio Wwise WEM → OGG (Windows). Conversions en parallèle (≤ 4) |
| `oodle_compress` | utilitaire | Compression Oodle Kraken d'un fichier |
| `oodle_decompress` | utilitaire | Décompression Oodle Kraken d'un fichier |
| `pack_archive` | écriture | Empaquette un dossier en archive `.archive` |
| `import_raw` | écriture | Importe des fichiers raw en REDengine CR2W |
| `build_project` | écriture | Compile un projet WolvenKit `.cpmodproj` → `packed/archive/pc/mod/<mod>.archive` (chaîne avec `create_mod_project` / `generate_modproj`) |
| `lint_mod` | écriture | Lint pré-install : extensions non-REDengine, conflits avec mods installés |
| `install_mod` | écriture | Installe une archive de mod dans `archive/pc/mod` du jeu |
| `create_mod_project` | workflow | Crée la structure d'un projet de mod (`source/{archive,raw,resources,customSounds}`, `packed`) **+ un `.cpmodproj`** directement compilable par `build_project` |
| `generate_modproj` | workflow | Génère un `.cpmodproj` (XML `<CP77Mod>`) dans un dossier de projet existant — rend compilable un projet qui n'en a pas |
| `create_redmod_project` | redmod | Crée un projet REDmod (`mods/<nom>/info.json` + sous-dossiers) |
| `pack_redmod` | redmod | Empaquette un REDmod en `.zip` pour distribution |
| `install_redmod` | redmod | Installe un REDmod dans `<jeu>/mods/<nom>/` |
| `list_installed_mods` | workflow | Liste les mods installés d'un dossier de jeu (archive + REDmod) |
| `read_tweak` | tweakdb | Lit un fichier `.tweak` (TweakXL — YAML) en JSON éditable |
| `write_tweak` | tweakdb | Reconvertit un JSON édité en `.tweak` (YAML) |
| `validate_tweak` | tweakdb | Valide un `.tweak` contre `tweakdb.bin` (clés inconnues détectées) |
| `install_tweak` | tweakdb | Copie un `.tweak` dans `<jeu>/r6/tweaks/` |
| `tweakdb_resolve` | tweakdb | Recherche inverse : hash d'identifiant TweakDB → nom |
| `tweakdb_query` | tweakdb | Charge une `tweakdb.bin` et liste records / flats filtrés (cap 100 + champ `truncated`) |
| `describe_tweak_record` | tweakdb | Pour un record TweakDB, liste tous ses flats avec types et valeurs |
| `generate_tweak_template` | tweakdb | Scaffolds `.tweak` (patterns : `override_field`, `new_record`, `boost_stat`) |
| `inspect_mesh` | inspection | Résumé d'un `.mesh` (LODs, sous-meshes, matériaux, bones) sans uncook complet |
| `inspect_texture` | inspection | Métadonnées d'un `.xbm` (résolution, format, mipmaps) sans conversion |
| `read_script` | scripts | Lit un fichier `.reds` / `.script` + extrait sa structure (func, class, @addMethod...) |
| `lint_script` | scripts | **Vrai parser de grammaire REDscript** (tokenizer + descente récursive) : erreurs de syntaxe avec ligne:colonne (signatures/types/génériques, appariement `(){}[]`, chaînes) **+ checks sémantiques** (annotations bien ciblées, `@wrapMethod`→`wrappedMethod()`, doublons). Calibré à **0 faux positif** sur 1374 `.reds` réels |
| `backup_mods` | sécurité | Snapshot ZIP horodaté de `archive/pc/mod` + `mods/` + `r6/tweaks/` |
| `restore_mods` | sécurité | Restaure un backup ZIP (modes `merge` / `replace`) |
| `uninstall_mod` | uninstall | Retire une `.archive` de `archive/pc/mod/` (garde-fou sandbox) |
| `uninstall_redmod` | uninstall | Supprime récursivement `mods/<nom>/` |
| `uninstall_tweak` | uninstall | Supprime un `.tweak` de `r6/tweaks/` |
| `deploy_redmod` | redmod | Wrap de `redMod.exe deploy` (compile scripts + applique tweaks) |
| `launch_game` | in-game | ⚠ Lance `Cyberpunk2077.exe` (avec `deploy_redmod` préalable optionnel) |
| `tail_game_logs` | in-game | Tail des logs `r6/logs/*.log` + `tools/redmod/logs/` (game / redmod / redscript / all) |
| `mod_summary` | intelligence | Synthèse compacte d'un mod : .archive (par extension) ou REDmod (info.json + tweaks + scripts) |
| `dump_records` | intelligence | Exporte tous les records TweakDB d'un type en JSONL / CSV (ex. toutes les armes) |
| `generate_redscript_template` | scaffolds | Scaffolds `.reds` (add_method, wrap_method, replace_method, add_field, new_class) |
| `extract_localization` | localisation | Extrait depuis TweakDB tous les champs traduisibles (displayName, etc.) en JSON |
| `build_localization` | localisation | Construit un `.tweak` de traduction depuis un JSON `{recordId: {field: value}}` |
| `clear_cache` | maintenance | Vide manuellement le cache LRU ou les métriques (`scope` ∈ archives / metrics / all) |

### Outils de workflow haut-niveau (23)

Composent les primitives ci-dessus + la connaissance de l'écosystème pour simplifier
la **création / évolution / maintenance** des mods.

| Outil | Type | Rôle |
|---|---|---|
| `analyze_dependencies` | maintenance | Déduit les frameworks requis d'un mod (redscript, RED4ext, ArchiveXL, TweakXL, Codeware, Audioware, Mod Settings, CET…) via imports/.xl/.tweak/types, et marque installé/manquant si `gamePath` fourni |
| `check_requirements` | maintenance | Inventaire des frameworks de modding **installés** (+ version) dans une install |
| `mod_doctor` | maintenance | Diagnostic santé en un appel : frameworks installés/manquants, dépendances requises par le contenu présent mais absentes, conflits, inventaire + recommandations |
| `validate_xl` | archivexl | Valide un fichier ArchiveXL `.xl` (YAML bien formé + sections reconnues) |
| `scaffold_archivexl` | archivexl | Génère un `.xl` de départ commenté (factory / customSounds / localization / resource) |
| `find_references` | évolution | Cherche toutes les références (TweakDBID / chemin / LocKey / nom) dans les sources d'un mod (.reds/.tweak/.yaml/.xl/.lua/.json/.csv) → fichier:ligne |
| `diff_mod_vs_base` | évolution | Diff sémantique d'un fichier surchargé vs sa version de base (ajouts/suppressions/modifs, bruit `$.Header` filtré) |
| `scaffold_mod` | création | Crée en 1 appel un mod fonctionnel (archive / redscript / tweak / redmod) + `MOD_MANIFEST.json` (type, deps déclarées) |
| `package_mod` | distribution | Empaquette un layout jeu (`archive/`, `r6/`, `mods/`…) en `.zip` distribuable (séparateurs `/` conformes) |
| `inspect_journal` | journal | Résumé navigable d'un `.journal` (28 000+ entrées, ~70 Mo) : total, répartition par `$type`, catégories de 1er niveau — sans tout charger |
| `find_journal_entry` | journal | Localise une entrée du journal par id / type / titre → **chemin JSON exact** pour l'éditer puis réinjecter via `write_game_file` |
| `inspect_cr2w` | navigation | Résumé navigable de N'IMPORTE quel gros CR2W (quête/scène/secteur/UI) : type racine, objets par `$type`, profondeur — généralise `inspect_journal` |
| `find_in_cr2w` | navigation | Cherche dans un CR2W par `$type` / champ / `*` → **chemin JSON exact** du nœud (édition ciblée puis `write_game_file`) |
| `diagnose_logs` | debug | Parse les 6 logs (redscript/RED4ext/ArchiveXL/TweakXL/Codeware/CET/REDmod), extrait/classe les erreurs et mappe les erreurs connues → correctif |
| `analyze_conflicts` | maintenance | Conflits **robustes** (sans le verbe WolvenKit buggé) : fichiers fournis par plusieurs `.archive` (+ qui gagne) et records définis par plusieurs `.tweak` |
| `validate_item_mod` | création | Valide la chaîne de références d'un item ArchiveXL (`.yaml` entityName ↔ `.csv`, displayName ↔ `.json secondaryKey`, présence `.ent` ; `deep` vérifie l'appearanceName dans le `.ent`) — tue la cause n°1 d'échec silencieux |
| `lint_tweak` | création | Lint sémantique TweakXL : TABS interdits, indentation, records en double, `inlineN` utilisé comme `$base` (casse aux MAJ) |
| `generate_manifest` | maintenance | Manifeste de dépendances + `REQUIREMENTS.md` (façon Nexus) depuis la détection de frameworks |
| `resolve_dynamic_appearance` | création | Développe un pattern d'apparence dynamique ArchiveXL (`{gender}`/`{camera}`) en chemins concrets + vérif d'existence |
| `migration_check` | maintenance | Un mod `.archive` est-il encore aligné sur la version du jeu ? (surcharges actives vs devenues inertes après une MAJ) |
| `toggle_mods` | maintenance | Active/désactive des `.archive` (déplacement réversible vers `_disabled`) — primitive de bissection de conflits |
| `list_entity_appearances` | création | Liste les apparences d'une entité `.ent` (`name` + `appearanceName` + `.app`) — pour savoir ce qu'elle expose avant d'éditer/exporter |
| `validate_appearance` | création | **Validation profonde** `.app`→`.mesh` : le `meshAppearance` référencé existe-t-il dans le `.mesh` ? (sinon mesh invisible) — résout les meshes mod ou base (`gamePath`) |

### Prompts MCP (5)

Recettes prêtes à l'emploi qu'un agent peut invoquer pour démarrer un workflow.

| Prompt | Rôle |
|---|---|
| `read_game_file_workflow` | Localiser et lire un fichier de jeu en un appel |
| `edit_tweakdb_item` | Modifier un item TweakDB via `.tweak` (TweakXL) |
| `pack_and_install_mod` | Empaqueter et installer un mod `.archive` |
| `recolor_texture` | Extraire / éditer / réimporter une texture |
| `inspect_mesh` | Exporter un mesh en glTF pour inspection |

## Ressources MCP (3)

En plus des outils, le serveur expose des **resources** — données lisibles
adressées par URI, que le client peut consulter ou attacher au contexte.

| URI | Type | Contenu |
|---|---|---|
| `wolvenkit://reference` | directe | Aide-mémoire : commandes, formats REDengine, workflow de modding |
| `wolvenkit://archive/{+path}` | template | Listing du contenu de l'archive `.archive` au chemin donné |
| `wolvenkit://cr2w-json/{+path}` | template | Fichier REDengine CR2W rendu en JSON |

## Configuration (variables d'environnement)

| Variable | Défaut | Rôle |
|---|---|---|
| `WOLVENKIT_DAEMON` | projet frère | Chemin de `WolvenKitDaemon.dll` (le chemin rapide) |
| `WOLVENKIT_CP77TOOLS` | `~/.dotnet/tools/cp77tools[.exe]` | Chemin de cp77tools (repli sous-processus) |
| `DOTNET_ROOT` | *auto-détecté* | Racine du runtime .NET — rarement à définir |
| `WOLVENKIT_CLI_TIMEOUT_SECONDS` | `300` | Délai maximal d'une commande |

## État de validation

Validé de bout en bout sur **Windows 11 avec Cyberpunk 2077 installé** : les
**69 outils, 5 prompts et 3 ressources** ont été exercés sur de vrais assets du
jeu via le serveur MCP (script `validate-windows.py`). Bilan
**78 OK · 2 réserves · 0 échec** — détail et bugs corrigés dans
`WINDOWS-VALIDATION.md`.

- ✅ Handshake MCP, `tools/list` (69 outils), `prompts/list` (5 prompts),
  `tools/call`, les 3 ressources
- ✅ **Outils de workflow** : `check_requirements` (10/10 frameworks détectés),
  `analyze_dependencies`, `mod_doctor` (santé du setup), `validate_xl` +
  `scaffold_archivexl`, `find_references`, `diff_mod_vs_base` (bruit `$.Header`
  filtré), `scaffold_mod`, `package_mod` — tous vérifiés sur l'install réelle
- ✅ Lecture (`archive_info`, `find_in_archives` cache ×6, `diff_archives`,
  `extract_files`, `uncook`), conversion (`cr2w_to_json`/`json_to_cr2w`,
  `export_files`, `import_raw`), écriture (`pack_archive`, `lint_mod`,
  `install_mod`), audio (`wwise_export`), TweakDB (`tweakdb_query` capé,
  `read_tweak`/`write_tweak`/`validate_tweak`/`install_tweak`), REDmod
  (`create_redmod_project`/`pack_redmod`/`install_redmod`), hash — sur de
  vrais assets Cyberpunk 2077
- ✅ Compression Kraken native : aller-retour `oodle compress`/`decompress` byte-exact
- ✅ Daemon : après le démarrage (~3 s, une fois), les appels suivants tombent à
  quelques millisecondes ; **plusieurs requêtes en vol simultanées** (pipelining)
- ✅ Cache LRU des listings d'archives (33 archives content + 318 mods) ;
  `find_in_archives` ×6 plus rapide sur appels successifs
- ✅ **Nouveaux outils** : `export_morphtarget`/`export_mlmask` (vérifiés sur
  assets réels), `extract_audio` (**82 578 fichiers opus** extraits d'une archive
  vocale), `generate_modproj`, `create_mod_project` étendu, `lint_script` sémantique,
  **`loc_resolve`** (LocKey→texte, 70 579 entrées en_us ; clé `40`→« News »),
  **`import_audio`** (WAV→opus, câblage vérifié)
- ✅ **`build_project`** : compile désormais le `.cpmodproj` généré par
  `create_mod_project` → `packed/archive/pc/mod/<mod>.archive`
- ✅ **Tests unitaires C#** (`WolvenKitMcp.Tests`, xUnit) : 26 tests verts (helpers purs
  `Truncate`/`MatchesGlob`/`BuildCpmodprojXml` + parser REDscript : acceptation du corpus
  réaliste, détection d'erreurs de syntaxe, extraction module/déclarations)
- ✅ **Parser REDscript** (`lint_script`) : 0 erreur sur les 1374 `.reds` de `r6/scripts`,
  erreurs ligne:colonne sur du code cassé (vérifié de bout en bout via le serveur MCP)
- ⚠️ `detect_conflicts` : le verbe `conflicts` de WolvenKit.CLI 8.18.0 plante sur
  un install réel — bug amont, reproductible tel quel avec `cp77tools`

## Limites connues

- **Textures / audio.** La conversion de textures (`texconv`) et l'audio Wwise
  dépendent de binaires natifs Windows — hors périmètre macOS.
- **Démarrage du daemon.** Le tout premier appel après le lancement du serveur
  attend le préchauffage du daemon (~7 s, une fois) ; ensuite tout est instantané.
- **Compresseur.** `rarten/ooz` n'est pas garanti byte-identique à Oodle pour les
  très petits blocs Mermaid/Selkie — sans incidence (le jeu décode tout flux valide).

## Pistes restantes

Livré jusqu'ici : sortie JSON structurée, `read_game_file` / `write_game_file`,
`install_mod`, `diff_archives`, `lint_mod`, REDmod packaging, édition TweakDB
structurée + `describe_tweak_record` + `generate_tweak_template`, inspection
mesh/texture, lecture + lint scripts `.reds` + `generate_redscript_template`,
backup/restore mods, **uninstall trio + `deploy_redmod`**, **launch_game /
tail_game_logs** (boucle d'itération en jeu), **`mod_summary` + `dump_records`**
(intelligence), **`extract_localization` / `build_localization`** (UI strings),
5 prompts MCP, cache LRU + stats + **métriques par verbe** + `clear_cache`,
parallélisation `wwise_export`, pipelining daemon, mode `verbose` pour debug,
**export anim/morphtarget/mlmask**, **`extract_audio` (opus voix-off)**,
**génération `.cpmodproj`** (`generate_modproj` + `create_mod_project` →
`build_project` de bout en bout), **lint sémantique `.reds`**, **tests unitaires
C# xUnit**, **bundle `.mcpb`** (`build-mcpb.ps1`) + **CI GitHub Actions**.

Round 3 : **`loc_resolve`** (LocKey → texte, vérifié : 70 579 entrées en_us, résolution
hash et clé secondaire OK), **`import_audio`** (WAV → `.opus` via `OpusTools.ImportWavs`,
encodeur `opusenc` embarqué), exports anim/morphtarget/mlmask vérifiés sur assets réels.

### Restant

- **`import_audio`** — câblé et vérifié au niveau du verbe (charge l'ArchiveManager,
  atteint `OpusTools.ImportWavs`) ; le **round-trip complet** n'a pas été testé faute
  de WAV nommés par un vrai hash opus. À valider sur un cas de remplacement de voix réel.
- **`export_animation`** — fonctionne, mais une `.anims` **seule** (sans rig associé)
  ne produit pas de glTF (contrainte WolvenKit) ; un mode « anim + rig » serait utile.
- **Toggles d'export fins** — `IsBinary`/`incRootMotion` (anim), `ExportTextures`
  (morphtarget) ne passent pas par `ConsoleFunctions.ExportTask` ; nécessiteraient
  d'appeler `IModTools.Export` directement avec un `GlobalExportArgs` construit.
- **Type-checking `.reds`** — la validation **syntaxique** est désormais faite par un
  vrai parser de grammaire (`RedscriptParser.cs`, 0 faux positif sur 1374 fichiers réels) ;
  la résolution de **types/signatures externes** exigerait le compilateur `scc` + tout
  l'écosystème de mods (lent ~15 min, échoue sur les deps manquantes — cf. note ci-dessous).
- **Re-validation macOS** — n'a pas été refaite depuis les extensions Windows.

## Structure

```
wolvenkit-mcp/
├── native/                  Reconstruction de libkraken.dylib (arm64)
│   ├── build-libkraken.sh
│   ├── README.md
│   ├── ooz-rarten/          Source rarten/ooz (modifiée — cf. native/README.md)
│   └── build/libkraken.dylib
├── src/WolvenKitMcp/        Serveur MCP C# / .NET 8
│   ├── Program.cs           Hôte + transport stdio
│   ├── Cp77ToolsRunner.cs   Pilote le daemon (IPC pipeliné, cache d'archives, repli cp77tools)
│   ├── WolvenKitTools.cs    Les 60 outils MCP de base
│   ├── ModdingTools.cs      Les 9 outils de workflow (deps, santé, scaffolding, refs, diff)
│   ├── RedscriptParser.cs   Parser de grammaire REDscript (lint_script)
│   ├── WolvenKitPrompts.cs  Les 5 prompts MCP (recettes)
│   └── WolvenKitResources.cs  Les 3 ressources MCP
├── docs/                    USER_GUIDE · TOOLS · MODDING_RECIPES · ARCHITECTURE
├── src/WolvenKitMcp.Tests/  Tests unitaires xUnit des helpers purs
├── src/WolvenKitDaemon/     Daemon persistant — hôte des bibliothèques WolvenKit
│   └── Program.cs           DI + dispatcher de verbes + IPC stdio pipeliné
├── manifest.json            Manifest d'extension Desktop (.mcpb)
├── build-mcpb.ps1           Construit dist/wolvenkit-mcp.mcpb (install 1-clic)
├── .github/workflows/ci.yml CI : build daemon + serveur + tests + bundle .mcpb
├── test-mcp-server.py       Client de test du serveur MCP
├── test-daemon.py           Client de test du daemon seul
├── validate-windows.py      Valide les 69 outils + 5 prompts sur de vrais assets
├── WINDOWS-VALIDATION.md    Checklist + résultats de validation Windows
└── README.md
```
