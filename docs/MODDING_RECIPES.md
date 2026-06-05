# Recettes de modding — WolvenKit MCP

Recettes concrètes, prêtes à copier-coller, pour les types de mods Cyberpunk 2077 les
plus courants. Chaque recette enchaîne des **appels d'outils MCP** (avec des exemples
d'arguments) et se termine par une **vérification**.

## Conventions

- Tous les outils renvoient un JSON `{ ok, status, summary, produced, warnings, errors, log }`.
  Le **succès se juge sur `produced`/`ok`**, pas sur le contenu du `log`.
- Les chemins doivent être **absolus** (Windows : `C:\...`). Dans les exemples, on note
  le dossier du jeu `<JEU>` (ex. `C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077`).
- Avant toute recette, vérifier que le CLI est opérationnel avec **`wolvenkit_status`**.
- Les outils dont les arguments comportent des valeurs par défaut (ex. `pattern`,
  `verbose`) peuvent être omis.

---

## 1. Tweak d'arme / d'item (TweakXL)

**Objectif** : modifier une stat d'une arme existante (ici : la capacité de chargeur du
Lexington) sans toucher aux fichiers de jeu, via un `.tweak` chargé à chaud par TweakXL.

1. **`generate_tweak_template`** — générer le `.tweak`.
   - `pattern`: `"override_field"`
   - `parametersJson`: `{"recordId":"Items.Preset_Lexington_Default","field":"magazineCapacity","value":24}`
   - `outputFile`: `C:\mods\lexington_mag\lexington.tweak`

   Variante « augmenter une stat numérique » : `pattern` = `"boost_stat"` avec
   `{"recordId":"Items.Preset_Lexington_Default","stat":"damage","value":150}`.

   Variante « nouvel item dérivé d'un existant » : `pattern` = `"new_record"` avec
   `{"newId":"MyMod.SuperLexington","baseId":"Items.Preset_Lexington_Default","overrides":"{\"magazineCapacity\":48}"}`.

2. **`validate_tweak`** — vérifier que chaque clé existe bien dans la TweakDB (sauf
   `$instanceOf`).
   - `tweakFile`: `C:\mods\lexington_mag\lexington.tweak`
   - `tweakdbBin`: `<JEU>\r6\cache\tweakdb.bin`

