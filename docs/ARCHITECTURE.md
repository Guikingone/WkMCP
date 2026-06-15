# Architecture — WolvenKit MCP

Document destiné à un **futur contributeur**. Il explique *pourquoi* le projet
est structuré ainsi, *comment* les morceaux communiquent, et *comment* y ajouter
du code sans casser les invariants. Les chemins sont donnés relativement à la
racine du dépôt (`wolvenkit-mcp/`).

---

## 1. Vue d'ensemble

Le projet est un **serveur MCP** (Model Context Protocol) qui expose les
capacités de modding Cyberpunk 2077 de WolvenKit à un agent LLM. Il se compose de
**deux processus** :

```
Claude ─MCP/JSON-RPC (stdio)─▶ WolvenKitMcp ─IPC stdio JSON─▶ WolvenKitDaemon ─▶ libs WolvenKit + libkraken
                                            └─repli──────────▶ cp77tools (sous-processus) si daemon indisponible
```

- **`src/WolvenKitMcp`** — l'hôte MCP. Il parle JSON-RPC MCP au client (Claude)
  sur stdio (ou HTTP/Streamable opt-in), expose **123 outils** (63 de base +
  25 workflow + 35 live), **8 prompts** et **4 ressources**, et ne lie *aucune*
  bibliothèque WolvenKit. Il pilote le daemon par IPC.
- **`src/WolvenKitDaemon`** — un processus persistant qui lie les bibliothèques
  WolvenKit (`WolvenKit.Modkit`, `WolvenKit.RED4.CR2W`, …) et `libkraken`/Oodle.
  Il charge les données de référence lourdes **une seule fois** puis traite des
  requêtes en boucle.
- **`src/WolvenKitMcp.Tests`** — 129 tests xUnit (helpers purs : `Truncate`,
  `MatchesGlob`, `BuildCpmodprojXml`, lint REDscript, histogramme d'archive,
  validation REDmod, résumé `.app`, garde-fou doc, etc.).

---

## 2. Pourquoi deux processus ?

Deux raisons, dans cet ordre d'importance.

### 2.1 Isolement de licence (GPL-3.0)

