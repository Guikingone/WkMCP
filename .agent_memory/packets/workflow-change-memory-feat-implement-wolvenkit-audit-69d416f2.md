---
type: "Workflow"
title: "Change memory: feat/implement-wolvenkit-audit"
description: "Repo-local context for 23 changed repo paths on feat/implement-wolvenkit-audit."
resource: ".gitignore"
tags: ["change-memory", "diff-proposal", "repo-local", "branch:feat-implement-wolvenkit-audit"]
timestamp: "2026-07-16T21:30:19.382Z"
x-kage-id: "repo:https-github-com-guikingone-wolvenkitmcp:workflow:change-memory-feat-implement-wolvenkit-audit"
x-kage-type: "workflow"
x-kage-status: "approved"
x-kage-scope: "repo"
x-kage-visibility: "team"
x-kage-verified: "verified"
x-kage-paths: [".gitignore", "AGENT.md", "CHANGELOG.md", "README.md", "docs/ARCHITECTURE.md", "docs/AUDIT-WOLVENKIT-MCP.md", "docs/HTTP_TRANSPORT.md", "docs/TOOLS.md", "manifest.json", "src/WkDaemon/Program.cs", "src/WkDaemon/WkDaemon.csproj", "src/WkMcp.Tests/AuditImplementationTests.cs", "src/WkMcp.Tests/ConsistencyTests.cs", "src/WkMcp/GameplayInspectionTools.cs", "src/WkMcp/LiveTools.cs", "src/WkMcp/ModdingTools.cs", "src/WkMcp/ProbeTools.cs", "src/WkMcp/Program.cs", "src/WkMcp/SceneTools.cs", "src/WkMcp/SemanticInspectionTools.cs", "src/WkMcp/ToolResult.cs", "src/WkMcp/WolvenKitResources.cs", "src/WkMcp/WolvenKitTools.cs"]
---

# Change memory: feat/implement-wolvenkit-audit

> Repo-local context for 23 changed repo paths on feat/implement-wolvenkit-audit.

Repo-local change memory generated from the current git diff.

Goal: preserve the durable context another agent should receive when it works in this repo later.

What changed:
- .gitignore
- AGENT.md
- CHANGELOG.md
- README.md
- docs/ARCHITECTURE.md
- docs/AUDIT-WOLVENKIT-MCP.md
- docs/HTTP_TRANSPORT.md
- docs/TOOLS.md
- manifest.json
- src/WkDaemon/Program.cs
- src/WkDaemon/WkDaemon.csproj
- src/WkMcp.Tests/AuditImplementationTests.cs
- src/WkMcp.Tests/ConsistencyTests.cs
- src/WkMcp/GameplayInspectionTools.cs
- src/WkMcp/LiveTools.cs
- src/WkMcp/ModdingTools.cs
- src/WkMcp/ProbeTools.cs
- src/WkMcp/Program.cs
- src/WkMcp/SceneTools.cs
- src/WkMcp/SemanticInspectionTools.cs
- src/WkMcp/ToolResult.cs
- src/WkMcp/WolvenKitResources.cs
- src/WkMcp/WolvenKitTools.cs

Diff summary:
```text
.gitignore                           |   2 +
 AGENT.md                             |  17 +-
 CHANGELOG.md                         |  17 ++
 CLAUDE.md                            | 103 ++++++++
 README.md                            |  18 +-
 docs/ARCHITECTURE.md                 |  10 +-
 docs/HTTP_TRANSPORT.md               |   2 +-
 docs/TOOLS.md                        | 101 +++++++-
 manifest.json                        |   2 +-
 src/WkDaemon/Program.cs              | 477 +++++++++++++++++++++++++++++++----
 src/WkDaemon/WkDaemon.csproj         |   8 +-
 src/WkMcp.Tests/ConsistencyTests.cs  |   2 +-
 src/WkMcp/GameplayInspectionTools.cs |   4 +
 src/WkMcp/LiveTools.cs               |  29 ++-
 src/WkMcp/ModdingTools.cs            |  12 +-
 src/WkMcp/ProbeTools.cs              |  14 +-
 src/WkMcp/Program.cs                 |   2 +-
 src/WkMcp/SceneTools.cs              |  23 +-
 src/WkMcp/WolvenKitResources.cs      |   6 +-
 src/WkMcp/WolvenKitTools.cs          | 342 ++++++++++++++++++++-----
 20 files changed, 989 insertions(+), 202 deletions(-)
.agent_memory/packets/code_explanation-audit-2026-des-outils-mcp-wolvenkit-38abee16.md | untracked
.agent_memory/packets/decision-audit-wolvenkit-8-19-int-gralement-impl-ment-48147aca.md | untracked
.agent_memory/packets/repo_map-wolvenkit-mcp-repo-overview-1039748b.md | untracked
.agent_memory/packets/repo_map-wolvenkit-mcp-repo-structure-6de60d90.md | untracked
.agent_memory/packets/workflow-change-memory-main-070d94aa.md | untracked
… [+230 chars truncated]
```
How to verify:
- Add the exact test, build, or manual verification command when you refine this memory.