3. **`install_tweak`** — copier vers `<JEU>\r6\tweaks\`.
   - `tweakFile`: `C:\mods\lexington_mag\lexington.tweak`
   - `gamePath`: `<JEU>`

**Vérification** : `validate_tweak` renvoie `status: "success"` et une liste de clés
inconnues vide ; `install_tweak` renvoie `installedPath` pointant dans `r6\tweaks`.
TweakXL charge le fichier au prochain lancement, sans rebuild.

---

## 2. Mod REDscript avec `@wrapMethod`

**Objectif** : étendre une méthode du jeu (ex. `PlayerPuppet.OnGameAttached`) sans la
remplacer, via un starter REDscript généré puis vérifié syntaxiquement.

1. **`scaffold_mod`** — créer le squelette REDscript.
   - `parentFolder`: `C:\mods`
   - `modName`: `MyHook`
   - `kind`: `"redscript"`
   - `dependencies` (optionnel) : `"redscript"`

   Produit `C:\mods\MyHook\r6\scripts\MyHook\MyHook.reds` contenant déjà un
   `@wrapMethod(PlayerPuppet)` sur `OnGameAttached` qui appelle `wrappedMethod()`, plus
   un `MOD_MANIFEST.json`.

   Alternative ciblée : **`generate_redscript_template`** avec `pattern` = `"wrap_method"`
   pour produire un seul `.reds` paramétré.

2. *(édition)* — adapter le corps de la méthode dans le `.reds`.

3. **`lint_script`** — analyse syntaxique + sémantique (parser réel, 0 faux positif
   calibré).
   - `scriptFile`: `C:\mods\MyHook\r6\scripts\MyHook\MyHook.reds`

   Vérifie notamment que l'annotation cible bien une classe et qu'un `@wrapMethod`
   appelle `wrappedMethod()`.

4. **Déploiement** : copier `r6\scripts\...` vers `<JEU>\r6\scripts\...` (le redscript
   « classique » est chargé directement ; en pipeline REDmod, voir recette 4).

**Vérification** : `lint_script` renvoie `status: "success"` (ou `"partial"` avec de
simples avertissements), `errors: []`, et un `declarations`/`parsedDeclarations` non nul.
`lint_script` n'effectue PAS de vérification de types (résolution externe = `scc`).

---

## 3. Apparence / item via ArchiveXL

**Objectif** : déclarer un record factory (ou un patch de ressource / des sons / de la
localisation) via un fichier `.xl` ArchiveXL.

1. **`scaffold_archivexl`** — générer le `.xl` de départ.
   - `outputFolder`: `C:\mods\MyAppearance`
   - `modName`: `MyAppearance`
   - `kind`: `"factory"`  (autres : `"customSounds"`, `"localization"`, `"resource"`)

   Produit `C:\mods\MyAppearance\MyAppearance.xl`. Pour `factory`, il référence un
   `MyAppearance\factory.csv` ; pour `resource`, il pré-remplit un bloc
   `resource: patch:` ; etc.

2. *(édition)* — compléter le `.xl` (chemins des `.app`/CSV) et préparer les fichiers
   ressources associés.

3. **`validate_xl`** — valider le YAML et les sections de premier niveau.
   - `xlFile`: `C:\mods\MyAppearance\MyAppearance.xl`

   Sections reconnues : `customSounds`, `resource`, `factories`, `localization`,
   `animations`. Une section inconnue génère un avertissement (`status: "partial"`),
   un YAML mal formé une erreur (`status: "error"`).

4. **Déploiement** : placer le `.xl` dans `<JEU>\archive\pc\mod\` à côté de l'archive
   du mod (ArchiveXL lit les `.xl` de ce dossier).

**Vérification** : `validate_xl` renvoie `status: "success"`, `errors: []` et liste les
`sections` attendues.

---

## 4. REDmod (format post-1.6)

**Objectif** : produire un mod au format REDmod (archives + scripts + tweaks), puis
l'installer et le déployer officiellement via `redMod.exe`.

1. **`create_redmod_project`** — créer la structure `mods/<nom>/`.
   - `parentFolder`: `C:\mods`
   - `modName`: `MyRedmod`
   - `description`: `"Mon premier REDmod"`
   - `version`: `"1.0.0"`

   Crée `C:\mods\MyRedmod\` avec `info.json` + sous-dossiers `archives/`, `scripts/`,
   `tweaks/`, `customSounds/`.

2. *(remplissage)* — déposer les `.archive` dans `archives/`, les `.reds` dans
   `scripts/`, les `.tweak` dans `tweaks/`.

3. **`pack_redmod`** *(optionnel, distribution)* — produire un `.zip` distribuable.
   - `modSourceFolder`: `C:\mods\MyRedmod`
   - `outputPath`: `C:\dist`

   Le `.zip` inclut le dossier `MyRedmod/` (avec `info.json`) ; l'utilisateur final le
   décompresse dans `<JEU>\mods\`.

4. **`install_redmod`** — copie récursive vers `<JEU>\mods\<nom>\`.
   - `modSourceFolder`: `C:\mods\MyRedmod`
   - `gamePath`: `<JEU>`

5. **`deploy_redmod`** — lance `<JEU>\tools\redmod\bin\redMod.exe deploy` (compile les
   scripts + applique les tweaks des REDmods installés).
   - `gamePath`: `<JEU>`

**Vérification** : `install_redmod` renvoie un `produced` dans `<JEU>\mods\<nom>` ;
`deploy_redmod` renvoie `exit=0` dans son `summary`. (Le DLC REDmod doit être installé,
sinon `redMod.exe` est introuvable.) On peut confirmer avec **`list_installed_mods`**
(`gamePath` = `<JEU>`) qui doit lister le mod dans `redMods`.

---

## 5. Localisation (traduction UI)

**Objectif** : extraire les chaînes traduisibles de la TweakDB, les traduire, puis
construire un `.tweak` qui surcharge `displayName` / `localizedDescription` / etc.

1. **`extract_localization`** — extraire les champs traduisibles vers un JSON
   `{recordId: {field: value}}`.
   - `tweakdbPath`: `<JEU>\r6\cache\tweakdb.bin`
   - `outputJson`: `C:\mods\loc_fr\strings.json`
   - `filter` (optionnel) : `"Items."` pour ne cibler que les items.

2. *(édition)* — traduire les valeurs dans `strings.json` (conserver la structure
   `{recordId: {field: "Traduction"}}`).

3. **`build_localization`** — construire le `.tweak` TweakXL depuis le JSON traduit.
   - `translationsJson`: `C:\mods\loc_fr\strings.json`
   - `outputTweak`: `C:\mods\loc_fr\loc_fr.tweak`
   - `lang` (informatif) : `"fr-fr"`

4. **`install_tweak`** — copier vers `<JEU>\r6\tweaks\`.
   - `tweakFile`: `C:\mods\loc_fr\loc_fr.tweak`
   - `gamePath`: `<JEU>`

**Vérification** : `build_localization` renvoie `recordCount`/`fieldCount` > 0 et
`status: "success"`. (Limitation : ne couvre que les strings UI de TweakDB ; les
sous-titres audio `.opusinfo` ne sont pas concernés.)

---

## 6. Remplacement de texture

**Objectif** : extraire une texture du jeu, l'éditer en PNG, la réimporter, l'empaqueter
en `.archive` et l'installer.

1. **`uncook`** — extraire + convertir la texture en image éditable.
   - `archivePath`: `<JEU>\archive\pc\content\basegame_4_gamedata.archive`
     (ou l'archive contenant la texture cible)
   - `outputPath`: `C:\work\uncooked`
   - `pattern`: `"*.xbm"` (ou un chemin de texture précis)
   - `textureFormat`: `"png"`

   *(Pour seulement extraire sans convertir : **`extract_files`** avec les mêmes
   `archivePath`/`outputPath`/`pattern`.)*

2. *(édition)* — modifier le PNG dans un éditeur d'image, en conservant l'arborescence
   relative `base\...` produite par `uncook`.

3. **`import_raw`** — réimporter le PNG en CR2W REDengine.
   - `path`: `C:\work\uncooked`  (fichier ou dossier)
   - `outputPath`: `C:\work\cooked`

   La structure de dossiers `base\...` doit être préservée (= le chemin de jeu de la
   ressource).

4. **`pack_archive`** — empaqueter le dossier en `.archive` (compression Kraken).
   - `folderPath`: `C:\work\cooked`
   - `outputPath`: `C:\dist`

5. **`install_mod`** — copier l'archive dans `<JEU>\archive\pc\mod\`.
   - `archivePath`: `C:\dist\cooked.archive`
   - `gamePath`: `<JEU>`

**Vérification** : chaque étape renvoie un `produced` non vide (`uncook` → image,
`import_raw` → CR2W, `pack_archive` → `.archive`) ; `install_mod` renvoie le chemin de
destination dans `archive\pc\mod`. Confirmer avec **`list_installed_mods`** ou
**`mod_summary`** sur l'archive produite.

---

## 7. Analyse d'un mod existant

**Objectif** : comprendre ce qu'un mod fait, ses dépendances, où une cible est
référencée, et ce qu'il change réellement par rapport au jeu de base.

1. **`mod_summary`** — synthèse compacte (accepte un `.archive` OU un dossier REDmod).
   - `modPath`: `C:\mods\SomeMod\SomeMod.archive`
     (ou `C:\mods\SomeRedmod` avec `info.json` à la racine)

   Pour une archive : nombre de fichiers + répartition par extension. Pour un REDmod :
   `info.json`, contenu de `archives/`/`scripts/`/`tweaks/`/`customSounds/`, clés
   top-level des `.tweak` et déclarations des `.reds`.

2. **`analyze_dependencies`** — déduire les frameworks requis (redscript, RED4ext,
   ArchiveXL, TweakXL, Codeware, Audioware, etc.).
   - `modPath`: `C:\mods\SomeRedmod`
   - `gamePath` (optionnel) : `<JEU>` — indique pour chaque dépendance si elle est
     `installé` ou `MANQUANT`.

3. **`find_references`** — trouver toutes les références textuelles à une cible dans les
   sources du mod (`.reds`, `.tweak`, `.xl`, `.yaml`, `.lua`, `.json`, `.csv`).
   - `target`: `"Items.Preset_Lexington_Default"`
   - `searchFolder`: `C:\mods\SomeRedmod`
   - `maxResults` (optionnel) : `200`

   *(Pour chercher dans les `.archive` du jeu, utiliser plutôt `find_in_archives`.)*

4. **`diff_mod_vs_base`** — diff sémantique d'un fichier surchargé contre sa version de
   base (champs ajoutés / supprimés / modifiés).
   - `modArchive`: `C:\mods\SomeMod\SomeMod.archive`
   - `gameFilePath`: `base\path\to\fichier.app`
   - `gamePath`: `<JEU>`  (localise la base dans `archive\pc\content`)
   - `baseArchive` (optionnel) : archive de base précise pour court-circuiter la
     recherche.

**Vérification** : `mod_summary`/`analyze_dependencies` renvoient `status: "success"`
(`"partial"` si dépendances manquantes) ; `find_references` renvoie un `matchCount` et la
liste `matches` (fichier:ligne) ; `diff_mod_vs_base` résume `added`/`removed`/`changed`.
Si `diff_mod_vs_base` ne trouve pas le fichier dans la base, c'est probablement un fichier
**ajouté** par le mod (pas une surcharge).

---

## Recette — Éditer le journal de quêtes/codex (`.journal`)

Le journal (`base\journal\cooked_journal.journal`) est un CR2W standard, donc éditable
via le pipeline générique — mais son JSON pèse **~70 Mo** (28 000+ entrées). On le
navigue d'abord, puis on n'édite que l'entrée ciblée.

**Objectif** : modifier une entrée précise (quête, contact, codex, e-mail…).

1. **Localiser** : `find_in_archives` (`pattern: "*.journal"`,
   `archivesFolder: <JEU>\archive\pc\content`) → `base\journal\cooked_journal.journal`
   (dans `basegame_4_gamedata.archive`).
2. **Extraire en JSON** : `read_game_file` (`archivePath` = l'archive,
   `gameFilePath: base\journal\cooked_journal.journal`). Le contenu renvoyé est tronqué ;
   noter le champ **`jsonFile`** (le JSON complet sur disque).
3. **Cartographier** : `inspect_journal` (`jsonFile`) → total d'entrées, répartition par
   `$type`, catégories de 1er niveau (`quests`, `codex`, `contacts`, `briefings`…).
4. **Cibler** : `find_journal_entry` (`jsonFile`, `query`, `field` ∈ `id`|`type`|`title`).
   Ex. `field:"id", query:"holofixer_used_1"` ou `field:"type", query:"gameJournalContact"`.
   Renvoie le **chemin JSON exact** de chaque entrée
   (ex. `Data.RootChunk.entry.Data.entries[3].Data.entries[1].Data`).
5. **Éditer** le `jsonFile` à ce chemin (modifier les champs de l'entrée).
6. **Réécrire** : `write_game_file` (`jsonFile`, `gameFilePath` identique,
   `modArchiveFolder` = `source/archive` du projet) → CR2W reconstruit.
7. **Empaqueter/installer** : `pack_archive` → `install_mod`.

**Vérification** : `inspect_journal` confirme total/types ; le `.journal` CR2W produit par
`write_game_file` est non vide (quelques Mo) ; `diff_mod_vs_base` confirme ce qui a changé.

---

## Annexe — appels utiles transverses

- **`wolvenkit_status`** : à appeler en premier (disponibilité du CLI + stats du cache).
- **`detect_conflicts`** (`gamePath` = `<JEU>`) : repère deux mods fournissant le même
  fichier de jeu.
- **`lint_mod`** : lint global d'un dossier de mod.
- **`package_mod`** (`sourceFolder` au layout jeu `archive/`, `r6/`, `mods/...` →
  `outputZip`) : produit un `.zip` distribuable (Nexus / install manuel) avec séparateurs
  `/`.
- **`check_requirements`** (`gamePath` = `<JEU>`) : inventorie les frameworks installés.