Les bibliothèques de WolvenKit sont sous **GPL-3.0** (copyleft). Lier ces
assemblies *dans le même processus/assembly* que le serveur MCP contaminerait ce
dernier par le copyleft. En isolant tout le code GPL dans un **processus séparé**
(le daemon) auquel le serveur MCP ne parle que par **IPC stdio** (échange de
texte JSON, pas de lien d'assembly), le serveur MCP reste hors du périmètre du
copyleft. C'est la frontière de séparation centrale du projet : **`WolvenKitMcp`
ne référence jamais d'assembly WolvenKit ; seul `WolvenKitDaemon` le fait.**

### 2.2 Performance : payer le cold-start une seule fois

Le coût dominant de WolvenKit est le chargement de `HashService` (la base de
hashes ↔ chemins REDengine) : **~6 s**. Le CLI `cp77tools` paie ce coût *à chaque
invocation*. En gardant le daemon vivant, ce coût est payé **une fois** au
démarrage ; les requêtes suivantes ne coûtent que quelques millisecondes d'IPC +
le travail réel.

Voir `src/WolvenKitDaemon/Program.cs` : `HashService`, `TweakDBService`,
`LocKeyService`, `HookService` sont des **singletons** (« chauds »), et un
préchauffage explicite construit `ConsoleFunctions` au démarrage avant de signaler
`{"ready":true}`.

---

## 3. Le protocole IPC stdio JSON pipeliné

### 3.1 Format des messages (une ligne JSON par message)

Référence : en-tête de `src/WolvenKitDaemon/Program.cs`.

```
requête  : {"id":N,"argv":["unbundle","/x.archive","--outpath","/out"]}
réponse  : {"id":N,"exit":0,"output":"[ 0: Information ] - ..."}
au prêt  : {"ready":true}
```

- `argv` est exactement la ligne de commande façon `cp77tools` (verbe +
  arguments). Le daemon dispatche le verbe sur une méthode WolvenKit.
- `output` agrège tout ce que la commande a écrit (logger + `Console.Out`
  capturé, cf. §3.4).
- Les DTO sont `DaemonRequest(int id, string[] argv)` et
  `DaemonResponse(int id, int exit, string output)`.

**`stdout` du daemon est strictement réservé au protocole JSON.** Une référence
`channel = Console.Out` est capturée *avant* toute redirection, puis
`Console.Out` est réaffecté vers un writer capturant (cf. §3.4). Toutes les
réponses sont écrites via `channel`. Côté serveur MCP, même discipline : `stdout`
est réservé au JSON-RPC MCP, donc **tous les logs partent sur stderr**
(`Program.cs` configure `LogToStandardErrorThreshold = Trace`).

### 3.2 Pipelining et corrélation par `id`

Le transport est **pipeliné** : plusieurs requêtes peuvent être en vol
simultanément, ré-appariées par `id`. Côté serveur MCP
(`src/WolvenKitMcp/Cp77ToolsRunner.cs`) :

- `SendToDaemonAsync` attribue un `id` incrémental, enregistre un
  `TaskCompletionSource` dans le dictionnaire concurrent `_outstanding[id]`, puis
  écrit la ligne JSON sur stdin du daemon. Les écritures stdin sont sérialisées
  par `_writeLock` (un seul writer à la fois, sinon deux JSON-lines
  s'entrelaceraient).
- `ReadResponseLoopAsync` est une boucle de lecture unique qui tourne pendant
  toute la vie du daemon : pour chaque ligne reçue, elle parse l'`id` et complète
  le `TaskCompletionSource` correspondant. À la mort du daemon (stream fermé),
  elle **échoue toutes les requêtes en vol** pour ne pas laisser d'appels pendus.

### 3.3 Sérialisation côté exécution

Bien que l'IPC soit pipeliné, **l'exécution dans le daemon reste sérialisée** par
un `execLock` (SemaphoreSlim 1,1) : les bibliothèques WolvenKit (logger, archive
manager, `Console.Out` capturé) **ne sont pas thread-safe**. L'overlap utile :
décoder/recevoir la requête N+1 pendant que N s'exécute, et permettre au client
d'envoyer en pipeline sans attendre la réponse précédente. Les écritures de
réponse sont aussi protégées par un `writeLock`.

### 3.4 Capture de la sortie

Deux canaux de sortie WolvenKit doivent être agrégés :

1. `ILoggerService` — la plupart des messages. Le daemon fournit
   `CapturingLoggerService`, qui accumule dans un `StringBuilder` drainable au
   format `[ 0: Level ] - message` (identique à celui de `cp77tools`).
2. `Console.WriteLine` — certaines tâches (notamment le listing
   `archive --list`) écrivent leur résultat directement sur la console. D'où le
   `CapturingTextWriter` qui redirige `Console.Out` vers le **même** tampon.
   Sans cela, `archive_info` / `find_in_archives` / la ressource d'archive
   renverraient du vide.

À chaque requête, le daemon `Drain()` le tampon avant exécution, exécute, puis
`Drain()` à nouveau pour récupérer exactement la sortie de cette requête.

---

## 4. Le runner : warmup, cache LRU, métriques, repli

Tout vit dans `src/WolvenKitMcp/Cp77ToolsRunner.cs`. Une **instance partagée
unique** (`Cp77ToolsRunner.Shared`) est injectée par DI dans tous les outils et
réutilisée par les ressources — donc **un seul daemon** pour tout le serveur.

### 4.1 Warmup

`Program.cs` lance, dès le démarrage et en tâche de fond, un appel
`--version` :

```csharp
_ = Task.Run(() => Cp77ToolsRunner.Shared.RunAsync(new[] { "--version" }, CancellationToken.None));
```

Cela démarre le daemon et déclenche son préchauffage (~6-8 s) pendant que le
client se connecte, pour que **même le premier vrai appel d'outil** bénéficie
d'un `HashService` déjà chaud.

`EnsureDaemonAsync` gère le cycle de vie : fast-path sans lock si le daemon est
vivant, sinon démarrage sous `_initLock` (double-check), attente de
`{"ready":true}` avec un timeout de 90 s, drainage continu de stderr (sinon le
tampon du pipe bloquerait le daemon), puis lancement de la boucle de lecture.

### 4.2 Cache LRU des listings d'archives

`GetArchiveListingAsync` met en cache la liste des chemins internes d'une
`.archive` (`_archiveCache`, clé = chemin absolu). L'invalidation est basée sur le
**mtime** du fichier (`LastWriteTimeUtc`) : si le mtime a changé, on relance
`archive --list` et on remplace l'entrée. Les compteurs `_cacheHits` /
`_cacheMisses` sont exposés via l'outil `wolvenkit_status`.
`InvalidateArchiveCache` permet de purger (outil `clear_cache`).

### 4.3 Métriques par verbe

`RunAsync` chronomètre chaque appel et l'enregistre dans `_metrics` (un
`RunnerMetrics` par verbe). `RunnerMetrics` garde `Count`, `TotalMs`, et un
**anneau circulaire des 100 dernières durées** d'où sont calculés **p50 / p95**.
Exposé via `wolvenkit_status` (clé `metrics`), réinitialisable via
`clear_cache(scope=metrics|all)` → `ResetMetrics()`.

### 4.4 Repli cp77tools

Si le daemon échoue (exception hors annulation), `RunAsync` le tue
(`KillDaemon`) — il sera relancé au prochain appel — puis se replie sur
`RunViaSubprocessAsync`, qui relance le CLI `cp77tools` en sous-processus
(comportement d'origine, ~6 s/appel, mais fonctionnel). Si le DLL du daemon est
carrément **absent**, le repli devient définitif (`_daemonDisabled = true`).

Résolution de chemins (toutes par variables d'environnement, sinon défauts) :
`WOLVENKIT_DAEMON` (DLL du daemon, sinon projet frère), `WOLVENKIT_CP77TOOLS`
(exe cp77tools, sinon `~/.dotnet/tools` puis PATH), `DOTNET_ROOT` /
`WOLVENKIT_DOTNET_ROOT`, `WOLVENKIT_CLI_TIMEOUT_SECONDS` (défaut 300 s).

---

## 5. Convention de résultat des outils

Chaque outil renvoie un JSON :

```json
{ "ok", "status", "summary", "produced", "warnings", "errors", "exitCode", "log" }
```

- `status` ∈ `success | partial | error | timeout`.
- **Le succès est déterminé par les fichiers réellement produits, pas par un
  marqueur de log.** Le helper `Structured` distingue deux cas :
  - **outil producteur** (on lui passe une liste `produced`) : `success` si des
    fichiers sont apparus (et `partial` s'il y a aussi des erreurs), `error`
    sinon. Cela évite qu'une erreur non fatale (ex. export de matériaux d'un mesh)
    fasse échouer un appel qui a bel et bien produit le fichier attendu.
  - **outil d'information** (pas de `produced`) : on se fie au code de sortie et à
    l'absence de marqueurs « Erreur daemon » / « Unhandled ».
- `warnings` / `errors` sont extraits du log par `LogLines` (lignes
  `[ 0: Warning/Error ] - …`).
- `log` est tronqué par `Truncate` (12 000 c.) en préservant tête, erreurs du
  milieu, et queue.

Helpers utilitaires dans `WolvenKitTools.cs` : `Err` (échec de validation
d'argument), `Snapshot`/`ProducedIn`/`WithSnapshot` (diff de répertoire pour
calculer `produced`), `MatchesGlob`, `BuildCpmodprojXml`.

---

## 6. Guide : ajouter un nouvel outil MCP

Un outil est une **méthode statique** dans une classe `[McpServerToolType]`
(`WolvenKitTools.cs` pour les 62 outils de base, `ModdingTools.cs` pour les 23
outils de workflow haut niveau, `LiveTools.cs` pour les 35 outils live).
**L'enregistrement est automatique par réflexion** : `Program.cs` appelle
`.WithToolsFromAssembly()`, donc il n'y a rien à enregistrer manuellement.
Déclarer les hints sur l'attribut (`ReadOnly`/`Destructive`/`Idempotent`) — un
test (`ConsistencyTests`) échoue s'ils manquent.

### 6.1 Squelette

```csharp
[McpServerTool(Name = "mon_outil")]
[Description("Phrase claire pour le LLM : ce que fait l'outil, ses limites, " +
             "et le format de sortie attendu.")]
public static async Task<string> MonOutil(
    Cp77ToolsRunner runner,   // injecté par DI (instance partagée → daemon unique)
    [Description("Décris chaque paramètre — le LLM s'en sert pour appeler l'outil.")] string entree,
    [Description("Chemin de sortie.")] string sortie,
    CancellationToken ct = default)
{
    if (!File.Exists(entree))
        return Err($"Fichier introuvable : {entree}");          // échec de validation

    var r = await runner.RunAsync(new[] { "verbe", entree, "--outpath", sortie }, ct);

    return Structured($"Mon traitement : {entree} → {sortie}", r,
        File.Exists(sortie) ? new List<string> { sortie } : new List<string>());
}
```

### 6.2 Règles à respecter

- **Signature** : `[McpServerTool(Name = "...")]` + `[Description]` sur la méthode
  et sur chaque paramètre. Premier paramètre `Cp77ToolsRunner runner` si l'outil
  délègue au daemon ; `CancellationToken ct = default` en dernier.
- **Délégation** : appeler `runner.RunAsync(argv, ct)` avec l'`argv` façon
  cp77tools. Si le verbe n'existe pas encore côté daemon, l'ajouter (cf. §7).
- **Résultat** : toujours renvoyer du JSON via `Structured(...)` (ou
  `Err(...)`/un objet anonyme conforme au schéma §5). Pour un outil producteur,
  passer la liste `produced` calculée à partir des fichiers réellement écrits
  (utiliser `WithSnapshot` quand la sortie est un répertoire dont on ne connaît
  pas le contenu à l'avance).
- **Validation amont** : vérifier l'existence des entrées et créer les
  répertoires de sortie *avant* d'appeler le daemon ; renvoyer `Err` tôt.
- Ne **rien écrire sur stdout** directement (réservé au JSON-RPC).
- Ajouter un test dans `WolvenKitMcp.Tests` si l'outil contient une logique pure
  (parsing, formatage, construction de chemin).

---

## 7. Guide : ajouter un verbe daemon

Tout se passe dans le dispatcher `Dispatch(...)` de
`src/WolvenKitDaemon/Program.cs`, qui mappe `argv[0]` (le verbe) sur une méthode.

### 7.1 Deux familles de services

| Type | Cycle de vie | Pourquoi |
|------|--------------|----------|
| **Hot singletons** | `HashService`, `TweakDBService`, `LocKeyService`, `HookService` | Coûteux à charger (~6 s) ; partagés et gardés chauds. |
| **Scoped (par requête)** | `ArchiveManager`, `ModTools`, `Red4ParserService`, `MeshTools`, `ConsoleFunctions` | Accumulent de l'état (ex. `ArchiveManager` mémorise les archives chargées) ; **doivent être neufs à chaque requête** sinon fuite d'état entre appels. |

Conséquence pratique : pour un verbe qui charge des archives de jeu, **crée
toujours un scope dédié** :

```csharp
using var scope = provider.CreateScope();
var am = scope.ServiceProvider.GetRequiredService<IArchiveManager>();
am.LoadGameArchives(exe);
// ... travail ...
```

Pour un verbe qui n'a besoin que d'une donnée de référence chaude (ex.
`resolve-hash`, `tweakdb-resolve`), lis directement le singleton depuis le
`provider` racine, sans scope.

### 7.2 ConsoleFunctions

La plupart des verbes « standards » (`unbundle`, `uncook`, `export`, `import`,
`pack`, `build`, `convert`, `archive`, `oodle`, `wwise`, `hash`) délèguent à des
méthodes de **`ConsoleFunctions`** (issu de `WolvenKit.Modkit` / `CP77Tools`),
résolu via un scope en fin de `Dispatch`. C'est l'implémentation réelle des
tâches WolvenKit. Les verbes « maison » (`loc-resolve`, `opus-import`,
`tweakdb-*`, `tweak read|write|validate`) sont implémentés en amont du switch,
directement avec les services.

### 7.3 ParseArgs

`ParseArgs(argv, skip)` sépare l'`argv` en **positionnels** et **options**
`--clé valeur`. Les flags booléens (sans valeur) sont déclarés dans le `HashSet`
`boolFlags` (`--list`, `--diff`, `--keep`, `--serialize`, …) — **si tu ajoutes une
nouvelle option booléenne, ajoute-la à ce set**, sinon le parser consommera
l'argument suivant comme sa valeur. Helpers associés : `Opt` (valeur d'option),
`Dir` (→ `DirectoryInfo`), `Fs` (fichier ou dossier), `ParseUext`,
`ParseUintList`.

### 7.4 Squelette d'un nouveau verbe

```csharp
if (verb == "mon-verbe")
{
    var (pos, o) = ParseArgs(argv, 1);
    if (pos.Count == 0) { logger.Error("mon-verbe : argument manquant"); return -1; }

    using var scope = provider.CreateScope();
    var am = scope.ServiceProvider.GetRequiredService<IArchiveManager>();
    // ... travail, écrire via logger.Info/Warning/Error ...
    logger.Info("mon-verbe : terminé");
    return 0;   // 0 = succès, ≠0 = échec (le serveur MCP le mappe sur status)
}
```

Le **code de retour** et la **sortie** (via `logger`) sont ce que le serveur MCP
recevra dans `{exit, output}`. Pour de gros volumes (ex. dumps TweakDB), applique
un **cap dur côté daemon** (cf. `tweakdb-query`, cap 100 + marqueur `+` pour
signaler la troncature) afin de ne pas saturer le contexte du LLM.

### 7.5 Piège : assemblies de contenu

Certaines dépendances (`DirectXTexNet`, etc.) sont livrées comme fichiers de
contenu, copiées dans la sortie de build mais **absentes du `deps.json`**. Le
daemon installe un résolveur de repli
(`AssemblyLoadContext.Default.Resolving`) qui les charge depuis le dossier de
l'application. Et `Oodle.Load()` est appelé au démarrage pour charger le codec
Kraken natif. Ne supprime pas ces deux initialisations.

---

## 8. Le parser REDscript et sa philosophie anti-faux-positifs

`src/WolvenKitMcp/RedscriptParser.cs` alimente l'outil `lint_script`. C'est un
**tokenizer + analyseur récursif-descendant** maison qui valide la **syntaxe**
d'un fichier `.reds` isolé. **Ce n'est PAS un type-checker** : il ne résout pas
les types/méthodes externes (cela exigerait le compilateur `scc` et tout
l'écosystème de mods, cf. `WINDOWS-VALIDATION.md`).

### 8.1 Philosophie

Calibré pour **0 faux positif** sur un corpus de **1374 `.reds` réels**. La règle
de design :

- **Strict** sur la structure stable : déclarations (`class`/`struct`/`enum`/
  `func`/`let`), types, en-têtes de statements, équilibrage des `() [] {}`.
- **Permissif mais équilibré** sur les expressions : on ne modélise **pas** la
  précédence des opérateurs. Une expression est consommée jusqu'à son terminateur
  (`;` ou `}`) en vérifiant uniquement l'équilibrage des parenthèses/crochets/
  accolades (`SkipExpression`, `SkipExpressionUntil`, `SkipBalanced`). Imposer une
  grammaire d'expression complète risquerait de rejeter du REDscript valide — d'où
  ce choix volontairement tolérant.

### 8.2 Mécanismes notables

- **Lexer** : gère commentaires `//` et `/* */` (signale les non fermés),
  chaînes avec préfixe (`n"…"`, `s"…"`) et **interpolation** `\( … )` avec
  guillemets imbriqués et chaînes multi-lignes, nombres avec suffixes (`u`, `f`,
  `ul`…), annotations `@…`, opérateurs composés.
- **Tolérance ciblée** : `if`/`while`/`for`/`switch` sans parenthèses obligatoires
  (`SkipExprUntilBlock`), lambdas `-> { }`, types raccourcis `[T]` / `[T; N]`,
  génériques `<T, U>` (`SkipAngles`, comptage caractère par caractère pour gérer
  `>>`), `native func` sans corps (signature suivie d'une frontière de
  déclaration), fonctions à corps-expression `= expr` (`SkipExprBody`),
  mots-clés contextuels utilisés comme noms.
- **Récupération d'erreur** : `Synchronize()` se resynchronise sur `;`, `}`, ou
  un mot-clé de déclaration, pour éviter les **cascades** de faux diagnostics.
  Garde-fou `MaxErrors = 60` et garantie de progression (`if (_p == before)
  Next();`) pour ne jamais boucler.

---

## 9. Build, test, validation

### 9.1 Ordre de build (le daemon d'abord)

```sh
dotnet build src/WolvenKitDaemon   # déploie aussi les natifs (libkraken/kraken.dll, DirectXTexNet)
dotnet build src/WolvenKitMcp
```

Le daemon doit être bâti **avant** le serveur MCP, car le serveur résout par
défaut le DLL du daemon dans le projet frère (`WOLVENKIT_DAEMON` permet de
surcharger).

### 9.2 Tests

```sh
dotnet test                        # 129 tests xUnit (src/WolvenKitMcp.Tests)
python3 test-daemon.py             # latence du daemon seul, par requête
python3 test-mcp-server.py         # serveur MCP de bout en bout
python  test-new-tools.py "<jeu>"  # validation jeu réel : archive_stats / validate_redmod / inspect_app
```

### 9.3 Validation de bout en bout sur de vrais assets (Windows)

```sh
python3 validate-windows.py        # exerce les outils + prompts sur de vrais assets du jeu
```

Voir `WINDOWS-VALIDATION.md` pour le détail et les prérequis (installation du jeu,
TweakDB, etc.).

### 9.4 Piège du verrou DLL (Windows) — tuer les process avant rebuild

Tant qu'un `WolvenKitDaemon` (ou un serveur MCP qui le tient ouvert) tourne, ses
DLL sont **verrouillées** par Windows et `dotnet build` échoue avec une erreur de
fichier en cours d'utilisation. **Avant de recompiler, tue les processus** :

```powershell
Get-Process dotnet, WolvenKitDaemon, WolvenKitMcp -ErrorAction SilentlyContinue | Stop-Process -Force
```

(Adapter les noms selon le mode de lancement.) Penser aussi à fermer le client
MCP — Claude Desktop relance le serveur, qui maintient le daemon vivant.

---

## 10. Carte des fichiers clés

| Fichier | Rôle |
|---------|------|
| `src/WolvenKitMcp/Program.cs` | Hôte MCP, transport stdio, DI, warmup du daemon. |
| `src/WolvenKitMcp/Cp77ToolsRunner.cs` | Pilote du daemon : IPC pipeliné, cache LRU, métriques p50/p95, repli cp77tools. |
| `src/WolvenKitMcp/WolvenKitTools.cs` | 62 outils MCP de base + helpers (`Structured`, `Err`, `Truncate`, `MatchesGlob`, `Snapshot`/`ProducedIn`, `BuildCpmodprojXml`). |
| `src/WolvenKitMcp/ModdingTools.cs` | 23 outils de workflow haut niveau + base de connaissance des frameworks. |
| `src/WolvenKitMcp/LiveTools.cs` | 35 outils `live_*` (jeu en cours d'exécution, via CetBridge). |
| `src/WolvenKitMcp/CetBridge.cs` | Pont TCP/fichier vers le mod Lua CETBridge (jeu vivant). |
| `src/WolvenKitMcp/RedscriptParser.cs` | Tokenizer + parser récursif REDscript (`lint_script`). |
| `src/WolvenKitMcp/WolvenKitResources.cs` | Ressources MCP (`[McpServerResourceType]`). |
| `src/WolvenKitMcp/WolvenKitPrompts.cs` | Prompts MCP (`[McpServerPromptType]`). |
| `src/WolvenKitDaemon/Program.cs` | DI, dispatcher de verbes, IPC, shims (`CapturingLoggerService`, etc.). |
| `src/WolvenKitMcp.Tests/` | Tests xUnit des helpers purs. |
