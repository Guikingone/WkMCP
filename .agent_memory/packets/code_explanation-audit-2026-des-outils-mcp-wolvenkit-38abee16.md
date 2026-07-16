---
type: "Code Explanation"
title: "Audit 2026 des outils MCP WolvenKit"
description: "L'audit du 15 juillet 2026 conclut que la couverture des grandes commandes est déjà large. Les priorités sont de corriger les contrats import mesh garmentSupport/keep=false et l'enveloppe JSON incohérente, de compléter l"
resource: "docs/AUDIT-WOLVENKIT-MCP.md"
tags: ["session-learning", "audit", "wolvenkit", "modkit", "tool-contract", "backlog"]
timestamp: "2026-07-15T20:45:04.437Z"
x-kage-id: "repo:https-github-com-guikingone-wolvenkitmcp:code_explanation:audit-2026-des-outils-mcp-wolvenkit-1784148304437"
x-kage-type: "code_explanation"
x-kage-status: "superseded"
x-kage-scope: "repo"
x-kage-visibility: "team"
x-kage-verified: "superseded"
x-kage-paths: ["docs/AUDIT-WOLVENKIT-MCP.md", "src/WkMcp/WolvenKitTools.cs", "src/WkMcp/LiveTools.cs", "src/WkMcp/SceneTools.cs", "src/WkDaemon/Program.cs", "src/WkDaemon/WkDaemon.csproj", "README.md"]
x-kage-stack: [".NET 8", "Model Context Protocol", "WolvenKit.Modkit"]
---

# Audit 2026 des outils MCP WolvenKit

> L'audit du 15 juillet 2026 conclut que la couverture des grandes commandes est déjà large. Les priorités sont de corr…

L'audit du 15 juillet 2026 conclut que la couverture des grandes commandes est déjà large. Les priorités sont de corriger les contrats import_mesh garmentSupport/keep=false et l'enveloppe JSON incohérente, de compléter la parité des options Modkit, d'évaluer le passage stable 8.18.0 vers 8.19.0, puis d'ajouter validate_project, inspect_entity, inspect_animation, inspect_streamingsector et generate_inkatlas. Le rapport détaillé et les sources sont dans docs/AUDIT-WOLVENKIT-MCP.md.
Evidence: Inspection du code local et des sources officielles WolvenKit 8.18.0/8.19.0; suite non-E2E: 359 tests réussis, 0 échec.
Verified by: dotnet test src/WkMcp.Tests/WkMcp.Tests.csproj avec McpE2ETests exclus, plus comparaison des sources officielles WolvenKit

## Verification

Inspection du code local et des sources officielles WolvenKit 8.18.0/8.19.0; suite non-E2E: 359 tests réussis, 0 échec.

# Citations

[1] explicit_capture (2026-07-15T20:45:04.437Z)

## Kage state

Machine state for lossless round-trip; OKF consumers can ignore it.

