# Guide utilisateur — WolvenKit MCP pour le modding de Cyberpunk 2077

Ce guide s'adresse aux **moddeurs** : il explique comment installer le serveur
WolvenKit MCP, le brancher sur Claude, puis enchaîner des workflows complets de
bout en bout (lire un fichier de jeu, éditer un item, créer et installer un mod,
vérifier la santé de l'installation, packager pour distribution).

Vous n'avez pas besoin d'écrire du code : vous décrivez votre intention à Claude,
et Claude appelle les **outils MCP** à votre place. Ce guide nomme les outils
réels pour que vous puissiez guider Claude précisément (« utilise `read_game_file`
sur… ») et comprendre ce qui se passe.

> Convention de résultat : chaque outil renvoie un JSON
> `{ ok, status, summary, produced, warnings, errors, log }`. Le succès se juge
> aux **fichiers réellement produits** (champ `produced`), pas à un message de log.

---

## 1. Prérequis

Ce guide vise **Windows** (la plateforme validée de bout en bout, avec Cyberpunk
2077 installé). Il vous faut :

1. **Windows 10/11.**
2. **Cyberpunk 2077 installé** (Steam, GOG ou Epic). Repérez le dossier racine
   du jeu, par exemple :
   `C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077`.
   Dans ce guide, on l'appelle `<JEU>`.
3. **SDK .NET 8 (ou supérieur)** — https://dotnet.microsoft.com/download
4. **WolvenKit CLI** :
   ```powershell
   dotnet tool install -g WolvenKit.CLI
   ```
   Cela installe `cp77tools` dans `~\.dotnet\tools\`.
5. **Claude** : Claude Desktop ou Claude Code (le client MCP qui pilotera les outils).

> Note macOS : le projet fonctionne aussi sur macOS Apple Silicon, mais demande la
> reconstruction de `libkraken.dylib` (voir `native/README.md`). La conversion de
> textures et l'audio Wwise restent hors périmètre macOS. Ce guide cible Windows.

---

## 2. Installation du serveur MCP

Sur Windows, `cp77tools` est natif : le dossier `native/` (reconstruction de
libkraken pour macOS) est inutile.

### 2.1 Compiler le daemon puis le serveur

L'ordre compte : compiler **d'abord le daemon** (son build déploie automatiquement
`kraken.dll` et `DirectXTexNet.dll`), **puis le serveur**.

```powershell
dotnet build src\WolvenKitDaemon
dotnet build src\WolvenKitMcp
```

À la fin, le serveur se trouve à :
`src\WolvenKitMcp\bin\Debug\net8.0\WolvenKitMcp.dll`

### 2.2 Comment ça marche (en bref)

```
Claude ─MCP/JSON-RPC─▶ WolvenKitMcp ─IPC stdio─▶ WolvenKitDaemon ─▶ libs WolvenKit + kraken
                                    └─repli───▶ cp77tools (sous-processus) si daemon indisponible
```

Le daemon WolvenKit reste **persistant** : le coûteux chargement de la base de
hashes (~6 s) n'est payé qu'une fois, au préchauffage. Le premier appel après le
lancement attend ce préchauffage (~7 s) ; ensuite tout est quasi instantané. Si le
daemon est indisponible, chaque appel se replie sur un sous-processus `cp77tools`
(fonctionnel mais ~6 s/appel).

---

## 3. Brancher le serveur sur Claude

### 3.1 Claude Desktop

Éditez `%APPDATA%\Claude\claude_desktop_config.json` :

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

Remplacez le chemin par le vôtre, puis **redémarrez Claude Desktop**.

### 3.2 Claude Code

```powershell
claude mcp add wolvenkit -s user -- dotnet "C:\chemin\vers\wolvenkit-mcp\src\WolvenKitMcp\bin\Debug\net8.0\WolvenKitMcp.dll"
```

Aucune variable `DOTNET_ROOT` n'est nécessaire sur Windows.

### 3.3 Vérifier le branchement

Demandez à Claude d'appeler **`wolvenkit_status`**. Vous devez obtenir
`ok: true`, le chemin de `cp77tools` et sa version, ainsi que les stats du cache.
C'est le premier réflexe de diagnostic si quelque chose cloche.

---

## 4. Variables d'environnement de configuration

Toutes optionnelles. À définir dans le bloc `"env"` de la config Claude Desktop,
ou via `-e VAR=valeur` avec `claude mcp add`.

| Variable | Défaut | Rôle |
|---|---|---|
| `WOLVENKIT_DAEMON` | projet frère (build local) | Chemin de `WolvenKitDaemon.dll` (le chemin rapide). |
| `WOLVENKIT_CP77TOOLS` | `~\.dotnet\tools\cp77tools.exe` | Chemin de `cp77tools` (repli sous-processus). |
| `DOTNET_ROOT` / `WOLVENKIT_DOTNET_ROOT` | auto-détecté | Racine du runtime .NET — rarement à définir sous Windows. |
| `WOLVENKIT_CLI_TIMEOUT_SECONDS` | `300` | Délai maximal d'une commande (en secondes). |

Exemple Claude Desktop avec timeout étendu :

```json
{
  "mcpServers": {
    "wolvenkit": {
      "command": "dotnet",
      "args": ["C:\\...\\WolvenKitMcp.dll"],
      "env": { "WOLVENKIT_CLI_TIMEOUT_SECONDS": "600" }
    }
  }
}
```

---

## 5. Workflow — Localiser et lire un fichier de jeu

But : retrouver un fichier dans les archives du jeu et en lire le contenu en JSON.

1. **Localiser le fichier.** Demandez à Claude d'utiliser **`find_in_archives`**
   sur le dossier de contenu, avec un motif glob :
   - `archivesFolder` : `<JEU>\archive\pc\content`
   - `pattern` : par exemple `*player*.ent` (ou `regex` pour une expression
     régulière)

   L'outil indique, pour chaque correspondance, **dans quelle archive** se trouve
   le fichier. Les appels suivants sur le même dossier sont quasi instantanés
   (cache LRU).

   > Variante : **`archive_info`** liste le contenu d'une archive précise ;
   > **`compute_hash`** / **`resolve_hash`** font le lien chemin ↔ hash FNV1a64.

2. **Lire le fichier.** Une fois l'archive et le chemin interne connus, utilisez
   **`read_game_file`** :
   - `archivePath` : le `.archive` retourné à l'étape 1
   - `gameFilePath` : le chemin interne (ex. `base\characters\...\x.ent`)

   L'outil extrait, convertit en JSON REDengine et renvoie le contenu. Le JSON
   complet est aussi écrit sur disque (champ `jsonFile`) : si `truncated` vaut
   `true`, lisez ce fichier en entier pour le détail.

   > Sous le capot, `read_game_file` enchaîne `extract_files` (extraction) puis
   > `cr2w_to_json` (sérialisation). Vous pouvez aussi appeler ces deux outils
   > séparément si besoin.

3. **Inspecter sans tout convertir.** Pour un aperçu compact :
   - **`inspect_mesh`** : LODs, sous-meshes, matériaux, bones d'un `.mesh`
   - **`inspect_texture`** : résolution, format, mipmaps d'un `.xbm`
   - **`uncook`** : extraction + conversion en une passe (mesh → glTF,
     textures → image)

---

## 6. Workflow — Éditer un item via TweakDB (`.tweak`)

But : modifier les stats ou propriétés d'un item du jeu sans toucher aux archives.
TweakXL charge les `.tweak` à chaud (pris en compte au prochain lancement, sans
rebuild). Le `tweakdb.bin` de référence est typiquement
`<JEU>\r6\cache\tweakdb.bin`.

1. **Trouver l'identifiant.** Utilisez **`tweakdb_query`** :
   - `tweakdbPath` : `<JEU>\r6\cache\tweakdb.bin`
   - `filter` : sous-chaîne, par exemple `Items.Preset_Lexington` (résultats
     plafonnés à 100 ; affinez le filtre si `truncated` le signale).

2. **Découvrir les champs du record.** Utilisez **`describe_tweak_record`** sur
   l'identifiant : il liste tous les flats du record avec leurs types et valeurs
   courantes. Indispensable avant d'éditer (savoir quel champ modifier).

3. **Écrire le `.tweak`.** Deux voies :
   - **`generate_tweak_template`** pour partir d'un squelette (patterns :
     `override_field`, `new_record`, `boost_stat`), puis ajustez les valeurs ;
   - ou éditez un `.tweak` existant via **`read_tweak`** (→ JSON éditable),
     modifiez le JSON, puis **`write_tweak`** (JSON → `.tweak` YAML).

4. **Valider.** Avant d'installer, **`validate_tweak`** :
   - `tweakFile` : votre `.tweak`
   - `tweakdbBin` : `<JEU>\r6\cache\tweakdb.bin`

   Il signale les clés inconnues (sauf nouveaux records déclarant `$instanceOf`).

5. **Installer.** **`install_tweak`** copie le fichier vers
   `<JEU>\r6\tweaks\<nom>.tweak` :
   - `tweakFile` : votre `.tweak`
   - `gamePath` : `<JEU>`

   Lancez le jeu : le changement est actif.

   > Pour retirer le tweak : **`uninstall_tweak`**.

---

## 7. Workflow — Créer, empaqueter et installer un mod `.archive`

But : produire un mod `.archive` (remplacement/ajout d'asset) et l'installer.

### 7.1 Créer la structure de projet

Utilisez **`create_mod_project`** :
- `parentFolder` : dossier parent (ex. `C:\mods`)
- `modName` : nom du mod
- (optionnels) `author`, `version`, `description`

Cela crée l'arborescence et un `.cpmodproj` directement compilable :

```
<modName>/
├── <modName>.cpmodproj      Projet WolvenKit (compilable par build_project)
├── source/archive/          Fichiers REDengine cuits (.mesh, .ent, .xbm…) → empaquetés
├── source/raw/              Fichiers bruts (glTF, images…) → à passer par import_raw
├── source/resources/        Fichiers libres copiés tels quels
├── source/customSounds/     Sons personnalisés (REDmod audio)
└── packed/                  Sortie : build_project y dépose le mod compilé
```

> Vous avez déjà un dossier de projet sans `.cpmodproj` ? Utilisez
> **`generate_modproj`** pour le rendre compilable.

### 7.2 Préparer le contenu modifié

Selon le cas :

- **Éditer un fichier de jeu existant.** Lisez-le avec `read_game_file`
  (workflow §5), modifiez le JSON, puis **`write_game_file`** :
  - `jsonFile` : le JSON édité
  - `gameFilePath` : le chemin interne visé (ex. `base\…\x.ent`)
  - `modArchiveFolder` : le `source/archive` du projet

  Le CR2W produit est placé au bon chemin interne, prêt à empaqueter.

- **Importer des assets bruts.** Placez vos glTF/images dans `source/raw`, puis
  **`import_raw`** (raw → CR2W) vers `source/archive`.

### 7.3 Empaqueter

Deux options :

- **`build_project`** (recommandé) : compile le `.cpmodproj`. Donnez
  `projectFolder` = le dossier du projet. La sortie est
  `packed\archive\pc\mod\<mod>.archive`.
- **`pack_archive`** : empaquette directement un dossier de fichiers REDengine
  (`folderPath` = `source/archive`, `outputPath` = dossier de destination).

### 7.4 Lint et installation

1. **`lint_mod`** (pré-install) : détecte les extensions non-REDengine et, si vous
   passez `gamePath` = `<JEU>`, les conflits avec les mods déjà installés.
   - `archivePath` : votre `.archive`
2. **`install_mod`** : copie l'archive dans `<JEU>\archive\pc\mod\`.
   - `archivePath` : votre `.archive`
   - `gamePath` : `<JEU>`
3. Lancez le jeu pour vérifier.

> Vérifier ce qui est installé : **`list_installed_mods`**.
> Retirer un mod : **`uninstall_mod`**.
> Filet de sécurité : **`backup_mods`** (snapshot ZIP de `archive/pc/mod`,
> `mods/`, `r6/tweaks/`) et **`restore_mods`** avant toute manipulation risquée.

> Mods REDmod (scripts/tweaks/sons à déployer) : utilisez plutôt
> **`create_redmod_project`** → **`pack_redmod`** → **`install_redmod`**, puis
> **`deploy_redmod`** pour compiler scripts et appliquer tweaks.

---

## 8. Workflow — Vérifier dépendances et santé du setup

But : s'assurer que les frameworks requis (redscript, RED4ext, ArchiveXL,
TweakXL, Codeware, Audioware, CET…) sont en place avant de jouer ou de distribuer.

1. **Quels frameworks mon mod exige-t-il ?** **`analyze_dependencies`** :
   - `modPath` : dossier du mod (projet ou déployé)
   - (optionnel) `gamePath` : `<JEU>` → marque chaque dépendance
     **installée / manquante**

   L'outil déduit les frameworks requis depuis les imports `.reds`, les fichiers
   `.xl`, les `.tweak` et les types employés.

2. **Quels frameworks sont installés ?** **`check_requirements`** :
   - `gamePath` : `<JEU>`

   Inventaire des frameworks de modding présents, avec version.

3. **Diagnostic complet en un appel.** **`mod_doctor`** :
   - `gamePath` : `<JEU>`

   Synthèse santé : frameworks installés/manquants, dépendances requises par le
   contenu présent mais absentes, conflits, inventaire et recommandations. C'est
   l'outil à lancer en cas de plantage ou de mod qui ne se charge pas.

> Outils d'appui : **`detect_conflicts`** (un même fichier fourni par plusieurs
> mods), **`validate_xl`** (vérifie un ArchiveXL `.xl`), **`find_references`**
> (toutes les références TweakDBID/chemin/LocKey dans les sources d'un mod),
> **`diff_mod_vs_base`** (diff sémantique d'un fichier surchargé vs le jeu de base).

---

## 9. Workflow — Packager pour distribution

But : produire un `.zip` propre, prêt à publier (Nexus, etc.), respectant le
layout du jeu.

1. **Construire le layout de distribution.** Rassemblez dans un dossier source la
   structure telle qu'elle doit atterrir dans le jeu :

   ```
   <dist>/
   ├── archive/pc/mod/<mod>.archive   (mod .archive)
   ├── r6/tweaks/<mod>.tweak          (tweaks éventuels)
   └── mods/<mod>/                    (REDmod éventuel)
   ```

   > Raccourci : **`scaffold_mod`** crée en un appel un mod fonctionnel
   > (`archive` / `redscript` / `tweak` / `redmod`) + un `MOD_MANIFEST.json`
   > (type, dépendances déclarées) — bon point de départ pour un layout propre.

2. **Empaqueter.** **`package_mod`** :
   - `sourceFolder` : le dossier `<dist>` au layout jeu
   - `outputZip` : chemin du `.zip` de sortie

   Le ZIP utilise des séparateurs `/` conformes (compatibles avec les
   installeurs de mods).

3. **Contrôle final avant publication.** Relancez `analyze_dependencies` sur le
   layout pour documenter les dépendances exigées dans votre description Nexus, et
   `mod_doctor` sur une install de test pour confirmer que tout se charge.

---

## 10. Pour aller plus loin

- **Prompts MCP (recettes prêtes à l'emploi)** : `read_game_file_workflow`,
  `edit_tweakdb_item`, `pack_and_install_mod`, `recolor_texture`, `inspect_mesh`.
  Demandez à Claude d'invoquer le prompt correspondant pour démarrer un workflow.
- **Ressources MCP** : `wolvenkit://reference` (aide-mémoire commandes/formats),
  `wolvenkit://archive/{path}` (listing d'une archive),
  `wolvenkit://cr2w-json/{path}` (CR2W rendu en JSON).
- **Scripts REDscript** : **`read_script`** (structure d'un `.reds`),
  **`lint_script`** (vrai parser de grammaire, erreurs ligne:colonne),
  **`generate_redscript_template`** (scaffolds d'annotations).
- **Itération en jeu** : **`launch_game`** (lance le jeu, avec
  `deploy_redmod` préalable optionnel) et **`tail_game_logs`** (suit les logs
  `r6/logs` et `tools/redmod/logs`).
- **Maintenance** : **`clear_cache`** vide le cache LRU des archives ou les
  métriques (`scope` = `archives` | `metrics` | `all`).

### Dépannage rapide

- **`wolvenkit_status` renvoie `ok: false`** → `cp77tools` introuvable :
  `dotnet tool install -g WolvenKit.CLI`, ou pointez `WOLVENKIT_CP77TOOLS`.
- **Premier appel lent (~7 s)** → préchauffage du daemon, normal et unique.
- **Un outil semble échouer** → regardez d'abord `produced` (fichiers réellement
  créés) et `errors`, pas seulement `log` ; relancez avec `verbose: true` sur les
  outils qui le proposent (`extract_files`, `uncook`, `pack_archive`, `import_raw`…).
