# Reprise de session — WolvenKit MCP

Projet entamé sur macOS, **validé sur Windows** le 19/05/2026, **étendu** sur
3 rounds le 20/05/2026 (round 1 : 9 outils + 5 prompts + cache LRU + pipelining ;
round 2 : 8 outils — inspection rapide, scaffolds TweakDB, scripts .reds,
backup/restore, observabilité ; round 3 : 12 outils — uninstall trio, deploy_redmod,
launch_game/tail_game_logs, mod_summary, dump_records, generate_redscript_template,
extract/build_localization, clear_cache + métriques par verbe). Les 53 outils,
5 prompts et 3 ressources exercés sur une vraie installation de Cyberpunk 2077.
Ce fichier permet à une nouvelle session Claude de reprendre le fil.

**Pour reprendre :** ouvre Claude (Code ou Desktop) dans le dossier
`wolvenkit-mcp/` et donne-lui ce fichier.

---

## Pour Claude — contexte de reprise

Tu reprends ce projet. Lis ce fichier en entier, puis `README.md` et
`WINDOWS-VALIDATION.md`. **La validation Windows est à jour** (62 OK · 1 réserve
· 1 partiel · 0 échec — cf. `WINDOWS-VALIDATION.md`). La suite logique est la
roadmap du `README.md` § « Pistes restantes ».

## Le projet en bref

Un serveur **MCP** qui expose le modding de Cyberpunk 2077 (WolvenKit / `cp77tools`)
à Claude — **53 outils, 5 prompts, 3 ressources**. Développé sur macOS, déployé et
étendu sur Windows.

## État actuel

- Serveur MCP C# / .NET 8, **53 outils, 5 prompts, 3 ressources**, **daemon
  persistant pipeliné** (plusieurs requêtes en vol, latence ~ms), compresseur
  Kraken natif, cross-platform, branché sur Claude, documenté.
  Sortie **JSON structurée** sur tous les outils, troncature log contextuelle,
  **cache LRU des listings d'archives** (×6 sur `find_in_archives` chaud) avec
  stats + métriques par verbe exposées via `wolvenkit_status` et `clear_cache`,
  mode `verbose` pour debug.
- **Validé sur macOS** : compilation, handshake MCP, daemon, `compute_hash` et
  `oodle` (byte-exact).
