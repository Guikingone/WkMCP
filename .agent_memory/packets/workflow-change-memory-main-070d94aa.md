---
type: "Workflow"
title: "Change memory: main"
description: "Repo-local context for 1 changed repo path on main."
resource: ".gitignore"
tags: ["change-memory", "diff-proposal", "repo-local", "branch:main"]
timestamp: "2026-07-15T20:29:25.076Z"
x-kage-id: "repo:https-github-com-guikingone-wolvenkitmcp:workflow:change-memory-main"
x-kage-type: "workflow"
x-kage-status: "superseded"
x-kage-scope: "repo"
x-kage-visibility: "team"
x-kage-verified: "superseded"
x-kage-paths: [".gitignore"]
---

# Change memory: main

> Repo-local context for 1 changed repo path on main.

Repo-local change memory generated from the current git diff.

Goal: preserve the durable context another agent should receive when it works in this repo later.

What changed:
- .gitignore

Diff summary:
```text
.gitignore |   2 ++
 CLAUDE.md  | 104 +++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++
 2 files changed, 106 insertions(+)
.agent_memory/packets/repo_map-wolvenkit-mcp-repo-overview-1039748b.md | untracked
.agent_memory/packets/repo_map-wolvenkit-mcp-repo-structure-6de60d90.md | untracked
.gitattributes | untracked
AGENTS.md | untracked
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
{"schema_version":2,"id":"repo:https-github-com-guikingone-wolvenkitmcp:workflow:change-memory-main","title":"Change memory: main","summary":"Repo-local context for 1 changed repo path on main.","body":"Repo-local change memory generated from the current git diff.\n\nGoal: preserve the durable context another agent should receive when it works in this repo later.\n\nWhat changed:\n- .gitignore\n\nDiff summary:\n```text\n.gitignore |   2 ++\n CLAUDE.md  | 104 +++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++\n 2 files changed, 106 insertions(+)\n.agent_memory/packets/repo_map-wolvenkit-mcp-repo-overview-1039748b.md | untracked\n.agent_memory/packets/repo_map-wolvenkit-mcp-repo-structure-6de60d90.md | untracked\n.gitattributes | untracked\nAGENTS.md | untracked\n```\n\nHow to verify:\n- Add the exact test, build, or manual verification command when you refine this memory.\n\nImprove this packet when more context is known:\n- The actual feature, fix, or refactor rationale.\n- Why the change was made, including relevant bugs, issues, decisions, and code explanations.\n- The package, API, command, or architectural pattern future agents should understand, verify, or reuse.\n- Any gotchas, follow-up risks, or branch-specific assumptions.\n\nPromote beyond this repo only after explicit org/global review.","type":"workflow","scope":"repo","visibility":"team","sensitivity":"internal","status":"superseded","confidence":0.62,"tags":["change-memory","diff-proposal","repo-local","branch:main"],"paths":[".gitignore"],"stack":[],"source_refs":[{"kind":"git_diff","branch":"main","head":"6f6be2fb74f3c4786fc49c205af4104e32ae27c2","merge_base":"6f6be2fb74f3c4786fc49c205af4104e32ae27c2","changed_files":[".agent_memory/packets/repo_map-wolvenkit-mcp-repo-overview-1039748b.md",".agent_memory/packets/repo_map-wolvenkit-mcp-repo-structure-6de60d90.md",".gitattributes",".gitignore","AGENTS.md","CLAUDE.md"],"summary_path":".agent_memory\\review\\branch-summary-main.json"}],"context":{"fact":"Current branch main changes 1 repo path.","why":"Branch change memory gives future agents durable context from the git diff when they continue, review, or verify this work.","trigger":"Recall when asking what changed on this branch, preparing a PR review, or resuming this work.","action":"Use the changed file list and diff summary as orientation, then inspect the actual diff and source files before making further edits.","verification":"Generated from git diff and refreshed by kage pr summarize or kage propose --from-diff.","risk_if_forgotten":"Future agents may repeat orientation work, miss branch-specific assumptions, or ignore files touched by this change.","stale_when":"The branch diff changes substantially, the branch is merged, or a newer change-memory packet supersedes it."},"freshness":{"last_verified_at":"2026-07-15T20:29:25.076Z","ttl_days":180,"path_fingerprints":[{"path":".gitattributes","sha256":"dd1d80f381f0e64aaf95eb3e852b378db3f6ba23ba50320ef2a087152bc07c08","size":45},{"path":".gitignore","sha256":"708eb261501bf707c2e2a04015bd3f24578384cbfa6fc8c58f40f9db3138fe61","size":512},{"path":"AGENTS.md","sha256":"8b09d17720705285c649ffc4a9d976d77ee3ed7ca1fd832bcb31961bcdc1bb86","size":7147},{"path":"CLAUDE.md","sha256":"7b25397b9192c640d52e31219954882e080c9e4d0718b41902537ecac36a251a","size":7160}],"path_fingerprint_policy":"source_hash_staleness","verification":"git_diff","superseded_at":"2026-07-16T21:30:27.366Z","superseded_by":"repo:https-github-com-guikingone-wolvenkitmcp:workflow:change-memory-feat-implement-wolvenkit-audit","superseded_reason":"La mémoire de diff initiale de main est remplacée par le résumé complet de la branche feat/implement-wolvenkit-audit."},"edges":[{"relation":"changes_path","to":"path:.agent_memory/packets/repo_map-wolvenkit-mcp-repo-overview-1039748b.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:.agent_memory/packets/repo_map-wolvenkit-mcp-repo-structure-6de60d90.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:.gitattributes","evidence":"git_diff"},{"relation":"changes_path","to":"path:.gitignore","evidence":"git_diff"},{"relation":"changes_path","to":"path:AGENTS.md","evidence":"git_diff"},{"relation":"changes_path","to":"path:CLAUDE.md","evidence":"git_diff"},{"relation":"superseded_by","to":"repo:https-github-com-guikingone-wolvenkitmcp:workflow:change-memory-feat-implement-wolvenkit-audit","evidence":"La mémoire de diff initiale de main est remplacée par le résumé complet de la branche feat/implement-wolvenkit-audit.","created_at":"2026-07-16T21:30:27.366Z"}],"quality":{"score":100,"reasons":["high-value memory type","has source evidence","grounded to repo paths","tagged","concise but substantive","actionable rationale or verification"],"risks":[],"duplicate_candidates":[],"estimated_tokens_saved":275,"admission":{"admit":true,"class":"candidate","score":70,"reasons":["durable memory type","has provenance","repo scoped or path grounded","has durable trigger, rationale, issue context, or explanation","substantive enough to reuse"],"risks":[]},"candidate_kind":"change_memory","review_boundary":"git_or_pr","promotion_requires_review":true,"uses_30d":4,"total_uses":4,"last_accessed_at":"2026-07-16T21:16:39.855Z","superseded_by":"repo:https-github-com-guikingone-wolvenkitmcp:workflow:change-memory-feat-implement-wolvenkit-audit","superseded_reason":"La mémoire de diff initiale de main est remplacée par le résumé complet de la branche feat/implement-wolvenkit-audit.","stale":true,"stale_reasons":["packet status is superseded","linked path changed since memory was verified: CLAUDE.md"],"suggested_action":"mark_stale"},"created_at":"2026-07-15T20:29:25.076Z","updated_at":"2026-07-16T21:30:27.366Z"}
```