```json kage-state
{"schema_version":2,"id":"repo:https-github-com-guikingone-wolvenkitmcp:code_explanation:audit-2026-des-outils-mcp-wolvenkit-1784148304437","title":"Audit 2026 des outils MCP WolvenKit","summary":"L'audit du 15 juillet 2026 conclut que la couverture des grandes commandes est déjà large. Les priorités sont de corriger les contrats import mesh garmentSupport/keep=false et l'enveloppe JSON incohérente, de compléter l","body":"L'audit du 15 juillet 2026 conclut que la couverture des grandes commandes est déjà large. Les priorités sont de corriger les contrats import_mesh garmentSupport/keep=false et l'enveloppe JSON incohérente, de compléter la parité des options Modkit, d'évaluer le passage stable 8.18.0 vers 8.19.0, puis d'ajouter validate_project, inspect_entity, inspect_animation, inspect_streamingsector et generate_inkatlas. Le rapport détaillé et les sources sont dans docs/AUDIT-WOLVENKIT-MCP.md.\nEvidence: Inspection du code local et des sources officielles WolvenKit 8.18.0/8.19.0; suite non-E2E: 359 tests réussis, 0 échec.\nVerified by: dotnet test src/WkMcp.Tests/WkMcp.Tests.csproj avec McpE2ETests exclus, plus comparaison des sources officielles WolvenKit","type":"code_explanation","scope":"repo","visibility":"team","sensitivity":"internal","status":"superseded","confidence":0.7,"tags":["session-learning","audit","wolvenkit","modkit","tool-contract","backlog"],"paths":["docs/AUDIT-WOLVENKIT-MCP.md","src/WkMcp/WolvenKitTools.cs","src/WkMcp/LiveTools.cs","src/WkMcp/SceneTools.cs","src/WkDaemon/Program.cs","src/WkDaemon/WkDaemon.csproj","README.md"],"stack":[".NET 8","Model Context Protocol","WolvenKit.Modkit"],"source_refs":[{"kind":"explicit_capture","captured_at":"2026-07-15T20:45:04.437Z"}],"context":{"fact":"L'audit du 15 juillet 2026 conclut que la couverture des grandes commandes est déjà large. Les priorités sont de corriger les contrats import_mesh garmentSupport/keep=false et l'enveloppe JSON incohérente, de compléter la parité des options Modkit, d'évaluer le passage stable 8.18.0 vers 8.19.0, puis d'ajouter validate_project, inspect_entity, inspect_animation, inspect_streamingsector et generate_inkatlas. Le rapport détaillé et les sources sont dans docs/AUDIT-WOLVENKIT-MCP.md.\nEvidence: Inspection du code local et des sources officielles WolvenKit 8.18.0/8.19.0; suite non-E2E: 359 tests réussis, 0 échec.\nVerified by: dotnet test src/WkMcp.Tests/WkMcp.Tests.csproj avec McpE2ETests exclus, plus comparaison des sources officielles WolvenKit","verification":"Inspection du code local et des sources officielles WolvenKit 8.18.0/8.19.0; suite non-E2E: 359 tests réussis, 0 échec."},"freshness":{"ttl_days":365,"last_verified_at":"2026-07-15T20:45:04.437Z","path_fingerprints":[{"path":"docs/AUDIT-WOLVENKIT-MCP.md","sha256":"08adba2c5d3f4e35f0631231e4dc68963751ba4b583765a29b51882969c57344","size":8225},{"path":"src/WkMcp/WolvenKitTools.cs","sha256":"2e231e4982a98beaf08edce51ca057645438df35db0aeca6e7f642610da95b5a","size":246858},{"path":"src/WkMcp/LiveTools.cs","sha256":"3c6461955df7e28a1bc676553d16201175b658790536a2916073c115618438bb","size":32222},{"path":"src/WkMcp/SceneTools.cs","sha256":"c78f0ce395e4a812a83f4264c0d6c42c4dbccc202c0834c7d4390496f36ebd3a","size":62289},{"path":"src/WkDaemon/Program.cs","sha256":"176dc6b3b6adb38b771511ed8b834ee55a1f155495f64696ef039b1f5ebaeb0b","size":69440},{"path":"src/WkDaemon/WkDaemon.csproj","sha256":"f64d66658f3b8efa2a0915834d675d390d7831b22d480478034423d9ccaa4aba","size":3227},{"path":"README.md","sha256":"50baa453d933a984721bd322505b82c48edec91e17e995ccb5dbb8658766eea3","size":20181}],"path_fingerprint_policy":"source_hash_staleness","verification":"repo_local_agent_capture","superseded_at":"2026-07-16T21:29:48.739Z","superseded_by":"repo:https-github-com-guikingone-wolvenkitmcp:decision:audit-wolvenkit-8-19-int-gralement-impl-ment-1784237376301","superseded_reason":"Le rapport d’audit décrivait des lacunes désormais implémentées et testées sur la branche dédiée."},"edges":[{"relation":"superseded_by","to":"repo:https-github-com-guikingone-wolvenkitmcp:decision:audit-wolvenkit-8-19-int-gralement-impl-ment-1784237376301","evidence":"Le rapport d’audit décrivait des lacunes désormais implémentées et testées sur la branche dédiée.","created_at":"2026-07-16T21:29:48.739Z"}],"quality":{"reviewer":"repo-local-agent","votes_up":0,"votes_down":0,"uses_30d":1,"reports_stale":0,"review_boundary":"git_or_pr","promotion_requires_review":true,"discovery_tokens":2000,"discovery_tokens_estimated":true,"score":100,"reasons":["high-value memory type","has source evidence","grounded to repo paths","tagged","concise but substantive","actionable rationale or verification"],"risks":[],"duplicate_candidates":[],"estimated_tokens_saved":188,"superseded_by":"repo:https-github-com-guikingone-wolvenkitmcp:decision:audit-wolvenkit-8-19-int-gralement-impl-ment-1784237376301","superseded_reason":"Le rapport d’audit décrivait des lacunes désormais implémentées et testées sur la branche dédiée.","stale":true,"stale_reasons":["packet status is superseded","linked path changed since memory was verified: docs/AUDIT-WOLVENKIT-MCP.md, src/WkMcp/WolvenKitTools.cs, src/WkMcp/LiveTools.cs, src/WkMcp/SceneTools.cs"],"suggested_action":"mark_stale","total_uses":1,"last_accessed_at":"2026-07-16T20:46:33.940Z"},"created_at":"2026-07-15T20:45:04.437Z","updated_at":"2026-07-16T21:29:48.739Z","author_branch":"main"}
```
