# Audit des outils MCP WolvenKit

Date de l'audit : 15 juillet 2026

> **État au 16 juillet 2026 : implémenté sur `feat/implement-wolvenkit-audit`.**
> Les corrections, la parité d'options, la migration 8.19.0, les cinq nouveaux
> outils et les améliorations secondaires décrites ci-dessous sont intégrées.

## Résumé

La surface MCP est déjà large : les grandes familles de commandes WolvenKit sont
couvertes et l'ajout de nombreux wrappers supplémentaires apporterait peu de valeur.
Les priorités sont plutôt :

1. corriger trois contrats trompeurs ou incohérents ;
2. compléter la transmission des options Modkit importantes ;
3. passer de WolvenKit 8.18.0 à la version stable 8.19.0 ;
4. ajouter quelques outils sémantiques à forte valeur.

## Corrections prioritaires

### 1. `import_mesh.garmentSupport=false` ne désactive pas le garment support

Le wrapper [`import_mesh`](../src/WkMcp/WolvenKitTools.cs) n'ajoute `--garment`
que lorsque le paramètre vaut `true`. Or
[`GltfImportArgs.ImportGarmentSupport`](https://github.com/WolvenKit/WolvenKit/blob/8.18.0/WolvenKit.Common/Model/Arguments/ImportArgs.cs)
vaut déjà `true` par défaut dans WolvenKit 8.18.0. Le chemin spécial du daemon ne
fait également que forcer la propriété à `true`.

En pratique, les valeurs `false` et `true` conduisent donc toutes deux à un import
avec garment support. La documentation et le changelog qui indiquent « off by
default » sont trompeurs.

Actions recommandées :

- transmettre une valeur explicite au daemon, ou ajouter `--no-garment` ;
- affecter systématiquement `ImportGarmentSupport` ;
- enregistrer `--garment` dans `DaemonState.BoolFlags` ;
- corriger le README, `docs/TOOLS.md`, `AGENT.md` et le changelog.

### 2. `import_mesh(keep=false)` promet une opération non implémentée

La description conseille `keep=false` pour créer un nouveau mesh. Le code amont
indique toutefois que l'import direct d'un mesh sans mesh de base n'est pas
implémenté et renvoie un échec
([source WolvenKit](https://github.com/WolvenKit/WolvenKit/blob/8.18.0/WolvenKit.Modkit/RED4/Import.cs)).

Le MCP devrait choisir l'une des approches suivantes :

- refuser clairement `keep=false` sans `baseMesh` ;
- exposer un paramètre `baseMesh` et construire les arguments d'import associés ;
- retirer de la documentation la promesse de création d'un mesh neuf.

### 3. Le contrat JSON universel n'est pas universel

Le README affirme que chaque outil renvoie :

```json
{
  "ok": true,
  "status": "success",
  "summary": "...",
  "produced": [],
  "warnings": [],
  "errors": [],
  "log": "..."
}
```

Les outils live renvoient notamment `transport`, `result` et `error`, les scènes
utilisent encore une autre forme, et plusieurs outils d'analyse omettent
`produced` ou `log`.

Actions recommandées :

- créer un DTO ou result builder commun à toutes les classes d'outils ;
- toujours inclure les champs universels, quitte à utiliser des listes vides ;
- conserver les champs spécialisés comme propriétés facultatives ;
- ajouter des tests de contrat sur les réponses de succès et d'erreur.

## Parité avec les tâches WolvenKit

Le daemon ne transmet qu'une partie des options déjà prises en charge par Modkit.

### `unbundle`

Options importantes manquantes :

- extraction par hash ou fichier de hashes ;
- `gamepath` ;
- mode de décompression debug.

L'extraction par hash est particulièrement utile quand le chemin n'est pas résolu
et ne peut donc pas être sélectionné par glob.

### `uncook`

Options importantes manquantes :

- `regex` et `hash` ;
- `raw` et `forcebuffers` ;
- dépôt de matériaux ;
- sérialisation explicite ;
- export merged expérimental.

### `export` et conversion CR2W/JSON

- `export` ne transmet pas `gamepath` ni `forcebuffers` ;
- le daemon de conversion accepte pattern/regex, mais `cr2w_to_json` et
  `json_to_cr2w` ne les exposent pas.

### Imports et exports typés

`import_texture` devrait pouvoir définir avant l'import :

- le groupe de texture ;
- le format brut et la compression ;
- le gamma ;
- la génération des mipmaps ;
- le streaming et la prémultiplication alpha.

Les outils mesh et animation gagneraient aussi à exposer le format GLB/glTF, les
matériaux, les rigs, le root motion, les événements et les réglages garment.

La priorité devrait aller aux hashes, aux options texture et aux réglages
mesh/animation plutôt qu'à des paramètres marginaux.

## Mise à niveau vers WolvenKit 8.19.0

Le daemon référence encore `WolvenKit.Modkit` 8.18.0. La version stable 8.19.0 a
été publiée le 3 juillet 2026 sur le
[package NuGet officiel](https://www.nuget.org/packages/WolvenKit.Modkit/).

Le [diff 8.18.0 vers 8.19.0](https://github.com/WolvenKit/WolvenKit/compare/8.18.0...8.19.0)
contient des changements directement pertinents :

- préservation et réencodage des animations SIMD ;
- gestion des événements d'animation ;
- option de compression forcée ;
- corrections d'import mesh, de rigs et d'os orphelins ;
- évolution importante de l'import MLMask.

La cible recommandée est la version stable 8.19.0, plutôt que `main` ou une version
nightly. La mise à niveau devrait être vérifiée avec un petit corpus réel :

- mesh garment avec support activé et désactivé ;
- mesh contenant des os orphelins ;
- animation SIMD avec événements ;
- MLMask représentatif.

## Outils manquants à forte valeur

### 1. `validate_project` ou `scan_broken_references`

Analyser globalement les références CR2W, les fichiers illisibles et les chemins
absents des archives du jeu et du projet. WolvenKit possède une logique similaire
dans
[`Cp77Project`](https://github.com/WolvenKit/WolvenKit/blob/8.18.0/WolvenKit.App/Models/ProjectManagement/Project/Cp77Project.cs).

Les outils MCP actuels inspectent un fichier ou recherchent du texte, mais ne
valident pas les références binaires à l'échelle d'un projet.

### 2. `inspect_entity`

Produire un résumé des composants, apparences, contrôleurs visuels, meshes,
animations et dépendances d'un fichier `.ent`. Cette vue serait beaucoup plus
exploitable par un agent que l'arbre JSON CR2W complet.

### 3. `inspect_animation`

Lister les clips, durées, root motion, événements, rigs et modes d'encodage SIMD.
Cet outil devient particulièrement pertinent avec WolvenKit 8.19.0.

### 4. `inspect_streamingsector`

Résumer les nœuds, types, ressources et transforms d'un `.streamingsector`. Ces
arbres sont très volumineux après conversion JSON et se prêtent mal à une inspection
générique.

### 5. `generate_inkatlas`

Créer un `.inkatlas` à partir d'un dossier de PNG. L'application WolvenKit possède
déjà un
[`InkatlasImageGenerator`](https://github.com/WolvenKit/WolvenKit/blob/8.18.0/WolvenKit.App/Helpers/InkatlasImageGenerator.cs),
alors que le MCP sait actuellement seulement inspecter un atlas et résoudre ses
parties.

## Améliorations secondaires

- permettre à `read_game_file` de retrouver automatiquement l'archive contenant
  la ressource à partir du dossier d'archives ou du chemin du jeu ;
- ajouter pagination, curseur ou sortie fichier à `tweakdb_query` ;
- exposer une sortie bulk ou paginée pour la résolution de localisation ;
- ajouter des tests unitaires sur la construction des options du daemon, pas
  uniquement sur les validateurs et fixtures JSON.

## Validation effectuée pendant l'audit

La suite de tests non-E2E a été exécutée avec une sortie de build redirigée pour
éviter le verrou du daemon connecté :

```powershell
dotnet test src/WkMcp.Tests/WkMcp.Tests.csproj `
  -p:OutDir=C:\tmp\wkmcp-audit-tests\ `
  --filter "FullyQualifiedName!~McpE2ETests"
```

Résultat : **359 tests réussis, 0 échec, 0 ignoré**.

Les tests E2E ont été volontairement exclus afin de ne pas interrompre le serveur
MCP connecté. Les tests existants donnent une bonne assurance structurelle, mais
ne détectent pas les divergences sémantiques `garmentSupport`/`keep=false` ni les
régressions sur des assets réels.

## Ordre d'implémentation recommandé

1. Corriger `garmentSupport`, `keep=false` et le contrat JSON.
2. Compléter les options Modkit essentielles.
3. Passer à WolvenKit 8.19.0 et ajouter le corpus de régression.
4. Ajouter `validate_project`.
5. Ajouter `inspect_entity`, `inspect_animation` et `inspect_streamingsector`.
6. Ajouter `generate_inkatlas`, puis les améliorations de pagination et de
   découverte automatique.
