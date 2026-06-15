# Changelog — WolvenKit MCP

Les dates sont celles des sessions de développement.

## 0.4.0 — 2026-06-15

Finalisation : audit complet du serveur (jugé fonctionnellement complet), correction
d'une dérive documentaire, garde-fou contre sa réapparition, et trois petits outils
utilitaires. **120 → 123 outils** (63 de base + 25 workflow + 35 live).

### Nouveaux outils (offline, déterministes, testés)
- **`archive_stats`** — répartition du contenu d'une `.archive` par extension
  (combien de `.mesh`, `.ent`, `.xbm`…), sur le cache de listing LRU.
- **`validate_redmod`** — valide le `info.json` d'un REDmod (champs requis
  `name`/`version` + format, cohérence des entrées `customSounds`). Complète la
  famille `validate_*`.
- **`inspect_app`** — résumé structurel d'un `.app` (apparences, composants mesh
  par apparence, meshes distincts), vue d'ensemble avant `validate_appearance`.

### Garde-fou anti-dérive
- `ConsistencyTests` vérifiait seulement le sens doc → code (les noms cités
  existent). Deux nouveaux tests vérifient l'**inverse** : le README documente tous
  les prompts/ressources du code, et les comptes annoncés (123/8/4) correspondent au
  code. C'est ce sens manquant qui avait laissé le README dériver (5 prompts /
  3 ressources documentés au lieu de 8 / 4).

### Validation jeu réel
- Nouveau `test-new-tools.py` : pilote le serveur MCP en stdio et exerce les trois
  outils sur une vraie installation (archive de base, REDmod installé, `.app` extrait
  du jeu). **6 PASS · 0 FAIL** sur Windows 11 le 15/06/2026.
- Bug corrigé, détecté par cette passe : les clés imbriquées `byExtension`
  (`archive_stats`) et `appearances` (`inspect_app`) sortaient en PascalCase au lieu
  du camelCase de l'enveloppe — projetées en minuscules au moment de la sérialisation.

### Documentation resynchronisée
- README : tableaux Prompts (5 → 8) et Ressources (3 → 4) complétés, en-tête
  « Outils exposés » désambiguïsé, total porté à 123 partout.
- `docs/TOOLS.md`, `docs/ARCHITECTURE.md`, `docs/LIVE_BRIDGE.md`,
  `docs/HTTP_TRANSPORT.md`, `manifest.json`, `HANDOFF.md` alignés sur 123.

## 0.3.0 — 2026-06-10

Chantier qualité issu d'un audit complet du serveur.

### Protocole MCP mieux exploité
- **Tool annotations** sur les 120 outils (`readOnlyHint`, `destructiveHint`,
  `idempotentHint`) : les clients peuvent auto-approuver les ~55 outils en lecture
  seule et demander confirmation pour les destructifs (`uninstall_*`,
  `write_game_file`, `live_kill_nearby`…). Vérifié par test E2E.
- **Notifications de progression** sur les outils longs (`uncook`, `extract_files`,
  `export_files`, `extract_audio`, `wwise_export`, `build_project`) : le daemon
  relaie ses lignes de log (throttlées à 2/s) et les pourcentages WolvenKit en
  messages `notifications/progress` au lieu de rester muet des minutes.
- **Ressource `wolvenkit://mods/{gamePath}`** : inventaire des mods installés
  (archives, REDmods, tweaks, scripts).
- **3 nouveaux prompts** : `create_archivexl_item` (chaîne record→factory→loc
  validée), `diagnose_broken_mod` (du doctor à la bissection),
  `live_iteration_loop` (itération TweakDB à chaud puis figée en .tweak).

### Cohérence (corrige des textes qui désinformaient l'agent)
- La ressource `wolvenkit://reference` est désormais **générée par réflexion**
  depuis les outils réels — comptes et noms ne peuvent plus être périmés
  (elle annonçait « 53 outils » pour 120 réels).
- Prompt `edit_tweakdb_item` corrigé : il prétendait que `write_tweak`/
  `install_tweak` n'existaient pas encore et recommandait l'édition manuelle.
- `manifest.json` : description à jour (120 outils), `user_config` enfin **câblé**
  dans l'env du serveur (`cli_timeout_seconds` était déclaré mais jamais transmis),
  ajout de `cet_tcp_port` et `cet_bridge_timeout_ms`.
- Test anti-régression : tout nom d'outil cité dans un prompt ou la référence doit
  exister dans l'assembly (réflexion), et chaque outil doit déclarer ses
  annotations.

### Sorties bornées (protection du contexte de l'agent)
- `find_in_archives` : cap à 500 correspondances (paramètre `maxMatches`),
  `matchCount` total + flag `truncated`.
- `archive_info --list` : cap à 500 fichiers (paramètre `maxFiles`).
- `diff_archives` : listes ajoutés/supprimés plafonnées à 500 + comptes réels.
- `diff_mod_vs_base` : comptes machine-lisibles par catégorie.

### Daemon et runner
- **Invalidation mtime de la TweakDB** : un tweakdb.bin régénéré hors-bande est
  rechargé (avant : seules les modifications de chemin déclenchaient un reload).
- **Index TweakDB par record** (paresseux) : `tweakdb-describe` passe de deux
  scans O(N) des flats à des lookups O(1).