- **Validé sur Windows** (20/05/2026) : 53 outils + 5 prompts + 3 ressources
  exercés sur de vrais assets Cyberpunk 2077 via `validate-windows.py` —
  **62 OK · 1 réserve (`detect_conflicts`, bug amont) · 1 partiel
  (`build_project`, besoin d'un `.cpmodproj`) · 0 échec**.

## Architecture (détail dans `README.md`)

- `src/WolvenKitMcp/` — le serveur MCP (transport stdio).
- `src/WolvenKitDaemon/` — daemon persistant hébergeant les bibliothèques WolvenKit
  (`HashService` chargé une seule fois → appels rapides). Le serveur lui parle par
  IPC stdio.
- `Cp77ToolsRunner.cs` — pilote le daemon, avec **repli** sur un sous-processus
  `cp77tools` si le daemon est indisponible.
- `native/` — reconstruction de `libkraken.dylib` pour macOS. **Inutile sur Windows.**

## Spécificités Windows (≠ macOS)

- Le dossier `native/` ne sert pas (cp77tools / `kraken.dll` fonctionnent nativement).
- Pas besoin de `DOTNET_ROOT` ni de Homebrew ; `dotnet` est sur le PATH.
- Chemins en `\` ; attention aux espaces (`Program Files`) → chemins absolus.
- Textures et audio fonctionnent (binaires natifs Windows présents) — c'était la
  grande limite macOS.

## Pièges connus (savoir durement acquis — ne pas redécouvrir)

1. **Le daemon a besoin de `kraken.dll` et `DirectXTexNet.dll`.** Les packages
   NuGet les livrent comme *contentFiles*, non copiés vers la sortie d'une
   dépendance transitive. `WolvenKitDaemon.csproj` les déploie désormais
   automatiquement (cible `DeployNativeWindows`) ; le daemon ajoute aussi un
   résolveur `AssemblyLoadContext` pour charger `DirectXTexNet` (absent du
   `deps.json`). Plus de copie manuelle nécessaire.
2. **cp77tools renvoie parfois un code de sortie ≠ 0 en cas de succès** (ex. `oodle`).
   `Format()` (dans `WolvenKitTools.cs`) se fie aux marqueurs `: Success` / `: Error`
   du log, pas au seul code de sortie.
3. **`pack` ignore les fichiers d'extension non-REDengine** (« Unknown file extension,
   Skipping ») — comportement normal.
4. `WolvenKit.CLI` a son host / logger / commandes en `internal` → le daemon
   **reconstruit** le conteneur DI à partir des types publics des bibliothèques.
   Voir `src/WolvenKitDaemon/Program.cs`.
5. `HashService.Get(hash)` fonctionne (via `ResourcePathPool`), mais `GetAllHashes()`
   est un champ mort de WolvenKit — ne pas s'y fier.
6. Le **1ᵉʳ appel** d'outil d'une session attend le préchauffage du daemon (~7 s) ;
   ensuite tout est en millisecondes.
7. `resolve_hash` et `tweakdb_*` ne marchent **que via le daemon** (pas de verbe
   `cp77tools` équivalent, donc pas de repli sous-processus).

## Validation Windows — faite (19/05/2026)

1. ✅ Installé : .NET 8 SDK, Python, `WolvenKit.CLI` (cp77tools).
2. ✅ `dotnet build src\WolvenKitDaemon` puis `dotnet build src\WolvenKitMcp`.
3. ✅ Smoke tests `test-daemon.py` / `test-mcp-server.py` (corrigés : chemins
   temporaires cross-platform, sorties forcées en UTF-8).
4. ✅ Branché sur Claude Code (`claude mcp add wolvenkit`).
5. ✅ `WINDOWS-VALIDATION.md` déroulé via `validate-windows.py` — tableau rempli.
6. ✅ Sept bugs trouvés et corrigés (cf. `WINDOWS-VALIDATION.md`).

## Roadmap

**Déjà livré** sur 3 rounds : sortie JSON structurée, read/write_game_file,
install/uninstall_mod (+ redmod + tweak), diff_archives, lint_mod, REDmod
packaging, édition TweakDB (.tweak + describe + generate_template), inspection
mesh/texture/record, scripts (read/lint/generate_redscript_template),
backup/restore, deploy_redmod, launch_game + tail_game_logs, mod_summary,
dump_records, extract/build_localization, 5 prompts MCP, cache LRU + stats +
métriques par verbe + clear_cache, parallélisation wwise_export, pipelining
daemon, mode verbose.

**Reste** (cf. `README.md` § « Pistes restantes ») : génération `.cpmodproj`
(format à reconstituer), parser sémantique `.reds`, localisation audio (.opusinfo),
packaging `.mcpb`, CI GitHub Actions + tests unitaires C#, re-validation macOS.

## Toolchain de référence

- .NET 8 ; packages : `ModelContextProtocol` 1.3.0, `Microsoft.Extensions.Hosting`
  8.0.1, `WolvenKit.Modkit` 8.18.0 ; `cp77tools` = WolvenKit.CLI 8.18.0 (dotnet tool).

## Transfert du projet

Copier le dossier `wolvenkit-mcp/`. Inutile de copier les dossiers `bin/` et `obj/`
(artefacts de build macOS — `dotnet build` les régénère). Le sous-dossier `native/`
est optionnel (inutile sur Windows).

## Fichiers clés

- `README.md` — doc complète (install, architecture, 53 outils, 5 prompts, 3 ressources, roadmap).
- `WINDOWS-VALIDATION.md` — la checklist à dérouler + tableau des résultats.
- `src/WolvenKitMcp/` — `Program.cs`, `Cp77ToolsRunner.cs` (cache LRU + IPC pipeliné),
  `WolvenKitTools.cs`, `WolvenKitPrompts.cs`, `WolvenKitResources.cs`.
- `src/WolvenKitDaemon/Program.cs` — le daemon (boucle pipelinée + verbes `tweak`).
- `test-mcp-server.py`, `test-daemon.py` — clients de test.
- `validate-windows.py` — validation des 33 outils et 5 prompts sur de vrais assets.

*Handoff rédigé le 2026-05-19, étendu le 2026-05-20 sur 3 rounds (round 1 :
9 outils + 5 prompts + cache + pipelining ; round 2 : 8 outils — inspection,
scaffolds TweakDB, scripts, sécurité, observabilité ; round 3 : 12 outils —
uninstall + deploy_redmod, in-game, intelligence mod, scaffolds REDscript,
localisation UI, clear_cache + métriques par verbe).*
