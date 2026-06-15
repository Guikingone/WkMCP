# Reprise de session — WolvenKit MCP

Serveur MCP C#/.NET 8 pour le modding de Cyberpunk 2077 : **123 outils**
(63 de base + 25 workflow + 35 live in-game), **8 prompts**, **4 ressources**.
Historique complet des évolutions dans `CHANGELOG.md` (0.1.0 → 0.4.0,
12 rounds + chantier qualité + finalisation 0.4.0 du 15/06/2026).

**Pour reprendre :** ouvre Claude (Code ou Desktop) dans le dossier
`wolvenkit-mcp/` et donne-lui ce fichier.

---

## Pour Claude — contexte de reprise

Tu reprends ce projet. Lis ce fichier, puis `CHANGELOG.md` (état réel le plus
fiable), `README.md` et `docs/ARCHITECTURE.md`. La validation jeu-réel la plus
récente est tracée dans `WINDOWS-VALIDATION.md` ; les tests automatiques (129,
dont un smoke test MCP E2E sans jeu) sont dans `src/WolvenKitMcp.Tests`.

## Le projet en bref

- **`src/WolvenKitMcp/`** — le serveur MCP. Transports stdio (défaut) et
  HTTP/Streamable (`WOLVENKIT_MCP_TRANSPORT=http`, fail-closed, cf.
  `docs/HTTP_TRANSPORT.md`). Outils/prompts/ressources enregistrés par réflexion.
  Les 123 outils portent des tool annotations (ReadOnly/Destructive/Idempotent) ;
  les outils longs remontent de la progression MCP.
- **`src/WolvenKitDaemon/`** — daemon persistant hébergeant les bibliothèques
  WolvenKit 8.18.0 (HashService chargé une fois ~6 s, puis requêtes en ms).
  IPC stdio JSON pipeliné (`{"id":N,"argv":[...]}` → réponse + messages
  `{"id":N,"progress":"…"}`), verbe `ping` hors sérialisation pour le watchdog.
- **`Cp77ToolsRunner.cs`** — pilote le daemon : cache LRU des listings
  (mtime+taille), timeouts d'inactivité par verbe, watchdog, repli sous-processus
  `cp77tools`, purge des dossiers temp au démarrage.
- **`CetBridge.cs` + `live-bridge/CETBridge/`** — pont live vers le jeu en cours
  (mod Lua CET, TCP 127.0.0.1:27010 + repli fichier via FileSystemWatcher).
- **`native/`** — reconstruction kraken pour macOS uniquement (inutile sur Windows).

## Pièges connus (savoir durement acquis — ne pas redécouvrir)

1. **Builder/tester en `-c Release`** : une instance serveur qui tourne verrouille
   la DLL Debug.
2. **Le daemon a besoin de `kraken.dll` et `DirectXTexNet.dll`** — déployées par
   la cible `DeployNativeWindows` du csproj + résolveur `AssemblyLoadContext`.
3. **cp77tools renvoie parfois un exit ≠ 0 en cas de succès** (ex. `oodle`) :
   `Format()` se fie aux marqueurs `: Success` / `: Error` du log.
4. `WolvenKit.CLI` a son host en `internal` → le daemon reconstruit le conteneur
   DI à partir des types publics des bibliothèques.
5. Le **1ᵉʳ appel** d'une session attend le préchauffage du daemon (~7 s).
6. `resolve_hash` et `tweakdb_*` ne marchent **que via le daemon** (pas de repli).
7. **`export_entity` reste expérimental** : `IModTools.ExportEntity` refuse en
   headless sur les entités NPC (le GUI fait un setup non documenté).
8. Les prompts et `wolvenkit://reference` citent des outils par nom : le test
   `ConsistencyTests` casse si un outil est renommé — c'est voulu. La référence
   est générée par réflexion, ne pas réintroduire de comptes en dur.
9. `build-mcpb.ps1` doit garder son **BOM UTF-8** (Windows PowerShell 5.1 ne parse
   pas l'UTF-8 accentué sans BOM ; la CI utilise pwsh qui s'en moque).

## Vérifications rapides

```powershell
dotnet build src/WolvenKitDaemon -c Release   # daemon d'abord (binaires natifs)
dotnet build src/WolvenKitMcp -c Release
dotnet test  src/WolvenKitMcp.Tests -c Release   # 129 tests, dont E2E MCP
./build-mcpb.ps1 -Configuration Release          # bundle dist/wolvenkit-mcp.mcpb
python validate-windows.py                       # validation jeu réel (longue)
```

## Pistes restantes (non bloquantes)

- `structuredContent`/outputSchema : écarté tant que le coût en tokens (réponse
  dupliquée) dépasse le gain client — réévaluer si les clients exploitent
  `structuredContent` nativement.
- Validation live des outils `live_*` : nécessite le jeu lancé
  (`test-live-bridge.py`, manuel).
- macOS : re-validation périodique (le gros des binaires natifs est Windows).

## Toolchain de référence

.NET 8 ; `ModelContextProtocol` 1.4.0 (+ AspNetCore) ; `WolvenKit.Modkit` 8.18.0 ;
`YamlDotNet` 16.3.0 ; `cp77tools` = WolvenKit.CLI 8.18.0 (repli).

*Handoff mis à jour le 2026-06-10 (chantier qualité 0.3.0 — voir CHANGELOG.md).*