- **Timeout d'inactivité par verbe** : les verbes lourds (uncook/build 900 s,
  export/pack 600 s) ne sont plus tués à 300 s ; la progression ré-arme le délai —
  un verbe vivant n'est jamais interrompu, un verbe figé l'est.
- **Watchdog** : ping périodique du daemon inactif (verbe `ping`, traité hors
  sérialisation) ; un daemon figé est tué proactivement au lieu de coûter un
  timeout complet à la requête suivante.
- Cache des listings d'archives : clé mtime **+ taille**.
- Purge au démarrage des dossiers temp `wolvenkit-mcp-*`/`wkmcp-*` de plus de 24 h.

### Pont live (CETBridge)
- Transport fichier piloté par **FileSystemWatcher** (latence ~ms) avec poll de
  filet à 250 ms (au lieu d'un poll fixe à 50 ms).
- Reconnexion TCP : les requêtes en vol de l'ancienne connexion échouent avec une
  erreur explicite « connexion remplacée — réessayer », sans toucher celles de la
  nouvelle connexion.
- `live_observe`/`live_observations` : les abonnements sont adressables par label
  stable `Classe/Event` (registre côté serveur, mod Lua inchangé).

### Diagnostic
- `analyze_conflicts` : pistes de résolution actionnables (`resolutionHints`).
- `analyze_dependencies` : les imports REDscript inter-mods sont **résolus contre
  les modules réellement déclarés** dans r6/scripts — on sait quel mod installé
  fournit chaque import, ou qu'il manque.

### Tests et CI
- 114 tests (92 → 114) dont un **smoke test MCP E2E** : lance le serveur stdio
  compilé, handshake `initialize`, pagine `tools/list`, vérifie les 120 outils,
  les annotations, et qu'aucun paramètre interne (IProgress) ne fuit dans les
  schémas. Tourne en CI sans jeu ni cp77tools.
- CI : le bundle `.mcpb` est dézippé et validé (manifest parsable, entry point,
  binaires natifs, mod Lua) avant upload.
- `build-mcpb.ps1` : BOM UTF-8 (exécutable aussi sous Windows PowerShell 5.1).

### Écarté sciemment
- `structuredContent` généralisé : duplique chaque réponse (`content` +
  `structuredContent`) — coût en tokens sans gain client aujourd'hui.
- AOT/trimming : incompatible avec la découverte d'outils par réflexion.
- ReadyToRun : le serveur démarre en ~0,4 s (mesuré) ; le coût réel est le
  chargement des données HashService côté daemon, que R2R n'améliore pas.
- Verbe daemon « batch » : `cr2w_to_json`/`uncook`/`export_files` acceptent déjà
  des dossiers.

## 0.2.0 — 2026-05-29 → 2026-06-05

- **Round 12** : transport **HTTP/Streamable** opt-in (`WOLVENKIT_MCP_TRANSPORT=http`),
  SDK 1.3.0 → 1.4.0, bind loopback par défaut, bearer token (SHA-256 temps
  constant), fail-closed hors loopback sans token. `docs/HTTP_TRANSPORT.md`.
- **Round 11** : pont live in-game — 35 outils `live_*` parlant à un jeu en cours
  via le mod Lua **CETBridge** (TCP 127.0.0.1:27010 + repli fichier). 120 outils.
- **Round 10** : fiabilisation apparences (`list_entity_appearances`,
  `validate_appearance`). 85 outils.
- **Round 9** (« on prend tout ») : `lint_tweak`, `generate_manifest`,
  `resolve_dynamic_appearance`, `migration_check`, `toggle_mods`,
  `export_entity`/`export_materials`. 83 outils.
- **Round 8** : `inspect_cr2w`/`find_in_cr2w`, `diagnose_logs`,
  `analyze_conflicts`, `validate_item_mod`. 76 outils.
- **Round 7** : intelligence journal (`inspect_journal`/`find_journal_entry`,
  28 476 entrées). 71 outils.
- **Round 6** : audit multi-agents — fix TweakDB singleton (rechargement par
  chemin), Zip-Slip dans `restore_mods`, collisions temp. Docs `docs/`.
- **Round 5** : 9 outils workflow (`ModdingTools.cs`) — analyze_dependencies,
  mod_doctor, validate_xl, scaffold_*, diff_mod_vs_base, find_references,
  package_mod. Parser REDscript dédié pour `lint_script` (0 faux positif sur les
  1374 .reds du jeu). 69 outils.
- **Rounds 3-4** : `loc_resolve`, `import_audio`, `extract_audio` (82k opus
  vérifiés), `.cpmodproj` généré (build_project bout en bout). 60 outils.

## 0.1.0 — 2026-05-19/20

- Serveur MCP C#/.NET 8 + daemon WolvenKit persistant (HashService chargé une
  fois, IPC stdio pipeliné, repli sous-processus cp77tools), cache LRU des
  listings, sortie JSON structurée, métriques par verbe.
- Validation Windows sur une vraie installation (`validate-windows.py`) :
  53 outils, 5 prompts, 3 ressources — 62 OK / 1 réserve / 1 partiel / 0 échec.
- Bundle Claude Desktop `.mcpb` (`build-mcpb.ps1`), CI GitHub Actions.