Improve this packet when more context is known:
- The actual feature, fix, or refactor rationale.
- Why the change was made, including relevant bugs, issues, decisions, and code explanations.
- The package, API, command, or architectural pattern future agents should understand, verify, or reuse.
- Any gotchas, follow-up risks, or branch-specific assumptions.

Promote beyond this repo only after explicit org/global review.

## Why

Branch change memory gives future agents durable context from the git diff when they continue, review, or verify this work.

## Trigger

Recall when asking what changed on this branch, preparing a PR review, or resuming this work.

## Action

Use the changed file list and diff summary as orientation, then inspect the actual diff and source files before making further edits.

## Verification

Generated from git diff and refreshed by kage pr summarize or kage propose --from-diff.

## Risk if forgotten

Future agents may repeat orientation work, miss branch-specific assumptions, or ignore files touched by this change.

## Stale when

The branch diff changes substantially, the branch is merged, or a newer change-memory packet supersedes it.

# Citations

[1] git_diff

## Kage state

Machine state for lossless round-trip; OKF consumers can ignore it.

```json kage-state
{"schema_version":2,"id":"repo:https-github-com-guikingone-wolvenkitmcp:workflow:change-memory-feat-implement-wolvenkit-audit","title":"Change memory: feat/implement-wolvenkit-audit","summary":"Repo-local context for 23 changed repo paths on feat/implement-wolvenkit-audit.","body":"Repo-local change memory generated from the current git diff.\n\nGoal: preserve the durable context another agent should receive when it works in this repo later.\n\nWhat changed:\n- .gitignore\n- AGENT.md\n- CHANGELOG.md\n- README.md\n- docs/ARCHITECTURE.md\n- docs/AUDIT-WOLVENKIT-MCP.md\n- docs/HTTP_TRANSPORT.md\n- docs/TOOLS.md\n- manifest.json\n- src/WkDaemon/Program.cs\n- src/WkDaemon/WkDaemon.csproj\n- src/WkMcp.Tests/AuditImplementationTests.cs\n- src/WkMcp.Tests/ConsistencyTests.cs\n- src/WkMcp/GameplayInspectionTools.cs\n- src/WkMcp/LiveTools.cs\n- src/WkMcp/ModdingTools.cs\n- src/WkMcp/ProbeTools.cs\n- src/WkMcp/Program.cs\n- src/WkMcp/SceneTools.cs\n- src/WkMcp/SemanticInspectionTools.cs\n- src/WkMcp/ToolResult.cs\n- src/WkMcp/WolvenKitResources.cs\n- src/WkMcp/WolvenKitTools.cs\n\nDiff summary:\n```text\n.gitignore                           |   2 +\n AGENT.md                             |  17 +-\n CHANGELOG.md                         |  17 ++\n CLAUDE.md                            | 103 ++++++++\n README.md                            |  18 +-\n docs/ARCHITECTURE.md                 |  10 +-\n docs/HTTP_TRANSPORT.md               |   2 +-\n docs/TOOLS.md                        | 101 +++++++-\n manifest.json                        |   2 +-\n src/WkDaemon/Program.cs              | 477 +++++++++++++++++++++++++++++++----\n src/WkDaemon/WkDaemon.csproj         |   8 +-\n src/WkMcp.Tests/ConsistencyTests.cs  |   2 +-\n src/WkMcp/GameplayInspectionTools.cs |   4 +\n src/WkMcp/LiveTools.cs               |  29 ++-\n src/WkMcp/ModdingTools.cs            |  12 +-\n src/WkMcp/ProbeTools.cs              |  14 +-\n src/WkMcp/Program.cs                 |   2 +-\n src/WkMcp/SceneTools.cs              |  23 +-\n src/WkMcp/WolvenKitResources.cs      |   6 +-\n src/WkMcp/WolvenKitTools.cs          | 342 ++++++++++++++++++++-----\n 20 files changed, 989 insertions(+), 202 deletions(-)\n.agent_memory/packets/code_explanation-audit-2026-des-outils-mcp-wolvenkit-38abee16.md | untracked\n.agent_memory/packets/decision-audit-wolvenkit-8-19-int-gralement-impl-ment-48147aca.md | untracked\n.agent_memory/packets/repo_map-wolvenkit-mcp-repo-overview-1039748b.md | untracked\n.agent_memory/packets/repo_map-wolvenkit-mcp-repo-structure-6de60d90.md | untracked\n.agent_memory/packets/workflow-change-memory-main-070d94aa.md | untracked\n… [+230 chars truncated]\n```\n\nHow to verify:\n- Add the exact test, build, or manual verification command when you refine this memory.\n\nImprove this packet when more context is known:\n- The actual feature, fix, or refactor rationale.\n- Why the change was made, including relevant bugs, issues, decisions, and code explanations.\n- The package, API, command, or architectural pattern future agents should understand, verify, or reuse.\n- Any gotchas, follow-up risks, or branch-specific assumptions.\n\nPromote beyond this repo only after explicit org/global review.","type":"workflow","scope":"repo","visibility":"team","sensitivity":"internal","status":"approved","confidence":0.62,"tags":["change-memory","diff-proposal","repo-local","branch:feat-implement-wolvenkit-audit"],"paths":[".gitignore","AGENT.md","CHANGELOG.md","README.md","docs/ARCHITECTURE.md","docs/AUDIT-WOLVENKIT-MCP.md","docs/HTTP_TRANSPORT.md","docs/TOOLS.md","manifest.json","src/WkDaemon/Program.cs","src/WkDaemon/WkDaemon.csproj","src/WkMcp.Tests/AuditImplementationTests.cs","src/WkMcp.Tests/ConsistencyTests.cs","src/WkMcp/GameplayInspectionTools.cs","src/WkMcp/LiveTools.cs","src/WkMcp/ModdingTools.cs","src/WkMcp/ProbeTools.cs","src/WkMcp/Program.cs","src/WkMcp/SceneTools.cs","src/WkMcp/SemanticInspectionTools.cs","src/WkMcp/ToolResult.cs","src/WkMcp/WolvenKitResources.cs","src/WkMcp/WolvenKitTools.cs"],"stack":[],"source_refs":[{"kind":"git_diff","branch":"feat/implement-wolvenkit-audit","head":"6f6be2fb74f3c4786fc49c205af4104e32ae27c2","merge_base":"6f6be2fb74f3c4786fc49c205af4104e32ae27c2","changed_files":[".agent_memory/packets/code_explanation-audit-2026-des-outils-mcp-wolvenkit-38abee16.md",".agent_memory/packets/decision-audit-wolvenkit-8-19-int-gralement-impl-ment-48147aca.md",".agent_memory/packets/repo_map-wolvenkit-mcp-repo-overview-1039748b.md",".agent_memory/packets/repo_map-wolvenkit-mcp-repo-structure-6de60d90.md",".agent_memory/packets/workflow-change-memory-main-070d94aa.md",".gitattributes",".gitignore","AGENT.md","AGENTS.md","CHANGELOG.md","CLAUDE.md","README.md","docs/ARCHITECTURE.md","docs/AUDIT-WOLVENKIT-MCP.md","docs/HTTP_TRANSPORT.md","docs/TOOLS.md","manifest.json","src/WkDaemon/Program.cs","src/WkDaemon/WkDaemon.csproj","src/WkMcp.Tests/AuditImplementationTests.cs","src/WkMcp.Tests/ConsistencyTests.cs","src/WkMcp/GameplayInspectionTools.cs","src/WkMcp/LiveTools.cs","src/WkMcp/ModdingTools.cs","src/WkMcp/ProbeTools.cs","src/WkMcp/Program.cs","src/WkMcp/SceneTools.cs","src/WkMcp/SemanticInspectionTools.cs","src/WkMcp/ToolResult.cs","src/WkMcp/WolvenKitResources.cs","src/WkMcp/WolvenKitTools.cs"],"summary_path":".agent_memory\\review\\branch-summary-feat-implement-wolvenkit-audit.json"}],"context":{"fact":"Current branch feat/implement-wolvenkit-audit changes 23 repo paths.","why":"Branch change memory gives future agents durable context from the git diff when they continue, review, or verify this work.","trigger":"Recall when asking what changed on this branch, preparing a PR review, or resuming this work.","action":"Use the changed file list and diff summary as orientation, then inspect the actual diff and source files before making further edits.","verification":"Generated from git diff and refreshed by kage pr summarize or kage propose --from-diff.","risk_if_forgotten":"Future agents may repeat orientation work, miss branch-specific assumptions, or ignore files touched by this change.","stale_when":"The branch diff changes substantially, the branch is merged, or a newer change-memory packet supersedes it."},"freshness":{"last_verified_at":"2026-07-16T21:30:19.382Z","ttl_days":180,"path_fingerprints":[{"path":".gitattributes","sha256":"dd1d80f381f0e64aaf95eb3e852b378db3f6ba23ba50320ef2a087152bc07c08","size":45},{"path":".gitignore","sha256":"708eb261501bf707c2e2a04015bd3f24578384cbfa6fc8c58f40f9db3138fe61","size":512},{"path":"AGENT.md","sha256":"2c1dbf7673af987c37c2a38dd254118463051123ef68840b7f2e77bc7e0f92c9","size":13273},{"path":"AGENTS.md","sha256":"8b09d17720705285c649ffc4a9d976d77ee3ed7ca1fd832bcb31961bcdc1bb86","size":7147},{"path":"CHANGELOG.md","sha256":"a02f5f16e93e14414715a1a2a80aabce3d51eda1fd6f0fa1d0f0ab1c8a764119","size":36662},{"path":"CLAUDE.md","sha256":"38d083cd8c218848f11ebdeff0c09597b40eaefcaf1338408eb61c3ea673c364","size":7159},{"path":"README.md","sha256":"a23eba51d0b4fca5372ec7dc96545de005e26906801f9a807a68f37882ff4ad9","size":20281},{"path":"docs/ARCHITECTURE.md","sha256":"3c0b4c19e22e5a975bfe2d302462355e5dd69a71219a710445f9117bb07774d4","size":21144},{"path":"docs/AUDIT-WOLVENKIT-MCP.md","sha256":"32f57fff49516081b3f6003b44ebb4bd4e116c9bd127f6651af76da6d586e51b","size":8472},{"path":"docs/HTTP_TRANSPORT.md","sha256":"6d4dcb3ab5c47a4fb2f7f62496d370c132ea07c36255168f4b8e10f9c242cd52","size":6057},{"path":"docs/TOOLS.md","sha256":"937f9872e6609bf5eaff007af7808a188afaadc6a0d9230a05b7af949d197744","size":77327},{"path":"manifest.json","sha256":"73f366cecc3dbd213075bfaaed669b5d4dfc2f0a9e50d99cc4a27c76a86195a1","size":2954},{"path":"src/WkDaemon/Program.cs","sha256":"fabfc1e15e3ec67e8ecf8e51a69a632ecdf245fd4361fbf9e83c580739873940","size":88273},{"path":"src/WkDaemon/WkDaemon.csproj","sha256":"ecd6aee21477a7e56090c9c7f6eebb46a14132b14c4d1f4ca23ef5736bb03841","size":3342},{"path":"src/WkMcp.Tests/AuditImplementationTests.cs","sha256":"ded06e788f2cc1070196406dc9ee29e6d19d14948c04806b8ae2d9e6b82e74dc","size":4590},{"path":"src/WkMcp.Tests/ConsistencyTests.cs","sha256":"4f4b54b457a25efb62987656c8c4e6c04fb7a7420fd2360dd893c0cf24809211","size":8421},{"path":"src/WkMcp/GameplayInspectionTools.cs","sha256":"de811cdbffbad7cb6085baeb2110d6157012a67068bfb03a9da3f8c9ea78cc44","size":19245},{"path":"src/WkMcp/LiveTools.cs","sha256":"cdc16e55a6f58608ef593e079659503785b43810a934ba0025933e823ca8f50a","size":32681},{"path":"src/WkMcp/ModdingTools.cs","sha256":"5e5b85d603bb99bc6c8b444ee4cc13a371074a91556a719311b510015c365f34","size":173777},{"path":"src/WkMcp/ProbeTools.cs","sha256":"596007fff776f967ef8cd137dd5a03416457ef052021ee5fad6ea1d7b33fad67","size":19852},{"path":"src/WkMcp/Program.cs","sha256":"50be6b8f75f4a339c1958101985e32e4dc9c826a55f1d1ba7a7ad46975374a84","size":3869},{"path":"src/WkMcp/SceneTools.cs","sha256":"57fb3c3361677931249fb760707c58c57ec500dbd7c5fcaedadd7e66630a5916","size":62152},{"path":"src/WkMcp/SemanticInspectionTools.cs","sha256":"4860edbdcaf9fe085548771b363c8681817f728894b04e2b8d5efaa5f3b59f95","size":20017},{"path":"src/WkMcp/ToolResult.cs","sha256":"697cc0f950bb6e0ef85aa094bf9f2fd9175a8668c3b0980e2509d11682b06e65","size":2301},{"path":"src/WkMcp/WolvenKitResources.cs","sha256":"ec0e56535296c58edb66909c433e7e7264e9d22f86c24a83cd49d51ec0a52c56","size":14822},{"path":"src/WkMcp/WolvenKitTools.cs","sha256":"4e79300706d75ff744f24d0dc226b40a85419ccaa4724c2bb334071041879e42","size":264046}],"path_fingerprint_policy":"source_hash_staleness","verification":"git_diff"},"edges":[{"relation":"changes_path","to":"path:.agent_memory/packets/code_explanation-audit-2026-des-outils-mcp-wolvenkit-38abee16.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:.agent_memory/packets/decision-audit-wolvenkit-8-19-int-gralement-impl-ment-48147aca.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:.agent_memory/packets/repo_map-wolvenkit-mcp-repo-overview-1039748b.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:.agent_memory/packets/repo_map-wolvenkit-mcp-repo-structure-6de60d90.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:.agent_memory/packets/workflow-change-memory-main-070d94aa.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:.gitattributes","evidence":"git_diff"},{"relation":"changes_path","to":"path:.gitignore","evidence":"git_diff"},{"relation":"changes_path","to":"path:AGENT.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:AGENTS.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:CHANGELOG.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:CLAUDE.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:README.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:docs/ARCHITECTURE.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:docs/AUDIT-WOLVENKIT-MCP.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:docs/HTTP_TRANSPORT.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:docs/TOOLS.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:manifest.json","evidence":"git_diff"},{"relation":"changes_path","to":"path:src/WkDaemon/Program.cs","evidence":"git_diff"},{"relation":"changes_path","to":"path:src/WkDaemon/WkDaemon.csproj","evidence":"git_diff"},{"relation":"changes_path","to":"path:src/WkMcp.Tests/AuditImplementationTests.cs","evidence":"git_diff"},{"relation":"supersedes","to":"repo:https-github-com-guikingone-wolvenkitmcp:workflow:change-memory-main","evidence":"La mémoire de diff initiale de main est remplacée par le résumé complet de la branche feat/implement-wolvenkit-audit.","created_at":"2026-07-16T21:30:27.366Z"}],"quality":{"score":100,"reasons":["high-value memory type","has source evidence","grounded to repo paths","tagged","concise but substantive","actionable rationale or verification"],"risks":[],"duplicate_candidates":[],"estimated_tokens_saved":715,"admission":{"admit":true,"class":"candidate","score":70,"reasons":["durable memory type","has provenance","repo scoped or path grounded","has durable trigger, rationale, issue context, or explanation","substantive enough to reuse"],"risks":[]},"candidate_kind":"change_memory","review_boundary":"git_or_pr","promotion_requires_review":true},"created_at":"2026-07-16T21:30:19.382Z","updated_at":"2026-07-16T21:30:27.366Z"}
```
